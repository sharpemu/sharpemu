// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.FileSystem.Pkg;

// Picks the reader for a .pkg file from its magic: "\x7FCNT" is a PS4 package, "\x7FFIH" a PS5 one.
public static class GamePackage
{
    private static ReadOnlySpan<byte> CntMagic => [0x7F, 0x43, 0x4E, 0x54];

    private static ReadOnlySpan<byte> FihMagic => [0x7F, 0x46, 0x49, 0x48];

    public static IGamePackage Open(string path)
    {
        Span<byte> magic = stackalloc byte[4];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (stream.Read(magic) != 4)
            {
                throw new InvalidDataException("This file is too short to be a PKG package.");
            }
        }

        if (magic.SequenceEqual(CntMagic))
        {
            return PkgPackage.Open(path);
        }

        if (magic.SequenceEqual(FihMagic))
        {
            return Ps5Package.Open(path);
        }

        throw new InvalidDataException("Not a PS4 or PS5 package (unknown magic).");
    }

    // Opens the package through `source`, which it then owns. Every read, the magic included, goes
    // through the source, so a recorded snapshot replays this exact open.
    internal static IGamePackage Open(PackageSource source)
    {
        try
        {
            Span<byte> magic = stackalloc byte[4];
            if (source.ReadAt(magic, 0) != 4)
            {
                throw new InvalidDataException("This file is too short to be a PKG package.");
            }

            if (magic.SequenceEqual(CntMagic))
            {
                return PkgPackage.Open(source);
            }

            if (magic.SequenceEqual(FihMagic))
            {
                return Ps5Package.Open(source);
            }

            throw new InvalidDataException("Not a PS4 or PS5 package (unknown magic).");
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    // True for a readable .pkg file with either known magic.
    public static bool IsPackagePath(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".pkg", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            Span<byte> magic = stackalloc byte[4];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Read(magic) == 4 && (magic.SequenceEqual(CntMagic) || magic.SequenceEqual(FihMagic));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
