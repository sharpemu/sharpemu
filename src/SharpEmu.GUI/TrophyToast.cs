// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SharpEmu.GUI.Controls;
using SharpEmu.Libs.Overlay;
using SkiaSharp;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpEmu.GUI;

internal sealed record TrophyToastContent(
    string Name,
    string Description,
    int Grade,
    byte[]? IconPng,
    string? GameName = null,
    int Earned = 0,
    int Total = 0);

internal sealed class TrophyToast : Window
{
    private const double WindowWidth = 640;
    private const double WindowHeight = 260;
    private const float CardWidth = 560;
    private const float CardMargin = 36;
    private const float Unit = 0.9f;
    private const double EnterSeconds = 0.32;
    private const double ExitSeconds = 0.4;
    private const double ChimeAt = 0.15;
    private const double Impact = ChimeAt + TrophySound.ImpactDelaySeconds;

    private static readonly Queue<TrophyToastContent> Pending = new();
    private static bool _showing;

    private readonly TrophyToastContent _content;
    private readonly OverlayPalette _palette = LauncherThemes.CurrentOverlayPalette();
    private readonly OverlayNotificationCard _card;
    private readonly IReadOnlyList<OverlayAccent> _accents;
    private readonly bool _reducedMotion = ReducedMotion();
    private readonly double _leaveAt;
    private readonly Stopwatch _clock = new();
    private readonly OverlaySurface _surface = new() { IsHitTestVisible = false };
    private double _time;
    private bool _chimed;
    private bool _closed;

    public static void Show(TrophyToastContent content)
    {
        Pending.Enqueue(content);
        ShowNext();
    }

    private static void ShowNext()
    {
        if (_showing || !Pending.TryDequeue(out var next))
        {
            return;
        }

        _showing = true;
        var toast = new TrophyToast(next);
        toast.Closed += (_, _) =>
        {
            _showing = false;
            ShowNext();
        };
        toast.Show();
        toast.Place();
        toast.MakeClickThrough();
        toast.Start();
    }

    private TrophyToast(TrophyToastContent content)
    {
        _content = content;
        var (light, mid, _) = TrophyMedal.Palette(content.Grade);
        _card = new OverlayNotificationCard(
            new OverlayNotification(
                Localization.Instance.Get("Trophies.Unlocked"),
                TrophyUi.GradeName(content.Grade),
                new SKColor(light.R, light.G, light.B),
                new SKColor(mid.R, mid.G, mid.B),
                content.Name,
                content.Description,
                content.IconPng,
                content.GameName,
                content.Earned,
                content.Total),
            OverlayText.CultureFor(Localization.Instance.CurrentCode));
        _accents = TrophySound.Accents(content.Grade);
        _leaveAt = content.Grade == 1 ? 4.0 : 3.5;

        Width = WindowWidth;
        Height = WindowHeight;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        _surface.Paint = Paint;
        Content = _surface;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    private void Start()
    {
        _clock.Start();
        Tick();
        this.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        if (!_closed && Tick())
        {
            this.RequestAnimationFrame(OnFrame);
        }
    }

    private bool Tick()
    {
        _time = _clock.Elapsed.TotalSeconds;
        if (_time >= _leaveAt + ExitSeconds)
        {
            Close();
            return false;
        }

        if (_time >= ChimeAt && !_chimed)
        {
            _chimed = true;
            TrophySound.Play(_content.Grade);
        }

        _surface.InvalidateVisual();
        return true;
    }

    private void Paint(SKCanvas canvas, Size size)
    {
        var t = _time;
        var enter = _reducedMotion ? Math.Min(1, t / 0.25) : EaseOutCubic(Math.Clamp(t / EnterSeconds, 0, 1));
        var leave = t > _leaveAt ? EaseInOutCubic(Math.Clamp((t - _leaveAt) / ExitSeconds, 0, 1)) : 0;
        var opacity = (float)(enter * (1 - leave));
        if (opacity <= 0)
        {
            return;
        }

        using var painter = new OverlayPainter(canvas, _palette, Unit);
        var left = (float)size.Width - CardMargin - CardWidth;
        var card = SKRect.Create(left, CardMargin, CardWidth, _card.Height(painter, CardWidth));
        var shiftX = _reducedMotion ? 0 : (float)((1 - enter) * 48);
        var shiftY = _reducedMotion ? 0 : (float)(-12 * leave);

        using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)(opacity * 255)) };
        canvas.SaveLayer(layer);
        canvas.Translate(shiftX, shiftY);
        var sinceImpact = t - Impact;
        var charge = Math.Clamp((t - ChimeAt - 0.08) / (Impact - ChimeAt - 0.08), 0, 1);
        var celebration = _reducedMotion
            ? OverlayCelebration.Settled
            : new OverlayCelebration(sinceImpact, charge, _accents, _content.Grade == 1);
        var progress = _reducedMotion ? 1 : EaseOutCubic(Math.Clamp((sinceImpact - 0.15) / 0.8, 0, 1));
        _card.Draw(painter, card, celebration, progress);
        canvas.Restore();
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    private static double EaseInOutCubic(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private void Place()
    {
        var screen = Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        Position = new PixelPoint(area.Right - (int)(Width * scale), area.Y);
    }

    private void MakeClickThrough()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle() is not { } handle)
        {
            return;
        }

        const int gwlExStyle = -20;
        const long wsExTransparent = 0x20;
        const long wsExNoActivate = 0x08000000;
        const long wsExToolWindow = 0x80;
        var style = GetWindowLongPtr(handle.Handle, gwlExStyle).ToInt64();
        SetWindowLongPtr(handle.Handle, gwlExStyle, new IntPtr(style | wsExTransparent | wsExNoActivate | wsExToolWindow));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

    private static bool ReducedMotion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        const uint spiGetClientAreaAnimation = 0x1042;
        var enabled = 1;
        return SystemParametersInfo(spiGetClientAreaAnimation, 0, ref enabled, 0) && enabled == 0;
    }
}
