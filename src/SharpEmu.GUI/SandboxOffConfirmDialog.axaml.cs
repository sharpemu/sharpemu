// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;

namespace SharpEmu.GUI;

// Confirms running without the sandbox, for the setting and for a one-off launch. Returns true only for "Turn off";
// Cancel and Escape keep the sandbox, and Cancel has the focus.
public partial class SandboxOffConfirmDialog : SheetView
{
    public SandboxOffConfirmDialog()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close(false);
        TurnOffButton.Click += (_, _) => Close(true);
    }

    public override Control? InitialFocus => CancelButton;
}
