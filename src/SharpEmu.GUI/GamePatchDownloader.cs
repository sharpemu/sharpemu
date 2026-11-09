// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.Core.Patches;

namespace SharpEmu.GUI;

internal static class GamePatchDownloader
{
    internal const string RepositoryRoot = "https://raw.githubusercontent.com/sharpemu/ps5patches/main/";
    internal const string Ps4RepositoryRoot = "https://raw.githubusercontent.com/sharpemu/ps4patches/main/";
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = 4 << 20,
    };

    private sealed record PatchIndex(int FormatVersion, PatchFile[] Files);
    private sealed record PatchFile(string File, string[] TitleIds, string[] Versions);

    internal static async Task<int> DownloadAsync(string titleId, string? version, string directory,
        CancellationToken cancellationToken = default, HttpClient? client = null, bool compatibilityMode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titleId);
        client ??= Http;
        var repositoryRoot = compatibilityMode ? Ps4RepositoryRoot : RepositoryRoot;
        var patchDirectory = compatibilityMode ? "orbis_patches/" : "prospero_patches/";
        var indexBytes = await client.GetByteArrayAsync(repositoryRoot + "patches.json", cancellationToken);
        var index = JsonSerializer.Deserialize<PatchIndex>(indexBytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (index is not { FormatVersion: 1, Files: { Length: <= 4096 } })
            throw new InvalidDataException("The patch repository index is invalid or unsupported.");

        var downloads = new List<(string File, byte[] Bytes)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0;
        foreach (var file in index.Files)
        {
            if (file is null || file.TitleIds is null || file.Versions is null || string.IsNullOrWhiteSpace(file.File) ||
                !file.File.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                !file.File.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ' ') ||
                !names.Add(file.File))
                throw new InvalidDataException("The patch repository index contains an invalid or duplicate filename.");

            if (!file.TitleIds.Contains(titleId, StringComparer.OrdinalIgnoreCase) || !file.Versions.Any(expected =>
                string.Equals(expected, "mask", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(version) && (string.Equals(expected, version, StringComparison.OrdinalIgnoreCase) ||
                    Version.TryParse(expected, out var wanted) && Version.TryParse(version, out var actual) && wanted == actual)))
                continue;

            if (downloads.Count >= 32) throw new InvalidDataException("Too many patch files match this game.");
            var bytes = await client.GetByteArrayAsync(repositoryRoot + patchDirectory + Uri.EscapeDataString(file.File), cancellationToken);
            totalBytes = checked(totalBytes + bytes.Length);
            if (totalBytes > 16 << 20)
                throw new InvalidDataException("The patch download exceeds its size limit.");
            using var source = new MemoryStream(bytes, writable: false);
            var patches = GamePatchRepository.Read(file.File, source, titleId);
            if (!patches.Any(patch => patch.Error is null && patch.MatchesVersion(version)))
                throw new InvalidDataException("The downloaded XML does not contain a valid patch for this title ID and game version.");
            downloads.Add((file.File, bytes));
        }

        if (downloads.Count == 0) return 0;
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        foreach (var download in downloads)
        {
            var path = Path.Combine(directory, download.File);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, download.Bytes, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        return downloads.Count;
    }
}
