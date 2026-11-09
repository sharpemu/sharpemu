// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace SharpEmu.GUI;

public partial class GameVersionDialog : SheetView
{
    public GameVersionDialog()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close();
    }

    public GameVersionDialog(IReadOnlyList<GameEntry> versions, bool forPacking = false) : this()
    {
        GameNameText.Text = versions[0].Name;
        if (forPacking)
        {
            TitleText.Text = Localization.Instance.Get("Library.Versions.PackTitle");
            DescriptionText.Text = Localization.Instance.Get("Library.Versions.PackDescription");
        }

        foreach (var game in versions)
        {
            var version = string.IsNullOrWhiteSpace(game.Version)
                ? Localization.Instance.Get("Library.Versions.Unknown")
                : $"v{game.Version}";
            var details = new StackPanel { Spacing = 6 };
            details.Children.Add(new TextBlock { Classes = { "versionName" }, Text = version });
            details.Children.Add(new TextBlock { Classes = { "versionPath" }, Text = game.Path });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 18 };
            row.Children.Add(details);
            var size = new TextBlock
            {
                Text = game.IsArchive ? $"ZAR · {game.SizeText}" : game.SizeText,
                Classes = { "versionPath" },
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(size, 1);
            row.Children.Add(size);

            var button = new Button { Classes = { "ghost", "versionOption" }, Content = row };
            ToolTip.SetTip(button, game.Path);
            AutomationProperties.SetName(button, $"{version}, {game.Path}");
            button.Click += (_, _) => Close(game);
            VersionList.Children.Add(button);
        }
    }

    public override Control? InitialFocus => VersionList.Children.FirstOrDefault() ?? CancelButton;
}
