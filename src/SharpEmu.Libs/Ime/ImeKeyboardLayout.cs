// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.Libs.Ime;

internal enum ImeKeyKind
{
    Character,
    Shift,
    Page,
    Space,
    Backspace,
    CursorLeft,
    CursorRight,
    Done,
}

internal enum ImePage
{
    Letters,
    Symbols,
    Accents,
}

internal sealed record ImeKey(ImeKeyKind Kind, string Lower, string Upper, float Width);

internal static class ImeKeyboardLayout
{
    internal const float Columns = 12;

    private const string AccentsRow0 = "àáâäåæçćčèéê";
    private const string AccentsRow1 = "ëęəìíîïığłñń";
    private const string AccentsRow2 = "òóôöõőøœśşšß";
    private const string AccentsRow3 = "ùúûüűiźżž";

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    internal static IReadOnlyList<IReadOnlyList<ImeKey>> Rows(ImePage page, string? language)
    {
        var rows = new List<IReadOnlyList<ImeKey>>(5);
        switch (page)
        {
            case ImePage.Symbols:
                rows.Add(Characters("1234567890", CultureInfo.InvariantCulture));
                rows.Add(Characters("!@#$%^&*()-_", CultureInfo.InvariantCulture));
                rows.Add(Characters("=+/\\:;'\"?~`|", CultureInfo.InvariantCulture));
                rows.Add(ShiftRow("<>[]{}€£¥", CultureInfo.InvariantCulture));
                break;
            case ImePage.Accents:
                rows.Add(Characters(AccentsRow0, Turkish));
                rows.Add(Characters(AccentsRow1, Turkish));
                rows.Add(Characters(AccentsRow2, Turkish));
                rows.Add(ShiftRow(AccentsRow3, Turkish));
                break;
            default:
                var (top, middle, bottom, culture) = Letters(language);
                rows.Add(Characters("1234567890", CultureInfo.InvariantCulture));
                rows.Add(Characters(top, culture));
                rows.Add(Characters(middle, culture));
                rows.Add(ShiftRow(bottom, culture));
                break;
        }

        rows.Add(
        [
            new ImeKey(ImeKeyKind.Page, string.Empty, string.Empty, 1.5f),
            Character(",", CultureInfo.InvariantCulture),
            new ImeKey(ImeKeyKind.Space, " ", " ", 4),
            Character(".", CultureInfo.InvariantCulture),
            new ImeKey(ImeKeyKind.CursorLeft, string.Empty, string.Empty, 1),
            new ImeKey(ImeKeyKind.CursorRight, string.Empty, string.Empty, 1),
            new ImeKey(ImeKeyKind.Done, string.Empty, string.Empty, 2.5f),
        ]);
        return rows;
    }

    internal static float RowWidth(IReadOnlyList<ImeKey> row)
    {
        var width = 0f;
        foreach (var key in row)
        {
            width += key.Width;
        }

        return width;
    }

    private static (string Top, string Middle, string Bottom, CultureInfo Culture) Letters(string? language) =>
        language?.Trim().ToLowerInvariant() switch
        {
            "tr" or "az" => ("qwertyuıopğü", "asdfghjklşi", "zxcvbnmöç", Turkish),
            "de" => ("qwertzuiopü", "asdfghjklöä", "yxcvbnmß-", CultureInfo.InvariantCulture),
            _ => ("qwertyuiop", "asdfghjkl", "zxcvbnm-'", CultureInfo.InvariantCulture),
        };

    private static List<ImeKey> Characters(string characters, CultureInfo culture)
    {
        var keys = new List<ImeKey>(characters.Length);
        foreach (var rune in characters.EnumerateRunes())
        {
            keys.Add(Character(rune.ToString(), culture));
        }

        return keys;
    }

    private static List<ImeKey> ShiftRow(string characters, CultureInfo culture)
    {
        var keys = new List<ImeKey> { new(ImeKeyKind.Shift, string.Empty, string.Empty, 1.5f) };
        keys.AddRange(Characters(characters, culture));
        keys.Add(new ImeKey(ImeKeyKind.Backspace, string.Empty, string.Empty, 1.5f));
        return keys;
    }

    private static ImeKey Character(string lower, CultureInfo culture) =>
        new(ImeKeyKind.Character, lower, Upper(lower, culture), 1);

    private static string Upper(string lower, CultureInfo culture) =>
        lower == "ß" ? "ẞ" : lower.ToUpper(culture);
}
