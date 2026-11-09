// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.IO;

namespace SharpEmu.Libs;

/// <summary>
/// Resolves the root directory that holds user-writable data: GUI settings,
/// the library cache, custom environment and per-game configs, save data,
/// logs and dumps.
///
/// The emulator is portable by default and keeps its data next to the
/// executable. System-style installs (package managers, Program Files) own a
/// read-only executable directory, so there the data falls back to the
/// platform's per-user application directory: <c>~/.local/share/SharpEmu</c>
/// (XDG) on Linux and <c>%APPDATA%\SharpEmu</c> on Windows.
/// The GUI's portable-data option writes a <c>portable.txt</c> marker beside
/// the executable that forces the portable layout regardless of writability.
/// <c>SHARPEMU_USER_DIR</c> overrides the choice on every platform.
/// </summary>
public static class SharpEmuPaths
{
    public const string PortableMarkerFileName = "portable.txt";

    public static string DataRoot { get; } = ResolveDataRoot();

    /// <summary>Marker file beside the executable that forces portable mode.</summary>
    public static string PortableMarkerPath =>
        Path.Combine(AppContext.BaseDirectory, PortableMarkerFileName);

    /// <summary>True when the portable marker file exists beside the executable.</summary>
    public static bool IsPortableMarkerPresent() => File.Exists(PortableMarkerPath);

    /// <summary>
    /// True when the executable directory is writable, i.e. portable mode can
    /// still be enabled from the GUI.
    /// </summary>
    public static bool CanWriteExecutableDirectory() => IsWritable(AppContext.BaseDirectory);

    /// <summary>
    /// Creates or removes the portable marker. Returns false when the marker
    /// could not be written or removed (read-only executable directory).
    /// </summary>
    public static bool SetPortableMarker(bool enabled)
    {
        try
        {
            if (enabled)
            {
                File.WriteAllText(
                    PortableMarkerPath,
                    "SharpEmu keeps its data (settings, saves, caches, logs) next to the executable while this file exists.\n");
            }
            else
            {
                File.Delete(PortableMarkerPath);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ResolveDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_USER_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        if (IsPortableMarkerPresent() ||
            Directory.Exists(Path.Combine(AppContext.BaseDirectory, "user")) ||
            IsWritable(AppContext.BaseDirectory))
        {
            return AppContext.BaseDirectory;
        }

        var root = OperatingSystem.IsLinux()
            ? Path.Combine(XdgDataHome(), "SharpEmu")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SharpEmu");

        try
        {
            // Create the root eagerly: consumers only create their own
            // subdirectories, and a missing root would fail the very first
            // settings write.
            Directory.CreateDirectory(root);
        }
        catch (Exception)
        {
            // Writers report the failure themselves; do not turn a bad root
            // into a startup crash.
        }

        return root;
    }

    private static string XdgDataHome()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(dataHome))
        {
            return dataHome;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "share");
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".sharpemu-write-probe-{Environment.ProcessId}");
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
