// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Compression;
using System.Diagnostics;
using System.Text.Json;
using SharpEmu.HLE.FileSystem;

namespace SharpEmu.Core.Mods;

public sealed record GameMod(string Name, string DirectoryPath, bool Enabled);
public sealed record ModImportProgress(string FileName, long ExtractedBytes, long TotalBytes);

public sealed class GameModRepository
{
    public static string DefaultDirectory => Path.Combine(SharpEmu.Libs.SharpEmuPaths.DataRoot, "user", "mods");
    public string DirectoryPath { get; }
    private string SettingsPath => Path.Combine(DirectoryPath, "enabled.json");

    public GameModRepository(string gameKey, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(gameKey) || gameKey.Length > 80 ||
            !gameKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("Invalid mod game ID.", nameof(gameKey));
        DirectoryPath = Path.GetFullPath(Path.Combine(directory ?? DefaultDirectory, gameKey.ToUpperInvariant()));
    }

    public IReadOnlyList<GameMod> Load()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var enabled = LoadEnabled();
        return Directory.EnumerateDirectories(DirectoryPath)
            .Where(path => !Path.GetFileName(path).StartsWith('.') && GameFileOverlay.IsSafePath(DirectoryPath, path))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ThenBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(path => new GameMod(Path.GetFileName(path), path, enabled.Contains(Path.GetFileName(path))))
            .ToArray();
    }

    private HashSet<string> LoadEnabled() => File.Exists(SettingsPath)
        ? new HashSet<string>(JsonSerializer.Deserialize<string[]>(File.ReadAllText(SettingsPath)) ?? [], StringComparer.Ordinal)
        : new(StringComparer.Ordinal);

    public void SetEnabled(string name, bool enabled)
    {
        if (!Load().Any(mod => mod.Name == name)) throw new DirectoryNotFoundException("The mod directory no longer exists.");
        var selected = LoadEnabled();
        if (enabled) selected.Add(name);
        else selected.Remove(name);
        Directory.CreateDirectory(DirectoryPath);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(selected.Order(StringComparer.Ordinal), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public string ImportZip(string zipPath, IProgress<ModImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count == 0 || archive.Entries.Count > 100_000)
            throw new InvalidDataException("The mod ZIP is empty or contains too many entries.");
        var entries = archive.Entries.Select(entry => (Entry: entry, Parts: ValidateEntry(entry))).ToArray();
        var total = entries.Sum(e => e.Entry.Length);
        var name = Path.GetFileNameWithoutExtension(zipPath);
        name = string.Concat(name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c)).Trim(' ', '.');
        if (name.Length == 0) name = "Mod";
        if (name.Length > 100) name = name[..100];
        if (IsReservedName(name)) name = "_" + name;
        // Accept the usual app0 wrapper, or a folder named after the ZIP; otherwise preserve game paths.
        var first = entries[0].Parts[0];
        var strip = entries.All(e => e.Parts[0] == first) &&
            (first.Equals("app0", StringComparison.OrdinalIgnoreCase) || first.Equals(name, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(DirectoryPath);
        if (!GameFileOverlay.IsSafePath(DirectoryPath, DirectoryPath)) throw new IOException("The mod directory is a symbolic link.");
        var staging = Path.Combine(DirectoryPath, ".import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            long extracted = 0;
            var count = 0;
            var buffer = new byte[1024 * 1024];
            var update = Stopwatch.StartNew();
            var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var (entry, parts) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pathParts = strip ? parts[1..] : parts;
                if (pathParts.Length == 0) continue;
                var relative = Path.Combine(pathParts);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                if (!seen.Add(relative)) throw new InvalidDataException("The mod ZIP contains duplicate file paths.");
                var target = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (extracted == 0 || update.ElapsedMilliseconds >= 80)
                {
                    progress?.Report(new(relative, extracted, total));
                    update.Restart();
                }
                using (var source = entry.Open())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    long entryBytes = 0;
                    int read;
                    while ((read = source.Read(buffer)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        entryBytes = checked(entryBytes + read);
                        if (entryBytes > entry.Length) throw new InvalidDataException("The mod ZIP has an invalid file size.");
                        output.Write(buffer, 0, read);
                        extracted += read;
                        if (extracted == read || update.ElapsedMilliseconds >= 80)
                        {
                            progress?.Report(new(relative, extracted, total));
                            update.Restart();
                        }
                    }
                    if (entryBytes != entry.Length) throw new InvalidDataException("The mod ZIP contains a truncated file.");
                }
                count++;
            }
            if (count == 0) throw new InvalidDataException("The mod ZIP contains no files.");
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(DirectoryPath, name);
            for (var suffix = 2; Directory.Exists(destination) || File.Exists(destination); suffix++)
                destination = Path.Combine(DirectoryPath, $"{name} ({suffix})");
            Directory.Move(staging, destination);
            progress?.Report(new(string.Empty, extracted, total));
            return Path.GetFileName(destination);
        }
        finally
        {
            // The staging path is always a fresh child of this game's mod directory.
            if (Path.GetFullPath(staging).StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static string[] ValidateEntry(ZipArchiveEntry entry)
    {
        var path = entry.FullName.Replace('\\', '/');
        var parts = path.TrimEnd('/').Split('/');
        var type = (entry.ExternalAttributes >> 16) & 0xF000;
        var isDirectory = path.EndsWith('/');
        if (type is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
            isDirectory && entry.Length != 0 || parts.Any(part => part.Length == 0 || part is "." or ".." || IsReservedName(part) ||
                part.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)) || part.EndsWith(' ') || part.EndsWith('.')))
            throw new InvalidDataException("The mod ZIP contains an unsafe path or symbolic link.");
        return parts;
    }

    private static bool IsReservedName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9';
    }
}
