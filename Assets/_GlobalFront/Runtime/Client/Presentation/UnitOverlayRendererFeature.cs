using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GlobalFront.Client.Presentation
{
    /// <summary>
    /// Registers <see cref="UnitOverlayRenderPass"/> with the Universal renderer and
    /// owns the <see cref="UnitOverlayBatcher"/> it draws from (Phase 3, step 3.4,
    /// OD-25).
    ///
    /// Adding it to <c>Assets/Settings/PC_Renderer.asset</c> (and the mobile renderer)
    /// through the renderer's Render Features list is the whole installation: the pass
    /// then runs for every camera, and the overlay disappears with the renderer rather
    /// than with a scene object that a match may not have loaded yet.
    ///
    /// <see cref="Active"/> exists because a renderer feature is a ScriptableObject
    /// owned by the pipeline, so nothing in the scene can hold a reference to it. A
    /// match that wants its units drawn with rings and bars looks itself up here —
    /// null simply means the feature was never installed, which is a presentation
    /// difference, not an error.
    /// </summary>
    public sealed class UnitOverlayRendererFeature : ScriptableRendererFeature
    {
        [Tooltip("Material built from the GlobalFront/Unit Overlay shader. Left empty, " +
                 "the feature creates one from that shader by name, which keeps the " +
                 "overlays alive before any material asset is authored.")]
        [SerializeField] private Material overlayMaterial;

        private UnitOverlayBatcher _batcher;
        private UnitOverlayRenderPass _pass;

        /// <summary>
        /// The assignment the current meshes and material were built from. Compared by
        /// reference rather than by Unity's overloaded equality, because the common
        /// case is two nulls: the feature created its own material, and an inspector
        /// that still has none assigned must not ask it to do so again on every reload.
        /// </summary>
        private Material _builtFrom;

        /// <summary>The installed instance whose overlays are currently drawn.</summary>
        public static UnitOverlayRendererFeature Active { get; private set; }

        /// <summary>
        /// The overlay buffers the pass draws from. Configuration lives here:
        /// <c>AlwaysShowHealthBars</c>, the capacity and the colours are set by the
        /// options screen and the load screen, not per frame.
        /// </summary>
        public UnitOverlayBatcher Batcher => _batcher;

        /// <inheritdoc />
        public override void Create()
        {
            _batcher ??= new UnitOverlayBatcher();
            _pass ??= new UnitOverlayRenderPass(_batcher);

            // Re-running Create is normal (it happens on every script reload and on
            // inspector edits of the renderer), and rebuilding the meshes each time
            // would leak one pair per reload for the length of the editor session.
            if (_pass != null && ReferenceEquals(_builtFrom, overlayMaterial))
            {
                Active = this;
                return;
            }

            _builtFrom = overlayMaterial;

            if (!UnitOverlayGeometry.TryCreateResources(
                    overlayMaterial,
                    out var material,
                    out var ringMesh,
                    out var barMesh,
                    out var ringPassIndex,
                    out var barPassIndex))
            {
                _pass.SetResources(null, null, null, -1, -1);
                Active = this;
                return;
            }

            _pass.SetResources(material, ringMesh, barMesh, ringPassIndex, barPassIndex);
            Active = this;
        }

        /// <inheritdoc />
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (renderer == null || _pass == null || !_pass.IsReady)
            {
                return;
            }

            renderer.EnqueuePass(_pass);
        }

        /// <summary>
        /// Points the overlays at one match's view table. Null detaches them, which is
        /// what leaving a match is: the pass keeps running and draws nothing, so a
        /// spectator hand-off or a tactical-pause overlay needs no renderer surgery.
        /// </summary>
        public void Attach(UnitViewBinder binder) => _pass?.Attach(binder);

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (ReferenceEquals(Active, this))
            {
                Active = null;
            }

            _pass = null;
            _builtFrom = null;
            base.Dispose(disposing);
        }
    }
}
