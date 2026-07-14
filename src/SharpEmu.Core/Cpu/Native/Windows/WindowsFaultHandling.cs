// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;

namespace SharpEmu.Core.Cpu.Native.Windows;

/// <summary>
/// Vectored-exception-handler installation and the handler pre-filter thunk.
/// The thunk is inherently Windows-shaped (TEB stack-limit reads via gs:,
/// NTSTATUS pre-filtering, Win64 calling convention) and moved here whole from
/// DirectExecutionBackend; a POSIX backend supplies a sibling built around
/// sigaction/sigaltstack instead.
/// </summary>
internal sealed unsafe partial class WindowsFaultHandling : IHostFaultHandling
{
    private readonly IHostMemory _memory;

    public WindowsFaultHandling(IHostMemory memory)
    {
        _memory = memory;
    }

    public nint CreateHandlerThunk(nint managedCallback, uint hostRspSwitchTlsSlot, nint tlsGetValueAddress)
    {
        const uint stubSize = 256u;
        void* ptr = (void*)_memory.Allocate(0, stubSize, HostPageProtection.ReadWriteExecute);
        if (ptr == null)
        {
            return 0;
        }

        byte* code = (byte*)ptr;
        int offset = 0;
        // Native pre-filter: these exception codes are raised while the thread can be in
        // cooperative GC mode (a C# throw is RaiseException(0xE0434352) on the throwing
        // thread; FailFast/stack-overflow arrive mid-runtime-failure). Entering the managed
        // handler then trips the CLR's reverse-P/Invoke check and kills the process with
        // "Invalid Program: attempted to call a UnmanagedCallersOnly method from managed
        // code" — this is why no managed throw (even one with a catch handler) ever
        // survived inside the emulator. Continue the handler search without touching
        // managed code; the CLR's own VEH handles its exceptions. MSVC C++ exceptions
        // (Vulkan drivers, host CRT) are excluded too: the managed handler only ever
        // returned CONTINUE_SEARCH for them.
        ReadOnlySpan<uint> nonManagedExceptionCodes =
            [WindowsFaultCodes.ClrManagedException, 0xE06D7363u, WindowsFaultCodes.FastFail, WindowsFaultCodes.StackOverflow];
        EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x01); // mov rax, [rcx] (ExceptionRecord*)
        EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x00);                                   // mov eax, [rax] (ExceptionCode)
        var passJumpOffsets = stackalloc int[nonManagedExceptionCodes.Length];
        for (int i = 0; i < nonManagedExceptionCodes.Length; i++)
        {
            EmitByte(code, ref offset, 0x3D);                                                                 // cmp eax, imm32
            EmitUInt32(code, ref offset, nonManagedExceptionCodes[i]);
            EmitByte(code, ref offset, 0x74);                                                                 // je pass
            passJumpOffsets[i] = offset;
            EmitByte(code, ref offset, 0x00);
        }
        EmitByte(code, ref offset, 0xEB); EmitByte(code, ref offset, 0x03);                                   // jmp over pass block
        int passOffset = offset;
        EmitByte(code, ref offset, 0x31); EmitByte(code, ref offset, 0xC0);                                   // pass: xor eax, eax (EXCEPTION_CONTINUE_SEARCH)
        EmitByte(code, ref offset, 0xC3);                                                                     // ret
        for (int i = 0; i < nonManagedExceptionCodes.Length; i++)
        {
            code[passJumpOffsets[i]] = checked((byte)(passOffset - (passJumpOffsets[i] + 1)));
        }
        EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x54); // push r12
        EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x55); // push r13
        EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xE4); // mov r12, rsp
        EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xCD); // mov r13, rcx
        EmitByte(code, ref offset, 0x65); EmitByte(code, ref offset, 0x48); // mov rax, gs:[8]
        EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x04); EmitByte(code, ref offset, 0x25);
        EmitUInt32(code, ref offset, 8u);
        EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0x39); EmitByte(code, ref offset, 0xC4); // cmp r12, rax
        EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x83); // jae guestStack
        int aboveStackJump = offset;
        EmitUInt32(code, ref offset, 0u);
        EmitByte(code, ref offset, 0x65); EmitByte(code, ref offset, 0x48); // mov rax, gs:[0x10]
        EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x04); EmitByte(code, ref offset, 0x25);
        EmitUInt32(code, ref offset, 0x10u);
        EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0x39); EmitByte(code, ref offset, 0xC4); // cmp r12, rax
        EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x82); // jb guestStack
        int belowStackJump = offset;
        EmitUInt32(code, ref offset, 0u);

        EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83); EmitByte(code, ref offset, 0xEC); EmitByte(code, ref offset, 0x28);
        EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xE9); // mov rcx, r13
        EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
        *(nint*)(code + offset) = managedCallback;
        offset += sizeof(nint);
        EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0);
        EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83); EmitByte(code, ref offset, 0xC4); EmitByte(code, ref offset, 0x28);
        EmitByte(code, ref offset, 0xE9);
        int hostRestoreJump = offset;
        EmitUInt32(code, ref offset, 0u);

        int guestStackOffset = offset;
        EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83); EmitByte(code, ref offset, 0xEC); EmitByte(code, ref offset, 0x28);
        EmitByte(code, ref offset, 0xB9);
        EmitUInt32(code, ref offset, hostRspSwitchTlsSlot);
        EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
        *(nint*)(code + offset) = tlsGetValueAddress;
        offset += sizeof(nint);
        EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0);
        EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83); EmitByte(code, ref offset, 0xC4); EmitByte(code, ref offset, 0x28);
        EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x85); EmitByte(code, ref offset, 0xC0); // test rax, rax
        EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x84);
        int missingTlsJump = offset;
        EmitUInt32(code, ref offset, 0u);
        EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x18); // mov r11, [rax]
        EmitByte(code, ref offset, 0x4D); EmitByte(code, ref offset, 0x85); EmitByte(code, ref offset, 0xDB); // test r11, r11
        EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x84);
        int missingHostStackJump = offset;
        EmitUInt32(code, ref offset, 0u);
        EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xDC); // mov rsp, r11
        EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83); EmitByte(code, ref offset, 0xEC); EmitByte(code, ref offset, 0x28);
        EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xE9); // mov rcx, r13
        EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
        *(nint*)(code + offset) = managedCallback;
        offset += sizeof(nint);
        EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0);
        EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83); EmitByte(code, ref offset, 0xC4); EmitByte(code, ref offset, 0x28);
        EmitByte(code, ref offset, 0xE9);
        int guestRestoreJump = offset;
        EmitUInt32(code, ref offset, 0u);

        int passThroughOffset = offset;
        EmitByte(code, ref offset, 0x31); EmitByte(code, ref offset, 0xC0); // xor eax, eax
        int restoreOffset = offset;
        EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xE4); // mov rsp, r12
        EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x5D);
        EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x5C);
        EmitByte(code, ref offset, 0xC3);

        *(int*)(code + aboveStackJump) = guestStackOffset - (aboveStackJump + sizeof(int));
        *(int*)(code + belowStackJump) = guestStackOffset - (belowStackJump + sizeof(int));
        *(int*)(code + hostRestoreJump) = restoreOffset - (hostRestoreJump + sizeof(int));
        *(int*)(code + missingTlsJump) = passThroughOffset - (missingTlsJump + sizeof(int));
        *(int*)(code + missingHostStackJump) = passThroughOffset - (missingHostStackJump + sizeof(int));
        *(int*)(code + guestRestoreJump) = restoreOffset - (guestRestoreJump + sizeof(int));

        if (!_memory.Protect((ulong)ptr, stubSize, HostPageProtection.ReadExecute, out _))
        {
            Console.Error.WriteLine($"[LOADER][ERROR] VirtualProtect failed for exception handler trampoline at 0x{(nint)ptr:X16}");
            _ = _memory.Free((ulong)ptr);
            return 0;
        }
        _memory.FlushInstructionCache((ulong)ptr, (ulong)offset);
        return (nint)ptr;
    }

    public void FreeThunk(nint thunk)
    {
        _ = _memory.Free((ulong)thunk);
    }

    public nint AddFirstChanceHandler(nint thunk)
    {
        return (nint)AddVectoredExceptionHandler(1u, thunk);
    }

    public void RemoveHandler(nint handle)
    {
        _ = RemoveVectoredExceptionHandler((void*)handle);
    }

    public void SetUnhandledFilter(nint thunk)
    {
        _ = SetUnhandledExceptionFilter(thunk);
    }

    private static void EmitByte(byte* code, ref int offset, byte value)
    {
        code[offset++] = value;
    }

    private static void EmitUInt32(byte* code, ref int offset, uint value)
    {
        *(uint*)(code + offset) = value;
        offset += sizeof(uint);
    }

    [LibraryImport("kernel32.dll")]
    private static partial void* AddVectoredExceptionHandler(uint first, IntPtr handler);

    [LibraryImport("kernel32.dll")]
    private static partial uint RemoveVectoredExceptionHandler(void* handle);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr SetUnhandledExceptionFilter(IntPtr lpTopLevelExceptionFilter);
}
