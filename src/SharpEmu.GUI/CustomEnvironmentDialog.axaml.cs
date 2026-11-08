// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using System.Diagnostics;

namespace SharpEmu.GUI;

public partial class CustomEnvironmentDialog : SheetView
{
    public CustomEnvironmentDialog()
        : this([])
    {
    }

    // Edits one game's custom variables; closes with the parsed entries on Save.
    public CustomEnvironmentDialog(IEnumerable<string> initialEntries)
    {
        InitializeComponent();
        EnvironmentTextBox.Text = string.Join(Environment.NewLine, initialEntries);

        DocumentationButton.Click += (_, _) => OpenDocumentation();
        CancelButton.Click += (_, _) => Close(null);
        SaveButton.Click += (_, _) => Save();
    }

    // The launcher-wide list: same editor, its own title and description.
    public CustomEnvironmentDialog(IEnumerable<string> initialEntries, string title, string description)
        : this(initialEntries)
    {
        TitleText.Text = title;
        DescriptionText.Text = description;
    }

    /// <summary>
    /// Variables for one launch: the main button launches with them (closing with
    /// <see cref="LaunchVariables"/>) and a second one saves them as the game's own instead.
    /// </summary>
    public static CustomEnvironmentDialog ForLaunch(IEnumerable<string> initialEntries, string gameName)
    {
        var dialog = new CustomEnvironmentDialog(
            initialEntries,
            Localization.Instance.Get("Launch.Variables.Title"),
            Localization.Instance.Format("Launch.Variables.Desc", gameName));
        dialog._launchMode = true;
        dialog.SaveButton.Content = Localization.Instance.Get("Launch.Launch");
        dialog.SaveToGameButton.IsVisible = true;
        dialog.SaveToGameButton.Click += (_, _) => dialog.Save(saveToGame: true);
        return dialog;
    }

    /// <summary>What a launch-mode dialog closes with.</summary>
    public sealed record LaunchVariables(IReadOnlyList<string> Entries, bool SaveToGame);

    private bool _launchMode;

    public override Control? InitialFocus => EnvironmentTextBox;

    private static void OpenDocumentation()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = ProjectLinks.DocumentUrl("docs/sharpemu-gui-undocumented-env-vars.md"),
            UseShellExecute = true,
        });
    }

    private void Save(bool saveToGame = false)
    {
        if (!CustomEnvironmentVariables.TryParseLines(
                EnvironmentTextBox.Text,
                out var parsed,
                out var invalidLine))
        {
            StatusText.Text = string.Format(
                Localization.Instance.Get("Launch.CustomEnv.Invalid"),
                invalidLine);
            return;
        }

        Close(_launchMode ? new LaunchVariables(parsed, saveToGame) : parsed);
    }
}
