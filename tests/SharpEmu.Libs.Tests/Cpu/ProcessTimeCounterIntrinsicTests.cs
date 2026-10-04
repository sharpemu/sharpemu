// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class ProcessTimeCounterIntrinsicTests
{
    private const string ProcessTimeCounterNid = "fgxnMeTNUtY";

    [Fact]
    public unsafe void WindowsIntrinsic_CallsNativeMonotonicCounter()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        using var backend = new DirectExecutionBackend(new ModuleManager());
        var method = typeof(DirectExecutionBackend).GetMethod(
            "TryCreateNativeImportIntrinsic",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        object?[] arguments = [ProcessTimeCounterNid, null];
        Assert.True((bool)method.Invoke(backend, arguments)!);
        var address = (nint)arguments[1]!;
        Assert.NotEqual(0, address);
        Assert.Equal(
            new byte[] { 0x48, 0x83, 0xEC, 0x28, 0x48, 0x8D, 0x4C, 0x24, 0x20, 0x48, 0xB8 },
            new ReadOnlySpan<byte>((void*)address, 11).ToArray());
        Assert.Equal(
            new byte[] { 0xFF, 0xD0, 0x48, 0x8B, 0x44, 0x24, 0x20, 0x48, 0x83, 0xC4, 0x28, 0xC3 },
            new ReadOnlySpan<byte>((void*)(address + 19), 12).ToArray());

        var counterAddressField = typeof(DirectExecutionBackend).GetField(
            "_queryPerformanceCounterAddress",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(counterAddressField);
        var counterAddress = (nint)counterAddressField.GetValue(backend)!;
        Assert.NotEqual(0, counterAddress);
        Assert.Equal(counterAddress, *(nint*)((byte*)address + 11));
        Assert.Null(typeof(DirectExecutionBackend).GetMethod(
            "ReadHostTimestamp",
            BindingFlags.Static | BindingFlags.NonPublic));

        var readCounter = (delegate* unmanaged<ulong>)address;
        var first = readCounter();
        Thread.Sleep(20);
        var second = readCounter();

        Assert.True(second > first);
        Assert.InRange(
            second - first,
            unchecked((ulong)Math.Max(1L, Stopwatch.Frequency / 1_000L)),
            unchecked((ulong)Stopwatch.Frequency));
    }
}
