// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPipelineCacheShardTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void ShardsLoadLazilyShareInitializationAndRecoverFromInvalidData()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        using var presenter = new PresenterUnderTest(vulkan);
        presenter.SetField("_pipelineCacheShardDirectory", directory);
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "test.bin");
            File.WriteAllBytes(path, [1, 2, 3]);
            var source = (Lazy<PipelineCache>)presenter.InvokeMethod("GetGuestPipelineCacheSource", "test")!;
            Assert.False(source.IsValueCreated);
            Assert.Same(source, presenter.InvokeMethod("GetGuestPipelineCacheSource", "test"));
            Assert.NotEqual(0ul, source.Value.Handle);
            presenter.InvokeMethod("SaveGuestPipelineCaches");
            Assert.True(new FileInfo(path).Length > 3);
            presenter.InvokeMethod("DestroyGuestPipelineCaches");
            var reloaded = (Lazy<PipelineCache>)presenter.InvokeMethod("GetGuestPipelineCacheSource", "test")!;
            Assert.NotSame(source, reloaded);
            Assert.NotEqual(0ul, reloaded.Value.Handle);
        }
        finally
        {
            presenter.InvokeMethod("DestroyGuestPipelineCaches");
            Directory.Delete(directory, recursive: true);
        }
    }
}
