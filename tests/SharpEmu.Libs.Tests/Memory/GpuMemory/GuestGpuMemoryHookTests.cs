// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

[Collection(GpuMemoryStateCollection.Name)]
public sealed class GuestGpuMemoryHookTests
{
    [Fact]
    public void ShutdownSummary_IsTakenOnceAndResetsForANewSession()
    {
        Assert.False(GuestGpuMemoryHook.TryTakeShutdownSummary(out _));
        for (var session = 0; session < 2; session++)
        {
            using var memory = new GuestGpuMemory(new RecordingAddressSpace(), new IdleBufferStore(), new IdleImageStore());
            GuestGpuMemoryHook.Attach(memory);
            try
            {
                GuestGpuMemoryHook.NoteMapped(0x10000, 0x1000);
                Assert.True(GuestGpuMemoryHook.TryTakeShutdownSummary(out var summary));
                Assert.Contains("mapped=1 unmapped=0 faults_resolved=0 faults_declined=0", summary);
                Assert.False(GuestGpuMemoryHook.TryTakeShutdownSummary(out _));
            }
            finally
            {
                GuestGpuMemoryHook.Attach(null);
            }
        }
    }

    [Theory]
    [InlineData(0UL, 0x1000UL, false)]
    [InlineData(0x1000UL, 0UL, false)]
    [InlineData(1UL << 40, 0x1000UL, false)]
    [InlineData(0x1000UL, (1UL << 40) - 0x1000, false)]
    [InlineData(0x1000UL, (1UL << 40) - 0x1001, true)]
    [InlineData(0x1000UL, 0x1000UL, true)]
    public void IsWithinGpuAddressSpace_ExcludesTheUpperAddressLimit(ulong address, ulong size, bool expected)
    {
        Assert.Equal(expected, GuestGpuMemoryHook.IsWithinGpuAddressSpace(address, size));
    }

    [Fact]
    public void Hook_IsNullTolerantForMapsAndDeclinesFaults()
    {
        Assert.Null(GuestGpuMemoryHook.Current);

        GuestGpuMemoryHook.NoteMapped(0x10000, 0x1000);
        GuestGpuMemoryHook.NoteUnmapped(0x10000, 0x1000);

        Assert.False(GuestGpuMemoryHook.TryResolveFault(FaultKind.Write, 0x10000));
    }

    [Fact]
    public void Hook_RoutesToTheAttachedMemoryAndCounts()
    {
        var memory = new GuestGpuMemory(new RecordingAddressSpace(), new IdleBufferStore(), new IdleImageStore());
        GuestGpuMemoryHook.Attach(memory);
        try
        {
            Assert.Same(memory, GuestGpuMemoryHook.Current);
            GuestGpuMemoryHook.NoteMapped(0x10000, 0x1000);
            Assert.True(memory.Covers(0x10000, 0x1000));
            Assert.False(GuestGpuMemoryHook.TryResolveFault(FaultKind.Read, 0x10008));
            Assert.False(GuestGpuMemoryHook.TryResolveFault(FaultKind.Write, 0x10008));
            GuestGpuMemoryHook.MarkCpuWrite(0x10000, 0);
            GuestGpuMemoryHook.MarkCpuWrite(0x10000, 0x10);
            GuestGpuMemoryHook.NoteUnmapped(0x10000, 0x1000);
            Assert.False(memory.Covers(0x10000, 0x1000));

            var summary = GuestGpuMemoryHook.GetSummary();
            Assert.StartsWith("gpu_memory: mapped=", summary);
            Assert.Contains(" faults_resolved=0 ", summary);
            Assert.Contains(" faults_declined=", summary);
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
            memory.Dispose();
        }
    }
}
