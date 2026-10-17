// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using SharpEmu.HLE.FileSystem;

namespace SharpEmu.GUI;

// Packs a game folder into a .zar on a worker thread and shows its progress. The button cancels
// while packing and closes once done; the dialog returns true when the archive was written. Deleting
// the game folder is offered only after packing, once the finished archive opens and holds every
// file of the folder at the same size, so an interrupted or incomplete archive never costs the folder.
public partial class ZarPackDialog : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly string _sourceFolder = string.Empty;
    private readonly string _destination = string.Empty;
    private readonly bool _canDeleteSource;
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
        _canDeleteSource = canDeleteSource;
        HeadingText.Text = Localization.Instance.Format("Library.Pack.Heading", gameName);
        DestinationText.Text = destination;
        StatusText.Text = Localization.Instance.Get("Library.Pack.Preparing");
        CloseButton.Click += (_, _) => OnCloseClicked();
        DeleteSourceButton.Click += async (_, _) => StatusText.Text = await DeleteSourceAsync();
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
        if (_succeeded && _canDeleteSource)
        {
            CloseButton.IsEnabled = false;
            StatusText.Text = Localization.Instance.Get("Library.Pack.Verifying");
            var verified = await ArchiveHoldsSourceAsync();
            StatusText.Text = Localization.Instance.Get(verified ? "Library.Pack.Done" : "Library.Pack.DeleteSkipped");
            DeleteSourcePanel.IsVisible = verified;
        }

        CloseButton.Content = Localization.Instance.Get("Library.Pack.Close");
        CloseButton.IsEnabled = true;
    }

    // The archive exists, opens, and holds every file of the game folder at the same size.
    private async Task<bool> ArchiveHoldsSourceAsync()
    {
        var source = _sourceFolder;
        var destination = _destination;
        try
        {
            return File.Exists(destination) && File.Exists(Path.Combine(source, "eboot.bin")) &&
                   await Task.Run(() => ZArchiveWriter.MatchesFolder(destination, source));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Deletes the game folder the verified archive holds. Returns the status to show.
    private async Task<string> DeleteSourceAsync()
    {
        var loc = Localization.Instance;
        DeleteSourcePanel.IsVisible = false;
        CloseButton.IsEnabled = false;
        try
        {
            // The archive could have been moved or replaced since it was checked.
            StatusText.Text = loc.Get("Library.Pack.Verifying");
            if (!await ArchiveHoldsSourceAsync())
            {
                return loc.Get("Library.Pack.DeleteSkipped");
            }

            StatusText.Text = loc.Get("Library.Pack.Deleting");
            var source = _sourceFolder;
            await Task.Run(() => Directory.Delete(source, recursive: true));
            return loc.Get("Library.Pack.DoneDeleted");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return loc.Format("Library.Pack.DeleteFailed", exception.Message);
        }
        finally
        {
            CloseButton.IsEnabled = true;
        }
    }

    private static string FormatGigabytes(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.00");
}
