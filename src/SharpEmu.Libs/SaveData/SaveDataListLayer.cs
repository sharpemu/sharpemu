// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Overlay;
using SkiaSharp;

namespace SharpEmu.Libs.SaveData;

internal readonly record struct SaveDataListItem(
    string Overline,
    string Title,
    string Detail,
    string Date,
    string Size,
    string? IconPath,
    bool IsNew);

internal sealed record SaveDataListView(
    long Session,
    string Caption,
    IReadOnlyList<SaveDataListItem> Items,
    int Focus,
    bool BackEnabled,
    bool DeleteEnabled,
    OverlayInputScheme Scheme,
    bool ConfirmingDelete,
    string? Notice);

internal sealed class SaveDataListLayer : OverlayLayer
{
    internal static readonly SaveDataListLayer Instance = OverlayLayers.Register(new SaveDataListLayer());

    private const float WidthShare = 0.56f;
    private const float HeightShare = 0.62f;

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["Title"] = "Saved Data",
        ["Caption.Load"] = "Select the saved data to load.",
        ["Caption.Save"] = "Select where to save.",
        ["Caption.Delete"] = "Select the saved data to delete.",
        ["Caption.Other"] = "Select saved data.",
        ["Select"] = "Select",
        ["Back"] = "Back",
        ["Move"] = "Move",
        ["Delete"] = "Delete",
        ["Cancel"] = "Cancel",
        ["NewItem"] = "New Saved Data",
        ["NewItemDetail"] = "Create a new save in an empty slot.",
        ["LastSaved"] = "Last saved",
        ["Size"] = "Size",
        ["ConfirmDelete"] = "Delete this saved data?",
        ["ConfirmDeleteNote"] = "Deleted saved data can't be restored.",
        ["InUse"] = "This saved data is in use and can't be deleted.",
    };

    private static readonly OverlayFont ConfirmFont = new(26, OverlayWeight.Bold);

    private readonly OverlayImages _images = new();
    private long _imageSession = -1;
    private SaveDataListView? _view;

    private SaveDataListLayer()
    {
    }

    public override bool IsVisible => Volatile.Read(ref _view) is not null;

    internal static string Text(string key) =>
        OverlayText.Get(OverlayText.KeyPrefix + "SaveData." + key, English.TryGetValue(key, out var value) ? value : key);

    internal void Show(SaveDataListView view)
    {
        Volatile.Write(ref _view, view);
        Invalidate();
    }

    internal void Hide()
    {
        Volatile.Write(ref _view, null);
        Invalidate();
    }

    public override SKRectI Bounds(int surfaceWidth, int surfaceHeight)
    {
        if (surfaceWidth < 200 || surfaceHeight < 160)
        {
            return SKRectI.Empty;
        }

        var scale = surfaceHeight / OverlayStyle.ReferenceHeight;
        var width = Math.Min(surfaceWidth - 32, Math.Max((int)(860 * scale), (int)(surfaceWidth * WidthShare)));
        var height = Math.Min(surfaceHeight - 32, Math.Max((int)(540 * scale), (int)(surfaceHeight * HeightShare)));
        return Centered(surfaceWidth, surfaceHeight, width, height);
    }

    public override int Render(SKCanvas canvas, int width, int height, float unit)
    {
        var view = Volatile.Read(ref _view);
        using var painter = new OverlayPainter(canvas, OverlayPalette.Current, unit);
        painter.Surface(width, height);
        if (view is null)
        {
            return height;
        }

        if (_imageSession != view.Session)
        {
            _images.Clear();
            _imageSession = view.Session;
        }

        DrawPanel(painter, width, height, view);
        return height;
    }

    private void DrawPanel(OverlayPainter p, int width, int height, SaveDataListView view)
    {
        var palette = p.Palette;
        var pad = p.U(OverlayStyle.Padding);
        var countText = view.Items.Count == 0
            ? string.Empty
            : string.Create(OverlayText.Culture, $"{view.Focus + 1} / {view.Items.Count}");
        var countWidth = countText.Length == 0 ? 0 : p.Measure(countText, OverlayStyle.Body) + pad;
        p.Text(Text("Title"), pad, p.U(58), OverlayStyle.Display, palette.Text, width - pad * 2 - countWidth);
        p.Text(countText, width - pad, p.U(58), OverlayStyle.Body, palette.Muted, countWidth, SKTextAlign.Right);
        p.Text(view.Caption, pad, p.U(90), OverlayStyle.Body, palette.Muted, width - pad * 2);

        var contentTop = p.U(OverlayStyle.HeaderHeight);
        var contentBottom = height - p.U(OverlayStyle.FooterHeight);
        var listWidth = (width - pad * 3) * 0.5f;
        DrawList(p, view, new SKRect(pad, contentTop, pad + listWidth, contentBottom));
        if (view.Items.Count > 0)
        {
            DrawHero(p, view.Items[view.Focus],
                new SKRect(pad * 2 + listWidth, contentTop + p.U(6), width - pad, contentBottom));
        }

        p.Divider(pad, contentBottom, width - pad * 2);
        var middle = contentBottom + p.U(OverlayStyle.FooterHeight) / 2;
        var prompts = new List<OverlayPrompt> { new(OverlayButton.Confirm, Text("Select"), "Enter") };
        if (view.BackEnabled)
        {
            prompts.Add(new(OverlayButton.Cancel, Text("Back"), "Esc"));
        }

        if (view.DeleteEnabled && view.Items.Count > 0 && !view.Items[view.Focus].IsNew)
        {
            prompts.Add(new(OverlayButton.Triangle, Text("Delete"), "Del"));
        }

        prompts.Add(new(OverlayButton.DirectionalPad, Text("Move"), "↑ ↓"));
        var promptsWidth = p.PromptsWidth(prompts, view.Scheme);
        p.Prompts(prompts, view.Scheme, width - pad - promptsWidth, middle);
        p.Text(view.Notice, pad, middle + p.U(6), OverlayStyle.Detail, OverlayStyle.Notice,
            width - pad * 2 - promptsWidth - p.U(24));

        if (view.ConfirmingDelete && view.Items.Count > 0)
        {
            DrawConfirm(p, view, width, height);
        }
    }

    private void DrawList(OverlayPainter p, SaveDataListView view, SKRect area)
    {
        var rowHeight = p.U(OverlayStyle.ListRowHeight);
        var gap = p.U(OverlayStyle.Gap);
        var inset = p.U(6);
        var top = area.Top + inset;
        var available = area.Bottom - inset - top;
        var visible = Math.Max(1, (int)((available + gap) / (rowHeight + gap)));
        var first = Math.Clamp(view.Focus - visible + 2, 0, Math.Max(0, view.Items.Count - visible));
        var scrollable = view.Items.Count > visible;
        var right = area.Right - (scrollable ? p.U(14) : 0);

        p.Canvas.Save();
        p.Canvas.ClipRect(new SKRect(area.Left - p.U(12), area.Top, area.Right + p.U(12), area.Bottom));
        var y = top;
        for (var index = first; index < view.Items.Count && index < first + visible; index++)
        {
            DrawRow(p, view.Items[index], index == view.Focus, new SKRect(area.Left + inset, y, right - inset, y + rowHeight));
            y += rowHeight + gap;
        }

        p.Canvas.Restore();
        if (scrollable)
        {
            p.ScrollBar(new SKRect(area.Right - p.U(4), top, area.Right, area.Bottom - inset), first, visible,
                view.Items.Count);
        }
    }

    private void DrawRow(OverlayPainter p, SaveDataListItem item, bool focused, SKRect bounds)
    {
        var palette = p.Palette;
        p.Card(bounds, focused);
        var thumbHeight = bounds.Height - p.U(24);
        var thumb = new SKRect(bounds.Left + p.U(12), bounds.MidY - thumbHeight / 2,
            bounds.Left + p.U(12) + thumbHeight * 16 / 9, bounds.MidY + thumbHeight / 2);
        p.Image(_images.FromFile(item.IconPath), thumb, p.U(OverlayStyle.ImageRadius), item.Title, item.IsNew);

        var textLeft = thumb.Right + p.U(16);
        var textWidth = bounds.Right - p.U(16) - textLeft;
        var lines = Lines(item.Detail, 2);
        var baseline = lines.Length switch
        {
            0 => bounds.MidY + p.U(7),
            1 => bounds.MidY - p.U(4),
            _ => bounds.MidY - p.U(14),
        };
        p.Text(item.Title, textLeft, baseline, OverlayStyle.Title, palette.Text, textWidth);
        for (var line = 0; line < lines.Length; line++)
        {
            baseline += p.U(line == 0 ? 25 : 21);
            p.Text(lines[line], textLeft, baseline, OverlayStyle.Detail,
                line == 0 && focused ? palette.Secondary : palette.Muted, textWidth);
        }
    }

    private void DrawHero(OverlayPainter p, SaveDataListItem item, SKRect area)
    {
        var palette = p.Palette;
        var imageHeight = Math.Min(area.Width * 9 / 16, area.Height - p.U(196));
        var y = area.Top;
        if (imageHeight >= p.U(60))
        {
            p.Image(_images.FromFile(item.IconPath), new SKRect(area.Left, y, area.Right, y + imageHeight),
                p.U(OverlayStyle.HeroImageRadius), item.Title, item.IsNew);
            y += imageHeight + p.U(24);
        }

        if (!string.IsNullOrEmpty(item.Overline))
        {
            y += p.U(15);
            p.Text(item.Overline.ToUpper(OverlayText.Culture), area.Left, y, OverlayStyle.Overline, palette.Faint,
                area.Width);
            y += p.U(12);
        }

        y += p.U(30);
        p.Text(item.Title, area.Left, y, OverlayStyle.Headline, palette.Text, area.Width);
        y += p.U(8);
        foreach (var line in Lines(item.Detail, 3))
        {
            y += p.U(26);
            p.Text(line, area.Left, y, OverlayStyle.Body, palette.Secondary, area.Width);
        }

        if (string.IsNullOrEmpty(item.Date) && string.IsNullOrEmpty(item.Size))
        {
            return;
        }

        y += p.U(34);
        var x = Meta(p, Text("LastSaved"), item.Date, area.Left, y, area.Right);
        Meta(p, Text("Size"), item.Size, x + p.U(28), y, area.Right);
    }

    private static float Meta(OverlayPainter p, string label, string value, float x, float baseline, float right)
    {
        if (string.IsNullOrEmpty(value) || x >= right)
        {
            return x;
        }

        p.Text(label, x, baseline, OverlayStyle.Caption, p.Palette.Faint, right - x);
        var valueLeft = x + p.Measure(label, OverlayStyle.Caption) + p.U(10);
        p.Text(value, valueLeft, baseline, OverlayStyle.CaptionStrong, p.Palette.Secondary, right - valueLeft);
        return valueLeft + p.Measure(value, OverlayStyle.CaptionStrong);
    }

    private static void DrawConfirm(OverlayPainter p, SaveDataListView view, int width, int height)
    {
        var palette = p.Palette;
        p.Dim(new SKRect(0, 0, width, height));
        var boxWidth = Math.Min(width - p.U(40), p.U(640));
        var box = SKRect.Create((width - boxWidth) / 2, (height - p.U(232)) / 2, boxWidth, p.U(232));
        p.Modal(box);
        var inner = box.Width - p.U(56);
        p.Text(Text("ConfirmDelete"), box.MidX, box.Top + p.U(62), ConfirmFont, palette.Text, inner, SKTextAlign.Center);
        p.Text(view.Items[view.Focus].Title, box.MidX, box.Top + p.U(98), OverlayStyle.Body, palette.Secondary, inner,
            SKTextAlign.Center);
        p.Text(Text("ConfirmDeleteNote"), box.MidX, box.Top + p.U(128), OverlayStyle.Detail, palette.Muted, inner,
            SKTextAlign.Center);
        var prompts = new List<OverlayPrompt>
        {
            new(OverlayButton.Confirm, Text("Delete"), "Enter"),
            new(OverlayButton.Cancel, Text("Cancel"), "Esc"),
        };
        var promptsWidth = p.PromptsWidth(prompts, view.Scheme);
        p.Prompts(prompts, view.Scheme, box.MidX - promptsWidth / 2, box.Bottom - p.U(46));
    }

    private static string[] Lines(string text, int max) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(max).ToArray();
}
