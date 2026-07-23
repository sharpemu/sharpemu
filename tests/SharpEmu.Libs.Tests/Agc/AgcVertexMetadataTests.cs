// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

/// <summary>
/// Coverage for AGC attrib-table → BufferFormat merge and semantic indexing.
/// </summary>
public sealed class AgcVertexMetadataTests
{
    [Fact]
    public void BuildVertexResources_UsesSemanticNotHardwareMappingAsAttribIndex()
    {
        // input_semantics[0]: semantic=1, hardware_mapping=4, size=2
        // If hardware_mapping were wrongly used as the attrib index, we'd read
        // attrib[4] instead of attrib[1] and get the wrong format/offset.
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        const ulong semanticsAddress = memoryBase + 0x100;
        const ulong attribTable = memoryBase + 0x200;
        const ulong bufferTable = memoryBase + 0x300;
        const ulong sharpBase = memoryBase + 0x800;

        // ShaderSemantic word: semantic=1, hw_mapping=4, size_in_elements=2
        WriteUInt32(memory, semanticsAddress, 1u | (4u << 8) | (2u << 16));

        // attrib[0] unused garbage
        WriteUInt32(memory, attribTable, 0xDEAD_BEEFu);
        // attrib[1]: buffer=0, format=k16_16Float(29), offset=8, fetch=0
        WriteUInt32(memory, attribTable + 4, 0u | (29u << 5) | (8u << 14));

        // V# at buffer table[0]: base=sharpBase, stride=16
        WriteUInt32(memory, bufferTable, (uint)(sharpBase & 0xFFFF_FFFFUL));
        WriteUInt32(
            memory,
            bufferTable + 4,
            (uint)(sharpBase >> 32) | (16u << 16));

        var scalars = new uint[32];
        scalars[8] = (uint)(attribTable & 0xFFFF_FFFFUL);
        scalars[9] = (uint)(attribTable >> 32);
        scalars[10] = (uint)(bufferTable & 0xFFFF_FFFFUL);
        scalars[11] = (uint)(bufferTable >> 32);

        var tables = new AgcVertexMetadata.VertexTableRegisters(
            VertexBufferReg: 10,
            VertexAttribReg: 8,
            InputSemanticsCount: 1,
            InputSemanticsAddress: semanticsAddress);

        Assert.True(
            AgcVertexMetadata.TryBuildVertexResourcesFromMetadata(
                ctx,
                scalars,
                tables,
                out var resources));
        Assert.Single(resources);
        Assert.Equal(1u, resources[0].Semantic);
        Assert.Equal(4u, resources[0].HardwareMapping);
        Assert.Equal(8u, resources[0].OffsetBytes);
        Assert.Equal(5u, resources[0].DataFormat); // R16G16
        Assert.Equal(7u, resources[0].NumberFormat); // Float
        Assert.Equal(2u, resources[0].ComponentCount);
        Assert.Equal(sharpBase, resources[0].SharpBase);
        Assert.False(resources[0].PerInstance);
    }

    [Fact]
    public void MergeVertexInputs_OverlaysFormatWithoutRebasingCapture()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        const ulong semanticsAddress = memoryBase + 0x100;
        const ulong attribTable = memoryBase + 0x200;
        const ulong bufferTable = memoryBase + 0x300;
        const ulong sharpBase = memoryBase + 0x800;

        WriteUInt32(memory, semanticsAddress, 0u | (0u << 8) | (4u << 16));
        // format k8_8_8_8UNorm(56), offset=12
        WriteUInt32(memory, attribTable, 0u | (56u << 5) | (12u << 14));
        WriteUInt32(memory, bufferTable, (uint)(sharpBase & 0xFFFF_FFFFUL));
        WriteUInt32(memory, bufferTable + 4, (uint)(sharpBase >> 32) | (16u << 16));

        var scalars = new uint[32];
        scalars[4] = (uint)(attribTable & 0xFFFF_FFFFUL);
        scalars[5] = (uint)(attribTable >> 32);
        scalars[6] = (uint)(bufferTable & 0xFFFF_FFFFUL);
        scalars[7] = (uint)(bufferTable >> 32);

        var tables = new AgcVertexMetadata.VertexTableRegisters(
            VertexBufferReg: 6,
            VertexAttribReg: 4,
            InputSemanticsCount: 1,
            InputSemanticsAddress: semanticsAddress);

