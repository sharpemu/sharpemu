// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Rendering;

// Counts how tessellated draws are split into hull dispatches and domain draws; part of the
// render profile. Each batch prepares and binds every stage again, so batches per draw is
// the multiplier on the tessellation cost.
internal static class TessellationProfile
{
    private static long _draws;
    private static long _instances;
    private static long _patches;
    private static long _batches;
    private static long _singleGroupBatches;
    private static long _maxBatchesPerDraw;

    public static void RecordDraw(uint instances, uint patches, long batches, bool oneGroupPerBatch)
    {
        _draws++;
        _instances += instances;
        _patches += (long)patches * instances;
        _batches += batches;
        if (oneGroupPerBatch)
        {
            _singleGroupBatches += batches;
        }

        _maxBatchesPerDraw = Math.Max(_maxBatchesPerDraw, batches);
    }

    public static string? TakeReport()
    {
        if (_draws == 0)
        {
            return null;
        }

        var report = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[PERF][TESSELLATION] draws={_draws} instances={_instances} patches={_patches} batches={_batches} batches_per_draw={(double)_batches / _draws:F1} max_batches_per_draw={_maxBatchesPerDraw} single_group_batches={_singleGroupBatches} offchip_buffers={TessellationOffchip.Configuration.Buffers}");
        _draws = _instances = _patches = _batches = _singleGroupBatches = _maxBatchesPerDraw = 0;
        return report;
    }
}
