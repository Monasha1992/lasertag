// Protocol.cs
// C# mirror of cuda_server/src/protocol.h
// Binary wire format for Quest <-> PC communication.
// All values little-endian, all structs packed (no padding).

using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Anaglyph.XRTemplate.Remote
{
    public enum MessageType : uint
    {
        DepthFrame   = 1,
        Clear        = 2,
        MeshRequest  = 3,
        MeshResult   = 4,
        Ping         = 5,
        Pong         = 6,
    }

    // 8-byte framing header that precedes every message
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MsgHeader
    {
        public uint Type;    // MessageType
        public uint Length;  // byte length of payload that follows
    }

    // Fixed part of MSG_DEPTH_FRAME payload.
    // After this struct in the byte stream:
    //   float[3 * NumPlayers]       player head world positions
    //   float[Width * Height]       depth NDC (0..1)
    //   float[4 * Width * Height]   normals (xyz + unused, floats)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DepthFrameHeader
    {
        public uint   Width;
        public uint   Height;

        // Column-major 4x4 matrices (Unity layout — same as HLSL)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] View;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] ViewInv;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] Proj;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] ProjInv;

        public uint NumPlayers;
    }

    // MSG_MESH_REQUEST payload (28 bytes)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MeshRequestPayload
    {
        public uint  RequestId;
        public float OriginX, OriginY, OriginZ;
        public float ExtentsX, ExtentsY, ExtentsZ;
    }

    // Fixed part of MSG_MESH_RESULT payload.
    // After this struct: Vertex[NumVertices] then uint32[NumIndices]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MeshResultHeader
    {
        public uint RequestId;
        public uint NumVertices;
        public uint NumIndices;
    }

    // Matches Mesher.cs Vertex layout and cuda_server Vertex struct exactly
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RemoteVertex
    {
        public float PosX, PosY, PosZ;
        public float NormX, NormY, NormZ;
    }

    // -----------------------------------------------------------------------
    // Wire helpers
    // -----------------------------------------------------------------------
    public static class Wire
    {
        public const int HeaderSize = 8; // sizeof(MsgHeader)

        // Write a complete framed message (header + payload) to a BinaryWriter
        public static void WriteMsg(BinaryWriter w, MessageType type, byte[] payload)
        {
            w.Write((uint)type);
            w.Write(payload != null ? (uint)payload.Length : 0u);
            if (payload != null && payload.Length > 0)
                w.Write(payload);
            w.Flush();
        }

        // Write a framed message with no payload
        public static void WriteMsg(BinaryWriter w, MessageType type)
        {
            WriteMsg(w, type, null);
        }

        // Read exactly n bytes from a stream (blocking)
        public static void ReadExact(Stream s, byte[] buf, int offset, int count)
        {
            int remaining = count;
            while (remaining > 0)
            {
                int read = s.Read(buf, offset + (count - remaining), remaining);
                if (read <= 0) throw new EndOfStreamException("Connection closed");
                remaining -= read;
            }
        }

        // Read the next framed message from a stream
        // Returns (type, payload). Payload may be empty (length 0).
        public static (MessageType type, byte[] payload) ReadMsg(Stream s)
        {
            byte[] hdrBuf = new byte[HeaderSize];
            ReadExact(s, hdrBuf, 0, HeaderSize);

            uint type   = BitConverter.ToUInt32(hdrBuf, 0);
            uint length = BitConverter.ToUInt32(hdrBuf, 4);

            byte[] payload = new byte[length];
            if (length > 0) ReadExact(s, payload, 0, (int)length);

            return ((MessageType)type, payload);
        }

        // Serialise a Unity Matrix4x4 to a flat float[16] in column-major order
        // Unity Matrix4x4 is already column-major in memory, so we just copy floats.
        public static float[] MatrixToFloats(Matrix4x4 m)
        {
            return new float[]
            {
                m.m00, m.m10, m.m20, m.m30,
                m.m01, m.m11, m.m21, m.m31,
                m.m02, m.m12, m.m22, m.m32,
                m.m03, m.m13, m.m23, m.m33
            };
        }

        // Write a float[] to a BinaryWriter
        public static void WriteFloats(BinaryWriter w, float[] arr)
        {
            foreach (float f in arr) w.Write(f);
        }

        // Write a float[] to a BinaryWriter from a NativeArray<float> via span
        public static void WriteFloatBytes(BinaryWriter w, ReadOnlySpan<byte> bytes)
        {
            w.Write(bytes);
        }
    }
}
