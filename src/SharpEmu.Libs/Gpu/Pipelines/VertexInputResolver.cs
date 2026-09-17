// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

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
        uint positionExportControl = 0, ClipSpaceTransform clipSpace = default)
    {
        if (!TryReadTables(context, shader, GpuCommands.Registers.UserScalarRegisters.Capacity, out var metadata, out var error))
        {
            throw SubmissionScheduler.Fatal($"The vertex program header is invalid: shader=0x{shader.CodeAddress:X16} error={error}.");
        }

        var attributes = new List<VertexAttributeResource>();
        VertexInputBuffer[] buffers = [];
        if (metadata.VertexAttributeRegister >= 0)
        {
            var attributeTable = ReadTablePointer(userData, metadata.VertexAttributeRegister);
            var bufferTable = ReadTablePointer(userData, metadata.VertexBufferRegister);
            if (attributeTable == 0 || bufferTable == 0)
            {
                throw SubmissionScheduler.Fatal($"The vertex table pointer is null: shader=0x{shader.CodeAddress:X16} attributes=0x{attributeTable:X16} buffers=0x{bufferTable:X16}.");
            }

            attributes = ApplySemantics(context, metadata.InputSemantics, attributeTable, bufferTable, shader.CodeAddress);
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

    private static ulong ReadTablePointer(ReadOnlySpan<uint> userData, int register) =>
        register < 0 || register + 1 >= userData.Length ? 0 : userData[register] | ((ulong)userData[register + 1] << 32);

    private const ushort IllegalDirectOffset = 0xFFFF;
    private const uint DirectResourceVertexBufferTable = 8;
    private const uint DirectResourceVertexAttributeTable = 10;
    private const uint DirectResourceCount = DirectResourceVertexAttributeTable + 1;
    private const ulong DirectResourceOffsetField = 0x00;
    private const ulong DirectResourceCountField = 0x2C;
    private const uint IdentityDestinationSelect = 4 | (5 << 3) | (6 << 6) | (7 << 9);

    private sealed class ResolvedBuffer
    {
        public ulong Address;
        public uint Stride;
        public uint RecordCount;
        public uint FetchIndex;
        public List<int> AttributeIndices { get; } = [];
    }

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
        ulong shaderAddress)
    {
        var resources = new List<VertexAttributeResource>(semantics.Length);
        Span<uint> descriptorWords = stackalloc uint[4];
        foreach (var word in semantics)
        {
            var semantic = new ShaderInputSemantic(word);
            if (semantic.StaticBufferIndex || semantic.StaticAttribute)
            {
                throw SubmissionScheduler.Fatal($"A static vertex attribute is not supported: shader=0x{shaderAddress:X16} semantic=0x{word:X8}.");
            }

            var attributeAddress = attributeTable + semantic.Semantic * sizeof(uint);
            if (!context.TryReadUInt32(attributeAddress, out var attribute))
            {
                throw SubmissionScheduler.Fatal($"The vertex attribute table is unreadable: shader=0x{shaderAddress:X16} address=0x{attributeAddress:X16}.");
            }

            var index = attribute & 0x1Fu;
            var format = (attribute >> 5) & 0x1FFu;
            var offset = (attribute >> 14) & 0xFFFu;
            var fetchIndex = (attribute >> 26) & 0x1u;
            if (index >= VertexInputInfo.MaxBuffers)
            {
                throw SubmissionScheduler.Fatal($"The vertex attribute names a buffer outside the table: shader=0x{shaderAddress:X16} index={index}.");
            }

            if (resources.Count >= VertexInputInfo.MaxBuffers)
            {
                throw SubmissionScheduler.Fatal($"The vertex program has too many attributes: shader=0x{shaderAddress:X16} count={resources.Count + 1}.");
            }

            var descriptorAddress = bufferTable + index * 16u;
            for (var component = 0; component < 4; component++)
            {
                if (!context.TryReadUInt32(descriptorAddress + (ulong)component * sizeof(uint), out descriptorWords[component]))
                {
                    throw SubmissionScheduler.Fatal($"The vertex buffer table is unreadable: shader=0x{shaderAddress:X16} address=0x{descriptorAddress:X16}.");
                }
            }

            var descriptor = BufferDescriptorWords.From(descriptorWords);
            if (format != 0)
            {
                var bufferFormat = VertexAttributeFormat.ToBufferFormat(format);
                descriptor = descriptor with
                {
                    Word3 = (descriptor.Word3 & ~((0x7Fu << 12) | 0xFFFu)) | ((bufferFormat & 0x7Fu) << 12) | IdentityDestinationSelect,
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
        var buffers = new List<ResolvedBuffer>();
        for (var resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
        {
            var resource = resources[resourceIndex];
            var descriptor = resource.Descriptor;
            var merged = false;
            foreach (var buffer in buffers)
            {
                ulong stride = buffer.Stride;
                if (stride != descriptor.Stride || buffer.FetchIndex != resource.FetchIndex)
                {
                    continue;
                }

                var resourceBase = descriptor.Address;
                var lowest = Math.Min(resourceBase, buffer.Address);
                var offset1 = resourceBase - lowest;
                var offset2 = buffer.Address - lowest;
                if (offset1 < stride && offset2 < stride)
                {
                    if (buffer.RecordCount != descriptor.RecordCount)
                    {
                        throw SubmissionScheduler.Fatal(
                            $"Two attributes of one vertex buffer disagree on the record count: shader=0x{shaderAddress:X16} records={buffer.RecordCount}/{descriptor.RecordCount}.");
                    }

                    buffer.Address = lowest;
                    if (buffer.AttributeIndices.Count >= VertexInputInfo.MaxBuffers)
                    {
                        throw SubmissionScheduler.Fatal($"A vertex buffer has too many attributes: shader=0x{shaderAddress:X16}.");
                    }

                    buffer.AttributeIndices.Add(resourceIndex);
                    merged = true;
                    break;
                }
            }

            if (merged)
            {
                continue;
            }

            if (buffers.Count >= VertexInputInfo.MaxBuffers)
            {
                throw SubmissionScheduler.Fatal($"The vertex program has too many buffers: shader=0x{shaderAddress:X16}.");
            }

            var created = new ResolvedBuffer
            {
                Address = descriptor.Address,
                Stride = descriptor.Stride,
                RecordCount = descriptor.RecordCount,
                FetchIndex = resource.FetchIndex,
            };
            created.AttributeIndices.Add(resourceIndex);
            buffers.Add(created);
        }

        var result = new VertexInputBuffer[buffers.Count];
        for (var bufferIndex = 0; bufferIndex < buffers.Count; bufferIndex++)
        {
            var buffer = buffers[bufferIndex];
            result[bufferIndex] = new VertexInputBuffer(buffer.Address, buffer.Stride, buffer.RecordCount, buffer.FetchIndex != 0);
            foreach (var resourceIndex in buffer.AttributeIndices)
            {
                var resource = resources[resourceIndex];
                resources[resourceIndex] = resource with
                {
                    BufferIndex = bufferIndex,
                    OffsetBytes = (uint)(resource.Descriptor.Address - buffer.Address),
                };
            }
        }

        return result;
    }
}

// The vertex attribute formats of the guest tables and the buffer formats they select.
public static class VertexAttributeFormat
{
    private static readonly Dictionary<uint, uint> _bufferFormats = new()
    {
        [0] = 0,
        [4] = 1, [8] = 2, [12] = 3, [16] = 4, [20] = 5, [24] = 6,
        [28] = 7, [32] = 8, [36] = 9, [40] = 10, [44] = 11, [48] = 12, [52] = 13,
        [57] = 14, [61] = 15, [65] = 16, [69] = 17, [73] = 18, [77] = 19,
        [80] = 20, [84] = 21, [88] = 22,
        [93] = 23, [97] = 24, [101] = 25, [105] = 26, [109] = 27, [113] = 28, [117] = 29,
        [122] = 30, [126] = 31, [130] = 32, [134] = 33, [138] = 34, [142] = 35, [146] = 36,
        [150] = 37, [154] = 38, [158] = 39, [162] = 40, [166] = 41, [170] = 42, [174] = 43,
        [179] = 44, [183] = 45, [187] = 46, [191] = 47, [195] = 48, [199] = 49,
        [203] = 50, [207] = 51, [211] = 52, [215] = 53, [219] = 54, [223] = 55,
        [227] = 56, [231] = 57, [235] = 58, [239] = 59, [243] = 60, [247] = 61,
        [249] = 62, [253] = 63, [257] = 64,
        [263] = 65, [267] = 66, [271] = 67, [275] = 68, [279] = 69, [283] = 70, [287] = 71,
        [290] = 72, [294] = 73, [298] = 74,
        [303] = 75, [307] = 76, [311] = 77,
    };

    // An unknown attribute format is kept as the buffer format value it names.
    public static uint ToBufferFormat(uint attributeFormat) =>
        _bufferFormats.TryGetValue(attributeFormat, out var bufferFormat) ? bufferFormat : attributeFormat;
}
