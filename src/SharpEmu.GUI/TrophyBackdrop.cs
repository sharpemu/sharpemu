// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SharpEmu.GUI;

internal sealed class TrophyBackdrop : Grid
{
    private const int ArtWidth = 96;
    private const double ArtOpacity = 0.5;

    private static readonly Dictionary<string, Bitmap?> Art = new(StringComparer.OrdinalIgnoreCase);

    private readonly Image _a = CreateLayer();
    private readonly Image _b = CreateLayer();
    private bool _onA;
    private int _token;

    public TrophyBackdrop()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Children.Add(_a);
        Children.Add(_b);
        Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#801B1B1B"), 0),
                    new GradientStop(Color.Parse("#E01B1B1B"), 0.45),
                    new GradientStop(Color.Parse("#FA1B1B1B"), 1),
                },
            },
        });
    }

    public async void Show(GameEntry? game)
    {
        var token = ++_token;
        var art = game?.BackgroundPath is { } path ? await LoadAsync(path) : null;
        if (token != _token)
        {
            return;
        }

        var next = _onA ? _b : _a;
        var previous = _onA ? _a : _b;
        if (art is null)
        {
            previous.Opacity = 0;
            next.Opacity = 0;
            return;
        }

        if (ReferenceEquals(previous.Source, art) && previous.Opacity > 0)
        {
            return;
        }

        next.Source = art;
        next.Opacity = ArtOpacity;
        previous.Opacity = 0;
        _onA = !_onA;
    }

    public void Clear()
    {
        ++_token;
        _a.Source = null;
        _b.Source = null;
        _a.Opacity = 0;
        _b.Opacity = 0;
        foreach (var bitmap in Art.Values)
        {
            bitmap?.Dispose();
        }

        Art.Clear();
    }

    private static Image CreateLayer()
    {
        var image = new Image
        {
            Stretch = Stretch.UniformToFill,
            Opacity = 0,
            Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(500) }],
        };
        RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        return image;
    }

    private static async Task<Bitmap?> LoadAsync(string path)
    {
        if (Art.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var art = await Task.Run(() =>
        {
            try
            {
                return GameArtCache.Load(path, ArtWidth);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                return null;
            }
        });
        Art[path] = art;
        return art;
    }
}
