using Anaglyph.XRTemplate;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// ArchitectureManager.cs — Selects the reconstruction architecture for this run
//
// WHAT THIS FILE DOES:
//   Single source of truth for which mesh path is active in the running build.
//   On Awake() it:
//     1. Toggles the architecture-specific GameObjects (standalone vs edge).
//     2. Sets EnvironmentMapper.UseEdgeServer accordingly.
//     3. Pushes identification metadata (architecture, headsetId, environment)
//        into MetricsLogger so it lands in every CSV's `meta` row.
//
// WHY THIS EXISTS:
//   The scene contains both pipelines (chunk-meshing for standalone + edge
//   client for offload). Before this script, switching modes required manually
//   activating the right child GameObjects and remembering to flip
//   EnvironmentMapper.UseEdgeServer. Now it's one Inspector dropdown.
//
// WHO USES IT:
//   - Set by the researcher per-build (rig builds: one is Standalone, one Edge).
//   - In the user study, can be flipped between participants via a study-rig UI.
//   - The environment label can also be updated mid-session by a controller
//     button (see EnvironmentLabel property setter) so the researcher can
//     re-label as the rig is rolled into a new room.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    /// <summary>Which reconstruction path is active for this app instance.</summary>
    public enum ArchitectureMode { Standalone, Edge }

    // Execution order -100: ArchitectureManager.Awake runs before any default-
    // order Awake, including those inside prefabs that the CameraRigSpawner
    // instantiates at runtime. This lets EnableInStandalone / EnableInEdge
    // (sitting inside the spawned rig) safely read ArchitectureManager.Instance.Mode
    // during their own Awake.
    [DefaultExecutionOrder(-100)]
    public class ArchitectureManager : MonoBehaviour
    {
        public static ArchitectureManager Instance { get; private set; }

        // ── Inspector fields ──────────────────────────────────────────────────
        [Header("Architecture selection")]
        [Tooltip("Which reconstruction path this build runs. Standalone uses the " +
                 "Quest's on-device EnvironmentMapper + ChunkManager. Edge sends " +
                 "depth to a Mac and applies the returned mesh.")]
        [SerializeField] private ArchitectureMode mode = ArchitectureMode.Standalone;

        [Header("Per-build identification")]
        [Tooltip("Stable label for this headset. Used in the CSV meta row so that " +
                 "data from the stacked-rig sessions can be associated with the " +
                 "correct device. Suggested values: 'quest_A', 'quest_B'.")]
        [SerializeField] private string headsetId = "quest_A";

        [Tooltip("Label describing the physical environment the session runs in. " +
                 "Settable at runtime via the EnvironmentLabel property so the " +
                 "researcher can re-tag as the rig moves between rooms.")]
        [SerializeField] private string environmentLabel = "unknown";

        [Tooltip("Optional: git SHA or build identifier. Written to the CSV meta " +
                 "row so post-hoc analysis can correlate data with code version.")]
        [SerializeField] private string buildSha = "";

        [Tooltip("Default study phase tag. 'rig' for the stacked-headset " +
                 "quantitative runs (Study 1); 'participant' for user-study runs " +
                 "(Study 2). Overridable at runtime via SetStudyPhase().")]
        [SerializeField] private string studyPhase = "rig";

        [Tooltip("Default network profile tag. Set to 'good' for normal Wi-Fi, " +
                 "or 'high_rtt' / 'bandwidth_cap_20mbps' / 'mid_rtt' when running " +
                 "behind macOS Network Link Conditioner. Standalone runs should " +
                 "use 'n/a' since they don't touch the network.")]
        [SerializeField] private string networkProfile = "n/a";

        [Header("GameObjects toggled by architecture (scene-level only)")]
        [Tooltip("Roots enabled when mode = Standalone. Use ONLY for GameObjects " +
                 "that exist in the scene at load — ChunkManager / chunk root if " +
                 "they live in the scene. For prefab GameObjects that are " +
                 "instantiated at runtime (e.g. inside CameraRigSpawner's rig), " +
                 "attach the EnableInStandalone component to them instead. " +
                 "NOTE: do NOT include EnvironmentMapper — it is needed in both " +
                 "modes (UseEdgeServer flag controls whether it integrates).")]
        [SerializeField] private GameObject[] standaloneGameObjects;

        [Tooltip("Roots enabled when mode = Edge — typically the EdgeServerClient " +
                 "and EdgeMesh GameObjects if they live in the scene. For prefab " +
                 "GameObjects, use the EnableInEdge component instead.")]
        [SerializeField] private GameObject[] edgeGameObjects;

        // ── Public accessors ──────────────────────────────────────────────────
        public ArchitectureMode Mode             => mode;
        public string           HeadsetId        => headsetId;
        public string           BuildSha         => buildSha;
        public bool             IsEdge           => mode == ArchitectureMode.Edge;
        public bool             IsStandalone     => mode == ArchitectureMode.Standalone;

        /// <summary>
        /// Settable at runtime — when the researcher rolls the rig into a new
        /// physical room they call SetEnvironmentLabel("cluttered_office") so
        /// subsequent meta rows reflect the new condition. (Note: the meta row
        /// is only written once at StartSession, so call this before each
        /// session start, or call EndSession()/StartSession() to re-tag.)
        /// </summary>
        public string EnvironmentLabel
        {
            get => environmentLabel;
            set
            {
                environmentLabel = value;
                PushMetadataToLogger();
            }
        }

        public string NetworkProfile
        {
            get => networkProfile;
            set { networkProfile = value; PushMetadataToLogger(); }
        }

        public string StudyPhase
        {
            get => studyPhase;
            set { studyPhase = value; PushMetadataToLogger(); }
        }

        public string ParticipantId { get; set; } = "";

        // ─────────────────────────────────────────────────────────────────────
        // Awake — Toggle GameObjects and flip EnvironmentMapper.UseEdgeServer
        //
        // Runs before any of the mesh-pipeline scripts so they see the correct
        // state before their own Start() executes. This matters because
        // EnvironmentMapper.UpdateLoop checks UseEdgeServer to decide whether
        // to run the local integration.
        // ─────────────────────────────────────────────────────────────────────
        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            bool edge = (mode == ArchitectureMode.Edge);

            // Toggle GameObjects to match the chosen mode
            if (standaloneGameObjects != null)
                foreach (var go in standaloneGameObjects)
                    if (go != null) go.SetActive(!edge);

            if (edgeGameObjects != null)
                foreach (var go in edgeGameObjects)
                    if (go != null) go.SetActive(edge);

            // Flip the global flag used by EnvironmentMapper.UpdateLoop to skip
            // on-device integration. EdgeServerClient.Start() also sets this to
            // true defensively, but doing it here ensures it's set even if the
            // EdgeServerClient GameObject is disabled at boot.
            EnvironmentMapper.UseEdgeServer = edge;

            Debug.Log($"[ArchitectureManager] Mode = {mode}, headsetId = {headsetId}, " +
                      $"environment = {environmentLabel}, network = {networkProfile}");
        }

        private void Start()
        {
            // Push metadata once the logger is alive. (MetricsLogger's Awake
            // runs at execution-order 0 by default; this runs at Start so the
            // singleton is guaranteed to exist.)
            PushMetadataToLogger();
        }

        // ─────────────────────────────────────────────────────────────────────
        // PushMetadataToLogger — Sync our fields into MetricsLogger.
        //
        // Called from Start() once and from any setter that mutates a metadata
        // field. The logger only writes the meta row at StartSession, so for
        // changes to take effect in the CSV they must happen *before* the next
        // session start. Calls outside that window still update the logger's
        // in-memory state — useful for HUD display etc.
        // ─────────────────────────────────────────────────────────────────────
        private void PushMetadataToLogger()
        {
            if (MetricsLogger.Instance == null) return;

            MetricsLogger.Instance.SetSessionMetadata(
                studyPhase:       studyPhase,
                participantId:    ParticipantId,
                headsetId:        headsetId,
                architecture:     IsEdge ? "edge" : "standalone",
                environmentLabel: environmentLabel,
                networkProfile:   IsEdge ? networkProfile : "n/a",
                buildSha:         buildSha);
        }
    }
}
