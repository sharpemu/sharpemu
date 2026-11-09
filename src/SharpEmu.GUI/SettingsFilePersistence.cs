// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
namespace SharpEmu.GUI;

// No platform handles or UI types: explicit callers receive the original I/O
// failure; legacy Save wrappers retain their historical best-effort behavior.
internal static class SettingsFilePersistence
{
    // Safe-context fixture seam; no configuration or production caller enables it.
    private static Action<string>? beforeReplacementForTesting = null;

    internal static void Write(string path, string json)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var existed = File.Exists(path);
        UnixFileMode? mode = !OperatingSystem.IsWindows() && existed ? File.GetUnixFileMode(path) : null;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var created = false;
        Exception? primary = null;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, Options = FileOptions.WriteThrough };
            // Environment settings may contain private values. Stage owner-only;
            // restore an existing Unix mode only after the complete write.
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            {
                created = true; file.Write(Encoding.UTF8.GetBytes(json)); file.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows() && mode is { } existingMode) File.SetUnixFileMode(temporary, existingMode);
            // Same-directory replacement never opens/truncates the live file.
            // File.Replace also preserves the existing Windows file metadata.
            beforeReplacementForTesting?.Invoke(temporary);
            if (existed) File.Replace(temporary, path, null);
            else File.Move(temporary, path); // a newly raced destination is preserved, not overwritten
            created = false;
        }
        catch (Exception exception) { primary = exception; throw; }
        finally
        {
            if (created)
                try { File.Delete(temporary); }
                catch (Exception cleanup) when (primary is not null && (cleanup is IOException or UnauthorizedAccessException))
                { primary.Data["SharpEmu.SettingsTemporaryCleanupFailure"] = cleanup; }
        }
    }
}
