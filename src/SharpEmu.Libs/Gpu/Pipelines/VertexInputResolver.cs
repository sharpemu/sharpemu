// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The vertex table registers and input semantics of a vertex program's header.
public sealed class VertexTableMetadata
{
    public const int NoRegister = -1;

    public int VertexBufferRegister { get; init; } = NoRegister;
    public int VertexAttributeRegister { get; init; } = NoRegister;
    public uint[] InputSemantics { get; init; } = [];
}

public sealed class VertexTableWords
{
    public const int MaxAttributes = 256;
    public const int DescriptorWords = 4;
    public const int MaxWords = MaxAttributes + VertexInputInfo.MaxBuffers * DescriptorWords;

    public uint FirstAttribute { get; init; }

    public int AttributeCount { get; init; }

    public uint FirstBuffer { get; init; }

    public int BufferCount { get; init; }

    public uint[] Words { get; init; } = [];

    public bool WholeSpans { get; init; } = true;
}

// One packed input semantic word of a shader header.
public readonly record struct ShaderInputSemantic(uint Word)
{
    public uint Semantic => Word & 0xFFu;
    public uint HardwareMapping => (Word >> 8) & 0xFFu;
    public uint SizeInElements => (Word >> 16) & 0xFu;
    public uint IsHalfFloat => (Word >> 20) & 0x3u;
    public bool IsCustom => ((Word >> 24) & 0x1u) != 0;
    public bool StaticBufferIndex => ((Word >> 25) & 0x1u) != 0;
    public bool StaticAttribute => ((Word >> 26) & 0x1u) != 0;
}

// The vertex tables of a draw: attributes from the header semantics, buffers merged by stream.
public static class VertexInputResolver
{
    public static VertexInputInfo ResolveVertexInputs(CpuContext context, RegisteredShader shader, ReadOnlySpan<uint> userData,
        uint positionExportControl = 0, ClipSpaceTransform clipSpace = default) =>
        ResolveVertexInputs(context, shader, RequireTables(context, shader), userData, positionExportControl, clipSpace, out _);

    public static VertexTableMetadata RequireTables(CpuContext context, RegisteredShader shader)
    {
        // Legacy fetches remain explicit buffer instructions in the frontend's IR, not AGC vertex tables.
        if (shader.Frontend is { HasVertexTables: false })
            return new VertexTableMetadata();
        if (!TryReadTables(context, shader, GpuCommands.Registers.UserScalarRegisters.Capacity, out var metadata, out var error))
        {
            throw SubmissionScheduler.Fatal($"The vertex program header is invalid: shader=0x{shader.CodeAddress:X16} error={error}.");
        }

        return metadata;
    }

    public static VertexInputInfo ResolveVertexInputs(CpuContext context, RegisteredShader shader, VertexTableMetadata metadata,
        ReadOnlySpan<uint> userData, uint positionExportControl, ClipSpaceTransform clipSpace, out VertexTableWords? tableWords)
    {
        tableWords = null;
        var attributes = new List<VertexAttributeResource>();
        VertexInputBuffer[] buffers = [];
        if (ReadsTables(metadata))
        {
            var attributeTable = ReadTablePointer(userData, metadata.VertexAttributeRegister);
            var bufferTable = ReadTablePointer(userData, metadata.VertexBufferRegister);
            if (attributeTable == 0 || bufferTable == 0)
            {
                throw SubmissionScheduler.Fatal(
                    $"The vertex table pointer is null: shader=0x{shader.CodeAddress:X16} attributes=0x{attributeTable:X16} buffers=0x{bufferTable:X16} " +
                    $"attributeRegister={metadata.VertexAttributeRegister} bufferRegister={metadata.VertexBufferRegister} " +
                    $"user_data=[{string.Join(",", userData.ToArray().Select(word => $"{word:X8}"))}].");
            }

            tableWords = ReadTableWords(context, metadata.InputSemantics, attributeTable, bufferTable, shader.CodeAddress);
            attributes = ApplySemantics(metadata.InputSemantics, tableWords);
            buffers = MergeBuffers(attributes, shader.CodeAddress);
        }

        return new VertexInputInfo
        {
            Buffers = buffers,
            Attributes = attributes.ToArray(),
            FetchEmbedded = metadata.VertexAttributeRegister >= 0,
            FetchAttributeRegister = Math.Max(metadata.VertexAttributeRegister, 0),
            FetchBufferRegister = Math.Max(metadata.VertexBufferRegister, 0),
            ScratchDwords = shader.ScratchDwords,
            PositionExportControl = positionExportControl,
            ClipSpace = clipSpace,
        };
    }

    public static bool ReadsTables(VertexTableMetadata metadata) =>
        metadata.VertexAttributeRegister >= 0 && metadata.InputSemantics.Length != 0;

