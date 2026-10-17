// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;
using static SharpEmu.Libs.VideoOut.VulkanVideoPresenter;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class ConcurrentResolveTests
{
    [Fact]
    public void ConsecutiveAndRepeatedReadsKeepOneProofPerQuery()
    {
        var assumptions = new ConcurrentAssumptions(1, 2);
        assumptions.Record(ConcurrentQuery.ResidentClean, 0x1000, 4, false);
        assumptions.Record(ConcurrentQuery.CleanWord, 0x1000, 4, false);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100000; index++)
        {
            var address = 0x1000UL + (ulong)index * 4;
            assumptions.Record(ConcurrentQuery.ResidentClean, address, 4, false);
            assumptions.Record(ConcurrentQuery.CleanWord, address, 4, false);
            assumptions.Record(ConcurrentQuery.ResidentClean, address, 4, false);
        }

        Assert.Equal(allocated, GC.GetAllocatedBytesForCurrentThread());
        Assert.Equal(2, assumptions.Answers.Count);
        Assert.All(assumptions.Answers, proof =>
        {
            Assert.Equal(0x1000UL, proof.Address);
            Assert.Equal(400000UL, proof.Size);
            Assert.False(proof.Answer);
        });
        Assert.True(assumptions.ReadsImageOwnership);
    }

    [Fact]
    public void OverlappingReadsMergeWithoutAddingUnreadGaps()
    {
        var assumptions = new ConcurrentAssumptions(1, 2);
        assumptions.Record(ConcurrentQuery.DirtyBytes, 0x1020, 0x20, false);
        assumptions.Record(ConcurrentQuery.DirtyBytes, 0x1000, 0x30, false);
        assumptions.Record(ConcurrentQuery.DirtyBytes, 0x1050, 0x10, false);

        Assert.Equal((ConcurrentQuery.DirtyBytes, 0x1000UL, 0x40UL, false), assumptions.Answers[0]);
        Assert.Equal((ConcurrentQuery.DirtyBytes, 0x1050UL, 0x10UL, false), assumptions.Answers[1]);
        Assert.False(assumptions.ReadsImageOwnership);
    }

    [Fact]
    public void OwnedReadsAndDifferentOwnershipRulesRemainSeparate()
    {
        var assumptions = new ConcurrentAssumptions(1, 2);
        assumptions.Record(ConcurrentQuery.Resident, 0x1000, 4, false);
        assumptions.Record(ConcurrentQuery.ResidentClean, 0x1000, 4, false);
        assumptions.Record(ConcurrentQuery.Resident, 0x1004, 4, true);
        assumptions.Record(ConcurrentQuery.Resident, 0x1008, 4, true);
        assumptions.Record(ConcurrentQuery.Resident, 0x1004, 4, true);

        Assert.Equal(4, assumptions.Answers.Count);
        Assert.Equal(2, assumptions.Answers.Count(proof => proof.Answer));
        Assert.True(assumptions.ReadsImageOwnership);
    }

    [Fact]
    public void WrappingRangesDoNotMergeWithLowAddresses()
    {
        var assumptions = new ConcurrentAssumptions(1, 2);
        assumptions.Record(ConcurrentQuery.Resident, ulong.MaxValue - 1, 4, false);
        assumptions.Record(ConcurrentQuery.Resident, 0, 4, false);

        Assert.Equal(2, assumptions.Answers.Count);
    }
}
