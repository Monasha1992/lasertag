using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// EnableInStandalone.cs — Self-toggle for standalone-only GameObjects
//
// Add to a GameObject that should ONLY be active in Standalone mode (typically
// ChunkManager and the chunk-parent root). In Edge mode it deactivates itself
// in Awake. See EnableInModeBase (EnableInMode.cs) for the shared logic.
//
// MUST live in its own file matching the class name — Unity only exposes a
// MonoBehaviour in Add Component when the file name equals the class name.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    public class EnableInStandalone : EnableInModeBase
    {
        protected override ArchitectureMode RequiredMode => ArchitectureMode.Standalone;
    }
}
