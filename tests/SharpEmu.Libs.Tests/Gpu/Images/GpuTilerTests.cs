// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// One tile transfer case with its linear bytes and the tiled bytes the CPU twin produces.
internal sealed record TilerCase(TileTransfer Transfer, TileBlockLayout Block, byte[] Linear, byte[] Tiled, string Name)
{
    public uint PitchBytes => Transfer.Pitch * Transfer.BytesPerElement;

    public uint SliceBytes => PitchBytes * Transfer.Height;

    public uint Columns => (Transfer.TiledWidth + Block.BlockWidth - 1) / Block.BlockWidth;

    public uint Rows => (Transfer.TiledHeight + Block.BlockHeight - 1) / Block.BlockHeight;

    public TileTransferArguments Arguments(uint sourceBase, uint destinationBase) => new()
    {
        SourceBase = sourceBase,
        DestinationBase = destinationBase,
        Width = Transfer.Width,
        Height = Transfer.Height,
        Depth = Transfer.Depth,
        SurfaceZ = Transfer.SurfaceZ,
        PitchBytes = PitchBytes,
        SliceBytes = SliceBytes,
        BlocksPerRow = Columns,
        BlocksPerSlice = Columns * Rows,
        TailX = Transfer.TailX,
        TailY = Transfer.TailY,
        Tail = Transfer.Tail ? 1u : 0u,
    };
}

internal static class TilerCases
{
    public enum Shape
    {
        Blocks,
        Tail,
        Layered,
    }

