// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Resources;

public readonly record struct BufferSpecialization(uint PackedStride, uint DescriptorFormat, uint DescriptorSwizzle);

// The module-affecting layout of one bounded runtime V# table. Candidate descriptors
// themselves are appended to Buffers; this names their contiguous run and the flattened
// key mapping, so the switch body does not have to embed per-draw addresses.
public readonly record struct BufferCandidateTableSpecialization(
    uint FirstCandidate,
    uint CandidateCount,
    uint MappingOffset,
    uint SearchIterations);

public readonly record struct ImageSpecialization(
    ImageNumericClass NumericClass,
    ImageDimension Dimension,
    uint MipCount,
    uint ConversionFormat,
    uint ShaderSwizzle,
    uint IndirectRoot,
    uint IndirectMappingOffset,
    uint IndirectSearchIterations,
    bool Cube,
    int EmulatedCompareFunction = -1);

// The module-affecting resource state of one draw. Addresses and descriptor payloads
// stay in the snapshot, so they never create a permutation.
public sealed class ResourceSpecialization : IEquatable<ResourceSpecialization>
{
    // The plan's base buffer count; Buffers beyond it are materialized candidates.
    public int BaseBufferCount { get; init; }
    public List<BufferSpecialization> Buffers { get; init; } = [];
    public List<ImageSpecialization> Images { get; init; } = [];
    public List<BufferCandidateTableSpecialization> BufferCandidateTables { get; init; } = [];

    public bool Equals(ResourceSpecialization? other) =>
        other is not null && BaseBufferCount == other.BaseBufferCount && Buffers.SequenceEqual(other.Buffers) &&
        Images.SequenceEqual(other.Images) && BufferCandidateTables.SequenceEqual(other.BufferCandidateTables);

    public override bool Equals(object? obj) => Equals(obj as ResourceSpecialization);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(BaseBufferCount);
        foreach (var buffer in Buffers)
        {
            hash.Add(buffer);
        }

        foreach (var image in Images)
        {
            hash.Add(image);
        }

        foreach (var table in BufferCandidateTables)
        {
            hash.Add(table);
        }

        return hash.ToHashCode();
    }

    public ResourceSpecialization Clone() => new()
    {
        BaseBufferCount = BaseBufferCount,
        Buffers = [.. Buffers],
        Images = [.. Images],
        BufferCandidateTables = [.. BufferCandidateTables],
    };

    // The specialization of a plan before any draw: raw buffers and the tracked image classes.
    public static ResourceSpecialization Default(ShaderResourceInfo info) => new()
    {
        BaseBufferCount = info.Buffers.Count,
        Buffers = info.Buffers.Select(_ => new BufferSpecialization(0, DescriptorConstants.InvalidFormat, DescriptorConstants.IdentityDestinationSelect)).ToList(),
        Images = info.Images.Select(image => new ImageSpecialization(
            image.NumericClass == ImageNumericClass.Unsupported ? (image.Atomic ? ImageNumericClass.Uint : ImageNumericClass.Float) : image.NumericClass,
            image.Dimension == ImageDimension.Unknown ? ImageDimension.Dim2D : image.Dimension,
            image.MipCount, image.ConversionFormat, image.ShaderSwizzle,
            image.IndirectRoot, image.IndirectMappingOffset, image.IndirectSearchIterations, image.Cube)).ToList(),
    };
}

// A plan's resource tables with one draw's specialization applied, which the emitter
// compiles against. Sampler overrides name the point-filtering duplicate an access uses.
public sealed class SpecializedResourceInfo
{
    public ShaderResourceInfo Info { get; init; } = new();
    public IReadOnlyDictionary<int, uint> SamplerByMemoryIndex { get; init; } = new Dictionary<int, uint>();
}
