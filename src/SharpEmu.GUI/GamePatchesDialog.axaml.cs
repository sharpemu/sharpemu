// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text.Json;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using SharpEmu.Core.Patches;

namespace SharpEmu.GUI;

public partial class GamePatchesDialog : Window
{
    private readonly GamePatchRepository _repository = new();
    private readonly GameEntry? _game;
    private readonly bool _readOnly;
    private IReadOnlyList<GamePatchOption> _options = [];
    private bool _loading;
    private bool _downloading;
    private bool _closed;
    private readonly CancellationTokenSource _downloadCancellation = new();
    private bool _filtering;
    private GamePatchOption? _detailsOption;
    private bool _detailsInitialized;

    public GamePatchesDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        OpenFolderButton.Click += (_, _) => OpenFolder();
        ReloadButton.Click += async (_, _) => await ReloadAsync();
        DownloadButton.Click += async (_, _) => await DownloadAsync();
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        FileFilter.SelectionChanged += (_, _) => { if (!_loading) ApplyFilter(); };
        PatchList.SelectionChanged += (_, _) =>
        {
            if (!_filtering) ShowDetails(PatchList.SelectedItem as GamePatchOption);
        };
        PatchList.AddHandler(PointerPressedEvent, OnPatchInteraction, RoutingStrategies.Tunnel, handledEventsToo: true);
        PatchList.AddHandler(GotFocusEvent, OnPatchInteraction, RoutingStrategies.Bubble, handledEventsToo: true);
        Closed += (_, _) =>
        {
            _closed = true;
            _downloadCancellation.Cancel();
            _downloadCancellation.Dispose();
        };
    }

    public GamePatchesDialog(GameEntry game, bool readOnly) : this()
    {
        _game = game;
        _readOnly = readOnly;
        UpdateBusyState();
        GameNameText.Text = game.Name;
        GameInfoText.Text = $"{game.TitleId}\n" + Localization.Instance.Format("Library.Patches.InstalledVersion", game.Version ?? "?");
        GameCover.Source = game.Cover;
        CoverInitials.Text = game.HasCover ? string.Empty : game.Initials;
        HintText.Text = Localization.Instance.Get(readOnly ? "Library.Patches.Running" : "Library.Patches.AutoSave");
        Opened += async (_, _) => await ReloadAsync();
    }

    public event Action<string>? Warning;

    private async Task ReloadAsync()
    {
        if (_loading || _game?.TitleId is not { } titleId) return;
        _loading = true;
        UpdateBusyState();
        StatusText.Text = string.Empty;
        EmptyText.Text = Localization.Instance.Get("Library.Patches.Loading");
        ShowDetails(null);
        try
        {
            var result = await Task.Run(() =>
            {
                var warnings = new List<string>();
                var selected = _repository.LoadEnabled(titleId);
                var patches = _repository.Load(titleId, warnings.Add);
                return (Patches: patches, Selected: selected, Warnings: warnings);
            });
            if (_closed) return;
            _options = result.Patches.Where(patch => patch.MatchesVersion(_game.Version)).Select(patch => new GamePatchOption(
                patch, _repository, titleId, _game.Version, result.Selected.Contains(patch.Id), _readOnly, ReportError)).ToArray();
            var files = _options.Select(option => option.Patch.FileName).Distinct(StringComparer.Ordinal).ToArray();
            FileFilter.ItemsSource = new[] { Localization.Instance.Get("Library.Patches.AllFiles") }.Concat(files).ToArray();
            FileFilter.SelectedIndex = 0;
            if (result.Warnings.Count > 0) ReportError(string.Join("\n", result.Warnings));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            if (_closed) return;
            _options = [];
            ReportError(Localization.Instance.Get("Library.Patches.LoadFailed") + "\n" + exception.Message);
        }
        finally
        {
            _loading = false;
            if (!_closed)
            {
                UpdateBusyState();
                ApplyFilter();
            }
        }
    }

    private void UpdateBusyState()
    {
        ReloadButton.IsEnabled = !_loading && !_downloading;
        DownloadButton.IsEnabled = !_readOnly && !_loading && !_downloading && _game?.TitleId is not null;
        PatchList.IsEnabled = !_loading && !_downloading;
    }

    private async Task DownloadAsync()
    {
        if (_loading || _downloading || _readOnly || _game?.TitleId is not { } titleId) return;
        _downloading = true;
        UpdateBusyState();
        StatusText.Text = string.Empty;
        HintText.Text = Localization.Instance.Get("Library.Patches.Downloading");
        try
        {
            var count = await GamePatchDownloader.DownloadAsync(titleId, _game.Version, _repository.DirectoryPath, _downloadCancellation.Token);
            if (_closed) return;
            StatusText.Text = string.Empty;
            if (count > 0)
            {
                SearchBox.Text = string.Empty;
                await ReloadAsync();
            }
            if (!_closed)
                await ShowDownloadMessageAsync(count > 0
                    ? Localization.Instance.Get("Library.Patches.DownloadComplete")
                    : Localization.Instance.Format("Library.Patches.DownloadNotFound", _game.Name, _game.Version ?? "?"));
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or
            JsonException or XmlException or FormatException or ArgumentException or OperationCanceledException)
        {
            if (_closed) return;
            ReportError(exception.Message);
            await ShowDownloadMessageAsync(Localization.Instance.Format("Library.Patches.DownloadFailed", exception.Message));
        }
        finally
        {
            _downloading = false;
            if (!_closed)
            {
                HintText.Text = Localization.Instance.Get(_readOnly ? "Library.Patches.Running" : "Library.Patches.AutoSave");
                UpdateBusyState();
            }
        }
    }

    private Task ShowDownloadMessageAsync(string message)
    {
        var close = new Button
        {
            Content = Localization.Instance.Get("Exit.Ok"),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true,
            IsCancel = true,
            Classes = { "accent" },
        };
        var dialog = new Window
        {
            Title = Localization.Instance.Get("Library.Patches.Download"),
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 20,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, close },
            },
        };
        close.Click += (_, _) => dialog.Close();
        return dialog.ShowDialog(this);
    }

    private void ApplyFilter()
    {
        if (_loading) return;
        var fileName = FileFilter.SelectedIndex > 0 ? FileFilter.SelectedItem as string : null;
        var visible = _options.Where(option => option.Matches(SearchBox.Text, fileName)).ToArray();
        var previous = PatchList.SelectedItem;
        _filtering = true;
        try
        {
            PatchList.ItemsSource = visible;
            PatchList.SelectedItem = previous is GamePatchOption selected && visible.Contains(selected) ? selected : visible.FirstOrDefault();
        }
        finally
        {
            _filtering = false;
        }
        EmptyText.IsVisible = visible.Length == 0;
        EmptyText.Text = Localization.Instance.Get(_options.Count == 0 ? "Library.Patches.Empty" : "Library.Patches.NoMatches");
        CountText.Text = Localization.Instance.Format("Library.Patches.Count", visible.Length, _options.Count);
        ShowDetails(PatchList.SelectedItem as GamePatchOption);
    }

    private void ShowDetails(GamePatchOption? option)
    {
        if (_detailsInitialized && ReferenceEquals(_detailsOption, option)) return;
        _detailsInitialized = true;
        _detailsOption = option;
        DetailsScroll.IsVisible = option is not null;
        NoDetailsText.IsVisible = option is null;
        DetailsName.Text = option?.Patch.Name;
        DetailsAuthor.Text = option?.Patch.Author;
        DetailsNote.Text = option?.Note;
        DetailsAppVersion.Text = option?.AppVersionText;
        DetailsPatchVersion.Text = option?.Patch.PatchVersion;
        DetailsExecutable.Text = option?.Patch.Executable;
        DetailsFile.Text = option?.Patch.FileName;
        DetailsStatus.Text = option?.Status;
        DetailsScroll.Offset = default;
    }

    private void OnPatchInteraction(object? sender, RoutedEventArgs args)
    {
        if (_loading || _filtering) return;
        if (args.Source is Visual source &&
            source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is GamePatchOption option &&
            !ReferenceEquals(PatchList.SelectedItem, option))
        {
            // Selection and the details panel must always name the same patch, even over a checkbox.
            PatchList.SelectedItem = option;
        }
    }

    private void ReportError(string message)
    {
        StatusText.Text = message;
        Warning?.Invoke(message);
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_repository.DirectoryPath);
            var start = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open",
                UseShellExecute = false,
            };
            start.ArgumentList.Add(_repository.DirectoryPath);
            Process.Start(start);
        }
        catch (Exception exception)
        {
            ReportError(exception.Message);
        }
    }
}
