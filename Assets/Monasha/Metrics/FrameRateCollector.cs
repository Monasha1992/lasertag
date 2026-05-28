using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// FrameRateCollector.cs — Per-frame frame-time and FPS sampler
//
// WHAT THIS FILE DOES:
//   Logs one `frame` row per Unity frame into MetricsLogger. Captures:
//     - frame_time_ms (unscaled, so it reflects actual wall-clock work)
//     - fps          (instantaneous, 1 / frameTime)
//     - display_hz   (Quest's current refresh rate, sampled once at Start)
//
// WHY LateUpdate:
//   Sampling in LateUpdate means we've executed every Update() this frame, so
//   the deltaTime we read is the full per-frame cost. Sampling in Update would
//   measure the gap between Updates without the post-update work.
//
// WHY UNSCALED:
//   Time.deltaTime gets clamped by Unity (Time.maximumDeltaTime, default 1/3 s)
//   to prevent physics blow-ups on long frames — which is exactly the frame
//   variance information we want to *measure*. Time.unscaledDeltaTime is the
//   raw value.
//
// PERFORMANCE NOTE:
//   At 90 Hz this writes ~90 rows per second to the CSV. The StringBuilder
//   in MetricsLogger is reused (no per-row alloc) and the file flushes once
//   per second (every 60 rows). Net cost on Quest 3: ~0.1 ms/frame.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    public class FrameRateCollector : MonoBehaviour
    {
        // Cached at Start. Quest's display Hz can technically change (e.g.
        // 72 → 90 Hz on the fly) but for thesis purposes we sample once and
        // treat it as constant. If the OVR display Hz API is unavailable
        // (Editor or non-Quest build), defaults to 90 — the Quest 3 default.
        private float displayHz = 90f;

        // Lightweight EMA of frame time, so the HUD can show a smoothed value.
        // The CSV row records the raw instantaneous value; this is for display.
        public float SmoothedFps { get; private set; } = 0f;

        private const float SmoothingAlpha = 0.1f;

        private void Start()
        {
            // OVRPlugin.systemDisplayFrequency returns the current refresh rate.
            // It throws on platforms without the Meta XR plugin, so guard it.
            try
            {
#if !UNITY_EDITOR
                displayHz = OVRPlugin.systemDisplayFrequency;
                if (displayHz < 30f || displayHz > 200f) displayHz = 90f;  // sanity
#endif
            }
            catch (System.Exception)
            {
                displayHz = 90f;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // LateUpdate — Capture the frame's wall-clock duration, log it.
        // ─────────────────────────────────────────────────────────────────────
        private void LateUpdate()
        {
            if (MetricsLogger.Instance == null || !MetricsLogger.Instance.IsSessionActive)
                return;

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;                              // Skip first frame

            float frameTimeMs = dt * 1000f;
            float fps         = 1f / dt;

            // Update the smoothed EMA for HUD consumption
            SmoothedFps = Mathf.Lerp(SmoothedFps == 0f ? fps : SmoothedFps, fps, SmoothingAlpha);

            MetricsLogger.Instance.LogFrameSample(frameTimeMs, fps, displayHz);
        }
    }
}
