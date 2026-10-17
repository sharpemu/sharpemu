// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

internal enum NativeStateKind
{
    Pipeline,
    PushConstants,
    Descriptors,
    VertexBuffers,
    IndexBuffer,
    Count,
}

internal static class GpuSyncProfile
{
    private static readonly long[] _nativeStateRecorded = new long[(int)NativeStateKind.Count];
    private static readonly long[] _nativeStateReused = new long[(int)NativeStateKind.Count];
    private static long _barriers;
    private static long _drainingBarriers;
    private static long _memoryBarriers;
    private static long _bufferBarriers;
    private static long _imageBarriers;
    private static long _mergedRequests;
    private static long _renderingScopes;
    private static long _batchFlushes;

    public static bool Enabled => VideoOut.RenderPhaseProfile.Enabled;

    public static void CountBarrier(PipelineStageFlags source, PipelineStageFlags destination, uint memory, uint buffer, uint image)
    {
        Interlocked.Increment(ref _barriers);
        if ((source & PipelineStageFlags.AllCommandsBit) != 0 && (destination & (PipelineStageFlags.AllCommandsBit | PipelineStageFlags.TopOfPipeBit | PipelineStageFlags.DrawIndirectBit)) != 0)
        {
            Interlocked.Increment(ref _drainingBarriers);
        }

        Interlocked.Add(ref _memoryBarriers, memory);
        Interlocked.Add(ref _bufferBarriers, buffer);
        Interlocked.Add(ref _imageBarriers, image);
    }

    public static void CountMergedRequest() => Interlocked.Increment(ref _mergedRequests);

    public static void CountRenderingScope() => Interlocked.Increment(ref _renderingScopes);

    public static void CountBatchFlush() => Interlocked.Increment(ref _batchFlushes);

    public static void CountNativeState(NativeStateKind kind, bool reused)
    {
        if (reused)
        {
            Interlocked.Increment(ref _nativeStateReused[(int)kind]);
        }
        else
        {
            Interlocked.Increment(ref _nativeStateRecorded[(int)kind]);
        }
    }

    private static string TakeNativeStateReport()
    {
        var parts = new string[(int)NativeStateKind.Count];
        for (var index = 0; index < parts.Length; index++)
        {
            var reused = Interlocked.Exchange(ref _nativeStateReused[index], 0);
            var recorded = Interlocked.Exchange(ref _nativeStateRecorded[index], 0);
            parts[index] = FormattableString.Invariant($"{(NativeStateKind)index}={reused}/{reused + recorded}");
        }

        return string.Join(" ", parts);
    }

    public static string TakeReport() => FormattableString.Invariant(
        $"[PERF][GPU_SYNC] barriers={Interlocked.Exchange(ref _barriers, 0)} draining={Interlocked.Exchange(ref _drainingBarriers, 0)} memory={Interlocked.Exchange(ref _memoryBarriers, 0)} buffer={Interlocked.Exchange(ref _bufferBarriers, 0)} image={Interlocked.Exchange(ref _imageBarriers, 0)} merged_requests={Interlocked.Exchange(ref _mergedRequests, 0)} rendering_scopes={Interlocked.Exchange(ref _renderingScopes, 0)} draw_count_flushes={Interlocked.Exchange(ref _batchFlushes, 0)} reused {TakeNativeStateReport()}");
}
