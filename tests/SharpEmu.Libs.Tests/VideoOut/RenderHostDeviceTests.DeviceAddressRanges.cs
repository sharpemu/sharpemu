// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Fact]
    public void AReusedStageCarriesNoCallRecordsOfThePreviousProgram()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            var named = harness.MapBacked(0x10000, ReadWrite);
            var program = FixedProgramProvider.EmptyProgram(ShaderStageKind.Compute, 0xCA11);
            var stage = new ShaderStageResources(program, new ResourceSnapshot());
            presenter.Run(() =>
            {
                // The pooled stage last served a program whose call records named a written range.
                var used = presenter.InvokeMethod("RentStage", stage, program)!;
                var type = used.GetType();
                type.GetProperty("CallRecordWords")!.SetValue(used, new uint[] { 1, 0, 0, 0 });
                type.GetProperty("CallRecordRanges")!.SetValue(used, new DeviceAddressRange[] { new(uint.MaxValue, named, 0x1000, true, true) });
                presenter.InvokeMethod("ReturnStageScratch", used);

                using var preparation = presenter.RenderHost.BeginPreparation();
                var prepared = presenter.RenderHost.PrepareBindings(stage);
                Assert.Same(used, prepared);
                Assert.Empty((uint[])type.GetProperty("CallRecordWords")!.GetValue(prepared)!);
                Assert.Empty((DeviceAddressRange[])type.GetProperty("CallRecordRanges")!.GetValue(prepared)!);
                presenter.RenderHost.BindResources(prepared);
                Assert.False(harness.Cache.IsRegionRegistered(named, 0x1000));
                Assert.False(harness.Cache.HasGpuDirtyBytes(named, 0x1000));
            });
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }

        harness.Shutdown();
    }
}
