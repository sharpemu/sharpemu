// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GameContent;

public readonly record struct GameFileEntry(
    string Name,
    bool IsDirectory,
    long Length,
    DateTime LastWriteTimeUtc)
{
    public bool IsFile => !IsDirectory;
}
