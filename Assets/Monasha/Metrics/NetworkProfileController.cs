using UnityEngine;
using UnityEngine.InputSystem;

// ─────────────────────────────────────────────────────────────────────────────
// NetworkProfileController.cs — Tag which Network Link Conditioner profile is
// active during a rig pass. Mirrors EnvironmentLabelController.cs; the button
// trigger mirrors MeasurementController.cs.
//
// This does NOT touch the network — throttling happens on the Mac via
// Network Link Conditioner (see docs/04-protocol.md § Network manipulation).
// This only tags ArchitectureManager.NetworkProfile so the value lands in the
// CSV's `network_profile` column (the field already existed; nothing ever set it).
//
// TRIGGER — controller buttons, same pattern as MeasurementController's run-start
// B button, on the LEFT controller since B/right is already taken:
//     Left Y (secondaryButton) → CycleNext (advance to the next profile)
//     Left X (primaryButton)   → CyclePrevious (step back if you overshoot)
//   Neither button is used elsewhere in Lasertag Input.inputactions (only Grip/
//   Trigger are bound on the left controller there), so there's no conflict.
//
// PROCEDURE: apply the NLC profile on the Mac FIRST, then press Left Y, THEN
// press MeasurementController's B to start the run. Cycling before a run
// starts is safe — it only updates ArchitectureManager's in-memory value;
// MetricsLogger.StartSession() picks up whatever is current when the run
// actually begins. Edge-only in effect — ArchitectureManager forces "n/a"
// for standalone.
//
// ATTACHMENT: add to MetricsRoot, alongside MeasurementController and
// EnvironmentLabelController.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    public class NetworkProfileController : MonoBehaviour
    {
        [Tooltip("Must match the Mac's NLC profile names exactly — see docs/04-protocol.md.")]
        [SerializeField] private string[] profiles = { "good", "mid_rtt", "high_rtt", "bandwidth_cap_20mbps" };

        [Header("Controller trigger")]
        [SerializeField] private string nextButtonBinding = "<XRController>{LeftHand}/secondaryButton";
        [SerializeField] private string prevButtonBinding = "<XRController>{LeftHand}/primaryButton";

        [Tooltip("EndSession()+StartSession() on every change so each network condition gets its own CSV file. Only fires if a session is already active — safe to cycle before a run starts.")]
        [SerializeField] private bool rotateSessionOnChange = true;

        private int currentIndex;
        private InputAction nextAction;
        private InputAction prevAction;

        private void Awake()
        {
            nextAction = new InputAction("NetworkProfileNext", InputActionType.Button);
            nextAction.AddBinding(nextButtonBinding);
            nextAction.AddBinding("<Keyboard>/y");
            nextAction.performed += _ => CycleNext();

            prevAction = new InputAction("NetworkProfilePrev", InputActionType.Button);
            prevAction.AddBinding(prevButtonBinding);
            prevAction.AddBinding("<Keyboard>/x");
            prevAction.performed += _ => CyclePrevious();
        }

        private void OnEnable()
        {
            nextAction?.Enable();
            prevAction?.Enable();
        }

        private void OnDisable()
        {
            nextAction?.Disable();
            prevAction?.Disable();
        }

        private void OnDestroy()
        {
            nextAction?.Dispose();
            prevAction?.Dispose();
        }

        private void Start()
        {
            currentIndex = 0;
            ApplyCurrentProfile(rotateSession: false);
        }

        /// <summary>Move forward in the profile list (wraps to start).</summary>
        public void CycleNext()
        {
            if (profiles == null || profiles.Length == 0) return;
            currentIndex = (currentIndex + 1) % profiles.Length;
            ApplyCurrentProfile(rotateSession: rotateSessionOnChange);
        }

        /// <summary>Move backward in the profile list (wraps to end).</summary>
        public void CyclePrevious()
        {
            if (profiles == null || profiles.Length == 0) return;
            currentIndex = (currentIndex - 1 + profiles.Length) % profiles.Length;
            ApplyCurrentProfile(rotateSession: rotateSessionOnChange);
        }

        /// <summary>Set to a specific profile by name — explicit-name wiring or programmatic use.</summary>
        public void SetProfile(string profile)
        {
            if (string.IsNullOrEmpty(profile)) return;

            if (profiles != null)
                for (int i = 0; i < profiles.Length; i++)
                    if (profiles[i] == profile) { currentIndex = i; break; }

            ApplyProfileString(profile, rotateSession: rotateSessionOnChange);
        }

        private void ApplyCurrentProfile(bool rotateSession)
        {
            if (profiles == null || profiles.Length == 0) return;
            ApplyProfileString(profiles[currentIndex], rotateSession);
        }

        private void ApplyProfileString(string profile, bool rotateSession)
        {
            var arch = ArchitectureManager.Instance;
            if (arch != null) arch.NetworkProfile = profile;

            MetricsLogger.Instance?.LogEvent("network_profile_changed", $"profile={profile}");

            if (rotateSession && MetricsLogger.Instance != null && MetricsLogger.Instance.IsSessionActive)
            {
                MetricsLogger.Instance.EndSession();
                MetricsLogger.Instance.StartSession();
            }

            Debug.Log($"[NetworkProfileController] Network profile → '{profile}' (index {currentIndex})");
        }
    }
}
