using System;
using GlobalFront.Client.Replication;
using UnityEngine;

namespace GlobalFront.Client.Presentation
{
    /// <summary>
    /// Builds the two instanced overlay batches of a frame — selection rings and
    /// health bars (Phase 3, step 3.4, OD-25).
    ///
    /// OD-25 bans the two obvious ways to draw an overlay per unit: a world-space
    /// uGUI canvas or a child GameObject on every unit (four hundred canvases is four
    /// hundred layout passes and four hundred draw calls), and a decal projector for
    /// the rings (reprojection of a moving footprint is exactly what it is worst at).
    /// What is left is one instanced draw per overlay kind, fed from flat arrays that
    /// this class owns.
    ///
    /// Two properties make the design work under the OD-28 budget:
    ///
    /// 1. <b>The buffers never grow.</b> All four arrays are allocated at
    ///    construction, so <see cref="BuildBatches"/> writes into memory that already
    ///    exists and allocates nothing however many units are on screen — the same
    ///    rule <see cref="UnitViewTickBuffer"/> and <see cref="UnitViewBinder"/> keep,
    ///    because a per-frame allocation at four hundred units is a garbage burst every
    ///    second regardless of which layer produced it.
    /// 2. <b>Nothing decides visibility but this pass.</b> The caller does not add or
    ///    remove overlays; it sets <see cref="UnitView.SetSelected"/> and
    ///    <see cref="AlwaysShowHealthBars"/> and the batch follows on the next build.
    ///    That is also what makes the lifecycle safe: an overlay cannot outlive the
    ///    unit it belongs to, because the batch is rebuilt from the binder's slot
    ///    table every frame instead of accumulating handles.
    ///
    /// Every guard below exists because the input is replicated, not trusted: a
    /// corrupted position, an archetype the client has no row for, or a zero-length
    /// camera rotation must cost one missing overlay, never a NaN in an instance
    /// matrix — a single NaN matrix can take the whole instanced draw apart on some
    /// APIs, which is worse than one unit without a ring.
    /// </summary>
    public sealed class UnitOverlayBatcher
    {
        /// <summary>
        /// Instances per overlay kind at construction: the OD-28 profile is four
        /// hundred units on screen, so the default leaves headroom without reserving
        /// the whole replication table.
        /// </summary>
        public const int DefaultCapacity = 512;

        /// <summary>
        /// Upper bound for <see cref="Capacity"/>: one overlay per replicated unit
        /// slot is the most any client can ever show, and <see cref="UnitViewPool"/>
        /// caps its views at the same number.
        /// </summary>
        public const int MaxCapacity = ClientReplicationWorld.DefaultCapacity;

        /// <summary>
        /// Instances one <c>DrawMeshInstanced</c> call may carry. This is not a
        /// performance choice but a shader limit: the built-in instancing path packs
        /// its constant buffer with <c>UNITY_INSTANCED_ARRAY_SIZE</c> entries, which
        /// is 250 on Vulkan-mobile, Switch and WebGPU and 500 elsewhere (see
        /// <c>UnityInstancing.hlsl</c>). Reading past it is undefined, so the draw
        /// path chunks to this size and URP's own decal system uses the same number.
        /// </summary>
        public const int MaxInstancesPerDraw = 250;

        /// <summary>
        /// Height of the ring plane above the ground. Terrain walkable areas are
        /// exactly Y = 0 by OD-27, so the offset is what keeps a flat ring from
        /// z-fighting with the mesh it lies on.
        /// </summary>
        public const float RingHeightMetres = 0.02f;

        /// <summary>
        /// Height of the bar above the hull origin. A per-archetype silhouette height
        /// belongs on the catalog row when real roster art exists; until then every
        /// archetype is the prototype footprint, so one constant cannot read wrong for
        /// one kind and right for another.
        /// </summary>
        public const float HealthBarHeightMetres = 1.1f;

        /// <summary>Bar height as a fraction of its own width, in metres.</summary>
        public const float HealthBarThicknessMetres = 0.16f;

        /// <summary>
        /// Bar width in unit footprints. Wider than the hull so the bar reads as an
        /// overlay rather than a stripe painted on the unit.
        /// </summary>
        public const float HealthBarWidthInDiameters = 1.25f;

        /// <summary>Squared norm below which a quaternion cannot orient anything.</summary>
        private const float RotationEpsilon = 1e-6f;

        private readonly Matrix4x4[] _ringMatrices;
        private readonly Vector4[] _ringProperties;
        private readonly Matrix4x4[] _barMatrices;
        private readonly Vector4[] _barProperties;

