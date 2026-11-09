// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SharpEmu.Core.Mods;

namespace SharpEmu.GUI;

public partial class GameModsDialog : SheetView
{
    private GameModRepository? _repository;
    private bool _busy;
    private bool _closed;
    private readonly CancellationTokenSource _cancellation = new();

    public GameModsDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        ReloadButton.Click += async (_, _) => await ReloadAsync();
        InstallButton.Click += async (_, _) => await InstallAsync();
        OpenFolderButton.Click += (_, _) => OpenFolder();
        Closed += (_, _) => { _closed = true; _cancellation.Cancel(); _cancellation.Dispose(); };
    }

    public GameModsDialog(GameEntry game) : this()
    {
        _repository = new GameModRepository(PerGameSettings.KeyFor(game.TitleId, game.Path));
        GameNameText.Text = game.Name;
        GameInfoText.Text = $"{game.TitleId}\n{game.Version}";
        GameCover.Source = game.Cover;
        CoverInitials.Text = game.HasCover ? string.Empty : game.Initials;
        FolderPathText.Text = _repository.DirectoryPath;
        Opened += async (_, _) => await ReloadAsync();
    }

    public event Action<string>? Warning;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ModList.IsEnabled = ReloadButton.IsEnabled = InstallButton.IsEnabled = OpenFolderButton.IsEnabled = !busy;
    }

    private async Task ReloadAsync()
    {
        if (_busy || _repository is null) return;
        SetBusy(true);
        StatusText.Text = string.Empty;
        EmptyText.Text = Localization.Instance.Get("Library.Mods.Loading");
        try
        {
            var mods = await Task.Run(_repository.Load);
            if (_closed) return;
            ModList.ItemsSource = mods.Select(mod => new GameModOption(mod, _repository, ReportError)).ToArray();
            EmptyText.IsVisible = mods.Count == 0;
            EmptyText.Text = Localization.Instance.Get("Library.Mods.Empty");
            CountText.Text = Localization.Instance.Format("Library.Mods.Count", mods.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            if (!_closed) ReportError(exception.Message);
        }
        finally { if (!_closed) SetBusy(false); }
    }

    private async Task InstallAsync()
    {
        if (_busy || _repository is null || TopLevel.GetTopLevel(this) is not { } top) return;
        SetBusy(true);
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localization.Instance.Get("Library.Mods.InstallZip"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Localization.Instance.Get("Library.Mods.ZipFiles")) { Patterns = ["*.zip"] }],
            });
            if (_closed || files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            StatusText.Text = string.Empty;
            ImportProgress.Value = 0;
            ImportSizeText.Text = ImportFileText.Text = string.Empty;
            ImportPanel.IsVisible = true;
            var progress = new Progress<ModImportProgress>(value =>
            {
                if (_closed) return;
                var percent = value.TotalBytes == 0 ? 100 : 100.0 * value.ExtractedBytes / value.TotalBytes;
                ImportProgress.Value = percent;
                ImportFileText.Text = value.FileName;
                ImportSizeText.Text = Localization.Instance.Format("Library.Mods.Progress", (int)percent,
                    GameEntry.FormatSize(value.ExtractedBytes), GameEntry.FormatSize(value.TotalBytes));
            });
            var token = _cancellation.Token;
            await Task.Run(() => _repository.ImportZip(path, progress, token), token);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        {
            if (!_closed) ReportError(exception.Message);
        }
        finally
        {
            if (!_closed)
            {
                ImportPanel.IsVisible = false;
                SetBusy(false);
            }
        }
        if (!_closed && string.IsNullOrEmpty(StatusText.Text)) await ReloadAsync();
    }

    private void ReportError(string message)
    {
        StatusText.Text = Localization.Instance.Format("Library.Mods.Failed", message);
        Warning?.Invoke(message);
    }

    private void OpenFolder()
    {
        if (_repository is null) return;
        try
        {
            Directory.CreateDirectory(_repository.DirectoryPath);
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(_repository.DirectoryPath);
            Process.Start(start);
        }
        catch (Exception exception) { ReportError(exception.Message); }
    }
}
