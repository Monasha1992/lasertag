using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// EnableInUserStudy.cs — Self-toggle for user-study-only GameObjects
//
// Add to a GameObject (e.g. the moving-target root) that should ONLY be active
// in the USER STUDY build (StudyMode.UserStudy). In a Quantitative build (the
// default), or with no StudyModeManager present, it deactivates itself in
// Awake. Orthogonal to ArchitectureMode.
//
// MUST live in its own file matching the class name — Unity only exposes a
// MonoBehaviour in Add Component when the file name equals the class name.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    public class EnableInUserStudy : MonoBehaviour
    {
        private void Awake()
        {
            if (!StudyModeManager.IsUserStudy)
                gameObject.SetActive(false);
        }
    }
}
