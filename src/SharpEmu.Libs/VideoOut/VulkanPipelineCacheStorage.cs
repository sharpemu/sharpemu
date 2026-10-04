// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;

namespace SharpEmu.Libs.VideoOut;

internal static class VulkanPipelineCacheStorage
{
    private const string CacheFileName = "vulkan-pipeline-cache.bin";
    private const long MaxCacheFileBytes = 257L * 1024L * 1024L;

    // A guest shader can produce many different native modules as translation
    // and specialization change. Keep the emitted-code identity separate from
    // the driver compatibility key used by the persistent cache.
    internal static string CompiledShaderIdentity(ReadOnlySpan<byte> spirv) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(spirv));

    // Used by shader-prewarm metadata, which is shared by all compatible native
    // cache namespaces for the title.
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

    internal static string ResolvePath(
        string? titleId,
        string compatibilityKey,
        string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configuredPath));
        }

        return ResolvePath(
            titleId,
            compatibilityKey,
            configuredPath: null,
            preferredRoot: GetPersistentCacheRoot(),
            fallbackRoot: GetFallbackCacheRoot(),
            prepareRoot: TryPrepareCacheRoot);
    }

    internal static string ResolvePath(
        string? titleId,
        string compatibilityKey,
        string? configuredPath,
        string preferredRoot,
        string fallbackRoot,
        Func<string, bool> prepareRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configuredPath));
        }

        ArgumentNullException.ThrowIfNull(prepareRoot);
        if (string.IsNullOrWhiteSpace(compatibilityKey))
        {
            throw new ArgumentException("A pipeline-cache compatibility key is required.", nameof(compatibilityKey));
        }

        var root = prepareRoot(preferredRoot) ? preferredRoot : fallbackRoot;
        if (string.Equals(root, fallbackRoot, HostPathComparison) &&
            !string.Equals(preferredRoot, fallbackRoot, HostPathComparison))
        {
            _ = prepareRoot(fallbackRoot);
        }

        return BuildPath(root, titleId, compatibilityKey);
    }

    internal static string GetFallbackPath(string? titleId, string compatibilityKey) =>
        BuildPath(GetFallbackCacheRoot(), titleId, compatibilityKey);

    // A verified PC2 file may move between cache namespaces only when its Vulkan
    // device/driver/UUID tuple matches. The payload is re-wrapped with the exact
    // current signature, and the source is retained for rollback.
    internal static bool ImportCompatibleCache(
        string sourcePath,
        string destinationPath,
        string expectedSignature)
    {
        if (!expectedSignature.StartsWith("SharpEmuPC2:", StringComparison.Ordinal) ||
            string.Equals(
                Path.GetFullPath(sourcePath),
                Path.GetFullPath(destinationPath),
                HostPathComparison) ||
            File.Exists(destinationPath) ||
            !File.Exists(sourcePath))
        {
            return false;
        }

        string? temporaryPath = null;
        try
        {
            var sourceLength = new FileInfo(sourcePath).Length;
            if (sourceLength <= 0 || sourceLength > MaxCacheFileBytes)
            {
                return false;
            }

            var file = File.ReadAllBytes(sourcePath);
            if (!PipelineCacheSignature.TryUnwrapDeviceCompatible(
                    expectedSignature,
                    file,
                    out var payload))
            {
                return false;
            }

            var migratedFile = PipelineCacheSignature.Wrap(expectedSignature, payload);

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            temporaryPath = destinationPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.migration.tmp";
            File.WriteAllBytes(temporaryPath, migratedFile);
            File.Move(temporaryPath, destinationPath, overwrite: false);
            temporaryPath = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    internal static bool ImportNewestCompatibleCache(
        string? titleId,
        string destinationPath,
        string expectedSignature) =>
        ImportNewestCompatibleCache(
            titleId,
            destinationPath,
            expectedSignature,
            [GetPersistentCacheRoot(), GetFallbackCacheRoot()]);

    internal static bool ImportNewestCompatibleCache(
        string? titleId,
        string destinationPath,
        string expectedSignature,
        IReadOnlyList<string> cacheRoots)
    {
        if (File.Exists(destinationPath))
        {
            return false;
        }

        var candidates = new List<FileInfo>();
        foreach (var root in cacheRoots)
        {
            CollectTitleCacheCandidates(root, titleId, destinationPath, candidates);
        }

        // Prefer the most complete cache, then the newest one. Candidate
        // discovery is bounded so stale local builds cannot delay startup.
        foreach (var candidate in candidates
                     .OrderByDescending(static candidate => candidate.Length)
                     .ThenByDescending(static candidate => candidate.LastWriteTimeUtc)
                     .Take(128))
        {
            if (ImportCompatibleCache(candidate.FullName, destinationPath, expectedSignature))
            {
                return true;
            }
        }

        return false;
    }

    private static StringComparison HostPathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string GetPersistentCacheRoot()
    {
        var profileRoot = Environment.GetFolderPath(
            OperatingSystem.IsMacOS()
                ? Environment.SpecialFolder.UserProfile
                : Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(profileRoot))
        {
            return GetFallbackCacheRoot();
        }

        var cacheRoot = OperatingSystem.IsMacOS()
            ? Path.Combine(profileRoot, "Library", "Caches")
            : profileRoot;
        return Path.Combine(cacheRoot, "SharpEmu", "pipeline-cache");
    }

    private static string GetFallbackCacheRoot() =>
        Path.Combine(AppContext.BaseDirectory, "user", "pipeline_cache");

    private static bool TryPrepareCacheRoot(string root)
    {
        string? probePath = null;
        try
        {
            Directory.CreateDirectory(root);
            probePath = Path.Combine(
                root,
                $".write-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
            using (File.Create(probePath))
            {
            }

            File.Delete(probePath);
            probePath = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] Vulkan pipeline cache root unavailable: path={root}: {exception.Message}");
            return false;
        }
        finally
        {
            if (probePath is not null)
            {
                try
                {
                    File.Delete(probePath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static string BuildPath(string root, string? titleId, string compatibilityKey) =>
        Path.GetFullPath(Path.Combine(
            root,
            SanitizeTitleId(titleId),
            SanitizeCompatibilityKey(compatibilityKey),
            CacheFileName));

    private static void CollectTitleCacheCandidates(
        string root,
        string? titleId,
        string destinationPath,
        List<FileInfo> candidates)
    {
        var titleRoot = Path.Combine(root, SanitizeTitleId(titleId));
        if (!Directory.Exists(titleRoot))
        {
            return;
        }

        var destinationFullPath = Path.GetFullPath(destinationPath);
        try
        {
            var directPath = Path.Combine(titleRoot, CacheFileName);
            AddCandidate(directPath, destinationFullPath, candidates);
            foreach (var namespaceDirectory in Directory.EnumerateDirectories(titleRoot).Take(128))
            {
                AddCandidate(
                    Path.Combine(namespaceDirectory, CacheFileName),
                    destinationFullPath,
                    candidates);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cache discovery is opportunistic and must never block startup.
        }
    }

    private static void AddCandidate(
        string candidatePath,
        string destinationFullPath,
        List<FileInfo> candidates)
    {
        if (string.Equals(
                Path.GetFullPath(candidatePath),
                destinationFullPath,
                HostPathComparison) ||
            !File.Exists(candidatePath))
        {
            return;
        }

        try
        {
            var candidate = new FileInfo(candidatePath);
            if (candidate.Length > 0 && candidate.Length <= MaxCacheFileBytes)
            {
                candidates.Add(candidate);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
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

    private static string SanitizeCompatibilityKey(string compatibilityKey)
    {
        var source = compatibilityKey.Trim();
        Span<char> sanitized = source.Length <= 128
            ? stackalloc char[source.Length]
            : new char[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            var value = source[index];
            sanitized[index] = char.IsAsciiLetterOrDigit(value) || value is '-' or '_'
                ? value
                : '_';
        }

        return new string(sanitized);
    }
}
