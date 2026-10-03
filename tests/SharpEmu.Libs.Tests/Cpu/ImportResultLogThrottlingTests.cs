// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// A title that retries an unresolved/failing import in a tight loop with no
// backoff of its own (e.g. #619, sceSaveDataDialogInitialize on Demon's
// Souls) used to make ShouldLogImportResult return true unconditionally for
// every single call, since only a specific allowlist of known-benign
// transient results (mutex-trylock-busy, semaphore timeouts, ...) was ever
// throttled. These tests pin the fix: any (nid, result) pair not on that
// allowlist is now sampled the same way (first 8 occurrences, then every
// 10000th) instead of logging without bound.
public sealed class ImportResultLogThrottlingTests
{
    private static DirectExecutionBackend CreateBackend()
    {
        var moduleManager = new ModuleManager();
        moduleManager.Freeze();
        return new DirectExecutionBackend(moduleManager);
    }

    [Fact]
    public void UnexpectedResult_SamplesInsteadOfLoggingUnconditionally()
    {
        using var backend = CreateBackend();
        const string nid = "test-unexpected-nid-for-619";

        var loggedCallNumbers = new System.Collections.Generic.List<int>();
        for (var call = 1; call <= 20000; call++)
        {
            if (backend.ShouldLogImportResult(nid, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND))
            {
                loggedCallNumbers.Add(call);
            }
        }

        // Calls 1-8 always log; then only every 10000th (10000, 20000) in this range.
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 10000, 20000 }, loggedCallNumbers);
    }

    [Fact]
    public void PositiveResult_NeverLogs()
    {
        using var backend = CreateBackend();

        Assert.False(backend.ShouldLogImportResult("test-positive-result-nid", (OrbisGen2Result)1));
    }

    [Fact]
    public void KnownBenignTransientResult_IsSilentByDefault()
    {
        using var backend = CreateBackend();

        // scePthreadMutexTrylock (K-jXhbt2gn4) returning BUSY is expected,
        // routine polling noise -- silent unless
        // SHARPEMU_LOG_EXPECTED_IMPORT_RESULTS=1 opts back in.
        Assert.False(backend.ShouldLogImportResult("K-jXhbt2gn4", OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY));
    }
}
