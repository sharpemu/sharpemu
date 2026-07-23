// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
	// Guest entry stubs must not run above CLR-managed frames on a CLR-created thread:
	// the suspension/stack-walk machinery then has to traverse a frame chain that is
	// interleaved with guest stubs carrying no CLR unwind info, and any window in which
	// the thread-mode bookkeeping disagrees with the actual stack (import dispatch, VEH
	// redirection) fail-fasts the runtime — the audio_output_thread ~7th-cycle
	// "attempted to call a UnmanagedCallersOnly method from managed code" crash.
	//
	// Guest execution therefore runs on dedicated raw OS threads whose run loop is
	// emitted native code. While guest code executes there is not a single managed
	// frame on the thread and it sits in preemptive mode, so the GC ignores it
	// entirely. The only managed activity on the thread is the reverse-P/Invoke import
	// dispatch and the per-run prologue/epilogue, which the CLR supports on foreign
	// threads (lazy attach, preemptive between calls). The managed orchestrator
	// (RunGuestThread / the main execution thread) parks in a preemptive wait for the
	// duration of the run, so its own frame chain is never walked across guest frames.
	private static readonly bool NativeGuestWorkersDisabled =
		string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_NATIVE_GUEST_WORKERS"), "1", StringComparison.Ordinal);

	// Cap concurrent native-worker Runs. Astro's tbb_thead burst overlaps many
	// UnmanagedCallersOnly prologues; a large prewarm + unbounded concurrency
	// FailFasts (0xC0000409) mid-storm with no VEH breadcrumb. Pool size and
	// in-flight Runs are separate knobs.
	private static readonly int NativeWorkerMaxConcurrent = ReadNativeWorkerMaxConcurrent();

	private static int ReadNativeWorkerMaxConcurrent()
	{
		if (int.TryParse(
			    Environment.GetEnvironmentVariable("SHARPEMU_NATIVE_WORKER_MAX_CONCURRENT"),
			    out var parsed) &&
		    parsed > 0)
		{
			return Math.Clamp(parsed, 1, 64);
		}

		return 2;
	}

	private readonly object _nativeWorkerGate = new();
	private readonly List<NativeGuestExecutor> _allNativeWorkers = new();
	private readonly Stack<NativeGuestExecutor> _idleNativeWorkers = new();
	private readonly SemaphoreSlim _nativeWorkerRunLimiter = new(NativeWorkerMaxConcurrent);
	private bool _nativeWorkersDisposed;
	private int _nativeWorkerCreationFailedLogged;

	private const uint StackSizeParamIsAReservation = 0x00010000u;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint CreateThread(
		nint lpThreadAttributes,
		nuint dwStackSize,
		nint lpStartAddress,
		nint lpParameter,
		uint dwCreationFlags,
		out uint lpThreadId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool TerminateThread(nint hThread, uint dwExitCode);

	// Runs an emitted guest entry stub. Preferred path is a pooled native worker
	// thread; falls back to the historical inline calli (guest frames above this
	// thread's managed frames) when workers are disabled or unavailable.
	//
	// Callers set the Active* thread-statics before emitting the stub and read the
	// yield/forced-exit flags right after this returns, so the worker outcome is
	// copied back into this thread's statics before returning.
	private unsafe int RunGuestEntryStub(void* entryStub, ulong hostRspSlot, bool requireNativeWorker = false)
	{
		// Limit in-flight native Runs before renting so the idle pool is not
		// drained by threads blocked on the concurrency gate.
		_nativeWorkerRunLimiter.Wait();
		NativeGuestExecutor? worker = null;
		try
		{
			// Astro can spawn a burst of tbb_thead while workers are still in
			// TerminateThread+respawn. Wait for a native worker — never fall back
			// to managed inline (FailFast) and never throw (uncaught throw mid-
			// storm was a silent process die).
			var maxAttempts = requireNativeWorker ? 500 : 48;
			for (var attempt = 0; attempt < maxAttempts; attempt++)
			{
				worker = RentNativeGuestExecutor();
				if (worker is not null)
				{
					break;
				}

				if (!requireNativeWorker)
				{
					break;
				}

				Thread.Sleep(attempt < 32 ? 1 : 4);
			}

			if (worker is null)
			{
				if (requireNativeWorker)
				{
					var n = Interlocked.Increment(ref _tbbNativeWorkerRefuseCount);
					if (n <= 8 || n % 32 == 0)
					{
						Console.Error.WriteLine(
							$"[LOADER][ERROR] tbb_native_worker unavailable #{n} after {maxAttempts} attempts; " +
							"skipping run (no managed inline, no throw)");
						Console.Error.Flush();
					}

					_activeGuestThreadYieldRequested = true;
					_activeGuestThreadYieldReason = "tbb_native_worker_unavailable";
					_activeForcedGuestExit = true;
					return unchecked((int)0x80020012);
				}

				TlsSetValue(_hostRspSlotTlsIndex, (nint)hostRspSlot);
				return CallNativeEntry(entryStub);
			}

			try
			{
				var state = _activeGuestThreadState;
				if (state is { Name: "tbb_thead" })
				{
					var n = Interlocked.Increment(ref _tbbNativeRunEnterCount);
					if (n <= 12 || n % 64 == 0)
					{
						Console.Error.WriteLine(
							$"[LOADER][INFO] tbb_run_enter #{n} native_tid_pending handle=0x{state.ThreadHandle:X16} " +
							$"max_concurrent={NativeWorkerMaxConcurrent}");
						Console.Error.Flush();
					}
				}

				var nativeReturn = worker.Run(
					_activeCpuContext!,
					state,
					GuestThreadExecution.CurrentGuestThreadHandle,
					_activeEntryReturnSentinelRip,
					_activeGuestReturnSlotAddress,
					(nint)hostRspSlot,
					(nint)entryStub,
					state?.AffinityMask ?? 0,
					out var yieldRequested,
					out var yieldReason,
					out var forcedExit);
				_activeGuestThreadYieldRequested = yieldRequested;
				_activeGuestThreadYieldReason = yieldReason;
				_activeForcedGuestExit = forcedExit;
				return nativeReturn;
			}
			finally
			{
				ReturnNativeGuestExecutor(worker);
			}
		}
		finally
		{
			_nativeWorkerRunLimiter.Release();
		}
	}

	private static int _tbbNativeRunEnterCount;
	private static int _tbbNativeWorkerRefuseCount;
	internal static int _tbbWorkerPrologueFaultCount;

	private void PrewarmNativeGuestWorkers(int count)
	{
		if (!OperatingSystem.IsWindows() || NativeGuestWorkersDisabled || count <= 0)
		{
			return;
		}

		var warmed = new List<NativeGuestExecutor>(count);
		for (var i = 0; i < count; i++)
		{
			var worker = NativeGuestExecutor.TryCreate(this);
			if (worker is null)
			{
				break;
			}

			warmed.Add(worker);
		}

		lock (_nativeWorkerGate)
		{
			if (_nativeWorkersDisposed)
			{
				foreach (var worker in warmed)
				{
					worker.Dispose();
				}
				return;
			}

			foreach (var worker in warmed)
			{
				_allNativeWorkers.Add(worker);
				_idleNativeWorkers.Push(worker);
			}
		}

		Console.Error.WriteLine(
			$"[LOADER][INFO] Native guest workers prewarmed: {warmed.Count}/{count} " +
			$"max_concurrent={NativeWorkerMaxConcurrent}");
		Console.Error.Flush();
	}

	private NativeGuestExecutor? RentNativeGuestExecutor()
	{
		// NativeGuestExecutor emits a Win32 wait loop and creates it with
		// kernel32!CreateThread. POSIX hosts use the established inline entry
		// path until the worker loop has a pthread/eventfd implementation.
		if (!OperatingSystem.IsWindows() || NativeGuestWorkersDisabled)
		{
			return null;
		}
		lock (_nativeWorkerGate)
		{
			if (_nativeWorkersDisposed)
			{
				return null;
			}
			if (_idleNativeWorkers.Count > 0)
			{
				return _idleNativeWorkers.Pop();
			}
		}
		var worker = NativeGuestExecutor.TryCreate(this);
		if (worker is null)
		{
			if (Interlocked.Exchange(ref _nativeWorkerCreationFailedLogged, 1) == 0)
			{
				Console.Error.WriteLine(
					"[LOADER][WARN] Failed to create a native guest worker thread; falling back to inline guest execution.");
			}
			return null;
		}
		lock (_nativeWorkerGate)
		{
			if (_nativeWorkersDisposed)
			{
				worker.Dispose();
				return null;
			}
			_allNativeWorkers.Add(worker);
		}
		return worker;
	}

	private void ReturnNativeGuestExecutor(NativeGuestExecutor worker)
	{
		lock (_nativeWorkerGate)
		{
			if (!_nativeWorkersDisposed)
			{
				_idleNativeWorkers.Push(worker);
				return;
			}
		}
		worker.Dispose();
	}

	private void DisposeNativeGuestExecutors()
	{
		NativeGuestExecutor[] workers;
		lock (_nativeWorkerGate)
		{
			if (_nativeWorkersDisposed)
			{
				return;
			}
			_nativeWorkersDisposed = true;
			workers = _allNativeWorkers.ToArray();
			_allNativeWorkers.Clear();
			_idleNativeWorkers.Clear();
		}
		foreach (var worker in workers)
		{
			worker.Dispose();
		}
	}

	// A pooled raw OS thread that executes guest entry stubs. The run loop is emitted
	// native code (no CLR unwind info required — nothing ever unwinds through it):
	//
	//   loop: WaitForSingleObject(work);
	//         if (*stopFlag) { SetEvent(done); ExitThread(0); }
	//         rax = RunPrologue(self);      // managed: binds per-run ambient, returns stub
	//         if (rax != 0) eax = rax();    // guest entry stub — zero managed frames below
	//         RunEpilogue(self, eax);       // managed: captures outcome, restores ambient
	//         SetEvent(done); goto loop;
	//
	// Workers carry no per-guest identity of their own: the prologue rebinds guest TLS,
	// the host-RSP slot, the Active* thread-statics and the GuestThreadExecution ambient
	// on every run, so a worker can be reused for any guest thread.
	private sealed unsafe class NativeGuestExecutor : IDisposable
	{
		private const uint LoopStubSize = 512u;
		private const uint WorkerStackReservation = 4u * 1024u * 1024u;

		private static nint _waitForSingleObjectAddress;
		private static nint _setEventAddress;
		private static nint _exitThreadAddress;

		private readonly DirectExecutionBackend _backend;
		// Windows uses AutoResetEvent (its SafeWaitHandle is a real kernel
		// event the emitted loop can wait on); POSIX uses worker-event
		// semaphores shared the same way via PosixHostStubs.
		private readonly AutoResetEvent? _workAvailable;
		private readonly AutoResetEvent? _workCompleted;
		private nint _workSemaphore;
		private nint _doneSemaphore;

		// RunPrologue/RunEpilogue compile to the host ABI (SysV on POSIX); the
		// emitted loop calls them with Win64 registers, so POSIX routes the
		// calls through register-shuffling thunks (shared by all workers).
		private static nint _posixPrologueThunk;
		private static nint _posixEpilogueThunk;
		private static readonly object PosixThunkGate = new();
		private GCHandle _selfHandle;
		private void* _controlBlock;
		private void* _loopStub;
		private nint _threadHandle;
		private uint _nativeThreadId;

		// Single in-flight run; publication is ordered by the work/done event pair.
		private CpuContext? _runContext;
		private GuestThreadState? _runState;
		private ulong _runGuestThreadHandle;
		private ulong _runSentinelRip;
		private ulong _runReturnSlotAddress;
		private nint _runHostRspSlot;
		private nint _runEntryStub;
		private ulong _runAffinityMask;
		private int _runNativeResult;
		private bool _runYieldRequested;
		private string? _runYieldReason;
		private bool _runForcedExit;
		private bool _runPrologueFailed;

		// Prologue -> epilogue carry, only touched on the worker thread.
		private DirectExecutionBackend? _prevBackend;
		private CpuContext? _prevContext;
		private ulong _prevSentinel;
		private ulong _prevReturnSlot;
		private bool _prevForcedExit;
		private bool _prevYieldRequested;
		private string? _prevYieldReason;
		private GuestThreadState? _prevState;
		private ulong _prevGuestThreadHandle;
		private nint _prevHostRspSlot;
		private int _prevHostThreadId;
		private bool _entered;

		private NativeGuestExecutor(DirectExecutionBackend backend)
		{
			_backend = backend;
			if (OperatingSystem.IsWindows())
			{
				_workAvailable = new AutoResetEvent(false);
				_workCompleted = new AutoResetEvent(false);
			}
		}

		public static NativeGuestExecutor? TryCreate(DirectExecutionBackend backend)
		{
			if (!EnsureKernel32Exports())
			{
				return null;
			}
			var executor = new NativeGuestExecutor(backend);
			if (!executor.Initialize())
			{
				executor.Dispose();
				return null;
			}
			return executor;
		}

		private static bool EnsureKernel32Exports()
		{
			if (_exitThreadAddress != 0)
			{
				return _waitForSingleObjectAddress != 0 && _setEventAddress != 0;
			}
			nint kernel32 = GetModuleHandle("kernel32.dll");
			if (kernel32 == 0)
			{
				return false;
			}
			_waitForSingleObjectAddress = GetProcAddress(kernel32, "WaitForSingleObject");
			_setEventAddress = GetProcAddress(kernel32, "SetEvent");
			_exitThreadAddress = GetProcAddress(kernel32, "ExitThread");
			return _waitForSingleObjectAddress != 0 && _setEventAddress != 0 && _exitThreadAddress != 0;
		}

		private bool Initialize()
		{
			_selfHandle = GCHandle.Alloc(this);
			_controlBlock = VirtualAlloc(null, 4096u, 12288u, 4u);
			if (_controlBlock == null)
			{
				return false;
			}
			_loopStub = VirtualAlloc(null, LoopStubSize, 12288u, 64u);
			if (_loopStub == null)
			{
				return false;
			}

			var prologuePtr = (nint)(delegate* unmanaged<nint, nint>)&RunPrologue;
			var epiloguePtr = (nint)(delegate* unmanaged<nint, int, void>)&RunEpilogue;
			var executorHandle = GCHandle.ToIntPtr(_selfHandle);
			nint workHandle;
			nint doneHandle;
			if (OperatingSystem.IsWindows())
			{
				workHandle = _workAvailable!.SafeWaitHandle.DangerousGetHandle();
				doneHandle = _workCompleted!.SafeWaitHandle.DangerousGetHandle();
			}
			else
			{
				lock (PosixThunkGate)
				{
					if (_posixPrologueThunk == 0)
					{
						_posixPrologueThunk = PosixHostStubs.CreateWin64ToSysVThunk(prologuePtr);
						_posixEpilogueThunk = PosixHostStubs.CreateWin64ToSysVThunk(epiloguePtr);
					}
				}
				prologuePtr = _posixPrologueThunk;
				epiloguePtr = _posixEpilogueThunk;
				_workSemaphore = PosixHostStubs.CreateWorkerEvent();
				_doneSemaphore = PosixHostStubs.CreateWorkerEvent();
				if (_workSemaphore == 0 || _doneSemaphore == 0)
				{
					return false;
				}
				workHandle = _workSemaphore;
				doneHandle = _doneSemaphore;
			}

			byte* code = (byte*)_loopStub;
			int offset = 0;
			void Emit(byte value) => code[offset++] = value;
			void EmitMovRcxImm64(ulong value)
			{
				Emit(0x48); Emit(0xB9);
				*(ulong*)(code + offset) = value;
				offset += sizeof(ulong);
			}
			void EmitMovRaxImm64(ulong value)
			{
				Emit(0x48); Emit(0xB8);
				*(ulong*)(code + offset) = value;
				offset += sizeof(ulong);
			}
			void EmitCallRax()
			{
				Emit(0xFF); Emit(0xD0);
			}
			void EmitSetDoneEvent()
			{
				EmitMovRcxImm64((ulong)doneHandle);
				EmitMovRaxImm64((ulong)_setEventAddress);
				EmitCallRax();
			}

			// Thread entry leaves RSP ≡ 8 (mod 16); after this every call below happens
			// at RSP ≡ 0 with shadow space available.
			Emit(0x48); Emit(0x83); Emit(0xEC); Emit(0x28); // sub rsp, 0x28
			int loopStart = offset;
			EmitMovRcxImm64((ulong)workHandle);
			Emit(0xBA);                                     // mov edx, INFINITE
			*(uint*)(code + offset) = 0xFFFFFFFFu;
			offset += sizeof(uint);
			EmitMovRaxImm64((ulong)_waitForSingleObjectAddress);
			EmitCallRax();
			EmitMovRaxImm64((ulong)_controlBlock);
			Emit(0x83); Emit(0x38); Emit(0x00);             // cmp dword [rax], 0
			Emit(0x0F); Emit(0x85);                         // jne stop
			int stopJump = offset;
			offset += sizeof(int);
			EmitMovRcxImm64((ulong)executorHandle);
			EmitMovRaxImm64((ulong)prologuePtr);
			EmitCallRax();                                  // rax = entry stub, or 0 on failure
			Emit(0x48); Emit(0x85); Emit(0xC0);             // test rax, rax
			Emit(0x0F); Emit(0x84);                         // je skipEntry
			int skipJump = offset;
			offset += sizeof(int);
			EmitCallRax();                                  // guest entry stub -> eax
			int skipEntryOffset = offset;
			Emit(0x89); Emit(0xC2);                         // mov edx, eax
			EmitMovRcxImm64((ulong)executorHandle);
			EmitMovRaxImm64((ulong)epiloguePtr);
			EmitCallRax();
			EmitSetDoneEvent();
			Emit(0xE9);                                     // jmp loop
			*(int*)(code + offset) = loopStart - (offset + sizeof(int));
			offset += sizeof(int);
			int stopOffset = offset;
			// Signal done so a Run() racing teardown cannot park forever; a stopped
			// worker is never re-rented, so the stale signal is unobservable.
			EmitSetDoneEvent();
			Emit(0x31); Emit(0xC9);                         // xor ecx, ecx
			EmitMovRaxImm64((ulong)_exitThreadAddress);
			EmitCallRax();
			Emit(0xCC);                                     // int3 (ExitThread never returns)
			*(int*)(code + stopJump) = stopOffset - (stopJump + sizeof(int));
			*(int*)(code + skipJump) = skipEntryOffset - (skipJump + sizeof(int));

			uint oldProtect = 0;
			if (!VirtualProtect(_loopStub, LoopStubSize, 32u, &oldProtect))
			{
				return false;
			}
			FlushInstructionCache(GetCurrentProcess(), _loopStub, LoopStubSize);
			return StartWorkerThread();
		}

		private bool RestartWorkerThread()
		{
			if (_loopStub == null || _controlBlock == null)
			{
				return false;
			}

			*(int*)_controlBlock = 0;
			return StartWorkerThread();
		}

		private bool StartWorkerThread()
		{
			_threadHandle = CreateThread(
				0,
				WorkerStackReservation,
				(nint)_loopStub,
				0,
				StackSizeParamIsAReservation,
				out _nativeThreadId);
			if (_threadHandle == 0)
			{
				return false;
			}
			if (LogThreadMode)
			{
				TraceThreadMode($"worker_created native_tid={_nativeThreadId} loop=0x{(ulong)_loopStub:X16}");
			}
			return true;
		}

		public int Run(
			CpuContext context,
			GuestThreadState? state,
			ulong guestThreadHandle,
			ulong sentinelRip,
			ulong returnSlotAddress,
			nint hostRspSlot,
			nint entryStub,
			ulong affinityMask,
			out bool yieldRequested,
			out string? yieldReason,
			out bool forcedExit)
		{
			_runContext = context;
			_runState = state;
			_runGuestThreadHandle = guestThreadHandle;
			_runSentinelRip = sentinelRip;
			_runReturnSlotAddress = returnSlotAddress;
			_runHostRspSlot = hostRspSlot;
			_runEntryStub = entryStub;
			_runAffinityMask = affinityMask;
			_runPrologueFailed = true;
			_runYieldRequested = false;
			_runYieldReason = null;
			_runForcedExit = false;
			SignalWorkAvailable();
			WaitWorkCompleted();

			// Normal path: RunEpilogue/ExitRun clears _entered before SetEvent(done).
			// TBB abort stub SetEvent's without ExitRun — _entered stays true.
			if (_entered)
			{
				var waitRc = WaitForSingleObject(_threadHandle, 500u);
				Console.Error.WriteLine(
					$"[LOADER][WARN] Native guest worker tid={_nativeThreadId} aborted during run; " +
					$"wait_rc=0x{waitRc:X8} respawning");
				Console.Error.Flush();
				if (_runState is { } abortedState)
				{
					_ = GuestThreadExecution.NotifyGuestThreadAbandoned(
						abortedState.ThreadHandle,
						"tbb_worker_abort");
					Volatile.Write(ref abortedState.HostThreadId, _prevHostThreadId);
				}
				_entered = false;
				if (_threadHandle != 0)
				{
					// Abort stub parks (no ExitThread). Force-kill the parked OS
					// thread so we can recreate the loop without process teardown.
					if (waitRc != 0u)
					{
						_ = TerminateThread(_threadHandle, unchecked((uint)(-1)));
						_ = WaitForSingleObject(_threadHandle, 1000u);
					}
					CloseHandle(_threadHandle);
					_threadHandle = 0;
					_nativeThreadId = 0;
				}
				if (!RestartWorkerThread())
				{
					_runPrologueFailed = true;
				}
				else
				{
					_runPrologueFailed = false;
					_runForcedExit = true;
					_runNativeResult = 0;
				}
			}

			_runContext = null;
			_runState = null;
			yieldRequested = _runYieldRequested;
			yieldReason = _runYieldReason;
			forcedExit = _runForcedExit;
			if (_runPrologueFailed)
			{
				// Never throw out of the native-worker rent path: an uncaught
				// exception mid-TBB storm kills the process with no FailFast
				// breadcrumb.
				var n = Interlocked.Increment(ref _tbbWorkerPrologueFaultCount);
				if (n <= 8 || n % 32 == 0)
				{
					Console.Error.WriteLine(
						$"[LOADER][ERROR] tbb_worker prologue fault #{n}; soft-fail run " +
						$"(tid={_nativeThreadId})");
					Console.Error.Flush();
				}

				yieldRequested = true;
				yieldReason = "tbb_worker_prologue_fault";
				forcedExit = true;
				return unchecked((int)0x80020012);
			}
			return _runNativeResult;
		}

		private void SignalWorkAvailable()
		{
			if (_workAvailable is not null)
			{
				_workAvailable.Set();
				return;
			}

			_ = PosixHostStubs.SignalWorkerEvent(_workSemaphore);
		}

		private void WaitWorkCompleted()
		{
			if (_workCompleted is not null)
			{
				_workCompleted.WaitOne();
				return;
			}

			_ = PosixHostStubs.WaitWorkerEvent(_doneSemaphore, -1);
		}

		[UnmanagedCallersOnly]
		private static nint RunPrologue(nint executorHandle)
		{
			try
			{
				var executor = (NativeGuestExecutor)GCHandle.FromIntPtr(executorHandle).Target!;
				return executor.EnterRun();
			}
			catch (Exception ex)
			{
				try
				{
					Console.Error.WriteLine(
						$"[LOADER][ERROR] Native guest worker prologue failed: {ex.GetType().Name}: {ex.Message}");
				}
				catch
				{
				}
				return 0;
			}
		}

		[UnmanagedCallersOnly]
		private static void RunEpilogue(nint executorHandle, int nativeResult)
		{
			try
			{
				var executor = (NativeGuestExecutor)GCHandle.FromIntPtr(executorHandle).Target!;
				executor.ExitRun(nativeResult);
			}
			catch (Exception ex)
			{
				try
				{
					Console.Error.WriteLine(
						$"[LOADER][ERROR] Native guest worker epilogue failed: {ex.GetType().Name}: {ex.Message}");
				}
				catch
				{
				}
			}
		}

		private nint EnterRun()
		{
			var backend = _backend;
			_prevBackend = _activeExecutionBackend;
			_prevContext = _activeCpuContext;
			_prevSentinel = _activeEntryReturnSentinelRip;
			_prevReturnSlot = _activeGuestReturnSlotAddress;
			_prevForcedExit = _activeForcedGuestExit;
			_prevYieldRequested = _activeGuestThreadYieldRequested;
			_prevYieldReason = _activeGuestThreadYieldReason;
			_prevState = _activeGuestThreadState;
			_prevHostRspSlot = TlsGetValue(backend._hostRspSlotTlsIndex);
			_prevGuestThreadHandle = GuestThreadExecution.EnterGuestThread(_runGuestThreadHandle);
			_entered = true;
			_activeExecutionBackend = backend;
			_activeCpuContext = _runContext;
			_activeEntryReturnSentinelRip = _runSentinelRip;
			_activeGuestReturnSlotAddress = _runReturnSlotAddress;
			_activeForcedGuestExit = false;
			_activeGuestThreadYieldRequested = false;
			_activeGuestThreadYieldReason = null;
			_activeGuestThreadState = _runState;
			backend.BindTlsBase(_runContext!);
			TlsSetValue(backend._hostRspSlotTlsIndex, _runHostRspSlot);
			if (backend._workerDoneEventTlsIndex != uint.MaxValue)
			{
				nint doneHandle = OperatingSystem.IsWindows()
					? _workCompleted!.SafeWaitHandle.DangerousGetHandle()
					: _doneSemaphore;
				TlsSetValue(backend._workerDoneEventTlsIndex, doneHandle);
			}
			if (backend._tbbAbortEligibleTlsIndex != uint.MaxValue)
			{
				nint eligible = _runState is { Name: "tbb_thead" } ? 1 : 0;
				TlsSetValue(backend._tbbAbortEligibleTlsIndex, eligible);
			}
			if (_runState is { } state)
			{
				_prevHostThreadId = Volatile.Read(ref state.HostThreadId);
				Volatile.Write(ref state.HostThreadId, unchecked((int)GetCurrentThreadId()));
			}
			if (_runAffinityMask != 0)
			{
				backend.ApplyGuestThreadAffinity(_runAffinityMask);
			}
			_runPrologueFailed = false;
			if (LogThreadMode)
			{
				TraceThreadMode(
					$"worker_enter guest=0x{_runGuestThreadHandle:X16} stub=0x{(ulong)_runEntryStub:X16}");
			}
			return _runEntryStub;
		}

		private void ExitRun(int nativeResult)
		{
			_runNativeResult = nativeResult;
			_runYieldRequested = _activeGuestThreadYieldRequested;
			_runYieldReason = _activeGuestThreadYieldReason;
			_runForcedExit = _activeForcedGuestExit;
			if (!_entered)
			{
				return;
			}
			_entered = false;
			if (_runState is { } state)
			{
				Volatile.Write(ref state.HostThreadId, _prevHostThreadId);
			}
			TlsSetValue(_backend._hostRspSlotTlsIndex, _prevHostRspSlot);
			if (_backend._workerDoneEventTlsIndex != uint.MaxValue)
			{
				TlsSetValue(_backend._workerDoneEventTlsIndex, 0);
			}
			if (_backend._tbbAbortEligibleTlsIndex != uint.MaxValue)
			{
				TlsSetValue(_backend._tbbAbortEligibleTlsIndex, 0);
			}
			GuestThreadExecution.RestoreGuestThread(_prevGuestThreadHandle);
			_activeExecutionBackend = _prevBackend;
			_activeCpuContext = _prevContext;
			_activeEntryReturnSentinelRip = _prevSentinel;
			_activeGuestReturnSlotAddress = _prevReturnSlot;
			_activeForcedGuestExit = _prevForcedExit;
			_activeGuestThreadYieldRequested = _prevYieldRequested;
			_activeGuestThreadYieldReason = _prevYieldReason;
			_activeGuestThreadState = _prevState;
			_prevBackend = null;
			_prevContext = null;
			_prevState = null;
			_prevYieldReason = null;
			if (LogThreadMode)
			{
				TraceThreadMode(
					$"worker_exit guest=0x{_runGuestThreadHandle:X16} result=0x{nativeResult:X8} yield={_runYieldRequested}");
			}
		}

		public void Dispose()
		{
			if (_controlBlock != null)
			{
				*(int*)_controlBlock = 1;
			}
			try
			{
				SignalWorkAvailable();
			}
			catch (ObjectDisposedException)
			{
			}
			var exited = _threadHandle == 0;
			if (_threadHandle != 0)
			{
				exited = WaitForSingleObject(_threadHandle, 1000u) == 0u;
				CloseHandle(_threadHandle);
				_threadHandle = 0;
			}
			if (!exited)
			{
				// The worker is still parked in guest code (teardown should have unwound
				// it first). Leak the stub, control block, events and GC handle rather
				// than have the thread execute freed memory.
				Console.Error.WriteLine(
					$"[LOADER][WARN] Native guest worker tid={_nativeThreadId} did not stop; leaking its run loop.");
				return;
			}
			if (_loopStub != null)
			{
				VirtualFree(_loopStub, 0u, 32768u);
				_loopStub = null;
			}
			if (_controlBlock != null)
			{
				VirtualFree(_controlBlock, 0u, 32768u);
				_controlBlock = null;
			}
			if (_selfHandle.IsAllocated)
			{
				_selfHandle.Free();
			}
			_workAvailable?.Dispose();
			_workCompleted?.Dispose();
			if (_workSemaphore != 0)
			{
				PosixHostStubs.DestroyWorkerEvent(_workSemaphore);
				_workSemaphore = 0;
			}
			if (_doneSemaphore != 0)
			{
				PosixHostStubs.DestroyWorkerEvent(_doneSemaphore);
				_doneSemaphore = 0;
			}
		}
	}
}
