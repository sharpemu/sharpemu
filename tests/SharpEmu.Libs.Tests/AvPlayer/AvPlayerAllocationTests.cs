// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using SharpEmu.Libs.Tests.Kernel;
using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class AvPlayerAllocationTests : IDisposable
{
    private const ulong Handle = 0xA0_0000_1000;
    private readonly IGuestThreadScheduler? _previousScheduler = GuestThreadExecution.Scheduler;

    [Fact]
    public void FailedGuestAllocatorsFallBackToHleMemoryInTheSameAttempt()
    {
        using var memory = new PhysicalVirtualMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        var scheduler = new FailingAllocatorScheduler();
        GuestThreadExecution.Scheduler = scheduler;
        AvPlayerExports.RegisterPlayerForTest(
            Handle,
            width: 16,
            height: 16,
            durationMilliseconds: 1,
            allocateTextureCallback: 0x1000,
            allocateCallback: 0x2000);

        Assert.True(AvPlayerExports.AllocateGuestVideoBuffersForTest(
            context,
            Handle,
            out var firstBuffer));
        Assert.NotEqual(0UL, firstBuffer);
        Assert.Equal(2, scheduler.CallCount);
    }

    [Fact]
    public void ReplacementFileCallbacksMaterializeOffsetReadsAndCleanUp()
    {
        using var memory = new PhysicalVirtualMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        var payload = new byte[(1024 * 1024) + 257];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = unchecked((byte)(index * 37));
        }

        var scheduler = new ReplacementFileScheduler(payload);
        GuestThreadExecution.Scheduler = scheduler;
        AvPlayerExports.RegisterPlayerForTest(
            Handle,
            width: 16,
            height: 16,
            durationMilliseconds: 1,
            fileObject: 0xCAFE,
            fileOpenCallback: ReplacementFileScheduler.OpenCallback,
            fileCloseCallback: ReplacementFileScheduler.CloseCallback,
            fileReadOffsetCallback: ReplacementFileScheduler.ReadCallback,
            fileSizeCallback: ReplacementFileScheduler.SizeCallback);

        Assert.True(AvPlayerExports.MaterializeReplacementSourceForTest(
            context,
            Handle,
            "archive:/packed.resource",
            out var path));
        Assert.Equal("archive:/packed.resource", scheduler.OpenedPath);
        Assert.Equal(payload, File.ReadAllBytes(path));
        Assert.Equal(2, scheduler.ReadCount);
        Assert.Equal(1, scheduler.CloseCount);

        AvPlayerExports.RemovePlayerForTest(Handle);
        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        AvPlayerExports.RemovePlayerForTest(Handle);
        GuestThreadExecution.Scheduler = _previousScheduler;
    }

    private sealed class FailingAllocatorScheduler : IGuestThreadScheduler
    {
        public int CallCount { get; private set; }

        public bool SupportsGuestContextTransfer => false;

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
        }

        public bool TryStartThread(
            CpuContext creatorContext,
            GuestThreadStartRequest request,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryJoinThread(
            CpuContext callerContext,
            ulong threadHandle,
            out ulong returnValue,
            out string? error)
        {
            returnValue = 0;
            error = "not supported";
            return false;
        }

        public void Pump(CpuContext callerContext, string reason)
        {
        }

        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;

        public bool HasPendingGuestExceptionForCurrentThread() => false;

        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => false;

        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => false;

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => [];

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            CallCount++;
            returnValue = 0;
            error = "allocator rejected the request";
            return false;
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong arg3,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error) =>
            TryCallGuestFunction(
                callerContext,
                entryPoint,
                arg0,
                arg1,
                arg2,
                stackAddress,
                stackSize,
                reason,
                out returnValue,
                out error);

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryRaiseGuestException(
            CpuContext callerContext,
            ulong threadHandle,
            ulong handler,
            int exceptionType,
            out string? error)
        {
            error = "not supported";
            return false;
        }
    }

    private sealed class ReplacementFileScheduler(byte[] payload) : IGuestThreadScheduler
    {
        public const ulong OpenCallback = 0x10_0000;
        public const ulong CloseCallback = 0x10_0100;
        public const ulong ReadCallback = 0x10_0200;
        public const ulong SizeCallback = 0x10_0300;

        public string? OpenedPath { get; private set; }
        public int ReadCount { get; private set; }
        public int CloseCount { get; private set; }
        public bool SupportsGuestContextTransfer => false;

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
        }

        public bool TryStartThread(
            CpuContext creatorContext,
            GuestThreadStartRequest request,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryJoinThread(
            CpuContext callerContext,
            ulong threadHandle,
            out ulong returnValue,
            out string? error)
        {
            returnValue = 0;
            error = "not supported";
            return false;
        }

        public void Pump(CpuContext callerContext, string reason)
        {
        }

        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;

        public bool HasPendingGuestExceptionForCurrentThread() => false;

        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => false;

        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => false;

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => [];

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error)
        {
            error = "unsupported callback shape";
            return false;
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error) =>
            TryCallGuestFunction(
                callerContext,
                entryPoint,
                arg0,
                arg1,
                arg2,
                0,
                stackAddress,
                stackSize,
                reason,
                out returnValue,
                out error);

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong arg3,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            error = null;
            switch (entryPoint)
            {
                case OpenCallback:
                    OpenedPath = ReadGuestString(callerContext, arg1);
                    returnValue = 0;
                    return true;
                case CloseCallback:
                    CloseCount++;
                    returnValue = 0;
                    return true;
                case SizeCallback:
                    returnValue = checked((ulong)payload.Length);
                    return true;
                case ReadCallback:
                    var position = checked((int)arg2);
                    if (position < 0 || position >= payload.Length)
                    {
                        returnValue = 0;
                        return true;
                    }

                    var count = Math.Min(checked((int)arg3), payload.Length - position);
                    if (count <= 0 ||
                        !callerContext.Memory.TryWrite(arg1, payload.AsSpan(position, count)))
                    {
                        returnValue = unchecked((ulong)-1L);
                        return true;
                    }

                    ReadCount++;
                    returnValue = checked((ulong)count);
                    return true;
                default:
                    returnValue = 0;
                    error = "unknown callback";
                    return false;
            }
        }

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryRaiseGuestException(
            CpuContext callerContext,
            ulong threadHandle,
            ulong handler,
            int exceptionType,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        private static string ReadGuestString(CpuContext context, ulong address)
        {
            var bytes = new List<byte>();
            Span<byte> value = stackalloc byte[1];
            while (context.Memory.TryRead(address++, value) && value[0] != 0)
            {
                bytes.Add(value[0]);
            }

            return Encoding.UTF8.GetString(bytes.ToArray());
        }
    }
}
