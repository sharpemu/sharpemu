// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;

namespace SharpEmu.GUI;

public enum SandboxVolumeChoice
{
    Convert,
    PlayWithoutSandbox,
}

// Shown when the sandboxed launch failed because the game folder is on a drive without ACLs (exFAT, FAT32).
// Returns a SandboxVolumeChoice, or null for Cancel and Escape.
public partial class SandboxVolumeDialog : SheetView
{
    public SandboxVolumeDialog()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close(null);
        PlayButton.Click += (_, _) => Close(SandboxVolumeChoice.PlayWithoutSandbox);
        ConvertButton.Click += (_, _) => Close(SandboxVolumeChoice.Convert);
    }

    // canConvert: the game is in the library and can be packed; canPlayWithout: the environment does not force the sandbox on.
    public SandboxVolumeDialog(string fileSystem, bool canConvert, bool canPlayWithout)
        : this()
    {
        TitleText.Text = Localization.Instance.Format("Sandbox.VolumeTitle", FileSystemName(fileSystem));
        ConvertButton.IsVisible = canConvert;
        PlayButton.IsVisible = canPlayWithout;
    }

    // The volume reports its file system by name; an empty name still reads as an exFAT-class drive.
    internal static string FileSystemName(string fileSystem) =>
        string.IsNullOrWhiteSpace(fileSystem) ? "exFAT" : fileSystem.Trim();

    public override Control? InitialFocus => ConvertButton.IsVisible ? ConvertButton : CancelButton;
}
