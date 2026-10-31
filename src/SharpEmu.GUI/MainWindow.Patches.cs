// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

public partial class MainWindow
{
    private async Task OpenSelectedGamePatchesAsync()
    {
        if (GameList.SelectedItem is not GameEntry game || string.IsNullOrWhiteSpace(game.TitleId))
        {
            return;
        }

        var dialog = new GamePatchesDialog(game, _isRunning);
        dialog.Warning += message => AppendConsoleLine($"[PATCH][WARN] {message}", WarningLineBrush);
        await Sheets.ShowAsync(dialog);
    }
}
