// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpEmu.Core.Cpu.Native;

/// <summary>
/// Opt-in per-import cost profile (SHARPEMU_PROFILE_IMPORTS=1).
/// </summary>
/// <remarks>
/// Guest code runs natively and reaches the emulator through a trampoline into
/// managed code, so a title that is guest-CPU bound is often paying for the
/// crossing rather than for the work on the far side. Answering that needs the
/// crossing and the export body measured separately, which is what this does:
/// every dispatch is charged to its import slot as <em>prologue</em> (gateway
/// entry up to the export call), <em>body</em> (the export itself) and
/// <em>epilogue</em> (register writeback and return).
///
/// Costs are kept per import slot, not per NID string, so recording one call is
/// an array index rather than a dictionary probe. Names are resolved once at
/// report time.
///
/// <para>Reading the numbers: totals are elapsed wall time and include any wait
/// an export performs, so a blocking mutex dominates the totals without costing
/// CPU. <c>min_us</c> is the cheapest call observed for that import, which is
/// the useful figure for comparing runs - it is the uncontended floor, immune
/// both to the guest blocking and to another process competing for the
/// machine.</para>
/// </remarks>
internal static class ImportProfile
{
    internal static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_IMPORTS"),
        "1",
        StringComparison.Ordinal);

    private static readonly double TicksToUs = 1_000_000.0 / Stopwatch.Frequency;

    private sealed class Slot
    {
        public long Calls;
        public long PrologueTicks;
        public long BodyTicks;
        public long EpilogueTicks;
        public long TotalTicks;
        public long MaxTicks;
        public long MinTicks = long.MaxValue;
        public long ReportedCalls;
        public long ReportedTotalTicks;
    }

    private static Slot[] _slots = [];
    private static string[] _names = [];
    private static int _reporterStarted;
    private static long _unslotted;

    // The gateway cannot thread the body marks through the dispatch chain
    // without changing every signature on it, so the two timestamps are left in
    // thread-local slots. Dispatch can re-enter (an export that calls back into
    // guest code which calls another import), and the inner dispatch overwrites
    // these; the outer call then charges the inner body to itself. That is
    // correct for an inclusive measure and is why the split is reported as
    // inclusive.
    [ThreadStatic] private static long _bodyStart;
    [ThreadStatic] private static long _bodyEnd;

    /// <summary>
    /// Publishes the import table's display names and sizes the cost slots.
    /// Called once per backend before any dispatch.
    /// </summary>
    internal static void Configure(string[] names)
    {
        if (!Enabled)
        {
            return;
        }

        var slots = new Slot[names.Length];
        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] = new Slot();
        }

        _names = names;
        // Publish the slots last: a dispatch racing this sees either the old
        // table or a fully built one, never a half-filled array.
        Volatile.Write(ref _slots, slots);
        EnsureReporter();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Begin()
    {
        if (!Enabled)
        {
            return 0;
        }

        _bodyStart = 0;
        _bodyEnd = 0;
        return Stopwatch.GetTimestamp();
    }

    /// <summary>Marks the transition from gateway prologue into the export.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void MarkBodyStart()
    {
        if (Enabled)
        {
            _bodyStart = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Marks the export's return, before the gateway epilogue.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void MarkBodyEnd()
    {
        if (Enabled)
        {
            _bodyEnd = Stopwatch.GetTimestamp();
        }
    }

    internal static void End(int importIndex, long started)
    {
        if (!Enabled || started == 0)
        {
            return;
        }

        var finished = Stopwatch.GetTimestamp();
        var slots = Volatile.Read(ref _slots);
        if ((uint)importIndex >= (uint)slots.Length)
        {
            Interlocked.Increment(ref _unslotted);
            return;
        }

        var total = finished - started;
        if (total < 0)
        {
            total = 0;
        }

        // An export that never reached the body (an early return in the
        // prologue, or an unresolved import) charges everything to the prologue.
        var bodyStart = _bodyStart;
        var bodyEnd = _bodyEnd;
        long prologue, body, epilogue;
        if (bodyStart >= started && bodyEnd >= bodyStart && finished >= bodyEnd)
        {
            prologue = bodyStart - started;
            body = bodyEnd - bodyStart;
            epilogue = finished - bodyEnd;
        }
        else
        {
            prologue = total;
            body = 0;
            epilogue = 0;
        }

        var slot = slots[importIndex];
        Interlocked.Increment(ref slot.Calls);
        Interlocked.Add(ref slot.PrologueTicks, prologue);
        Interlocked.Add(ref slot.BodyTicks, body);
        Interlocked.Add(ref slot.EpilogueTicks, epilogue);
        Interlocked.Add(ref slot.TotalTicks, total);

        // Racy extremes: a lost update only blunts the reported range, which
        // never justifies a lock on the dispatch path.
        if (total > Volatile.Read(ref slot.MaxTicks))
        {
            Volatile.Write(ref slot.MaxTicks, total);
        }

        if (total < Volatile.Read(ref slot.MinTicks))
        {
            Volatile.Write(ref slot.MinTicks, total);
        }
    }

    private static void EnsureReporter()
    {
        if (Volatile.Read(ref _reporterStarted) != 0 ||
            Interlocked.Exchange(ref _reporterStarted, 1) != 0)
        {
            return;
        }

        var intervalSeconds = 5;
        if (int.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_IMPORTS_INTERVAL"),
                out var configured) &&
            configured is > 0 and <= 600)
        {
            intervalSeconds = configured;
        }

        var topCount = 15;
        if (int.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_IMPORTS_TOP"),
                out var configuredTop) &&
            configuredTop is > 0 and <= 200)
        {
            topCount = configuredTop;
        }

        var reporter = new Thread(() => ReportLoop(intervalSeconds, topCount))
        {
            IsBackground = true,
            Name = "import-profile",
        };
        reporter.Start();
    }

    private static void ReportLoop(int intervalSeconds, int topCount)
    {
        var started = Stopwatch.StartNew();
        var process = Process.GetCurrentProcess();
        var previousCpu = TimeSpan.Zero;
        var previousWall = TimeSpan.Zero;
        while (true)
        {
            Thread.Sleep(intervalSeconds * 1000);
            try
            {
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var wall = started.Elapsed;
                Console.Error.Write(FormatReport(
                    wall,
                    topCount,
                    cpu - previousCpu,
                    wall - previousWall));
                Console.Error.Flush();
                previousCpu = cpu;
                previousWall = wall;
            }
            catch (IOException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                // The process object can go stale during shutdown; the profile
                // is diagnostic only.
                return;
            }
        }
    }

    /// <summary>Renders the current totals. Exposed for tests.</summary>
    internal static string FormatReport(
        TimeSpan uptime,
        int topCount,
        TimeSpan windowCpu = default,
        TimeSpan windowWall = default)
    {
        var slots = Volatile.Read(ref _slots);
        var names = _names;
        var builder = new StringBuilder(8192);

        var totalCalls = 0L;
        var totalTicks = 0L;
        var prologueTicks = 0L;
        var bodyTicks = 0L;
        var epilogueTicks = 0L;
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            totalCalls += Volatile.Read(ref slot.Calls);
            totalTicks += Volatile.Read(ref slot.TotalTicks);
            prologueTicks += Volatile.Read(ref slot.PrologueTicks);
            bodyTicks += Volatile.Read(ref slot.BodyTicks);
            epilogueTicks += Volatile.Read(ref slot.EpilogueTicks);
        }

        var cores = windowWall > TimeSpan.Zero
            ? windowCpu.TotalSeconds / windowWall.TotalSeconds
            : 0;
        builder.Append("[IMPORTS] t=").Append(uptime.TotalSeconds.ToString("F1"))
            .Append("s calls=").Append(totalCalls)
            .Append(" wall_s=").Append((totalTicks * TicksToUs / 1_000_000.0).ToString("F2"))
            .Append(" transition_s=")
            .Append(((prologueTicks + epilogueTicks) * TicksToUs / 1_000_000.0).ToString("F2"))
            .Append(" body_s=").Append((bodyTicks * TicksToUs / 1_000_000.0).ToString("F2"))
            .Append(" transition_share=")
            .Append(totalTicks > 0
                ? ((prologueTicks + epilogueTicks) * 100.0 / totalTicks).ToString("F1")
                : "0")
            .Append("% busy_cores=").Append(cores.ToString("F2"))
            .Append(" unslotted=").Append(Volatile.Read(ref _unslotted))
            .AppendLine(" (inclusive elapsed, not CPU; min_us is the uncontended floor)");

        var order = new int[slots.Length];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(
            order,
            (left, right) => Volatile.Read(ref slots[right].TotalTicks)
                .CompareTo(Volatile.Read(ref slots[left].TotalTicks)));

        var shown = 0;
        for (var i = 0; i < order.Length && shown < topCount; i++)
        {
            var index = order[i];
            var slot = slots[index];
            var calls = Volatile.Read(ref slot.Calls);
            if (calls == 0)
            {
                continue;
            }

            shown++;
            var total = Volatile.Read(ref slot.TotalTicks);
            var windowCalls = calls - slot.ReportedCalls;
            var windowTicks = total - slot.ReportedTotalTicks;
            slot.ReportedCalls = calls;
            slot.ReportedTotalTicks = total;
            var minTicks = Volatile.Read(ref slot.MinTicks);

            builder.Append("[IMPORTS]   ")
                .Append((index < names.Length ? names[index] : "?").PadRight(42))
                .Append(" n=").Append(calls)
                .Append(" total_ms=").Append((total * TicksToUs / 1000.0).ToString("F1"))
                .Append(" avg_us=").Append((total * TicksToUs / calls).ToString("F3"))
                .Append(" min_us=")
                .Append((minTicks == long.MaxValue ? 0 : minTicks * TicksToUs).ToString("F3"))
                .Append(" max_us=").Append((Volatile.Read(ref slot.MaxTicks) * TicksToUs).ToString("F1"))
                .Append(" pro_us=")
                .Append((Volatile.Read(ref slot.PrologueTicks) * TicksToUs / calls).ToString("F3"))
                .Append(" body_us=")
                .Append((Volatile.Read(ref slot.BodyTicks) * TicksToUs / calls).ToString("F3"))
                .Append(" epi_us=")
                .Append((Volatile.Read(ref slot.EpilogueTicks) * TicksToUs / calls).ToString("F3"))
                .Append(" +n=").Append(windowCalls)
                .Append(" +ms=").Append((windowTicks * TicksToUs / 1000.0).ToString("F1"))
                .AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>Test hook: drop all accumulated state.</summary>
    internal static void ResetForTests()
    {
        var slots = Volatile.Read(ref _slots);
        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] = new Slot();
        }

        Volatile.Write(ref _unslotted, 0);
    }
}
