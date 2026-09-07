// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed unsafe class WindowsFaultFilterTests
{
    [Theory]
    [InlineData(0x40010006u)]
    [InlineData(0x4001000Au)]
    public void RuntimeFilterReturnsBeforeManagedCallbackForDebugOutput(uint code)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        var modules = new ModuleManager();
        modules.Freeze();
        using var backend = new DirectExecutionBackend(modules);
        var create = typeof(DirectExecutionBackend).GetMethod(
            "CreateExceptionHandlerTrampoline", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var thunk = (nint)create.Invoke(backend, [(nint)0])!;
        Assert.NotEqual(0, thunk);
        try
        {
            nint* pointers = stackalloc nint[2];
            pointers[0] = (nint)(&code);
            pointers[1] = 0;
            Assert.Equal(0, ((delegate* unmanaged<nint, int>)thunk)((nint)pointers));
        }
        finally
        {
            HostPlatform.Current.Memory.Free((ulong)thunk);
        }
    }

    [Theory]
    [InlineData(0x40010006u)]
    [InlineData(0x4001000Au)]
    public void HostFilterReturnsBeforeManagedCallbackForDebugOutput(uint code)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        var type = typeof(DirectExecutionBackend).Assembly.GetType(
            "SharpEmu.Core.Cpu.Native.Windows.WindowsFaultHandling", throwOnError: true)!;
        var handler = (IHostFaultHandling)Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [HostPlatform.Current.Memory], null)!;
        // A null callback makes the test fail if the filter calls managed code.
        var thunk = handler.CreateHandlerThunk(0, uint.MaxValue, 0);
        Assert.NotEqual(0, thunk);
        try
        {
            nint* pointers = stackalloc nint[2];
            pointers[0] = (nint)(&code);
            pointers[1] = 0;
            var result = ((delegate* unmanaged<nint, int>)thunk)((nint)pointers);
            Assert.Equal(0, result);
        }
        finally
        {
            handler.FreeThunk(thunk);
        }
    }
}
