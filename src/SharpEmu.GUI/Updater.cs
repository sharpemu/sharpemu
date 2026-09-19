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

namespace SharpEmu.GUI;

/// <summary>Self-contained Windows updater; the emulator layers do not depend on it.</summary>
public static class Updater
{
    private const string ApplyArgument = "--sharpemu-apply-update";
    private const string DefaultApiBaseUrl = "https://api.github.com";
    private const int ReleasePageSize = 100;
    private const int MaxReleasePages = 10;
    private const int MaxConcurrentComparisons = 3;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private const int CheckAttempts = 3;
    private static HttpClient Http = CreateHttpClient();
    private static Func<TimeSpan, CancellationToken, Task> DelayAsync = Task.Delay;
    private static string? _releasesEtag;
    private static string? _releasesJson;

    private static string ApiBaseUrl =>
        (Environment.GetEnvironmentVariable("SHARPEMU_UPDATE_API_BASE_URL") ?? DefaultApiBaseUrl).TrimEnd('/');

    public sealed class RateLimitException : HttpRequestException
    {
        public RateLimitException() : base("GitHub API rate limit reached.") { }
    }

    public sealed record UpdateInfo(
        string Sha,
        string Name,
        string DownloadUrl,
        long Size,
        string Sha256,
        string TagName,
        IReadOnlyList<UpdateReleaseNotes> Changelog)
    {
        public string? ManifestUrl { get; init; }
        public bool HistoryTruncated { get; init; }
    }

    public sealed record UpdateReleaseNotes(string TagName, string Notes);

    public static async Task<UpdateInfo?> CheckAsync(string? currentSha, CancellationToken cancellationToken = default)
    {
        OperationCanceledException? lastTimeout = null;
        for (var attempt = 1; attempt <= CheckAttempts; attempt++)
        {
            try
            {
                return await CheckOnceAsync(currentSha, cancellationToken);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastTimeout = ex;
                if (attempt < CheckAttempts)
                {
                    await DelayAsync(RetryDelay, cancellationToken);
                }
            }
        }

        if (lastTimeout is not null)
        {
            throw lastTimeout;
        }

        throw new TimeoutException("The update check timed out.");
    }

    private static async Task<UpdateInfo?> CheckOnceAsync(string? currentSha, CancellationToken cancellationToken)
    {
        var platform = CurrentPlatform();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);

        var releaseResult = await GetReleasesAsync(platform.Rid, platform.Extension, timeout.Token);
        var releases = releaseResult.Releases;
        if (currentSha is null)
        {
            return null;
        }

