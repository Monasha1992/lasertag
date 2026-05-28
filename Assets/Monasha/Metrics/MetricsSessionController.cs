using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// MetricsSessionController.cs — Simple start/stop controls for metrics sessions
//
// WHAT THIS FILE DOES:
//   Provides three ways to start and stop a metrics recording session:
//     1. A public ToggleSession() method you can wire to a Unity UI Button
//     2. A keyboard shortcut (default: F9) for Editor testing
//     3. An auto-start option if you want recording to begin on Play
//
//   Internally it just calls MetricsLogger.Instance.StartSession() /
//   EndSession(), so add the MetricsLogger component to the same (or any)
//   GameObject in the scene.
//
// HOW TO USE:
//   Option A — Keyboard:
//     Drop this component onto any GameObject. Press F9 in the Editor (or
//     during a build with a keyboard attached) to toggle recording on/off.
//     Watch the Console for "[MetricsLogger] Session started/ended" lines.
//
//   Option B — Unity UI Button on the Quest:
//     Add a world-space Canvas with two buttons:
//       "Start Recording" → OnClick → MetricsSessionController.StartSession()
//       "Stop Recording"  → OnClick → MetricsSessionController.EndSession()
//     Or a single toggle button wired to ToggleSession().
//
//   Option C — Auto-start on Play:
//     Check the `autoStartOnPlay` checkbox. Recording begins when the scene
//     starts and runs until you quit or manually call EndSession().
//
// OUTPUT:
//   CSV files go to Application.persistentDataPath. On the Quest, pull with:
//     adb pull /sdcard/Android/data/<package>/files/edge_metrics_<ts>.csv
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    public class MetricsSessionController : MonoBehaviour
    {
        [Header("Hotkey (Editor/keyboard builds only)")]
        [Tooltip("Press this key to toggle recording on/off. Useful for Editor testing.")]
        [SerializeField] private KeyCode toggleHotkey = KeyCode.F9;

        [Header("Auto-start")]
        [Tooltip("If true, a recording session begins as soon as this component loads.")]
        [SerializeField] private bool autoStartOnPlay = false;

        [Header("Status (read-only)")]
        [Tooltip("Reflects whether a recording session is currently active. " +
                 "Read-only at runtime — use Start/End/ToggleSession to change state.")]
        [SerializeField] private bool sessionIsActive;

        private void Start()
        {
            if (autoStartOnPlay) StartSession();
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleHotkey))
                ToggleSession();
        }

        // ─────────────────────────────────────────────────────────────────────
        // ToggleSession — If a session is running, stop it. Otherwise, start one.
        //
        // Wire this to a single UI button labelled "Start/Stop Recording".
        // ─────────────────────────────────────────────────────────────────────
        public void ToggleSession()
        {
            if (sessionIsActive) EndSession();
            else                 StartSession();
        }

        // ─────────────────────────────────────────────────────────────────────
        // StartSession — Begin a new CSV recording session
        //
        // Safe to call when already running (MetricsLogger guards against it).
        // ─────────────────────────────────────────────────────────────────────
        public void StartSession()
        {
            if (MetricsLogger.Instance == null)
            {
                Debug.LogError("[MetricsSessionController] No MetricsLogger in scene — add one and try again");
                return;
            }

            MetricsLogger.Instance.StartSession();
            sessionIsActive = true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // EndSession — Flush the current CSV file and stop recording
        // ─────────────────────────────────────────────────────────────────────
        public void EndSession()
        {
            if (MetricsLogger.Instance == null) return;

            MetricsLogger.Instance.EndSession();
            sessionIsActive = false;
        }

        // ─────────────────────────────────────────────────────────────────────
        // OnApplicationQuit — Ensure any in-progress session is flushed to disk
        // before the app exits, otherwise unflushed rows would be lost.
        // ─────────────────────────────────────────────────────────────────────
        private void OnApplicationQuit()
        {
            if (sessionIsActive) EndSession();
        }
    }
}
