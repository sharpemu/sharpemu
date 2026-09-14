// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class WindowsThreadContextCaptureTests
{
    private const string WorkerVariable = "SHARPEMU_THREAD_CONTEXT_TEST_WORKER";
    private static bool CanRun => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    [Theory]
    [InlineData(uint.MaxValue, 1u, 1u, false, 0x1)]
    [InlineData(0u, 0u, 1u, false, 0x123)]
    [InlineData(0u, 1u, 1u, true, 0x123)]
    [InlineData(2u, 1u, 3u, true, 0x123)]
    [InlineData(0u, 1u, uint.MaxValue, false, 0x123)]
    public unsafe void NativeSequence_BalancesSuccessfulSuspension(
        uint suspendResult, uint contextResult, uint resumeResult, bool expectedResult, int expectedCalls)
    {
        if (!CanRun) return;
        using var suspend = new NativeCode(BuildRecordingFunction(1, suspendResult));
        using var capture = new NativeCode(BuildRecordingFunction(2, contextResult));
        using var resume = new NativeCode(BuildRecordingFunction(3, resumeResult));
        using var sequence = new NativeCode(DirectExecutionBackend.BuildThreadContextCaptureCode(
            suspend.Address, capture.Address, resume.Address));
        int* recorded = stackalloc int[2];
        recorded[0] = 0;
        recorded[1] = 1;

        var result = ((delegate* unmanaged<nint, nint, int>)sequence.Address)((nint)recorded, 0);

        Assert.Equal(expectedResult, result != 0);
        Assert.Equal(expectedCalls, recorded[0]);
        Assert.Equal(1, recorded[1]);
    }

    [Fact]
    public unsafe void ContextDecoder_ReadsEveryIntegerRegister()
    {
        var context = stackalloc byte[0x4D0];
        new Span<byte>(context, 0x4D0).Clear();
        var fields = new (string Name, int Offset)[]
        {
            ("Rax", 120), ("Rcx", 128), ("Rdx", 136), ("Rbx", 144), ("Rsp", 152),
            ("Rbp", 160), ("Rsi", 168), ("Rdi", 176), ("R8", 184), ("R9", 192),
            ("R10", 200), ("R11", 208), ("R12", 216), ("R13", 224), ("R14", 232), ("R15", 240), ("Rip", 248)
        };
        foreach (var field in fields) *(ulong*)(context + field.Offset) = (ulong)field.Offset * 17;
        var decoder = FindMethod("DecodeHostThreadContext");
        var snapshot = decoder.Invoke(null, [123u, Pointer.Box(context, typeof(void*))]);
        Assert.NotNull(snapshot);
        Assert.Equal(123u, ReadProperty<uint>(snapshot, "ThreadId"));
        foreach (var field in fields)
            Assert.Equal((ulong)field.Offset * 17, ReadProperty<ulong>(snapshot, field.Name));
    }

    [Fact]
    public unsafe void NativeCaptureCode_ReleasesItsAllocation()
    {
        if (!CanRun) return;
        var codeType = typeof(DirectExecutionBackend).GetNestedType("ThreadContextCaptureCode", BindingFlags.NonPublic)!;
        using var code = Assert.IsAssignableFrom<SafeHandle>(Activator.CreateInstance(codeType));
        Assert.False(code.IsInvalid);
        var address = code.DangerousGetHandle();
        Assert.NotEqual((nuint)0, HostMemory.Query((void*)address, out var before));
        Assert.Equal(HostMemory.PAGE_EXECUTE_READ, before.Protect);
        code.Dispose();
        Assert.NotEqual((nuint)0, HostMemory.Query((void*)address, out var after));
        Assert.Equal(HostMemory.MEM_FREE_STATE, after.State);
    }

    [Fact]
    public async Task NativeCapture_ResumesThreadsAndReleasesResources()
    {
        if (!CanRun) return;
        if (GuestFaultWorker.IsWorker(WorkerVariable))
        {
            RunNativeCases();
            GuestFaultWorker.Report(GuestFaultWorker.Completed, "native context and cleanup checks passed");
            return;
        }
        await GuestFaultWorker.RunIsolatedAsync(WorkerVariable, typeof(WindowsThreadContextCaptureTests),
            nameof(NativeCapture_ResumesThreadsAndReleasesResources));
    }

    private static void RunNativeCases()
    {
        using var worker = new NativeWorker();
        Assert.False(TryCapture(null, out _));
        using (var self = new SafeWaitHandle(OpenThread(0x080A, false, GetCurrentThreadId()), true))
            Assert.False(TryCapture(self, out _));
        using (var missing = new SafeWaitHandle(0, false))
            Assert.False(TryCapture(missing, out _));

        Assert.True(TryCapture(worker.Handle, out var captured));
        Assert.Equal(worker.ThreadId, ReadProperty<uint>(captured!, "ThreadId"));
        Assert.NotEqual(0UL, ReadProperty<ulong>(captured!, "Rip"));
        Assert.NotEqual(0UL, ReadProperty<ulong>(captured!, "Rsp"));
        Assert.Equal(0u, ResumeThread(worker.Handle.DangerousGetHandle()));

        Assert.Equal(0u, SuspendThread(worker.Handle.DangerousGetHandle()));
        try
        {
            Assert.True(TryCapture(worker.Handle, out _));
        }
        finally
        {
            Assert.Equal(1u, ResumeThread(worker.Handle.DangerousGetHandle()));
        }

        // Context access is denied after suspension; the native sequence must still resume.
        using (var restricted = new SafeWaitHandle(OpenThread(0x0802, false, worker.ThreadId), true))
        {
            Assert.False(restricted.IsInvalid);
            Assert.False(TryCapture(restricted, out _));
            Assert.Equal(0u, ResumeThread(worker.Handle.DangerousGetHandle()));
        }

        var borrowed = new SafeWaitHandle(OpenThread(0x080A, false, worker.ThreadId), true);
        var borrowedAddress = borrowed.DangerousGetHandle();
        Assert.True(TryCapture(borrowed, out _));
        borrowed.Dispose();
        Assert.False(TryCapture(borrowed, out _));
        Assert.Equal(0u, GetThreadId(borrowedAddress));

        VerifyMainThreadReport(worker.Handle);
        VerifyManagedAllocationStress();
        worker.Stop();
        Assert.False(TryCapture(worker.Handle, out _));
    }

    private static void VerifyMainThreadReport(SafeWaitHandle threadHandle)
    {
        var backend = (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(typeof(DirectExecutionBackend));
        var field = typeof(DirectExecutionBackend).GetField("_mainExecutionThreadHandle",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(backend, threadHandle);
        var log = typeof(DirectExecutionBackend).GetMethod("LogMainThreadStallContext",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var originalError = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetError(output);
            log.Invoke(backend, null);
            Assert.Contains("Live hardware context", output.ToString());
            Assert.Contains("R15=0x", output.ToString());
            Assert.Contains("Live stack pointer candidates", output.ToString());
            output.GetStringBuilder().Clear();
            field.SetValue(backend, null);
            log.Invoke(backend, null);
            Assert.Equal(string.Empty, output.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            field.SetValue(backend, null);
        }
    }

    private static void VerifyManagedAllocationStress()
    {
        using var ready = new ManualResetEventSlim();
        var stop = 0;
        uint threadId = 0;
        var thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            ready.Set();
            while (Volatile.Read(ref stop) == 0) GC.KeepAlive(new byte[4096]);
        }) { IsBackground = true };
        thread.Start();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            using var handle = new SafeWaitHandle(OpenThread(0x080A, false, threadId), true);
            for (var sampleIndex = 0; sampleIndex < 50; sampleIndex++)
            {
                Assert.True(TryCapture(handle, out _));
                Assert.Equal(0u, ResumeThread(handle.DangerousGetHandle()));
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    private static bool TryCapture(SafeWaitHandle? handle, out object? snapshot)
    {
        object?[] arguments = [handle, null];
        var result = Assert.IsType<bool>(FindMethod("TryCaptureThreadContext").Invoke(null, arguments));
        snapshot = arguments[1];
        return result;
    }

    private static MethodInfo FindMethod(string methodName) => typeof(DirectExecutionBackend).GetMethod(
        methodName, BindingFlags.NonPublic | BindingFlags.Static)!;

    private static TProperty ReadProperty<TProperty>(object value, string propertyName) =>
        Assert.IsType<TProperty>(value.GetType().GetProperty(propertyName)!.GetValue(value));

    private static byte[] BuildRecordingFunction(byte step, uint result)
    {
        // Check call alignment and record the call order without entering managed code.
        return [0x48, 0x89, 0xE0, 0x83, 0xE0, 0x0F, 0x83, 0xF8, 0x08,
            0x0F, 0x94, 0xC0, 0x0F, 0xB6, 0xC0, 0x21, 0x41, 0x04,
            0xC1, 0x21, 0x04, 0x83, 0x09, step, 0xB8, .. BitConverter.GetBytes(result), 0xC3];
    }

    private sealed unsafe class NativeCode : IDisposable
    {
        public nint Address { get; }

        public NativeCode(byte[] instructions)
        {
            Address = (nint)HostMemory.Alloc(null, (nuint)instructions.Length, 0x3000, HostMemory.PAGE_READWRITE);
            Assert.NotEqual(0, Address);
            instructions.CopyTo(new Span<byte>((void*)Address, instructions.Length));
            Assert.True(HostMemory.Protect((void*)Address, (nuint)instructions.Length, HostMemory.PAGE_EXECUTE_READ, out _));
            HostMemory.FlushInstructionCache((void*)Address, (nuint)instructions.Length);
        }

        public void Dispose() => Assert.True(HostMemory.Free((void*)Address, 0, HostMemory.MEM_RELEASE));
    }

    private sealed unsafe class NativeWorker : IDisposable
    {
        private readonly NativeCode _code = new([0xC7, 0x01, 1, 0, 0, 0,
            0xFF, 0x41, 0x08, 0xF3, 0x90, 0x83, 0x79, 0x04, 0, 0x74, 0xF5, 0x31, 0xC0, 0xC3]);
        private readonly nint _state = (nint)NativeMemory.AllocZeroed(16);
        public SafeWaitHandle Handle { get; }
        public uint ThreadId { get; }

        public NativeWorker()
        {
            Handle = new SafeWaitHandle(CreateThread(0, 0, _code.Address, _state, 0, out var threadId), true);
            ThreadId = threadId;
            Assert.False(Handle.IsInvalid);
            Assert.True(SpinWait.SpinUntil(() => Marshal.ReadInt32(_state) == 1, TimeSpan.FromSeconds(5)));
        }

        public void Stop()
        {
            Marshal.WriteInt32(_state + 4, 1);
            Assert.Equal(0u, WaitForSingleObject(Handle.DangerousGetHandle(), 5000));
        }

        public void Dispose()
        {
            Stop();
            Handle.Dispose();
            _code.Dispose();
            NativeMemory.Free((void*)_state);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")]
    private static extern uint GetThreadId(nint threadHandle);
    [DllImport("kernel32.dll")]
    private static extern uint SuspendThread(nint threadHandle);
    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(nint threadHandle);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    private static extern nint CreateThread(nint attributes, nuint stackSize, nint entryPoint,
        nint argument, uint flags, out uint threadId);
}
