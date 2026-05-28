using System.Collections;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// OvrPerfCollector.cs — 1 Hz OVR perf-level sampler + throttle-event emitter
//
// WHAT THIS FILE DOES:
//   Samples Meta OVRPlugin's perf-tier state once per second and emits an
//   `ovr` row into MetricsLogger:
//     - cpu_level      (int 0-4, the CPU performance tier the system is at)
//     - gpu_level      (int 0-4, ditto for GPU)
//     - app_fps        (float — the app's recent framerate as reported by OVRPlugin)
//     - headroom       (float ≈ 1.0 - app_gpu_time / frame_budget; >0 means
//                       still has slack, <0 means over-budget)
//
//   Also detects DROPS in cpu_level or gpu_level frame-over-frame and emits an
//   event row each time. These are the most direct signal we have that the
//   Quest is thermally throttling — when the OS reduces the perf tier the app
//   asked for, it's because the SoC can't sustain that tier any more.
//
// WHY THESE METRICS:
//   The OVR-reported levels are the actionable "throttling" indicators for
//   RQ1 ("on-device limits") and RQ3 (when does the network cost outweigh the
//   local heat / battery cost). Pair this with SystemMetricsCollector's
//   thermal_status field for two independent views of the same phenomenon:
//
//     - OS thermal_status — the kernel's view of system thermal pressure
//     - OVR cpu/gpu level — the Meta runtime's response (it reduces tier when
//                            the OS asks it to throttle)
//
//   When the two agree, you have a high-confidence throttling event. When
//   they disagree, you've found a Meta-runtime quirk worth noting in the
//   thesis discussion.
//
// PLATFORM NOTES:
//   - OVRPlugin is part of the Meta XR SDK; available in all Quest builds.
//   - Editor without Quest Link → OVRPlugin queries throw; we catch and
//     default to -1 / 0 so the CSV still writes a row (just an empty one).
//   - `OVRPlugin.GetAppFramerate()` and the app-time queries are wrapped in
//     try/catch because their exact signatures vary between SDK versions.
//     The cpu/gpu level properties have been stable across SDK versions for
//     years, so they're the most reliable signal.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    public class OvrPerfCollector : MonoBehaviour
    {
        [Tooltip("Seconds between samples. 1 Hz captures throttling events " +
                 "well; the OS doesn't change perf tiers faster than that " +
                 "in practice.")]
        [SerializeField] private float sampleIntervalSec = 1f;

        // Tracks the previous sample so we can detect level DROPS (treated as
        // throttling events). -1 means "haven't sampled yet".
        private int lastCpuLevel = -1;
        private int lastGpuLevel = -1;

        private void Start()
        {
            StartCoroutine(SampleLoop());
        }

        private IEnumerator SampleLoop()
        {
            // Stagger 500 ms relative to SystemMetricsCollector so the two
            // collectors don't both hit JNI / OVRPlugin on the same frame.
            yield return new WaitForSecondsRealtime(0.5f);

            var wait = new WaitForSecondsRealtime(sampleIntervalSec);

            while (true)
            {
                if (MetricsLogger.Instance != null && MetricsLogger.Instance.IsSessionActive)
                {
                    SampleOnce();
                }
                yield return wait;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // SampleOnce — Read every OVR field, emit one `ovr` row, detect drops.
        //
        // Each OVRPlugin access is wrapped in try/catch so a single missing
        // API on an older SDK doesn't take down the whole sample. The fallback
        // values (cpu/gpu = -1, fps = 0, headroom = 0) are recognisable as
        // "unavailable" in the CSV.
        // ─────────────────────────────────────────────────────────────────────
        private void SampleOnce()
        {
            int   cpuLevel = -1;
            int   gpuLevel = -1;
            float appFps   = 0f;
            float headroom = 0f;

#if !UNITY_EDITOR
            // ── CPU/GPU perf tiers (the throttle signal) ──────────────────────
            // These properties have been stable in OVRPlugin since Meta XR SDK
            // v50ish; they return the *current* perf tier the OS is granting
            // the app, NOT the level the app requested. So when these drop,
            // it means the OS has reduced what the app gets.
            try { cpuLevel = OVRPlugin.cpuLevel; }
            catch (System.Exception) { cpuLevel = -1; }

            try { gpuLevel = OVRPlugin.gpuLevel; }
            catch (System.Exception) { gpuLevel = -1; }

            // ── App framerate (OVR-reported) ──────────────────────────────────
            // GetAppFramerate returns the smoothed framerate OVRPlugin is
            // tracking internally — closer to the compositor's view than
            // 1/Time.deltaTime is.
            try { appFps = OVRPlugin.GetAppFramerate(); }
            catch (System.Exception) { appFps = 0f; }

            // ── Headroom: how much of the frame budget the app uses ──────────
            //   1.0  = 0 % used (idle)
            //   0.0  = exactly at limit (no slack)
            //   <0.0 = over budget (frame missed deadline)
            //
            // Computed from the actual frame time vs the display's budget.
            // This includes both CPU and GPU work (not just GPU), which is a
            // less precise signal than OVRPlugin.GetAppGpuTime() would give,
            // but the latter's signature varies across Meta XR SDK versions
            // (sometimes float seconds, sometimes ms, sometimes absent) — a
            // simple Time.unscaledDeltaTime works on every SDK and is good
            // enough as a throttling-headroom proxy for thesis purposes.
            try
            {
                float displayHz = OVRPlugin.systemDisplayFrequency;
                if (displayHz > 0f)
                {
                    float frameTimeMs   = Time.unscaledDeltaTime * 1000f;
                    float frameBudgetMs = 1000f / displayHz;
                    headroom = 1f - (frameTimeMs / frameBudgetMs);
                }
            }
            catch (System.Exception) { headroom = 0f; }

            // ── Throttle-event detection ──────────────────────────────────────
            // When the perf tier DROPS frame-over-frame, the OS has reduced
            // the resources Meta is granting us. That's the canonical
            // "throttling occurred" event. Rises are also possible (when the
            // device cools down and the OS gives back) but less interesting
            // for the thesis. We log drops only.
            if (lastCpuLevel >= 0 && cpuLevel >= 0 && cpuLevel < lastCpuLevel)
            {
                MetricsLogger.Instance?.LogEvent(
                    "cpu_level_drop",
                    $"from={lastCpuLevel};to={cpuLevel}");
            }
            if (lastGpuLevel >= 0 && gpuLevel >= 0 && gpuLevel < lastGpuLevel)
            {
                MetricsLogger.Instance?.LogEvent(
                    "gpu_level_drop",
                    $"from={lastGpuLevel};to={gpuLevel}");
            }
            if (cpuLevel >= 0) lastCpuLevel = cpuLevel;
            if (gpuLevel >= 0) lastGpuLevel = gpuLevel;
#endif

            MetricsLogger.Instance?.LogOvrSample(cpuLevel, gpuLevel, appFps, headroom);
        }
    }
}
