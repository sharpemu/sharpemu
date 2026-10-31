// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

[Collection(SchedulingStateCollection.Name)]
public sealed class PackedTilerWordTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public unsafe void PackedAndFallbackTransfersFlushPendingBarriersAndInvalidateBindings(bool packed)
    {
        var gpu = fixture.Vulkan;
        if (!GatePrerequisites.Ready(gpu)) return;
        using var harness = new ImageTestHarness(gpu);
        Assert.True(TileGeometry.TryGetBlockLayout(TileBlockKind.Standard4KB3D, 1, out var block));
        var input = ImageTestHarness.Pattern(8, 37);
        var width = packed ? 8u : 7u;
        var expected = Enumerable.Repeat((byte)0x5A, (int)block.BlockSize).ToArray();
        for (uint x = 0; x < width; x++)
        {
            Assert.True(TileGeometry.TryGetBlockOffset(block, x, 0, 0, out var offset));
            expected[offset] = input[x];
        }
        var source = harness.Upload(input);
        using var target = new GpuBuffer(harness.Device, harness.Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, block.BlockSize);
        target.Mapped.Fill(0x5A);
        target.Flush(0, target.Size);
        var transfer = new TileTransfer
        {
            Kind = block.Kind,
            BytesPerElement = 1,
            Width = width,
            Height = 1,
            Depth = 1,
            Pitch = 8,
            TiledWidth = block.BlockWidth,
            TiledHeight = block.BlockHeight,
            LinearSize = 8,
            TiledSize = block.BlockSize,
        };
        harness.Run(() =>
        {
            var before = NativeBindingEpoch.Value;
            var recordedAt = -1L;
            harness.Scheduler.MemoryBarrierRecorder = (handle, request) =>
            {
                recordedAt = NativeBindingEpoch.Value;
                var barrier = new MemoryBarrier2
                {
                    SType = StructureType.MemoryBarrier2,
                    SrcAccessMask = VulkanSynchronization.Access(request.SourceAccess),
                    DstAccessMask = VulkanSynchronization.Access(request.DestinationAccess),
                };
                VulkanSynchronization.PipelineBarrier(harness.Vk, new CommandBuffer(handle), request.SourceStages,
                    request.DestinationStages, 0, 1, &barrier, 0, null, 0, null);
            };
            harness.Scheduler.Current.RequestMemoryBarrier(new MemoryBarrierRequest(
                PipelineStageFlags.AllCommandsBit, PipelineStageFlags.ComputeShaderBit,
                AccessFlags.MemoryWriteBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit));
            harness.Tiler.Tile(source.Handle, 0, 8, target.Handle, 0, block.BlockSize, new[] { transfer });
            Assert.Equal(before, recordedAt);
            Assert.True(NativeBindingEpoch.Value > before);
        });
        Assert.Equal(expected, harness.ReadBack(target.Handle, 0, target.Size));
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void PackedDispatchRejectsTheActualWorkgroupCountBeforeBinding()
    {
        var gpu = fixture.Vulkan;
        if (!GatePrerequisites.Ready(gpu)) return;
        using var fatal = new FatalScope();
        using var harness = new ImageTestHarness(gpu);
        Assert.True(TileGeometry.TryGetBlockLayout(TileBlockKind.Standard4KB3D, 1, out var block));
        var local = TilerShaders.BlockCopyWorkgroupSize((TilerBlockShape)block.Kind, 1, true, true);
        var width = local.X;
        var height = ((ulong)harness.Device.MaxComputeWorkGroupCount.Y + 1) * local.Y;
        var rows = (height + block.BlockHeight - 1) / block.BlockHeight;
        // A device with a very large dispatch limit hits the existing capacity guard first.
        if (height > uint.MaxValue || width * height > uint.MaxValue || rows * block.BlockSize > uint.MaxValue) return;
        var transfer = new TileTransfer
        {
            Kind = block.Kind,
            BytesPerElement = 1,
            Width = width,
            Height = (uint)height,
            Depth = 1,
            Pitch = width,
            LinearSize = width * height,
            TiledSize = rows * block.BlockSize,
        };
        var source = harness.Upload(new byte[4]);
        var target = harness.CreateDeviceLocalBuffer(4);
        harness.Run(() => Assert.Throws<SchedulerFatalException>(() => harness.Tiler.Tile(source.Handle, 0,
            transfer.LinearSize, target.Handle, 0, transfer.TiledSize, new[] { transfer })));
        Assert.Contains(fatal.Messages, message => message.Contains("exceeds the workgroup limits"));
        Console.WriteLine($"Rejected packed dispatch with {height / local.Y} Y groups against limit {harness.Device.MaxComputeWorkGroupCount.Y}");
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void NativeProductionTransfersMatchOracleAcrossBlockAndSliceBoundaries()
    {
        var gpu = fixture.Vulkan;
        if (!GatePrerequisites.Ready(gpu)) return;
        using var harness = new ImageTestHarness(gpu);
        var count = 0;
        foreach (var kind in Enum.GetValues<TileBlockKind>())
            foreach (var bytes in new[] { 1u, 2u })
            {
                Assert.True(TileGeometry.TryGetBlockLayout(kind, bytes, out var block));
                for (var variant = 0; variant < 9; variant++)
                {
                    var tail = variant >= 6;
                    if (tail && kind == TileBlockKind.Standard256B) continue;
                    var wordWidth = 4 / bytes;
                    var width = variant == 1 ? 1u : variant == 2 ? block.BlockWidth + wordWidth : wordWidth;
                    var height = variant == 0 || variant == 8 ? 1u : variant == 2 ? block.BlockHeight + 1 : 5u;
                    var depth = block.BlockDepth == 1 || variant == 0 ? 1u : tail ? block.BlockDepth : block.BlockDepth + 1;
                    var pitch = width + (variant == 4 ? 1u : wordWidth);
                    var slice = (ulong)pitch * bytes * height + (variant == 5 ? bytes : 4u);
                    var prefix = variant == 3 ? bytes : 4u;
                    var columns = tail ? 1u : (width + block.BlockWidth - 1) / block.BlockWidth;
                    var rows = tail ? 1u : (height + block.BlockHeight - 1) / block.BlockHeight;
                    var slabs = tail ? 1u : (depth + block.BlockDepth - 1) / block.BlockDepth;
                    var transfer = new TileTransfer
                    {
                        Kind = kind,
                        BytesPerElement = bytes,
                        Width = width,
                        Height = height,
                        Depth = depth,
                        Pitch = pitch,
                        LinearSliceStride = slice,
                        LinearOffset = prefix,
                        LinearSize = slice * depth,
                        TiledOffset = prefix,
                        TiledSize = (ulong)columns * rows * slabs * block.BlockSize,
                        TiledWidth = columns * block.BlockWidth,
                        TiledHeight = rows * block.BlockHeight,
                        Tail = tail,
                        TailX = variant == 7 ? 1u : variant == 8 ? block.BlockWidth - wordWidth : tail ? wordWidth : 0u,
                        TailY = variant == 8 ? block.BlockHeight - 1 : tail ? 1u : 0u,
                        SurfaceZ = variant == 2 && kind is TileBlockKind.RenderTarget64KB or TileBlockKind.RenderTarget64KBGen5 or TileBlockKind.Depth64KB ? 13u : 0u,
                    };
                    var linearCapacity = (transfer.LinearOffset + transfer.LinearSize + 7) & ~3UL;
                    var tiledCapacity = (transfer.TiledOffset + transfer.TiledSize + 7) & ~3UL;
                    var linear = ImageTestHarness.Pattern((int)linearCapacity, (uint)(count + 101));
                    var tiled = ImageTestHarness.Pattern((int)tiledCapacity, (uint)(count + 103));
                    var expectedTiled = Enumerable.Repeat((byte)0x5A, (int)tiledCapacity).ToArray();
                    var expectedLinear = new byte[(int)linearCapacity];
                    for (uint z = 0; z < depth; z++)
                        for (uint y = 0; y < height; y++)
                            for (uint x = 0; x < width; x++)
                            {
                                var bx = tail ? 0u : x / block.BlockWidth;
                                var by = tail ? 0u : y / block.BlockHeight;
                                var bz = tail ? 0u : z / block.BlockDepth;
                                Assert.True(TileGeometry.TryGetBlockOffset(block, tail ? x + transfer.TailX : x % block.BlockWidth,
                                    tail ? y + transfer.TailY : y % block.BlockHeight, z % block.BlockDepth, out var offset));
                                Assert.True(TileGeometry.TryGetBlockXor(block, bx, by, transfer.SurfaceZ, out var xor));
                                var tiledIndex = (int)(prefix + (ulong)(bz * columns * rows + by * columns + bx) * block.BlockSize + (offset ^ xor));
                                var linearIndex = (int)(prefix + z * slice + (ulong)y * pitch * bytes + x * bytes);
                                Array.Copy(linear, linearIndex, expectedTiled, tiledIndex, bytes);
                                Array.Copy(tiled, tiledIndex, expectedLinear, linearIndex, bytes);
                            }

                    var linearBuffer = harness.Upload(linear);
                    var tiledBuffer = harness.Upload(tiled);
                    using var target = new GpuBuffer(harness.Device, harness.Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, tiledCapacity);
                    target.Mapped.Fill(0x5A);
                    target.Flush(0, target.Size);
                    harness.Run(() => harness.Tiler.Tile(linearBuffer.Handle, 0, linearCapacity, target.Handle, 0, tiledCapacity, new[] { transfer }));
                    Assert.True(expectedTiled.AsSpan().SequenceEqual(harness.ReadBack(target.Handle, 0, tiledCapacity)), $"Tile {kind}/{bytes}/{variant}");
                    var detiled = harness.Run(() => harness.Tiler.Detile(tiledBuffer.Handle, 0, tiledCapacity, linearCapacity, new[] { transfer }));
                    Assert.True(expectedLinear.AsSpan().SequenceEqual(harness.ReadBack(detiled.Buffer, detiled.Offset, detiled.Size)), $"Detile {kind}/{bytes}/{variant}");
                    count += 2;
                }
            }
        Assert.Equal(384, count); // Original 348 plus 36 GCN transfers.
        Console.WriteLine($"Production tiler cases checked against CPU byte oracle: {count}");
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void NativePackedCopiesPreservePrefixesPartialWordsRowsAndMipTails()
    {
        var gpu = fixture.Vulkan;
        if (!GatePrerequisites.Ready(gpu)) return;
        using var harness = new ImageTestHarness(gpu);
        using var runner = new TilerComputeRunner(harness);
        var count = 0;
        foreach (var kind in Enum.GetValues<TileBlockKind>())
            foreach (var bytes in new[] { 1u, 2u })
                foreach (var toTiled in new[] { false, true })
                {
                    Assert.True(TileGeometry.TryGetBlockLayout(kind, bytes, out var block));
                    var pipeline = runner.CreatePipeline(TilerShaders.CreateBlockCopy((TilerBlockShape)kind, bytes, toTiled), []);
                    var eligible = TilerShaders.SupportsPackedWords((TilerBlockShape)kind, bytes);
                    var packedPipeline = eligible ? runner.CreatePipeline(TilerShaders.CreateBlockCopy((TilerBlockShape)kind, bytes, toTiled, true), []) : pipeline;
                    for (var variant = 0; variant < 7; variant++)
                    {
                        var tail = variant is 3 or 4 or 6;
                        if (tail && kind == TileBlockKind.Standard256B) continue;
                        var width = variant == 5 ? 8u / bytes : variant == 6 ? 4u / bytes : tail ? (bytes == 1 ? 5u : 3u) : (bytes == 1 ? 13u : 7u);
                        // GCN microtiles are only eight elements wide; keep the new tails inside one block.
                        if (tail) width = Math.Min(width, block.BlockWidth - 4u / bytes);
                        var height = tail ? 3u : 9u;
                        var depth = block.BlockDepth == 1 ? 1u : 2u;
                        var pitch = variant == 2 ? width + 2 : ((width * bytes + 7) & ~3u) / bytes;
                        var row = pitch * bytes;
                        var slice = row * height;
                        var columns = tail ? 1u : (width + block.BlockWidth - 1) / block.BlockWidth;
                        var rows = tail ? 1u : (height + block.BlockHeight - 1) / block.BlockHeight;
                        var linearSize = slice * depth;
                        var tiledSize = columns * rows * block.BlockSize;
                        var sourceBase = variant == 1 ? bytes : 4u;
                        var destinationBase = variant == 1 ? bytes : 8u;
                        var tailX = tail ? (variant == 4 ? 1u : 4u / bytes) : 0u;
                        var tailY = tail ? block.BlockHeight / 4 : 0u;
                        var inputSize = (sourceBase + (toTiled ? linearSize : tiledSize) + 19) & ~3u;
                        var outputSize = (destinationBase + (toTiled ? tiledSize : linearSize) + 19) & ~3u;
                        var input = ImageTestHarness.Pattern((int)inputSize, (uint)(count + 73));
                        var expected = Enumerable.Repeat((byte)0x5A, (int)outputSize).ToArray();
                        for (uint z = 0; z < depth; z++)
                            for (uint y = 0; y < height; y++)
                                for (uint x = 0; x < width; x++)
                                {
                                    var bx = tail ? 0 : x / block.BlockWidth;
                                    var by = tail ? 0 : y / block.BlockHeight;
                                    var bz = tail ? 0 : z / block.BlockDepth;
                                    Assert.True(TileGeometry.TryGetBlockOffset(block, tail ? x + tailX : x % block.BlockWidth,
                                        tail ? y + tailY : y % block.BlockHeight, z % block.BlockDepth, out var offset));
                                    Assert.True(TileGeometry.TryGetBlockXor(block, bx, by, out var xor));
                                    var tiled = (bz * columns * rows + by * columns + bx) * block.BlockSize + (offset ^ xor);
                                    var linear = z * slice + y * row + x * bytes;
                                    Array.Copy(input, sourceBase + (toTiled ? linear : tiled), expected,
                                        destinationBase + (toTiled ? tiled : linear), bytes);
                                }
                        var inputBuffer = harness.Upload(input);
                        using var output = new GpuBuffer(harness.Device, harness.Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, outputSize);
                        output.Mapped.Fill(0x5A); output.Flush(0, output.Size);
                        var arguments = new TileTransferArguments
                        {
                            SourceBase = sourceBase,
                            DestinationBase = destinationBase,
                            Width = width,
                            Height = height,
                            Depth = depth,
                            PitchBytes = row,
                            SliceBytes = slice,
                            BlocksPerRow = columns,
                            BlocksPerSlice = columns * rows,
                            Tail = tail ? 1u : 0u,
                            TailX = tailX,
                            TailY = tailY,
                        };
                        var argumentBytes = new byte[TileTransferArguments.Size];
                        MemoryMarshal.Write(argumentBytes, in arguments);
                        var uniform = harness.Upload(argumentBytes, 256);
                        var packed = eligible && variant >= 5;
                        var local = TilerShaders.BlockCopyWorkgroupSize((TilerBlockShape)kind, bytes, toTiled, packed);
                        harness.Run(() => runner.Dispatch(packed ? packedPipeline : pipeline, inputBuffer, output, uniform, 0, default,
                            (width + local.X - 1) / local.X, (height + local.Y - 1) / local.Y, (depth + local.Z - 1) / local.Z));
                        var actual = harness.ReadBack(output.Handle, 0, outputSize);
                        Assert.True(expected.AsSpan().SequenceEqual(actual), $"{kind} bytes={bytes} tile={toTiled} variant={variant}");
                        count++;
                    }
                }
        Assert.Equal(296, count); // Original 268 plus 28 GCN transfers.
        Console.WriteLine($"Packed tiler cases checked against CPU byte oracle: {count}");
        harness.AssertNoValidationMessages();
    }
}

// Geometry checks do not create a Vulkan fixture and can run without a GPU.
public sealed class PackedTilerGeometryTests
{
    [Fact]
    public void PackedWordsStayContiguousAcrossEveryEligibleBlockLayout()
    {
        var layouts = 0;
        foreach (var kind in Enum.GetValues<TileBlockKind>())
            foreach (var bytes in new[] { 1u, 2u })
            {
                if (!TilerShaders.SupportsPackedWords((TilerBlockShape)kind, bytes)) continue;
                layouts++;
                Assert.True(TileGeometry.TryGetBlockLayout(kind, bytes, out var block));
                for (uint z = 0; z < block.BlockDepth; z++)
                    for (uint y = 0; y < block.BlockHeight; y++)
                        for (uint x = 0; x < block.BlockWidth; x += 4 / bytes)
                        {
                            Assert.True(TileGeometry.TryGetBlockOffset(block, x, y, z, out var first));
                            Assert.Equal(0u, first & 3);
                            for (uint lane = 1; lane < 4 / bytes; lane++)
                            {
                                Assert.True(TileGeometry.TryGetBlockOffset(block, x + lane, y, z, out var offset));
                                Assert.Equal(first + lane * bytes, offset);
                            }
                        }
            }
        Assert.Equal(20, layouts); // All original 19 eligible layouts plus GCN16.
    }

    [Fact]
    public void PackedTiledWorkgroupsCoverOneContiguousSector()
    {
        foreach (var kind in Enum.GetValues<TileBlockKind>())
            foreach (var bytes in new[] { 1u, 2u })
            {
                if (!TilerShaders.SupportsPackedWords((TilerBlockShape)kind, bytes)) continue;
                Assert.True(TileGeometry.TryGetBlockLayout(kind, bytes, out var block));
                var local = TilerShaders.BlockCopyWorkgroupSize((TilerBlockShape)kind, bytes, true, true);
                var addresses = new List<uint>();
                for (uint z = 0; z < local.Z; z++)
                    for (uint y = 0; y < local.Y; y++)
                        for (uint x = 0; x < local.X; x += 4 / bytes)
                        {
                            Assert.True(TileGeometry.TryGetBlockOffset(block, x, y, z, out var offset));
                            addresses.Add(offset);
                        }
                Assert.Equal(Enumerable.Range(0, 8).Select(i => (uint)i * 4), addresses.Order());
            }
    }

    [Fact]
    public void GcnEightBitWordsInterleaveRowsAndMustUseFallback()
    {
        Assert.True(TileGeometry.TryGetBlockLayout(TileBlockKind.GcnThin1D, 1, out var block));
        var offsets = new List<uint>();
        for (uint x = 0; x < 4; x++)
        {
            Assert.True(TileGeometry.TryGetBlockOffset(block, x, 0, 0, out var offset));
            offsets.Add(offset);
        }
        Assert.Equal(new uint[] { 0, 1, 4, 5 }, offsets);
        Assert.False(TilerShaders.SupportsPackedWords(TilerBlockShape.GcnThin1D, 1));
        Assert.True(TilerShaders.SupportsPackedWords(TilerBlockShape.GcnThin1D, 2));
    }
}
