using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
// Alias for the static class Anaglyph.Anaglyph (name collides with its namespace).
using AnaglyphCore = Anaglyph.Anaglyph;

// ─────────────────────────────────────────────────────────────────────────────
// EdgeChunkStore.cs — Client-side persistent cache of edge-server mesh chunks
//
// WHAT THIS FILE DOES:
//   Holds the dictionary of chunk meshes received from the Mac via the 0x04
//   chunk-batch protocol. Each chunk is a child GameObject with its own
//   MeshFilter / MeshRenderer / MeshCollider, keyed by the chunk's grid
//   coordinate. An arriving chunk replaces ONLY its own cell — every other
//   cell keeps its triangles, so geometry persists when the camera looks
//   away. This mirrors the standalone path's ChunkManager behaviour and is
//   what makes the two architectures structurally comparable in the study.
//
// HOW IT'S WIRED:
//   EdgeServerClient auto-adds this component to the EdgeMesh GameObject on
//   first use (no manual Inspector step). Chunk children inherit the EdgeMesh
//   GameObject's layer (6, "Chunk" — so bullets hit them) and its material.
//
// MESH UPLOAD:
//   Same zero-copy path as the legacy single-mesh route: declared vertex
//   layout (pos.xyz + normal.xyz, 24 B), raw byte upload via
//   SetVertexBufferData/SetIndexBufferData with all validation skipped.
//
// COLLIDER BAKING (and why each chunk has TWO meshes):
//   Physics.BakeMesh cooks on a background Task; the mesh's data must not be
//   rewritten mid-cook or the result is torn. Each chunk therefore owns two
//   mesh buffers and alternates uploads between them: a new arrival writes
//   the buffer that is NOT being baked, swaps the visual mesh immediately,
//   and queues a bake for the collider. One bake runs at a time (bounded
//   CPU); the cooked result is assigned on the main thread when ready.
//   Collider therefore lags the visuals by at most one bake (~10-50 ms per
//   chunk — chunks are small).
//
// DEBUG VISIBILITY:
//   Chunk renderers are on the "Chunk" layer. Visibility is driven by the
//   Camera Culling Mask (typically via the "Show Mesh" setting).
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.EdgeServer
{
    public class EdgeChunkStore : MonoBehaviour
    {
        // ── Per-chunk record ──────────────────────────────────────────────────
        private class ChunkEntry
        {
            public GameObject   go;
            public MeshFilter   filter;
            public MeshRenderer renderer;
            public MeshCollider collider;

            // Double-buffered meshes — see header comment. `current` is the
            // index of the mesh currently assigned to the MeshFilter;
            // `baking` is the index being cooked on a worker (-1 = none).
            public Mesh[] meshes = new Mesh[2];
            public int    current = 0;
            public int    baking  = -1;

            // Size guards so SetVertexBufferParams (a GPU realloc) only runs
            // when this buffer's capacity actually changed.
            // Declared GPU buffer capacity per buffer (pow2, grow-only).
            // SetVertexBufferParams is a GPU reallocation — calling it only
            // when the pow2 capacity grows (instead of on every size change)
            // removes ~80 reallocs/sec that caused hitches on Adreno.
            public int[] capVerts = { 0, 0 };
            public int[] capIdx   = { 0, 0 };

            // Actual in-use counts per buffer. mesh.vertexCount now equals
            // the DECLARED capacity (bigger than used), so totals must come
            // from these, not from the Mesh object.
            public int[] actualVerts = { 0, 0 };
            public int[] actualTris  = { 0, 0 };

            public bool  hasGeometry;          // false once cleared (vertCount == 0)
            public bool  rebakeQueued;         // arrived while its bake was in flight
        }

        private readonly Dictionary<Vector3Int, ChunkEntry> chunks = new();

        // Chunks waiting for a collider bake, FIFO. One bake task in flight.
        private readonly Queue<ChunkEntry> bakeQueue = new();
        [Tooltip("Minimum seconds between collider-bake starts across all chunks. " +
                 "Caps PhysX cook + sharedMesh-assign traffic (each assign costs " +
                 "main-thread time): 0.05 = at most 20 collider updates/sec. The " +
                 "first implementation re-baked every arriving chunk (~80/sec) " +
                 "which caused the freezes. Visual meshes still update at full " +
                 "rate; only collider refresh is throttled.")]
        [SerializeField] private float minBakeIntervalSec = 0.05f;
        private float lastBakeStartTime = -999f;
        private Task bakeTask;
        private ChunkEntry bakingEntry;

        // Public stats for the HUD / metrics
        public int ChunkCount { get; private set; }
        public int TotalVertexCount { get; private set; }
        public int TotalTriangleCount { get; private set; }

        // ── Shared constants (mirror EdgeServerClient's private consts) ───────
        private static readonly VertexAttributeDescriptor[] VertexLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal,   VertexAttributeFormat.Float32, 3),
        };

        private const MeshUpdateFlags FastMeshFlags =
            MeshUpdateFlags.DontValidateIndices   |
            MeshUpdateFlags.DontResetBoneBounds   |
            MeshUpdateFlags.DontNotifyMeshUsers   |
            MeshUpdateFlags.DontRecalculateBounds;

        // MUST match EdgeServerClient.FastCookingOptions and the options the
        // bake call uses — a mismatch forces a main-thread re-cook on assign.
        private const MeshColliderCookingOptions FastCookingOptions =
            MeshColliderCookingOptions.CookForFasterSimulation |
            MeshColliderCookingOptions.EnableMeshCleaning       |
            MeshColliderCookingOptions.WeldColocatedVertices    |
            MeshColliderCookingOptions.UseFastMidphase;

        // Large fixed bounds — skips RecalculateBounds. Chunks are ≤3.4 m but
        // a generous constant is culling-safe and costs nothing at room scale.
        private static readonly Bounds ChunkBounds = new Bounds(Vector3.zero, new Vector3(100f, 100f, 100f));

        // Template material taken from the EdgeMesh GameObject's own renderer
        // (this component is added to that GameObject by EdgeServerClient).
        private Material chunkMaterial;

        private void Awake()
        {
            var ownRenderer = GetComponent<MeshRenderer>();
            chunkMaterial = ownRenderer != null ? ownRenderer.sharedMaterial : null;
        }

        private void OnDestroy()
        {
        }

        // ─────────────────────────────────────────────────────────────────────
        // ApplyChunk — Upload one received chunk into its grid cell.
        //
        // `data` is the raw batch payload buffer (pooled — valid only during
        // this call). vertStart/indexStart are byte offsets into it.
        // vertCount == 0 clears the cell (geometry disappeared on the server).
        // ─────────────────────────────────────────────────────────────────────
        public void ApplyChunk(Vector3Int coord, byte[] data,
                               int vertStart, int vertCount,
                               int indexStart, int idxCount)
        {
            if (!chunks.TryGetValue(coord, out var entry))
            {
                // Don't create GameObjects for empty cells we never stored.
                if (vertCount == 0) return;
                entry = CreateEntry(coord);
                chunks.Add(coord, entry);
                ChunkCount = chunks.Count;
            }

            // ── Empty chunk → clear the cell ──────────────────────────────────
            if (vertCount == 0 || idxCount == 0)
            {
                if (entry.hasGeometry)
                {
                    TotalVertexCount   -= entry.actualVerts[entry.current];
                    TotalTriangleCount -= entry.actualTris[entry.current];
                    entry.actualVerts[entry.current] = 0;
                    entry.actualTris[entry.current]  = 0;

                    entry.hasGeometry = false;
                    entry.renderer.enabled = false;
                    entry.collider.sharedMesh = null;
                }
                return;
            }

            // ── Pick the upload buffer: never the one being baked ─────────────
            // Normally alternate to the non-displayed buffer. If that buffer is
            // mid-cook on the worker thread (an earlier version of this chunk),
            // overwrite the displayed mesh in place instead — visually a brief
            // in-place update, and the baking buffer stays untouched.
            int write = 1 - entry.current;
            if (write == entry.baking)
                write = entry.current;

            var mesh = entry.meshes[write];
            if (mesh == null)
            {
                mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                mesh.MarkDynamic();
                entry.meshes[write] = mesh;
            }

            // Track totals (replace this cell's old contribution)
            if (entry.hasGeometry)
            {
                TotalVertexCount   -= entry.actualVerts[entry.current];
                TotalTriangleCount -= entry.actualTris[entry.current];
            }

            int vertexBytes = vertCount * 24;
            int indexBytes  = idxCount * 4;

            // (Re)allocate GPU buffers only when capacity changed — same
            // size-guard trick as the legacy path.
            // Grow-only pow2 capacity: chunk sizes jitter every arrival (depth
            // noise), so exact-size tracking reallocated constantly. With
            // headroom, reallocs stop after warm-up; uploads/submesh use the
            // actual counts so rendering is unaffected.
            int vertCap = Mathf.NextPowerOfTwo(vertCount);
            int idxCap  = Mathf.NextPowerOfTwo(idxCount);
            if (entry.capVerts[write] < vertCap)
            {
                mesh.SetVertexBufferParams(vertCap, VertexLayout);
                entry.capVerts[write] = vertCap;
            }
            if (entry.capIdx[write] < idxCap)
            {
                mesh.SetIndexBufferParams(idxCap, IndexFormat.UInt32);
                entry.capIdx[write] = idxCap;
            }

            mesh.SetVertexBufferData(data, vertStart,  0, vertexBytes, 0, FastMeshFlags);
            mesh.SetIndexBufferData (data, indexStart, 0, indexBytes,     FastMeshFlags);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, idxCount, MeshTopology.Triangles), FastMeshFlags);
            mesh.bounds = ChunkBounds;

            entry.actualVerts[write] = vertCount;
            entry.actualTris[write]  = idxCount / 3;

            // Swap the visual immediately; collider follows after its bake.
            entry.current = write;
            entry.filter.sharedMesh = mesh;
            entry.hasGeometry = true;
            entry.renderer.enabled = true;

            TotalVertexCount   += vertCount;
            TotalTriangleCount += idxCount / 3;

            // Queue a collider bake (deduped — one pending bake per chunk).
            if (!entry.rebakeQueued && entry.baking < 0)
            {
                entry.rebakeQueued = true;
                bakeQueue.Enqueue(entry);
            }
            else
            {
                // A bake is in flight or already queued — mark so the entry
                // re-queues itself once the current bake completes.
                entry.rebakeQueued = true;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Update — Poll the in-flight bake; start the next one when free.
        // ─────────────────────────────────────────────────────────────────────
        private void Update()
        {
            // ── Finish a completed bake ───────────────────────────────────────
            if (bakeTask != null && bakeTask.IsCompleted)
            {
                if (bakeTask.IsFaulted)
                {
                    Debug.LogWarning($"[EdgeChunkStore] BakeMesh failed: {bakeTask.Exception?.GetBaseException().Message}");
                }
                else if (bakingEntry != null && bakingEntry.hasGeometry)
                {
                    var baked = bakingEntry.meshes[bakingEntry.baking];
                    // Assign the cooked BVH — near-free since the cook is cached.
                    bakingEntry.collider.sharedMesh = null;
                    bakingEntry.collider.sharedMesh = baked;
                }

                if (bakingEntry != null)
                {
                    int bakedIdx = bakingEntry.baking;
                    bakingEntry.baking = -1;

                    // If a newer version of this chunk arrived during the bake,
                    // bake again so the collider catches up to the visuals.
                    if (bakingEntry.rebakeQueued && bakingEntry.current != bakedIdx)
                        bakeQueue.Enqueue(bakingEntry);
                    else
                        bakingEntry.rebakeQueued = false;
                }

                bakeTask    = null;
                bakingEntry = null;
            }

            // ── Start the next queued bake ────────────────────────────────────
            while (bakeTask == null && bakeQueue.Count > 0
                   && Time.realtimeSinceStartup - lastBakeStartTime >= minBakeIntervalSec)
            {
                var entry = bakeQueue.Dequeue();
                if (!entry.hasGeometry) { entry.rebakeQueued = false; continue; }

                entry.baking      = entry.current;
                entry.rebakeQueued = false;
                bakingEntry        = entry;

                int bakeId = entry.meshes[entry.current].GetInstanceID();
                // Physics.BakeMesh is thread-safe; cooking options MUST match
                // the MeshCollider's (set in CreateEntry) or PhysX re-cooks on
                // the main thread at assignment time.
                lastBakeStartTime = Time.realtimeSinceStartup;
                bakeTask = Task.Run(() => Physics.BakeMesh(bakeId, /* convex: */ false, FastCookingOptions));
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // CreateEntry — Build the child GameObject for a new grid cell.
        // Inherits this GameObject's layer (Chunk, 6) and material.
        // ─────────────────────────────────────────────────────────────────────
        private ChunkEntry CreateEntry(Vector3Int coord)
        {
            var go = new GameObject($"EdgeChunk_{coord.x}_{coord.y}_{coord.z}")
            {
                layer = gameObject.layer
            };
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;   // vertices are world-space already
            go.transform.localRotation = Quaternion.identity;

            var entry = new ChunkEntry
            {
                go       = go,
                filter   = go.AddComponent<MeshFilter>(),
                renderer = go.AddComponent<MeshRenderer>(),
                collider = go.AddComponent<MeshCollider>(),
            };

            entry.renderer.sharedMaterial = chunkMaterial;
            entry.renderer.enabled        = false;        // until geometry arrives
            entry.collider.convex         = false;        // room geometry is non-convex
            entry.collider.cookingOptions = FastCookingOptions;

            return entry;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ClearAll — Drop every cached chunk (e.g. on disconnect, if desired).
        // Not called automatically — persistence across reconnects is usually
        // what you want, since the Mac's TSDF volume also persists.
        // ─────────────────────────────────────────────────────────────────────
        public void ClearAll()
        {
            foreach (var entry in chunks.Values)
            {
                if (entry.meshes[0] != null) Destroy(entry.meshes[0]);
                if (entry.meshes[1] != null) Destroy(entry.meshes[1]);
                Destroy(entry.go);
            }
            chunks.Clear();
            bakeQueue.Clear();
            ChunkCount = 0;
            TotalVertexCount = 0;
            TotalTriangleCount = 0;
        }
    }
}
