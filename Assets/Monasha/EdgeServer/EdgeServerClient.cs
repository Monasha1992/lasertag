using System;
using System.Collections;
using UnityEngine;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Unity.Collections;
using Anaglyph;
using Anaglyph.XRTemplate;
using Anaglyph.XRTemplate.DepthKit;
using UnityEngine.Rendering;
// Alias for the static class Anaglyph.Anaglyph.
// The class name collides with its own namespace — without this alias, a bare
// `Anaglyph.DebugMode` resolves to the namespace and fails to find DebugMode.
using AnaglyphCore = Anaglyph.Anaglyph;

// ─────────────────────────────────────────────────────────────────────────────
// EdgeServerClient.cs — The Quest-side client that communicates with the Mac edge server
//
// WHAT THIS FILE DOES:
//   This MonoBehaviour runs on the Quest 3 inside the Unity AR app.
//   It connects to the Mac edge server over Wi-Fi TCP, sends raw depth frames,
//   and receives back a triangle mesh that is applied to a MeshFilter in the scene.
//
// HOW IT FITS IN THE SYSTEM:
//   Quest 3 (this script) ──(raw depth + camera matrices)──► Mac edge server
//   Quest 3 (this script) ◄─────────(triangle mesh)────────── Mac edge server
//
// MESSAGE PROTOCOL:
//   Outgoing (Quest → Mac):
//     [0x01][len3][len2][len1][len0]  ← 5-byte header
//     [8 bytes timestamp ms]          ← uint64 Unix ms (for RTT measurement)
//     [540 bytes frame data]          ← matrices + volume config
//     [width×height×4 bytes]          ← raw float32 depth pixels
//
//   Incoming (Mac → Quest):
//     [0x03][len3][len2][len1][len0]  ← 5-byte header
//     [8 bytes timestamp echo]        ← same timestamp we sent (RTT = now - echo)
//     [4 bytes vertCount]
//     [4 bytes idxCount]
//     [vertCount×24 bytes vertices]   ← pos.xyz + normal.xyz per vertex
//     [idxCount×4 bytes indices]      ← uint32 triangle indices
//
// FLOW CONTROL:
//   waitingForMesh  — true once a frame is sent; blocks sending until Mac responds
//   readbackPending — true while AsyncGPUReadback is in progress
//   minSendInterval — minimum seconds between sends (prevents flooding the Mac)
//
// THREADING MODEL:
//   Unity's render thread is the "main thread" and must not block — any stall
//   longer than one frame (~13 ms at 72 Hz) causes VR nausea. We push every
//   piece of work that can be moved off it:
//
//     Main thread (Update, callbacks):
//       - Drain incoming payload queue, upload meshes to GPU
//       - Poll async PhysX bake completion, swap sharedMesh on finish
//       - Receive AsyncGPUReadback callbacks (depth texture → managed buffer)
//       - Compose outbound payload (memcpy only, no I/O)
//
//     Background reader thread ('EdgeServerReader'):
//       - Blocking stream.Read of the 5-byte header + payload
//       - Posts completed payloads onto a ConcurrentQueue for the main thread
//
//     Task.Run workers:
//       - stream.Write of the outbound payload (Wi-Fi TX can block 5–15 ms)
//       - Physics.BakeMesh BVH cook (10–50 ms for room-scale non-convex meshes)
//
//   NetworkStream is documented as safe for one reader thread + one writer
//   thread concurrently; that's what we have. Reads happen on the reader
//   thread, writes happen on Task.Run workers.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.EdgeServer
{
    public class EdgeServerClient : MonoBehaviour
    {
        // ── Inspector-configurable settings ──────────────────────────────────
        // Each field below exposes a Tooltip so hovering in the Unity Inspector
        // shows the same guidance that's in the code comments.
        [Header("Mac server endpoint")]
        [Tooltip("IP address of the Mac running EdgeMetalServer on the local Wi-Fi. " +
                 "Use 127.0.0.1 only when running the Mac app on the same device as " +
                 "the Unity Editor (Link); otherwise set to the Mac's LAN IP.")]
        [SerializeField] private string serverIP       = "127.0.0.1";

        [Tooltip("TCP port the Mac server listens on. Must match EdgeMetalServer's port (9876).")]
        [SerializeField] private int    serverPort     = 9876;

        [Header("Send pacing")]
        [Tooltip("Minimum seconds between outgoing depth frames. " +
                 "Lower = more frequent mesh updates (smoother reconstruction) but more Wi-Fi load. " +
                 "Higher = cheaper but the mesh lags reality more. " +
                 "10 Hz (0.1s) is a good default for a room-scale scene.")]
        [SerializeField] private float  minSendInterval = 0.1f;

        [Header("Stale-mesh discard")]
        [Tooltip("Metres. If the head moved farther than this between send and receive, the " +
                 "incoming mesh is considered stale and discarded — the room geometry would be " +
                 "mis-registered relative to current head pose otherwise.")]
        [SerializeField] private float  posDiscardThreshold = 0.15f;

        [Tooltip("Degrees. If the head rotated more than this between send and receive, the " +
                 "incoming mesh is considered stale and discarded. Kept loose (15°) because the " +
                 "mesh is world-space — small rotations don't de-register it.")]
        [SerializeField] private float  rotDiscardThreshold = 15f;

        [Header("Physics collider")]
        [Tooltip("How many meshes arrive between MeshCollider BVH rebakes. Higher = less physics " +
                 "cost but collision geometry lags visual geometry further behind. At 10 Hz send rate " +
                 "with interval=3, the collider updates ~3× per second. The bake itself runs on a " +
                 "background thread so the main thread is unaffected either way.")]
        [SerializeField] private int    colliderUpdateInterval = 3;

        [Header("Connection robustness")]
        [Tooltip("If true, the client retries the TCP connection every reconnectIntervalSec " +
                 "until it succeeds. Lets you start the Quest scene before the Mac server is ready.")]
        [SerializeField] private bool  autoReconnect        = true;

        [Tooltip("Seconds between reconnect attempts when autoReconnect is on.")]
        [SerializeField] private float reconnectIntervalSec = 2f;

        [Header("Diagnostics")]
        [Tooltip("If true, prints per-mesh arrival/send logs. Disable for study builds — " +
                 "Debug.Log on Quest IL2CPP has non-trivial cost that shows up in main-thread " +
                 "profiles when firing at 10 Hz.")]
        [SerializeField] private bool verboseLogging = false;

        [Header("Connection error UI")]
        [Tooltip("Optional GameObject (typically a world-space Canvas with a 'Server not connected' " +
                 "label) that is activated whenever the edge server is unreachable and deactivated " +
                 "once a connection is established. The on-device meshing path is intentionally NOT " +
                 "used as a fallback — if the Mac server isn't running, the user sees this banner. " +
                 "Leave null to rely on console logs only.")]
        [SerializeField] private GameObject disconnectedErrorUI;

        // ── Public state for gameplay / UI coordination ──────────────────────
        // Other scripts (e.g. a "waiting for mesh" UI, or a script that disables
        // firing until the room is reconstructed) can subscribe to FirstMeshReceived
        // or poll IsMeshReady.
        public static event Action FirstMeshReceived;
        // Fires whenever the connection state changes (true = connected, false = disconnected).
        // Subscribe from UI / gameplay code to react without polling IsConnected each frame.
        public static event Action<bool> ConnectionStatusChanged;
        public static bool  IsConnected { get; private set; }
        public static bool  IsMeshReady { get; private set; }   // At least one mesh has been applied
        public static long  LastRttMs   { get; private set; }
        public static int   LastVertexCount   { get; private set; }
        public static int   LastTriangleCount { get; private set; }
        public static float LastMeshAgeSec    { get; private set; } // Seconds since the last mesh was applied

        // ── TCP connection ────────────────────────────────────────────────────
        private TcpClient     client;
        private NetworkStream stream;

        // True while an AsyncGPUReadback is in progress — blocks sending a new frame
        private bool readbackPending;

        // Time of the last successful depth send (UnityEngine time, seconds)
        private float lastSendTime = -999f;

        // ── Depth copy compute shader ─────────────────────────────────────────
        // Converts the Quest's GPU depth texture to a CPU-readable R32Float format
        // for AsyncGPUReadback. The asset ships in the same folder as this script.
        [Header("Compute shaders")]
        [Tooltip("Compute shader with a 'CopyDepth' kernel that reads the Quest's " +
                 "depth texture and writes it to a CPU-readable R32Float render texture. " +
                 "Required — the client won't send depth frames without it.")]
        [SerializeField] private ComputeShader depthCopyCompute;
        private ComputeKernel  depthCopyKernel;
        private RenderTexture  depthCopy;

        // ── Incoming TCP: dedicated reader thread ─────────────────────────────
        // A dedicated background thread does blocking stream.Read calls so the
        // Unity main thread never spends time on network I/O or TCP reassembly.
        // Complete payloads are posted onto `receivedPayloads`; Update() drains
        // them at frame rate. NetworkStream is documented as safe for concurrent
        // read on one thread + write on another (we write from Task.Run).
        private Thread readerThread;
        private volatile bool readerShouldStop;
        // Flipped to true by the reader thread when stream.Read returns 0 / throws.
        // Update() polls this and treats it as "connection lost" — re-shows the
        // error banner, logs an error, and restarts the reconnect loop.
        private volatile bool readerExited;

        // Queue of completed payloads ready to be applied on the main thread.
        // Each item's buffer comes from `bufferPool` and must be returned there
        // after processing so we don't allocate per-mesh.
        private struct ReceivedPayload { public byte type; public byte[] buffer; public int length; }
        private readonly ConcurrentQueue<ReceivedPayload> receivedPayloads = new ConcurrentQueue<ReceivedPayload>();

        // Thread-safe pool of reusable receive buffers. Reader rents on demand,
        // main thread returns after applying. Grow-only: small buffers are
        // discarded when a bigger one is needed.
        private readonly ConcurrentQueue<byte[]> bufferPool = new ConcurrentQueue<byte[]>();

        // ── Legacy voxel path (unused in current pipeline — kept for reference) ──
        // Earlier prototype had the Mac send raw voxels back (one per changed cell)
        // and the Quest wrote them into a local TSDF volume. Superseded by the
        // mesh pipeline below, which transfers much less data per frame. The
        // voxel shader reference is still here so switching back is a one-line
        // change if we ever need to A/B test the two transports.
        [Header("Legacy voxel path (unused)")]
        [Tooltip("Legacy compute shader for writing received voxels into a TSDF volume. " +
                 "Kept for reference — the current pipeline uses the mesh path instead " +
                 "and this slot can be left empty.")]
        [SerializeField] private ComputeShader voxelWriterCompute;
        private ComputeKernel  writeVoxelsKernel;
        private ComputeBuffer  voxelBuffer;

        // ── Mesh output ───────────────────────────────────────────────────────
        [Header("Mesh output")]
        [Tooltip("MeshFilter the received triangle mesh is written to. Typically the " +
                 "MeshFilter on the 'EdgeMesh' GameObject in the scene.")]
        [SerializeField] private MeshFilter   meshFilter;

        [Tooltip("MeshCollider that gets a baked copy of the received mesh for bullet " +
                 "collision. Updated every 'Collider Update Interval' meshes. Convex must " +
                 "be UNCHECKED on the collider — the room geometry is non-convex.")]
        [SerializeField] private MeshCollider meshCollider;

        [Tooltip("Optional: the visual Renderer for the edge mesh. If assigned, " +
                 "its visibility is driven by the Anaglyph.DebugMode toggle in the menu. " +
                 "Leave null to keep the mesh always visible. If left null and a " +
                 "MeshRenderer exists on the same GameObject as the MeshFilter, it " +
                 "will be auto-discovered in Start().")]
        [SerializeField] private Renderer     meshRenderer;
        private Mesh receivedMesh;
        private int  meshesReceived = 0; // Counter used to throttle MeshCollider updates

        // ── Mesh buffer size tracking ─────────────────────────────────────────
        // SetVertexBufferParams / SetIndexBufferParams reallocate the GPU buffer
        // even when the size is unchanged. Tracking the last allocated sizes
        // lets us skip the reconfigure when vert/index counts match the previous
        // mesh — the common case when the room geometry is stable.
        private int receivedMeshLastVertCount = -1;
        private int receivedMeshLastIdxCount  = -1;
        private int bakeMeshLastVertCount     = -1;
        private int bakeMeshLastIdxCount      = -1;

        // ── Async PhysX cook state ────────────────────────────────────────────
        // Baking a non-convex MeshCollider is 10–50ms of work. Doing it on the
        // main thread causes a frame drop / nausea in VR. Instead we:
        //   1. Call Physics.BakeMesh on a background Task (thread-safe in Unity)
        //   2. Poll the Task in Update
        //   3. Assign sharedMesh only once the BVH is ready (near-free)
        //
        // A separate "bake mesh" holds a snapshot of the mesh the background
        // task is cooking — needed because the live `receivedMesh` keeps
        // changing, and BakeMesh takes a mesh instance ID.
        private Mesh bakeMesh;                        // Snapshot mesh being baked
        private Task bakeTask;                        // Running bake task, null if idle
        private bool bakePending;                     // True while bakeTask is running
        private int  lastBakedMeshesReceived = -1;    // Counter of last scheduled bake

        // ── Fixed render bounds ───────────────────────────────────────────────
        // Setting large fixed bounds lets us skip RecalculateBounds() every mesh.
        // Centred at origin, 100m cube — big enough for any room.
        private static readonly Bounds FixedBounds = new Bounds(Vector3.zero, new Vector3(100f, 100f, 100f));

        // ── Vertex layout matching the server's output ────────────────────────
        // Server sends 24 B per vertex: pos.xyz (float3) + normal.xyz (float3).
        // Declaring this layout lets Unity upload raw bytes straight to the GPU.
        private static readonly VertexAttributeDescriptor[] VertexLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal,   VertexAttributeFormat.Float32, 3),
        };

        // Flags that skip every piece of per-upload validation Unity does by default.
        // Safe here because the server already produces clean, consistent meshes.
        private const MeshUpdateFlags FastMeshFlags =
            MeshUpdateFlags.DontValidateIndices   |
            MeshUpdateFlags.DontResetBoneBounds   |
            MeshUpdateFlags.DontNotifyMeshUsers   |
            MeshUpdateFlags.DontRecalculateBounds;

        // ── PhysX cooking options ─────────────────────────────────────────────
        // UseFastMidphase is the critical one for raycast cost against non-convex
        // meshes — PhysX builds a faster mid-phase BVH structure. For a room-scale
        // mesh with ~10k triangles and many bullets linecasting per frame, this
        // can drop per-raycast cost 2–5× versus the default cooking profile.
        //
        // Other flags:
        //   CookForFasterSimulation — tuned for runtime queries, not editor-cook time
        //   EnableMeshCleaning      — drops degenerate triangles (cheap, safer cooks)
        //   WeldColocatedVertices   — merges duplicate positions (surface-nets output
        //                             already unique, but harmless)
        private const MeshColliderCookingOptions FastCookingOptions =
            MeshColliderCookingOptions.CookForFasterSimulation |
            MeshColliderCookingOptions.EnableMeshCleaning       |
            MeshColliderCookingOptions.WeldColocatedVertices    |
            MeshColliderCookingOptions.UseFastMidphase;

        // ── One-in-flight frame control ───────────────────────────────────────
        private bool waitingForMesh;

        // ── Pose recording for stale-mesh discard ─────────────────────────────
        // Recorded at send time; compared to current pose on mesh arrival
        private Vector3    sentHeadPos;
        private Quaternion sentHeadRot;

        // Cached main camera transform — Camera.main is a FindObjectWithTag call
        // under the hood and was being hit on every mesh arrival + every send.
        // Resolve once at Start() and reuse.
        private Transform headTransform;

        // ── Send-side buffer pools ────────────────────────────────────────────
        // Depth readback and TCP serialization used to allocate ~360KB per send.
        // At 10Hz that's 3.6 MB/sec of GC churn. We pool both the intermediate
        // depth copy and the final payload buffer, grow-only — a single large
        // send early in the session sets the buffer sizes permanently.
        private byte[] depthBytesBuffer;     // Managed copy of the readback NativeArray
        private byte[] sendPayloadBuffer;    // Final header + payload bytes written to TCP

        // Async-send lock: ensures only one background send is in flight at a
        // time (already enforced by waitingForMesh, but makes the invariant
        // explicit in the write path).
        private readonly object sendLock = new object();

        // ── Metrics tracking ──────────────────────────────────────────────────
        // Byte counts are stored when we send, then logged when the response arrives
        private int lastPayloadBytesSent;
        private int lastResponseBytesRecv;
        private float lastMeshAppliedTime = -1f; // realtimeSinceStartup of last successful apply

        // ─────────────────────────────────────────────────────────────────────
        // Start — Initialises compute kernels and connects to the Mac server
        // ─────────────────────────────────────────────────────────────────────
        private void Start()
        {
            depthCopyKernel   = new ComputeKernel(depthCopyCompute, "CopyDepth");
            writeVoxelsKernel = new ComputeKernel(voxelWriterCompute, "WriteVoxels");

            // Cache the main camera transform. Camera.main is a tag lookup that
            // we were hitting every mesh arrival + every send — easily 10–20×
            // per second when head is moving.
            var cam = Camera.main;
            if (cam != null) headTransform = cam.transform;

            // ── Hook the Anaglyph debug toggle for mesh visibility ────────────
            // Auto-discover a Renderer on the meshFilter GameObject if the
            // inspector slot is empty — saves the user from having to wire
            // both MeshFilter and MeshRenderer manually.
            if (meshRenderer == null && meshFilter != null)
                meshRenderer = meshFilter.GetComponent<Renderer>();

            if (meshRenderer != null)
            {
                // Initial visibility mirrors the current debug-mode state.
                meshRenderer.enabled = AnaglyphCore.DebugMode;
                // Live updates when the user toggles the debug button in the UI.
                AnaglyphCore.DebugModeChanged += OnDebugModeChanged;
            }

            // Match the MeshCollider's cooking options to the ones we'll bake with.
            // If the runtime options differ from the bake options, Unity invalidates
            // the cached cook and re-cooks on the main thread when sharedMesh is
            // assigned — which is exactly the hitch we're trying to avoid.
            if (meshCollider != null)
                meshCollider.cookingOptions = FastCookingOptions;

            // ── Hard-disable the Quest's on-device TSDF integration ───────────
            // Set BEFORE the first connection attempt so the local mapper is
            // never running — even if the Mac server is unreachable. The user
            // explicitly chose edge-only operation; falling back to local
            // meshing on disconnect would silently give a degraded experience.
            // Instead, we surface the disconnect via the error banner below.
            EnvironmentMapper.UseEdgeServer = true;

            // Show the "not connected" banner immediately. ConnectLoop will
            // hide it once TryConnect succeeds.
            SetConnectedState(false);

            // Subscribe to the depth sensor — fires every time a new depth frame
            // arrives. We subscribe even before connecting; OnDepthUpdated is
            // guarded on `stream != null` so pre-connect frames are dropped.
            DepthKitDriver.Instance.Updated += OnDepthUpdated;

            var mapper = EnvironmentMapper.Instance;
            if (mapper != null && verboseLogging)
                Debug.Log($"Volume: {mapper.VoxelCount}, voxelSize={mapper.VoxelSize}, voxelDist={mapper.VoxelDistance}");

            // Kick off the connection coroutine — retries if the Mac isn't up
            // when the Quest scene starts.
            StartCoroutine(ConnectLoop());
        }

        // ─────────────────────────────────────────────────────────────────────
        // SetConnectedState — single place that mutates IsConnected so the UI
        // banner, public event, and console log stay in sync.
        //
        //   connected = true  → hide error banner, fire ConnectionStatusChanged(true)
        //   connected = false → show error banner, fire ConnectionStatusChanged(false)
        //
        // Called from TryConnect on success, from Update() when the reader
        // thread reports a disconnect, and from Start() to set the initial
        // "not yet connected" state.
        // ─────────────────────────────────────────────────────────────────────
        private void SetConnectedState(bool connected)
        {
            // Toggle the error UI even if the boolean state didn't change —
            // ensures the banner reflects reality after a scene reload.
            if (disconnectedErrorUI != null)
                disconnectedErrorUI.SetActive(!connected);

            if (IsConnected == connected) return;
            IsConnected = connected;

            try { ConnectionStatusChanged?.Invoke(connected); }
            catch (Exception e) { Debug.LogError($"[EdgeServerClient] ConnectionStatusChanged handler threw: {e}"); }
        }

        // ─────────────────────────────────────────────────────────────────────
        // ConnectLoop — Retries the TCP connection until it succeeds
        //
        // On first failure (Mac not yet running) we keep retrying every
        // `reconnectIntervalSec` so the Quest scene doesn't require the Mac
        // to be up first. Also re-entered from Update() when the reader thread
        // reports a mid-session disconnect. Exits on success or when
        // autoReconnect is disabled — but the disconnected error banner stays
        // visible so the user knows the system isn't running.
        // ─────────────────────────────────────────────────────────────────────
        private IEnumerator ConnectLoop()
        {
            while (!IsConnected)
            {
                bool ok = TryConnect();
                if (ok) yield break;
                if (!autoReconnect) yield break;
                yield return new WaitForSeconds(reconnectIntervalSec);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // OnDebugModeChanged — Toggles edge-mesh visibility with the debug UI
        //
        // Subscribed in Start() once a meshRenderer is discovered. Unsubscribed
        // in OnDestroy() to prevent leaked handlers surviving scene reloads.
        // ─────────────────────────────────────────────────────────────────────
        private void OnDebugModeChanged(bool on)
        {
            if (meshRenderer != null)
                meshRenderer.enabled = on;
        }

        // Attempts a single TCP connection. Returns true on success and starts
        // the reader thread. Returns false (and logs an error) on failure —
        // the on-device meshing path is intentionally NOT activated as a
        // fallback, so the user sees the disconnected banner instead.
        private bool TryConnect()
        {
            try
            {
                client = new TcpClient(serverIP, serverPort);
                stream = client.GetStream();

                // Reset the reader-exit flag from any previous disconnect.
                readerExited     = false;
                readerShouldStop = false;

                // Spin up the background reader.
                readerThread = new Thread(ReaderLoop)
                {
                    IsBackground = true,
                    Name         = "EdgeServerReader",
                };
                readerThread.Start();

                SetConnectedState(true);
                Debug.Log($"[EdgeServerClient] Connected to {serverIP}:{serverPort}");
                return true;
            }
            catch (Exception e)
            {
                // LogError (not LogWarning) so the failure is visible without
                // verboseLogging. We don't fall back to on-device meshing —
                // the user must start the Mac server.
                Debug.LogError($"[EdgeServerClient] Cannot reach edge server at {serverIP}:{serverPort} — " +
                               $"start EdgeMetalServer on the Mac. Auto-retry in {reconnectIntervalSec}s. " +
                               $"({e.Message})");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Update — main-thread work only:
        //   1. Poll the async PhysX bake and swap the collider when ready
        //   2. Drain any complete payloads from the reader thread
        //
        // All network I/O (reads, reassembly, header parsing) happens on a
        // dedicated background thread — zero TCP work on this thread.
        // ─────────────────────────────────────────────────────────────────────
        private void Update()
        {
            // Update the public mesh-age metric so HUDs / UI can show it live.
            if (lastMeshAppliedTime > 0f)
                LastMeshAgeSec = Time.realtimeSinceStartup - lastMeshAppliedTime;

            // ── Detect a mid-session disconnect ───────────────────────────────
            // The reader thread sets `readerExited` when stream.Read returns 0
            // (peer closed) or throws. We surface that on the main thread:
            // log an error, show the banner, tear down the dead socket, and
            // restart the reconnect loop if autoReconnect is on.
            if (readerExited)
            {
                readerExited = false;
                Debug.LogError("[EdgeServerClient] Lost connection to edge server — restart EdgeMetalServer on the Mac.");
                SetConnectedState(false);

                try { stream?.Close(); } catch { /* already closed */ }
                try { client?.Close(); } catch { /* already closed */ }
                stream = null;
                client = null;

                // Reset in-flight gating so we don't get stuck waiting for a
                // mesh that will never arrive.
                waitingForMesh = false;

                if (autoReconnect)
                    StartCoroutine(ConnectLoop());
            }

            // ── Poll the async MeshCollider bake ──────────────────────────────
            // If a bake finished on the worker thread, assign sharedMesh here
            // (cheap, because the BVH is already cooked).
            if (bakePending && bakeTask != null && bakeTask.IsCompleted)
            {
                bakePending = false;
                if (bakeTask.IsFaulted)
                {
                    Debug.LogWarning($"[EdgeServerClient] BakeMesh failed: {bakeTask.Exception?.GetBaseException().Message}");
                }
                else if (meshCollider != null && bakeMesh != null)
                {
                    // Force a re-assign so PhysX picks up the newly-baked BVH.
                    meshCollider.sharedMesh = null;
                    meshCollider.sharedMesh = bakeMesh;
                }
                bakeTask = null;
            }

            // ── Drain queued payloads from the reader thread ──────────────────
            // Apply each one on the main thread, then return its buffer to the
            // pool so the reader can reuse it.
            while (receivedPayloads.TryDequeue(out var item))
            {
                try
                {
                    if (item.type == 0x03)      ApplyMeshData(item.buffer);
                    else if (item.type == 0x02) ApplyVoxelData(item.buffer);
                }
                finally
                {
                    bufferPool.Enqueue(item.buffer);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // ReaderLoop — runs on a dedicated background thread
        //
        // Blocking-reads the TCP stream into pooled buffers, parses the 5-byte
        // framing header, and posts complete payloads onto `receivedPayloads`
        // for the main thread to apply.
        //
        // Thread-safety: reads stream.Read on this thread while Update() only
        // touches `receivedPayloads` / `bufferPool` via ConcurrentQueue, and
        // writes happen via Task.Run. NetworkStream explicitly supports one
        // reader thread + one writer thread concurrently.
        // ─────────────────────────────────────────────────────────────────────
        private void ReaderLoop()
        {
            var localStream = stream;
            var headerBuf   = new byte[5];

            try
            {
                while (!readerShouldStop)
                {
                    // ── Read the 5-byte framing header ───────────────────────
                    if (!ReadExact(localStream, headerBuf, 0, 5)) return;
                    byte type = headerBuf[0];
                    int  len  = (headerBuf[1] << 24) | (headerBuf[2] << 16) |
                                (headerBuf[3] << 8)  |  headerBuf[4];

                    // ── Rent a buffer from the pool ──────────────────────────
                    byte[] payloadBuf = RentBuffer(len);

                    // ── Read the payload ─────────────────────────────────────
                    if (!ReadExact(localStream, payloadBuf, 0, len))
                    {
                        bufferPool.Enqueue(payloadBuf);
                        return;
                    }

                    // ── Hand off to main thread ──────────────────────────────
                    receivedPayloads.Enqueue(new ReceivedPayload
                    {
                        type   = type,
                        buffer = payloadBuf,
                        length = len,
                    });
                }
            }
            catch (Exception e)
            {
                if (!readerShouldStop)
                    Debug.LogWarning($"[EdgeServerClient] Reader thread exiting: {e.Message}");
            }
            finally
            {
                // Notify the main thread that the connection is gone. Update()
                // polls this flag (we can't touch UI/coroutines from here).
                // Skipped during clean shutdown so OnDestroy doesn't bounce.
                if (!readerShouldStop)
                    readerExited = true;
            }
        }

        // Reads `count` bytes into `buf[offset..offset+count]`. Returns false
        // on disconnect or if the reader was asked to stop mid-read.
        private bool ReadExact(NetworkStream s, byte[] buf, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                if (readerShouldStop) return false;
                int n = s.Read(buf, offset + read, count - read);
                if (n <= 0) return false; // peer closed
                read += n;
            }
            return true;
        }

        // Rent a buffer of at least `size` bytes from the pool. Allocates a new
        // power-of-two sized buffer if the pool has none large enough. Small
        // pooled buffers are discarded (GC'd) on the way out when a bigger one
        // is needed — so after a couple of large payloads, the pool settles at
        // max size and never allocates again.
        private byte[] RentBuffer(int size)
        {
            while (bufferPool.TryDequeue(out var b))
            {
                if (b.Length >= size) return b;
                // Too small, drop it (falls out of scope → GC)
            }
            return new byte[Mathf.NextPowerOfTwo(size)];
        }

        // ─────────────────────────────────────────────────────────────────────
        // VoxelData — (Legacy) Raw voxel coordinate + TSDF value
        // Used by ApplyVoxelData. Not used in the current mesh pipeline.
        // ─────────────────────────────────────────────────────────────────────
        private struct VoxelData
        {
            public uint  coordX, coordY, coordZ;
            public float value;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ApplyVoxelData — (Legacy) Writes raw voxels into the Quest's TSDF volume
        //
        // Replaced by the mesh pipeline (ApplyMeshData). Kept for reference.
        // ─────────────────────────────────────────────────────────────────────
        private void ApplyVoxelData(byte[] data)
        {
            int offset     = 0;
            int voxelCount = (int)BitConverter.ToUInt32(data, offset);
            offset += 4;

            if (voxelCount == 0) return;

            var voxels = new VoxelData[voxelCount];
            for (int i = 0; i < voxelCount; i++)
            {
                voxels[i].coordX = BitConverter.ToUInt32(data, offset); offset += 4;
                voxels[i].coordY = BitConverter.ToUInt32(data, offset); offset += 4;
                voxels[i].coordZ = BitConverter.ToUInt32(data, offset); offset += 4;
                voxels[i].value  = BitConverter.ToSingle(data, offset); offset += 4;
            }

            if (voxelBuffer == null || voxelBuffer.count < voxelCount)
            {
                voxelBuffer?.Release();
                voxelBuffer = new ComputeBuffer(voxelCount, 16);
            }
            voxelBuffer.SetData(voxels);

            var volume = EnvironmentMapper.Instance.Volume;
            voxelWriterCompute.SetInt("voxelCount", voxelCount);
            writeVoxelsKernel.Set("volume", volume);
            writeVoxelsKernel.Set("voxels", voxelBuffer);
            writeVoxelsKernel.DispatchFit(voxelCount, 1, 1);

            Debug.Log($"[EdgeServerClient] Wrote {voxelCount} voxels");
        }

        // ─────────────────────────────────────────────────────────────────────
        // ApplyMeshData — Parses the received mesh and applies it to the scene
        //
        // PAYLOAD LAYOUT:
        //   Bytes 0–7   : uint64 timestamp echo → used to compute RTT
        //   Bytes 8–11  : uint32 vertCount
        //   Bytes 12–15 : uint32 idxCount
        //   Then: vertCount × 24 bytes (pos.xyz + normal.xyz per vertex)
        //   Then: idxCount  × 4  bytes (uint32 triangle indices)
        //
        // POSE DISCARD:
        //   If the head moved >posDiscardThreshold metres or >rotDiscardThreshold degrees
        //   since we sent the frame, the mesh is too stale and is discarded.
        //
        // MESH COLLIDER:
        //   Updated every `colliderUpdateInterval` meshes to avoid rebuilding the
        //   physics BVH every frame (which is CPU-heavy). Bullets can then hit the
        //   mesh via Physics.Raycast against the MeshCollider.
        // ─────────────────────────────────────────────────────────────────────
        private void ApplyMeshData(byte[] data)
        {
            waitingForMesh = false;

            int offset = 0;

            // ── Read timestamp echo and compute RTT ───────────────────────────
            long sentTimestampMs = (long)BitConverter.ToUInt64(data, offset);
            offset += 8;
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long rttMs = nowMs - sentTimestampMs;
            lastResponseBytesRecv = data.Length + 5; // payload + 5-byte header

            // ── Pose discard check ────────────────────────────────────────────
            // Use cached headTransform to avoid Camera.main's tag lookup cost.
            Vector3    nowPos = headTransform != null ? headTransform.position : Vector3.zero;
            Quaternion nowRot = headTransform != null ? headTransform.rotation : Quaternion.identity;
            float posDelta = Vector3.Distance(nowPos, sentHeadPos);
            float rotDelta = Quaternion.Angle(nowRot, sentHeadRot);
            if (posDelta > posDiscardThreshold || rotDelta > rotDiscardThreshold)
            {
                if (verboseLogging)
                    Debug.Log($"[EdgeServerClient] Mesh discarded — pos drift={posDelta:F3}m rot drift={rotDelta:F1}°");
                // Log the discard event — vertex/tri count are 0 since no mesh was applied
                MetricsLogger.Instance?.LogFrame(rttMs, lastPayloadBytesSent, lastResponseBytesRecv,
                    0, 0, posDelta, rotDelta, discarded: true);
                return;
            }

            // ── Parse vertex and index counts ─────────────────────────────────
            int vertCount = (int)BitConverter.ToUInt32(data, offset); offset += 4;
            int idxCount  = (int)BitConverter.ToUInt32(data, offset); offset += 4;

            if (vertCount == 0) return;

            int vertexBytes = vertCount * 24;          // 24 B per vertex (pos.xyz + normal.xyz)
            int indexBytes  = idxCount  * 4;           // 4 B per uint32 index
            int vertexStart = offset;                  // byte offset of first vertex in `data`
            int indexStart  = offset + vertexBytes;    // byte offset of first index in `data`

            // ── Apply to Unity Mesh (zero-parse path) ─────────────────────────
            // We declare the vertex layout once and push the raw server bytes
            // directly into the GPU buffer. No Vector3[], no loop, no GC.
            if (receivedMesh == null)
            {
                receivedMesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                receivedMesh.MarkDynamic();   // Hints Unity we'll rewrite this mesh often
            }

            // (Re)allocate GPU buffers only when the size actually changed.
            // SetVertexBufferParams / SetIndexBufferParams reallocate the GPU
            // buffer unconditionally, which is measurably expensive — skipping
            // it on same-size frames means most updates become pure uploads.
            if (receivedMeshLastVertCount != vertCount)
            {
                receivedMesh.SetVertexBufferParams(vertCount, VertexLayout);
                receivedMeshLastVertCount = vertCount;
            }
            if (receivedMeshLastIdxCount != idxCount)
            {
                receivedMesh.SetIndexBufferParams(idxCount, IndexFormat.UInt32);
                receivedMeshLastIdxCount = idxCount;
            }

            // Upload raw bytes straight from `data` into the GPU vertex buffer.
            // `data` is byte[], stride is 24 B which matches the declared layout.
            receivedMesh.SetVertexBufferData(data, vertexStart, 0, vertexBytes, 0, FastMeshFlags);
            receivedMesh.SetIndexBufferData (data, indexStart,  0, indexBytes,     FastMeshFlags);

            // Describe the single sub-mesh — one triangle list covering all indices.
            receivedMesh.subMeshCount = 1;
            receivedMesh.SetSubMesh(0, new SubMeshDescriptor(0, idxCount, MeshTopology.Triangles), FastMeshFlags);

            // Fixed bounds — skips RecalculateBounds() which would otherwise scan every vertex.
            receivedMesh.bounds = FixedBounds;

            // Update the visual mesh renderer (cheap — just a reference swap)
            if (meshFilter != null)
                meshFilter.sharedMesh = receivedMesh;

            // ── Update MeshCollider for bullet collision (async cook) ──────────
            // PhysX mesh cook (BakeMesh) is the single biggest frame spike in this
            // path. We run it on a background task so the main thread stays at 72Hz.
            // The cooked BVH is then assigned in Update() when the task completes.
            meshesReceived++;
            if (meshCollider != null && !bakePending && meshesReceived - lastBakedMeshesReceived >= colliderUpdateInterval)
            {
                // Snapshot the current mesh data into a dedicated "bake mesh".
                // We can't reuse `receivedMesh` because it'll keep mutating while
                // the worker thread is cooking — PhysX cooks from the mesh's data
                // at the moment BakeMesh() runs, so we need stable contents.
                if (bakeMesh == null)
                {
                    bakeMesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                    bakeMesh.MarkDynamic();
                }
                // Same size-changed skip as above — avoids reallocating the
                // GPU buffer on the bakeMesh when the mesh shape is stable.
                if (bakeMeshLastVertCount != vertCount)
                {
                    bakeMesh.SetVertexBufferParams(vertCount, VertexLayout);
                    bakeMeshLastVertCount = vertCount;
                }
                if (bakeMeshLastIdxCount != idxCount)
                {
                    bakeMesh.SetIndexBufferParams(idxCount, IndexFormat.UInt32);
                    bakeMeshLastIdxCount = idxCount;
                }
                bakeMesh.SetVertexBufferData(data, vertexStart, 0, vertexBytes, 0, FastMeshFlags);
                bakeMesh.SetIndexBufferData (data, indexStart,  0, indexBytes,     FastMeshFlags);
                bakeMesh.subMeshCount = 1;
                bakeMesh.SetSubMesh(0, new SubMeshDescriptor(0, idxCount, MeshTopology.Triangles), FastMeshFlags);
                bakeMesh.bounds = FixedBounds;

                int bakeId = bakeMesh.GetInstanceID();
                bakePending = true;
                lastBakedMeshesReceived = meshesReceived;

                // Physics.BakeMesh is thread-safe and the documented way to move
                // collider cooking off the main thread. Only the final
                // `sharedMesh = ...` assignment must run on the main thread
                // (handled in Update()).
                //
                // The cooking options MUST match meshCollider.cookingOptions set
                // in Start(). Mismatched options invalidate the cache and force
                // a main-thread re-cook on assignment — the exact hitch we're
                // trying to avoid.
                bakeTask = Task.Run(() => Physics.BakeMesh(bakeId, /* convex: */ false, FastCookingOptions));
            }

            // ── Public state update + first-mesh event ────────────────────────
            LastRttMs            = rttMs;
            LastVertexCount      = vertCount;
            LastTriangleCount    = idxCount / 3;
            lastMeshAppliedTime  = Time.realtimeSinceStartup;

            bool wasFirstMesh = !IsMeshReady;
            IsMeshReady = true;
            if (wasFirstMesh)
            {
                // Announce first mesh arrival so UI/gameplay can enable things
                // that depend on the reconstruction being available.
                try { FirstMeshReceived?.Invoke(); }
                catch (Exception e) { Debug.LogError($"[EdgeServerClient] FirstMeshReceived handler threw: {e}"); }
            }

            // Log successful mesh application
            MetricsLogger.Instance?.LogFrame(rttMs, lastPayloadBytesSent, lastResponseBytesRecv,
                vertCount, idxCount / 3, posDelta, rotDelta, discarded: false);

            if (verboseLogging)
                Debug.Log($"[EdgeServerClient] Mesh applied: {vertCount}v {idxCount / 3}t | RTT={rttMs}ms drift={posDelta:F3}m/{rotDelta:F1}°");
        }

        // ─────────────────────────────────────────────────────────────────────
        // OnDepthUpdated — Called by DepthKitDriver when a new depth frame is ready
        //
        // PACING:
        //   - Skip if minSendInterval hasn't elapsed since the last send
        //   - Skip if a GPU readback is already pending
        //   - Skip if we're still waiting for the last mesh to arrive
        //
        // This function copies the depth texture to CPU memory asynchronously,
        // then sends the frame data to the Mac in the readback callback.
        // ─────────────────────────────────────────────────────────────────────
        private void OnDepthUpdated()
        {
            if (stream == null) return;

            // Pacing: don't send faster than minSendInterval
            if (Time.realtimeSinceStartup - lastSendTime < minSendInterval) return;

            // Flow control: one frame in-flight at a time
            if (readbackPending || waitingForMesh) return;

            var depthTex = DepthKitDriver.Instance.DepthTex;
            if (depthTex == null) return;

            // Ensure the CPU-readable copy texture matches the current depth resolution
            if (depthCopy == null || depthCopy.width != depthTex.width || depthCopy.height != depthTex.height)
            {
                if (depthCopy != null) depthCopy.Release();
                depthCopy = new RenderTexture(depthTex.width, depthTex.height, 0, RenderTextureFormat.RFloat)
                {
                    enableRandomWrite = true
                };
                depthCopy.Create();
            }

            // GPU → GPU: convert depth texture to R32Float for readback
            depthCopyKernel.Set("_InputDepth",  depthTex);
            depthCopyKernel.Set("_OutputDepth", depthCopy);
            depthCopyKernel.DispatchFit(depthCopy);

            // Kick off async readback — callback fires 1-2 frames later (non-blocking)
            readbackPending = true;
            AsyncGPUReadback.Request(depthCopy, 0, request =>
            {
                readbackPending = false;

                if (request.hasError)
                {
                    Debug.LogError("[EdgeServerClient] GPU readback failed");
                    return;
                }

                // ── Copy depth bytes into a pooled managed buffer ─────────────
                // NativeArray.CopyTo(managed[]) does a single memcpy with no GC,
                // replacing the old `.ToArray()` call that allocated ~360KB per
                // send. The pool grows once and is reused forever.
                NativeArray<byte> depthNative = request.GetData<byte>();
                int depthLen = depthNative.Length;
                if (depthBytesBuffer == null || depthBytesBuffer.Length < depthLen)
                    depthBytesBuffer = new byte[Mathf.NextPowerOfTwo(depthLen)];
                NativeArray<byte>.Copy(depthNative, 0, depthBytesBuffer, 0, depthLen);

                // ── Build the frame payload into the pooled send buffer ───────
                // Layout: [5 B header][8 B timestamp][540 B frame meta][depth]
                // Everything is composed into a single contiguous buffer so the
                // background send only needs one stream.Write call.
                byte[] frameData = SerializeFrameData();       // 636 B (reused)
                const int headerLen = 5;
                const int tsLen     = 8;
                int payloadLen = tsLen + frameData.Length + depthLen;
                int totalLen   = headerLen + payloadLen;

                if (sendPayloadBuffer == null || sendPayloadBuffer.Length < totalLen)
                    sendPayloadBuffer = new byte[Mathf.NextPowerOfTwo(totalLen)];
                byte[] buf = sendPayloadBuffer;

                // Header
                buf[0] = 0x01;
                buf[1] = (byte)(payloadLen >> 24);
                buf[2] = (byte)(payloadLen >> 16);
                buf[3] = (byte)(payloadLen >> 8);
                buf[4] = (byte)(payloadLen);

                // Timestamp (captured as late as possible, right before the send)
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Buffer.BlockCopy(BitConverter.GetBytes((ulong)nowMs), 0, buf, headerLen, tsLen);

                // Frame metadata + depth pixels
                Buffer.BlockCopy(frameData, 0, buf, headerLen + tsLen, frameData.Length);
                Buffer.BlockCopy(depthBytesBuffer, 0, buf, headerLen + tsLen + frameData.Length, depthLen);

                // ── Record pose/time on MAIN thread ───────────────────────────
                // headTransform access must stay on main thread.
                sentHeadPos          = headTransform != null ? headTransform.position : Vector3.zero;
                sentHeadRot          = headTransform != null ? headTransform.rotation : Quaternion.identity;
                waitingForMesh       = true;
                lastSendTime         = Time.realtimeSinceStartup;
                lastPayloadBytesSent = totalLen;

                // ── Push the TCP write to a background thread ─────────────────
                // NetworkStream.Write is synchronous — on Wi-Fi with head-motion
                // backpressure it can block 5–15ms. Doing it on the main thread
                // was the dominant cause of head-move stutter. Read() stays on
                // the main thread (in Update()), which is the documented safe
                // pattern: separate reader/writer threads on one NetworkStream.
                //
                // waitingForMesh already gates to one in-flight request, so the
                // sendLock here is belt-and-braces.
                NetworkStream localStream = stream;
                Task.Run(() =>
                {
                    try
                    {
                        lock (sendLock)
                        {
                            localStream.Write(buf, 0, totalLen);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[EdgeServerClient] Send failed: {e.Message}");
                    }
                });
            });
        }

        // Maximum player head positions we serialize per frame
        // Must match MAX_PLAYERS on the Mac side (DepthFrame.swift + Shader)
        private const int MAX_PLAYERS = 8;

        // ─────────────────────────────────────────────────────────────────────
        // SerializeFrameData — Builds the 636-byte frame metadata header
        //
        // LAYOUT (matches parseDepthFrame in DepthFrame.swift):
        //   512 bytes: 8 camera matrices (view/proj/viewInv/projInv × 2 eyes)
        //   28 bytes:  volume config (voxelCount xyz + voxelSize + voxelDist +
        //              maxUpdateDist + numPlayers)
        //   96 bytes:  8 player head positions × 12 bytes (float3 x/y/z each)
        //              Only the first `numPlayers` slots are valid — rest are zero.
        //              Used by the Mac integrate shader to skip voxels inside
        //              player-body cylinders (PLAYER_RADIUS, PLAYER_TOP, PLAYER_BOTTOM).
        //
        // The 8-byte timestamp is NOT included here — it's prepended separately
        // in OnDepthUpdated so the timestamp is captured as late as possible
        // (right before write, not during matrix serialization).
        // ─────────────────────────────────────────────────────────────────────
        private byte[] SerializeFrameData()
        {
            var dkd    = DepthKitDriver.Instance;
            var mapper = EnvironmentMapper.Instance;

            // 512 (matrices) + 28 (volume config) + 96 (player heads) = 636 bytes
            var data   = new byte[512 + 28 + MAX_PLAYERS * 12];
            int offset = 0;

            void WriteFloat(float f)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(f), 0, data, offset, 4);
                offset += 4;
            }
            void WriteInt(int i)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(i), 0, data, offset, 4);
                offset += 4;
            }
            void WriteMatrix(Matrix4x4 m)
            {
                for (int i = 0; i < 16; i++) WriteFloat(m[i]);
            }

            // ── 512 bytes: matrices for both eyes ────────────────────────────
            // Order must match readMatrix() calls in DepthFrame.swift parseDepthFrame()
            for (int eye = 0; eye < 2; eye++) WriteMatrix(dkd.View[eye]);
            for (int eye = 0; eye < 2; eye++) WriteMatrix(dkd.Proj[eye]);
            for (int eye = 0; eye < 2; eye++) WriteMatrix(dkd.ViewInv[eye]);
            for (int eye = 0; eye < 2; eye++) WriteMatrix(dkd.ProjInv[eye]);

            // ── 28 bytes: volume config ──────────────────────────────────────
            int numPlayers = Mathf.Min(mapper.PlayerHeads.Count, MAX_PLAYERS);
            WriteInt(mapper.VoxelCount.x);
            WriteInt(mapper.VoxelCount.y);
            WriteInt(mapper.VoxelCount.z);
            WriteFloat(mapper.VoxelSize);
            WriteFloat(mapper.VoxelDistance);
            WriteFloat(mapper.MaxUpdateDist);
            WriteInt(numPlayers);

            // ── 96 bytes: player head positions (8 slots × 3 floats) ─────────
            // Valid slots hold the player's head world position.
            // Unused slots are filled with zeros and ignored by the shader.
            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                if (i < numPlayers && mapper.PlayerHeads[i] != null)
                {
                    Vector3 p = mapper.PlayerHeads[i].position;
                    WriteFloat(p.x);
                    WriteFloat(p.y);
                    WriteFloat(p.z);
                }
                else
                {
                    WriteFloat(0f);
                    WriteFloat(0f);
                    WriteFloat(0f);
                }
            }

            return data;
        }

        // ─────────────────────────────────────────────────────────────────────
        // OnDestroy — Cleanup when play mode ends or GameObject is destroyed
        // ─────────────────────────────────────────────────────────────────────
        private void OnDestroy()
        {
            // Unsubscribe from the debug-mode event so we don't leak a handler
            // across scene reloads / domain reloads.
            AnaglyphCore.DebugModeChanged -= OnDebugModeChanged;

            // Signal the reader thread to exit. Closing the stream will unblock
            // any in-progress Read() by throwing — the reader's catch handles it.
            readerShouldStop = true;

            stream?.Close();
            client?.Close();

            // Best-effort join; if the reader is stuck we don't want to hang
            // Unity's shutdown, so give it a short deadline.
            if (readerThread != null && readerThread.IsAlive)
                readerThread.Join(250);

            voxelBuffer?.Release();
            // Re-enable on-device meshing only here, on full teardown (scene
            // unload / app quit). We deliberately do NOT flip this back to
            // false on a runtime disconnect — that would silently fall back
            // to local TSDF integration, defeating the point of edge-only.
            EnvironmentMapper.UseEdgeServer = false;

            IsConnected = false;
            IsMeshReady = false;
        }
    }
}
