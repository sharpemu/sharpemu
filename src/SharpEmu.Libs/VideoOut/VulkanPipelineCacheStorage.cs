// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

internal static class VulkanPipelineCacheStorage
{
    private const string CacheFileName = "vulkan-pipeline-cache.bin";

    // A guest shader can produce many different native modules as translation
    // and specialization change. Importing their shared driver blob recompiles
    // obsolete MSL on MoltenVK, so native cache buckets identify emitted code.
    internal static string CompiledShaderIdentity(ReadOnlySpan<byte> spirv) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(spirv));

    internal static string ResolvePath(string? titleId, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configuredPath));
        }

        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "user",
            "pipeline_cache",
            SanitizeTitleId(titleId),
            CacheFileName));
    }

    internal static string GetLegacyPath()
    {
        var root = OperatingSystem.IsMacOS()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Caches")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "SharpEmu", CacheFileName);
    }

    internal static bool ImportLegacyCache(string legacyPath, string destinationPath)
    {
        if (File.Exists(destinationPath) || !File.Exists(legacyPath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            File.Copy(legacyPath, destinationPath, overwrite: false);
            try
            {
                File.Delete(legacyPath);
            }
            catch (IOException)
            {
                // The destination is valid; a locked legacy cache can remain
                // as an unused artifact without affecting future writes.
            }
            catch (UnauthorizedAccessException)
            {
                // See above. Cache migration must not block game startup.
            }

            return true;
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            return false;
        }
    }

    private static string SanitizeTitleId(string? titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return "UNKNOWN";
        }

        var source = titleId.Trim();
        Span<char> sanitized = source.Length <= 128
            ? stackalloc char[source.Length]
            : new char[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            var value = source[index];
            sanitized[index] = char.IsAsciiLetterOrDigit(value) || value is '-' or '_'
                ? char.ToUpperInvariant(value)
                : '_';
        }

        return new string(sanitized);
    }
}