        private int _ringCount;
        private int _barCount;

        public UnitOverlayBatcher(int capacity = DefaultCapacity)
        {
            if (capacity <= 0 || capacity > MaxCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    $"overlay capacity must be between 1 and {MaxCapacity}.");
            }

            _ringMatrices = new Matrix4x4[capacity];
            _ringProperties = new Vector4[capacity];
            _barMatrices = new Matrix4x4[capacity];
            _barProperties = new Vector4[capacity];
            Capacity = capacity;

            SelectionRingColor = new Color(0.20f, 1.00f, 0.38f, 1.00f);
            LowHealthBarColor = new Color(0.90f, 0.12f, 0.08f, 1.00f);
            FullHealthBarColor = new Color(0.15f, 0.90f, 0.25f, 1.00f);
        }

        /// <summary>Instances per overlay kind this batcher can ever emit.</summary>
        public int Capacity { get; }

        /// <summary>Rings in the last build.</summary>
        public int SelectionRingCount => _ringCount;

        /// <summary>Bars in the last build.</summary>
        public int HealthBarCount => _barCount;

        /// <summary>
        /// Overlays the last build could not write because the batch was already
        /// full. A warm-up question, not a bug: the count says how far short of the
        /// match's selection the configured <see cref="Capacity"/> was.
        /// </summary>
        public int ClippedInstanceCount { get; private set; }

        /// <summary>
        /// Draw a bar over every living unit, not only the injured and the selected
        /// ones. A player option, and the reason the bar rule is a predicate rather
        /// than "damaged units only".
        /// </summary>
        public bool AlwaysShowHealthBars { get; set; }

        /// <summary>Colour of every selection ring, per instance in batch order.</summary>
        public Color SelectionRingColor { get; set; }

        /// <summary>Bar colour at zero health; the fill ramps to <see cref="FullHealthBarColor"/>.</summary>
        public Color LowHealthBarColor { get; set; }

        /// <summary>Bar colour at full health.</summary>
        public Color FullHealthBarColor { get; set; }

        /// <summary>Rings of the last build, in emission order.</summary>
        public ReadOnlySpan<Matrix4x4> SelectionRingMatrices => _ringMatrices.AsSpan(0, _ringCount);

        /// <summary>Ring colours, parallel to <see cref="SelectionRingMatrices"/>.</summary>
        public ReadOnlySpan<Vector4> SelectionRingProperties => _ringProperties.AsSpan(0, _ringCount);

        /// <summary>Billboarded bars of the last build, in emission order.</summary>
        public ReadOnlySpan<Matrix4x4> HealthBarMatrices => _barMatrices.AsSpan(0, _barCount);

        /// <summary>
        /// Bar parameters, parallel to <see cref="HealthBarMatrices"/>:
        /// <c>(health fraction, red, green, blue)</c>.
        /// </summary>
        public ReadOnlySpan<Vector4> HealthBarProperties => _barProperties.AsSpan(0, _barCount);

        // The draw path needs the buffers themselves rather than a span copy:
        // DrawMeshInstanced takes a Matrix4x4[], and MaterialPropertyBlock takes a
        // Vector4[], both by reference and both reading only the first `count`
        // entries. Internal so that the arrays stay out of the public API — a
        // caller that wrote into them could put a NaN in front of the GPU.
        internal Matrix4x4[] SelectionRingMatrixBuffer => _ringMatrices;
        internal Vector4[] SelectionRingPropertyBuffer => _ringProperties;
        internal Matrix4x4[] HealthBarMatrixBuffer => _barMatrices;
        internal Vector4[] HealthBarPropertyBuffer => _barProperties;

        /// <summary>
        /// Empties both batches without touching the configuration. Call it when
        /// presentation is hidden, so a paused or exited match draws no overlay even
        /// though the pass keeps running.
        /// </summary>
        public void Clear()
        {
            _ringCount = 0;
            _barCount = 0;
            ClippedInstanceCount = 0;
        }

