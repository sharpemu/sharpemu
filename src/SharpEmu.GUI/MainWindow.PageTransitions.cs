// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace SharpEmu.GUI;

public partial class MainWindow
{
    private static readonly TimeSpan PageTransitionTime = TimeSpan.FromMilliseconds(260);
    private static readonly int[] PageOrder = [0, 2, 1];

    private int _pageTransition;

    private Control? PageControl(int index) => index switch
    {
        0 => LibraryPage,
        1 => OptionsPage,
        2 => TrophyPageHost,
        _ => null,
    };

    private void AnimatePageChange(int from, int to)
    {
        var token = ++_pageTransition;
        MarkControllerFocus(null);
        var direction = Array.IndexOf(PageOrder, to) > Array.IndexOf(PageOrder, from) ? 1 : -1;
        if (PageControl(to) is { } arriving)
        {
            SlideIn(arriving, 36 * direction);
        }

        if (to == 0)
        {
            SlideIn(LibraryToolbar, 0);
        }

        var surfaceBefore = from is 1 or 2;
        var surfaceAfter = to is 1 or 2;
        if (surfaceAfter && !surfaceBefore)
        {
            FadeTo(OptionsPageSurface, 1, fromZero: true);
        }
        else if (surfaceBefore && !surfaceAfter)
        {
            FadeOut(OptionsPageSurface, token);
        }

        if (to == 1)
        {
            FadeTo(OptionsBackdrop, 1, fromZero: from != 1);
        }
        else if (from == 1)
        {
            FadeOut(OptionsBackdrop, token);
        }

        if (to == 2)
        {
            FadeTo(TrophyBackdropHost, 1, fromZero: from != 2);
        }
        else if (from == 2)
        {
            FadeOut(TrophyBackdropHost, token);
            DispatcherTimer.RunOnce(
                () =>
                {
                    if (_activePageIndex != 2)
                    {
                        TrophyPageHost.Unload();
                        GuiMemory.Trim();
                    }
                },
                PageTransitionTime + TimeSpan.FromMilliseconds(120));
        }
    }

    private void FadeOut(Control control, int token)
    {
        control.IsVisible = true;
        FadeTo(control, 0, fromZero: false);
        DispatcherTimer.RunOnce(
            () =>
            {
                if (token == _pageTransition)
                {
                    control.IsVisible = _activePageIndex is 1 or 2 && control == OptionsPageSurface ||
                                        _activePageIndex == 2 && control == TrophyBackdropHost;
                }
            },
            PageTransitionTime + TimeSpan.FromMilliseconds(40));
    }

    private static void FadeTo(Control control, float opacity, bool fromZero)
    {
        if (ElementComposition.GetElementVisual(control) is not { } visual)
        {
            return;
        }

        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.Target = "Opacity";
        animation.Duration = PageTransitionTime;
        if (fromZero)
        {
            animation.InsertKeyFrame(0f, 0f);
        }
        else
        {
            animation.InsertExpressionKeyFrame(0f, "this.StartingValue");
        }

        animation.InsertKeyFrame(1f, opacity, new CubicEaseOut());
        visual.StartAnimation("Opacity", animation);
    }

    private static void SlideIn(Control control, double fromX)
    {
        if (ElementComposition.GetElementVisual(control) is not { } visual)
        {
            return;
        }

        var easing = new CubicEaseOut();
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = "Opacity";
        opacity.Duration = PageTransitionTime;
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f, easing);
        var translation = visual.Compositor.CreateVector3DKeyFrameAnimation();
        translation.Target = "Translation";
        translation.Duration = PageTransitionTime;
        translation.InsertKeyFrame(0f, new Vector3D(fromX, 0, 0));
        translation.InsertKeyFrame(1f, new Vector3D(0, 0, 0), easing);
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", translation);
    }
}
