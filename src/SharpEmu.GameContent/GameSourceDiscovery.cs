// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GameContent;

public static class GameSourceDiscovery
{
    private static readonly StringComparer SourceIdentityComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// Opens and selects one source per normalized game identity. The caller owns the returned sources.
    /// </summary>
    public static IReadOnlyList<GameSource> OpenPreferredSources(IEnumerable<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        var selected = new Dictionary<string, Selection>(SourceIdentityComparer);
        try
        {
            foreach (var sourcePath in sourcePaths)
            {
                GameSource source;
                try
                {
                    source = GameSource.Open(sourcePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
                {
                    continue;
                }

                var identity = source.IsArchive ? source.SourcePath : source.PhysicalRootPath!;
                var priority = IsDecryptedExecutable(source) ? 1 : 0;
                if (!selected.TryGetValue(identity, out var current))
                {
                    selected.Add(identity, new Selection(source, priority));
                    continue;
                }

                if (priority > current.Priority)
                {
                    current.Source.Dispose();
                    selected[identity] = new Selection(source, priority);
                }
                else
                {
                    source.Dispose();
                }
            }
        }
        catch
        {
            foreach (var selection in selected.Values)
            {
                selection.Source.Dispose();
            }

            throw;
        }

        return selected.Values.Select(static selection => selection.Source).ToArray();
    }

    private static bool IsDecryptedExecutable(GameSource source) =>
        !source.IsArchive &&
        string.Equals(source.ExecutablePath, "decrypted/eboot.bin", StringComparison.OrdinalIgnoreCase);

    private sealed record Selection(GameSource Source, int Priority);
}
