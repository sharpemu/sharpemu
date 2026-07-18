// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;

namespace SharpEmu.GUI;

/// <summary>Self-contained Windows updater; the emulator layers do not depend on it.</summary>
public static class Updater
{
    private const string ApplyArgument = "--sharpemu-apply-update";
    private const string LatestReleaseUrl = "https://api.github.com/repos/sharpemu/sharpemu/releases/latest";
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient Http = CreateHttpClient();

    public sealed record UpdateInfo(string Sha, string Name, string DownloadUrl, long Size, string Sha256, string TagName);

    public static async Task<UpdateInfo?> CheckAsync(string? currentSha, CancellationToken cancellationToken = default)
    {
        var platform = CurrentPlatform();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);

        using var response = await Http.GetAsync(LatestReleaseUrl, timeout.Token);
        response.EnsureSuccessStatusCode();
        var update = ParseRelease(
            await response.Content.ReadAsStringAsync(timeout.Token),
            null,
            platform.Rid,
            platform.Extension);
        var currentVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (update is null || currentSha is null ||
            string.Equals(update.Sha, currentSha, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (currentVersion is not null &&
            TryParseVersion(currentVersion, out var installed) &&
            TryParseVersion(update.TagName, out var available) &&
            available.CompareTo(installed) <= 0)
        {
            return null;
        }

        var comparison = await CompareCommitsAsync(currentSha, update.Sha, timeout.Token);
        return comparison.Status == "ahead" && comparison.ReleaseDate > comparison.CurrentDate
            ? update
            : null;
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

    private static UpdateInfo? ParseRelease(
        string json,
        string? currentSha,
        string rid,
        string extension)
    {
        using var document = JsonDocument.Parse(json);
        var releaseSha = ExtractReleaseSha(document.RootElement);
        var candidates = new List<(DateTimeOffset Created, UpdateInfo Update)>();
        foreach (var asset in document.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var marker = $"-{rid}";
            var markerIndex = name.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
                markerIndex < 0)
            {
                continue;
            }

            var suffix = name[(markerIndex + marker.Length)..^extension.Length].TrimStart('-');
            var assetSha = suffix.Length >= 7 && suffix.All(Uri.IsHexDigit)
                ? suffix
                : releaseSha;
            if (assetSha is null ||
                !asset.TryGetProperty("digest", out var digestProperty) ||
                digestProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var digest = digestProperty.GetString() ?? "";
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                digest.Length != "sha256:".Length + 64 ||
                !digest["sha256:".Length..].All(Uri.IsHexDigit))
            {
                continue;
            }

            candidates.Add((
                asset.GetProperty("created_at").GetDateTimeOffset(),
                new UpdateInfo(
                    assetSha,
                    name,
                    asset.GetProperty("browser_download_url").GetString()!,
                    asset.GetProperty("size").GetInt64(),
                    digest["sha256:".Length..],
                    document.RootElement.GetProperty("tag_name").GetString() ?? "")));
        }

        var latest = candidates.OrderByDescending(candidate => candidate.Created).FirstOrDefault().Update;
        return latest is null || string.Equals(latest.Sha, currentSha, StringComparison.OrdinalIgnoreCase)
            ? null
            : latest;
    }

    private static async Task<CommitComparison> CompareCommitsAsync(
        string currentSha,
        string releaseSha,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/sharpemu/sharpemu/compare/{currentSha}...{releaseSha}";
        using var response = await Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var currentDate = root.GetProperty("base_commit").GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTimeOffset();
        var releaseDate = currentDate;
        if (root.TryGetProperty("commits", out var commits) && commits.GetArrayLength() > 0)
        {
            releaseDate = commits[commits.GetArrayLength() - 1]
                .GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTimeOffset();
        }

        return new CommitComparison(root.GetProperty("status").GetString() ?? "", currentDate, releaseDate);
    }

    private static string? ExtractReleaseSha(JsonElement release)
    {
        if (!release.TryGetProperty("body", out var bodyProperty) ||
            bodyProperty.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var body = bodyProperty.GetString();
        var match = Regex.Match(
            body ?? "",
            @"\bcommit\s+([0-9a-f]{7,40})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var sha = match.Groups[1].Value;
        return sha.Length > 7 ? sha[..7] : sha;
    }

    private static bool TryParseVersion(string value, out ReleaseVersion version)
    {
        var match = Regex.Match(value.TrimStart('v'), @"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Value, out var patch))
        {
            version = default;
            return false;
        }

        version = new ReleaseVersion(major, minor, patch, match.Groups[4].Value);
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
    private sealed record CommitComparison(string Status, DateTimeOffset CurrentDate, DateTimeOffset ReleaseDate);
    private readonly record struct ReleaseVersion(int Major, int Minor, int Patch, string PreRelease) : IComparable<ReleaseVersion>
    {
        public int CompareTo(ReleaseVersion other) =>
            (Major, Minor, Patch) != (other.Major, other.Minor, other.Patch)
                ? (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch))
                : string.IsNullOrEmpty(PreRelease) == string.IsNullOrEmpty(other.PreRelease)
                    ? string.CompareOrdinal(PreRelease, other.PreRelease)
                    : string.IsNullOrEmpty(PreRelease) ? 1 : -1;
    }
}
