// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

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

// The immutable part of a compiled program the executor and the hosts read.
public class ShaderProgramInfo
{
    public const int NoScalarRegister = -1;

    public ShaderStageKind Stage { get; init; }
    public ulong Hash { get; init; }
    public uint UserDataBase { get; init; }
    public uint UserDataCount { get; init; }
    public uint ParameterExportMask { get; init; }
    // Four component bits per color target (MRT0 in the low nibble) the pixel program exports.
    public uint PixelColorExportMasks { get; init; } = uint.MaxValue;
    public int VertexOffsetScalarRegister { get; init; } = NoScalarRegister;
    public int InstanceOffsetScalarRegister { get; init; } = NoScalarRegister;
    public bool UsesDeviceAddresses { get; init; }
    public bool HasBitwiseExclusiveOr { get; init; }
    public BufferResourceInfo[] Buffers { get; init; } = [];
    public ImageResourceInfo[] Images { get; init; } = [];
    public int SamplerCount { get; init; }
    public uint WaveSize { get; init; } = 64;
    public uint ScratchDwords { get; init; }

    // The components the vertex program fetches per attribute location; zero when it fetches none.
    public byte[] VertexFetchComponents { get; init; } = new byte[VertexInputInfo.MaxBuffers];

    // The specialized resources and the binding layout the module was compiled against.
    public SpecializedResourceInfo? Resources { get; init; }
    public BindingLayout? Bindings { get; init; }
}

// One shader stage bound to a draw or dispatch: its program, the resources it reads and its code base.
public readonly record struct ShaderStageResources(ShaderProgramInfo? Program, ResourceSnapshot Resources, ulong ShaderBase = 0)
{
    public bool IsValid => Program is not null;

    public DispatchThreadLimits? ThreadLimits { get; init; }

    public void WriteDispatchThreadLimits(Span<uint> shaderData)
    {
        if (Program?.Bindings is not { UsesDispatchThreadLimits: true } layout) return;
        if (ThreadLimits is not { } limits || shaderData.Length != layout.ShaderDataDwordCount)
        {
            throw SubmissionScheduler.Fatal("The compute dispatch has missing thread limits or invalid shader data.");
        }

        var offset = (int)layout.DispatchThreadLimitsDword;
        shaderData[offset] = limits.X;
        shaderData[offset + 1] = limits.Y;
        shaderData[offset + 2] = limits.Z;
    }
}

public readonly record struct DispatchThreadLimits(uint X, uint Y, uint Z);

// The vertex buffer words of one fetch slot as the vertex program declares them.
public readonly record struct VertexInputBuffer(ulong Address, uint Stride, uint RecordCount, bool PerInstance = false)
{
    public ulong Size => Stride != 0 ? (ulong)Stride * RecordCount : RecordCount;
}

// One attribute of the vertex tables: its buffer words, the registers it fills and its buffer slot.
public readonly record struct VertexAttributeResource(
    BufferDescriptorWords Descriptor,
    int RegisterStart,
    int RegisterCount,
    int AttributeId,
    uint FetchIndex,
    int BufferIndex,
    uint OffsetBytes);

// The viewport transform a vertex program applies when clipping is off.
public readonly record struct ClipSpaceTransform(
    bool Enabled,
    float ScaleX,
    float ScaleY,
    float OffsetX,
    float OffsetY,
    float HalfExtentX,
    float HalfExtentY);

public sealed class VertexInputInfo
{
    public const int MaxBuffers = 32;

    public VertexInputBuffer[] Buffers { get; init; } = [];
    public VertexAttributeResource[] Attributes { get; init; } = [];
    public bool FetchEmbedded { get; init; }
    public int FetchAttributeRegister { get; init; }
    public int FetchBufferRegister { get; init; }
    public uint ScratchDwords { get; init; }
    public uint PositionExportControl { get; init; }
    public ClipSpaceTransform ClipSpace { get; init; }
    public ShaderStageResources Stage { get; set; }
}

public sealed class PixelInputInfo
{
    public const int InterpolatorCount = 32;
    public const int TargetCount = 8;
    public const uint NoPerspectiveCenterRegister = uint.MaxValue;

    public uint InputCount { get; init; }
    public uint SystemInputBase { get; init; }
    public uint CustomInterpolationMask { get; init; }
    public uint PerspectiveCenterRegister { get; init; } = NoPerspectiveCenterRegister;
    public uint[] InterpolatorSettings { get; init; } = new uint[InterpolatorCount];
    public byte[] TargetOutputModes { get; init; } = new byte[TargetCount];
    public ColorComponentMap[] TargetExportMappings { get; init; } = new ColorComponentMap[TargetCount];
    public uint ScratchDwords { get; init; }
    public bool PositionX { get; init; }
    public bool PositionY { get; init; }
    public bool PositionZ { get; init; }
    public bool PositionW { get; init; }
    public bool FrontFace { get; init; }
    public bool NoPerspective { get; init; }
    public bool KillEnable { get; init; }
    public bool DepthExportEnable { get; init; }
    public bool SampleMaskExportEnable { get; init; }
    public bool SampleShading { get; init; }
    public bool EarlyDepth { get; init; }
    public bool ExecuteOnNoop { get; init; }
    public ShaderStageResources Stage { get; set; }

    public bool PositionXY => PositionX && PositionY;
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
    public uint ScratchDwords { get; init; }
    public bool NeedsLocalDataShareBarriers { get; init; }
    public int WorkgroupRegister { get; init; }
    public ShaderStageResources Stage { get; set; }
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
    public byte DestinationSelectY => (byte)((Word3 >> 3) & 0x7);
    public byte DestinationSelectZ => (byte)((Word3 >> 6) & 0x7);
    public byte DestinationSelectW => (byte)((Word3 >> 9) & 0x7);
    public uint DestinationSelectXY => Word3 & 0x3F;
    public uint DestinationSelectXYZ => Word3 & 0x1FF;
    public uint DestinationSelectXYZW => Word3 & 0xFFF;
    public uint IndexStride => (Word3 >> 21) & 0x3;
    public bool AddThreadId => ((Word3 >> 23) & 0x1) != 0;
    public uint Format => (Word3 >> 12) & 0x7F;
    public uint OutOfBounds => (Word3 >> 28) & 0x3;
    public uint Type => (Word3 >> 30) & 0x3;

    public uint PackedStride =>
        Stride | ((SwizzleEnabled ? 1u : 0u) << 14) | (IndexStride << 16) | ((AddThreadId ? 1u : 0u) << 20);

    // The words with the 48-bit base replaced; the high address bits keep their other fields.
    public BufferDescriptorWords WithAddress(ulong address) =>
        this with { Word0 = (uint)address, Word1 = (Word1 & 0xFFFF_0000u) | (uint)((address >> 32) & 0xFFFFu) };

    public void CopyTo(Span<uint> words)
    {
        words[0] = Word0;
        words[1] = Word1;
        words[2] = Word2;
        words[3] = Word3;
    }

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
