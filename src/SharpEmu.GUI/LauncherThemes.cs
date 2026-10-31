// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using Avalonia.Media;

namespace SharpEmu.GUI;

/// <summary>
/// One launcher colour theme. Every field maps onto a brush in Themes/Tokens.axaml.
/// </summary>
internal sealed record LauncherPalette(
    string Id,
    string Background,
    string Chrome,
    string Menu,
    string Card,
    string Elevated,
    string RaisedHover,
    string Field,
    string FieldHover,
    string Overlay,
    string Text,
    string SecondaryText,
    string Description,
    string Muted,
    string Faint,
    string Accent,
    string AccentHover,
    string AccentPressed,
    string AccentTint,
    string AccentForeground);

/// <summary>
/// Everything the user picks about the launcher's look: the theme, the custom theme's
/// two colours, glass opacity (percent) and contrast (-50 to +50).
/// </summary>
internal sealed record LauncherAppearance(
    string? Theme,
    string? CustomBackground = null,
    string? CustomAccent = null,
    int GlassOpacity = LauncherThemes.DefaultGlassOpacity,
    int Contrast = 0);

/// <summary>
/// The launcher themes. Applying one repaints the brush instances declared in
/// Themes/Tokens.axaml in place, so every StaticResource user follows the change
/// without a restart.
/// </summary>
internal static class LauncherThemes
{
    public const string DefaultId = "Graphite";
    public const string CustomId = "Custom";
    public const string DefaultCustomBackground = "#121212";
    public const string DefaultCustomAccent = "#2E7CF6";
    public const int DefaultGlassOpacity = 75;
    public const int MinGlassOpacity = 30;
    public const int MaxContrast = 50;

    // Text, rims and focus rings are drawn light, so a custom background is kept dark.
    private const byte MaxCustomBackgroundChannel = 0x40;

