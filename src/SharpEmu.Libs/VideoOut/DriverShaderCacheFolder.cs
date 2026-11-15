// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

// SHARPEMU_DRIVER_CACHE_FOLDER=0 turns it off. The NVIDIA driver keeps this game's shader disk cache beside the game's
// other caches (<pipeline cache>.driver) instead of in the driver's global shader cache folder. NVIDIA compresses
// pipeline binaries with the dictionary of the executable's driver cache file and refuses them once that file is
// replaced. The global folder has one size budget for every OpenGL and Vulkan program together (about 1000 MiB), so
// another program's compiles can evict this executable's file, and with it the use of every stored binary: the next
// session then builds its stored pipelines again from scratch. A per-game folder is used by the game alone and moves
// with the game's caches. The driver reads the path when it loads, so it is set before the Vulkan loader first runs. A
// path already set is kept. The switch ends in _FOLDER, so translation keys ignore it
// (TranslationDiskCache.IgnoredVariable). The first session in a new folder starts a new driver file: binaries stored
// under the global file's dictionary are refused once and stored again.
internal static class DriverShaderCacheFolder
{
    internal const string PathVariable = "__GL_SHADER_DISK_CACHE_PATH";
    internal const string Suffix = ".driver";

    // The folder to give the driver: null when the switch is off or a path is already set.
    internal static string? Resolve(string? mode, string? configuredPath, string pipelineCachePath) =>
        mode != "0" && string.IsNullOrEmpty(configuredPath) ? pipelineCachePath + Suffix : null;

    // Points the driver at the game's folder (creating it) unless the switch is off or a path is already set. Call it
    // before Vk.GetApi. Returns the folder, or null when nothing was set.
    internal static string? Apply(string pipelineCachePath)
    {
        var folder = Resolve(Environment.GetEnvironmentVariable("SHARPEMU_DRIVER_CACHE_FOLDER"),
            Environment.GetEnvironmentVariable(PathVariable), pipelineCachePath);
        if (folder is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(folder);
            var files = new DirectoryInfo(folder).GetFiles("*", SearchOption.AllDirectories);
            Environment.SetEnvironmentVariable(PathVariable, folder);
            Console.Error.WriteLine($"[LOADER][INFO] Driver shader cache: folder={folder} files={files.Length} " +
                $"bytes={files.Sum(static file => file.Length)}");
            return folder;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Driver shader cache folder unavailable: {exception.Message}");
            return null;
        }
    }
}
