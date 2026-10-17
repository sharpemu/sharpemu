// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestThreadStackBoundsTests
{
    [NativeX64Fact]
    public void SchedulerReturnsAuthoritativeBoundsAcrossTheFullSlotWindow()
    {
        const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        const ulong stackSize = 0x20_0000;
        const ulong stackStride = 0x100_0000;
        var highestStack = OperatingSystem.IsWindows()
            ? 0x0000_7FFF_E000_0000UL
            : 0x0000_6FFF_E000_0000UL;
        var backendType = typeof(DirectExecutionBackend);
        var threadType = backendType.GetNestedType("GuestThreadState", BindingFlags.NonPublic)!;
        var modules = new ModuleManager();
        modules.Freeze();
        using var backend = new DirectExecutionBackend(modules);
        var gate = backendType.GetField("_guestThreadGate", privateInstance)!.GetValue(backend)!;
        var threads = (IDictionary)backendType.GetField("_guestThreads", privateInstance)!.GetValue(backend)!;

        foreach (var slot in new[] { 0, 48, 56, 1023 })
        {
            var handle = 0x1000UL + (ulong)slot;
            var expectedBase = highestStack - ((ulong)slot * stackStride);
            var thread = Activator.CreateInstance(threadType)!;
            threadType.GetProperty("ThreadHandle")!.SetValue(thread, handle);
            threadType.GetProperty("StackBase")!.SetValue(thread, expectedBase);
            threadType.GetProperty("StackSize")!.SetValue(thread, stackSize);
            lock (gate)
                threads.Add(handle, thread);

            Assert.True(backend.TryGetGuestThreadStackBounds(handle, out var actualBase, out var actualSize));
            Assert.Equal(expectedBase, actualBase);
            Assert.Equal(stackSize, actualSize);
        }

        Assert.False(backend.TryGetGuestThreadStackBounds(0xDEAD, out var missingBase, out var missingSize));
        Assert.Equal(0UL, missingBase);
        Assert.Equal(0UL, missingSize);
    }
}
