// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GraphicsPipelineProfile_UsesProfileGateAndPreservesRenderedOutput(bool enabled)
    {
        if (!Ready()) return;
        using var profile = new VulkanCommandProfile(_vulkan.Vk, _vulkan.Physical, _vulkan.Device,
            _vulkan.QueueFamily, write: _ => { });
        using var presenter = new PresenterUnderTest(_vulkan);
        if (enabled) presenter.SetField("_gpuCommandProfile", profile);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        harness.Write(vertices, Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
        var provider = new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices);
        var executor = new RenderExecutor(presenter.RenderHost, provider);
        using var output = new StringWriter();
        var previousError = Console.Error;
        try
        {
            Console.SetError(output);
            presenter.Run(() => executor.DrawAuto(1, Banks(words), Draw()));
            presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
            harness.Finish();
        }
        finally
        {
            Console.SetError(previousError);
        }

        var reports = output.ToString().Split('\n').Where(line =>
            line.StartsWith("[PERF][GPU_GRAPHICS_STATE]", StringComparison.Ordinal)).ToArray();
        if (enabled)
        {
            var report = Assert.Single(reports);
            Assert.Contains("pipeline=1 stage=Vertex topology=TriangleList samples=1", report);
            Assert.Contains("sample_shading=False cull_front=False cull_back=False depth_clip=True", report);
            Assert.Contains("color_formats=R8G8B8A8Unorm depth_format=Undefined stencil_format=Undefined", report);
        }
        else
        {
            Assert.Empty(reports);
        }
        var pixels = harness.ReadImageBytes(TargetImage(presenter, words));
        Assert.Equal(Red, Pixel(pixels, Size / 2, Size / 2));
        harness.Shutdown();
    }
}
