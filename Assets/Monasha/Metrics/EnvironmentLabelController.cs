using UnityEngine;
using UnityEngine.InputSystem;
using VariableObjects;

// ─────────────────────────────────────────────────────────────────────────────
// EnvironmentLabelController.cs — Cycle through preset environment labels
//
// WHAT THIS FILE DOES:
//   Lets the researcher change the `environment` tag on the CSV's meta row
//   WITHOUT removing the headset between rooms. Ways to trigger:
//
//     - Controller buttons, same pattern as NetworkProfileController. The rig
//       researcher has no keyboard paired to the headset, so this is the one
//       that matters on-device:
//         Right A (primaryButton)      → CycleNext
//         Right thumbstick click       → CyclePrevious (overshoot recovery)
//       Both are free: the game's input actions bind only grip/trigger/pose,
//       right B is MeasurementController's run-start, and left X/Y belong to
//       NetworkProfileController.
//     - Public CycleNext() / CyclePrevious() / SetLabel(string) — for
//       UnityEvent wiring or programmatic use.
//     - Keyboard F8 — Editor fallback only.
//
//   PROCEDURE per room: press Right A to advance the label (which rotates the
//   CSV when rotateSessionOnChange is on, so the new file opens already tagged),
//   THEN press Right B to start the recorded run.
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

        [Header("Controller trigger")]
        [Tooltip("Right A. Free on this project — the game's input actions only " +
                 "bind grip/trigger/pose, right B is MeasurementController's " +
                 "run-start, and left X/Y are NetworkProfileController.")]
        [SerializeField] private string nextButtonBinding = "<XRController>{RightHand}/primaryButton";

        [Tooltip("Right thumbstick click — steps back if you overshoot. Clear this " +
                 "field if the path doesn't resolve on your runtime; an unresolved " +
                 "binding is simply ignored, it does not throw.")]
        [SerializeField] private string prevButtonBinding = "<XRController>{RightHand}/thumbstickClicked";

        [Header("UI readout (optional)")]
        [Tooltip("StringObject the active label is pushed into, so a field on the " +
                 "Settings panel shows which room is currently tagged — the rig " +
                 "researcher can confirm the button press landed without pulling " +
                 "logcat. If the field is editable, typing into it applies that " +
                 "label (handy for an ad-hoc room name not in the preset list). " +
                 "Leave empty to skip the UI entirely.")]
        [SerializeField] private StringObject environmentLabelDisplay;

        private InputAction nextAction;
        private InputAction prevAction;

        // Guards the StringObject round-trip: we write the label into the
        // StringObject, which fires onChange, which would call back into
        // SetLabel and rotate the CSV a second time. Set while we're the ones
        // doing the writing.
        private bool suppressDisplayCallback;

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

        // ─────────────────────────────────────────────────────────────────────
        // Controller bindings — mirrors NetworkProfileController's pattern.
        // The rig researcher has no keyboard on the headset, so the label MUST
        // be reachable from a controller button; F8 stays as an Editor fallback.
        // ─────────────────────────────────────────────────────────────────────
        private void Awake()
        {
            nextAction = new InputAction("EnvironmentLabelNext", InputActionType.Button);
            if (!string.IsNullOrEmpty(nextButtonBinding)) nextAction.AddBinding(nextButtonBinding);
            nextAction.AddBinding("<Keyboard>/f8");
            nextAction.performed += _ => CycleNext();

            prevAction = new InputAction("EnvironmentLabelPrev", InputActionType.Button);
            if (!string.IsNullOrEmpty(prevButtonBinding)) prevAction.AddBinding(prevButtonBinding);
            prevAction.performed += _ => CyclePrevious();
        }

        private void OnEnable()
        {
            nextAction?.Enable();
            prevAction?.Enable();

            if (environmentLabelDisplay != null)
                environmentLabelDisplay.onChange += OnDisplayEdited;
        }

        private void OnDisable()
        {
            nextAction?.Disable();
            prevAction?.Disable();

            if (environmentLabelDisplay != null)
                environmentLabelDisplay.onChange -= OnDisplayEdited;
        }

        // Someone typed into the Settings-panel field — treat it as a label
        // change. Ignored when we're the ones who just wrote the value.
        private void OnDisplayEdited(string label)
        {
            if (suppressDisplayCallback) return;
            SetLabel(label);
        }

        private void OnDestroy()
        {
            nextAction?.Dispose();
            prevAction?.Dispose();
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

            // Mirror into the UI readout. Guarded so the resulting onChange
            // doesn't bounce back through OnDisplayEdited and rotate again.
            if (environmentLabelDisplay != null)
            {
                suppressDisplayCallback = true;
                environmentLabelDisplay.Value = label;
                suppressDisplayCallback = false;
            }

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
