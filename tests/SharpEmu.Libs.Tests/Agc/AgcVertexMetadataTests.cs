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

        // hardware_mapping 0 for the single semantic; associate the fetch's
        // destination VGPR with it so the merge has a validated association.
        var program = CreateVertexFetchProgram((Pc: 0x40u, VectorData: 0u));

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            ctx,
            scalars,
            tables,
            program,
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

        WriteUInt32(memory, semanticsAddress, 0u | (4u << 8));
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
            CreateVertexFetchProgram((Pc: 0x40u, VectorData: 4u)),
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

        WriteUInt32(memory, semanticsAddress, 0u | (4u << 8));
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
            CreateVertexFetchProgram((Pc: 0x40u, VectorData: 4u)),
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

        var program = CreateVertexFetchProgram((Pc: 0x40u, VectorData: 0u));

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            ctx,
            scalars,
            tables,
            program,
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

        // semantic0 → hardware_mapping 0, semantic1 → hardware_mapping 4.
        var program = CreateVertexFetchProgram(
            (Pc: 0x40u, VectorData: 0u),
            (Pc: 0x80u, VectorData: 4u));

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            ctx,
            scalars,
            tables,
            program,
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
                0x10, 0, 4, 14, 7, fixture.SharpBase, 16, 12,
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
                0x20, 3, 4, 14, 7, fixture.SharpBase, 16, 12,
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
    public void MergeVertexInputs_PreservesDiscoveryWhenHardwareMappingIsAmbiguous()
    {
        // Two resources claim the same hardware_mapping, so the destination
        // VGPR does not identify one of them. Falling back to a byte-offset
        // match would pick a resource the association never endorsed; there is
        // no basis for preferring either key, so discovery stands.
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

        Assert.Same(discovered, merged);
        Assert.Equal(14u, merged[0].DataFormat);
        Assert.Equal(7u, merged[0].NumberFormat);
        Assert.Equal(12u, merged[0].OffsetBytes);
    }

    [Fact]
    public void MergeVertexInputs_PreservesDiscoveryWhenHardwareMappingIsMissing()
    {
        // No metadata hardware_mapping matches either fetch destination VGPR,
        // so nothing associates. A byte-offset-only match is not a validated
        // association and must not rewrite the bindings.
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

        Assert.Same(discovered, merged);
        Assert.Equal(14u, merged[0].DataFormat);
        Assert.Equal(12u, merged[0].OffsetBytes);
        Assert.Equal(14u, merged[1].DataFormat);
        Assert.Equal(0u, merged[1].OffsetBytes);
    }

    [Fact]
    public void MergeVertexInputs_PreservesDiscoveryForDifferentMetadataAddress()
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

        Assert.Same(discovered, merged);
        Assert.Equal(14u, merged[0].DataFormat);
        Assert.Equal(0u, merged[0].OffsetBytes);
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

    [Fact]
    public void MergeVertexInputs_RefinesFiveResourceInterleavedStream()
    {
        // The HOA character draw: five attributes in one stride-60 stream,
        // hardware_mapping laid out as a contiguous VGPR allocation
        // (9, 13, 17, 20, 24) whose spacings match the component counts.
        var fixture = CreateMetadataFixture(
            (HardwareMapping: 9u, Format: 74u, Offset: 0u),   // k32_32_32Float
            (HardwareMapping: 13u, Format: 77u, Offset: 24u), // k32_32_32_32Float
            (HardwareMapping: 17u, Format: 74u, Offset: 12u), // k32_32_32Float
            (HardwareMapping: 20u, Format: 77u, Offset: 44u), // k32_32_32_32Float
            (HardwareMapping: 24u, Format: 56u, Offset: 40u)); // k8_8_8_8UNorm
        var data = new byte[256];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x178, 0, 4, 10, 0, fixture.SharpBase, 60, 40, data, data.Length, false),
            new Gen5VertexInputBinding(
                0x1E0, 1, 3, 13, 7, fixture.SharpBase, 60, 0, data, data.Length, false),
            new Gen5VertexInputBinding(
                0x258, 2, 4, 14, 7, fixture.SharpBase, 60, 44, data, data.Length, false),
            new Gen5VertexInputBinding(
                0x2CC, 3, 3, 13, 7, fixture.SharpBase, 60, 12, data, data.Length, false),
            new Gen5VertexInputBinding(
                0x33C, 4, 4, 14, 7, fixture.SharpBase, 60, 24, data, data.Length, false),
        };
        var program = CreateVertexFetchProgram(
            (Pc: 0x178u, VectorData: 24u),
            (Pc: 0x1E0u, VectorData: 9u),
            (Pc: 0x258u, VectorData: 20u),
            (Pc: 0x2CCu, VectorData: 17u),
            (Pc: 0x33Cu, VectorData: 13u));

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            fixture.Context,
            fixture.Scalars,
            fixture.Tables,
            program,
            discovered);

        // Every association agrees with its byte offset, so all five refine and
        // none has its offset moved.
        Assert.Equal(5, merged.Count);
        Assert.Equal((10u, 0u, 40u), (merged[0].DataFormat, merged[0].NumberFormat, merged[0].OffsetBytes));
        Assert.Equal((13u, 7u, 0u), (merged[1].DataFormat, merged[1].NumberFormat, merged[1].OffsetBytes));
        Assert.Equal((14u, 7u, 44u), (merged[2].DataFormat, merged[2].NumberFormat, merged[2].OffsetBytes));
        Assert.Equal((13u, 7u, 12u), (merged[3].DataFormat, merged[3].NumberFormat, merged[3].OffsetBytes));
        Assert.Equal((14u, 7u, 24u), (merged[4].DataFormat, merged[4].NumberFormat, merged[4].OffsetBytes));
        for (var index = 0; index < merged.Count; index++)
        {
            Assert.Equal(discovered[index].Pc, merged[index].Pc);
            Assert.Equal(discovered[index].Location, merged[index].Location);
            Assert.Same(data, merged[index].Data);
        }
    }

    [Fact]
    public void MergeVertexInputs_PreservesDiscoveryWhenHardwareAndOffsetKeysConflict()
    {
        // The hardware_mapping association points at the resource 24 bytes into
        // the stream while discovery resolved a definite offset of 12. Applying
        // the association would silently move the attribute; preserve instead.
        var fixture = CreateMetadataFixture(
            (HardwareMapping: 13u, Format: 77u, Offset: 24u),
            (HardwareMapping: 17u, Format: 74u, Offset: 12u));
        var data = new byte[128];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x2CC, 0, 3, 13, 7, fixture.SharpBase, 60, 12, data, data.Length, false),
        };
        var program = CreateVertexFetchProgram((Pc: 0x2CCu, VectorData: 13u));

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            fixture.Context,
            fixture.Scalars,
            fixture.Tables,
            program,
            discovered);

        Assert.Same(discovered, merged);
        Assert.Equal(12u, merged[0].OffsetBytes);
        Assert.Equal(13u, merged[0].DataFormat);
        Assert.Equal(7u, merged[0].NumberFormat);
    }

    [Fact]
    public void MergeVertexInputs_PreservesDiscoveryWhenMetadataFormatIsUnknown()
    {
        // 500 is neither a VertexAttribFormat nor a BufferFormat. The old
        // fallback turned it into R32G32B32A32_SFLOAT, widening float3
        // attributes to float4 and reading past the end of each one.
        var fixture = CreateMetadataFixture(
            (HardwareMapping: 4u, Format: 500u, Offset: 12u));
        var data = new byte[64];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x10, 0, 3, 13, 7, fixture.SharpBase, 16, 12,
                data, data.Length, false),
        };
        var program = CreateVertexFetchProgram((Pc: 0x10u, VectorData: 4u));

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            fixture.Context,
            fixture.Scalars,
            fixture.Tables,
            program,
            discovered);

        Assert.Same(discovered, merged);
        Assert.Equal(13u, merged[0].DataFormat);
        Assert.Equal(7u, merged[0].NumberFormat);
        Assert.Equal(3u, merged[0].ComponentCount);
    }

    [Fact]
    public void MergeVertexInputs_InvalidFormatPreservesFormatAndAppliesOtherMetadata()
    {
        // Format 0 is the SDK kInvalid sentinel. It does not override the
        // shader-discovered format or offset. An exact association can still
        // refine the input rate.
        var fixture = CreateMetadataFixture(
            perInstance: true,
            (HardwareMapping: 4u, Format: 0u, Offset: 0u));
        var data = new byte[64];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x10, 0, 3, 13, 7, fixture.SharpBase, 16, 0,
                data, data.Length, false),
        };
        var program = CreateVertexFetchProgram((Pc: 0x10u, VectorData: 4u));

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            fixture.Context,
            fixture.Scalars,
            fixture.Tables,
            program,
            discovered);

        Assert.NotSame(discovered, merged);
        Assert.Equal(13u, merged[0].DataFormat);
        Assert.Equal(7u, merged[0].NumberFormat);
        Assert.Equal(3u, merged[0].ComponentCount);
        Assert.Equal(0u, merged[0].OffsetBytes);
        Assert.True(merged[0].PerInstance);
    }

    [Fact]
    public void MergeVertexInputs_InvalidFormatCannotMoveDiscoveredAttribute()
    {
        // An inlined prolog can write a fetch result to a VGPR that another
        // semantic names as hardware_mapping. A mapping match alone must not
        // move the attribute from byte 0 to byte 8.
        var fixture = CreateMetadataFixture(
            perInstance: true,
            (HardwareMapping: 13u, Format: 0u, Offset: 8u));
        var data = new byte[64];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x10, 0, 3, 12, 5, fixture.SharpBase, 40, 0,
                data, data.Length, false),
        };

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            fixture.Context,
            fixture.Scalars,
            fixture.Tables,
            CreateVertexFetchProgram((Pc: 0x10u, VectorData: 13u)),
            discovered);

        Assert.Same(discovered, merged);
        Assert.Equal(0u, merged[0].OffsetBytes);
        Assert.Equal(12u, merged[0].DataFormat);
        Assert.False(merged[0].PerInstance);
    }

    [Fact]
    public void MergeVertexInputs_UsesCompleteFormatZeroTableForLocations()
    {
        var fixture = CreateMetadataFixture(
            (HardwareMapping: 20u, Format: 0u, Offset: 0u),
            (HardwareMapping: 21u, Format: 0u, Offset: 12u));
        var data = new byte[64];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x10, 7, 4, 10, 0, fixture.SharpBase, 16, 12,
                data, data.Length, false),
            new Gen5VertexInputBinding(
                0x20, 3, 3, 13, 7, fixture.SharpBase, 16, 0,
                data, data.Length, false),
        };

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            fixture.Context,
            fixture.Scalars,
            fixture.Tables,
            CreateVertexFetchProgram(
                (Pc: 0x10u, VectorData: 4u),
                (Pc: 0x20u, VectorData: 8u)),
            discovered);

        Assert.Equal(new uint[] { 1, 0 }, merged.Select(input => input.Location));
        Assert.Equal(new uint[] { 12, 0 }, merged.Select(input => input.OffsetBytes));
        Assert.Equal(new uint[] { 10, 13 }, merged.Select(input => input.DataFormat));
    }

    [Fact]
    public void MergeVertexInputs_DoesNotRemapIncompleteFormatZeroTable()
    {
        var fixture = CreateMetadataFixture(
            (HardwareMapping: 20u, Format: 0u, Offset: 0u));
        var data = new byte[64];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x10, 5, 4, 10, 0, fixture.SharpBase, 16, 0,
                data, data.Length, false),
            new Gen5VertexInputBinding(
                0x20, 0, 2, 13, 7, fixture.SharpBase + 0x100, 4, 0,
                data, data.Length, false),
        };

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            fixture.Context,
            fixture.Scalars,
            fixture.Tables,
            CreateVertexFetchProgram(
                (Pc: 0x10u, VectorData: 4u),
                (Pc: 0x20u, VectorData: 8u)),
            discovered);

        Assert.Same(discovered, merged);
        Assert.Equal(new uint[] { 5, 0 }, merged.Select(input => input.Location));
    }

    [Theory]
    [InlineData(113u, 5u, 5u, 2u)]  // k16_16SInt — was overridden to RGBA32F
    [InlineData(117u, 5u, 7u, 2u)]  // k16_16Float — 121 was never this value
    [InlineData(227u, 10u, 0u, 4u)] // k8_8_8_8UNorm
    [InlineData(298u, 13u, 7u, 3u)] // k32_32_32Float
    [InlineData(311u, 14u, 7u, 4u)] // k32_32_32_32Float
    public void MergeVertexInputs_MapsSdkVertexAttribFormats(
        uint attribFormat,
        uint expectedDataFormat,
        uint expectedNumberFormat,
        uint expectedComponents)
    {
        var fixture = CreateMetadataFixture(
            (HardwareMapping: 4u, Format: attribFormat, Offset: 0u));
        var data = new byte[64];
        var discovered = new[]
        {
            new Gen5VertexInputBinding(
                0x10, 0, 4, 1, 1, fixture.SharpBase, 16, 0,
                data, data.Length, false),
        };
        var program = CreateVertexFetchProgram((Pc: 0x10u, VectorData: 4u));

        var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
            fixture.Context,
            fixture.Scalars,
            fixture.Tables,
            program,
            discovered);

        Assert.Equal(expectedDataFormat, merged[0].DataFormat);
        Assert.Equal(expectedNumberFormat, merged[0].NumberFormat);
        Assert.Equal(expectedComponents, merged[0].ComponentCount);
    }

    [Fact]
    public void VertexTableRegisters_AddNggUserDataScalarBase()
    {
        var registers = new AgcVertexMetadata.VertexTableRegisters(
            VertexBufferReg: 26,
            VertexAttribReg: 28,
            InputSemanticsCount: 5,
            InputSemanticsAddress: 0x1234);

        var resolved = AgcVertexMetadata.AddUserDataScalarRegisterBase(
            registers,
            userDataScalarRegisterBase: 8);

        Assert.Equal(34, resolved.VertexBufferReg);
        Assert.Equal(36, resolved.VertexAttribReg);
        Assert.Equal(registers.InputSemanticsCount, resolved.InputSemanticsCount);
        Assert.Equal(registers.InputSemanticsAddress, resolved.InputSemanticsAddress);
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
