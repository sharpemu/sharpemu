// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Diagnostics;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcRegisterPacketProfileTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData("1", null, false)]
    [InlineData(null, "1", false)]
    [InlineData("1", "0", false)]
    [InlineData("0", "1", false)]
    [InlineData("1", "1", true)]
    public void MeasurementsRequireBothSwitches(string? performance, string? frameTrace, bool expected) =>
        Assert.Equal(expected, AgcRegisterPacketProfile.IsEnabled(performance, frameTrace));

    [Fact]
    public void CountersKeepPhasesPathsAndResultsSeparate()
    {
        var counters = new AgcRegisterPacketProfile.Counters();
        counters.RecordPhase(AgcRegisterPacketProfile.Phase.BulkCopy, 17);
        counters.RecordPhase(AgcRegisterPacketProfile.Phase.BulkCopy, 23);
        counters.RecordPhase(AgcRegisterPacketProfile.Phase.ScalarCopy, 31);
        foreach (var path in Enum.GetValues<AgcRegisterPacketProfile.PayloadPath>())
        {
            if (path == AgcRegisterPacketProfile.PayloadPath.Count) continue;
            counters.RecordPath(path, (uint)path + 1);
        }
        counters.RecordResult(true);
        counters.RecordResult(false);

        Assert.Equal(new AgcRegisterPacketProfile.PhaseSnapshot(2, 40), counters.ReadPhase(AgcRegisterPacketProfile.Phase.BulkCopy));
        Assert.Equal(new AgcRegisterPacketProfile.PhaseSnapshot(1, 31), counters.ReadPhase(AgcRegisterPacketProfile.Phase.ScalarCopy));
        Assert.Equal(default, counters.ReadPhase(AgcRegisterPacketProfile.Phase.PacketSetup));
        foreach (var path in Enum.GetValues<AgcRegisterPacketProfile.PayloadPath>())
        {
            if (path == AgcRegisterPacketProfile.PayloadPath.Count) continue;
            Assert.Equal(new AgcRegisterPacketProfile.PathSnapshot(1, (long)path + 1), counters.ReadPath(path));
        }
        Assert.Equal(1, counters.Succeeded);
        Assert.Equal(1, counters.Failed);
    }

    [Fact]
    public void ConcurrentWritersDoNotLoseCounts()
    {
        var counters = new AgcRegisterPacketProfile.Counters();
        Parallel.For(0, 10000, _ =>
        {
            counters.RecordPhase(AgcRegisterPacketProfile.Phase.Emitter, 7);
            counters.RecordPath(AgcRegisterPacketProfile.PayloadPath.NullSource, 64);
            counters.RecordResult(true);
        });

        Assert.Equal(new AgcRegisterPacketProfile.PhaseSnapshot(10000, 70000), counters.ReadPhase(AgcRegisterPacketProfile.Phase.Emitter));
        Assert.Equal(new AgcRegisterPacketProfile.PathSnapshot(10000, 640000), counters.ReadPath(AgcRegisterPacketProfile.PayloadPath.NullSource));
        Assert.Equal(10000, counters.Succeeded);
        Assert.Equal(0, counters.Failed);
    }

    [Fact]
    public void ScopeRecordsExceptionsAndKeepsNestedTimesInclusive()
    {
        var counters = new AgcRegisterPacketProfile.Counters();
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var emitter = new AgcRegisterPacketProfile.Scope(counters, AgcRegisterPacketProfile.Phase.Emitter);
            using var copy = new AgcRegisterPacketProfile.Scope(counters, AgcRegisterPacketProfile.Phase.BulkCopy);
            throw new InvalidOperationException();
        }));

        var emitter = counters.ReadPhase(AgcRegisterPacketProfile.Phase.Emitter);
        var copy = counters.ReadPhase(AgcRegisterPacketProfile.Phase.BulkCopy);
        Assert.Equal(1, emitter.Calls);
        Assert.Equal(1, copy.Calls);
        Assert.True(emitter.Ticks >= copy.Ticks);
        default(AgcRegisterPacketProfile.Scope).Dispose();
        Assert.Equal(emitter, counters.ReadPhase(AgcRegisterPacketProfile.Phase.Emitter));
    }
}
