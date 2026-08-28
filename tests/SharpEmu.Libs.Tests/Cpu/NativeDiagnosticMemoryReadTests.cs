// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class NativeDiagnosticMemoryReadTests
{
    private static readonly MethodInfo TryReadDiagnosticHostQword =
        typeof(DirectExecutionBackend).GetMethod(
            "TryReadDiagnosticHostQword",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo TryReadHostBytes =
        typeof(DirectExecutionBackend).GetMethod(
            "TryReadHostBytes",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void DiagnosticReadsRejectNonCanonicalAddresses()
    {
        object?[] qwordArguments = [0x3120_2C30_202C_3120UL, 0UL];
        Assert.False((bool)TryReadDiagnosticHostQword.Invoke(null, qwordArguments)!);

        var destination = new byte[16];
        Assert.False((bool)TryReadHostBytes.Invoke(
            null,
            [0x3120_2C30_202C_3120UL, destination])!);
    }

    [Fact]
    public void DiagnosticReadsCopyCommittedMemory()
    {
        var source = Marshal.AllocHGlobal(16);
        try
        {
            const long expected = 0x1122_3344_5566_7788;
            Marshal.WriteInt64(source, expected);

            object?[] qwordArguments = [(ulong)source, 0UL];
            Assert.True((bool)TryReadDiagnosticHostQword.Invoke(null, qwordArguments)!);
            Assert.Equal((ulong)expected, qwordArguments[1]);

            var destination = new byte[8];
            Assert.True((bool)TryReadHostBytes.Invoke(
                null,
                [(ulong)source, destination])!);
            Assert.Equal(BitConverter.GetBytes(expected), destination);
        }
        finally
        {
            Marshal.FreeHGlobal(source);
        }
    }
}
