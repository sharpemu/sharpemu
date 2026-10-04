// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The prefix of a saved driver pipeline cache: the build and device it belongs to, then the payload hash.
public static class PipelineCacheSignature
{
    public const int UuidSize = 16;

    public static string BuildVersion =>
        typeof(PipelineCacheSignature).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
        typeof(PipelineCacheSignature).Assembly.GetName().Version?.ToString() ?? "unknown";

    // Retained for diagnostics, but deliberately excluded from BuildIdentity.
    // SharpEmu.Libs also contains audio, kernel, input and service HLE code; using
    // its MVID made an unrelated HLE edit cold-start every title's driver cache.
    public static Guid BuildModuleVersionId => typeof(PipelineCacheSignature).Module.ModuleVersionId;

    // Vulkan validates the opaque cache payload against the device, driver and
    // pipelineCacheUUID. Keep the host-side namespace tied to the two assemblies
    // that actually translate guest programs instead of the entire HLE library.
    public static string BuildIdentity =>
        $"{typeof(Gen5ShaderTranslator).Module.ModuleVersionId:N}-" +
        $"{typeof(Gen5SpirvTranslator).Module.ModuleVersionId:N}";

    public static string Build(uint vendorId, uint deviceId, uint driverVersion, ReadOnlySpan<byte> pipelineCacheUuid)
        => Build(vendorId, deviceId, driverVersion, pipelineCacheUuid, BuildIdentity);

    internal static string Build(
        uint vendorId,
        uint deviceId,
        uint driverVersion,
        ReadOnlySpan<byte> pipelineCacheUuid,
        string buildIdentity)
    {
        if (string.IsNullOrWhiteSpace(buildIdentity))
        {
            throw new ArgumentException("A pipeline-cache build identity is required.", nameof(buildIdentity));
        }

        var uuid = new StringBuilder(UuidSize * 2);
        for (var index = 0; index < UuidSize && index < pipelineCacheUuid.Length; index++)
        {
            uuid.Append(pipelineCacheUuid[index].ToString("x2"));
        }

        return $"SharpEmuPC2:{BuildVersion}:{buildIdentity}:{vendorId:x8}:{deviceId:x8}:{driverVersion:x8}:{uuid}\n";
    }

    // The full signature is also the storage namespace. Hashing it keeps path
    // components short while retaining the complete signature inside the file.
    internal static string StorageKey(string signature) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(signature))).ToLowerInvariant();

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
    public static bool TryUnwrap(string signature, ReadOnlySpan<byte> file, out byte[] payload)
    {
        if (!TryUnwrapFile(file, out var storedSignature, out payload) ||
            !string.Equals(signature, storedSignature, StringComparison.Ordinal))
        {
            payload = [];
            return false;
        }

        return true;
    }

    // Driver cache data may safely survive an emulator-only rebuild when the
    // Vulkan implementation's compatibility tuple is unchanged. The caller
    // re-wraps the verified payload with the current exact signature.
    internal static bool TryUnwrapDeviceCompatible(
        string expectedSignature,
        ReadOnlySpan<byte> file,
        out byte[] payload)
    {
        payload = [];
        if (!TryParseCompatibility(expectedSignature, out var expected) ||
            !TryUnwrapFile(file, out var storedSignature, out var storedPayload) ||
            !TryParseCompatibility(storedSignature, out var stored) ||
            expected != stored)
        {
            return false;
        }

        payload = storedPayload;
        return true;
    }

    private static bool TryUnwrapFile(
        ReadOnlySpan<byte> file,
        out string signature,
        out byte[] payload)
    {
        signature = string.Empty;
        payload = [];
        if (file.Length > int.MaxValue)
        {
            return false;
        }

        // A PC2 signature is short and newline-terminated. Bounding the scan
        // prevents a malformed cache from turning arbitrary binary data into a
        // very large managed string.
        var searchLength = Math.Min(file.Length, 2_048);
        var newlineIndex = file[..searchLength].IndexOf((byte)'\n');
        if (newlineIndex < 0)
        {
            return false;
        }

        var prefixLength = newlineIndex + 1;
        if (file.Length < prefixLength + sizeof(ulong))
        {
            return false;
        }

        var expectedHash = BinaryPrimitives.ReadUInt64LittleEndian(file[prefixLength..]);
        var data = file[(prefixLength + sizeof(ulong))..];
        if (XxHash3.HashToUInt64(data) != expectedHash)
        {
            return false;
        }

        signature = Encoding.ASCII.GetString(file[..prefixLength]);
        payload = data.ToArray();
        return true;
    }

    private static bool TryParseCompatibility(
        string signature,
        out DeviceCompatibility compatibility)
    {
        compatibility = default;
        if (!signature.EndsWith('\n') ||
            !signature.StartsWith("SharpEmuPC2:", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = signature[..^1].Split(':');
        if (parts.Length != 7 ||
            parts[2].Length == 0 ||
            !uint.TryParse(parts[3], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var vendorId) ||
            !uint.TryParse(parts[4], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var deviceId) ||
            !uint.TryParse(parts[5], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var driverVersion) ||
            parts[6].Length != UuidSize * 2 ||
            !parts[6].All(Uri.IsHexDigit))
        {
            return false;
        }

        compatibility = new DeviceCompatibility(
            vendorId,
            deviceId,
            driverVersion,
            parts[6]);
        return true;
    }

    private readonly record struct DeviceCompatibility(
        uint VendorId,
        uint DeviceId,
        uint DriverVersion,
        string PipelineCacheUuid);
}
