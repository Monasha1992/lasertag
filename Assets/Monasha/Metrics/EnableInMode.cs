using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// EnableInMode.cs — Shared base for per-GameObject architecture self-toggles
//
// WHAT THIS FILE DOES:
//   Defines the abstract EnableInModeBase used by the concrete components
//   EnableInStandalone and EnableInEdge (each in its own file — Unity only
//   exposes a MonoBehaviour in Add Component when the file name matches the
//   class name, so they cannot share this file). Each concrete component
//   checks ArchitectureManager.Instance.Mode in its Awake() (via this base)
//   and deactivates its GameObject if the mode doesn't match.
//   EnableInUserStudy (StudyMode toggle) lives in its own file too.
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
}
