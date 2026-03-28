using System;
using System.IO;
using UnityEngine;

namespace Anaglyph.EdgeServer
{
    // ── Packet types ──────────────────────────────────────────────────────────────
    // Must stay in sync with PacketType enum in Protocol.swift.

    public enum PacketType : byte
    {
        Sync       = 0x00,
        DepthFrame = 0x01,
        MeshChunk  = 0x02,
    }

    // ── Parsed mesh chunk received from the Metal server ─────────────────────────

    public struct MeshChunkData
    {
        public long    depthTimestampMs;
        public long    serverSendMs;
        public long    serverComputeStartMs;
        public long    serverComputeEndMs;
        public Vector3 worldPos;
        public Vector3[] vertices;
        public Vector3[] normals;
        public int[]     indices;
        public int     serverQueueDepth;

        /// Round-trip latency in ms: depth captured on Quest → first mesh chunk sent by server.
        public long RoundTripMs => serverSendMs - depthTimestampMs;

        /// Server GPU + CPU processing time in ms.
        public long ServerComputeMs => serverComputeEndMs - serverComputeStartMs;
    }

    // ── Wire helpers ─────────────────────────────────────────────────────────────

    public static class EdgeProtocol
    {
        // ── Depth frame builder (Quest → Mac) ─────────────────────────────────
        // Layout (little-endian throughout):
        //   [1]  PacketType = 0x01
        //   [8]  timestampMs  int64
        //   [4]  width        int32
        //   [4]  height       int32
        //   [64] proj[0]      4×4 float32, row-major
        //   [64] proj[1]      4×4 float32, row-major
        //   [64] view[0]      4×4 float32, row-major
        //   [64] view[1]      4×4 float32, row-major
        //   [4]  near         float32
        //   [4]  far          float32
        //   [w*h*2] slice0    R16Unorm bytes (left eye)
        //   [w*h*2] slice1    R16Unorm bytes (right eye)

        public static byte[] BuildDepthFramePacket(
            long        timestampMs,
            int         width,
            int         height,
            Matrix4x4[] proj,      // length 2
            Matrix4x4[] view,      // length 2
            float       near,
            float       farPlane,
            byte[]      slice0,    // width*height*2 bytes
            byte[]      slice1)    // width*height*2 bytes
        {
            using var ms = new MemoryStream(1 + 8 + 4 + 4 + 64 * 4 + 8 + slice0.Length + slice1.Length);
            using var w  = new BinaryWriter(ms);

            w.Write((byte)PacketType.DepthFrame);
            w.Write(timestampMs);
            w.Write(width);
            w.Write(height);

            WriteMatrix4x4(w, proj[0]);
            WriteMatrix4x4(w, proj[1]);
            WriteMatrix4x4(w, view[0]);
            WriteMatrix4x4(w, view[1]);

            w.Write(near);
            w.Write(farPlane);

            w.Write(slice0);
            w.Write(slice1);

            return ms.ToArray();
        }

        // ── Mesh chunk parser (Mac → Quest) ───────────────────────────────────
        // Layout (must match serialiseMeshChunk in Protocol.swift):
        //   [1]  PacketType = 0x02
        //   [8]  depthTimestampMs     int64
        //   [8]  serverSendMs         int64
        //   [8]  serverComputeStartMs int64
        //   [8]  serverComputeEndMs   int64
        //   [12] worldPos             float32 × 3
        //   [4]  vertexCount          int32
        //   [4]  indexCount           int32
        //   [vertexCount*12] vertices float32 × 3 each
        //   [vertexCount*12] normals  float32 × 3 each
        //   [indexCount*4]   indices  int32 each
        //   [4]  serverQueueDepth     int32

        public static bool TryReadMeshChunkPacket(byte[] raw, out MeshChunkData result)
        {
            result = default;
            try
            {
                using var ms = new MemoryStream(raw);
                using var r  = new BinaryReader(ms);

                if (r.ReadByte() != (byte)PacketType.MeshChunk) return false;

                result.depthTimestampMs     = r.ReadInt64();
                result.serverSendMs         = r.ReadInt64();
                result.serverComputeStartMs = r.ReadInt64();
                result.serverComputeEndMs   = r.ReadInt64();

                result.worldPos = ReadVec3(r);

                int vertCount = r.ReadInt32();
                int idxCount  = r.ReadInt32();

                result.vertices = new Vector3[vertCount];
                for (int i = 0; i < vertCount; i++)
                    result.vertices[i] = ReadVec3(r);

                result.normals = new Vector3[vertCount];
                for (int i = 0; i < vertCount; i++)
                    result.normals[i] = ReadVec3(r);

                result.indices = new int[idxCount];
                for (int i = 0; i < idxCount; i++)
                    result.indices[i] = r.ReadInt32();

                result.serverQueueDepth = r.ReadInt32();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[EdgeProtocol] TryReadMeshChunkPacket failed: {e.Message}");
                return false;
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────

        // Unity Matrix4x4: m{row}{col}.  Write row-major so Swift reads them
        // as flat[0..15] = row0col0, row0col1, … row3col3 and transposes into
        // column-major simd_float4x4 (see readMatrix() in Protocol.swift).
        private static void WriteMatrix4x4(BinaryWriter w, Matrix4x4 m)
        {
            w.Write(m.m00); w.Write(m.m01); w.Write(m.m02); w.Write(m.m03);
            w.Write(m.m10); w.Write(m.m11); w.Write(m.m12); w.Write(m.m13);
            w.Write(m.m20); w.Write(m.m21); w.Write(m.m22); w.Write(m.m23);
            w.Write(m.m30); w.Write(m.m31); w.Write(m.m32); w.Write(m.m33);
        }

        private static Vector3 ReadVec3(BinaryReader r) =>
            new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
