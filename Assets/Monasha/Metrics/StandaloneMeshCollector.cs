using Anaglyph.DepthKit.Meshing;
using Anaglyph.XRTemplate;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// StandaloneMeshCollector.cs — Emits `mesh` rows in standalone-architecture mode
//
// WHAT THIS FILE DOES:
//   The edge architecture writes one `mesh` CSV row per round-trip completion
//   (handled by EdgeServerClient.ApplyMeshData → MetricsLogger.LogMeshSample).
//   The standalone architecture has no equivalent — meshing happens locally
//   on chunks and there's no single "mesh applied" moment. Without parity, RQ1
//   has no data and the comparison is one-sided.
//
//   This collector closes that gap. It subscribes to EnvironmentMapper.Updated
//   (which fires every local TSDF integration cycle, ~5 Hz by default) and
//   writes a `mesh` row summarising the current state of the chunk mesh:
//     - vertex_count    — sum of ChunkManager's chunk vertex counts
//     - triangle_count  — sum of ChunkManager's chunk triangle counts
//     - rtt_ms          — 0 (no network)
//     - payload_bytes   — 0
//     - response_bytes  — 0
//     - pos_drift_m     — 0 (no flight-time, no stale-mesh discard)
//     - rot_drift_deg   — 0
//     - discarded       — false (standalone never discards)
//     - server_ts_ms    — 0
//
//   The MetricsLogger.LogMeshSample call also automatically emits the
//   mesh_churn event (delta vs previous sample), giving objective visual-
//   stability data for the standalone build without any extra code.
//
// WHEN TO ATTACH:
//   Add this component anywhere in the scene that lives in standalone mode
//   (typically MetricsRoot). It deactivates itself on Edge builds via the
//   EnableInStandalone helper (or by gating on ArchitectureManager.Mode
//   internally).
//
//   In edge mode, EnvironmentMapper.Updated still fires (the update loop
//   skips ApplyScan but still invokes the event), so this collector would
//   emit spurious zero-count mesh rows if left enabled. To avoid that, the
//   handler is guarded by the ArchitectureManager.Mode check below.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    public class StandaloneMeshCollector : MonoBehaviour
    {
        // Cached at first use; refreshed if null (handles late-spawn rig prefab).
        private ChunkManager chunkManager;

        private void OnEnable()
        {
            // Subscribe to the local TSDF integration completion event.
            // The handler is safe even before EnvironmentMapper.Instance is
            // set — it'll just early-return on the null check below.
            if (EnvironmentMapper.Instance != null)
                EnvironmentMapper.Instance.Updated += OnEnvironmentUpdated;
        }

        private void Start()
        {
            // Late-bind in case EnvironmentMapper.Instance wasn't ready at OnEnable
            // (which happens when this script Awake's BEFORE the runtime-spawned
            // rig has finished initialising).
            if (EnvironmentMapper.Instance != null)
            {
                EnvironmentMapper.Instance.Updated -= OnEnvironmentUpdated;
                EnvironmentMapper.Instance.Updated += OnEnvironmentUpdated;
            }
        }

        private void OnDisable()
        {
            if (EnvironmentMapper.Instance != null)
                EnvironmentMapper.Instance.Updated -= OnEnvironmentUpdated;
        }

        // ─────────────────────────────────────────────────────────────────────
        // OnEnvironmentUpdated — Fires every local TSDF integration cycle
        //
        // In edge mode, EnvironmentMapper.UpdateLoop still raises Updated
        // (because some shaders listen for "depth was processed" regardless),
        // so we filter on architecture here. Belt-and-braces in case the
        // EnableInStandalone helper isn't on this GameObject.
        // ─────────────────────────────────────────────────────────────────────
        private void OnEnvironmentUpdated()
        {
            if (MetricsLogger.Instance == null || !MetricsLogger.Instance.IsSessionActive)
                return;

            // Skip if we're in edge mode — the edge client owns the mesh row.
            // (ArchitectureManager may be null in dev/test scenes; in that
            // case we fall through and log, which is correct for standalone-
            // dev iteration.)
            if (ArchitectureManager.Instance != null && ArchitectureManager.Instance.IsEdge)
                return;

            // Lazy-cache the ChunkManager reference.
            if (chunkManager == null) chunkManager = ChunkManager.Instance;

            int verts = chunkManager != null ? chunkManager.TotalVertexCount   : 0;
            int tris  = chunkManager != null ? chunkManager.TotalTriangleCount : 0;

            // Emit the mesh row. Edge-specific fields are zeroed.
            MetricsLogger.Instance.LogMeshSample(
                rttMs:         0,
                payloadBytes:  0,
                responseBytes: 0,
                vertexCount:   verts,
                triangleCount: tris,
                posDriftM:     0f,
                rotDriftDeg:   0f,
                discarded:     false,
                serverTsMs:    0);
        }
    }
}
