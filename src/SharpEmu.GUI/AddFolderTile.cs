// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

/// <summary>
/// Stateless trailing action that opens the library folder picker.
/// </summary>
public sealed class AddFolderTile : LibraryTile
{
    public static AddFolderTile Instance { get; } = new();

    private AddFolderTile()
    {
    }
}