    public static ulong ReadTablePointer(ReadOnlySpan<uint> userData, int register) =>
        register < 0 || register + 1 >= userData.Length ? 0 : userData[register] | ((ulong)userData[register + 1] << 32);

    private const ushort IllegalDirectOffset = 0xFFFF;
    private const uint DirectResourceVertexBufferTable = 8;
    private const uint DirectResourceVertexAttributeTable = 10;
    private const uint DirectResourceCount = DirectResourceVertexAttributeTable + 1;
    private const ulong DirectResourceOffsetField = 0x00;
    private const ulong DirectResourceCountField = 0x2C;
    private static uint DestinationSelectForChannels(uint channels) =>
        4u |
        ((channels > 1 ? 5u : 0u) << 3) |
        ((channels > 2 ? 6u : 0u) << 6) |
        ((channels > 3 ? 7u : 1u) << 9);

    // Reads the direct resource registers and the input semantics; false with the reason when invalid.
    public static bool TryReadTables(CpuContext context, RegisteredShader shader, uint maxUserScalarRegisters, out VertexTableMetadata metadata, out string error)
    {
        metadata = new VertexTableMetadata();
        if (shader.UserDataAddress == 0)
        {
            error = "the header has no user data block";
            return false;
        }

        if (!context.TryReadUInt64(shader.UserDataAddress + DirectResourceOffsetField, out var directResourceOffsets) ||
            !context.TryReadUInt16(shader.UserDataAddress + DirectResourceCountField, out var directResourceCount))
        {
            error = "the user data block is unreadable";
            return false;
        }

        if (directResourceCount > DirectResourceCount)
        {
            error = "the direct resource count is outside the known resource domain";
            return false;
        }

        var bufferRegister = VertexTableMetadata.NoRegister;
        var attributeRegister = VertexTableMetadata.NoRegister;
        if (directResourceCount != 0)
        {
            if (directResourceOffsets == 0)
            {
                error = "the direct resource offsets are missing";
                return false;
            }

            for (uint type = 0; type < directResourceCount; type++)
            {
                if (!context.TryReadUInt16(directResourceOffsets + type * sizeof(ushort), out var register))
                {
                    error = "the direct resource offsets are unreadable";
                    return false;
                }

                if (register == IllegalDirectOffset)
                {
                    continue;
                }

                switch (type)
                {
                    case DirectResourceVertexBufferTable:
                        bufferRegister = register;
                        break;
                    case DirectResourceVertexAttributeTable:
                        attributeRegister = register;
                        break;
                }
            }
        }

        if (attributeRegister >= 0 && bufferRegister < 0)
        {
            error = "the vertex attribute table needs a vertex buffer table";
            return false;
        }

        if (bufferRegister < 0)
        {
            error = string.Empty;
            return true;
        }

        if ((uint)bufferRegister + 1u >= maxUserScalarRegisters)
        {
            error = "the vertex table pointer is outside the user register domain";
            return false;
        }

        if (attributeRegister < 0)
        {
            metadata = new VertexTableMetadata { VertexBufferRegister = bufferRegister };
            error = string.Empty;
            return true;
        }

        if ((uint)attributeRegister + 1u >= maxUserScalarRegisters)
        {
            error = "the vertex table pointer is outside the user register domain";
            return false;
        }

        if (shader.InputSemanticsCount == 0 || shader.InputSemanticsCount > VertexInputInfo.MaxBuffers)
        {
            error = "the vertex semantic count is outside the supported domain";
            return false;
        }

        if (shader.InputSemanticsAddress == 0)
        {
            error = "the vertex input semantics are missing";
            return false;
        }

        var semantics = new uint[shader.InputSemanticsCount];
        for (var index = 0; index < semantics.Length; index++)
        {
            if (!context.TryReadUInt32(shader.InputSemanticsAddress + (ulong)index * sizeof(uint), out semantics[index]))
            {
                error = "the vertex input semantics are unreadable";
                return false;
            }
        }

        metadata = new VertexTableMetadata
        {
            VertexBufferRegister = bufferRegister,
            VertexAttributeRegister = attributeRegister,
            InputSemantics = semantics,
        };
        error = string.Empty;
        return true;
    }

    // One attribute per semantic from the two tables; a known format replaces the buffer's.
    public static List<VertexAttributeResource> ApplySemantics(
        CpuContext context,
        ReadOnlySpan<uint> semantics,
        ulong attributeTable,
        ulong bufferTable,
        ulong shaderAddress) =>
        ApplySemantics(semantics, ReadTableWords(context, semantics, attributeTable, bufferTable, shaderAddress));

