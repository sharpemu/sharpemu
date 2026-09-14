// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class ImportLoopGuardBoundaryTests
{
    [Theory]
    [InlineData(typeof(KernelRuntimeCompatExports), "gettimeofday", "n88vx3C5nW8")]
    [InlineData(typeof(KernelMemoryCompatExports), "clock_gettime", "lLMT9vJAck0")]
    [InlineData(typeof(KernelRuntimeCompatExports), "sceKernelReadTsc", "-2IRUCO--PM")]
    [InlineData(typeof(KernelRuntimeCompatExports), "sceKernelGetProcessTime", "4J2sUJmuHZQ")]
    [InlineData(typeof(KernelRuntimeCompatExports), "sceKernelGetProcessTimeCounter", "fgxnMeTNUtY")]
    public void TimeQuery_ClearsExpiredHistory(Type exportContainer, string exportName, string expectedNid)
    {
        var export = Assert.Single(exportContainer.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(method => method.GetCustomAttributes<SysAbiExportAttribute>()),
            attribute => attribute.ExportName == exportName);
        Assert.Equal(expectedNid, export.Nid);
        AssertBoundaryClearsExpiredHistory(export.Nid);
    }

    [Theory]
    [InlineData("1jfXLRVzisc")]
    [InlineData("WKAXJ4XBPQ4")]
    [InlineData("BmMjYxmew1w")]
    [InlineData("Op8TBGY5KHg")]
    [InlineData("27bAgiJmOh0")]
    public void ExistingWait_ClearsExpiredHistory(string nid)
    {
        AssertBoundaryClearsExpiredHistory(nid);
    }

    [Theory]
    [InlineData("BNowx2l588E")]
    [InlineData("j4ViWNHEgww")]
    [InlineData("0V5nU-Z6t4U")]
    [InlineData("aI6lQW5v57k")]
    [InlineData("unknown-import")]
    public void OtherImport_DoesNotClearExpiredHistory(string nid)
    {
        Assert.False(IsBoundary(nid));
        var guard = new GuardFixture();
        guard.RecordRepeatingImports(nid);
        guard.ExpireHistory();

        Assert.True(guard.ObserveImport(nid));
        Assert.NotEqual(0, guard.ReadField<int>("_importLoopSignatureCount"));
    }

    [Fact]
    public void OrdinaryRepeatingImports_RequireElapsedTimeout()
    {
        var guard = new GuardFixture();
        guard.RecordRepeatingImports();
        guard.WriteField("_importLoopPatternHits", 6);
        guard.WriteField("_importLoopPatternStartTimestamp", Stopwatch.GetTimestamp());

        Assert.False(guard.ObserveImport());
        guard.ExpireHistory();
        Assert.True(guard.ObserveImport());
    }

    [Fact]
    public void Boundary_RequiresNewHistoryBeforeAnotherExit()
    {
        var guard = new GuardFixture();
        guard.RecordRepeatingImports();
        guard.ExpireHistory();
        Assert.True(guard.ObserveImport());
        Assert.False(guard.ObserveImport("lLMT9vJAck0"));
        var newHistoryStart = Stopwatch.GetTimestamp();

        Assert.False(guard.ObserveImport());
        Assert.Equal(1, guard.ReadField<int>("_importLoopSignatureCount"));
        Assert.Equal(0, guard.ReadField<long>("_importLoopPatternStartTimestamp"));
        guard.RecordRepeatingImports();
        Assert.False(guard.ObserveImport());
        Assert.Equal(1, guard.ReadField<int>("_importLoopPatternHits"));
        Assert.True(guard.ReadField<long>("_importLoopPatternStartTimestamp") >= newHistoryStart);

        guard.ExpireHistory();
        Assert.True(guard.ObserveImport());
    }

    [Fact]
    public void ExpiredHistory_DoesNotBypassDispatchSampling()
    {
        var guard = new GuardFixture();
        guard.RecordRepeatingImports();
        guard.ExpireHistory();

        Assert.False(guard.ObserveImport(dispatchIndex: 1281));
        Assert.True(guard.ObserveImport());
    }

    [Theory]
    [InlineData(true, 5)]
    [InlineData(false, 0)]
    public void DisabledGuard_DoesNotForceExit(bool disabled, int timeoutSeconds)
    {
        var guard = new GuardFixture();
        guard.RecordRepeatingImports();
        guard.ExpireHistory();
        guard.WriteField("_disableImportLoopGuard", disabled);
        guard.WriteField("_importLoopGuardSeconds", timeoutSeconds);

        Assert.False(guard.ObserveImport());
    }

    private static void AssertBoundaryClearsExpiredHistory(string nid)
    {
        Assert.True(IsBoundary(nid));
        var guard = new GuardFixture();
        guard.RecordRepeatingImports();
        guard.ExpireHistory();
        Assert.True(guard.ObserveImport());

        Assert.False(guard.ObserveImport(nid));
        Assert.Equal(0, guard.ReadField<int>("_importLoopPatternHits"));
        Assert.Equal(0, guard.ReadField<long>("_importLoopPatternStartTimestamp"));
        Assert.Equal(0, guard.ReadField<int>("_importLoopSignatureCount"));
        Assert.Equal(0, guard.ReadField<int>("_importLoopSignatureWriteIndex"));
    }

    private static bool IsBoundary(string nid)
    {
        var method = typeof(DirectExecutionBackend).GetMethod("IsImportLoopGuardBoundary",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method.Invoke(null, [nid]));
    }

    private sealed class GuardFixture
    {
        private readonly DirectExecutionBackend _backend;

        public GuardFixture()
        {
            // The guard needs history arrays, not native thread and memory resources.
            _backend = (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(typeof(DirectExecutionBackend));
            var historyLength = typeof(DirectExecutionBackend).GetField("ImportLoopHistoryLength",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(historyLength);
            var capacity = Assert.IsType<int>(historyLength.GetRawConstantValue());
            WriteField("_importLoopSignatures", new ulong[capacity]);
            WriteField("_importLoopNidHashes", new ulong[capacity]);
            WriteField("_importLoopReturnRips", new ulong[capacity]);
            WriteField("_importLoopGuardSeconds", 5);
        }

        public void RecordRepeatingImports(string nid = "ordinary-import")
        {
            for (var importIndex = 0; importIndex < 1024; importIndex++)
                Assert.False(ObserveImport(nid, dispatchIndex: 1281));
        }

        public void ExpireHistory()
        {
            // Seed elapsed history so these tests do not wait for the timeout.
            WriteField("_importLoopPatternHits", 6);
            WriteField("_importLoopPatternStartTimestamp", Stopwatch.GetTimestamp() - 6 * Stopwatch.Frequency);
        }

        public bool ObserveImport(string nid = "ordinary-import", long dispatchIndex = 1280)
        {
            var entryType = typeof(DirectExecutionBackend).GetNestedType("ImportStubEntry", BindingFlags.NonPublic);
            Assert.NotNull(entryType);
            var entry = Activator.CreateInstance(entryType, [0UL, nid, null, false, false, false, IsBoundary(nid), 1UL]);
            var method = typeof(DirectExecutionBackend).GetMethod("ShouldForceGuestExitOnImportLoop",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            return Assert.IsType<bool>(method.Invoke(_backend, [entry, 0x1000UL, dispatchIndex, 0UL, 0UL]));
        }

        public void WriteField(string fieldName, object value) => FindField(fieldName).SetValue(_backend, value);

        public TField ReadField<TField>(string fieldName) => Assert.IsType<TField>(FindField(fieldName).GetValue(_backend));

        private static FieldInfo FindField(string fieldName)
        {
            var field = typeof(DirectExecutionBackend).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            return field;
        }
    }
}
