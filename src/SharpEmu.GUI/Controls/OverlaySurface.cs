// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace SharpEmu.GUI.Controls;

internal sealed class OverlaySurface : Control
{
    public Action<SKCanvas, Size>? Paint { get; set; }

    public override void Render(DrawingContext context)
    {
        if (Paint is { } paint)
        {
            context.Custom(new Operation(new Rect(Bounds.Size), paint));
        }
    }

    private sealed class Operation(Rect bounds, Action<SKCanvas, Size> paint) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public bool HitTest(Point point) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
            {
                return;
            }

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var saved = canvas.Save();
            try
            {
                paint(canvas, Bounds.Size);
            }
            finally
            {
                canvas.RestoreToCount(saved);
            }
        }
    }
}
