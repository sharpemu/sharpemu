// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class NativeCpuSessionStatisticsTests
{
    [Fact]
    public void Snapshot_WithoutImports_IsEmpty()
    {
        var counters = new NativeImportSessionCounters();

        Assert.Equal(new NativeCpuSessionStatistics(0, 0), counters.Snapshot());
    }

    [Fact]
    public void Snapshot_AfterOneImport_ReportsOneImportAndOneNid()
    {
        var counters = new NativeImportSessionCounters();

        counters.Record("first-nid");

        Assert.Equal(new NativeCpuSessionStatistics(1, 1), counters.Snapshot());
    }

    [Fact]
    public void Snapshot_AfterRepeatedNid_CountsEveryImportOnce()
    {
        var counters = new NativeImportSessionCounters();

        counters.Record("same-nid");
        counters.Record("same-nid");

        Assert.Equal(new NativeCpuSessionStatistics(2, 1), counters.Snapshot());
    }

    [Fact]
    public void Snapshot_AfterDistinctNids_CountsBothNids()
    {
        var counters = new NativeImportSessionCounters();

        counters.Record("first-nid");
        counters.Record("second-nid");

        Assert.Equal(new NativeCpuSessionStatistics(2, 2), counters.Snapshot());
    }

    [Fact]
    public void Snapshot_AfterConcurrentImports_IsExact()
    {
        const int dispatchCount = 10_000;
        const int uniqueNidCount = 17;
        var counters = new NativeImportSessionCounters();

        Parallel.For(0, dispatchCount, index => counters.Record($"nid-{index % uniqueNidCount}"));

        Assert.Equal(
            new NativeCpuSessionStatistics(dispatchCount, uniqueNidCount),
            counters.Snapshot());
    }

    [Fact]
    public void DispatchEntry_CopiesNativeStatisticsIntoSessionSummary()
    {
        var memory = new VirtualMemory();
        var moduleManager = new ModuleManager();
        var backend = new StatisticsBackend(new NativeCpuSessionStatistics(2, 1));
        using var dispatcher = new CpuDispatcher(memory, moduleManager, backend);

        var result = dispatcher.DispatchEntry(
            0x1_0000,
            Generation.Gen5,
            executionOptions: new CpuExecutionOptions { CpuEngine = CpuExecutionEngine.NativeOnly });

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(2, dispatcher.LastSessionSummary.ImportsHit);
        Assert.Equal(1, dispatcher.LastSessionSummary.UniqueNidsHit);
    }

    [Fact]
    public void DispatchEntry_PreservesNativeStatisticsWhenBackendFails()
    {
        var memory = new VirtualMemory();
        var moduleManager = new ModuleManager();
        var backend = new StatisticsBackend(
            new NativeCpuSessionStatistics(2, 2),
            succeeds: false);
        using var dispatcher = new CpuDispatcher(memory, moduleManager, backend);

        _ = dispatcher.DispatchEntry(
            0x1_0000,
            Generation.Gen5,
            executionOptions: new CpuExecutionOptions { CpuEngine = CpuExecutionEngine.NativeOnly });

        Assert.Equal(2, dispatcher.LastSessionSummary.ImportsHit);
        Assert.Equal(2, dispatcher.LastSessionSummary.UniqueNidsHit);
    }

    private sealed class StatisticsBackend(
        NativeCpuSessionStatistics statistics,
        bool succeeds = true) : INativeCpuBackend, INativeCpuSessionStatisticsProvider
    {
        public string BackendName => "statistics-test";

        public string? LastError => succeeds ? null : "expected test failure";

        public NativeCpuSessionStatistics LastSessionStatistics => statistics;

        public bool TryExecute(
            CpuContext context,
            ulong entryPoint,
            Generation generation,
            IReadOnlyDictionary<ulong, string> importStubs,
            IReadOnlyDictionary<string, ulong> runtimeSymbols,
            CpuExecutionOptions executionOptions,
            out OrbisGen2Result result)
        {
            _ = context;
            _ = entryPoint;
            _ = generation;
            _ = importStubs;
            _ = runtimeSymbols;
            _ = executionOptions;
            result = succeeds
                ? OrbisGen2Result.ORBIS_GEN2_OK
                : OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP;
            return succeeds;
        }
    }
}
