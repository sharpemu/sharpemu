// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using System.Runtime.InteropServices;

namespace SharpEmu.GUI;

public partial class MainWindow
{
    private async Task CreateSelectedGameShortcutAsync()
    {
        if (GameList.SelectedItem is not GameEntry selected ||
            await SelectGameVersionAsync(selected) is not { } game)
            return;

        var loc = Localization.Instance;
        string message;
        try
        {
            LocateEmulator();
            if (_emulatorExePath is null)
                throw new FileNotFoundException(loc.Get("Launch.ExeNotFound"));
            var shortcut = DesktopShortcut.Create(game, _emulatorExePath);
            message = loc.Format("Library.Shortcut.Created", shortcut);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            COMException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            message = loc.Format("Library.Shortcut.Failed", exception.Message);
        }

        var sheet = new SheetView();
        var close = new Button { Classes = { "ghost" }, Content = loc.Get("Common.Close"), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => sheet.Close();
        sheet.Content = new StackPanel
        {
            Margin = new Thickness(32, 28), Spacing = 20,
            Children =
            {
                new TextBlock { Classes = { "sheetTitle" }, Text = loc.Get("Library.Context.CreateShortcut"), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                close,
            },
        };
        await Sheets.ShowAsync(sheet);
    }
}
