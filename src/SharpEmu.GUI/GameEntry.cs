// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

public sealed record GameEntry(string Name, string? TitleId, string Path, long SizeBytes)
{
    public string Detail => TitleId is not null
        ? $"{TitleId}  •  {FormatSize(SizeBytes)}"
        : $"{FormatSize(SizeBytes)}  •  {Path}";

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GiB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MiB",
            >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KiB",
            _ => $"{bytes} B",
        };
    }
}