    private static uint AlignUp(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;

    // Null when the kind and size cannot form the requested shape.
    public static TilerCase? Create(TileBlockKind kind, uint bytes, Shape shape, uint seed)
    {
        if (!TileGeometry.TryGetBlockLayout(kind, bytes, out var block))
        {
            return null;
        }

        var transfer = new TileTransfer { Kind = kind, BytesPerElement = bytes };
        switch (shape)
        {
            case Shape.Blocks:
                transfer.Width = block.BlockWidth + 3;
                transfer.Height = block.BlockHeight + 5;
                transfer.Depth = block.BlockDepth == 1 ? 1u : block.BlockDepth + 2;
                break;
            case Shape.Tail:
                if (kind == TileBlockKind.Standard256B)
                {
                    return null;
                }

                transfer.Width = Math.Min(5, block.BlockWidth / 2);
                transfer.Height = Math.Min(6, block.BlockHeight / 2);
                transfer.Depth = block.BlockDepth == 1 ? 1u : 2u;
                transfer.TailX = block.BlockWidth / 2;
                transfer.TailY = block.BlockHeight / 4;
                transfer.Tail = true;
                break;
            case Shape.Layered:
                if (kind is not (TileBlockKind.RenderTarget64KB or TileBlockKind.Depth64KB))
                {
                    return null;
                }

                transfer.Width = block.BlockWidth + 1;
                transfer.Height = 9;
                transfer.Depth = 1;
                transfer.SurfaceZ = 3;
                break;
        }

        transfer.Pitch = transfer.Width + 2;
        transfer.TiledWidth = transfer.Tail ? block.BlockWidth : AlignUp(transfer.Width, block.BlockWidth);
        transfer.TiledHeight = transfer.Tail ? block.BlockHeight : AlignUp(transfer.Height, block.BlockHeight);
        var columns = transfer.TiledWidth / block.BlockWidth;
        var rows = transfer.TiledHeight / block.BlockHeight;
        var slabs = (transfer.Depth + block.BlockDepth - 1) / block.BlockDepth;
        transfer.TiledSize = transfer.Tail ? block.BlockSize : (ulong)columns * rows * slabs * block.BlockSize;
        transfer.LinearSize = (ulong)transfer.Pitch * bytes * transfer.Height * transfer.Depth;
        transfer.LinearOffset = 0;
        transfer.TiledOffset = 0;

        var linear = ImageTestHarness.Pattern((int)transfer.LinearSize, seed);
        var tiled = new byte[transfer.TiledSize];
        var pitchBytes = transfer.Pitch * bytes;
        var sliceBytes = pitchBytes * transfer.Height;
        for (uint z = 0; z < transfer.Depth; z++)
        {
            for (uint y = 0; y < transfer.Height; y++)
            {
                for (uint x = 0; x < transfer.Width; x++)
                {
                    uint blockX = 0, blockY = 0, blockZ = 0, elementX, elementY;
                    if (transfer.Tail)
                    {
                        elementX = x + transfer.TailX;
                        elementY = y + transfer.TailY;
                    }
                    else
                    {
                        blockX = x / block.BlockWidth;
                        blockY = y / block.BlockHeight;
                        blockZ = z / block.BlockDepth;
                        elementX = x % block.BlockWidth;
                        elementY = y % block.BlockHeight;
                    }

                    var elementZ = (z + transfer.SurfaceZ) % block.BlockDepth;
                    var blockIndex = blockZ * columns * rows + blockY * columns + blockX;
                    if (transfer.SurfaceZ != 0)
                    {
                        // Thin blocks fold the surface layer into the offset; the CPU twin has no layer input.
                        tiled = Array.Empty<byte>();
                        break;
                    }

                    if (!TileGeometry.TryGetBlockOffset(block, elementX, elementY, elementZ, out var offset) ||
                        !TileGeometry.TryGetBlockXor(block, blockX, blockY, out var blockXor))
                    {
                        throw new InvalidOperationException($"No block offset for {kind} bytes={bytes} at ({elementX},{elementY},{elementZ}).");
                    }

                    var tiledIndex = (int)(blockIndex * block.BlockSize + (offset ^ blockXor));
                    var linearIndex = (int)(z * sliceBytes + y * pitchBytes + x * bytes);
                    Array.Copy(linear, linearIndex, tiled, tiledIndex, (int)bytes);
                }

                if (tiled.Length == 0)
                {
                    break;
                }
            }

            if (tiled.Length == 0)
            {
                break;
            }
        }

        return new TilerCase(transfer, block, linear, tiled, $"{kind}/{bytes}/{shape}");
    }

    public static IEnumerable<TilerCase> All()
    {
        uint seed = 1;
        foreach (var kind in Enum.GetValues<TileBlockKind>())
        {
            for (uint bytes = 1; bytes <= 16; bytes *= 2)
            {
                foreach (var shape in Enum.GetValues<Shape>())
                {
                    if (Create(kind, bytes, shape, seed++) is { } created)
                    {
                        yield return created;
                    }
                }
            }
        }
    }
}

[Collection(SchedulingStateCollection.Name)]
public sealed class GpuTilerTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public GpuTilerTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    [Fact]
    public void ConversionRows_HonorTheDescriptorAndDispatchLimits()
    {
        const ulong limit = 128UL << 20;
        Assert.Equal(4096u, GpuTiler.ConversionRows(0, 32UL << 10, 32UL << 10, 4096, 256, limit, 65535));
        Assert.Equal(4096u, GpuTiler.ConversionRows(0, 32UL << 10, 32UL << 10, 4097, 256, limit, 65535));
        Assert.Equal(4095u, GpuTiler.ConversionRows(257, 32UL << 10, 32UL << 10, 4097, 256, limit, 65535));
        Assert.Equal(0u, GpuTiler.ConversionRows(0, 0, 16, 4, 256, limit, 65535));
        Assert.Equal(0u, GpuTiler.ConversionRows(0, 16, 16, 4, 256, 8, 65535));
        Assert.Equal(3u, GpuTiler.ConversionRows(0, 16, 16, 4, 256, limit, 3));
    }

    [Fact]
    public void CpuTwinCases_CoverEveryKindAndElementSize()
    {
        var cases = TilerCases.All().ToList();
        Assert.Equal(44 + 39 + 9, cases.Count);
        Assert.All(cases, tilerCase => Assert.True(tilerCase.Transfer.SurfaceZ != 0 || tilerCase.Tiled.Length == (int)tilerCase.Transfer.TiledSize));
    }

