// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestThreadBlockWaiterRepresentationTests
{
    [Fact]
    public void SchedulerStoresOnlyTheWaiterObjectRepresentation()
    {
        var stateType = typeof(DirectExecutionBackend).GetNestedType(
            "GuestThreadState",
            BindingFlags.NonPublic);
        Assert.NotNull(stateType);

        var waiterProperty = stateType.GetProperty(
            "BlockWaiter",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(waiterProperty);
        Assert.Equal(typeof(IGuestThreadBlockWaiter), waiterProperty.PropertyType);
        Assert.Null(stateType.GetProperty(
            "BlockResumeHandler",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(stateType.GetProperty(
            "BlockWakeHandler",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

        var registerMethods = typeof(DirectExecutionBackend)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.Name == "RegisterBlockedGuestThreadContinuation")
            .ToArray();
        var registerMethod = Assert.Single(registerMethods);
        Assert.Contains(
            registerMethod.GetParameters(),
            parameter => parameter.ParameterType == typeof(IGuestThreadBlockWaiter));
        Assert.DoesNotContain(
            registerMethod.GetParameters(),
            parameter => IsFuncParameter(parameter.ParameterType));

        var consumeMethods = typeof(GuestThreadExecution)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Where(method => method.Name == nameof(GuestThreadExecution.TryConsumeCurrentThreadBlock));
        Assert.DoesNotContain(
            consumeMethods.SelectMany(method => method.GetParameters()),
            parameter => IsFuncParameter(parameter.ParameterType));
    }

    [Fact]
    public void DelegateCompatibilityBridgeIsConsumedAsOneWaiterObject()
    {
        var previousThread = GuestThreadExecution.EnterGuestThread(0x1234);
        try
        {
            var canWake = false;
            var wakeCalls = 0;
            var resumeCalls = 0;
            Assert.True(GuestThreadExecution.RequestCurrentThreadBlock(
                context: null,
                reason: "test_wait",
                wakeKey: "test_waiter:1",
                resumeHandler: () =>
                {
                    resumeCalls++;
                    return 42;
                },
                wakeHandler: () =>
                {
                    wakeCalls++;
                    return canWake;
                }));

            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out var hasContinuation,
                out var wakeKey,
                out IGuestThreadBlockWaiter? waiter,
                out var deadline));
            Assert.Equal("test_wait", reason);
            Assert.Equal("test_waiter:1", wakeKey);
            Assert.False(hasContinuation);
            Assert.Equal(0, deadline);
            Assert.NotNull(waiter);

            Assert.False(waiter.TryWake());
            canWake = true;
            Assert.True(waiter.TryWake());
            Assert.Equal(42, waiter.Resume());
            Assert.Equal(2, wakeCalls);
            Assert.Equal(1, resumeCalls);
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }

    private static bool IsFuncParameter(Type parameterType)
    {
        var type = parameterType.IsByRef
            ? parameterType.GetElementType()
            : parameterType;
        return type is not null &&
            type.IsGenericType &&
            type.GetGenericTypeDefinition() == typeof(Func<>);
    }
}