    public static VertexTableWords ReadTableWords(CpuContext context, ReadOnlySpan<uint> semantics, ulong attributeTable, ulong bufferTable,
        ulong shaderAddress)
    {
        if (semantics.Length > VertexInputInfo.MaxBuffers)
        {
            throw SubmissionScheduler.Fatal($"The vertex program has too many attributes: shader=0x{shaderAddress:X16} count={semantics.Length}.");
        }

        if (semantics.IsEmpty)
        {
            return new VertexTableWords();
        }

        var firstAttribute = uint.MaxValue;
        var lastAttribute = 0u;
        foreach (var word in semantics)
        {
            var semantic = new ShaderInputSemantic(word);
            if (semantic.StaticBufferIndex || semantic.StaticAttribute)
            {
                throw SubmissionScheduler.Fatal($"A static vertex attribute is not supported: shader=0x{shaderAddress:X16} semantic=0x{word:X8}.");
            }

            firstAttribute = Math.Min(firstAttribute, semantic.Semantic);
            lastAttribute = Math.Max(lastAttribute, semantic.Semantic);
        }

        var attributeCount = (int)(lastAttribute - firstAttribute) + 1;
        Span<uint> attributes = stackalloc uint[VertexTableWords.MaxAttributes];
        attributes = attributes[..attributeCount];
        var wholeSpans = context.Memory.TryRead(attributeTable + (ulong)firstAttribute * sizeof(uint), MemoryMarshal.AsBytes(attributes));
        var firstBuffer = uint.MaxValue;
        var lastBuffer = 0u;
        foreach (var word in semantics)
        {
            var semantic = new ShaderInputSemantic(word).Semantic;
            ref var attribute = ref attributes[(int)(semantic - firstAttribute)];
            var attributeAddress = attributeTable + (ulong)semantic * sizeof(uint);
            if (!wholeSpans && !context.TryReadUInt32(attributeAddress, out attribute))
            {
                throw SubmissionScheduler.Fatal($"The vertex attribute table is unreadable: shader=0x{shaderAddress:X16} address=0x{attributeAddress:X16}.");
            }

            firstBuffer = Math.Min(firstBuffer, attribute & 0x1Fu);
            lastBuffer = Math.Max(lastBuffer, attribute & 0x1Fu);
        }

        var bufferCount = (int)(lastBuffer - firstBuffer) + 1;
        var words = new uint[attributeCount + bufferCount * VertexTableWords.DescriptorWords];
        attributes.CopyTo(words);
        var buffers = words.AsSpan(attributeCount);
        if (!context.Memory.TryRead(bufferTable + (ulong)firstBuffer * 16, MemoryMarshal.AsBytes(buffers)))
        {
            wholeSpans = false;
            foreach (var word in semantics)
            {
                var index = attributes[(int)(new ShaderInputSemantic(word).Semantic - firstAttribute)] & 0x1Fu;
                var descriptorAddress = bufferTable + (ulong)index * 16;
                var descriptor = buffers.Slice((int)(index - firstBuffer) * VertexTableWords.DescriptorWords, VertexTableWords.DescriptorWords);
                for (var component = 0; component < descriptor.Length; component++)
                {
                    if (!context.TryReadUInt32(descriptorAddress + (ulong)component * sizeof(uint), out descriptor[component]))
                    {
                        throw SubmissionScheduler.Fatal($"The vertex buffer table is unreadable: shader=0x{shaderAddress:X16} address=0x{descriptorAddress:X16}.");
                    }
                }
            }
        }

        return new VertexTableWords
        {
            FirstAttribute = firstAttribute,
            AttributeCount = attributeCount,
            FirstBuffer = firstBuffer,
            BufferCount = bufferCount,
            Words = words,
            WholeSpans = wholeSpans,
        };
    }

    public static bool SameTableWords(CpuContext context, VertexTableWords kept, ulong attributeTable, ulong bufferTable, Span<uint> scratch)
    {
        if (!kept.WholeSpans)
        {
            return false;
        }

        if (kept.Words.Length == 0)
        {
            return true;
        }

        var current = scratch[..kept.Words.Length];
        var bytes = MemoryMarshal.AsBytes(current);
        var attributeBytes = kept.AttributeCount * sizeof(uint);
        return context.Memory.TryRead(attributeTable + (ulong)kept.FirstAttribute * sizeof(uint), bytes[..attributeBytes]) &&
            context.Memory.TryRead(bufferTable + (ulong)kept.FirstBuffer * 16, bytes[attributeBytes..]) &&
            current.SequenceEqual(kept.Words);
    }

