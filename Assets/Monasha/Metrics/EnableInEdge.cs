using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// EnableInEdge.cs — Self-toggle for edge-only GameObjects
//
// Add to a GameObject that should ONLY be active in Edge mode (typically
// EdgeServerClient and the EdgeMesh GameObject). In Standalone mode it
// deactivates itself in Awake. See EnableInModeBase (EnableInMode.cs) for the
// shared logic.
//
// MUST live in its own file matching the class name — Unity only exposes a
// MonoBehaviour in Add Component when the file name equals the class name.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    public class EnableInEdge : EnableInModeBase
    {
        protected override ArchitectureMode RequiredMode => ArchitectureMode.Edge;
    }
}
