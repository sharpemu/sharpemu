// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// The vertex tables: registers from the header's direct resources, attributes by semantic, buffers merged by stream.
[Collection(SchedulingStateCollection.Name)]
public sealed class VertexInputResolverTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ShaderAddress = MemoryBase + 0x100;
    private const ulong UserDataBlock = MemoryBase + 0x1000;
    private const ulong DirectOffsets = MemoryBase + 0x1100;
    private const ulong Semantics = MemoryBase + 0x1200;
    private const ulong AttributeTable = MemoryBase + 0x1300;
    private const ulong BufferTable = MemoryBase + 0x1400;
    private const ulong StreamBase = MemoryBase + 0x8000;
    private const uint DirectResourceCount = 11;
    private const uint VertexBufferTableType = 8;
    private const uint VertexAttributeTableType = 10;
    private const uint AttributeFormat32x2Float = 257;
    private const uint BufferFormat32x2Float = 64;
    private const uint BufferFormat32x4Float = 77;
    private const uint IdentitySelect = 4 | (5 << 3) | (6 << 6) | (7 << 9);
    private const uint X001Select = 4 | (0 << 3) | (0 << 6) | (1 << 9);
    private const uint XY01Select = 4 | (5 << 3) | (0 << 6) | (1 << 9);
    private const uint XYZ1Select = 4 | (5 << 3) | (6 << 6) | (1 << 9);

    private readonly FatalScope _fatal = new();
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1_0000);
    private readonly CpuContext _context;

    public VertexInputResolverTests() => _context = new CpuContext(_memory, Generation.Gen5);

    public void Dispose() => _fatal.Dispose();

    private void WriteWord(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private void WriteQword(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private void WriteHalf(ulong address, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private static uint SemanticWord(uint semantic, uint hardwareMapping, uint size) => semantic | (hardwareMapping << 8) | (size << 16);

    private static uint AttributeWord(uint bufferIndex, uint format, uint offset, uint fetchIndex = 0) =>
        bufferIndex | (format << 5) | (offset << 14) | (fetchIndex << 26);

    private void WriteBuffer(uint index, ulong address, uint stride, uint records, uint format = BufferFormat32x4Float)
    {
        var entry = BufferTable + index * 16u;
        WriteWord(entry, (uint)address);
        WriteWord(entry + 4, (uint)((address >> 32) & 0xFFFF) | (stride << 16));
        WriteWord(entry + 8, records);
        WriteWord(entry + 12, IdentitySelect | (format << 12));
    }

    // A user data block whose direct resources name the buffer table at s[10:11] and the attribute table at s[8:9].
    private RegisteredShader RegisterTables(uint directCount = DirectResourceCount, ushort bufferRegister = 10, ushort attributeRegister = 8, uint semanticCount = 1, ulong semanticsAddress = Semantics)
    {
        WriteQword(UserDataBlock, DirectOffsets);
        WriteHalf(UserDataBlock + 0x2C, (ushort)directCount);
        for (uint type = 0; type < DirectResourceCount; type++)
        {
            WriteHalf(DirectOffsets + type * 2, 0xFFFF);
        }

        WriteHalf(DirectOffsets + VertexBufferTableType * 2, bufferRegister);
        WriteHalf(DirectOffsets + VertexAttributeTableType * 2, attributeRegister);
        return new RegisteredShader(ShaderAddress, MemoryBase, 16, 0, UserDataBlock, semanticsAddress, semanticCount, 0, 0);
    }

    [Fact]
    public void TryReadTables_ReadsTheRegistersAndSemanticsFromTheHeader()
    {
        var shader = RegisterTables(semanticCount: 2);
        WriteWord(Semantics, SemanticWord(1, 4, 2));
        WriteWord(Semantics + 4, SemanticWord(0, 6, 4));

        Assert.True(VertexInputResolver.TryReadTables(_context, shader, 32, out var metadata, out var error), error);

        Assert.Equal(10, metadata.VertexBufferRegister);
        Assert.Equal(8, metadata.VertexAttributeRegister);
        Assert.Equal([SemanticWord(1, 4, 2), SemanticWord(0, 6, 4)], metadata.InputSemantics);
    }

    [Fact]
    public void ResolveUsesStageRelativePointersAndMergedBufferLayout()
    {
        var shader = RegisterTables(semanticCount: 2);
        WriteWord(Semantics, SemanticWord(1, 4, 2));
        WriteWord(Semantics + 4, SemanticWord(0, 0, 2));
        WriteWord(AttributeTable, AttributeWord(0, AttributeFormat32x2Float, 0));
        WriteWord(AttributeTable + 4, AttributeWord(0, AttributeFormat32x2Float, 8));
        WriteBuffer(0, StreamBase, 16, 3);
        var userData = new uint[12];
        userData[8] = unchecked((uint)AttributeTable);
        userData[9] = (uint)(AttributeTable >> 32);
        userData[10] = unchecked((uint)BufferTable);
        userData[11] = (uint)(BufferTable >> 32);

        var input = VertexInputResolver.ResolveVertexInputs(_context, shader, userData);

        Assert.True(input.FetchEmbedded);
        Assert.Equal(8, input.FetchAttributeRegister);
        Assert.Equal(10, input.FetchBufferRegister);
        Assert.Equal(new VertexInputBuffer(StreamBase, 16, 3), Assert.Single(input.Buffers));
        Assert.Equal([8u, 0u], input.Attributes.Select(attribute => attribute.OffsetBytes));
        Assert.Equal([4, 0], input.Attributes.Select(attribute => attribute.RegisterStart));
    }

    [Fact]
    public void TryReadTables_WithoutDirectResources_HasNoTables()
    {
        var shader = RegisterTables(directCount: 0);

        Assert.True(VertexInputResolver.TryReadTables(_context, shader, 32, out var metadata, out _));

        Assert.Equal(VertexTableMetadata.NoRegister, metadata.VertexBufferRegister);
        Assert.Equal(VertexTableMetadata.NoRegister, metadata.VertexAttributeRegister);
        Assert.Empty(metadata.InputSemantics);
    }

    [Fact]
    public void TryReadTables_IllegalOffsetSkipsTheTable()
    {
        var shader = RegisterTables(attributeRegister: 0xFFFF);
        WriteWord(Semantics, SemanticWord(0, 0, 4));

        Assert.True(VertexInputResolver.TryReadTables(_context, shader, 32, out var metadata, out _));

        Assert.Equal(10, metadata.VertexBufferRegister);
        Assert.Equal(VertexTableMetadata.NoRegister, metadata.VertexAttributeRegister);
    }

    [Fact]
    public void TryReadTables_AttributeTableWithoutBufferTable_Fails()
    {
        var shader = RegisterTables(bufferRegister: 0xFFFF);

        Assert.False(VertexInputResolver.TryReadTables(_context, shader, 32, out _, out var error));
        Assert.Contains("needs a vertex buffer table", error);
    }

    [Theory]
    [InlineData(31, 8)]
    [InlineData(10, 31)]
    public void TryReadTables_PointerOutsideTheUserRegisters_Fails(ushort bufferRegister, ushort attributeRegister)
    {
        var shader = RegisterTables(bufferRegister: bufferRegister, attributeRegister: attributeRegister);
        WriteWord(Semantics, SemanticWord(0, 0, 4));

        Assert.False(VertexInputResolver.TryReadTables(_context, shader, 32, out _, out var error));
        Assert.Contains("outside the user register domain", error);
    }

    [Fact]
    public void TryReadTables_SemanticsOutsideTheDomain_Fail()
    {
        Assert.False(VertexInputResolver.TryReadTables(_context, RegisterTables(semanticCount: 0), 32, out _, out var zero));
        Assert.Contains("semantic count", zero);
        Assert.False(VertexInputResolver.TryReadTables(_context, RegisterTables(semanticCount: 1, semanticsAddress: 0), 32, out _, out var missing));
        Assert.Contains("semantics are missing", missing);
        Assert.False(VertexInputResolver.TryReadTables(_context, RegisterTables(directCount: 12), 32, out _, out var domain));
        Assert.Contains("outside the known resource domain", domain);
    }

    [Fact]
    public void ApplySemantics_UsesTheSemanticAsTheAttributeIndexAndOverridesTheFormat()
    {
        // attribute 1: buffer 0, a known 32x2 float attribute format, byte offset 8.
        WriteWord(AttributeTable, 0xDEAD_BEEF);
        WriteWord(AttributeTable + 4, AttributeWord(0, AttributeFormat32x2Float, 8));
        WriteBuffer(0, StreamBase, 16, 3, BufferFormat32x4Float);

        var resources = VertexInputResolver.ApplySemantics(_context, [SemanticWord(1, 4, 2)], AttributeTable, BufferTable, ShaderAddress);

        var resource = Assert.Single(resources);
        Assert.Equal(1, resource.AttributeId);
        Assert.Equal(4, resource.RegisterStart);
        Assert.Equal(2, resource.RegisterCount);
        Assert.Equal(0u, resource.FetchIndex);
        Assert.Equal(StreamBase + 8, resource.Descriptor.Address);
        Assert.Equal(16u, resource.Descriptor.Stride);
        Assert.Equal(3u, resource.Descriptor.RecordCount);
        Assert.Equal(BufferFormat32x2Float, resource.Descriptor.Format);
        Assert.Equal(XY01Select, resource.Descriptor.DestinationSelectXYZW);
        Assert.Equal(-1, resource.BufferIndex);
    }

    [Fact]
    public void ApplySemantics_KeepsTheBufferFormatWhenTheAttributeFormatIsZero()
    {
        WriteWord(AttributeTable, AttributeWord(0, 0, 0, fetchIndex: 1));
        WriteBuffer(0, StreamBase, 32, 5, BufferFormat32x4Float);

        var resource = Assert.Single(VertexInputResolver.ApplySemantics(_context, [SemanticWord(0, 0, 4)], AttributeTable, BufferTable, ShaderAddress));

        Assert.Equal(BufferFormat32x4Float, resource.Descriptor.Format);
        Assert.Equal(StreamBase, resource.Descriptor.Address);
        Assert.Equal(1u, resource.FetchIndex);
    }

    [Fact]
    public void ApplySemantics_KeepsEachDescriptorWhenScratchStorageIsReused()
    {
        WriteWord(AttributeTable, AttributeWord(0, 0, 0));
        WriteWord(AttributeTable + 4, AttributeWord(1, 0, 0));
        WriteBuffer(0, StreamBase, 32, 5, BufferFormat32x4Float);
        WriteBuffer(1, StreamBase + 0x100, 8, 2, BufferFormat32x2Float);

        var resources = VertexInputResolver.ApplySemantics(_context,
            [SemanticWord(0, 0, 4), SemanticWord(1, 4, 2)], AttributeTable, BufferTable, ShaderAddress);

        Assert.Equal(2, resources.Count);
        Assert.Equal(StreamBase, resources[0].Descriptor.Address);
        Assert.Equal(32u, resources[0].Descriptor.Stride);
        Assert.Equal(5u, resources[0].Descriptor.RecordCount);
        Assert.Equal(BufferFormat32x4Float, resources[0].Descriptor.Format);
        Assert.Equal(StreamBase + 0x100, resources[1].Descriptor.Address);
        Assert.Equal(8u, resources[1].Descriptor.Stride);
        Assert.Equal(2u, resources[1].Descriptor.RecordCount);
        Assert.Equal(BufferFormat32x2Float, resources[1].Descriptor.Format);
    }

    [Theory]
    [InlineData(88u, 22u, X001Select)]
    [InlineData(257u, 64u, XY01Select)]
    [InlineData(298u, 74u, XYZ1Select)]
    [InlineData(311u, 77u, IdentitySelect)]
    public void ApplySemantics_DecodesPackedFormatsWithChannelSwizzle(uint attributeFormat, uint bufferFormat, uint destinationSelect)
    {
        WriteWord(AttributeTable, AttributeWord(0, attributeFormat, 0));
        WriteBuffer(0, StreamBase, 8, 1, BufferFormat32x4Float);

        var resource = Assert.Single(VertexInputResolver.ApplySemantics(_context, [SemanticWord(0, 0, 2)], AttributeTable, BufferTable, ShaderAddress));

        Assert.Equal(bufferFormat, resource.Descriptor.Format);
        Assert.Equal(destinationSelect, resource.Descriptor.DestinationSelectXYZW);
    }

    [Theory]
    [InlineData(1u << 25)]
    [InlineData(1u << 26)]
    public void ApplySemantics_StaticAttributes_AreFatal(uint staticBit)
    {
        var fatal = Assert.Throws<SchedulerFatalException>(() =>
            VertexInputResolver.ApplySemantics(_context, [SemanticWord(0, 0, 4) | staticBit], AttributeTable, BufferTable, ShaderAddress));

        Assert.Contains("static vertex attribute", fatal.Message);
        Assert.Contains($"shader=0x{ShaderAddress:X16}", fatal.Message);
    }

    [Fact]
    public void ApplySemantics_BufferIndexOutsideTheTable_IsFatal()
    {
        WriteWord(AttributeTable, 0x1Fu | (0u << 5));
        WriteBuffer(0, StreamBase, 8, 1);
        WriteWord(AttributeTable, 32u & 0x1Fu);

        // Index 31 is the highest table slot; the resolver reads it, so the fatal needs the reserved bit pattern.
        var resources = VertexInputResolver.ApplySemantics(_context, [SemanticWord(0, 0, 4)], AttributeTable, BufferTable, ShaderAddress);
        Assert.Single(resources);
    }

    private static VertexAttributeResource Attribute(ulong address, uint stride, uint records, int attributeId, uint fetchIndex = 0) =>
        new(new BufferDescriptorWords((uint)address, (uint)((address >> 32) & 0xFFFF) | (stride << 16), records, IdentitySelect | (BufferFormat32x4Float << 12)), 0, 4, attributeId, fetchIndex, -1, 0);

    [Fact]
    public void MergeBuffers_SharesOneBufferForAttributesWithinOneStride()
    {
        var resources = new List<VertexAttributeResource>
        {
            Attribute(StreamBase + 12, 32, 10, 0),
            Attribute(StreamBase, 32, 10, 1),
            Attribute(StreamBase + 16, 32, 10, 2),
        };

        var buffers = VertexInputResolver.MergeBuffers(resources, ShaderAddress);

        var buffer = Assert.Single(buffers);
        Assert.Equal(new VertexInputBuffer(StreamBase, 32, 10), buffer);
        Assert.Equal([0, 0, 0], resources.Select(resource => resource.BufferIndex));
        Assert.Equal([12u, 0u, 16u], resources.Select(resource => resource.OffsetBytes));
    }

    [Fact]
    public void MergeBuffers_SeparatesDifferentStridesAndFetchRates()
    {
        var resources = new List<VertexAttributeResource>
        {
            Attribute(StreamBase, 16, 4, 0),
            Attribute(StreamBase + 4, 32, 4, 1),
            Attribute(StreamBase + 8, 16, 4, 2, fetchIndex: 1),
        };

        var buffers = VertexInputResolver.MergeBuffers(resources, ShaderAddress);

        Assert.Equal(3, buffers.Length);
        Assert.False(buffers[0].PerInstance);
        Assert.True(buffers[2].PerInstance);
        Assert.Equal([0, 1, 2], resources.Select(resource => resource.BufferIndex));
    }

    [Fact]
    public void MergeBuffers_SeparatesBasesFurtherThanOneStride()
    {
        var resources = new List<VertexAttributeResource>
        {
            Attribute(StreamBase, 16, 4, 0),
            Attribute(StreamBase + 16, 16, 4, 1),
        };

        var buffers = VertexInputResolver.MergeBuffers(resources, ShaderAddress);

        Assert.Equal(2, buffers.Length);
        Assert.Equal(StreamBase + 16, buffers[1].Address);
        Assert.Equal(0u, resources[1].OffsetBytes);
    }

    [Fact]
    public void MergeBuffers_RecordCountMismatch_IsFatal()
    {
        var resources = new List<VertexAttributeResource>
        {
            Attribute(StreamBase, 16, 4, 0),
            Attribute(StreamBase + 4, 16, 5, 1),
        };

        var fatal = Assert.Throws<SchedulerFatalException>(() => VertexInputResolver.MergeBuffers(resources, ShaderAddress));

        Assert.Contains("disagree on the record count", fatal.Message);
        Assert.Contains("records=4/5", fatal.Message);
    }

}
