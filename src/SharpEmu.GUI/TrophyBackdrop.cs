// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SharpEmu.GUI;

/// <summary>
/// Blurred key art behind a page. The art is decoded small and blurred once on load, then
/// scaled up, so it costs nothing per frame. With no game (or no art) it shows SharpEmu's
/// emblem as a glow in the theme's accent.
/// </summary>
internal sealed class TrophyBackdrop : Grid
{
    private const int ArtWidth = 96;
    // SharpEmu's own art is an emblem, not a scene: blurred as hard as key art it is only a
    // dark wash, so it is decoded larger and keeps its shape.
    private const int DefaultArtWidth = 320;
    private const int BlurRadius = 3;
    private const double ArtOpacity = 0.7;
    private const double EmblemOpacity = 0.55;
    private const string DefaultArtKey = "avares://SharpEmu.GUI/Assets/pic0.png";

    // Per backdrop, so clearing one never disposes art another is still showing.
    private readonly Dictionary<string, Bitmap?> _art = new(StringComparer.OrdinalIgnoreCase);

    private readonly Image _a = CreateLayer();
    private readonly Image _b = CreateLayer();

    // The emblem is a brightness mask over an accent fill, so it takes every theme's colour
    // (and follows a theme change) instead of the art's fixed blue and purple.
    private readonly Border _emblem = new()
    {
        Background = LauncherThemes.Brush("AccentBrush"),
        Opacity = 0,
        Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(350) }],
    };

    private bool _onA;
    private int _token;

    public TrophyBackdrop()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Children.Add(_a);
        Children.Add(_b);
        Children.Add(_emblem);
        Children.Add(new Border
        {
            Background = LauncherThemes.Brush("TrophyBackdropFadeBrush"),
        });
    }

    public async void Show(GameEntry? game)
    {
        var token = ++_token;
        var path = game?.BackgroundPath ?? game?.CoverPath ?? DefaultArtKey;
        var art = await LoadAsync(path);
        if (token != _token)
        {
            return;
        }

        var next = _onA ? _b : _a;
        var previous = _onA ? _a : _b;
        if (path == DefaultArtKey)
        {
            previous.Opacity = 0;
            next.Opacity = 0;
            if (art is not null)
            {
                _emblem.OpacityMask = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
                _emblem.Opacity = EmblemOpacity;
            }

            return;
        }

        _emblem.Opacity = 0;
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
        _emblem.OpacityMask = null;
        _emblem.Opacity = 0;
        _a.Source = null;
        _b.Source = null;
        _a.Opacity = 0;
        _b.Opacity = 0;
        foreach (var bitmap in _art.Values)
        {
            bitmap?.Dispose();
        }

        _art.Clear();
    }

    private static Image CreateLayer()
    {
        var image = new Image
        {
            Stretch = Stretch.UniformToFill,
            Opacity = 0,
            Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(350) }],
        };
        return image;
    }

    private async Task<Bitmap?> LoadAsync(string path)
    {
        if (_art.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var art = await Task.Run(() =>
        {
            try
            {
                if (path == DefaultArtKey)
                {
                    using var stream = AssetLoader.Open(new Uri(DefaultArtKey));
                    using var decoded = Bitmap.DecodeToWidth(stream, DefaultArtWidth, BitmapInterpolationMode.HighQuality);
                    return Blur(decoded, asMask: true);
                }

                using var small = GameArtCache.Load(path, ArtWidth);
                return small is null ? null : Blur(small);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                return null;
            }
        });
        _art[path] = art;
        return art;
    }

    // Three box passes each way approximate a Gaussian; on a 96 px image that is a few
    // hundred thousand adds, done once per game.
    // asMask keeps only brightness, as alpha over white, for the emblem's opacity mask.
    private static Bitmap Blur(Bitmap source, bool asMask = false)
    {
        var size = source.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var address = handle.AddrOfPinnedObject();
            source.CopyPixels(new PixelRect(size), address, pixels.Length, stride);
            var scratch = new byte[pixels.Length];
            for (var pass = 0; pass < 3; pass++)
            {
                BoxBlur(pixels, scratch, size.Width, size.Height, 4, stride);
                BoxBlur(scratch, pixels, size.Height, size.Width, stride, 4);
            }

            if (asMask)
            {
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    var bright = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                    var alpha = (byte)Math.Min(255, bright * 1.4);
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = alpha;
                }
            }

            return new Bitmap(
                source.Format ?? PixelFormat.Bgra8888,
                source.AlphaFormat ?? AlphaFormat.Premul,
                address,
                size,
                source.Dpi,
                stride);
        }
        finally
        {
            handle.Free();
        }
    }

    // Blurs each line of `count` pixels `step` bytes apart; lines start `lineStep` bytes apart.
    private static void BoxBlur(byte[] from, byte[] to, int count, int lines, int step, int lineStep)
    {
        const int window = (BlurRadius * 2) + 1;
        for (var line = 0; line < lines; line++)
        {
            var start = line * lineStep;
            for (var channel = 0; channel < 4; channel++)
            {
                var origin = start + channel;
                var sum = 0;
                for (var i = -BlurRadius; i <= BlurRadius; i++)
                {
                    sum += from[origin + (Math.Clamp(i, 0, count - 1) * step)];
                }

                for (var i = 0; i < count; i++)
                {
                    to[origin + (i * step)] = (byte)(sum / window);
                    sum += from[origin + (Math.Min(i + BlurRadius + 1, count - 1) * step)];
                    sum -= from[origin + (Math.Max(i - BlurRadius, 0) * step)];
                }
            }
        }
    }
}
