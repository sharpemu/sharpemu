// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Theory]
    [InlineData(ShaderStageKind.Vertex)]
    [InlineData(ShaderStageKind.Pixel)]
    [InlineData(ShaderStageKind.Compute)]
    public void BindResources_ImageUploadKeepsTheStorageViewOnTheCurrentOwner(ShaderStageKind stage)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        using var fatal = new FatalScope();
        presenter.SetField("_minStorageBufferOffsetAlignment", 256UL);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x20000, ReadWrite);
        var sampled = RegisterWords.Texture(address, GuestPixelFormat.Bits8_8_8_8UNorm, 128, 128);
        var info = new ShaderResourceInfo
        {
            Buffers = [new BufferResource { Read = true, MaxByteExtent = 32 }],
            Images = [new ImageResource
            {
                Dimension = ImageDimension.Dim2D,
                ResourceClass = global::SharpEmu.ShaderCompiler.Resources.ImageResourceClass.Sampled,
                NumericClass = ImageNumericClass.Float,
                Read = true,
            }],
        };
        var snapshot = new ResourceSnapshot
        {
            Buffers = [[(uint)address, (uint)(address >> 32), 32, 0]],
            Images = [sampled],
        };
        var program = FixedProgramProvider.EmptyProgram(stage, 0x188, info);
        presenter.Run(() =>
        {
            // One small persistent GPU-written owner overlaps a larger texture.
            var (original, offset) = harness.Cache.ObtainBuffer(address, 32, isWritten: true);
            original.Fill(offset, 32, 0x11223344);
            var texture = ImageRequestBuilders.Texture(sampled,
                new ShaderImageShape(false, false, false, false, TextureNumericClass.Float)).Request;
            Assert.True(texture.Description.Data.Size > original.Size);
            using var preparation = presenter.RenderHost.BeginPreparation();
            var prepared = presenter.RenderHost.PrepareBindings(new ShaderStageResources(program, snapshot));
            presenter.RenderHost.BindResources(prepared);
            var current = harness.Cache.GetBuffer(harness.Cache.FindBuffer(address, 32));
            Assert.NotSame(original, current);
            // Inspect metadata only: never submit a shader with a known stale handle.
            Assert.Equal(current.Handle.Handle, PreparedStorageHandle(prepared, 0));
        });
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void Preparation_AcquiredNativeBufferSurvivesALaterResourcePublicationWait()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        using var fatal = new FatalScope();
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x20000, ReadWrite);
        presenter.Run(() =>
        {
            var pending = presenter.GetField<PendingGuestPublications>("_guestPublications");
            var publicationAddress = address + 0x18000;
            var publication = pending.AddWrite(33, publicationAddress, 4);
            pending.Register(publication, harness.Scheduler.CurrentTick);
            harness.Scheduler.QueueMemoryPublication(() => pending.Publish(publication, () =>
                Assert.True(harness.Memory.TryWrite(publicationAddress, BitConverter.GetBytes(0xAABBCCDDu)))));
            using var preparation = presenter.RenderHost.BeginPreparation();
            _ = presenter.RenderHost.ObtainBuffer(address, 32, isWritten: true);
            var original = harness.Cache.GetBuffer(harness.Cache.FindBuffer(address, 32));
            var capturedHandle = original.Handle.Handle;
            var replacement = harness.Cache.GetBuffer(harness.Cache.FindBuffer(address, 0x10000));
            Assert.NotSame(original, replacement);
            // The next resource completes its held write through the normal host path.
            // This submits the unregister tick and runs its retirement callback.
            _ = presenter.RenderHost.ObtainBuffer(publicationAddress, 4, isWritten: false);
            Assert.Equal(0, pending.Count);
            Assert.Equal(capturedHandle, original.Handle.Handle);
        });
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void RetainingAnOldOwnerDoesNotForwardLaterWritesToItsReplacement()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        using var fatal = new FatalScope();
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x20000, ReadWrite);
        var original = presenter.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(address, 32, isWritten: true);
            buffer.Fill(offset, 32, 0x11223344);
            buffer.RetainForeignRead();
            return buffer;
        });
        try
        {
            var replacement = presenter.Run(() =>
            {
                var current = harness.Cache.GetBuffer(harness.Cache.FindBuffer(address, 0x10000));
                Assert.NotSame(original, current);
                // The earlier native binding still names original, after the merge copy.
                // Fill stands in for its late GPU write without needing a shader module.
                original.Fill(original.Offset(address), 4, 0x55667788);
                harness.Scheduler.Finish();
                Assert.NotEqual(0UL, original.Handle.Handle);
                return current;
            });
            Assert.Equal(0x55667788u, BitConverter.ToUInt32(harness.ReadBack(original, original.Offset(address), 4)));
            Assert.Equal(0x11223344u, BitConverter.ToUInt32(harness.ReadBack(replacement, replacement.Offset(address), 4)));
        }
        finally
        {
            presenter.Run(original.ReleaseForeignRead);
        }
        harness.Shutdown();
    }

    private static ulong PreparedStorageHandle(IPreparedBindings prepared, int slot)
    {
        const System.Reflection.BindingFlags members = PresenterUnderTest.InstanceMembers;
        var descriptors = prepared.GetType().GetProperty("Descriptors", members)!.GetValue(prepared)!;
        var views = (Array)descriptors.GetType().GetField("Buffers", members)!.GetValue(descriptors)!;
        var view = views.GetValue(slot)!;
        return ((Silk.NET.Vulkan.Buffer)view.GetType().GetProperty("Buffer", members)!.GetValue(view)!).Handle;
    }
}
