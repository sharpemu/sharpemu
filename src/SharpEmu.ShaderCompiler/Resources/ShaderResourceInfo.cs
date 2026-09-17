// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Resources;

public enum ShaderStage : byte
{
    Unknown,
    Vertex,
    Pixel,
    Compute,
}

public enum ImageNumericClass : byte
{
    Unsupported,
    Float,
    Uint,
    Sint,
}

public enum ImageMipMode : byte
{
    None,
    DynamicStorage,
}

// The identity dword selection with every component in place.
public static class DescriptorConstants
{
    public const uint IdentityDestinationSelect = 4 | (5 << 3) | (6 << 6) | (7 << 9);
    public const uint IdentityImageSwizzle = 0xFAC;
    public const uint InvalidFormat = 0;
    public const uint NoIndex = uint.MaxValue;
}

// One buffer resource of a program and how the program uses it.
public sealed class BufferResource
{
    public uint Source { get; set; }
    public uint FirstUsePc { get; set; }
    public uint MaxByteExtent { get; set; }
    public uint PackedStride { get; set; }
    public uint DescriptorFormat { get; set; } = DescriptorConstants.InvalidFormat;
    public uint DescriptorSwizzle { get; set; } = DescriptorConstants.IdentityDestinationSelect;
    public uint ImageAlias { get; set; } = DescriptorConstants.NoIndex;
    public bool Read { get; set; }
    public bool Written { get; set; }
    public bool Atomic { get; set; }
    public bool Formatted { get; set; }
    public bool Scalar { get; set; }

    public BufferResource Clone() => (BufferResource)MemberwiseClone();
}

// One image resource: a descriptor source with its view class, dimension and use.
public sealed class ImageResource
{
    public uint Source { get; set; }
    public uint FirstUsePc { get; set; }
    public ImageResourceClass ResourceClass { get; set; }
    public ImageNumericClass NumericClass { get; set; } = ImageNumericClass.Unsupported;
    public ImageDimension Dimension { get; set; } = ImageDimension.Unknown;
    public ImageMipMode MipMode { get; set; }
    public uint MipCount { get; set; } = 1;
    public uint ConversionFormat { get; set; } = DescriptorConstants.InvalidFormat;
    public uint ShaderSwizzle { get; set; } = DescriptorConstants.IdentityImageSwizzle;
    public bool Read { get; set; }
    public bool Written { get; set; }
    public bool Atomic { get; set; }
    public bool DepthCompare { get; set; }
    public bool Cube { get; set; }
    public bool R128 { get; set; }
    public uint IndirectRoot { get; set; } = DescriptorConstants.NoIndex;
    public uint IndirectMappingOffset { get; set; }
    public uint IndirectSearchIterations { get; set; }
    public List<uint> IndirectResources { get; set; } = [];

    public ImageResource Clone()
    {
        var clone = (ImageResource)MemberwiseClone();
        clone.IndirectResources = [.. IndirectResources];
        return clone;
    }
}

public sealed class SamplerResource
{
    public uint Source { get; set; }
    public uint FirstUsePc { get; set; }
    public bool ForcePointFiltering { get; set; }
    public bool DepthCompare { get; set; }

    public SamplerResource Clone() => (SamplerResource)MemberwiseClone();
}

public sealed class SampledImagePair
{
    public uint Image { get; set; }
    public uint Sampler { get; set; }
    public uint FirstUsePc { get; set; }

    public SampledImagePair Clone() => (SampledImagePair)MemberwiseClone();
}

public enum StageInputKind : byte
{
    VertexIndex,
    InstanceIndex,
    FragCoord,
    FrontFacing,
    BaryCoordSmooth,
    BaryCoordNoPerspective,
    WorkgroupId,
    LocalInvocationId,
    LocalInvocationIndex,
    GlobalInvocationId,
    Parameter,
}

public enum StageOutputKind : byte
{
    Position,
    Parameter,
    Mrt,
    Depth,
    SampleMask,
    PointSize,
    ClipDistance,
    CullDistance,
    Layer,
}

public sealed record StageInput(StageInputKind Kind, uint Location, uint ComponentCount, string DebugName, bool PerVertex);

public sealed record StageOutput(StageOutputKind Kind, uint Index, uint Location, string DebugName);

// The dense resource tables of a program plus the facts the pipeline layout needs.
public sealed class ShaderResourceInfo
{
    public const int MaxBuffers = 32;
    public const int MaxImages = 32;
    public const int MaxSamplers = 32;
    public const int MaxSampledPairs = 64;
    public const int NoScalarRegister = -1;

    public List<BufferResource> Buffers { get; set; } = [];
    public List<ImageResource> Images { get; set; } = [];
    public List<SamplerResource> Samplers { get; set; } = [];
    public List<SampledImagePair> SampledPairs { get; set; } = [];
    public List<StageInput> Inputs { get; set; } = [];
    public List<StageOutput> Outputs { get; set; } = [];
    public byte[] VertexFetchComponents { get; set; } = new byte[32];
    public int VertexOffsetScalarRegister { get; set; } = NoScalarRegister;
    public int InstanceOffsetScalarRegister { get; set; } = NoScalarRegister;
    public bool HasBitwiseExclusiveOr { get; set; }
    public bool UsesDeviceAddresses { get; set; }

    public ShaderResourceInfo Clone() => new()
    {
        Buffers = Buffers.Select(buffer => buffer.Clone()).ToList(),
        Images = Images.Select(image => image.Clone()).ToList(),
        Samplers = Samplers.Select(sampler => sampler.Clone()).ToList(),
        SampledPairs = SampledPairs.Select(pair => pair.Clone()).ToList(),
        Inputs = [.. Inputs],
        Outputs = [.. Outputs],
        VertexFetchComponents = (byte[])VertexFetchComponents.Clone(),
        VertexOffsetScalarRegister = VertexOffsetScalarRegister,
        InstanceOffsetScalarRegister = InstanceOffsetScalarRegister,
        HasBitwiseExclusiveOr = HasBitwiseExclusiveOr,
        UsesDeviceAddresses = UsesDeviceAddresses,
    };
}

// A material-table key that selects one of several heap descriptors at run time.
public sealed record IndirectImageSelector(
    uint MaterialSource,
    uint HeapSource,
    uint SelectorStride,
    uint SelectorOffset,
    uint KeyArgument)
{
    public IndirectSelectorValues? SelectorValues { get; init; }
    public IReadOnlyList<DirectImageCandidate>? DirectCandidates { get; init; }
}

public sealed record DirectImageCandidate(uint Offset, uint Source);

// The graph values one descriptor is assembled from, up to eight dwords.
public sealed class DescriptorSource
{
    public ScalarValue[] Dwords { get; init; } = [];
    public uint DwordCount => (uint)Dwords.Length;
    public IndirectImageSelector? IndirectImage { get; init; }
}

// One immediate-offset scalar read the host evaluates into the flattened table.
public sealed record ResourceTableRead(ScalarValue Value, uint FlatOffset);

// The dwords of one materialised descriptor.
public readonly record struct DescriptorWords(uint[] Dwords)
{
    public uint DwordCount => (uint)Dwords.Length;

    public static DescriptorWords Empty(uint dwordCount) => new(new uint[dwordCount]);

    public bool SameAs(DescriptorWords other) => Dwords.AsSpan().SequenceEqual(other.Dwords);
}
