// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using Avalonia.Threading;
using SharpEmu.Libs.Np;

namespace SharpEmu.GUI;

public partial class MainWindow
{
    private IReadOnlyList<TrophyEntry> _trophyTestCatalog = [];
    private int _trophyTestGeneration;

    private void InitializeTrophies()
    {
        TrophyPageHost.Backdrop = TrophyBackdropHost;
        TrophyTestGameBox.SelectionChanged += (_, _) => LoadTrophyTestCatalog();
        ConsoleLanguageBox.SelectionChanged += (_, _) => Dispatcher.UIThread.Post(LoadTrophyTestCatalog);
        TrophyTestButton.Click += (_, _) => ShowTrophyTestNotification();
        TrophySound.Prepare();
        TrophySoundToggle.IsCheckedChanged += (_, _) =>
        {
            _settings.TrophySound = TrophySoundToggle.IsChecked == true;
            TrophySound.Enabled = _settings.TrophySound;
            ScheduleSettingsSave();
        };
    }

    private IReadOnlyList<GameEntry> GamesWithTrophies() =>
        _allGames.Where(g => !string.IsNullOrWhiteSpace(g.TitleId)).ToArray();

    private void OpenTrophyHub()
    {
        SetActivePage(2);
        TrophyPageHost.ShowCollection(GamesWithTrophies());
    }

    private void OpenSelectedGameTrophies()
    {
        if (GameList.SelectedItem is not GameEntry game || string.IsNullOrWhiteSpace(game.TitleId))
        {
            return;
        }

        SetActivePage(2);
        TrophyPageHost.ShowGame(GamesWithTrophies(), game);
    }

    private async void ShowTrophyToast(TrophyUnlockNotice notice, string? ebootPath)
    {
        IReadOnlyList<TrophyEntry> catalog = [];
        if (ebootPath is not null && TrophyUi.GameDirectory(ebootPath) is { } gameDirectory)
        {
            var titleId = notice.TitleId;
            var language = TrophyLanguage.FromConsoleLanguage(_settings.ConsoleLanguage);
            catalog = await Task.Run(() => TrophyCatalog.Load(gameDirectory, titleId, language: language));
        }

        var entry = catalog.FirstOrDefault(t => t.Id == notice.TrophyId);
        var gameName = _allGames.FirstOrDefault(g => g.TitleId == notice.TitleId)?.Name;
        TrophyToast.Show(new TrophyToastContent(notice.Name, entry?.Description ?? string.Empty, notice.Grade, entry?.IconPng,
            gameName, EarnedWith(catalog, notice.TrophyId), catalog.Count));
    }

    private static int EarnedWith(IReadOnlyList<TrophyEntry> catalog, int trophyId) =>
        catalog.Count(t => t.Unlocked || t.Id == trophyId);

    private void RefreshTrophyTestGames()
    {
        var selectedPath = (TrophyTestGameBox.SelectedItem as GameEntry)?.Path;
        var games = _allGames.Where(g => !string.IsNullOrWhiteSpace(g.TitleId)).OrderBy(g => g.Name).ToList();
        TrophyTestGameBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<GameEntry>(
            (game, _) => new TextBlock { Text = game?.Name });
        TrophyTestGameBox.ItemsSource = games;
        TrophyTestGameBox.SelectedItem = games.FirstOrDefault(g => g.Path == selectedPath) ?? games.FirstOrDefault();
    }

    private async void LoadTrophyTestCatalog()
    {
        var generation = ++_trophyTestGeneration;
        _trophyTestCatalog = [];
        TrophyTestTrophyBox.ItemsSource = null;
        if (TrophyTestGameBox.SelectedItem is not GameEntry game ||
            TrophyUi.GameDirectory(game.Path) is not { } directory)
        {
            return;
        }

        var language = TrophyLanguage.FromConsoleLanguage(_settings.ConsoleLanguage);
        var catalog = await Task.Run(() => TrophyCatalog.Load(directory, game.TitleId!, language: language));
        if (generation != _trophyTestGeneration)
        {
            return;
        }

        _trophyTestCatalog = catalog;
        TrophyTestTrophyBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<TrophyEntry>(
            (trophy, _) => new TextBlock { Text = trophy is null ? string.Empty : $"{TrophyUi.GradeName(trophy.Grade)} · {trophy.Name}" });
        TrophyTestTrophyBox.ItemsSource = catalog;
        TrophyTestTrophyBox.SelectedIndex = catalog.Count > 0 ? 0 : -1;
    }

    private void ShowTrophyTestNotification()
    {
        if (TrophyTestTrophyBox.SelectedItem is TrophyEntry trophy)
        {
            TrophyToast.Show(new TrophyToastContent(trophy.Name, trophy.Description, trophy.Grade, trophy.IconPng,
                (TrophyTestGameBox.SelectedItem as GameEntry)?.Name, EarnedWith(_trophyTestCatalog, trophy.Id), _trophyTestCatalog.Count));
        }
    }
}
