// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut.Upscaling;

namespace SharpEmu.GUI;

public partial class UpscalerInstallDialog : SheetView
{
    private readonly CancellationTokenSource _cancellation = new();
    private bool _finished;

    internal Task Installation { get; private set; } = Task.CompletedTask;

    public UpscalerInstallDialog()
    {
        InitializeComponent();
    }

    internal UpscalerInstallDialog(string sdk, SuperResolutionBackend backend, string destination) : this()
    {
        HeadingText.Text = sdk;
        DestinationText.Text = destination;
        StatusText.Text = Localization.Instance.Format("Options.Pssr.Install.Downloading", 0);
        CloseButton.Click += (_, _) => RequestCancel();
        Opened += (_, _) => Installation = InstallAsync(backend, destination);
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
        if (_finished)
        {
            Close(false);
        }
        else
        {
            CloseButton.IsEnabled = false;
            _cancellation.Cancel();
        }
    }

    private async Task InstallAsync(SuperResolutionBackend backend, string destination)
    {
        var localization = Localization.Instance;
        var progress = new Progress<int>(percent =>
        {
            if (_finished || _cancellation.IsCancellationRequested)
            {
                return;
            }

            DownloadProgress.Value = percent;
            DownloadProgress.IsIndeterminate = percent == 100;
            StatusText.Text = percent < 100
                ? localization.Format("Options.Pssr.Install.Downloading", percent)
                : localization.Get("Options.Pssr.Install.Verifying");
        });

        try
        {
            var token = _cancellation.Token;
            await Task.Run(() => UpscalerInstaller.InstallAsync(backend, destination, progress, token));
            _finished = true;
            Close(true);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            _finished = true;
            Close(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException
            or InvalidDataException or OperationCanceledException)
        {
            _finished = true;
            DownloadProgress.IsVisible = false;
            StatusText.Text = localization.Format("Options.Pssr.Install.Failed", exception.Message);
            CloseButton.Content = localization.Get("Library.Pack.Close");
            CloseButton.IsEnabled = true;
        }
        finally
        {
            _cancellation.Dispose();
        }
    }
}
