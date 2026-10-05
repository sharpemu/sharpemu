// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class WindowsExceptionTrampolineTests
{
    private static bool CanRun => OperatingSystem.IsWindows() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ExceptionCallback(nint exceptionPointers);

    [Fact]
    public async Task ConcurrentCallbacksCanEnterWhileAnotherCallbackWaits()
    {
        if (!CanRun) return;

        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var callbackCount = 0;
        using var trampoline = new TestTrampoline(_ =>
        {
            if (Interlocked.Increment(ref callbackCount) == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait();
            }
            else
            {
                secondEntered.Set();
            }

            return -1;
        });

        Task<int>? firstCallback = null;
        Task<int>? secondCallback = null;
        try
        {
            firstCallback = StartCallback(trampoline.Address);
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)), "The first callback did not enter.");
            secondCallback = StartCallback(trampoline.Address);
            Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(5)),
                "The second callback was blocked by the first callback.");
        }
        finally
        {
            // Release the first callback before joining either thread, including a failed assertion.
            releaseFirst.Set();
            if (firstCallback is not null) await firstCallback;
            if (secondCallback is not null) await secondCallback;
        }

        Assert.Equal(-1, await firstCallback!);
        Assert.Equal(-1, await secondCallback!);
        Assert.Equal(2, callbackCount);
    }

    [Theory]
    [InlineData(0xE0434352u)]
    [InlineData(0xE06D7363u)]
    [InlineData(0xC00000FDu)]
    [InlineData(0x40010006u)]
    [InlineData(0x4001000Au)]
    [InlineData(0xC0000005u)]
    public void HostExceptionsBypassManagedCallback(uint exceptionCode)
    {
        if (!CanRun) return;

        var callbackEntered = false;
        using var trampoline = new TestTrampoline(_ =>
        {
            callbackEntered = true;
            return -1;
        });

        Assert.Equal(0, InvokeCallback(trampoline.Address, exceptionCode));
        Assert.False(callbackEntered);
    }

    [Fact]
    public unsafe void CallbackPreservesNonvolatileRegistersAndStackBounds()
    {
        if (!CanRun) return;

        using var trampoline = new TestTrampoline(_ =>
        {
            GC.KeepAlive(new byte[256]);
            return -1;
        });
        var probe = CreateRegisterProbe(trampoline.Address);
        try
        {
            Span<ulong> observed = stackalloc ulong[14];
            Span<byte> exceptionRecord = stackalloc byte[0xA0];
            Span<byte> contextRecord = stackalloc byte[0x4D0];
            exceptionRecord.Clear();
            contextRecord.Clear();
            fixed (byte* recordAddress = exceptionRecord)
            fixed (byte* contextAddress = contextRecord)
            fixed (ulong* observedAddress = observed)
            {
                *(uint*)recordAddress = 0xC000001D;
                nint* exceptionPointers = stackalloc nint[2];
                exceptionPointers[0] = (nint)recordAddress;
                exceptionPointers[1] = (nint)contextAddress;
                var result = ((delegate* unmanaged<nint, nint, int>)probe)(
                    (nint)exceptionPointers, (nint)observedAddress);
                Assert.Equal(-1, result);
            }

            for (var registerIndex = 0; registerIndex < 8; registerIndex++)
            {
                Assert.Equal(0x1111111111111111UL * (ulong)(registerIndex + 1), observed[registerIndex]);
            }

            Assert.Equal(observed[8], observed[11]);
            Assert.Equal(observed[9], observed[12]);
            Assert.Equal(observed[10], observed[13]);
        }
        finally
        {
            Assert.True(HostMemory.Free((void*)probe, 0, HostMemory.MEM_RELEASE));
        }
    }

    private static Task<int> StartCallback(nint trampoline) => Task.Factory.StartNew(
        () => InvokeCallback(trampoline, 0xC000001D), CancellationToken.None,
        TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static unsafe int InvokeCallback(nint trampoline, uint exceptionCode)
    {
        byte* exceptionRecord = stackalloc byte[0xA0];
        byte* contextRecord = stackalloc byte[0x4D0];
        new Span<byte>(exceptionRecord, 0xA0).Clear();
        new Span<byte>(contextRecord, 0x4D0).Clear();
        *(uint*)exceptionRecord = exceptionCode;
        nint* exceptionPointers = stackalloc nint[2];
        exceptionPointers[0] = (nint)exceptionRecord;
        exceptionPointers[1] = (nint)contextRecord;
        return ((delegate* unmanaged<nint, int>)trampoline)((nint)exceptionPointers);
    }

    private static unsafe nint CreateRegisterProbe(nint trampoline)
    {
        var instructions = new List<byte>();
        void WriteImmediate(ulong value) => instructions.AddRange(BitConverter.GetBytes(value));

        // Save the caller's registers and reserve aligned call space plus two local values.
        instructions.AddRange([0x53, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57]);
        instructions.AddRange([0x48, 0x83, 0xEC, 0x38, 0x48, 0x89, 0x54, 0x24, 0x30]);
        ReadOnlySpan<byte> registerCodes = [3, 5, 6, 7, 12, 13, 14, 15];
        for (var registerIndex = 0; registerIndex < registerCodes.Length; registerIndex++)
        {
            var registerCode = registerCodes[registerIndex];
            instructions.Add(registerCode < 8 ? (byte)0x48 : (byte)0x49);
            instructions.Add((byte)(0xB8 + (registerCode & 7)));
            WriteImmediate(0x1111111111111111UL * (ulong)(registerIndex + 1));
        }

        // Record RSP and both thread stack bounds before and after the callback.
        instructions.AddRange([0x48, 0x89, 0x62, 0x40]);
        instructions.AddRange([0x65, 0x48, 0x8B, 0x04, 0x25, 8, 0, 0, 0, 0x48, 0x89, 0x42, 0x48]);
        instructions.AddRange([0x65, 0x48, 0x8B, 0x04, 0x25, 16, 0, 0, 0, 0x48, 0x89, 0x42, 0x50]);
        instructions.AddRange([0x48, 0xB8]);
        WriteImmediate((ulong)trampoline);
        instructions.AddRange([0xFF, 0xD0, 0x89, 0x44, 0x24, 0x28, 0x48, 0x8B, 0x44, 0x24, 0x30]);
        for (var registerIndex = 0; registerIndex < registerCodes.Length; registerIndex++)
        {
            var registerCode = registerCodes[registerIndex];
            instructions.Add(registerCode < 8 ? (byte)0x48 : (byte)0x4C);
            instructions.Add(0x89);
            instructions.Add((byte)(0x40 | ((registerCode & 7) << 3)));
            instructions.Add((byte)(registerIndex * sizeof(ulong)));
        }

        instructions.AddRange([0x48, 0x89, 0x60, 0x58]);
        instructions.AddRange([0x65, 0x48, 0x8B, 0x14, 0x25, 8, 0, 0, 0, 0x48, 0x89, 0x50, 0x60]);
        instructions.AddRange([0x65, 0x48, 0x8B, 0x14, 0x25, 16, 0, 0, 0, 0x48, 0x89, 0x50, 0x68]);
        instructions.AddRange([0x8B, 0x44, 0x24, 0x28, 0x48, 0x83, 0xC4, 0x38,
            0x41, 0x5F, 0x41, 0x5E, 0x41, 0x5D, 0x41, 0x5C, 0x5F, 0x5E, 0x5D, 0x5B, 0xC3]);
        var address = (byte*)HostMemory.Alloc(null, (nuint)instructions.Count,
            HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE, HostMemory.PAGE_EXECUTE_READWRITE);
        Assert.NotEqual(0, (nint)address);
        instructions.ToArray().CopyTo(new Span<byte>(address, instructions.Count));
        return (nint)address;
    }

    private sealed class TestTrampoline : IDisposable
    {
        private readonly DirectExecutionBackend _backend;
        private readonly ExceptionCallback _callback;
        public nint Address { get; }

        public TestTrampoline(ExceptionCallback callback)
        {
            _callback = callback;
            var modules = new ModuleManager();
            modules.Freeze();
            _backend = new DirectExecutionBackend(modules);
            try
            {
                var createTrampoline = typeof(DirectExecutionBackend).GetMethod(
                    "CreateExceptionHandlerTrampoline", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(createTrampoline);
                Address = (nint)createTrampoline.Invoke(_backend,
                    [Marshal.GetFunctionPointerForDelegate(callback)])!;
                Assert.NotEqual(0, Address);
            }
            catch
            {
                _backend.Dispose();
                throw;
            }
        }

        public unsafe void Dispose()
        {
            try
            {
                Assert.True(HostMemory.Free((void*)Address, 0, HostMemory.MEM_RELEASE));
            }
            finally
            {
                GC.KeepAlive(_callback);
                _backend.Dispose();
            }
        }
    }
}
