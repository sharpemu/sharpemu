// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
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
    public void MacOsDiagnosticReadsRespectNativePageProtection()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;

        var memory = HostPlatform.Current.Memory;
        var size = (ulong)Environment.SystemPageSize;
        var address = memory.Allocate(0, size, HostPageProtection.ReadWrite);
        Assert.NotEqual(0UL, address);
        try
        {
            const long expected = 0x1122334455667788;
            Marshal.WriteInt64((nint)address, expected);
            var bytes = new byte[8];
            Assert.True((bool)TryReadHostBytes.Invoke(null, [address, bytes])!);
            Assert.Equal(BitConverter.GetBytes(expected), bytes);

            Assert.True(memory.Protect(address, size, HostPageProtection.NoAccess, out _));
            Assert.False((bool)TryReadHostBytes.Invoke(null, [address, bytes])!, "byte read accepted a protected page");
            object?[] arguments = [address, 0UL];
            Assert.False((bool)TryReadDiagnosticHostQword.Invoke(null, arguments)!, "qword read accepted a protected page");
            Assert.Equal(0UL, arguments[1]);
        }
        finally
        {
            Assert.True(memory.Free(address));
        }
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

    [Theory]
    [InlineData(HostMemory.PAGE_NOACCESS)]
    [InlineData(HostMemory.PAGE_READWRITE | 0x100u)]
    public unsafe void WindowsDiagnosticReadsRejectAnUnreadableTrailingPage(uint protection)
    {
        if (!OperatingSystem.IsWindows()) return;
        var pageSize = (nuint)Environment.SystemPageSize;
        var memory = (byte*)HostMemory.Alloc(null, pageSize * 2, 0x3000, HostMemory.PAGE_READWRITE);
        Assert.NotEqual(0, (nint)memory);
        try
        {
            var address = (ulong)(memory + pageSize - 4);
            var destination = new byte[16];
            Assert.True((bool)TryReadHostBytes.Invoke(null, [address, destination])!);
            Assert.True(HostMemory.Protect(memory + pageSize, pageSize, protection, out _));
            Assert.False((bool)TryReadHostBytes.Invoke(null, [address, destination])!);
            object?[] arguments = [address, 0UL];
            Assert.False((bool)TryReadDiagnosticHostQword.Invoke(null, arguments)!);
            Assert.Equal(0UL, arguments[1]);
            Assert.NotEqual((nuint)0, HostMemory.Query(memory + pageSize, out var region));
            Assert.Equal(protection, region.Protect);
        }
        finally
        {
            Assert.True(HostMemory.Free(memory, 0, HostMemory.MEM_RELEASE));
        }
    }

    [Fact]
    public unsafe void LinuxDiagnosticReadsRejectAnUnreadableTrailingPage()
    {
        if (!OperatingSystem.IsLinux()) return;
        var pageSize = (nuint)Environment.SystemPageSize;
        var memory = (byte*)HostMemory.Alloc(null, pageSize * 2, 0x3000, HostMemory.PAGE_READWRITE);
        Assert.NotEqual(0, (nint)memory);
        try
        {
            var address = (ulong)(memory + pageSize - 4);
            Assert.True((bool)TryReadHostBytes.Invoke(null, [address, new byte[16]])!);
            Assert.True(HostMemory.Protect(memory + pageSize, pageSize, HostMemory.PAGE_NOACCESS, out _));
            Assert.False((bool)TryReadHostBytes.Invoke(null, [address, new byte[16]])!);
            object?[] arguments = [address, 0UL];
            Assert.False((bool)TryReadDiagnosticHostQword.Invoke(null, arguments)!);
            Assert.Equal(0UL, arguments[1]);
        }
        finally
        {
            Assert.True(HostMemory.Free(memory, 0, HostMemory.MEM_RELEASE));
        }
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(0xFFF0UL)]
    [InlineData(0x0000_7FFF_FFFF_FFF8UL)]
    [InlineData(ulong.MaxValue - 7)]
    public void DiagnosticReadsRejectInvalidRanges(ulong address)
    {
        Assert.False((bool)TryReadHostBytes.Invoke(null, [address, new byte[16]])!);
        object?[] arguments = [address, 0UL];
        Assert.False((bool)TryReadDiagnosticHostQword.Invoke(null, arguments)!);
        Assert.Equal(0UL, arguments[1]);
    }
}
