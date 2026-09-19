// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Emulation;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>
/// Coverage for the register forms of SSE4a EXTRQ (66 0F 79 /r) and INSERTQ (F2 0F 79 /r), which
/// take their bit-field length and index from a source register instead of from immediates.
///
/// Each test fabricates the state the OS hands the #UD handler - a Win64 CONTEXT record carrying
/// the XMM registers, plus a RIP pointing at real instruction bytes in probe-visible host memory -
/// and drives the production entry point (TryRecoverAmdCompatInstruction) over it, so the decode,
/// the bit math and the CONTEXT write-back are all exercised exactly as they are on a live fault.
/// </summary>
public sealed unsafe class Sse4aRegisterFormRecoveryTests
{
    private const int Win64ContextSize = 0x4D0;
    private const int Win64ContextXmm0Offset = 0x1A0;
    private const int CtxRip = 248;

    private static readonly MethodInfo TryRecoverAmdCompat = typeof(DirectExecutionBackend).GetMethod(
        "TryRecoverAmdCompatInstruction",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo XmmBridgedFlag = typeof(DirectExecutionBackend).GetField(
        "_posixXmmContextBridged",
        BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>
    /// Packs a length/index pair the way a guest register carries it for the register forms:
    /// length in bits [5:0] of the controlling quadword, index in bits [13:8].
    /// </summary>
    private static ulong Control(int length, int index) =>
        (ulong)(uint)(length & 0x3F) | ((ulong)(uint)(index & 0x3F) << 8);

    [Fact]
    public void ExtrqRegisterForm_TakesControlFromTheSourceLowQuadwordAndExtractsFromTheDestination()
    {
        if (!IsSupportedHost)
        {
            return;
        }

        // extrq xmm1, xmm2
        using var frame = new FaultFrame([0x66, 0x0F, 0x79, 0xCA]);
        const ulong data = 0x1234_5678_9ABC_DEF0UL;
        frame.SetXmmLow(1, data);
        frame.SetXmmLow(2, Control(length: 0x10, index: 0x08));

        Assert.True(frame.Recover());

        Assert.Equal(
            Sse4aBitFieldEmulator.ExtractBitField(data, length: 0x10, index: 0x08),
            frame.XmmLow(1));
        Assert.Equal(0UL, frame.XmmHigh(1));
        Assert.Equal(frame.CodeAddress + 4, frame.Rip);
    }

    [Fact]
    public void InsertqRegisterForm_TakesControlFromTheSourceHighQuadword()
    {
        if (!IsSupportedHost)
        {
            return;
        }

        // insertq xmm1, xmm2
        using var frame = new FaultFrame([0xF2, 0x0F, 0x79, 0xCA]);
        const ulong destination = 0x1111_2222_3333_4444UL;
        const ulong source = 0xAAAA_BBBB_CCCC_DDDDUL;
        frame.SetXmmLow(1, destination);
        frame.SetXmmLow(2, source);
        frame.SetXmmHigh(2, Control(length: 0x10, index: 0x08));

        Assert.True(frame.Recover());

        Assert.Equal(
            Sse4aBitFieldEmulator.InsertBitField(destination, source, length: 0x10, index: 0x08),
            frame.XmmLow(1));
        Assert.Equal(0UL, frame.XmmHigh(1));
        Assert.Equal(frame.CodeAddress + 4, frame.Rip);
    }

    /// <summary>
    /// The asymmetry that makes these two forms easy to get wrong: EXTRQ reads its control fields
    /// out of the low quadword of the source, INSERTQ out of the high one. Here the source's low
    /// quadword also happens to look like a (different) control pair, so reading the wrong half
    /// produces a different, detectably wrong result rather than silently agreeing.
    /// </summary>
    [Fact]
    public void InsertqRegisterForm_IgnoresAControlPairSittingInTheSourceLowQuadword()
    {
        if (!IsSupportedHost)
        {
            return;
        }

        // insertq xmm1, xmm2
        using var frame = new FaultFrame([0xF2, 0x0F, 0x79, 0xCA]);
        const ulong destination = 0xFFFF_FFFF_FFFF_FFFFUL;
        var source = 0xAAAA_BBBB_CCCC_0000UL | Control(length: 0x04, index: 0x20);
        frame.SetXmmLow(1, destination);
        frame.SetXmmLow(2, source);
        frame.SetXmmHigh(2, Control(length: 0x10, index: 0x08));

        Assert.True(frame.Recover());

        Assert.Equal(
            Sse4aBitFieldEmulator.InsertBitField(destination, source, length: 0x10, index: 0x08),
            frame.XmmLow(1));
        Assert.NotEqual(
            Sse4aBitFieldEmulator.InsertBitField(destination, source, length: 0x04, index: 0x20),
            frame.XmmLow(1));
    }

    /// <summary>
    /// Nothing stops a compiler emitting the register form against a single register, and then the
    /// control fields and the data being extracted share one quadword. The recovery has to read
    /// the destination before it writes it for that to come out right.
    /// </summary>
    [Fact]
    public void ExtrqRegisterForm_HandlesTheSameRegisterAsBothSourceAndDestination()
    {
        if (!IsSupportedHost)
        {
            return;
        }

        // extrq xmm1, xmm1
        using var frame = new FaultFrame([0x66, 0x0F, 0x79, 0xC9]);
        var value = 0xDEAD_BEEF_0000_0000UL | Control(length: 0x20, index: 0x00);
        frame.SetXmmLow(1, value);

        Assert.True(frame.Recover());

        Assert.Equal(
            Sse4aBitFieldEmulator.ExtractBitField(value, length: 0x20, index: 0x00),
            frame.XmmLow(1));
        Assert.Equal(frame.CodeAddress + 4, frame.Rip);
    }

    /// <summary>
    /// AMD leaves the result undefined once index + length runs past the quadword. The recovery
    /// declines instead of inventing one, and must not have partially written the CONTEXT on the
    /// way to that decision - the fault then reaches the existing diagnostics unchanged.
    /// </summary>
    [Fact]
    public void RegisterForm_DeclinesAndLeavesTheContextUntouchedWhenTheFieldOverrunsTheQuadword()
    {
        if (!IsSupportedHost)
        {
            return;
        }

        // extrq xmm1, xmm2
        using var frame = new FaultFrame([0x66, 0x0F, 0x79, 0xCA]);
        const ulong data = 0x1234_5678_9ABC_DEF0UL;
        const ulong untouchedHigh = 0x5555_5555_5555_5555UL;
        frame.SetXmmLow(1, data);
        frame.SetXmmHigh(1, untouchedHigh);
        frame.SetXmmLow(2, Control(length: 0x30, index: 0x30));

        Assert.False(frame.Recover());

        Assert.Equal(data, frame.XmmLow(1));
        Assert.Equal(untouchedHigh, frame.XmmHigh(1));
        Assert.Equal(0UL, frame.Rip);
    }

    [Fact]
    public void ImmediateExtrqStillRecoversAfterTheRegisterFormsWereAdded()
    {
        if (!IsSupportedHost)
        {
            return;
        }

        // extrq xmm0, 0x10, 0x08
        using var frame = new FaultFrame([0x66, 0x0F, 0x78, 0xC0, 0x10, 0x08]);
        const ulong data = 0x1234_5678_9ABC_DEF0UL;
        frame.SetXmmLow(0, data);

        Assert.True(frame.Recover());

        Assert.Equal(
            Sse4aBitFieldEmulator.ExtractBitField(data, length: 0x10, index: 0x08),
            frame.XmmLow(0));
        Assert.Equal(frame.CodeAddress + 6, frame.Rip);
    }

    [Fact]
    public void ImmediateInsertqStillRecoversAfterTheRegisterFormsWereAdded()
    {
        if (!IsSupportedHost)
        {
            return;
        }

        // insertq xmm0, xmm1, 0x10, 0x08
        using var frame = new FaultFrame([0xF2, 0x0F, 0x78, 0xC1, 0x10, 0x08]);
        const ulong destination = 0x1111_2222_3333_4444UL;
        const ulong source = 0xAAAA_BBBB_CCCC_DDDDUL;
        frame.SetXmmLow(0, destination);
        frame.SetXmmLow(1, source);

        Assert.True(frame.Recover());

        Assert.Equal(
            Sse4aBitFieldEmulator.InsertBitField(destination, source, length: 0x10, index: 0x08),
            frame.XmmLow(0));
        Assert.Equal(frame.CodeAddress + 6, frame.Rip);
    }

    /// <summary>
    /// The recovery reads and writes guest XMM state, so it only runs where the host CPU actually
    /// has those registers in the CONTEXT this test fabricates.
    /// </summary>
    private static bool IsSupportedHost =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>
    /// A fabricated #UD fault: a Win64 CONTEXT record plus a page of probe-visible host memory
    /// holding the faulting instruction.
    /// </summary>
    private sealed class FaultFrame : IDisposable
    {
        private readonly byte[] _context = new byte[Win64ContextSize];
        private readonly nint _code;

        public FaultFrame(ReadOnlySpan<byte> instruction)
        {
            // The instruction bytes must live where the fault-time page probe can see them, which
            // is HostMemory's region table - the allocator guest code pages come from. A pinned
            // managed array would be invisible and the recovery would decline before decoding.
            var size = checked((nuint)Environment.SystemPageSize);
            _code = (nint)HostMemory.Alloc(
                null,
                size,
                HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE,
                HostMemory.PAGE_READWRITE);
            Assert.NotEqual((nint)0, _code);
            instruction.CopyTo(new Span<byte>((void*)_code, checked((int)size)));
        }

        public ulong CodeAddress => (ulong)_code;

        public ulong Rip
        {
            get
            {
                fixed (byte* context = _context)
                {
                    return *(ulong*)(context + CtxRip);
                }
            }
        }

        public void SetXmmLow(int register, ulong value) => WriteXmm(register, 0, value);

        public void SetXmmHigh(int register, ulong value) => WriteXmm(register, 8, value);

        public ulong XmmLow(int register) => ReadXmm(register, 0);

        public ulong XmmHigh(int register) => ReadXmm(register, 8);

        public bool Recover()
        {
            // On POSIX hosts the recovery is gated on the signal bridge having copied real XMM
            // state into the CONTEXT. This frame supplies that state directly, so assert the same
            // precondition the bridge would have established.
            XmmBridgedFlag.SetValue(null, true);
            var backend = RuntimeHelpers.GetUninitializedObject(typeof(DirectExecutionBackend));
            fixed (byte* context = _context)
            {
                return (bool)TryRecoverAmdCompat.Invoke(
                    backend,
                    [Pointer.Box(context, typeof(void*)), (ulong)_code])!;
            }
        }

        public void Dispose() => Assert.True(HostMemory.Free((void*)_code, 0, HostMemory.MEM_RELEASE));

        private void WriteXmm(int register, int half, ulong value)
        {
            fixed (byte* context = _context)
            {
                *(ulong*)(context + Win64ContextXmm0Offset + (16 * register) + half) = value;
            }
        }

        private ulong ReadXmm(int register, int half)
        {
            fixed (byte* context = _context)
            {
                return *(ulong*)(context + Win64ContextXmm0Offset + (16 * register) + half);
            }
        }
    }
}
