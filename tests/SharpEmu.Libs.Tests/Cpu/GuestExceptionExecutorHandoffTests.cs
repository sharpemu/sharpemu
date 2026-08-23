// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestExceptionExecutorHandoffTests
{
    [Fact]
    public void BlockedThreadTakesPendingExceptionWhenExecutorReleasesIt()
    {
        var moduleManager = new ModuleManager();
        moduleManager.Freeze();
        using var backend = new DirectExecutionBackend(moduleManager);

        var backendType = typeof(DirectExecutionBackend);
        var threadType = backendType.GetNestedType(
            "GuestThreadState",
            BindingFlags.NonPublic);
        var runStateType = backendType.GetNestedType(
            "GuestThreadRunState",
            BindingFlags.NonPublic);
        var pendingType = backendType.GetNestedType(
            "PendingGuestException",
            BindingFlags.NonPublic);
        Assert.NotNull(threadType);
        Assert.NotNull(runStateType);
        Assert.NotNull(pendingType);

        const ulong threadHandle = 0x1234;
        const ulong handler = 0x5678;
        const int exceptionType = 0x1E;
        const ulong exceptionStack = 0x9000;

        var thread = Activator.CreateInstance(threadType);
        Assert.NotNull(thread);
        threadType.GetProperty("ThreadHandle")!.SetValue(thread, threadHandle);
        threadType.GetProperty("State")!.SetValue(
            thread,
            Enum.Parse(runStateType, "Blocked"));
        threadType.GetProperty("ExecutorActive")!.SetValue(thread, true);
        threadType.GetProperty("ExceptionDeliveryActive")!.SetValue(thread, false);

        var pending = Activator.CreateInstance(
            pendingType,
            handler,
            exceptionType,
            exceptionStack);
        Assert.NotNull(pending);
        var pendingDictionary = Assert.IsAssignableFrom<IDictionary>(
            backendType.GetField(
                "_pendingGuestExceptions",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend));
        pendingDictionary.Add(threadHandle, pending);

        var handoff = backendType.GetMethod(
            "TryReleaseGuestThreadExecutorLocked",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handoff);
        object?[] arguments = [thread, null];

        Assert.True(Assert.IsType<bool>(handoff.Invoke(backend, arguments)));
        Assert.False(Assert.IsType<bool>(
            threadType.GetProperty("ExecutorActive")!.GetValue(thread)));
        Assert.Empty(pendingDictionary);
        Assert.NotNull(arguments[1]);
        Assert.Equal(handler, pendingType.GetProperty("Handler")!.GetValue(arguments[1]));
        Assert.Equal(exceptionType, pendingType.GetProperty("ExceptionType")!.GetValue(arguments[1]));
        Assert.Equal(exceptionStack, pendingType.GetProperty("ExceptionStackBase")!.GetValue(arguments[1]));
    }
}
