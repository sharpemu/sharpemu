// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpEmu.Libs.Pad;

internal static class PadInputProfileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };

    private static readonly object SyncRoot = new();
    private static PadInputProfile? _cachedProfile;
    private static DateTime _cachedWriteTimeUtc;

    // Per-user config location: %APPDATA%\SharpEmu on Windows,
    // ~/.config/SharpEmu on Linux, ~/Library/Application Support/SharpEmu on macOS.
    public static string ProfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SharpEmu",
        "pad-input-profile.json");

    public static PadInputProfile Load()
    {
        try
        {
            if (File.Exists(ProfilePath))
            {
                var json = File.ReadAllText(ProfilePath);
                var profile = JsonSerializer.Deserialize<PadInputProfile>(json, SerializerOptions);
                if (profile is not null)
                {
                    profile.EnsureDefaults();
                    return profile;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt or unreadable mappings fall back to defaults.
        }

        return PadInputProfile.CreateDefault();
    }

    public static PadInputProfile LoadCached()
    {
        lock (SyncRoot)
        {
            var writeTimeUtc = File.Exists(ProfilePath)
                ? File.GetLastWriteTimeUtc(ProfilePath)
                : DateTime.MinValue;

            if (_cachedProfile is null || writeTimeUtc != _cachedWriteTimeUtc)
            {
                _cachedProfile = Load();
                _cachedWriteTimeUtc = writeTimeUtc;
            }

            return _cachedProfile;
        }
    }

    public static void Save(PadInputProfile profile)
    {
        profile.EnsureDefaults();

        var directory = Path.GetDirectoryName(ProfilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(ProfilePath, JsonSerializer.Serialize(profile, SerializerOptions));

        lock (SyncRoot)
        {
            _cachedProfile = profile;
            _cachedWriteTimeUtc = File.GetLastWriteTimeUtc(ProfilePath);
        }
    }
}
