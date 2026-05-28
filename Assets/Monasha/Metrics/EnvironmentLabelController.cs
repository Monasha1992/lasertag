using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// EnvironmentLabelController.cs — Cycle through preset environment labels
//
// WHAT THIS FILE DOES:
//   Lets the researcher change the `environment` tag on the CSV's meta row
//   WITHOUT removing the headset between rooms. Three ways to trigger:
//
//     - Public CycleNext() / CyclePrevious() methods — wire to a Quest
//       controller button via Unity's UnityEvent / XR Input system, or call
//       directly from another script.
//     - Public SetLabel(string) — for explicit-name wiring (one button per
//       label) or programmatic use.
//     - Editor hotkey (F8 by default) — handy in Play mode without a headset.
//
//   Every label change pushes the new value into
//   ArchitectureManager.Instance.EnvironmentLabel (which propagates to the
//   logger's metadata) AND emits an `environment_label_changed` event into
//   the metrics CSV so the moment of transition is preserved.
//
// WHY THIS MATTERS:
//   The Study 1 rig moves through multiple environments per session
//   (empty room → cluttered → hallway → …). The researcher pushes the rig,
//   stops to let thermals stabilise, then needs to mark the new environment
//   before the next walk pattern. Without this component they'd have to
//   take the headset off, edit a config file or Inspector value, rebuild,
//   redeploy — wasting study time and risking thermal contamination from
//   the gap. With this component, a single controller-button press
//   re-tags the recording in <100 ms.
//
// METADATA INTERACTION:
//   The new label only lands in the `meta` row of the NEXT session — meta
//   is written once at StartSession. To mid-session-tag, the researcher
//   either:
//     - calls EndSession() + StartSession() to open a fresh CSV per
//       environment (recommended — keeps one file per room for clean
//       analysis), or
//     - lets the `environment_label_changed` event in the existing CSV
//       mark the transition point, then slices by that event in pandas.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    public class EnvironmentLabelController : MonoBehaviour
    {
        [Tooltip("Preset environment labels to cycle through. The first entry " +
                 "is the default applied at Start. Edit this list to match " +
                 "your study's actual rooms — e.g. 'empty_office', " +
                 "'cluttered_office', 'large_hall', 'wifi_edge'.")]
        [SerializeField] private string[] labels = {
            "empty",
            "cluttered",
            "hallway",
            "outdoor"
        };

        [Tooltip("Editor / keyboard-build hotkey to cycle to the next label. " +
                 "Has no effect on headset builds without a keyboard attached.")]
        [SerializeField] private KeyCode cycleHotkey = KeyCode.F8;

        [Tooltip("If true, EndSession() + StartSession() are called on every " +
                 "label change so each environment gets its own CSV file. " +
                 "Recommended for the rig study — easier per-environment analysis. " +
                 "Leave OFF if you'd rather have one continuous CSV with " +
                 "environment_label_changed events marking transitions.")]
        [SerializeField] private bool rotateSessionOnChange = true;

        // Current position in the labels[] array.
        private int currentIndex;

        // ─────────────────────────────────────────────────────────────────────
        // Start — Apply the default label so the very first CSV has it set.
        //
        // If MetricsSessionController.autoStartOnPlay is also true and starts
        // a session before this Start() runs, the meta row will already be
        // written with whatever ArchitectureManager.EnvironmentLabel had at
        // that moment. ApplyCurrentLabel() called here will still update the
        // logger's in-memory state, and rotateSessionOnChange (if on) will
        // give the next environment a properly-tagged file.
        // ─────────────────────────────────────────────────────────────────────
        private void Start()
        {
            currentIndex = 0;
            ApplyCurrentLabel(rotateSession: false);  // don't roll over the auto-start session
        }

        private void Update()
        {
            if (Input.GetKeyDown(cycleHotkey)) CycleNext();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public API — wire any of these to controller buttons via UnityEvent.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Move forward in the label list (wraps to start).</summary>
        public void CycleNext()
        {
            if (labels == null || labels.Length == 0) return;
            currentIndex = (currentIndex + 1) % labels.Length;
            ApplyCurrentLabel(rotateSession: rotateSessionOnChange);
        }

        /// <summary>Move backward in the label list (wraps to end).</summary>
        public void CyclePrevious()
        {
            if (labels == null || labels.Length == 0) return;
            currentIndex = (currentIndex - 1 + labels.Length) % labels.Length;
            ApplyCurrentLabel(rotateSession: rotateSessionOnChange);
        }

        /// <summary>
        /// Set to a specific label by name. If the label isn't in the preset
        /// list, it's still applied — useful for ad-hoc labels typed in via
        /// some external UI.
        /// </summary>
        public void SetLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return;

            // Try to align currentIndex to the new label so subsequent cycles
            // start from the right place.
            if (labels != null)
                for (int i = 0; i < labels.Length; i++)
                    if (labels[i] == label) { currentIndex = i; break; }

            ApplyLabelString(label, rotateSession: rotateSessionOnChange);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Internal — actually push the label into ArchitectureManager + log.
        // ─────────────────────────────────────────────────────────────────────
        private void ApplyCurrentLabel(bool rotateSession)
        {
            if (labels == null || labels.Length == 0) return;
            ApplyLabelString(labels[currentIndex], rotateSession);
        }

        private void ApplyLabelString(string label, bool rotateSession)
        {
            var arch = ArchitectureManager.Instance;
            if (arch != null) arch.EnvironmentLabel = label;

            // Mark the transition in the current CSV regardless of whether we
            // rotate sessions — useful if the researcher decides to slice
            // post-hoc rather than per-file.
            MetricsLogger.Instance?.LogEvent("environment_label_changed", $"label={label}");

            // Optionally rotate to a fresh CSV so the new environment gets
            // its own file. The new file's meta row will reflect the updated
            // EnvironmentLabel (since SetSessionMetadata was just called by
            // the ArchitectureManager.EnvironmentLabel setter).
            if (rotateSession && MetricsLogger.Instance != null && MetricsLogger.Instance.IsSessionActive)
            {
                MetricsLogger.Instance.EndSession();
                MetricsLogger.Instance.StartSession();
            }

            Debug.Log($"[EnvironmentLabelController] Environment label → '{label}'");
        }
    }
}
