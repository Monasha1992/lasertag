using System.Collections.Generic;
using UnityEngine;

namespace Anaglyph.EdgeServer
{
    // ── NetworkMeshApplier ────────────────────────────────────────────────────────
    // Receives MeshChunkData events from EdgeClient and applies them to chunk
    // GameObjects in the scene.
    //
    // Key design decisions:
    //
    //   Buffered-per-frame updates:
    //     EdgeClient.Update() can deliver many chunk packets in a single frame
    //     (network burst after a delay, or multiple depth frames queued up).
    //     Calling mesh.Clear() + rebuild for the same chunk N times per frame
    //     causes needless CPU work and triggers N MeshCollider bakes.
    //     Instead, OnChunkReceived stores only the LATEST packet per chunk key
    //     in pendingUpdates; Update() applies each key exactly once per frame.
    //
    //   Deferred MeshCollider baking:
    //     MeshCollider.sharedMesh triggers synchronous physics mesh cooking on
    //     the main thread — expensive enough to stall a frame.  We defer it to
    //     a periodic call (every colliderBakeInterval seconds) so normal
    //     rendering frames are unaffected.  For a thesis rendering comparison
    //     collider accuracy at 5 Hz is more than sufficient.
    //
    //   No chunk overlap:
    //     The server must be started with overlap=0.0.  Overlapping mesh regions
    //     from adjacent chunks occupy the same world-space volume and z-fight.

    [RequireComponent(typeof(EdgeClient))]
    public class NetworkMeshApplier : MonoBehaviour
    {
        [SerializeField] private EdgeClient client;

        [Tooltip("Material for received mesh chunks. " +
                 "Use a double-sided or back-face-visible variant to be safe.")]
        [SerializeField] private Material chunkMaterial;

        [Tooltip("How often (seconds) the MeshCollider is rebaked. " +
                 "Baking is expensive; once per second is sufficient for gameplay.")]
        [SerializeField] private float colliderBakeInterval = 1f;

        // ── Chunk tracking ────────────────────────────────────────────────────
        private readonly Dictionary<Vector3Int, ChunkEntry> chunks = new();

        // Latest incoming packet per chunk key — filled by OnChunkReceived (called
        // from EdgeClient.Update), consumed by this.Update() later in the same frame.
        private readonly Dictionary<Vector3Int, MeshChunkData> pendingUpdates = new();

        private struct ChunkEntry
        {
            public GameObject  go;
            public Mesh        mesh;
            public MeshCollider collider;
            public float       lastColliderBakeTime;
        }

        // ── Stats ─────────────────────────────────────────────────────────────
        public int  ChunkCount    { get; private set; }
        public long LastRttMs     { get; private set; }
        public long LastComputeMs { get; private set; }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Reset() => client = GetComponent<EdgeClient>();

        private void OnEnable()
        {
            if (client) client.OnMeshChunkReceived += OnChunkReceived;
        }

        private void OnDisable()
        {
            if (client) client.OnMeshChunkReceived -= OnChunkReceived;
        }

        // ── Per-frame: apply buffered updates ─────────────────────────────────
        // Runs AFTER EdgeClient.Update() (EdgeClient has ExecutionOrder -20).

        private void Update()
        {
            if (pendingUpdates.Count == 0) return;

            foreach (var (key, data) in pendingUpdates)
                ApplyChunk(key, data);

            pendingUpdates.Clear();
        }

        // ── Incoming chunk (called from EdgeClient.Update → mainQueue) ────────

        private void OnChunkReceived(MeshChunkData data)
        {
            if (data.vertices == null || data.vertices.Length < 3 ||
                data.indices  == null || data.indices.Length  < 3)
            {
                Debug.LogWarning($"[NetworkMeshApplier] Received degenerate chunk at " +
                                 $"worldPos={data.worldPos}  " +
                                 $"verts={data.vertices?.Length ?? 0}  " +
                                 $"idx={data.indices?.Length ?? 0} — skipping");
                return;
            }

            LastRttMs     = data.RoundTripMs;
            LastComputeMs = data.ServerComputeMs;

            // Keep only the latest packet per chunk key (discard older arrivals)
            Vector3Int key = ChunkKey(data.worldPos);
            pendingUpdates[key] = data;
        }

        // ── Mesh application ──────────────────────────────────────────────────

        private void ApplyChunk(Vector3Int key, MeshChunkData data)
        {
            if (!chunks.TryGetValue(key, out ChunkEntry entry))
                entry = CreateChunkEntry(data.worldPos, key);

            Mesh mesh = entry.mesh;
            mesh.Clear();
            mesh.SetVertices(data.vertices);
            mesh.SetNormals(data.normals);
            mesh.SetIndices(data.indices, MeshTopology.Triangles, 0);
            mesh.RecalculateBounds();

            // Defer collider baking — only rebake if enough time has passed
            float now = Time.realtimeSinceStartup;
            if (now - entry.lastColliderBakeTime >= colliderBakeInterval)
            {
                entry.collider.sharedMesh = mesh;
                entry.lastColliderBakeTime = now;
            }

            // Write back (struct)
            entry.mesh = mesh;
            chunks[key] = entry;

            Debug.Log($"[NetworkMeshApplier] Chunk {key}  " +
                      $"worldPos={data.worldPos}  " +
                      $"verts={data.vertices.Length}  tris={data.indices.Length / 3}  " +
                      $"bounds={mesh.bounds.size}  " +
                      $"rtt={data.RoundTripMs}ms  compute={data.ServerComputeMs}ms");
        }

        // ── Cleanup ───────────────────────────────────────────────────────────

        public void ClearAll()
        {
            pendingUpdates.Clear();

            foreach (var entry in chunks.Values)
            {
                if (entry.go)   Destroy(entry.go);
                if (entry.mesh) Destroy(entry.mesh);
            }
            chunks.Clear();
            ChunkCount = 0;
            Debug.Log("[NetworkMeshApplier] All chunks cleared");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private ChunkEntry CreateChunkEntry(Vector3 worldPos, Vector3Int key)
        {
            var go   = new GameObject($"NetChunk_({key.x},{key.y},{key.z})");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.position = worldPos;

            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            var mc = go.AddComponent<MeshCollider>();

            var mesh = new Mesh { name = $"NetMesh_({key.x},{key.y},{key.z})" };
            mesh.MarkDynamic();

            mf.sharedMesh     = mesh;
            mr.sharedMaterial = chunkMaterial;

            Debug.Log($"[NetworkMeshApplier] Created chunk {key} at world {worldPos}");

            var entry = new ChunkEntry
            {
                go                  = go,
                mesh                = mesh,
                collider            = mc,
                lastColliderBakeTime = -colliderBakeInterval  // allow bake on first update
            };
            chunks[key] = entry;
            ChunkCount++;
            return entry;
        }

        /// Round worldPos to 1 cm precision for a stable dictionary key.
        private static Vector3Int ChunkKey(Vector3 pos) => new Vector3Int(
            Mathf.RoundToInt(pos.x * 100),
            Mathf.RoundToInt(pos.y * 100),
            Mathf.RoundToInt(pos.z * 100));
    }
}
