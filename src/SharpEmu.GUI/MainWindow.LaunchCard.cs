// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Threading;

namespace SharpEmu.GUI;

/// <summary>
/// The launch card: a launch answers at once even with the console closed. It shows the game
/// starting until the game's window opens, and why the game stopped when it fails.
/// </summary>
public partial class MainWindow
{
    // A title that never opens a window must not leave the card up for good.
    private static readonly TimeSpan LaunchCardTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan LaunchCardFailureTime = TimeSpan.FromSeconds(15);

    private DispatcherTimer? _launchCardTimer;
    private DateTime _launchStartedAt;
    private bool _gameWindowShown;
    private int _launchCardToken;

    private void InitializeLaunchCard()
    {
        LaunchCardConsoleButton.Click += (_, _) =>
        {
            HideLaunchCard();
            SetActivePage(0);
            ConsoleToggle.IsChecked = true;
        };
        LaunchCardDismissButton.Click += (_, _) => HideLaunchCard();
    }

    private void ShowLaunchStarting(string displayName)
    {
        _gameWindowShown = false;
        _launchStartedAt = DateTime.UtcNow;
        LaunchCard.Classes.Remove("failed");
        LaunchCardIcon.Text = "rocket_launch";
        LaunchCardTitle.Text = Localization.Instance.Format("Launch.Card.Starting", displayName);
        LaunchCardProgress.IsVisible = true;
        LaunchCardProgress.IsIndeterminate = true;
        LaunchCardDismissButton.IsVisible = false;
        UpdateLaunchCardElapsed();
        if (_launchCardTimer is null)
        {
            _launchCardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _launchCardTimer.Tick += (_, _) => UpdateLaunchCardElapsed();
        }

        _launchCardTimer.Start();
        RevealLaunchCard();
    }

    private void UpdateLaunchCardElapsed()
    {
        var elapsed = DateTime.UtcNow - _launchStartedAt;
        if (elapsed >= LaunchCardTimeout)
        {
            OnGameWindowShown();
            return;
        }

        LaunchCardDetail.Text = Localization.Instance.Format("Launch.Card.Loading", (int)elapsed.TotalSeconds);
    }

    // Called when the game reports its window open (or the card gives up waiting).
    private void OnGameWindowShown()
    {
        if (!_isRunning || _gameWindowShown)
        {
            return;
        }

        _gameWindowShown = true;
        HideLaunchCard();

        // The game's window is in front now, so the pause of a full collection goes unseen;
        // done any earlier it stalls the launcher mid-launch.
        GuiMemory.Release();
    }

    private void ShowLaunchFailed(string displayName, int exitCode, string meaning)
    {
        _launchCardTimer?.Stop();
        LaunchCard.Classes.Add("failed");
        LaunchCardIcon.Text = "error";
        LaunchCardTitle.Text = Localization.Instance.Format("Launch.Card.Failed", displayName);
        // The exit meanings are written to follow "code N (...)" in the console, so lower case.
        var reason = meaning.Length == 0 ? meaning : char.ToUpperInvariant(meaning[0]) + meaning[1..];
        LaunchCardDetail.Text = Localization.Instance.Format("Launch.Card.FailedDetail", reason, exitCode);
        LaunchCardProgress.IsIndeterminate = false;
        LaunchCardProgress.IsVisible = false;
        LaunchCardDismissButton.IsVisible = true;
        RevealLaunchCard();

        var token = _launchCardToken;
        DispatcherTimer.RunOnce(
            () =>
            {
                if (token == _launchCardToken)
                {
                    HideLaunchCard();
                }
            },
            LaunchCardFailureTime);
    }

    private void RevealLaunchCard()
    {
        _launchCardToken++;
        LaunchCardConsoleButton.IsVisible = !ConsolePanel.IsVisible && _consoleWindow is null;
        if (LaunchCard.IsVisible)
        {
            return;
        }

        LaunchCard.IsVisible = true;
        SlideIn(LaunchCard, 24);
    }

    private void HideLaunchCard()
    {
        _launchCardToken++;
        _launchCardTimer?.Stop();
        LaunchCardProgress.IsIndeterminate = false;
        LaunchCard.IsVisible = false;
    }
}
