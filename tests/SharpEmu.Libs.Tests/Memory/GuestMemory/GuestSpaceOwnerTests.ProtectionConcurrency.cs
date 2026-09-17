// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

public sealed unsafe partial class GuestSpaceOwnerTests
{
    private const ulong ProtectionBlockBytes = 4 * 1024 * 1024;

    [Fact]
    public void ProtectionWaitsUntilAMappingIsPublished()
    {
        if (!Supported) return;
        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var address = AcquireRange(owner, host, HoleSize(host));
        WithProtectionPaused(host, address,
            () => Assert.True(owner.MapShared(address, Page, 0, HostPageProtection.ReadWrite, out _)),
            () => Assert.True(owner.SetTransientAccess(address, Page, HostPageProtection.ReadOnly)),
            mustCompleteWhilePaused: false, pauseMapping: true);
        Assert.Contains(FailingHostViews.Op.ChangeAccess, host.Log);
        Assert.True(owner.SetAccess(address, Page, HostPageProtection.ReadWrite));
        *(ulong*)address = Marker;
        Assert.Equal(Marker, *(ulong*)owner.AliasBase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtectionOrdersLockCollisionsAndFullLockCoverage(bool coverAllLocks)
    {
        if (!Supported) return;
        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var address = AcquireRange(owner, host, 258 * ProtectionBlockBytes);
        var otherAddress = address + (coverAllLocks ? 128UL : 256UL) * ProtectionBlockBytes;
        Assert.True(owner.MapShared(address, Page, 0, HostPageProtection.ReadWrite, out _));
        Assert.True(owner.MapShared(otherAddress, Page, Page, HostPageProtection.ReadWrite, out _));
        var size = coverAllLocks ? 257 * ProtectionBlockBytes : Page;
        WithProtectionPaused(host, address,
            () => Assert.True(owner.SetTransientAccess(address, size, HostPageProtection.ReadOnly)),
            () => Assert.True(owner.SetTransientAccess(otherAddress, Page, HostPageProtection.ReadWrite)),
            mustCompleteWhilePaused: false);
        *(ulong*)otherAddress = Marker;
        Assert.Equal(Marker, *(ulong*)(owner.AliasBase + Page));
    }

    [Fact]
    public void TransientProtectionAllowsIndependentBlocksToProgress()
    {
        if (!Supported) return;
        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var address = AcquireRange(owner, host, 2 * ProtectionBlockBytes);
        var otherAddress = address + ProtectionBlockBytes;
        Assert.True(owner.MapShared(address, Page, 0, HostPageProtection.ReadWrite, out _));
        Assert.True(owner.MapShared(otherAddress, Page, Page, HostPageProtection.ReadWrite, out _));

        WithProtectionPaused(host, address,
            () => Assert.True(owner.SetTransientAccess(address, Page, HostPageProtection.ReadOnly)),
            () => Assert.True(owner.SetTransientAccess(otherAddress, Page, HostPageProtection.ReadOnly)),
            mustCompleteWhilePaused: true);
        Assert.True(owner.SetAccess(address, Page, HostPageProtection.ReadWrite));
        Assert.True(owner.SetAccess(otherAddress, Page, HostPageProtection.ReadWrite));
        *(ulong*)address = Marker;
        *(ulong*)otherAddress = Marker + 1;
        Assert.Equal(Marker, *(ulong*)owner.AliasBase);
        Assert.Equal(Marker + 1, *(ulong*)(owner.AliasBase + Page));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlappingProtectionWaitsForTheWholeRange(bool wrapsLockIndices)
    {
        if (!Supported) return;
        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var reservationSize = wrapsLockIndices ? 258 * ProtectionBlockBytes : 3 * ProtectionBlockBytes;
        var reservation = AcquireRange(owner, host, reservationSize);
        var boundary = wrapsLockIndices
            ? AlignUp(reservation + Page, 256 * ProtectionBlockBytes)
            : AlignUp(reservation + Page, ProtectionBlockBytes);
        var address = boundary - Page;
        Assert.True(owner.MapShared(address, Page, 0, HostPageProtection.ReadWrite, out _));
        Assert.True(owner.MapShared(boundary, Page, Page, HostPageProtection.ReadWrite, out _));
        WithProtectionPaused(host, address,
            () => Assert.True(owner.SetTransientAccess(address, 2 * Page, HostPageProtection.ReadOnly)),
            () => Assert.True(owner.SetTransientAccess(boundary, Page, HostPageProtection.ReadWrite)),
            mustCompleteWhilePaused: false);
        *(ulong*)boundary = Marker;
        Assert.Equal(Marker, *(ulong*)(owner.AliasBase + Page));
    }

    [Theory]
    [InlineData("unmap")]
    [InlineData("free")]
    [InlineData("release")]
    [InlineData("dispose")]
    [InlineData("map")]
    [InlineData("allocate")]
    [InlineData("access")]
    public void MappingChangesWaitForActiveProtection(string operation)
    {
        if (!Supported) return;
        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var address = AcquireRange(owner, host, HoleSize(host));
        if (operation == "free")
            Assert.True(owner.AllocatePrivate(address, Page, HostPageProtection.ReadWrite));
        else
            Assert.True(owner.MapShared(address, Page, 0, HostPageProtection.ReadWrite, out _));

        WithProtectionPaused(host, address,
            () => Assert.True(owner.SetTransientAccess(address, Page, HostPageProtection.ReadOnly)),
            () =>
            {
                switch (operation)
                {
                    case "unmap": Assert.True(owner.UnmapShared(address, Page)); break;
                    case "free": Assert.True(owner.FreePrivate(address, Page)); break;
                    case "release": owner.ReleaseAddressRanges(); break;
                    case "dispose": owner.Dispose(); break;
                    case "map": Assert.True(owner.MapShared(address + Page, Page, Page, HostPageProtection.ReadWrite, out _)); break;
                    case "allocate": Assert.True(owner.AllocatePrivate(address + Page, Page, HostPageProtection.ReadWrite)); break;
                    case "access": Assert.True(owner.SetAccess(address, Page, HostPageProtection.ReadWrite)); break;
                    default: Assert.Fail("Unknown mapping operation."); break;
                }
            },
            mustCompleteWhilePaused: false);
        if (operation == "dispose")
        {
            Assert.False(owner.SetTransientAccess(address, Page, HostPageProtection.ReadOnly));
            Assert.False(owner.SetAccess(address, Page, HostPageProtection.ReadOnly));
        }
    }

    [Fact]
    public void ThrowingProtectionReleasesAllAddressLocks()
    {
        if (!Supported) return;
        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var address = AcquireRange(owner, host, 2 * ProtectionBlockBytes);
        Assert.True(owner.MapShared(address, Page, 0, HostPageProtection.ReadWrite, out _));
        host.BeforeChangeAccess = (_, _) => throw new InvalidOperationException("Injected protection failure.");
        Assert.Throws<InvalidOperationException>(() =>
            owner.SetTransientAccess(address, 2 * ProtectionBlockBytes, HostPageProtection.ReadOnly));
        host.BeforeChangeAccess = null;
        RunOnNewThread(() =>
        {
            Assert.True(owner.SetTransientAccess(address, Page, HostPageProtection.ReadWrite));
            Assert.True(owner.UnmapShared(address, Page));
        });
    }

    [Fact]
    public void TransientProtectionDoesNotAllocateOnNewThread()
    {
        if (!Supported) return;
        var host = HostViewMemory.Create();
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var address = AcquireRange(owner, host, HoleSize(host));
        Assert.True(owner.MapShared(address, Page, 0, HostPageProtection.ReadWrite, out _));
        long allocated = -1;
        RunOnNewThread(() =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var succeeded = true;
            for (var iteration = 0; iteration < 100; iteration++)
                succeeded &= owner.SetTransientAccess(address, Page, HostPageProtection.ReadWrite);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(succeeded);
        });
        Assert.Equal(0, allocated);
    }

    private static void WithProtectionPaused(FailingHostViews host, ulong address,
        Action protection, Action competingOperation, bool mustCompleteWhilePaused, bool pauseMapping = false)
    {
        using var protectionEntered = new ManualResetEventSlim();
        using var releaseProtection = new ManualResetEventSlim();
        using var competitorStarted = new ManualResetEventSlim();
        using var competitorFinished = new ManualResetEventSlim();
        Exception? protectionFailure = null;
        Exception? competitorFailure = null;
        var intercepted = 0;
        void Pause()
        {
            protectionEntered.Set();
            Assert.True(releaseProtection.Wait(TimeSpan.FromSeconds(10)), "The test did not release the host operation.");
        }
        host.BeforeChangeAccess = (currentAddress, _) =>
        {
            if (pauseMapping) return;
            if (currentAddress != address || Interlocked.Exchange(ref intercepted, 1) != 0) return;
            Pause();
        };
        if (pauseMapping) host.AfterMapView = Pause;
        var protectionThread = new Thread(() =>
        {
            try { protection(); }
            catch (Exception exception) { protectionFailure = exception; }
        }) { IsBackground = true };
        var competitorThread = new Thread(() =>
        {
            competitorStarted.Set();
            try { competingOperation(); }
            catch (Exception exception) { competitorFailure = exception; }
            finally { competitorFinished.Set(); }
        }) { IsBackground = true };
        protectionThread.Start();
        try
        {
            Assert.True(protectionEntered.Wait(TimeSpan.FromSeconds(10)));
            competitorThread.Start();
            Assert.True(competitorStarted.Wait(TimeSpan.FromSeconds(10)));
            if (mustCompleteWhilePaused)
                Assert.True(competitorFinished.Wait(TimeSpan.FromSeconds(10)), "An independent range could not progress.");
            else
            {
                Assert.True(SpinWait.SpinUntil(() => competitorFinished.IsSet ||
                    (competitorThread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
                Assert.False(competitorFinished.IsSet, "A conflicting operation bypassed active protection.");
            }
        }
        finally
        {
            releaseProtection.Set();
            Assert.True(protectionThread.Join(TimeSpan.FromSeconds(10)));
            if ((competitorThread.ThreadState & ThreadState.Unstarted) == 0)
                Assert.True(competitorThread.Join(TimeSpan.FromSeconds(10)));
            host.BeforeChangeAccess = null;
            host.AfterMapView = null;
        }
        Assert.Null(protectionFailure);
        Assert.Null(competitorFailure);
    }

    private static void RunOnNewThread(Action operation)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { operation(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The operation retained a protection lock.");
        Assert.Null(failure);
    }
}
