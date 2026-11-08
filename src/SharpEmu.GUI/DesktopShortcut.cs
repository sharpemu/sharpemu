// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace SharpEmu.GUI;

internal static class DesktopShortcut
{
    public static string Create(GameEntry game, string emulatorPath, string? desktopDirectory = null)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Desktop shortcuts are supported on Windows and Linux.");
        emulatorPath = Path.GetFullPath(emulatorPath);
        if (!File.Exists(emulatorPath))
            throw new FileNotFoundException("The emulator executable was not found.", emulatorPath);
        if (!File.Exists(game.Path))
            throw new FileNotFoundException("The game was not found.", game.Path);

        desktopDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(desktopDirectory))
            throw new DirectoryNotFoundException("The desktop folder is unavailable.");
        Directory.CreateDirectory(desktopDirectory);

        var title = DisplayName(game);
        var name = SafeFileName(title);
        var extension = OperatingSystem.IsWindows() ? ".lnk" : ".desktop";
        var destination = Path.Combine(desktopDirectory, name + extension);
        for (var suffix = 2; File.Exists(destination) || Directory.Exists(destination); suffix++)
            destination = Path.Combine(desktopDirectory, $"{name} ({suffix}){extension}");

        var temporary = Path.Combine(desktopDirectory, $".sharpemu-{Guid.NewGuid():N}{extension}");
        try
        {
            ShortcutSplashCache.Prepare(game.Path, game.BackgroundPath);
            var icon = game.CoverPath is { } cover && File.Exists(cover)
                ? CacheIcon(cover, Path.Combine(SharpEmu.Libs.SharpEmuPaths.DataRoot, "user", "shortcut-icons"), OperatingSystem.IsWindows())
                : OperatingSystem.IsWindows() ? emulatorPath : "applications-games";
            if (OperatingSystem.IsWindows())
                WriteWindowsLink(temporary, emulatorPath, game.Path, title, icon);
            else
            {
                File.WriteAllText(temporary, LinuxEntry(game, emulatorPath, icon), new UTF8Encoding(false));
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            File.Move(temporary, destination);
            return destination;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static string LinuxEntry(GameEntry game, string emulatorPath, string? iconPath = null)
    {
        var command = new[] { Path.GetFullPath(emulatorPath) }.Concat(LaunchConfiguration.ShortcutArguments(game.Path));
        var icon = iconPath ?? (game.CoverPath is { } cover && File.Exists(cover) ? Path.GetFullPath(cover) : "applications-games");
        return $"[Desktop Entry]\nType=Application\nName={EscapeValue(DisplayName(game))}\n" +
            $"Exec={string.Join(' ', command.Select(QuoteDesktopArgument))}\n" +
            $"Path={EscapeValue(Path.GetDirectoryName(Path.GetFullPath(emulatorPath))!)}\n" +
            $"Icon={EscapeValue(icon)}\nTerminal=false\nCategories=Game;\n";
    }

    private static string DisplayName(GameEntry game) =>
        !game.HasMultipleVersions || string.IsNullOrWhiteSpace(game.Version) ? game.Name : $"{game.Name} (v{game.Version})";

    internal static string CacheIcon(string coverPath, string directory, bool windows)
    {
        var png = File.ReadAllBytes(coverPath);
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(png)) + (windows ? ".ico" : ".png"));
        if (File.Exists(path))
            return path;

        Directory.CreateDirectory(directory);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            // Shortcuts keep their art even if the game or its extracted ZAR metadata moves.
            File.WriteAllBytes(temporary, windows ? WindowsIcon(png) : png);
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static byte[] WindowsIcon(byte[] png)
    {
        using var source = SKBitmap.Decode(png) ?? throw new InvalidDataException("The game icon could not be decoded.");
        var scale = Math.Min(1.0, 256.0 / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var bitmap = source.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKFilterMode.Linear)) ?? throw new InvalidDataException("The game icon could not be resized.");
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var image = encoded.ToArray();
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)(width == 256 ? 0 : width));
        writer.Write((byte)(height == 256 ? 0 : height));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(image.Length);
        writer.Write(22);
        writer.Write(image);
        return output.ToArray();
    }

    private static string SafeFileName(string name)
    {
        var safe = string.Concat(name.Select(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '_' : character));
        safe = string.Concat(safe.EnumerateRunes().Take(52)).Trim(' ', '.');
        if (Regex.IsMatch(safe, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            safe = "_" + safe;
        return string.IsNullOrEmpty(safe) ? "Game" : safe;
    }

    private static string EscapeValue(string value) => value.Replace("\\", "\\\\")
        .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    private static string QuoteDesktopArgument(string value)
    {
        var quoted = value.Replace("%", "%%").Replace("\\", "\\\\")
            .Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");
        return EscapeValue('"' + quoted + '"');
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindowsLink(string path, string emulatorPath, string gamePath, string title, string iconPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(emulatorPath);
            link.SetArguments(string.Join(' ', LaunchConfiguration.ShortcutArguments(gamePath).Select(EmulatorProcess.QuoteArgument)));
            link.SetWorkingDirectory(Path.GetDirectoryName(emulatorPath)!);
            link.SetDescription(title);
            link.SetIconLocation(iconPath, 0);
            link.SetShowCmd(1);
            ((IPersistFile)link).Save(path, false);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, nint findData, uint flags);
        void GetIDList(out nint itemIdList);
        void SetIDList(nint itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
