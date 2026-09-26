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
    /// from reading past the end of it. Each chunk gets its own arrays and its own
    /// <see cref="MaterialPropertyBlock"/> (see <see cref="PrepareChunks"/>), because
    /// the instance data a recorded draw carries is resolved at playback rather than
    /// at record time.
    /// </summary>
    public sealed class UnitOverlayRenderPass : ScriptableRenderPass
    {
        private const string ProfilerTag = "GlobalFront Unit Overlay";

        /// <summary>
        /// Chunks one batch can ever split into: <see cref="UnitOverlayBatcher.MaxCapacity"/>
        /// instances at <see cref="UnitOverlayBatcher.MaxInstancesPerDraw"/> per draw. It is
        /// the worst case of <see cref="GetChunkCount"/>, and the bound a maximally sized
        /// pass has to have scratch for.
        /// </summary>
        public const int MaxChunksPerBatch =
            (UnitOverlayBatcher.MaxCapacity + UnitOverlayBatcher.MaxInstancesPerDraw - 1) /
            UnitOverlayBatcher.MaxInstancesPerDraw;

        private readonly UnitOverlayBatcher _batcher;

        // One scratch set per chunk, deliberately not shared: a command buffer resolves a
        // MaterialPropertyBlock when it plays back, not when it is recorded, so a single
        // block (or a single matrix array) used by two chunks would make the first draw
        // wear the second chunk's instance data — and sharing one set between the ring and
        // bar draws would make the rings wear the health bars'.
        //
        // Sized from the batcher's own capacity rather than from MaxChunksPerBatch: the
        // batcher cannot emit more instances than it can hold, so this is the exact bound,
        // and reserving the 4096-instance worst case for a 512-slot batcher would put
        // ~660 KB of dead scratch behind every renderer.
        internal readonly OverlayChunkBuffers[] RingChunks;
        internal readonly OverlayChunkBuffers[] BarChunks;

        private UnitViewBinder _binder;
        private Material _material;
        private Mesh _ringMesh;
        private Mesh _barMesh;
        private bool _ownsMaterial;
        private int _ringPassIndex = -1;
        private int _barPassIndex = -1;

        public UnitOverlayRenderPass(UnitOverlayBatcher batcher)
        {
            _batcher = batcher ?? throw new ArgumentNullException(nameof(batcher));

            RingChunks = CreateChunks(GetChunkCount(_batcher.Capacity));
            BarChunks = CreateChunks(GetChunkCount(_batcher.Capacity));

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

        /// <summary>
        /// Installs the meshes, material and pass indices to draw with.
        /// <paramref name="ownsMaterial"/> says whether the material came from
        /// <see cref="UnitOverlayGeometry"/> or from the renderer's own assignment, and
        /// only the former may be destroyed with the meshes.
        /// </summary>
        public void SetResources(
            Material material,
            Mesh ringMesh,
            Mesh barMesh,
            bool ownsMaterial,
            int ringPassIndex,
            int barPassIndex)
        {
            _material = material;
            _ringMesh = ringMesh;
            _barMesh = barMesh;
            _ownsMaterial = ownsMaterial;
            _ringPassIndex = ringPassIndex;
            _barPassIndex = barPassIndex;
        }

        /// <summary>
        /// Drops and destroys whatever <see cref="SetResources"/> installed. The meshes
        /// and, when the feature built it itself, the material are native objects owned
        /// by this pass, so a rebuild or a disposal that only drops the references would
        /// leak one pair per script reload for the length of the session.
        /// </summary>
        public void ReleaseResources()
        {
            CoreUtils.Destroy(_ringMesh);
            CoreUtils.Destroy(_barMesh);
            if (_ownsMaterial)
            {
                CoreUtils.Destroy(_material);
            }

            _material = null;
            _ringMesh = null;
            _barMesh = null;
            _ownsMaterial = false;
            _ringPassIndex = -1;
            _barPassIndex = -1;
        }

        /// <summary>
        /// Instance data for one <c>DrawMeshInstanced</c> call. The arrays are the chunk
        /// the draw reads and the block is the one recorded with it; <see cref="Count"/> is
        /// how many entries <see cref="UnitOverlayRenderPass.PrepareChunks"/> filled.
        /// </summary>
        internal sealed class OverlayChunkBuffers
        {
            internal readonly Matrix4x4[] Matrices = new Matrix4x4[UnitOverlayBatcher.MaxInstancesPerDraw];
            internal readonly Vector4[] Properties = new Vector4[UnitOverlayBatcher.MaxInstancesPerDraw];
            internal readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
            internal int Count;
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
            internal int ringCount;
            internal int barCount;
            internal OverlayChunkBuffers[] ringChunks;
            internal OverlayChunkBuffers[] barChunks;
        }

        /// <inheritdoc />
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!IsReady)
            {
                return;
            }

            // Scene views, material previews and reflection probes all run this pass when
            // their renderer carries the feature. Each of them would rebuild the batches
            // for its own camera — and because the instance buffers are resolved at
            // playback, the last one to record decides what the game camera draws. A probe
            // would also put health bars into a reflection cubemap. Game cameras only.
            var cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData.cameraType != CameraType.Game)
            {
                return;
            }

            var resources = frameData.Get<UniversalResourceData>();
            if (!resources.activeColorTexture.IsValid())
            {
                return;
            }

            var camera = cameraData.camera;
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
                // The counts are snapshotted here rather than re-read at execution: the
                // batcher is shared state, and by playback time a later camera may have
                // rebuilt it.
                passData.ringCount = ringCount;
                passData.barCount = barCount;
                passData.ringChunks = RingChunks;
                passData.barChunks = BarChunks;

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
                data.ringCount,
                UnitOverlayGeometry.RingColorProperty,
                data.ringChunks);
            DrawInstances(
                cmd,
                data.barMesh,
                data.material,
                data.barPassIndex,
                batcher.HealthBarMatrixBuffer,
                batcher.HealthBarPropertyBuffer,
                data.barCount,
                UnitOverlayGeometry.HealthBarParamsProperty,
                data.barChunks);
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
            OverlayChunkBuffers[] chunks)
        {
            if (mesh == null || material == null || passIndex < 0 || count <= 0)
            {
                return;
            }

            var chunkCount = PrepareChunks(sourceMatrices, sourceProperties, count, propertyId, chunks);
            for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                var chunk = chunks[chunkIndex];
                cmd.DrawMeshInstanced(mesh, 0, material, passIndex, chunk.Matrices, chunk.Count, chunk.Block);
            }
        }

        /// <summary>
        /// Splits <paramref name="count"/> instances into <paramref name="chunks"/>, one
        /// draw each, and returns how many chunks were filled.
        ///
        /// DrawMeshInstanced always takes the first <c>count</c> entries of the array it is
        /// handed, so a batch past one draw has to be copied down into a chunk. 250 matrices
        /// is 16 KB of memcpy, which is nothing next to the draw it feeds, and it keeps this
        /// the only code path. Each chunk writes its own arrays and its own property block:
        /// the block is resolved when the command buffer plays back, so a shared one would
        /// leave every draw of the batch reading the last chunk's data.
        /// </summary>
        internal static int PrepareChunks(
            Matrix4x4[] sourceMatrices,
            Vector4[] sourceProperties,
            int count,
            int propertyId,
            OverlayChunkBuffers[] chunks)
        {
            var chunkCount = GetChunkCount(count);
            for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                var start = chunkIndex * UnitOverlayBatcher.MaxInstancesPerDraw;
                var length = Math.Min(UnitOverlayBatcher.MaxInstancesPerDraw, count - start);
                var chunk = chunks[chunkIndex];

                Array.Copy(sourceMatrices, start, chunk.Matrices, 0, length);
                Array.Copy(sourceProperties, start, chunk.Properties, 0, length);
                chunk.Block.SetVectorArray(propertyId, chunk.Properties);
                chunk.Count = length;
            }

            return chunkCount;
        }

        /// <summary>
        /// Chunks an instance count splits into. Never larger than the buffer arrays this
        /// pass was constructed with, because a batcher cannot emit past its own capacity.
        /// </summary>
        internal static int GetChunkCount(int instanceCount) =>
            (instanceCount + UnitOverlayBatcher.MaxInstancesPerDraw - 1) / UnitOverlayBatcher.MaxInstancesPerDraw;

        private static OverlayChunkBuffers[] CreateChunks(int count)
        {
            var chunks = new OverlayChunkBuffers[count];
            for (var index = 0; index < count; index++)
            {
                chunks[index] = new OverlayChunkBuffers();
            }

            return chunks;
        }
    }
}
