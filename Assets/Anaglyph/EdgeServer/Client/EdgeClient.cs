using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace Anaglyph.EdgeServer
{
    // ── EdgeClient ────────────────────────────────────────────────────────────────
    // Maintains a single bidirectional TCP connection to EdgeMetalServer.
    //
    // Thread model:
    //   ConnectionLoop  — background thread; reconnects on disconnect
    //   SendLoopInner   — spawned per connection; drains sendQueue
    //   (ConnectionLoop also runs the receive path for each connection)
    //
    // Main-thread events are dispatched via a ConcurrentQueue drained in Update().

    [DefaultExecutionOrder(-20)]
    public class EdgeClient : MonoBehaviour
    {
        [SerializeField] private EdgeServerSettings settings;

        // ── Public API ────────────────────────────────────────────────────────
        public bool IsConnected { get; private set; }

        /// Fired on the main thread when a complete MeshChunk packet is received.
        public event Action<MeshChunkData> OnMeshChunkReceived;

        /// Fired on the main thread when the TCP connection is established.
        public event Action OnConnected;

        /// Fired on the main thread when the TCP connection is lost.
        public event Action OnDisconnected;

        // ── Internals ─────────────────────────────────────────────────────────
        private readonly ConcurrentQueue<byte[]>  sendQueue  = new();
        private readonly ConcurrentQueue<Action>  mainQueue  = new();

        private volatile bool running;
        private TcpClient    activeTcp;   // touched only by ConnectionLoop / SendLoopInner

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public void Connect()
        {
            if (running) return;
            running = true;
            new Thread(ConnectionLoop) { IsBackground = true, Name = "EdgeClient-Connection" }.Start();
            Debug.Log($"[EdgeClient] Connecting to {settings.serverIP}:{settings.port} …");
        }

        public void Disconnect()
        {
            running = false;
            activeTcp?.Close();
            activeTcp = null;
        }

        /// Enqueue payload for sending.  Thread-safe; fire-and-forget.
        public void Send(byte[] payload)
        {
            if (!running || !IsConnected) return;
            sendQueue.Enqueue(payload);
        }

        private void Update()
        {
            while (mainQueue.TryDequeue(out Action action))
                action();
        }

        private void OnDestroy() => Disconnect();

        // ── Connection loop ───────────────────────────────────────────────────
        // Reconnects indefinitely while running == true.

        private void ConnectionLoop()
        {
            while (running)
            {
                TcpClient tcp = null;
                try
                {
                    tcp = new TcpClient();
                    tcp.Connect(settings.serverIP, settings.port);
                    tcp.NoDelay = true;
                    tcp.ReceiveBufferSize = 1024 * 1024;
                    tcp.SendBufferSize    = 1024 * 1024;

                    activeTcp   = tcp;
                    NetworkStream stream = tcp.GetStream();

                    mainQueue.Enqueue(() =>
                    {
                        IsConnected = true;
                        Debug.Log($"[EdgeClient] Connected to {settings.serverIP}:{settings.port}");
                        OnConnected?.Invoke();
                    });

                    // Drain any stale sends from a previous session
                    while (sendQueue.TryDequeue(out _)) { }

                    // Dedicated send thread for this connection
                    Thread sendT = new Thread(() => SendLoopInner(stream))
                    {
                        IsBackground = true,
                        Name         = "EdgeClient-Send"
                    };
                    sendT.Start();

                    // Receive on this thread (blocks until error)
                    ReceiveLoopInner(stream);

                    sendT.Join(500);
                }
                catch (Exception e)
                {
                    if (running)
                        mainQueue.Enqueue(() =>
                            Debug.LogWarning($"[EdgeClient] {e.GetType().Name}: {e.Message}"));
                }
                finally
                {
                    activeTcp = null;
                    tcp?.Close();
                    mainQueue.Enqueue(() =>
                    {
                        if (IsConnected)
                        {
                            IsConnected = false;
                            Debug.Log("[EdgeClient] Disconnected");
                            OnDisconnected?.Invoke();
                        }
                    });
                }

                if (running)
                {
                    mainQueue.Enqueue(() =>
                        Debug.Log($"[EdgeClient] Reconnecting in {settings.reconnectDelay}s …"));
                    Thread.Sleep(TimeSpan.FromSeconds(settings.reconnectDelay));
                }
            }
        }

        // ── Send loop ─────────────────────────────────────────────────────────

        private void SendLoopInner(NetworkStream stream)
        {
            try
            {
                while (running)
                {
                    if (!sendQueue.TryDequeue(out byte[] payload))
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    // 4-byte LE length prefix + payload (matches readPacket in Protocol.swift)
                    byte[] lenBytes = BitConverter.GetBytes(payload.Length);
                    stream.Write(lenBytes, 0, 4);
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();
                }
            }
            catch
            {
                // Stream closed — ConnectionLoop will handle reconnect
            }
        }

        // ── Receive loop ──────────────────────────────────────────────────────

        private void ReceiveLoopInner(NetworkStream stream)
        {
            while (running)
            {
                byte[] raw = ReadFramed(stream);

                if (raw == null || raw.Length == 0) continue;

                if (raw[0] == (byte)PacketType.MeshChunk)
                {
                    if (EdgeProtocol.TryReadMeshChunkPacket(raw, out MeshChunkData chunk))
                    {
                        MeshChunkData captured = chunk;
                        mainQueue.Enqueue(() => OnMeshChunkReceived?.Invoke(captured));
                    }
                    else
                    {
                        mainQueue.Enqueue(() =>
                            Debug.LogWarning($"[EdgeClient] Failed to parse MeshChunk ({raw.Length} bytes)"));
                    }
                }
                else
                {
                    mainQueue.Enqueue(() =>
                        Debug.LogWarning($"[EdgeClient] Unknown packet type 0x{raw[0]:X2}"));
                }
            }
        }

        // ── Framed read ───────────────────────────────────────────────────────

        private static byte[] ReadFramed(NetworkStream stream)
        {
            byte[] lenBuf = new byte[4];
            ReadExact(stream, lenBuf, 4);

            int length = BitConverter.ToInt32(lenBuf, 0);
            if (length <= 0 || length > 64 * 1024 * 1024)
                throw new IOException($"Invalid packet length {length}");

            byte[] payload = new byte[length];
            ReadExact(stream, payload, length);
            return payload;
        }

        private static void ReadExact(NetworkStream stream, byte[] buf, int count)
        {
            int received = 0;
            while (received < count)
            {
                int n = stream.Read(buf, received, count - received);
                if (n <= 0) throw new IOException("Connection closed mid-read");
                received += n;
            }
        }
    }
}
