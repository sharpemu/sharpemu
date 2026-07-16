// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Text;

namespace SharpEmu.ShaderCompiler.Metal;

/// <summary>
/// Loads the static MSL blocks from embedded Templates/*.msl resources and
/// substitutes {{placeholder}} tokens. The static prelude and fixed shaders
/// are authored as real Metal source files; only the per-instruction body
/// emission stays programmatic in the translator.
/// </summary>
internal static class MslTemplates
{
    private static readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public static string Render(string name, params (string Key, string Value)[] substitutions)
    {
        var template = _cache.GetOrAdd(name, Load);
        if (substitutions.Length == 0)
        {
            return template;
        }

        var builder = new StringBuilder(template);
        foreach (var (key, value) in substitutions)
        {
            builder.Replace("{{" + key + "}}", value);
        }

        var rendered = builder.ToString();
        var marker = rendered.IndexOf("{{", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var end = rendered.IndexOf("}}", marker, StringComparison.Ordinal);
            var token = end > marker ? rendered[marker..(end + 2)] : "{{...";
            throw new InvalidOperationException(
                $"template '{name}' has an unsubstituted placeholder {token}");
        }

        return rendered;
    }

    private static string Load(string name)
    {
        var assembly = typeof(MslTemplates).Assembly;
        var resourceName = $"SharpEmu.ShaderCompiler.Metal.Templates.{name}.msl";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"missing embedded MSL template {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
