// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// SHARPEMU_PROFILE_DESCRIPTOR_COST=1: calls, time and allocated bytes of each step of binding a draw's resources
/// (prepare, buffers, device-address ranges, images, the commit and its driver calls), plus descriptor counts per
/// commit. One `[PERF][DESCRIPTOR_COST]` line every 5 s. Measurement only: no behaviour change.
/// </summary>
internal static class DescriptorCostProfile
{
    internal enum Op { Prepare, BindResources, BindBuffers, ObtainAddressRanges, BindImages, Commit, CommitDriver, Count }

    internal static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_DESCRIPTOR_COST"), "1", StringComparison.Ordinal);

    private static readonly long[] Ticks = new long[(int)Op.Count];
    private static readonly long[] Calls = new long[(int)Op.Count];
    private static readonly long[] Allocated = new long[(int)Op.Count];
    private static long _writes, _bufferInfos, _imageInfos, _pushSets, _heapSets, _shadowed, _commits;
    private static long _windowStart = Stopwatch.GetTimestamp();
    private static int _reporting;

    internal static Scope Measure(Op op) => Enabled ? new Scope(op) : default;

    internal static void NoteCommit(int writes, int bufferInfos, int imageInfos, bool push, bool shadowed)
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Increment(ref _commits);
        Interlocked.Add(ref _writes, writes);
        Interlocked.Add(ref _bufferInfos, bufferInfos);
        Interlocked.Add(ref _imageInfos, imageInfos);
        if (shadowed) Interlocked.Increment(ref _shadowed);
        else if (writes != 0) Interlocked.Increment(ref push ? ref _pushSets : ref _heapSets);
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly Op _op;
        private readonly long _started;
        private readonly long _allocated;

        internal Scope(Op op)
        {
            _op = op;
            _allocated = GC.GetAllocatedBytesForCurrentThread();
            _started = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_started == 0)
            {
                return;
            }

            var ended = Stopwatch.GetTimestamp();
            Interlocked.Add(ref Ticks[(int)_op], ended - _started);
            Interlocked.Increment(ref Calls[(int)_op]);
            Interlocked.Add(ref Allocated[(int)_op], GC.GetAllocatedBytesForCurrentThread() - _allocated);
            MaybeReport(ended);
        }
    }

    private static void MaybeReport(long now)
    {
        var started = Interlocked.Read(ref _windowStart);
        if (now - started < 5 * Stopwatch.Frequency || Interlocked.Exchange(ref _reporting, 1) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _windowStart, now);
            var parts = new string[(int)Op.Count];
            for (var index = 0; index < parts.Length; index++)
            {
                var ticks = Interlocked.Exchange(ref Ticks[index], 0);
                var calls = Interlocked.Exchange(ref Calls[index], 0);
                var allocated = Interlocked.Exchange(ref Allocated[index], 0);
                parts[index] = FormattableString.Invariant(
                    $"{(Op)index}={ticks * 1000.0 / Stopwatch.Frequency:F0}ms/n{calls}/{allocated / 1024}KB");
            }

            var commits = Interlocked.Exchange(ref _commits, 0);
            var writes = Interlocked.Exchange(ref _writes, 0);
            var bufferInfos = Interlocked.Exchange(ref _bufferInfos, 0);
            var imageInfos = Interlocked.Exchange(ref _imageInfos, 0);
            var pushSets = Interlocked.Exchange(ref _pushSets, 0);
            var heapSets = Interlocked.Exchange(ref _heapSets, 0);
            var shadowed = Interlocked.Exchange(ref _shadowed, 0);
            var seconds = (now - started) / (double)Stopwatch.Frequency;
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[PERF][DESCRIPTOR_COST] window={seconds:F1}s {string.Join(" ", parts)} commits={commits} writes={writes} buffer_infos={bufferInfos} image_infos={imageInfos} push_sets={pushSets} heap_sets={heapSets} shadowed={shadowed}"));
        }
        finally
        {
            Volatile.Write(ref _reporting, 0);
        }
    }
}
