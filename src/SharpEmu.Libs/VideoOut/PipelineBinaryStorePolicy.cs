// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.Libs.VideoOut;

// SHARPEMU_PIPELINE_BINARY_MS=<milliseconds> (default 20): the build time from which a pipeline's binaries are stored
// (VK_KHR_pipeline_binary). While binaries are on, every pipeline captures its data and is created without a pipeline
// cache, and the NVIDIA driver adds none of these pipelines to its own disk cache (its cache file is not written during
// a session). A build under the threshold is therefore kept nowhere and compiles again in each session, so a high
// threshold leaves the render thread waiting for many pipelines of tens to hundreds of milliseconds each time; stored,
// they load from their binaries. Builds under 20 ms are mostly links of parts already built and cost no more than a
// load.
internal static class PipelineBinaryStorePolicy
{
    internal const double DefaultMilliseconds = 20;

    internal static readonly double Milliseconds = Parse(Environment.GetEnvironmentVariable("SHARPEMU_PIPELINE_BINARY_MS"));

    // The threshold a setting names: a finite number of milliseconds, zero or more. Anything else keeps the default.
    internal static double Parse(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) &&
        double.IsFinite(milliseconds) && milliseconds >= 0
            ? milliseconds
            : DefaultMilliseconds;
}
