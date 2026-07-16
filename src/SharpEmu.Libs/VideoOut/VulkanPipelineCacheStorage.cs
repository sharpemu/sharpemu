// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.VideoOut;

internal static class VulkanPipelineCacheStorage
{
    internal const int UuidSize = 16;
    internal const long MaximumCacheSize = 256L * 1024L * 1024L;

    private const int HeaderSize = 32;
    private const uint HeaderVersionOne = 1;

    internal static string GetFileName(
        uint vendorId,
        uint deviceId,
        ReadOnlySpan<byte> pipelineCacheUuid)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(pipelineCacheUuid.Length, UuidSize);
        return $"vulkan-{vendorId:X8}-{deviceId:X8}-{Convert.ToHexString(pipelineCacheUuid)}.bin";
    }

    internal static bool TryReadCompatible(
        string path,
        uint vendorId,
        uint deviceId,
        ReadOnlySpan<byte> pipelineCacheUuid,
        out byte[] data)
    {
        data = [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < HeaderSize or > MaximumCacheSize)
        {
            return false;
        }

        data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        if (HasCompatibleHeader(data, vendorId, deviceId, pipelineCacheUuid))
        {
            return true;
        }

        data = [];
        return false;
    }

    internal static bool HasCompatibleHeader(
        ReadOnlySpan<byte> data,
        uint vendorId,
        uint deviceId,
        ReadOnlySpan<byte> pipelineCacheUuid)
    {
        return pipelineCacheUuid.Length == UuidSize &&
               data.Length >= HeaderSize &&
               BinaryPrimitives.ReadUInt32LittleEndian(data) == HeaderSize &&
               BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) == HeaderVersionOne &&
               BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) == vendorId &&
               BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) == deviceId &&
               data.Slice(16, UuidSize).SequenceEqual(pipelineCacheUuid);
    }
}
