// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// WalkGuestStackReturnAddresses only understands classic push-rbp/mov-rbp-rsp
// frames: it reads the saved RBP at [rbp] and the return address at [rbp+8],
// and requires each successive RBP to be strictly higher than the last (the
// stack grows down, so that's true for any real caller frame). These tests
// exercise that contract directly against a fake memory map so a regression
// here fails without needing a live guest.
public sealed class DirectExecutionBackendStackWalkTests
{
    private sealed class FakeStack
    {
        private readonly Dictionary<ulong, ulong> _values = new();

        public void SetFrame(ulong rbp, ulong savedRbp, ulong returnAddress)
        {
            _values[rbp] = savedRbp;
            _values[rbp + 8] = returnAddress;
        }

        public bool TryReadUInt64(ulong address, out ulong value) =>
            _values.TryGetValue(address, out value);
    }

    [Fact]
    public void WalksThreeFrameChainToSentinel()
    {
        var stack = new FakeStack();
        stack.SetFrame(0x1000, savedRbp: 0x2000, returnAddress: 0x800000010);
        stack.SetFrame(0x2000, savedRbp: 0x3000, returnAddress: 0x800000020);
        stack.SetFrame(0x3000, savedRbp: 0, returnAddress: 0x800000030);

        var frames = DirectExecutionBackend.WalkGuestStackReturnAddresses(stack.TryReadUInt64, 0x1000);

        Assert.Equal(new ulong[] { 0x800000010, 0x800000020, 0x800000030 }, frames);
    }

    [Fact]
    public void ZeroStartingRbpProducesNoFrames()
    {
        var stack = new FakeStack();

        var frames = DirectExecutionBackend.WalkGuestStackReturnAddresses(stack.TryReadUInt64, 0);

        Assert.Empty(frames);
    }

    [Fact]
    public void ZeroReturnAddressStopsTheWalk()
    {
        var stack = new FakeStack();
        stack.SetFrame(0x1000, savedRbp: 0x2000, returnAddress: 0x800000010);
        stack.SetFrame(0x2000, savedRbp: 0x3000, returnAddress: 0); // e.g. a zeroed/unmapped slot

        var frames = DirectExecutionBackend.WalkGuestStackReturnAddresses(stack.TryReadUInt64, 0x1000);

        Assert.Equal(new ulong[] { 0x800000010 }, frames);
    }

    [Fact]
    public void NonMonotonicRbpStopsWithoutFollowingIt()
    {
        // A frame-pointer-omitted callee between two real frames leaves a
        // stale/garbage value at what the walker reads as "saved RBP" -- here
        // one that happens to sit *below* the current frame. Real callers
        // never do this (the stack only grows down), so the walk must treat
        // it as the end of a trustworthy chain rather than follow it into
        // unrelated memory.
        var stack = new FakeStack();
        stack.SetFrame(0x2000, savedRbp: 0x1000, returnAddress: 0x800000010);
        stack.SetFrame(0x1000, savedRbp: 0x3000, returnAddress: 0x800000099); // would be frame 2 if followed

        var frames = DirectExecutionBackend.WalkGuestStackReturnAddresses(stack.TryReadUInt64, 0x2000);

        Assert.Equal(new ulong[] { 0x800000010 }, frames);
    }

    [Fact]
    public void FailedReadStopsTheWalk()
    {
        var stack = new FakeStack();
        stack.SetFrame(0x1000, savedRbp: 0x2000, returnAddress: 0x800000010);
        // 0x2000 is deliberately never populated -- an unmapped/unreadable page.

        var frames = DirectExecutionBackend.WalkGuestStackReturnAddresses(stack.TryReadUInt64, 0x1000);

        Assert.Equal(new ulong[] { 0x800000010 }, frames);
    }

    [Fact]
    public void MaxFramesCapsAnUnboundedChain()
    {
        var stack = new FakeStack();
        ulong rbp = 0x1000;
        for (var i = 0; i < 100; i++)
        {
            var next = rbp + 0x1000;
            stack.SetFrame(rbp, savedRbp: next, returnAddress: 0x800000000UL + (ulong)i);
            rbp = next;
        }

        var frames = DirectExecutionBackend.WalkGuestStackReturnAddresses(stack.TryReadUInt64, 0x1000, maxFrames: 5);

        Assert.Equal(5, frames.Count);
    }
}
