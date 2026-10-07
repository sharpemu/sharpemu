// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// Ownership hand-overs between the guest, the buffer store and the image store.
public sealed partial class GuestImageCacheTests
{
    [Fact]
    public void BufferMirror_ReadsTheImageAndKeepsItsOwnership()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address + 0x5000, Bytes(0xa1b2c3d4u));
        var mirror = Color32(address + 0x5000);
        var mirrorId = harness.Acquire(ref mirror);
        harness.MarkGpuWritten(mirrorId);

        Assert.True(harness.WriteFault(address + 0x5000));
        harness.Write(address + 0x5000, Bytes(0x0f1e2d3cu));
        var (mirrorBuffer, mirrorOffset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address + 0x5000, 4, isWritten: false, isTexelBuffer: true));
        Assert.False(harness.Image(mirrorId).IsBufferModified);
        Assert.Equal(Bytes(0x0f1e2d3cu), harness.ReadBufferBytes(mirrorBuffer, mirrorOffset, 4));

        var refresh = mirror;
        var refreshId = harness.Acquire(ref refresh);
        Assert.Equal(mirrorId, refreshId);
        Assert.False(harness.Image(refreshId).IsBufferModified);
        harness.MarkGpuWritten(refreshId);

        harness.Write(address + 0x9000, Bytes(0x3f234567u));
        var exact = Color32(address + 0x9000);
        var exactId = harness.Acquire(ref exact);
        harness.MarkGpuWritten(exactId);
        var (exactBuffer, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address + 0x9000, 4, isWritten: false, isTexelBuffer: true));
        Assert.NotNull(exactBuffer);
        Assert.False(harness.Image(exactId).IsBufferModified);
        Assert.True(harness.Image(exactId).IsGpuModified);

        var exactFloat = exact;
        exactFloat.Description.PixelFormat = Format.R32Sfloat;
        exactFloat.Description.GuestFormat = GuestPixelFormat.Bits32Float;
        exactFloat.View = exactFloat.View with { Format = Format.R32Sfloat };
        var exactFloatId = harness.Acquire(ref exactFloat, exactFormat: true);
        Assert.NotEqual(exactId, exactFloatId);
        Assert.True(harness.Images.Contains(exactId));
        Assert.True(harness.Image(exactId).IsGpuModified);
        Assert.False(harness.Image(exactFloatId).IsBufferModified);
        Assert.False(harness.Image(exactFloatId).IsGpuModified);
        Assert.Equal(Bytes(0x3f234567u), harness.ReadImageBytes(harness.Image(exactFloatId)));
        harness.Shutdown();
    }

    [Fact]
    public void BufferWriteThenImageClear_TransfersOwnershipToTheImage()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address + 0x6000, Bytes(0x01010101u));
        var clear = LinearRequest(address + 0x6000, 4, Format.R8G8B8A8Unorm, GuestPixelFormat.Bits8_8_8_8UNorm, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
        var clearId = harness.Find(ref clear);
        harness.Worker.Run(() =>
        {
            var (buffer, _) = harness.Cache.ObtainBuffer(address + 0x6000, 4, isWritten: true, isTexelBuffer: true);
            Assert.NotNull(buffer);
            harness.Images.InvalidateMemoryFromGpu(address + 0x6000, 4);
        });
        Assert.True(harness.Image(clearId).IsBufferModified);

        Assert.True(harness.Worker.Run(() => harness.Images.TryClearImageFromBuffer(address + 0x6000, 4, 0xaabbccddu)));
        Assert.False(harness.Image(clearId).IsBufferModified);
        Assert.True(harness.Image(clearId).IsGpuModified);
        Assert.True(harness.Image(clearId).IsWatched);
        var reread = clear;
        Assert.Equal(clearId, harness.Find(ref reread));
        Assert.Equal(new byte[] { 0xdd, 0xcc, 0xbb, 0xaa }, harness.ReadImageBytes(harness.Image(clearId)));
        harness.Shutdown();
    }

    [Fact]
    public void FullColorClear_SkipsSourceBufferAndRetainsWriteObservation()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        ImageClearBacking? backing = null;
        using var harness = new CacheHarness(_vulkan, backing: memory => backing = new ImageClearBacking(memory));
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, Bytes(0x01020304u));
        var request = Color32(address);
        var imageIdentifier = harness.Find(ref request);
        var bufferCount = harness.Cache.BufferCount;
        var readCount = backing!.ReadCount;
        Assert.True(harness.Image(imageIdentifier).IsCpuDirty);

        Assert.True(harness.Worker.Run(() => harness.Images.TryClearImageFromBuffer(address, 4, 0xaabbccddu)));

        Assert.Equal(bufferCount, harness.Cache.BufferCount);
        Assert.Equal(readCount, backing.ReadCount);
        Assert.False(harness.Image(imageIdentifier).IsCpuDirty);
        Assert.True(harness.Image(imageIdentifier).IsGpuModified);
        Assert.True(harness.Image(imageIdentifier).IsWatched);
        Assert.Equal(Bytes(0xaabbccddu), harness.ReadImageBytes(harness.Image(imageIdentifier)));
        Assert.True(harness.WriteFault(address));
        Assert.True(harness.Image(imageIdentifier).IsCpuDirty);
        harness.Shutdown();
    }

    private sealed class ImageClearBacking(IGuestBackedSpace inner) : IGuestBackedSpace
    {
        public int ReadCount { get; private set; }

        public bool TryHoldRange(ulong address, ulong size) => inner.TryHoldRange(address, size);
        public bool TryHoldRangeAtOrAbove(ulong searchStart, ulong size, ulong alignment, out ulong address) =>
            inner.TryHoldRangeAtOrAbove(searchStart, size, alignment, out address);
        public bool TryMapBacked(ulong address, ulong size, ulong backingOffset, GuestPageProtection protection, out HostViewFailure failure) =>
            inner.TryMapBacked(address, size, backingOffset, protection, out failure);
        public bool TryUnmapBacked(ulong address, ulong size) => inner.TryUnmapBacked(address, size);
        public bool TryClearBacking(ulong offset, ulong size) => inner.TryClearBacking(offset, size);
        public bool IsBackedView(ulong address) => inner.IsBackedView(address);
        public bool IsBackedRange(ulong address, ulong size) => inner.IsBackedRange(address, size);
        public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data) => inner.TryWriteBacking(address, data);

        public bool TryReadBacking(ulong address, Span<byte> data)
        {
            ReadCount++;
            return inner.TryReadBacking(address, data);
        }
    }

    [Fact]
    public void PartialPage_BufferAndImageShareOneTrackerPage()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var imageAddress = address + 0xa000;
        var bufferAddress = address + 0xa010;
        var cleanAddress = address + 0xa020;
        harness.Write(cleanAddress, Bytes(0xabcdef01u));
        harness.Worker.Run(() =>
        {
            var (buffer, _) = harness.Cache.ObtainBuffer(imageAddress, 4, isWritten: true);
            Assert.NotNull(buffer);
            harness.Cache.FillBuffer(imageAddress, 4, 0x31415926u, isGds: false);
            harness.Cache.FillBuffer(bufferAddress, 4, 0x27182818u, isGds: false);
        });

        var partial = Color32(imageAddress);
        var partialId = harness.Find(ref partial);
        Assert.False(harness.Image(partialId).IsGpuModified);
        harness.Worker.Run(() =>
        {
            var (mirror, _) = harness.Cache.ObtainBuffer(imageAddress, 4, isWritten: false, isTexelBuffer: true);
            Assert.NotNull(mirror);
        });
        Assert.False(harness.Image(partialId).IsGpuModified);
        harness.Worker.Run(() => Assert.NotNull(harness.Cache.ObtainBufferForImage(cleanAddress, 4).Buffer));

        Assert.True(harness.ReadFault(bufferAddress));
        Assert.Equal(0x31415926u, harness.ReadUInt32(imageAddress));
        Assert.Equal(0x27182818u, harness.ReadUInt32(bufferAddress));

        Assert.True(harness.WriteFault(cleanAddress));
        harness.Write(cleanAddress, Bytes(0x13579bdfu));
        var (source, sourceOffset) = harness.Worker.Run(() => harness.Cache.ObtainBufferForImage(cleanAddress, 4));
        Assert.NotNull(source);
        Assert.Equal(Bytes(0x13579bdfu), harness.ReadBufferBytes(source, sourceOffset, 4));
        harness.Shutdown();
    }

    [Fact]
    public void SamePageImages_WatchAndInvalidateIndependently()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address + 0x8000, Bytes(0x01020304u));
        harness.Write(address + 0x8010, Bytes(0x11121314u));
        var first = Color32(address + 0x8000);
        var second = Color32(address + 0x8010);
        var firstId = harness.Acquire(ref first);
        var secondId = harness.Acquire(ref second);
        harness.MarkGpuWritten(firstId);
        harness.MarkGpuWritten(secondId);
        Assert.True(harness.Image(firstId).IsWatched);
        Assert.True(harness.Image(secondId).IsWatched);
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address + 0x8000));

        Assert.Equal(0x01020304u, harness.ReadUInt32(address + 0x8000));
        Assert.Equal(0x11121314u, harness.ReadUInt32(address + 0x8010));
        Assert.True(harness.Image(firstId).IsGpuModified);
        Assert.True(harness.Image(secondId).IsGpuModified);

        Assert.True(harness.WriteFault(address + 0x8080));
        Assert.True(harness.Image(firstId).IsGpuModified);
        Assert.True(harness.Image(secondId).IsGpuModified);
        Assert.False(harness.Image(firstId).IsWatched);
        Assert.False(harness.Image(secondId).IsWatched);
        Assert.True(harness.Image(firstId).IsMaybeCpuDirty);
        Assert.True(harness.Image(secondId).IsMaybeCpuDirty);
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address + 0x8000));

        var retrackedFirst = harness.Acquire(ref first);
        var retrackedSecond = harness.Acquire(ref second);
        var (mirror, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address + 0x8000, 4, isWritten: false, isTexelBuffer: true));
        Assert.Equal(firstId, retrackedFirst);
        Assert.Equal(secondId, retrackedSecond);
        Assert.NotNull(mirror);
        Assert.True(harness.Image(firstId).IsWatched);
        Assert.True(harness.Image(secondId).IsWatched);
        Assert.False(harness.Image(firstId).IsCpuDirty);
        Assert.False(harness.Image(secondId).IsCpuDirty);
        Assert.True(harness.Image(firstId).IsGpuModified);
        Assert.True(harness.Image(secondId).IsGpuModified);

        Assert.True(harness.WriteFault(address + 0x8010));
        Assert.True(harness.Image(secondId).IsGpuModified);
        Assert.True(harness.Image(secondId).IsDefinitelyCpuDirty);
        harness.Write(address + 0x8010, Bytes(0xa5a6a7a8u));
        var refreshedSecond = harness.Acquire(ref second);
        Assert.Equal(secondId, refreshedSecond);
        Assert.False(harness.Image(secondId).IsDefinitelyCpuDirty);
        Assert.True(harness.Image(secondId).IsGpuModified);
        Assert.Equal(Bytes(0xa5a6a7a8u), harness.ReadImageBytes(harness.Image(secondId)));
        harness.Shutdown();
    }

    [Fact]
    public void ExactDisjointReplacement_PublishesWithoutDirtyingNeighbors()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x40000, ReadWrite);
        var imageAddress = address + 0x26000;
        var bufferAddress = address + 0x26010;
        harness.Write(imageAddress, Bytes(0x31415926u));
        var publish = Color32(imageAddress);
        var publishId = harness.Acquire(ref publish);
        harness.MarkGpuWritten(publishId);
        harness.Worker.Run(() => harness.Cache.FillBuffer(bufferAddress, 4, 0x27182818u, isGds: false));

        var replacement = publish;
        replacement.Description.PixelFormat = Format.R32Sfloat;
        replacement.Description.GuestFormat = GuestPixelFormat.Bits32Float;
        replacement.View = replacement.View with { Format = Format.R32Sfloat };
        var replacementId = harness.Find(ref replacement, exactFormat: true);
        Assert.True(replacementId.IsValid);
        Assert.NotEqual(publishId, replacementId);
        Assert.False(harness.Cache.HasGpuDirtyBytes(bufferAddress, 4));
        Assert.Equal(0x31415926u, harness.ReadUInt32(imageAddress));
        Assert.Equal(0x27182818u, harness.ReadUInt32(bufferAddress));
        harness.Shutdown();
    }

    [Fact]
    public void StandaloneMultisampleTarget_KeepsWriteOnlyOwnership()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        foreach (var (offset, samples) in new[] { (0x10000UL, 2u), (0x11000UL, 4u) })
        {
            harness.Write(address + offset, new byte[samples * 2]);
            var request = LinearRequest(address + offset, samples * 2, Format.D16Unorm, GuestPixelFormat.Bits16UNorm, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 2, samples);
            request = AsDepthTarget(request, Format.D16Unorm);
            var imageIdentifier = harness.Acquire(ref request);
            harness.MarkGpuWritten(imageIdentifier);
            Assert.True(harness.Image(imageIdentifier).IsGpuModified);
            Assert.Equal(0, BitConverter.ToUInt16(harness.Read(address + offset, 2)));
            Assert.True(harness.Image(imageIdentifier).IsGpuModified);
            Assert.True(harness.WriteFault(address + offset));
            Assert.True(harness.Image(imageIdentifier).IsGpuModified);
            Assert.True(harness.Image(imageIdentifier).IsDefinitelyCpuDirty);
        }

        harness.Shutdown();
    }

    [Fact]
    public void UnformattedBufferAlias_KeepsImageOwnership()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var request = Color32(address + 0x5000);
        var imageIdentifier = harness.Acquire(ref request);
        harness.MarkGpuWritten(imageIdentifier);
        var (buffer, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address + 0x5000, 4, isWritten: false));
        Assert.NotNull(buffer);
        Assert.True(harness.Image(imageIdentifier).IsGpuModified);
        Assert.False(harness.Image(imageIdentifier).IsBufferModified);
        harness.Shutdown();
    }

    [Fact]
    public void Replacement_WatchesTheWholeRangeOfTheNewImage()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var start = address + 0x18000;
        harness.Write(start, new byte[0x2000]);
        harness.Write(start, Bytes(0xcafebabeu));
        harness.Write(start + 0x2000 - 4, Bytes(0x0badf00du));
        var source = Color32(start);
        var sourceId = harness.Acquire(ref source);
        harness.MarkGpuWritten(sourceId);

        var replacement = LinearRequest(start, 0x2000, Format.R32Uint, GuestPixelFormat.Bits32UInt, GuestImageType.Color2D, new Extent3D(1, 1, 1), 2, 4, 1);
        var replacementId = harness.Find(ref replacement);
        Assert.NotEqual(sourceId, replacementId);
        Assert.True(harness.Image(replacementId).IsGpuModified);
        Assert.True(harness.WriteFault(start + 0x1000));
        Assert.True(harness.Image(replacementId).IsGpuModified);
        Assert.True(harness.Image(replacementId).IsDefinitelyCpuDirty);
        Assert.Equal(0xcafebabeu, harness.ReadUInt32(start));
        Assert.Equal(0x0badf00du, harness.ReadUInt32(start + 0x2000 - 4));
        harness.Shutdown();
    }

    [Fact]
    public void MixedPageSource_UploadsBothOwnershipDomains()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x40000, ReadWrite);
        var source = address + 0x20000;
        const uint width = 1025;
        const ulong size = 0x1004;
        harness.Worker.Run(() => Assert.NotNull(harness.Cache.ObtainBuffer(source, size, isWritten: true).Buffer));
        Assert.True(harness.WriteFault(source));
        harness.Write(source, Bytes(0x1234abcdu));
        harness.Worker.Run(() => harness.Cache.FillBuffer(source + 0x1000, 4, 0x9876fedcu, isGds: false));

        var request = Color32(source, width);
        var imageIdentifier = harness.Acquire(ref request);
        Assert.False(harness.Image(imageIdentifier).IsGpuModified);
        var bytes = harness.ReadImageBytes(harness.Image(imageIdentifier));
        Assert.Equal(0x1234abcdu, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal(0x9876fedcu, BitConverter.ToUInt32(bytes, 0x1000));
        harness.Shutdown();
    }

    [Fact]
    public void BridgedBufferOwners_UploadBothDomains()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x200000, ReadWrite);
        var gpuAddress = address + 0x101000;
        var cpuAddress = address + 0x105000;
        const ulong texel = 16;
        var sourceSize = cpuAddress - gpuAddress + texel;
        var width = (uint)(sourceSize / texel);
        harness.Write(cpuAddress, Bytes(0x56473829u, 0x56473829u, 0x56473829u, 0x56473829u));
        harness.Worker.Run(() =>
        {
            Assert.NotNull(harness.Cache.ObtainBuffer(cpuAddress, texel, isWritten: true).Buffer);
            harness.Cache.FillBuffer(cpuAddress, texel, 0, isGds: false);
        });
        Assert.True(harness.WriteFault(cpuAddress));
        harness.Write(cpuAddress, Bytes(0x56473829u, 0x56473829u, 0x56473829u, 0x56473829u));
        harness.Write(gpuAddress, Bytes(0xa5a5a5a5u, 0xa5a5a5a5u, 0xa5a5a5a5u, 0xa5a5a5a5u));
        var (cpuOwner, gpuOwner) = harness.Worker.Run(() =>
        {
            var (gpu, _) = harness.Cache.ObtainBuffer(gpuAddress, texel, isWritten: true);
            harness.Cache.FillBuffer(gpuAddress, texel, 0x10293847u, isGds: false);
            return (harness.Cache.ObtainBuffer(cpuAddress, texel, isWritten: false).Buffer, gpu);
        });
        Assert.NotSame(cpuOwner, gpuOwner);
        Assert.True(harness.Cache.HasGpuDirtyBytes(gpuAddress, texel));
        Assert.False(harness.Cache.HasGpuDirtyBytes(cpuAddress, texel));

        var request = LinearRequest(gpuAddress, sourceSize, Format.R32G32B32A32Uint, GuestPixelFormat.Bits32_32_32_32UInt, GuestImageType.Color2D, new Extent3D(width, 1, 1), 1, (uint)texel, 1);
        var imageIdentifier = harness.Acquire(ref request);
        var bytes = harness.ReadImageBytes(harness.Image(imageIdentifier));
        Assert.Equal(0x10293847u, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal(0x56473829u, BitConverter.ToUInt32(bytes, bytes.Length - 4));
        harness.Shutdown();
    }

    [Fact]
    public void ByteImageMirror_KeepsOwnership()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x40000, ReadWrite);
        harness.Write(address + 0x24000, [0xac, 0x5a, 0xbd, 0xce]);
        var request = LinearRequest(address + 0x24001, 1, Format.R8Unorm, GuestPixelFormat.Bits8UNorm, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 1, 1);
        var imageIdentifier = harness.Acquire(ref request);
        harness.MarkGpuWritten(imageIdentifier);
        var (mirror, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address + 0x24001, 1, isWritten: false, isTexelBuffer: true));
        Assert.NotNull(mirror);
        Assert.False(harness.Image(imageIdentifier).IsBufferModified);
        Assert.True(harness.Image(imageIdentifier).IsGpuModified);
        harness.Shutdown();
    }

    [Fact]
    public void Bgra16Target_UploadsTheSwappedHalves()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address + 0xb000, Bytes((ushort)0x3c00, (ushort)0x4000, (ushort)0x4200, (ushort)0x4400));
        var request = LinearRequest(address + 0xb000, 8, Format.R16G16B16A16Sfloat, GuestPixelFormat.Bits16_16_16_16Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 8, 1);
        request = AsColorTarget(request);
        request.Description.Bgra16 = true;
        var imageIdentifier = harness.Acquire(ref request);
        Assert.Equal(Bytes(0x40004200u, 0x44003c00u), harness.ReadImageBytes(harness.Image(imageIdentifier)));
        harness.Shutdown();
    }

    [Fact]
    public void SamePageDirtySibling_BlocksTheStaleImageSource()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x400000, ReadWrite);
        var imageAddress = address + 0x334100;
        var siblingAddress = address + 0x334200;
        harness.Worker.Run(() =>
        {
            Assert.NotNull(harness.Cache.ObtainBuffer(siblingAddress, 4, isWritten: true).Buffer);
            harness.Cache.FillBuffer(siblingAddress, 4, 0xc001d00du, isGds: false);
        });
        var exact = Color32(imageAddress);
        var exactId = harness.Acquire(ref exact);
        harness.MarkGpuWritten(exactId);
        Assert.True(harness.Cache.HasGpuDirtyBytes(siblingAddress, 4));
        Assert.False(harness.Cache.HasGpuDirtyBytes(imageAddress, 4));
        Assert.True(harness.Image(exactId).IsGpuModified);

        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(imageAddress, 4, isWritten: true);
            buffer.Fill(offset, 4, 0xc001d00du);
            using var download = new GpuBuffer(harness.Vulkan.DeviceInfo, harness.Scheduler, GpuBufferUsage.DeviceLocal, imageAddress, GpuBuffer.AllFlags, 4);
            Assert.False(harness.Images.TrySynchronizeBufferFromImage(download, imageAddress, 4));
            harness.Scheduler.Finish();
        });
        Assert.True(harness.Cache.TrySynchronizeCpuRead(imageAddress, 4));
        Assert.True(harness.Cache.TrySynchronizeCpuRead(siblingAddress, 4));
        harness.Shutdown();
    }

    [Fact]
    public void PartialUnmap_DeletesTheImage()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var request = LinearRequest(address, 0x2000, Format.R32Uint, GuestPixelFormat.Bits32UInt, GuestImageType.Color2D, new Extent3D(2048, 1, 1), 1, 4, 1);
        var imageIdentifier = harness.Find(ref request);
        Assert.True(imageIdentifier.IsValid);
        harness.Worker.Run(() => harness.ImageStore.Unregister(address, 0x1000));
        Assert.False(harness.Images.Contains(imageIdentifier));
        Assert.Empty(harness.ImagesInRange(address, 0x2000));
        harness.Shutdown();
    }
}
