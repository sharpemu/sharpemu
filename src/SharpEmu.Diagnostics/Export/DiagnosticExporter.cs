// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text;

namespace SharpEmu.Diagnostics.Export;

/// <summary>
/// Export layer — converts plugin data to files. Plugins return data
/// from Shutdown(); the exporter writes it to disk in the requested format.
/// This separation means a plugin never does IO directly.
/// </summary>
public static class DiagnosticExporter
{
    /// <summary>Export all plugin data as JSON files.</summary>
    public static void ExportJson(string sessionDir, Dictionary<string, object?> pluginData)
    {
        foreach (var (name, data) in pluginData)
        {
            if (data is null) continue;
            var path = Path.Combine(sessionDir, $"{ToFileName(name)}.json");
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }

    /// <summary>Export all plugin data as text files.</summary>
    public static void ExportText(string sessionDir, Dictionary<string, object?> pluginData)
    {
        foreach (var (name, data) in pluginData)
        {
            if (data is null) continue;
            var path = Path.Combine(sessionDir, $"{ToFileName(name)}.txt");
            var text = FormatAsText(data);
            File.WriteAllText(path, text);
        }
    }

    /// <summary>Export a combined markdown report.</summary>
    public static void ExportMarkdown(string sessionDir, string gameId, Dictionary<string, object?> pluginData)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# SharpEmu Diagnostic Report — {gameId}");
        sb.AppendLine($"Generated: {DateTime.UtcNow:o}");
        sb.AppendLine();
        foreach (var (name, data) in pluginData)
        {
            if (data is null) continue;
            sb.AppendLine($"## {name}");
            sb.AppendLine("```");
            sb.AppendLine(FormatAsText(data));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        File.WriteAllText(Path.Combine(sessionDir, "diagnostic_report.md"), sb.ToString());
    }

    private static string FormatAsText(object data)
    {
        if (data is string s) return s;
        if (data is IFormattable f) return f.ToString(null, null);
        return data.ToString() ?? "";
    }

    private static string ToFileName(string name) =>
        name.Replace(" ", "_").ToLowerInvariant();
}
