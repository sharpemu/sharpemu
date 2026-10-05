// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

[Collection(SchedulingStateCollection.Name)]
public sealed class DrawImageTypeRejectionTests
{
    [Theory]
    [InlineData(false, ShaderStageKind.Vertex)]
    [InlineData(false, ShaderStageKind.Pixel)]
    [InlineData(true, ShaderStageKind.Vertex)]
    [InlineData(true, ShaderStageKind.Pixel)]
    public void SkipMode_ReleasesPreparationAndRecordsOnlyTheNextValidDraw(bool indexed, ShaderStageKind rejectedStage)
    {
        using var fatal = new FatalScope();
        var host = new RecordingRenderHost();
        var pipelines = new FakePipelineProvider();
        var executor = new RenderExecutor(host, pipelines, strictDrawResources: false);
        host.PreparationFailure = stage => stage.Program!.Stage == rejectedStage ? Mismatch() : null;
        var banks = Banks();
        void Draw()
        {
            if (indexed) executor.DrawIndexed(1, banks, Indexed(3));
            else executor.DrawAuto(1, banks, Auto(3));
        }

        Draw();
        Assert.Equal(0, host.PreparationDepth);
        Assert.Contains("preparation_end", host.Calls);
        Assert.Equal("reset_bindings", host.Calls[^1]);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("bind_resources") ||
            call.StartsWith("acquire_color") || call.StartsWith("commit ") || call.StartsWith("draw"));
        Assert.Empty(pipelines.PipelineRequests);

        host.PreparationFailure = null;
        Draw();
        Assert.Single(host.Calls, call => call.StartsWith(indexed ? "draw_indexed " : "draw "));
        Assert.Equal(0, host.PreparationDepth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrictMode_RemainsFatalAndReleasesPreparation(bool indexed)
    {
        using var fatal = new FatalScope();
        var host = new RecordingRenderHost { PreparationFailure = _ => Mismatch() };
        var executor = new RenderExecutor(host, new FakePipelineProvider(), strictDrawResources: true);
        var banks = Banks();
        var failure = Assert.Throws<RenderExecutorFatalException>(() =>
        {
            if (indexed) executor.DrawIndexed(1, banks, Indexed(3));
            else executor.DrawAuto(1, banks, Auto(3));
        });
        Assert.Contains("imageType=0 viewType=1", failure.Message);
        Assert.Equal(0, host.PreparationDepth);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("bind_resources") || call.StartsWith("draw"));
    }

    [Fact]
    public void SkipMode_DoesNotCatchOtherPreparationFailures()
    {
        using var fatal = new FatalScope();
        var host = new RecordingRenderHost { FailPrepareBindings = true };
        var executor = new RenderExecutor(host, new FakePipelineProvider(), strictDrawResources: false);
        Assert.Throws<RenderExecutorFatalException>(() => executor.DrawIndexed(1, Banks(), Indexed(3)));
        Assert.Equal(0, host.PreparationDepth);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("invalid")]
    public void EnvironmentPolicyStopsByDefault(string? value)
    {
        const string variable = "SHARPEMU_STRICT_COMPUTE";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, value);
            using var fatal = new FatalScope();
            var host = new RecordingRenderHost { PreparationFailure = _ => Mismatch() };
            var executor = new RenderExecutor(host, new FakePipelineProvider());
            if (value == "0") executor.DrawAuto(1, Banks(), Auto(3));
            else Assert.Throws<RenderExecutorFatalException>(() => executor.DrawAuto(1, Banks(), Auto(3)));
            Assert.Equal(0, host.PreparationDepth);
            Assert.DoesNotContain(host.Calls, call => call.StartsWith("draw"));
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    private static DrawImageTypeMismatchException Mismatch() =>
        new(0x1234, 0x213590000, ImageType.Type1D, ImageViewType.Type2D);
}
