using UnityEngine;
using UnityEngine.Rendering;


namespace GlobalFront.Client.Presentation
{
    /// <summary>
    /// The meshes and material the overlay pass draws with (Phase 3, step 3.4,
    /// OD-25).
    ///
    /// Both overlays are one quad each, generated instead of imported: a selection
    /// ring is a square of ground and a health bar is a rectangle facing the camera,
    /// so the only thing an authored asset could add is vertices the CPU then throws
    /// away. The shapes that make them read as a ring and as a bar are drawn in the
    /// shader from the quad's own UVs, which is also what keeps a ring circular at any
    /// scale and a bar edge crisp at any zoom.
    ///
    /// The two quads differ only in the plane they lie in, and that choice is what the
    /// instance matrices rely on: the ring is authored in <b>XZ</b> so an identity
    /// rotation puts it flat on the ground (OD-27), and the bar in <b>XY</b> with its
    /// normal on <b>+Z</b> so the camera's own rotation is the whole billboard.
    ///
    /// Created once, when the renderer feature is set up, and never touched again:
    /// <see cref="UnitOverlayRenderPass"/> draws from these objects every frame and
    /// allocating a mesh or a material mid-frame is the exact pattern ADR-012 exists
    /// to retire.
    /// </summary>
    internal static class UnitOverlayGeometry
    {
        /// <summary>Name of the overlay shader, for the content author to assign.</summary>
        public const string ShaderName = "GlobalFront/Unit Overlay";

        /// <summary>Pass index name of the ground ring; resolved with <c>FindPass</c>.</summary>
        public const string RingPassName = "UnitOverlayRing";

        /// <summary>Pass index name of the billboarded health bar.</summary>
        public const string HealthBarPassName = "UnitOverlayHealthBar";

        /// <summary>Per-instance ring colour, fed by <c>SetVectorArray</c>.</summary>
        public static readonly int RingColorProperty = Shader.PropertyToID("_OverlayColor");

        /// <summary>Per-instance bar parameters, fed by <c>SetVectorArray</c>.</summary>
        public static readonly int HealthBarParamsProperty = Shader.PropertyToID("_HealthBarParams");

        /// <summary>
        /// One unit square in the XZ plane (normal +Y), centred on the origin, so a
        /// ring instance is <c>scale = diameter</c> on both horizontal axes.
        /// </summary>
        public static Mesh CreateGroundQuad(string name) => CreateQuad(name, Vector3.right, Vector3.forward);

        /// <summary>
        /// One unit square in the XY plane (normal +Z), centred on the origin, so
        /// multiplying by the camera rotation aims it straight at the viewer.
        /// </summary>
        public static Mesh CreateBillboardQuad(string name) => CreateQuad(name, Vector3.right, Vector3.up);

        /// <summary>
        /// Resolves the shader, mesh and pass indices the overlay draws with.
        ///
        /// <paramref name="assignMaterial"/> lets content own the material (and with
        /// it the blend state and the ring thickness) instead of this class guessing;
        /// null falls back to an instance created from <see cref="ShaderName"/>, which
        /// is what keeps the overlays alive in a build that has no material asset yet
        /// and in a headless run where the shader may not be importable at all. Which
        /// of the two happened is reported through <paramref name="ownsMaterial"/>:
        /// only a material this method created may this method destroy, and the caller
        /// has to know the difference to release correctly.
        /// Returns false, having logged once and left every out parameter empty, when
        /// there is nothing to draw with: a missing overlay shader is a missing ring,
        /// not an exception in the render loop.
        /// </summary>
        public static bool TryCreateResources(
            Material assignMaterial,
            out Material material,
            out bool ownsMaterial,
            out Mesh ringMesh,
            out Mesh barMesh,
            out int ringPassIndex,
            out int barPassIndex)
        {
            material = assignMaterial;
            ownsMaterial = false;
            ringMesh = null;
            barMesh = null;
            ringPassIndex = -1;
            barPassIndex = -1;

            if (material == null)
            {
                var shader = Shader.Find(ShaderName);
                if (shader == null)
                {
                    Debug.LogWarning(
                        $"{nameof(UnitOverlayGeometry)}: shader '{ShaderName}' was not found, so selection rings and health bars are not drawn. Assign a material built from it on {nameof(UnitOverlayRendererFeature)}, or check that the shader is included in the build.");
                    return false;
                }

                material = new Material(shader)
                {
                    name = "Unit Overlay",
                    // Not selectable and not savable: this object is created by the
                    // renderer, and a second copy of it appearing in the project would
                    // read as the asset the feature was supposed to reference.
                    hideFlags = HideFlags.DontSave,
                };
                ownsMaterial = true;
            }

            // DrawMeshInstanced is refused by URP without this, and the instanced colour
            // arrays the batches feed are the whole overlay design. Applied to whichever
            // material ends up being used, not only to the one created above: the flag is
            // a checkbox on a material asset, but a content author who points the feature
            // at an existing overlay material can easily miss it, and the symptom (no
            // overlays at all) is nowhere near the cause, so it is turned on here rather
            // than warned about.
            if (!material.enableInstancing)
            {
                material.enableInstancing = true;
            }

            var usable = material.shader != null;
            if (!usable)
            {
                Debug.LogWarning(
                    $"{nameof(UnitOverlayGeometry)}: the assigned overlay material has no shader, so rings and health bars are not drawn.");
            }
            else
            {
                ringPassIndex = material.FindPass(RingPassName);
                barPassIndex = material.FindPass(HealthBarPassName);
                usable = ringPassIndex >= 0 && barPassIndex >= 0;
                if (!usable)
                {
                    Debug.LogWarning(
                        $"{nameof(UnitOverlayGeometry)}: material '{material.name}' is missing the '{RingPassName}' or '{HealthBarPassName}' pass, so rings and health bars are not drawn. Use the '{ShaderName}' shader rather than a generic unlit one.");
                }
            }

            if (usable)
            {
                ringMesh = CreateGroundQuad("GlobalFront Selection Ring");
                barMesh = CreateBillboardQuad("GlobalFront Health Bar");
                return true;
            }

            // One tail for every failure, so a material created above can never be
            // stranded: it is a native object, and dropping the managed reference does
            // not free it. A headless run that finds the shader but cannot compile its
            // passes lands here with the material already allocated.
            if (ownsMaterial)
            {
                CoreUtils.Destroy(material);
            }

            material = null;
            ownsMaterial = false;
            ringMesh = null;
            barMesh = null;
            ringPassIndex = -1;
            barPassIndex = -1;
            return false;
        }

        private static Mesh CreateQuad(string name, Vector3 right, Vector3 up)
        {
            const float half = 0.5f;

            var mesh = new Mesh
            {
                name = name,
                hideFlags = HideFlags.DontSave,
                vertices = new[]
                {
                    -right * half - up * half,
                    right * half - up * half,
                    -right * half + up * half,
                    right * half + up * half,
                },
                uv = new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(1f, 0f),
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                },
                // The shader culls nothing, so the winding only has to be consistent
                // rather than correct: a health bar flips its facing as the camera
                // moves, and a backface-culled overlay would blink out mid-turn.
                triangles = new[] { 0, 1, 2, 2, 1, 3 },
            };

            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