    public static List<VertexAttributeResource> ApplySemantics(ReadOnlySpan<uint> semantics, VertexTableWords tables)
    {
        var resources = new List<VertexAttributeResource>(semantics.Length);
        foreach (var word in semantics)
        {
            var semantic = new ShaderInputSemantic(word);
            var attribute = tables.Words[(int)(semantic.Semantic - tables.FirstAttribute)];
            var index = attribute & 0x1Fu;
            var format = (attribute >> 5) & 0x1FFu;
            var offset = (attribute >> 14) & 0xFFFu;
            var fetchIndex = (attribute >> 26) & 0x1u;
            var descriptor = BufferDescriptorWords.From(tables.Words.AsSpan(
                tables.AttributeCount + (int)(index - tables.FirstBuffer) * VertexTableWords.DescriptorWords, VertexTableWords.DescriptorWords));
            if (format != 0)
            {
                var bufferFormat = format >> 2;
                var channels = (format & 0x3u) + 1u;
                descriptor = descriptor with
                {
                    Word3 = (descriptor.Word3 & ~((0x7Fu << 12) | 0xFFFu)) |
                            ((bufferFormat & 0x7Fu) << 12) |
                            DestinationSelectForChannels(channels),
                };
            }

            if (offset != 0)
            {
                descriptor = descriptor.WithAddress(descriptor.Address + offset);
            }

            resources.Add(new VertexAttributeResource(
                descriptor,
                (int)semantic.HardwareMapping,
                (int)semantic.SizeInElements,
                (int)semantic.Semantic,
                fetchIndex,
                BufferIndex: -1,
                OffsetBytes: 0));
        }

        return resources;
    }

    // Attributes with one stride and fetch rate whose bases lie within one record share a buffer.
    public static VertexInputBuffer[] MergeBuffers(List<VertexAttributeResource> resources, ulong shaderAddress)
    {
        const int StackResources = 64;
        Span<int> bufferOf = resources.Count <= StackResources ? stackalloc int[StackResources] : new int[resources.Count];
        Span<ulong> addresses = stackalloc ulong[VertexInputInfo.MaxBuffers];
        Span<uint> strides = stackalloc uint[VertexInputInfo.MaxBuffers];
        Span<uint> recordCounts = stackalloc uint[VertexInputInfo.MaxBuffers];
        Span<uint> fetchIndices = stackalloc uint[VertexInputInfo.MaxBuffers];
        Span<int> attributeCounts = stackalloc int[VertexInputInfo.MaxBuffers];
        var bufferCount = 0;
        for (var resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
        {
            var resource = resources[resourceIndex];
            var descriptor = resource.Descriptor;
            var merged = false;
            for (var bufferIndex = 0; bufferIndex < bufferCount; bufferIndex++)
            {
                ulong stride = strides[bufferIndex];
                if (stride != descriptor.Stride || fetchIndices[bufferIndex] != resource.FetchIndex)
                {
                    continue;
                }

                var resourceBase = descriptor.Address;
                var lowest = Math.Min(resourceBase, addresses[bufferIndex]);
                var offset1 = resourceBase - lowest;
                var offset2 = addresses[bufferIndex] - lowest;
                if (offset1 < stride && offset2 < stride)
                {
                    if (recordCounts[bufferIndex] != descriptor.RecordCount)
                    {
                        throw SubmissionScheduler.Fatal(
                            $"Two attributes of one vertex buffer disagree on the record count: shader=0x{shaderAddress:X16} records={recordCounts[bufferIndex]}/{descriptor.RecordCount}.");
                    }

                    addresses[bufferIndex] = lowest;
                    if (attributeCounts[bufferIndex] >= VertexInputInfo.MaxBuffers)
                    {
                        throw SubmissionScheduler.Fatal($"A vertex buffer has too many attributes: shader=0x{shaderAddress:X16}.");
                    }

                    attributeCounts[bufferIndex]++;
                    bufferOf[resourceIndex] = bufferIndex;
                    merged = true;
                    break;
                }
            }

            if (merged)
            {
                continue;
            }

            if (bufferCount >= VertexInputInfo.MaxBuffers)
            {
                throw SubmissionScheduler.Fatal($"The vertex program has too many buffers: shader=0x{shaderAddress:X16}.");
            }

            addresses[bufferCount] = descriptor.Address;
            strides[bufferCount] = descriptor.Stride;
            recordCounts[bufferCount] = descriptor.RecordCount;
            fetchIndices[bufferCount] = resource.FetchIndex;
            attributeCounts[bufferCount] = 1;
            bufferOf[resourceIndex] = bufferCount++;
        }

        var result = new VertexInputBuffer[bufferCount];
        for (var bufferIndex = 0; bufferIndex < bufferCount; bufferIndex++)
        {
            result[bufferIndex] = new VertexInputBuffer(addresses[bufferIndex], strides[bufferIndex], recordCounts[bufferIndex], fetchIndices[bufferIndex] != 0);
        }

        for (var resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
        {
            var resource = resources[resourceIndex];
            var bufferIndex = bufferOf[resourceIndex];
            resources[resourceIndex] = resource with
            {
                BufferIndex = bufferIndex,
                OffsetBytes = (uint)(resource.Descriptor.Address - addresses[bufferIndex]),
            };
        }

        return result;
    }
}
