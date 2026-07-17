// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe partial class DirectExecutionBackend
{
	// POSIX bridge for the Windows vectored-exception-handler logic. A
	// sigaction(SIGSEGV/SIGBUS/SIGILL) handler rebuilds the EXCEPTION_POINTERS
	// view the shared handlers expect (Win64 CONTEXT register offsets) from
	// the signal's mcontext, runs the same recovery chain the VEH path uses
	// (unresolved-import trap sentinels, demand-paging of lazily-committed
	// guest pages, fault diagnostics), and writes register changes back into
	// the mcontext so sigreturn resumes the repaired guest. Unrecovered
	// faults are forwarded to the previously installed handler so the .NET
	// runtime keeps turning its own faults into managed exceptions.

	private const int PosixSigIll = 4;
	private const int PosixSigTrap = 5;
	private const int PosixSigAbort = 6;
	private const int PosixSigSegv = 11;
	private static readonly int PosixSigBus = OperatingSystem.IsMacOS() ? 10 : 7;

	// struct sigaction: the handler pointer leads on both platforms; Darwin
	// packs { handler(8), mask(4), flags(4) }, Linux glibc/musl packs
	// { handler(8), mask(128), flags(4), restorer(8) }.
	private static readonly int PosixSigactionSize = OperatingSystem.IsMacOS() ? 16 : 152;
	private static readonly int PosixSigactionFlagsOffset = OperatingSystem.IsMacOS() ? 12 : 136;

	private static readonly int PosixSaSigInfo = OperatingSystem.IsMacOS() ? 0x0040 : 0x0004;
	private static readonly int PosixSaNoDefer = OperatingSystem.IsMacOS() ? 0x0010 : 0x40000000;

	// siginfo_t.si_addr: Darwin { signo, errno, code, pid, uid, status, addr },
	// Linux { signo, errno, code, pad32, addr }.
	private static readonly int PosixSigInfoAddressOffset = OperatingSystem.IsMacOS() ? 24 : 16;

	// Darwin ucontext_t stores a pointer to __darwin_mcontext64 at +48; the
	// general registers live in its __ss thread state after the 16-byte
	// exception state. Linux glibc embeds mcontext_t inline at +40 with the
	// registers in gregs[23]. Rosetta 2 delivers the regular x86-64 layout
	// to translated processes.
	private const int DarwinUcontextMcontextOffset = 48;
	private const int DarwinMcontextErrOffset = 4;
	private const int DarwinMcontextFaultAddressOffset = 8;
	private const int LinuxUcontextGregsOffset = 40;
	private const int LinuxGregsErrOffset = 19 * 8;

	// Byte offsets of the general registers relative to GetPosixRegisterBase,
	// ordered to match the contiguous Win64 CONTEXT block CTX_RAX..CTX_RIP
	// (rax, rcx, rdx, rbx, rsp, rbp, rsi, rdi, r8..r15, rip). Verified
	// against the x86-64 platform headers.
	private static readonly int[] PosixRegisterOffsets = OperatingSystem.IsMacOS()
		? new[] { 16, 32, 40, 24, 72, 64, 56, 48, 80, 88, 96, 104, 112, 120, 128, 136, 144 }
		: new[] { 104, 112, 96, 88, 120, 80, 72, 64, 0, 8, 16, 24, 32, 40, 48, 56, 128 };

	private static DirectExecutionBackend? _posixSignalBackend;
	private static bool _posixSignalHandlersInstalled;
	private static bool _posixRawRecoveryEnabled;
	private static bool _posixSignalWarmup;
	private static readonly nint[] _posixPreviousActions = new nint[32];
	private static int _posixSignalTraceCount;
	private static long _perfSignalCount;
	private static readonly bool _perfSignalCounter =
		string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_PERF_MEM"), "1", StringComparison.Ordinal);

	[ThreadStatic]
	private static int _posixSignalHandlerDepth;

	private void SetupPosixExceptionHandler()
	{
		if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_POSIX_SIGNALS"), "1", StringComparison.Ordinal))
		{
			Console.Error.WriteLine("[LOADER][WARN] POSIX signal exception bridge disabled by SHARPEMU_DISABLE_POSIX_SIGNALS=1; guest faults will not be recovered.");
			return;
		}

		_posixSignalBackend = this;
		if (_posixSignalHandlersInstalled)
		{
			return;
		}

		_posixRawRecoveryEnabled = !string.Equals(
			Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_RAW_HANDLER"), "1", StringComparison.Ordinal);
		if (!_posixRawRecoveryEnabled)
		{
			Console.Error.WriteLine("[LOADER][INFO] Raw sentinel recovery disabled by SHARPEMU_DISABLE_RAW_HANDLER=1");
		}

		WarmUpPosixSignalPath();
		SharpEmu.HLE.GuestImageWriteTracker.WarmUp();

		if (!InstallPosixSignalHandler(PosixSigSegv) ||
			!InstallPosixSignalHandler(PosixSigBus) ||
			!InstallPosixSignalHandler(PosixSigIll) ||
			!InstallPosixSignalHandler(PosixSigTrap) ||
			!InstallPosixSignalHandler(PosixSigAbort))
		{
			throw new InvalidOperationException("Failed to install POSIX fault signal handlers");
		}

		_posixSignalHandlersInstalled = true;
		Console.Error.WriteLine("[LOADER][INFO] POSIX signal exception bridge installed (SIGSEGV/SIGBUS/SIGILL)");
	}

	/// <summary>
	/// Runs the signal-recovery path once with fabricated inputs before the
	/// handlers are installed. The first entry into the handler must not
	/// require JIT compilation (a fault can interrupt arbitrary runtime
	/// states), and under Rosetta 2 the signal trampoline cannot enter x86
	/// code that has never been executed (and therefore never translated): a
	/// cold handler is silently never invoked and the faulting instruction
	/// retries forever.
	/// </summary>
	private void WarmUpPosixSignalPath()
	{
		byte* fakeUcontext = stackalloc byte[512];
		new Span<byte>(fakeUcontext, 512).Clear();
		byte* fakeMcontext = stackalloc byte[512];
		new Span<byte>(fakeMcontext, 512).Clear();
		if (OperatingSystem.IsMacOS())
		{
			*(byte**)(fakeUcontext + DarwinUcontextMcontextOffset) = fakeMcontext;
		}

		_posixSignalWarmup = true;
		try
		{
			((delegate* unmanaged<int, nint, nint, void>)&HandlePosixSignal)(PosixSigSegv, 0, (nint)fakeUcontext);

			// Warm the branches the fabricated fault above skips without
			// spamming diagnostics: the benign-exception path through
			// VectoredHandler, the lazy-commit probe (fault address 0 bails
			// out immediately), and the chain helper (signal 0 has no saved
			// action and sigaction(0, ...) fails with EINVAL).
			EXCEPTION_RECORD record = default;
			record.ExceptionCode = DBG_PRINTEXCEPTION_C;
			byte* contextRecord = stackalloc byte[Win64ContextSize];
			new Span<byte>(contextRecord, Win64ContextSize).Clear();
			EXCEPTION_POINTERS pointers;
			pointers.ExceptionRecord = &record;
			pointers.ContextRecord = contextRecord;
			_ = VectoredHandler(&pointers);

			record.ExceptionCode = 3221225477u;
			record.NumberParameters = 2;
			// 0x70000 is never guest-owned, so this walks the vmem region
			// scan and the PRT range check, then bails out silently.
			record.ExceptionInformation[1] = 0x70000;
			_ = TryHandleLazyCommittedPage(&record, 0, 0);
			ChainPreviousPosixAction(0, 0, 0);
		}
		finally
		{
			_posixSignalWarmup = false;
		}
	}

	private static bool InstallPosixSignalHandler(int signal)
	{
		byte* action = stackalloc byte[PosixSigactionSize];
		new Span<byte>(action, PosixSigactionSize).Clear();
		*(nint*)action = (nint)(delegate* unmanaged<int, nint, nint, void>)&HandlePosixSignal;
		// No SA_ONSTACK: the runtime's alternate stacks are far too small for
		// the recovery/diagnostic path (JIT compilation of cold handler code
		// can run inside the signal frame). Guest faults deliver onto the 2MB
		// guest stack, host faults onto the regular thread stack — the same
		// stacks Windows dispatches exceptions on.
		*(int*)(action + PosixSigactionFlagsOffset) = PosixSaSigInfo | PosixSaNoDefer;

		var previous = (byte*)NativeMemory.AllocZeroed((nuint)PosixSigactionSize);
		if (sigaction(signal, action, previous) != 0)
		{
			NativeMemory.Free(previous);
			Console.Error.WriteLine($"[LOADER][ERROR] sigaction({signal}) failed: errno={Marshal.GetLastPInvokeError()}");
			return false;
		}

		_posixPreviousActions[signal] = (nint)previous;
		return true;
	}

	[UnmanagedCallersOnly]
	private static void HandlePosixSignal(int signal, nint siginfo, nint ucontext)
	{
		if (_posixSignalHandlerDepth > 0)
		{
			// A fault inside our own fault handler (diagnostics touched an
			// unmapped address): restore the default action and return so the
			// re-executed instruction terminates the process.
			RestoreDefaultPosixAction(signal);
			return;
		}

		_posixSignalHandlerDepth++;
		if (_perfSignalCounter)
		{
			var n = Interlocked.Increment(ref _perfSignalCount);
			if (n % 100000 == 0)
			{
				Console.Error.WriteLine($"[PERF][MEM] posix_faults={n}");
			}
		}
		try
		{
			// Guest-image write tracking runs first: it only needs the fault
			// address (safe for host and guest threads alike) and must resume
			// the faulting write immediately after restoring write access.
			if (signal != PosixSigIll &&
				siginfo != 0 &&
				SharpEmu.HLE.GuestImageWriteTracker.TryHandleWriteFault(
					*(ulong*)((byte*)siginfo + PosixSigInfoAddressOffset)))
			{
				return;
			}

			if (TryHandlePosixFault(signal, siginfo, ucontext))
			{
				return;
			}
		}
		catch
		{
			// A managed exception must never unwind out of a signal frame.
		}
		finally
		{
			_posixSignalHandlerDepth--;
		}

		ChainPreviousPosixAction(signal, siginfo, ucontext);
	}

	private static bool TryHandlePosixFault(int signal, nint siginfo, nint ucontext)
	{
		byte* registers = GetPosixRegisterBase(ucontext);
		if (registers == null)
		{
			return false;
		}

		byte* contextRecord = stackalloc byte[Win64ContextSize];
		new Span<byte>(contextRecord, Win64ContextSize).Clear();
		int[] offsets = PosixRegisterOffsets;
		for (int i = 0; i < offsets.Length; i++)
		{
			WriteCtxU64(contextRecord, CTX_RAX + i * 8, *(ulong*)(registers + offsets[i]));
		}

		EXCEPTION_RECORD record = default;
		record.ExceptionAddress = (void*)ReadCtxU64(contextRecord, CTX_RIP);
		if (signal == PosixSigIll)
		{
			record.ExceptionCode = 3221225501u;
		}
		else if (signal == PosixSigTrap)
		{
			record.ExceptionCode = 2147483651u;
		}
		else if (signal == PosixSigAbort)
		{
			record.ExceptionCode = 1073741845u;
		}
		else
		{
			ulong faultAddress = GetPosixFaultAddress(siginfo, registers);
			record.ExceptionCode = 3221225477u;
			record.NumberParameters = 2;
			record.ExceptionInformation[0] = GetPosixAccessType(registers, faultAddress, ReadCtxU64(contextRecord, CTX_RIP));
			record.ExceptionInformation[1] = faultAddress;
		}

		EXCEPTION_POINTERS pointers;
		pointers.ExceptionRecord = &record;
		pointers.ContextRecord = contextRecord;

		int traceIndex = _posixSignalWarmup ? 0 : Interlocked.Increment(ref _posixSignalTraceCount);
		bool traceSignal = traceIndex > 0 && (traceIndex <= 16 || traceIndex % 1024 == 0 ||
			string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_POSIX_SIGNALS"), "1", StringComparison.Ordinal));
		if (traceSignal)
		{
			Console.Error.WriteLine(
				$"[LOADER][TRACE] posix-signal#{traceIndex}: sig={signal} rip=0x{ReadCtxU64(contextRecord, CTX_RIP):X16} " +
				$"fault=0x{record.ExceptionInformation[1]:X16} access={record.ExceptionInformation[0]} rsp=0x{ReadCtxU64(contextRecord, CTX_RSP):X16}");
			Console.Error.Flush();
		}

		// Sentinel recovery runs first: on Windows both vectored handlers see
		// every fault anyway, and recovering here avoids dumping the full
		// VectoredHandler diagnostics for each recoverable trap.
		int disposition = 0;
		if (_posixRawRecoveryEnabled)
		{
			disposition = TryRecoverUnresolvedSentinel(&pointers);
		}
		if (disposition != -1 && !_posixSignalWarmup && _posixSignalBackend is { } backend)
		{
			disposition = backend.VectoredHandler(&pointers);
		}
		if (traceSignal)
		{
			Console.Error.WriteLine(
				$"[LOADER][TRACE] posix-signal#{traceIndex}: recovered={disposition == -1} new_rip=0x{ReadCtxU64(contextRecord, CTX_RIP):X16}");
			Console.Error.Flush();
		}
		if (disposition != -1 && !_posixSignalWarmup)
		{
			return false;
		}

		for (int i = 0; i < offsets.Length; i++)
		{
			*(ulong*)(registers + offsets[i]) = ReadCtxU64(contextRecord, CTX_RAX + i * 8);
		}
		return true;
	}

	private static byte* GetPosixRegisterBase(nint ucontext)
	{
		if (ucontext == 0)
		{
			return null;
		}

		if (OperatingSystem.IsMacOS())
		{
			return *(byte**)((byte*)ucontext + DarwinUcontextMcontextOffset);
		}

		return (byte*)ucontext + LinuxUcontextGregsOffset;
	}

	private static ulong GetPosixFaultAddress(nint siginfo, byte* registers)
	{
		ulong address = siginfo != 0 ? *(ulong*)((byte*)siginfo + PosixSigInfoAddressOffset) : 0;
		if (address == 0 && OperatingSystem.IsMacOS())
		{
			address = *(ulong*)(registers + DarwinMcontextFaultAddressOffset);
		}

		return address;
	}

	private static ulong GetPosixAccessType(byte* registers, ulong faultAddress, ulong rip)
	{
		// x86 page-fault error code: bit 1 = write access, bit 4 = instruction
		// fetch. Fall back to comparing the fault address against RIP when
		// the error code is not populated (e.g. under Rosetta 2 translation).
		ulong error = OperatingSystem.IsMacOS()
			? *(uint*)(registers + DarwinMcontextErrOffset)
			: *(ulong*)(registers + LinuxGregsErrOffset);
		if ((error & 0x10) != 0)
		{
			return 8;
		}
		if ((error & 0x2) != 0)
		{
			return 1;
		}

		return faultAddress != 0 && faultAddress == rip ? 8u : 0u;
	}

	private static void RestoreDefaultPosixAction(int signal)
	{
		byte* action = stackalloc byte[PosixSigactionSize];
		new Span<byte>(action, PosixSigactionSize).Clear();
		_ = sigaction(signal, action, null);
	}

	private static void ChainPreviousPosixAction(int signal, nint siginfo, nint ucontext)
	{
		byte* previous = (uint)signal < (uint)_posixPreviousActions.Length
			? (byte*)_posixPreviousActions[signal]
			: null;
		nint handler = previous != null ? *(nint*)previous : 0;
		if (handler == 0)
		{
			// SIG_DFL (or nothing saved): reinstate the default action and
			// return, so re-executing the faulting instruction terminates the
			// process with the original fault context intact.
			RestoreDefaultPosixAction(signal);
			return;
		}
		if (handler == 1)
		{
			// SIG_IGN
			return;
		}

		int flags = *(int*)(previous + PosixSigactionFlagsOffset);
		if ((flags & PosixSaSigInfo) != 0)
		{
			((delegate* unmanaged<int, nint, nint, void>)handler)(signal, siginfo, ucontext);
		}
		else
		{
			((delegate* unmanaged<int, void>)handler)(signal);
		}
	}

	[DllImport("libc", SetLastError = true)]
	private static extern int sigaction(int signum, void* act, void* oldact);
}
