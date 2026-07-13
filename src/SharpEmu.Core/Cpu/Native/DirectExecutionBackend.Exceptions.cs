// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.Core.Cpu.Disasm;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
	private const ulong LazyCommitWindowBytes = 0x0200_0000UL;
	private static int _lazyCommitTraceCount;

	private unsafe void SetupExceptionHandler()
	{
		if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_RAW_HANDLER"), "1", StringComparison.Ordinal))
		{
			_rawExceptionHandlerStub = CreateExceptionHandlerTrampoline(RawVectoredHandlerPtrManaged);
			if (_rawExceptionHandlerStub == 0)
			{
				throw new InvalidOperationException("Failed to create raw exception handler trampoline");
			}
			_rawExceptionHandler = (nint)AddVectoredExceptionHandler(1u, _rawExceptionHandlerStub);
			Console.Error.WriteLine($"[LOADER][INFO] Raw exception handler installed: 0x{_rawExceptionHandler:X16}");
		}
		else
		{
			Console.Error.WriteLine("[LOADER][INFO] Raw exception handler disabled by SHARPEMU_DISABLE_RAW_HANDLER=1");
		}

		_handlerDelegate = VectoredHandler;
		_handlerHandle = GCHandle.Alloc(_handlerDelegate);
		_exceptionHandlerStub = CreateExceptionHandlerTrampoline(Marshal.GetFunctionPointerForDelegate(_handlerDelegate));
		if (_exceptionHandlerStub == 0)
		{
			throw new InvalidOperationException("Failed to create exception handler trampoline");
		}
		_exceptionHandler = (nint)AddVectoredExceptionHandler(1u, _exceptionHandlerStub);
		Console.Error.WriteLine($"[LOADER][INFO] Exception handler installed: 0x{_exceptionHandler:X16}");

		_unhandledFilterDelegate = UnhandledExceptionFilter;
		_unhandledFilterHandle = GCHandle.Alloc(_unhandledFilterDelegate);
		_unhandledFilterStub = CreateExceptionHandlerTrampoline(Marshal.GetFunctionPointerForDelegate(_unhandledFilterDelegate));
		if (_unhandledFilterStub == 0)
		{
			throw new InvalidOperationException("Failed to create unhandled exception filter trampoline");
		}
		SetUnhandledExceptionFilter(_unhandledFilterStub);
	}

	private unsafe int UnhandledExceptionFilter(void* exceptionInfo)
	{
		try
		{
			EXCEPTION_RECORD* exceptionRecord = ((EXCEPTION_POINTERS*)exceptionInfo)->ExceptionRecord;
			ulong rip = ReadCtxU64(((EXCEPTION_POINTERS*)exceptionInfo)->ContextRecord, 248);
			ulong rsp = ReadCtxU64(((EXCEPTION_POINTERS*)exceptionInfo)->ContextRecord, 152);
			Console.Error.WriteLine("[LOADER][FATAL] Unhandled exception filter fired.");
			Console.Error.WriteLine($"[LOADER][FATAL]   Code: 0x{exceptionRecord->ExceptionCode:X8}");
			Console.Error.WriteLine($"[LOADER][FATAL]   Exception Address: 0x{(ulong)(nint)exceptionRecord->ExceptionAddress:X16}");
			Console.Error.WriteLine($"[LOADER][FATAL]   RIP: 0x{rip:X16}");
			Console.Error.WriteLine($"[LOADER][FATAL]   RSP: 0x{rsp:X16}");
			Console.Error.Flush();
		}
		catch
		{
		}

		return 0;
	}

	private unsafe int VectoredHandler(void* exceptionInfo)
	{
		if (_vectoredHandlerDepth > 0)
		{
			LogNestedVectoredException(exceptionInfo);
			Console.Error.Flush();
			return 0;
		}

		_vectoredHandlerDepth++;
		try
		{
			EXCEPTION_RECORD* exceptionRecord = ((EXCEPTION_POINTERS*)exceptionInfo)->ExceptionRecord;
			uint exceptionCode = exceptionRecord->ExceptionCode;
			uint exceptionFlags = exceptionRecord->ExceptionFlags;
			ulong exceptionAddress = (ulong)exceptionRecord->ExceptionAddress;
			void* contextRecord = ((EXCEPTION_POINTERS*)exceptionInfo)->ContextRecord;
			if (contextRecord == null)
			{
				Console.Error.WriteLine("[LOADER][FATAL] ContextRecord is null!");
				Console.Error.Flush();
				return 0;
			}

			ulong rip = ReadCtxU64(contextRecord, 248);
			ulong rsp = ReadCtxU64(contextRecord, 152);

			// Thread-mode probe: a hardware exception raised while this thread is inside
			// the managed import gateway means the VEH->managed reentry happened from
			// cooperative GC mode — a ReversePInvokeBadTransition candidate.
			if (LogThreadMode && _threadModeGatewayDepth > 0)
			{
				TraceThreadMode(
					$"veh_in_gateway code=0x{exceptionCode:X8} rip=0x{rip:X16} gateway_depth={_threadModeGatewayDepth}");
			}

			if (exceptionCode == 3221225477u && TryHandleLazyCommittedPage(exceptionRecord, rip, rsp))
			{
				return -1;
			}
			if (IsBenignHostDebugException(exceptionCode))
			{
				return -1;
			}
			if (exceptionCode == MSVC_CPP_EXCEPTION)
			{
				return 0;
			}

			switch (exceptionCode)
			{
				case 3221225477u:
					LogAccessViolationTrace(exceptionAddress, exceptionRecord);
					break;
				case 3221226505u:
					{
						ulong p0 = exceptionRecord->NumberParameters >= 1 ? (*exceptionRecord->ExceptionInformation) : 0;
						ulong p1 = exceptionRecord->NumberParameters >= 2 ? exceptionRecord->ExceptionInformation[1] : 0;
						Console.Error.WriteLine($"[LOADER][TRACE] VEH_FASTFAIL code=0x{exceptionCode:X8} ex=0x{exceptionAddress:X16} rip=0x{rip:X16} rsp=0x{rsp:X16} p0=0x{p0:X16} p1=0x{p1:X16}");
						Console.Error.Flush();
						break;
					}
			}

			ulong rax = ReadCtxU64(contextRecord, 120);
			ulong rbx = ReadCtxU64(contextRecord, 144);
			ulong rcx = ReadCtxU64(contextRecord, 128);
			ulong rdx = ReadCtxU64(contextRecord, 136);
			ulong rsi = ReadCtxU64(contextRecord, 168);
			ulong rdi = ReadCtxU64(contextRecord, 176);
			ulong rbp = ReadCtxU64(contextRecord, 160);
			ulong r8 = ReadCtxU64(contextRecord, 184);
			ulong r9 = ReadCtxU64(contextRecord, 192);
			ulong r10 = ReadCtxU64(contextRecord, 200);
			ulong r11 = ReadCtxU64(contextRecord, 208);
			ulong r12 = ReadCtxU64(contextRecord, 216);
			ulong r13 = ReadCtxU64(contextRecord, 224);
			ulong r14 = ReadCtxU64(contextRecord, 232);
			ulong r15 = ReadCtxU64(contextRecord, 240);

			Console.Error.WriteLine("[LOADER][INFO] =========================================");
			Console.Error.WriteLine("[LOADER][INFO] NATIVE EXCEPTION CAUGHT!");
			Console.Error.WriteLine($"[LOADER][INFO]   Code: 0x{exceptionCode:X8}");
			Console.Error.WriteLine($"[LOADER][INFO]   Exception Address: 0x{exceptionAddress:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RIP: 0x{rip:X16}");
			if (TryFormatNearestRuntimeSymbol(rip, out string symbol))
			{
				Console.Error.WriteLine("[LOADER][INFO]   RIP symbol: " + symbol);
			}
			Console.Error.WriteLine($"[LOADER][INFO]   RAX: 0x{rax:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RBX: 0x{rbx:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RCX: 0x{rcx:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RDX: 0x{rdx:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RSI: 0x{rsi:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RDI: 0x{rdi:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RBP: 0x{rbp:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RSP: 0x{rsp:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R8 : 0x{r8:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R9 : 0x{r9:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R10: 0x{r10:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R11: 0x{r11:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R12: 0x{r12:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R13: 0x{r13:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R14: 0x{r14:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R15: 0x{r15:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   Flags: 0x{exceptionFlags:X8}");

			ulong accessType = 0;
			ulong target = 0;
			if (exceptionCode == 3221225477u && exceptionRecord->NumberParameters >= 2)
			{
				accessType = *exceptionRecord->ExceptionInformation;
				target = exceptionRecord->ExceptionInformation[1];
				string accessText = accessType switch
				{
					0uL => "read",
					1uL => "write",
					8uL => "execute",
					_ => $"unknown({accessType})"
				};
				Console.Error.WriteLine("[LOADER][INFO]   AV access: " + accessText);
				Console.Error.WriteLine($"[LOADER][INFO]   AV target: 0x{target:X16}");
				if (VirtualQuery((void*)target, out var mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) != 0)
				{
					Console.Error.WriteLine($"[LOADER][INFO]   AV target region: base=0x{mbi.BaseAddress:X16} size=0x{mbi.RegionSize:X16} state=0x{mbi.State:X08} protect=0x{mbi.Protect:X08}");
				}

			}

			try
			{
				Console.Error.WriteLine("[LOADER][INFO]   Stack qwords (RSP..):");
				for (int i = 0; i < 16; i++)
				{
					ulong stackAddr = rsp + (ulong)(i * 8);
					ulong value = (ulong)Marshal.ReadInt64((nint)stackAddr);
					Console.Error.WriteLine($"[LOADER][INFO]     [rsp+0x{i * 8:X2}] @0x{stackAddr:X16} = 0x{value:X16}");
				}
			}
			catch
			{
				Console.Error.WriteLine("[LOADER][WARNING]   Could not read stack qwords.");
			}

			try
			{
				Console.Error.WriteLine("[LOADER][INFO]   Frame chain (RBP walk):");
				ulong frame = rbp;
				for (int i = 0; i < 12; i++)
				{
					if (frame < 140733193388032L || frame > 140737488355327L)
					{
						break;
					}
					ulong next = (ulong)Marshal.ReadInt64((nint)frame);
					ulong ret = (ulong)Marshal.ReadInt64((nint)(frame + 8));
					string extra = TryFormatNearestRuntimeSymbol(ret, out string retSym) ? $" [{retSym}]" : string.Empty;
					Console.Error.WriteLine($"[LOADER][INFO]     frame#{i}: rbp=0x{frame:X16} ret=0x{ret:X16}{extra} next=0x{next:X16}");
					if (next <= frame)
					{
						break;
					}
					frame = next;
				}
			}
			catch
			{
				Console.Error.WriteLine("[LOADER][WARNING]   Could not walk RBP frame chain.");
			}

			switch (exceptionCode)
			{
				case 3221225477u:
					Console.Error.WriteLine("[LOADER][ERROR]   Type: Access Violation");
					Console.Error.WriteLine("[LOADER][ERROR]   This usually means:");
					Console.Error.WriteLine("[LOADER][ERROR]     - Guest code called an unmapped import");
					Console.Error.WriteLine("[LOADER][ERROR]     - Guest code accessed unmapped memory");
					Console.Error.WriteLine("[LOADER][ERROR]     - Need to implement HLE for this NID");
					try
					{
						byte[] code = new byte[16];
						Marshal.Copy((nint)rip, code, 0, code.Length);
						Console.Error.WriteLine("[LOADER][INFO]   Code at RIP: " + BitConverter.ToString(code).Replace("-", " "));
						if (code[0] == 100)
						{
							Console.Error.WriteLine("[LOADER][ERROR]   Detected FS segment prefix - TLS access not patched!");
						}
						else if (code[0] == 101)
						{
							Console.Error.WriteLine("[LOADER][ERROR]   Detected GS segment prefix - TLS access not patched!");
						}
						else if (code[0] == 197 || code[0] == 196)
						{
							Console.Error.WriteLine("[LOADER][INFO]   Detected AVX instruction - check CPU support!");
							Console.Error.WriteLine($"[LOADER][INFO]   RBP: 0x{rbp:X16} (mod 16 = {rbp % 16})");
							Console.Error.WriteLine($"[LOADER][INFO]   RSP: 0x{rsp:X16} (mod 16 = {rsp % 16})");
						}
						if (rip > 16)
						{
							byte[] before = new byte[16];
							Marshal.Copy((nint)(rip - 16), before, 0, before.Length);
							Console.Error.WriteLine("[LOADER][INFO]   Code before RIP: " + BitConverter.ToString(before).Replace("-", " "));
						}
						if (rip > 32)
						{
							byte[] window = new byte[64];
							Marshal.Copy((nint)(rip - 32), window, 0, window.Length);
							Console.Error.WriteLine("[LOADER][INFO]   Code window [RIP-0x20..]: " + BitConverter.ToString(window).Replace("-", " "));
						}
					}
					catch
					{
						Console.Error.WriteLine("[LOADER][ERROR]   Could not read code at RIP");
					}
					DumpRecentImportTrace();
					DumpGuestDisasmDiagnostics(rip, rbp);
					DumpGuestReferenceDiagnostics();
					DumpGuestPointerWindowDiagnostics();
					break;
				case 2147483651u:
					Console.Error.WriteLine("[LOADER][WARNING]   Type: Breakpoint (int3)");
					Console.Error.WriteLine("[LOADER][WARNING]   Unexpected breakpoint in direct-bridge mode");
					break;
				case 3221225501u:
					Console.Error.WriteLine("[LOADER][INFO]   Type: Illegal Instruction");
					break;
			}

			Console.Error.WriteLine("[LOADER][INFO] =========================================");
			Console.Error.Flush();
			return 0;
		}
		finally
		{
			_vectoredHandlerDepth--;
		}
	}

	private static bool IsBenignHostDebugException(uint exceptionCode)
	{
		return exceptionCode is DBG_PRINTEXCEPTION_C or DBG_PRINTEXCEPTION_WIDE_C or MS_VC_THREADNAME_EXCEPTION;
	}

	private unsafe static void LogNestedVectoredException(void* exceptionInfo)
	{
		int count = Interlocked.Increment(ref _nestedVehTraceCount);
		if (count > 16 && count % 128 != 0)
		{
			return;
		}

		try
		{
			EXCEPTION_POINTERS* pointers = (EXCEPTION_POINTERS*)exceptionInfo;
			EXCEPTION_RECORD* record = pointers->ExceptionRecord;
			void* contextRecord = pointers->ContextRecord;
			ulong rip = contextRecord != null ? ReadCtxU64(contextRecord, 248) : 0;
			ulong rsp = contextRecord != null ? ReadCtxU64(contextRecord, 152) : 0;
			ulong accessType = record->NumberParameters >= 1 ? *record->ExceptionInformation : 0;
			ulong target = record->NumberParameters >= 2 ? record->ExceptionInformation[1] : 0;
			Console.Error.WriteLine(
				$"[LOADER][TRACE] Nested VEH exception#{count}: code=0x{record->ExceptionCode:X8} ex=0x{(ulong)record->ExceptionAddress:X16} rip=0x{rip:X16} rsp=0x{rsp:X16} type={accessType} target=0x{target:X16}; passing through.");
		}
		catch
		{
			Console.Error.WriteLine($"[LOADER][TRACE] Nested VEH exception#{count}; passing through.");
		}
	}

	private unsafe void LogAccessViolationTrace(ulong exceptionAddress, EXCEPTION_RECORD* exceptionRecord)
	{
		ulong accessType = exceptionRecord->NumberParameters >= 1 ? (*exceptionRecord->ExceptionInformation) : 0;
		ulong target = exceptionRecord->NumberParameters >= 2 ? exceptionRecord->ExceptionInformation[1] : 0;
		if (_lastAvTraceRip == exceptionAddress && _lastAvTraceType == accessType && _lastAvTraceTarget == target)
		{
			_lastAvTraceRepeatCount++;
			if (_lastAvTraceRepeatCount > 4 && _lastAvTraceRepeatCount % 128 != 0)
			{
				return;
			}
			Console.Error.WriteLine($"[LOADER][TRACE] VEH_AV repeat#{_lastAvTraceRepeatCount} at 0x{exceptionAddress:X16} type={accessType} target=0x{target:X16}");
			Console.Error.Flush();
			return;
		}

		_lastAvTraceRip = exceptionAddress;
		_lastAvTraceType = accessType;
		_lastAvTraceTarget = target;
		_lastAvTraceRepeatCount = 1;
		Console.Error.WriteLine($"[LOADER][TRACE] VEH_AV first-chance at 0x{exceptionAddress:X16} type={accessType} target=0x{target:X16}");
		Console.Error.Flush();
	}

	private void DumpGuestInstructionStream(string name, ulong startRip, int maxInstructions)
	{
		if (_cpuContext == null || startRip < 0x10000 || maxInstructions <= 0)
		{
			return;
		}

		Console.Error.WriteLine($"[LOADER][INFO]   {name} disasm @0x{startRip:X16}:");
		ulong rip = startRip;
		for (int i = 0; i < maxInstructions; i++)
		{
			if (!IcedDecoder.TryReadGuestBytes(_cpuContext.Memory, rip, maxLen: 15, out var bytes) ||
				!IcedDecoder.TryDecode(rip, bytes, out var instruction))
			{
				Console.Error.WriteLine($"[LOADER][INFO]     0x{rip:X16}: <decode-failed>");
				break;
			}

			Console.Error.WriteLine(
				$"[LOADER][INFO]     0x{instruction.Rip:X16}: {instruction.Text} bytes={IcedDecoder.FormatBytes(instruction.Bytes)}");
			rip += (ulong)instruction.Length;
		}
	}

	private void DumpGuestDisasmDiagnostics(ulong rip, ulong rbp)
	{
		if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_DISASM"), "1", StringComparison.Ordinal))
		{
			return;
		}

		if (rip >= 0x20)
		{
			DumpGuestInstructionStream("fault-prelude", rip - 0x20, 24);
		}

		try
		{
			ulong frame = rbp;
			for (int i = 0; i < 3; i++)
			{
				if (frame < 140733193388032L || frame > 140737488355327L)
				{
					break;
				}

				ulong ret = (ulong)Marshal.ReadInt64((nint)(frame + 8));
				if (ret >= 0x40)
				{
					DumpGuestInstructionStream($"frame#{i}-ret-prelude", ret - 0x40, 24);
				}

				ulong next = (ulong)Marshal.ReadInt64((nint)frame);
				if (next <= frame)
				{
					break;
				}

				frame = next;
			}
		}
		catch
		{
			Console.Error.WriteLine("[LOADER][WARNING]   Could not dump disasm diagnostics.");
		}

		var extraAddresses = Environment.GetEnvironmentVariable("SHARPEMU_LOG_DISASM_ADDRS");
		if (string.IsNullOrWhiteSpace(extraAddresses))
		{
			return;
		}

		foreach (var token in extraAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var normalized = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				? token[2..]
				: token;
			if (!ulong.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address) || address < 0x20)
			{
				continue;
			}

			DumpGuestInstructionStream($"extra-0x{address:X16}", address, 48);
		}
	}

	private unsafe void DumpGuestReferenceDiagnostics()
	{
		var targetList = ParseDiagnosticAddresses(Environment.GetEnvironmentVariable("SHARPEMU_LOG_REFSCAN_ADDRS"));
		if (targetList.Count == 0 || _cpuContext == null)
		{
			return;
		}

		const ulong scanBase = 0x0000000800000000UL;
		const ulong scanEnd = 0x0000000810000000UL;
		const int maxHitsPerTarget = 24;

		Console.Error.WriteLine(
			$"[LOADER][INFO]   Ref scan targets: {string.Join(", ", targetList.ConvertAll(static addr => $"0x{addr:X16}"))}");

		var hitCounts = new Dictionary<ulong, int>(targetList.Count);
		for (var i = 0; i < targetList.Count; i++)
		{
			hitCounts[targetList[i]] = 0;
		}

		ulong address = scanBase;
		while (address < scanEnd)
		{
			if (VirtualQuery((void*)address, out var mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) == 0)
			{
				break;
			}

			ulong regionBase = mbi.BaseAddress;
			ulong regionEnd = regionBase + mbi.RegionSize;
			if (regionEnd <= address)
			{
				break;
			}

			if (mbi.State == MEM_COMMIT &&
				IsReadableProtection(mbi.Protect) &&
				IsExecutableProtection(mbi.Protect))
			{
				ScanExecutableRegionForTargetReferences(regionBase, regionEnd, targetList, hitCounts, maxHitsPerTarget);
			}

			var allTargetsSatisfied = true;
			for (var i = 0; i < targetList.Count; i++)
			{
				if (hitCounts[targetList[i]] < maxHitsPerTarget)
				{
					allTargetsSatisfied = false;
					break;
				}
			}

			if (allTargetsSatisfied)
			{
				break;
			}

			address = regionEnd;
		}

		for (var i = 0; i < targetList.Count; i++)
		{
			var target = targetList[i];
			if (!hitCounts.TryGetValue(target, out var count) || count == 0)
			{
				Console.Error.WriteLine($"[LOADER][INFO]   Ref scan 0x{target:X16}: none");
			}
		}
	}

	private void DumpGuestPointerWindowDiagnostics()
	{
		var targetList = ParseDiagnosticAddresses(Environment.GetEnvironmentVariable("SHARPEMU_LOG_POINTER_WINDOWS"));
		if (targetList.Count == 0)
		{
			return;
		}

		var windowSize = 0x80;
		var rawWindowSize = Environment.GetEnvironmentVariable("SHARPEMU_LOG_POINTER_WINDOW_SIZE");
		if (!string.IsNullOrWhiteSpace(rawWindowSize))
		{
			var normalized = rawWindowSize.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				? rawWindowSize[2..]
				: rawWindowSize;
			if (int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedWindowSize) &&
				parsedWindowSize > 0)
			{
				windowSize = parsedWindowSize;
			}
		}

		foreach (var target in targetList)
		{
			DumpPointerWindow($"ptrwin-0x{target:X16}", target, windowSize);
		}
	}

	private void ScanExecutableRegionForTargetReferences(
		ulong regionBase,
		ulong regionEnd,
		IReadOnlyList<ulong> targets,
		IDictionary<ulong, int> hitCounts,
		int maxHitsPerTarget)
	{
		if (_cpuContext == null || regionEnd <= regionBase)
		{
			return;
		}

		ulong rip = regionBase;
		while (rip < regionEnd)
		{
			if (!IcedDecoder.TryReadGuestBytes(_cpuContext.Memory, rip, maxLen: 15, out var bytes) ||
				!IcedDecoder.TryDecode(rip, bytes, out var instruction) ||
				instruction.Length <= 0)
			{
				rip++;
				continue;
			}

			if (instruction.MemoryAddress is { } memoryAddress)
			{
				for (var i = 0; i < targets.Count; i++)
				{
					var target = targets[i];
					if (memoryAddress != target ||
						!hitCounts.TryGetValue(target, out var count) ||
						count >= maxHitsPerTarget)
					{
						continue;
					}

					hitCounts[target] = count + 1;
					Console.Error.WriteLine(
						$"[LOADER][INFO]   Ref scan hit target=0x{target:X16} rip=0x{instruction.Rip:X16} text={instruction.Text} bytes={IcedDecoder.FormatBytes(instruction.Bytes)}");
				}
			}

			rip += (ulong)instruction.Length;
		}
	}

	private static List<ulong> ParseDiagnosticAddresses(string? rawValue)
	{
		var result = new List<ulong>();
		if (string.IsNullOrWhiteSpace(rawValue))
		{
			return result;
		}

		foreach (var token in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var normalized = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				? token[2..]
				: token;
			if (!ulong.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
			{
				continue;
			}

			if (!result.Contains(address))
			{
				result.Add(address);
			}
		}

		return result;
	}

	private void DumpUnresolvedSentinelWindow(string name, ulong baseAddress, int size)
	{
		if (baseAddress < 0x10000 || size <= 0)
		{
			return;
		}

		ulong scanStart = baseAddress;
		ulong scanEnd = baseAddress + (ulong)size;
		List<ulong> hits = ScanSuspiciousResolverPointers(scanStart, scanEnd);
		if (hits.Count == 0)
		{
			Console.Error.WriteLine($"[LOADER][INFO]   {name} unresolved scan: none");
			return;
		}

		Console.Error.WriteLine($"[LOADER][INFO]   {name} unresolved scan hits: {hits.Count}");
		for (int i = 0; i < hits.Count && i < 8; i++)
		{
			ulong slotAddress = hits[i];
			if (TryReadQword(slotAddress, out var value))
			{
				Console.Error.WriteLine($"[LOADER][INFO]     hit#{i}: slot=0x{slotAddress:X16} value=0x{value:X16}");
			}
		}
	}

	private void DumpSentinelPatternWindow(string name, ulong baseAddress, int size)
	{
		if (_cpuContext == null || baseAddress < 0x10000 || size <= 0)
		{
			return;
		}

		byte[] buffer = new byte[size];
		if (!_cpuContext.Memory.TryRead(baseAddress, buffer))
		{
			Console.Error.WriteLine($"[LOADER][INFO]   {name} sentinel-pattern scan: unreadable");
			return;
		}

		List<string> hits = new();
		for (int offset = 0; offset + 2 <= buffer.Length; offset++)
		{
			if (BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2)) == 0xFFFE)
			{
				hits.Add($"+0x{offset:X}:u16");
			}

			if (offset + 4 <= buffer.Length &&
				BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4)) == 0xFFFFFFFEu)
			{
				hits.Add($"+0x{offset:X}:u32");
			}

			if (offset + 8 <= buffer.Length &&
				BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, 8)) == 0xFFFFFFFFFFFFFFFEuL)
			{
				hits.Add($"+0x{offset:X}:u64");
			}
		}

		if (hits.Count == 0)
		{
			Console.Error.WriteLine($"[LOADER][INFO]   {name} sentinel-pattern scan: none");
			return;
		}

		Console.Error.WriteLine($"[LOADER][INFO]   {name} sentinel-pattern hits: {string.Join(", ", hits.GetRange(0, Math.Min(hits.Count, 12)))}");
	}

	private void DumpReturnTargetCandidates(ulong rsp)
	{
		if (rsp < 0x10000)
		{
			return;
		}

		ulong start = rsp >= 0x10 ? rsp - 0x10 : rsp;
		Console.Error.WriteLine($"[LOADER][INFO]   Return-target candidates near RSP=0x{rsp:X16}:");
		for (int offset = 0; offset <= 0x20; offset++)
		{
			ulong address = start + (ulong)offset;
			try
			{
				ulong value = (ulong)Marshal.ReadInt64((nint)address);
				Console.Error.WriteLine($"[LOADER][INFO]     [0x{address:X16}] -> 0x{value:X16}");
			}
			catch
			{
				Console.Error.WriteLine($"[LOADER][INFO]     [0x{address:X16}] -> <unreadable>");
				break;
			}
		}
	}

	private void DumpObjectFieldTargets(string name, ulong objectAddress, int[] offsets, int windowSize)
	{
		if (objectAddress < 0x10000 || offsets.Length == 0)
		{
			return;
		}

		foreach (int offset in offsets)
		{
			ulong slotAddress = objectAddress + (ulong)offset;
			if (!TryReadQword(slotAddress, out var target) || target < 0x10000)
			{
				continue;
			}

			Console.Error.WriteLine($"[LOADER][INFO]   {name}+0x{offset:X2} target = {FormatPointerWithNearestSymbol(target)}");
			DumpPointerWindow($"{name}+0x{offset:X2}", target, windowSize);
			DumpUnresolvedSentinelWindow($"{name}+0x{offset:X2}", target, 0x80);
		}
	}

	private void DumpSuspiciousGlobalSlots()
	{
		DumpAbsoluteSlot("callback_slot[0x80293BD08]", 0x000000080293BD08uL);
		DumpAbsoluteSlot("callback_arg[0x8030FBBE8]", 0x00000008030FBBE8uL);
		DumpAbsoluteSlot("tsc_freq_global[0x8030FD590]", 0x00000008030FD590uL);
		DumpAbsoluteSlot("tsc_base_global[0x8030FD598]", 0x00000008030FD598uL);
		DumpAbsoluteSlot("plt_got[0x8028F6100]", 0x00000008028F6100uL);
		DumpAbsoluteSlot("plt_got[0x8028F6158]", 0x00000008028F6158uL);
		DumpAbsoluteSlot("plt_got[0x8028F6160]", 0x00000008028F6160uL);
		DumpAbsoluteSlot("plt_got[0x8028F64C0]", 0x00000008028F64C0uL);
		DumpAbsoluteSlot("plt_got[0x8028F64C8]", 0x00000008028F64C8uL);
		DumpAbsoluteSlot("plt_got[0x8028F6590]", 0x00000008028F6590uL);
		DumpAbsoluteSlot("plt_got[0x8028F6708]", 0x00000008028F6708uL);
		DumpUnresolvedSentinelWindow("PLT-GOT", 0x00000008028F6100uL, 0x700);
	}

	private void DumpAbsoluteSlot(string name, ulong slotAddress)
	{
		if (!TryReadQword(slotAddress, out var value))
		{
			Console.Error.WriteLine($"[LOADER][INFO]   {name} @0x{slotAddress:X16} = <unreadable>");
			return;
		}

		Console.Error.WriteLine($"[LOADER][INFO]   {name} @0x{slotAddress:X16} = {FormatPointerWithNearestSymbol(value)}");
	}
	private void DumpPointerWindow(string name, ulong baseAddress, int size)
	{
		if (baseAddress < 0x10000 || size <= 0)
		{
			return;
		}

		Console.Error.WriteLine($"[LOADER][INFO]   {name} window @0x{baseAddress:X16}:");
		for (int offset = 0; offset < size; offset += 8)
		{
			ulong slotAddress = baseAddress + (ulong)offset;
			if (!TryReadQword(slotAddress, out var value))
			{
				Console.Error.WriteLine($"[LOADER][INFO]     +0x{offset:X2}: <unreadable>");
				break;
			}

			Console.Error.WriteLine($"[LOADER][INFO]     +0x{offset:X2}: {FormatPointerWithNearestSymbol(value)}");
		}
	}

	private unsafe bool TryReadQword(ulong address, out ulong value)
	{
		value = 0;
		if (address < 0x10000)
		{
			return false;
		}

		if (VirtualQuery((void*)address, out var mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) == 0)
		{
			return false;
		}

		ulong regionEnd = mbi.BaseAddress + mbi.RegionSize;
		if (mbi.State != MEM_COMMIT || !IsReadableProtection(mbi.Protect) || regionEnd <= address || address > regionEnd - 8)
		{
			return false;
		}

		try
		{
			value = (ulong)Marshal.ReadInt64((nint)address);
			return true;
		}
		catch
		{
			value = 0;
			return false;
		}
	}

	private string FormatPointerWithNearestSymbol(ulong value)
	{
		string text = $"0x{value:X16}";
		if (TryFormatNearestRuntimeSymbol(value, out string symbol))
		{
			text += $" [{symbol}]";
		}

		return text;
	}

	private void InitializeRuntimeSymbolIndex(IReadOnlyDictionary<string, ulong> runtimeSymbols)
	{
		_runtimeSymbolsByName.Clear();
		if (runtimeSymbols.Count == 0)
		{
			_runtimeSymbolsByAddress = Array.Empty<KeyValuePair<string, ulong>>();
			return;
		}

		List<KeyValuePair<string, ulong>> list = new(runtimeSymbols.Count);
		foreach (KeyValuePair<string, ulong> runtimeSymbol in runtimeSymbols)
		{
			if (runtimeSymbol.Value != 0L && !string.IsNullOrWhiteSpace(runtimeSymbol.Key))
			{
				list.Add(runtimeSymbol);
				_runtimeSymbolsByName[runtimeSymbol.Key] = runtimeSymbol.Value;
			}
		}

		list.Sort((a, b) => a.Value.CompareTo(b.Value));
		_runtimeSymbolsByAddress = list.ToArray();
	}

	private bool TryFormatNearestRuntimeSymbol(ulong address, out string text)
	{
		text = string.Empty;
		KeyValuePair<string, ulong>[] runtimeSymbolsByAddress = _runtimeSymbolsByAddress;
		if (runtimeSymbolsByAddress.Length == 0)
		{
			return false;
		}

		int low = 0;
		int high = runtimeSymbolsByAddress.Length - 1;
		int best = -1;
		while (low <= high)
		{
			int mid = low + ((high - low) >> 1);
			ulong value = runtimeSymbolsByAddress[mid].Value;
			if (value <= address)
			{
				best = mid;
				low = mid + 1;
			}
			else
			{
				high = mid - 1;
			}
		}

		if (best < 0)
		{
			return false;
		}

		KeyValuePair<string, ulong> symbol = runtimeSymbolsByAddress[best];
		ulong delta = address - symbol.Value;
		text = delta == 0
			? $"{symbol.Key} (0x{symbol.Value:X16})"
			: $"{symbol.Key}+0x{delta:X} (0x{symbol.Value:X16})";
		return true;
	}

	private unsafe bool TryHandleLazyCommittedPage(EXCEPTION_RECORD* exceptionRecord, ulong rip, ulong rsp)
	{
		if (exceptionRecord->NumberParameters < 2)
		{
			return false;
		}

		ulong accessType = *exceptionRecord->ExceptionInformation;
		ulong faultAddress = exceptionRecord->ExceptionInformation[1];
		if (accessType == 8 && faultAddress < 4294967296L)
		{
			return false;
		}
		if (faultAddress < 65536 || faultAddress >= 140737488355328L)
		{
			return false;
		}
		if (!IsGuestOwnedLazyCommitAddress(faultAddress, out var owner))
		{
			return false;
		}
		if (VirtualQuery((void*)faultAddress, out var mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) == 0)
		{
			return false;
		}

		ulong pageBase = faultAddress & 0xFFFFFFFFFFFFF000uL;
		uint commitProtect = ResolveLazyCommitProtection(accessType, mbi.AllocationProtect);
		int traceIndex = Interlocked.Increment(ref _lazyCommitTraceCount);
		bool traceLazyCommit = ShouldTraceLazyCommit(traceIndex);
		if (traceLazyCommit)
		{
			Console.Error.WriteLine($"[LOADER][TRACE] lazy-query#{traceIndex}: fault=0x{faultAddress:X16} owner={owner} rip=0x{rip:X16} rsp=0x{rsp:X16} state=0x{mbi.State:X08} base=0x{mbi.BaseAddress:X16} size=0x{mbi.RegionSize:X16} alloc=0x{mbi.AllocationProtect:X08} prot=0x{mbi.Protect:X08}");
		}

		if (mbi.State == 4096 && IsAccessCompatible(accessType, mbi.Protect))
		{
			if (traceLazyCommit)
			{
				Console.Error.WriteLine($"[LOADER][TRACE] lazy-commit-race#{traceIndex}: fault=0x{faultAddress:X16} protect=0x{mbi.Protect:X08}");
			}
			return true;
		}

		bool committed = false;
		ulong committedBase = 0;
		ulong committedSize = 0;

		if (mbi.State == 65536)
		{
			if (TryGetLazyCommitWindow(faultAddress, mbi.BaseAddress, mbi.RegionSize, out var windowBase, out var windowSize) &&
				TryReserveThenCommit(windowBase, windowSize, windowBase, windowSize, commitProtect))
			{
				committed = true;
				committedBase = windowBase;
				committedSize = windowSize;
			}
			else
			{
				ulong largeBase = faultAddress & 0xFFFFFFFFFFE00000uL;
				if (TryReserveThenCommit(largeBase, 2097152uL, largeBase, 2097152uL, commitProtect))
				{
					committed = true;
					committedBase = largeBase;
					committedSize = 2097152uL;
				}
			}

			if (!committed)
			{
				ulong region64kBase = faultAddress & 0xFFFFFFFFFFFF0000uL;
				if (TryReserveThenCommit(region64kBase, 65536uL, region64kBase, 65536uL, commitProtect))
				{
					committed = true;
					committedBase = region64kBase;
					committedSize = 65536uL;
				}
				else if (TryReserveThenCommit(pageBase, 4096uL, pageBase, 4096uL, commitProtect))
				{
					committed = true;
					committedBase = pageBase;
					committedSize = 4096uL;
				}
			}

			if (!committed)
			{
				return false;
			}

			TryCommitRange(pageBase + 4096, 4096uL, commitProtect);
			if (traceLazyCommit)
			{
				Console.Error.WriteLine($"[LOADER][TRACE] lazy-reserve-commit#{traceIndex}: addr=0x{committedBase:X16} size=0x{committedSize:X16} access={accessType} protect=0x{commitProtect:X8}");
			}
			return true;
		}

		if (mbi.State != 8192)
		{
			return false;
		}

		if (TryGetLazyCommitWindow(faultAddress, mbi.BaseAddress, mbi.RegionSize, out var commitWindowBase, out var commitWindowSize) &&
			TryCommitRange(commitWindowBase, commitWindowSize, commitProtect))
		{
			committed = true;
			committedBase = commitWindowBase;
			committedSize = commitWindowSize;
		}
		else
		{
			ulong largeCommitBase = faultAddress & 0xFFFFFFFFFFE00000uL;
			if (TryCommitRange(largeCommitBase, 2097152uL, commitProtect))
			{
				committed = true;
				committedBase = largeCommitBase;
				committedSize = 2097152uL;
			}
		}

		if (!committed)
		{
			ulong region64kBase = faultAddress & 0xFFFFFFFFFFFF0000uL;
			if (TryCommitRange(region64kBase, 65536uL, commitProtect))
			{
				committed = true;
				committedBase = region64kBase;
				committedSize = 65536uL;
			}
			else if (TryCommitRange(pageBase, 8192uL, commitProtect))
			{
				committed = true;
				committedBase = pageBase;
				committedSize = 8192uL;
			}
			else if (TryCommitRange(pageBase, 4096uL, commitProtect))
			{
				committed = true;
				committedBase = pageBase;
				committedSize = 4096uL;
			}
		}

		if (!committed)
		{
			return false;
		}

		TryCommitRange(pageBase + 4096, 4096uL, commitProtect);
		if (traceLazyCommit)
		{
			Console.Error.WriteLine($"[LOADER][TRACE] lazy-commit#{traceIndex}: addr=0x{committedBase:X16} size=0x{committedSize:X16} access={accessType} protect=0x{commitProtect:X8}");
		}
		return true;

		static bool TryGetLazyCommitWindow(ulong fault, ulong regionBase, ulong regionSize, out ulong baseAddress, out ulong length)
		{
			baseAddress = 0;
			length = 0;
			if (regionSize == 0 || ulong.MaxValue - regionBase < regionSize)
			{
				return false;
			}

			ulong regionEnd = regionBase + regionSize;
			ulong windowBase = fault & ~(LazyCommitWindowBytes - 1);
			if (windowBase < regionBase)
			{
				windowBase = regionBase;
			}

			if (windowBase >= regionEnd)
			{
				return false;
			}

			ulong windowEnd = Math.Min(regionEnd, windowBase + LazyCommitWindowBytes);
			ulong windowSize = windowEnd - windowBase;
			windowSize &= 0xFFFFFFFFFFFFF000uL;
			if (windowSize == 0)
			{
				return false;
			}

			baseAddress = windowBase;
			length = windowSize;
			return true;
		}

		static unsafe bool TryCommitRange(ulong baseAddress, ulong length, uint protection)
		{
			if (length == 0)
			{
				return false;
			}
			return VirtualAlloc((void*)baseAddress, (nuint)length, 4096u, protection) != null;
		}

		static unsafe bool TryReserveRange(ulong baseAddress, ulong length)
		{
			if (length == 0)
			{
				return false;
			}
			return VirtualAlloc((void*)baseAddress, (nuint)length, 8192u, 4u) != null;
		}

		static bool TryReserveThenCommit(ulong reserveAddress, ulong reserveSize, ulong commitAddress, ulong commitSize, uint protection)
		{
			if (!TryReserveRange(reserveAddress, reserveSize))
			{
				return false;
			}
			return TryCommitRange(commitAddress, commitSize, protection);
		}

		static bool IsAccessCompatible(ulong accessType, uint protection)
		{
			const uint pageNoAccess = 0x01;
			const uint pageReadOnly = 0x02;
			const uint pageReadWrite = 0x04;
			const uint pageWriteCopy = 0x08;
			const uint pageExecute = 0x10;
			const uint pageExecuteRead = 0x20;
			const uint pageExecuteReadWrite = 0x40;
			const uint pageExecuteWriteCopy = 0x80;
			const uint pageGuard = 0x100;
			const uint accessMask = 0xFF;

			if ((protection & pageGuard) != 0)
			{
				return false;
			}

			uint access = protection & accessMask;
			if (access == pageNoAccess)
			{
				return false;
			}

			return accessType switch
			{
				0 => access is pageReadOnly or pageReadWrite or pageWriteCopy or pageExecuteRead or pageExecuteReadWrite or pageExecuteWriteCopy,
				1 => access is pageReadWrite or pageWriteCopy or pageExecuteReadWrite or pageExecuteWriteCopy,
				8 => access is pageExecute or pageExecuteRead or pageExecuteReadWrite or pageExecuteWriteCopy,
				_ => false
			};
		}
	}

	private static bool ShouldTraceLazyCommit(int traceIndex)
	{
		if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_LAZY_COMMIT"), "1", StringComparison.Ordinal))
		{
			return true;
		}

		return traceIndex <= 16 || traceIndex % 256 == 0;
	}

	private static uint ResolveLazyCommitProtection(ulong accessType, uint allocationProtect)
	{
		if (accessType == 8 || (allocationProtect & 0xF0) != 0)
		{
			return 64u;
		}
		return 4u;
	}
}
