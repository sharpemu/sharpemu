// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using SharpEmu.Logging;

namespace SharpEmu.GUI;

/// <summary>Coordinates update checks, user prompts, and downloads for the main window.</summary>
internal sealed class UpdateController
{
    private readonly Window _owner;
    private readonly Button _updateButton;
    private readonly TextBlock _statusText;
    private readonly ToggleSwitch _reminderToggle;
    private readonly GuiSettings _settings;
    private Updater.UpdateInfo? _availableUpdate;
    private string _statusKey = "Updater.Status.Ready";
    private object?[] _statusArgs = [BuildInfo.CommitSha ?? "dev"];

    public UpdateController(
        Window owner,
        Button updateButton,
        TextBlock statusText,
        ToggleSwitch reminderToggle,
        GuiSettings settings)
    {
        _owner = owner;
        _updateButton = updateButton;
        _statusText = statusText;
        _reminderToggle = reminderToggle;
        _settings = settings;

        _updateButton.Click += async (_, _) => await OnUpdateButtonAsync();
        _reminderToggle.IsCheckedChanged += (_, _) =>
            _settings.ShowUpdateNotifications = _reminderToggle.IsChecked == true;
        RefreshText();
    }

    public async Task CheckForUpdatesAsync(bool forcePrompt = false)
    {
        _availableUpdate = null;
        _updateButton.IsEnabled = false;
        SetStatus("Updater.Status.Checking");
        try
        {
            _availableUpdate = await Updater.CheckAsync(BuildInfo.CommitSha);
            SetStatus(
                _availableUpdate is null ? "Updater.Status.Current" : "Updater.Status.Available",
                _availableUpdate?.Sha ?? BuildInfo.CommitSha ?? "dev");
            if (_availableUpdate is not null && (forcePrompt || _settings.ShowUpdateNotifications))
            {
                await ShowUpdateDialogAsync();
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Updater.Status.Timeout");
        }
        catch (PlatformNotSupportedException)
        {
            SetStatus("Updater.Status.Unsupported");
        }
        catch (Updater.RateLimitException)
        {
            SetStatus("Updater.Status.RateLimited");
        }
        catch
        {
            SetStatus("Updater.Status.Failed");
        }
        finally
        {
            _updateButton.IsEnabled = true;
            RefreshText();
        }
    }

    public void RefreshText()
    {
        _statusText.Text = Localization.Instance.Format(_statusKey, _statusArgs);
        _updateButton.Content = Localization.Instance.Get("Updater.Check");
    }

    private async Task OnUpdateButtonAsync()
    {
        if (_availableUpdate is null)
        {
            await CheckForUpdatesAsync(forcePrompt: true);
            return;
        }

        await ShowUpdateDialogAsync();
    }

    private async Task DownloadAvailableUpdateAsync(Updater.UpdateInfo update)
    {
        _updateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(value => SetStatus("Updater.Status.Downloading", value));
            await Updater.DownloadAndRestartAsync(update, progress);
            SetStatus("Updater.Status.Installing");
            _owner.Close();
        }
        catch (InvalidDataException)
        {
            SetStatus("Updater.Status.ChecksumFailed");
            _updateButton.IsEnabled = true;
        }
        catch
        {
            SetStatus("Updater.Status.Failed");
            _updateButton.IsEnabled = true;
        }
    }

    private async Task ShowUpdateDialogAsync()
    {
        if (_availableUpdate is null)
        {
            return;
        }

        var result = await new UpdateDialog(_availableUpdate).ShowDialog<UpdateDialog.Result?>(_owner);
        if (result is null)
        {
            return;
        }

        if (result.SuppressReminder)
        {
            _settings.ShowUpdateNotifications = false;
            _reminderToggle.IsChecked = false;
            _settings.Save();
        }

        if (result.Download)
        {
            await DownloadAvailableUpdateAsync(_availableUpdate);
        }
    }

    private void SetStatus(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        RefreshText();
    }
}
