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

    public override Control? InitialFocus => EnvironmentTextBox;

    private static void OpenDocumentation()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = ProjectLinks.DocumentUrl("docs/sharpemu-gui-undocumented-env-vars.md"),
            UseShellExecute = true,
        });
    }

    private void Save()
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

        Close(parsed);
    }
}
