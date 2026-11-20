// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using SharpEmu.HLE.FileSystem.Pkg;

namespace SharpEmu.GUI;

// Extracts a .pkg into a game folder on a worker thread and shows its progress. The button cancels
// while installing. The sheet closes itself on success (returning true) and stays open on a
// failure. A cancelled or failed install removes the partial folder, unless it was there before.
public partial class PkgInstallDialog : SheetView
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly string _pkgPath = string.Empty;
    private readonly string _destination = string.Empty;
    private readonly bool _destinationExisted;
    private bool _finished;
    private bool _succeeded;

    public PkgInstallDialog()
    {
        InitializeComponent();
    }

    public PkgInstallDialog(string gameName, string pkgPath, string destination)
        : this()
    {
        _pkgPath = pkgPath;
        _destination = destination;
        _destinationExisted = Directory.Exists(destination);
        HeadingText.Text = Localization.Instance.Format("Library.Pkg.Heading", gameName);
        DestinationText.Text = destination;
        StatusText.Text = Localization.Instance.Get("Library.Pkg.Preparing");
        CloseButton.Click += (_, _) => OnCloseClicked();
        Opened += async (_, _) => await InstallAsync();
        Closed += (_, _) =>
        {
            if (!_finished)
            {
                _cancellation.Cancel();
            }
        };
    }

    public override void RequestCancel()
    {
        if (CloseButton.IsEnabled)
        {
            OnCloseClicked();
        }
    }

    private void OnCloseClicked()
    {
        if (_finished)
        {
            Close(_succeeded);
            return;
        }

        CloseButton.IsEnabled = false;
        StatusText.Text = Localization.Instance.Get("Library.Pkg.Cancelling");
        _cancellation.Cancel();
    }

    private async Task InstallAsync()
    {
        // Progress<T> captures the UI context, so reports arrive on the UI thread.
        var progress = new Progress<PkgExtractProgress>(report =>
        {
            var fraction = report.BytesTotal == 0 ? 1 : (double)report.BytesDone / report.BytesTotal;
            InstallProgress.Value = fraction;
            FileText.Text = report.CurrentFile;
            StatusText.Text = Localization.Instance.Format(
                "Library.Pkg.Progress",
                FormatGigabytes(report.BytesDone),
                FormatGigabytes(report.BytesTotal),
                (int)(fraction * 100));
        });

        string? error = null;
        var cancelled = false;
        try
        {
            await Task.Run(() =>
            {
                using var package = GamePackage.Open(_pkgPath);
                package.Extract(_destination, progress, _cancellation.Token);
            });
            _succeeded = true;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = exception.Message;
        }

        if (!_succeeded && !_destinationExisted)
        {
            // A partial game folder would look installed yet not boot. Never delete a folder the
            // user already had: the title id can collide with their own directory.
            try
            {
                if (Directory.Exists(_destination))
                {
                    await Task.Run(() => Directory.Delete(_destination, recursive: true));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error ??= exception.Message;
            }
        }

        _finished = true;
        if (cancelled && !IsOpen)
        {
            return;
        }

        if (cancelled)
        {
            Close(false);
            return;
        }

        // A finished install closes by itself, so a batch of packages runs without a click per
        // game; only a failure waits for the user to read it.
        if (_succeeded)
        {
            if (IsOpen)
            {
                Close(true);
            }

            return;
        }

        StatusText.Text = Localization.Instance.Format("Library.Pkg.Failed", error ?? string.Empty);
        CloseButton.Content = Localization.Instance.Get("Library.Pack.Close");
        CloseButton.IsEnabled = true;
    }

    private static string FormatGigabytes(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.00");
}
