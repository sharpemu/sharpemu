// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SharpEmu.GUI;

// Fades a scroll list out over the room its content keeps around itself (the margin
// that holds the focus ring's glow), so rows that scroll past the edge dissolve instead
// of being cut off by a hard line.
internal static class EdgeFade
{
    public static void Attach(ScrollViewer viewer)
    {
        viewer.SizeChanged += (_, args) => Update(viewer, args.NewSize.Height);
    }

    private static void Update(ScrollViewer viewer, double height)
    {
        if (height <= 0)
        {
            return;
        }

        var room = (viewer.Content as Control)?.Margin ?? default;
        var top = Math.Clamp(room.Top / height, 0, 0.5);
        var bottom = Math.Clamp(1 - (room.Bottom / height), 0.5, 1);
        viewer.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Colors.Black, top),
                new GradientStop(Colors.Black, bottom),
                new GradientStop(Colors.Transparent, 1),
            },
        };
    }
}
