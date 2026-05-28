using System.Collections;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// SystemMetricsCollector.cs — 1 Hz battery / thermal / voltage / current sampler
//
// WHAT THIS FILE DOES:
//   Samples Android system telemetry once per second and emits a `system` row
//   into MetricsLogger. Specifically:
//     - Battery percentage             (BatteryManager.BATTERY_PROPERTY_CAPACITY)
//     - Instantaneous current draw mA  (BatteryManager.BATTERY_PROPERTY_CURRENT_NOW, µA → mA)
//     - Battery voltage mV             (ACTION_BATTERY_CHANGED extra "voltage")
//     - Battery temperature °C         (ACTION_BATTERY_CHANGED extra "temperature", tenths °C)
//     - Thermal status (0-6)           (PowerManager.getCurrentThermalStatus, Android Q+)
//
//   Also detects thermal-status TRANSITIONS and emits an `event` row each time
//   the status changes, so the timeline of throttling events is preserved
//   independently of the 1 Hz cadence.
//
// WHY THESE METRICS:
//   They answer the bulk of RQ1 ("on-device limits") and RQ3 ("when does
//   network cost outweigh local compute cost"):
//
//     - Battery percentage over time → drain rate per minute, per architecture
//     - Current draw → instantaneous power consumption
//     - Battery temperature → device-wide heat proxy
//     - Thermal status → OS's view of system stress (the most authoritative
//                       "is throttling about to happen" indicator)
//
// PLATFORM NOTES:
//   - JNI calls only run on Android device. Editor sees -1 / empty values.
//   - Some Quest builds restrict certain BatteryManager properties; the try/
//     catch wrappers default to -1 so the CSV column shows "unavailable" rather
//     than crashing the session.
//   - PowerManager.getCurrentThermalStatus requires API 29+ (Android 10).
//     Quest 3 runs Android 12+ so this is always available, but the try/catch
//     covers older test builds.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    public class SystemMetricsCollector : MonoBehaviour
    {
        [Tooltip("Seconds between samples. 1 Hz is fine for slow-moving state " +
                 "(battery and thermal). Lower values give finer resolution at " +
                 "the cost of JNI overhead on the main thread.")]
        [SerializeField] private float sampleIntervalSec = 1f;

#if UNITY_ANDROID && !UNITY_EDITOR
        // ── BatteryManager.BATTERY_PROPERTY_* constants ───────────────────────
        // Not exposed by AndroidJavaClass at runtime — must be hard-coded.
        // See: https://developer.android.com/reference/android/os/BatteryManager
        private const int BATTERY_PROPERTY_CAPACITY        = 4;   // % charge
        private const int BATTERY_PROPERTY_CURRENT_NOW     = 2;   // µA, signed
        private const int BATTERY_PROPERTY_CURRENT_AVERAGE = 3;   // µA over ~30 s

        private AndroidJavaObject batteryManager;
        private AndroidJavaObject powerManager;
        private AndroidJavaObject activity;
        private AndroidJavaObject batteryFilter;

        // Tracks the last observed thermal status so transitions can be
        // emitted as event rows. -1 = haven't sampled yet.
        private int lastThermalStatus = -1;
#endif

        // ─────────────────────────────────────────────────────────────────────
        // Start — Resolve JNI handles once, then start the polling coroutine.
        //
        // We grab handles to:
        //   - currentActivity (the Unity Player's Android Activity)
        //   - BatteryManager system service
        //   - PowerManager system service
        //   - IntentFilter for ACTION_BATTERY_CHANGED (reused every sample)
        //
        // These persist for the lifetime of this component, so the per-sample
        // JNI cost is just method calls — no per-call object construction.
        // ─────────────────────────────────────────────────────────────────────
        private void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                }
                batteryManager = activity.Call<AndroidJavaObject>("getSystemService", "batterymanager");
                powerManager   = activity.Call<AndroidJavaObject>("getSystemService", "power");
                batteryFilter  = new AndroidJavaObject("android.content.IntentFilter",
                                                       "android.intent.action.BATTERY_CHANGED");

                Debug.Log("[SystemMetricsCollector] JNI handles resolved");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[SystemMetricsCollector] JNI init failed: {e.Message}");
                // The coroutine will still run — every sample just logs -1s.
            }
