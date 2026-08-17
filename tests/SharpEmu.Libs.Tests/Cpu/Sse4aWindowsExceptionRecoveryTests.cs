// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Iced.Intel;
using SharpEmu.Core.Cpu.Emulation;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed unsafe class Sse4aWindowsExceptionRecoveryTests
{
    private const int Win64ContextRipOffset = 0xF8;
    private const int Win64ContextXmm0Offset = 0x1A0;

    private static readonly MethodInfo TryRecoverAmdCompat = typeof(DirectExecutionBackend).GetMethod(
        "TryRecoverAmdCompatInstruction",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void RegisterExtrqRecoversAstroEncoding()
    {
        AssertRegisterExtrqRecovery(
            [0x66, 0x0F, 0x79, 0xD5],
            Register.XMM2,
            Register.XMM5,
            destinationIndex: 2,
            controlIndex: 5,
            length: 16,
            index: 8);
    }

    [Fact]
    public void RegisterExtrqRecoversAstroExtendedRegisterEncoding()
    {
        AssertRegisterExtrqRecovery(
            [0x66, 0x41, 0x0F, 0x79, 0xE0],
            Register.XMM4,
            Register.XMM8,
            destinationIndex: 4,
            controlIndex: 8,
            length: 16,
            index: 8);
    }

    [Fact]
    public void RegisterExtrqRecoversAstroUndefinedRangeEncoding()
    {
        AssertRegisterExtrqRecovery(
            [0x66, 0x44, 0x0F, 0x79, 0xF7],
            Register.XMM14,
            Register.XMM7,
            destinationIndex: 14,
            controlIndex: 7,
            length: 8,
            index: 60);
    }

    private static void AssertRegisterExtrqRecovery(
        byte[] instructionBytes,
        Register expectedDestination,
        Register expectedControl,
        int destinationIndex,
        int controlIndex,
        int length,
        int index)
    {
        if (!OperatingSystem.IsWindows() ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var decoder = Decoder.Create(64, new ByteArrayCodeReader(instructionBytes));
        decoder.Decode(out var instruction);
        Assert.Equal(Mnemonic.Extrq, instruction.Mnemonic);
        Assert.Equal(2, instruction.OpCount);
        Assert.Equal(expectedDestination, instruction.Op0Register);
        Assert.Equal(expectedControl, instruction.Op1Register);

        var code = AllocateProbeVisibleCode(instructionBytes);
        try
        {
            const ulong value = 0x1234_5678_9ABC_DEF0UL;
            var contextRecord = stackalloc byte[0x4D0];
            *(ulong*)(contextRecord + Win64ContextRipOffset) = (ulong)code;
            *(ulong*)(contextRecord + XmmOffset(destinationIndex)) = value;
            *(ulong*)(contextRecord + XmmOffset(destinationIndex) + 8) = ulong.MaxValue;
            *(ulong*)(contextRecord + XmmOffset(controlIndex)) = ((ulong)index << 8) | (uint)length;
            var backend = RuntimeHelpers.GetUninitializedObject(typeof(DirectExecutionBackend));

            var recovered = (bool)TryRecoverAmdCompat.Invoke(
                backend,
                [Pointer.Box(contextRecord, typeof(void*)), (ulong)code])!;

            Assert.True(recovered);
            Assert.Equal(
                Sse4aBitFieldEmulator.ExtractBitField(value, length, index),
                *(ulong*)(contextRecord + XmmOffset(destinationIndex)));
            Assert.Equal(0UL, *(ulong*)(contextRecord + XmmOffset(destinationIndex) + 8));
            Assert.Equal(
                (ulong)code + (ulong)instructionBytes.Length,
                *(ulong*)(contextRecord + Win64ContextRipOffset));
        }
        finally
        {
            Assert.True(HostMemory.Free((void*)code, 0, HostMemory.MEM_RELEASE));
        }
    }

    private static int XmmOffset(int register) => Win64ContextXmm0Offset + register * 16;

    private static nint AllocateProbeVisibleCode(ReadOnlySpan<byte> instructions)
    {
        var size = checked((nuint)Environment.SystemPageSize);
        var mapping = (nint)HostMemory.Alloc(
            null,
            size,
            HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE,
            HostMemory.PAGE_READWRITE);
        Assert.NotEqual((nint)0, mapping);

        instructions.CopyTo(new Span<byte>((void*)mapping, checked((int)size)));
        return mapping;
    }
}
