// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.FileSystem.Pkg;

// What callers need from an installable package, whatever its format (PS4 CNT or PS5 FIH).
public interface IGamePackage : IDisposable
{
    string ContentId { get; }

    string TitleId { get; }

    // The game's name, or null when the package does not carry one.
    string? Title { get; }

    // Package category when the format exposes one (PS4: param.sfo CATEGORY), else null.
    string? Category { get; }

    // Base games are installable; patches and DLC are not. A package without a category counts as a game.
    bool IsGameApplication { get; }

    // Folder name for an install: the title made safe for a file name, else the title id, else `fallback`.
    string GetFolderName(string fallback);

    // Bytes on disk that Extract writes.
    long GetInstalledSize();

    // Writes the game folder (eboot.bin at its root) into `destination`. Throws IOException before
    // writing anything when the drive is known to be too small.
    void Extract(string destination, IProgress<PkgExtractProgress>? progress = null, CancellationToken cancellation = default);
}