#endif
            StartCoroutine(SampleLoop());
        }

        // ─────────────────────────────────────────────────────────────────────
        // SampleLoop — Coroutine that fires once per sampleIntervalSec.
        //
        // Uses WaitForSecondsRealtime so it's unaffected by Time.timeScale
        // (just in case some game code freezes time temporarily — battery
        // sampling should keep going regardless).
        // ─────────────────────────────────────────────────────────────────────
        private IEnumerator SampleLoop()
        {
            // Stagger the first sample by 250 ms so all collectors that start
            // simultaneously don't all hit JNI on the same frame.
            yield return new WaitForSecondsRealtime(0.25f);

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
        // SampleOnce — Read all telemetry fields and emit one `system` row.
        //
        // Every field is wrapped in its own try/catch so a single failed JNI
        // call (e.g. a restricted property on a particular OS build) doesn't
        // take down the whole sample.
        // ─────────────────────────────────────────────────────────────────────
        private void SampleOnce()
        {
            int   batteryPct    = -1;
            int   voltageMv     = -1;
            int   currentMa     = 0;
            float batteryTempC  = -1f;
            int   thermalStatus = -1;

#if UNITY_ANDROID && !UNITY_EDITOR
            // ── Battery percentage (integer property) ─────────────────────────
            try
            {
                if (batteryManager != null)
                    batteryPct = batteryManager.Call<int>("getIntProperty", BATTERY_PROPERTY_CAPACITY);
            }
            catch (System.Exception) { batteryPct = -1; }

            // ── Instantaneous current (µA, signed — negative = discharging) ──
            // On most Quest builds this is a long property. Fall back to int
            // on older devices where the long signature isn't exposed.
            try
            {
                if (batteryManager != null)
                {
                    long microAmps = batteryManager.Call<long>("getLongProperty", BATTERY_PROPERTY_CURRENT_NOW);
                    currentMa = (int)(microAmps / 1000);
                }
            }
            catch (System.Exception)
            {
                try
                {
                    int microAmpsInt = batteryManager.Call<int>("getIntProperty", BATTERY_PROPERTY_CURRENT_NOW);
                    currentMa = microAmpsInt / 1000;
                }
                catch (System.Exception) { currentMa = 0; }
            }

            // ── Voltage + battery temperature: sticky-broadcast lookup ────────
            // Registering null as the receiver with an ACTION_BATTERY_CHANGED
            // filter returns the most recent sticky Intent without subscribing.
            // The "voltage" extra is in mV; "temperature" is in tenths of °C
            // (e.g. 287 = 28.7 °C).
            try
            {
                if (activity != null && batteryFilter != null)
                {
                    var batteryIntent = activity.Call<AndroidJavaObject>("registerReceiver", null, batteryFilter);
                    if (batteryIntent != null)
                    {
                        try { voltageMv = batteryIntent.Call<int>("getIntExtra", "voltage", -1); }
                        catch (System.Exception) { voltageMv = -1; }

                        try
                        {
                            int tenthsC = batteryIntent.Call<int>("getIntExtra", "temperature", -1);
                            if (tenthsC > 0) batteryTempC = tenthsC / 10f;
                        }
                        catch (System.Exception) { batteryTempC = -1f; }

                        batteryIntent.Dispose();
                    }
                }
            }
            catch (System.Exception) { /* extras stay defaulted */ }

            // ── Thermal status (Android Q+ / API 29+) ─────────────────────────
            // Returns one of:
            //   0 NONE       — no throttling
            //   1 LIGHT      — small action being taken
            //   2 MODERATE   — moderate throttling
            //   3 SEVERE     — severe — animations should stop, etc.
            //   4 CRITICAL   — critical — UI should reduce work
            //   5 EMERGENCY  — about to shut down
            //   6 SHUTDOWN   — shutdown imminent
            try
            {
                if (powerManager != null)
                    thermalStatus = powerManager.Call<int>("getCurrentThermalStatus");
            }
            catch (System.Exception) { thermalStatus = -1; }

            // ── Transition event ──────────────────────────────────────────────
            // Whenever the OS thermal status changes, post an event row so the
            // exact moment of throttling is preserved (not just the 1 Hz
            // snapshots — useful if the transition happened mid-second).
            if (thermalStatus >= 0 && thermalStatus != lastThermalStatus)
            {
                if (lastThermalStatus >= 0)
                {
                    MetricsLogger.Instance?.LogEvent(
                        "thermal_status_change",
                        $"from={lastThermalStatus};to={thermalStatus}");
                }
                lastThermalStatus = thermalStatus;
            }
#endif

            MetricsLogger.Instance?.LogSystemSample(
                batteryPct, voltageMv, currentMa, batteryTempC, thermalStatus);
        }

        private void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Release JNI references to avoid leaks across scene reloads.
            batteryManager?.Dispose();
            powerManager?.Dispose();
            batteryFilter?.Dispose();
            activity?.Dispose();
#endif
        }
    }
}
