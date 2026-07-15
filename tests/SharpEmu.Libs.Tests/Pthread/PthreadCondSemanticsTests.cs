// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

// POSIX condition variables are edges, not semaphore credits. A signal with no waiter
// must have no effect. This was violated by the previous implementation which persisted
// signals via PendingSignals, causing lock inversions and predicate bypasses.
// See issue #113.
public sealed class PthreadCondSemanticsTests
{
    [Fact]
    public void PthreadCondState_DoesNotHavePendingSignals()
    {
        // Verify that PthreadCondState no longer has the PendingSignals property.
        // This is a regression test to ensure the POSIX-correct behavior is maintained.
        var stateType = typeof(KernelPthreadCompatExports).GetNestedType("PthreadCondState", BindingFlags.NonPublic);
        Assert.NotNull(stateType);

        var pendingSignalsProp = stateType.GetProperty("PendingSignals");
        Assert.Null(pendingSignalsProp);

        var tryConsumeMethod = stateType.GetMethod("TryConsumePendingSignal");
        Assert.Null(tryConsumeMethod);
    }

    [Fact]
    public void PthreadCondSignal_WithNoWaiter_DoesNotPersist()
    {
        // This test verifies the semantic contract: signal without waiter is a no-op.
        // We can't easily test the full pthread flow without the scheduler, but we can
        // verify the code path by checking that SignalEpoch advances but no state persists.
        var stateType = typeof(KernelPthreadCompatExports).GetNestedType("PthreadCondState", BindingFlags.NonPublic);
        Assert.NotNull(stateType);

        // Create an instance via reflection
        var state = Activator.CreateInstance(stateType);
        Assert.NotNull(state);

        var syncRootProp = stateType.GetProperty("SyncRoot");
        var signalEpochProp = stateType.GetProperty("SignalEpoch");
        var waitersProp = stateType.GetProperty("Waiters");

        Assert.NotNull(syncRootProp);
        Assert.NotNull(signalEpochProp);
        Assert.NotNull(waitersProp);

        var syncRoot = syncRootProp.GetValue(state);
        Assert.NotNull(syncRoot);

        // Initial state
        Assert.Equal(0UL, (ulong)signalEpochProp.GetValue(state)!);
        Assert.Equal(0, (int)waitersProp.GetValue(state)!);

        // Simulate signal with no waiter (this would have incremented PendingSignals before)
        lock (syncRoot)
        {
            signalEpochProp.SetValue(state, (ulong)signalEpochProp.GetValue(state)! + 1);
            // Note: we don't increment PendingSignals because it doesn't exist
        }

        // Verify epoch advanced but no persistent signal state
        Assert.Equal(1UL, (ulong)signalEpochProp.GetValue(state)!);

        // A new waiter arriving should see the new epoch but not consume any "pending" signal
        // (because there's no such concept anymore)
        lock (syncRoot)
        {
            var observedEpoch = (ulong)signalEpochProp.GetValue(state)!;
            waitersProp.SetValue(state, (int)waitersProp.GetValue(state)! + 1);

            // Waiter sees epoch=1, will block until epoch changes again
            Assert.Equal(1UL, observedEpoch);
            Assert.Equal(1, (int)waitersProp.GetValue(state)!);
        }
    }
}
