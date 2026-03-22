// RemoteProcessorClient.cs
// Connects to the CUDA server on the PC via TCP.
// Sends depth frames at the TSDF update rate, sends mesh requests instead
// of running local marching cubes, and receives mesh results back.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Anaglyph.XRTemplate.DepthKit;
using Anaglyph.XRTemplate.Remote;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Anaglyph.XRTemplate
{
    public class RemoteProcessorClient : MonoBehaviour
    {
        public static RemoteProcessorClient Instance { get; private set; }

        [Header("Connection")]
        [SerializeField] private string serverIp  = "192.168.1.100";
        [SerializeField] private int    serverPort = 9876;
        [SerializeField] private float  reconnectInterval = 3f;

        public bool IsConnected => m_connected;

        // ---------------------------------------------------------------
        // Mesh result data returned to MeshChunk
        // ---------------------------------------------------------------
        public class MeshData
        {
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public int[]     Indices;
        }

        // ---------------------------------------------------------------
        // Private state
        // ---------------------------------------------------------------
        private TcpClient  m_tcp;
        private Stream     m_stream;
        private bool       m_connected;

        // Pending mesh requests: requestId -> TCS
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<MeshData>>
            m_pending = new();

        private uint m_nextRequestId;
        private CancellationTokenSource m_cts;

        // ---------------------------------------------------------------
        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            m_cts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            _ = ConnectLoop(m_cts.Token);

            EnvironmentMapper.Instance.Updated += OnEnvironmentUpdated;
            EnvironmentMapper.Instance.Cleared  += OnEnvironmentCleared;
        }

        private void OnDestroy()
        {
            m_cts?.Cancel();
            m_tcp?.Close();
        }

        // ---------------------------------------------------------------
        // Connection loop — reconnects after disconnect
        // ---------------------------------------------------------------
        private async Task ConnectLoop(CancellationToken ctkn)
        {
            while (!ctkn.IsCancellationRequested)
            {
                try
                {
                    Debug.Log($"[RemoteClient] Connecting to {serverIp}:{serverPort}...");
                    m_tcp = new TcpClient();
                    await m_tcp.ConnectAsync(serverIp, serverPort);
                    m_stream    = m_tcp.GetStream();
                    m_connected = true;
                    Debug.Log("[RemoteClient] Connected to CUDA server.");

                    await ReceiveLoop(ctkn);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[RemoteClient] Connection error: {e.Message}");
                }
                finally
                {
                    m_connected = false;
                    m_tcp?.Close();
                    m_tcp = null;

                    // Fail all pending requests
                    foreach (var kv in m_pending)
                        kv.Value.TrySetResult(null);
                    m_pending.Clear();
                }

                if (!ctkn.IsCancellationRequested)
                    await Task.Delay(TimeSpan.FromSeconds(reconnectInterval), ctkn);
            }
        }

        // ---------------------------------------------------------------
        // Background receive loop — runs on a thread pool thread
        // ---------------------------------------------------------------
        private Task ReceiveLoop(CancellationToken ctkn)
        {
            return Task.Run(() =>
            {
                while (!ctkn.IsCancellationRequested && m_connected)
                {
                    var (type, payload) = Wire.ReadMsg(m_stream);

                    switch (type)
                    {
                        case MessageType.MeshResult:
                            HandleMeshResult(payload);
                            break;

                        case MessageType.Pong:
                            // nothing needed
                            break;

                        default:
                            Debug.LogWarning($"[RemoteClient] Unexpected message type {type}");
                            break;
                    }
                }
            }, ctkn);
        }

        // ---------------------------------------------------------------
        // Handle incoming mesh result
        // ---------------------------------------------------------------
        private void HandleMeshResult(byte[] payload)
        {
            if (payload.Length < Marshal.SizeOf<MeshResultHeader>()) return;

            MeshResultHeader hdr = MemoryMarshal.Read<MeshResultHeader>(payload);
            int hdrSize    = Marshal.SizeOf<MeshResultHeader>();
            int vertStride = 6 * sizeof(float);  // pos(3) + norm(3)

            if (!m_pending.TryRemove(hdr.RequestId, out var tcs)) return;

            uint nv = hdr.NumVertices;
            uint ni = hdr.NumIndices;

            var verts   = new Vector3[nv];
            var normals = new Vector3[nv];
            var indices = new int[ni];

            int offset = hdrSize;
            for (int i = 0; i < nv; i++)
            {
                float px = BitConverter.ToSingle(payload, offset);      offset += 4;
                float py = BitConverter.ToSingle(payload, offset);      offset += 4;
                float pz = BitConverter.ToSingle(payload, offset);      offset += 4;
                float nx = BitConverter.ToSingle(payload, offset);      offset += 4;
                float ny = BitConverter.ToSingle(payload, offset);      offset += 4;
                float nz = BitConverter.ToSingle(payload, offset);      offset += 4;
                verts[i]   = new Vector3(px, py, pz);
                normals[i] = new Vector3(nx, ny, nz);
            }
            for (int i = 0; i < ni; i++)
            {
                indices[i] = (int)BitConverter.ToUInt32(payload, offset);
                offset += 4;
            }

            var data = new MeshData { Vertices = verts, Normals = normals, Indices = indices };
            tcs.TrySetResult(data);
        }

        // ---------------------------------------------------------------
        // Public: request marching cubes for a chunk
        // Returns null if not connected or on error (caller falls back to local)
        // ---------------------------------------------------------------
        public async Task<MeshData> RequestMeshAsync(
            Vector3 chunkOrigin, Vector3 chunkExtents,
            CancellationToken ctkn = default)
        {
            if (!m_connected) return null;

            uint id = unchecked(m_nextRequestId++);
            var tcs = new TaskCompletionSource<MeshData>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            m_pending[id] = tcs;

            try
            {
                var req = new MeshRequestPayload
                {
                    RequestId = id,
                    OriginX   = chunkOrigin.x,  OriginY  = chunkOrigin.y,  OriginZ  = chunkOrigin.z,
                    ExtentsX  = chunkExtents.x, ExtentsY = chunkExtents.y, ExtentsZ = chunkExtents.z
                };

                byte[] buf = new byte[Marshal.SizeOf<MeshRequestPayload>()];
                MemoryMarshal.Write(buf, ref req);

                lock (m_stream)
                    Wire.WriteMsg(new BinaryWriter(m_stream), MessageType.MeshRequest, buf);

                using (ctkn.Register(() => tcs.TrySetCanceled()))
                    return await tcs.Task;
            }
            catch
            {
                m_pending.TryRemove(id, out _);
                return null;
            }
        }

        // ---------------------------------------------------------------
        // Send a depth frame to the PC for TSDF integration
        // Called when EnvironmentMapper fires Updated (5 Hz)
        // ---------------------------------------------------------------
        private bool m_sendingFrame;

        private void OnEnvironmentUpdated()
        {
            if (!m_connected || m_sendingFrame) return;
            m_sendingFrame = true;
            _ = SendDepthFrameAsync();
        }

        private void OnEnvironmentCleared()
        {
            if (!m_connected) return;
            try
            {
                lock (m_stream)
                    Wire.WriteMsg(new BinaryWriter(m_stream), MessageType.Clear);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteClient] SendClear error: {e.Message}");
            }
        }

        private async Task SendDepthFrameAsync()
        {
            try
            {
                DepthKitDriver dkd = DepthKitDriver.Instance;
                if (!DepthKitDriver.DepthAvailable || dkd.DepthReadbackTex == null
                    || dkd.NormTex == null) return;

                // Readback depth (float R32_SFloat) — eye 0 only
                AsyncGPUReadbackRequest depthReq =
                    await AsyncGPUReadback.RequestAsync(dkd.DepthReadbackTex);

                // Readback normals (R8G8B8A8_SNorm Texture2DArray) — mip 0, then get layer 0 (eye 0)
                AsyncGPUReadbackRequest normReq = await AsyncGPUReadback.RequestAsync(dkd.NormTex, 0);

                if (depthReq.hasError || normReq.hasError) return;
                if (!m_connected) return;

                NativeArray<float> depthData = depthReq.GetData<float>();
                NativeArray<byte>  normData  = normReq.GetData<byte>(0); // layer 0 = eye 0

                int w = dkd.DepthReadbackTex.width;
                int h = dkd.DepthReadbackTex.height;

                EnvironmentMapper em = EnvironmentMapper.Instance;

                // Pack normals: SNorm byte → float (divide each by 127)
                float[] normFloats = new float[w * h * 4];
                for (int i = 0; i < w * h * 4; i++)
                    normFloats[i] = (sbyte)normData[i] / 127.0f;

                // Build player positions
                List<Vector3> heads = em.PlayerHeads.ConvertAll(t => t.position);

                // Serialize
                using MemoryStream ms = new();
                using BinaryWriter bw = new(ms);

                // DepthFrameHeader fields
                bw.Write((uint)w);
                bw.Write((uint)h);

                Wire.WriteFloats(bw, Wire.MatrixToFloats(dkd.View[0]));
                Wire.WriteFloats(bw, Wire.MatrixToFloats(dkd.ViewInv[0]));
                Wire.WriteFloats(bw, Wire.MatrixToFloats(dkd.Proj[0]));
                Wire.WriteFloats(bw, Wire.MatrixToFloats(dkd.ProjInv[0]));

                bw.Write((uint)heads.Count);

                // Player positions
                foreach (Vector3 p in heads) { bw.Write(p.x); bw.Write(p.y); bw.Write(p.z); }

                // Depth data
                foreach (float d in depthData) bw.Write(d);

                // Normal data
                foreach (float n in normFloats) bw.Write(n);

                byte[] payload = ms.ToArray();

                lock (m_stream)
                    Wire.WriteMsg(new BinaryWriter(m_stream), MessageType.DepthFrame, payload);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteClient] SendDepthFrame error: {e.Message}");
            }
            finally
            {
                m_sendingFrame = false;
            }
        }

        // ---------------------------------------------------------------
        // Optional: check latency
        // ---------------------------------------------------------------
        public async Task<bool> PingAsync(CancellationToken ctkn = default)
        {
            if (!m_connected) return false;
            try
            {
                // The receive loop handles Pong — here we just send Ping
                // and rely on the protocol being in sync (no TCS for pong for now)
                lock (m_stream)
                    Wire.WriteMsg(new BinaryWriter(m_stream), MessageType.Ping);
                return true;
            }
            catch { return false; }
        }
    }
}
