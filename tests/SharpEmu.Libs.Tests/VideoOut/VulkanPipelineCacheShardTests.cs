// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
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

    [Fact]
    public void AnUnchangedShardIsNotWrittenAgain()
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
            _ = ((Lazy<PipelineCache>)presenter.InvokeMethod("GetGuestPipelineCacheSource", "test")!).Value;
            presenter.InvokeMethod("SaveGuestPipelineCaches");
            Assert.True(File.Exists(path));

            File.Delete(path);
            presenter.InvokeMethod("GetGuestPipelineCacheSource", "test");
            presenter.InvokeMethod("SaveGuestPipelineCaches");
            Assert.False(File.Exists(path));

            presenter.InvokeMethod("SaveGuestPipelineCaches");
            presenter.InvokeMethod("DestroyGuestPipelineCaches");
            _ = ((Lazy<PipelineCache>)presenter.InvokeMethod("GetGuestPipelineCacheSource", "test")!).Value;
            presenter.InvokeMethod("SaveGuestPipelineCaches");
            Assert.True(File.Exists(path));

            presenter.InvokeMethod("DestroyGuestPipelineCaches");
            _ = ((Lazy<PipelineCache>)presenter.InvokeMethod("GetGuestPipelineCacheSource", "test")!).Value;
            File.Delete(path);
            presenter.InvokeMethod("SaveGuestPipelineCaches");
            Assert.False(File.Exists(path));
        }
        finally
        {
            presenter.InvokeMethod("DestroyGuestPipelineCaches");
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ACheckpointWritesNewShardsAndWaitsForTheNextInterval()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        using var presenter = new PresenterUnderTest(vulkan);
        presenter.SetField("_pipelineCacheShardDirectory", directory);
        presenter.SetField("_pipelineCachePath", Path.Combine(directory, "present.bin"));
        var pending = PresenterUnderTest.PresenterType.GetField("_pendingComputePipelines", PresenterUnderTest.InstanceMembers)!;
        pending.SetValue(presenter.Instance, Activator.CreateInstance(pending.FieldType, nonPublic: true));
        try
        {
            Directory.CreateDirectory(directory);
            _ = ((Lazy<PipelineCache>)presenter.InvokeMethod("GetGuestPipelineCacheSource", "first")!).Value;
            presenter.SetField("_pipelineCacheDirty", true);
            presenter.SetField("_lastPipelineCacheSaveTick", Environment.TickCount64 - 600_000);
            presenter.InvokeMethod("CheckpointPipelineCache");
            Assert.True(File.Exists(Path.Combine(directory, "first.bin")));

            _ = ((Lazy<PipelineCache>)presenter.InvokeMethod("GetGuestPipelineCacheSource", "second")!).Value;
            presenter.SetField("_pipelineCacheDirty", true);
            presenter.InvokeMethod("CheckpointPipelineCache");
            Assert.False(File.Exists(Path.Combine(directory, "second.bin")));

            presenter.SetField("_lastPipelineCacheSaveTick", Environment.TickCount64 - 600_000);
            presenter.SetField("_pipelineCacheRetryTick", 0L);
            presenter.InvokeMethod("CheckpointPipelineCache");
            Assert.True(File.Exists(Path.Combine(directory, "second.bin")));
        }
        finally
        {
            presenter.InvokeMethod("DestroyGuestPipelineCaches");
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ACacheOverItsLimitLeavesAnEmptyCacheForTheNextLaunch()
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
            var cache = ((Lazy<PipelineCache>)presenter.InvokeMethod("GetGuestPipelineCacheSource", "test")!).Value;
            var signature = (string)presenter.InvokeMethod("DriverCacheSignature")!;

            Assert.True((bool)presenter.InvokeMethod("SaveDriverPipelineCache", cache, path, ulong.MaxValue, null)!);
            Assert.True(PipelineCacheSignature.TryUnwrap(signature, File.ReadAllBytes(path), out var saved));
            Assert.NotEmpty(saved);

            Assert.True((bool)presenter.InvokeMethod("SaveDriverPipelineCache", cache, path, 1UL, null)!);
            Assert.True(PipelineCacheSignature.TryUnwrap(signature, File.ReadAllBytes(path), out var reset));
            Assert.Empty(reset);

            File.Delete(path);
            Assert.True((bool)presenter.InvokeMethod("SaveDriverPipelineCache", cache, path, 1UL, null)!);
            Assert.False(File.Exists(path));
        }
        finally
        {
            presenter.InvokeMethod("DestroyGuestPipelineCaches");
            Directory.Delete(directory, recursive: true);
        }
    }
}
