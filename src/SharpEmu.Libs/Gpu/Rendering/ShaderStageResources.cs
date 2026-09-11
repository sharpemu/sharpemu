// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Rendering;

public enum ShaderStageKind
{
    Unknown,
    Vertex,
    Pixel,
    Compute,
}

public enum ImageResourceClass : byte
{
    None,
    Sampled,
    Storage,
}

// How a compiled program uses one buffer resource.
public readonly record struct BufferResourceInfo(
    bool Read,
    bool Written,
    bool Atomic,
    bool Formatted,
    bool Scalar,
    uint MaxByteExtent,
    uint PackedStride);

// How a compiled program uses one image resource.
public readonly record struct ImageResourceInfo(ImageResourceClass Class, bool Written);

// The immutable part of a compiled program the executor reads.
public class ShaderProgramInfo
{
    public const int NoScalarRegister = -1;

    public ShaderStageKind Stage { get; init; }
    public ulong Hash { get; init; }
    public uint UserDataBase { get; init; }
    public uint ParameterExportMask { get; init; }
    public int VertexOffsetScalarRegister { get; init; } = NoScalarRegister;
    public int InstanceOffsetScalarRegister { get; init; } = NoScalarRegister;
    public bool UsesDeviceAddresses { get; init; }
    public bool HasBitwiseExclusiveOr { get; init; }
    public BufferResourceInfo[] Buffers { get; init; } = [];
    public ImageResourceInfo[] Images { get; init; } = [];
    public int SamplerCount { get; init; }
}

// The descriptor words and user data captured for one program at draw time.
public sealed class ResourceSnapshot
{
    public uint[][] Buffers { get; init; } = [];
    public uint[][] Images { get; init; } = [];
    public uint[][] Samplers { get; init; } = [];
    public uint[] UserData { get; init; } = [];
}

// One shader stage bound to a draw or dispatch: its program and the resources it reads.
public readonly record struct ShaderStageResources(ShaderProgramInfo? Program, ResourceSnapshot Resources)
{
    public bool IsValid => Program is not null;
}

// The vertex buffer words of one fetch slot as the vertex program declares them.
public readonly record struct VertexInputBuffer(ulong Address, uint Stride, uint RecordCount)
{
    public ulong Size => Stride != 0 ? (ulong)Stride * RecordCount : RecordCount;
}

public sealed class VertexInputInfo
{
    public const int MaxBuffers = 32;

    public VertexInputBuffer[] Buffers { get; init; } = [];
    public bool FetchEmbedded { get; init; }
    public ShaderStageResources Stage { get; init; }
}

public sealed class PixelInputInfo
{
    public uint InputCount { get; init; }
    public ShaderStageResources Stage { get; init; }
}

public sealed class ComputeInputInfo
{
    public uint ThreadsX { get; init; }
    public uint ThreadsY { get; init; }
    public uint ThreadsZ { get; init; }
    public bool DispatchThreadDimensions { get; init; }
    public uint DispatchThreadsX { get; set; }
    public uint DispatchThreadsY { get; set; }
    public uint DispatchThreadsZ { get; set; }
    public bool GroupIdX { get; init; }
    public bool GroupIdY { get; init; }
    public bool GroupIdZ { get; init; }
    public int ThreadIdCount { get; init; }
    public bool ThreadGroupSizeEnabled { get; init; }
    public uint WaveSize { get; init; } = 64;
    public uint LocalDataShareDwords { get; init; }
    public bool NeedsLocalDataShareBarriers { get; init; }
    public ShaderStageResources Stage { get; init; }
}

// The four buffer resource words as the guest writes them.
public readonly record struct BufferDescriptorWords(uint Word0, uint Word1, uint Word2, uint Word3)
{
    public const uint Format32x4UInt = 75;

    public static BufferDescriptorWords From(ReadOnlySpan<uint> words) => new(words[0], words[1], words[2], words[3]);

    public ulong Address => (Word0 | ((ulong)Word1 << 32)) & 0xFFFF_FFFF_FFFFul;
    public uint Stride => (Word1 >> 16) & 0x3FFF;
    public bool SwizzleEnabled => (Word1 >> 31) != 0;
    public uint RecordCount => Word2;
    public byte DestinationSelectX => (byte)(Word3 & 0x7);
    public uint IndexStride => (Word3 >> 21) & 0x3;
    public bool AddThreadId => ((Word3 >> 23) & 0x1) != 0;
    public uint Format => (Word3 >> 12) & 0x7F;
    public uint OutOfBounds => (Word3 >> 28) & 0x3;
    public uint Type => (Word3 >> 30) & 0x3;

    public uint PackedStride =>
        Stride | ((SwizzleEnabled ? 1u : 0u) << 14) | (IndexStride << 16) | ((AddThreadId ? 1u : 0u) << 20);

    // The byte footprint the records cover; null when it overflows.
    public ulong? Footprint()
    {
        ulong records = RecordCount;
        ulong stride = Stride;
        if (stride != 0 && records > ulong.MaxValue / stride)
        {
            return null;
        }

        return stride == 0 ? records : records * stride;
    }
}