        /// <summary>
        /// Rebuilds both batches from the binder's slot table.
        ///
        /// Deterministic and self-contained on purpose: no camera, no cull, no URP
        /// context is needed, which is what lets the whole visibility rule set be
        /// tested in EditMode. The only input the frame contributes is
        /// <paramref name="cameraRotation"/>, the billboard orientation of the bars;
        /// rings lie on the ground and ignore it.
        ///
        /// Emission order is slot order, so a caller that wants to know which unit an
        /// instance belongs to walks the same slots in the same order. Nothing here
        /// allocates.
        /// </summary>
        /// <param name="binder">Slot table to read bound views from.</param>
        /// <param name="cameraRotation">
        /// World rotation of the camera the bars face. A degenerate or non-finite
        /// value falls back to <see cref="Quaternion.identity"/>.
        /// </param>
        public void BuildBatches(UnitViewBinder binder, Quaternion cameraRotation)
        {
            if (binder == null)
            {
                throw new ArgumentNullException(nameof(binder));
            }

            _ringCount = 0;
            _barCount = 0;
            ClippedInstanceCount = 0;

            var barRotation = IsRotationUsable(cameraRotation) ? cameraRotation : Quaternion.identity;
            var slotCount = binder.SlotCount;

            for (var slot = 0; slot < slotCount; slot++)
            {
                if (!binder.TryGetView(slot, out var view))
                {
                    continue;
                }

                // Three independent reasons a view has no overlay to show: it is back
                // in the pool free list (identity cleared), the unit is dead (a corpse
                // keeps its last pose for the death animation but not its ring), or
                // its archetype resolved to nothing, in which case there is no
                // geometry to size an instance from and a bar would be a 0-width quad.
                if (!view.IsBound || view.Health <= 0 || view.RadiusMillimetres <= 0)
                {
                    continue;
                }

                var position = view.Hull.position;
                if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z))
                {
                    continue;
                }

                var diameter = view.RadiusMillimetres * UnitView.MillimetresToMetres * 2f;

                if (view.IsSelected)
                {
                    AddRing(in position, diameter);
                }

                if (view.MaxHealth > 0 && IsBarWanted(view))
                {
                    AddBar(in position, barRotation, diameter * HealthBarWidthInDiameters, view.HealthFraction);
                }
            }
        }

        private bool IsBarWanted(UnitView view)
        {
            // Selected, wounded, or the player asked for all of them. The first two
            // are the information the bar carries; the third is a preference, and it
            // is why the rule is a predicate instead of "damaged only".
            return AlwaysShowHealthBars || view.IsSelected || view.Health < view.MaxHealth;
        }

        private void AddRing(in Vector3 position, float diameter)
        {
            if (_ringCount >= Capacity)
            {
                ClippedInstanceCount++;
                return;
            }

            var index = _ringCount++;
            _ringMatrices[index] = Matrix4x4.TRS(
                new Vector3(position.x, RingHeightMetres, position.z),
                Quaternion.identity,
                // The ring quad is authored in the XZ plane, so the two horizontal
                // axes carry the diameter and Y stays 1: a flat quad has no height to
                // scale, and squaring it with the vertical axis would be a no-op that
                // reads like a mistake.
                new Vector3(diameter, 1f, diameter));
            _ringProperties[index] = new Vector4(
                SelectionRingColor.r,
                SelectionRingColor.g,
                SelectionRingColor.b,
                SelectionRingColor.a);
        }

        private void AddBar(
            in Vector3 position,
            Quaternion rotation,
            float width,
            float healthFraction)
        {
            // Validated before anything is counted, so a poisoned fraction costs an
            // instance and not a slot. The ordering matters: NaN fails the first test
            // and infinity the second, where a two-sided clamp would let either one
            // reach the shader and turn into an undefined clip.
            if (!IsFinite(healthFraction))
            {
                return;
            }

            if (healthFraction < 0f)
            {
                healthFraction = 0f;
            }
            else if (healthFraction > 1f)
            {
                healthFraction = 1f;
            }

            if (_barCount >= Capacity)
            {
                ClippedInstanceCount++;
                return;
            }

            var color = Color.Lerp(LowHealthBarColor, FullHealthBarColor, healthFraction);
            var index = _barCount++;

            // The bar quad is authored in the XY plane with its normal on +Z, so the
            // camera rotation is the whole billboard: no per-instance look-at, no
            // Quaternion.LookRotation and no division in the hot path.
            _barMatrices[index] = Matrix4x4.TRS(
                new Vector3(position.x, position.y + HealthBarHeightMetres, position.z),
                rotation,
                new Vector3(width, HealthBarThicknessMetres, 1f));
            _barProperties[index] = new Vector4(healthFraction, color.r, color.g, color.b);
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsRotationUsable(Quaternion rotation)
        {
            var squared = rotation.x * rotation.x +
                rotation.y * rotation.y +
                rotation.z * rotation.z +
                rotation.w * rotation.w;

            // An infinity squared stays infinite and would pass a bare threshold
            // test, and NaN would pass nothing but is stated here so the next reader
            // does not "simplify" the check into one comparison.
            return IsFinite(squared) && squared > RotationEpsilon;
        }
    }
}
