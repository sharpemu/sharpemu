// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GameContent;

public interface IReadOnlyGameFileSystem : IDisposable
{
    bool TryGetEntry(string path, out GameFileEntry entry);

    IReadOnlyList<GameFileEntry> EnumerateDirectory(string path);

    Stream OpenRead(string path);
}
