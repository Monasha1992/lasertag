using System;
using Anaglyph.XRTemplate.DepthKit;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Anaglyph.EdgeServer
{
    // ── DepthFrameSender ──────────────────────────────────────────────────────────
    // Hooks DepthKitDriver.Updated, rate-limits to sendFrequency, then:
    //   1. Dispatches DepthCapture.compute to copy each eye slice from the external
    //      Android depth Texture2DArray into two readable R32Float RenderTextures.
    //   2. Issues AsyncGPUReadback on each staging texture (works on R32Float).
    //   3. When both readbacks complete, builds and sends a DepthFrame packet.
    //
    // Why the compute indirection:
    //   The external HW-buffer texture that the Quest provides has Unity format
    //   'None', so AsyncGPUReadback fails on it directly.  The existing DepthNorm
    //   compute shader already reads from that texture successfully, so our copy
    //   kernel works the same way.
    //
    // Wire format note:
    //   Depth pixels are written as float32 (4 bytes each), NOT R16Unorm (2 bytes).
    //   Protocol.swift reads sliceBytes = w*h*4 to match.

    [RequireComponent(typeof(EdgeClient))]
    public class DepthFrameSender : MonoBehaviour
    {
        [SerializeField] private EdgeServerSettings settings;
        [SerializeField] private EdgeClient         client;

        [Tooltip("Assign Assets/Anaglyph/EdgeServer/DepthCapture.compute")]
        [SerializeField] private ComputeShader depthCaptureCompute;

        // ── Kernel handles ────────────────────────────────────────────────────
        private int kernelSlice0;
        private int kernelSlice1;
        private static readonly int PropDepthTex     = Shader.PropertyToID("agDepthTex");
        private static readonly int PropCaptureDest  = Shader.PropertyToID("captureOutput");

        // ── Staging textures (R32Float, one per eye) ──────────────────────────
        private RenderTexture stagingSlice0;
        private RenderTexture stagingSlice1;

        // ── Readback coordination ─────────────────────────────────────────────
        private bool   pendingReadback;
        private byte[] pending0;    // set when slice-0 readback lands
        private byte[] pending1;    // set when slice-1 readback lands
        private int    readbacksDone;

        // Captured per-frame (Matrix4x4 is a struct — these are value copies)
        private long       capturedTs;
        private int        capturedW, capturedH;
        private Matrix4x4[] capturedProj = new Matrix4x4[2];
        private Matrix4x4[] capturedView = new Matrix4x4[2];
        private float      capturedNear, capturedFar;

        // ── Rate limiting ─────────────────────────────────────────────────────
        private float lastSendTime = -999f;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            if (depthCaptureCompute == null)
            {
                Debug.LogError("[DepthFrameSender] depthCaptureCompute is not assigned — " +
                               "drag DepthCapture.compute into the Inspector field.");
                enabled = false;
                return;
            }

            kernelSlice0 = depthCaptureCompute.FindKernel("CaptureSlice0");
            kernelSlice1 = depthCaptureCompute.FindKernel("CaptureSlice1");
        }

        private void Reset() => client = GetComponent<EdgeClient>();

        private void OnEnable()
        {
            if (DepthKitDriver.Instance)
                DepthKitDriver.Instance.Updated += OnDepthUpdated;
        }

        private void OnDisable()
        {
            if (DepthKitDriver.Instance)
                DepthKitDriver.Instance.Updated -= OnDepthUpdated;
        }

        private void OnDestroy()
        {
            stagingSlice0?.Release();
            stagingSlice1?.Release();
        }

        // ── Depth frame handler ───────────────────────────────────────────────

        private void OnDepthUpdated()
        {
            if (Time.realtimeSinceStartup - lastSendTime < 1f / settings.sendFrequency) return;
            if (pendingReadback) return;
            if (!client.IsConnected) return;

            DepthKitDriver dkd = DepthKitDriver.Instance;
            if (!DepthKitDriver.DepthAvailable || dkd.DepthTex == null) return;

            int w = dkd.DepthTex.width;
            int h = dkd.DepthTex.height;

            EnsureStaging(w, h);

            // Capture state (structs → value copies)
            capturedTs      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            capturedW       = w;
            capturedH       = h;
            capturedProj[0] = dkd.Proj[0];
            capturedProj[1] = dkd.Proj[1];
            capturedView[0] = dkd.View[0];
            capturedView[1] = dkd.View[1];
            capturedNear    = dkd.Planes.x;
            capturedFar     = dkd.Planes.y;

            // ── Step 1: GPU copy external depth → R32Float staging ────────────
            depthCaptureCompute.SetTexture(kernelSlice0, PropDepthTex,    dkd.DepthTex);
            depthCaptureCompute.SetTexture(kernelSlice0, PropCaptureDest, stagingSlice0);
            depthCaptureCompute.Dispatch(kernelSlice0,
                Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);

            depthCaptureCompute.SetTexture(kernelSlice1, PropDepthTex,    dkd.DepthTex);
            depthCaptureCompute.SetTexture(kernelSlice1, PropCaptureDest, stagingSlice1);
            depthCaptureCompute.Dispatch(kernelSlice1,
                Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);

            Debug.Log($"[DepthFrameSender] Dispatched depth capture  {w}×{h}  " +
                      $"ts={capturedTs}  near={capturedNear:F3}  far={capturedFar:F3}");

            // ── Step 2: AsyncGPUReadback from the staging textures ────────────
            pendingReadback = true;
            pending0        = null;
            pending1        = null;
            readbacksDone   = 0;
            lastSendTime    = Time.realtimeSinceStartup;

            // R32Float → raw float bytes (4 bytes per pixel)
            AsyncGPUReadback.Request(stagingSlice0, 0, GraphicsFormat.R32_SFloat, req =>
            {
                if (req.hasError)
                    Debug.LogWarning("[DepthFrameSender] Readback slice0 error");
                else
                    pending0 = req.GetData<byte>().ToArray();
                OnSliceDone();
            });

            AsyncGPUReadback.Request(stagingSlice1, 0, GraphicsFormat.R32_SFloat, req =>
            {
                if (req.hasError)
                    Debug.LogWarning("[DepthFrameSender] Readback slice1 error");
                else
                    pending1 = req.GetData<byte>().ToArray();
                OnSliceDone();
            });
        }

        // ── Readback completion (main thread) ─────────────────────────────────
        // AsyncGPUReadback callbacks always fire on the main thread, so
        // readbacksDone++ is safe without Interlocked.

        private void OnSliceDone()
        {
            readbacksDone++;
            if (readbacksDone < 2) return;

            pendingReadback = false;

            if (pending0 == null || pending1 == null)
            {
                Debug.LogWarning("[DepthFrameSender] One or both slice readbacks failed — skipping frame");
                return;
            }

            Debug.Log($"[DepthFrameSender] Sending depth frame  " +
                      $"{capturedW}×{capturedH}  slice={pending0.Length} bytes each  " +
                      $"totalPacket≈{1 + 8 + 4 + 4 + 256 + 8 + pending0.Length * 2} bytes");

            byte[] packet = EdgeProtocol.BuildDepthFramePacket(
                capturedTs, capturedW, capturedH,
                capturedProj, capturedView,
                capturedNear, capturedFar,
                pending0, pending1);

            client.Send(packet);
        }

        // ── Staging texture management ────────────────────────────────────────

        private void EnsureStaging(int w, int h)
        {
            if (stagingSlice0 != null && stagingSlice0.width == w && stagingSlice0.height == h)
                return;

            stagingSlice0?.Release();
            stagingSlice1?.Release();

            var desc = new RenderTextureDescriptor(w, h, GraphicsFormat.R32_SFloat, 0)
            {
                enableRandomWrite = true    // required for RWTexture2D in compute
            };
            stagingSlice0 = new RenderTexture(desc);
            stagingSlice1 = new RenderTexture(desc);
            stagingSlice0.Create();
            stagingSlice1.Create();

            Debug.Log($"[DepthFrameSender] Allocated R32Float staging textures {w}×{h} × 2");
        }
    }
}