        var newerShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in releases.Chunk(MaxConcurrentComparisons))
        {
            var comparisons = await Task.WhenAll(batch.Select(async release =>
            {
                if (release.ManifestUrl is not null)
                {
                    var manifest = await ReadManifestAsync(release.ManifestUrl, timeout.Token);
                    if (manifest is null ||
                        !manifest.Commit.StartsWith(release.Sha, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(manifest.Version, release.TagName.TrimStart('v'), StringComparison.OrdinalIgnoreCase))
                    {
                        return (release.Sha, IsNewer: false);
                    }
                }

                if (string.Equals(release.Sha, currentSha, StringComparison.OrdinalIgnoreCase))
                {
                    return (release.Sha, IsNewer: false);
                }

                var comparison = await CompareCommitsAsync(currentSha, release.Sha, timeout.Token);
                return (release.Sha, IsNewer: comparison.Status == "ahead" && comparison.ReleaseDate > comparison.CurrentDate);
            }));
            foreach (var comparison in comparisons.Where(comparison => comparison.IsNewer))
            {
                newerShas.Add(comparison.Sha);
            }
        }

        var newerReleases = releases.Where(release => newerShas.Contains(release.Sha)).ToList();

        if (newerReleases.Count == 0)
        {
            return null;
        }

        var latest = newerReleases[0];
        return latest with
        {
            HistoryTruncated = releaseResult.Truncated,
            Changelog = newerReleases
                .Select(release => new UpdateReleaseNotes(release.TagName, release.Changelog[0].Notes))
                .ToArray(),
        };
    }

    private static async Task<(IReadOnlyList<UpdateInfo> Releases, bool Truncated)> GetReleasesAsync(
        string rid,
        string extension,
        CancellationToken cancellationToken)
    {
        if (_releasesJson is null)
        {
            (_releasesEtag, _releasesJson) = UpdateCache.Load();
        }

        var pages = new List<string>();
        var reachedPageLimit = true;
        for (var page = 1; page <= MaxReleasePages; page++)
        {
            var url = $"{ApiBaseUrl}/repos/sharpemu/sharpemu/releases?per_page={ReleasePageSize}&page={page}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // The persisted cache currently represents page 1 only; never send
            // its validator for later pages, which would mix page bodies.
            if (page == 1 && _releasesEtag is not null)
            {
                request.Headers.IfNoneMatch.ParseAdd(_releasesEtag);
            }

            using var response = await Http.SendAsync(request, cancellationToken);
            string json;
            if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
            {
                throw new RateLimitException();
            }

            if (page == 1 && response.StatusCode == System.Net.HttpStatusCode.NotModified && _releasesJson is not null)
            {
                json = _releasesJson;
            }
            else
            {
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(cancellationToken);
                if (page == 1)
                {
                    _releasesEtag = response.Headers.ETag?.ToString();
                    _releasesJson = json;
                    UpdateCache.Save(_releasesEtag, json);
                }
            }
            pages.Add(json);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() < ReleasePageSize)
            {
                reachedPageLimit = false;
                break;
            }
        }

        return (ParseReleasePages(pages, rid, extension), reachedPageLimit);
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

    internal static IReadOnlyList<UpdateInfo> ParseReleases(
        string json,
        string rid,
        string extension)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var releases = new List<UpdateInfo>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            var update = ParseRelease(release, rid, extension);
            if (update is not null)
            {
                releases.Add(update);
            }
        }

        return releases;
    }

    internal static IReadOnlyList<UpdateInfo> ParseReleasePages(
        IEnumerable<string> pages,
        string rid,
        string extension) => pages.SelectMany(page => ParseReleases(page, rid, extension)).ToArray();

    private static UpdateInfo? ParseRelease(JsonElement release, string rid, string extension)
    {
        var tagName = release.GetProperty("tag_name").GetString() ?? "";
        if (!IsVersionedReleaseTag(tagName))
        {
            return null;
        }

        var releaseSha = ExtractReleaseSha(release);
        string? manifestUrl = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (string.Equals(asset.GetProperty("name").GetString(), "sharpemu-update-manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                manifestUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }
        var candidates = new List<(DateTimeOffset Created, UpdateInfo Update)>();
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
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
                    tagName,
                    [new UpdateReleaseNotes(
                        tagName,
                        release.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
                            ? body.GetString() ?? ""
                        : "")]) { ManifestUrl = manifestUrl }));
        }

        var latest = candidates.OrderByDescending(candidate => candidate.Created).FirstOrDefault().Update;
        return latest;
    }

    internal static bool IsVersionedReleaseTag(string tagName) => Regex.IsMatch(
        tagName,
        @"^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant);

    private static async Task<UpdateManifest?> ReadManifestAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
        {
            throw new RateLimitException();
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return UpdateManifest.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    internal static IDisposable UseHttpClientForTests(
        HttpClient client,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        var previousClient = Http;
        var previousDelay = DelayAsync;
        Http = client;
        DelayAsync = delayAsync ?? Task.Delay;
        _releasesEtag = null;
        _releasesJson = null;
        return new TestHttpScope(previousClient, previousDelay);
    }

    private static async Task<CommitComparison> CompareCommitsAsync(
        string currentSha,
        string releaseSha,
        CancellationToken cancellationToken)
    {
        var url = $"{ApiBaseUrl}/repos/sharpemu/sharpemu/compare/{currentSha}...{releaseSha}";
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
        if (release.TryGetProperty("target_commitish", out var targetProperty) &&
            targetProperty.ValueKind == JsonValueKind.String)
        {
            var target = targetProperty.GetString();
            if (target is { Length: >= 7 } && target.All(Uri.IsHexDigit))
            {
                return target.Length > 7 ? target[..7] : target;
            }
        }

        if (!release.TryGetProperty("body", out var bodyProperty) ||
            bodyProperty.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var body = bodyProperty.GetString();
        var match = Regex.Match(
            body ?? "",
            @"\bcommit\s+(?:\[\s*`?)?([0-9a-f]{7,40})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var sha = match.Groups[1].Value;
        return sha.Length > 7 ? sha[..7] : sha;
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

    private sealed class TestHttpScope(
        HttpClient previousClient,
        Func<TimeSpan, CancellationToken, Task> previousDelay) : IDisposable
    {
        public void Dispose()
        {
            Http = previousClient;
            DelayAsync = previousDelay;
            _releasesEtag = null;
            _releasesJson = null;
        }
    }

    private static PlatformInfo CurrentPlatform()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            var allowNonX64 = Environment.GetEnvironmentVariable("SHARPEMU_ALLOW_NON_X64");
            if (!string.Equals(allowNonX64, "true", StringComparison.OrdinalIgnoreCase))
            {
                throw new PlatformNotSupportedException("SharpEmu releases require an x64 process.");
            }
        }

        if (OperatingSystem.IsWindows()) return new("win-x64", ".zip", "SharpEmu.exe");
        if (OperatingSystem.IsLinux()) return new("linux-x64", ".tar.gz", "SharpEmu");
        if (OperatingSystem.IsMacOS()) return new("osx-x64", ".tar.gz", "SharpEmu");
        throw new PlatformNotSupportedException();
    }

    private sealed record PlatformInfo(string Rid, string Extension, string ExecutableName);
    private sealed record CommitComparison(string Status, DateTimeOffset CurrentDate, DateTimeOffset ReleaseDate);
}
