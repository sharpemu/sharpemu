// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

[Collection(SchedulingStateCollection.Name)]
public sealed class VertexInputBufferSizeTests : IDisposable
{
    private const ulong Address = 0x1_0000_0000;
    private const uint IdentitySelect = 4 | (5 << 3) | (6 << 6) | (7 << 9);
    private readonly FatalScope _fatal = new();

    public void Dispose() => _fatal.Dispose();

    [Theory]
    [InlineData(1u, 1u)]
    [InlineData(7u, 2u)]
    [InlineData(14u, 2u)]
    [InlineData(22u, 4u)]
    [InlineData(29u, 4u)]
    [InlineData(50u, 4u)]
    [InlineData(64u, 8u)]
    [InlineData(71u, 8u)]
    [InlineData(74u, 12u)]
    [InlineData(77u, 16u)]
    [InlineData(113u, 16u)]
    [InlineData(121u, 4u)]
    public void DescriptorFormatByteSize_UsesTheCompleteGuestFormat(uint format, uint expected)
    {
        Assert.Equal(expected, Descriptor(format, outOfBounds: 2).FormatByteSize);
    }

    [Theory]
    [InlineData(77u, 4u, 20ul)]
    [InlineData(113u, 8u, 24ul)]
    [InlineData(121u, 12u, 16ul)]
    public void ZeroStrideOobSelectTwo_UsesTheCompleteAttributeExtent(uint format, uint offset, ulong expected)
    {
        var input = Input(
            new VertexInputBuffer(Address, 0, 1),
            Attribute(format, outOfBounds: 2, offset));

        Assert.Equal(expected, input.BufferSize(0));
    }

    [Fact]
    public void ZeroStride_UsesTheLargestAttributeExtent()
    {
        var input = Input(
            new VertexInputBuffer(Address, 0, 1),
            Attribute(22, outOfBounds: 2, offset: 20),
            Attribute(77, outOfBounds: 2, offset: 4));

        Assert.Equal(24ul, input.BufferSize(0));
    }

    [Theory]
    [InlineData(0u, 2u, 0ul)]
    [InlineData(7u, 0u, 7ul)]
    [InlineData(7u, 1u, 7ul)]
    public void ZeroStrideWithoutEnabledOobSelectTwo_KeepsTheRecordFootprint(uint records, uint outOfBounds, ulong expected)
    {
        var input = Input(
            new VertexInputBuffer(Address, 0, records),
            Attribute(77, outOfBounds, offset: 8));

        Assert.Equal(expected, input.BufferSize(0));
    }

    [Fact]
    public void NonZeroStride_KeepsTheRecordFootprint()
    {
        var input = Input(
            new VertexInputBuffer(Address, 32, 7),
            Attribute(77, outOfBounds: 2, offset: 64));

        Assert.Equal(224ul, input.BufferSize(0));
    }

    [Fact]
    public void ZeroStrideOobSelectTwo_WithUnknownFormatFailsClosed()
    {
        var input = Input(
            new VertexInputBuffer(Address, 0, 1),
            Attribute(0, outOfBounds: 2, offset: 0));

        var fatal = Assert.Throws<SchedulerFatalException>(() => input.BufferSize(0));
        Assert.Contains("unknown format", fatal.Message);
    }

    private static VertexInputInfo Input(VertexInputBuffer buffer, params VertexAttributeResource[] attributes) =>
        new()
        {
            Buffers = [buffer],
            Attributes = attributes,
        };

    private static VertexAttributeResource Attribute(uint format, uint outOfBounds, uint offset)
    {
        return new VertexAttributeResource(Descriptor(format, outOfBounds), 0, 4, 0, 0, 0, offset);
    }

    private static BufferDescriptorWords Descriptor(uint format, uint outOfBounds)
    {
        return new BufferDescriptorWords(
            unchecked((uint)Address),
            (uint)(Address >> 32),
            1,
            IdentitySelect | (format << 12) | (outOfBounds << 28));
    }
}
