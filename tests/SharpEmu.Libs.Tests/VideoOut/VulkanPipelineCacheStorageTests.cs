// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPipelineCacheStorageTests
{
    [Fact]
    public void ResolvePathUsesExecutableUserDirectoryAndTitleId()
    {
        var path = VulkanPipelineCacheStorage.ResolvePath("PPSA02929", configuredPath: null);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "user",
                "pipeline_cache",
                "PPSA02929",
                "vulkan-pipeline-cache.bin")),
            path);
    }

    [Fact]
    public void ResolvePathSanitizesTitleId()
    {
        var path = VulkanPipelineCacheStorage.ResolvePath("ppsa/02:929", configuredPath: null);

        Assert.EndsWith(
            Path.Combine("PPSA_02_929", "vulkan-pipeline-cache.bin"),
            path,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePathPreservesConfiguredOverride()
    {
        var configured = Path.Combine(Path.GetTempPath(), "SharpEmuTests", "custom-cache.bin");

        Assert.Equal(
            Path.GetFullPath(configured),
            VulkanPipelineCacheStorage.ResolvePath("PPSA02929", configured));
    }

    [Fact]
    public void ImportLegacyCacheDoesNotOverwriteExistingTitleCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        var legacyPath = Path.Combine(root, "legacy.bin");
        var destinationPath = Path.Combine(root, "user", "pipeline_cache", "PPSA02929", "cache.bin");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(legacyPath, [1, 2, 3]);

            Assert.True(VulkanPipelineCacheStorage.ImportLegacyCache(legacyPath, destinationPath));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destinationPath));
            Assert.False(File.Exists(legacyPath));

            File.WriteAllBytes(legacyPath, [4, 5, 6]);
            Assert.False(VulkanPipelineCacheStorage.ImportLegacyCache(legacyPath, destinationPath));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destinationPath));
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
