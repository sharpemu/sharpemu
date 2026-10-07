// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    [Fact]
    public void VolumeUploadDumps_PreserveBytesAndLimitRepeatedUploads()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_DUMP_VOLUME_UPLOADS") != "1") return;
        if (!GatePrerequisites.Ready(_vulkan)) return;
        Assert.True(_vulkan.ValidationEnabled);
        var directory = Path.Combine(AppContext.BaseDirectory, "user", "logs", "volume-uploads");
        var previous = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories).ToHashSet()
            : new HashSet<string>();
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var bytes = Bytes(0x12345678u);
        harness.Write(address, bytes);
        var request = LinearRequest(address, 256, Format.R32Uint, GuestPixelFormat.Bits32UInt,
            GuestImageType.Color3D, new Extent3D(1, 1, 1), 1, sizeof(uint), 1);
        var identifier = harness.Acquire(ref request);
        for (var upload = 1; upload < 5; upload++)
        {
            harness.Worker.Run(() => harness.Image(identifier).MarkBufferModified());
            Assert.Equal(identifier, harness.Acquire(ref request));
        }
        Assert.Equal(bytes, harness.ReadImageBytes(harness.Image(identifier)));
        harness.Shutdown();
        var manifests = Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories)
            .Where(path => !previous.Contains(path)).Order().ToArray();
        Assert.Equal(4, manifests.Length);
        foreach (var path in manifests)
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(256UL, manifest.RootElement.GetProperty("SourceSize").GetUInt64());
            Assert.Equal(256UL, manifest.RootElement.GetProperty("LinearSize").GetUInt64());
            Assert.True(manifest.RootElement.GetProperty("BackingReadable").GetBoolean());
            foreach (var suffix in new[] { ".backing.bin", ".tiled.bin", ".linear.bin" })
            {
                var dumped = File.ReadAllBytes(Path.ChangeExtension(path, null) + suffix);
                Assert.Equal(256, dumped.Length);
                Assert.Equal(bytes, dumped[..4]);
            }
        }
    }
}
