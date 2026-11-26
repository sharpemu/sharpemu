// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Overlay;
using SkiaSharp;

namespace SharpEmu.Libs.Ime;

internal sealed class ImeKeyboardLayer : OverlayLayer
{
    internal static readonly ImeKeyboardLayer Instance = OverlayLayers.Register(new ImeKeyboardLayer());

    private const float Pad = 26;
    private const float HeaderHeight = 44;
    private const float FieldHeight = 62;
    private const float KeyHeight = 58;
    private const float KeyGap = 8;
    private const float FooterHeight = 54;
    private const float HintScale = 0.8f;

    private static readonly OverlayFont FieldFont = new(24, OverlayWeight.Regular);
    private static readonly OverlayFont KeyFont = new(22, OverlayWeight.Regular);
    private static readonly OverlayFont KeyLabelFont = new(16, OverlayWeight.Bold);

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["Title"] = "Enter text",
        ["Done"] = "Done",
        ["Send"] = "Send",
        ["Search"] = "Search",
        ["Go"] = "Go",
        ["Close"] = "Close",
        ["Delete"] = "Delete",
        ["Space"] = "Space",
        ["Cursor"] = "Cursor",
        ["Select"] = "Select",
        ["Type"] = "Type with your keyboard",
    };

    private ImeKeyboardView? _view;

    private ImeKeyboardLayer()
    {
    }

    public override bool IsVisible => Volatile.Read(ref _view) is not null;

    internal static string Text(string key) =>
        OverlayText.Get(OverlayText.KeyPrefix + "Ime." + key, English.TryGetValue(key, out var value) ? value : key);

    internal void Show(ImeKeyboardView view)
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
        if (surfaceWidth < 240 || surfaceHeight < 200)
        {
            return SKRectI.Empty;
        }

        var unit = OverlayStyle.UnitFor(surfaceHeight);
        var scale = surfaceHeight / OverlayStyle.ReferenceHeight;
        var width = Math.Min(surfaceWidth - 32, Math.Max((int)(1000 * scale), (int)(surfaceWidth * 0.6f)));
        var height = Math.Min(surfaceHeight - 32, (int)Math.Ceiling(PanelHeight * unit));
        var bottomMargin = Math.Max(16, (int)(surfaceHeight * 0.05f));
        return SKRectI.Create((surfaceWidth - width) / 2, Math.Max(16, surfaceHeight - height - bottomMargin), width, height);
    }

    private static float PanelHeight =>
        Pad + HeaderHeight + FieldHeight + 18 + 5 * KeyHeight + 4 * KeyGap + 18 + FooterHeight;

    public override int Render(SKCanvas canvas, int width, int height, float unit)
    {
        var view = Volatile.Read(ref _view);
        using var p = new OverlayPainter(canvas, OverlayPalette.Current, unit);
        p.Surface(width, height);
        if (view is null)
        {
            return height;
        }

        var palette = p.Palette;
        var pad = p.U(Pad);
        var counter = string.Create(OverlayText.Culture, $"{view.Length} / {view.MaxLength}");
        var counterWidth = p.Measure(counter, OverlayStyle.Caption) + pad;
        var title = string.IsNullOrWhiteSpace(view.Title) ? Text("Title") : view.Title;
        p.Text(title, pad, pad + p.U(22), OverlayStyle.Title, palette.Text, width - pad * 2 - counterWidth);
        p.Text(counter, width - pad, pad + p.U(22), OverlayStyle.Caption, palette.Muted, counterWidth, SKTextAlign.Right);

        var field = SKRect.Create(pad, pad + p.U(HeaderHeight), width - pad * 2, p.U(FieldHeight));
        DrawField(p, view, field);

        var keysTop = field.Bottom + p.U(18);
        var keysWidth = width - pad * 2;
        var gap = p.U(KeyGap);
        var keyUnit = (keysWidth - gap * (ImeKeyboardLayout.Columns - 1)) / ImeKeyboardLayout.Columns;
        for (var row = 0; row < view.Rows.Count; row++)
        {
            var keys = view.Rows[row];
            var rowWidth = ImeKeyboardLayout.RowWidth(keys);
            var x = pad + (ImeKeyboardLayout.Columns - rowWidth) / 2 * (keyUnit + gap);
            var y = keysTop + row * (p.U(KeyHeight) + gap);
            for (var column = 0; column < keys.Count; column++)
            {
                var key = keys[column];
                var keyWidth = key.Width * keyUnit + (key.Width - 1) * gap;
                DrawKey(p, view, key, SKRect.Create(x, y, keyWidth, p.U(KeyHeight)),
                    row == view.FocusRow && column == view.FocusColumn);
                x += keyWidth + gap;
            }
        }

        var footerTop = height - p.U(FooterHeight);
        p.Divider(pad, footerTop, width - pad * 2);
        DrawFooter(p, view, pad, footerTop + p.U(FooterHeight) / 2, width - pad);
        return height;
    }

    private static void DrawField(OverlayPainter p, ImeKeyboardView view, SKRect field)
    {
        var palette = p.Palette;
        var radius = p.U(OverlayStyle.CardRadius);
        p.RoundRect(field, radius, palette.Elevated);
        p.Outline(field, radius, palette.Accent, 2);

        var inset = p.U(18);
        var inner = new SKRect(field.Left + inset, field.Top, field.Right - inset, field.Bottom);
        var baseline = field.MidY + p.U(9);
        if (view.Text.Length == 0)
        {
            p.Text(view.Placeholder, inner.Left, baseline, FieldFont, palette.Faint, inner.Width);
        }

        var caretX = p.Measure(view.Text[..Math.Clamp(view.Caret, 0, view.Text.Length)], FieldFont);
        var offset = Math.Max(0, caretX - inner.Width + p.U(4));
        p.Canvas.Save();
        p.Canvas.ClipRect(inner);
        p.Text(view.Text, inner.Left - offset, baseline, FieldFont, palette.Text, float.MaxValue);
        if (view.CaretVisible)
        {
            var caret = SKRect.Create(inner.Left - offset + caretX, field.MidY - p.U(15), Math.Max(2, p.U(2.5f)), p.U(30));
            p.Rect(caret, palette.Accent);
        }

        p.Canvas.Restore();
    }

    private static void DrawKey(OverlayPainter p, ImeKeyboardView view, ImeKey key, SKRect bounds, bool focused)
    {
        var palette = p.Palette;
        var radius = p.U(8);
        var disabled = key.Kind == ImeKeyKind.Character && view.NumbersOnly && !IsNumeric(key.Lower) ||
                       key.Kind == ImeKeyKind.Shift && view.Page == ImePage.Symbols;
        if (key.Kind == ImeKeyKind.Done)
        {
            if (focused)
            {
                var ring = bounds;
                ring.Inflate(p.U(3), p.U(3));
                p.Outline(ring, radius + p.U(3), palette.Text, 2.5f);
            }

            p.RoundRect(bounds, radius, palette.Accent);
            p.Text(DoneLabel(view.EnterLabel), bounds.MidX, bounds.MidY + p.U(6), KeyLabelFont, palette.AccentForeground,
                bounds.Width - p.U(12), SKTextAlign.Center);
            DrawHint(p, view, key, bounds, palette.AccentForeground);
            return;
        }

        p.Card(bounds, focused);
        if (key.Kind == ImeKeyKind.Shift && view.Shift == ImeShift.Locked && !disabled)
        {
            p.RoundRect(bounds, radius, palette.Accent.WithAlpha(0x40));
        }

        var color = disabled ? palette.Faint : palette.Text;
        switch (key.Kind)
        {
            case ImeKeyKind.Character:
                var label = view.Shift == ImeShift.Off || view.Page == ImePage.Symbols ? key.Lower : key.Upper;
                p.Text(label, bounds.MidX, bounds.MidY + p.U(8), KeyFont, color, bounds.Width, SKTextAlign.Center);
                break;
            case ImeKeyKind.Shift:
                p.Text("⇧", bounds.MidX, bounds.MidY + p.U(8), KeyFont,
                    view.Shift != ImeShift.Off && !disabled ? palette.Accent : color, bounds.Width, SKTextAlign.Center);
                break;
            case ImeKeyKind.Page:
                var page = view.Page switch
                {
                    ImePage.Letters => "!#1",
                    ImePage.Symbols => "àé",
                    _ => "ABC",
                };
                p.Text(page, bounds.MidX, bounds.MidY + p.U(6), KeyLabelFont, color, bounds.Width, SKTextAlign.Center);
                break;
            case ImeKeyKind.Space:
                p.Text(Text("Space"), bounds.MidX, bounds.MidY + p.U(6), KeyLabelFont, palette.Secondary, bounds.Width,
                    SKTextAlign.Center);
                break;
            case ImeKeyKind.Backspace:
                p.Text("⌫", bounds.MidX, bounds.MidY + p.U(8), KeyFont, color, bounds.Width, SKTextAlign.Center);
                break;
            case ImeKeyKind.CursorLeft:
                p.Text("←", bounds.MidX, bounds.MidY + p.U(8), KeyFont, color, bounds.Width, SKTextAlign.Center);
                break;
            case ImeKeyKind.CursorRight:
                p.Text("→", bounds.MidX, bounds.MidY + p.U(8), KeyFont, color, bounds.Width, SKTextAlign.Center);
                break;
        }

        DrawHint(p, view, key, bounds, palette.Text);
    }

    private static void DrawHint(OverlayPainter p, ImeKeyboardView view, ImeKey key, SKRect bounds, SKColor ink)
    {
        if (view.Scheme == OverlayInputScheme.Keyboard)
        {
            return;
        }

        OverlayButton? button = key.Kind switch
        {
            ImeKeyKind.Shift => OverlayButton.L2,
            ImeKeyKind.Page => OverlayButton.L3,
            ImeKeyKind.Space => OverlayButton.Triangle,
            ImeKeyKind.Backspace => OverlayButton.Square,
            ImeKeyKind.CursorLeft => OverlayButton.L1,
            ImeKeyKind.CursorRight => OverlayButton.R1,
            ImeKeyKind.Done => OverlayButton.R2,
            _ => null,
        };
        if (button is not { } shortcut)
        {
            return;
        }

        var prompt = new OverlayPrompt(shortcut, string.Empty, string.Empty);
        var width = p.PromptGlyphWidth(prompt, view.Scheme, HintScale);
        var middle = bounds.Top + p.U(15);
        if (width > bounds.Width * 0.6f)
        {
            return;
        }

        p.PromptGlyph(prompt, view.Scheme, bounds.Right - width - p.U(6), middle, HintScale);
    }

    private static void DrawFooter(OverlayPainter p, ImeKeyboardView view, float left, float middle, float right)
    {
        List<OverlayPrompt> prompts;
        if (view.Scheme == OverlayInputScheme.Keyboard)
        {
            prompts =
            [
                new(OverlayButton.R2, DoneLabel(view.EnterLabel), "Enter"),
                new(OverlayButton.Cancel, Text("Close"), "Esc"),
                new(OverlayButton.Square, Text("Delete"), "Backspace"),
                new(OverlayButton.L1, Text("Cursor"), "← →"),
            ];
            p.Text(Text("Type"), left, middle + p.U(6), OverlayStyle.Label, p.Palette.Muted,
                right - left - p.PromptsWidth(prompts, view.Scheme) - p.U(24));
        }
        else
        {
            prompts =
            [
                new(OverlayButton.Confirm, Text("Select"), "Enter"),
                new(OverlayButton.Cancel, Text("Close"), "Esc"),
            ];
        }

        var width = p.PromptsWidth(prompts, view.Scheme);
        p.Prompts(prompts, view.Scheme, right - width, middle);
    }

    private static string DoneLabel(ImeEnterLabel label) => label switch
    {
        ImeEnterLabel.Send => Text("Send"),
        ImeEnterLabel.Search => Text("Search"),
        ImeEnterLabel.Go => Text("Go"),
        _ => Text("Done"),
    };

    private static bool IsNumeric(string text) =>
        text.Length == 1 && (char.IsDigit(text[0]) || text[0] is '.' or ',' or '-' or '+');
}
