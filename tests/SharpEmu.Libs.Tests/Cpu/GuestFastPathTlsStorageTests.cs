// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>
/// pthread-specific storage moved out of a per-thread dictionary into
/// GuestFastPath's native tables so an emitted stub can read it. These tests
/// pin the semantics the pthread exports rely on: per-thread isolation,
/// unset keys reading zero, pthread_key_delete clearing every thread, and the
/// compare-and-clear the destructor loop needs.
/// </summary>
[Collection("GuestFastPath")]
public sealed class GuestFastPathTlsStorageTests
{
    private static int _handleCounter;

    // pthread_key_create hands out keys from 1 upwards, and ClearKeyEverywhere is
    // global by design, so these tests use a high range no other test reaches.
    private const int KeyBase = 700;

    private static ulong NewHandle() =>
        0x0000700030000000UL + ((ulong)Interlocked.Increment(ref _handleCounter) << 12);

    [Fact]
    public void UnsetKeysReadZeroAndValuesAreThreadLocal()
    {
        var first = NewHandle();
        var second = NewHandle();

        Assert.Equal(0UL, GuestFastPath.GetSpecific(first, KeyBase + 3));

        GuestFastPath.SetSpecific(first, KeyBase + 3, 0xAAUL);

        Assert.Equal(0xAAUL, GuestFastPath.GetSpecific(first, KeyBase + 3));
        Assert.Equal(0UL, GuestFastPath.GetSpecific(second, KeyBase + 3));
        Assert.Equal(0UL, GuestFastPath.GetSpecific(first, KeyBase + 4));
        Assert.Equal(0UL, GuestFastPath.GetSpecific(0, KeyBase + 3));
    }

    [Fact]
    public void KeysBeyondTheTableStillRoundTrip()
    {
        var handle = NewHandle();
        var key = GuestFastPath.TlsSlotCount + 5;

        GuestFastPath.SetSpecific(handle, key, 0xBBUL);

        Assert.Equal(0xBBUL, GuestFastPath.GetSpecific(handle, key));
        Assert.Contains(new KeyValuePair<int, ulong>(key, 0xBBUL), GuestFastPath.SnapshotThreadValues(handle));

        GuestFastPath.ClearKeyEverywhere(key);

        Assert.Equal(0UL, GuestFastPath.GetSpecific(handle, key));
    }

    [Fact]
    public void ClearKeyEverywhereMatchesPthreadKeyDelete()
    {
        var first = NewHandle();
        var second = NewHandle();
        GuestFastPath.SetSpecific(first, KeyBase + 9, 1);
        GuestFastPath.SetSpecific(second, KeyBase + 9, 2);
        GuestFastPath.SetSpecific(second, KeyBase + 10, 3);

        GuestFastPath.ClearKeyEverywhere(KeyBase + 9);

        Assert.Equal(0UL, GuestFastPath.GetSpecific(first, KeyBase + 9));
        Assert.Equal(0UL, GuestFastPath.GetSpecific(second, KeyBase + 9));
        Assert.Equal(3UL, GuestFastPath.GetSpecific(second, KeyBase + 10));
    }

    [Fact]
    public void TryClearSpecificOnlyClearsTheObservedValue()
    {
        var handle = NewHandle();
        GuestFastPath.SetSpecific(handle, KeyBase + 11, 0x1234UL);

        // The destructor loop clears the value it is about to pass to the guest
        // destructor; a value the guest re-set in the meantime must survive so
        // the next iteration picks it up instead of dropping it.
        Assert.False(GuestFastPath.TryClearSpecific(handle, KeyBase + 11, 0x9999UL));
        Assert.Equal(0x1234UL, GuestFastPath.GetSpecific(handle, KeyBase + 11));

        Assert.True(GuestFastPath.TryClearSpecific(handle, KeyBase + 11, 0x1234UL));
        Assert.Equal(0UL, GuestFastPath.GetSpecific(handle, KeyBase + 11));
    }

    [Fact]
    public void SnapshotSkipsUnsetKeysAndReleaseDropsTheThread()
    {
        var handle = NewHandle();
        Assert.False(GuestFastPath.HasThreadValues(handle));

        GuestFastPath.SetSpecific(handle, KeyBase + 1, 0x10UL);
        GuestFastPath.SetSpecific(handle, KeyBase + 2, 0UL);
        GuestFastPath.SetSpecific(handle, KeyBase + 3, 0x30UL);

        var snapshot = GuestFastPath.SnapshotThreadValues(handle);

        Assert.True(GuestFastPath.HasThreadValues(handle));
        Assert.Equal(
            [new KeyValuePair<int, ulong>(KeyBase + 1, 0x10UL), new KeyValuePair<int, ulong>(KeyBase + 3, 0x30UL)],
            snapshot);

        GuestFastPath.ReleaseThread(handle);

        Assert.False(GuestFastPath.HasThreadValues(handle));
        Assert.Equal(0UL, GuestFastPath.GetSpecific(handle, KeyBase + 1));
    }
}
