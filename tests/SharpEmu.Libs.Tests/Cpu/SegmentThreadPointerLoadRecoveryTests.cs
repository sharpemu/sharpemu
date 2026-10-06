// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// #789: the ahead-of-time TLS load patcher has never patched a single load in any
// submitted log, so guest "mov reg, fs:[0]" instructions reach the CPU
// unrewritten. With the host's FS base at 0 that reads address 0 and faults.
// God of War, Minecraft and EA UFC 5 all die on the byte-identical instruction.
//
// RIP points at the segment prefix; the three 0x66 operand-size prefixes are
// LLVM's TLS general-dynamic relaxation padding and sit *before* it.
//
// The CONTEXT write itself needs a real backend and a real TLS index, so what is
// pinned here is the decode: that the reported encoding is recognised as a load
// of the thread pointer, and that near misses are refused rather than silently
// "recovered" into wrong register state.
public sealed class SegmentThreadPointerLoadRecoveryTests
{
    private static byte[] Opcode(params int[] bytes) => bytes.Select(b => (byte)b).ToArray();

    // "66 66 66 64 48 8B 04 25 00 00 00 00" - from RIP (the 0x64) onwards this is
    // "64 48 8B 04 25 00 00 00 00", i.e. mov rax, fs:[0].
    private static readonly byte[] MovRaxFsZero =
        Opcode(0x64, 0x48, 0x8B, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00);

    // The same encoding targeting rcx: ModRM mod=00 reg=001 rm=100 -> 0x0C.
    private static readonly byte[] MovRcxFsZero =
        Opcode(0x64, 0x48, 0x8B, 0x0C, 0x25, 0x00, 0x00, 0x00, 0x00);

    // EA UFC 5, and the GS variant of the same instruction.
    private static readonly byte[] MovRaxGsZero =
        Opcode(0x65, 0x48, 0x8B, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00);

    [Fact]
    public void DecodesTheEncodingReportedInEveryFailingLog()
    {
        Assert.True(DirectExecutionBackend.TryDecodeSegmentThreadPointerLoad(
            MovRaxFsZero, out var destination));

        Assert.Equal(0, destination); // rax
    }

    [Fact]
    public void DecodesEveryDestinationRegisterFromTheReportedLogs()
    {
        Assert.True(DirectExecutionBackend.TryDecodeSegmentThreadPointerLoad(
            MovRcxFsZero, out var destination));

        Assert.Equal(1, destination); // rcx
    }

    [Fact]
    public void DecodesTheGsFormToo()
    {
        Assert.True(DirectExecutionBackend.TryDecodeSegmentThreadPointerLoad(
            MovRaxGsZero, out var destination));

        Assert.Equal(0, destination);
    }

    [Fact]
    public void DecodesExtendedRegistersViaTheRexRBit()
    {
        // REX.W+R = 0x4C sets REX.R, which extends ModRM.reg; ModRM.reg 0 selects r8.
        var opcode = Opcode(0x64, 0x4C, 0x8B, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00);

        Assert.True(DirectExecutionBackend.TryDecodeSegmentThreadPointerLoad(opcode, out var destination));

        Assert.Equal(8, destination); // r8
    }

    [Theory]
    // Not a segment override at all.
    [InlineData(0x48, 0x48, 0x8B, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00)]
    // GS/FS with a non-zero displacement: a different TLS access, not the
    // thread pointer, and the patcher does not model it.
    [InlineData(0x64, 0x48, 0x8B, 0x04, 0x25, 0x30, 0x00, 0x00, 0x00)]
    // Not a MOV load (0x89 is MOV r/m, r).
    [InlineData(0x64, 0x48, 0x89, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00)]
    // REX without W: not a 64-bit operand, so not this instruction.
    [InlineData(0x64, 0x40, 0x8B, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00)]
    // REX.W+R+X = 0x4F: X is not part of this form.
    [InlineData(0x64, 0x4F, 0x8B, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00)]
    // mod != 00, so this is not the absolute disp32 form.
    [InlineData(0x64, 0x48, 0x8B, 0x44, 0x25, 0x00, 0x00, 0x00, 0x00)]
    // rm != 100, so no SIB byte follows and the layout is different.
    [InlineData(0x64, 0x48, 0x8B, 0x05, 0x25, 0x00, 0x00, 0x00, 0x00)]
    // SIB base is "none" rather than rbp/disp32.
    [InlineData(0x64, 0x48, 0x8B, 0x04, 0x24, 0x00, 0x00, 0x00, 0x00)]
    // Destination RSP has no ModRM.reg encoding in this form.
    [InlineData(0x64, 0x48, 0x8B, 0x24, 0x25, 0x00, 0x00, 0x00, 0x00)]
    public void RefusesEverythingThatIsNotTheThreadPointerLoad(params int[] bytes)
    {
        Assert.False(DirectExecutionBackend.TryDecodeSegmentThreadPointerLoad(
            Opcode(bytes), out var destination));

        Assert.Equal(0, destination);
    }

    [Fact]
    public void RefusesATruncatedRead()
    {
        // A short buffer cannot contain the full 9-byte instruction.
        Assert.False(DirectExecutionBackend.TryDecodeSegmentThreadPointerLoad(
            MovRaxFsZero.Take(5).ToArray(), out _));
    }
}
