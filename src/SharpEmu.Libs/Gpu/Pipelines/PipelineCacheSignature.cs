// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Hashing;
using System.Reflection;
using System.Text;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The prefix of a saved driver pipeline cache: the build and device it belongs to, then the payload hash.
public static class PipelineCacheSignature
{
    public const int UuidSize = 16;

    public static string BuildVersion =>
        typeof(PipelineCacheSignature).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
        typeof(PipelineCacheSignature).Assembly.GetName().Version?.ToString() ?? "unknown";

    public static string Build(uint vendorId, uint deviceId, uint driverVersion, ReadOnlySpan<byte> pipelineCacheUuid)
    {
        var uuid = new StringBuilder(UuidSize * 2);
        for (var index = 0; index < UuidSize && index < pipelineCacheUuid.Length; index++)
        {
            uuid.Append(pipelineCacheUuid[index].ToString("x2"));
        }

        return $"SharpEmuPC1:{BuildVersion}:{vendorId:x8}:{deviceId:x8}:{driverVersion:x8}:{uuid}\n";
    }

    // The signature line, the payload hash and the payload.
    public static byte[] Wrap(string signature, ReadOnlySpan<byte> payload)
    {
        var prefix = Encoding.ASCII.GetBytes(signature);
        var file = new byte[prefix.Length + sizeof(ulong) + payload.Length];
        prefix.CopyTo(file, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(prefix.Length), XxHash3.HashToUInt64(payload));
        payload.CopyTo(file.AsSpan(prefix.Length + sizeof(ulong)));
        return file;
    }

    // The payload when the signature and the hash match; false for any other file.
    public static bool TryUnwrap(string signature, ReadOnlySpan<byte> file, out byte[] payload) =>
        TryUnwrap(signature, file, out payload, checkHash: true);

    private static bool TryUnwrap(string signature, ReadOnlySpan<byte> file, out byte[] payload, bool checkHash)
    {
        payload = [];
        if (file.Length > int.MaxValue)
        {
            return false;
        }

        var newline = file.IndexOf((byte)'\n');
        if (newline < 0)
        {
            return false;
        }

        var actualSignature = Encoding.ASCII.GetString(file[..(newline + 1)]);
        if (!string.Equals(actualSignature, signature, StringComparison.Ordinal))
        {
            // Pipeline binaries are keyed by the Vulkan device and driver;
            // the SharpEmu build version only controls which shader records
            // may be present. A newer build can safely reuse an older driver
            // cache because Vulkan validates each pipeline key and ignores
            // stale records. Keep the cache when the hardware suffix matches.
            var expectedParts = signature.TrimEnd('\n').Split(':');
            var actualParts = actualSignature.TrimEnd('\n').Split(':');
            if (expectedParts.Length != actualParts.Length ||
                expectedParts.Length < 6 ||
                !string.Equals(expectedParts[0], actualParts[0], StringComparison.Ordinal) ||
                !expectedParts[2..].SequenceEqual(actualParts[2..], StringComparer.Ordinal))
            {
                return false;
            }
        }

        var hashOffset = newline + 1;
        if (file.Length < hashOffset + sizeof(ulong))
        {
            return false;
        }

        var expectedHash = BinaryPrimitives.ReadUInt64LittleEndian(file[hashOffset..]);
        var data = file[(hashOffset + sizeof(ulong))..];
        if (checkHash && XxHash3.HashToUInt64(data) != expectedHash)
        {
            return false;
        }

        payload = data.ToArray();
        return true;
    }

    // Driver caches of large titles pass 2 GiB, beyond a .NET array, so these stream
    // the same file format from and to native memory in chunks.
    private const int StreamChunk = 64 << 20;

    // Writes the signature line, the payload hash and the payload.
    public static unsafe void WriteTo(Stream stream, string signature, byte* payload, ulong length)
    {
        stream.Write(Encoding.ASCII.GetBytes(signature));
        var hash = new XxHash3();
        for (ulong offset = 0; offset < length; offset += StreamChunk)
            hash.Append(new ReadOnlySpan<byte>(payload + offset, (int)Math.Min(StreamChunk, length - offset)));
        Span<byte> hashBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(hashBytes, hash.GetCurrentHashAsUInt64());
        stream.Write(hashBytes);
        for (ulong offset = 0; offset < length; offset += StreamChunk)
            stream.Write(new ReadOnlySpan<byte>(payload + offset, (int)Math.Min(StreamChunk, length - offset)));
    }

    // Reads the payload into native memory (free it with NativeMemory.Free) when the
    // signature (as TryUnwrap accepts it) and the hash match.
    public static unsafe bool TryReadFrom(Stream stream, string signature, out byte* payload, out ulong length)
    {
        payload = null;
        length = 0;
        var line = new List<byte>(256);
        int value;
        while ((value = stream.ReadByte()) >= 0)
        {
            line.Add((byte)value);
            if (value == '\n' || line.Count > 4096) break;
        }

        if (line.Count == 0 || line[^1] != (byte)'\n' ||
            !TryUnwrap(signature, [.. line, .. new byte[sizeof(ulong)]], out _, checkHash: false))
        {
            return false;
        }

        Span<byte> hashBytes = stackalloc byte[sizeof(ulong)];
        stream.ReadExactly(hashBytes);
        var expectedHash = BinaryPrimitives.ReadUInt64LittleEndian(hashBytes);
        var remaining = (ulong)(stream.Length - stream.Position);
        var data = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)Math.Max(remaining, 1));
        var hash = new XxHash3();
        for (ulong offset = 0; offset < remaining; offset += StreamChunk)
        {
            var chunk = new Span<byte>(data + offset, (int)Math.Min(StreamChunk, remaining - offset));
            stream.ReadExactly(chunk);
            hash.Append(chunk);
        }

        if (hash.GetCurrentHashAsUInt64() != expectedHash)
        {
            System.Runtime.InteropServices.NativeMemory.Free(data);
            return false;
        }

        payload = data;
        length = remaining;
        return true;
    }
}
