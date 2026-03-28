using Anaglyph.DepthKit.Meshing;
using Anaglyph.XRTemplate;
using UnityEngine;

namespace Anaglyph.EdgeServer
{
    // ── EdgeModeController ────────────────────────────────────────────────────────
    // Switches the scene between two processing modes at startup (and at runtime
    // via SetEdgeMode).
    //
    // Standalone mode  — EnvironmentMapper + ChunkManager run on-device as normal.
    //                    Edge components (EdgeClient, DepthFrameSender,
    //                    NetworkMeshApplier) are disabled.
    //
    // Edge mode        — EnvironmentMapper + ChunkManager are disabled so the Quest
    //                    GPU is freed.  EdgeClient connects to the Mac; depth frames
    //                    are sent; mesh chunks are received and applied to the scene.
    //
    // Scene wiring (assign all references in the Inspector):
    //   - settings          EdgeServerSettings asset
    //   - environmentMapper EnvironmentMapper component in the scene
    //   - chunkManager      ChunkManager component in the scene
    //   - edgeClient        EdgeClient component (same GameObject or child)
    //   - depthSender       DepthFrameSender component
    //   - meshApplier       NetworkMeshApplier component

    public class EdgeModeController : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private EdgeServerSettings settings;

        [Header("Standalone components (disabled in edge mode)")]
        [SerializeField] private EnvironmentMapper environmentMapper;
        [SerializeField] private ChunkManager      chunkManager;

        [Header("Edge components (disabled in standalone mode)")]
        [SerializeField] private EdgeClient         edgeClient;
        [SerializeField] private DepthFrameSender   depthSender;
        [SerializeField] private NetworkMeshApplier meshApplier;

        // ── Public state ──────────────────────────────────────────────────────
        public bool IsEdgeMode { get; private set; }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Start()
        {
            SetEdgeMode(settings != null && settings.startInEdgeMode);
        }

        // ── Mode switch ───────────────────────────────────────────────────────

        public void SetEdgeMode(bool edgeMode)
        {
            IsEdgeMode = edgeMode;

            // Standalone pipeline
            if (environmentMapper) environmentMapper.enabled = !edgeMode;
            if (chunkManager)      chunkManager.enabled      = !edgeMode;

            // Edge pipeline
            if (edgeClient)   edgeClient.enabled   = edgeMode;
            if (depthSender)  depthSender.enabled  = edgeMode;
            if (meshApplier)  meshApplier.enabled  = edgeMode;

            if (edgeMode)
            {
                // Clear any previously received chunks when switching in
                meshApplier?.ClearAll();
                edgeClient?.Connect();
                Debug.Log("[EdgeModeController] Edge mode ON — " +
                          $"connecting to {settings?.serverIP}:{settings?.port}");
            }
            else
            {
                edgeClient?.Disconnect();
                meshApplier?.ClearAll();
                Debug.Log("[EdgeModeController] Standalone mode ON");
            }
        }

        /// Toggle for UI buttons / debug inspector.
        public void ToggleEdgeMode() => SetEdgeMode(!IsEdgeMode);
    }
}
