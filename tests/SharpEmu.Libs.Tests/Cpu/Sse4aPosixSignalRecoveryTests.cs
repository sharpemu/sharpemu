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

// Test instruction recovery with Linux and macOS signal frames.
// Confirm that recovery reads and updates the vector register values.
public sealed unsafe class Sse4aPosixSignalRecoveryTests
{
    private const int PosixSigIll = 4;
    private const int LinuxUcontextGregsOffset = 40;
    private const int LinuxGregsRipOffset = 16 * 8;
    private const int LinuxGregsFpstateOffset = 184;
    private const int FxsaveXmm0Offset = 160;
    private const int FxsaveXmm1Offset = 176;

    private static readonly MethodInfo TryHandlePosixFault = typeof(DirectExecutionBackend).GetMethod(
        "TryHandlePosixFault",
        BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly FieldInfo PosixSignalBackend = typeof(DirectExecutionBackend).GetField(
        "_posixSignalBackend",
        BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly FieldInfo EmulatedCounter = typeof(DirectExecutionBackend).GetField(
        "_sse4aInstructionsEmulated",
        BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly FieldInfo XmmBridgedFlag = typeof(DirectExecutionBackend).GetField(
        "_posixXmmContextBridged",
        BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly MethodInfo TryRecoverAmdCompat = typeof(DirectExecutionBackend).GetMethod(
        "TryRecoverAmdCompatInstruction",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData(184UL, false)]
    [InlineData(607UL, false)]
    [InlineData(608UL, true)]
    [InlineData(712UL, true)]
    [InlineData(1032UL, true)]
    public void MacOsSignalRequiresCompleteVectorRegisters(ulong contextSize, bool expected)
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        var getVectorRegisterAddress = typeof(DirectExecutionBackend).GetMethod(
            "GetSignalVectorRegisterAddress", BindingFlags.Static | BindingFlags.NonPublic)!;
        byte* userContext = stackalloc byte[64];
        byte* machineContext = stackalloc byte[1032];
        *(ulong*)(userContext + 40) = contextSize;

        var result = Pointer.Unbox(getVectorRegisterAddress.Invoke(null,
            [(nint)userContext, Pointer.Box(machineContext, typeof(byte*))])!);

        Assert.Equal(expected ? (nint)(machineContext + 352) : 0, (nint)result);
    }

    [Fact]
    public void ExtrqSigillRoundTripsXmmThroughTheBridge()
    {
        if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        // extrq xmm0, 0x10, 0x08
        var code = AllocateProbeVisibleCode([0x66, 0x0F, 0x78, 0xC0, 0x10, 0x08]);
        try
        {
            const ulong value = 0x1234_5678_9ABC_DEF0UL;
            var frame = new FakeSignalFrame((ulong)code);
            frame.SetXmmLow(FxsaveXmm0Offset, value);
            var emulatedBefore = (long)EmulatedCounter.GetValue(null)!;

            Assert.True(frame.Dispatch());

            Assert.Equal(
                Sse4aBitFieldEmulator.ExtractBitField(value, length: 0x10, index: 0x08),
                frame.XmmLow(FxsaveXmm0Offset));
            Assert.Equal(0UL, frame.XmmHigh(FxsaveXmm0Offset));
            Assert.Equal((ulong)code + 6, frame.Rip);
            Assert.True((long)EmulatedCounter.GetValue(null)! > emulatedBefore);
        }
        finally
        {
            FreeProbeVisibleCode(code);
        }
    }

    [Fact]
    public void InsertqSigillReadsSourceXmmThroughTheBridge()
    {
        if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        // insertq xmm0, xmm1, 0x10, 0x08
        var code = AllocateProbeVisibleCode([0xF2, 0x0F, 0x78, 0xC1, 0x10, 0x08]);
        try
        {
            const ulong destination = 0x1111_2222_3333_4444UL;
            const ulong source = 0xAAAA_BBBB_CCCC_DDDDUL;
            var frame = new FakeSignalFrame((ulong)code);
            frame.SetXmmLow(FxsaveXmm0Offset, destination);
            frame.SetXmmLow(FxsaveXmm1Offset, source);

            Assert.True(frame.Dispatch());

            Assert.Equal(
                Sse4aBitFieldEmulator.InsertBitField(destination, source, length: 0x10, index: 0x08),
                frame.XmmLow(FxsaveXmm0Offset));
            Assert.Equal((ulong)code + 6, frame.Rip);
        }
        finally
        {
            FreeProbeVisibleCode(code);
        }
    }

    [Fact]
    public void InsertInstructionPreservesOtherVectorRegisters()
    {
        if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        // Use the same register for both INSERTQ operands.
        // Keep all other vector register values unchanged.
        var code = AllocateProbeVisibleCode([0xF2, 0x0F, 0x78, 0xC0, 0x08, 0x08]);
        try
        {
            var frame = new FakeSignalFrame((ulong)code);
            frame.SetXmmLow(FxsaveXmm0Offset, 0x10);
            frame.SetXmmLow(FxsaveXmm0Offset + 8, ulong.MaxValue);
            for (int registerIndex = 1; registerIndex < 16; registerIndex++)
            {
                frame.SetXmmLow(FxsaveXmm0Offset + registerIndex * 16, (ulong)registerIndex);
                frame.SetXmmLow(FxsaveXmm0Offset + registerIndex * 16 + 8, ~(ulong)registerIndex);
            }
            frame.FillMacOsExtendedVectorState(0xA5);

            Assert.True(frame.Dispatch());

            Assert.Equal(0x1010UL, frame.XmmLow(FxsaveXmm0Offset));
            Assert.Equal(0UL, frame.XmmHigh(FxsaveXmm0Offset));
            Assert.Equal((ulong)code + 6, frame.Rip);
            for (int registerIndex = 1; registerIndex < 16; registerIndex++)
            {
                Assert.Equal((ulong)registerIndex, frame.XmmLow(FxsaveXmm0Offset + registerIndex * 16));
                Assert.Equal(~(ulong)registerIndex, frame.XmmHigh(FxsaveXmm0Offset + registerIndex * 16));
            }
            frame.AssertMacOsExtendedVectorStateEquals(0xA5);
        }
        finally
        {
            FreeProbeVisibleCode(code);
        }
    }

    [Fact]
    public void RecoveryDeclinesWhenNoXmmStateWasBridged()
    {
        if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        // Clear the register state flag to represent a signal frame without vector registers.
        // Recovery must reject this frame.
        var code = AllocateProbeVisibleCode([0x66, 0x0F, 0x78, 0xC0, 0x10, 0x08]);
        try
        {
            XmmBridgedFlag.SetValue(null, false);
            var backend = RuntimeHelpers.GetUninitializedObject(typeof(DirectExecutionBackend));
            var contextRecord = stackalloc byte[0x4D0];

            var recovered = (bool)TryRecoverAmdCompat.Invoke(
                backend,
                [Pointer.Box(contextRecord, typeof(void*)), (ulong)code])!;

            Assert.False(recovered);
        }
        finally
        {
            FreeProbeVisibleCode(code);
        }
    }

    // Build the signal frame that the host system gives to the signal handler.
    // Use the register offsets for Linux or macOS.
    private sealed class FakeSignalFrame
    {
        private readonly byte[] _userContext = new byte[512];
        private readonly byte[] _floatingPointState = new byte[512];
        private readonly byte[] _machineContext = new byte[1032];
        private readonly bool _includeFloatingPointState;

        public FakeSignalFrame(ulong instructionPointer, bool includeFloatingPointState = true)
        {
            _includeFloatingPointState = includeFloatingPointState;
            fixed (byte* userContext = _userContext)
            fixed (byte* machineContext = _machineContext)
            {
                if (OperatingSystem.IsMacOS())
                    *(ulong*)(machineContext + 144) = instructionPointer;
                else
                    *(ulong*)(userContext + LinuxUcontextGregsOffset + LinuxGregsRipOffset) = instructionPointer;
            }
        }

        public ulong Rip
        {
            get
            {
                fixed (byte* userContext = _userContext)
                fixed (byte* machineContext = _machineContext)
                {
                    return OperatingSystem.IsMacOS()
                        ? *(ulong*)(machineContext + 144)
                        : *(ulong*)(userContext + LinuxUcontextGregsOffset + LinuxGregsRipOffset);
                }
            }
        }

        public void SetXmmLow(int fxsaveOffset, ulong value)
        {
            fixed (byte* state = OperatingSystem.IsMacOS() ? _machineContext : _floatingPointState)
            {
                var offset = OperatingSystem.IsMacOS() ? 352 + fxsaveOffset - FxsaveXmm0Offset : fxsaveOffset;
                *(ulong*)(state + offset) = value;
            }
        }

        public ulong XmmLow(int fxsaveOffset)
        {
            fixed (byte* state = OperatingSystem.IsMacOS() ? _machineContext : _floatingPointState)
            {
                var offset = OperatingSystem.IsMacOS() ? 352 + fxsaveOffset - FxsaveXmm0Offset : fxsaveOffset;
                return *(ulong*)(state + offset);
            }
        }

        public ulong XmmHigh(int fxsaveOffset)
        {
            return XmmLow(fxsaveOffset + 8);
        }

        public void FillMacOsExtendedVectorState(byte value) => _machineContext.AsSpan(772, 256).Fill(value);

        public void AssertMacOsExtendedVectorStateEquals(byte value)
        {
            foreach (var actual in _machineContext.AsSpan(772, 256))
                Assert.Equal(value, actual);
        }

        public bool Dispatch()
        {
            EnsureBridgeBackend();
            fixed (byte* userContext = _userContext)
            fixed (byte* floatingPointState = _floatingPointState)
            fixed (byte* machineContext = _machineContext)
            {
                if (OperatingSystem.IsMacOS())
                {
                    *(byte**)(userContext + 48) = machineContext;
                    *(ulong*)(userContext + 40) = _includeFloatingPointState ? (ulong)_machineContext.Length : 184;
                }
                else if (_includeFloatingPointState)
                {
                    *(byte**)(userContext + LinuxUcontextGregsOffset + LinuxGregsFpstateOffset) = floatingPointState;
                }

                return (bool)TryHandlePosixFault.Invoke(
                    null,
                    [PosixSigIll, (nint)0, (nint)userContext])!;
            }
        }
    }

    /// <summary>
    /// TryHandlePosixFault only runs the recovery chain when a backend instance is
    /// registered. The tests do not need any of the constructor's state (and must not run
    /// it: it installs process-wide signal handlers), so register an uninitialized
    /// instance - the SIGILL recovery path only touches static state.
    /// </summary>
    private static void EnsureBridgeBackend()
    {
        if (PosixSignalBackend.GetValue(null) == null)
        {
            PosixSignalBackend.SetValue(
                null,
                RuntimeHelpers.GetUninitializedObject(typeof(DirectExecutionBackend)));
        }
    }

    // Use the host allocator so Linux memory probes can read the instruction bytes.
    // macOS reads these bytes through Mach.
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

    private static void FreeProbeVisibleCode(nint mapping)
    {
        Assert.True(HostMemory.Free((void*)mapping, 0, HostMemory.MEM_RELEASE));
    }
}