        var data = new byte[64];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                Pc: 0x40,
                Location: 0,
                ComponentCount: 4,
                DataFormat: 14, // wrong IR guess
                NumberFormat: 7,
                BaseAddress: sharpBase,
                Stride: 16,
                OffsetBytes: 0,
                Data: data,
                DataLength: data.Length,
                DataPooled: false),
        };

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            ctx,
            scalars,
            tables,
            discovered);
        Assert.Single(merged);
        Assert.Equal(0u, merged[0].Location);
        Assert.Equal(sharpBase, merged[0].BaseAddress);
        Assert.Same(data, merged[0].Data);
        Assert.Equal(10u, merged[0].DataFormat); // RGBA8
        Assert.Equal(0u, merged[0].NumberFormat); // Unorm
        Assert.Equal(12u, merged[0].OffsetBytes);
        Assert.Equal(0x40u, merged[0].Pc);
    }

    [Fact]
    public void MergeVertexInputs_AcceptsVertexAttribFormatEnums()
    {
        // Attrib tables store VertexAttribFormat (227 = rgba8 unorm), not
        // BufferFormat (56). Without conversion the format patch is a no-op.
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        const ulong semanticsAddress = memoryBase + 0x100;
        const ulong attribTable = memoryBase + 0x200;
        const ulong bufferTable = memoryBase + 0x300;
        const ulong sharpBase = memoryBase + 0x800;

        WriteUInt32(memory, semanticsAddress, 0u | (0u << 8) | (4u << 16));
        WriteUInt32(memory, attribTable, 0u | (227u << 5) | (12u << 14)); // VertexAttribFormat
        WriteUInt32(memory, bufferTable, (uint)(sharpBase & 0xFFFF_FFFFUL));
        WriteUInt32(memory, bufferTable + 4, (uint)(sharpBase >> 32) | (16u << 16));

        var scalars = new uint[32];
        scalars[4] = (uint)(attribTable & 0xFFFF_FFFFUL);
        scalars[5] = (uint)(attribTable >> 32);
        scalars[6] = (uint)(bufferTable & 0xFFFF_FFFFUL);
        scalars[7] = (uint)(bufferTable >> 32);

        var tables = new AgcVertexMetadata.VertexTableRegisters(
            VertexBufferReg: 6,
            VertexAttribReg: 4,
            InputSemanticsCount: 1,
            InputSemanticsAddress: semanticsAddress);

        var data = new byte[64];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x40, 0, 4, 14, 7, sharpBase, 16, 12, data, data.Length, false),
        };

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            ctx,
            scalars,
            tables,
            discovered);
        Assert.Equal(10u, merged[0].DataFormat);
        Assert.Equal(0u, merged[0].NumberFormat);
        Assert.Equal(12u, merged[0].OffsetBytes);
    }

    [Fact]
    public void MergeVertexInputs_MatchesInterleavedAttrsByOffsetNotBareBase()
    {
        // Both attributes share SharpBase. Matching by base alone would assign
        // the color format to position (video/UI regression).
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        const ulong semanticsAddress = memoryBase + 0x100;
        const ulong attribTable = memoryBase + 0x200;
        const ulong bufferTable = memoryBase + 0x300;
        const ulong sharpBase = memoryBase + 0x800;

        // semantic0 → pos float4 @0; semantic1 → color rgba8 @12
        WriteUInt32(memory, semanticsAddress, 0u | (0u << 8) | (4u << 16));
        WriteUInt32(memory, semanticsAddress + 4, 1u | (4u << 8) | (4u << 16));
        WriteUInt32(memory, attribTable, 0u | (77u << 5) | (0u << 14)); // k32_32_32_32Float
        WriteUInt32(memory, attribTable + 4, 0u | (56u << 5) | (12u << 14)); // rgba8unorm @12
        WriteUInt32(memory, bufferTable, (uint)(sharpBase & 0xFFFF_FFFFUL));
        WriteUInt32(memory, bufferTable + 4, (uint)(sharpBase >> 32) | (16u << 16));

        var scalars = new uint[32];
        scalars[4] = (uint)(attribTable & 0xFFFF_FFFFUL);
        scalars[5] = (uint)(attribTable >> 32);
        scalars[6] = (uint)(bufferTable & 0xFFFF_FFFFUL);
        scalars[7] = (uint)(bufferTable >> 32);

        var tables = new AgcVertexMetadata.VertexTableRegisters(
            VertexBufferReg: 6,
            VertexAttribReg: 4,
            InputSemanticsCount: 2,
            InputSemanticsAddress: semanticsAddress);

        var data = new byte[64];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x40, 0, 4, 14, 7, sharpBase, 16, 0, data, data.Length, false),
            new Gen5VertexInputBinding(
                0x80, 1, 4, 14, 7, sharpBase, 16, 12, data, data.Length, false),
        };

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            ctx,
            scalars,
            tables,
            discovered);
        Assert.Equal(2, merged.Count);
        Assert.Equal(0u, merged[0].OffsetBytes);
        Assert.Equal(12u, merged[1].OffsetBytes);
        Assert.Equal(0u, merged[1].NumberFormat); // Unorm color, not float
        Assert.Equal(10u, merged[1].DataFormat); // RGBA8
        Assert.Equal(sharpBase, merged[0].BaseAddress);
        Assert.Equal(sharpBase, merged[1].BaseAddress);
        Assert.Same(data, merged[0].Data);
    }

    [Fact]
    public void CollectFetchPrologPcs_FindsSBufferLoadsFromTableRegisters()
    {
        var tables = new AgcVertexMetadata.VertexTableRegisters(
            VertexBufferReg: 10,
            VertexAttribReg: 8,
            InputSemanticsCount: 1,
            InputSemanticsAddress: 1);

        var program = new Gen5ShaderProgram(
            0,
            [
                new Gen5ShaderInstruction(
                    0x10,
                    Gen5ShaderEncoding.Smem,
                    "SBufferLoadDword",
                    Words: [],
                    Sources: [Gen5Operand.Scalar(8)],
                    Destinations: [Gen5Operand.Scalar(20)],
                    new Gen5ScalarMemoryControl(1, 0, null)),
                new Gen5ShaderInstruction(
                    0x20,
                    Gen5ShaderEncoding.Smem,
                    "SBufferLoadDword",
                    Words: [],
                    Sources: [Gen5Operand.Scalar(12)],
                    Destinations: [Gen5Operand.Scalar(24)],
                    new Gen5ScalarMemoryControl(1, 0, null)),
                new Gen5ShaderInstruction(
                    0x30,
                    Gen5ShaderEncoding.Sopp,
                    "SEndpgm",
                    Words: [],
                    Sources: [],
                    Destinations: [],
                    null),
            ]);

        var pcs = AgcVertexMetadata.CollectFetchPrologPcs(program, tables);
        Assert.Contains(0x10u, pcs);
        Assert.DoesNotContain(0x20u, pcs);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
