// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

public partial class MainWindow
{
    private async Task OpenSelectedGameModsAsync()
    {
        if (GameList.SelectedItem is not GameEntry game) return;
        var dialog = new GameModsDialog(game);
        dialog.Warning += message => AppendConsoleLine($"[MODS][WARN] {message}", WarningLineBrush);
        await Sheets.ShowAsync(dialog);
    }
}