    // The built-in themes. The scale-based ones use Radix Colors' dark scales
    // (radix-ui.com/colors), each neutral paired with the accent Radix pairs it with;
    // Catppuccin Mocha, Nord and Tokyo Night use those schemes' published colours.
    public static readonly LauncherPalette[] All =
    [
        // Neutral greys with a white primary action, the PS5 look.
        FromScale(
            "Graphite",
            ["#111111", "#191919", "#222222", "#2A2A2A", "#313131", "#3A3A3A",
             "#484848", "#606060", "#6E6E6E", "#7B7B7B", "#B4B4B4", "#EEEEEE"],
            accent: "#F2F2F2", accentHover: "#FFFFFF", accentForeground: "#111111", tintAlpha: 0x24),
        // Onyx: true black surfaces under the glass. The id keeps its old name so saved settings still match.
        new(
            Id: "Oled",
            Background: "#000000", Chrome: "#000000", Menu: "#0A0A0A",
            Card: "#161616", Elevated: "#1E1E1E", RaisedHover: "#2A2A2A",
            Field: "#262626", FieldHover: "#323232", Overlay: "#1C1C1C",
            Text: "#F5F5F5", SecondaryText: "#CCCCCC", Description: "#B4B4B4", Muted: "#9C9C9C", Faint: "#5E5E5E",
            Accent: "#F2F2F2", AccentHover: "#FFFFFF", AccentPressed: "#D4D4D4", AccentTint: "#24FFFFFF",
            AccentForeground: "#000000"),
        // Radix slate and blue: cool blue-grey surfaces.
        FromScale(
            "Midnight",
            ["#111113", "#18191B", "#212225", "#272A2D", "#2E3135", "#363A3F",
             "#43484E", "#5A6169", "#696E77", "#777B84", "#B0B4BA", "#EDEEF0"],
            accent: "#0090FF", accentHover: "#3B9EFF", accentForeground: "#FFFFFF"),
        // Radix mauve and violet.
        FromScale(
            "Violet",
            ["#121113", "#1A191B", "#232225", "#2B292D", "#323035", "#3C393F",
             "#49474E", "#625F69", "#6F6D78", "#7C7A85", "#B5B2BC", "#EEEEF0"],
            accent: "#6E56CF", accentHover: "#7D66D9", accentForeground: "#FFFFFF"),
        // Radix sage and teal.
        FromScale(
            "Forest",
            ["#101211", "#171918", "#202221", "#272A29", "#2E3130", "#373B39",
             "#444947", "#5B625F", "#63706B", "#717D79", "#ADB5B2", "#ECEEED"],
            accent: "#12A594", accentHover: "#0EB39E", accentForeground: "#FFFFFF"),
        // Radix sand and amber.
        FromScale(
            "Ember",
            ["#111110", "#191918", "#222221", "#2A2A28", "#31312E", "#3B3A37",
             "#494844", "#62605B", "#6F6D66", "#7C7B74", "#B5B3AD", "#EEEEEC"],
            accent: "#FFC53D", accentHover: "#FFD60A", accentForeground: "#111110"),
        // Catppuccin Mocha (catppuccin.com/palette). Its pastel blue read only with dark
        // text, so the accent is a deeper blue that carries a white label.
        new(
            Id: "Catppuccin",
            Background: "#1E1E2E", Chrome: "#11111B", Menu: "#181825",
            Card: "#26263A", Elevated: "#313244", RaisedHover: "#3B3D52",
            Field: "#313244", FieldHover: "#45475A", Overlay: "#2A2B3D",
            Text: "#CDD6F4", SecondaryText: "#BAC2DE", Description: "#A6ADC8", Muted: "#9399B2", Faint: "#6C7086",
            Accent: "#3A72E8", AccentHover: "#4C7FE6", AccentPressed: "#2F62CC", AccentTint: "#333A72E8",
            AccentForeground: "#FFFFFF"),
        // Nord (nordtheme.com): Polar Night surfaces, Snow Storm text, and the deepest Frost
        // blue (nord10) as the accent, so labels on it are white.
        new(
            Id: "Nord",
            Background: "#2E3440", Chrome: "#242933", Menu: "#323846",
            Card: "#3B4252", Elevated: "#434C5E", RaisedHover: "#4C566A",
            Field: "#434C5E", FieldHover: "#4C566A", Overlay: "#3B4252",
            Text: "#ECEFF4", SecondaryText: "#E5E9F0", Description: "#D8DEE9", Muted: "#B1BACB", Faint: "#7B88A1",
            Accent: "#5E81AC", AccentHover: "#6A8DB8", AccentPressed: "#4F6F96", AccentTint: "#335E81AC",
            AccentForeground: "#FFFFFF"),
        // Tokyo Night (github.com/folke/tokyonight.nvim, night style), its blue deepened so
        // labels on the accent are white.
        new(
            Id: "TokyoNight",
            Background: "#1A1B26", Chrome: "#16161E", Menu: "#1F2030",
            Card: "#24283B", Elevated: "#292E42", RaisedHover: "#343A55",
            Field: "#292E42", FieldHover: "#343A55", Overlay: "#24283B",
            Text: "#C0CAF5", SecondaryText: "#A9B1D6", Description: "#9AA5CE", Muted: "#828BB8", Faint: "#565F89",
            Accent: "#4F74D6", AccentHover: "#5A7FD8", AccentPressed: "#4262BC", AccentTint: "#334F74D6",
            AccentForeground: "#FFFFFF"),
    ];

