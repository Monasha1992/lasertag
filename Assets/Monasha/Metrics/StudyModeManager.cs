using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// StudyModeManager.cs — Build-time toggle between the two uses of the app
//
// WHAT THIS FILE DOES:
//   Selects whether this build is for QUANTITATIVE data collection (the existing
//   project: metrics, dual-headset rig, Study 1) or the USER STUDY (Study 2:
//   participants shoot moving virtual targets occluded by a real obstacle; the
//   room mesh is hidden; outcomes captured by surveys, not in-app metrics).
//
//   This is ORTHOGONAL to ArchitectureManager (edge vs standalone) — every
//   combination is valid, since the user study also compares both architectures.
//
// HOW MODE IS SELECTED (mirrors ArchitectureManager):
//   - USER_STUDY_BUILD scripting define → forces UserStudy.
//   - Otherwise the Inspector value is used (default = Quantitative).
//   So the DEFAULT build behaves exactly like the existing project — nothing
//   changes unless you opt in.
//
// WHAT THE MODE DRIVES:
//   - EnableInUserStudy components self-disable their GameObjects unless this is
//     a UserStudy build (so the moving targets only exist in user-study builds).
//   - Settings hides the room mesh in UserStudy mode (Settings.cs reads
//     IsUserStudy).
//   - The metrics framework stays available in both modes; the user-study tasks
//     just don't rely on it.
//
// IF THIS COMPONENT IS ABSENT:
//   Current defaults to Quantitative — i.e. the existing behaviour, no targets,
//   mesh visible — so a scene without it is unchanged.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    public enum StudyMode { Quantitative, UserStudy }

    // Execution order -100 (same as ArchitectureManager) so Current is set before
    // any default-order Awake reads it (EnableInUserStudy) and before Settings.Start.
    [DefaultExecutionOrder(-100)]
    public class StudyModeManager : MonoBehaviour
    {
        [Header("Study mode (Editor default — overridden by USER_STUDY_BUILD define)")]
        [Tooltip("Quantitative = the existing project (metrics / Study 1). " +
                 "UserStudy = moving virtual targets + hidden mesh (Study 2). " +
                 "A USER_STUDY_BUILD scripting define overrides this for real builds.")]
        [SerializeField] private StudyMode mode = StudyMode.Quantitative;

        public static StudyMode Current { get; private set; } = StudyMode.Quantitative;
        public static bool IsUserStudy => Current == StudyMode.UserStudy;

        private void Awake()
        {
#if USER_STUDY_BUILD
            mode = StudyMode.UserStudy;
#endif
            Current = mode;

            string define =
#if USER_STUDY_BUILD
                "USER_STUDY_BUILD";
#else
                "(no build define — Inspector value used)";
#endif
            Debug.Log(
                "[StudyModeManager] ============================================\n" +
                $"[StudyModeManager]  Study mode   : {mode}\n" +
                $"[StudyModeManager]  Build define : {define}\n" +
                "[StudyModeManager] ============================================");
        }
    }
}
