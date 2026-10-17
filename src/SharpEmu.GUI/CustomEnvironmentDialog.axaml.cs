// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using System.Diagnostics;

namespace SharpEmu.GUI;

public partial class CustomEnvironmentDialog : Window
{
    private readonly string? _titleId;

    public CustomEnvironmentDialog()
        : this(null, [])
    {
    }

    public CustomEnvironmentDialog(string? titleId, IEnumerable<string> initialEntries)
    {
        InitializeComponent();
        _titleId = titleId;
        EnvironmentTextBox.Text = string.Join(Environment.NewLine, initialEntries);
        SaveGameButton.IsEnabled = !string.IsNullOrWhiteSpace(titleId);

        SaveGameButton.Click += (_, _) => SaveForGame();
        SaveGlobalButton.Click += (_, _) => SaveGlobally();
        DocumentationButton.Click += (_, _) => OpenDocumentation();
        CancelButton.Click += (_, _) => Close(null);
        PlayButton.Click += (_, _) => Play();
    }

    public event Action<IReadOnlyList<string>>? SaveGlobalRequested;
    public event Action<string, IReadOnlyList<string>>? SaveGameRequested;

    private static void OpenDocumentation()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = ProjectLinks.DocumentUrl("docs/sharpemu-gui-undocumented-env-vars.md"),
            UseShellExecute = true,
        });
    }

    private bool TryGetEntries(out IReadOnlyList<string> entries)
    {
        if (!CustomEnvironmentVariables.TryParseLines(
                EnvironmentTextBox.Text,
                out var parsed,
                out var invalidLine))
        {
            StatusText.Text = string.Format(
                Localization.Instance.Get("Launch.CustomEnv.Invalid"),
                invalidLine);
            entries = [];
            return false;
        }

        StatusText.Text = string.Empty;
        entries = parsed;
        return true;
    }

    private void SaveForGame()
    {
        if (string.IsNullOrWhiteSpace(_titleId) || !TryGetEntries(out var entries))
        {
            return;
        }

        SaveGameRequested?.Invoke(_titleId, entries);
        StatusText.Text = Localization.Instance.Get("Launch.CustomEnv.SavedGame");
    }

    private void SaveGlobally()
    {
        if (!TryGetEntries(out var entries))
        {
            return;
        }

        SaveGlobalRequested?.Invoke(entries);
        StatusText.Text = Localization.Instance.Get("Launch.CustomEnv.SavedGlobal");
    }

    private void Play()
    {
        if (TryGetEntries(out var entries))
        {
            Close(entries);
        }
    }
}