    [Fact]
    public void DetileAndTile_MatchTheCpuTwinAndInvertEachOther()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var failures = new List<string>();
        foreach (var tilerCase in TilerCases.All())
        {
            var transfer = tilerCase.Transfer;
            var transfers = new[] { transfer };
            var linearSize = transfer.LinearSize;
            var tiledSize = transfer.TiledSize;

            var tiledOutput = harness.CreateDeviceLocalBuffer(tiledSize);
            var linearInput = harness.Upload(tilerCase.Linear, linearSize);
            harness.Run(() =>
            {
                tiledOutput.Fill(0, tiledOutput.Size, 0);
                harness.Tiler.Tile(linearInput.Handle, 0, linearSize, tiledOutput.Handle, 0, tiledSize, transfers);
            });
            var tiledBytes = harness.ReadBack(tiledOutput.Handle, 0, tiledSize);
            if (tilerCase.Tiled.Length != 0 && !tiledBytes.AsSpan().SequenceEqual(tilerCase.Tiled))
            {
                failures.Add($"{tilerCase.Name}: tile differs from the CPU twin");
            }

            var detiled = harness.Run(() => harness.Tiler.Detile(tiledOutput.Handle, 0, tiledSize, linearSize, transfers));
            var detiledBytes = harness.ReadBack(detiled.Buffer, detiled.Offset, linearSize);
            var expected = tilerCase.Linear.ToArray();
            MaskPadding(expected, transfer);
            MaskPadding(detiledBytes, transfer);
            if (!detiledBytes.AsSpan().SequenceEqual(expected))
            {
                failures.Add($"{tilerCase.Name}: detile does not invert tile");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        harness.AssertNoValidationMessages();
    }

    // Bytes between the row width and the pitch are never transferred.
    private static void MaskPadding(byte[] bytes, in TileTransfer transfer)
    {
        var pitchBytes = transfer.Pitch * transfer.BytesPerElement;
        var rowBytes = transfer.Width * transfer.BytesPerElement;
        var sliceBytes = pitchBytes * transfer.Height;
        for (uint z = 0; z < transfer.Depth; z++)
        {
            for (uint y = 0; y < transfer.Height; y++)
            {
                Array.Clear(bytes, (int)(z * sliceBytes + y * pitchBytes + rowBytes), (int)(pitchBytes - rowBytes));
            }
        }
    }

    [Fact]
    // Scratches go back to a pool when their tick completes instead of being freed, so
    // the next tick's scratches of the same sizes reuse them without new allocations.
    public void Detile_ReusesTheScratchAfterTheTick()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var tilerCase = TilerCases.Create(TileBlockKind.Standard4KB, 4, TilerCases.Shape.Blocks, 77)!;
        var tiled = harness.Upload(tilerCase.Tiled);
        var baseline = harness.Device.LiveAllocations;
        for (var round = 0; round < 3; round++)
        {
            harness.Run(() =>
            {
                var linear = harness.Tiler.Detile(tiled.Handle, 0, tilerCase.Transfer.TiledSize, tilerCase.Transfer.LinearSize, new[] { tilerCase.Transfer });
                var scratch = harness.Tiler.GetScratchBuffer(64);
                Assert.Equal(tilerCase.Transfer.LinearSize, linear.Size);
                Assert.Equal(64UL, scratch.Size);
                Assert.NotEqual(linear.Buffer.Handle, scratch.Buffer.Handle);
                Assert.Equal(baseline + 2, harness.Device.LiveAllocations);
                harness.Scheduler.Finish();
            });
            Assert.Equal(baseline + 2, harness.Device.LiveAllocations);
        }

        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void TileAndDetile_PreserveRetainedStreamDataWhenTheRingIsFull()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var tilerCase = TilerCases.Create(TileBlockKind.Standard4KB, 4, TilerCases.Shape.Blocks, 91)!;
        var linear = harness.Upload(tilerCase.Linear);
        var tiled = harness.CreateDeviceLocalBuffer(tilerCase.Transfer.TiledSize);
        harness.Run(() =>
        {
            Assert.True(harness.Stream.TryMap(harness.Stream.Size, out var offset));
            Assert.Equal(0UL, offset);
            harness.Stream.Mapped.Fill(0xA5);
            harness.Stream.Commit();
        });
        using var retention = harness.Stream.RetainContents();
        var baseline = harness.Device.LiveAllocations;
        harness.Run(() =>
        {
            harness.Tiler.Tile(linear.Handle, 0, linear.Size, tiled.Handle, 0, tiled.Size, [tilerCase.Transfer]);
            Assert.Equal(baseline + 1, harness.Device.LiveAllocations);
            harness.Scheduler.Finish();
            Assert.Equal(baseline, harness.Device.LiveAllocations);
            Assert.False(harness.Stream.Mapped.ContainsAnyExcept((byte)0xA5));
        });
        Assert.Equal(tilerCase.Tiled, harness.ReadBack(tiled.Handle, 0, tiled.Size));

        var detiled = harness.Run(() => harness.Tiler.Detile(tiled.Handle, 0, tiled.Size,
            tilerCase.Transfer.LinearSize, [tilerCase.Transfer]));
        var actual = harness.ReadBack(detiled.Buffer, detiled.Offset, detiled.Size);
        var expected = tilerCase.Linear.ToArray();
        MaskPadding(actual, tilerCase.Transfer);
        MaskPadding(expected, tilerCase.Transfer);
        Assert.Equal(expected, actual);
        Assert.False(harness.Stream.Mapped.ContainsAnyExcept((byte)0xA5));
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void Prepare_RejectsTransfersThatDoNotFit()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new FatalScope();
        using var harness = new ImageTestHarness(_vulkan);
        var tilerCase = TilerCases.Create(TileBlockKind.Standard64KB, 4, TilerCases.Shape.Blocks, 5)!;
        var linear = harness.Upload(tilerCase.Linear);
        var tiled = harness.CreateDeviceLocalBuffer(tilerCase.Transfer.TiledSize);

        void Reject(TileTransfer transfer, string fragment, ulong tiledCapacity = 0, ulong linearCapacity = 0)
        {
            var before = fatal.Messages.Count;
            harness.Run(() => Assert.Throws<SchedulerFatalException>(() =>
                harness.Tiler.Tile(linear.Handle, 0, linearCapacity != 0 ? linearCapacity : transfer.LinearSize, tiled.Handle, 0, tiledCapacity != 0 ? tiledCapacity : transfer.TiledSize, new[] { transfer })));
            Assert.Contains(fragment, fatal.Messages[before]);
        }

        Reject(tilerCase.Transfer with { Width = 0 }, "tile transfer is invalid");
        Reject(tilerCase.Transfer with { Pitch = 1 }, "tile transfer is invalid");
        Reject(tilerCase.Transfer with { Depth = 2 }, "tile transfer is invalid");
        Reject(tilerCase.Transfer with { TiledSize = 65536 }, "tiled extent does not fit");
        Reject(tilerCase.Transfer with { LinearSize = 16 }, "linear extent does not fit", linearCapacity: 16);
        Reject(tilerCase.Transfer with { LinearOffset = 2 }, "tile transfer is invalid");
        Reject(tilerCase.Transfer with { Tail = true, TailX = 200 }, "tail tile transfer does not fit");
        Reject(tilerCase.Transfer with { BytesPerElement = 3 }, "tile transfer is invalid");
        harness.Run(() => Assert.Throws<SchedulerFatalException>(() => harness.Tiler.Tile(linear.Handle, 0, 16, tiled.Handle, 0, 16, ReadOnlySpan<TileTransfer>.Empty)));
        Assert.Contains(fatal.Messages, message => message.Contains("batch is empty"));
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DepthConversion_WidensAndNarrowsEveryRow(bool useFloat32Depth)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var layout = new DepthConversionLayout
        {
            Width = 37,
            Height = 11,
            Layers = 2,
            SourceRowStride = 80,
            TargetRowStride = 160,
            SourceSliceStride = 1024,
            TargetSliceStride = 2048,
        };
        var source = new byte[2 * 1024];
        var random = new System.Random(41);
        var values = new ushort[2, 11, 37];
        for (var layer = 0; layer < 2; layer++)
        {
            for (var row = 0; row < 11; row++)
            {
                for (var x = 0; x < 37; x++)
                {
                    var value = (ushort)random.Next(0, 65536);
                    values[layer, row, x] = value;
                    BitConverter.TryWriteBytes(source.AsSpan(layer * 1024 + row * 80 + x * 2), value);
                }
            }
        }

        var narrowLayout = layout with { SourceRowStride = 160, TargetRowStride = 80, SourceSliceStride = 2048, TargetSliceStride = 1024 };
        var sourceBuffer = harness.Upload(source);
        var wideBuffer = harness.CreateDeviceLocalBuffer(2 * 2048);
        var narrowBuffer = harness.CreateDeviceLocalBuffer(2 * 1024);
        harness.Run(() =>
        {
            wideBuffer.Fill(0, wideBuffer.Size, 0);
            narrowBuffer.Fill(0, narrowBuffer.Size, 0);
            harness.Tiler.ConvertDepth16(new TilerBufferSpan(sourceBuffer.Handle, 0, sourceBuffer.Size), new TilerBufferSpan(wideBuffer.Handle, 0, wideBuffer.Size), DepthConversionDirection.Widen, useFloat32Depth, layout);
            harness.Tiler.ConvertDepth16(new TilerBufferSpan(wideBuffer.Handle, 0, wideBuffer.Size), new TilerBufferSpan(narrowBuffer.Handle, 0, narrowBuffer.Size), DepthConversionDirection.Narrow, useFloat32Depth, narrowLayout);
        });
        var wide = harness.ReadBack(wideBuffer.Handle, 0, wideBuffer.Size);
        var narrow = harness.ReadBack(narrowBuffer.Handle, 0, narrowBuffer.Size);
        for (var layer = 0; layer < 2; layer++)
        {
            for (var row = 0; row < 11; row++)
            {
                for (var x = 0; x < 37; x++)
                {
                    uint value = values[layer, row, x];
                    var expected = useFloat32Depth ? BitConverter.SingleToUInt32Bits(value * (1.0f / 65535.0f)) : value * 256 + (value + 128) / 257;
                    Assert.Equal(expected, BitConverter.ToUInt32(wide, layer * 2048 + row * 160 + x * 4));
                    Assert.Equal(value, BitConverter.ToUInt16(narrow, layer * 1024 + row * 80 + x * 2));
                }

                Assert.Equal(0u, BitConverter.ToUInt32(wide, layer * 2048 + row * 160 + 37 * 4));
                Assert.Equal(0, BitConverter.ToUInt16(narrow, layer * 1024 + row * 80 + 37 * 2));
            }
        }
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void SwapBgra16_ExchangesTheOuterChannels()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new FatalScope();
        using var harness = new ImageTestHarness(_vulkan);
        var pixels = ImageTestHarness.Pattern(8 * 13, 19);
        var input = harness.Upload(pixels);
        var swapped = harness.Run(() => harness.Tiler.SwapBgra16(new TilerBufferSpan(input.Handle, 0, input.Size)));
        var output = harness.ReadBack(swapped.Buffer, swapped.Offset, swapped.Size);
        for (var pixel = 0; pixel < 13; pixel++)
        {
            var offset = pixel * 8;
            Assert.Equal(pixels[(offset + 4)..(offset + 6)], output[offset..(offset + 2)]);
            Assert.Equal(pixels[(offset + 2)..(offset + 4)], output[(offset + 2)..(offset + 4)]);
            Assert.Equal(pixels[offset..(offset + 2)], output[(offset + 4)..(offset + 6)]);
            Assert.Equal(pixels[(offset + 6)..(offset + 8)], output[(offset + 6)..(offset + 8)]);
        }

        var explicitOutput = harness.CreateDeviceLocalBuffer(input.Size);
        harness.Run(() => harness.Tiler.SwapBgra16(new TilerBufferSpan(input.Handle, 0, input.Size), new TilerBufferSpan(explicitOutput.Handle, 0, explicitOutput.Size)));
        Assert.Equal(output, harness.ReadBack(explicitOutput.Handle, 0, explicitOutput.Size));

        harness.Run(() => Assert.Throws<SchedulerFatalException>(() => harness.Tiler.SwapBgra16(new TilerBufferSpan(input.Handle, 0, 12))));
        Assert.Contains(fatal.Messages, message => message.Contains("swap input size is invalid"));
        harness.AssertNoValidationMessages();
    }
}
