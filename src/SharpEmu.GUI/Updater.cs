// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SharpEmu.GUI;

/// <summary>Self-contained Windows updater; the emulator layers do not depend on it.</summary>
public static class Updater
{
    private const string ApplyArgument = "--sharpemu-apply-update";
    private const string LatestReleaseUrl = "https://api.github.com/repos/sharpemu/sharpemu/releases/latest";
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient Http = CreateHttpClient();

    public sealed record UpdateInfo(string Sha, string Name, string DownloadUrl, long Size);

    public static async Task<UpdateInfo?> CheckAsync(string? currentSha, CancellationToken cancellationToken = default)
    {
        var platform = CurrentPlatform();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);

        using var response = await Http.GetAsync(LatestReleaseUrl, timeout.Token);
        response.EnsureSuccessStatusCode();
        return ParseRelease(
            await response.Content.ReadAsStringAsync(timeout.Token),
            currentSha,
            platform.Rid,
            platform.Extension);
    }

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
    }

    /// <summary>Runs from the downloaded executable after the old GUI exits.</summary>
    public static bool TryApply(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length != 3 || args[0] != ApplyArgument)
        {
            return false;
        }

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
        }
        catch (Exception ex)
        {
            exitCode = 1;
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

    private static UpdateInfo? ParseRelease(
        string json,
        string? currentSha,
        string rid,
        string extension)
    {
        using var document = JsonDocument.Parse(json);
        var candidates = new List<(DateTimeOffset Created, UpdateInfo Update)>();
        foreach (var asset in document.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var marker = $"-{rid}-";
            var markerIndex = name.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
                markerIndex < 0)
            {
                continue;
            }

            var sha = name[(markerIndex + marker.Length)..^extension.Length];
            if (sha.Length < 7 || !sha.All(Uri.IsHexDigit))
            {
                continue;
            }

            candidates.Add((
                asset.GetProperty("created_at").GetDateTimeOffset(),
                new UpdateInfo(
                    sha,
                    name,
                    asset.GetProperty("browser_download_url").GetString()!,
                    asset.GetProperty("size").GetInt64())));
        }

        var latest = candidates.OrderByDescending(candidate => candidate.Created).FirstOrDefault().Update;
        return latest is null || string.Equals(latest.Sha, currentSha, StringComparison.OrdinalIgnoreCase)
            ? null
            : latest;
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

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SharpEmu", "0.0.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static PlatformInfo CurrentPlatform()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("SharpEmu releases require an x64 process.");
        }

        if (OperatingSystem.IsWindows()) return new("win-x64", ".zip", "SharpEmu.exe");
        if (OperatingSystem.IsLinux()) return new("linux-x64", ".tar.gz", "SharpEmu");
        if (OperatingSystem.IsMacOS()) return new("osx-x64", ".tar.gz", "SharpEmu");
        throw new PlatformNotSupportedException();
    }

    private sealed record PlatformInfo(string Rid, string Extension, string ExecutableName);
}
