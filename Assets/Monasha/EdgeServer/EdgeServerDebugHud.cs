using UnityEngine;
using TMPro;

// ─────────────────────────────────────────────────────────────────────────────
// EdgeServerDebugHud.cs — Live on-screen metrics for the edge-server pipeline
//
// WHAT THIS FILE DOES:
//   Polls the public static state on EdgeServerClient + MetricsLogger and writes
//   a short status line into a TextMeshPro field so you can see RTT / FPS /
//   mesh age / connection state while wearing the headset.
//
// HOW TO USE:
//   1. Create a world-space Canvas in your scene positioned where you want
//      the HUD to appear (e.g. attached to the off-hand controller, or
//      floating in front of the player).
//   2. Add a TextMeshPro - Text (UI) component to the canvas.
//   3. Add this component to any GameObject, drag the TMP_Text into the
//      `label` field.
//
// FORMAT:
//   Line 1: Conn / Mesh ready state
//   Line 2: RTT + FPS
//   Line 3: Mesh age + vertex/triangle counts
//   Line 4: Recording state (when MetricsLogger is present)
//
// PERFORMANCE NOTES:
//   - Updates at `updateHz` (default 5 Hz) instead of every frame, so string
//     allocation for the label text is bounded.
//   - Reads only public static properties — zero coupling to EdgeServerClient
//     internals.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.EdgeServer
{
    public class EdgeServerDebugHud : MonoBehaviour
    {
        [Header("Target label")]
        [Tooltip("TextMeshPro text field the HUD will write into.")]
        [SerializeField] private TMP_Text label;

        [Header("Update rate")]
        [Tooltip("How many times per second the HUD refreshes. Lower = less GC.")]
        [SerializeField] private float updateHz = 5f;

        // Smoothed FPS — raw 1/Time.deltaTime is noisy; a rolling average
        // feels much more readable in VR.
        private float smoothedFps;
        private float fpsSmoothing = 0.1f;
        private float nextUpdateTime;

        private void Update()
        {
            // Always-on FPS smoothing so the value is fresh when we render.
            float instantFps = 1f / Mathf.Max(Time.unscaledDeltaTime, 0.001f);
            smoothedFps = Mathf.Lerp(smoothedFps, instantFps, fpsSmoothing);

            // Throttle the actual label update to `updateHz` to keep text
            // string allocations bounded.
            if (Time.unscaledTime < nextUpdateTime) return;
            nextUpdateTime = Time.unscaledTime + 1f / Mathf.Max(updateHz, 1f);

            if (label == null) return;

            string connLine = EdgeServerClient.IsConnected
                ? (EdgeServerClient.IsMeshReady ? "<color=#88ff88>CONNECTED · MESH READY</color>"
                                                : "<color=#ffdd66>CONNECTED · WAITING FOR MESH</color>")
                : "<color=#ff6666>DISCONNECTED</color>";

            string recLine = (MetricsLogger.Instance != null)
                ? (IsRecording() ? "<color=#ff88aa>● REC</color>" : "idle")
                : "no logger";

            label.text =
                $"{connLine}\n" +
                $"RTT: {EdgeServerClient.LastRttMs} ms   FPS: {smoothedFps:F0}\n" +
                $"Mesh age: {EdgeServerClient.LastMeshAgeSec:F2} s   " +
                $"{EdgeServerClient.LastVertexCount}v / {EdgeServerClient.LastTriangleCount}t\n" +
                $"Metrics: {recLine}";
        }

        // Checks MetricsLogger's public session state.
        private static bool IsRecording()
        {
            return MetricsLogger.Instance != null && MetricsLogger.Instance.IsSessionActive;
        }
    }
}
