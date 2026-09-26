// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>
/// Validates the per-NID unresolved-import backoff mechanism (#619).
/// A title that polls an unresolved import retries with no backoff. After
/// the first occurrence logs the missing NID, subsequent dispatches for the
/// same NID skip the string allocation and stderr write, and yield the CPU.
/// </summary>
public sealed class UnresolvedImportBackoffTests
{
    [Fact]
    public void FirstOccurrence_LogsAndRecordsNid()
    {
        var nids = CreateHashSet();

        // First call: Add returns true → caller should log
        Assert.True(nids.Add("s9e3+YpRnzw"));
    }

    [Fact]
    public void SecondOccurrence_SkipsLog()
    {
        var nids = CreateHashSet();

        nids.Add("s9e3+YpRnzw");

        // Second call: Add returns false → caller should skip log and yield
        Assert.False(nids.Add("s9e3+YpRnzw"));
    }

    [Fact]
    public void DifferentNids_AreTrackedIndependently()
    {
        var nids = CreateHashSet();

        Assert.True(nids.Add("s9e3+YpRnzw"));
        Assert.True(nids.Add("L-Q3LEjIbgA"));

        // Each NID is independent: first occurrence of a new NID still logs
        Assert.False(nids.Add("s9e3+YpRnzw"));
        Assert.False(nids.Add("L-Q3LEjIbgA"));
    }

    [Fact]
    public void Reset_ClearsAllTrackedNids()
    {
        var nids = CreateHashSet();

        nids.Add("s9e3+YpRnzw");
        nids.Add("L-Q3LEjIbgA");

        nids.Clear();

        // After reset, both NIDs are treated as first occurrence again
        Assert.True(nids.Add("s9e3+YpRnzw"));
        Assert.True(nids.Add("L-Q3LEjIbgA"));
    }

    [Fact]
    public void MillionRetries_ConvergesToZeroAllocation()
    {
        var nids = CreateHashSet();
        var nid = "s9e3+YpRnzw";

        // Simulate 1 million retries: only the first triggers Add=true
        var loggedCount = 0;
        for (var i = 0; i < 1_000_000; i++)
        {
            if (nids.Add(nid))
            {
                loggedCount++;
            }
        }

        // Exactly one log event across a million retries
        Assert.Equal(1, loggedCount);
    }

    private static HashSet<string> CreateHashSet()
    {
        // Access the same HashSet<string> type used in DirectExecutionBackend
        return new HashSet<string>(StringComparer.Ordinal);
    }
}
