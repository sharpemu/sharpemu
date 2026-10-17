// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.ShaderCompiler.Resources;

public static class ResourceMaterializationProfile
{
    public enum Phase { Total, Snapshot, DeviceAddressRanges, Specialization, OutputAssembly, GuestRead, CleanGuestRead, Count }

    public static readonly bool Enabled = IsEnabled(
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE"),
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE_FRAME_TRACE"));
    private static readonly long[] _elapsedTicks = new long[(int)Phase.Count];
    private static readonly long[] _calls = new long[(int)Phase.Count];
    private static long _allocatedBytes;
    private static long _branchEvaluations;
    private static long _descriptorSources;
    private static long _inactiveSources;

    internal static bool IsEnabled(string? performance, string? frameTrace) => performance == "1" && frameTrace == "1";

    public static Scope Measure(Phase phase) => Enabled ? new Scope(phase) : default;

    public readonly struct Scope : IDisposable
    {
        private readonly Phase _phase;
        private readonly long _started;
        private readonly long _initialAllocatedBytes;

        internal Scope(Phase phase)
        {
            _phase = phase;
            _initialAllocatedBytes = phase == Phase.Total ? GC.GetAllocatedBytesForCurrentThread() : 0;
            _started = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_started == 0) return;
            Interlocked.Add(ref _elapsedTicks[(int)_phase], Stopwatch.GetTimestamp() - _started);
            Interlocked.Increment(ref _calls[(int)_phase]);
            if (_phase == Phase.Total)
                Interlocked.Add(ref _allocatedBytes, GC.GetAllocatedBytesForCurrentThread() - _initialAllocatedBytes);
        }
    }

    internal static void RecordActivity(bool[] activeSources)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _branchEvaluations);
        Interlocked.Add(ref _descriptorSources, activeSources.Length);
        var inactiveCount = 0;
        foreach (var active in activeSources)
            if (!active) inactiveCount++;
        Interlocked.Add(ref _inactiveSources, inactiveCount);
    }

    // Cumulative inclusive times overlap: memory reads are also part of snapshot evaluation.
    public static void WriteReport() => WriteReport(Console.Error);

    internal static void WriteReport(TextWriter output)
    {
        if (!Enabled) return;
        var parts = new List<string>();
        for (var index = 0; index < (int)Phase.Count; index++)
            parts.Add(FormattableString.Invariant($"{(Phase)index}={Interlocked.Read(ref _elapsedTicks[index]) * 1000.0 / Stopwatch.Frequency:F3}ms/n{Interlocked.Read(ref _calls[index])}"));
        output.WriteLine($"[PERF][RESOURCE_MATERIALIZATION] cumulative=1 inclusive=1 allocated_bytes={Interlocked.Read(ref _allocatedBytes)} branch_evaluations={Interlocked.Read(ref _branchEvaluations)} descriptor_sources={Interlocked.Read(ref _descriptorSources)} inactive_sources={Interlocked.Read(ref _inactiveSources)} {string.Join(" ", parts)}");
    }
}
