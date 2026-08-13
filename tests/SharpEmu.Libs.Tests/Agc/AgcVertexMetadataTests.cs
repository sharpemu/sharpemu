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
    public void MergeVertexInputs_OverlaysLayoutWithoutRebasingCapture()
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
                OffsetBytes: 12,
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
    public void MergeVertexInputs_MetadataCorrectsStaleStride40()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        const ulong semanticsAddress = memoryBase + 0x100;
        const ulong attribTable = memoryBase + 0x200;
        const ulong bufferTable = memoryBase + 0x300;
        const ulong sharpBase = memoryBase + 0x800;

        WriteUInt32(memory, semanticsAddress, 0u | (0u << 8) | (4u << 16));
        WriteUInt32(memory, attribTable, 0u | (56u << 5) | (12u << 14));
        WriteUInt32(memory, bufferTable, (uint)(sharpBase & 0xFFFF_FFFFUL));
        WriteUInt32(memory, bufferTable + 4, (uint)(sharpBase >> 32) | (40u << 16));

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

        var data = new byte[160];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x40, 0, 4, 14, 7, sharpBase, 32, 12, data, data.Length, false),
        };

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            ctx,
            scalars,
            tables,
            discovered);

        Assert.Single(merged);
        Assert.Equal(40u, merged[0].Stride);
        Assert.Equal(12u, merged[0].OffsetBytes);
        Assert.Equal(sharpBase, merged[0].BaseAddress);
        Assert.Same(data, merged[0].Data);
        Assert.Equal(0x40u, merged[0].Pc);
    }

    [Fact]
    public void MergeVertexInputs_ConflictingMetadataOffsetDoesNotMoveBinding()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        const ulong semanticsAddress = memoryBase + 0x100;
        const ulong attribTable = memoryBase + 0x200;
        const ulong bufferTable = memoryBase + 0x300;
        const ulong sharpBase = memoryBase + 0x800;

        WriteUInt32(memory, semanticsAddress, 0u | (0u << 8) | (4u << 16));
        WriteUInt32(memory, attribTable, 0u | (56u << 5) | (12u << 14));
        WriteUInt32(memory, bufferTable, (uint)(sharpBase & 0xFFFF_FFFFUL));
        WriteUInt32(memory, bufferTable + 4, (uint)(sharpBase >> 32) | (40u << 16));

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

        var original = new Gen5VertexInputBinding(
            0x40, 0, 4, 14, 7, sharpBase, 32, 0, new byte[160], 160, false);
        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            ctx,
            scalars,
            tables,
            [original]);

        Assert.Same(original, Assert.Single(merged));
    }

    [Fact]
    public void MergeVertexInputs_UsesOffsetRelativeToCapturedBase()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        const ulong semanticsAddress = memoryBase + 0x100;
        const ulong attribTable = memoryBase + 0x200;
        const ulong bufferTable = memoryBase + 0x300;
        const ulong capturedBase = memoryBase + 0x7F8;
        const ulong sharpBase = memoryBase + 0x800;

        WriteUInt32(memory, semanticsAddress, 0u | (0u << 8) | (4u << 16));
        WriteUInt32(memory, attribTable, 0u | (56u << 5) | (12u << 14));
        WriteUInt32(memory, bufferTable, (uint)(sharpBase & 0xFFFF_FFFFUL));
        WriteUInt32(memory, bufferTable + 4, (uint)(sharpBase >> 32) | (40u << 16));

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

        var data = new byte[160];
        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            ctx,
            scalars,
            tables,
            [new Gen5VertexInputBinding(
                0x40, 0, 4, 14, 7, capturedBase, 32, 20, data, data.Length, false)]);

        Assert.Equal(40u, Assert.Single(merged).Stride);
        Assert.Equal(20u, merged[0].OffsetBytes);
        Assert.Equal(capturedBase, merged[0].BaseAddress);
        Assert.Same(data, merged[0].Data);
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
    public void MergeVertexInputs_UsesHardwareMappingWhenDiscoveryOrderIsReversed()
    {
        var fixture = CreateMetadataFixture(
            (HardwareMapping: 8u, Format: 29u, Offset: 0u),
            (HardwareMapping: 4u, Format: 56u, Offset: 12u));
        var firstData = new byte[64];
        var secondData = new byte[64];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x10, 0, 4, 14, 7, fixture.SharpBase, 16, 0,
                firstData, firstData.Length, false),
            new Gen5VertexInputBinding(
                0x20, 1, 4, 14, 7, fixture.SharpBase, 16, 0,
                secondData, secondData.Length, false),
        };
        var program = CreateVertexFetchProgram(
            (Pc: 0x10u, VectorData: 4u),
            (Pc: 0x20u, VectorData: 8u));

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            fixture.Context,
            fixture.Scalars,
            fixture.Tables,
            program,
            discovered);

        Assert.Equal(10u, merged[0].DataFormat);
        Assert.Equal(12u, merged[0].OffsetBytes);
        Assert.Equal(5u, merged[1].DataFormat);
        Assert.Equal(0u, merged[1].OffsetBytes);
        Assert.Equal(0u, merged[0].Location);
        Assert.Equal(1u, merged[1].Location);
        Assert.Equal(0x10u, merged[0].Pc);
        Assert.Equal(0x20u, merged[1].Pc);
        Assert.Same(firstData, merged[0].Data);
        Assert.Same(secondData, merged[1].Data);
    }

    [Fact]
    public void MergeVertexInputs_UsesSparseHardwareMappingsAndAliasPcs()
    {
        var fixture = CreateMetadataFixture(
            (HardwareMapping: 2u, Format: 29u, Offset: 0u),
            (HardwareMapping: 12u, Format: 29u, Offset: 8u),
            (HardwareMapping: 31u, Format: 56u, Offset: 12u));
        var data = new byte[64];
        var aliasPcs = new uint[] { 0x24 };
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x10, 7, 4, 14, 7, fixture.SharpBase, 16, 0,
                data, data.Length, false),
            new Gen5VertexInputBinding(
                0x20, 3, 4, 14, 7, fixture.SharpBase, 16, 0,
                data, data.Length, false, PerInstance: true, AliasPcs: aliasPcs),
        };
        var program = CreateVertexFetchProgram(
            (Pc: 0x10u, VectorData: 2u),
            (Pc: 0x20u, VectorData: 63u),
            (Pc: 0x24u, VectorData: 31u));

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            fixture.Context,
            fixture.Scalars,
            fixture.Tables,
            program,
            discovered);

        Assert.Equal(5u, merged[0].DataFormat);
        Assert.Equal(0u, merged[0].OffsetBytes);
        Assert.Equal(10u, merged[1].DataFormat);
        Assert.Equal(12u, merged[1].OffsetBytes);
        Assert.Equal(7u, merged[0].Location);
        Assert.Equal(3u, merged[1].Location);
        Assert.False(merged[1].PerInstance);
        Assert.Same(aliasPcs, merged[1].AliasPcs);
        Assert.Same(data, merged[1].Data);
    }

    [Fact]
    public void MergeVertexInputs_FallsBackToOffsetWhenHardwareMappingIsAmbiguous()
    {
        var fixture = CreateMetadataFixture(
            (HardwareMapping: 4u, Format: 29u, Offset: 0u),
            (HardwareMapping: 4u, Format: 56u, Offset: 12u));
        var data = new byte[64];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x10, 5, 4, 14, 7, fixture.SharpBase, 16, 12,
                data, data.Length, false),
        };
        var program = CreateVertexFetchProgram((Pc: 0x10u, VectorData: 4u));

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            fixture.Context,
            fixture.Scalars,
            fixture.Tables,
            program,
            discovered);

        Assert.Equal(10u, merged[0].DataFormat);
        Assert.Equal(0u, merged[0].NumberFormat);
        Assert.Equal(12u, merged[0].OffsetBytes);
        Assert.Equal(5u, merged[0].Location);
        Assert.Equal(0x10u, merged[0].Pc);
        Assert.Same(data, merged[0].Data);
    }

    [Fact]
    public void MergeVertexInputs_UsesOffsetMatchingWhenHardwareMappingIsMissing()
    {
        var fixture = CreateMetadataFixture(
            (HardwareMapping: 20u, Format: 29u, Offset: 0u),
            (HardwareMapping: 21u, Format: 56u, Offset: 12u));
        var data = new byte[64];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x10, 0, 4, 14, 7, fixture.SharpBase, 16, 12,
                data, data.Length, false),
            new Gen5VertexInputBinding(
                0x20, 1, 4, 14, 7, fixture.SharpBase, 16, 0,
                data, data.Length, false),
        };
        var program = CreateVertexFetchProgram(
            (Pc: 0x10u, VectorData: 4u),
            (Pc: 0x20u, VectorData: 8u));

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            fixture.Context,
            fixture.Scalars,
            fixture.Tables,
            program,
            discovered);

        // Location pairing would produce the reverse formats here. The
        // program-aware path must instead use the unambiguous byte offsets.
        Assert.Equal(10u, merged[0].DataFormat);
        Assert.Equal(12u, merged[0].OffsetBytes);
        Assert.Equal(5u, merged[1].DataFormat);
        Assert.Equal(0u, merged[1].OffsetBytes);
    }

    [Fact]
    public void MergeVertexInputs_PreservesBaseDeltaForMergedCapture()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x3000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        const ulong semanticsAddress = memoryBase + 0x100;
        const ulong attribTable = memoryBase + 0x200;
        const ulong bufferTable = memoryBase + 0x300;
        const ulong captureBase = memoryBase + 0x800;
        const ulong resourceBase = captureBase + 0x80;

        WriteUInt32(memory, semanticsAddress, 0u | (4u << 8) | (4u << 16));
        WriteUInt32(memory, attribTable, 1u | (56u << 5) | (12u << 14));
        WriteUInt32(memory, bufferTable + 16, (uint)(resourceBase & uint.MaxValue));
        WriteUInt32(memory, bufferTable + 20, (uint)(resourceBase >> 32) | (16u << 16));
        var scalars = new uint[32];
        scalars[4] = (uint)(attribTable & uint.MaxValue);
        scalars[5] = (uint)(attribTable >> 32);
        scalars[6] = (uint)(bufferTable & uint.MaxValue);
        scalars[7] = (uint)(bufferTable >> 32);
        var tables = new AgcVertexMetadata.VertexTableRegisters(
            VertexBufferReg: 6,
            VertexAttribReg: 4,
            InputSemanticsCount: 1,
            InputSemanticsAddress: semanticsAddress);
        var data = new byte[0x200];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x10, 0, 4, 14, 7, captureBase, 16, 0,
                data, data.Length, false),
        };

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            ctx,
            scalars,
            tables,
            CreateVertexFetchProgram((Pc: 0x10u, VectorData: 4u)),
            discovered);

        Assert.Equal(10u, merged[0].DataFormat);
        Assert.Equal(0x8Cu, merged[0].OffsetBytes);
        Assert.Equal(captureBase, merged[0].BaseAddress);
        Assert.Same(data, merged[0].Data);
    }

    [Fact]
    public void MergeVertexInputs_PropagatesPerInstanceFetchIndex()
    {
        var fixture = CreateMetadataFixture(
            perInstance: true,
            (HardwareMapping: 4u, Format: 56u, Offset: 0u));
        var data = new byte[64];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x10, 0, 4, 14, 7, fixture.SharpBase, 16, 0,
                data, data.Length, false, PerInstance: false),
        };

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            fixture.Context,
            fixture.Scalars,
            fixture.Tables,
            CreateVertexFetchProgram((Pc: 0x10u, VectorData: 4u)),
            discovered);

        Assert.True(merged[0].PerInstance);
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

    private static MetadataFixture CreateMetadataFixture(
        params (uint HardwareMapping, uint Format, uint Offset)[] entries) =>
        CreateMetadataFixture(perInstance: false, entries);

    private static MetadataFixture CreateMetadataFixture(
        bool perInstance,
        params (uint HardwareMapping, uint Format, uint Offset)[] entries)
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        const ulong semanticsAddress = memoryBase + 0x100;
        const ulong attribTable = memoryBase + 0x200;
        const ulong bufferTable = memoryBase + 0x300;
        const ulong sharpBase = memoryBase + 0x800;

        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            WriteUInt32(
                memory,
                semanticsAddress + (ulong)(index * sizeof(uint)),
                (uint)index | (entry.HardwareMapping << 8) | (4u << 16));
            WriteUInt32(
                memory,
                attribTable + (ulong)(index * sizeof(uint)),
                (entry.Format << 5) | (entry.Offset << 14) |
                (perInstance ? 1u << 26 : 0u));
        }

        WriteUInt32(memory, bufferTable, (uint)(sharpBase & uint.MaxValue));
        WriteUInt32(memory, bufferTable + 4, (uint)(sharpBase >> 32) | (16u << 16));
        var scalars = new uint[32];
        scalars[4] = (uint)(attribTable & uint.MaxValue);
        scalars[5] = (uint)(attribTable >> 32);
        scalars[6] = (uint)(bufferTable & uint.MaxValue);
        scalars[7] = (uint)(bufferTable >> 32);
        var tables = new AgcVertexMetadata.VertexTableRegisters(
            VertexBufferReg: 6,
            VertexAttribReg: 4,
            InputSemanticsCount: (uint)entries.Length,
            InputSemanticsAddress: semanticsAddress);
        return new MetadataFixture(ctx, scalars, tables, sharpBase);
    }

    private static Gen5ShaderProgram CreateVertexFetchProgram(
        params (uint Pc, uint VectorData)[] fetches)
    {
        var instructions = new List<Gen5ShaderInstruction>(fetches.Length + 1);
        foreach (var fetch in fetches)
        {
            instructions.Add(new Gen5ShaderInstruction(
                fetch.Pc,
                Gen5ShaderEncoding.Mubuf,
                "BufferLoadFormatXyzw",
                Words: [],
                Sources: [],
                Destinations: [],
                new Gen5BufferMemoryControl(
                    DwordCount: 4,
                    VectorAddress: 0,
                    VectorData: fetch.VectorData,
                    ScalarResource: 0,
                    OffsetBytes: 0,
                    IndexEnabled: true,
                    OffsetEnabled: false,
                    Glc: false,
                    Slc: false)));
        }

        instructions.Add(new Gen5ShaderInstruction(
            fetches.Length == 0 ? 0u : fetches.Max(static fetch => fetch.Pc) + 4u,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            Words: [],
            Sources: [],
            Destinations: [],
            Control: null));
        return new Gen5ShaderProgram(0, instructions);
    }

    private sealed record MetadataFixture(
        CpuContext Context,
        uint[] Scalars,
        AgcVertexMetadata.VertexTableRegisters Tables,
        ulong SharpBase);

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
