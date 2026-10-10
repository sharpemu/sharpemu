// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.ShaderCache;

public static class ShaderCacheSettings
{
    public const string EnvironmentVariable = "SHARPEMU_SHADER_CACHE";
    public const string WaitEnvironmentVariable = "SHARPEMU_SHADER_CACHE_WAIT";
    public const string FullPrecompileEnvironmentVariable = "SHARPEMU_SHADER_PRECOMPILE_FULL";
    public const string LearnEnvironmentVariable = "SHARPEMU_SHADER_LEARN";
    public const string JobsEnvironmentVariable = "SHARPEMU_SHADER_PRECOMPILE_JOBS";

    private const string LegacyEnvironmentVariable = "SHARPEMU_SHADER_PREWARM";

    public static bool Enabled => Resolve(
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        Environment.GetEnvironmentVariable(LegacyEnvironmentVariable));

    public static bool HoldsGuestUntilReady =>
        !string.Equals(Environment.GetEnvironmentVariable(WaitEnvironmentVariable), "0", StringComparison.Ordinal);

    public static bool FullPrecompile =>
        TryParse(Environment.GetEnvironmentVariable(FullPrecompileEnvironmentVariable), out var full) && full;

    public static bool Learn =>
        TryParse(Environment.GetEnvironmentVariable(LearnEnvironmentVariable), out var learn) && learn;

    public static int PrecompileJobs =>
        int.TryParse(Environment.GetEnvironmentVariable(JobsEnvironmentVariable), out var jobs)
            ? Math.Clamp(jobs, 1, 32)
            : Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    public static bool Resolve(string? value, string? legacyValue = null) =>
        TryParse(value, out var enabled) ? enabled : !string.Equals(legacyValue, "0", StringComparison.Ordinal);

    public static bool TryParse(string? value, out bool enabled)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "1" or "on" or "true" or "yes" or "enabled":
                enabled = true;
                return true;
            case "0" or "off" or "false" or "no" or "disabled":
                enabled = false;
                return true;
            default:
                enabled = true;
                return false;
        }
    }

    public static string Format(bool enabled) => enabled ? "on" : "off";

    public static string CacheFilePath(string titleId) =>
        Path.Combine(
            Path.GetDirectoryName(VideoOut.VulkanPipelineCacheStorage.ResolvePath(
                titleId,
                Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE_PATH")))!,
            ShaderCacheFile.FileName);

    public static string SeedPath(string titleId)
    {
        var cacheDirectory = Path.GetDirectoryName(CacheFilePath(titleId))!;
        var userDirectory = Path.GetDirectoryName(Path.GetDirectoryName(cacheDirectory)) ?? cacheDirectory;
        return Path.Combine(userDirectory, "shader_seeds", titleId + ShaderSeed.Extension);
    }

    public static bool TryClear(string titleId)
    {
        try
        {
            var path = CacheFilePath(titleId);
            var directory = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(directory))
            {
                return true;
            }

            File.Delete(path);
            foreach (var part in Directory.EnumerateFiles(directory, ShaderCacheFile.FileName + ".part*"))
            {
                File.Delete(part);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
