// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace SharpEmu.GUI;

internal sealed class TrophyPage : UserControl
{
    private static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(260);

    private readonly TrophyHubView _hub = new();
    private readonly TrophyGameView _game = new() { IsVisible = false };
    private IReadOnlyList<GameEntry> _games = [];
    private bool _inGame;
    private int _navigation;

    public TrophyPage()
    {
        Focusable = true;
        var stage = new Grid();
        stage.Children.Add(_hub);
        stage.Children.Add(_game);
        Content = stage;

        _hub.GameChosen += (game, directory) => OpenGame(game, directory);
        _game.BackRequested += ShowCollection;
        KeyDown += OnKeyDown;
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.XButton1Pressed && _inGame)
            {
                ShowCollection();
                e.Handled = true;
            }
        };
    }

    private TrophyBackdrop? _backdrop;

    public TrophyBackdrop? Backdrop
    {
        set
        {
            _backdrop = value;
            _hub.Backdrop = value;
            _game.Backdrop = value;
        }
    }

    public void Unload()
    {
        ++_navigation;
        _inGame = false;
        _game.IsVisible = false;
        _hub.IsVisible = true;
        Place(_hub, 0, 1);
        _hub.Unload();
        _game.Unload();
        _backdrop?.Clear();
    }

    public async void ShowCollection(IReadOnlyList<GameEntry> games)
    {
        _games = games;
        Focus();
        if (_inGame)
        {
            Slide(_game, _hub, forward: false);
            _inGame = false;
        }

        await _hub.ShowAsync(games);
    }

    public async void ShowGame(IReadOnlyList<GameEntry> games, GameEntry game)
    {
        _games = games;
        Focus();
        if (TrophyUi.GameDirectory(game.Path) is not { } directory)
        {
            return;
        }

        ++_navigation;
        _game.Show(game, directory);
        _game.IsVisible = true;
        Place(_game, 0, 1);
        Place(_hub, 0, 0);
        _hub.IsVisible = false;
        _inGame = true;
        await _hub.ShowAsync(games);
    }

    private void ShowCollection()
    {
        Slide(_game, _hub, forward: false);
        _inGame = false;
        Focus();
        _ = _hub.ShowAsync(_games);
    }

    private void OpenGame(GameEntry game, string directory)
    {
        _game.Show(game, directory);
        Slide(_hub, _game, forward: true);
        _inGame = true;
        Focus();
    }

    private void Slide(Control leaving, Control arriving, bool forward)
    {
        var token = ++_navigation;
        var distance = forward ? -48 : 48;
        arriving.IsVisible = true;
        Move(leaving, 0, distance, 1, 0);
        Move(arriving, -distance, 0, 0, 1);
        DispatcherTimer.RunOnce(
            () =>
            {
                if (token == _navigation)
                {
                    leaving.IsVisible = false;
                }
            },
            SlideTime + TimeSpan.FromMilliseconds(40));
    }

    private static void Place(Control control, double x, float opacity) =>
        Move(control, x, x, opacity, opacity, TimeSpan.FromMilliseconds(1));

    private static void Move(Control control, double fromX, double toX, float fromOpacity, float toOpacity) =>
        Move(control, fromX, toX, fromOpacity, toOpacity, SlideTime);

    private static void Move(Control control, double fromX, double toX, float fromOpacity, float toOpacity, TimeSpan duration)
    {
        if (ElementComposition.GetElementVisual(control) is not { } visual)
        {
            return;
        }

        var easing = new CubicEaseOut();
        var translation = visual.Compositor.CreateVector3DKeyFrameAnimation();
        translation.Target = "Translation";
        translation.Duration = duration;
        translation.InsertKeyFrame(0f, new Vector3D(fromX, 0, 0));
        translation.InsertKeyFrame(1f, new Vector3D(toX, 0, 0), easing);
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = "Opacity";
        opacity.Duration = duration;
        opacity.InsertKeyFrame(0f, fromOpacity);
        opacity.InsertKeyFrame(1f, toOpacity, easing);
        visual.StartAnimation("Translation", translation);
        visual.StartAnimation("Opacity", opacity);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Back && _inGame)
        {
            ShowCollection();
            e.Handled = true;
        }
        else if (_inGame ? _game.HandleKey(e.Key) : _hub.HandleKey(e.Key))
        {
            e.Handled = true;
        }
    }
}
