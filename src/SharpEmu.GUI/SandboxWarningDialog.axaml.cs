// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;

namespace SharpEmu.GUI;

// Tells the user that the sandbox refused something the running game tried to do.
public partial class SandboxWarningDialog : SheetView
{
    public SandboxWarningDialog()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Close();
    }

    public override Control? InitialFocus => OkButton;
}
