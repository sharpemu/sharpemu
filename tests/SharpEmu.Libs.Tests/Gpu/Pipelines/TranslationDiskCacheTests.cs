// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(TranslationDiskCacheCollection.Name)]
public sealed class TranslationDiskCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
    private readonly VulkanGuestGpuBackend _backend = new();

    public TranslationDiskCacheTests() => TranslationDiskCache.UseGameFolder(_directory, "test-stamp");

    public void Dispose()
    {
        TranslationDiskCache.UseGameFolder(null, null);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static string Key(char digit) => new(digit, 64);

    [Theory]
    [InlineData("SHARPEMU_TRANSLATION_CACHE", true)]
    [InlineData("SHARPEMU_VK_PIPELINE_CACHE_PATH", true)]
    [InlineData("SHARPEMU_SHADER_MODULE_DUMP", true)]
    [InlineData("SHARPEMU_PROFILE_RENDER", true)]
    [InlineData("sharpemu_log_agc", true)]
    [InlineData("SHARPEMU_SHADER_OPTIMIZATIONS", false)]
    [InlineData("SHARPEMU_BRANCHES_WITH_WRITES", false)]
    public void PathsDumpsLogsAndProfilesStayOutOfTheKey(string name, bool ignored) =>
        Assert.Equal(ignored, TranslationDiskCache.IgnoredVariable(name));

    [Theory]
    [InlineData("SHARPEMU_CAPTURE_PIXEL_VGPR_ADDRESS", true)]
    [InlineData("SHARPEMU_FORCE_TITLE_EARLY_COLOR", true)]
    [InlineData("SHARPEMU_FORCE_PACKED_EXPORT_STORE_ONE", true)]
    [InlineData("sharpemu_trace_packed_export_address", true)]
    [InlineData("SHARPEMU_PREPARED_ADDRESS_RANGES", false)]
    [InlineData("SHARPEMU_CRASH_CAPTURE", false)]
    [InlineData("SHARPEMU_TRACE_MUTEX_ADDRESS", false)]
    [InlineData("SHARPEMU_SHADER_OPTIMIZATIONS", false)]
    public void OnlyTheTranslatorsAddressSwitchesPutTheAddressInTheKey(string name, bool selects) =>
        Assert.Equal(selects, TranslationDiskCache.AddressSelectsTranslation(name));

    [Fact]
    public void AStoredTranslationLoadsAsTheSameShader()
    {
        var shader = new VulkanCompiledGuestShader([1, 2, 3, 4]) { InputLocationMask = 0x5 };
        TranslationDiskCache.Store(Key('A'), shader);

        var loaded = TranslationDiskCache.TryLoad(Key('A'), _backend);

        Assert.NotNull(loaded);
        Assert.Equal(shader.Payload, loaded.Payload);
        Assert.Equal(0x5u, loaded.InputLocationMask);
    }

    [Fact]
    public void AnotherKeyOrADamagedEntryIsAMiss()
    {
        TranslationDiskCache.Store(Key('B'), new VulkanCompiledGuestShader([9, 8, 7, 6]));
        Assert.Null(TranslationDiskCache.TryLoad(Key('C'), _backend));

        var path = Path.Combine(TranslationDiskCache.Folder!, Key('B') + ".spvc");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        Assert.Null(TranslationDiskCache.TryLoad(Key('B'), _backend));
    }

    // A checksummed entry whose payload is shorter than its declared length is a miss.
    [Theory]
    [InlineData(1, 0)]
    [InlineData(4, 3)]
    [InlineData(64, 4)]
    public void AChecksummedEntryWithAShortPayloadIsAMiss(int declaredLength, int actualLength)
    {
        var key = Key('D');
        WritePayloadEntry(key, declaredLength, Enumerable.Range(0, actualLength).Select(value => (byte)value).ToArray());

        Assert.Null(TranslationDiskCache.TryLoad(key, _backend));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 4)]
    [InlineData(3, 4)]
    public void ACompletePayloadKeepsItsExistingAdmissionContract(int declaredLength, int actualLength)
    {
        var key = Key('E');
        var payload = Enumerable.Range(0, actualLength).Select(value => (byte)value).ToArray();
        WritePayloadEntry(key, declaredLength, payload);

        var loaded = TranslationDiskCache.TryLoad(key, _backend);

        Assert.NotNull(loaded);
        Assert.Equal(payload.Take(declaredLength).ToArray(), loaded.Payload);
        Assert.Null(loaded.InputLocationMask);
    }

    // A complete version-1 envelope with a valid checksum and compressed stream; the declared payload length varies
    // independently of the bytes present.
    private static void WritePayloadEntry(string key, int declaredLength, byte[] payload)
    {
        using var content = new MemoryStream();
        using (var writer = new BinaryWriter(content, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1);
            writer.Write(key);
            writer.Write(false); // No input location mask.
            writer.Write(declaredLength);
            writer.Write(payload);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(content.GetBuffer(), 0, (int)content.Length);
        }

        var bytes = compressed.ToArray();
        File.WriteAllBytes(Path.Combine(TranslationDiskCache.Folder!, key + ".spvc"), [.. SHA256.HashData(bytes), .. bytes]);
    }

    [Fact]
    public void ABuildUsesItsOwnFolderAndKeepsOnlyTheNewestOtherBuild()
    {
        var game = Path.Combine(_directory, "game");
        Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game, "flat.spvc"), [1]);
        var now = DateTime.UtcNow;
        foreach (var (name, hoursAgo) in new[] { ("old1", 30), ("old2", 20), ("old3", 10) })
        {
            var build = Directory.CreateDirectory(Path.Combine(game, name));
            File.WriteAllBytes(Path.Combine(build.FullName, "entry.spvc"), [2]);
            Directory.SetLastWriteTimeUtc(build.FullName, now.AddHours(-hoursAgo));
        }

        TranslationDiskCache.UseGameFolder(game, "another-stamp");

        var current = TranslationDiskCache.Folder!;
        Assert.Equal(game, Path.GetDirectoryName(current));
        Assert.True(Directory.Exists(current));
        Assert.True(Directory.Exists(Path.Combine(game, "old3")));
        Assert.False(Directory.Exists(Path.Combine(game, "old2")));
        Assert.False(Directory.Exists(Path.Combine(game, "old1")));
        Assert.False(File.Exists(Path.Combine(game, "flat.spvc")));
    }

    [Fact]
    public void TrimDropsTheEntriesUsedLongestAgo()
    {
        Directory.CreateDirectory(_directory);
        var now = DateTime.UtcNow;
        foreach (var (name, hoursAgo) in new[] { ("a", 10), ("b", 9), ("c", 8), ("d", 7) })
        {
            var path = Path.Combine(_directory, name + ".spvc");
            File.WriteAllBytes(path, new byte[100]);
            File.SetLastWriteTimeUtc(path, now.AddHours(-hoursAgo));
            File.SetLastAccessTimeUtc(path, now.AddHours(-hoursAgo));
        }

        Assert.Equal(200, TranslationDiskCache.Trim(_directory, 100, 450, 300));
        Assert.False(File.Exists(Path.Combine(_directory, "a.spvc")));
        Assert.False(File.Exists(Path.Combine(_directory, "b.spvc")));
        Assert.True(File.Exists(Path.Combine(_directory, "d.spvc")));
    }
}

// The cache's folder is process-wide: its tests run one at a time.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TranslationDiskCacheCollection
{
    public const string Name = "TranslationDiskCache";
}
