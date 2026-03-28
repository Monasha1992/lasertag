using UnityEngine;

namespace Anaglyph.EdgeServer
{
    // ── Edge server configuration ─────────────────────────────────────────────────
    // Create via: Assets → Create → Edge Server → Settings
    // Assign the asset to EdgeModeController in the scene.

    [CreateAssetMenu(fileName = "EdgeServerSettings", menuName = "Edge Server/Settings")]
    public class EdgeServerSettings : ScriptableObject
    {
        [Tooltip("IP address of the Mac running EdgeMetalServer.")]
        public string serverIP = "192.168.1.100";

        [Tooltip("TCP port — must match --port on the server (default 9555).")]
        public int port = 9555;

        [Tooltip("How many depth frames per second to send to the server. " +
                 "Lower values reduce bandwidth; higher values reduce mesh staleness.")]
        [Range(1f, 30f)]
        public float sendFrequency = 5f;

        [Tooltip("If true, the scene starts in edge-offload mode. " +
                 "EnvironmentMapper and ChunkManager are disabled; " +
                 "EdgeClient, DepthFrameSender, and NetworkMeshApplier are enabled.")]
        public bool startInEdgeMode = false;

        [Tooltip("Seconds to wait between reconnect attempts after a disconnect.")]
        [Range(0.5f, 10f)]
        public float reconnectDelay = 2f;
    }
}
