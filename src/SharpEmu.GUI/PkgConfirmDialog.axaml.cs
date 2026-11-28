// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;

namespace SharpEmu.GUI;

// Asks whether a .pkg should be installed now. Returns true to install; anything else (Cancel,
// Escape) leaves it alone. When the caller offers a format, the sheet also holds the choice
// between a game folder and a .zar archive, and for the archive the opt-in to delete the .pkg
// while it is converted (AsZar, ConsumeSource). The resume variant asks whether to pick an
// unfinished conversion up again.
public partial class PkgConfirmDialog : SheetView
{
    public PkgConfirmDialog()
    {
        InitializeComponent();
        SkipButton.Click += (_, _) => Close(false);
        InstallButton.Click += (_, _) => Close(true);
        FolderButton.Click += (_, _) => SetFormat(zar: false);
        ZarButton.Click += (_, _) => SetFormat(zar: true);
        ConsumeCheck.IsCheckedChanged += (_, _) => ConsumeWarning.IsVisible = ConsumeCheck.IsChecked == true;
    }

    public PkgConfirmDialog(string message, bool offerFormat = false, bool canConsume = true)
        : this()
    {
        MessageText.Text = message;
        FormatPanel.IsVisible = offerFormat;
        ConsumeCheck.IsEnabled = canConsume;
        ConsumeBusy.IsVisible = !canConsume;
        SetFormat(zar: false);
    }

    // The sheet for an unfinished consuming conversion: resume it or leave it paused.
    public static PkgConfirmDialog ForResume(string message)
    {
        var dialog = new PkgConfirmDialog(message);
        dialog.TitleText.Text = Localization.Instance.Get("Library.Pkg.ResumeTitle");
        dialog.InstallButton.Content = Localization.Instance.Get("Library.Pkg.ResumeButton");
        return dialog;
    }

    public bool AsZar { get; private set; }

    public bool ConsumeSource => AsZar && ConsumeCheck.IsEnabled && ConsumeCheck.IsChecked == true;

    public override Control? InitialFocus => InstallButton;

    private void SetFormat(bool zar)
    {
        AsZar = zar;
        FolderButton.Classes.Set("active", !zar);
        ZarButton.Classes.Set("active", zar);
        ZarOptions.IsVisible = zar;
        FormatHint.Text = Localization.Instance.Get(zar ? "Library.Pkg.FormatZarHint" : "Library.Pkg.FormatFolderHint");
    }
}
