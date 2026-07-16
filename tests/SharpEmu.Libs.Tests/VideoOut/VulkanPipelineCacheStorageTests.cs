// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPipelineCacheStorageTests
{
    private const uint VendorId = 0x10DE;
    private const uint DeviceId = 0x2C02;

    private static readonly byte[] CacheUuid = Enumerable.Range(0, 16).Select(static value => (byte)value).ToArray();

    [Fact]
    public void CompatibleCacheIsDeviceKeyedAndLoaded()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var fileName = VulkanPipelineCacheStorage.GetFileName(VendorId, DeviceId, CacheUuid);
            var path = Path.Combine(directory, fileName);
            var expected = CreateCacheData(CacheUuid);
            File.WriteAllBytes(path, expected);

            Assert.Contains(Convert.ToHexString(CacheUuid), fileName, StringComparison.Ordinal);
            Assert.True(VulkanPipelineCacheStorage.TryReadCompatible(
                path,
                VendorId,
                DeviceId,
                CacheUuid,
                out var actual));
            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CacheFromAnotherDriverIsRejected()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "cache.bin");
            File.WriteAllBytes(path, CreateCacheData(new byte[16]));

            Assert.False(VulkanPipelineCacheStorage.TryReadCompatible(
                path,
                VendorId,
                DeviceId,
                CacheUuid,
                out var data));
            Assert.Empty(data);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OversizedCacheIsRejectedBeforeReading()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "cache.bin");
            using (var stream = File.Create(path))
            {
                stream.SetLength(VulkanPipelineCacheStorage.MaximumCacheSize + 1);
            }

            Assert.False(VulkanPipelineCacheStorage.TryReadCompatible(
                path,
                VendorId,
                DeviceId,
                CacheUuid,
                out var data));
            Assert.Empty(data);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] CreateCacheData(ReadOnlySpan<byte> uuid)
    {
        var data = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), VendorId);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), DeviceId);
        uuid.CopyTo(data.AsSpan(16, 16));
        data[32..].AsSpan().Fill(0xA5);
        return data;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sharpemu-pipeline-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
