// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.HLE.FileSystem;

namespace SharpEmu.HLE.Host.Windows;

/// <summary>
/// The title id of the game the launcher is about to start, read the way the runtime reads it (sce_sys/param.json,
/// key <c>titleId</c>), so the folders the plan grants are the ones the runtime keys by that id: save data, pipeline
/// cache and game logs.
/// </summary>
public static class SandboxTitleId
{
    private const int MaxParamBytes = 1 << 20;

    /// <summary>
    /// The title id of an archive or of a game folder, or null when there is none or it cannot be used as a folder
    /// name. A game with only a param.sfo has no title id for the runtime either, so it reads as unknown here too.
    /// </summary>
    public static string? Read(string? ebootPath, string? app0Root)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(ebootPath) && ZArchive.IsArchivePath(System.IO.Path.GetFullPath(ebootPath)))
            {
                return FromJson(ReadArchiveParam(System.IO.Path.GetFullPath(ebootPath)));
            }

            var directories = new List<string>();
            if (!string.IsNullOrWhiteSpace(app0Root))
            {
                directories.Add(app0Root);
            }

            if (!string.IsNullOrWhiteSpace(ebootPath) && System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(ebootPath)) is { Length: > 0 } game)
            {
                directories.Add(game);
            }

            foreach (var directory in directories)
            {
                foreach (var path in new[]
                {
                    System.IO.Path.Combine(directory, "sce_sys", "param.json"),
                    System.IO.Path.Combine(directory, "param.json"),
                })
                {
                    if (File.Exists(path) && new FileInfo(path).Length <= MaxParamBytes &&
                        FromJson(File.ReadAllBytes(path)) is { } titleId)
                    {
                        return titleId;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return null;
    }

    /// <summary>The <c>titleId</c> of a param.json, or null when missing, empty or not a usable folder name.</summary>
    public static string? FromJson(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            return null;
        }

        try
        {
            ReadOnlyMemory<byte> json = data;
            if (json.Span.StartsWith("﻿"u8))
            {
                json = json[3..];
            }

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("titleId", out var element) ||
                element.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var titleId = element.GetString()?.Trim();
            // "." and ".." would name the folder above; an all-dot name is never a title id.
            return string.IsNullOrEmpty(titleId) || titleId.All(character => character == '.') ? null : titleId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The folder name SaveDataStorage gives a title (invalid file name characters replaced, trimmed).</summary>
    public static string SaveDataSegment(string titleId)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var text = new string([.. titleId.Trim().Select(character => Array.IndexOf(invalid, character) >= 0 ? '_' : character)]).Trim();
        return string.IsNullOrWhiteSpace(text) ? "default" : text;
    }

    /// <summary>The folder name of the pipeline cache and game logs: upper case letters, digits, '-' and '_'.</summary>
    public static string CacheSegment(string titleId) =>
        new([.. titleId.Trim().Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? char.ToUpperInvariant(character) : '_')]);

    private static byte[]? ReadArchiveParam(string archivePath)
    {
        using var archive = ZArchive.Open(archivePath);
        if (archive is null)
        {
            return null;
        }

        foreach (var name in new[] { "sce_sys/param.json", "param.json" })
        {
            var node = archive.LookUp(name);
            if (archive.IsFile(node) && archive.GetFileSize(node) is > 0 and <= MaxParamBytes)
            {
                var data = new byte[archive.GetFileSize(node)];
                return archive.Read(node, 0, data) == data.Length ? data : null;
            }
        }

        return null;
    }
}
