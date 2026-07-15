// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Posix;

/// <summary>
/// POSIX replacements for the kernel32 helpers the native backend embeds in
/// emitted x86-64 code. Every stub exposed here follows the Win64 calling
/// convention the emitted call sites were written for (first argument in
/// ECX, result in RAX, Win64 non-volatile registers preserved), so the
/// emission code stays identical across platforms.
/// </summary>
internal static unsafe class PosixHostStubs
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static nint _tlsGetValueStub;
    private static nint _queryPerformanceCounterStub;
    private static nint _switchToThreadStub;
    private static nint _sleepStub;

    public static nint TlsGetValueStubAddress
    {
        get { EnsureInitialized(); return _tlsGetValueStub; }
    }

    public static nint QueryPerformanceCounterStubAddress
    {
        get { EnsureInitialized(); return _queryPerformanceCounterStub; }
    }

    public static nint SwitchToThreadStubAddress
    {
        get { EnsureInitialized(); return _switchToThreadStub; }
    }

    public static nint SleepStubAddress
    {
        get { EnsureInitialized(); return _sleepStub; }
    }

    /// <summary>Allocates a pthread TLS key, mirroring kernel32!TlsAlloc.</summary>
    public static uint TlsAlloc()
    {
        if (OperatingSystem.IsMacOS())
        {
            nuint key;
            return pthread_key_create_mac(&key, 0) == 0 ? (uint)key : uint.MaxValue;
        }

        uint key32;
        return pthread_key_create_linux(&key32, 0) == 0 ? key32 : uint.MaxValue;
    }

    public static bool TlsFree(uint key)
    {
        return OperatingSystem.IsMacOS()
            ? pthread_key_delete_mac((nuint)key) == 0
            : pthread_key_delete_linux(key) == 0;
    }

    public static bool TlsSetValue(uint key, nint value)
    {
        return OperatingSystem.IsMacOS()
            ? pthread_setspecific_mac((nuint)key, value) == 0
            : pthread_setspecific_linux(key, value) == 0;
    }

    public static nint TlsGetValue(uint key)
    {
        return OperatingSystem.IsMacOS()
            ? pthread_getspecific_mac((nuint)key)
            : pthread_getspecific_linux(key);
    }

    /// <summary>Stable numeric id of the calling thread (kernel32!GetCurrentThreadId).</summary>
    public static uint GetCurrentThreadId()
    {
        if (OperatingSystem.IsMacOS())
        {
            ulong tid;
            return pthread_threadid_np(0, &tid) == 0 ? unchecked((uint)tid) : 0u;
        }

        return unchecked((uint)gettid());
    }

    /// <summary>
    /// Wraps a managed callback (compiled for the SysV ABI on POSIX .NET) in a
    /// thunk that accepts up to four integer arguments in the Win64 ABI the
    /// emitted x86-64 call sites use. Win64 passes args in rcx/rdx/r8/r9 and
    /// treats rdi/rsi as non-volatile; SysV expects rdi/rsi/rdx/rcx and
    /// clobbers them, so the thunk saves rdi/rsi, shuffles the registers, keeps
    /// the stack 16-byte aligned for the call, and forwards the rax result.
    /// </summary>
    public static nint CreateWin64ToSysVThunk(nint sysvTarget)
    {
        var memory = HostPlatform.Current.Memory;
        var page = (byte*)memory.Allocate(
            0,
            4096,
            HostPageProtection.ReadWriteExecute);
        if (page == null)
        {
            throw new OutOfMemoryException("Failed to allocate Win64->SysV thunk page");
        }

        var offset = 0;
        Emit(page, ref offset, 0x57);                   // push rdi
        Emit(page, ref offset, 0x56);                   // push rsi
        Emit(page, ref offset, 0x48, 0x89, 0xCF);       // mov rdi, rcx
        Emit(page, ref offset, 0x48, 0x89, 0xD6);       // mov rsi, rdx
        Emit(page, ref offset, 0x4C, 0x89, 0xC2);       // mov rdx, r8
        Emit(page, ref offset, 0x4C, 0x89, 0xC9);       // mov rcx, r9
        Emit(page, ref offset, 0x48, 0x83, 0xEC, 0x08); // sub rsp, 8 (realign to 16)
        EmitMovRaxImm64(page, ref offset, sysvTarget);  // mov rax, target
        Emit(page, ref offset, 0xFF, 0xD0);             // call rax
        Emit(page, ref offset, 0x48, 0x83, 0xC4, 0x08); // add rsp, 8
        Emit(page, ref offset, 0x5E);                   // pop rsi
        Emit(page, ref offset, 0x5F);                   // pop rdi
        Emit(page, ref offset, 0xC3);                   // ret

        if (!memory.Protect((ulong)page, 4096, HostPageProtection.ReadExecute, out _))
        {
            throw new InvalidOperationException("Failed to protect Win64->SysV thunk page");
        }

        memory.FlushInstructionCache((ulong)page, (ulong)offset);
        return (nint)page;
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            BuildStubs();
            _initialized = true;
        }
    }

    private static void BuildStubs()
    {
        var memory = HostPlatform.Current.Memory;
        var page = (byte*)memory.Allocate(
            0,
            4096,
            HostPageProtection.ReadWriteExecute);
        if (page == null)
        {
            throw new OutOfMemoryException("Failed to allocate POSIX host helper stub page");
        }

        var offset = 0;
        _tlsGetValueStub = EmitTlsGetValue(page, ref offset);
        _queryPerformanceCounterStub = EmitQueryPerformanceCounter(page, ref offset);
        _switchToThreadStub = EmitSwitchToThread(page, ref offset);
        _sleepStub = EmitSleep(page, ref offset);

        if (!memory.Protect((ulong)page, 4096, HostPageProtection.ReadExecute, out _))
        {
            throw new InvalidOperationException("Failed to protect POSIX host helper stub page");
        }

        memory.FlushInstructionCache((ulong)page, (ulong)offset);
    }

    private static nint EmitTlsGetValue(byte* page, ref int offset)
    {
        var start = (nint)(page + offset);
        if (OperatingSystem.IsMacOS())
        {
            // On macOS x86-64 pthread keys index the gs-based thread specific
            // data array directly, so TlsGetValue(index in ecx) collapses to a
            // single load that clobbers nothing but RAX.
            Emit(page, ref offset, 0x89, 0xC8);                                     // mov eax, ecx
            Emit(page, ref offset, 0x65, 0x48, 0x8B, 0x04, 0xC5, 0, 0, 0, 0);       // mov rax, gs:[rax*8]
            Emit(page, ref offset, 0xC3);                                           // ret
            return start;
        }

        // Linux: call pthread_getspecific, preserving the registers that are
        // volatile in SysV but non-volatile in Win64 (rsi, rdi).
        var pthreadGetSpecific = ResolveLibcExport("pthread_getspecific");
        Emit(page, ref offset, 0x56);                                               // push rsi
        Emit(page, ref offset, 0x57);                                               // push rdi
        Emit(page, ref offset, 0x48, 0x83, 0xEC, 0x08);                             // sub rsp, 8
        Emit(page, ref offset, 0x89, 0xCF);                                         // mov edi, ecx
        EmitMovRaxImm64(page, ref offset, pthreadGetSpecific);                      // mov rax, imm64
        Emit(page, ref offset, 0xFF, 0xD0);                                         // call rax
        Emit(page, ref offset, 0x48, 0x83, 0xC4, 0x08);                             // add rsp, 8
        Emit(page, ref offset, 0x5F);                                               // pop rdi
        Emit(page, ref offset, 0x5E);                                               // pop rsi
        Emit(page, ref offset, 0xC3);                                               // ret
        return start;
    }

    private static nint EmitQueryPerformanceCounter(byte* page, ref int offset)
    {
        // BOOL QueryPerformanceCounter(LARGE_INTEGER* out in rcx): the emitted
        // consumers only need a monotonically increasing counter, which rdtsc
        // provides without leaving Win64-safe registers.
        var start = (nint)(page + offset);
        Emit(page, ref offset, 0x0F, 0x31);                                         // rdtsc
        Emit(page, ref offset, 0x48, 0xC1, 0xE2, 0x20);                             // shl rdx, 32
        Emit(page, ref offset, 0x48, 0x09, 0xD0);                                   // or rax, rdx
        Emit(page, ref offset, 0x48, 0x89, 0x01);                                   // mov [rcx], rax
        Emit(page, ref offset, 0xB8, 0x01, 0x00, 0x00, 0x00);                       // mov eax, 1
        Emit(page, ref offset, 0xC3);                                               // ret
        return start;
    }

    private static nint EmitSwitchToThread(byte* page, ref int offset)
    {
        var schedYield = ResolveLibcExport("sched_yield");
        var start = (nint)(page + offset);
        Emit(page, ref offset, 0x56);                                               // push rsi
        Emit(page, ref offset, 0x57);                                               // push rdi
        Emit(page, ref offset, 0x48, 0x83, 0xEC, 0x08);                             // sub rsp, 8
        EmitMovRaxImm64(page, ref offset, schedYield);                              // mov rax, imm64
        Emit(page, ref offset, 0xFF, 0xD0);                                         // call rax
        Emit(page, ref offset, 0x48, 0x83, 0xC4, 0x08);                             // add rsp, 8
        Emit(page, ref offset, 0x5F);                                               // pop rdi
        Emit(page, ref offset, 0x5E);                                               // pop rsi
        Emit(page, ref offset, 0xB8, 0x01, 0x00, 0x00, 0x00);                       // mov eax, 1
        Emit(page, ref offset, 0xC3);                                               // ret
        return start;
    }

    private static nint EmitSleep(byte* page, ref int offset)
    {
        // void Sleep(DWORD milliseconds in ecx) -> usleep(microseconds in edi).
        var usleep = ResolveLibcExport("usleep");
        var start = (nint)(page + offset);
        Emit(page, ref offset, 0x56);                                               // push rsi
        Emit(page, ref offset, 0x57);                                               // push rdi
        Emit(page, ref offset, 0x48, 0x83, 0xEC, 0x08);                             // sub rsp, 8
        Emit(page, ref offset, 0x89, 0xCF);                                         // mov edi, ecx
        Emit(page, ref offset, 0x81, 0xFF, 0xFF, 0x0F, 0x00, 0x00);                 // cmp edi, 0xFFF
        Emit(page, ref offset, 0x76, 0x05);                                         // jbe +5
        Emit(page, ref offset, 0xBF, 0xFF, 0x0F, 0x00, 0x00);                       // mov edi, 0xFFF (cap at ~4s)
        Emit(page, ref offset, 0x69, 0xFF, 0xE8, 0x03, 0x00, 0x00);                 // imul edi, edi, 1000
        EmitMovRaxImm64(page, ref offset, usleep);                                  // mov rax, imm64
        Emit(page, ref offset, 0xFF, 0xD0);                                         // call rax
        Emit(page, ref offset, 0x48, 0x83, 0xC4, 0x08);                             // add rsp, 8
        Emit(page, ref offset, 0x5F);                                               // pop rdi
        Emit(page, ref offset, 0x5E);                                               // pop rsi
        Emit(page, ref offset, 0xC3);                                               // ret
        return start;
    }

    private static nint ResolveLibcExport(string name)
    {
        var libc = NativeLibrary.Load(OperatingSystem.IsMacOS() ? "libSystem.dylib" : "libc.so.6");
        return NativeLibrary.GetExport(libc, name);
    }

    private static void Emit(byte* page, ref int offset, params byte[] bytes)
    {
        foreach (var value in bytes)
        {
            page[offset++] = value;
        }
    }

    private static void EmitMovRaxImm64(byte* page, ref int offset, nint value)
    {
        Emit(page, ref offset, 0x48, 0xB8);
        *(long*)(page + offset) = value;
        offset += sizeof(long);
    }

    [DllImport("libc", EntryPoint = "pthread_key_create", SetLastError = true)]
    private static extern int pthread_key_create_mac(nuint* key, nint destructor);

    [DllImport("libc", EntryPoint = "pthread_key_create", SetLastError = true)]
    private static extern int pthread_key_create_linux(uint* key, nint destructor);

    [DllImport("libc", EntryPoint = "pthread_key_delete")]
    private static extern int pthread_key_delete_mac(nuint key);

    [DllImport("libc", EntryPoint = "pthread_key_delete")]
    private static extern int pthread_key_delete_linux(uint key);

    [DllImport("libc", EntryPoint = "pthread_setspecific")]
    private static extern int pthread_setspecific_mac(nuint key, nint value);

    [DllImport("libc", EntryPoint = "pthread_setspecific")]
    private static extern int pthread_setspecific_linux(uint key, nint value);

    [DllImport("libc", EntryPoint = "pthread_getspecific")]
    private static extern nint pthread_getspecific_mac(nuint key);

    [DllImport("libc", EntryPoint = "pthread_getspecific")]
    private static extern nint pthread_getspecific_linux(uint key);

    [DllImport("libc")]
    private static extern int pthread_threadid_np(nint thread, ulong* threadId);

    [DllImport("libc")]
    private static extern int gettid();
}
