using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// EnableInMode.cs — Per-GameObject self-toggle based on ArchitectureMode
//
// WHAT THIS FILE DOES:
//   Defines two tiny MonoBehaviours, EnableInStandalone and EnableInEdge,
//   that check ArchitectureManager.Instance.Mode in their own Awake() and
//   deactivate their parent GameObject if the mode doesn't match.
//
// WHY:
//   ArchitectureManager has Inspector arrays for toggling scene-level
//   GameObjects, but that only works when the targets exist at scene load.
//   In this project, EnvironmentMapper and ChunkManager live inside the
//   XR rig prefab which CameraRigSpawner instantiates at runtime — so the
//   Inspector array can't reference them. Instead, attach EnableInStandalone
//   or EnableInEdge directly to those prefab GameObjects, and they'll
//   self-toggle correctly regardless of spawn order.
//
// HOW TO USE:
//   - Open the XR Rig prefab.
//   - On the ChunkManager GameObject, add EnableInStandalone.
//   - On the EdgeServerClient GameObject (and EdgeMesh), add EnableInEdge.
//   - EnvironmentMapper itself: do NOT add either — it's needed in both modes.
//
// RACE CONDITIONS:
//   ArchitectureManager has [DefaultExecutionOrder(-100)] so its Awake runs
//   before any default-order Awake including the spawned rig's. Even when the
//   rig is instantiated later, ArchitectureManager.Instance is guaranteed to
//   be set by then. If you hit a startup-order edge case, the helpers fall
//   back gracefully (treat missing manager as "let me stay enabled").
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    // ─────────────────────────────────────────────────────────────────────────
    // Base helper — shared logic so the two concrete components stay tiny.
    // ─────────────────────────────────────────────────────────────────────────
    public abstract class EnableInModeBase : MonoBehaviour
    {
        protected abstract ArchitectureMode RequiredMode { get; }

        private void Awake()
        {
            var arch = ArchitectureManager.Instance;
            if (arch == null)
            {
                // No manager in scene — leave the GameObject enabled. This is
                // the right fallback for Editor play tests that don't include
                // an ArchitectureManager (developer working on one path in
                // isolation).
                return;
            }

            if (arch.Mode != RequiredMode)
            {
                gameObject.SetActive(false);
            }
        }
    }

    /// <summary>
    /// Add to a GameObject that should ONLY be active in Standalone mode.
    /// Typically attached to ChunkManager and the chunk-parent root.
    /// In Edge mode, this GameObject is deactivated in its Awake().
    /// </summary>
    public class EnableInStandalone : EnableInModeBase
    {
        protected override ArchitectureMode RequiredMode => ArchitectureMode.Standalone;
    }

    /// <summary>
    /// Add to a GameObject that should ONLY be active in Edge mode.
    /// Typically attached to EdgeServerClient and the EdgeMesh GameObject.
    /// In Standalone mode, this GameObject is deactivated in its Awake().
    /// </summary>
    public class EnableInEdge : EnableInModeBase
    {
        protected override ArchitectureMode RequiredMode => ArchitectureMode.Edge;
    }
}
