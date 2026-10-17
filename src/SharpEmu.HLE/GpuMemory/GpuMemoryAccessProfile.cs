// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE.GpuMemory;

public static class GpuMemoryAccessProfile
{
    public enum Operation
    {
        WriteFault,
        ReadFault,
        UnknownFault,
        HostProtection,
        FaultHostProtection,
        AddressSpaceProtectionWait,
        FaultAddressSpaceProtectionWait,
        BackingProtectionWait,
        FaultBackingProtectionWait,
        HostProtectionCall,
        FaultHostProtectionCall,
        RegionLockWait,
        FaultRegionLockWait,
        PageLockWait,
        FaultPageLockWait,
        UploadTracking,
        TrackerLockWait,
        FaultTrackerLockWait,
        ImageCacheLockWait,
        FaultImageCacheLockWait,
        TrackerLockHold,
        FaultTrackerLockHold,
        ImageCacheLockHold,
        FaultImageCacheLockHold,
        ImageCpuWrite,
        FaultImageCpuWrite,
        ImageRegionQuery,
        ImageGpuDirtyQuery,
        Count,
    }

    internal readonly record struct Measurement(long Calls, long Ticks, long MaximumTicks, long Bytes);

    internal sealed class Measurements
    {
        private sealed class Counter
        {
            internal long Calls;
            internal long Ticks;
            internal long MaximumTicks;
            internal long Bytes;
        }

        private readonly Counter[] _values = Enumerable.Range(0, (int)Operation.Count).Select(_ => new Counter()).ToArray();

        internal void CountCall(Operation operation) => Interlocked.Increment(ref _values[(int)operation].Calls);

        internal void Record(Operation operation, long ticks, long bytes)
        {
            var counter = _values[(int)operation];
            Interlocked.Add(ref counter.Ticks, ticks);
            Interlocked.Add(ref counter.Bytes, bytes);
            var maximum = Volatile.Read(ref counter.MaximumTicks);
            while (ticks > maximum)
            {
                var observed = Interlocked.CompareExchange(ref counter.MaximumTicks, ticks, maximum);
                if (observed == maximum) break;
                maximum = observed;
            }
            Interlocked.Increment(ref counter.Calls);
        }

        // Cumulative fields can straddle an active call; reports never reset fault-side counters.
        internal Measurement[] Snapshot() => _values.Select(counter => new Measurement(
            Volatile.Read(ref counter.Calls), Volatile.Read(ref counter.Ticks),
            Volatile.Read(ref counter.MaximumTicks), Volatile.Read(ref counter.Bytes))).ToArray();
    }

    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE_FRAME_TRACE") == "1" &&
        (Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1" ||
         Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RENDER") == "1");
    private static readonly Measurements Counters = new();
    [ThreadStatic] private static int _faultDepth;

    // Initialize before protected pages can fault, not from the fault handler.
    internal static void Initialize() => _ = Counters;

    // Prepare thread-local storage before guest code can cause a memory fault.
    internal static void InitializeCurrentThread()
    {
        if (Enabled) Volatile.Write(ref _faultDepth, _faultDepth);
    }

    internal static Measurement[] Snapshot() => Counters.Snapshot();

    internal static Operation GetLockOperation(RegionLock.Category category, bool hold)
    {
        var inFault = _faultDepth != 0;
        return (category, hold, inFault) switch
        {
            (RegionLock.Category.ImageCache, true, true) => Operation.FaultImageCacheLockHold,
            (RegionLock.Category.ImageCache, true, false) => Operation.ImageCacheLockHold,
            (RegionLock.Category.ImageCache, false, true) => Operation.FaultImageCacheLockWait,
            (RegionLock.Category.ImageCache, false, false) => Operation.ImageCacheLockWait,
            (_, true, true) => Operation.FaultTrackerLockHold,
            (_, true, false) => Operation.TrackerLockHold,
            (_, false, true) => Operation.FaultTrackerLockWait,
            _ => Operation.TrackerLockWait,
        };
    }

    internal static void RecordLockWait(RegionLock.Category category, long ticks)
    {
        Counters.Record(GetLockOperation(category, hold: false), ticks, 0);
        Counters.Record(_faultDepth == 0 ? Operation.RegionLockWait : Operation.FaultRegionLockWait, ticks, 0);
    }

    internal static void RecordLockHold(Operation operation, long ticks) => Counters.Record(operation, ticks, 0);

    public static void CountImageCpuWrite()
    {
        if (Enabled) Counters.CountCall(_faultDepth == 0 ? Operation.ImageCpuWrite : Operation.FaultImageCpuWrite);
    }

    public static void CountImageQuery(bool gpuDirtyOnly)
    {
        if (Enabled) Counters.CountCall(gpuDirtyOnly ? Operation.ImageGpuDirtyQuery : Operation.ImageRegionQuery);
    }

    public readonly ref struct Scope
    {
        private readonly Operation _operation;
        private readonly long _started;
        private readonly long _bytes;
        private readonly bool _active;
        private readonly bool _fault;

        internal Scope(Operation operation, ulong bytes, bool fault)
        {
            _operation = operation;
            _bytes = checked((long)bytes);
            _active = true;
            _fault = fault;
            if (fault) _faultDepth++;
            _started = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (!_active) return;
            var elapsed = Stopwatch.GetTimestamp() - _started;
            if (_fault) _faultDepth--;
            Counters.Record(_operation, elapsed, _bytes);
        }
    }

    internal static Scope Measure(Operation operation, ulong bytes = 0) =>
        Enabled ? new Scope(operation, bytes, false) : default;

    public static Scope MeasureAddressSpaceProtectionWait() =>
        MeasureAccess(Operation.AddressSpaceProtectionWait, Operation.FaultAddressSpaceProtectionWait);

    internal static Scope MeasureBackingProtectionWait() =>
        MeasureAccess(Operation.BackingProtectionWait, Operation.FaultBackingProtectionWait);

    public static Scope MeasureHostProtectionCall(ulong bytes) =>
        MeasureAccess(Operation.HostProtectionCall, Operation.FaultHostProtectionCall, bytes);

    internal static Scope MeasureAccess(Operation regular, Operation fault, ulong bytes = 0) =>
        Enabled ? new Scope(_faultDepth == 0 ? regular : fault, bytes, false) : default;

    internal static Scope MeasureFault(FaultKind kind) => Enabled
        ? new Scope(kind switch
        {
            FaultKind.Write => Operation.WriteFault,
            FaultKind.Read => Operation.ReadFault,
            _ => Operation.UnknownFault,
        }, 0, true)
        : default;

    // Nested scopes overlap; these are cumulative inclusive times across all threads.
    public static void WriteReport()
    {
        if (!Enabled) return;
        var snapshot = Counters.Snapshot();
        for (var index = 0; index < snapshot.Length; index++)
        {
            var value = snapshot[index];
            if (value.Calls == 0) continue;
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[PERF][GPU_MEMORY_ACCESS] operation={(Operation)index} cumulative=1 calls={value.Calls} inclusive_ms={value.Ticks * 1000.0 / Stopwatch.Frequency:F3} max_ms={value.MaximumTicks * 1000.0 / Stopwatch.Frequency:F3} bytes={value.Bytes}"));
        }
    }
}
