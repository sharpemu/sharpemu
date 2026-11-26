// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;

namespace SharpEmu.GUI;

// Asks whether one package that cannot run in place (a PS4 .pkg) should be installed now. Returns
// true to install; anything else (Cancel, Escape) leaves it alone.
public partial class PkgConfirmDialog : SheetView
{
    public PkgConfirmDialog()
    {
        InitializeComponent();
        SkipButton.Click += (_, _) => Close(false);
        InstallButton.Click += (_, _) => Close(true);
    }

    public PkgConfirmDialog(string message)
        : this()
    {
        MessageText.Text = message;
    }

    public override Control? InitialFocus => InstallButton;
}
