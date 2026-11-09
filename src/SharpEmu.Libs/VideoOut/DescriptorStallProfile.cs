// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

internal static class DescriptorStallProfile
{
    internal enum Operation { Prepare, ResolveImages, ResolveSamplers, FindAddressBuffers, FindBuffers, ValidateImages, Bind, BindBuffers, ObtainAddressRanges, BindImages, Count }
    private static readonly long[] Ticks = new long[(int)Operation.Count];
    private static readonly long[] Calls = new long[(int)Operation.Count];

    internal static void WriteReport(TextWriter? output = null)
    {
        if (!RenderPhaseProfile.FrameTraceEnabled) return;
        var parts = new string[(int)Operation.Count];
        for (var index = 0; index < parts.Length; index++)
            parts[index] = FormattableString.Invariant($"{(Operation)index}={Interlocked.Read(ref Ticks[index]) * 1000.0 / Stopwatch.Frequency:F3}ms/n{Interlocked.Read(ref Calls[index])}");
        (output ?? Console.Error).WriteLine($"[PERF][DESCRIPTOR_DETAIL] cumulative=1 inclusive=1 {string.Join(" ", parts)}");
    }

    internal static Scope Measure(Operation operation, ulong hash, int buffers, int images, int samplers) =>
        RenderPhaseProfile.Enabled && RenderPhaseProfile.FrameTraceEnabled
            ? new Scope(operation, hash, buffers, images, samplers) : default;

    internal readonly struct Scope : IDisposable
    {
        private readonly Operation _operation;
        private readonly ulong _hash;
        private readonly int _buffers, _images, _samplers, _generationZero, _generationOne, _generationTwo;
        private readonly long _started, _allocated;

        internal Scope(Operation operation, ulong hash, int buffers, int images, int samplers)
        {
            _operation = operation;
            _hash = hash;
            _buffers = buffers;
            _images = images;
            _samplers = samplers;
            _allocated = GC.GetAllocatedBytesForCurrentThread();
            _generationZero = GC.CollectionCount(0);
            _generationOne = GC.CollectionCount(1);
            _generationTwo = GC.CollectionCount(2);
            _started = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_started == 0) return;
            var ended = Stopwatch.GetTimestamp();
            Interlocked.Add(ref Ticks[(int)_operation], ended - _started);
            Interlocked.Increment(ref Calls[(int)_operation]);
            var milliseconds = (ended - _started) * 1000.0 / Stopwatch.Frequency;
            if (milliseconds < 10) return;
            // Collection counts are process-wide. They do not establish the cause of a stall.
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[PERF][DESCRIPTOR_STALL] operation={_operation} hash=0x{_hash:X16} start={_started} end={ended} inclusive_ms={milliseconds:F3} buffers={_buffers} images={_images} samplers={_samplers} allocated_bytes={GC.GetAllocatedBytesForCurrentThread() - _allocated} gc0={GC.CollectionCount(0) - _generationZero} gc1={GC.CollectionCount(1) - _generationOne} gc2={GC.CollectionCount(2) - _generationTwo}"));
        }
    }
}
