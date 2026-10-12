// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using SharpEmu.HLE.FileSystem;

namespace SharpEmu.GUI;

// Packs a game folder into a .zar on a worker thread and shows its progress. The button cancels
// while packing and closes once done; the dialog returns true when the archive was written. When
// the user opts in, the game folder is deleted afterwards, but only once the finished archive opens
// and holds every file of the folder at the same size.
public partial class ZarPackDialog : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly string _sourceFolder = string.Empty;
    private readonly string _destination = string.Empty;
    private bool _finished;
    private bool _succeeded;

    public ZarPackDialog()
    {
        InitializeComponent();
    }

    public ZarPackDialog(string gameName, string sourceFolder, string destination, bool canDeleteSource = true)
        : this()
    {
        _sourceFolder = sourceFolder;
        _destination = destination;
        DeleteSourceCheck.IsEnabled = canDeleteSource;
        HeadingText.Text = Localization.Instance.Format("Library.Pack.Heading", gameName);
        DestinationText.Text = destination;
        StatusText.Text = Localization.Instance.Get("Library.Pack.Preparing");
        CloseButton.Click += (_, _) => OnCloseClicked();
        MuteMusicButton.IsCheckedChanged += (_, _) =>
        {
            var muted = MuteMusicButton.IsChecked == true;
            MuteMusicButton.Content = muted ? "🔇" : "🔊";
            MuteMusicChanged?.Invoke(muted);
        };
        Opened += async (_, _) => await PackAsync();
        Closing += (_, _) =>
        {
            if (!_finished)
            {
                _cancellation.Cancel();
            }
        };
    }

    // Raised when the mute button changes; the owner silences its title music while it is set.
    public event Action<bool>? MuteMusicChanged;

    private void OnCloseClicked()
    {
        if (_finished)
        {
            Close(_succeeded);
            return;
        }

        CloseButton.IsEnabled = false;
        StatusText.Text = Localization.Instance.Get("Library.Pack.Cancelling");
        _cancellation.Cancel();
    }

    private async Task PackAsync()
    {
        // Progress<T> captures the UI context, so reports arrive on the UI thread.
        var progress = new Progress<ZArchiveWriter.Progress>(report =>
        {
            var fraction = report.BytesTotal == 0 ? 1 : (double)report.BytesDone / report.BytesTotal;
            PackProgress.Value = fraction;
            StatusText.Text = Localization.Instance.Format(
                "Library.Pack.Progress",
                FormatGigabytes(report.BytesDone),
                FormatGigabytes(report.BytesTotal),
                (int)(fraction * 100));
        });

        string? error = null;
        var cancelled = false;
        try
        {
            await Task.Run(() => ZArchiveWriter.Pack(_sourceFolder, _destination, progress, _cancellation.Token));
            _succeeded = true;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
        }

        _finished = true;
        if (cancelled && !IsVisible)
        {
            return;
        }

        if (cancelled)
        {
            Close(false);
            return;
        }

        PackProgress.Value = _succeeded ? 1 : PackProgress.Value;
        StatusText.Text = _succeeded
            ? Localization.Instance.Get("Library.Pack.Done")
            : Localization.Instance.Format("Library.Pack.Failed", error ?? string.Empty);
        DeleteSourceCheck.IsEnabled = false;
        if (_succeeded && DeleteSourceCheck.IsChecked == true)
        {
            StatusText.Text = await DeleteSourceAsync();
        }

        CloseButton.Content = Localization.Instance.Get("Library.Pack.Close");
        CloseButton.IsEnabled = true;
    }

    // Deletes the game folder once the archive is confirmed to hold all of it. Returns the status
    // to show.
    private async Task<string> DeleteSourceAsync()
    {
        var loc = Localization.Instance;
        CloseButton.IsEnabled = false;
        StatusText.Text = loc.Get("Library.Pack.Verifying");
        try
        {
            var source = _sourceFolder;
            var destination = _destination;
            if (!File.Exists(Path.Combine(source, "eboot.bin")) ||
                !await Task.Run(() => ZArchiveWriter.MatchesFolder(destination, source)))
            {
                return loc.Get("Library.Pack.DeleteSkipped");
            }

            StatusText.Text = loc.Get("Library.Pack.Deleting");
            await Task.Run(() => Directory.Delete(source, recursive: true));
            return loc.Get("Library.Pack.DoneDeleted");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return loc.Format("Library.Pack.DeleteFailed", exception.Message);
        }
    }

    private static string FormatGigabytes(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.00");
}
