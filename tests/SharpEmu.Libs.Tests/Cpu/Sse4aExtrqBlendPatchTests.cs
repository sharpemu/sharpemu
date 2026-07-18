// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class Sse4aExtrqBlendPatchTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public unsafe void Patches_ExtrqBlend_ForAnySourceRegister(byte xmmRegister)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var modRm = (byte)(0xC0 | xmmRegister);
        byte[] code =
        [
            0x66, 0x0F, 0x78, modRm, 0x28, 0x00, // extrq xmmN, 0x28, 0
            0xC4, 0xE3, 0x79, 0x02, modRm, 0x02, // vpblendd xmm0, xmm0, xmmN, 2
        ];

        var patched = InvokeTryPatch(code, out var result);

        Assert.True(patched);
        var expectedPextrbModRm = (byte)(0xC0 | (xmmRegister << 3));
        Assert.Equal(
        [
            0x66, 0x0F, 0x3A, 0x14, expectedPextrbModRm, 0x04, // pextrb eax, xmmN, 4
            0x66, 0x0F, 0x3A, 0x22, 0xC0, 0x01,                // pinsrd xmm0, eax, 1
        ],
            result);
    }

    [Fact]
    public unsafe void DoesNotPatch_WhenExtrqAndVpblenddReferenceDifferentRegisters()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        byte[] code =
        [
            0x66, 0x0F, 0x78, 0xC1, 0x28, 0x00, // extrq xmm1, 0x28, 0
            0xC4, 0xE3, 0x79, 0x02, 0xC2, 0x02, // vpblendd xmm0, xmm0, xmm2, 2 (mismatched)
        ];

        var patched = InvokeTryPatch(code, out var unchanged);

        Assert.False(patched);
        Assert.Equal(code, unchanged);
    }

    [Fact]
    public unsafe void DoesNotPatch_UnrelatedByteSequence()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        byte[] code = new byte[12];
        Array.Fill(code, (byte)0x90); // nop sled

        var patched = InvokeTryPatch(code, out var unchanged);

        Assert.False(patched);
        Assert.Equal(code, unchanged);
    }

    private static unsafe bool InvokeTryPatch(byte[] code, out byte[] resultBytes)
    {
        var backend = (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(
            typeof(DirectExecutionBackend));
        var method = typeof(DirectExecutionBackend).GetMethod(
            "TryPatchSse4aExtrqBlend",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var size = (nuint)code.Length;
        var buffer = HostMemory.Alloc(
            null,
            size,
            HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE,
            HostMemory.PAGE_EXECUTE_READWRITE);
        Assert.True(buffer != null);

        try
        {
            var source = (byte*)buffer;
            new ReadOnlySpan<byte>(code).CopyTo(new Span<byte>(source, code.Length));

            var patched = (bool)method.Invoke(backend, [(nint)source, (nint)source])!;
            resultBytes = new ReadOnlySpan<byte>(source, code.Length).ToArray();
            return patched;
        }
        finally
        {
            HostMemory.Free(buffer, size, HostMemory.MEM_RELEASE);
        }
    }
}
