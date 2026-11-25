// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.Libs.Gpu.Upscaling;

public enum SuperResolutionInput
{
    OutputColor = 0,
    Color = 1,
    MotionVectors = 2,
    PreviousDepth = 3,
    Depth = 4,
    Exposure = 5,
    ReactiveMask = 6,
    PreviousMotionVectors = 7,
}

[Flags]
public enum SuperResolutionFlags : uint
{
    None = 0,
    ViewSpaceDepth = 1u << 14,
    ReverseDepth = 1u << 15,
    AutoExposure = 1u << 16,
    PassThrough = 1u << 31,
}

public static class SuperResolutionPacket
{
    public const uint MarkerMagic = 0x6875_0000u;
    public const uint MarkerId = 0xE5A;
    public const uint Tag = 0x5253_5053u;
    public const uint Version = 1;
    public const int TextureCount = 8;
    public const int TextureDwords = 8;
    public const int ParameterOffset = 0x58;
    public const int ParameterBytes = 0x100;
    public const int DispatchParameterBytes = ParameterOffset + ParameterBytes;
    private const int HeaderDwords = 6;
    private const int TextureBlockDwords = TextureCount * TextureDwords;
    public const int Dwords = HeaderDwords + TextureBlockDwords + ParameterBytes / sizeof(uint);

    public static bool IsMarker(uint markerWord) => markerWord == (MarkerMagic | MarkerId);

    public static void Write(
        Span<uint> destination,
        uint contextId,
        uint outputWidth,
        uint outputHeight,
        uint textureMask,
        ReadOnlySpan<uint> textures,
        ReadOnlySpan<byte> parameters)
    {
        if (destination.Length < Dwords || textures.Length < TextureBlockDwords || parameters.Length < ParameterBytes)
        {
            throw new ArgumentException("The super resolution packet needs its full texture and parameter blocks.");
        }

        destination[..Dwords].Clear();
        destination[0] = PacketHeader.Make(Dwords, PacketOpcode.Nop);
        destination[1] = MarkerMagic | MarkerId;
        destination[2] = Tag;
        destination[3] = Version | ((textureMask & 0xFFu) << 8);
        destination[4] = contextId;
        destination[5] = (outputWidth & 0xFFFFu) | ((outputHeight & 0xFFFFu) << 16);
        textures[..TextureBlockDwords].CopyTo(destination[HeaderDwords..]);
        MemoryMarshal.Cast<byte, uint>(parameters[..ParameterBytes]).CopyTo(destination[(HeaderDwords + TextureBlockDwords)..]);
    }

    public static bool TryRead(ReadOnlySpan<uint> payload, out SuperResolutionDispatch dispatch)
    {
        dispatch = null!;
        if (payload.Length < Dwords - 1 || !IsMarker(payload[0]) || payload[1] != Tag || (payload[2] & 0xFFu) != Version)
        {
            return false;
        }

        var textures = payload.Slice(HeaderDwords - 1, TextureBlockDwords).ToArray();
        var parameters = MemoryMarshal.AsBytes(payload.Slice(HeaderDwords - 1 + TextureBlockDwords, ParameterBytes / sizeof(uint))).ToArray();
        dispatch = new SuperResolutionDispatch(
            ContextId: payload[3],
            OutputWidth: payload[4] & 0xFFFFu,
            OutputHeight: payload[4] >> 16,
            TextureMask: (payload[2] >> 8) & 0xFFu,
            Textures: textures,
            Parameters: parameters);
        return true;
    }
}

public sealed record SuperResolutionDispatch(
    uint ContextId,
    uint OutputWidth,
    uint OutputHeight,
    uint TextureMask,
    uint[] Textures,
    byte[] Parameters)
{
    public bool Has(SuperResolutionInput input) => (TextureMask & (1u << (int)input)) != 0;

    public ReadOnlySpan<uint> Texture(SuperResolutionInput input) =>
        Textures.AsSpan((int)input * SuperResolutionPacket.TextureDwords, SuperResolutionPacket.TextureDwords);

    private float Single(int offset) => BinaryPrimitives.ReadSingleLittleEndian(Parameters.AsSpan(offset - SuperResolutionPacket.ParameterOffset));

    private uint UInt32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Parameters.AsSpan(offset - SuperResolutionPacket.ParameterOffset));

    private double Double(int offset) => BinaryPrimitives.ReadDoubleLittleEndian(Parameters.AsSpan(offset - SuperResolutionPacket.ParameterOffset));

    public uint InputColorSpace => UInt32(0x58);

    public float PreExposure => Single(0x64);

    public float LightIntensityToNits => Single(0x68);

    public float Projection(int row, int column) => Single(0x6C + (row * 4 + column) * sizeof(float));

    public (float X, float Y, float Z) CameraForward => (Single(0xAC), Single(0xB0), Single(0xB4));

    public (float X, float Y, float Z) CameraUp => (Single(0xB8), Single(0xBC), Single(0xC0));

    public (float X, float Y, float Z) CameraRight => (Single(0xC4), Single(0xC8), Single(0xCC));

    public (double X, double Y, double Z) CameraPosition => (Double(0xD0), Double(0xD8), Double(0xE0));

    public float NearZ => Single(0xE8);

    public float FarZ => Single(0xEC);

    public uint RenderWidth => UInt32(0xF0);

    public uint RenderHeight => UInt32(0xF4);

    public uint RenderOffsetX => UInt32(0xF8);

    public uint RenderOffsetY => UInt32(0xFC);

    public float JitterX => Single(0x100);

    public float JitterY => Single(0x104);

    public float MotionVectorScaleX => Single(0x108);

    public float MotionVectorScaleY => Single(0x10C);

    public float MotionVectorOffsetX => Single(0x110);

    public float MotionVectorOffsetY => Single(0x114);

    public bool Reset => Parameters[0x11C - SuperResolutionPacket.ParameterOffset] != 0;

    public SuperResolutionFlags Flags => (SuperResolutionFlags)UInt32(0x120);
}
