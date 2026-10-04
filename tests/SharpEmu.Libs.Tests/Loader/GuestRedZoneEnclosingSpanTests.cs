// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;

using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

/// <summary>
/// The forward span search anchors at the faulting instruction, so it gives up
/// whenever the next instruction is a branch target, touches RSP, or changes
/// control flow. These are the three shapes that actually occur around the
/// faulting sites of a red-zone function in a shipped title; the enclosing
/// search has to take its bytes from before the faulting instruction instead,
/// and must never let the RSP shift cover an RSP-relative access.
/// </summary>
public sealed class GuestRedZoneEnclosingSpanTests
{
    private const ulong Base = 0x8_0000_0000UL;

    [Fact]
    public void TakesBytesFromBeforeWhenTheNextInstructionIsABranchTarget()
    {
        // jne +8            -> makes the instruction after the site a target
        // shr r11, 12       -> 4 bytes, no memory operand
        // and r10d,[rcx+8]  -> 4 bytes, faultable: the site
        // mov rax,[rcx]     -> branch target, must stay untouched
        byte[] code =
        [
            0x75, 0x08,
            0x49, 0xC1, 0xEB, 0x0C,
            0x44, 0x23, 0x51, 0x08,
            0x48, 0x8B, 0x01,
        ];

        Assert.True(GuestRedZonePatcher.TryBuildEnclosingSpan(
            code, Base, Base + 6, out var address, out var length, out var coreStart, out var coreCount));

        Assert.Equal(Base + 2, address);
        Assert.Equal(8, length);
        // The span ends exactly at the branch target, which is never rewritten.
        Assert.Equal(Base + 10, address + (ulong)length);
        Assert.Equal(1, coreStart);
        Assert.Equal(1, coreCount);
    }

    [Fact]
    public void KeepsAnRspRelativeInstructionOutsideTheShiftedCore()
    {
        // jne +8
        // mov rax,[rsp-0x10] -> reads the red zone; must run before the shift
        // mov [rax+0x18],ebp -> 3 bytes, faultable: the site
        // pop rbx            -> branch target and stack dependent
        byte[] code =
        [
            0x75, 0x08,
            0x48, 0x8B, 0x44, 0x24, 0xF0,
            0x89, 0x68, 0x18,
            0x5B,
        ];

        Assert.True(GuestRedZonePatcher.TryBuildEnclosingSpan(
            code, Base, Base + 7, out var address, out var length, out var coreStart, out var coreCount));

        Assert.Equal(Base + 2, address);
        Assert.Equal(8, length);
        // coreStart == 1 is what keeps the shift off the [rsp-0x10] load: shifting
        // it would move the access 128 bytes and silently read the wrong slot.
        Assert.Equal(1, coreStart);
        Assert.Equal(1, coreCount);
    }

    [Fact]
    public void BracketsEveryFaultableInstructionOfTheSpan()
    {
        // mov eax,[rcx+8]  -> faultable, absorbed by the span
        // test [rcx+8],eax -> faultable: the site
        // je +2            -> control flow, cannot be absorbed
        byte[] code =
        [
            0x8B, 0x41, 0x08,
            0x85, 0x41, 0x08,
            0x74, 0x02,
        ];

        Assert.True(GuestRedZonePatcher.TryBuildEnclosingSpan(
            code, Base, Base + 3, out var address, out var length, out var coreStart, out var coreCount));

        Assert.Equal(Base, address);
        Assert.Equal(6, length);
        // A stolen instruction that can fault too has to sit inside the shift,
        // otherwise it recreates the very corruption this pass prevents.
        Assert.Equal(0, coreStart);
        Assert.Equal(2, coreCount);
    }

    [Fact]
    public void RefusesWhenTheFaultingInstructionIsItselfABranchTarget()
    {
        // jne +6 lands on the faulting instruction, so a jump placed before it
        // would be entered in the middle.
        byte[] code =
        [
            0x75, 0x04,
            0x49, 0xC1, 0xEB, 0x0C,
            0x44, 0x23, 0x51, 0x08,
            0x48, 0x8B, 0x01,
        ];

        Assert.False(GuestRedZonePatcher.TryBuildEnclosingSpan(
            code, Base, Base + 6, out _, out _, out _, out _));
    }

    [Fact]
    public void RefusesWhenControlFlowSitsBetweenTheStartAndTheSite()
    {
        // A conditional branch cannot be relocated as part of a span, and the
        // bytes before the site are not otherwise sufficient.
        byte[] code =
        [
            0x74, 0x02,
            0x44, 0x23, 0x51, 0x08,
            0x48, 0x8B, 0x01,
        ];

        Assert.False(GuestRedZonePatcher.TryBuildEnclosingSpan(
            code, Base, Base + 2, out _, out _, out _, out _));
    }

    [Fact]
    public void RefusesNeighborRelocationWhenFunctionHasIndirectControlFlow()
    {
        // jmp rax makes every instruction boundary in the function a possible
        // unresolved entry. Even though the bytes directly before the faulting
        // instruction look safe, an enclosing span must not relocate them.
        byte[] code =
        [
            0xFF, 0xE0,
            0x49, 0xC1, 0xEB, 0x0C,
            0x44, 0x23, 0x51, 0x08,
            0xC3,
        ];

        Assert.False(GuestRedZonePatcher.TryBuildEnclosingSpan(
            code, Base, Base + 6, out _, out _, out _, out _));
    }

    [Fact]
    public void AllowsNeighborRelocationWhenFunctionOnlyHasAnIndirectCall()
    {
        // call rax has a statically known continuation and cannot jump into an
        // undiscovered boundary in this function. It must not make an otherwise
        // safe enclosing span look like an unresolved indirect jump table.
        byte[] code =
        [
            0xFF, 0xD0,
            0x49, 0xC1, 0xEB, 0x0C,
            0x44, 0x23, 0x51, 0x08,
            0xC3,
        ];

        Assert.True(GuestRedZonePatcher.TryBuildEnclosingSpan(
            code, Base, Base + 6, out var address, out var length, out var coreStart, out var coreCount));

        Assert.Equal(Base + 2, address);
        Assert.Equal(8, length);
        Assert.Equal(1, coreStart);
        Assert.Equal(1, coreCount);
    }

    [Fact]
    public void BuildsASpanOverRegisterOnlyShaInstructions()
    {
        // sha256msg1 xmm1, xmm2 -> 4 bytes, no memory operand: the rewrite site
        // sha256msg2 xmm1, xmm2 -> 4 bytes, taken to reach the 5-byte jump
        // ret
        byte[] code =
        [
            0x0F, 0x38, 0xCC, 0xCA,
            0x0F, 0x38, 0xCD, 0xCA,
            0xC3,
        ];

        // A span with no guest memory access has nothing to keep outside the RSP shift, so
        // the whole span is the core. Refusing it left SHA instructions unrewritten.
        Assert.True(GuestRedZonePatcher.TryBuildForwardSpan(
            code, Base, Base, out var length, out var coreStart, out var coreCount));

        Assert.Equal(8, length);
        Assert.Equal(0, coreStart);
        Assert.Equal(2, coreCount);
    }
}
