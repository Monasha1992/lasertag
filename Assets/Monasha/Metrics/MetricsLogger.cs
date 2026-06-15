using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// MetricsLogger.cs — Multi-sample-type CSV recorder for the thesis study
//
// WHAT THIS FILE DOES:
//   Records metrics to a single CSV file per session, with multiple row types
//   sharing one schema. Each row's `sample_type` column tells the analysis
//   script how to parse it. Columns not relevant to a given row type are left
//   empty (pandas reads them as NaN).
//
// HOW TO USE:
//   1. Add MetricsLogger to a GameObject early in the scene (e.g. on the
//      ArchitectureManager root). The singleton survives scene reloads.
//   2. Set session metadata via SetSessionMetadata(...) before the session
//      starts. A session auto-starts in Start() — call EndSession() +
//      StartSession() to open a new CSV file (auto-named with timestamp)
//      with updated metadata.
//   3. Collectors call LogFrameSample / LogMeshSample / LogSystemSample /
//      LogOvrSample / LogEvent on the singleton.
//   4. Call EndSession() to flush and close (also fires on OnApplicationQuit).
//
// CSV FORMAT:
//   One header row, then rows of varying sample types. All rows share the
//   same column union; unused columns are empty.
//
//   Column order (37 columns):
//     sample_type, session_time_s, wall_ms,
//     // event-specific
//     event_name, event_payload,
//     // frame-specific
//     frame_time_ms, fps, display_hz,
//     // mesh-specific
//     rtt_ms, payload_bytes, response_bytes, cum_bytes_sent, cum_bytes_recv,
//     vertex_count, triangle_count, pos_drift_m, rot_drift_deg, discarded,
//     server_ts_ms, server_total_ms, server_parse_ms, server_integrate_ms,
//     server_mesh_ms,
//     // system-specific (1 Hz)
//     battery_pct, voltage_mv, current_ma, battery_temp_c, thermal_status,
//     // ovr-specific (1 Hz)
//     cpu_level, gpu_level,
//     // meta-specific (session header)
//     study_phase, participant_id, headset_id, architecture, environment,
//     network_profile, build_sha
//
// FILE LOCATION:
//   Application.persistentDataPath / metrics_[portNNNN_]<UTC-timestamp>.csv
//   (edge builds include the connected server port, e.g. metrics_port9901_*.csv)
//   On Quest: /sdcard/Android/data/<package>/files/metrics_*.csv
//   Pull with:  adb pull /sdcard/Android/data/<package>/files/ ~/Desktop/
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    public enum SampleType { Meta, Frame, Mesh, System, Ovr, Event }

    public class MetricsLogger : MonoBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static MetricsLogger Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // Optional filename tag (e.g. "port9901") so each headset's CSV is
        // identifiable when several run at once. Set by EdgeServerClient once it
        // knows which server instance it connected to.
        private string fileTag = "";
        public void SetFileTag(string tag) => fileTag = tag ?? "";

        // Set true by MeasurementController (button-B trigger) so the session is
        // started by the trigger, not auto-started here. Left false otherwise so
        // standalone-only setups keep recording automatically.
        public static bool ManualStart = false;

        [Header("Auto-start")]
        [Tooltip("In edge mode the session waits this many seconds for the client " +
                 "to connect (so the CSV filename can include the server port) " +
                 "before starting anyway. Standalone mode starts immediately.")]
        [SerializeField] private float edgeConnectTimeoutSec = 10f;

        private void Start()
        {
            // Auto-start recording on every run. In edge mode, wait briefly for
            // the port tag so the filename carries it (see AutoStartRoutine).
            StartCoroutine(AutoStartRoutine());
        }

        private IEnumerator AutoStartRoutine()
        {
            // A MeasurementController (button B) owns start/stop — don't auto-start.
            if (ManualStart) yield break;

            var arch = ArchitectureManager.Instance;
            bool edge = arch != null && arch.IsEdge;

            if (edge)
            {
                // Hold off until EdgeServerClient reports its port (SetFileTag),
                // or the timeout elapses (server never came up → start untagged).
                float waited = 0f;
                while (string.IsNullOrEmpty(fileTag) && waited < edgeConnectTimeoutSec)
                {
                    waited += Time.unscaledDeltaTime;
                    yield return null;
                }
            }

            if (!sessionActive) StartSession();
        }

        // ── State ─────────────────────────────────────────────────────────────
        private StreamWriter writer;
        private bool         sessionActive;
        private float        sessionStartTime;
        private string       currentFilePath;

        // Buffered StringBuilder for row composition — reused, no per-row alloc
        private readonly StringBuilder sb = new StringBuilder(512);

        // Public read-only view of session state
        public bool   IsSessionActive => sessionActive;
        public string CurrentFilePath => currentFilePath;
        public int    FrameCount      => frameCount;

        // ── Session metadata (set before StartSession) ────────────────────────
        // Written into the `meta` row at the top of the CSV and recoverable by
        // analysis scripts. Defaults are placeholders — set them via
        // SetSessionMetadata before starting a session.
        private string studyPhase       = "rig";          // "rig" or "participant"
        private string participantId    = "";
        private string headsetId        = "";
        private string architecture     = "standalone";    // "standalone" or "edge"
        private string environmentLabel = "unknown";
        private string networkProfile   = "n/a";
        private string buildSha         = "";

        // Public accessors so the debug HUD / external code can show current state
        public string Architecture      => architecture;
        public string EnvironmentLabel  => environmentLabel;
        public string ParticipantId     => participantId;

        // ── Cumulative bandwidth totals (sent/recv including headers) ──────────
        private long cumBytesSent;
        private long cumBytesRecv;

        public long CumBytesSent => cumBytesSent;
        public long CumBytesRecv => cumBytesRecv;

        // ── Rolling totals (printed as summary at session end) ────────────────
        private int   frameCount;
        private int   meshCount;
        private double sumRttMs;
        private double sumPayloadBytes;
        private double sumResponseBytes;

        // ── Mesh-churn tracking ──────────────────────────────────────────────
        // After each LogMeshSample, we compare to the previous sample and emit
        // a `mesh_churn` event row whenever the vert/tri count changed. This
        // is the "objective visual stability" metric — a mesh whose churn is
        // 0 frame-after-frame is visually stable; one whose churn fluctuates
        // is wobbling regardless of what RTT or framerate say.
        //
        // -1 means "no prior sample" (suppress churn on the first mesh).
        private int lastMeshVertCount = -1;
        private int lastMeshTriCount  = -1;

        // ─────────────────────────────────────────────────────────────────────
        // SetSessionMetadata — Fill the per-session identification fields.
        //
        // Called by ArchitectureManager and any study-protocol controller.
        // Safe to call before StartSession (gets written into the meta row) or
        // mid-session (updates fields for subsequent rows — meta row is only
        // written once at the start, so changes to studyPhase/participantId
        // after StartSession won't be reflected in the CSV).
        // ─────────────────────────────────────────────────────────────────────
        public void SetSessionMetadata(
            string studyPhase, string participantId, string headsetId,
            string architecture, string environmentLabel, string networkProfile,
            string buildSha)
        {
            this.studyPhase       = studyPhase       ?? "";
            this.participantId    = participantId    ?? "";
            this.headsetId        = headsetId        ?? "";
            this.architecture     = architecture     ?? "standalone";
            this.environmentLabel = environmentLabel ?? "unknown";
            this.networkProfile   = networkProfile   ?? "n/a";
            this.buildSha         = buildSha         ?? "";
        }

        // ─────────────────────────────────────────────────────────────────────
        // StartSession — Open a new CSV file and write the header + meta row
        // ─────────────────────────────────────────────────────────────────────
        public void StartSession()
        {
            if (sessionActive)
            {
                Debug.LogWarning("[MetricsLogger] Session already active — call EndSession() first");
                return;
            }

            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            string tagPart   = string.IsNullOrEmpty(fileTag) ? "" : fileTag + "_";
            currentFilePath  = Path.Combine(Application.persistentDataPath, $"metrics_{tagPart}{timestamp}.csv");

            try
            {
                writer = new StreamWriter(currentFilePath, append: false, Encoding.UTF8);
                writer.NewLine = "\n";

                // Single header row — union of all sample-type-specific columns
                writer.WriteLine(
                    "sample_type,session_time_s,wall_ms," +
                    "event_name,event_payload," +
                    "frame_time_ms,fps,display_hz," +
                    "rtt_ms,payload_bytes,response_bytes,cum_bytes_sent,cum_bytes_recv," +
                    "vertex_count,triangle_count,pos_drift_m,rot_drift_deg,discarded,server_ts_ms," +
                    "server_total_ms,server_parse_ms,server_integrate_ms,server_mesh_ms," +
                    "battery_pct,voltage_mv,current_ma,battery_temp_c,thermal_status," +
                    "cpu_level,gpu_level," +
                    "study_phase,participant_id,headset_id,architecture,environment," +
                    "network_profile,build_sha"
                );

                sessionActive    = true;
                sessionStartTime = Time.realtimeSinceStartup;
                frameCount       = 0;
                meshCount        = 0;
                sumRttMs         = 0;
                sumPayloadBytes  = 0;
                sumResponseBytes = 0;
                cumBytesSent     = 0;
                cumBytesRecv     = 0;

                // Meta row: snapshot of session identity at the moment recording began.
                WriteMetaRow();

                writer.Flush();

                Debug.Log($"[MetricsLogger] Session started → {currentFilePath} ({architecture}, {environmentLabel})");
            }
            catch (Exception e)
            {
                Debug.LogError($"[MetricsLogger] Failed to open file: {e.Message}");
                sessionActive = false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Internal row writers
        //
        // Each LogXxx method composes a CSV row into the shared StringBuilder
        // then flushes it via writer.WriteLine. Empty cells are commas with no
        // value between them. Numeric formatting uses InvariantCulture so that
        // locale-dependent decimal separators don't sneak in (some Quest
        // locales use ',' as decimal separator, which would corrupt CSV).
        // ─────────────────────────────────────────────────────────────────────

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private void BeginRow(string sampleType)
        {
            sb.Clear();
            sb.Append(sampleType).Append(',');
            sb.Append((Time.realtimeSinceStartup - sessionStartTime).ToString("F3", Inv)).Append(',');
            sb.Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(Inv));
        }

        // Each subsequent column adds a leading comma. The final WriteLine
        // appends the newline.
        private void Col(string s) { sb.Append(',').Append(s ?? ""); }
        private void Col(int    v) { sb.Append(',').Append(v.ToString(Inv)); }
        private void Col(long   v) { sb.Append(',').Append(v.ToString(Inv)); }
        private void Col(float  v) { sb.Append(',').Append(v.ToString("F3", Inv)); }
        private void Col(double v) { sb.Append(',').Append(v.ToString("F3", Inv)); }
        private void Col(bool   v) { sb.Append(',').Append(v ? "1" : "0"); }
        private void EmptyN(int n) { for (int i = 0; i < n; i++) sb.Append(','); }

        // Writes the meta row using current metadata values. Called from StartSession.
        private void WriteMetaRow()
        {
            BeginRow("meta");
            EmptyN(2);    // event_name, event_payload
            EmptyN(3);    // frame_time_ms, fps, display_hz
            EmptyN(15);   // mesh columns (rtt..server_mesh_ms)
            EmptyN(5);    // system columns
            EmptyN(2);    // ovr columns
            Col(studyPhase);
            Col(participantId);
            Col(headsetId);
            Col(architecture);
            Col(environmentLabel);
            Col(networkProfile);
            Col(buildSha);

            writer.WriteLine(sb.ToString());
        }

        // ─────────────────────────────────────────────────────────────────────
        // LogFrameSample — one row per Unity frame (called from FrameRateCollector)
        // ─────────────────────────────────────────────────────────────────────
        public void LogFrameSample(float frameTimeMs, float fps, float displayHz)
        {
            if (!sessionActive || writer == null) return;

            BeginRow("frame");
            EmptyN(2);                           // event cols
            Col(frameTimeMs);                    // frame_time_ms
            Col(fps);                            // fps
            Col(displayHz);                      // display_hz
            EmptyN(15);                          // mesh cols
            EmptyN(5);                           // system cols
            EmptyN(2);                           // ovr cols
            EmptyN(7);                           // meta cols

            writer.WriteLine(sb.ToString());
            frameCount++;
            if (frameCount % 60 == 0) writer.Flush();   // ~once per second at 60 fps
        }

        // ─────────────────────────────────────────────────────────────────────
        // LogMeshSample — one row per mesh applied (or discarded).
        // Edge mode: includes RTT, payload/response bytes, server_ts_ms (echo).
        // Standalone mode: RTT/bytes/server_ts blank-ish (0), but vertex/triangle
        //                  counts and discarded flag still populated.
        // ─────────────────────────────────────────────────────────────────────
        public void LogMeshSample(
            long  rttMs,            // 0 in standalone mode
            int   payloadBytes,     // 0 in standalone mode
            int   responseBytes,    // 0 in standalone mode
            int   vertexCount,
            int   triangleCount,
            float posDriftM,
            float rotDriftDeg,
            bool  discarded,
            long  serverTsMs = 0,   // timestamp echo from Mac; 0 in standalone
            float serverTotalMs = 0f,
            float serverParseMs = 0f,
            float serverIntegrateMs = 0f,
            float serverMeshMs = 0f
        )
        {
            if (!sessionActive || writer == null) return;

            cumBytesSent += payloadBytes;
            cumBytesRecv += responseBytes;

            BeginRow("mesh");
            EmptyN(2);                           // event cols
            EmptyN(3);                           // frame cols
            Col(rttMs);                          // rtt_ms
            Col(payloadBytes);                   // payload_bytes
            Col(responseBytes);                  // response_bytes
            Col(cumBytesSent);                   // cum_bytes_sent
            Col(cumBytesRecv);                   // cum_bytes_recv
            Col(vertexCount);                    // vertex_count
            Col(triangleCount);                  // triangle_count
            Col(posDriftM);                      // pos_drift_m
            Col(rotDriftDeg);                    // rot_drift_deg
            Col(discarded);                      // discarded
            Col(serverTsMs);                     // server_ts_ms
            Col(serverTotalMs);                  // server_total_ms
            Col(serverParseMs);                  // server_parse_ms
            Col(serverIntegrateMs);              // server_integrate_ms
            Col(serverMeshMs);                   // server_mesh_ms
            EmptyN(5);                           // system cols
            EmptyN(2);                           // ovr cols
            EmptyN(7);                           // meta cols

            writer.WriteLine(sb.ToString());

            // Maintain rolling totals
            meshCount++;
            sumRttMs += rttMs;
            sumPayloadBytes += payloadBytes;
            sumResponseBytes += responseBytes;
            if (meshCount % 10 == 0) writer.Flush();

            // ── Mesh churn ────────────────────────────────────────────────
            // Emit a mesh_churn event row whenever the geometry size changed
            // from the previous mesh. Skipped on discarded meshes (they have
            // counts == 0 by convention which would always count as a change)
            // and on the very first mesh of the session (no prior to diff
            // against). The payload uses key=value;key=value so the analysis
            // script can parse it back into pandas columns.
            if (!discarded && lastMeshVertCount >= 0)
            {
                int vDelta = vertexCount   - lastMeshVertCount;
                int tDelta = triangleCount - lastMeshTriCount;
                if (vDelta != 0 || tDelta != 0)
                {
                    LogEvent("mesh_churn",
                        $"vdelta={vDelta};tdelta={tDelta};v={vertexCount};t={triangleCount}");
                }
            }
            if (!discarded)
            {
                lastMeshVertCount = vertexCount;
                lastMeshTriCount  = triangleCount;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // LogSystemSample — 1 Hz battery / thermal / voltage / current row.
        // Called by SystemMetricsCollector. All values pre-converted to the
        // documented units (mA, mV, ºC, integer thermal status 0-6).
        // ─────────────────────────────────────────────────────────────────────
        public void LogSystemSample(
            int   batteryPct,
            int   voltageMv,
            int   currentMa,
            float batteryTempC,
            int   thermalStatus
        )
        {
            if (!sessionActive || writer == null) return;

            BeginRow("system");
            EmptyN(2);                           // event cols
            EmptyN(3);                           // frame cols
            EmptyN(15);                          // mesh cols
            Col(batteryPct);                     // battery_pct
            Col(voltageMv);                      // voltage_mv
            Col(currentMa);                      // current_ma
            Col(batteryTempC);                   // battery_temp_c
            Col(thermalStatus);                  // thermal_status
            EmptyN(2);                           // ovr cols
            EmptyN(7);                           // meta cols

            writer.WriteLine(sb.ToString());
        }

        // ─────────────────────────────────────────────────────────────────────
        // LogOvrSample — 1 Hz OVR perf-tier row (cpu_level, gpu_level).
        // Called by OvrPerfCollector. app_fps/headroom were removed: under the
        // OpenXR backend GetAppFramerate() returns 0, and headroom was just a
        // restatement of frame_time_ms — use the `frame` rows for fps/frame time.
        // ─────────────────────────────────────────────────────────────────────
        public void LogOvrSample(int cpuLevel, int gpuLevel)
        {
            if (!sessionActive || writer == null) return;

            BeginRow("ovr");
            EmptyN(2);                           // event cols
            EmptyN(3);                           // frame cols
            EmptyN(15);                          // mesh cols
            EmptyN(5);                           // system cols
            Col(cpuLevel);                       // cpu_level
            Col(gpuLevel);                       // gpu_level
            EmptyN(7);                           // meta cols

            writer.WriteLine(sb.ToString());
        }

        // ─────────────────────────────────────────────────────────────────────
        // LogEvent — async/event-driven row (game events, connect/disconnect,
        // throttling transitions, study annotations).
        //
        // `payload` may contain ad-hoc detail; commas and double quotes are
        // sanitised to keep the CSV well-formed. For complex payloads, prefer
        // key=value;key=value format (the analysis script can parse it back).
        // ─────────────────────────────────────────────────────────────────────
        public void LogEvent(string name, string payload = "")
        {
            if (!sessionActive || writer == null) return;

            // Sanitise commas/quotes to avoid breaking CSV parsing
            string safeName    = name?.Replace(",", ";").Replace("\"", "'") ?? "";
            string safePayload = payload?.Replace(",", ";").Replace("\"", "'") ?? "";

            BeginRow("event");
            Col(safeName);                       // event_name
            Col(safePayload);                    // event_payload
            EmptyN(3);                           // frame cols
            EmptyN(15);                          // mesh cols
            EmptyN(5);                           // system cols
            EmptyN(2);                           // ovr cols
            EmptyN(7);                           // meta cols

            writer.WriteLine(sb.ToString());
            writer.Flush();      // events are rare → always flush immediately
        }

        // ─────────────────────────────────────────────────────────────────────
        // EndSession — Flush, print summary, close file
        // ─────────────────────────────────────────────────────────────────────
        public void EndSession()
        {
            if (!sessionActive || writer == null) return;

            writer.Flush();
            writer.Close();
            writer        = null;
            sessionActive = false;

            if (meshCount > 0)
            {
                Debug.Log(
                    $"[MetricsLogger] Session ended — {frameCount} frames, {meshCount} meshes\n" +
                    $"  Avg RTT:           {sumRttMs / meshCount:F1} ms\n" +
                    $"  Avg payload sent:  {sumPayloadBytes / meshCount:F0} bytes\n" +
                    $"  Avg response recv: {sumResponseBytes / meshCount:F0} bytes\n" +
                    $"  File: {currentFilePath}"
                );
            }
            else
            {
                Debug.Log($"[MetricsLogger] Session ended — {frameCount} frames, no meshes.\n  File: {currentFilePath}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // LogFrame — DEPRECATED back-compat alias. Forwards to LogMeshSample.
        // EdgeServerClient now calls LogMeshSample directly — no callers
        // remain, so this can be deleted whenever convenient.
        // ─────────────────────────────────────────────────────────────────────
        [Obsolete("Use LogMeshSample. Kept for backwards compatibility during refactor.")]
        public void LogFrame(
            long  rttMs,
            int   payloadBytes,
            int   responseBytes,
            int   vertexCount,
            int   triangleCount,
            float posDriftM,
            float rotDriftDeg,
            bool  discarded
        ) => LogMeshSample(rttMs, payloadBytes, responseBytes, vertexCount, triangleCount,
                           posDriftM, rotDriftDeg, discarded, 0, 0, 0, 0, 0);

        private void OnDestroy()
        {
            if (sessionActive) EndSession();
        }

        private void OnApplicationQuit()
        {
            if (sessionActive) EndSession();
        }
    }
}
