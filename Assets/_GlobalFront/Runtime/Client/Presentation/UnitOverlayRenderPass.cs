using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace GlobalFront.Client.Presentation
{
    /// <summary>
    /// Draws the two overlay batches of <see cref="UnitOverlayBatcher"/> as a URP
    /// render pass (Phase 3, step 3.4, OD-25).
    ///
    /// OD-25 puts the overlays into a <see cref="ScriptableRenderPass"/> on
    /// <see cref="RenderPassEvent.AfterRenderingOpaques"/> for two reasons: the units
    /// are already drawn and depth-tested against by then, and staying inside the
    /// renderer is what keeps the pass alive under Unity 6, where a custom pass has no
    /// execution hook other than <see cref="RecordRenderGraph"/> — the legacy
    /// <c>Execute(ScriptableRenderContext, ref RenderingData)</c> override is gone from
    /// the base class, so recording onto the graph is the only path this pass has.
    ///
    /// The frame budget is spent nowhere: <see cref="UnitOverlayBatcher.BuildBatches"/>
    /// fills arrays that were allocated at construction, and the render function only
    /// hands those arrays to <c>DrawMeshInstanced</c>. One draw per overlay kind while
    /// a batch fits inside <see cref="UnitOverlayBatcher.MaxInstancesPerDraw"/>, one
    /// more per chunk past it — the chunking is forced by the instancing constant
    /// buffer's size, not by a preference, and it is what keeps an oversized selection
    /// from reading past the end of it.
    /// </summary>
    public sealed class UnitOverlayRenderPass : ScriptableRenderPass
    {
        private const string ProfilerTag = "GlobalFront Unit Overlay";

        private readonly UnitOverlayBatcher _batcher;

        // One property block and one scratch pair per batch, deliberately not shared:
        // a command buffer resolves a MaterialPropertyBlock when it plays back, not
        // when it is recorded, so reusing one block for both draws would make the ring
        // draw wear the health bar's instance data.
        private readonly MaterialPropertyBlock _ringProperties = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock _barProperties = new MaterialPropertyBlock();
        private readonly Matrix4x4[] _ringChunk = new Matrix4x4[UnitOverlayBatcher.MaxInstancesPerDraw];
        private readonly Vector4[] _ringDataChunk = new Vector4[UnitOverlayBatcher.MaxInstancesPerDraw];
        private readonly Matrix4x4[] _barChunk = new Matrix4x4[UnitOverlayBatcher.MaxInstancesPerDraw];
        private readonly Vector4[] _barDataChunk = new Vector4[UnitOverlayBatcher.MaxInstancesPerDraw];

        private UnitViewBinder _binder;
        private Material _material;
        private Mesh _ringMesh;
        private Mesh _barMesh;
        private int _ringPassIndex = -1;
        private int _barPassIndex = -1;

        public UnitOverlayRenderPass(UnitOverlayBatcher batcher)
        {
            _batcher = batcher ?? throw new ArgumentNullException(nameof(batcher));

            // The base constructor already defaults to this event; stated anyway,
            // because the whole overlay design depends on drawing after the units.
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
            profilingSampler = new ProfilingSampler(ProfilerTag);
        }

        /// <summary>Overlay buffers this pass draws from.</summary>
        public UnitOverlayBatcher Batcher => _batcher;

        /// <summary>False until a binder and drawable resources are both in place.</summary>
        public bool IsReady => _binder != null && _material != null && _ringMesh != null && _barMesh != null;

        /// <summary>
        /// Points the pass at the match whose views it draws. Null detaches it, which
        /// is what leaving a match looks like from the renderer's side: the pass stays
        /// registered and simply draws nothing.
        /// </summary>
        public void Attach(UnitViewBinder binder) => _binder = binder;

        /// <summary>Installs the meshes, material and pass indices to draw with.</summary>
        public void SetResources(
            Material material,
            Mesh ringMesh,
            Mesh barMesh,
            int ringPassIndex,
            int barPassIndex)
        {
            _material = material;
            _ringMesh = ringMesh;
            _barMesh = barMesh;
            _ringPassIndex = ringPassIndex;
            _barPassIndex = barPassIndex;
        }

        /// <summary>
        /// Everything the recorded draw function needs. Graph-owned and pooled per
        /// frame, so filling it is free; the fields are references to state this pass
        /// already owns, never copies of it.
        /// </summary>
        private class PassData
        {
            internal Material material;
            internal Mesh ringMesh;
            internal Mesh barMesh;
            internal int ringPassIndex;
            internal int barPassIndex;
            internal UnitOverlayBatcher batcher;
            internal Matrix4x4[] ringChunk;
            internal Vector4[] ringDataChunk;
            internal Matrix4x4[] barChunk;
            internal Vector4[] barDataChunk;
            internal MaterialPropertyBlock ringProperties;
            internal MaterialPropertyBlock barProperties;
        }

        /// <inheritdoc />
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!IsReady)
            {
                return;
            }

            var resources = frameData.Get<UniversalResourceData>();
            if (!resources.activeColorTexture.IsValid())
            {
                return;
            }

            var camera = frameData.Get<UniversalCameraData>().camera;
            if (camera == null)
            {
                return;
            }

            // The bars billboard to whichever camera is recording, so the batches are
            // built per camera rather than once per frame: with two views on screen the
            // second would otherwise wear the first one's orientation.
            _batcher.BuildBatches(_binder, camera.transform.rotation);

            var ringCount = _batcher.SelectionRingCount;
            var barCount = _batcher.HealthBarCount;
            if (ringCount == 0 && barCount == 0)
            {
                // No overlays, so no pass: recording an empty one would cost a
                // render-target switch for nothing.
                return;
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(ProfilerTag, out var passData, profilingSampler))
            {
                passData.material = _material;
                passData.ringMesh = _ringMesh;
                passData.barMesh = _barMesh;
                passData.ringPassIndex = _ringPassIndex;
                passData.barPassIndex = _barPassIndex;
                passData.batcher = _batcher;
                passData.ringChunk = _ringChunk;
                passData.ringDataChunk = _ringDataChunk;
                passData.barChunk = _barChunk;
                passData.barDataChunk = _barDataChunk;
                passData.ringProperties = _ringProperties;
                passData.barProperties = _barProperties;

                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
                if (resources.activeDepthTexture.IsValid())
                {
                    // Read only: the quads are depth-tested against the terrain and the
                    // units (a ring behind a hill must not float over it) but never
                    // write depth, so they cannot occlude anything.
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                }

                // The pass declares no renderer list and no sampled texture, so the
                // graph's dead-code elimination would drop it as unused.
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                    ExecutePass(data, context.cmd));
            }
        }

        private static void ExecutePass(PassData data, RasterCommandBuffer cmd)
        {
            var batcher = data.batcher;
            DrawInstances(
                cmd,
                data.ringMesh,
                data.material,
                data.ringPassIndex,
                batcher.SelectionRingMatrixBuffer,
                batcher.SelectionRingPropertyBuffer,
                batcher.SelectionRingCount,
                UnitOverlayGeometry.RingColorProperty,
                data.ringChunk,
                data.ringDataChunk,
                data.ringProperties);
            DrawInstances(
                cmd,
                data.barMesh,
                data.material,
                data.barPassIndex,
                batcher.HealthBarMatrixBuffer,
                batcher.HealthBarPropertyBuffer,
                batcher.HealthBarCount,
                UnitOverlayGeometry.HealthBarParamsProperty,
                data.barChunk,
                data.barDataChunk,
                data.barProperties);
        }

        private static void DrawInstances(
            RasterCommandBuffer cmd,
            Mesh mesh,
            Material material,
            int passIndex,
            Matrix4x4[] sourceMatrices,
            Vector4[] sourceProperties,
            int count,
            int propertyId,
            Matrix4x4[] chunkMatrices,
            Vector4[] chunkProperties,
            MaterialPropertyBlock properties)
        {
            if (mesh == null || material == null || passIndex < 0 || count <= 0)
            {
                return;
            }

            for (var start = 0; start < count; start += UnitOverlayBatcher.MaxInstancesPerDraw)
            {
                var length = Math.Min(UnitOverlayBatcher.MaxInstancesPerDraw, count - start);

                // DrawMeshInstanced always takes the first `count` entries of the
                // array it is handed, so a batch past one draw has to be copied down
                // into the chunk. 250 matrices is 16 KB of memcpy, which is nothing
                // next to the draw it feeds, and it keeps this the only code path.
                Array.Copy(sourceMatrices, start, chunkMatrices, 0, length);
                Array.Copy(sourceProperties, start, chunkProperties, 0, length);
                properties.SetVectorArray(propertyId, chunkProperties);
                cmd.DrawMeshInstanced(mesh, 0, material, passIndex, chunkMatrices, length, properties);
            }
        }
    }
}
