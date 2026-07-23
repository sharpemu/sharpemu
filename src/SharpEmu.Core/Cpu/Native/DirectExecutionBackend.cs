// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Debugging;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe partial class DirectExecutionBackend : INativeCpuBackend, IGuestThreadScheduler, IDisposable
{
	private static readonly SharpEmu.Logging.SharpEmuLogger Log = SharpEmu.Logging.SharpEmuLog.For("Native");

	private static readonly bool LogThreadMode =
		string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_THREAD_MODE"), "1", StringComparison.Ordinal);

	private static void TraceThreadMode(string message)
	{
		Console.Error.WriteLine(
			$"[THREADMODE] {message} tid={GetCurrentThreadId()} managed={Environment.CurrentManagedThreadId}");
		Console.Error.Flush();
	}

	private const int ImportLoopHistoryLength = 2048;

	private const int ImportLoopWideDiversityWindow = 768;

	private const int DefaultImportLoopGuardSeconds = 5;

	private readonly struct ImportStubEntry
	{
		public ulong Address { get; }

		public string Nid { get; }

		public ExportedFunction? Export { get; }

		// Precomputed per-import classification: DispatchImport runs for
		// every guest HLE call, so per-call string pattern matches and NID
		// hashing are hoisted to stub-setup time.
		public bool IsLeaf { get; }

		public bool IsNoBlockLeaf { get; }

		public bool SuppressStrlenTrace { get; }

		public bool IsLoopGuardBoundary { get; }

		public ulong NidHash { get; }

		public ImportStubEntry(
			ulong address,
			string nid,
			ExportedFunction? export,
			bool isLeaf,
			bool isNoBlockLeaf,
			bool suppressStrlenTrace,
			bool isLoopGuardBoundary,
			ulong nidHash)
		{
			Address = address;
			Nid = nid;
			Export = export;
			IsLeaf = isLeaf;
			IsNoBlockLeaf = isNoBlockLeaf;
			SuppressStrlenTrace = suppressStrlenTrace;
			IsLoopGuardBoundary = isLoopGuardBoundary;
			NidHash = nidHash;
		}
	}

	private readonly record struct RecentImportTraceEntry(
		long DispatchIndex,
		string Nid,
		ulong ReturnRip,
		ulong Arg0,
		ulong Arg1,
		ulong Arg2,
		ulong GuestThreadHandle,
		int ManagedThreadId);

	private readonly record struct DeferredBootstrapTraceEntry(
		long DispatchIndex,
		ulong Op,
		ulong SymbolPointer,
		ulong OutputPointer,
		ulong ReturnRip);

#pragma warning disable CS0649
	private struct EXCEPTION_POINTERS
	{
		public unsafe EXCEPTION_RECORD* ExceptionRecord;

		public unsafe void* ContextRecord;
	}

	private struct EXCEPTION_RECORD
	{
		public uint ExceptionCode;

		public uint ExceptionFlags;

		public unsafe EXCEPTION_RECORD* ExceptionRecord;

		public unsafe void* ExceptionAddress;

		public uint NumberParameters;

		public unsafe fixed ulong ExceptionInformation[15];
	}
#pragma warning restore CS0649

	private delegate int ExceptionHandlerDelegate(void* exceptionInfo);

#pragma warning disable CS0649
	private struct MEMORY_BASIC_INFORMATION64
	{
		public ulong BaseAddress;

		public ulong AllocationBase;

		public uint AllocationProtect;

		public uint __alignment1;

		public ulong RegionSize;

		public uint State;

		public uint Protect;

		public uint Type;

		public uint __alignment2;
	}
#pragma warning restore CS0649

	private const ulong SYSTEM_RESERVED = 34359738368uL;

	private const ulong CODE_BASE_OFFSET = 4294967296uL;

	private const ulong CODE_BASE_INCR = 268435456uL;

	private const ulong GuestImageScanStart = 34359738368uL;

	private const ulong GuestImageScanEnd = 36507222016uL;

	// See CpuDispatcher: the 0x7FFx window is Windows-only; POSIX hosts
	// (dyld shared cache, Rosetta 2 runtime) use 0x6FFx instead.
	private static readonly ulong GuestThreadStackBaseAddress = OperatingSystem.IsWindows() ? 0x7FFF_E000_0000UL : 0x6FFF_E000_0000UL;

	private static readonly ulong GuestThreadTlsBaseAddress = OperatingSystem.IsWindows() ? 0x7FFE_0000_0000UL : 0x6FFE_0000_0000UL;

	private const ulong GuestThreadStackSize = 0x0020_0000UL;

	private const ulong GuestThreadTlsSize = 0x0001_0000UL;

	// Matches CpuDispatcher.TlsPrefixSize: static TLS blocks sit below the
	// thread pointer and PS5 modules can reach beyond one host page.
	private const ulong GuestThreadTlsPrefixSize = GuestTlsTemplate.StartupStaticTlsReservation;

	private const ulong GuestThreadRegionStride = 0x0100_0000UL;

	// Unity titles routinely create more than 64 workers once native plugins,
	// lighting, streaming, and audio are active at the same time. Keep a broad
	// deterministic address window for their stack and TLS regions.
	private const int GuestThreadRegionSlots = 1024;

	[ThreadStatic]
	private static List<(IVirtualMemory Memory, ulong Base)>? _nestedGuestCallbackStacks;

	[ThreadStatic]
	private static int _nestedGuestCallbackDepth;

	private const uint PAGE_EXECUTE_READWRITE = 64u;

	private const uint PAGE_READWRITE = 4u;

	private const uint PAGE_EXECUTE_READ = 32u;

	private const int TlsHandlerRegionSize = 16384;

	private const ulong TlsModuleAllocStart = 140726751354880uL;

	private const ulong TlsModuleAllocStride = 65536uL;

	private readonly IModuleManager _moduleManager;

	private nint _tlsHandlerAddress;

	private nint _tlsBaseAddress;

	private nint _ownedTlsBaseAddress;

	private bool _ownsTlsBaseAddress;

	private uint _guestTlsBaseTlsIndex = uint.MaxValue;

	private uint _hostRspSlotTlsIndex = uint.MaxValue;

	private nint _tlsGetValueAddress;

	private nint _queryPerformanceCounterAddress;

	private nint _switchToThreadAddress;

	private nint _sleepAddress;

	private int _tlsPatchStubOffset;

	private nint _unresolvedReturnStub;

	private nint _guestReturnStub;

	private nint _workerAbortStub;
	private nint _vehManagedEntryLock;

	private uint _workerDoneEventTlsIndex = uint.MaxValue;

	private uint _tbbAbortEligibleTlsIndex = uint.MaxValue;

	private nint _setEventAddress;

	private nint _rawExceptionHandler;

	private nint _rawExceptionHandlerStub;

	private nint _exceptionHandler;

	private nint _exceptionHandlerStub;

	private nint _unhandledFilterStub;

	private nint _lowIndexedTableScratch;

	private nint _stackGuardCompareScratch;

	private nint _nullObjectStoreScratch;

	private readonly Dictionary<uint, nint> _tlsModuleBases = new Dictionary<uint, nint>();

	private ulong _entryPoint;

	private CpuContext? _cpuContext;

	// Debugger seam; both null when no debugger is attached.
	private ICpuDebugHook? _debugHook;

	private ICpuDebugFrame? _activeDebugFrame;

	[ThreadStatic]
	private static DirectExecutionBackend? _activeExecutionBackend;

	[ThreadStatic]
	private static CpuContext? _activeCpuContext;

	[ThreadStatic]
	private static ulong _activeEntryReturnSentinelRip;

	[ThreadStatic]
	private static ulong _activeGuestReturnSlotAddress;

	[ThreadStatic]
	private static bool _activeForcedGuestExit;

	[ThreadStatic]
	private static bool _activeGuestThreadYieldRequested;

	[ThreadStatic]
	private static string? _activeGuestThreadYieldReason;

	[ThreadStatic]
	private static GuestThreadState? _activeGuestThreadState;

	[ThreadStatic]
	private static DirectExecutionBackend? _importCounterOwner;

	[ThreadStatic]
	private static long _nextImportDispatchIndex;

	[ThreadStatic]
	private static long _importDispatchBlockEnd;

	private ImportStubEntry[] _importEntries = Array.Empty<ImportStubEntry>();

	private readonly List<nint> _importHandlerTrampolines = new List<nint>();

	private const int GuestContextTransferFrameQwords = 20;

	private readonly object _guestContextTransferStubGate = new();

	private readonly ThreadLocal<nint> _guestContextTransferFrames = new(
		static () => (nint)NativeMemory.AllocZeroed(GuestContextTransferFrameQwords, sizeof(ulong)),
		trackAllValues: true);

	private nint _guestContextTransferStub;

	private long _importDispatchCount;

	private const int ImportDispatchBlockSize = 256;

	private KeyValuePair<string, ulong>[] _runtimeSymbolsByAddress = Array.Empty<KeyValuePair<string, ulong>>();

	private readonly ConcurrentDictionary<string, ulong> _runtimeSymbolsByName =
		new(StringComparer.Ordinal);

	// Keep in sync with SelfLoader import-stub mapping constants.
	private const ulong ImportStubRegionCanonicalBase = 0x0000_7000_0000_0000UL;

	private const ulong ImportStubRegionAddressStride = 0x0000_0000_0100_0000UL;

	private const ulong LazyImportStubSlotSize = 0x10;

	private const ulong ImportStubRegionPageSize = 0x1000UL;

	private const string KernelDynlibDlsymAerolibNid = "LwG8g3niqwA";

	private readonly object _lazyDlsymStubGate = new();

	private readonly Dictionary<string, ulong> _lazyDlsymStubCache = new(StringComparer.Ordinal);

	private ulong _lazyImportStubPoolBase;

	private ulong _lazyImportStubNextSlot;

	private ulong _lazyImportStubPoolLimit;

	private bool _lazyImportStubPoolMapped;

	private readonly RecentImportTraceEntry[] _recentImportTrace = new RecentImportTraceEntry[64];

	private int _recentImportTraceCount;

	private int _recentImportTraceWriteIndex;

	private readonly DeferredBootstrapTraceEntry[] _deferredBootstrapTrace = new DeferredBootstrapTraceEntry[32];

	private int _deferredBootstrapTraceCount;

	private int _deferredBootstrapTraceWriteIndex;

	private readonly object _deferredBootstrapTraceGate = new();

	private readonly string[] _distinctImportNidHistory = new string[128];

	private int _distinctImportNidHistoryCount;

	private int _distinctImportNidHistoryWriteIndex;

	private string _lastDistinctImportNid = string.Empty;

	private int _consecutiveStrlenImports;

	private bool _strlenPreludeLogged;

	private bool _logStrlenImports;

	private bool _logStrlenBursts;

	private bool _logGuestContext;

	private bool _ignoreGuestInt41;

	private int _ignoredGuestInt41Count;

	private bool _logGuestThreads;

	private bool _logUsleep;

	private bool _logFiber;

	private bool _logBootstrap;

	private bool _logAllImports;

	private bool _logImportFrames;

	private bool _logImportRecent;

	private bool _logStackCheck;

	private string? _probeImportReturn;

	private ulong _probeImportReturnAddress;

	private long _probeImportReturnAddressCount;

	private string? _importFilter;

	private bool _disableImportLoopGuard;

	private int _importLoopGuardSeconds;

	private readonly HashSet<ulong> _patchedResolverReturnSites = new HashSet<ulong>();

	private readonly HashSet<ulong> _patchedTlsImmediateThunkTargets = new HashSet<ulong>();

	private readonly HashSet<ulong> _contextualUnresolvedReturnSites = new HashSet<ulong>();

	private readonly object _lazyCommitRangeGate = new object();

	private readonly List<LazyCommitRange> _prtLazyCommitRanges = new List<LazyCommitRange>();

	private ulong _returnFallbackTarget;

	private static int _rawSentinelRecoveries;

	private int _lastReportedRawSentinelRecoveries;

	private static ulong _globalFallbackTarget;

	private static ulong _globalUnresolvedReturnStub;

	private nint _hostRspSlotStorage;

	private bool _patchedEa020eLookupCall;

	private ulong _entryReturnSentinelRip;

	private readonly ulong[] _importLoopSignatures = new ulong[ImportLoopHistoryLength];

	private readonly ulong[] _importLoopNidHashes = new ulong[ImportLoopHistoryLength];

	private readonly ulong[] _importLoopReturnRips = new ulong[ImportLoopHistoryLength];

	private int _importLoopSignatureCount;

	private int _importLoopSignatureWriteIndex;

	private int _importLoopPatternHits;

	private long _importLoopPatternStartTimestamp;


	private enum GuestThreadRunState
	{
		Ready,
		Running,
		Blocked,
		Exited,
		Faulted,
	}

	private enum GuestNativeCallExitReason
	{
		Returned,
		Blocked,
		ForcedExit,
		Exception,
	}

	private sealed class GuestThreadState
	{
		public ulong ThreadHandle { get; init; }

		public ulong EntryPoint { get; init; }

		public ulong Argument { get; init; }

		public string Name { get; init; } = string.Empty;

		public int Priority { get; set; }

		public ulong AffinityMask { get; set; }

		public CpuContext Context { get; set; } = null!;

		public bool IsExternalExecutor { get; init; }

		public ulong StackBase { get; init; }

		public ulong StackSize { get; init; }

		public ulong ExceptionStackBase { get; set; }

		public GuestThreadRunState State { get; set; }

		public ulong ExitValue { get; set; }

		public string? BlockReason { get; set; }

		public bool HasBlockedContinuation { get; set; }

		public GuestCpuContinuation BlockedContinuation { get; set; }

		public string? BlockWakeKey { get; set; }

		// Stays set through the wake transition; Resume() consumes it when the thread pumps.
		public IGuestThreadBlockWaiter? BlockWaiter { get; set; }

		public long BlockDeadlineTimestamp { get; set; }

		public long ImportCount;

		public string? LastImportNid;

		public ulong LastReturnRip;

		// Busy guest workers overwrite the global recent-import ring. Preserve
		// the most recent complete SysV call frame per guest thread so native
		// fault diagnostics can identify the exact preceding HLE invocation.
		public ulong LastImportRdi;
		public ulong LastImportRsi;
		public ulong LastImportRdx;
		public ulong LastImportRcx;
		public ulong LastImportR8;
		public ulong LastImportR9;
		public ulong LastImportStack0;
		public ulong LastImportStack1;
		public ulong LastImportStack2;
		public ulong LastImportStack3;
		public ulong LastImportStack4;
		public ulong LastImportStack5;
		public ulong LastImportRax;
		public int LastImportResultValid;

		public Thread? HostThread { get; set; }

		public int HostThreadId;

		// State may become Ready as soon as another guest thread satisfies this
		// thread's wait, while the host executor that yielded it is still
		// unwinding. Keep executor ownership separate from State so a Ready
		// continuation cannot be claimed concurrently with that unwind.
		public bool ExecutorActive { get; set; }

		public bool ExceptionDeliveryActive { get; set; }

		public long ExecutorClaimDeferrals { get; set; }

		public GuestContinuationRunner? ContinuationRunner { get; set; }

		public GuestExecutionRunner? ExecutionRunner { get; set; }
	}

	private sealed class ExternalGuestThreadState
	{
		public CpuContext Context { get; set; } = null!;

		public ulong ExceptionStackBase { get; set; }
	}

	private readonly record struct PendingGuestException(
		ulong Handler,
		int ExceptionType,
		ulong ExceptionStackBase);

	private sealed class GuestContinuationRunner : IDisposable
	{
		private readonly ulong _guestThreadHandle;
		private readonly object _runGate = new();
		private readonly AutoResetEvent _workAvailable = new(false);
		private readonly AutoResetEvent _workCompleted = new(false);
		private readonly Thread _thread;
		private Action? _work;
		private volatile bool _stopping;

		public GuestContinuationRunner(ulong guestThreadHandle, ThreadPriority priority)
		{
			_guestThreadHandle = guestThreadHandle;
			_thread = new Thread(ThreadMain)
			{
				IsBackground = true,
				Name = $"GuestContinuation-{guestThreadHandle:X}",
				Priority = priority,
			};
			_thread.Start();
		}

		public bool IsCurrentThread => ReferenceEquals(Thread.CurrentThread, _thread);

		public void Run(Action work)
		{
			lock (_runGate)
			{
				_work = work;
				_workAvailable.Set();
				_workCompleted.WaitOne();
				_work = null;
			}
		}

		private void ThreadMain()
		{
			var previousGuestThreadHandle = GuestThreadExecution.EnterGuestThread(_guestThreadHandle);
			try
			{
				while (true)
				{
					_workAvailable.WaitOne();
					if (_stopping)
					{
						return;
					}

					try
					{
						_work?.Invoke();
					}
					finally
					{
						_workCompleted.Set();
					}
				}
			}
			finally
			{
				GuestThreadExecution.RestoreGuestThread(previousGuestThreadHandle);
			}
		}

		public void Dispose()
		{
			_stopping = true;
			_workAvailable.Set();
			if (!IsCurrentThread)
			{
				_thread.Join(500);
			}
			_workAvailable.Dispose();
			_workCompleted.Dispose();
		}
	}

	// A guest pthread may block and resume hundreds of times per second. Creating
	// a new host Thread for every resume is especially costly for audio workers
	// and eventually floods macOS with short-lived FMOD threads. Keep one dormant
	// host executor per guest pthread and signal it for each runnable slice.
	private sealed class GuestExecutionRunner : IDisposable
	{
		private readonly object _gate = new();
		private readonly AutoResetEvent _workAvailable = new(false);
		private readonly Thread _thread;
		private Action? _work;
		private volatile bool _stopping;

		public GuestExecutionRunner(ulong guestThreadHandle, string name, ThreadPriority priority)
		{
			_thread = new Thread(() => ThreadMain(guestThreadHandle))
			{
				IsBackground = true,
				Name = $"SharpEmu-{name}",
				Priority = priority,
			};
			_thread.Start();
		}

		public void Schedule(Action work)
		{
			lock (_gate)
			{
				if (_stopping)
				{
					return;
				}
				if (_work is not null)
				{
					throw new InvalidOperationException("Guest execution runner already has pending work.");
				}
				_work = work;
				_workAvailable.Set();
			}
		}

		private void ThreadMain(ulong guestThreadHandle)
		{
			var previousGuestThreadHandle = GuestThreadExecution.EnterGuestThread(guestThreadHandle);
			try
			{
				while (true)
				{
					_workAvailable.WaitOne();
					if (_stopping)
					{
						return;
					}

					Action? work;
					lock (_gate)
					{
						work = _work;
						_work = null;
					}
					work?.Invoke();
				}
			}
			finally
			{
				GuestThreadExecution.RestoreGuestThread(previousGuestThreadHandle);
			}
		}

		public void Dispose()
		{
			_stopping = true;
			_workAvailable.Set();
			if (!ReferenceEquals(Thread.CurrentThread, _thread))
			{
				_thread.Join(500);
			}
			_workAvailable.Dispose();
		}
	}

	private readonly record struct LazyCommitRange(ulong BaseAddress, ulong Size);

	private readonly object _guestThreadGate = new object();

	// Diagnostic owner tracking for _guestThreadGate; written only while the
	// gate is held, read lock-free by the stall watchdog's periodic snapshot.
	private volatile string? _gateOwnerSite;
	private int _gateOwnerManagedThreadId;
	private long _gateAcquireTimestamp;

	private GateHolder LockGate(string site)
	{
		Monitor.Enter(_guestThreadGate);
		_gateOwnerSite = site;
		Volatile.Write(ref _gateOwnerManagedThreadId, Environment.CurrentManagedThreadId);
		Volatile.Write(ref _gateAcquireTimestamp, Stopwatch.GetTimestamp());
		return new GateHolder(this);
	}

	private readonly struct GateHolder : IDisposable
	{
		private readonly DirectExecutionBackend _owner;

		public GateHolder(DirectExecutionBackend owner)
		{
			_owner = owner;
		}

		public void Dispose()
		{
			_owner._gateOwnerSite = null;
			Volatile.Write(ref _owner._gateOwnerManagedThreadId, 0);
			Monitor.Exit(_owner._guestThreadGate);
		}
	}

	private readonly Queue<GuestThreadState> _readyGuestThreads = new Queue<GuestThreadState>();

	private int _readyGuestThreadCount;

	private readonly Dictionary<ulong, GuestThreadState> _guestThreads = new Dictionary<ulong, GuestThreadState>();

	private readonly Dictionary<ulong, ExternalGuestThreadState> _externalGuestThreads = new Dictionary<ulong, ExternalGuestThreadState>();

	[ThreadStatic]
	private static ulong _currentExternalGuestThreadHandle;

	private readonly Dictionary<ulong, PendingGuestException> _pendingGuestExceptions = new Dictionary<ulong, PendingGuestException>();

	// Import dispatch is the hottest managed path in UE titles. Most imports do
	// not have an exception queued, so publish the dictionary population and let
	// safe points skip _guestThreadGate entirely in the common case.
	private int _pendingGuestExceptionCount;

	private readonly HashSet<ulong> _activeGuestExceptionDeliveries = new HashSet<ulong>();

	private int _guestThreadPumpDepth;

	private bool _guestThreadYieldRequested;

	private string? _guestThreadYieldReason;

	private volatile bool _forcedGuestExit;

	private ulong _lastAvTraceRip;

	private ulong _lastAvTraceType;

	private ulong _lastAvTraceTarget;

	private int _lastAvTraceRepeatCount;

	private long _lastProgressTimestamp;

	private int _stallWatchdogTriggered;

	private volatile bool _stallWatchdogStop;

	private Thread? _stallWatchdogThread;

	private volatile bool _readyDispatchStop;

	private Thread? _readyDispatchThread;

	private GCHandle _selfHandle;

	private nint _selfHandlePtr;

	private const int MinTlsPatchInstructionBytes = 9;

	private delegate ulong ImportGatewayDelegate(nint backendHandle, int importIndex, nint argPackPtr);
	private delegate int RawExceptionHandlerDelegate(void* exceptionInfo);
	private static readonly ImportGatewayDelegate ImportGatewayDelegateInstance = ImportDispatchGatewayManaged;
	private static readonly RawExceptionHandlerDelegate RawVectoredHandlerDelegateInstance = RawVectoredHandlerManaged;
	private static readonly RawExceptionHandlerDelegate RawUnhandledFilterDelegateInstance = RawUnhandledFilterManaged;

	private static readonly nint ImportGatewayPtr = ResolveWin64CallbackPtr(
		Marshal.GetFunctionPointerForDelegate(ImportGatewayDelegateInstance));

	// Emitted trampolines call managed callbacks with the Win64 ABI. On
	// Windows the runtime already compiles them that way; on POSIX .NET they
	// are SysV, so route through a Win64->SysV thunk.
	private static nint ResolveWin64CallbackPtr(nint sysvPtr) =>
		OperatingSystem.IsWindows() ? sysvPtr : PosixHostStubs.CreateWin64ToSysVThunk(sysvPtr);

	private static readonly nint RawVectoredHandlerPtrManaged =
		Marshal.GetFunctionPointerForDelegate(RawVectoredHandlerDelegateInstance);

	private static readonly nint RawUnhandledFilterPtrManaged =
		Marshal.GetFunctionPointerForDelegate(RawUnhandledFilterDelegateInstance);

	private const int CTX_MXCSR = 52;

	private const int CTX_RAX = 120;

	private const int CTX_RCX = 128;

	private const int CTX_RDX = 136;

	private const int CTX_RBX = 144;

	private const int CTX_RSP = 152;

	private const int CTX_RBP = 160;

	private const int CTX_RSI = 168;

	private const int CTX_RDI = 176;

	private const int CTX_R8 = 184;

	private const int CTX_R9 = 192;

	private const int CTX_R10 = 200;

	private const int CTX_R11 = 208;

	private const int CTX_R12 = 216;

	private const int CTX_R13 = 224;

	private const int CTX_R14 = 232;

	private const int CTX_R15 = 240;

	private const int CTX_RIP = 248;

	private ExceptionHandlerDelegate? _handlerDelegate;

	private GCHandle _handlerHandle;

	private ExceptionHandlerDelegate? _unhandledFilterDelegate;

	private GCHandle _unhandledFilterHandle;

	[ThreadStatic]
	private static int _vectoredHandlerDepth;

	private static int _nestedVehTraceCount;

	private const uint MEM_COMMIT = 4096u;

	private const uint MEM_RESERVE = 8192u;

	private const uint MEM_FREE = 65536u;

	private const uint MEM_RELEASE = 32768u;

	private const uint PAGE_EXECUTE = 16u;

	private const uint PAGE_EXECUTE_WRITECOPY = 128u;

	private const uint PAGE_GUARD = 256u;

	private const uint PAGE_NOACCESS = 1u;

	private const uint DBG_PRINTEXCEPTION_C = 0x40010006u;

	private const uint DBG_PRINTEXCEPTION_WIDE_C = 0x4001000Au;

	private const uint MS_VC_THREADNAME_EXCEPTION = 0x406D1388u;

	private const uint MSVC_CPP_EXCEPTION = 0xE06D7363u;

	private const uint HostXmmSaveAreaSize = 0xA0u;

	private const uint ContextAmd64ControlInteger = 0x00100003u;

	private const uint ThreadGetContext = 0x0008u;

	private const uint ThreadSuspendResume = 0x0002u;

	private const int Win64ContextSize = 0x4D0;

	private const int Win64ContextFlagsOffset = 0x30;

	private readonly record struct HostThreadContextSnapshot(
		bool IsValid,
		ulong Rip,
		ulong Rsp,
		ulong Rbp,
		ulong Rax,
		ulong Rbx,
		ulong Rcx,
		ulong Rdx);

	public string BackendName => "native-backend";

	public string? LastError { get; private set; }

	private unsafe static ulong ReadCtxU64(void* contextRecord, int offset)
	{
		return *(ulong*)((byte*)contextRecord + offset);
	}

	private unsafe static int CallNativeEntry(void* entry)
	{
		var nativeEntry = (delegate* unmanaged[Cdecl]<int>)entry;
		return nativeEntry();
	}

	private unsafe static void WriteCtxU64(void* contextRecord, int offset, ulong value)
	{
		*(ulong*)((byte*)contextRecord + offset) = value;
	}

	private unsafe static uint ReadCtxU32(void* contextRecord, int offset)
	{
		return *(uint*)((byte*)contextRecord + offset);
	}

	private unsafe static void WriteCtxU32(void* contextRecord, int offset, uint value)
	{
		*(uint*)((byte*)contextRecord + offset) = value;
	}

	private bool HasActiveExecutionThread => ReferenceEquals(_activeExecutionBackend, this);

	/// <summary>
	/// Binds the debug frame view the dispatcher created for the frame about to
	/// run, so stall notifications reference the same frame the debugger saw at
	/// entry. Set to null when no debugger is attached.
	/// </summary>
	internal void SetActiveDebugFrame(ICpuDebugFrame? frame) => _activeDebugFrame = frame;

	/// <summary>
	/// Notifies an attached debugger of a detected execution stall. No-op when no
	/// debugger is attached or no frame is bound. The debugger may block here to
	/// present a break before the backend forces the guest out of the loop.
	/// </summary>
	private void NotifyDebuggerStall(
		CpuStallKind kind,
		in ImportStubEntry import,
		ulong instructionPointer,
		long dispatchIndex,
		ulong argument0,
		ulong argument1)
	{
		var hook = _debugHook;
		var frame = _activeDebugFrame;
		if (hook is null || frame is null)
		{
			return;
		}

		var export = import.Export;
		var exportDescription = export is null ? "unresolved" : $"{export.LibraryName}:{export.Name}";
		var detail = $"kind={kind}, nid={import.Nid}, export={exportDescription}, dispatch#{dispatchIndex}, " +
			$"rip=0x{instructionPointer:X16}, arg0=0x{argument0:X16}, arg1=0x{argument1:X16}";
		hook.OnStall(frame, new CpuStallInfo(
			kind,
			import.Nid,
			instructionPointer,
			dispatchIndex,
			argument0,
			argument1,
			detail,
			export?.LibraryName,
			export?.Name));
	}

	private CpuContext? ActiveCpuContext => HasActiveExecutionThread ? _activeCpuContext : _cpuContext;

	private ulong ActiveEntryReturnSentinelRip
	{
		get => HasActiveExecutionThread ? _activeEntryReturnSentinelRip : _entryReturnSentinelRip;
		set
		{
			if (HasActiveExecutionThread)
			{
				_activeEntryReturnSentinelRip = value;
			}
			else
			{
				_entryReturnSentinelRip = value;
			}
		}
	}

	private ulong ActiveGuestReturnSlotAddress =>
		HasActiveExecutionThread ? _activeGuestReturnSlotAddress : 0;

	private bool ActiveForcedGuestExit
	{
		// Host shutdown is requested from a UI or VideoOut thread. Native guest
		// workers have their own thread-local execution state, so they must also
		// observe the backend-wide shutdown flag.
		get => _forcedGuestExit || (HasActiveExecutionThread && _activeForcedGuestExit);
		set
		{
			if (HasActiveExecutionThread)
			{
				_activeForcedGuestExit = value;
			}
			else
			{
				_forcedGuestExit = value;
			}
		}
	}

	private bool ActiveGuestThreadYieldRequested
	{
		get => HasActiveExecutionThread ? _activeGuestThreadYieldRequested : _guestThreadYieldRequested;
		set
		{
			if (HasActiveExecutionThread)
			{
				_activeGuestThreadYieldRequested = value;
			}
			else
			{
				_guestThreadYieldRequested = value;
			}
		}
	}

	private string? ActiveGuestThreadYieldReason
	{
		get => HasActiveExecutionThread ? _activeGuestThreadYieldReason : _guestThreadYieldReason;
		set
		{
			if (HasActiveExecutionThread)
			{
				_activeGuestThreadYieldReason = value;
			}
			else
			{
				_guestThreadYieldReason = value;
			}
		}
	}

	private static void RestoreActiveExecutionThread(
		DirectExecutionBackend? previousBackend,
		CpuContext? previousContext,
		ulong previousSentinel,
		ulong previousReturnSlotAddress,
		bool previousForcedExit,
		bool previousYieldRequested,
		string? previousYieldReason)
	{
		_activeExecutionBackend = previousBackend;
		_activeCpuContext = previousContext;
		_activeEntryReturnSentinelRip = previousSentinel;
		_activeGuestReturnSlotAddress = previousReturnSlotAddress;
		_activeForcedGuestExit = previousForcedExit;
		_activeGuestThreadYieldRequested = previousYieldRequested;
		_activeGuestThreadYieldReason = previousYieldReason;
	}

	public unsafe DirectExecutionBackend(IModuleManager moduleManager)
	{
		_moduleManager = moduleManager ?? throw new ArgumentNullException("moduleManager");
		_selfHandle = GCHandle.Alloc(this);
		_selfHandlePtr = GCHandle.ToIntPtr(_selfHandle);
		_guestTlsBaseTlsIndex = TlsAlloc();
		_hostRspSlotTlsIndex = TlsAlloc();
		_workerDoneEventTlsIndex = OperatingSystem.IsWindows() ? TlsAlloc() : uint.MaxValue;
		_tbbAbortEligibleTlsIndex = OperatingSystem.IsWindows() ? TlsAlloc() : uint.MaxValue;
		if (_guestTlsBaseTlsIndex == uint.MaxValue || _hostRspSlotTlsIndex == uint.MaxValue)
		{
			throw new OutOfMemoryException("Failed to allocate native TLS slots");
		}
		if (OperatingSystem.IsWindows())
		{
			nint kernel32 = GetModuleHandle("kernel32.dll");
			_tlsGetValueAddress = kernel32 != 0 ? GetProcAddress(kernel32, "TlsGetValue") : 0;
			if (_tlsGetValueAddress == 0)
			{
				throw new InvalidOperationException("Failed to resolve kernel32!TlsGetValue");
			}
			_queryPerformanceCounterAddress = kernel32 != 0 ? GetProcAddress(kernel32, "QueryPerformanceCounter") : 0;
			if (_queryPerformanceCounterAddress == 0)
			{
				throw new InvalidOperationException("Failed to resolve kernel32!QueryPerformanceCounter");
			}
			_switchToThreadAddress = kernel32 != 0 ? GetProcAddress(kernel32, "SwitchToThread") : 0;
			_sleepAddress = kernel32 != 0 ? GetProcAddress(kernel32, "Sleep") : 0;
			if (_switchToThreadAddress == 0 || _sleepAddress == 0)
			{
				throw new InvalidOperationException("Failed to resolve kernel32 thread timing functions");
			}
			_setEventAddress = kernel32 != 0 ? GetProcAddress(kernel32, "SetEvent") : 0;
		}
		else
		{
			// Win64-ABI-compatible helper stubs so the emitted call sites do
			// not need to change per platform.
			_tlsGetValueAddress = PosixHostStubs.TlsGetValueStubAddress;
			_queryPerformanceCounterAddress = PosixHostStubs.QueryPerformanceCounterStubAddress;
			_switchToThreadAddress = PosixHostStubs.SwitchToThreadStubAddress;
			_sleepAddress = PosixHostStubs.SleepStubAddress;
		}
		_tlsBaseAddress = (nint)VirtualAlloc(null, 4096u, 12288u, 4u);
		if (_tlsBaseAddress == 0)
		{
			throw new OutOfMemoryException("Failed to allocate TLS base");
		}
		_ownedTlsBaseAddress = _tlsBaseAddress;
		_ownsTlsBaseAddress = true;
		SeedTlsLayout(_tlsBaseAddress);
		_hostRspSlotStorage = (nint)VirtualAlloc(null, 4096u, 12288u, 4u);
		if (_hostRspSlotStorage == 0)
		{
			throw new OutOfMemoryException("Failed to allocate host stack slot storage");
		}
		_vehManagedEntryLock = (nint)VirtualAlloc(null, 64u, 12288u, 4u);
		if (_vehManagedEntryLock == 0)
		{
			throw new OutOfMemoryException("Failed to allocate VEH managed-entry lock");
		}
		// owner (nint) + depth (int); recursive — nested VEH on same thread must reenter.
		*(nint*)_vehManagedEntryLock = 0;
		*(int*)(_vehManagedEntryLock + sizeof(nint)) = 0;
		_unresolvedReturnStub = CreateUnresolvedReturnStub();
		_guestReturnStub = CreateGuestReturnStub();
		if (_guestReturnStub == 0)
		{
			throw new OutOfMemoryException("Failed to allocate guest return stub");
		}
		_workerAbortStub = CreateWorkerAbortStub();
		if (_workerAbortStub == 0 && OperatingSystem.IsWindows())
		{
			Console.Error.WriteLine(
				"[LOADER][WARN] Worker abort stub unavailable; TBB execute-fault recover will use host_exit");
		}
		SetupExceptionHandler();
		// Cover the Astro TBB spawn storm (often 8–12 concurrent tbb_thead).
		PrewarmNativeGuestWorkers(Math.Max(NativeWorkerMaxConcurrent, 4));
	}

	public bool TryExecute(CpuContext context, ulong entryPoint, Generation generation, IReadOnlyDictionary<ulong, string> importStubs, IReadOnlyDictionary<string, ulong> runtimeSymbols, CpuExecutionOptions executionOptions, out OrbisGen2Result result)
	{
		Console.Error.WriteLine("[LOADER][INFO] === Execute START ===");
		Console.Error.WriteLine($"[LOADER][INFO] EntryPoint: 0x{entryPoint:X16}, ImportStubs: {importStubs.Count}");
		Console.Error.WriteLine($"[LOADER][INFO] RuntimeSymbols: {runtimeSymbols.Count}");
		Console.Error.WriteLine(_moduleManager.TryGetExport("QrZZdJ8XsX0", out ExportedFunction export) ? ("[LOADER][INFO] ExportCheck fputs: " + export.LibraryName + ":" + export.Name) : "[LOADER][INFO] ExportCheck fputs: MISSING");
		Console.Error.WriteLine(_moduleManager.TryGetExport("L-Q3LEjIbgA", out ExportedFunction export2) ? ("[LOADER][INFO] ExportCheck map_direct: " + export2.LibraryName + ":" + export2.Name) : "[LOADER][INFO] ExportCheck map_direct: MISSING");
		_entryPoint = entryPoint;
		_cpuContext = context;
		_debugHook = executionOptions.DebugHook;
		_returnFallbackTarget = context[CpuRegister.Rsi];
		Volatile.Write(ref _globalFallbackTarget, _returnFallbackTarget);
		Volatile.Write(ref _globalUnresolvedReturnStub, (ulong)_unresolvedReturnStub);
		result = OrbisGen2Result.ORBIS_GEN2_OK;
		LastError = null;
		InitializeRuntimeSymbolIndex(runtimeSymbols);
		ResetLazyDlsymStubState();
		_recentImportTraceCount = 0;
		_recentImportTraceWriteIndex = 0;
		lock (_deferredBootstrapTraceGate)
		{
			_deferredBootstrapTraceCount = 0;
			_deferredBootstrapTraceWriteIndex = 0;
		}
		_distinctImportNidHistoryCount = 0;
		_distinctImportNidHistoryWriteIndex = 0;
		_lastDistinctImportNid = string.Empty;
		_consecutiveStrlenImports = 0;
		_strlenPreludeLogged = false;
		_logStrlenImports = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_STRLEN"), "1", StringComparison.Ordinal);
		_logStrlenBursts = _logStrlenImports ||
			string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_STRLEN_BURSTS"), "1", StringComparison.Ordinal);
		_logGuestContext = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_CONTEXT"), "1", StringComparison.Ordinal);
		var ignoreGuestInt41Env = Environment.GetEnvironmentVariable("SHARPEMU_IGNORE_INT41");
		_ignoreGuestInt41 = !string.Equals(ignoreGuestInt41Env, "0", StringComparison.Ordinal) &&
			!string.Equals(ignoreGuestInt41Env, "false", StringComparison.OrdinalIgnoreCase);
		_ignoredGuestInt41Count = 0;
		_logGuestThreads = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_GUEST_THREADS"), "1", StringComparison.Ordinal);
		_logUsleep = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_USLEEP"), "1", StringComparison.Ordinal);
		_logFiber = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_FIBER"), "1", StringComparison.Ordinal);
		_logBootstrap = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_BOOTSTRAP"), "1", StringComparison.Ordinal);
		_logAllImports = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_ALL_IMPORTS"), "1", StringComparison.Ordinal);
		_logImportFrames = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_IMPORT_FRAMES"), "1", StringComparison.Ordinal);
		_logImportRecent = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_IMPORT_RECENT"), "1", StringComparison.Ordinal);
		_logStackCheck = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_STACK_CHK"), "1", StringComparison.Ordinal);
		_probeImportReturn = Environment.GetEnvironmentVariable("SHARPEMU_PROBE_IMPORT_RET");
		_probeImportReturnAddress = ParseOptionalHexAddress(
			Environment.GetEnvironmentVariable("SHARPEMU_PROBE_IMPORT_RET_ADDRESS"));
		_probeImportReturnAddressCount = 0;
		_importFilter = Environment.GetEnvironmentVariable("SHARPEMU_LOG_IMPORT_FILTER");
		_disableImportLoopGuard = string.Equals(
			Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_IMPORT_LOOP_GUARD"),
			"1",
			StringComparison.Ordinal);
		_importLoopGuardSeconds = GetImportLoopGuardSeconds();
		_entryReturnSentinelRip = 0uL;
		_forcedGuestExit = false;
		HostSessionControl.SetShutdownHandler(RequestHostShutdown);
		_importLoopSignatureCount = 0;
		_importLoopSignatureWriteIndex = 0;
		_importLoopPatternHits = 0;
		_importLoopPatternStartTimestamp = 0;
		lock (_importResultLogSampleGate)
		{
			_importResultLogSamples.Clear();
		}
		lock (_lazyCommitRangeGate)
		{
			_prtLazyCommitRanges.Clear();
		}
		ClearGuestThreads();
		_contextualUnresolvedReturnSites.Clear();
		_stallWatchdogTriggered = 0;
		_stallWatchdogStop = false;
		_readyDispatchStop = false;
		_patchedEa020eLookupCall = false;
		MarkExecutionProgress();
		BindTlsBase(context);
		var previousGuestThreadScheduler = GuestThreadExecution.Scheduler;
		GuestThreadExecution.Scheduler = this;
		try
		{
			if (!SetupImportStubs(importStubs))
			{
				if (string.IsNullOrEmpty(LastError))
				{
					LastError = "SetupImportStubs failed";
				}
				result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
				return false;
			}
			CreateTlsHandler();
			PatchTlsPatterns();
			return ExecuteEntry(context, entryPoint, out result);
		}
		catch (Exception ex)
		{
			LastError = "Exception in TryExecute: " + ex.GetType().Name + ": " + ex.Message;
			Console.Error.WriteLine("[LOADER][ERROR] " + LastError);
			Console.Error.WriteLine("[LOADER][ERROR] Stack trace: " + ex.StackTrace);
			result = OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP;
			return false;
		}
		finally
		{
			HostSessionControl.SetShutdownHandler(null);
			DrainDeferredBootstrapTraces();
			GuestThreadExecution.Scheduler = previousGuestThreadScheduler;
			Console.Error.WriteLine("[LOADER][INFO] === Execute END (LastError: " + (LastError ?? "null") + ") ===");
		}
	}

	internal void RequestHostShutdown(string reason)
	{
		_forcedGuestExit = true;
		LastError = string.IsNullOrWhiteSpace(reason)
			? "Host shutdown requested."
			: $"Host shutdown requested: {reason}";
		Console.Error.WriteLine($"[LOADER][INFO] {LastError}");
	}

	private bool SetupImportStubs(IReadOnlyDictionary<ulong, string> importStubs)
	{
		Console.Error.WriteLine($"[LOADER][INFO] Setting up {importStubs.Count} import stubs...");
		ClearImportHandlerTrampolines();
		_importEntries = new ImportStubEntry[importStubs.Count];
		HashSet<ulong> hashSet = new HashSet<ulong>(importStubs.Keys);
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		foreach (var (num4, text2) in importStubs)
		{
			_ = _moduleManager.TryGetExport(text2, out var resolvedExport);
			_importEntries[num] = new ImportStubEntry(
				num4,
				text2,
				resolvedExport,
				IsLeafImport(text2),
				IsNoBlockLeafImport(text2),
				ShouldSuppressStrlenTrace(text2),
				IsImportLoopGuardBoundary(text2),
				StableHash64(text2));
			if ((num4 >= 34393242112L && num4 <= 34393242624L) || (num4 >= 34393258496L && num4 <= 34393259008L))
			{
				if (resolvedExport is not null)
				{
					Console.Error.WriteLine($"[LOADER][INFO] ImportStubMap: 0x{num4:X16} -> {resolvedExport.LibraryName}:{resolvedExport.Name} ({text2})");
				}
				else
				{
					Console.Error.WriteLine($"[LOADER][INFO] ImportStubMap: 0x{num4:X16} -> {text2}");
				}
			}
			if (TryResolveDirectImportTarget(text2, out var targetAddress, out var resolvedSymbol) && !hashSet.Contains(targetAddress))
			{
				if (_logAllImports)
				{
					Console.Error.WriteLine($"[LOADER][DEBUG] SetupImportStubs: Direct bridge for {text2} -> 0x{targetAddress:X16}");
				}
				if (!PatchImportStub((nint)(long)num4, (nint)(long)targetAddress))
				{
					LastError = $"Failed to patch direct import stub at 0x{num4:X16}";
					return false;
				}
				num3++;
				num2++;
				if (num3 <= 48)
				{
					Console.Error.WriteLine(
						$"[LOADER][INFO] LLE redirect: 0x{num4:X16} {text2} -> {resolvedSymbol}@0x{targetAddress:X16}");
				}
				num++;
				continue;
			}
			if (TryCreateNativeImportIntrinsic(text2, out var intrinsicAddress))
			{
				if (!PatchImportStub((nint)(long)num4, intrinsicAddress))
				{
					LastError = $"Failed to patch native intrinsic import stub at 0x{num4:X16}";
					return false;
				}
				num2++;
				num++;
				continue;
			}
			nint num5 = CreateImportHandlerTrampoline(num);
			if (num5 == 0)
			{
				LastError = "Failed to create import trampoline for NID " + text2;
				return false;
			}
			if (_logAllImports)
			{
				Console.Error.WriteLine($"[LOADER][DEBUG] SetupImportStubs: Trampoline for {text2} -> 0x{num5:X16}");
			}
			if (!PatchImportStub((nint)num4, num5))
			{
				LastError = $"Failed to patch import stub at 0x{num4:X16}";
				return false;
			}
			num2++;
			num++;
		}
		Console.Error.WriteLine($"[LOADER][INFO] Setup {num2}/{importStubs.Count} import stubs (direct bridge, lle_redirects={num3})");
		return num2 == importStubs.Count;
	}

	private unsafe bool TryCreateNativeImportIntrinsic(string nid, out nint address)
	{
		if (IsHlePreferredNid(nid))
		{
			address = 0;
			return false;
		}

		if (nid == "1jfXLRVzisc" &&
			string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_USLEEP"), "1", StringComparison.Ordinal))
		{
			address = 0;
			return false;
		}

		ReadOnlySpan<byte> code = nid switch
		{
			"-2IRUCO--PM" =>
			[
				0x0F, 0x31,
				0x48, 0xC1, 0xE2, 0x20,
				0x48, 0x09, 0xD0,
				0xC3,
			],
			"fgxnMeTNUtY" =>
			[
				0x48, 0x83, 0xEC, 0x28,
				0x48, 0x8D, 0x4C, 0x24, 0x20,
				0x48, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0xFF, 0xD0,
				0x48, 0x8B, 0x44, 0x24, 0x20,
				0x48, 0x83, 0xC4, 0x28,
				0xC3,
			],
			"1jfXLRVzisc" =>
			[
				0x48, 0x85, 0xFF,
				0x74, 0x1D,
				0x48, 0x81, 0xFF, 0xE8, 0x03, 0x00, 0x00,
				0x73, 0x17,
				0x48, 0x83, 0xEC, 0x28,
				0x48, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0xFF, 0xD0,
				0x48, 0x83, 0xC4, 0x28,
				0x31, 0xC0,
				0xC3,
				0x48, 0x89, 0xF8,
				0x48, 0x05, 0xE7, 0x03, 0x00, 0x00,
				0x31, 0xD2,
				0xB9, 0xE8, 0x03, 0x00, 0x00,
				0x48, 0xF7, 0xF1,
				0x89, 0xC1,
				0x48, 0x83, 0xEC, 0x28,
				0x48, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0xFF, 0xD0,
				0x48, 0x83, 0xC4, 0x28,
				0x31, 0xC0,
				0xC3,
			],
			"j4ViWNHEgww" =>
			[
				0x31, 0xC0,
				0x48, 0xC7, 0xC1, 0xFF, 0xFF, 0xFF, 0xFF,
				0xF2, 0xAE,
				0x48, 0xF7, 0xD1,
				0x48, 0x8D, 0x41, 0xFF,
				0xC3,
			],
			"5jNubw4vlAA" =>
			[
				0x31, 0xC0,
				0x48, 0x85, 0xF6,
				0x74, 0x0E,
				0x80, 0x3C, 0x07, 0x00,
				0x74, 0x08,
				0x48, 0xFF, 0xC0,
				0x48, 0x39, 0xF0,
				0x72, 0xF2,
				0xC3,
			],
			"LHMrG7e8G78" or "WkkeywLJcgU" =>
			[
				0x31, 0xC0,
				0x66, 0x83, 0x3C, 0x47, 0x00,
				0x74, 0x05,
				0x48, 0xFF, 0xC0,
				0xEB, 0xF4,
				0xC3,
			],
			"Ovb2dSJOAuE" =>
			[
				0x0F, 0xB6, 0x07,
				0x0F, 0xB6, 0x16,
				0x29, 0xD0,
				0x75, 0x0C,
				0x84, 0xD2,
				0x74, 0x08,
				0x48, 0xFF, 0xC7,
				0x48, 0xFF, 0xC6,
				0xEB, 0xEA,
				0xC3,
			],
			"aesyjrHVWy4" =>
			[
				0x31, 0xC0,
				0x48, 0x85, 0xD2,
				0x74, 0x19,
				0x0F, 0xB6, 0x07,
				0x0F, 0xB6, 0x0E,
				0x29, 0xC8,
				0x75, 0x0F,
				0x84, 0xC9,
				0x74, 0x0B,
				0x48, 0xFF, 0xC7,
				0x48, 0xFF, 0xC6,
				0x48, 0xFF, 0xCA,
				0x75, 0xE7,
				0xC3,
			],
			"AV6ipCNa4Rw" =>
			[
				0x0F, 0xB6, 0x07,
				0x0F, 0xB6, 0x16,
				0x8D, 0x48, 0xBF,
				0x83, 0xF9, 0x19,
				0x77, 0x03,
				0x83, 0xC0, 0x20,
				0x8D, 0x4A, 0xBF,
				0x83, 0xF9, 0x19,
				0x77, 0x03,
				0x83, 0xC2, 0x20,
				0x29, 0xD0,
				0x75, 0x0C,
				0x85, 0xD2,
				0x74, 0x08,
				0x48, 0xFF, 0xC7,
				0x48, 0xFF, 0xC6,
				0xEB, 0xD4,
				0xC3,
			],
			"viiwFMaNamA" =>
			[
				0x0F, 0xB6, 0x16,
				0x84, 0xD2,
				0x74, 0x2D,
				0x0F, 0xB6, 0x07,
				0x84, 0xC0,
				0x74, 0x2A,
				0x38, 0xD0,
				0x75, 0x1D,
				0x4C, 0x8D, 0x47, 0x01,
				0x4C, 0x8D, 0x4E, 0x01,
				0x41, 0x0F, 0xB6, 0x09,
				0x84, 0xC9,
				0x74, 0x12,
				0x41, 0x38, 0x08,
				0x75, 0x08,
				0x49, 0xFF, 0xC0,
				0x49, 0xFF, 0xC1,
				0xEB, 0xEB,
				0x48, 0xFF, 0xC7,
				0xEB, 0xD3,
				0x48, 0x89, 0xF8,
				0xC3,
				0x31, 0xC0,
				0xC3,
			],
			"pNtJdE3x49E" or "fV2xHER+bKE" =>
			[
				0x0F, 0xB7, 0x07,
				0x0F, 0xB7, 0x16,
				0x29, 0xD0,
				0x75, 0x0F,
				0x66, 0x85, 0xD2,
				0x74, 0x0A,
				0x48, 0x83, 0xC7, 0x02,
				0x48, 0x83, 0xC6, 0x02,
				0xEB, 0xE7,
				0xC3,
			],
			"E8wCoUEbfzk" =>
			[
				0x31, 0xC0,
				0x48, 0x85, 0xD2,
				0x74, 0x1C,
				0x0F, 0xB7, 0x07,
				0x0F, 0xB7, 0x0E,
				0x29, 0xC8,
				0x75, 0x12,
				0x66, 0x85, 0xC9,
				0x74, 0x0D,
				0x48, 0x83, 0xC7, 0x02,
				0x48, 0x83, 0xC6, 0x02,
				0x48, 0xFF, 0xCA,
				0x75, 0xE4,
				0xC3,
			],
			"kiZSXIWd9vg" =>
			[
				0x48, 0x89, 0xF8,
				0x8A, 0x16,
				0x88, 0x17,
				0x48, 0xFF, 0xC6,
				0x48, 0xFF, 0xC7,
				0x84, 0xD2,
				0x75, 0xF2,
				0xC3,
			],
			"6sJWiWSRuqk" =>
			[
				0x48, 0x89, 0xF8,
				0x48, 0x85, 0xD2,
				0x74, 0x20,
				0x8A, 0x0E,
				0x88, 0x0F,
				0x48, 0xFF, 0xC7,
				0x48, 0xFF, 0xCA,
				0x74, 0x14,
				0x84, 0xC9,
				0x74, 0x05,
				0x48, 0xFF, 0xC6,
				0xEB, 0xEB,
				0xC6, 0x07, 0x00,
				0x48, 0xFF, 0xC7,
				0x48, 0xFF, 0xCA,
				0x75, 0xF5,
				0xC3,
			],
			"Q3VBxCXhUHs" =>
			[
				0x48, 0x89, 0xF8,
				0x48, 0x85, 0xD2,
				0x74, 0x11,
				0x44, 0x8A, 0x06,
				0x44, 0x88, 0x07,
				0x48, 0xFF, 0xC6,
				0x48, 0xFF, 0xC7,
				0x48, 0xFF, 0xCA,
				0x75, 0xEF,
				0xC3,
			],
			"8zTFvBIAIN8" =>
			[
				0x49, 0x89, 0xF8,
				0x48, 0x89, 0xF0,
				0x48, 0x89, 0xD1,
				0xF3, 0xAA,
				0x4C, 0x89, 0xC0,
				0xC3,
			],
			_ => default,
		};
		if (code.IsEmpty)
		{
			address = 0;
			return false;
		}

		const uint intrinsicAllocationSize = 128u;
		void* memory = VirtualAlloc(null, intrinsicAllocationSize, 12288u, 64u);
		if (memory == null)
		{
			address = 0;
			return false;
		}

		code.CopyTo(new Span<byte>(memory, code.Length));
		if (nid == "fgxnMeTNUtY")
		{
			*(nint*)((byte*)memory + 11) = _queryPerformanceCounterAddress;
		}
		else if (nid == "1jfXLRVzisc")
		{
			*(nint*)((byte*)memory + 20) = _switchToThreadAddress;
			*(nint*)((byte*)memory + 64) = _sleepAddress;
		}
		uint oldProtect = 0;
		if (!VirtualProtect(memory, intrinsicAllocationSize, 32u, &oldProtect))
		{
			VirtualFree(memory, 0u, 32768u);
			address = 0;
			return false;
		}

		FlushInstructionCache(GetCurrentProcess(), memory, (nuint)code.Length);
		address = (nint)memory;
		_importHandlerTrampolines.Add(address);
		return true;
	}

	private bool TryResolveDirectImportTarget(string nid, out ulong targetAddress, out string resolvedSymbol)
	{
		targetAddress = 0uL;
		resolvedSymbol = string.Empty;
		if (string.IsNullOrWhiteSpace(nid) || string.Equals(nid, RuntimeStubNids.KernelDynlibDlsym, StringComparison.Ordinal))
		{
			return false;
		}
		if (IsHlePreferredNid(nid))
		{
			return false;
		}

		if (_moduleManager.TryGetExport(nid, out ExportedFunction export))
		{
			if (IsKernelLibrary(export.LibraryName))
			{
				if (_logAllImports)
				{
					Console.Error.WriteLine($"[LOADER][DEBUG] TryResolveDirectImportTarget: {nid} ({export.LibraryName}:{export.Name}) -> HLE (kernel library)");
				}
				return false;
			}
			if (!IsLibcLibrary(export.LibraryName) || !PreferLleForLibcExport(export.Name))
			{
				return false;
			}
			if (TryResolveRuntimeSymbolAddress(nid, out var value2) && IsDirectImportTargetUsable(value2))
			{
				targetAddress = value2;
				resolvedSymbol = nid;
				return true;
			}
			foreach (string item in EnumerateRuntimeSymbolCandidates(export.Name))
			{
				if (TryResolveRuntimeSymbolAddress(item, out value2) && IsDirectImportTargetUsable(value2))
				{
					targetAddress = value2;
					resolvedSymbol = item;
					return true;
				}
			}
			return false;
		}

		if (_logAllImports)
		{
			Console.Error.WriteLine($"[LOADER][DEBUG] TryResolveDirectImportTarget: {nid} not in HLE table, checking runtime symbols...");
		}

		if (TryResolveRuntimeSymbolAddress(nid, out var directValue) && IsDirectImportTargetUsable(directValue))
		{
			targetAddress = directValue;
			resolvedSymbol = nid;
			if (_logAllImports)
			{
				Console.Error.WriteLine($"[LOADER][DEBUG] TryResolveDirectImportTarget: {nid} -> runtime symbol 0x{targetAddress:X16}");
			}
			return true;
		}

		if (Aerolib.Instance.TryGetByNid(nid, out var symbolByNid))
		{
			if (!PreferLleForLibcExport(symbolByNid.ExportName))
			{
				return false;
			}
			foreach (string item in EnumerateRuntimeSymbolCandidates(symbolByNid.ExportName))
			{
				if (TryResolveRuntimeSymbolAddress(item, out var value) && IsDirectImportTargetUsable(value))
				{
					targetAddress = value;
					resolvedSymbol = item;
					return true;
				}
			}
		}
		return false;
	}

	private static bool IsHlePreferredNid(string nid)
	{
		return string.Equals(nid, "QrZZdJ8XsX0", StringComparison.Ordinal) ||
			string.Equals(nid, "Q3VBxCXhUHs", StringComparison.Ordinal);
	}

	private static bool IsLibcLibrary(string libraryName)
	{
		return !string.IsNullOrWhiteSpace(libraryName) && libraryName.IndexOf("libc", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool IsKernelLibrary(string libraryName)
	{
		if (string.IsNullOrWhiteSpace(libraryName))
		{
			return false;
		}
		return libraryName.Equals("libKernel", StringComparison.OrdinalIgnoreCase) ||
			   libraryName.Equals("libKernelExt", StringComparison.OrdinalIgnoreCase) ||
			   libraryName.IndexOf("Kernel", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private bool PreferLleForLibcExport(string exportName)
	{
		if (string.IsNullOrWhiteSpace(exportName))
		{
			return false;
		}
		if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_LLE_LIBC"), "1", StringComparison.Ordinal))
		{
			return false;
		}
		var value = Environment.GetEnvironmentVariable("SHARPEMU_LLE_LIBC_SAFE_ONLY");
		if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LLE_LIBC_ALL"), "1", StringComparison.Ordinal))
		{
			return true;
		}
		if (IsLibcAllocatorExport(exportName))
		{
			return CanUseLleLibcAllocatorFamily();
		}
		if (string.Equals(value, "0", StringComparison.Ordinal))
		{
			return true;
		}
		if (string.Equals(value, "1", StringComparison.Ordinal))
		{
			return IsSafeLleLibcExport(exportName);
		}
		return IsSafeLleLibcExport(exportName);
	}

	private bool CanUseLleLibcAllocatorFamily()
	{
		return HasUsableLleLibcExport("gQX+4GDQjpM", "malloc") &&
			   HasUsableLleLibcExport("tIhsqj0qsFE", "free") &&
			   HasUsableLleLibcExport("2X5agFjKxMc", "calloc") &&
			   HasUsableLleLibcExport("Y7aJ1uydPMo", "realloc") &&
			   HasUsableLleLibcExport("Ujf3KzMvRmI", "memalign") &&
			   HasUsableLleLibcExport("2Btkg8k24Zg", "aligned_alloc") &&
			   HasUsableLleLibcExport("cVSk9y8URbc", "posix_memalign");
	}

	private bool HasUsableLleLibcExport(string nid, string exportName)
	{
		if (TryResolveRuntimeSymbolAddress(nid, out var address) && IsDirectImportTargetUsable(address))
		{
			return true;
		}

		foreach (var candidate in EnumerateRuntimeSymbolCandidates(exportName))
		{
			if (TryResolveRuntimeSymbolAddress(candidate, out address) && IsDirectImportTargetUsable(address))
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsLibcAllocatorExport(string exportName)
	{
		return exportName switch
		{
			"malloc" or
			"free" or
			"calloc" or
			"realloc" or
			"memalign" or
			"aligned_alloc" or
			"posix_memalign" or
			"malloc_usable_size" => true,
			_ => false,
		};
	}

	private static bool IsSafeLleLibcExport(string exportName)
	{
		return exportName switch
		{
			"memcpy" or
			"memmove" or
			"memset" or
			"memcmp" => true,
			_ => false,
		};
	}

	private static IEnumerable<string> EnumerateRuntimeSymbolCandidates(string exportName)
	{
		if (string.IsNullOrWhiteSpace(exportName))
		{
			yield break;
		}
		yield return exportName;
		if (exportName.StartsWith("_", StringComparison.Ordinal))
		{
			if (exportName.Length > 1)
			{
				yield return exportName[1..];
			}
			yield break;
		}
		yield return "_" + exportName;
	}

	private bool IsDirectImportTargetUsable(ulong address)
	{
		if (address < 65536 || IsUnresolvedSentinel(address) ||
			_cpuContext is null || !TryGetVirtualMemory(_cpuContext, out var virtualMemory))
		{
			return false;
		}

		foreach (var region in virtualMemory.SnapshotRegions())
		{
			if ((region.Protection & ProgramHeaderFlags.Execute) != 0 &&
				ContainsAddress(region.VirtualAddress, region.MemorySize, address))
			{
				return true;
			}
		}

		return false;
	}

	private unsafe void BindTlsBase(CpuContext context)
	{
		nint num = (nint)((context.FsBase != 0L) ? context.FsBase : context.GsBase);
		if (num == 0)
		{
			num = _tlsBaseAddress;
		}
		if (!HasActiveExecutionThread && num != _tlsBaseAddress)
		{
			_tlsBaseAddress = num;
			_ownsTlsBaseAddress = _tlsBaseAddress == _ownedTlsBaseAddress;
		}
		if (num != 0)
		{
			context.FsBase = (ulong)num;
			context.GsBase = (ulong)num;
			SeedTlsLayout(num);
			TlsSetValue(_guestTlsBaseTlsIndex, num);
		}
	}

	private unsafe static void SeedTlsLayout(nint tlsBase)
	{
		ulong num = (ulong)tlsBase;
		*(ulong*)tlsBase = num;
		if (*(ulong*)(tlsBase + 16) == 0)
		{
			*(ulong*)(tlsBase + 16) = num;
		}
		*(long*)(tlsBase + 40) = -4548986510476657986L;
		*(ulong*)(tlsBase + 96) = num;
	}

	private unsafe void UpdateTlsHandlerBase(nint tlsBase)
	{
		if (_tlsHandlerAddress == 0)
		{
			return;
		}

		uint oldProtect = default;
		if (!VirtualProtect((void*)_tlsHandlerAddress, 16u, 64u, &oldProtect))
		{
			return;
		}

		try
		{
			*(long*)((byte*)_tlsHandlerAddress + 2) = tlsBase;
		}
		finally
		{
			VirtualProtect((void*)_tlsHandlerAddress, 16u, oldProtect, &oldProtect);
			FlushInstructionCache(GetCurrentProcess(), (void*)_tlsHandlerAddress, 16u);
		}
	}

	private unsafe bool TryPrepareGuestContextTransfer(
		GuestCpuContinuation target,
		out nint frameAddress,
		out nint transferStub,
		out string? error)
	{
		frameAddress = 0;
		transferStub = 0;
		error = null;
		if (ActiveCpuContext is not { } activeContext)
		{
			error = "guest context transfer without an active CPU context";
			return false;
		}
		if (!TryValidateGuestContextTransferTarget(activeContext.Memory, target, out error))
		{
			return false;
		}
		if (!activeContext.TryWriteUInt64(target.Rsp - sizeof(ulong), target.Rip))
		{
			error = $"guest context transfer slot is not writable at 0x{target.Rsp - sizeof(ulong):X16}";
			return false;
		}

		transferStub = GetOrCreateGuestContextTransferStub();
		if (transferStub == 0)
		{
			error = "failed to allocate guest context transfer stub";
			return false;
		}

		frameAddress = _guestContextTransferFrames.Value;
		if (frameAddress == 0)
		{
			error = "failed to allocate guest context transfer frame";
			return false;
		}

		var frame = (ulong*)frameAddress;
		frame[0] = target.Rip;
		frame[1] = target.Rsp;
		frame[2] = target.Rax;
		frame[3] = target.Rcx;
		frame[4] = target.Rdx;
		frame[5] = target.Rbx;
		frame[6] = target.Rbp;
		frame[7] = target.Rsi;
		frame[8] = target.Rdi;
		frame[9] = target.R8;
		frame[10] = target.R9;
		frame[11] = target.R10;
		frame[12] = target.R11;
		frame[13] = target.R12;
		frame[14] = target.R13;
		frame[15] = target.R14;
		frame[16] = target.R15;
		frame[17] = target.Mxcsr == 0 ? 0x1F80u : target.Mxcsr;
		frame[18] = target.FpuControlWord == 0 ? 0x037Fu : target.FpuControlWord;
		frame[19] = target.RestoreFullFpuState ? 1u : 0u;
		return true;
	}

	internal static bool TryValidateGuestContextTransferTarget(
		ICpuMemory memory,
		in GuestCpuContinuation target,
		out string? error)
	{
		if (target.Rip < 65536 || target.Rsp < sizeof(ulong))
		{
			error = $"invalid guest context transfer target rip=0x{target.Rip:X16} rsp=0x{target.Rsp:X16}";
			return false;
		}

		Span<byte> ripProbe = stackalloc byte[1];
		if (!memory.TryRead(target.Rip, ripProbe))
		{
			error =
				$"guest context transfer target rip=0x{target.Rip:X16} is not mapped guest memory " +
				$"(rsp=0x{target.Rsp:X16})";
			return false;
		}

		error = null;
		return true;
	}

	private unsafe nint GetOrCreateGuestContextTransferStub()
	{
		if (Volatile.Read(ref _guestContextTransferStub) != 0)
		{
			return _guestContextTransferStub;
		}

		lock (_guestContextTransferStubGate)
		{
			if (_guestContextTransferStub != 0)
			{
				return _guestContextTransferStub;
			}

			const uint stubSize = 256;
			var code = (byte*)VirtualAlloc(null, stubSize, 12288u, 64u);
			if (code == null)
			{
				return 0;
			}

			var offset = 0;
			void Emit(byte value) => code[offset++] = value;
			void EmitLoadFromR11(int register, byte displacement)
			{
				Emit((byte)(0x49 | (register >= 8 ? 0x04 : 0x00)));
				Emit(0x8B);
				Emit((byte)(0x40 | ((register & 7) << 3) | 0x03));
				Emit(displacement);
			}
			void EmitLoadFromR11Disp32(int register, int displacement)
			{
				Emit((byte)(0x49 | (register >= 8 ? 0x04 : 0x00)));
				Emit(0x8B);
				Emit((byte)(0x80 | ((register & 7) << 3) | 0x03));
				*(int*)(code + offset) = displacement;
				offset += sizeof(int);
			}

			Emit(0x49); Emit(0x89); Emit(0xC3); // mov r11, rax
			// A new >=3.50 fiber receives the SDK-defined MXCSR verbatim. A
			// resumed fiber follows _sceFiberLongJmp: preserve status bits 0-5
			// while restoring the saved control bits.
			Emit(0x49); Emit(0x83); Emit(0xBB);                         // cmp qword [r11+152],0
			*(int*)(code + offset) = 152; offset += sizeof(int); Emit(0x00);
			Emit(0x0F); Emit(0x84);                                     // je merge_status
			var mergeBranch = offset; offset += sizeof(int);
			Emit(0x41); Emit(0x0F); Emit(0xAE); Emit(0x93);             // ldmxcsr [r11+136]
			*(int*)(code + offset) = 136; offset += sizeof(int);
			Emit(0xE9);                                                  // jmp mxcsr_done
			var doneBranch = offset; offset += sizeof(int);
			var mergeLabel = offset;
			Emit(0x48); Emit(0x83); Emit(0xEC); Emit(0x08);             // sub rsp,8
			Emit(0x0F); Emit(0xAE); Emit(0x1C); Emit(0x24);             // stmxcsr [rsp]
			Emit(0x41); Emit(0x8B); Emit(0x83);                         // mov eax,[r11+136]
			*(int*)(code + offset) = 136; offset += sizeof(int);
			Emit(0x25); *(uint*)(code + offset) = 0xFFFFFFC0u; offset += sizeof(uint); // and eax,~0x3f
			Emit(0x8B); Emit(0x0C); Emit(0x24);                         // mov ecx,[rsp]
			Emit(0x83); Emit(0xE1); Emit(0x3F);                         // and ecx,0x3f
			Emit(0x09); Emit(0xC8);                                     // or eax,ecx
			Emit(0x89); Emit(0x04); Emit(0x24);                         // mov [rsp],eax
			Emit(0x0F); Emit(0xAE); Emit(0x14); Emit(0x24);             // ldmxcsr [rsp]
			Emit(0x48); Emit(0x83); Emit(0xC4); Emit(0x08);             // add rsp,8
			var mxcsrDoneLabel = offset;
			*(int*)(code + mergeBranch) = mergeLabel - (mergeBranch + sizeof(int));
			*(int*)(code + doneBranch) = mxcsrDoneLabel - (doneBranch + sizeof(int));
			Emit(0x41); Emit(0xD9); Emit(0xAB);                         // fldcw [r11+144]
			*(int*)(code + offset) = 144; offset += sizeof(int);

			EmitLoadFromR11(4, 8);              // resume rsp
			Emit(0x48); Emit(0x83); Emit(0xEC); Emit(0x08); // point at transfer return slot
			EmitLoadFromR11(1, 24);             // rcx
			EmitLoadFromR11(2, 32);             // rdx
			EmitLoadFromR11(3, 40);             // rbx
			EmitLoadFromR11(5, 48);             // rbp
			EmitLoadFromR11(6, 56);             // rsi
			EmitLoadFromR11(7, 64);             // rdi
			EmitLoadFromR11(8, 72);             // r8
			EmitLoadFromR11(9, 80);             // r9
			EmitLoadFromR11(10, 88);            // r10
			EmitLoadFromR11(12, 104);           // r12
			EmitLoadFromR11(13, 112);           // r13
			EmitLoadFromR11(14, 120);           // r14
			EmitLoadFromR11Disp32(15, 128);     // r15
			EmitLoadFromR11(0, 16);             // rax
			EmitLoadFromR11(11, 96);            // r11 (last: frame pointer)
			Emit(0xC3);                         // ret through [resume_rsp-8]
			Debug.Assert(offset <= stubSize, "Guest context transfer stub exceeded its allocation.");

			uint oldProtect = 0;
			if (!VirtualProtect(code, stubSize, 32u, &oldProtect))
			{
				VirtualFree(code, 0u, 32768u);
				return 0;
			}

			FlushInstructionCache(GetCurrentProcess(), code, stubSize);
			Volatile.Write(ref _guestContextTransferStub, (nint)code);
			return (nint)code;
		}
	}

	private unsafe nint CreateImportHandlerTrampoline(int importIndex)
	{
		void* ptr = VirtualAlloc(null, 512u, 12288u, 64u);
		if (ptr == null)
		{
			return 0;
		}
		_importHandlerTrampolines.Add((nint)ptr);
		try
		{
			byte* ptr2 = (byte*)ptr;
			int num = 0;
			ptr2[num++] = 65;
			ptr2[num++] = 87;
			ptr2[num++] = 65;
			ptr2[num++] = 86;
			ptr2[num++] = 65;
			ptr2[num++] = 85;
			ptr2[num++] = 65;
			ptr2[num++] = 84;
			ptr2[num++] = 85;
			ptr2[num++] = 83;
			ptr2[num++] = 65;
			ptr2[num++] = 81;
			ptr2[num++] = 65;
			ptr2[num++] = 80;
			ptr2[num++] = 81;
			ptr2[num++] = 82;
			ptr2[num++] = 86;
			ptr2[num++] = 87;
			// Preserve incoming guest RAX/AL and XMM0-XMM7 below the existing
			// GPR argument pack.  The original pack still begins with RDI and its
			// return address remains at +0x60.
			ptr2[num++] = 0x48;
			ptr2[num++] = 0x81;
			ptr2[num++] = 0xEC;
			*(uint*)(ptr2 + num) = 0xB0;
			num += 4;
			ptr2[num++] = 0x48;
			ptr2[num++] = 0x89;
			ptr2[num++] = 0x04;
			ptr2[num++] = 0x24;
			// Preserve the remaining volatile guest machine context before any
			// host call is made.  libSceFiber's setjmp/longjmp contract includes
			// R10/R11 and the x87/MXCSR control state.
			ptr2[num++] = 0x4C; ptr2[num++] = 0x89; ptr2[num++] = 0x54; ptr2[num++] = 0x24; ptr2[num++] = 0x08; // mov [rsp+8],r10
			ptr2[num++] = 0x4C; ptr2[num++] = 0x89; ptr2[num++] = 0x5C; ptr2[num++] = 0x24; ptr2[num++] = 0x10; // mov [rsp+16],r11
			ptr2[num++] = 0x0F; ptr2[num++] = 0xAE; ptr2[num++] = 0x5C; ptr2[num++] = 0x24; ptr2[num++] = 0x18; // stmxcsr [rsp+24]
			ptr2[num++] = 0xD9; ptr2[num++] = 0x7C; ptr2[num++] = 0x24; ptr2[num++] = 0x1C; // fnstcw [rsp+28]
			for (var xmm = 0; xmm < 8; xmm++)
			{
				ptr2[num++] = 0xF3;
				ptr2[num++] = 0x0F;
				ptr2[num++] = 0x7F;
				ptr2[num++] = (byte)(0x84 | (xmm << 3));
				ptr2[num++] = 0x24;
				*(uint*)(ptr2 + num) = (uint)(0x30 + (xmm * 0x10));
				num += 4;
			}
			ptr2[num++] = 0x4C;
			ptr2[num++] = 0x8D;
			ptr2[num++] = 0xA4;
			ptr2[num++] = 0x24;
			*(uint*)(ptr2 + num) = 0xB0;
			num += 4;
			ptr2[num++] = 72;
			ptr2[num++] = 131;
			ptr2[num++] = 236;
			ptr2[num++] = 40;
			ptr2[num++] = 185;
			*(uint*)(ptr2 + num) = _hostRspSlotTlsIndex;
			num += 4;
			ptr2[num++] = 72;
			ptr2[num++] = 184;
			*(long*)(ptr2 + num) = _tlsGetValueAddress;
			num += 8;
			ptr2[num++] = byte.MaxValue;
			ptr2[num++] = 208;
			ptr2[num++] = 72;
			ptr2[num++] = 131;
			ptr2[num++] = 196;
			ptr2[num++] = 40;
			ptr2[num++] = 0x4C;
			ptr2[num++] = 0x8D;
			ptr2[num++] = 0xA4;
			ptr2[num++] = 0x24;
			*(uint*)(ptr2 + num) = 0xB0;
			num += 4;
			ptr2[num++] = 73;
			ptr2[num++] = 137;
			ptr2[num++] = 195;
			ptr2[num++] = 73;
			ptr2[num++] = 139;
			ptr2[num++] = 35;
			ptr2[num++] = 72;
			ptr2[num++] = 131;
			ptr2[num++] = 236;
			ptr2[num++] = 56;
			ptr2[num++] = 0x4C; ptr2[num++] = 0x89; ptr2[num++] = 0x64; ptr2[num++] = 0x24; ptr2[num++] = 0x28; // mov [rsp+0x28],r12
			ptr2[num++] = 72;
			ptr2[num++] = 185;
			*(long*)(ptr2 + num) = _selfHandlePtr;
			num += 8;
			ptr2[num++] = 186;
			*(int*)(ptr2 + num) = importIndex;
			num += 4;
			ptr2[num++] = 77;
			ptr2[num++] = 137;
			ptr2[num++] = 224;
			ptr2[num++] = 72;
			ptr2[num++] = 184;
			*(long*)(ptr2 + num) = ImportGatewayPtr;
			num += 8;
			ptr2[num++] = byte.MaxValue;
			ptr2[num++] = 208;
			ptr2[num++] = 0x4C; ptr2[num++] = 0x8B; ptr2[num++] = 0x64; ptr2[num++] = 0x24; ptr2[num++] = 0x28; // mov r12,[rsp+0x28]
			ptr2[num++] = 72;
			ptr2[num++] = 131;
			ptr2[num++] = 196;
			ptr2[num++] = 56;
			// Materialize SysV vector return registers written by the managed HLE
			// gateway before restoring the guest stack.
			for (var xmm = 0; xmm < 2; xmm++)
			{
				ptr2[num++] = 0xF3;
				ptr2[num++] = 0x41;
				ptr2[num++] = 0x0F;
				ptr2[num++] = 0x6F;
				ptr2[num++] = (byte)(0x84 | (xmm << 3));
				ptr2[num++] = 0x24;
				*(int*)(ptr2 + num) = -0x80 + (xmm * 0x10);
				num += 4;
			}
			ptr2[num++] = 76;
			ptr2[num++] = 137;
			ptr2[num++] = 228;
			ptr2[num++] = 95;
			ptr2[num++] = 94;
			ptr2[num++] = 90;
			ptr2[num++] = 89;
			ptr2[num++] = 65;
			ptr2[num++] = 88;
			ptr2[num++] = 65;
			ptr2[num++] = 89;
			ptr2[num++] = 91;
			ptr2[num++] = 93;
			ptr2[num++] = 65;
			ptr2[num++] = 92;
			ptr2[num++] = 65;
			ptr2[num++] = 93;
			ptr2[num++] = 65;
			ptr2[num++] = 94;
			ptr2[num++] = 65;
			ptr2[num++] = 95;
			ptr2[num++] = 195;
			Debug.Assert(num <= 512, "Import handler trampoline exceeded its allocation.");
			uint num2 = default(uint);
			VirtualProtect(ptr, 512u, 32u, &num2);
			FlushInstructionCache(GetCurrentProcess(), ptr, 512u);
			return (nint)ptr;
		}
		catch
		{
			return 0;
		}
	}

	private unsafe bool PatchImportStub(nint address, nint trampoline)
	{
		uint flNewProtect = default(uint);
		if (!VirtualProtect((void*)address, 16u, 64u, &flNewProtect))
		{
			Console.Error.WriteLine($"[LOADER][ERROR] VirtualProtect failed for import stub at 0x{address:X16}");
			return false;
		}
		try
		{
			*(sbyte*)address = 72;
			*(sbyte*)(address + 1) = -72;
			*(long*)(address + 2) = trampoline;
			*(sbyte*)(address + 10) = -1;
			*(sbyte*)(address + 11) = -32;
			*(sbyte*)(address + 12) = -112;
			*(sbyte*)(address + 13) = -112;
			*(sbyte*)(address + 14) = -112;
			*(sbyte*)(address + 15) = -112;
			return true;
		}
		finally
		{
			VirtualProtect((void*)address, 16u, flNewProtect, &flNewProtect);
			FlushInstructionCache(GetCurrentProcess(), (void*)address, 16u);
		}
	}

	private unsafe void ClearImportHandlerTrampolines()
	{
		foreach (nint importHandlerTrampoline in _importHandlerTrampolines)
		{
			if (importHandlerTrampoline != 0)
			{
				VirtualFree((void*)importHandlerTrampoline, 0u, 32768u);
			}
		}
		_importHandlerTrampolines.Clear();
	}

	private unsafe void CreateTlsHandler()
	{
		_tlsHandlerAddress = (nint)TryAllocateNearEntry(TlsHandlerRegionSize);
		if (_tlsHandlerAddress == 0)
		{
			_tlsHandlerAddress = (nint)VirtualAlloc(null, TlsHandlerRegionSize, 12288u, 64u);
		}
		if (_tlsHandlerAddress == 0)
		{
			throw new OutOfMemoryException("Failed to allocate TLS handler");
		}
		// The handler runs in place of a patched guest `mov reg, fs:[0]`,
		// which preserves every register and the flags. TlsGetValue (and the
		// Win64 ABI in general) clobbers rcx/rdx/r8-r11 and the arithmetic
		// flags, so save them all: guest code legitimately keeps live values
		// and comparison results across TLS reads, and losing them corrupted
		// deterministic computations (e.g. procedural texture generation).
		byte* tlsHandlerAddress = (byte*)_tlsHandlerAddress;
		int num = 0;
		tlsHandlerAddress[num++] = 0x9C;                    // pushfq
		tlsHandlerAddress[num++] = 0x51;                    // push rcx
		tlsHandlerAddress[num++] = 0x52;                    // push rdx
		tlsHandlerAddress[num++] = 0x41;                    // push r8
		tlsHandlerAddress[num++] = 0x50;
		tlsHandlerAddress[num++] = 0x41;                    // push r9
		tlsHandlerAddress[num++] = 0x51;
		tlsHandlerAddress[num++] = 0x41;                    // push r10
		tlsHandlerAddress[num++] = 0x52;
		tlsHandlerAddress[num++] = 0x41;                    // push r11
		tlsHandlerAddress[num++] = 0x53;
		tlsHandlerAddress[num++] = 0x48;                    // sub rsp, 0x20
		tlsHandlerAddress[num++] = 0x83;
		tlsHandlerAddress[num++] = 0xEC;
		tlsHandlerAddress[num++] = 0x20;
		tlsHandlerAddress[num++] = 0xB9;                    // mov ecx, index
		*(uint*)(tlsHandlerAddress + num) = _guestTlsBaseTlsIndex;
		num += 4;
		tlsHandlerAddress[num++] = 0x48;                    // mov rax, TlsGetValue
		tlsHandlerAddress[num++] = 0xB8;
		*(long*)(tlsHandlerAddress + num) = _tlsGetValueAddress;
		num += 8;
		tlsHandlerAddress[num++] = 0xFF;                    // call rax
		tlsHandlerAddress[num++] = 0xD0;
		tlsHandlerAddress[num++] = 0x48;                    // add rsp, 0x20
		tlsHandlerAddress[num++] = 0x83;
		tlsHandlerAddress[num++] = 0xC4;
		tlsHandlerAddress[num++] = 0x20;
		tlsHandlerAddress[num++] = 0x41;                    // pop r11
		tlsHandlerAddress[num++] = 0x5B;
		tlsHandlerAddress[num++] = 0x41;                    // pop r10
		tlsHandlerAddress[num++] = 0x5A;
		tlsHandlerAddress[num++] = 0x41;                    // pop r9
		tlsHandlerAddress[num++] = 0x59;
		tlsHandlerAddress[num++] = 0x41;                    // pop r8
		tlsHandlerAddress[num++] = 0x58;
		tlsHandlerAddress[num++] = 0x5A;                    // pop rdx
		tlsHandlerAddress[num++] = 0x59;                    // pop rcx
		tlsHandlerAddress[num++] = 0x9D;                    // popfq
		tlsHandlerAddress[num++] = 0xC3;                    // ret
		_tlsPatchStubOffset = (num + 15) & ~15;
		uint num2 = default(uint);
		VirtualProtect((void*)_tlsHandlerAddress, TlsHandlerRegionSize, 32u, &num2);
		FlushInstructionCache(GetCurrentProcess(), (void*)_tlsHandlerAddress, TlsHandlerRegionSize);
		Console.Error.WriteLine($"[LOADER][INFO] TLS handler at 0x{_tlsHandlerAddress:X16}");
	}

	private unsafe static nint CreateUnresolvedReturnStub()
	{
		void* ptr = VirtualAlloc(null, 4096u, 12288u, 64u);
		if (ptr == null)
		{
			return 0;
		}
		byte* ptr2 = (byte*)ptr;
		*ptr2 = 49;
		ptr2[1] = 192;
		ptr2[2] = 195;
		for (int i = 3; i < 16; i++)
		{
			ptr2[i] = 144;
		}
		uint num = default(uint);
		VirtualProtect(ptr, 4096u, 32u, &num);
		FlushInstructionCache(GetCurrentProcess(), ptr, 16u);
		return (nint)ptr;
	}

	private unsafe nint CreateGuestReturnStub()
	{
		const uint stubSize = 256u;
		void* ptr = VirtualAlloc(null, stubSize, 12288u, 4u);
		if (ptr == null)
		{
			return 0;
		}

		byte* code = (byte*)ptr;
		int offset = 0;
		EmitByte(code, ref offset, 0x48); // sub rsp, 0x20
		EmitByte(code, ref offset, 0x83);
		EmitByte(code, ref offset, 0xEC);
		EmitByte(code, ref offset, 0x20);
		EmitByte(code, ref offset, 0xB9); // mov ecx, tlsIndex
		EmitUInt32(code, ref offset, _hostRspSlotTlsIndex);
		EmitByte(code, ref offset, 0x48); // mov rax, TlsGetValue
		EmitByte(code, ref offset, 0xB8);
		*(long*)(code + offset) = _tlsGetValueAddress;
		offset += sizeof(ulong);
		EmitByte(code, ref offset, 0xFF); // call rax
		EmitByte(code, ref offset, 0xD0);
		EmitByte(code, ref offset, 0x48); // add rsp, 0x20
		EmitByte(code, ref offset, 0x83);
		EmitByte(code, ref offset, 0xC4);
		EmitByte(code, ref offset, 0x20);
		EmitByte(code, ref offset, 0x48); // mov rsp, [rax]
		EmitByte(code, ref offset, 0x8B);
		EmitByte(code, ref offset, 0x20);
		EmitHostNonvolatileXmmRestore(code, ref offset);
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x5F);
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x5E);
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x5D);
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x5C);
		EmitByte(code, ref offset, 0x5E);
		EmitByte(code, ref offset, 0x5F);
		EmitByte(code, ref offset, 0x5D);
		EmitByte(code, ref offset, 0x5B);
		EmitByte(code, ref offset, 0xC3);

		uint oldProtect = default;
		if (!VirtualProtect(ptr, stubSize, 32u, &oldProtect))
		{
			VirtualFree(ptr, 0u, 32768u);
			return 0;
		}
		FlushInstructionCache(GetCurrentProcess(), ptr, (nuint)offset);
		return (nint)ptr;
	}

	/// <summary>
	/// After a TBB execute-fault, VEH redirects here on a host stack.
	/// SetEvent(done) then park forever — ExitThread from VEH CONTINUE_EXECUTION
	/// was taking down the whole process (recover logged, no respawning). The
	/// renter TerminateThread's the parked worker and respawns a clean loop.
	/// </summary>
	private unsafe nint CreateWorkerAbortStub()
	{
		if (!OperatingSystem.IsWindows() ||
			_workerDoneEventTlsIndex == uint.MaxValue ||
			_tlsGetValueAddress == 0 ||
			_setEventAddress == 0)
		{
			return 0;
		}

		nint kernel32 = GetModuleHandle("kernel32.dll");
		nint getStdHandle = kernel32 != 0 ? GetProcAddress(kernel32, "GetStdHandle") : 0;
		nint writeFile = kernel32 != 0 ? GetProcAddress(kernel32, "WriteFile") : 0;
		nint flushFileBuffers = kernel32 != 0 ? GetProcAddress(kernel32, "FlushFileBuffers") : 0;

		const uint stubSize = 256u;
		void* ptr = VirtualAlloc(null, stubSize, 12288u, 4u);
		if (ptr == null)
		{
			return 0;
		}

		byte* code = (byte*)ptr;
		int offset = 0;
		EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83);
		EmitByte(code, ref offset, 0xEC); EmitByte(code, ref offset, 0x28); // sub rsp, 0x28
		EmitByte(code, ref offset, 0xB9);
		EmitUInt32(code, ref offset, _workerDoneEventTlsIndex); // mov ecx, tls
		EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
		*(nint*)(code + offset) = _tlsGetValueAddress;
		offset += sizeof(nint);
		EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0); // call TlsGetValue
		EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x85);
		EmitByte(code, ref offset, 0xC0); // test rax, rax
		EmitByte(code, ref offset, 0x74); EmitByte(code, ref offset, 0x0F); // jz skip SetEvent (15 bytes)
		EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x89);
		EmitByte(code, ref offset, 0xC1); // mov rcx, rax
		EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
		*(nint*)(code + offset) = _setEventAddress;
		offset += sizeof(nint);
		EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0); // call SetEvent

		// Breadcrumb on host stack (survives silent teardown better than managed log).
		int msgAbsSlot = -1;
		if (getStdHandle != 0 && writeFile != 0)
		{
			ReadOnlySpan<byte> msg = "[LOADER][WARN] tbb_abort_stub SetEvent+park\n"u8;
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83);
			EmitByte(code, ref offset, 0xEC); EmitByte(code, ref offset, 0x20); // extra shadow for WriteFile args
			EmitByte(code, ref offset, 0xB9); EmitUInt32(code, ref offset, unchecked((uint)-12));
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
			*(nint*)(code + offset) = getStdHandle;
			offset += sizeof(nint);
			EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0);
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x89);
			EmitByte(code, ref offset, 0xC1); // mov rcx, handle
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x89);
			EmitByte(code, ref offset, 0xC3); // mov rbx, handle (nonvolatile for flush)
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
			msgAbsSlot = offset;
			*(nint*)(code + offset) = 0;
			offset += sizeof(nint);
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x89);
			EmitByte(code, ref offset, 0xC2); // mov rdx, msg
			EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0xB8);
			EmitUInt32(code, ref offset, (uint)msg.Length);
			EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x8D);
			EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, 0x20); // lea r9, [rsp+0x20]
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xC7);
			EmitByte(code, ref offset, 0x44); EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, 0x20); EmitUInt32(code, ref offset, 0);
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xC7);
			EmitByte(code, ref offset, 0x44); EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, 0x28); EmitUInt32(code, ref offset, 0);
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
			*(nint*)(code + offset) = writeFile;
			offset += sizeof(nint);
			EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0);
			if (flushFileBuffers != 0)
			{
				EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x89);
				EmitByte(code, ref offset, 0xD9); // mov rcx, rbx
				EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
				*(nint*)(code + offset) = flushFileBuffers;
				offset += sizeof(nint);
				EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0);
			}
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83);
			EmitByte(code, ref offset, 0xC4); EmitByte(code, ref offset, 0x20);
		}

		// Park: do not ExitThread (process-wide silent die after VEH redirect).
		int parkOffset = offset;
		EmitByte(code, ref offset, 0xF3); EmitByte(code, ref offset, 0x90); // pause
		EmitByte(code, ref offset, 0xEB);
		EmitByte(code, ref offset, unchecked((byte)(parkOffset - (offset + 1)))); // jmp park

		if (msgAbsSlot >= 0)
		{
			ReadOnlySpan<byte> msgEmbed = "[LOADER][WARN] tbb_abort_stub SetEvent+park\n"u8;
			*(nint*)(code + msgAbsSlot) = (nint)ptr + offset;
			for (int i = 0; i < msgEmbed.Length; i++)
			{
				EmitByte(code, ref offset, msgEmbed[i]);
			}
		}

		if (offset > (int)stubSize)
		{
			Console.Error.WriteLine(
				$"[LOADER][ERROR] Worker abort stub overflow: used={offset} cap={stubSize}");
			VirtualFree(ptr, 0u, 32768u);
			return 0;
		}

		uint oldProtect = default;
		if (!VirtualProtect(ptr, stubSize, 32u, &oldProtect))
		{
			VirtualFree(ptr, 0u, 32768u);
			return 0;
		}
		FlushInstructionCache(GetCurrentProcess(), ptr, (nuint)offset);
		return (nint)ptr;
	}

	private unsafe nint CreateExceptionHandlerTrampoline(nint managedHandler)
	{
		// Live VEH trampoline used by SetupExceptionHandler. Must pre-filter
		// FastFail / CLR / MSVC C++ / stack-overflow the same way as
		// WindowsFaultHandling.CreateHandlerThunk: entering managed VEH while
		// the thread is in cooperative GC mode fail-fasts with
		// "UnmanagedCallersOnly method from managed code" (tLT18–22).
		// Extra headroom for native tbb abort + recursive managed-entry spinlock.
		const uint stubSize = 2048u;
		void* ptr = VirtualAlloc(null, stubSize, 12288u, 64u);
		if (ptr == null)
		{
			return 0;
		}

		byte* code = (byte*)ptr;
		int offset = 0;

		ReadOnlySpan<uint> nonManagedExceptionCodes =
		[
			0xE0434352u, // CLR managed exception
			0xE06D7363u, // MSVC C++ exception
			0xC0000409u, // STATUS_STACK_BUFFER_OVERRUN / FailFast
			0xC00000FDu, // STATUS_STACK_OVERFLOW
		];
		EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x01); // mov rax, [rcx]
		EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x00); // mov eax, [rax] ExceptionCode
		var passJumpOffsets = stackalloc int[nonManagedExceptionCodes.Length];
		int fastFailJumpSlot = -1;
		for (int i = 0; i < nonManagedExceptionCodes.Length; i++)
		{
			EmitByte(code, ref offset, 0x3D);
			EmitUInt32(code, ref offset, nonManagedExceptionCodes[i]);
			EmitByte(code, ref offset, 0x74);
			passJumpOffsets[i] = offset;
			EmitByte(code, ref offset, 0x00);
			if (nonManagedExceptionCodes[i] == 0xC0000409u)
			{
				fastFailJumpSlot = i;
			}
		}

		EmitByte(code, ref offset, 0xE9); // jmp mainBody (rel32; FastFail breadcrumb sits between)
		var mainBodyJumpSlot = offset;
		EmitUInt32(code, ref offset, 0u);

		int passOffset = offset;
		EmitByte(code, ref offset, 0x31); EmitByte(code, ref offset, 0xC0); // xor eax, eax
		EmitByte(code, ref offset, 0xC3);

		int fastFailPassOffset = offset;
		var fastFailLogInstalled = false;
		nint kernel32 = GetModuleHandle("kernel32.dll");
		nint getStdHandle = kernel32 != 0 ? GetProcAddress(kernel32, "GetStdHandle") : 0;
		nint writeFile = kernel32 != 0 ? GetProcAddress(kernel32, "WriteFile") : 0;
		if (fastFailJumpSlot >= 0 && getStdHandle != 0 && writeFile != 0)
		{
			// Prefix + Context.Rip hex (AMD64 CONTEXT.Rip @ 0xF8) + newline.
			// Keep in sync with WindowsFaultHandling.CreateHandlerThunk.
			ReadOnlySpan<byte> msg =
				"[LOADER][FATAL] VEH_PASS FastFail 0xC0000409 (live trampoline; skip managed VEH) rip=0x"u8;
			ReadOnlySpan<byte> hexDigits = "0123456789ABCDEF"u8;
			// rcx=EXCEPTION_POINTERS*: capture Rip into r10 before clobbering.
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x41);
			EmitByte(code, ref offset, 0x08); // mov rax, [rcx+8] ContextRecord*
			EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x90);
			EmitUInt32(code, ref offset, 0xF8u); // mov r10, [rax+0xF8] Rip
			EmitByte(code, ref offset, 0x50);
			EmitByte(code, ref offset, 0x51);
			EmitByte(code, ref offset, 0x52);
			EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x50);
			EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x51);
			EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x52); // push r10 (Rip)
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83);
			EmitByte(code, ref offset, 0xEC); EmitByte(code, ref offset, 0x40); // sub rsp, 0x40 (hex buf @ +0x30)
			EmitByte(code, ref offset, 0xB9); EmitUInt32(code, ref offset, unchecked((uint)-12));
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
			*(nint*)(code + offset) = getStdHandle;
			offset += sizeof(nint);
			EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0);
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x89);
			EmitByte(code, ref offset, 0x44); EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, 0x28); // mov [rsp+0x28], rax  stderr handle
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xC1);
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
			var msgAbsSlot = offset;
			*(nint*)(code + offset) = 0;
			offset += sizeof(nint);
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xC2);
			EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0xB8);
			EmitUInt32(code, ref offset, (uint)msg.Length);
			EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x8D);
			EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, 0x20);
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xC7);
			EmitByte(code, ref offset, 0x44); EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, 0x20); EmitUInt32(code, ref offset, 0);
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xC7);
			EmitByte(code, ref offset, 0x44); EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, 0x38); EmitUInt32(code, ref offset, 0); // lpOverlapped slot
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
			*(nint*)(code + offset) = writeFile;
			offset += sizeof(nint);
			EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0);

			// Hex-encode Rip. Stack after sub 0x40: [rsp+0x40]=saved Rip (push r10).
			EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x8B);
			EmitByte(code, ref offset, 0x54); EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, 0x40); // mov r10, [rsp+0x40]
			EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0xB8);
			var hexDigitsAbsSlot = offset;
			*(nint*)(code + offset) = 0;
			offset += sizeof(nint); // mov r8, hexDigits
			EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x8D);
			EmitByte(code, ref offset, 0x5C); EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, 0x30); // lea r11, [rsp+0x30] hex out
			EmitByte(code, ref offset, 0xB9); EmitUInt32(code, ref offset, 16u); // ecx = 16 nibbles
			EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xD0); // mov rax, r10
			int hexLoopOffset = offset;
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xC1); EmitByte(code, ref offset, 0xC0);
			EmitByte(code, ref offset, 0x04); // rol rax, 4
			EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xC2); // mov edx, eax
			EmitByte(code, ref offset, 0x83); EmitByte(code, ref offset, 0xE2); EmitByte(code, ref offset, 0x0F); // and edx, 0xF
			EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0xB6);
			EmitByte(code, ref offset, 0x14); EmitByte(code, ref offset, 0x10); // movzx edx, byte [r8+rdx]
			EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x88); EmitByte(code, ref offset, 0x13); // mov [r11], dl
			EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xC3); // inc r11
			EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xC9); // dec ecx
			EmitByte(code, ref offset, 0x75);
			EmitByte(code, ref offset, unchecked((byte)(hexLoopOffset - (offset + 1)))); // jnz hexLoop (rel8)
			EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0xC6); EmitByte(code, ref offset, 0x03);
			EmitByte(code, ref offset, 0x0A); // mov byte [r11], '\n'

			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x8B);
			EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, 0x28); // mov rcx, [rsp+0x28] stderr
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x8D);
			EmitByte(code, ref offset, 0x54); EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, 0x30); // lea rdx, [rsp+0x30]
			EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0xB8);
			EmitUInt32(code, ref offset, 17u); // 16 hex + newline
			EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x8D);
			EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, 0x20);
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xC7);
			EmitByte(code, ref offset, 0x44); EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, 0x20); EmitUInt32(code, ref offset, 0);
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xC7);
			EmitByte(code, ref offset, 0x44); EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, 0x38); EmitUInt32(code, ref offset, 0);
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
			*(nint*)(code + offset) = writeFile;
			offset += sizeof(nint);
			EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0);

			// Flush redirected stderr so FastFail rip survives process teardown.
			nint flushFileBuffers = kernel32 != 0 ? GetProcAddress(kernel32, "FlushFileBuffers") : 0;
			if (flushFileBuffers != 0)
			{
				EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x8B);
				EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x24);
				EmitByte(code, ref offset, 0x28); // mov rcx, stderr
				EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
				*(nint*)(code + offset) = flushFileBuffers;
				offset += sizeof(nint);
				EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0);
			}

			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83);
			EmitByte(code, ref offset, 0xC4); EmitByte(code, ref offset, 0x40);
			EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x5A); // pop r10
			EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x59);
			EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x58);
			EmitByte(code, ref offset, 0x5A);
			EmitByte(code, ref offset, 0x59);
			EmitByte(code, ref offset, 0x58);
			EmitByte(code, ref offset, 0x31); EmitByte(code, ref offset, 0xC0);
			EmitByte(code, ref offset, 0xC3);

			var msgOffset = offset;
			for (int i = 0; i < msg.Length; i++)
			{
				EmitByte(code, ref offset, msg[i]);
			}

			var hexDigitsOffset = offset;
			for (int i = 0; i < hexDigits.Length; i++)
			{
				EmitByte(code, ref offset, hexDigits[i]);
			}

			*(nint*)(code + msgAbsSlot) = (nint)ptr + msgOffset;
			*(nint*)(code + hexDigitsAbsSlot) = (nint)ptr + hexDigitsOffset;
			code[passJumpOffsets[fastFailJumpSlot]] =
				checked((byte)(fastFailPassOffset - (passJumpOffsets[fastFailJumpSlot] + 1)));
			fastFailLogInstalled = true;
		}

		int mainBodyOffset = offset;
		*(int*)(code + mainBodyJumpSlot) = mainBodyOffset - (mainBodyJumpSlot + sizeof(int));
		for (int i = 0; i < nonManagedExceptionCodes.Length; i++)
		{
			if (i == fastFailJumpSlot && fastFailLogInstalled)
			{
				continue;
			}

			code[passJumpOffsets[i]] = checked((byte)(passOffset - (passJumpOffsets[i] + 1)));
		}

		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x54); // push r12
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x55); // push r13
		EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xE4); // mov r12, rsp
		EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xCD); // mov r13, rcx

		// Native worker EXECUTE-AV abort without managed VEH.
		// Do NOT catch read/write AVs — workers need managed lazy-commit (tLTJ
		// silent-die when every worker AV was aborted). Execute faults on
		// tbb_thead are the concurrent-managed FailFast case (tLTC).
		int tbbFallthroughJump = -1;
		if (_workerAbortStub != 0 &&
			_tlsGetValueAddress != 0 &&
			_hostRspSlotTlsIndex != uint.MaxValue)
		{
			EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0x8B);
			EmitByte(code, ref offset, 0x45); EmitByte(code, ref offset, 0x00); // mov rax, [r13]
			EmitByte(code, ref offset, 0x81); EmitByte(code, ref offset, 0x38);
			EmitUInt32(code, ref offset, 0xC0000005u); // cmp dword [rax], AV
			EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x85);
			tbbFallthroughJump = offset;
			EmitUInt32(code, ref offset, 0u); // jne fallthrough

			// ExceptionInformation[0] == 8 → EXECUTE (DEP). Offset 32 on x64 RECORD.
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83);
			EmitByte(code, ref offset, 0xB8); EmitUInt32(code, ref offset, 32u);
			EmitByte(code, ref offset, 0x08); // cmp qword [rax+32], 8
			EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x85);
			var tbbNotExecuteJump = offset;
			EmitUInt32(code, ref offset, 0u);

			int tbbNotEligibleJump = -1;
			if (_tbbAbortEligibleTlsIndex != uint.MaxValue)
			{
				EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83);
				EmitByte(code, ref offset, 0xEC); EmitByte(code, ref offset, 0x28);
				EmitByte(code, ref offset, 0xB9);
				EmitUInt32(code, ref offset, _tbbAbortEligibleTlsIndex);
				EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
				*(nint*)(code + offset) = _tlsGetValueAddress;
				offset += sizeof(nint);
				EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0);
				EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83);
				EmitByte(code, ref offset, 0xC4); EmitByte(code, ref offset, 0x28);
				EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x85);
				EmitByte(code, ref offset, 0xC0);
				EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x84);
				tbbNotEligibleJump = offset;
				EmitUInt32(code, ref offset, 0u);
			}

			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83);
			EmitByte(code, ref offset, 0xEC); EmitByte(code, ref offset, 0x28);
			EmitByte(code, ref offset, 0xB9);
			EmitUInt32(code, ref offset, _hostRspSlotTlsIndex);
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
			*(nint*)(code + offset) = _tlsGetValueAddress;
			offset += sizeof(nint);
			EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0);
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83);
			EmitByte(code, ref offset, 0xC4); EmitByte(code, ref offset, 0x28);
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x85);
			EmitByte(code, ref offset, 0xC0);
			EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x84);
			var tbbNoHostRspJump = offset;
			EmitUInt32(code, ref offset, 0u);
			EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x8B);
			EmitByte(code, ref offset, 0x00); // mov r8, [rax] hostRsp
			EmitByte(code, ref offset, 0x4D); EmitByte(code, ref offset, 0x85);
			EmitByte(code, ref offset, 0xC0);
			EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x84);
			var tbbZeroRspJump = offset;
			EmitUInt32(code, ref offset, 0u);

			EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0x83);
			EmitByte(code, ref offset, 0xE0); EmitByte(code, ref offset, 0xF0); // and r8, ~0xF
			EmitByte(code, ref offset, 0x4D); EmitByte(code, ref offset, 0x8B);
			EmitByte(code, ref offset, 0x4D); EmitByte(code, ref offset, 0x08); // mov r9, [r13+8]
			EmitByte(code, ref offset, 0x4D); EmitByte(code, ref offset, 0x89);
			EmitByte(code, ref offset, 0x81); EmitUInt32(code, ref offset, 0x98u); // Context.Rsp
			EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
			*(nint*)(code + offset) = _workerAbortStub;
			offset += sizeof(nint);
			EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0x89);
			EmitByte(code, ref offset, 0x81); EmitUInt32(code, ref offset, 0xF8u); // Context.Rip
			EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0xC7);
			EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x78);
			EmitUInt32(code, ref offset, 0u); // Context.Rax = 0

			EmitByte(code, ref offset, 0xB8); EmitUInt32(code, ref offset, unchecked((uint)-1));
			EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x89);
			EmitByte(code, ref offset, 0xE4); // mov rsp, r12
			EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x5D);
			EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x5C);
			EmitByte(code, ref offset, 0xC3);

			int tbbFallthroughOffset = offset;
			*(int*)(code + tbbFallthroughJump) = tbbFallthroughOffset - (tbbFallthroughJump + sizeof(int));
			*(int*)(code + tbbNotExecuteJump) = tbbFallthroughOffset - (tbbNotExecuteJump + sizeof(int));
			if (tbbNotEligibleJump >= 0)
			{
				*(int*)(code + tbbNotEligibleJump) = tbbFallthroughOffset - (tbbNotEligibleJump + sizeof(int));
			}
			*(int*)(code + tbbNoHostRspJump) = tbbFallthroughOffset - (tbbNoHostRspJump + sizeof(int));
			*(int*)(code + tbbZeroRspJump) = tbbFallthroughOffset - (tbbZeroRspJump + sizeof(int));
		}

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
		// Serialize managed VEH entry (recursive spinlock). Concurrent UnmanagedCallersOnly
		// FailFast was the tLTQ silent mid-TBB pattern (enter without abort breadcrumb).
		// Lock layout: [0]=owner UniqueThread (nint), [8]=depth (int).
		EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0xB9);
		*(nint*)(code + offset) = _vehManagedEntryLock;
		offset += sizeof(nint); // mov r9, lock*
		EmitByte(code, ref offset, 0x65); EmitByte(code, ref offset, 0x4C);
		EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x14);
		EmitByte(code, ref offset, 0x25); EmitUInt32(code, ref offset, 0x48u); // mov r10, gs:[0x48]
		int hostAcquireSpin = offset;
		EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x01); // mov rax, [r9]
		EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x39); EmitByte(code, ref offset, 0xD0); // cmp rax, r10
		EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x84);
		int hostMineJump = offset;
		EmitUInt32(code, ref offset, 0u);
		EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x85); EmitByte(code, ref offset, 0xC0); // test rax, rax
		EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x85);
		int hostPauseJump = offset;
		EmitUInt32(code, ref offset, 0u);
		EmitByte(code, ref offset, 0xF0); EmitByte(code, ref offset, 0x4C);
		EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0xB1); EmitByte(code, ref offset, 0x11); // lock cmpxchg [r9], r10
		EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x85);
		int hostRetryJump = offset;
		EmitUInt32(code, ref offset, 0u);
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0xC7);
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x08);
		EmitUInt32(code, ref offset, 1u); // mov dword [r9+8], 1
		EmitByte(code, ref offset, 0xE9);
		int hostGotJump = offset;
		EmitUInt32(code, ref offset, 0u);
		int hostPauseOffset = offset;
		EmitByte(code, ref offset, 0xF3); EmitByte(code, ref offset, 0x90); // pause
		EmitByte(code, ref offset, 0xE9);
		int hostPauseBackJump = offset;
		EmitUInt32(code, ref offset, 0u);
		int hostMineOffset = offset;
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0xFF);
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x08); // inc dword [r9+8]
		int hostGotOffset = offset;
		*(int*)(code + hostMineJump) = hostMineOffset - (hostMineJump + sizeof(int));
		*(int*)(code + hostPauseJump) = hostPauseOffset - (hostPauseJump + sizeof(int));
		*(int*)(code + hostRetryJump) = hostAcquireSpin - (hostRetryJump + sizeof(int));
		*(int*)(code + hostGotJump) = hostGotOffset - (hostGotJump + sizeof(int));
		*(int*)(code + hostPauseBackJump) = hostAcquireSpin - (hostPauseBackJump + sizeof(int));
		EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xE9); // mov rcx, r13
		EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
		*(nint*)(code + offset) = managedHandler;
		offset += sizeof(nint);
		EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0);
		EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0xB9);
		*(nint*)(code + offset) = _vehManagedEntryLock;
		offset += sizeof(nint); // mov r9, lock*
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0xFF);
		EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0x08); // dec dword [r9+8]
		EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x85);
		int hostStillJump = offset;
		EmitUInt32(code, ref offset, 0u);
		EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0xC7);
		EmitByte(code, ref offset, 0x01); EmitUInt32(code, ref offset, 0u); // mov qword [r9], 0
		int hostStillOffset = offset;
		*(int*)(code + hostStillJump) = hostStillOffset - (hostStillJump + sizeof(int));
		EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83); EmitByte(code, ref offset, 0xC4); EmitByte(code, ref offset, 0x28);
		EmitByte(code, ref offset, 0xE9);
		int hostRestoreJump = offset;
		EmitUInt32(code, ref offset, 0u);

		int guestStackOffset = offset;
		EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x83); EmitByte(code, ref offset, 0xEC); EmitByte(code, ref offset, 0x28);
		EmitByte(code, ref offset, 0xB9);
		EmitUInt32(code, ref offset, _hostRspSlotTlsIndex);
		EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
		*(nint*)(code + offset) = _tlsGetValueAddress;
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
		EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0xB9);
		*(nint*)(code + offset) = _vehManagedEntryLock;
		offset += sizeof(nint); // mov r9, lock*
		EmitByte(code, ref offset, 0x65); EmitByte(code, ref offset, 0x4C);
		EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x14);
		EmitByte(code, ref offset, 0x25); EmitUInt32(code, ref offset, 0x48u); // mov r10, gs:[0x48]
		int guestAcquireSpin = offset;
		EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x01); // mov rax, [r9]
		EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x39); EmitByte(code, ref offset, 0xD0); // cmp rax, r10
		EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x84);
		int guestMineJump = offset;
		EmitUInt32(code, ref offset, 0u);
		EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x85); EmitByte(code, ref offset, 0xC0);
		EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x85);
		int guestPauseJump = offset;
		EmitUInt32(code, ref offset, 0u);
		EmitByte(code, ref offset, 0xF0); EmitByte(code, ref offset, 0x4C);
		EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0xB1); EmitByte(code, ref offset, 0x11);
		EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x85);
		int guestRetryJump = offset;
		EmitUInt32(code, ref offset, 0u);
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0xC7);
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x08);
		EmitUInt32(code, ref offset, 1u);
		EmitByte(code, ref offset, 0xE9);
		int guestGotJump = offset;
		EmitUInt32(code, ref offset, 0u);
		int guestPauseOffset = offset;
		EmitByte(code, ref offset, 0xF3); EmitByte(code, ref offset, 0x90);
		EmitByte(code, ref offset, 0xE9);
		int guestPauseBackJump = offset;
		EmitUInt32(code, ref offset, 0u);
		int guestMineOffset = offset;
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0xFF);
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x08);
		int guestGotOffset = offset;
		*(int*)(code + guestMineJump) = guestMineOffset - (guestMineJump + sizeof(int));
		*(int*)(code + guestPauseJump) = guestPauseOffset - (guestPauseJump + sizeof(int));
		*(int*)(code + guestRetryJump) = guestAcquireSpin - (guestRetryJump + sizeof(int));
		*(int*)(code + guestGotJump) = guestGotOffset - (guestGotJump + sizeof(int));
		*(int*)(code + guestPauseBackJump) = guestAcquireSpin - (guestPauseBackJump + sizeof(int));
		EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xE9); // mov rcx, r13
		EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
		*(nint*)(code + offset) = managedHandler;
		offset += sizeof(nint);
		EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0xD0);
		EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0xB9);
		*(nint*)(code + offset) = _vehManagedEntryLock;
		offset += sizeof(nint);
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0xFF);
		EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0x08); // dec dword [r9+8]
		EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x85);
		int guestStillJump = offset;
		EmitUInt32(code, ref offset, 0u);
		EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0xC7);
		EmitByte(code, ref offset, 0x01); EmitUInt32(code, ref offset, 0u);
		int guestStillOffset = offset;
		*(int*)(code + guestStillJump) = guestStillOffset - (guestStillJump + sizeof(int));
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

		if (offset > (int)stubSize)
		{
			Console.Error.WriteLine(
				$"[LOADER][ERROR] Exception handler trampoline overflow: used={offset} cap={stubSize}");
			VirtualFree(ptr, 0, 0x8000u);
			return 0;
		}

		Console.Error.WriteLine(
			$"[LOADER][INFO] VEH trampoline built: bytes={offset} native_worker_abort=" +
			$"{(_workerAbortStub != 0 && _hostRspSlotTlsIndex != uint.MaxValue)}");

		uint oldProtect = default;
		VirtualProtect(ptr, stubSize, 32u, &oldProtect);
		FlushInstructionCache(GetCurrentProcess(), ptr, (nuint)offset);
		return (nint)ptr;
	}

	private unsafe void* TryAllocateNearEntry(nuint size)
	{
		ulong entryPoint = _entryPoint;
		ulong baseAddress = entryPoint & 0xFFFFFFFFFFFF0000uL;
		for (long num = 0L; num <= 1879048192; num += 16777216)
		{
			if (TryAllocAt(baseAddress, num, size, out var memory))
			{
				return memory;
			}
			if (num != 0L && TryAllocAt(baseAddress, -num, size, out memory))
			{
				return memory;
			}
		}
		return null;
	}

	private unsafe static bool TryAllocAt(ulong baseAddress, long signedDelta, nuint size, out void* memory)
	{
		memory = null;
		ulong num;
		if (signedDelta >= 0)
		{
			if (baseAddress > (ulong)(-1 - signedDelta))
			{
				return false;
			}
			num = baseAddress + (ulong)signedDelta;
		}
		else
		{
			ulong num2 = (ulong)(-signedDelta);
			if (baseAddress < num2)
			{
				return false;
			}
			num = baseAddress - num2;
		}
		void* ptr = VirtualAlloc((void*)num, size, 12288u, 64u);
		if (ptr == null)
		{
			return false;
		}
		memory = ptr;
		return true;
	}

	private unsafe void PatchTlsPatterns()
	{
        // Large Gen5 executables can keep valid code well past the first 32 MiB.
        // Astro Bot, for example, has an FS:[0] TLS load near +0x70A0000.
        const ulong MaxScanBytes = 134217728uL;
		ulong num = _entryPoint;
		ulong num2 = num + MaxScanBytes;
		int num3 = 0;
		int num4 = 0;
		int num9 = 0;
		int sse4aPatchCount = 0;
		while (num < num2)
		{
			if (VirtualQuery((void*)num, out var lpBuffer, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) == 0 || lpBuffer.RegionSize == 0)
			{
				num += 4096uL;
				continue;
			}
			ulong num5 = Math.Max(num, lpBuffer.BaseAddress);
			ulong num6 = lpBuffer.BaseAddress + lpBuffer.RegionSize;
			if (num6 > num2)
			{
				num6 = num2;
			}
			uint num7 = lpBuffer.Protect & 0xFF;
			bool flag = lpBuffer.State == 4096 && (lpBuffer.Protect & PAGE_GUARD) == 0 && num7 != PAGE_NOACCESS;
			bool flag2 = num7 == PAGE_EXECUTE || num7 == 32 || num7 == 64 || num7 == PAGE_EXECUTE_WRITECOPY;
			if (flag && flag2 && num6 > num5 + MinTlsPatchInstructionBytes)
			{
				byte* ptr = (byte*)num5;
				int scanBytes = (int)(num6 - num5);
				for (int i = 0; i <= scanBytes - MinTlsPatchInstructionBytes; i++)
				{
					nint address = (nint)(ptr + i);
					int remainingBytes = scanBytes - i;
					if (TryPatchTlsLoadInstruction(address, ptr + i, remainingBytes))
					{
						num3++;
					}
					else if (remainingBytes >= 12 && TryPatchTlsImmediateStoreInstruction(address, ptr + i))
					{
						num9++;
					}
					else if (remainingBytes >= 12 && TryPatchSse4aExtrqBlend(address, ptr + i))
					{
						sse4aPatchCount++;
					}
					else if (TryPatchStackCanaryInstruction(address, ptr + i))
					{
						num4++;
					}
				}
			}
			num = num6 > num ? num6 : num + 4096uL;
		}
		Console.Error.WriteLine($"[LOADER][INFO] Patched {num3} TLS loads, {num9} TLS stores, {num4} stack-canary accesses, {sse4aPatchCount} SSE4a EXTRQ blends");
	}

	private unsafe bool TryPatchSse4aExtrqBlend(nint address, byte* source)
	{
		// Rosetta does not implement AMD SSE4a EXTRQ. Recognize the compiler's
		// EXTRQ+blend idiom (against whichever xmm0-xmm7 it allocated) and rewrite
		// it into an equivalent SSE4.1 sequence. Match/encode is isolated in
		// Sse4aExtrqBlendPatch so it can be unit-tested; here we only patch bytes.
		var window = new ReadOnlySpan<byte>(source, Sse4aExtrqBlendPatch.SequenceLength);
		if (!Sse4aExtrqBlendPatch.TryMatch(window, out var destRegister, out var srcRegister))
		{
			return false;
		}

		Span<byte> replacement = stackalloc byte[Sse4aExtrqBlendPatch.SequenceLength];
		if (!Sse4aExtrqBlendPatch.TryEncode(destRegister, srcRegister, replacement))
		{
			return false;
		}

		uint oldProtect = 0;
		if (!VirtualProtect((void*)address, (nuint)replacement.Length, 64u, &oldProtect))
		{
			return false;
		}
		try
		{
			for (var i = 0; i < replacement.Length; i++)
			{
				((byte*)address)[i] = replacement[i];
			}
		}
		finally
		{
			VirtualProtect((void*)address, (nuint)replacement.Length, oldProtect, &oldProtect);
			FlushInstructionCache(GetCurrentProcess(), (void*)address, (nuint)replacement.Length);
		}
		return true;
	}

	private unsafe bool IsPatternMatch(byte* ptr, byte[] pattern)
	{
		for (int i = 0; i < pattern.Length; i++)
		{
			if (ptr[i] != pattern[i])
			{
				return false;
			}
		}
		return true;
	}

	private unsafe bool TryPatchStackCanaryInstruction(nint address, byte* source)
	{
		if (*source != 100)
		{
			return false;
		}
		byte b = 0;
		int num = 1;
		int num2 = 8;
		if (source[1] >= 64 && source[1] <= 79)
		{
			b = source[1];
			num = 2;
			num2 = 9;
		}
		byte b2 = source[num];
		if (b2 != 139 && b2 != 51)
		{
			return false;
		}
		byte b3 = source[num + 1];
		byte b4 = source[num + 2];
		if (b3 >> 6 != 0 || (b3 & 7) != 4 || b4 != 37)
		{
			return false;
		}
		int num3 = *(int*)(source + num + 3);
		if (num3 != 40)
		{
			return false;
		}
		int num4 = ((b3 >> 3) & 7) | (((b & 4) != 0) ? 8 : 0);
		bool flag = (b & 8) != 0;
		int num5 = 64;
		if (flag)
		{
			num5 |= 8;
		}
		if (num4 >= 8)
		{
			num5 |= 5;
		}
		byte b5 = (byte)(0xC0 | ((num4 & 7) << 3) | (num4 & 7));
		uint flNewProtect = default(uint);
		if (!VirtualProtect((void*)address, (nuint)num2, 64u, &flNewProtect))
		{
			return false;
		}
		try
		{
			*(byte*)address = (byte)num5;
			*(sbyte*)(address + 1) = 49;
			*(byte*)(address + 2) = b5;
			for (int i = 3; i < num2; i++)
			{
				*(sbyte*)(address + i) = -112;
			}
		}
		finally
		{
			VirtualProtect((void*)address, (nuint)num2, flNewProtect, &flNewProtect);
			FlushInstructionCache(GetCurrentProcess(), (void*)address, (nuint)num2);
		}
		return true;
	}

	private unsafe bool TryPatchTlsLoadInstruction(nint address, byte* source, int availableLength)
	{
		if (availableLength < MinTlsPatchInstructionBytes)
		{
			return false;
		}

		var offset = 0;
		while (offset < availableLength && source[offset] == 0x66)
		{
			offset++;
		}

		if (offset >= availableLength || source[offset] != 0x64)
		{
			return false;
		}

		offset++;
		if (offset >= availableLength)
		{
			return false;
		}

		var rex = (byte)0;
		if (source[offset] >= 0x40 && source[offset] <= 0x4F)
		{
			rex = source[offset];
			offset++;
		}

		if (offset + 7 > availableLength || source[offset] != 0x8B)
		{
			return false;
		}

		var modRm = source[offset + 1];
		var sib = source[offset + 2];
		if ((modRm >> 6) != 0 || (modRm & 7) != 4 || sib != 0x25)
		{
			return false;
		}

		var displacement = *(int*)(source + offset + 3);
		if (displacement != 0)
		{
			return false;
		}

		var destinationRegister = ((modRm >> 3) & 7) | (((rex & 4) != 0) ? 8 : 0);
		var instructionLength = offset + 7;
		if (instructionLength < MinTlsPatchInstructionBytes)
		{
			return false;
		}

		return PatchTlsLoadInstruction(address, instructionLength, destinationRegister);
	}

	private unsafe bool PatchTlsLoadInstruction(nint address, int instructionLength, int destinationRegister)
	{
		uint flNewProtect = default(uint);
		if (!VirtualProtect((void*)address, (nuint)instructionLength, 64u, &flNewProtect))
		{
			return false;
		}
		try
		{
			*(sbyte*)address = -24;
			long num = _tlsHandlerAddress;
			long num2 = address + 5;
			long num3 = num - num2;
			if (num3 < int.MinValue || num3 > int.MaxValue)
			{
				Console.Error.WriteLine($"[LOADER][WARNING] TLS patch out of rel32 range at 0x{address:X16}");
				return false;
			}

			*(int*)(address + 1) = (int)num3;
			var offset = 5;
			if (destinationRegister != 0)
			{
				*(byte*)(address + offset++) = (byte)(0x48 | (destinationRegister >= 8 ? 1 : 0));
				*(byte*)(address + offset++) = 0x89;
				*(byte*)(address + offset++) = (byte)(0xC0 | (destinationRegister & 7));
			}

			while (offset < instructionLength)
			{
				*(byte*)(address + offset++) = 0x90;
			}

			return true;
		}
		finally
		{
			VirtualProtect((void*)address, (nuint)instructionLength, flNewProtect, &flNewProtect);
			FlushInstructionCache(GetCurrentProcess(), (void*)address, (nuint)instructionLength);
		}
	}

	private unsafe bool TryPatchTlsImmediateStoreInstruction(nint address, byte* source)
	{
		if (source[0] != 100 || source[1] != 199 || source[2] != 4 || source[3] != 37)
		{
			return false;
		}
		int tlsOffset = *(int*)(source + 4);
		int immediateValue = *(int*)(source + 8);
		nint num = CreateTlsImmediateStoreHelper(tlsOffset, immediateValue);
		if (num == 0)
		{
			return false;
		}
		return PatchCallSite(address, 12, num);
	}

	private unsafe nint CreateTlsImmediateStoreHelper(int tlsOffset, int immediateValue)
	{
		nint num = AllocateTlsPatchStub(32);
		if (num == 0)
		{
			return 0;
		}
		byte* ptr = (byte*)num;
		int num2 = 0;
		ptr[num2++] = 80;
		ptr[num2++] = 232;
		long num3 = _tlsHandlerAddress - (num + num2 + 4);
		if (num3 < int.MinValue || num3 > int.MaxValue)
		{
			Console.Error.WriteLine($"[LOADER][WARNING] TLS store helper out of rel32 range at 0x{num:X16}");
			return 0;
		}
		*(int*)(ptr + num2) = (int)num3;
		num2 += 4;
		ptr[num2++] = 199;
		ptr[num2++] = 128;
		*(int*)(ptr + num2) = tlsOffset;
		num2 += 4;
		*(int*)(ptr + num2) = immediateValue;
		num2 += 4;
		ptr[num2++] = 88;
		ptr[num2++] = 195;
		while (num2 < 32)
		{
			ptr[num2++] = 144;
		}
		uint flNewProtect = default(uint);
		VirtualProtect((void*)num, 32u, 32u, &flNewProtect);
		FlushInstructionCache(GetCurrentProcess(), (void*)num, 32u);
		return num;
	}

	private unsafe nint AllocateTlsPatchStub(int size)
	{
		if (_tlsHandlerAddress == 0 || size <= 0)
		{
			return 0;
		}
		int num = (size + 15) & -16;
		if (_tlsPatchStubOffset + num > TlsHandlerRegionSize)
		{
			Console.Error.WriteLine("[LOADER][WARNING] TLS patch stub region exhausted.");
			return 0;
		}
		nint result = _tlsHandlerAddress + _tlsPatchStubOffset;
		_tlsPatchStubOffset += num;
		uint flNewProtect = default(uint);
		if (!VirtualProtect((void*)result, (nuint)num, 64u, &flNewProtect))
		{
			return 0;
		}
		return result;
	}

	private unsafe bool PatchCallSite(nint address, int instructionLength, nint target)
	{
		if (instructionLength < 5)
		{
			return false;
		}
		uint flNewProtect = default(uint);
		if (!VirtualProtect((void*)address, (nuint)instructionLength, 64u, &flNewProtect))
		{
			return false;
		}
		try
		{
			long num = target - (address + 5);
			if (num < int.MinValue || num > int.MaxValue)
			{
				Console.Error.WriteLine($"[LOADER][WARNING] TLS patch out of rel32 range at 0x{address:X16}");
				return false;
			}
			*(byte*)address = 232;
			*(int*)(address + 1) = (int)num;
			for (int i = 5; i < instructionLength; i++)
			{
				*(byte*)(address + i) = 144;
			}
		}
		finally
		{
			VirtualProtect((void*)address, (nuint)instructionLength, flNewProtect, &flNewProtect);
			FlushInstructionCache(GetCurrentProcess(), (void*)address, (nuint)instructionLength);
		}
		return true;
	}

	private unsafe void TryPreReservePrtAperture(ulong baseAddress, ulong size)
	{
		if (VirtualQuery((void*)baseAddress, out var lpBuffer, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) != 0 && lpBuffer.State != 65536)
		{
			Console.Error.WriteLine($"[LOADER][INFO] PRT aperture at 0x{baseAddress:X16} already in use (state=0x{lpBuffer.State:X}), will use lazy-commit");
			return;
		}
		ulong num = baseAddress;
		ulong num2 = baseAddress + size;
		int num3 = 0;
		int num4 = 0;
		nuint num5;
		for (; num < num2; num += num5)
		{
			ulong val = num2 - num;
			num5 = (nuint)Math.Min(2097152uL, val);
			void* ptr = VirtualAlloc((void*)num, num5, 8192u, 4u);
			if (ptr != null)
			{
				num3++;
			}
			else
			{
				num4++;
			}
		}
		if (num4 == 0)
		{
			Console.Error.WriteLine($"[LOADER][INFO] Pre-reserved PRT aperture: 0x{baseAddress:X16}-0x{num2:X16} ({num3} chunks)");
		}
		else
		{
			Console.Error.WriteLine($"[LOADER][INFO] Partial PRT aperture reserve: 0x{baseAddress:X16}-0x{num2:X16} ({num3} chunks OK, {num4} failed)");
		}
		ulong num6 = baseAddress;
		ulong num7 = baseAddress + 67108864;
		int num8 = 0;
		for (; num6 < num7; num6 += 2097152)
		{
			void* ptr2 = VirtualAlloc((void*)num6, 2097152u, 4096u, 4u);
			if (ptr2 != null)
			{
				num8++;
			}
		}
		if (num8 > 0)
		{
			Console.Error.WriteLine($"[LOADER][INFO] Pre-committed PRT bootstrap: 0x{baseAddress:X16}-0x{num7:X16} ({num8 * 2}MB in {num8} chunks)");
		}
		else
		{
			Console.Error.WriteLine($"[LOADER][WARN] Failed to pre-commit any PRT bootstrap chunks at 0x{baseAddress:X16}");
		}
	}

	private void RegisterPrtLazyCommitRange(ulong baseAddress, ulong size)
	{
		if (size == 0)
		{
			return;
		}

		bool added = false;
		lock (_lazyCommitRangeGate)
		{
			if (!_prtLazyCommitRanges.Any(range => range.BaseAddress == baseAddress && range.Size == size))
			{
				_prtLazyCommitRanges.Add(new LazyCommitRange(baseAddress, size));
				added = true;
			}
		}

		if (added)
		{
			Console.Error.WriteLine($"[LOADER][TRACE] registered PRT lazy range: base=0x{baseAddress:X16} size=0x{size:X16}");
		}
	}

	private bool IsGuestOwnedLazyCommitAddress(ulong address, out string owner)
	{
		var cpuContext = ActiveCpuContext;
		if (cpuContext != null && TryGetVirtualMemory(cpuContext, out var virtualMemory))
		{
			foreach (var region in virtualMemory.SnapshotRegions())
			{
				if (ContainsAddress(region.VirtualAddress, region.MemorySize, address))
				{
					owner = $"vmem:0x{region.VirtualAddress:X16}+0x{region.MemorySize:X}";
					return true;
				}
			}
		}

		lock (_lazyCommitRangeGate)
		{
			foreach (var range in _prtLazyCommitRanges)
			{
				if (ContainsAddress(range.BaseAddress, range.Size, address))
				{
					owner = $"prt:0x{range.BaseAddress:X16}+0x{range.Size:X}";
					return true;
				}
			}
		}

		owner = string.Empty;
		return false;
	}

	private static bool ContainsAddress(ulong baseAddress, ulong size, ulong address)
	{
		return size != 0 && address >= baseAddress && address - baseAddress < size;
	}

	public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error)
	{
		error = null;
		if (request.ThreadHandle == 0 || request.EntryPoint < 65536)
		{
			error = $"invalid thread start request: handle=0x{request.ThreadHandle:X16} entry=0x{request.EntryPoint:X16}";
			return false;
		}
		if (!TryCreateGuestThreadState(creatorContext, request, out var thread, out error))
		{
			return false;
		}
		using (LockGate("TryStartThread"))
		{
			_guestThreads[request.ThreadHandle] = thread;
			_readyGuestThreads.Enqueue(thread);
			Interlocked.Increment(ref _readyGuestThreadCount);
		}
		Console.Error.WriteLine(
			$"[LOADER][INFO] Scheduled guest thread '{thread.Name}' handle=0x{thread.ThreadHandle:X16} " +
			$"entry=0x{thread.EntryPoint:X16} arg=0x{thread.Argument:X16} priority={thread.Priority} " +
			$"host_priority={MapGuestThreadPriority(thread.Priority)} affinity=0x{thread.AffinityMask:X}");
		Pump(creatorContext, "pthread_create");
		// Pump is suppressed while another cooperative dispatch is active. The
		// background dispatcher would eventually observe this thread, but an
		// immediate authoritative drain avoids making thread creation depend on
		// the approximate ready-count polling hint.
		DispatchReadyGuestThreads();
		return true;
	}

	public bool SupportsGuestContextTransfer => true;

	public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
	{
		if (threadHandle == 0)
		{
			return;
		}

		lock (_guestThreadGate)
		{
			_currentExternalGuestThreadHandle = threadHandle;
			if (_guestThreads.ContainsKey(threadHandle))
			{
				return;
			}

			if (_externalGuestThreads.TryGetValue(threadHandle, out var existing))
			{
				existing.Context = context;
				return;
			}

			_externalGuestThreads[threadHandle] = new ExternalGuestThreadState
			{
				Context = context,
			};
		}
	}

	public bool TryJoinThread(
		CpuContext callerContext,
		ulong threadHandle,
		out ulong returnValue,
		out string? error)
	{
		returnValue = 0;
		error = null;
		if (threadHandle == 0)
		{
			error = "thread handle is zero";
			return false;
		}

		if (threadHandle == GuestThreadExecution.CurrentGuestThreadHandle)
		{
			error = "thread cannot join itself";
			return false;
		}

		// Joins regularly park here for minutes (a game main thread joining a
		// streamer); polling at a fixed 1ms burns half a host core for the
		// whole wait, so back off toward a 10ms cadence once the join is
		// clearly long-lived.
		var joinPollMilliseconds = 1;
		while (!ActiveForcedGuestExit)
		{
			Thread? hostThread;
			using (LockGate("TryJoinThread"))
			{
				if (!_guestThreads.TryGetValue(threadHandle, out var thread))
				{
					error = $"unknown guest thread 0x{threadHandle:X16}";
					return false;
				}

				if (thread.State == GuestThreadRunState.Exited)
				{
					returnValue = thread.ExitValue;
					return true;
				}

				if (thread.State == GuestThreadRunState.Faulted)
				{
					error =
						$"guest thread 0x{threadHandle:X16} faulted: " +
						(thread.BlockReason ?? "unknown error");
					return false;
				}

				hostThread = thread.HostThread;
			}

			if (hostThread is not null &&
				!ReferenceEquals(hostThread, Thread.CurrentThread))
			{
				hostThread.Join(1);
			}
			else
			{
				Thread.Sleep(joinPollMilliseconds);
			}

			if (joinPollMilliseconds < 10)
			{
				joinPollMilliseconds++;
			}
		}

		error = "guest execution stopped while joining thread";
		return false;
	}

	public void Pump(CpuContext callerContext, string reason)
	{
		_ = callerContext;
		var runSynchronously = string.Equals(reason, "entry_return", StringComparison.Ordinal);
		WakeExpiredBlockedGuestThreads();
		if (Volatile.Read(ref _readyGuestThreadCount) == 0)
		{
			return;
		}
		if (Interlocked.CompareExchange(ref _guestThreadPumpDepth, 1, 0) != 0)
		{
			return;
		}
		try
		{
			for (int i = 0; i < 8; i++)
			{
				GuestThreadState? thread = null;
				using (LockGate("Pump.dequeue"))
				{
					_ = TryClaimReadyGuestThreadLocked(out thread);
				}
				if (thread == null)
				{
					return;
				}

				if (runSynchronously)
				{
					RunGuestThread(thread, reason);
					continue;
				}

				ScheduleGuestThreadExecution(thread, reason);
			}
		}
		finally
		{
			Volatile.Write(ref _guestThreadPumpDepth, 0);
		}
	}

	public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue)
	{
		if (string.IsNullOrWhiteSpace(wakeKey) || maxCount <= 0)
		{
			return 0;
		}

		var wakeCount = 0;
		using (LockGate("WakeBlockedThreads"))
		{
			foreach (var thread in _guestThreads.Values)
			{
				if (wakeCount >= maxCount)
				{
					break;
				}

				if (thread.State != GuestThreadRunState.Blocked ||
					!thread.HasBlockedContinuation ||
					!string.Equals(wakeKey, thread.BlockWakeKey, StringComparison.Ordinal))
				{
					continue;
				}

				if (thread.BlockWaiter is not null && !thread.BlockWaiter.TryWake())
				{
					continue;
				}

				thread.State = GuestThreadRunState.Ready;
				thread.BlockReason = null;
				thread.BlockDeadlineTimestamp = 0;
				_readyGuestThreads.Enqueue(thread);
				Interlocked.Increment(ref _readyGuestThreadCount);
				wakeCount++;
			}
		}

		if (wakeCount != 0)
		{
			if (_logGuestThreads)
			{
				Console.Error.WriteLine($"[LOADER][INFO] guest_threads.wake key={wakeKey} count={wakeCount}");
			}

			// Pump or the readied thread waits for an import dispatch that never comes.
			if (_cpuContext is { } wakeContext)
			{
				Pump(wakeContext, "wake");
			}
		}

		return wakeCount;
	}

	public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads()
	{
		using (LockGate("SnapshotThreads"))
		{
			var snapshots = new GuestThreadSnapshot[_guestThreads.Count];
			var index = 0;
			foreach (var thread in _guestThreads.Values)
			{
				snapshots[index++] = new GuestThreadSnapshot(
					thread.ThreadHandle,
					thread.Name,
					thread.State.ToString(),
					Interlocked.Read(ref thread.ImportCount),
					Volatile.Read(ref thread.LastImportNid),
					Volatile.Read(ref thread.LastReturnRip),
					thread.BlockReason);
			}

			return snapshots;
		}
	}

	private void RegisterBlockedGuestThreadContinuation(
		ulong guestThreadHandle,
		GuestCpuContinuation continuation,
		string wakeKey,
		IGuestThreadBlockWaiter? waiter,
		long blockDeadlineTimestamp)
	{
		if (guestThreadHandle == 0 || continuation.Rip < 65536 || continuation.Rsp == 0)
		{
			return;
		}

		using (LockGate("RegisterBlockedContinuation"))
		{
			if (!_guestThreads.TryGetValue(guestThreadHandle, out var thread))
			{
				return;
			}

			thread.BlockedContinuation = continuation;
			thread.HasBlockedContinuation = true;
			thread.BlockWakeKey = wakeKey;
			thread.BlockWaiter = waiter;
			thread.BlockDeadlineTimestamp = blockDeadlineTimestamp;
			TraceFocusedContinuation(
				"register",
				guestThreadHandle,
				continuation,
				wakeKey);
		}
	}

	private int WakeExpiredBlockedGuestThreads()
	{
		var now = Stopwatch.GetTimestamp();
		var wakeCount = 0;
		using (LockGate("WakeExpiredBlockedGuestThreads"))
		{
			foreach (var thread in _guestThreads.Values)
			{
				if (thread.State != GuestThreadRunState.Blocked ||
					!thread.HasBlockedContinuation ||
					thread.BlockDeadlineTimestamp == 0 ||
					thread.BlockDeadlineTimestamp > now)
				{
					continue;
				}

				thread.State = GuestThreadRunState.Ready;
				thread.BlockReason = null;
				thread.BlockDeadlineTimestamp = 0;
				_readyGuestThreads.Enqueue(thread);
				Interlocked.Increment(ref _readyGuestThreadCount);
				wakeCount++;
			}
		}

		if (wakeCount != 0 && _logGuestThreads)
		{
			Console.Error.WriteLine($"[LOADER][INFO] guest_threads.timeout_wake count={wakeCount}");
		}

		return wakeCount;
	}

	private void PumpUntilGuestThreadsIdle(CpuContext callerContext, string reason)
	{
		var nextSnapshotTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
		while (!ActiveForcedGuestExit)
		{
			Pump(callerContext, reason);

			// Tally run states under the lock without allocating a snapshot every
			// spin (this loop can iterate rapidly); the full snapshot is only
			// materialized for the gated diagnostic dump below.
			GetGuestThreadActivity(out var threadCount, out var hasReadyThread, out var hasRunningThread, out var hasBlockedThread);
			if (threadCount == 0)
			{
				return;
			}

			if (hasReadyThread)
			{
				continue;
			}

			if (!hasRunningThread && !hasBlockedThread)
			{
				return;
			}

			if (_logGuestThreads && Stopwatch.GetTimestamp() >= nextSnapshotTimestamp)
			{
				foreach (var thread in SnapshotGuestThreads())
				{
					Console.Error.WriteLine(
						$"[LOADER][TRACE] guest_thread.idle_wait reason={reason} handle=0x{thread.ThreadHandle:X16} " +
						$"name='{thread.Name}' state={thread.State} imports={Interlocked.Read(ref thread.ImportCount)} " +
						$"nid={Volatile.Read(ref thread.LastImportNid) ?? "none"} ret=0x{Volatile.Read(ref thread.LastReturnRip):X16} " +
						$"block={thread.BlockReason ?? "none"}");
				}

				nextSnapshotTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
			}

			Thread.Sleep(1);
		}
	}

	private GuestThreadState[] SnapshotGuestThreads()
	{
		using (LockGate("SnapshotGuestThreads"))
		{
			var snapshot = new GuestThreadState[_guestThreads.Count];
			_guestThreads.Values.CopyTo(snapshot, 0);
			return snapshot;
		}
	}

	// Allocation-free run-state tally for the idle spin loop.
	private void GetGuestThreadActivity(out int count, out bool hasReady, out bool hasRunning, out bool hasBlocked)
	{
		hasReady = false;
		hasRunning = false;
		hasBlocked = false;
		using (LockGate("GetGuestThreadActivity"))
		{
			count = _guestThreads.Count;
			foreach (var thread in _guestThreads.Values)
			{
				switch (thread.State)
				{
					case GuestThreadRunState.Ready:
						hasReady = true;
						break;
					case GuestThreadRunState.Running:
						hasRunning = true;
						break;
					case GuestThreadRunState.Blocked:
						hasBlocked = true;
						break;
				}
			}
		}
	}

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
		return TryCallGuestFunction(
			callerContext,
			entryPoint,
			arg0,
			arg1,
			0,
			stackAddress,
			stackSize,
			reason,
			out _,
			out error);
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
		returnValue = 0;
		error = null;
		if (_forcedGuestExit)
		{
			error = "guest execution is shutting down";
			return false;
		}
		if (entryPoint < 65536)
		{
			error = $"invalid guest callback entry=0x{entryPoint:X16}";
			return false;
		}
		if (!TryGetVirtualMemory(callerContext, out var virtualMemory))
		{
			error = "caller context memory is not backed by IVirtualMemory";
			return false;
		}

		ulong callbackStackBase;
		ulong callbackStackSize;
		var usesCachedCallbackStack = false;
		if (stackAddress != 0 && stackSize >= 0x100)
		{
			callbackStackBase = stackAddress;
			callbackStackSize = stackSize;
		}
		else
		{
			var callbackDepth = _nestedGuestCallbackDepth;
			_nestedGuestCallbackStacks ??= [];
			if (callbackDepth < _nestedGuestCallbackStacks.Count &&
				ReferenceEquals(_nestedGuestCallbackStacks[callbackDepth].Memory, virtualMemory))
			{
				callbackStackBase = _nestedGuestCallbackStacks[callbackDepth].Base;
			}
			else
			{
				if (!TryMapGuestThreadRegion(
						virtualMemory,
						GuestThreadStackBaseAddress,
						GuestThreadStackSize,
						ProgramHeaderFlags.Read | ProgramHeaderFlags.Write,
						out callbackStackBase,
						out error))
				{
					return false;
				}

				if (callbackDepth < _nestedGuestCallbackStacks.Count)
				{
					_nestedGuestCallbackStacks[callbackDepth] = (virtualMemory, callbackStackBase);
				}
				else
				{
					_nestedGuestCallbackStacks.Add((virtualMemory, callbackStackBase));
				}
			}

			usesCachedCallbackStack = true;
			callbackStackSize = GuestThreadStackSize;
		}

		var trackedMemory = new TrackedCpuMemory(virtualMemory);
		var fallbackTlsBase = unchecked((ulong)_tlsBaseAddress);
		var context = new CpuContext(trackedMemory, callerContext.TargetGeneration)
		{
			Rip = entryPoint,
			Rflags = 0x202,
			FsBase = callerContext.FsBase != 0 ? callerContext.FsBase : fallbackTlsBase,
			GsBase = callerContext.GsBase != 0 ? callerContext.GsBase : fallbackTlsBase,
		};
		context[CpuRegister.Rsp] = AlignDown(callbackStackBase + callbackStackSize, 16) - sizeof(ulong);
		context[CpuRegister.Rdi] = arg0;
		context[CpuRegister.Rsi] = arg1;
		context[CpuRegister.Rdx] = arg2;
		context[CpuRegister.Rcx] = 0;
		context[CpuRegister.R8] = 0;
		context[CpuRegister.R9] = 0;
		if (!InitializeGuestThreadFrame(context))
		{
			error = "failed to initialize guest callback stack";
			return false;
		}
		if (usesCachedCallbackStack)
		{
			_nestedGuestCallbackDepth++;
		}

		var previousLastError = LastError;
		try
		{
			LastError = null;
			var exitReason = ExecuteGuestThreadEntry(context, entryPoint, reason, out var callbackReason);
			if (exitReason == GuestNativeCallExitReason.Blocked &&
				!ResumeBlockedNestedGuestCallback(context, reason, ref exitReason, ref callbackReason))
			{
				error = callbackReason ?? LastError ?? "guest callback could not resume after blocking";
				return false;
			}
			if (exitReason is GuestNativeCallExitReason.Exception or GuestNativeCallExitReason.ForcedExit)
			{
				error = callbackReason ?? LastError ?? "guest callback failed";
				return false;
			}

			returnValue = context[CpuRegister.Rax];
			return true;
		}
		finally
		{
			if (usesCachedCallbackStack)
			{
				_nestedGuestCallbackDepth--;
			}
			LastError = previousLastError;
		}
	}

	/// <summary>
	/// Completes a nested guest callback which blocked in an HLE import. The
	/// outer guest entry is still executing managed HLE code, so returning a
	/// successful callback result here would abandon the callback continuation
	/// and let a noreturn operation such as pthread_exit unwind through live
	/// libc cleanup state. Temporarily expose the owning guest thread as blocked,
	/// let the normal scheduler wake it, and resume the callback continuation on
	/// this executor until it either returns or fails.
	/// </summary>
	private bool ResumeBlockedNestedGuestCallback(
		CpuContext callbackContext,
		string reason,
		ref GuestNativeCallExitReason exitReason,
		ref string? callbackReason)
	{
		var guestThreadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
		if (guestThreadHandle == 0)
		{
			callbackReason = $"nested guest callback '{reason}' blocked without a schedulable guest thread";
			exitReason = GuestNativeCallExitReason.Exception;
			return false;
		}

		while (exitReason == GuestNativeCallExitReason.Blocked && !ActiveForcedGuestExit)
		{
			GuestThreadState? owner;
			lock (_guestThreadGate)
			{
				if (!_guestThreads.TryGetValue(guestThreadHandle, out owner) ||
					!owner.HasBlockedContinuation)
				{
					callbackReason =
						$"nested guest callback '{reason}' blocked without a captured continuation";
					exitReason = GuestNativeCallExitReason.Exception;
					return false;
				}

				owner.State = GuestThreadRunState.Blocked;
				owner.BlockReason = callbackReason ?? reason;
				if (owner.BlockWaiter is not null && owner.BlockWaiter.TryWake())
				{
					owner.State = GuestThreadRunState.Ready;
					owner.BlockReason = null;
					owner.BlockDeadlineTimestamp = 0;
				}
			}
			if (_logGuestThreads)
			{
				Console.Error.WriteLine(
					$"[LOADER][INFO] nested_callback.block name='{owner!.Name}' callback='{reason}' " +
					$"wake={owner.BlockWakeKey ?? "none"} continuation=0x{owner.BlockedContinuation.Rip:X16}");
			}

			GuestCpuContinuation continuation = default;
			IGuestThreadBlockWaiter? blockWaiter = null;
			while (!ActiveForcedGuestExit)
			{
				WakeExpiredBlockedGuestThreads();
				var ready = false;
				lock (_guestThreadGate)
				{
					if (!_guestThreads.TryGetValue(guestThreadHandle, out owner))
					{
						callbackReason =
							$"nested guest callback '{reason}' lost its owning guest thread";
						exitReason = GuestNativeCallExitReason.Exception;
						return false;
					}

					if (owner.State == GuestThreadRunState.Ready && owner.HasBlockedContinuation)
					{
						continuation = owner.BlockedContinuation;
						owner.BlockedContinuation = default;
						owner.HasBlockedContinuation = false;
						owner.BlockWakeKey = null;
						blockWaiter = owner.BlockWaiter;
						owner.BlockWaiter = null;
						owner.BlockDeadlineTimestamp = 0;
						owner.BlockReason = null;
						owner.State = GuestThreadRunState.Running;
						ready = true;
					}
				}

				if (ready)
				{
					break;
				}

				Thread.Sleep(1);
			}

			if (ActiveForcedGuestExit)
			{
				callbackReason = LastError ?? $"nested guest callback '{reason}' was forced to exit";
				exitReason = GuestNativeCallExitReason.ForcedExit;
				return false;
			}

			if (blockWaiter is not null)
			{
				continuation = continuation with { Rax = unchecked((ulong)(long)blockWaiter.Resume()) };
			}
			if (_logGuestThreads)
			{
				Console.Error.WriteLine(
					$"[LOADER][INFO] nested_callback.resume thread=0x{guestThreadHandle:X16} callback='{reason}' " +
					$"continuation=0x{continuation.Rip:X16}");
			}

			exitReason = ExecuteBlockedGuestThreadContinuation(
				callbackContext,
				continuation,
				reason,
				out callbackReason);
		}

		if (exitReason == GuestNativeCallExitReason.Blocked && ActiveForcedGuestExit)
		{
			callbackReason = LastError ?? $"nested guest callback '{reason}' was forced to exit";
			exitReason = GuestNativeCallExitReason.ForcedExit;
		}

		return exitReason == GuestNativeCallExitReason.Returned;
	}

	public bool TryCallGuestContinuation(
		CpuContext callerContext,
		GuestCpuContinuation continuation,
		string reason,
		out string? error)
	{
		error = null;
		if (_forcedGuestExit)
		{
			error = "guest execution is shutting down";
			return false;
		}
		if (continuation.Rip < 65536 || continuation.Rsp == 0)
		{
			error = $"invalid guest continuation rip=0x{continuation.Rip:X16} rsp=0x{continuation.Rsp:X16}";
			return false;
		}
		if (!TryGetVirtualMemory(callerContext, out var virtualMemory))
		{
			error = "caller context memory is not backed by IVirtualMemory";
			return false;
		}

		var trackedMemory = new TrackedCpuMemory(virtualMemory);
		var fallbackTlsBase = unchecked((ulong)_tlsBaseAddress);
		var context = new CpuContext(trackedMemory, callerContext.TargetGeneration)
		{
			Rip = continuation.Rip,
			Rflags = continuation.Rflags == 0 ? 0x202UL : continuation.Rflags,
			FsBase = callerContext.FsBase != 0 ? callerContext.FsBase : (continuation.FsBase != 0 ? continuation.FsBase : fallbackTlsBase),
			GsBase = callerContext.GsBase != 0 ? callerContext.GsBase : (continuation.GsBase != 0 ? continuation.GsBase : fallbackTlsBase),
			FpuControlWord = continuation.FpuControlWord == 0 ? (ushort)0x037F : continuation.FpuControlWord,
			Mxcsr = continuation.Mxcsr == 0 ? 0x1F80u : continuation.Mxcsr,
		};

		context[CpuRegister.Rax] = continuation.Rax;
		context[CpuRegister.Rcx] = continuation.Rcx;
		context[CpuRegister.Rdx] = continuation.Rdx;
		context[CpuRegister.Rbx] = continuation.Rbx;
		context[CpuRegister.Rbp] = continuation.Rbp;
		context[CpuRegister.Rsi] = continuation.Rsi;
		context[CpuRegister.Rdi] = continuation.Rdi;
		context[CpuRegister.R8] = continuation.R8;
		context[CpuRegister.R9] = continuation.R9;
		context[CpuRegister.R10] = continuation.R10;
		context[CpuRegister.R11] = continuation.R11;
		context[CpuRegister.R12] = continuation.R12;
		context[CpuRegister.R13] = continuation.R13;
		context[CpuRegister.R14] = continuation.R14;
		context[CpuRegister.R15] = continuation.R15;
		context[CpuRegister.Rsp] = continuation.Rsp;

		var exitReason = GuestNativeCallExitReason.Exception;
		string? callbackReason = null;
		string? callbackLastError = null;
		Exception? callbackException = null;
		var currentGuestThreadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
		var currentFiberAddress = GuestThreadExecution.CurrentFiberAddress;
		var currentGuestThreadState = _activeGuestThreadState;

		void RunContinuation()
		{
			var restoreGuestThread = currentGuestThreadHandle != 0 &&
				GuestThreadExecution.CurrentGuestThreadHandle != currentGuestThreadHandle;
			var previousGuestThreadHandle = restoreGuestThread
				? GuestThreadExecution.EnterGuestThread(currentGuestThreadHandle)
				: 0UL;
			var restoreFiber = currentFiberAddress != 0 &&
				GuestThreadExecution.CurrentFiberAddress != currentFiberAddress;
			var previousFiberAddress = restoreFiber
				? GuestThreadExecution.EnterFiber(currentFiberAddress)
				: 0UL;
			var previousGuestThreadState = _activeGuestThreadState;
			_activeGuestThreadState = currentGuestThreadState;
			var previousLastError = LastError;
			try
			{
				TraceGuestContext(
					$"continuation-enter reason={reason} managed={Environment.CurrentManagedThreadId} guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} fiber=0x{GuestThreadExecution.CurrentFiberAddress:X16} captured_guest=0x{currentGuestThreadHandle:X16} captured_fiber=0x{currentFiberAddress:X16} restore_guest={restoreGuestThread} restore_fiber={restoreFiber}");
				LastError = null;
				exitReason = ExecuteGuestContinuationEntry(
					context,
					continuation.Rip,
					continuation.ReturnSlotAddress,
					reason,
					out callbackReason);
				callbackLastError = LastError;
			}
			catch (Exception ex)
			{
				callbackException = ex;
				callbackReason = ex.GetType().Name + ": " + ex.Message;
				exitReason = GuestNativeCallExitReason.Exception;
			}
			finally
			{
				_activeGuestThreadState = previousGuestThreadState;
				TraceGuestContext(
					$"continuation-exit reason={reason} managed={Environment.CurrentManagedThreadId} guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} fiber=0x{GuestThreadExecution.CurrentFiberAddress:X16} exit={exitReason}");
				LastError = previousLastError;
				if (restoreFiber)
				{
					GuestThreadExecution.RestoreFiber(previousFiberAddress);
				}
				if (restoreGuestThread)
				{
					GuestThreadExecution.RestoreGuestThread(previousGuestThreadHandle);
				}
			}
		}

		if (currentGuestThreadHandle != 0)
		{
			GuestContinuationRunner? runner;
			using (LockGate("TryCallGuestContinuation"))
			{
				if (_guestThreads.TryGetValue(currentGuestThreadHandle, out var guestThread))
				{
					runner = guestThread.ContinuationRunner ??= new GuestContinuationRunner(
						currentGuestThreadHandle,
						MapGuestThreadPriority(guestThread.Priority));
				}
				else
				{
					runner = null;
				}
			}

			if (runner is not null && !runner.IsCurrentThread)
			{
				runner.Run(RunContinuation);
			}
			else if (runner is not null)
			{
				TraceGuestContext(
					$"continuation-inline reason={reason} managed={Environment.CurrentManagedThreadId} guest=0x{currentGuestThreadHandle:X16} fiber=0x{currentFiberAddress:X16}");
				RunContinuation();
			}
			else
			{
				RunContinuationOnTemporaryThread(currentGuestThreadHandle, RunContinuation);
			}
		}
		else
		{
			RunContinuation();
		}

		if (callbackException is not null)
		{
			error = callbackReason ?? callbackException.Message;
			return false;
		}

		if (exitReason is GuestNativeCallExitReason.Exception or GuestNativeCallExitReason.ForcedExit)
		{
			error = callbackReason ?? callbackLastError ?? "guest continuation failed";
			return false;
		}

		return true;
	}

	public bool TryRaiseGuestException(
		CpuContext callerContext,
		ulong threadHandle,
		ulong handler,
		int exceptionType,
		out string? error)
	{
		error = null;
		var logGuestExceptions = string.Equals(
			Environment.GetEnvironmentVariable("SHARPEMU_LOG_GUEST_EXCEPTIONS"),
			"1",
			StringComparison.Ordinal);
		if (threadHandle == 0 || handler < 65536 || exceptionType is < 0 or >= 128)
		{
			error = "invalid guest exception delivery request";
			return false;
		}

		GuestThreadState target;
		GuestThreadRunState savedState;
		bool savedExecutorActive;
		string? savedBlockReason;
		bool savedHasBlockedContinuation;
		GuestCpuContinuation savedBlockedContinuation;
		string? savedBlockWakeKey;
		IGuestThreadBlockWaiter? savedBlockWaiter;
		long savedBlockDeadlineTimestamp;
		ulong exceptionStackBase;
		lock (_guestThreadGate)
		{
			if (!_guestThreads.TryGetValue(threadHandle, out target!))
			{
				if (!_externalGuestThreads.TryGetValue(threadHandle, out var external))
				{
					error = $"unknown guest exception target 0x{threadHandle:X16}";
					return false;
				}

				if (external.ExceptionStackBase == 0)
				{
					string? mapError = null;
					if (!TryGetVirtualMemory(external.Context, out var virtualMemory) ||
						!TryMapGuestThreadRegion(
							virtualMemory,
							GuestThreadStackBaseAddress,
							GuestThreadStackSize,
							ProgramHeaderFlags.Read | ProgramHeaderFlags.Write,
							out var stackBase,
							out mapError))
					{
						error = mapError ?? "external guest context has no virtual memory";
						return false;
					}

					external.ExceptionStackBase = stackBase;
				}

				if (_pendingGuestExceptions.ContainsKey(threadHandle))
				{
					return true;
				}
				if (_activeGuestExceptionDeliveries.Contains(threadHandle))
				{
					// Preserve one signal raised while the previous handler is
					// unwinding. Unity can begin its next stop-the-world cycle in
					// that window; treating the new raise as part of the old delivery
					// strands the collector waiting for an acknowledgement.
					QueuePendingGuestExceptionLocked(threadHandle, new PendingGuestException(
						handler,
						exceptionType,
						external.ExceptionStackBase));
					return true;
				}

				// A primary/external executor is already running guest code on its
				// own host thread. Running its signal handler concurrently on a new
				// managed thread corrupts the worker's control state. Queue the
				// request and let that exact executor consume it at its next HLE
				// boundary, where the original guest thread is safely paused.
				QueuePendingGuestExceptionLocked(threadHandle, new PendingGuestException(
					handler,
					exceptionType,
					external.ExceptionStackBase));
				if (logGuestExceptions)
				{
					Console.Error.WriteLine(
						$"[LOADER][TRACE] guest_exception.queued " +
						$"target=0x{threadHandle:X16} type=0x{exceptionType:X2} mode=external");
				}
				return true;
			}

			if (target.State is GuestThreadRunState.Exited or GuestThreadRunState.Faulted)
			{
				error = $"guest exception target 0x{threadHandle:X16} is no longer running";
				return false;
			}

			if (target.ExceptionStackBase == 0)
			{
				string? mapError = null;
				if (!TryGetVirtualMemory(target.Context, out var virtualMemory) ||
					!TryMapGuestThreadRegion(
						virtualMemory,
						GuestThreadStackBaseAddress,
						GuestThreadStackSize,
						ProgramHeaderFlags.Read | ProgramHeaderFlags.Write,
						out var mappedExceptionStack,
						out mapError))
				{
					error = mapError ?? "guest thread context has no virtual memory";
					return false;
				}

				target.ExceptionStackBase = mappedExceptionStack;
			}
			exceptionStackBase = target.ExceptionStackBase;

			if (target.State != GuestThreadRunState.Blocked || target.ExecutorActive)
			{
				if (_pendingGuestExceptions.ContainsKey(threadHandle) ||
					_activeGuestExceptionDeliveries.Contains(threadHandle))
				{
					return true;
				}
				if (target.ExceptionDeliveryActive)
				{
					QueuePendingGuestExceptionLocked(threadHandle, new PendingGuestException(
						handler,
						exceptionType,
						exceptionStackBase));
					return true;
				}

				QueuePendingGuestExceptionLocked(threadHandle, new PendingGuestException(
					handler,
					exceptionType,
					exceptionStackBase));
				if (logGuestExceptions)
				{
					Console.Error.WriteLine(
						$"[LOADER][TRACE] guest_exception.queued " +
						$"target=0x{threadHandle:X16} type=0x{exceptionType:X2} " +
						$"mode=scheduled state={target.State} executor={target.ExecutorActive}");
				}
				return true;
			}

			// A parked cooperative thread has no active executor, so its saved
			// continuation can be handled immediately on a temporary executor.
			if (target.ExceptionDeliveryActive)
			{
				return true;
			}

			savedState = target.State;
			savedExecutorActive = target.ExecutorActive;
			savedBlockReason = target.BlockReason;
			savedHasBlockedContinuation = target.HasBlockedContinuation;
			savedBlockedContinuation = target.BlockedContinuation;
			savedBlockWakeKey = target.BlockWakeKey;
			savedBlockWaiter = target.BlockWaiter;
			savedBlockDeadlineTimestamp = target.BlockDeadlineTimestamp;

			target.State = GuestThreadRunState.Running;
			target.ExecutorActive = true;
			target.ExceptionDeliveryActive = true;
			target.BlockReason = null;
			target.HasBlockedContinuation = false;
			target.BlockedContinuation = default;
			target.BlockWakeKey = null;
			target.BlockWaiter = null;
			target.BlockDeadlineTimestamp = 0;
		}

		const ulong exceptionContextSize = 0x500;
		const ulong callbackStackOffset = 0x1000;
		const ulong callbackStackSize = 0xF000;
		var exceptionContextAddress = exceptionStackBase + 0x100;
		var guestExceptionCallback = 0UL;
		if (handler >= 0x210)
		{
			_ = target.Context.TryReadUInt64(handler - 0x210 + 0xC020, out guestExceptionCallback);
		}
		if (!TryWriteGuestExceptionContext(
				target.Context,
				exceptionContextAddress,
				savedHasBlockedContinuation ? savedBlockedContinuation : default,
				exceptionContextSize))
		{
			lock (_guestThreadGate)
			{
				RestoreInterruptedGuestThread();
			}
			error = "failed to write guest exception context";
			return false;
		}

		if (logGuestExceptions)
		{
			Console.Error.WriteLine(
				$"[LOADER][TRACE] guest_exception.delivery_enter " +
				$"target=0x{threadHandle:X16} type=0x{exceptionType:X2} mode=parked " +
				$"rip=0x{savedBlockedContinuation.Rip:X16} rsp=0x{savedBlockedContinuation.Rsp:X16} " +
				$"rbp=0x{savedBlockedContinuation.Rbp:X16} rbx=0x{savedBlockedContinuation.Rbx:X16} " +
				$"r12=0x{savedBlockedContinuation.R12:X16} r13=0x{savedBlockedContinuation.R13:X16} " +
				$"r14=0x{savedBlockedContinuation.R14:X16} r15=0x{savedBlockedContinuation.R15:X16} " +
				$"stack=0x{exceptionStackBase:X16} callback=0x{guestExceptionCallback:X16}");
		}

		void RestoreInterruptedGuestThread()
		{
			target.State = savedState;
			target.ExecutorActive = savedExecutorActive;
			target.ExceptionDeliveryActive = false;
			target.BlockReason = savedBlockReason;
			target.HasBlockedContinuation = savedHasBlockedContinuation;
			target.BlockedContinuation = savedBlockedContinuation;
			target.BlockWakeKey = savedBlockWakeKey;
			target.BlockWaiter = savedBlockWaiter;
			target.BlockDeadlineTimestamp = savedBlockDeadlineTimestamp;

			// A condition/event wake can arrive while the parked thread is
			// temporarily marked Running for signal delivery. WakeBlockedThreads
			// cannot claim it in that state, so re-check the restored wait before
			// releasing scheduler ownership. Without this handoff a completed
			// pthread wait remains parked forever after a GC suspension races it.
			if (target.State == GuestThreadRunState.Blocked &&
				target.HasBlockedContinuation &&
				target.BlockWaiter is not null &&
				target.BlockWaiter.TryWake())
			{
				target.State = GuestThreadRunState.Ready;
				target.BlockReason = null;
				target.BlockDeadlineTimestamp = 0;
				_readyGuestThreads.Enqueue(target);
				Interlocked.Increment(ref _readyGuestThreadCount);
			}
		}

		void DeliverException()
		{
			var previousGuestThreadHandle = GuestThreadExecution.EnterGuestThread(threadHandle);
			var previousGuestThreadState = _activeGuestThreadState;
			_activeGuestThreadState = target;
			var deliverySucceeded = false;
			string? deliveryError = null;
			var deliveryStarted = Stopwatch.GetTimestamp();
			try
			{
				deliverySucceeded = TryCallGuestFunction(
						target.Context,
						handler,
						unchecked((ulong)exceptionType),
						exceptionContextAddress,
						exceptionStackBase + callbackStackOffset,
						callbackStackSize,
						$"kernel exception 0x{exceptionType:X2}",
						out deliveryError);
				if (!deliverySucceeded)
				{
					Console.Error.WriteLine(
						$"[LOADER][ERROR] Guest exception delivery failed: " +
						$"target=0x{threadHandle:X16} type=0x{exceptionType:X2} " +
						$"error={deliveryError ?? "unknown"}");
				}
			}
			finally
			{
				PendingGuestException? followUp = null;
				if (logGuestExceptions)
				{
					var recordAddress = FindGuestExceptionThreadRecord(
						target.Context,
						guestExceptionCallback,
						threadHandle);
					var recordedStack = 0UL;
					var registeredStackBound = 0UL;
					if (recordAddress != 0)
					{
						_ = target.Context.TryReadUInt64(recordAddress + 0x100, out registeredStackBound);
						_ = target.Context.TryReadUInt64(recordAddress + 0x18, out recordedStack);
					}
					Console.Error.WriteLine(
						$"[LOADER][TRACE] guest_exception.delivery_exit " +
						$"target=0x{threadHandle:X16} type=0x{exceptionType:X2} " +
						$"success={deliverySucceeded} error={deliveryError ?? "none"} " +
						$"elapsed_ms={Stopwatch.GetElapsedTime(deliveryStarted).TotalMilliseconds:F3} " +
						$"record=0x{recordAddress:X16} stack_bound=0x{registeredStackBound:X16} " +
						$"recorded_rsp=0x{recordedStack:X16}");
				}
				_activeGuestThreadState = previousGuestThreadState;
				GuestThreadExecution.RestoreGuestThread(previousGuestThreadHandle);
				lock (_guestThreadGate)
				{
					RestoreInterruptedGuestThread();
					if (target.State == GuestThreadRunState.Blocked &&
						!target.ExecutorActive &&
						TryRemovePendingGuestExceptionLocked(threadHandle, out var queued))
					{
						followUp = queued;
					}
				}
				if (followUp is { } pendingFollowUp &&
					!TryRaiseGuestException(
						target.Context,
						threadHandle,
						pendingFollowUp.Handler,
						pendingFollowUp.ExceptionType,
						out var followUpError))
				{
					Console.Error.WriteLine(
						$"[LOADER][ERROR] Guest exception follow-up delivery failed: " +
						$"target=0x{threadHandle:X16} type=0x{pendingFollowUp.ExceptionType:X2} " +
						$"error={followUpError ?? "unknown"}");
				}
			}
		}

		// A real sceKernelRaiseException interrupts the target pthread and runs
		// its signal handler on that same native thread.  Parked cooperative
		// guest threads have no active call frame to interrupt, but their
		// persistent execution runner is idle and preserves the native-thread
		// identity/TLS that Unity's stop-the-world collector registered.  Using
		// an unrelated temporary host thread makes the suspension acknowledge
		// appear valid while publishing roots from the wrong native execution
		// context, which lets live IL2CPP delegates be reclaimed.
		GuestExecutionRunner deliveryRunner;
		lock (_guestThreadGate)
		{
			deliveryRunner = target.ExecutionRunner ??= new GuestExecutionRunner(
				target.ThreadHandle,
				target.Name,
				MapGuestThreadPriority(target.Priority));
		}
		deliveryRunner.Schedule(DeliverException);
		return true;
	}

	private static ulong FindGuestExceptionThreadRecord(
		CpuContext context,
		ulong callback,
		ulong threadHandle)
	{
		if (callback < 65536)
		{
			return 0;
		}

		// Unity's suspend callback uses a 256-bucket table at this fixed
		// image-relative offset from the callback entry. Each node stores the
		// pthread handle at +0x08 and the next pointer at +0x00.
		var tableAddress = callback + 0x102E8B0;
		for (var bucket = 0; bucket < 256; bucket++)
		{
			if (!context.TryReadUInt64(tableAddress + unchecked((ulong)bucket * 8), out var node))
			{
				return 0;
			}

			for (var depth = 0; node >= 65536 && depth < 1024; depth++)
			{
				if (!context.TryReadUInt64(node + 0x08, out var registeredThread))
				{
					break;
				}
				if (registeredThread == threadHandle)
				{
					return node;
				}
				if (!context.TryReadUInt64(node, out node))
				{
					break;
				}
			}
		}

		return 0;
	}

	private void DeliverPendingGuestExceptionAtSafePoint(
		CpuContext currentContext,
		GuestCpuContinuation interruptedContinuation)
	{
		if (Volatile.Read(ref _pendingGuestExceptionCount) == 0)
		{
			return;
		}

		var threadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
		if (threadHandle == 0)
		{
			threadHandle = _currentExternalGuestThreadHandle;
		}
		PendingGuestException pending;
		lock (_guestThreadGate)
		{
			if (threadHandle == 0)
			{
				return;
			}

			if (!TryRemovePendingGuestExceptionLocked(threadHandle, out pending))
			{
				return;
			}

			_activeGuestExceptionDeliveries.Add(threadHandle);
		}

		const ulong exceptionContextSize = 0x500;
		const ulong callbackStackOffset = 0x1000;
		const ulong callbackStackSize = 0xF000;
		var exceptionContextAddress = pending.ExceptionStackBase + 0x100;
		try
		{
			if (!TryWriteGuestExceptionContext(
					currentContext,
					exceptionContextAddress,
					interruptedContinuation,
					exceptionContextSize))
			{
				Console.Error.WriteLine(
					$"[LOADER][ERROR] Guest exception safe-point context write failed: " +
					$"target=0x{threadHandle:X16} type=0x{pending.ExceptionType:X2}");
				return;
			}

			if (string.Equals(
					Environment.GetEnvironmentVariable("SHARPEMU_LOG_GUEST_EXCEPTIONS"),
					"1",
					StringComparison.Ordinal))
			{
				Console.Error.WriteLine(
					$"[LOADER][TRACE] guest_exception.safe_point_enter " +
					$"target=0x{threadHandle:X16} type=0x{pending.ExceptionType:X2} " +
					$"rip=0x{interruptedContinuation.Rip:X16}");
			}

			if (!TryCallGuestFunction(
					currentContext,
					pending.Handler,
					unchecked((ulong)pending.ExceptionType),
					exceptionContextAddress,
					pending.ExceptionStackBase + callbackStackOffset,
					callbackStackSize,
					$"kernel exception 0x{pending.ExceptionType:X2} safe point",
					out var callbackError))
			{
				Console.Error.WriteLine(
					$"[LOADER][ERROR] Guest exception safe-point delivery failed: " +
					$"target=0x{threadHandle:X16} type=0x{pending.ExceptionType:X2} " +
					$"error={callbackError ?? "unknown"}");
			}
		}
		finally
		{
			lock (_guestThreadGate)
			{
				_activeGuestExceptionDeliveries.Remove(threadHandle);
			}
		}
	}

	private void QueuePendingGuestExceptionLocked(
		ulong threadHandle,
		PendingGuestException pending)
	{
		_pendingGuestExceptions[threadHandle] = pending;
		Volatile.Write(ref _pendingGuestExceptionCount, _pendingGuestExceptions.Count);
	}

	private bool TryRemovePendingGuestExceptionLocked(
		ulong threadHandle,
		out PendingGuestException pending)
	{
		if (!_pendingGuestExceptions.Remove(threadHandle, out pending))
		{
			return false;
		}

		Volatile.Write(ref _pendingGuestExceptionCount, _pendingGuestExceptions.Count);
		return true;
	}

	private static bool TryWriteGuestExceptionContext(
		CpuContext context,
		ulong address,
		GuestCpuContinuation continuation,
		ulong size)
	{
		var bytes = new byte[checked((int)size)];
		void Write64(int offset, ulong value) =>
			BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, sizeof(ulong)), value);

		var hasContinuation = continuation.Rip >= 65536 && continuation.Rsp != 0;
		// Orbis ucontext_t has a 0x10-byte signal mask and 0x30 bytes of
		// private fields before its amd64 mcontext. These offsets match the
		// platform ABI used by libScePs5Util and Unity's Boehm GC. Supplying a
		// bare mcontext here makes the collector miss live register roots.
		const int mcontext = 0x40;
		Write64(mcontext + 0x08, hasContinuation ? continuation.Rdi : context[CpuRegister.Rdi]);
		Write64(mcontext + 0x10, hasContinuation ? continuation.Rsi : context[CpuRegister.Rsi]);
		Write64(mcontext + 0x18, hasContinuation ? continuation.Rdx : context[CpuRegister.Rdx]);
		Write64(mcontext + 0x20, hasContinuation ? continuation.Rcx : context[CpuRegister.Rcx]);
		Write64(mcontext + 0x28, hasContinuation ? continuation.R8 : context[CpuRegister.R8]);
		Write64(mcontext + 0x30, hasContinuation ? continuation.R9 : context[CpuRegister.R9]);
		Write64(mcontext + 0x38, hasContinuation ? continuation.Rax : context[CpuRegister.Rax]);
		Write64(mcontext + 0x40, hasContinuation ? continuation.Rbx : context[CpuRegister.Rbx]);
		Write64(mcontext + 0x48, hasContinuation ? continuation.Rbp : context[CpuRegister.Rbp]);
		Write64(mcontext + 0x50, hasContinuation ? continuation.R10 : context[CpuRegister.R10]);
		Write64(mcontext + 0x58, hasContinuation ? continuation.R11 : context[CpuRegister.R11]);
		Write64(mcontext + 0x60, hasContinuation ? continuation.R12 : context[CpuRegister.R12]);
		Write64(mcontext + 0x68, hasContinuation ? continuation.R13 : context[CpuRegister.R13]);
		Write64(mcontext + 0x70, hasContinuation ? continuation.R14 : context[CpuRegister.R14]);
		Write64(mcontext + 0x78, hasContinuation ? continuation.R15 : context[CpuRegister.R15]);
		var rip = hasContinuation ? continuation.Rip : context.Rip;
		var rsp = hasContinuation ? continuation.Rsp : context[CpuRegister.Rsp];
		Write64(mcontext + 0xA0, rip);
		Write64(mcontext + 0xB0, hasContinuation ? continuation.Rflags : 0);
		Write64(mcontext + 0xB8, rsp);
		Write64(mcontext + 0xC8, 0x480); // sizeof(Orbis mcontext_t)
		Write64(mcontext + 0x440, hasContinuation ? continuation.FsBase : context.FsBase);
		Write64(mcontext + 0x448, hasContinuation ? continuation.GsBase : context.GsBase);
		return context.Memory.TryWrite(address, bytes);
	}

	private void TraceGuestContext(string message)
	{
		if (_logGuestContext)
		{
			Console.Error.WriteLine($"[LOADER][TRACE] guest_context.{message}");
		}
	}

	private static void RunContinuationOnTemporaryThread(ulong guestThreadHandle, Action continuation)
	{
		var continuationThread = new Thread(() =>
		{
			var previousGuestThreadHandle = GuestThreadExecution.EnterGuestThread(guestThreadHandle);
			try
			{
				continuation();
			}
			finally
			{
				GuestThreadExecution.RestoreGuestThread(previousGuestThreadHandle);
			}
		})
		{
			IsBackground = true,
			Name = $"GuestContinuationNested-{guestThreadHandle:X}",
			Priority = ThreadPriority.BelowNormal,
		};
		continuationThread.Start();
		continuationThread.Join();
	}

	private void ClearGuestThreads()
	{
		GuestContinuationRunner[] continuationRunners;
		GuestExecutionRunner[] executionRunners;
		lock (_guestThreadGate)
		{
			continuationRunners = _guestThreads.Values
				.Select(static thread => thread.ContinuationRunner)
				.Where(static runner => runner is not null)
				.Cast<GuestContinuationRunner>()
				.ToArray();
			executionRunners = _guestThreads.Values
				.Select(static thread => thread.ExecutionRunner)
				.Where(static runner => runner is not null)
				.Cast<GuestExecutionRunner>()
				.ToArray();
			_readyGuestThreads.Clear();
			Interlocked.Exchange(ref _readyGuestThreadCount, 0);
			_guestThreads.Clear();
			_externalGuestThreads.Clear();
			_pendingGuestExceptions.Clear();
			Volatile.Write(ref _pendingGuestExceptionCount, 0);
			_activeGuestExceptionDeliveries.Clear();
		}

		foreach (var runner in continuationRunners)
		{
			runner.Dispose();
		}
		foreach (var runner in executionRunners)
		{
			runner.Dispose();
		}
	}

	private bool TryCreateGuestThreadState(CpuContext creatorContext, GuestThreadStartRequest request, out GuestThreadState thread, out string? error)
	{
		thread = null!;
		if (!TryGetVirtualMemory(creatorContext, out var virtualMemory))
		{
			error = "creator context memory is not backed by IVirtualMemory";
			return false;
		}
		if (!TryMapGuestThreadRegion(virtualMemory, GuestThreadStackBaseAddress, GuestThreadStackSize, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write, out var stackBase, out error))
		{
			return false;
		}
		if (!TryMapGuestThreadTlsRegion(virtualMemory, out var tlsBase, out error))
		{
			return false;
		}

		var trackedMemory = new TrackedCpuMemory(virtualMemory);
		var context = new CpuContext(trackedMemory, creatorContext.TargetGeneration)
		{
			Rip = request.EntryPoint,
			Rflags = 0x202,
			FsBase = tlsBase,
			GsBase = tlsBase,
		};
		context[CpuRegister.Rsp] = stackBase + GuestThreadStackSize - sizeof(ulong);
		context[CpuRegister.Rdi] = request.Argument;
		context[CpuRegister.Rsi] = 0;
		context[CpuRegister.Rdx] = 0;
		context[CpuRegister.Rcx] = 0;
		context[CpuRegister.R8] = 0;
		context[CpuRegister.R9] = 0;
		if (!InitializeGuestThreadFrame(context) || !InitializeGuestThreadTls(context, tlsBase, request.ThreadHandle))
		{
			error = "failed to initialize guest thread stack/TLS";
			return false;
		}

		thread = new GuestThreadState
		{
			ThreadHandle = request.ThreadHandle,
			EntryPoint = request.EntryPoint,
			Argument = request.Argument,
			Name = string.IsNullOrWhiteSpace(request.Name) ? $"Thread-{request.ThreadHandle:X}" : request.Name,
			Priority = request.Priority,
			AffinityMask = request.AffinityMask,
			Context = context,
			StackBase = stackBase,
			StackSize = GuestThreadStackSize,
			State = GuestThreadRunState.Ready,
		};
		error = null;
		return true;
	}

	private static bool TryGetVirtualMemory(CpuContext context, out IVirtualMemory virtualMemory)
	{
		if (context.Memory is IVirtualMemory directMemory)
		{
			virtualMemory = directMemory;
			return true;
		}
		if (context.Memory is TrackedCpuMemory trackedMemory && trackedMemory.Inner is IVirtualMemory trackedInner)
		{
			virtualMemory = trackedInner;
			return true;
		}

		virtualMemory = null!;
		return false;
	}

	private static bool TryMapGuestThreadRegion(
		IVirtualMemory virtualMemory,
		ulong baseAddress,
		ulong size,
		ProgramHeaderFlags protection,
		out ulong mappedBase,
		out string? error)
	{
		for (int i = 0; i < GuestThreadRegionSlots; i++)
		{
			var candidateBase = baseAddress - ((ulong)i * GuestThreadRegionStride);
			if (!IsGuestThreadRegionFree(virtualMemory, candidateBase, size))
			{
				continue;
			}
			try
			{
				virtualMemory.Map(
					candidateBase,
					size,
					fileOffset: 0,
					fileData: ReadOnlySpan<byte>.Empty,
					protection: protection);
				mappedBase = candidateBase;
				error = null;
				return true;
			}
			catch (InvalidOperationException)
			{
			}
		}

		mappedBase = 0;
		error = $"failed to map guest thread region near 0x{baseAddress:X16}";
		return false;
	}

	private static bool TryMapGuestThreadTlsRegion(
		IVirtualMemory virtualMemory,
		out ulong tlsBase,
		out string? error)
	{
		for (int i = 0; i < GuestThreadRegionSlots; i++)
		{
			var candidateBase = GuestThreadTlsBaseAddress - ((ulong)i * GuestThreadRegionStride);
			var mappedBase = candidateBase - GuestThreadTlsPrefixSize;
			var mappedSize = GuestThreadTlsSize + GuestThreadTlsPrefixSize;
			if (!IsGuestThreadRegionFree(virtualMemory, mappedBase, mappedSize))
			{
				continue;
			}
			try
			{
				virtualMemory.Map(
					mappedBase,
					mappedSize,
					fileOffset: 0,
					fileData: ReadOnlySpan<byte>.Empty,
					protection: ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
				tlsBase = candidateBase;
				error = null;
				return true;
			}
			catch (InvalidOperationException)
			{
			}
		}

		tlsBase = 0;
		error = $"failed to map guest TLS region near 0x{GuestThreadTlsBaseAddress:X16}";
		return false;
	}

	private static bool IsGuestThreadRegionFree(IVirtualMemory virtualMemory, ulong candidateBase, ulong size)
	{
		var candidateEnd = candidateBase + size;
		foreach (var region in virtualMemory.SnapshotRegions())
		{
			var regionStart = region.VirtualAddress;
			var regionEnd = regionStart + region.MemorySize;
			if (candidateBase < regionEnd && regionStart < candidateEnd)
			{
				return false;
			}
		}

		return true;
	}

	private static bool InitializeGuestThreadFrame(CpuContext context)
	{
		var stackTop = context[CpuRegister.Rsp] + sizeof(ulong);
		var sentinelFrame = AlignDown(stackTop - 0x20, 16);
		var seedRsp = sentinelFrame - sizeof(ulong);
		if (!context.TryWriteUInt64(sentinelFrame, 0) ||
			!context.TryWriteUInt64(sentinelFrame + sizeof(ulong), 0) ||
			!context.TryWriteUInt64(seedRsp, 0))
		{
			return false;
		}

		context[CpuRegister.Rbp] = sentinelFrame;
		context[CpuRegister.Rsp] = seedRsp;
		return true;
	}

	private static bool InitializeGuestThreadTls(CpuContext context, ulong tlsBase, ulong threadHandle)
	{
		if (!context.TryWriteUInt64(tlsBase - 0xF0, 0) ||
			!context.TryWriteUInt64(tlsBase + 0x00, tlsBase) ||
			!context.TryWriteUInt64(tlsBase + 0x10, threadHandle) ||
			!context.TryWriteUInt64(tlsBase + 0x28, 0xC0DEC0DECAFEBA00UL) ||
			!context.TryWriteUInt64(tlsBase + 0x60, tlsBase))
		{
			return false;
		}

		// Seed initialized thread-locals into the static TLS block below the
		// thread pointer so per-thread TLS matches the main thread.
		SharpEmu.HLE.GuestTlsTemplate.SeedThreadBlock(context, tlsBase);
		return true;
	}

	private static ThreadPriority MapGuestThreadPriority(int priority)
	{
		if (priority <= 478)
		{
			return ThreadPriority.Highest;
		}
		if (priority >= 733)
		{
			return ThreadPriority.Lowest;
		}

		return ThreadPriority.Normal;
	}

	private void ApplyGuestThreadAffinity(ulong guestAffinityMask)
	{
		var hostAffinityMask = MapGuestThreadAffinity(guestAffinityMask);
		if (hostAffinityMask == 0)
		{
			return;
		}

		if (SetThreadAffinityMask(GetCurrentThread(), (nuint)hostAffinityMask) == 0 && _logGuestThreads)
		{
			Console.Error.WriteLine(
				$"[LOADER][WARN] Failed to set guest thread affinity guest=0x{guestAffinityMask:X} " +
				$"host=0x{hostAffinityMask:X} error={Marshal.GetLastWin32Error()}");
		}
	}

	private static ulong MapGuestThreadAffinity(ulong guestAffinityMask)
	{
		if (guestAffinityMask == 0 || guestAffinityMask == ulong.MaxValue)
		{
			return 0;
		}

		var processorCount = Math.Min(Environment.ProcessorCount, 64);
		if (processorCount == 0)
		{
			return 0;
		}

		ulong hostAffinityMask = 0;
		for (var guestCpu = 0; guestCpu < 64; guestCpu++)
		{
			if ((guestAffinityMask & (1UL << guestCpu)) == 0)
			{
				continue;
			}

			var hostCpu = processorCount < 8
				? guestCpu % processorCount
				: processorCount >= 16
					? guestCpu * 2
					: guestCpu;
			if (hostCpu < processorCount)
			{
				hostAffinityMask |= 1UL << hostCpu;
			}
		}

		return hostAffinityMask;
	}

	public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority)
	{
		lock (_guestThreadGate)
		{
			if (!_guestThreads.TryGetValue(guestThreadHandle, out var thread))
			{
				return false;
			}

			thread.Priority = guestPriority;
			var host = thread.HostThread;
			if (host is not null && host.IsAlive)
			{
				try
				{
					host.Priority = MapGuestThreadPriority(guestPriority);
				}
				catch (Exception exception) when (exception is ThreadStateException or InvalidOperationException)
				{
					// The thread may have exited between the alive check and
					// the assignment; the stored priority still takes effect
					// if it is ever restarted.
				}
			}

			return true;
		}
	}

	public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask)
	{
		lock (_guestThreadGate)
		{
			if (!_guestThreads.TryGetValue(guestThreadHandle, out var thread))
			{
				return false;
			}

			thread.AffinityMask = affinityMask;
			// A running thread applies its own affinity via
			// ApplyGuestThreadAffinity; cross-thread affinity is not portable,
			// so the new mask takes effect on the thread's next scheduling.
			return true;
		}
	}

	private void RunGuestThread(GuestThreadState thread, string reason)
	{
		if (_forcedGuestExit)
		{
			// Host shutdown: never enter guest code again. Teardown is about to
			// free trampolines and the guest address space, and it only waits
			// for executors that are already inside a slice.
			lock (_guestThreadGate)
			{
				thread.State = GuestThreadRunState.Faulted;
				thread.BlockReason = "host shutdown";
				thread.HostThread = null;
				thread.ExecutorActive = false;
			}
			return;
		}

		lock (_guestThreadGate)
		{
			if (!thread.ExecutorActive)
			{
				throw new InvalidOperationException(
					$"Guest thread '{thread.Name}' started without scheduler executor ownership.");
			}

			thread.HostThread = Thread.CurrentThread;
		}
		var previousLastError = LastError;
		var previousGuestThreadHandle = GuestThreadExecution.EnterGuestThread(thread.ThreadHandle);
		var previousGuestThreadState = _activeGuestThreadState;
		ApplyGuestThreadAffinity(thread.AffinityMask);
		Volatile.Write(ref thread.HostThreadId, unchecked((int)GetCurrentThreadId()));
		_activeGuestThreadState = thread;
		try
		{
			LastError = null;
			GuestCpuContinuation continuation = default;
			IGuestThreadBlockWaiter? blockWaiter = null;
			var resumeContinuation = false;
			using (LockGate("RunGuestThread.block"))
			{
				if (thread.HasBlockedContinuation)
				{
					continuation = thread.BlockedContinuation;
					thread.BlockedContinuation = default;
					thread.HasBlockedContinuation = false;
					thread.BlockWakeKey = null;
					blockWaiter = thread.BlockWaiter;
					thread.BlockWaiter = null;
					thread.BlockDeadlineTimestamp = 0;
					resumeContinuation = true;
				}
			}

			if (blockWaiter is not null)
			{
				continuation = continuation with { Rax = unchecked((ulong)(long)blockWaiter.Resume()) };
			}

			if (_logGuestThreads)
			{
				Console.Error.WriteLine(
					resumeContinuation
						? $"[LOADER][INFO] Pumping guest thread '{thread.Name}' reason={reason} resume=0x{continuation.Rip:X16}"
						: $"[LOADER][INFO] Pumping guest thread '{thread.Name}' reason={reason} entry=0x{thread.EntryPoint:X16}");
			}
			var exitReason = resumeContinuation
				? ExecuteBlockedGuestThreadContinuation(thread.Context, continuation, thread.Name, out var blockReason)
				: ExecuteGuestThreadEntry(thread.Context, thread.EntryPoint, thread.Name, out blockReason);
			using (LockGate("RunGuestThread.exit"))
			{
				switch (exitReason)
				{
					case GuestNativeCallExitReason.Returned:
						thread.ExitValue = thread.Context[CpuRegister.Rax];
						thread.State = GuestThreadRunState.Exited;
						if (_logGuestThreads)
						Console.Error.WriteLine(
							$"[LOADER][INFO] Guest thread exited: name='{thread.Name}' " +
							$"exitValue=0x{thread.ExitValue:X16} imports={Interlocked.Read(ref thread.ImportCount)} " +
							$"lastNid={Volatile.Read(ref thread.LastImportNid) ?? "none"} " +
							$"entry=0x{thread.EntryPoint:X16} ret=0x{Volatile.Read(ref thread.LastReturnRip):X16}");
						break;
					case GuestNativeCallExitReason.Blocked:
						thread.State = GuestThreadRunState.Blocked;
						thread.BlockReason = blockReason;
						if (thread.HasBlockedContinuation &&
							thread.BlockWaiter is not null &&
							thread.BlockWaiter.TryWake())
						{
							thread.State = GuestThreadRunState.Ready;
							thread.BlockReason = null;
							thread.BlockDeadlineTimestamp = 0;
							_readyGuestThreads.Enqueue(thread);
							Interlocked.Increment(ref _readyGuestThreadCount);
						}
						break;
					default:
						thread.State = GuestThreadRunState.Faulted;
						thread.BlockReason = blockReason;
						break;
				}
			}
			if (_logGuestThreads)
			{
				Console.Error.WriteLine(
					$"[LOADER][INFO] Guest thread '{thread.Name}' state={thread.State} reason={blockReason ?? "none"}");
			}
		}
		finally
		{
			_activeGuestThreadState = previousGuestThreadState;
			Volatile.Write(ref thread.HostThreadId, 0);
			GuestThreadExecution.RestoreGuestThread(previousGuestThreadHandle);
			LastError = previousLastError;
			lock (_guestThreadGate)
			{
				if (ReferenceEquals(thread.HostThread, Thread.CurrentThread))
				{
					thread.HostThread = null;
				}
				thread.ExecutorActive = false;
			}
		}
	}

	private GuestNativeCallExitReason ExecuteBlockedGuestThreadContinuation(
		CpuContext context,
		GuestCpuContinuation continuation,
		string name,
		out string? reason)
	{
		TraceFocusedContinuation(
			"execute",
			GuestThreadExecution.CurrentGuestThreadHandle,
			continuation,
			name);
		ApplyGuestContinuation(context, continuation);
		return ExecuteGuestContinuationEntry(
			context,
			continuation.Rip,
			continuation.ReturnSlotAddress,
			name,
			out reason);
	}

	private static void TraceFocusedContinuation(
		string operation,
		ulong threadHandle,
		GuestCpuContinuation continuation,
		string detail)
	{
		if (!string.Equals(
				Environment.GetEnvironmentVariable("SHARPEMU_TRACE_FOCUSED_CONTINUATION"),
				"1",
				StringComparison.Ordinal) ||
			continuation.Rsp < 0x00006FFFAC000000UL ||
			continuation.Rsp >= 0x00006FFFAC200000UL)
		{
			return;
		}

		Console.Error.WriteLine(
			$"[LOADER][TRACE] focused_continuation.{operation} " +
			$"thread=0x{threadHandle:X16} rip=0x{continuation.Rip:X16} " +
			$"rsp=0x{continuation.Rsp:X16} slot=0x{continuation.ReturnSlotAddress:X16} " +
			$"rbp=0x{continuation.Rbp:X16} rbx=0x{continuation.Rbx:X16} " +
			$"detail={detail}");
	}

	private static void ApplyGuestContinuation(CpuContext context, GuestCpuContinuation continuation)
	{
		context.Rip = continuation.Rip;
		context.Rflags = continuation.Rflags == 0 ? 0x202UL : continuation.Rflags;
		if (continuation.FsBase != 0)
		{
			context.FsBase = continuation.FsBase;
		}
		if (continuation.GsBase != 0)
		{
			context.GsBase = continuation.GsBase;
		}

		context[CpuRegister.Rax] = continuation.Rax;
		context[CpuRegister.Rcx] = continuation.Rcx;
		context[CpuRegister.Rdx] = continuation.Rdx;
		context[CpuRegister.Rbx] = continuation.Rbx;
		context[CpuRegister.Rbp] = continuation.Rbp;
		context[CpuRegister.Rsi] = continuation.Rsi;
		context[CpuRegister.Rdi] = continuation.Rdi;
		context[CpuRegister.R8] = continuation.R8;
		context[CpuRegister.R9] = continuation.R9;
		context[CpuRegister.R10] = continuation.R10;
		context[CpuRegister.R11] = continuation.R11;
		context[CpuRegister.R12] = continuation.R12;
		context[CpuRegister.R13] = continuation.R13;
		context[CpuRegister.R14] = continuation.R14;
		context[CpuRegister.R15] = continuation.R15;
		context[CpuRegister.Rsp] = continuation.Rsp;
		context.FpuControlWord = continuation.FpuControlWord == 0
			? (ushort)0x037F
			: continuation.FpuControlWord;
		context.Mxcsr = continuation.Mxcsr == 0 ? 0x1F80u : continuation.Mxcsr;
	}

	private unsafe GuestNativeCallExitReason ExecuteGuestThreadEntry(CpuContext context, ulong entryPoint, string name, out string? reason)
	{
		reason = null;
		if (context[CpuRegister.Rsp] == 0)
		{
			reason = "guest thread stack pointer is zero";
			return GuestNativeCallExitReason.Exception;
		}
		const uint stubSize = 512u;
		void* ptr = VirtualAlloc(null, stubSize, 12288u, 4u);
		if (ptr == null)
		{
			reason = "failed to allocate executable memory for guest thread stub";
			return GuestNativeCallExitReason.Exception;
		}
		void* hostRspStorage = NativeMemory.Alloc((nuint)sizeof(ulong));
		if (hostRspStorage == null)
		{
			VirtualFree(ptr, 0u, 32768u);
			reason = "failed to allocate writable host-RSP storage for guest thread stub";
			return GuestNativeCallExitReason.Exception;
		}
		var previousActiveBackend = _activeExecutionBackend;
		var previousActiveContext = _activeCpuContext;
		var previousSentinel = _activeEntryReturnSentinelRip;
		var previousReturnSlotAddress = _activeGuestReturnSlotAddress;
		var previousForcedExit = _activeForcedGuestExit;
		var previousYieldRequested = _activeGuestThreadYieldRequested;
		var previousYieldReason = _activeGuestThreadYieldReason;
		nint previousHostRspSlotValue = TlsGetValue(_hostRspSlotTlsIndex);
		try
		{
			_activeExecutionBackend = this;
			_activeCpuContext = context;
			_activeEntryReturnSentinelRip = 0;
			_activeGuestReturnSlotAddress = 0;
			_activeForcedGuestExit = false;
			_activeGuestThreadYieldRequested = false;
			_activeGuestThreadYieldReason = null;
			BindTlsBase(context);
			byte* ptr2 = (byte*)ptr;
			// Rosetta does not reliably permit a generated x86 thunk to write data
			// in the same page from which it is currently executing, even when the
			// mapping reports PAGE_EXECUTE_READWRITE. Keep mutable transition state
			// in a separate writable allocation.
			ulong hostRspSlot = (ulong)hostRspStorage;
			int offset = 0;
			ptr2[offset++] = 83;
			ptr2[offset++] = 85;
			ptr2[offset++] = 87;
			ptr2[offset++] = 86;
			ptr2[offset++] = 65;
			ptr2[offset++] = 84;
			ptr2[offset++] = 65;
			ptr2[offset++] = 85;
			ptr2[offset++] = 65;
			ptr2[offset++] = 86;
			ptr2[offset++] = 65;
			ptr2[offset++] = 87;
			EmitHostNonvolatileXmmSave(ptr2, ref offset);
			ptr2[offset++] = 73;
			ptr2[offset++] = 186;
			*(ulong*)(ptr2 + offset) = hostRspSlot;
			offset += 8;
			ptr2[offset++] = 73;
			ptr2[offset++] = 137;
			ptr2[offset++] = 34;
			ptr2[offset++] = 72;
			ptr2[offset++] = 184;
			*(ulong*)(ptr2 + offset) = context[CpuRegister.Rsp];
			offset += 8;
			ptr2[offset++] = 72;
			ptr2[offset++] = 137;
			ptr2[offset++] = 196;
			ptr2[offset++] = 72;
			ptr2[offset++] = 131;
			ptr2[offset++] = 236;
			ptr2[offset++] = 8;
			ptr2[offset++] = 72;
			ptr2[offset++] = 189;
			*(ulong*)(ptr2 + offset) = context[CpuRegister.Rbp];
			offset += 8;
			ptr2[offset++] = 72;
			ptr2[offset++] = 184;
			*(ulong*)(ptr2 + offset) = context[CpuRegister.Rdi];
			offset += 8;
			ptr2[offset++] = 72;
			ptr2[offset++] = 137;
			ptr2[offset++] = 199;
			ptr2[offset++] = 72;
			ptr2[offset++] = 184;
			*(ulong*)(ptr2 + offset) = context[CpuRegister.Rsi];
			offset += 8;
			ptr2[offset++] = 72;
			ptr2[offset++] = 137;
			ptr2[offset++] = 198;
			ptr2[offset++] = 72;
			ptr2[offset++] = 184;
			*(ulong*)(ptr2 + offset) = context[CpuRegister.Rdx];
			offset += 8;
			ptr2[offset++] = 72;
			ptr2[offset++] = 137;
			ptr2[offset++] = 194;
			ptr2[offset++] = 72;
			ptr2[offset++] = 184;
			*(ulong*)(ptr2 + offset) = context[CpuRegister.Rcx];
			offset += 8;
			ptr2[offset++] = 72;
			ptr2[offset++] = 137;
			ptr2[offset++] = 193;
			ptr2[offset++] = 72;
			ptr2[offset++] = 184;
			*(ulong*)(ptr2 + offset) = entryPoint;
			offset += 8;
			ptr2[offset++] = byte.MaxValue;
			ptr2[offset++] = 208;
			int sentinelOffset = offset + 4;
			ptr2[offset++] = 72;
			ptr2[offset++] = 131;
			ptr2[offset++] = 196;
			ptr2[offset++] = 8;
			ptr2[offset++] = 73;
			ptr2[offset++] = 186;
			*(ulong*)(ptr2 + offset) = hostRspSlot;
			offset += 8;
			ptr2[offset++] = 73;
			ptr2[offset++] = 139;
			ptr2[offset++] = 34;
			EmitHostNonvolatileXmmRestore(ptr2, ref offset);
			ptr2[offset++] = 65;
			ptr2[offset++] = 95;
			ptr2[offset++] = 65;
			ptr2[offset++] = 94;
			ptr2[offset++] = 65;
			ptr2[offset++] = 93;
			ptr2[offset++] = 65;
			ptr2[offset++] = 92;
			ptr2[offset++] = 94;
			ptr2[offset++] = 95;
			ptr2[offset++] = 93;
			ptr2[offset++] = 91;
			ptr2[offset++] = 195;
			ulong sentinel = (ulong)ptr + (ulong)sentinelOffset;
			ActiveEntryReturnSentinelRip = (ulong)_guestReturnStub;
			_activeGuestReturnSlotAddress = context[CpuRegister.Rsp] - 16uL;
			if (!context.TryWriteUInt64(context[CpuRegister.Rsp], sentinel))
			{
				reason = $"failed to patch guest thread return sentinel at 0x{context[CpuRegister.Rsp]:X16}";
				return GuestNativeCallExitReason.Exception;
			}
			uint oldProtect = default(uint);
			if (!VirtualProtect(ptr, stubSize, 32u, &oldProtect))
			{
				reason = "failed to seal guest thread stub execute-read";
				return GuestNativeCallExitReason.Exception;
			}
			FlushInstructionCache(GetCurrentProcess(), ptr, stubSize);
			ActiveGuestThreadYieldRequested = false;
			ActiveGuestThreadYieldReason = null;
			try
			{
				// TBB execute-AV recover needs native-worker TLS (eligible/done).
				// Other guests stay on CallNativeEntry — full native-worker migration
				// increased splash hangs / UnmanagedCallersOnly (tLTN/tLTO).
				int nativeReturn;
				if (name == "tbb_thead")
				{
					nativeReturn = RunGuestEntryStub(ptr, hostRspSlot, requireNativeWorker: true);
				}
				else
				{
					if (!TlsSetValue(_hostRspSlotTlsIndex, (nint)hostRspSlot))
					{
						reason = "failed to bind host-RSP storage for guest thread stub";
						return GuestNativeCallExitReason.Exception;
					}
					nativeReturn = CallNativeEntry(ptr);
				}
				if (ActiveGuestThreadYieldRequested)
				{
					reason = ActiveGuestThreadYieldReason ?? "guest thread blocked";
					return GuestNativeCallExitReason.Blocked;
				}
				if (ActiveForcedGuestExit)
				{
					reason = LastError ?? "guest thread forced exit";
					return GuestNativeCallExitReason.ForcedExit;
				}
				reason = $"returned 0x{nativeReturn:X8}";
				return GuestNativeCallExitReason.Returned;
			}
			catch (AccessViolationException ex)
			{
				reason = "access violation: " + ex.Message;
				return GuestNativeCallExitReason.Exception;
			}
			catch (Exception ex)
			{
				reason = ex.GetType().Name + ": " + ex.Message;
				return GuestNativeCallExitReason.Exception;
			}
		}
		finally
		{
			TlsSetValue(_hostRspSlotTlsIndex, previousHostRspSlotValue);
			RestoreActiveExecutionThread(
				previousActiveBackend,
				previousActiveContext,
				previousSentinel,
				previousReturnSlotAddress,
				previousForcedExit,
				previousYieldRequested,
				previousYieldReason);
			NativeMemory.Free(hostRspStorage);
			VirtualFree(ptr, 0u, 32768u);
		}
	}

	private unsafe GuestNativeCallExitReason ExecuteGuestContinuationEntry(
		CpuContext context,
		ulong entryPoint,
		ulong returnSlotAddress,
		string name,
		out string? reason)
	{
		reason = null;
		if (context[CpuRegister.Rsp] == 0)
		{
			reason = "guest thread stack pointer is zero";
			return GuestNativeCallExitReason.Exception;
		}
		const uint stubSize = 512u;
		void* ptr = VirtualAlloc(null, stubSize, 12288u, 4u);
		if (ptr == null)
		{
			reason = "failed to allocate executable memory for guest thread stub";
			return GuestNativeCallExitReason.Exception;
		}
		void* hostRspStorage = NativeMemory.Alloc((nuint)sizeof(ulong));
		if (hostRspStorage == null)
		{
			VirtualFree(ptr, 0u, 32768u);
			reason = "failed to allocate writable host-RSP storage for guest continuation stub";
			return GuestNativeCallExitReason.Exception;
		}
		var previousActiveBackend = _activeExecutionBackend;
		var previousActiveContext = _activeCpuContext;
		var previousSentinel = _activeEntryReturnSentinelRip;
		var previousReturnSlotAddress = _activeGuestReturnSlotAddress;
		var previousForcedExit = _activeForcedGuestExit;
		var previousYieldRequested = _activeGuestThreadYieldRequested;
		var previousYieldReason = _activeGuestThreadYieldReason;
		nint previousHostRspSlotValue = TlsGetValue(_hostRspSlotTlsIndex);
		try
		{
			_activeExecutionBackend = this;
			_activeCpuContext = context;
			_activeEntryReturnSentinelRip = 0;
			_activeGuestReturnSlotAddress = returnSlotAddress;
			_activeForcedGuestExit = false;
			_activeGuestThreadYieldRequested = false;
			_activeGuestThreadYieldReason = null;
			BindTlsBase(context);
			byte* ptr2 = (byte*)ptr;
			ulong hostRspSlot = (ulong)hostRspStorage;
			var emitter = new NativeCodeEmitter(ptr2);

			emitter.Emit(0x53); // push rbx
			emitter.Emit(0x55); // push rbp
			emitter.Emit(0x57); // push rdi
			emitter.Emit(0x56); // push rsi
			emitter.Emit(0x41); emitter.Emit(0x54); // push r12
			emitter.Emit(0x41); emitter.Emit(0x55); // push r13
			emitter.Emit(0x41); emitter.Emit(0x56); // push r14
			emitter.Emit(0x41); emitter.Emit(0x57); // push r15
			EmitHostNonvolatileXmmSave(ptr2, ref emitter.Offset);
			// Restore the fiber's floating-point control environment before
			// abandoning the host stack. This path is used when a blocked guest
			// continuation migrates to another managed worker.
			emitter.Emit(0x48); emitter.Emit(0x83); emitter.Emit(0xEC); emitter.Emit(0x08); // sub rsp,8
			emitter.Emit(0xC7); emitter.Emit(0x04); emitter.Emit(0x24); // mov dword [rsp],imm32
			emitter.Emit(context.Mxcsr);
			emitter.Emit(0x0F); emitter.Emit(0xAE); emitter.Emit(0x14); emitter.Emit(0x24); // ldmxcsr [rsp]
			emitter.Emit(0x66); emitter.Emit(0xC7); emitter.Emit(0x04); emitter.Emit(0x24); // mov word [rsp],imm16
			emitter.Emit(context.FpuControlWord);
			emitter.Emit(0xD9); emitter.Emit(0x2C); emitter.Emit(0x24); // fldcw [rsp]
			emitter.Emit(0x48); emitter.Emit(0x83); emitter.Emit(0xC4); emitter.Emit(0x08); // add rsp,8
			emitter.EmitMovR64Immediate(0x49, 0xBA, hostRspSlot); // mov r10, hostRspSlot
			emitter.Emit(0x49); emitter.Emit(0x89); emitter.Emit(0x22); // mov [r10], rsp
			emitter.EmitMovR64Immediate(0x48, 0xB8, context[CpuRegister.Rsp]); // mov rax, guest rsp
			emitter.Emit(0x48); emitter.Emit(0x89); emitter.Emit(0xC4); // mov rsp, rax
			emitter.Emit(0x48); emitter.Emit(0x83); emitter.Emit(0xEC); emitter.Emit(0x08); // reserve transfer slot
			emitter.EmitMovR64Immediate(0x48, 0xB8, entryPoint); // mov rax, entryPoint
			emitter.Emit(0x48); emitter.Emit(0x89); emitter.Emit(0x04); emitter.Emit(0x24); // mov [rsp],rax
			emitter.EmitMovR64Immediate(0x48, 0xBB, context[CpuRegister.Rbx]); // mov rbx, imm64
			emitter.EmitMovR64Immediate(0x48, 0xBD, context[CpuRegister.Rbp]); // mov rbp, imm64
			emitter.EmitMovR64Immediate(0x48, 0xBF, context[CpuRegister.Rdi]); // mov rdi, imm64
			emitter.EmitMovR64Immediate(0x48, 0xBE, context[CpuRegister.Rsi]); // mov rsi, imm64
			emitter.EmitMovR64Immediate(0x48, 0xBA, context[CpuRegister.Rdx]); // mov rdx, imm64
			emitter.EmitMovR64Immediate(0x48, 0xB9, context[CpuRegister.Rcx]); // mov rcx, imm64
			emitter.EmitMovR64Immediate(0x49, 0xB8, context[CpuRegister.R8]); // mov r8, imm64
			emitter.EmitMovR64Immediate(0x49, 0xB9, context[CpuRegister.R9]); // mov r9, imm64
			emitter.EmitMovR64Immediate(0x49, 0xBA, context[CpuRegister.R10]); // mov r10, imm64
			emitter.EmitMovR64Immediate(0x49, 0xBC, context[CpuRegister.R12]); // mov r12, imm64
			emitter.EmitMovR64Immediate(0x49, 0xBD, context[CpuRegister.R13]); // mov r13, imm64
			emitter.EmitMovR64Immediate(0x49, 0xBE, context[CpuRegister.R14]); // mov r14, imm64
			emitter.EmitMovR64Immediate(0x49, 0xBF, context[CpuRegister.R15]); // mov r15, imm64
			emitter.EmitMovR64Immediate(0x49, 0xBB, context[CpuRegister.R11]); // mov r11, imm64
			emitter.EmitMovR64Immediate(0x48, 0xB8, context[CpuRegister.Rax]); // mov rax, imm64
			emitter.Emit(0xC3); // ret through the synthetic transfer slot
			ActiveEntryReturnSentinelRip = (ulong)_guestReturnStub;
			if (returnSlotAddress == 0 || !context.TryWriteUInt64(returnSlotAddress, (ulong)_guestReturnStub))
			{
				reason = $"failed to patch guest continuation return slot at 0x{returnSlotAddress:X16}";
				return GuestNativeCallExitReason.Exception;
			}
			uint oldProtect = default(uint);
			if (!VirtualProtect(ptr, stubSize, 32u, &oldProtect))
			{
				reason = "failed to seal guest continuation stub execute-read";
				return GuestNativeCallExitReason.Exception;
			}
			FlushInstructionCache(GetCurrentProcess(), ptr, stubSize);
			ActiveGuestThreadYieldRequested = false;
			ActiveGuestThreadYieldReason = null;
			try
			{
				int nativeReturn;
				if (name == "tbb_thead")
				{
					nativeReturn = RunGuestEntryStub(ptr, hostRspSlot, requireNativeWorker: true);
				}
				else
				{
					if (!TlsSetValue(_hostRspSlotTlsIndex, (nint)hostRspSlot))
					{
						reason = "failed to bind host-RSP storage for guest continuation stub";
						return GuestNativeCallExitReason.Exception;
					}
					nativeReturn = CallNativeEntry(ptr);
				}
				if (ActiveGuestThreadYieldRequested)
				{
					reason = ActiveGuestThreadYieldReason ?? "guest thread blocked";
					return GuestNativeCallExitReason.Blocked;
				}
				if (ActiveForcedGuestExit)
				{
					reason = LastError ?? "guest thread forced exit";
					return GuestNativeCallExitReason.ForcedExit;
				}
				reason = $"returned 0x{nativeReturn:X8}";
				return GuestNativeCallExitReason.Returned;
			}
			catch (AccessViolationException ex)
			{
				reason = "access violation: " + ex.Message;
				return GuestNativeCallExitReason.Exception;
			}
			catch (Exception ex)
			{
				reason = ex.GetType().Name + ": " + ex.Message;
				return GuestNativeCallExitReason.Exception;
			}
		}
		finally
		{
			TlsSetValue(_hostRspSlotTlsIndex, previousHostRspSlotValue);
			RestoreActiveExecutionThread(
				previousActiveBackend,
				previousActiveContext,
				previousSentinel,
				previousReturnSlotAddress,
				previousForcedExit,
				previousYieldRequested,
				previousYieldReason);
			NativeMemory.Free(hostRspStorage);
			VirtualFree(ptr, 0u, 32768u);
		}
	}

	// The continuation trampoline is rebuilt on every blocked-thread resume.
	// Keep its tiny writer on the stack: capturing local emit functions create a
	// managed display-class allocation on this extremely hot path.
	private unsafe ref struct NativeCodeEmitter(byte* code)
	{
		private readonly byte* _code = code;
		public int Offset;

		public void Emit(byte value)
		{
			_code[Offset++] = value;
		}

		public void Emit(ushort value)
		{
			*(ushort*)(_code + Offset) = value;
			Offset += sizeof(ushort);
		}

		public void Emit(uint value)
		{
			*(uint*)(_code + Offset) = value;
			Offset += sizeof(uint);
		}

		private void Emit(ulong value)
		{
			*(ulong*)(_code + Offset) = value;
			Offset += sizeof(ulong);
		}

		public void EmitMovR64Immediate(byte rex, byte opcode, ulong value)
		{
			Emit(rex);
			Emit(opcode);
			Emit(value);
		}
	}

	private static ulong AlignDown(ulong value, ulong alignment)
	{
		if (alignment == 0)
		{
			return value;
		}
		return value & ~(alignment - 1);
	}

	private static unsafe void EmitByte(byte* code, ref int offset, byte value)
	{
		code[offset++] = value;
	}

	private static unsafe void EmitUInt32(byte* code, ref int offset, uint value)
	{
		*(uint*)(code + offset) = value;
		offset += sizeof(uint);
	}

	private static unsafe void EmitHostNonvolatileXmmSave(byte* code, ref int offset)
	{
		EmitByte(code, ref offset, 0x48);
		EmitByte(code, ref offset, 0x81);
		EmitByte(code, ref offset, 0xEC);
		EmitUInt32(code, ref offset, HostXmmSaveAreaSize);
		for (int xmm = 6; xmm <= 15; xmm++)
		{
			EmitMovdquRspXmm(code, ref offset, store: true, xmm, (byte)((xmm - 6) * 16));
		}
	}

	private static unsafe void EmitHostNonvolatileXmmRestore(byte* code, ref int offset)
	{
		for (int xmm = 6; xmm <= 15; xmm++)
		{
			EmitMovdquRspXmm(code, ref offset, store: false, xmm, (byte)((xmm - 6) * 16));
		}
		EmitByte(code, ref offset, 0x48);
		EmitByte(code, ref offset, 0x81);
		EmitByte(code, ref offset, 0xC4);
		EmitUInt32(code, ref offset, HostXmmSaveAreaSize);
	}

	private static unsafe void EmitMovdquRspXmm(byte* code, ref int offset, bool store, int xmm, byte displacement)
	{
		EmitByte(code, ref offset, 0xF3);
		if (xmm >= 8)
		{
			EmitByte(code, ref offset, 0x44);
		}
		EmitByte(code, ref offset, 0x0F);
		EmitByte(code, ref offset, store ? (byte)0x7F : (byte)0x6F);
		if (displacement < 0x80)
		{
			EmitByte(code, ref offset, (byte)(0x44 | ((xmm & 7) << 3)));
			EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, displacement);
		}
		else
		{
			EmitByte(code, ref offset, (byte)(0x84 | ((xmm & 7) << 3)));
			EmitByte(code, ref offset, 0x24);
			EmitUInt32(code, ref offset, displacement);
		}
	}

	private unsafe bool ExecuteEntry(CpuContext context, ulong entryPoint, out OrbisGen2Result result)
	{
		Console.Error.WriteLine($"[LOADER][INFO] ExecuteEntry starting at 0x{entryPoint:X16}");
		Console.Error.WriteLine($"[LOADER][INFO] RSP=0x{context[CpuRegister.Rsp]:X16}, RDI=0x{context[CpuRegister.Rdi]:X16}");
		ulong num = context[CpuRegister.Rsp];
		if (num == 0)
		{
			LastError = "Guest stack pointer is zero";
			result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
			return false;
		}
		Console.Error.WriteLine($"[LOADER][INFO] StackTop: 0x{num:X16}");
		const uint stubSize = 512u;
		void* ptr = VirtualAlloc(null, stubSize, 12288u, 4u);
		if (ptr == null)
		{
			LastError = "Failed to allocate executable memory for stub";
			result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
			return false;
		}
		void* hostRspStorage = NativeMemory.Alloc((nuint)sizeof(ulong));
		if (hostRspStorage == null)
		{
			VirtualFree(ptr, 0u, 32768u);
			LastError = "Failed to allocate writable host-RSP storage for stub";
			result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
			return false;
		}
		var previousActiveBackend = _activeExecutionBackend;
		var previousActiveContext = _activeCpuContext;
		var previousSentinel = _activeEntryReturnSentinelRip;
		var previousReturnSlotAddress = _activeGuestReturnSlotAddress;
		var previousForcedExit = _activeForcedGuestExit;
		var previousYieldRequested = _activeGuestThreadYieldRequested;
		var previousYieldReason = _activeGuestThreadYieldReason;
		nint previousHostRspSlotValue = TlsGetValue(_hostRspSlotTlsIndex);
		try
		{
			_activeExecutionBackend = this;
			_activeCpuContext = context;
			_activeEntryReturnSentinelRip = 0;
			_activeGuestReturnSlotAddress = 0;
			_activeForcedGuestExit = false;
			_activeGuestThreadYieldRequested = false;
			_activeGuestThreadYieldReason = null;
			BindTlsBase(context);
			byte* ptr2 = (byte*)ptr;
			ulong num2 = (ulong)hostRspStorage;
			int num3 = 0;
			ptr2[num3++] = 83;
			ptr2[num3++] = 85;
			ptr2[num3++] = 87;
			ptr2[num3++] = 86;
			ptr2[num3++] = 65;
			ptr2[num3++] = 84;
			ptr2[num3++] = 65;
			ptr2[num3++] = 85;
			ptr2[num3++] = 65;
			ptr2[num3++] = 86;
			ptr2[num3++] = 65;
			ptr2[num3++] = 87;
			EmitHostNonvolatileXmmSave(ptr2, ref num3);
			ptr2[num3++] = 73;
			ptr2[num3++] = 186;
			*(ulong*)(ptr2 + num3) = num2;
			num3 += 8;
			ptr2[num3++] = 73;
			ptr2[num3++] = 137;
			ptr2[num3++] = 34;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rsp];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 137;
			ptr2[num3++] = 196;
			ptr2[num3++] = 72;
			ptr2[num3++] = 131;
			ptr2[num3++] = 236;
			ptr2[num3++] = 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 189;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rbp];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rdi];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 137;
			ptr2[num3++] = 199;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rsi];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 137;
			ptr2[num3++] = 198;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rdx];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 137;
			ptr2[num3++] = 194;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rcx];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 137;
			ptr2[num3++] = 193;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = entryPoint;
			num3 += 8;
			ptr2[num3++] = byte.MaxValue;
			ptr2[num3++] = 208;
			int num4 = num3 + 4;
			ptr2[num3++] = 72;
			ptr2[num3++] = 131;
			ptr2[num3++] = 196;
			ptr2[num3++] = 8;
			ptr2[num3++] = 73;
			ptr2[num3++] = 186;
			*(ulong*)(ptr2 + num3) = num2;
			num3 += 8;
			ptr2[num3++] = 73;
			ptr2[num3++] = 139;
			ptr2[num3++] = 34;
			EmitHostNonvolatileXmmRestore(ptr2, ref num3);
			ptr2[num3++] = 65;
			ptr2[num3++] = 95;
			ptr2[num3++] = 65;
			ptr2[num3++] = 94;
			ptr2[num3++] = 65;
			ptr2[num3++] = 93;
			ptr2[num3++] = 65;
			ptr2[num3++] = 92;
			ptr2[num3++] = 94;
			ptr2[num3++] = 95;
			ptr2[num3++] = 93;
			ptr2[num3++] = 91;
			ptr2[num3++] = 195;
			ulong value = (ulong)ptr + (ulong)num4;
			ActiveEntryReturnSentinelRip = (ulong)_guestReturnStub;
			_activeGuestReturnSlotAddress = context[CpuRegister.Rsp] - 16uL;
			if (!context.TryWriteUInt64(context[CpuRegister.Rsp], value))
			{
				LastError = $"Failed to patch native return sentinel at 0x{context[CpuRegister.Rsp]:X16}";
				result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
				return false;
			}
			uint num5 = default(uint);
			if (!VirtualProtect(ptr, stubSize, 32u, &num5))
			{
				LastError = "Failed to seal native entry stub execute-read";
				result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
				return false;
			}
			FlushInstructionCache(GetCurrentProcess(), ptr, stubSize);
			if (_hostRspSlotStorage != 0)
			{
				*(ulong*)_hostRspSlotStorage = num2;
			}
			if (!TlsSetValue(_hostRspSlotTlsIndex, (nint)num2))
			{
				LastError = "Failed to bind host-RSP storage for native entry stub";
				result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
				return false;
			}
			if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_SENTINEL_PROBE"), "1", StringComparison.Ordinal))
			{
				Console.Error.WriteLine("[LOADER][INFO] Running unresolved sentinel probe...");
				CallNativeEntry((void*)65534);
				Console.Error.WriteLine("[LOADER][INFO] Sentinel probe returned.");
			}
			Console.Error.WriteLine("[LOADER][INFO] Calling guest entry...");
			StartStallWatchdog();
			StartReadyThreadDispatcher();
			int num6 = -1;
			try
			{
				num6 = CallNativeEntry(ptr);
				Console.Error.WriteLine($"[LOADER][INFO] Guest returned: {num6}");
				// A host stop has already invalidated the session. Draining guest
				// continuations here can re-enter a blocked HLE call after its owner
				// has exited, preventing the embedded GUI from receiving its exit
				// callback.
				if (!ActiveForcedGuestExit)
				{
					PumpUntilGuestThreadsIdle(context, "entry_return");
				}
			}
			catch (AccessViolationException ex)
			{
				Console.Error.WriteLine("[LOADER][ERROR] Access Violation during execution: " + ex.Message);
				Console.Error.WriteLine("[LOADER][ERROR] This usually means:");
				Console.Error.WriteLine("  1. Invalid memory access in guest code");
				Console.Error.WriteLine("  2. Unpatched import/TLS call");
				Console.Error.WriteLine("  3. Stack corruption");
				num6 = -1;
			}
			catch (Exception ex2)
			{
				Console.Error.WriteLine("[LOADER][ERROR] Exception during execution: " + ex2.GetType().Name + ": " + ex2.Message);
				LastError = "Exception during execution: " + ex2.GetType().Name + ": " + ex2.Message;
				num6 = -1;
			}
			if (ActiveForcedGuestExit)
			{
				result = OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP;
				if (string.IsNullOrEmpty(LastError))
				{
					LastError = "Detected repeating import loop and forced guest unwind to host.";
				}
				Console.Error.WriteLine("[LOADER][ERROR] " + LastError);
				return false;
			}
			if (num6 == 0)
			{
				result = OrbisGen2Result.ORBIS_GEN2_OK;
				LastError = null;
				return true;
			}
			result = OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP;
			if (string.IsNullOrEmpty(LastError))
			{
				LastError = $"Guest entry point returned non-zero: {num6}";
			}
			Console.Error.WriteLine("[LOADER][ERROR] " + LastError);
			return false;
		}
		finally
		{
			StopReadyThreadDispatcher();
			StopStallWatchdog();
			ActiveEntryReturnSentinelRip = 0uL;
			TlsSetValue(_hostRspSlotTlsIndex, previousHostRspSlotValue);
			if (_hostRspSlotStorage != 0)
			{
				*(long*)_hostRspSlotStorage = 0L;
			}
			RestoreActiveExecutionThread(
				previousActiveBackend,
				previousActiveContext,
				previousSentinel,
				previousReturnSlotAddress,
				previousForcedExit,
				previousYieldRequested,
				previousYieldReason);
			NativeMemory.Free(hostRspStorage);
			VirtualFree(ptr, 0u, 32768u);
		}
	}


	private void MarkExecutionProgress()
	{
		Volatile.Write(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
	}

	private static int GetStallWatchdogSeconds()
	{
		if (int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_STALL_WATCHDOG_SECONDS"), out var result))
		{
			return Math.Max(0, result);
		}
		return 20;
	}


	private void StartStallWatchdog()
	{
		int stallWatchdogSeconds = GetStallWatchdogSeconds();
		if (stallWatchdogSeconds <= 0 || _stallWatchdogThread != null)
		{
			return;
		}
		_stallWatchdogStop = false;

		// Drives woken threads when every guest thread is parked (nothing dispatches then).
		var dispatcherThread = new Thread(new ThreadStart(delegate
		{
			while (!_stallWatchdogStop)
			{
				Thread.Sleep(1);
				WakeExpiredBlockedGuestThreads();
				if (Volatile.Read(ref _readyGuestThreadCount) > 0 && _cpuContext is { } dispatchContext)
				{
					Pump(dispatchContext, "dispatcher");
				}
			}
		}))
		{
			IsBackground = true,
			Name = "SharpEmu-GuestThreadDispatcher"
		};
		dispatcherThread.Start();

		long num = (long)((double)stallWatchdogSeconds * Stopwatch.Frequency);
		int periodicSnapshotSeconds =
			int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_PERIODIC_SNAPSHOT_SECONDS"), out var pss)
				? Math.Max(0, pss)
				: 0;
		long periodicSnapshotTicks = (long)((double)periodicSnapshotSeconds * Stopwatch.Frequency);
		long lastPeriodicSnapshot = Stopwatch.GetTimestamp();
		_stallWatchdogThread = new Thread(new ThreadStart(delegate
		{
			while (!_stallWatchdogStop)
			{
				Thread.Sleep(200);
				if (_stallWatchdogStop)
				{
					break;
				}
				if (periodicSnapshotTicks > 0 &&
					Stopwatch.GetTimestamp() - lastPeriodicSnapshot >= periodicSnapshotTicks)
				{
					lastPeriodicSnapshot = Stopwatch.GetTimestamp();
					Console.Error.WriteLine("[LOADER][ERROR] --- periodic snapshot ---");
					LogStallWatchdogSnapshot();
					Console.Error.Flush();
				}
				long num2 = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastProgressTimestamp);
				if (num2 < num)
				{
					continue;
				}
				if (HasReadyGuestThread())
				{
					Console.Error.WriteLine(
						$"[LOADER][WARN] No import progress for {stallWatchdogSeconds}s, but a guest thread is ready; dispatcher will resume it.");
					LogStallWatchdogSnapshot();
					Console.Error.Flush();
					MarkExecutionProgress();
					continue;
				}
				if (IsExpectedBlockingImportStall(out var blockingNid, out var blockingName))
				{
					Console.Error.WriteLine(
						$"[LOADER][WARN] No import progress for {stallWatchdogSeconds}s while waiting in {blockingName} ({blockingNid}); continuing.");
					LogStallWatchdogSnapshot();
					Console.Error.Flush();
					MarkExecutionProgress();
					continue;
				}
				if (Interlocked.Exchange(ref _stallWatchdogTriggered, 1) != 0)
				{
					continue;
				}
				LastError = $"Execution stalled with no import progress for {stallWatchdogSeconds}s (imports={Volatile.Read(ref _importDispatchCount)}).";
				Console.Error.WriteLine("[LOADER][ERROR] " + LastError);
				LogStallWatchdogSnapshot();
				Console.Error.Flush();
				Environment.Exit(4);
			}
		}))
		{
			IsBackground = true,
			Name = "SharpEmu-StallWatchdog"
		};
		_stallWatchdogThread.Start();
	}

	private bool HasReadyGuestThread()
	{
		WakeExpiredBlockedGuestThreads();
		using (LockGate("HasReadyGuestThread"))
		{
			foreach (var thread in _guestThreads.Values)
			{
				if (thread.State is GuestThreadRunState.Ready)
				{
					return true;
				}
			}
		}

		return false;
	}

	// A thread parked in a blocking wait is idle by design, not stalled.
	private bool IsExpectedBlockingImportStall(out string nid, out string name)
	{
		nid = string.Empty;
		name = string.Empty;
		var cpuContext = _cpuContext;
		if (cpuContext is null)
		{
			return false;
		}

		var importAddress = cpuContext.Rip & 0xFFFFFFFFFFFFFFF0uL;
		foreach (var entry in _importEntries)
		{
			if (entry.Address != importAddress)
			{
				continue;
			}

			nid = entry.Nid;
			name = _moduleManager.TryGetExport(nid, out var export)
				? $"{export.LibraryName}:{export.Name}"
				: nid;
			return nid is
				"Op8TBGY5KHg" or // pthread_cond_wait
				"27bAgiJmOh0" or // pthread_cond_timedwait
				"fzyMKs9kim0";   // sceKernelWaitEqueue
		}

		return false;
	}

	private void StopStallWatchdog()
	{
		_stallWatchdogStop = true;
		Thread? stallWatchdogThread = _stallWatchdogThread;
		if (stallWatchdogThread == null)
		{
			return;
		}
		if (!ReferenceEquals(Thread.CurrentThread, stallWatchdogThread))
		{
			try
			{
				stallWatchdogThread.Join(300);
			}
			catch
			{
			}
		}
		_stallWatchdogThread = null;
	}

	// A guest thread only gets dispatched to a native thread when some running
	// guest thread calls Pump (which happens inside blocking HLE primitives:
	// waits, usleep, pthread_create, entry_return). That leaves a starvation
	// hole: a guest thread that spins on a non-blocking HLE call (e.g.
	// sceAudioOutOutput) never pumps, so any thread that was made Ready — for
	// example a job worker woken by sceKernelSetEventFlag — sits in the ready
	// queue forever. Import progress keeps advancing (the spin), so the stall
	// watchdog never fires either, and the whole game deadlocks with 0 draws.
	//
	// This background dispatcher closes the hole: it drains the ready queue on
	// a short interval regardless of whether any guest thread pumps. It is
	// deliberately self-contained (it does not touch Pump or the pump-depth
	// guard) so it cannot alter the existing cooperative dispatch path.
	private void StartReadyThreadDispatcher()
	{
		if (_readyDispatchThread != null)
		{
			return;
		}
		var logSnapshots = string.Equals(
			Environment.GetEnvironmentVariable("SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS"),
			"1",
			StringComparison.Ordinal);
		var nextSnapshotTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
		_readyDispatchStop = false;
		_readyDispatchThread = new Thread(new ThreadStart(delegate
		{
			while (!_readyDispatchStop)
			{
				Thread.Sleep(1);
				if (_readyDispatchStop)
				{
					break;
				}
				// The count is a fast diagnostic hint, while the queue/state pair under
				// _guestThreadGate is authoritative. Always attempt a locked drain so a
				// stale hint cannot strand a runnable continuation.
				DispatchReadyGuestThreads();
				if (logSnapshots && Stopwatch.GetTimestamp() >= nextSnapshotTimestamp)
				{
					lock (_guestThreadGate)
					{
						foreach (var thread in _guestThreads.Values)
						{
							Console.Error.WriteLine(
								$"[LOADER][TRACE] guest_thread.snapshot " +
								$"handle=0x{thread.ThreadHandle:X16} name='{thread.Name}' " +
								$"state={thread.State} executor={thread.ExecutorActive} " +
								$"imports={Interlocked.Read(ref thread.ImportCount)} " +
								$"nid={Volatile.Read(ref thread.LastImportNid) ?? "none"} " +
								$"ret=0x{Volatile.Read(ref thread.LastReturnRip):X16} " +
								$"block={thread.BlockReason ?? "none"} " +
								$"wake={thread.BlockWakeKey ?? "none"} " +
								$"host_managed={thread.HostThread?.ManagedThreadId ?? 0} " +
								$"host_tid={Volatile.Read(ref thread.HostThreadId)}");
						}
					}
					nextSnapshotTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
				}
			}
		}))
		{
			IsBackground = true,
			Name = "SharpEmu-ReadyDispatch",
		};
		_readyDispatchThread.Start();
	}

	private void StopReadyThreadDispatcher()
	{
		_readyDispatchStop = true;
		Thread? readyDispatchThread = _readyDispatchThread;
		if (readyDispatchThread == null)
		{
			return;
		}
		if (!ReferenceEquals(Thread.CurrentThread, readyDispatchThread))
		{
			try
			{
				readyDispatchThread.Join(300);
			}
			catch
			{
			}
		}
		_readyDispatchThread = null;
	}

	// Dequeue every currently-ready guest thread and start a native thread for
	// each, mirroring Pump's dispatch step. Dequeue and the Ready->Running
	// transition happen under _guestThreadGate, so this races safely with a
	// concurrent Pump: each ready thread is claimed once (the State check skips
	// any that another dispatcher already took).
	private void DispatchReadyGuestThreads()
	{
		while (true)
		{
			GuestThreadState? thread = null;
			lock (_guestThreadGate)
			{
				_ = TryClaimReadyGuestThreadLocked(out thread);
			}

			if (thread == null)
			{
				return;
			}

			ScheduleGuestThreadExecution(thread, "ready-dispatch");
		}
	}

	private void ScheduleGuestThreadExecution(GuestThreadState thread, string reason)
	{
		GuestExecutionRunner runner;
		lock (_guestThreadGate)
		{
			runner = thread.ExecutionRunner ??= new GuestExecutionRunner(
				thread.ThreadHandle,
				thread.Name,
				MapGuestThreadPriority(thread.Priority));
		}
		runner.Schedule(() => RunGuestThread(thread, reason));
	}

	// Caller must hold _guestThreadGate. A guest wait can be satisfied before
	// RunGuestThread has finished restoring its host/TLS state, so Ready alone
	// is not sufficient to authorize another executor. ExecutorActive is the
	// scheduler's authoritative single-owner token and covers both asynchronous
	// host threads and synchronous Pump("entry_return") execution.
	private bool TryClaimReadyGuestThreadLocked(out GuestThreadState? thread)
	{
		thread = null;
		var candidatesToInspect = _readyGuestThreads.Count;
		for (var index = 0; index < candidatesToInspect; index++)
		{
			var candidate = _readyGuestThreads.Dequeue();
			Interlocked.Decrement(ref _readyGuestThreadCount);
			if (candidate.State != GuestThreadRunState.Ready)
			{
				continue;
			}

			if (candidate.ExecutorActive)
			{
				_readyGuestThreads.Enqueue(candidate);
				Interlocked.Increment(ref _readyGuestThreadCount);
				candidate.ExecutorClaimDeferrals++;
				if (_logGuestThreads &&
					(candidate.ExecutorClaimDeferrals <= 4 ||
					(candidate.ExecutorClaimDeferrals & (candidate.ExecutorClaimDeferrals - 1)) == 0))
				{
					Console.Error.WriteLine(
						$"[LOADER][TRACE] guest_threads.defer_active_executor " +
						$"handle=0x{candidate.ThreadHandle:X16} name='{candidate.Name}' " +
						$"host_managed={candidate.HostThread?.ManagedThreadId ?? 0} " +
						$"host_tid={Volatile.Read(ref candidate.HostThreadId)} " +
						$"deferrals={candidate.ExecutorClaimDeferrals}");
				}
				continue;
			}

			candidate.ExecutorActive = true;
			candidate.State = GuestThreadRunState.Running;
			thread = candidate;
			return true;
		}

		return false;
	}

	private void LogStallWatchdogSnapshot()
	{
		try
		{
			var cpuContext = _cpuContext;
			if (cpuContext is null)
			{
				return;
			}
			ulong rsp = cpuContext[CpuRegister.Rsp];
			Console.Error.WriteLine($"[LOADER][ERROR] Stall snapshot: rip=0x{cpuContext.Rip:X16} rsp=0x{rsp:X16} rbp=0x{cpuContext[CpuRegister.Rbp]:X16} rax=0x{cpuContext[CpuRegister.Rax]:X16} rbx=0x{cpuContext[CpuRegister.Rbx]:X16} rcx=0x{cpuContext[CpuRegister.Rcx]:X16} rdx=0x{cpuContext[CpuRegister.Rdx]:X16} rsi=0x{cpuContext[CpuRegister.Rsi]:X16} rdi=0x{cpuContext[CpuRegister.Rdi]:X16}");
			ulong num = cpuContext.Rip & 0xFFFFFFFFFFFFFFF0uL;
			var importEntries = _importEntries;
			for (int i = 0; i < importEntries.Length; i++)
			{
				if (importEntries[i].Address != num)
				{
					continue;
				}
				string text = importEntries[i].Nid;
				if (_moduleManager.TryGetExport(text, out ExportedFunction export))
				{
					Console.Error.WriteLine($"[LOADER][ERROR] Stall import-stub: rip=0x{num:X16} nid={text} -> {export.LibraryName}:{export.Name}");
				}
				else
				{
					Console.Error.WriteLine($"[LOADER][ERROR] Stall import-stub: rip=0x{num:X16} nid={text}");
				}
				break;
			}
			Span<byte> destination = stackalloc byte[16];
			if (cpuContext.Memory.TryRead(cpuContext.Rip, destination))
			{
				Console.Error.WriteLine($"[LOADER][ERROR] Stall bytes @rip: {BitConverter.ToString(destination.ToArray()).Replace("-", " ")}");
			}
			else if (cpuContext.Memory.TryRead(num, destination))
			{
				Console.Error.WriteLine($"[LOADER][ERROR] Stall bytes @rip_align: {BitConverter.ToString(destination.ToArray()).Replace("-", " ")}");
			}
			if (rsp != 0 && cpuContext.TryReadUInt64(rsp, out var value) && cpuContext.TryReadUInt64(rsp + 8, out var value2))
			{
				Console.Error.WriteLine($"[LOADER][ERROR] Stall stack: [rsp]=0x{value:X16} [rsp+8]=0x{value2:X16}");
			}

			var threads = SnapshotGuestThreads();
			if (threads.Length != 0)
			{
				var logged = 0;
				foreach (var thread in threads)
				{
					var hostThreadId = Volatile.Read(ref thread.HostThreadId);
					var hostContextText = string.Empty;
					if (TryCaptureHostThreadContext(hostThreadId, out var hostContext))
					{
						hostContextText =
							$" host_tid={hostThreadId} host_rip=0x{hostContext.Rip:X16} host_rsp=0x{hostContext.Rsp:X16} " +
							$"host_rbp=0x{hostContext.Rbp:X16} host_rax=0x{hostContext.Rax:X16} host_rbx=0x{hostContext.Rbx:X16} " +
							$"host_rcx=0x{hostContext.Rcx:X16} host_rdx=0x{hostContext.Rdx:X16}";
					}
					else if (hostThreadId != 0)
					{
						hostContextText = $" host_tid={hostThreadId} host_ctx=unavailable";
					}

					Console.Error.WriteLine(
						$"[LOADER][ERROR] Stall guest-thread: handle=0x{thread.ThreadHandle:X16} name='{thread.Name}' " +
						$"state={thread.State} imports={Interlocked.Read(ref thread.ImportCount)} " +
						$"nid={Volatile.Read(ref thread.LastImportNid) ?? "none"} ret=0x{Volatile.Read(ref thread.LastReturnRip):X16} " +
						$"rdi=0x{Volatile.Read(ref thread.LastImportRdi):X16} rsi=0x{Volatile.Read(ref thread.LastImportRsi):X16} " +
						$"rdx=0x{Volatile.Read(ref thread.LastImportRdx):X16} block={thread.BlockReason ?? "none"}{hostContextText}");
					logged++;
					if (logged >= 48 && threads.Length > logged)
					{
						Console.Error.WriteLine($"[LOADER][ERROR] Stall guest-thread: ... {threads.Length - logged} more");
						break;
					}
				}
			}
		}
		catch
		{
		}
	}

	private unsafe static bool TryCaptureHostThreadContext(int hostThreadId, out HostThreadContextSnapshot snapshot)
	{
		snapshot = default;
		if (hostThreadId == 0 || unchecked((uint)hostThreadId) == GetCurrentThreadId())
		{
			return false;
		}

		var threadHandle = OpenThread(ThreadGetContext | ThreadSuspendResume, false, unchecked((uint)hostThreadId));
		if (threadHandle == 0)
		{
			return false;
		}

		void* contextRecord = null;
		var suspended = false;
		try
		{
			if (SuspendThread(threadHandle) == uint.MaxValue)
			{
				return false;
			}

			suspended = true;
			contextRecord = NativeMemory.AllocZeroed((nuint)Win64ContextSize);
			WriteCtxU32(contextRecord, Win64ContextFlagsOffset, ContextAmd64ControlInteger);
			if (!GetThreadContext(threadHandle, contextRecord))
			{
				return false;
			}

			snapshot = new HostThreadContextSnapshot(
				true,
				ReadCtxU64(contextRecord, 248),
				ReadCtxU64(contextRecord, 152),
				ReadCtxU64(contextRecord, 160),
				ReadCtxU64(contextRecord, 120),
				ReadCtxU64(contextRecord, 144),
				ReadCtxU64(contextRecord, 128),
				ReadCtxU64(contextRecord, 136));
			return true;
		}
		finally
		{
			if (contextRecord != null)
			{
				NativeMemory.Free(contextRecord);
			}
			if (suspended)
			{
				_ = ResumeThread(threadHandle);
			}
			_ = CloseHandle(threadHandle);
		}
	}


	private static uint TlsAlloc() =>
		OperatingSystem.IsWindows() ? Win32TlsAlloc() : PosixHostStubs.TlsAlloc();

	private static bool TlsFree(uint dwTlsIndex) =>
		OperatingSystem.IsWindows() ? Win32TlsFree(dwTlsIndex) : PosixHostStubs.TlsFree(dwTlsIndex);

	private static bool TlsSetValue(uint dwTlsIndex, nint lpTlsValue) =>
		OperatingSystem.IsWindows() ? Win32TlsSetValue(dwTlsIndex, lpTlsValue) : PosixHostStubs.TlsSetValue(dwTlsIndex, lpTlsValue);

	private static nint TlsGetValue(uint dwTlsIndex) =>
		OperatingSystem.IsWindows() ? Win32TlsGetValue(dwTlsIndex) : PosixHostStubs.TlsGetValue(dwTlsIndex);

	private unsafe static void* AddVectoredExceptionHandler(uint first, IntPtr handler) =>
		OperatingSystem.IsWindows() ? Win32AddVectoredExceptionHandler(first, handler) : null;

	private unsafe static uint RemoveVectoredExceptionHandler(void* handle) =>
		OperatingSystem.IsWindows() ? Win32RemoveVectoredExceptionHandler(handle) : 0u;

	private static IntPtr SetUnhandledExceptionFilter(IntPtr lpTopLevelExceptionFilter) =>
		OperatingSystem.IsWindows() ? Win32SetUnhandledExceptionFilter(lpTopLevelExceptionFilter) : IntPtr.Zero;

	private static uint GetCurrentThreadId() =>
		OperatingSystem.IsWindows() ? Win32GetCurrentThreadId() : PosixHostStubs.GetCurrentThreadId();

	private static nint GetCurrentThread() =>
		OperatingSystem.IsWindows() ? Win32GetCurrentThread() : 0;

	private static nuint SetThreadAffinityMask(nint hThread, nuint dwThreadAffinityMask) =>
		OperatingSystem.IsWindows() ? Win32SetThreadAffinityMask(hThread, dwThreadAffinityMask) : 1;

	private static nint OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId) =>
		OperatingSystem.IsWindows() ? Win32OpenThread(dwDesiredAccess, bInheritHandle, dwThreadId) : 0;

	private static uint SuspendThread(nint hThread) =>
		OperatingSystem.IsWindows() ? Win32SuspendThread(hThread) : uint.MaxValue;

	private static uint ResumeThread(nint hThread) =>
		OperatingSystem.IsWindows() ? Win32ResumeThread(hThread) : uint.MaxValue;

	private unsafe static bool GetThreadContext(nint hThread, void* lpContext) =>
		OperatingSystem.IsWindows() && Win32GetThreadContext(hThread, lpContext);

	private static bool CloseHandle(nint hObject) =>
		OperatingSystem.IsWindows() && Win32CloseHandle(hObject);

	[DllImport("kernel32.dll", EntryPoint = "TlsAlloc")]
	private static extern uint Win32TlsAlloc();

	[DllImport("kernel32.dll", EntryPoint = "TlsFree")]
	private static extern bool Win32TlsFree(uint dwTlsIndex);

	[DllImport("kernel32.dll", EntryPoint = "TlsSetValue")]
	private static extern bool Win32TlsSetValue(uint dwTlsIndex, nint lpTlsValue);

	[DllImport("kernel32.dll", EntryPoint = "TlsGetValue")]
	private static extern nint Win32TlsGetValue(uint dwTlsIndex);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern nint GetModuleHandle(string lpModuleName);

	[DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
	private static extern nint GetProcAddress(nint hModule, string procName);

	[DllImport("kernel32.dll", EntryPoint = "AddVectoredExceptionHandler")]
	private unsafe static extern void* Win32AddVectoredExceptionHandler(uint first, IntPtr handler);

	[DllImport("kernel32.dll", EntryPoint = "RemoveVectoredExceptionHandler")]
	private unsafe static extern uint Win32RemoveVectoredExceptionHandler(void* handle);

	[DllImport("kernel32.dll", EntryPoint = "SetUnhandledExceptionFilter")]
	private static extern IntPtr Win32SetUnhandledExceptionFilter(IntPtr lpTopLevelExceptionFilter);

	[DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
	private static extern uint Win32GetCurrentThreadId();

	[DllImport("kernel32.dll", EntryPoint = "GetCurrentThread")]
	private static extern nint Win32GetCurrentThread();

	[DllImport("kernel32.dll", EntryPoint = "SetThreadAffinityMask", SetLastError = true)]
	private static extern nuint Win32SetThreadAffinityMask(nint hThread, nuint dwThreadAffinityMask);

	[DllImport("kernel32.dll", EntryPoint = "OpenThread", SetLastError = true)]
	private static extern nint Win32OpenThread(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwThreadId);

	[DllImport("kernel32.dll", EntryPoint = "SuspendThread", SetLastError = true)]
	private static extern uint Win32SuspendThread(nint hThread);

	[DllImport("kernel32.dll", EntryPoint = "ResumeThread", SetLastError = true)]
	private static extern uint Win32ResumeThread(nint hThread);

	[DllImport("kernel32.dll", EntryPoint = "GetThreadContext", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private unsafe static extern bool Win32GetThreadContext(nint hThread, void* lpContext);

	[DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Win32CloseHandle(nint hObject);

	/// <summary>
	/// Set when <see cref="Dispose"/> intentionally left the native session
	/// state (import trampolines, TLS, exception handlers) alive because guest
	/// worker threads were still executing guest code. The owning runtime must
	/// then keep the guest address space mapped as well; freeing either under a
	/// running worker faults the whole process, which hosts the GUI launcher.
	/// </summary>
	internal bool GuestSessionLeaked { get; private set; }

	private bool WaitForGuestThreadQuiescence(TimeSpan timeout)
	{
		var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
		while (true)
		{
			var busyCount = 0;
			string? busyName = null;
			var now = Stopwatch.GetTimestamp();
			using (LockGate("WaitForGuestThreadQuiescence"))
			{
				foreach (var thread in _guestThreads.Values)
				{
					if (thread.ExecutorActive)
					{
						busyCount++;
						busyName ??= thread.Name;
					}
				}
			}

			if (busyCount == 0)
			{
				return true;
			}

			if (now >= deadline)
			{
				Console.Error.WriteLine(
					$"[LOADER][WARN] {busyCount} guest worker(s) (first: '{busyName}') did not leave guest code " +
					"during shutdown; the native session state stays alive to avoid faulting them.");
				return false;
			}

			Thread.Sleep(5);
		}
	}

	public unsafe void Dispose()
	{
		// Guest workers unwind cooperatively at their next import or block
		// boundary once the forced-exit flag is set. Everything freed below is
		// still reachable from a worker inside guest code, so drain the
		// scheduler first and leak the session rather than fault a straggler.
		_forcedGuestExit = true;
		StopReadyThreadDispatcher();
		StopStallWatchdog();
		if (!WaitForGuestThreadQuiescence(TimeSpan.FromSeconds(5)))
		{
			GuestSessionLeaked = true;
			return;
		}

		ClearGuestThreads();
		if (ReferenceEquals(_posixSignalBackend, this))
		{
			// The signal handlers stay installed (they chain to the previous
			// action when no backend is active), but must stop dispatching
			// into a disposed backend.
			_posixSignalBackend = null;
		}
		ClearImportHandlerTrampolines();
		_importEntries = Array.Empty<ImportStubEntry>();
		_runtimeSymbolsByName.Clear();
		StopReadyThreadDispatcher();
		ResetLazyDlsymStubState();
		StopStallWatchdog();
		if (_exceptionHandler != 0)
		{
			RemoveVectoredExceptionHandler((void*)_exceptionHandler);
			_exceptionHandler = 0;
		}
		if (_rawExceptionHandler != 0)
		{
			RemoveVectoredExceptionHandler((void*)_rawExceptionHandler);
			_rawExceptionHandler = 0;
		}
		if (_rawExceptionHandlerStub != 0)
		{
			VirtualFree((void*)_rawExceptionHandlerStub, 0u, 32768u);
			_rawExceptionHandlerStub = 0;
		}
		if (_exceptionHandlerStub != 0)
		{
			VirtualFree((void*)_exceptionHandlerStub, 0u, 32768u);
			_exceptionHandlerStub = 0;
		}
		if (_unhandledFilterStub != 0)
		{
			SetUnhandledExceptionFilter(0);
			VirtualFree((void*)_unhandledFilterStub, 0u, 32768u);
			_unhandledFilterStub = 0;
		}
		if (_handlerHandle.IsAllocated)
		{
			_handlerHandle.Free();
		}
		if (_unhandledFilterHandle.IsAllocated)
		{
			_unhandledFilterHandle.Free();
		}
		if (_selfHandle.IsAllocated)
		{
			_selfHandle.Free();
			_selfHandlePtr = 0;
		}
		if (_ownedTlsBaseAddress != 0)
		{
			VirtualFree((void*)_ownedTlsBaseAddress, 0u, 32768u);
			_ownedTlsBaseAddress = 0;
		}
		_tlsBaseAddress = 0;
		_ownsTlsBaseAddress = false;
		if (_tlsModuleBases.Count > 0)
		{
			foreach (var (_, num3) in _tlsModuleBases)
			{
				if (num3 != 0)
				{
					VirtualFree((void*)num3, 0u, 32768u);
				}
			}
			_tlsModuleBases.Clear();
		}
		if (_tlsHandlerAddress != 0)
		{
			VirtualFree((void*)_tlsHandlerAddress, 0u, 32768u);
			_tlsHandlerAddress = 0;
		}
		if (_hostRspSlotStorage != 0)
		{
			VirtualFree((void*)_hostRspSlotStorage, 0u, 32768u);
			_hostRspSlotStorage = 0;
		}
		if (_vehManagedEntryLock != 0)
		{
			VirtualFree((void*)_vehManagedEntryLock, 0u, 32768u);
			_vehManagedEntryLock = 0;
		}
		if (_workerAbortStack != 0)
		{
			VirtualFree((void*)_workerAbortStack, 0u, 32768u);
			_workerAbortStack = 0;
		}
		if (_guestTlsBaseTlsIndex != uint.MaxValue)
		{
			TlsFree(_guestTlsBaseTlsIndex);
			_guestTlsBaseTlsIndex = uint.MaxValue;
		}
		if (_hostRspSlotTlsIndex != uint.MaxValue)
		{
			TlsFree(_hostRspSlotTlsIndex);
			_hostRspSlotTlsIndex = uint.MaxValue;
		}
		if (_workerDoneEventTlsIndex != uint.MaxValue)
		{
			TlsFree(_workerDoneEventTlsIndex);
			_workerDoneEventTlsIndex = uint.MaxValue;
		}
		if (_unresolvedReturnStub != 0)
		{
			VirtualFree((void*)_unresolvedReturnStub, 0u, 32768u);
			_unresolvedReturnStub = 0;
		}
		if (_guestReturnStub != 0)
		{
			VirtualFree((void*)_guestReturnStub, 0u, 32768u);
			_guestReturnStub = 0;
		}
		if (_workerAbortStub != 0)
		{
			VirtualFree((void*)_workerAbortStub, 0u, 32768u);
			_workerAbortStub = 0;
		}
		if (_guestContextTransferStub != 0)
		{
			VirtualFree((void*)_guestContextTransferStub, 0u, 32768u);
			_guestContextTransferStub = 0;
		}
		foreach (var frame in _guestContextTransferFrames.Values)
		{
			if (frame != 0)
			{
				NativeMemory.Free((void*)frame);
			}
		}
		_guestContextTransferFrames.Dispose();
		if (_lowIndexedTableScratch != 0)
		{
			VirtualFree((void*)_lowIndexedTableScratch, 0u, 32768u);
			_lowIndexedTableScratch = 0;
		}
		if (_stackGuardCompareScratch != 0)
		{
			VirtualFree((void*)_stackGuardCompareScratch, 0u, 32768u);
			_stackGuardCompareScratch = 0;
		}
		if (_nullObjectStoreScratch != 0)
		{
			VirtualFree((void*)_nullObjectStoreScratch, 0u, 32768u);
			_nullObjectStoreScratch = 0;
		}
		Volatile.Write(ref _globalUnresolvedReturnStub, 0uL);
	}

	private unsafe static void* VirtualAlloc(void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect) =>
		HostMemory.Alloc(lpAddress, dwSize, flAllocationType, flProtect);

	private unsafe static bool VirtualFree(void* lpAddress, nuint dwSize, uint dwFreeType) =>
		HostMemory.Free(lpAddress, dwSize, dwFreeType);

	private unsafe static bool VirtualProtect(void* lpAddress, nuint dwSize, uint flNewProtect, uint* lpflOldProtect)
	{
		var success = HostMemory.Protect(lpAddress, dwSize, flNewProtect, out var oldProtect);
		if (lpflOldProtect != null)
		{
			*lpflOldProtect = oldProtect;
		}

		return success;
	}

	private unsafe static void* GetCurrentProcess() => null;

	private unsafe static bool FlushInstructionCache(void* hProcess, void* lpBaseAddress, nuint dwSize)
	{
		_ = hProcess;
		HostMemory.FlushInstructionCache(lpBaseAddress, dwSize);
		return true;
	}

	private unsafe static nuint VirtualQuery(void* lpAddress, out MEMORY_BASIC_INFORMATION64 lpBuffer, nuint dwLength)
	{
		_ = dwLength;
		var result = HostMemory.Query(lpAddress, out var info);
		lpBuffer = default;
		lpBuffer.BaseAddress = info.BaseAddress;
		lpBuffer.AllocationBase = info.AllocationBase;
		lpBuffer.AllocationProtect = info.AllocationProtect;
		lpBuffer.RegionSize = info.RegionSize;
		lpBuffer.State = info.State;
		lpBuffer.Protect = info.Protect;
		return result;
	}
}
