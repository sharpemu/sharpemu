// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcSubmissionShutdownTests
{
    [Fact]
    public void Shutdown_RejectsGuestSubmissionsWithoutReadingPackets()
    {
        var memory = new FakeCpuMemory(0x10000000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        HostSessionControl.ResetShutdownRequest();
        try
        {
            HostSessionControl.RequestShutdown("submission-test");
            var cancelled = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED;
            Assert.Equal(cancelled, AgcExports.DriverSubmitDcb(context));
            Assert.Equal(cancelled, AgcExports.DriverSubmitAcb(context));
            Assert.Equal(cancelled, AgcExports.DriverSubmitMultiDcbs(context));
        }
        finally
        {
            HostSessionControl.ResetShutdownRequest();
        }
    }

    [Fact]
    public void Shutdown_DoesNotRunHeadlessSubmissions()
    {
        var memory = new FakeCpuMemory(0x10000000, 0x1000);
        HostSessionControl.ResetShutdownRequest();
        try
        {
            HostSessionControl.RequestShutdown("submission-test");
            Assert.Throws<OperationCanceledException>(() =>
                VulkanVideoPresenter.SubmitCommandStream(memory, 0, 0x10000000, 1, 1, null));
            Assert.Equal(IdleOutcome.Cancelled, VulkanVideoPresenter.SubmitDone(memory));
        }
        finally
        {
            HostSessionControl.ResetShutdownRequest();
        }
    }
}
