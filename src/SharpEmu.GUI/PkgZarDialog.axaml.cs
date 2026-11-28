// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using SharpEmu.HLE.FileSystem.Pkg;

namespace SharpEmu.GUI;

// Converts a .pkg straight into a .zar on a worker thread and shows its progress. The button
// cancels while converting. A direct conversion (the .pkg stays) that is cancelled closes the sheet
// and the core removes its partial archive. A consuming conversion (the .pkg is deleted as it is
// converted) only pauses on cancel or when the disk fills up: the sheet stays open with a Resume
// button, and starting the same install later resumes too. The sheet closes itself on success
// (returning true); a failure stays open and exposes ErrorMessage so the owner can log it.
public partial class PkgZarDialog : SheetView
{
    // A fresh source per run: a resumed conversion must not inherit the cancelled one.
    private CancellationTokenSource _run = new();
    private readonly string _pkgPath = string.Empty;
    private readonly string _destination = string.Empty;
    private readonly bool _consume;
    private bool _finished;
    private bool _succeeded;

    public PkgZarDialog()
    {
        InitializeComponent();
    }

    public PkgZarDialog(string gameName, string pkgPath, string destination, bool consumeSource, bool resuming = false)
        : this()
    {
        _pkgPath = pkgPath;
        _destination = destination;
        _consume = consumeSource;
        var loc = Localization.Instance;
        HeadingText.Text = loc.Format(resuming ? "Library.PkgZar.HeadingResume" : "Library.PkgZar.Heading", gameName);
        ExplanationText.Text = loc.Get(consumeSource ? "Library.PkgZar.ExplanationConsume" : "Library.PkgZar.Explanation");
        DestinationText.Text = destination;
        StatusText.Text = loc.Get("Library.Pkg.Preparing");
        CloseButton.Click += (_, _) => OnCloseClicked();
        ResumeButton.Click += async (_, _) => await ConvertAsync();
        Opened += async (_, _) => await ConvertAsync();
        Closed += (_, _) =>
        {
            if (!_finished)
            {
                _run.Cancel();
            }
        };
    }

    // Set when the conversion failed with something other than a pause.
    public string? ErrorMessage { get; private set; }

    public override Control? InitialFocus => CloseButton;

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
        StatusText.Text = Localization.Instance.Get(_consume ? "Library.PkgZar.Pausing" : "Library.Pkg.Cancelling");
        _run.Cancel();
    }

    private async Task ConvertAsync()
    {
        var loc = Localization.Instance;
        _finished = false;
        ErrorMessage = null;
        ResumeButton.IsVisible = false;
        CloseButton.IsEnabled = true;
        CloseButton.Content = loc.Get(_consume ? "Library.PkgZar.Pause" : "Common.Cancel");
        StatusText.Foreground = LauncherThemes.Brush("MutedBrush");
        StatusText.Text = loc.Get("Library.Pkg.Preparing");

        _run.Dispose();
        _run = new CancellationTokenSource();
        var token = _run.Token;

        // Progress<T> captures the UI context, so reports arrive on the UI thread.
        var progress = new Progress<PkgExtractProgress>(report =>
        {
            var fraction = report.BytesTotal == 0 ? 1 : (double)report.BytesDone / report.BytesTotal;
            ConvertProgress.Value = fraction;
            FileText.Text = report.CurrentFile;
            StatusText.Text = loc.Format(
                "Library.Pkg.Progress",
                FormatGigabytes(report.BytesDone),
                FormatGigabytes(report.BytesTotal),
                (int)(fraction * 100));
        });

        string? error = null;
        var cancelled = false;
        var outOfSpace = false;
        try
        {
            await Task.Run(() => PkgZarConverter.Convert(_pkgPath, _destination, _consume, progress, token));
            _succeeded = true;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (PkgConversionPausedException)
        {
            outOfSpace = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = exception.Message;
        }

        _finished = true;
        if (!IsOpen)
        {
            return;
        }

        if (_succeeded)
        {
            Close(true);
            return;
        }

        if (cancelled && !_consume)
        {
            Close(false);
            return;
        }

        CloseButton.Content = loc.Get("Library.Pack.Close");
        CloseButton.IsEnabled = true;
        if (_consume && (cancelled || outOfSpace))
        {
            // The journal keeps the progress; nothing is lost and nothing claims success.
            StatusText.Foreground = outOfSpace ? LauncherThemes.Brush("DangerBrush") : StatusText.Foreground;
            StatusText.Text = loc.Get(outOfSpace ? "Library.PkgZar.OutOfSpace" : "Library.PkgZar.Paused");
            ResumeButton.IsVisible = true;
            ResumeButton.Focus();
            return;
        }

        if (outOfSpace)
        {
            error = loc.Get("Library.PkgZar.OutOfSpaceDirect");
        }

        ErrorMessage = error ?? string.Empty;
        StatusText.Foreground = LauncherThemes.Brush("DangerBrush");
        StatusText.Text = loc.Format("Library.PkgZar.Failed", ErrorMessage);
        if (_consume && File.Exists(PkgZarConverter.JournalPath(_destination)))
        {
            StatusText.Text += " " + loc.Get("Library.PkgZar.FailedResumable");
        }
    }

    private static string FormatGigabytes(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.00");
}
