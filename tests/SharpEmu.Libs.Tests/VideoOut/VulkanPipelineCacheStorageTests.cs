// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPipelineCacheStorageTests
{
    [Fact]
    public void ResolvePathUsesWritableStableRootTitleAndCompatibilityKey()
    {
        var preferredRoot = Path.Combine(Path.GetTempPath(), "SharpEmuTests", "preferred");
        var fallbackRoot = Path.Combine(Path.GetTempPath(), "SharpEmuTests", "fallback");
        var prepared = new List<string>();
        var path = VulkanPipelineCacheStorage.ResolvePath(
            "PPSA02929",
            "pc2-abcdef",
            configuredPath: null,
            preferredRoot: preferredRoot,
            fallbackRoot: fallbackRoot,
            prepareRoot: candidate =>
            {
                prepared.Add(candidate);
                return true;
            });

        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                preferredRoot,
                "PPSA02929",
                "pc2-abcdef",
                "vulkan-pipeline-cache.bin")),
            path);
        Assert.Equal(new[] { preferredRoot }, prepared);
    }

    [Fact]
    public void ResolvePathFallsBackWhenStableRootIsNotWritable()
    {
        var preferredRoot = Path.Combine(Path.GetTempPath(), "SharpEmuTests", "preferred");
        var fallbackRoot = Path.Combine(Path.GetTempPath(), "SharpEmuTests", "fallback");
        var prepared = new List<string>();
        var path = VulkanPipelineCacheStorage.ResolvePath(
            "PPSA02929",
            "pc2-abcdef",
            configuredPath: null,
            preferredRoot: preferredRoot,
            fallbackRoot: fallbackRoot,
            prepareRoot: candidate =>
            {
                prepared.Add(candidate);
                return candidate == fallbackRoot;
            });

        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                fallbackRoot,
                "PPSA02929",
                "pc2-abcdef",
                "vulkan-pipeline-cache.bin")),
            path);
        Assert.Equal(new[] { preferredRoot, fallbackRoot }, prepared);
    }

    [Fact]
    public void ResolvePathSanitizesTitleAndCompatibilityComponents()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpEmuTests", "preferred");
        var path = VulkanPipelineCacheStorage.ResolvePath(
            "ppsa/02:929",
            "pc2/build:key",
            configuredPath: null,
            preferredRoot: root,
            fallbackRoot: root,
            prepareRoot: _ => true);

        Assert.EndsWith(
            Path.Combine("PPSA_02_929", "pc2_build_key", "vulkan-pipeline-cache.bin"),
            path,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePathPreservesConfiguredFullFileOverrideWithoutProbingRoots()
    {
        var configured = Path.Combine(Path.GetTempPath(), "SharpEmuTests", "custom-cache.bin");

        Assert.Equal(
            Path.GetFullPath(configured),
            VulkanPipelineCacheStorage.ResolvePath(
                "PPSA02929",
                compatibilityKey: string.Empty,
                configuredPath: configured,
                preferredRoot: "unused-preferred",
                fallbackRoot: "unused-fallback",
                prepareRoot: _ => throw new InvalidOperationException("The override must not probe cache roots.")));
    }

    [Fact]
    public void ImportCompatibleCacheCopiesWithoutDeletingSourceOrOverwritingDestination()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(root, "fallback.bin");
        var destinationPath = Path.Combine(root, "user", "pipeline_cache", "PPSA02929", "cache.bin");
        var uuid = Enumerable.Range(0, PipelineCacheSignature.UuidSize).Select(index => (byte)index).ToArray();
        var signature = PipelineCacheSignature.Build(0x1002, 0x73BF, 0x00401000, uuid);
        var sourceFile = PipelineCacheSignature.Wrap(signature, [1, 2, 3]);
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(sourcePath, sourceFile);

            Assert.True(VulkanPipelineCacheStorage.ImportCompatibleCache(sourcePath, destinationPath, signature));
            Assert.Equal(sourceFile, File.ReadAllBytes(destinationPath));
            Assert.Equal(sourceFile, File.ReadAllBytes(sourcePath));

            File.WriteAllBytes(sourcePath, PipelineCacheSignature.Wrap(signature, [4, 5, 6]));
            Assert.False(VulkanPipelineCacheStorage.ImportCompatibleCache(sourcePath, destinationPath, signature));
            Assert.Equal(sourceFile, File.ReadAllBytes(destinationPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ImportCompatibleCacheRejectsPc1AndMismatchedPc2Files()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(root, "fallback.bin");
        var destinationPath = Path.Combine(root, "stable.bin");
        var uuid = Enumerable.Range(0, PipelineCacheSignature.UuidSize).Select(index => (byte)index).ToArray();
        var expected = PipelineCacheSignature.Build(0x1002, 0x73BF, 0x00401000, uuid);
        var other = PipelineCacheSignature.Build(0x1002, 0x73BF, 0x00402000, uuid);
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(sourcePath, PipelineCacheSignature.Wrap(other, [1, 2, 3]));

            Assert.False(VulkanPipelineCacheStorage.ImportCompatibleCache(sourcePath, destinationPath, expected));
            Assert.False(File.Exists(destinationPath));
            Assert.True(File.Exists(sourcePath));

            var pc1 = $"SharpEmuPC1:{PipelineCacheSignature.BuildVersion}:legacy\n";
            File.WriteAllBytes(sourcePath, PipelineCacheSignature.Wrap(pc1, [1, 2, 3]));
            Assert.False(VulkanPipelineCacheStorage.ImportCompatibleCache(sourcePath, destinationPath, pc1));
            Assert.False(File.Exists(destinationPath));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ImportCompatibleCacheRewrapsAnEarlierBuildForTheSameDevice()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(root, "old.bin");
        var destinationPath = Path.Combine(root, "new.bin");
        var uuid = Enumerable.Range(0, PipelineCacheSignature.UuidSize).Select(index => (byte)index).ToArray();
        var oldSignature = PipelineCacheSignature.Build(
            0x1002,
            0x73BF,
            0x00401000,
            uuid,
            "11111111111111111111111111111111");
        var newSignature = PipelineCacheSignature.Build(
            0x1002,
            0x73BF,
            0x00401000,
            uuid,
            "22222222222222222222222222222222");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(sourcePath, PipelineCacheSignature.Wrap(oldSignature, [7, 8, 9]));

            Assert.True(VulkanPipelineCacheStorage.ImportCompatibleCache(
                sourcePath,
                destinationPath,
                newSignature));
            Assert.True(PipelineCacheSignature.TryUnwrap(
                newSignature,
                File.ReadAllBytes(destinationPath),
                out var payload));
            Assert.Equal(new byte[] { 7, 8, 9 }, payload);
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ImportNewestCompatibleCacheDiscoversPriorNamespaceWithoutTouchingSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        var titleId = "PPSA02929";
        var sourcePath = Path.Combine(root, titleId, "old-key", "vulkan-pipeline-cache.bin");
        var destinationPath = Path.Combine(root, titleId, "new-key", "vulkan-pipeline-cache.bin");
        var uuid = Enumerable.Range(0, PipelineCacheSignature.UuidSize).Select(index => (byte)index).ToArray();
        var oldSignature = PipelineCacheSignature.Build(
            0x10DE,
            0x2504,
            0x988F8000,
            uuid,
            "11111111111111111111111111111111");
        var newSignature = PipelineCacheSignature.Build(
            0x10DE,
            0x2504,
            0x988F8000,
            uuid,
            "22222222222222222222222222222222");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllBytes(sourcePath, PipelineCacheSignature.Wrap(oldSignature, [4, 5, 6]));

            Assert.True(VulkanPipelineCacheStorage.ImportNewestCompatibleCache(
                titleId,
                destinationPath,
                newSignature,
                [root]));
            Assert.True(PipelineCacheSignature.TryUnwrap(
                newSignature,
                File.ReadAllBytes(destinationPath),
                out var payload));
            Assert.Equal(new byte[] { 4, 5, 6 }, payload);
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
