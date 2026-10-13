// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SharpEmu.GUI;

public static partial class Updater
{
    public static async Task DownloadAndRestartAsync(
        UpdateInfo update,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpEmu.Update");
        var payload = Path.Combine(root, "payload");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        var launched = false;
        try
        {
            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, update.Name);
            using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(archive);
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                    progress?.Report(update.Size == 0 ? 0 : (int)(written * 100 / update.Size));
                }

                if (written != update.Size)
                {
                    throw new InvalidDataException($"Downloaded {written} bytes; expected {update.Size}.");
                }
            }

            await using (var archiveStream = File.OpenRead(archive))
            {
                var actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream, cancellationToken));
                if (!string.Equals(actualSha256, update.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"SHA-256 mismatch; expected {update.Sha256}, got {actualSha256}.");
                }
            }

            var platform = CurrentPlatform();
            var stagedExe = ExtractArchive(archive, payload, platform.Extension, platform.ExecutableName);

            var start = new ProcessStartInfo(stagedExe)
            {
                UseShellExecute = false,
                WorkingDirectory = payload,
            };
            start.ArgumentList.Add(ApplyArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(AppContext.BaseDirectory);
            using var helper = Process.Start(start)
                ?? throw new InvalidOperationException("The update installer could not be started.");
            launched = true;
        }
        finally
        {
            if (!launched)
            {
                TryDeleteDirectory(root);
            }
        }
    }

    /// <summary>Runs from the downloaded executable after the old GUI exits.</summary>
    public static bool TryApply(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length != 3 || args[0] != ApplyArgument)
        {
            return false;
        }

        var backup = Path.Combine(Path.GetTempPath(), $"SharpEmu.UpdateBackup-{Environment.ProcessId}");
        var changed = new List<(string Destination, string? Backup)>();
        try
        {
            if (int.TryParse(args[1], out var oldPid))
            {
                try
                {
                    if (!Process.GetProcessById(oldPid).WaitForExit(30_000))
                    {
                        throw new TimeoutException("SharpEmu did not close within 30 seconds.");
                    }
                }
                catch (ArgumentException)
                {
                    // The old process has already exited.
                }
            }

            var source = AppContext.BaseDirectory;
            var target = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(backup);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                if (relative.Equals("gui-settings.json", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("user" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("Languages" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string? backupFile = null;
                if (File.Exists(destination))
                {
                    backupFile = Path.Combine(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    File.Copy(destination, backupFile, overwrite: true);
                }
                changed.Add((destination, backupFile));
                File.Copy(file, destination, overwrite: true);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(destination, File.GetUnixFileMode(file));
                }
            }

            using var restarted = Process.Start(new ProcessStartInfo(
                Path.Combine(target, CurrentPlatform().ExecutableName))
            {
                UseShellExecute = false,
                WorkingDirectory = target,
            }) ?? throw new InvalidOperationException("The updated SharpEmu could not be started.");
            TryDeleteDirectory(backup);
        }
        catch (Exception ex)
        {
            exitCode = 1;
            foreach (var (destination, backupFile) in changed.AsEnumerable().Reverse())
            {
                try
                {
                    if (backupFile is null)
                    {
                        File.Delete(destination);
                    }
                    else if (File.Exists(backupFile))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(backupFile, destination, overwrite: true);
                    }
                }
                catch
                {
                    // Best-effort rollback; the original error is more useful to the user.
                }
            }
            TryDeleteDirectory(backup);
            try
            {
                File.WriteAllText(Path.Combine(args[2], "update-error.log"), ex.ToString());
            }
            catch
            {
                // Best-effort diagnostics only.
            }
        }

        return true;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    private static string ExtractArchive(
        string archive,
        string payload,
        string extension,
        string executableName)
    {
        if (extension == ".zip")
        {
            ZipFile.ExtractToDirectory(archive, payload);
        }
        else
        {
            Directory.CreateDirectory(payload);
            using var compressed = File.OpenRead(archive);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, payload, overwriteFiles: false);
        }

        var executable = Path.Combine(payload, executableName);
        if (!File.Exists(executable))
        {
            throw new InvalidDataException($"The update archive does not contain {executableName}.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        }

        return executable;
    }

}
