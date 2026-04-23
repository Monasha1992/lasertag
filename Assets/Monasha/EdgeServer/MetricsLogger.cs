using System;
using System.IO;
using System.Text;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// MetricsLogger.cs — CSV metrics recorder for the edge server study
//
// WHAT THIS FILE DOES:
//   Records per-frame metrics to a CSV file on the Quest's storage so you can
//   pull the data after a test session and analyse it in Python or Excel.
//
// HOW TO USE:
//   1. Call MetricsLogger.Instance.LogFrame(...) from EdgeServerClient each time
//      a mesh is successfully applied.
//   2. Call MetricsLogger.Instance.StartSession() when a test begins.
//   3. Call MetricsLogger.Instance.EndSession() to flush and close the file.
//
// OUTPUT FILE:
//   Saved to Application.persistentDataPath (accessible via Android File Transfer
//   or adb pull). File is named: edge_metrics_<timestamp>.csv
//
// CSV COLUMNS:
//   session_time_s    — seconds since session started
//   rtt_ms            — round-trip latency (depth capture → mesh received) in ms
//   payload_bytes     — bytes of the last outgoing depth frame
//   response_bytes    — bytes of the last incoming mesh response
//   vertex_count      — number of vertices in the received mesh
//   triangle_count    — number of triangles in the received mesh
//   pos_drift_m       — head position delta between send and receive (metres)
//   rot_drift_deg     — head rotation delta between send and receive (degrees)
//   discarded         — 1 if the mesh was discarded due to pose drift, 0 if applied
//   fps               — Unity frame rate at the time this mesh was received
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.EdgeServer
{
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

        // ── State ─────────────────────────────────────────────────────────────
        private StreamWriter writer;
        private bool         sessionActive;
        private float        sessionStartTime;
        private string       currentFilePath;

        // Public read-only view of session state for HUDs / UI.
        public bool   IsSessionActive   => sessionActive;
        public string CurrentFilePath   => currentFilePath;
        public int    FrameCount        => frameCount;

        // ── Rolling averages (printed as summary at session end) ──────────────
        private int   frameCount;
        private double sumRttMs;
        private double sumPayloadBytes;
        private double sumResponseBytes;
        private float  sumFps;

        // ─────────────────────────────────────────────────────────────────────
        // StartSession — Opens a new CSV file and writes the header row
        //
        // Call this when you want to begin a recorded test.
        // Each session gets a unique timestamped filename so sessions don't overwrite.
        // ─────────────────────────────────────────────────────────────────────
        public void StartSession()
        {
            if (sessionActive)
            {
                Debug.LogWarning("[MetricsLogger] Session already active — call EndSession() first");
                return;
            }

            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            currentFilePath  = Path.Combine(Application.persistentDataPath, $"edge_metrics_{timestamp}.csv");

            try
            {
                writer = new StreamWriter(currentFilePath, append: false, Encoding.UTF8);

                // CSV header row
                writer.WriteLine(
                    "session_time_s,rtt_ms,payload_bytes,response_bytes," +
                    "vertex_count,triangle_count,pos_drift_m,rot_drift_deg,discarded,fps"
                );
                writer.Flush();

                sessionActive    = true;
                sessionStartTime = Time.realtimeSinceStartup;
                frameCount       = 0;
                sumRttMs         = 0;
                sumPayloadBytes  = 0;
                sumResponseBytes = 0;
                sumFps           = 0;

                Debug.Log($"[MetricsLogger] Session started → {currentFilePath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[MetricsLogger] Failed to open file: {e.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // LogFrame — Records one frame's metrics as a CSV row
        //
        // Call this from EdgeServerClient.ApplyMeshData (or wherever the final
        // decision is made about whether to apply or discard a mesh).
        //
        // Parameters:
        //   rttMs          — measured round-trip time in milliseconds
        //   payloadBytes   — bytes sent for this frame (header + frame data + depth)
        //   responseBytes  — bytes received for this frame (header + mesh)
        //   vertexCount    — vertices in the received mesh (0 if discarded)
        //   triangleCount  — triangles in the received mesh (0 if discarded)
        //   posDriftM      — metres of head movement since frame was sent
        //   rotDriftDeg    — degrees of head rotation since frame was sent
        //   discarded      — true if mesh was discarded (pose too stale)
        // ─────────────────────────────────────────────────────────────────────
        public void LogFrame(
            long  rttMs,
            int   payloadBytes,
            int   responseBytes,
            int   vertexCount,
            int   triangleCount,
            float posDriftM,
            float rotDriftDeg,
            bool  discarded
        )
        {
            if (!sessionActive || writer == null) return;

            float sessionTime = Time.realtimeSinceStartup - sessionStartTime;
            float fps         = 1.0f / Mathf.Max(Time.deltaTime, 0.001f);

            try
            {
                writer.WriteLine(
                    $"{sessionTime:F3},{rttMs},{payloadBytes},{responseBytes}," +
                    $"{vertexCount},{triangleCount},{posDriftM:F4},{rotDriftDeg:F2},{(discarded ? 1 : 0)},{fps:F1}"
                );

                // Flush periodically (every 10 frames) to avoid losing data on crash
                frameCount++;
                if (frameCount % 10 == 0) writer.Flush();

                // Update rolling totals for end-of-session summary
                sumRttMs         += rttMs;
                sumPayloadBytes  += payloadBytes;
                sumResponseBytes += responseBytes;
                sumFps           += fps;
            }
            catch (Exception e)
            {
                Debug.LogError($"[MetricsLogger] Write failed: {e.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // EndSession — Flushes, prints a summary, and closes the CSV file
        // ─────────────────────────────────────────────────────────────────────
        public void EndSession()
        {
            if (!sessionActive || writer == null) return;

            writer.Flush();
            writer.Close();
            writer        = null;
            sessionActive = false;

            // Print summary to Unity console
            if (frameCount > 0)
            {
                Debug.Log(
                    $"[MetricsLogger] Session ended — {frameCount} frames\n" +
                    $"  Avg RTT:           {sumRttMs / frameCount:F1} ms\n" +
                    $"  Avg payload sent:  {sumPayloadBytes / frameCount:F0} bytes\n" +
                    $"  Avg response recv: {sumResponseBytes / frameCount:F0} bytes\n" +
                    $"  Avg FPS:           {sumFps / frameCount:F1}\n" +
                    $"  File: {currentFilePath}"
                );
            }
        }

        // Cleanup if the object is destroyed without EndSession being called
        private void OnDestroy()
        {
            if (sessionActive) EndSession();
        }
    }
}