    // Maps a 12-step dark scale (Radix roles: 1-2 backgrounds, 3-5 element fills,
    // 6-8 borders, 9-10 solids, 11-12 text) onto the launcher's surfaces and text tiers.
    private static LauncherPalette FromScale(
        string id,
        string[] scale,
        string accent,
        string accentHover,
        string accentForeground,
        byte tintAlpha = 0x33)
    {
        Color Step(int step) => Color.Parse(scale[step - 1]);
        var ac = Color.Parse(accent);
        return new LauncherPalette(
            Id: id,
            Background: scale[0],
            Chrome: Hex(Mix(Step(1), Colors.Black, 0.35)),
            Menu: scale[1],
            Card: scale[2],
            Elevated: scale[3],
            RaisedHover: scale[4],
            Field: scale[4],
            FieldHover: scale[5],
            Overlay: Hex(Mix(Step(3), Step(4), 0.5)),
            Text: Hex(Mix(Step(12), Colors.White, 0.4)),
            SecondaryText: Hex(Mix(Step(11), Step(12), 0.35)),
            Description: scale[10],
            Muted: Hex(Mix(Step(10), Step(11), 0.5)),
            Faint: scale[7],
            Accent: accent,
            AccentHover: accentHover,
            AccentPressed: Hex(Mix(ac, Colors.Black, 0.15)),
            AccentTint: $"#{tintAlpha:X2}{ac.R:X2}{ac.G:X2}{ac.B:X2}",
            AccentForeground: accentForeground);
    }

