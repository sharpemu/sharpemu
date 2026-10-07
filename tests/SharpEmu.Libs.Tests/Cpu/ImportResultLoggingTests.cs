// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class ImportResultLoggingTests
{
    private delegate bool ImportResultFilter(string nid, OrbisGen2Result result, out int mutexOccurrence);

    [NativeX64Fact]
    public void MutexSelfLockWarningsAreSampledWithoutHidingOtherFailures()
    {
        var modules = new ModuleManager();
        modules.Freeze();
        using var backend = new DirectExecutionBackend(modules);
        var method = typeof(DirectExecutionBackend).GetMethod(
            "ShouldLogImportResult", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var filter = method.CreateDelegate<ImportResultFilter>(backend);
        bool ShouldLog(string nid, OrbisGen2Result result) => filter(nid, result, out _);

        foreach (var nid in new[] { "9UK1vLZQft4", "7H0iTOciTLo" })
        {
            for (var occurrence = 1; occurrence <= 10001; occurrence++)
            {
                Assert.Equal(occurrence <= 8 || occurrence == 10000,
                    filter(nid, OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK, out var recordedOccurrence));
                Assert.Equal(occurrence, recordedOccurrence);
            }
            Assert.True(ShouldLog(nid, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT));
            Assert.True(ShouldLog(nid, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT));
        }
        Assert.True(filter("another-import", OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK, out var otherOccurrence));
        Assert.Equal(0, otherOccurrence);
        foreach (var nid in new[] { "2Z+PpY6CaJg", "tn3VlD0hG60" })
        foreach (var result in new[] { OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED })
        {
            for (var occurrence = 1; occurrence <= 10001; occurrence++)
            {
                Assert.Equal(occurrence <= 8 || occurrence == 10000, filter(nid, result, out var recordedOccurrence));
                Assert.Equal(occurrence, recordedOccurrence);
            }
        }
        Assert.True(ShouldLog("2Z+PpY6CaJg", OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT));
    }
}
