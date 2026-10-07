// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Rendering;

// The draw and dispatch trace channel. Every caller tests Enabled before it forms a line.
public static class RenderTrace
{
    public static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"),
        "1",
        StringComparison.Ordinal);

    private static int _geometryStageSkips;
    private static int _framebufferSkips;
    private static int _scissorDefaults;
    private static int _clipRuleWarnings;
    private static int _lineWidthClamps;
    private static int _unknownInitiators;
    private static int _threadDimensionConversions;
    private static int _zeroDispatches;
    private static int _imageClears;
    private static int _metadataClears;
    private static int _nullComputeShaders;
    private static int _pipelineLines;
    private static long _sequence;

    public static long NextSequence() => Interlocked.Increment(ref _sequence);

    public static void Write(string line) => Console.Error.WriteLine($"[RENDER][TRACE] {line}");

    // Each limited line class logs at most the given number of times.
    public static bool GeometryStageSkip() => Interlocked.Increment(ref _geometryStageSkips) <= 32;

    public static bool FramebufferSkip() => Interlocked.Increment(ref _framebufferSkips) <= 128;

    public static bool ScissorDefault() => Interlocked.Increment(ref _scissorDefaults) <= 32;

    public static bool ClipRuleWarning() => Interlocked.Increment(ref _clipRuleWarnings) <= 32;

    public static bool LineWidthClamp() => Interlocked.Increment(ref _lineWidthClamps) <= 1;

    public static bool UnknownInitiator() => Interlocked.Increment(ref _unknownInitiators) <= 32;

    public static bool ThreadDimensionConversion() => Interlocked.Increment(ref _threadDimensionConversions) <= 32;

    public static bool ZeroDispatch() => Interlocked.Increment(ref _zeroDispatches) <= 32;

    public static bool ImageClear() => Interlocked.Increment(ref _imageClears) <= 32;

    public static bool MetadataClear() => Interlocked.Increment(ref _metadataClears) <= 32;

    public static bool NullComputeShader() => Interlocked.Increment(ref _nullComputeShaders) <= 32;

    // The shader and pipeline cache lines: lookups, hits, compiles, bindings and commits.
    public static bool Pipeline() => Interlocked.Increment(ref _pipelineLines) <= 4096;
}
