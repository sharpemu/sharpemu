// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe partial class DirectExecutionBackend
{
    private SafeWaitHandle? _mainExecutionThreadHandle;
    // One immutable code page serves all diagnostic samples for the process lifetime.
    private static readonly Lazy<ThreadContextCaptureCode> ThreadCaptureCode = new(() => new());

    private sealed class ThreadContextCaptureCode : SafeHandle
    {
        public override bool IsInvalid => handle == 0;

        public ThreadContextCaptureCode() : base(0, ownsHandle: true)
        {
            var kernelModule = GetModuleHandle("kernel32.dll");
            var suspendAddress = GetProcAddress(kernelModule, "SuspendThread");
            var contextAddress = GetProcAddress(kernelModule, "GetThreadContext");
            var resumeAddress = GetProcAddress(kernelModule, "ResumeThread");
            if (suspendAddress == 0 || contextAddress == 0 || resumeAddress == 0) return;
            var instructions = BuildThreadContextCaptureCode(suspendAddress, contextAddress, resumeAddress);
            SetHandle((nint)VirtualAlloc(null, (nuint)instructions.Length, 0x3000, 0x04));
            if (IsInvalid) return;
            instructions.CopyTo(new Span<byte>((void*)handle, instructions.Length));
            uint previousProtection = 0;
            if (!VirtualProtect((void*)handle, (nuint)instructions.Length, 0x20, &previousProtection))
            {
                Dispose();
                return;
            }
            FlushInstructionCache(GetCurrentProcess(), (void*)handle, (nuint)instructions.Length);
        }

        protected override bool ReleaseHandle() => VirtualFree((void*)handle, 0, 0x8000);
    }

    private static SafeWaitHandle? OpenThreadForContext(uint threadId)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || threadId == 0)
            return null;

        const uint queryLimitedInformation = 0x0800;
        var threadHandle = OpenThread(ThreadGetContext | ThreadSuspendResume | queryLimitedInformation, false, threadId);
        return threadHandle == 0 ? null : new SafeWaitHandle(threadHandle, ownsHandle: true);
    }

    private static bool TryCaptureHostThreadContext(int threadId, out HostThreadContextSnapshot snapshot)
    {
        using var threadHandle = OpenThreadForContext(unchecked((uint)threadId));
        return TryCaptureThreadContext(threadHandle, out snapshot);
    }

    private static bool TryCaptureThreadContext(SafeWaitHandle? threadHandle, out HostThreadContextSnapshot snapshot)
    {
        snapshot = default;
        if (threadHandle is null || !OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return false;

        var handleReferenced = false;
        try
        {
            threadHandle.DangerousAddRef(ref handleReferenced);
            var nativeHandle = threadHandle.DangerousGetHandle();
            var threadId = GetThreadId(nativeHandle);
            if (threadId == 0 || threadId == GetCurrentThreadId()) return false;

            var captureCode = ThreadCaptureCode.Value;
            if (captureCode.IsInvalid || captureCode.IsClosed) return false;

            byte* contextStorage = stackalloc byte[Win64ContextSize + 15];
            var contextRecord = (void*)(((nuint)contextStorage + 15) & ~(nuint)15);
            new Span<byte>(contextRecord, Win64ContextSize).Clear();
            WriteCtxU32(contextRecord, Win64ContextFlagsOffset, ContextAmd64ControlInteger);

            // One native transition keeps managed work outside the suspended interval.
            var captured = ((delegate* unmanaged<nint, void*, int>)captureCode.DangerousGetHandle())(nativeHandle, contextRecord);
            if (captured == 0) return false;
            snapshot = DecodeHostThreadContext(threadId, contextRecord);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            if (handleReferenced) threadHandle!.DangerousRelease();
        }
    }

    private static HostThreadContextSnapshot DecodeHostThreadContext(uint threadId, void* contextRecord) => new(
        threadId,
        ReadCtxU64(contextRecord, 248), ReadCtxU64(contextRecord, 152), ReadCtxU64(contextRecord, 160),
        ReadCtxU64(contextRecord, 120), ReadCtxU64(contextRecord, 144), ReadCtxU64(contextRecord, 128),
        ReadCtxU64(contextRecord, 136), ReadCtxU64(contextRecord, 168), ReadCtxU64(contextRecord, 176),
        ReadCtxU64(contextRecord, 184), ReadCtxU64(contextRecord, 192), ReadCtxU64(contextRecord, 200),
        ReadCtxU64(contextRecord, 208), ReadCtxU64(contextRecord, 216), ReadCtxU64(contextRecord, 224),
        ReadCtxU64(contextRecord, 232), ReadCtxU64(contextRecord, 240));

    internal static byte[] BuildThreadContextCaptureCode(nint suspendAddress, nint contextAddress, nint resumeAddress)
    {
        var instructions = new List<byte>();
        void CallAddress(nint address)
        {
            instructions.AddRange([0x48, 0xB8]);
            instructions.AddRange(BitConverter.GetBytes((long)address));
            instructions.AddRange([0xFF, 0xD0]);
        }

        // Preserve nonvolatile registers and reserve aligned argument space.
        instructions.AddRange([0x53, 0x56, 0x57, 0x48, 0x83, 0xEC, 0x20,
            0x48, 0x89, 0xCB, 0x48, 0x89, 0xD6, 0x31, 0xFF]);
        CallAddress(suspendAddress);
        instructions.AddRange([0x83, 0xF8, 0xFF, 0x74, 0]);
        var failedSuspendJump = instructions.Count - 1;
        instructions.AddRange([0x48, 0x89, 0xD9, 0x48, 0x89, 0xF2]);
        CallAddress(contextAddress);
        instructions.AddRange([0x89, 0xC7, 0x48, 0x89, 0xD9]);
        CallAddress(resumeAddress);
        instructions.AddRange([0x83, 0xF8, 0xFF, 0x75, 0x02, 0x31, 0xFF]);
        var returnOffset = instructions.Count;
        instructions.AddRange([0x89, 0xF8, 0x48, 0x83, 0xC4, 0x20, 0x5F, 0x5E, 0x5B, 0xC3]);
        instructions[failedSuspendJump] = checked((byte)(returnOffset - failedSuspendJump - 1));
        return instructions.ToArray();
    }

    private void LogMainThreadStallContext()
    {
        if (!TryCaptureThreadContext(Volatile.Read(ref _mainExecutionThreadHandle), out var context)) return;

        Console.Error.WriteLine($"[LOADER][ERROR] Live hardware context (main thread {context.ThreadId}):");
        Console.Error.WriteLine($"[LOADER][ERROR]   RIP=0x{context.Rip:X16} RSP=0x{context.Rsp:X16} RBP=0x{context.Rbp:X16}");
        Console.Error.WriteLine($"[LOADER][ERROR]   RAX=0x{context.Rax:X16} RBX=0x{context.Rbx:X16} RCX=0x{context.Rcx:X16} RDX=0x{context.Rdx:X16}");
        Console.Error.WriteLine($"[LOADER][ERROR]   RSI=0x{context.Rsi:X16} RDI=0x{context.Rdi:X16} R8=0x{context.R8:X16} R9=0x{context.R9:X16}");
        Console.Error.WriteLine($"[LOADER][ERROR]   R10=0x{context.R10:X16} R11=0x{context.R11:X16} R12=0x{context.R12:X16} R13=0x{context.R13:X16} R14=0x{context.R14:X16} R15=0x{context.R15:X16}");

        var modules = Array.Empty<(ulong Start, ulong Size, string Name)>();
        try
        {
            using var process = Process.GetCurrentProcess();
            modules = process.Modules.Cast<ProcessModule>().Select(module =>
                (Start: unchecked((ulong)module.BaseAddress), Size: (ulong)module.ModuleMemorySize, Name: module.ModuleName)).ToArray();
        }
        catch (Win32Exception) { }
        catch (InvalidOperationException) { }
        string DescribeAddress(ulong address)
        {
            foreach (var module in modules)
                if (address >= module.Start && address - module.Start < module.Size)
                    return $"{module.Name}+0x{address - module.Start:X}";
            return $"0x{address:X16}";
        }

        Console.Error.WriteLine($"[LOADER][ERROR]   RIP location: {DescribeAddress(context.Rip)}");
        // The thread has resumed. These bytes are best-effort observations, not an atomic snapshot.
        var instructionBytes = new byte[48];
        if (context.Rip >= 32 && TryReadHostBytes(context.Rip - 32, instructionBytes))
            Console.Error.WriteLine($"[LOADER][ERROR]   Live bytes @[RIP-32..RIP+16]: {Convert.ToHexString(instructionBytes)}");
        var currentInstruction = new byte[16];
        if (TryReadHostBytes(context.Rip, currentInstruction))
            Console.Error.WriteLine($"[LOADER][ERROR]   Live bytes @RIP: {Convert.ToHexString(currentInstruction)}");

        Console.Error.WriteLine("[LOADER][ERROR]   Live stack pointer candidates:");
        for (var offset = 0; offset < 256; offset += sizeof(ulong))
        {
            var address = context.Rsp + (ulong)offset;
            if (address < context.Rsp || !TryReadDiagnosticHostQword(address, out var value)) break;
            if (value != 0)
                Console.Error.WriteLine($"[LOADER][ERROR]     [rsp+0x{offset:X2}] = 0x{value:X16} ({DescribeAddress(value)})");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetThreadId(nint threadHandle);
}
