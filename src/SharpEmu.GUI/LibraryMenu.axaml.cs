// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;

namespace SharpEmu.GUI;

// The launcher owns the settings behind these controls; it wires them when it builds the menu
// once and keeps them in step with the Options page.
public partial class LibraryMenu : SheetView
{
    public LibraryMenu()
    {
        InitializeComponent();
    }

    public override Control? InitialFocus => CarouselViewButton.Classes.Contains("active") ? CarouselViewButton : GridViewButton;
}
