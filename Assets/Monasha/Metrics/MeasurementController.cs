using System;
using Anaglyph.XRTemplate;
using Monasha.EdgeServer;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

// ─────────────────────────────────────────────────────────────────────────────
// MeasurementController.cs — Controller-button trigger to start/stop a run
//
// WHAT THIS FILE DOES:
//   Gates BOTH reconstruction (meshing) and metrics recording behind a manual
//   start/stop trigger so a measurement run begins at a known moment, from a
//   clean slate, in BOTH architectures. Bound by default to the right Touch
//   controller's button B (<XRController>{RightHand}/secondaryButton); the same
//   action also accepts keyboard "B" for in-Editor testing.
//
//   Toggle behaviour: first press starts a run, next press stops it.
//     START → clear the existing mesh (fresh scan), enable meshing, open a new
//             metrics CSV.
//     STOP  → disable meshing, close the CSV.
//
//   FRESH START per architecture:
//     - Standalone: EnvironmentMapper.Clear() wipes the on-device TSDF volume and
//       (via its Cleared event) destroys all ChunkManager chunks.
//     - Edge: EdgeChunkStore.ClearAll() drops the client's cached chunk meshes.
//       The Mac server keeps its own TSDF volume per process, so for a 100%-clean
//       edge trial restart the Mac server before pressing B (operator does this
//       manually, by design — no reset message is sent over the wire).
//
// HOW IT GATES:
//   Sets EnvironmentMapper.MeasurementActive. Standalone integration/meshing
//   (EnvironmentMapper.UpdateLoop) and edge depth streaming
//   (EdgeServerClient.OnDepthUpdated) both early-out while it's false. Depth is
//   still acquired upstream, so a run starts responding instantly.
//   Also sets MetricsLogger.ManualStart=true in Awake so the logger does NOT
//   auto-start — this controller owns the session lifecycle instead.
//
// IF THIS COMPONENT IS ABSENT:
//   EnvironmentMapper.MeasurementActive defaults to true and MetricsLogger
//   auto-starts, so the project behaves exactly as before (no regression).
//
// ATTACHMENT:
//   Add to MetricsRoot (alongside MetricsLogger). No Inspector wiring needed —
//   the input action is created in code with the button-B + keyboard-B bindings.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    public class MeasurementController : MonoBehaviour
    {
        [Header("Trigger")]
        [Tooltip("OpenXR binding for the start/stop button. Default: right controller B.")]
        [SerializeField] private string buttonBinding = "<XRController>{RightHand}/secondaryButton";

        [Tooltip("If true, clear the existing mesh on START so each run is a fresh scan. " +
                 "Standalone clears the on-device volume; edge clears the client chunk cache " +
                 "(restart the Mac server for a fully fresh edge volume).")]
        [SerializeField] private bool freshStart = true;

        [Header("Status (read-only)")]
        [SerializeField] private bool runActive;

        [Header("Events")]
        public UnityEvent onRunStarted = new();
        public UnityEvent onRunStopped = new();

        private InputAction trigger;

        private void Awake()
        {
            // Take ownership of the run lifecycle: gate meshing off and stop the
            // logger from auto-starting. (Runs in Awake, before MetricsLogger.Start.)
            EnvironmentMapper.MeasurementActive = false;
            MetricsLogger.ManualStart = true;

            trigger = new InputAction("StartStopRun", InputActionType.Button);
            trigger.AddBinding(buttonBinding);          // controller B
            trigger.AddBinding("<Keyboard>/b");         // Editor / keyboard fallback
            trigger.performed += OnTrigger;

            // If you DON'T see this line in adb logcat, this component isn't running
            // (wrong/inactive GameObject) — meshing & metrics won't be gated.
            Debug.Log("[MeasurementController] Armed — meshing & metrics gated until button B.");
        }

        private void OnEnable()  => trigger?.Enable();
        private void OnDisable() => trigger?.Disable();
        private void OnDestroy()
        {
            if (trigger != null) { trigger.performed -= OnTrigger; trigger.Dispose(); }
        }

        private void OnTrigger(InputAction.CallbackContext _) => ToggleRun();

        /// <summary>Public so a UI button can also drive it.</summary>
        public void ToggleRun()
        {
            if (runActive) StopRun();
            else           StartRun();
        }

        public void StartRun()
        {
            if (runActive) return;

            if (freshStart) ClearMesh();

            EnvironmentMapper.MeasurementActive = true;   // unblocks meshing/streaming
            runActive = true;

            var log = MetricsLogger.Instance;
            if (log != null && !log.IsSessionActive) log.StartSession();

            MetricsLogger.Instance?.LogEvent("run_started",
                $"arch={(IsEdge ? "edge" : "standalone")};fresh={freshStart}");
            Debug.Log("[MeasurementController] RUN STARTED");
            try { onRunStarted.Invoke(); } catch (Exception e) { Debug.LogError(e); }
        }

        public void StopRun()
        {
            if (!runActive) return;

            EnvironmentMapper.MeasurementActive = false;  // freezes meshing/streaming
            runActive = false;

            MetricsLogger.Instance?.LogEvent("run_stopped");
            MetricsLogger.Instance?.EndSession();
            Debug.Log("[MeasurementController] RUN STOPPED");
            try { onRunStopped.Invoke(); } catch (Exception e) { Debug.LogError(e); }
        }

        private void ClearMesh()
        {
            if (IsEdge)
            {
                // Drop the client-side cached chunks; the Mac volume is reset by
                // the operator restarting the server (see header).
                var store = FindFirstObjectByType<EdgeChunkStore>();
                if (store != null) store.ClearAll();
            }
            else
            {
                // Standalone: wipe the on-device TSDF; Cleared event drops chunks.
                EnvironmentMapper.Instance?.Clear();
            }
        }

        private bool IsEdge =>
            ArchitectureManager.Instance != null
                ? ArchitectureManager.Instance.IsEdge
                : EnvironmentMapper.UseEdgeServer;
    }
}