    public static LauncherPalette Find(string? id) =>
        Array.Find(All, palette => string.Equals(palette.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static bool IsCustom(string? id) => string.Equals(id, CustomId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses #RRGGBB (or RRGGBB); anything else is rejected.</summary>
    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        var hex = text?.Trim().TrimStart('#');
        return hex is { Length: 6 } &&
            hex.All(Uri.IsHexDigit) &&
            Color.TryParse("#" + hex, out color);
    }

    /// <summary>
    /// Builds a full palette from a background and an accent: surfaces step up from
    /// the background in the same increments as Graphite, text is light, and the
    /// accent's hover, pressed, tint and foreground colours follow from the accent.
    /// </summary>
    public static LauncherPalette Derive(string? background, string? accent)
    {
        if (!TryParseHex(background, out var bg))
        {
            bg = Color.Parse(DefaultCustomBackground);
        }

        if (!TryParseHex(accent, out var ac))
        {
            ac = Color.Parse(DefaultCustomAccent);
        }

        bg = Darken(bg);
        var text = Color.Parse("#F5F5F5");
        return new LauncherPalette(
            Id: CustomId,
            Background: Hex(bg),
            Chrome: Hex(Mix(bg, Colors.Black, 0.4)),
            Menu: Hex(Mix(bg, Colors.White, 0.034)),
            Card: Hex(Mix(bg, Colors.White, 0.068)),
            Elevated: Hex(Mix(bg, Colors.White, 0.101)),
            RaisedHover: Hex(Mix(bg, Colors.White, 0.148)),
            Field: Hex(Mix(bg, Colors.White, 0.135)),
            FieldHover: Hex(Mix(bg, Colors.White, 0.181)),
            Overlay: Hex(Mix(bg, Colors.White, 0.084)),
            Text: Hex(text),
            SecondaryText: Hex(Mix(text, bg, 0.2)),
            Description: Hex(Mix(text, bg, 0.33)),
            Muted: Hex(Mix(text, bg, 0.4)),
            Faint: Hex(Mix(text, bg, 0.62)),
            Accent: Hex(ac),
            AccentHover: Hex(Mix(ac, Colors.White, 0.15)),
            AccentPressed: Hex(Mix(ac, Colors.Black, 0.15)),
            AccentTint: $"#38{ac.R:X2}{ac.G:X2}{ac.B:X2}",
            AccentForeground: ForegroundOn(ac));
    }

    /// <summary>
    /// Stretches (positive) or softens (negative) a palette: surfaces move away from or
    /// toward the background, and secondary text moves toward or away from primary text.
    /// Zero returns the palette unchanged.
    /// </summary>
    public static LauncherPalette AdjustContrast(LauncherPalette palette, int contrast)
    {
        var amount = Math.Clamp(contrast, -MaxContrast, MaxContrast) / (double)MaxContrast;
        if (amount == 0)
        {
            return palette;
        }

        var bg = Color.Parse(palette.Background);
        var text = Color.Parse(palette.Text);
        string Surface(string hex) => Hex(Extend(bg, Color.Parse(hex), 1 + (0.6 * amount)));
        string Tier(string hex) => Hex(Extend(text, Color.Parse(hex), 1 - (0.5 * amount)));
        return palette with
        {
            Menu = Surface(palette.Menu),
            Card = Surface(palette.Card),
            Elevated = Surface(palette.Elevated),
            RaisedHover = Surface(palette.RaisedHover),
            Field = Surface(palette.Field),
            FieldHover = Surface(palette.FieldHover),
            Overlay = Surface(palette.Overlay),
            SecondaryText = Tier(palette.SecondaryText),
            Description = Tier(palette.Description),
            Muted = Tier(palette.Muted),
            Faint = Tier(palette.Faint),
        };
    }

    // The point at `scale` times the distance from `origin` to `color`, clamped per channel.
    private static Color Extend(Color origin, Color color, double scale)
    {
        static byte Channel(byte from, byte to, double factor) =>
            (byte)Math.Clamp(Math.Round(from + ((to - from) * factor)), 0, 255);

        return Color.FromRgb(
            Channel(origin.R, color.R, scale),
            Channel(origin.G, color.G, scale),
            Channel(origin.B, color.B, scale));
    }

    private static Color Darken(Color color)
    {
        var max = Math.Max(color.R, Math.Max(color.G, color.B));
        if (max <= MaxCustomBackgroundChannel)
        {
            return color;
        }

        var scale = (double)MaxCustomBackgroundChannel / max;
        return Color.FromRgb(
            (byte)Math.Round(color.R * scale),
            (byte)Math.Round(color.G * scale),
            (byte)Math.Round(color.B * scale));
    }

    private static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B);

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    // WCAG relative luminance.
    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    // White label text while it keeps 3:1 (large, bold button text), dark text otherwise:
    // mid-bright accents like green fell under 2.5:1 with white.
    private static string ForegroundOn(Color accent) =>
        1.05 / (Luminance(accent) + 0.05) >= 3 ? "#FFFFFF" : "#111111";

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Resolves a token brush so code-built views share the themed instance.</summary>
    public static IBrush Brush(string key) =>
        Avalonia.Application.Current?.TryGetResource(key, null, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Transparent;

    public static void Apply(LauncherAppearance appearance)
    {
        if (Avalonia.Application.Current is not { } app)
        {
            return;
        }

        var palette = AdjustContrast(
            IsCustom(appearance.Theme)
                ? Derive(appearance.CustomBackground, appearance.CustomAccent)
                : Find(appearance.Theme),
            appearance.Contrast);
        var background = Color.Parse(palette.Background);
        var overlay = Color.Parse(palette.Overlay);
        var field = Color.Parse(palette.Field);
        var fieldHover = Color.Parse(palette.FieldHover);
        var text = Color.Parse(palette.Text);
        var accent = Color.Parse(palette.Accent);

        Paint(app, "BgBrush", background);
        Paint(app, "ChromeBrush", Color.Parse(palette.Chrome));
        Paint(app, "GameOptionsMenuBrush", Color.Parse(palette.Menu));
        Paint(app, "CardBrush", Color.Parse(palette.Card));
        Paint(app, "PaneGlassBrush", WithAlpha(Color.Parse(palette.Card), 0.65));
        Paint(app, "ElevatedBrush", Color.Parse(palette.Elevated));
        Paint(app, "RaisedHoverBrush", Color.Parse(palette.RaisedHover));
        Paint(app, "FieldBrush", field);
        Paint(app, "FieldHoverBrush", fieldHover);
        Paint(app, "OverlayBrush", overlay);
        Paint(app, "ToastBrush", Color.FromArgb(0xF0, overlay.R, overlay.G, overlay.B));
        Paint(app, "TextBrush", text);
        Paint(app, "SecondaryTextBrush", Color.Parse(palette.SecondaryText));
        Paint(app, "SettingsDescriptionBrush", Color.Parse(palette.Description));
        Paint(app, "MutedBrush", Color.Parse(palette.Muted));
        Paint(app, "FaintBrush", Color.Parse(palette.Faint));
        Paint(app, "AccentBrush", accent);
        Paint(app, "AccentHoverBrush", Color.Parse(palette.AccentHover));
        Paint(app, "AccentPressedBrush", Color.Parse(palette.AccentPressed));
        Paint(app, "AccentTintBrush", Color.Parse(palette.AccentTint));
        Paint(app, "AccentForegroundBrush", Color.Parse(palette.AccentForeground));

        Paint(app, "ComboBoxBackground", field);
        Paint(app, "ComboBoxBackgroundUnfocused", field);
        Paint(app, "ComboBoxBackgroundPointerOver", fieldHover);
        Paint(app, "ComboBoxBackgroundPressed", fieldHover);
        Paint(app, "ComboBoxForegroundFocused", text);
        Paint(app, "ComboBoxForegroundFocusedPressed", text);
        Paint(app, "ComboBoxDropDownGlyphForegroundFocused", text);
        Paint(app, "ComboBoxDropDownGlyphForegroundFocusedPressed", text);

        // Overlays, hairlines and the selected segment come from the theme's text colour, so
        // tinted themes get tinted overlays instead of Graphite's neutral white.
        Paint(app, "CardBorderBrush", WithAlpha(text, 0.08));
        Paint(app, "TileHoverBrush", WithAlpha(text, 0.045));
        Paint(app, "TileSelectedBrush", WithAlpha(text, 0.07));
        Paint(app, "HoverOverlayBrush", WithAlpha(text, 0.06));
        Paint(app, "PressedOverlayBrush", WithAlpha(text, 0.1));
        Paint(app, "SegmentActiveBrush", WithAlpha(text, 0.18));

        Tint(app, "ArtScrimBrush", background);
        Tint(app, "TrophyBackdropFadeBrush", Color.Parse(palette.Menu));

        // Glass is the theme's raised colour, mostly opaque by default: tinted like the
        // theme and calm enough over bright key art that labels stay readable.
        var raised = Color.Parse(palette.RaisedHover);
        var opacity = Math.Clamp(appearance.GlassOpacity, MinGlassOpacity, 100) / 100.0;
        Paint(app, "GlassBrush", WithAlpha(raised, opacity));
        Paint(app, "GlassHoverBrush", WithAlpha(fieldHover, Math.Min(1, opacity + 0.1)));
        Paint(app, "GlassPressedBrush", WithAlpha(fieldHover, Math.Min(1, opacity + 0.2)));

        // Focus and selection rings take the accent. A neutral accent (Graphite, Onyx) is
        // drawn a little translucent so the ring reads grey rather than stark white.
        var neutral = Math.Max(accent.R, Math.Max(accent.G, accent.B)) - Math.Min(accent.R, Math.Min(accent.G, accent.B)) < 24;
        var ring = WithAlpha(accent, neutral ? 0.72 : 0.95);
        Paint(app, "FocusRingBrush", ring);
        app.Resources["FocusRingShadow"] = new BoxShadows(
            new BoxShadow { Spread = 2, Color = ring },
            [new BoxShadow { Blur = 18, Spread = 2, Color = WithAlpha(accent, neutral ? 0.16 : 0.3) }]);
        app.Resources["FocusRingInsetShadow"] = new BoxShadows(
            new BoxShadow { Spread = 2, Color = ring, IsInset = true });
        app.Resources["SelectedRingShadow"] = new BoxShadows(
            new BoxShadow { Spread = 2, Color = WithAlpha(accent, neutral ? 0.5 : 0.7) });

        app.Resources["SystemAccentColor"] = accent;
    }

    private static void Paint(Avalonia.Application app, string key, Color color)
    {
        if (app.TryGetResource(key, null, out var value) && value is SolidColorBrush brush)
        {
            brush.Color = color;
        }
    }

    // Recolours a fade while keeping each stop's alpha.
    private static void Tint(Avalonia.Application app, string key, Color color)
    {
        if (app.TryGetResource(key, null, out var value) && value is GradientBrush brush)
        {
            foreach (var stop in brush.GradientStops)
            {
                stop.Color = Color.FromArgb(stop.Color.A, color.R, color.G, color.B);
            }
        }
    }
}
