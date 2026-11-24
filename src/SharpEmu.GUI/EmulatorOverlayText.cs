// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.Libs.Overlay;

namespace SharpEmu.GUI;

internal static class EmulatorOverlayText
{
    public static string Language(GuiSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Language) ? "en" : settings.Language.Trim();

    public static string Strings(GuiSettings settings)
    {
        var localization = new Localization(Localization.LanguagesDirectory);
        localization.Load(Language(settings));
        var strings = new Dictionary<string, string>(localization.WithPrefix(OverlayText.KeyPrefix), StringComparer.Ordinal);
        return JsonSerializer.Serialize(strings, EmulatorOverlayTextContext.Default.DictionaryStringString);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class EmulatorOverlayTextContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
