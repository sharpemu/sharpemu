// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SharpEmu.Core.Cpu;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
	// The native import trampoline keeps the original guest GPR stack layout at
	// argPackPtr and stores volatile SysV-only state immediately below it.  This
	// lets the managed gateway observe AL (the variadic vector-argument count)
	// and all eight vector argument registers without changing the return-slot
	// offsets used by the guest scheduler.
	private const int ImportSavedRaxOffset = -176;
	private const int ImportSavedR10Offset = -168;
	private const int ImportSavedR11Offset = -160;
	private const int ImportSavedMxcsrOffset = -152;
	private const int ImportSavedFpuControlOffset = -148;
	private const int ImportSavedXmmOffset = -128;
	private const int ImportVectorRegisterCount = 8;
	private const ulong StackCheckGuardValue = 0xC0DEC0DECAFEBA00UL;
	private static long _canaryReturnRecoveries;

	private readonly object _importResultLogSampleGate = new();
	private readonly Dictionary<string, int> _importResultLogSamples = new(StringComparer.Ordinal);
	private int _il2CppExceptionDiagnosticCount;

	private static ulong ImportDispatchGatewayManaged(nint backendHandle, int importIndex, nint argPackPtr)
	{
		try
		{
			if (!(GCHandle.FromIntPtr(backendHandle).Target is DirectExecutionBackend directExecutionBackend))
			{
				Console.Error.WriteLine(
					$"[LOADER][ERROR] ImportDispatchGatewayManaged: invalid backend handle 0x{backendHandle:X16}");
				return 18446744071562199042uL;
			}

			if (_perfHleHistogram)
			{
				var startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
				var r = directExecutionBackend.DispatchImport(importIndex, argPackPtr);
				RecordPerfHleDispatchTime(System.Diagnostics.Stopwatch.GetTimestamp() - startTicks);
				return r;
			}

			return directExecutionBackend.DispatchImport(importIndex, argPackPtr);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine(
				$"[LOADER][ERROR] ImportDispatchGatewayManaged exception: {ex.GetType().Name}: {ex.Message}");
			return 18446744071562199298uL;
		}
	}

	private unsafe static int RawVectoredHandlerManaged(void* exceptionInfo)
	{
		return TryRecoverUnresolvedSentinel(exceptionInfo);
	}

	private unsafe static int RawUnhandledFilterManaged(void* exceptionInfo)
	{
		return TryRecoverUnresolvedSentinel(exceptionInfo);
	}

	private unsafe static int TryRecoverUnresolvedSentinel(void* exceptionInfo)
	{
		EXCEPTION_RECORD* exceptionRecord = ((EXCEPTION_POINTERS*)exceptionInfo)->ExceptionRecord;
		if (exceptionRecord->ExceptionCode != 3221225477u)
		{
			return 0;
		}
		void* contextRecord = ((EXCEPTION_POINTERS*)exceptionInfo)->ContextRecord;
		ulong value = ReadCtxU64(contextRecord, 248);
		ulong value2 = (ulong)exceptionRecord->ExceptionAddress;
		if (value == StackCheckGuardValue && TryRecoverCanaryReturn(contextRecord))
		{
			return -1;
		}
		if (!IsUnresolvedSentinel(value) && !IsUnresolvedSentinel(value2))
		{
			return 0;
		}
		ulong rsp = ReadCtxU64(contextRecord, 152);
		WriteCtxU64(contextRecord, 120, 0uL);
		if (TryGetPlausibleReturnFromStack(rsp, out var returnRip, out var nextRsp))
		{
			WriteCtxU64(contextRecord, 152, nextRsp);
			WriteCtxU64(contextRecord, 248, returnRip);
			Interlocked.Increment(ref _rawSentinelRecoveries);
			return -1;
		}
		return 0;
	}

	private unsafe static bool TryRecoverCanaryReturn(void* contextRecord)
	{
		var rsp = ReadCtxU64(contextRecord, CTX_RSP);
		var interruptedReturn = ReadCtxU64(contextRecord, CTX_RBP);
		var interruptedFrame = rsp + 0x18;
		if (!IsLikelyReturnAddress(interruptedReturn) ||
			rsp < sizeof(ulong) ||
			!TryReadStackU64(interruptedFrame, out var callerRbp) ||
			!TryReadStackU64(rsp + 0x20, out var callerReturn) ||
			callerRbp <= rsp || callerRbp - rsp > 0x10000 ||
			!IsLikelyReturnAddress(callerReturn))
		{
			return false;
		}

		// The guest unwind reached this callback return one stack slot late: the
		// final pop loaded the interrupted return into rbp and ret consumed the
		// stack guard. Resume at that return with the outer frame pointer rebuilt
		// from the still-intact caller frame on the stack.
		WriteCtxU64(contextRecord, CTX_RBP, interruptedFrame);
		WriteCtxU64(contextRecord, CTX_RSP, rsp - sizeof(ulong));
		WriteCtxU64(contextRecord, CTX_RIP, interruptedReturn);
		var recoveryCount = Interlocked.Increment(ref _canaryReturnRecoveries);
		if (recoveryCount <= 4 || recoveryCount % 1000 == 0)
		{
			Console.Error.WriteLine(
				$"[LOADER][WARN] Recovered malformed canary return #{recoveryCount}: " +
				$"resume=0x{interruptedReturn:X16} rsp=0x{rsp - sizeof(ulong):X16} " +
				$"rbp=0x{interruptedFrame:X16} caller_rbp=0x{callerRbp:X16} " +
				$"caller=0x{callerReturn:X16}");
			Console.Error.Flush();
		}
		return true;
	}

	private unsafe ulong DispatchImport(int importIndex, nint argPackPtr)
	{
		long num = NextImportDispatchIndex();
		if ((num & 0x3F) == 0)
		{
			MarkExecutionProgress();
		}
		var cpuContext = ActiveCpuContext;
		if (cpuContext == null)
		{
			LastError = "Import dispatch called without active CPU context";
			return 18446744071562199298uL;
		}
		if ((uint)importIndex >= (uint)_importEntries.Length)
		{
			LastError = $"Import dispatch index out of range: {importIndex}";
			return 18446744071562199042uL;
		}
		ImportStubEntry importStubEntry = _importEntries[importIndex];
		if (_perfHleHistogram)
		{
			RecordPerfHleCall(importStubEntry.Export?.Name ?? importStubEntry.Nid);
		}
		int num2 = Volatile.Read(in _rawSentinelRecoveries);
		if (num2 != _lastReportedRawSentinelRecoveries)
		{
			Console.Error.WriteLine($"[LOADER][TRACE] Raw sentinel recoveries: {num2} (last import index={importIndex})");
			_lastReportedRawSentinelRecoveries = num2;
		}
		if (importStubEntry.IsLeaf &&
			TryDispatchLeafImport(cpuContext, importStubEntry, argPackPtr, num, out var leafResult))
		{
			return leafResult;
		}

		cpuContext.Rip = importStubEntry.Address;
		LoadImportVolatileArguments(cpuContext, argPackPtr);
		cpuContext[CpuRegister.Rdi] = *(ulong*)argPackPtr;
		cpuContext[CpuRegister.Rsi] = *(ulong*)(argPackPtr + 8);
		cpuContext[CpuRegister.Rdx] = *(ulong*)(argPackPtr + 16);
		cpuContext[CpuRegister.Rcx] = *(ulong*)(argPackPtr + 24);
		cpuContext[CpuRegister.R8] = *(ulong*)(argPackPtr + 32);
		cpuContext[CpuRegister.R9] = *(ulong*)(argPackPtr + 40);
		cpuContext[CpuRegister.Rbx] = *(ulong*)(argPackPtr + 48);
		cpuContext[CpuRegister.Rbp] = *(ulong*)(argPackPtr + 56);
		cpuContext[CpuRegister.R12] = *(ulong*)(argPackPtr + 64);
		cpuContext[CpuRegister.R13] = *(ulong*)(argPackPtr + 72);
		cpuContext[CpuRegister.R14] = *(ulong*)(argPackPtr + 80);
		cpuContext[CpuRegister.R15] = *(ulong*)(argPackPtr + 88);
		cpuContext[CpuRegister.Rsp] = (ulong)argPackPtr + 96uL;
		ulong value = cpuContext[CpuRegister.Rdi];
		ulong value2 = cpuContext[CpuRegister.Rsi];
		ulong num3 = cpuContext[CpuRegister.Rdx];
		ulong num4 = cpuContext[CpuRegister.Rcx];
		ulong num5 = cpuContext[CpuRegister.R8];
		ulong num6 = cpuContext[CpuRegister.R9];
		ulong value3 = cpuContext[CpuRegister.Rbx];
		ulong value4 = cpuContext[CpuRegister.Rbp];
		ulong value5 = cpuContext[CpuRegister.R12];
		ulong value6 = cpuContext[CpuRegister.R13];
		ulong value7 = cpuContext[CpuRegister.R14];
		ulong value8 = cpuContext[CpuRegister.R15];
		ulong num7 = *(ulong*)(argPackPtr + 96);
		var importStackPointer = (ulong)argPackPtr + 96;
		var probeTarget = (_probeImportReturnAddress != 0 && num7 == _probeImportReturnAddress) ||
			(string.Equals(importStubEntry.Nid, "2Z+PpY6CaJg", StringComparison.Ordinal) &&
			 importStackPointer >= 0x00006FFFAC1FF000UL &&
			 importStackPointer < 0x00006FFFAC200000UL);
		if (probeTarget &&
			Interlocked.Increment(ref _probeImportReturnAddressCount) <= 2048)
		{
			var frameValue = TryReadStackU64(value4, out var savedRbp) ? savedRbp : 0;
			var frameReturn = TryReadStackU64(value4 + sizeof(ulong), out var savedReturn)
				? savedReturn
				: 0;
			Console.Error.WriteLine(
				$"[LOADER][TRACE] import-return-address-probe " +
				$"thread=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} " +
				$"nid={importStubEntry.Nid} ret=0x{num7:X16} " +
				$"rsp=0x{(ulong)argPackPtr + 96:X16} rbp=0x{value4:X16} " +
				$"saved_rbp=0x{frameValue:X16} saved_ret=0x{frameReturn:X16}");
		}
		var isGuestWorker = GuestThreadExecution.IsGuestThread;
		if (!IsLikelyReturnAddress(num7))
		{
			for (int i = 1; i <= 4; i++)
			{
				ulong num8 = *(ulong*)(argPackPtr + 96 + i * 8);
				if (IsLikelyReturnAddress(num8))
				{
					*(ulong*)(argPackPtr + 96) = num8;
					num7 = num8;
					Console.Error.WriteLine($"[LOADER][WARNING] Import#{num}: corrected suspicious return RIP using stack slot +0x{i * 8:X} -> 0x{num7:X16}");
					break;
				}
			}
		}
		// Diagnostic compatibility escape hatch for a guest stack-protector
		// failure whose noreturn call is immediately followed by UD2.  Returning
		// normally from the HLE export would execute that UD2; redirect this one
		// well-known compiler epilogue back through its register/stack unwind.
		// Keep the byte-pattern check strict so the opt-in cannot guess at an
		// unrelated function layout.
		if (string.Equals(importStubEntry.Nid, "Ou3iL1abvng", StringComparison.Ordinal) &&
			string.Equals(
				Environment.GetEnvironmentVariable("SHARPEMU_IGNORE_STACK_CHK"),
				"1",
				StringComparison.Ordinal) &&
			num7 >= 0x20)
		{
			var returnCode = (byte*)num7;
			if (returnCode[0] == 0x0F && returnCode[1] == 0x0B &&
				returnCode[-22] == 0x75 && returnCode[-21] == 0x0F &&
				returnCode[-20] == 0x48 && returnCode[-19] == 0x83 &&
				returnCode[-18] == 0xC4)
			{
				var recoveredReturn = num7 - 20;
				*(ulong*)(argPackPtr + 96) = recoveredReturn;
				cpuContext[CpuRegister.Rax] = 0;
				Console.Error.WriteLine(
					$"[LOADER][WARN] Recovered guest stack-check epilogue " +
					$"ret=0x{num7:X16} -> 0x{recoveredReturn:X16}");
				return 0;
			}
		}
		if (_activeGuestThreadState is { } activeGuestThreadState)
		{
			Interlocked.Increment(ref activeGuestThreadState.ImportCount);
			activeGuestThreadState.LastImportRdi = value;
			activeGuestThreadState.LastImportRsi = value2;
			activeGuestThreadState.LastImportRdx = num3;
			activeGuestThreadState.LastImportRcx = num4;
			activeGuestThreadState.LastImportR8 = num5;
			activeGuestThreadState.LastImportR9 = num6;
			activeGuestThreadState.LastImportStack0 = ReadImportStackArgument(argPackPtr, 0);
			activeGuestThreadState.LastImportStack1 = ReadImportStackArgument(argPackPtr, 1);
			activeGuestThreadState.LastImportStack2 = ReadImportStackArgument(argPackPtr, 2);
			activeGuestThreadState.LastImportStack3 = ReadImportStackArgument(argPackPtr, 3);
			activeGuestThreadState.LastImportStack4 = ReadImportStackArgument(argPackPtr, 4);
			activeGuestThreadState.LastImportStack5 = ReadImportStackArgument(argPackPtr, 5);
			Volatile.Write(ref activeGuestThreadState.LastImportResultValid, 0);
			Volatile.Write(ref activeGuestThreadState.LastReturnRip, num7);
			// Publish the NID last so readers cannot pair a new import name with
			// the preceding import's argument snapshot.
			Volatile.Write(ref activeGuestThreadState.LastImportNid, importStubEntry.Nid);
		}
		if (_logStrlenBursts)
		{
			TrackDistinctImportNid(importStubEntry.Nid);
			TrackStrlenPrelude(importStubEntry.Nid, num, num7);
		}
		if (!string.IsNullOrWhiteSpace(_probeImportReturn) &&
			(string.Equals(_probeImportReturn, "*", StringComparison.Ordinal) ||
			 string.Equals(_probeImportReturn, importStubEntry.Nid, StringComparison.Ordinal)) &&
			Interlocked.Increment(ref _probeImportReturnAddressCount) <= 8)
		{
			ProbeReturnRip(num7, num);
		}
		if (_logGuestContext)
		{
			TraceGuestContext(
				$"import dispatch={num} nid={importStubEntry.Nid} ret=0x{num7:X16} managed={Environment.CurrentManagedThreadId} guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} fiber=0x{GuestThreadExecution.CurrentFiberAddress:X16} active={HasActiveExecutionThread}");
		}
		if (_logBootstrap && string.Equals(importStubEntry.Nid, RuntimeStubNids.BootstrapBridge, StringComparison.Ordinal))
		{
			string symbolText = "<unreadable>";
			if (TryReadAsciiZ(value2, 256, out var sym))
			{
				symbolText = sym;
			}
			Console.Error.WriteLine(
				$"[LOADER][TRACE] bootstrap_call#{num}: op=0x{value:X16} sym_ptr=0x{value2:X16} sym='{symbolText}' out_ptr=0x{num3:X16} ret=0x{num7:X16}");
		}
		if (!isGuestWorker &&
			!ActiveForcedGuestExit &&
			ShouldForceGuestExitOnImportLoop(in importStubEntry, num7, num, value, value2) &&
			TryForceGuestExitToHostStub(argPackPtr, num, num7, importStubEntry.Nid))
		{
			cpuContext[CpuRegister.Rax] = 1uL;
			return 1uL;
		}
		bool flag0 = importStubEntry.SuppressStrlenTrace;
		bool flag = num7 >= 2156221920u && num7 <= 2156225024u;
		bool flag2 = num7 >= 2156351360u && num7 <= 2156352080u;
		bool flag3 = num >= 1020 && num <= 1040;
		bool flag4 = !string.IsNullOrWhiteSpace(_importFilter);
		bool flag5 = false;
		ExportedFunction? matchedExport = importStubEntry.Export;
		bool periodicTrace = num <= 128 ||
			(num >= 240 && num <= 400) ||
			(num >= 900 && num <= 1300) ||
			num % 100000 == 0L ||
			(importStubEntry.Nid == "tsvEmnenz48" && (num <= 256 || num % 1000 == 0L)) ||
			(importStubEntry.Nid == "rTXw65xmLIA" && (num <= 256 || num % 128 == 0)) ||
			flag ||
			flag2 ||
			flag3;
		if (matchedExport is not null)
		{
			if (flag4)
			{
				flag5 = matchedExport.LibraryName.Contains(_importFilter!, StringComparison.OrdinalIgnoreCase)
					|| matchedExport.Name.Contains(_importFilter!, StringComparison.OrdinalIgnoreCase)
					|| importStubEntry.Nid.Contains(_importFilter!, StringComparison.OrdinalIgnoreCase);
			}
		}
		else if (flag4)
		{
			flag5 = importStubEntry.Nid.Contains(_importFilter!, StringComparison.OrdinalIgnoreCase);
		}
		bool flag6 = _logAllImports || flag5;
		if (!flag0 && (flag6 || periodicTrace))
		{
			if (matchedExport != null)
			{
				if (flag6)
				{
					Console.Error.WriteLine(
						$"[LOADER][TRACE] Import#{num}: {matchedExport.LibraryName}:{matchedExport.Name} ({importStubEntry.Nid}) " +
						$"rdi=0x{value:X16} rsi=0x{value2:X16} rdx=0x{num3:X16} rcx=0x{num4:X16} ret=0x{num7:X16}");
				}
				else
				{
					Console.Error.WriteLine($"[LOADER][TRACE] Import#{num}: {matchedExport.LibraryName}:{matchedExport.Name} ({importStubEntry.Nid})");
				}
			}
			else
			{
				if (flag6)
				{
					Console.Error.WriteLine(
						$"[LOADER][TRACE] Import#{num}: {importStubEntry.Nid} " +
						$"rdi=0x{value:X16} rsi=0x{value2:X16} rdx=0x{num3:X16} rcx=0x{num4:X16} ret=0x{num7:X16}");
				}
				else
				{
					Console.Error.WriteLine($"[LOADER][TRACE] Import#{num}: {importStubEntry.Nid}");
				}
			}
			if (flag6)
			{
				Console.Error.Flush();
			}
		}
		if (!flag0 && !isGuestWorker)
		{
			RecordRecentImportTrace(
				num,
				importStubEntry.Nid,
				num7,
				cpuContext[CpuRegister.Rdi],
				cpuContext[CpuRegister.Rsi],
				cpuContext[CpuRegister.Rdx]);
		}
		if (importStubEntry.Nid == "8zTFvBIAIN8" && num <= 256)
		{
			Console.Error.WriteLine($"[LOADER][TRACE] memset#{num}: dst=0x{cpuContext[CpuRegister.Rdi]:X16} val=0x{cpuContext[CpuRegister.Rsi] & 0xFF:X2} len=0x{cpuContext[CpuRegister.Rdx]:X16} ret=0x{num7:X16}");
		}
		if (importStubEntry.Nid == "tsvEmnenz48" && num <= 64)
		{
			Console.Error.WriteLine($"[LOADER][TRACE] __cxa_atexit#{num}: func=0x{cpuContext[CpuRegister.Rdi]:X16} arg=0x{cpuContext[CpuRegister.Rsi]:X16} dso=0x{cpuContext[CpuRegister.Rdx]:X16} ret=0x{num7:X16}");
		}
		if (importStubEntry.Nid == "bzQExy189ZI" || importStubEntry.Nid == "8G2LB+A3rzg")
		{
			Console.Error.WriteLine($"[LOADER][TRACE] {importStubEntry.Nid}#{num}: rdi=0x{cpuContext[CpuRegister.Rdi]:X16} rsi=0x{cpuContext[CpuRegister.Rsi]:X16} rdx=0x{cpuContext[CpuRegister.Rdx]:X16} ret=0x{num7:X16}");
		}
		if (flag6 || flag || flag2 || flag3)
		{
			Console.Error.WriteLine($"[LOADER][TRACE] ImportCtx#{num}: nid={importStubEntry.Nid} ret=0x{num7:X16} rdi=0x{cpuContext[CpuRegister.Rdi]:X16} rsi=0x{cpuContext[CpuRegister.Rsi]:X16} rdx=0x{cpuContext[CpuRegister.Rdx]:X16} rcx=0x{cpuContext[CpuRegister.Rcx]:X16}");
			if (flag6)
			{
				Console.Error.WriteLine(
					$"[LOADER][TRACE] ImportArgs#{num}: " +
					$"r8=0x{cpuContext[CpuRegister.R8]:X16} r9=0x{cpuContext[CpuRegister.R9]:X16} " +
					$"stack0=0x{ReadImportStackArgument(argPackPtr, 0):X16} " +
					$"stack1=0x{ReadImportStackArgument(argPackPtr, 1):X16} " +
					$"stack2=0x{ReadImportStackArgument(argPackPtr, 2):X16} " +
					$"stack3=0x{ReadImportStackArgument(argPackPtr, 3):X16} " +
					$"stack4=0x{ReadImportStackArgument(argPackPtr, 4):X16} " +
					$"stack5=0x{ReadImportStackArgument(argPackPtr, 5):X16}");
			}
			Console.Error.WriteLine($"[LOADER][TRACE] ImportNV#{num}: rbx=0x{value3:X16} rbp=0x{value4:X16} r12=0x{value5:X16} r13=0x{value6:X16} r14=0x{value7:X16} r15=0x{value8:X16}");
			if (flag3)
			{
				ulong num9 = cpuContext[CpuRegister.Rsp];
				if (cpuContext.TryReadUInt64(num9, out var value9) && cpuContext.TryReadUInt64(num9 + 8, out var value10) && cpuContext.TryReadUInt64(num9 + 16, out var value11) && cpuContext.TryReadUInt64(num9 + 24, out var value12) && cpuContext.TryReadUInt64(num9 + 32, out var value13) && cpuContext.TryReadUInt64(num9 + 40, out var value14) && cpuContext.TryReadUInt64(num9 + 48, out var value15) && cpuContext.TryReadUInt64(num9 + 56, out var value16) && cpuContext.TryReadUInt64(num9 + 64, out var value17))
				{
					Console.Error.WriteLine($"[LOADER][TRACE] ImportStackHead#{num}: rsp=0x{num9:X16} [0]=0x{value9:X16} [20]=0x{value13:X16} [40]=0x{value17:X16}");
					Console.Error.WriteLine($"[LOADER][TRACE] ImportStack#{num}: rsp=0x{num9:X16} [0]=0x{value9:X16} [8]=0x{value10:X16} [10]=0x{value11:X16} [18]=0x{value12:X16} [20]=0x{value13:X16} [28]=0x{value14:X16} [30]=0x{value15:X16} [38]=0x{value16:X16} [40]=0x{value17:X16}");
				}
			}
			if (flag6 && _logImportFrames)
			{
				TraceImportFrameChain(cpuContext, num);
			}
			if (flag6 && _logImportRecent)
			{
				DumpRecentImportTrace();
			}
			if (flag3)
			{
				Console.Error.Flush();
			}
		}
		if (importStubEntry.Nid == "Ou3iL1abvng")
		{
			if (_logStackCheck)
			{
				var rbxGuardKnown = TryReadUInt64Compat(value3, out var rbxGuardValue);
				var r12GuardKnown = TryReadUInt64Compat(value5, out var r12GuardValue);
				Console.Error.WriteLine(
					$"[LOADER][TRACE] stack_chk_diag#{num}: ret=0x{num7:X16} " +
					$"rbx=0x{value3:X16}:{(rbxGuardKnown ? $"0x{rbxGuardValue:X16}" : "?")} " +
					$"r12=0x{value5:X16}:{(r12GuardKnown ? $"0x{r12GuardValue:X16}" : "?")} " +
					$"rbp=0x{value4:X16} rsp=0x{((ulong)argPackPtr + 96uL):X16}");

				// Stack-protector layouts vary by compiler and function. Emit the
				// bounded caller-frame tail instead of assuming a fixed canary
				// offset; this also exposes an adjacent ABI output buffer overwrite.
				for (var frameOffset = 0x10; frameOffset <= 0x80; frameOffset += sizeof(ulong))
				{
					var slotAddress = value4 >= (ulong)frameOffset ? value4 - (ulong)frameOffset : 0;
					if (slotAddress != 0 && TryReadUInt64Compat(slotAddress, out var slotValue))
					{
						Console.Error.WriteLine(
							$"[LOADER][TRACE] stack_chk_frame#{num}: [rbp-0x{frameOffset:X}]=0x{slotValue:X16}");
					}
				}
			}
			try
			{
				byte[] array = new byte[64];
				Marshal.Copy((nint)(num7 - 32), array, 0, array.Length);
				Console.Error.WriteLine($"[LOADER][TRACE] __stack_chk_fail return-site @0x{num7:X16}: {BitConverter.ToString(array).Replace("-", " ")}");
			}
			catch
			{
			}
		}
		try
		{
			OrbisGen2Result orbisGen2Result;
			bool dispatchResolved = true;
			var previousImportCallFrame = GuestThreadExecution.EnterImportCallFrame(
				num7,
				(ulong)argPackPtr + 104uL,
				ActiveGuestReturnSlotAddress);
			try
			{
				if (string.Equals(importStubEntry.Nid, RuntimeStubNids.BootstrapBridge, StringComparison.Ordinal))
				{
					orbisGen2Result = DispatchBootstrapBridge();
				}
				else if (string.Equals(importStubEntry.Nid, RuntimeStubNids.KernelDynlibDlsym, StringComparison.Ordinal) ||
					string.Equals(importStubEntry.Nid, "LwG8g3niqwA", StringComparison.Ordinal))
				{
					orbisGen2Result = DispatchKernelDynlibDlsym();
				}
				else if (string.Equals(importStubEntry.Nid, "r8mvOaWdi28", StringComparison.Ordinal))
				{
					orbisGen2Result = DispatchIl2CppApiLookupSymbol();
				}
				else if (importStubEntry.Export is { } cachedExport &&
					(cachedExport.Target & cpuContext.TargetGeneration) != 0)
				{
					cpuContext.ClearRaxWriteFlag();
					var returnValue = cachedExport.Function(cpuContext);
					if (!cpuContext.WasRaxWritten)
					{
						cpuContext[CpuRegister.Rax] = unchecked((ulong)returnValue);
					}
					orbisGen2Result = (OrbisGen2Result)returnValue;
				}
				else
				{
					dispatchResolved = false;
					orbisGen2Result = OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
					cpuContext[CpuRegister.Rax] = unchecked((ulong)(int)orbisGen2Result);
				}
			}
			finally
			{
				GuestThreadExecution.RestoreImportCallFrame(previousImportCallFrame);
			}
			DeliverPendingGuestExceptionAtSafePoint(
				cpuContext,
				CaptureImportBoundaryContinuation(cpuContext, argPackPtr, num7));
			StoreImportVectorReturn(cpuContext, argPackPtr);
			if (dispatchResolved &&
				orbisGen2Result == OrbisGen2Result.ORBIS_GEN2_OK &&
				string.Equals(importStubEntry.Nid, "BohYr-F7-is", StringComparison.Ordinal))
			{
				RegisterPrtLazyCommitRange(value2, num3);
			}
			if (!dispatchResolved)
			{
				LastError = "Missing HLE export for NID: " + importStubEntry.Nid;
				if (string.Equals(importStubEntry.Nid, "cfwBSQyr5Ys", StringComparison.Ordinal) &&
					string.Equals(
						Environment.GetEnvironmentVariable("SHARPEMU_LOG_IL2CPP_EXCEPTION"),
						"1",
						StringComparison.Ordinal) &&
					Interlocked.Increment(ref _il2CppExceptionDiagnosticCount) <= 4)
				{
					DumpIl2CppExceptionDiagnostic(cpuContext, value, num7);
				}
				Console.Error.WriteLine(
					$"[LOADER][WARN] Import#{num} unresolved: nid={importStubEntry.Nid} ret=0x{num7:X16} " +
					$"rdi=0x{value:X16} rsi=0x{value2:X16} rdx=0x{num3:X16} rcx=0x{num4:X16} r8=0x{num5:X16} r9=0x{num6:X16}");
				if (importStubEntry.Nid == "L-Q3LEjIbgA")
				{
					string value18 = string.Join(" ", importStubEntry.Nid.Select(delegate (char c)
					{
						int num10 = c;
						return num10.ToString("X2");
					}));
					Console.Error.WriteLine($"[LOADER][WARN] map_direct nid raw len={importStubEntry.Nid.Length} chars=[{value18}]");
					Delegate function;
					bool value19 = _moduleManager.TryGetFunction(importStubEntry.Nid, out function);
					ExportedFunction export2;
					bool value20 = _moduleManager.TryGetExport(importStubEntry.Nid, out export2);
					Console.Error.WriteLine($"[LOADER][WARN] map_direct lookup with import nid: function={value19}, export={value20}");
					Console.Error.WriteLine(_moduleManager.TryGetExport("L-Q3LEjIbgA", out ExportedFunction export3) ? $"[LOADER][WARN] Canonical map_direct exists as {export3.LibraryName}:{export3.Name}, target={export3.Target}, ctx_target={cpuContext.TargetGeneration}" : "[LOADER][WARN] Canonical map_direct export lookup also missing");
				}
			}
			else if (orbisGen2Result != OrbisGen2Result.ORBIS_GEN2_OK)
			{
				if (ShouldLogImportResult(importStubEntry.Nid, orbisGen2Result))
				{
					Console.Error.WriteLine(
						$"[LOADER][WARN] Import#{num} result: {orbisGen2Result} ({importStubEntry.Nid}) " +
						$"rdi=0x{value:X16} rsi=0x{value2:X16} rdx=0x{num3:X16} rcx=0x{num4:X16} ret=0x{num7:X16}");
				}
			}
			cpuContext[CpuRegister.Rbx] = value3;
			cpuContext[CpuRegister.Rbp] = value4;
			cpuContext[CpuRegister.R12] = value5;
			cpuContext[CpuRegister.R13] = value6;
			cpuContext[CpuRegister.R14] = value7;
			cpuContext[CpuRegister.R15] = value8;
			cpuContext[CpuRegister.Rdi] = value;
			cpuContext[CpuRegister.Rsi] = value2;
			if (GuestThreadExecution.TryConsumeCurrentContextTransfer(out var transferTarget))
			{
				if (!TryPrepareGuestContextTransfer(
						transferTarget,
						out var transferFrame,
						out var transferStub,
						out var transferError))
				{
					LastError = transferError ?? "failed to prepare guest context transfer";
					ActiveForcedGuestExit = true;
					cpuContext[CpuRegister.Rax] = 18446744071562199298uL;
					return cpuContext[CpuRegister.Rax];
				}

				*(ulong*)(argPackPtr + 96) = unchecked((ulong)transferStub);
				if (_logFiber)
				{
					Console.Error.WriteLine(
						$"[LOADER][TRACE] fiber.context-transfer rip=0x{transferTarget.Rip:X16} " +
						$"rsp=0x{transferTarget.Rsp:X16} guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} " +
						$"fiber=0x{GuestThreadExecution.CurrentFiberAddress:X16}");
				}

				if (_activeGuestThreadState is { } transferGuestThreadState)
				{
					Volatile.Write(ref transferGuestThreadState.LastImportRax, transferTarget.Rax);
					Volatile.Write(ref transferGuestThreadState.LastImportResultValid, 1);
				}
				return unchecked((ulong)transferFrame);
			}
			if (GuestThreadExecution.TryConsumeCurrentEntryExit(out var exitValue, out var exitReason))
			{
				if (TryCompleteGuestEntryToHostStub(argPackPtr, num, num7, importStubEntry.Nid, exitReason, exitValue))
				{
					cpuContext[CpuRegister.Rax] = exitValue;
				}
				else
				{
					LastError = $"Failed to complete guest entry after {importStubEntry.Nid}: missing host return sentinel";
					cpuContext[CpuRegister.Rax] = 18446744071562199298uL;
				}
			}
			if (GuestThreadExecution.TryConsumeCurrentThreadBlock(
					out var blockReason,
					out var blockContinuation,
					out var hasBlockContinuation,
					out var blockWakeKey,
					out var blockWaiter,
					out var blockDeadlineTimestamp) &&
				TryYieldGuestThreadToHostStub(argPackPtr, num, num7, importStubEntry.Nid, blockReason))
			{
				if (hasBlockContinuation)
				{
					RegisterBlockedGuestThreadContinuation(
						GuestThreadExecution.CurrentGuestThreadHandle,
						blockContinuation,
						blockWakeKey,
						blockWaiter,
						blockDeadlineTimestamp);
				}

				cpuContext[CpuRegister.Rax] = 0uL;
			}
			if (flag || flag2 || flag3)
			{
				Console.Error.WriteLine($"[LOADER][TRACE] ImportRet#{num}: nid={importStubEntry.Nid} result={orbisGen2Result} rax=0x{cpuContext[CpuRegister.Rax]:X16}");
				if (flag3)
				{
					Console.Error.Flush();
				}
			}
			var guestReturnValue = cpuContext[CpuRegister.Rax];
			if (probeTarget)
			{
				ulong finalReturnSlot;
				try
				{
					finalReturnSlot = *(ulong*)(argPackPtr + 96);
				}
				catch
				{
					finalReturnSlot = 0;
				}
				Console.Error.WriteLine(
					$"[LOADER][TRACE] import-return-address-probe-exit " +
					$"thread=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} " +
					$"nid={importStubEntry.Nid} original=0x{num7:X16} " +
					$"final=0x{finalReturnSlot:X16} rsp=0x{(ulong)argPackPtr + 96:X16} " +
					$"yield={ActiveGuestThreadYieldRequested}");
			}
			if (_activeGuestThreadState is { } completedGuestThreadState)
			{
				Volatile.Write(ref completedGuestThreadState.LastImportRax, guestReturnValue);
				Volatile.Write(ref completedGuestThreadState.LastImportResultValid, 1);
			}
			return guestReturnValue;
		}
		catch (Exception ex)
		{
			LastError = $"HLE dispatch error for {importStubEntry.Nid}: {ex.GetType().Name}: {ex.Message}";
			Console.Error.WriteLine($"[LOADER][ERROR] {LastError}");
			Console.Error.WriteLine($"[LOADER][ERROR] {ex.StackTrace}");
			cpuContext[CpuRegister.Rax] = 18446744071562199298uL;
			if (_activeGuestThreadState is { } failedGuestThreadState)
			{
				Volatile.Write(ref failedGuestThreadState.LastImportRax, cpuContext[CpuRegister.Rax]);
				Volatile.Write(ref failedGuestThreadState.LastImportResultValid, 1);
			}
			return cpuContext[CpuRegister.Rax];
		}
	}

	private static void DumpIl2CppExceptionDiagnostic(
		CpuContext cpuContext,
		ulong wrapperAddress,
		ulong returnAddress)
	{
		Console.Error.WriteLine(
			$"[LOADER][TRACE] il2cpp_exception.wrapper=0x{wrapperAddress:X16} " +
			$"ret=0x{returnAddress:X16}");
		Console.Error.WriteLine(
			$"[LOADER][TRACE] il2cpp_exception.registers " +
			$"rbx=0x{cpuContext[CpuRegister.Rbx]:X16} " +
			$"r12=0x{cpuContext[CpuRegister.R12]:X16} " +
			$"r13=0x{cpuContext[CpuRegister.R13]:X16} " +
			$"r14=0x{cpuContext[CpuRegister.R14]:X16} " +
			$"r15=0x{cpuContext[CpuRegister.R15]:X16}");
		if (GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame))
		{
			DumpGuestCodePointers(cpuContext, "stack", frame.ResumeRsp, 0x1000);
		}
		DumpGuestFramePointerChain(cpuContext, cpuContext[CpuRegister.Rbp]);
		if (string.Equals(
				Environment.GetEnvironmentVariable("SHARPEMU_LOG_PS5_USER_SLOTS"),
				"1",
				StringComparison.Ordinal))
		{
			for (var slot = 0; slot < 4; slot++)
			{
				DumpGuestQwords(
					cpuContext,
					$"ps5_user_slot[{slot}]",
					0x0000000801A73110 + (ulong)(slot * 0x51C8),
					0x60);
			}
		}

		if (!TryReadGuestU64(cpuContext, wrapperAddress, out var exceptionAddress))
		{
			Console.Error.WriteLine("[LOADER][TRACE] il2cpp_exception.wrapper unreadable");
			return;
		}

		Console.Error.WriteLine(
			$"[LOADER][TRACE] il2cpp_exception.object=0x{exceptionAddress:X16}");
		DumpGuestQwords(cpuContext, "exception", exceptionAddress, 0x90);

		// Il2CppException begins with Il2CppObject (klass, monitor), followed by
		// trace_ips, inner_ex and message.  Decode both the documented message
		// slot and nearby object pointers because Unity revisions have appended
		// fields without changing the wrapper itself.
		for (var offset = 0; offset <= 0x80; offset += 8)
		{
			if (!TryReadGuestU64(cpuContext, exceptionAddress + (ulong)offset, out var candidate) ||
				candidate < 0x10000)
			{
				continue;
			}

			if (TryReadIl2CppString(cpuContext, candidate, out var text))
			{
				Console.Error.WriteLine(
					$"[LOADER][TRACE] il2cpp_exception.string+0x{offset:X2}=" +
					$"'{text}'");
			}
		}

		if (TryReadGuestU64(cpuContext, exceptionAddress + 0x38, out var traceIpsAddress))
		{
			DumpIl2CppPointerArray(cpuContext, "trace_ips", traceIpsAddress);
		}

		if (TryReadGuestU64(cpuContext, exceptionAddress, out var klassAddress))
		{
			DumpGuestQwords(cpuContext, "exception_class", klassAddress, 0x100);
			for (var offset = 0; offset <= 0xF8; offset += 8)
			{
				if (!TryReadGuestU64(cpuContext, klassAddress + (ulong)offset, out var candidate) ||
					candidate < 0x10000)
				{
					continue;
				}

				if (TryReadGuestCString(cpuContext, candidate, out var text))
				{
					Console.Error.WriteLine(
						$"[LOADER][TRACE] il2cpp_exception.class_string+0x{offset:X2}=" +
						$"'{text}'");
				}
			}
		}
	}

	private static void DumpGuestCodePointers(
		CpuContext cpuContext,
		string label,
		ulong address,
		int byteCount)
	{
		var buffer = new byte[byteCount];
		if (!cpuContext.Memory.TryRead(address, buffer))
		{
			return;
		}

		var logged = 0;
		for (var offset = 0; offset <= buffer.Length - 8 && logged < 128; offset += 8)
		{
			var candidate = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, 8));
			if (candidate < 0x0000000800000000 || candidate >= 0x0000001000000000)
			{
				continue;
			}

			Console.Error.WriteLine(
				$"[LOADER][TRACE] il2cpp_exception.{label}+0x{offset:X3}=0x{candidate:X16}");
			logged++;
		}
	}

	private static void DumpGuestFramePointerChain(CpuContext cpuContext, ulong framePointer)
	{
		for (var depth = 0; depth < 64 && framePointer >= 0x10000; depth++)
		{
			if (!TryReadGuestU64(cpuContext, framePointer, out var nextFrame) ||
				!TryReadGuestU64(cpuContext, framePointer + 8, out var returnRip))
			{
				break;
			}

			Console.Error.WriteLine(
				$"[LOADER][TRACE] il2cpp_exception.frame[{depth}] " +
				$"rbp=0x{framePointer:X16} ret=0x{returnRip:X16}");
			if (framePointer >= 0x40)
			{
				DumpGuestQwords(
					cpuContext,
					$"frame[{depth}]_locals",
					framePointer - 0x40,
					0x60);
				if (depth <= 10 &&
					TryReadGuestU64(cpuContext, framePointer - 0x20, out var savedRbx) &&
					savedRbx >= 0x0000000100000000 &&
					savedRbx < 0x0000000800000000)
				{
					DumpGuestQwords(
						cpuContext,
						$"frame[{depth}]_saved_rbx_object",
						savedRbx,
						0x50);
					if (depth is >= 4 and <= 12)
					{
						DumpIl2CppObjectGraph(
							cpuContext,
							$"frame[{depth}]_saved_rbx",
							savedRbx);
					}
				}
				DumpIl2CppStringsInRange(
					cpuContext,
					$"frame[{depth}]",
					framePointer >= 0x100 ? framePointer - 0x100 : 0,
					0x140);
				DumpIl2CppObjectsInRange(
					cpuContext,
					$"frame[{depth}]",
					framePointer >= 0x100 ? framePointer - 0x100 : 0,
					0x140);
			}
			if (nextFrame <= framePointer || nextFrame - framePointer > 0x100000)
			{
				break;
			}

			framePointer = nextFrame;
		}
	}

	private static void DumpIl2CppObjectsInRange(
		CpuContext cpuContext,
		string label,
		ulong address,
		int byteCount)
	{
		var buffer = new byte[byteCount];
		if (address == 0 || !cpuContext.Memory.TryRead(address, buffer))
		{
			return;
		}

		for (var offset = 0; offset <= buffer.Length - 8; offset += 8)
		{
			var candidate = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, 8));
			if (candidate < 0x0000000100000000 ||
				candidate >= 0x0000000800000000 ||
				!TryReadGuestU64(cpuContext, candidate, out var klass) ||
				klass < 0x0000000100000000 ||
				klass >= 0x0000000800000000 ||
				!TryReadGuestU64(cpuContext, klass + 0x10, out var nameAddress) ||
				!TryReadGuestCString(cpuContext, nameAddress, out var name))
			{
				continue;
			}

			var nameSpace = string.Empty;
			if (TryReadGuestU64(cpuContext, klass + 0x18, out var namespaceAddress))
			{
				_ = TryReadGuestCString(cpuContext, namespaceAddress, out nameSpace);
			}
			Console.Error.WriteLine(
				$"[LOADER][TRACE] il2cpp_exception.{label}_object@{offset:X3}=" +
				$"0x{candidate:X16} {nameSpace}.{name}");
			if (string.Equals(name, "ControllerMap_Editor", StringComparison.Ordinal))
			{
				DumpGuestQwords(cpuContext, $"{label}_controller_map", candidate, 0x40);
				for (var fieldOffset = 0x20; fieldOffset <= 0x28; fieldOffset += 8)
				{
					if (TryReadGuestU64(cpuContext, candidate + (ulong)fieldOffset, out var stringAddress) &&
						TryReadIl2CppString(cpuContext, stringAddress, out var fieldText))
					{
						Console.Error.WriteLine(
							$"[LOADER][TRACE] il2cpp_exception.{label}_controller_map+0x{fieldOffset:X2}=" +
							$"'{fieldText}'");
					}
				}
			}
		}
	}

	private static void DumpIl2CppStringsInRange(
		CpuContext cpuContext,
		string label,
		ulong address,
		int byteCount)
	{
		var buffer = new byte[byteCount];
		if (address == 0 || !cpuContext.Memory.TryRead(address, buffer))
		{
			return;
		}

		for (var offset = 0; offset <= buffer.Length - 8; offset += 8)
		{
			var candidate = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, 8));
			if (candidate < 0x0000000100000000 ||
				candidate >= 0x0000000800000000 ||
				!TryReadIl2CppString(cpuContext, candidate, out var text) ||
				text.Length == 0)
			{
				continue;
			}

			Console.Error.WriteLine(
				$"[LOADER][TRACE] il2cpp_exception.{label}_string@{offset:X3}=" +
				$"0x{candidate:X16} '{text}'");
		}
	}

	private static void DumpIl2CppObjectGraph(
		CpuContext cpuContext,
		string label,
		ulong objectAddress)
	{
		if (!TryReadGuestU64(cpuContext, objectAddress, out var klassAddress))
		{
			return;
		}

		DumpGuestQwords(cpuContext, $"{label}_class", klassAddress, 0x400);
		for (var offset = 0; offset <= 0xF8; offset += 8)
		{
			if (TryReadGuestU64(cpuContext, klassAddress + (ulong)offset, out var candidate) &&
				candidate >= 0x10000 &&
				TryReadGuestCString(cpuContext, candidate, out var text))
			{
				Console.Error.WriteLine(
					$"[LOADER][TRACE] il2cpp_exception.{label}_class_string+0x{offset:X2}='{text}'");
			}
		}
		for (var offset = 0x10; offset <= 0x48; offset += 8)
		{
			if (!TryReadGuestU64(cpuContext, objectAddress + (ulong)offset, out var candidate) ||
				candidate < 0x0000000100000000 ||
				candidate >= 0x0000000800000000)
			{
				continue;
			}

			DumpGuestQwords(
				cpuContext,
				$"{label}_field+0x{offset:X2}",
				candidate,
				0x80);
			if (TryReadGuestU64(cpuContext, candidate, out var candidateClass))
			{
				DumpGuestQwords(
					cpuContext,
					$"{label}_field+0x{offset:X2}_class",
					candidateClass,
					0x400);
				for (var classOffset = 0; classOffset <= 0xF8; classOffset += 8)
				{
					if (TryReadGuestU64(
							cpuContext,
							candidateClass + (ulong)classOffset,
							out var classCandidate) &&
						classCandidate >= 0x10000 &&
						TryReadGuestCString(cpuContext, classCandidate, out var text))
					{
						Console.Error.WriteLine(
							$"[LOADER][TRACE] il2cpp_exception.{label}_field+0x{offset:X2}" +
							$"_class_string+0x{classOffset:X2}='{text}'");
					}
				}
			}
		}
	}

	private static void DumpIl2CppPointerArray(
		CpuContext cpuContext,
		string label,
		ulong address)
	{
		if (address < 0x10000 ||
			!TryReadGuestU64(cpuContext, address + 0x18, out var rawLength))
		{
			return;
		}

		var length = (int)Math.Min(rawLength, 128);
		Console.Error.WriteLine(
			$"[LOADER][TRACE] il2cpp_exception.{label}=0x{address:X16} " +
			$"length={rawLength}");
		for (var index = 0; index < length; index++)
		{
			if (!TryReadGuestU64(cpuContext, address + 0x20 + (ulong)(index * 8), out var value))
			{
				break;
			}

			Console.Error.WriteLine(
				$"[LOADER][TRACE] il2cpp_exception.{label}[{index}]=0x{value:X16}");
		}
	}

	private static void DumpGuestQwords(
		CpuContext cpuContext,
		string label,
		ulong address,
		int byteCount)
	{
		var buffer = new byte[byteCount];
		if (!cpuContext.Memory.TryRead(address, buffer))
		{
			Console.Error.WriteLine(
				$"[LOADER][TRACE] il2cpp_exception.{label}=unreadable@0x{address:X16}");
			return;
		}

		for (var offset = 0; offset < buffer.Length; offset += 32)
		{
			var values = new string[Math.Min(4, (buffer.Length - offset) / 8)];
			for (var i = 0; i < values.Length; i++)
			{
				values[i] = $"{BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset + i * 8, 8)):X16}";
			}

			Console.Error.WriteLine(
				$"[LOADER][TRACE] il2cpp_exception.{label}+0x{offset:X2}: " +
				string.Join(" ", values));
		}
	}

	private static bool TryReadGuestU64(CpuContext cpuContext, ulong address, out ulong value)
	{
		Span<byte> buffer = stackalloc byte[8];
		if (!cpuContext.Memory.TryRead(address, buffer))
		{
			value = 0;
			return false;
		}

		value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
		return true;
	}

	private static bool TryReadIl2CppString(
		CpuContext cpuContext,
		ulong address,
		out string text)
	{
		text = string.Empty;
		Span<byte> header = stackalloc byte[20];
		if (!cpuContext.Memory.TryRead(address, header))
		{
			return false;
		}

		var length = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
		if (length <= 0 || length > 2048)
		{
			return false;
		}

		var bytes = new byte[length * 2];
		if (!cpuContext.Memory.TryRead(address + 20, bytes))
		{
			return false;
		}

		text = SanitizeDiagnosticText(Encoding.Unicode.GetString(bytes));
		return text.Length != 0;
	}

	private static bool TryReadGuestCString(
		CpuContext cpuContext,
		ulong address,
		out string text)
	{
		text = string.Empty;
		var buffer = new byte[256];
		if (!cpuContext.Memory.TryRead(address, buffer))
		{
			return false;
		}

		var length = Array.IndexOf(buffer, (byte)0);
		if (length <= 0)
		{
			return false;
		}

		for (var i = 0; i < length; i++)
		{
			if (buffer[i] is < 0x20 or > 0x7E)
			{
				return false;
			}
		}

		text = Encoding.UTF8.GetString(buffer, 0, length);
		return true;
	}

	private static string SanitizeDiagnosticText(string text)
	{
		var builder = new StringBuilder(Math.Min(text.Length, 512));
		foreach (var character in text)
		{
			if (builder.Length >= 512)
			{
				break;
			}

			builder.Append(char.IsControl(character) ? ' ' : character);
		}

		return builder.ToString().Trim();
	}

	private unsafe static void LoadImportVolatileArguments(CpuContext cpuContext, nint argPackPtr)
	{
		cpuContext[CpuRegister.Rax] = *(ulong*)(argPackPtr + ImportSavedRaxOffset);
		cpuContext[CpuRegister.R10] = *(ulong*)(argPackPtr + ImportSavedR10Offset);
		cpuContext[CpuRegister.R11] = *(ulong*)(argPackPtr + ImportSavedR11Offset);
		cpuContext.Mxcsr = *(uint*)(argPackPtr + ImportSavedMxcsrOffset);
		cpuContext.FpuControlWord = *(ushort*)(argPackPtr + ImportSavedFpuControlOffset);
		for (var registerIndex = 0; registerIndex < ImportVectorRegisterCount; registerIndex++)
		{
			var registerAddress = argPackPtr + ImportSavedXmmOffset + (registerIndex * 16);
			cpuContext.SetXmmRegister(
				registerIndex,
				*(ulong*)registerAddress,
				*(ulong*)(registerAddress + 8));
		}
	}

	private unsafe static void StoreImportVectorReturn(CpuContext cpuContext, nint argPackPtr)
	{
		// AMD64 returns scalar/vector floating-point values in XMM0 and may use
		// XMM1 for the second eightbyte of a classified aggregate.
		for (var registerIndex = 0; registerIndex < 2; registerIndex++)
		{
			cpuContext.GetXmmRegister(registerIndex, out var low, out var high);
			var registerAddress = argPackPtr + ImportSavedXmmOffset + (registerIndex * 16);
			*(ulong*)registerAddress = low;
			*(ulong*)(registerAddress + 8) = high;
		}
	}

	private static ulong ReadImportStackArgument(nint argPackPtr, int index)
	{
		var address = checked((ulong)argPackPtr + 104UL + (ulong)index * sizeof(ulong));
		return TryReadHostQword(address, out var value) ? value : 0;
	}

	private static GuestCpuContinuation CaptureImportBoundaryContinuation(
		CpuContext context,
		nint argPackPtr,
		ulong returnRip) =>
		new(
			Rip: returnRip,
			Rsp: (ulong)argPackPtr + 104UL,
			ReturnSlotAddress: (ulong)argPackPtr + 96UL,
			Rflags: context.Rflags,
			FsBase: context.FsBase,
			GsBase: context.GsBase,
			Rax: context[CpuRegister.Rax],
			Rcx: context[CpuRegister.Rcx],
			Rdx: context[CpuRegister.Rdx],
			Rbx: context[CpuRegister.Rbx],
			Rbp: context[CpuRegister.Rbp],
			Rsi: context[CpuRegister.Rsi],
			Rdi: context[CpuRegister.Rdi],
			R8: context[CpuRegister.R8],
			R9: context[CpuRegister.R9],
			R10: context[CpuRegister.R10],
			R11: context[CpuRegister.R11],
			R12: context[CpuRegister.R12],
			R13: context[CpuRegister.R13],
			R14: context[CpuRegister.R14],
			R15: context[CpuRegister.R15],
			FpuControlWord: context.FpuControlWord,
			Mxcsr: context.Mxcsr,
			RestoreFullFpuState: false);

	private unsafe bool TryDispatchLeafImport(
		CpuContext cpuContext,
		ImportStubEntry importStubEntry,
		nint argPackPtr,
		long dispatchIndex,
		out ulong result)
	{
		result = 0;
		if (importStubEntry.Export is not { } export ||
			(export.Target & cpuContext.TargetGeneration) == 0)
		{
			return false;
		}

		var arg0 = *(ulong*)argPackPtr;
		var returnRip = *(ulong*)(argPackPtr + 96);
		var leafStackPointer = (ulong)argPackPtr + 96UL;
		var probeLeafReturn = _logAllImports &&
			string.Equals(importStubEntry.Nid, "2Z+PpY6CaJg", StringComparison.Ordinal) &&
			leafStackPointer >= 0x00006FFFAC1FF000UL &&
			leafStackPointer < 0x00006FFFAC200000UL;
		if (probeLeafReturn)
		{
			Console.Error.WriteLine(
				$"[LOADER][TRACE] leaf-return-probe-enter nid={importStubEntry.Nid} " +
				$"ret=0x{returnRip:X16} rsp=0x{leafStackPointer:X16} " +
				$"active_slot=0x{ActiveGuestReturnSlotAddress:X16}");
		}
		cpuContext.Rip = importStubEntry.Address;
		LoadImportVolatileArguments(cpuContext, argPackPtr);
		cpuContext[CpuRegister.Rdi] = arg0;
		cpuContext[CpuRegister.Rsi] = *(ulong*)(argPackPtr + 8);
		cpuContext[CpuRegister.Rdx] = *(ulong*)(argPackPtr + 16);
		cpuContext[CpuRegister.Rcx] = *(ulong*)(argPackPtr + 24);
		cpuContext[CpuRegister.R8] = *(ulong*)(argPackPtr + 32);
		cpuContext[CpuRegister.R9] = *(ulong*)(argPackPtr + 40);
		cpuContext[CpuRegister.Rbx] = *(ulong*)(argPackPtr + 48);
		cpuContext[CpuRegister.Rbp] = *(ulong*)(argPackPtr + 56);
		cpuContext[CpuRegister.R12] = *(ulong*)(argPackPtr + 64);
		cpuContext[CpuRegister.R13] = *(ulong*)(argPackPtr + 72);
		cpuContext[CpuRegister.R14] = *(ulong*)(argPackPtr + 80);
		cpuContext[CpuRegister.R15] = *(ulong*)(argPackPtr + 88);
		cpuContext[CpuRegister.Rsp] = (ulong)argPackPtr + 96uL;

		if (_activeGuestThreadState is { } activeGuestThreadState)
		{
			Interlocked.Increment(ref activeGuestThreadState.ImportCount);
			activeGuestThreadState.LastImportRdi = arg0;
			activeGuestThreadState.LastImportRsi = *(ulong*)(argPackPtr + 8);
			activeGuestThreadState.LastImportRdx = *(ulong*)(argPackPtr + 16);
			activeGuestThreadState.LastImportRcx = *(ulong*)(argPackPtr + 24);
			activeGuestThreadState.LastImportR8 = *(ulong*)(argPackPtr + 32);
			activeGuestThreadState.LastImportR9 = *(ulong*)(argPackPtr + 40);
			activeGuestThreadState.LastImportStack0 = ReadImportStackArgument(argPackPtr, 0);
			activeGuestThreadState.LastImportStack1 = ReadImportStackArgument(argPackPtr, 1);
			activeGuestThreadState.LastImportStack2 = ReadImportStackArgument(argPackPtr, 2);
			activeGuestThreadState.LastImportStack3 = ReadImportStackArgument(argPackPtr, 3);
			activeGuestThreadState.LastImportStack4 = ReadImportStackArgument(argPackPtr, 4);
			activeGuestThreadState.LastImportStack5 = ReadImportStackArgument(argPackPtr, 5);
			Volatile.Write(ref activeGuestThreadState.LastImportResultValid, 0);
			Volatile.Write(ref activeGuestThreadState.LastReturnRip, returnRip);
			Volatile.Write(ref activeGuestThreadState.LastImportNid, importStubEntry.Nid);
		}
		if (dispatchIndex % 100000 == 0)
		{
			Console.Error.WriteLine(
				$"[LOADER][TRACE] Import#{dispatchIndex}: {export.LibraryName}:{export.Name} ({importStubEntry.Nid}) " +
				$"rdi=0x{arg0:X16} rsi=0x{cpuContext[CpuRegister.Rsi]:X16} " +
				$"rdx=0x{cpuContext[CpuRegister.Rdx]:X16} rcx=0x{cpuContext[CpuRegister.Rcx]:X16} " +
				$"ret=0x{returnRip:X16}");
		}

		int returnValue;
		if (importStubEntry.IsNoBlockLeaf)
		{
			cpuContext.ClearRaxWriteFlag();
			returnValue = export.Function(cpuContext);
			if (!cpuContext.WasRaxWritten)
			{
				cpuContext[CpuRegister.Rax] = unchecked((ulong)returnValue);
			}
		}
		else
		{
			var previousImportCallFrame = GuestThreadExecution.EnterImportCallFrame(
				returnRip,
				(ulong)argPackPtr + 104uL,
				ActiveGuestReturnSlotAddress);
			try
			{
				cpuContext.ClearRaxWriteFlag();
				returnValue = export.Function(cpuContext);
				if (!cpuContext.WasRaxWritten)
				{
					cpuContext[CpuRegister.Rax] = unchecked((ulong)returnValue);
				}
			}
			finally
			{
				GuestThreadExecution.RestoreImportCallFrame(previousImportCallFrame);
			}
		}
		DeliverPendingGuestExceptionAtSafePoint(
			cpuContext,
			CaptureImportBoundaryContinuation(cpuContext, argPackPtr, returnRip));
		StoreImportVectorReturn(cpuContext, argPackPtr);

		if (returnValue != (int)OrbisGen2Result.ORBIS_GEN2_OK)
		{
			var returnResult = (OrbisGen2Result)returnValue;
			if (ShouldLogImportResult(importStubEntry.Nid, returnResult))
			{
				Console.Error.WriteLine(
					$"[LOADER][WARN] Import#{dispatchIndex} result: {returnResult} ({importStubEntry.Nid}) " +
					$"rdi=0x{arg0:X16} rsi=0x{cpuContext[CpuRegister.Rsi]:X16} " +
					$"rdx=0x{cpuContext[CpuRegister.Rdx]:X16} rcx=0x{cpuContext[CpuRegister.Rcx]:X16} " +
					$"r8=0x{cpuContext[CpuRegister.R8]:X16} r9=0x{cpuContext[CpuRegister.R9]:X16} " +
					$"ret=0x{returnRip:X16}");
			}
		}

		var consumedThreadBlock = GuestThreadExecution.TryConsumeCurrentThreadBlock(
				out var blockReason,
				out var blockContinuation,
				out var hasBlockContinuation,
				out var blockWakeKey,
				out var blockResumeHandler,
				out var blockWakeHandler,
				out var blockDeadlineTimestamp);
		if (consumedThreadBlock &&
			TryYieldGuestThreadToHostStub(argPackPtr, dispatchIndex, returnRip, importStubEntry.Nid, blockReason))
		{
			if (hasBlockContinuation)
			{
				RegisterBlockedGuestThreadContinuation(
					GuestThreadExecution.CurrentGuestThreadHandle,
					blockContinuation,
					blockWakeKey,
					blockResumeHandler,
					blockWakeHandler,
					blockDeadlineTimestamp);
			}

			cpuContext[CpuRegister.Rax] = 0uL;
		}
		if (probeLeafReturn)
		{
			Console.Error.WriteLine(
				$"[LOADER][TRACE] leaf-return-probe-exit nid={importStubEntry.Nid} " +
				$"original=0x{returnRip:X16} final=0x{*(ulong*)(argPackPtr + 96):X16} " +
				$"rsp=0x{leafStackPointer:X16} active_slot=0x{ActiveGuestReturnSlotAddress:X16} " +
				$"block={consumedThreadBlock} yield={ActiveGuestThreadYieldRequested}");
		}

		result = cpuContext[CpuRegister.Rax];
		if (_activeGuestThreadState is { } completedGuestThreadState)
		{
			Volatile.Write(ref completedGuestThreadState.LastImportRax, result);
			Volatile.Write(ref completedGuestThreadState.LastImportResultValid, 1);
		}
		return true;
	}

	private static bool IsNoBlockLeafImport(string nid) =>
		nid is
			"8aI7R7WaOlc" or // sceAmprCommandBufferConstructor
			"a8uLzYY--tM" or // sceAmprAprCommandBufferConstructor
			"Qs1xtplKo0U" or // sceAmprAprCommandBufferDestructor
			"GuchCTefuZw" or // sceAmprCommandBufferDestructor
			"N-FSPA4S3nI" or // sceAmprCommandBufferSetBuffer
			"baQO9ez2gL4" or // sceAmprCommandBufferReset
			"ULvXMDz56po" or // sceAmprCommandBufferClearBuffer
			"mQ16-QdKv7k" or // sceAmprAprCommandBufferReadFile
			"vWU-odnS+fU" or // sceAmprMeasureCommandSizeReadFile
			"sSAUCCU1dv4" or // sceAmprMeasureCommandSizeWriteKernelEventQueue_04_00
			"C+IEj+BsAFM" or // sceAmprMeasureCommandSizeWriteAddressOnCompletion
			"tZDDEo2tE5k" or // sceAmprCommandBufferGetSize
			"GnxKOHEawhk" or // sceAmprCommandBufferGetCurrentOffset
			"gzndltBEzWc" or // sceAmprCommandBufferGetNumCommands
			"H896Pt-yB4I" or // sceAmprCommandBufferWriteKernelEventQueue_04_00
			"sJXyWHjP-F8" or // sceAmprCommandBufferWriteAddressOnCompletion
			"mPpPxv5CZt4" or // sceSystemServiceGetHdrToneMapLuminance
			"1FZBKy8HeNU" or // sceVideoOutGetVblankStatus
			"ASoW5WE-UPo" or // sceKernelAprSubmitCommandBufferAndGetResult
			"rqwFKI4PAiM" or // sceKernelAprWaitCommandBuffer
			"eE4Szl8sil8" or // sceKernelAprSubmitCommandBuffer
			"qvMUCyyaCSI" or // sceKernelAprSubmitCommandBufferAndGetId
			"Q2V+iqvjgC0" or // vsnprintf
			"q1cHNfGycLI" or // scePadRead
			"xk0AcarP3V4" or // scePadOpen
			"yH17Q6NWtVg" or // sceUserServiceGetEvent
			"D-CzAxQL0XI" or // sceUserServiceGetPlatformPrivacySetting
			"K-jXhbt2gn4";   // scePthreadMutexTrylock

	private bool ShouldLogImportResult(string nid, OrbisGen2Result result)
	{
		var resultValue = unchecked((int)result);
		if (resultValue > 0)
		{
			return false;
		}

		var expectedFileProbeMiss =
			result == OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND &&
			IsExpectedFileProbeNotFoundNid(nid);
		var expectedTimedWaitTimeout =
			string.Equals(nid, "27bAgiJmOh0", StringComparison.Ordinal) &&
			unchecked((int)result) == 60;
		var expectedEqueueTimeout =
			string.Equals(nid, "fzyMKs9kim0", StringComparison.Ordinal) &&
			result == OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
		var expectedMutexTrylockBusy =
			string.Equals(nid, "K-jXhbt2gn4", StringComparison.Ordinal) &&
			result == OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
		var expectedNetAcceptWouldBlock =
			string.Equals(nid, "PIWqhn9oSxc", StringComparison.Ordinal) &&
			resultValue == unchecked((int)0x80410123);
		var expectedUserServiceNoEvent =
			string.Equals(nid, "yH17Q6NWtVg", StringComparison.Ordinal) &&
			resultValue == unchecked((int)0x80960007);
		var expectedPrivacyInvalidParameter =
			string.Equals(nid, "D-CzAxQL0XI", StringComparison.Ordinal) &&
			resultValue == unchecked((int)0x80960009);
		if (!expectedFileProbeMiss &&
			!expectedTimedWaitTimeout &&
			!expectedEqueueTimeout &&
			!expectedMutexTrylockBusy &&
			!expectedNetAcceptWouldBlock &&
			!expectedUserServiceNoEvent &&
			!expectedPrivacyInvalidParameter)
		{
			return true;
		}

		if (!ShouldLogExpectedImportResults())
		{
			return false;
		}

		var key = nid + "\0" + resultValue;
		int count;
		lock (_importResultLogSampleGate)
		{
			_importResultLogSamples.TryGetValue(key, out count);
			count++;
			_importResultLogSamples[key] = count;
		}

		return count <= 8 || count % 10000 == 0;
	}

	private static bool ShouldLogExpectedImportResults() =>
		string.Equals(
			Environment.GetEnvironmentVariable("SHARPEMU_LOG_EXPECTED_IMPORT_RESULTS"),
			"1",
			StringComparison.Ordinal);

	private static bool IsExpectedFileProbeNotFoundNid(string nid) =>
		nid is
			"eV9wAD2riIA" or // sceKernelStat
			"1G3lF1Gg1k8" or // sceKernelOpen
			"gEpBkcwxUjw";   // sceKernelAprResolveFilepathsToIdsAndFileSizes

	private bool IsLeafImport(string nid)
	{
		if (nid == "1jfXLRVzisc")
		{
			return !_logUsleep;
		}

		// These leaf operations cannot park the current guest thread. Unlocks may
		// wake another cooperative thread, but the scheduler drain is independent
		// of the caller's import-boundary bookkeeping.
		return nid is
			"tn3VlD0hG60" or // scePthreadMutexUnlock
			"2Z+PpY6CaJg" or // pthread_mutex_unlock
			"EgmLo6EWgso" or // scePthreadRwlockUnlock
			"+L98PIbGttk" or // pthread_rwlock_unlock
			"8aI7R7WaOlc" or // sceAmprCommandBufferConstructor
			"zgXifHT9ErY" or // sceVideoOutIsFlipPending
			"V++UgBtQhn0" or // sceAgcGetDataPacketPayloadAddress
			"qj7QZpgr9Uw" or // Gen5 graphics type-2 packet
			"LtTouSCZjHM" or // sceAgcCbNop
			"k3GhuSNmBLU" or // sceAgcCbDispatch
			"UZbQjYAwwXM" or // sceAgcCbSetShRegistersDirect
			"JrtiDtKeS38" or // sceAgcAcbResetQueue
			"cFazmnXpJOE" or // sceAgcAcbEventWrite
			"KT-hTp-Ch14" or // sceAgcAcbAcquireMem
			"htn36gPnBk4" or // sceAgcAcbWaitRegMem
			"eZ4+17OQz4Q" or // sceAgcAcbWriteData
			"j3EtxFkSIhQ" or // sceAgcAcbDispatchIndirect
			"gSRnr79F8tQ" or // sceAgcDriverSubmitAcb
			"i1jyy49AjXU" or // sceAgcDcbWriteData
			"VmW0Tdpy420" or // sceAgcDcbWaitRegMem
			"WmAc2MEj6Io" or // sceAgcDcbDmaData
			"rUuVjyR+Rd4" or // sceAgcDcbGetLodStatsGetSize
			"vuSXe69VILM" or // sceAgcDcbGetLodStats
			"RmaJwLtc8rY" or // sceAgcDcbSetBaseIndirectArgs
			"CtB+A9-VxO0" or // sceAgcDcbDispatchIndirect
			"+kSrjIVxKFE" or // sceAgcDcbPushMarker
			"H7uZqCoNuWk" or // sceAgcDcbPopMarker
			"IxYiarKlXxM" or // sceAgcDmaDataPatchSetDstAddressOrOffset
			"3KDcnM3lrcU" or // sceAgcWaitRegMemPatchAddress
			"n485EBnIWmk" or // sceAgcWaitRegMemPatchCompareFunction
			"7nOoijNPvEU" or // sceAgcWaitRegMemPatchReference
			"hXAnLgDHCoI" or // sceAgcWaitRegMemPatchMask
			"0fWWK5uG9rQ" or // sceAgcQueueEndOfPipeActionPatchAddress
			"J8YCgfKAMQs" or // sceAgcQueueEndOfPipeActionPatchGcrCntl
			"MlEw1feXcjg" or // sceAgcQueueEndOfPipeActionPatchData
			"T9fjQIINoeE" or // sceAgcQueueEndOfPipeActionPatchType
			"a8uLzYY--tM" or
			"Qs1xtplKo0U" or
			"GuchCTefuZw" or
			"N-FSPA4S3nI" or
			"baQO9ez2gL4" or
			"ULvXMDz56po" or
			"mQ16-QdKv7k" or
			"vWU-odnS+fU" or
			"sSAUCCU1dv4" or
			"C+IEj+BsAFM" or
			"tZDDEo2tE5k" or
			"GnxKOHEawhk" or
			"gzndltBEzWc" or
			"H896Pt-yB4I" or
			"sJXyWHjP-F8" or
			"mPpPxv5CZt4" or
			"1FZBKy8HeNU" or
			"ASoW5WE-UPo" or
			"rqwFKI4PAiM" or
			"eE4Szl8sil8" or
			"qvMUCyyaCSI" or
			"Vo5V8KAwCmk" or // sceSystemServiceHideSplashScreen
			"TywrFKCoLGY" or // sceSaveDataInitialize3
			"dyIhnXq-0SM" or // sceSaveDataDirNameSearch
			"ZP4e7rlzOUk" or // sceSaveDataMount3
			"ERKzksauAJA" or // sceSaveDataDialogGetStatus
			"KK3Bdg1RWK0" or // sceSaveDataDialogUpdateStatus
			"en7gNVnh878" or // sceSaveDataDialogIsReadyToDisplay
			"jO8DM8oyego" or // sceNpEntitlementAccessInitialize
			"TFyU+KFBv54" or // sceNpEntitlementAccessGetAddcontEntitlementInfoList
			"27bAgiJmOh0" or // pthread_cond_timedwait
			"iQw3iQPhvUQ" or // sceNetCtlCheckCallback
			"Q2V+iqvjgC0" or // vsnprintf
			"j4ViWNHEgww" or // strlen
			"5jNubw4vlAA" or // strnlen
			"LHMrG7e8G78" or // wcslen
			"WkkeywLJcgU" or // wcslen
			"Ovb2dSJOAuE" or // strcmp
			"aesyjrHVWy4" or // strncmp
			"pNtJdE3x49E" or // wcscmp
			"fV2xHER+bKE" or // wcscoll
			"E8wCoUEbfzk" or // wcsncmp
			"Q3VBxCXhUHs" or // memcpy
			"+P6FRGH4LfA" or // memmove
			"DfivPArhucg" or // memcmp
			"ytQULN-nhL4" or // pthread_rwlock_init
			"6ULAa0fq4jA" or // scePthreadRwlockInit
			"1471ajPzxh0" or // pthread_rwlock_destroy
			"BB+kb08Tl9A" or // scePthreadRwlockDestroy
			// rwlock rd/wr lock removed (can block); init/destroy/unlock stay.
			"aI+OeCz8xrQ" or // scePthreadSelf
			"EotR8a3ASf4" or // pthread_self
			"eoht7mQOCmo" or // scePthreadGetspecific
			"0-KXaS70xy4" or // pthread_getspecific
			"+BzXYkqYeLE" or // scePthreadSetspecific
			"WrOLvHU0yQM" or // pthread_setspecific
			"vz+pg2zdopI" or // sceKernelGetEventUserData
			"mJ7aghmgvfc" or // sceKernelGetEventId
			"23CPPI1tyBY" or // sceKernelGetEventFilter
			"kwGyyjohI50";   // sceKernelGetEventData
	}

	private long NextImportDispatchIndex()
	{
		if (!ReferenceEquals(_importCounterOwner, this) ||
			_nextImportDispatchIndex >= _importDispatchBlockEnd)
		{
			var blockEnd = Interlocked.Add(ref _importDispatchCount, ImportDispatchBlockSize);
			_importCounterOwner = this;
			_nextImportDispatchIndex = blockEnd - ImportDispatchBlockSize + 1;
			_importDispatchBlockEnd = blockEnd + 1;
		}

		return _nextImportDispatchIndex++;
	}

	private void TraceImportFrameChain(CpuContext context, long dispatchIndex)
	{
		var frame = context[CpuRegister.Rbp];
		for (int i = 0; i < 16; i++)
		{
			if (!context.TryReadUInt64(frame, out var next) ||
				!context.TryReadUInt64(frame + sizeof(ulong), out var returnRip))
			{
				break;
			}

			var symbol = TryFormatNearestRuntimeSymbol(returnRip, out var formatted)
				? $" [{formatted}]"
				: string.Empty;
			Console.Error.WriteLine(
				$"[LOADER][TRACE] ImportFrame#{dispatchIndex}.{i}: rbp=0x{frame:X16} ret=0x{returnRip:X16}{symbol} next=0x{next:X16}");
			if (next <= frame || next - frame > 0x100000)
			{
				break;
			}

			frame = next;
		}
	}

	private unsafe bool TryForceGuestExitToHostStub(nint argPackPtr, long dispatchIndex, ulong returnRip, string nid)
	{
		ulong num = ActiveEntryReturnSentinelRip;
		if (num < 65536 || !TryPatchActiveGuestReturnSlot(num))
		{
			return false;
		}
		try
		{
			*(ulong*)(argPackPtr + 96) = num;
		}
		catch
		{
			return false;
		}
		ActiveForcedGuestExit = true;
		LastError = $"Detected repeating import loop at import#{dispatchIndex} ({nid}) and forced guest exit.";
		Console.Error.WriteLine($"[LOADER][ERROR] Import-loop guard fired at import#{dispatchIndex}: nid={nid} ret=0x{returnRip:X16} -> host_exit=0x{num:X16}");
		DumpRecentImportTrace();
		return true;
	}

	private unsafe bool TryCompleteGuestEntryToHostStub(nint argPackPtr, long dispatchIndex, ulong returnRip, string nid, string reason, ulong value)
	{
		ulong hostExit = ActiveEntryReturnSentinelRip;
		if (hostExit < 65536 || !TryPatchActiveGuestReturnSlot(hostExit))
		{
			return false;
		}
		try
		{
			*(ulong*)(argPackPtr + 96) = hostExit;
		}
		catch
		{
			return false;
		}
		Console.Error.WriteLine(
			$"[LOADER][INFO] Guest entry exit at import#{dispatchIndex}: nid={nid} ret=0x{returnRip:X16} reason={reason} value=0x{value:X16}");
		return true;
	}

	private unsafe bool TryYieldGuestThreadToHostStub(nint argPackPtr, long dispatchIndex, ulong returnRip, string nid, string reason)
	{
		ulong hostExit = ActiveEntryReturnSentinelRip;
		if (hostExit < 65536 || !TryPatchActiveGuestReturnSlot(hostExit))
		{
			return false;
		}
		try
		{
			*(ulong*)(argPackPtr + 96) = hostExit;
		}
		catch
		{
			return false;
		}

		ActiveGuestThreadYieldRequested = true;
		ActiveGuestThreadYieldReason = string.IsNullOrWhiteSpace(reason) ? nid : reason;
		if (_logGuestThreads)
		{
			Console.Error.WriteLine(
				$"[LOADER][INFO] Guest thread yield at import#{dispatchIndex}: nid={nid} ret=0x{returnRip:X16} reason={ActiveGuestThreadYieldReason}");
		}
		return true;
	}

	private bool TryPatchActiveGuestReturnSlot(ulong hostExit)
	{
		ulong returnSlotAddress = ActiveGuestReturnSlotAddress;
		return returnSlotAddress != 0 &&
			ActiveCpuContext is not null &&
			ActiveCpuContext.TryWriteUInt64(returnSlotAddress, hostExit);
	}

	private bool ShouldForceGuestExitOnImportLoop(in ImportStubEntry entry, ulong returnRip, long dispatchIndex, ulong arg0, ulong arg1)
	{
		if (dispatchIndex < 1200)
		{
			return false;
		}
		if (_disableImportLoopGuard || _importLoopGuardSeconds <= 0)
		{
			return false;
		}
		if (entry.IsLoopGuardBoundary)
		{
			ResetImportLoopPattern();
			return false;
		}
		var value = entry.NidHash;
		RecordImportLoopSignature(value, returnRip, BuildImportLoopSignature(value, returnRip, arg0, arg1));
		// The O(period x repeats) pattern scan is a boot/hang watchdog, not a
		// steady-state feature; sampling every 256th dispatch keeps its cost
		// off the hot path while still tripping within a couple of thousand
		// dispatches of a genuine import loop.
		if ((dispatchIndex & 0xFF) != 0)
		{
			return false;
		}
		if (!HasRepeatingImportLoopPattern())
		{
			if (_importLoopPatternHits > 0)
			{
				_importLoopPatternHits--;
			}
			if (_importLoopPatternHits == 0)
			{
				_importLoopPatternStartTimestamp = 0;
			}
			return false;
		}
		if (_importLoopPatternStartTimestamp == 0)
		{
			_importLoopPatternStartTimestamp = Stopwatch.GetTimestamp();
		}
		_importLoopPatternHits++;
		if (_importLoopPatternHits < 6)
		{
			return false;
		}

		var elapsedTicks = Stopwatch.GetTimestamp() - _importLoopPatternStartTimestamp;
		return elapsedTicks >= (long)(_importLoopGuardSeconds * Stopwatch.Frequency);
	}

	private static bool IsImportLoopGuardBoundary(string nid) =>
		nid is
			"1jfXLRVzisc" or // sceKernelUsleep
			"WKAXJ4XBPQ4" or // scePthreadCondWait
			"BmMjYxmew1w" or // scePthreadCondTimedwait
			"Op8TBGY5KHg" or // pthread_cond_wait
			"27bAgiJmOh0";   // pthread_cond_timedwait

	private void ResetImportLoopPattern()
	{
		_importLoopPatternHits = 0;
		_importLoopPatternStartTimestamp = 0;
		_importLoopSignatureCount = 0;
		_importLoopSignatureWriteIndex = 0;
	}

	private static int GetImportLoopGuardSeconds()
	{
		if (int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_IMPORT_LOOP_GUARD_SECONDS"), out var seconds))
		{
			return Math.Max(0, seconds);
		}

		return DefaultImportLoopGuardSeconds;
	}

	private ulong BuildImportLoopSignature(ulong nidHash, ulong returnRip, ulong arg0, ulong arg1)
	{
		ulong num = returnRip >> 2;
		ulong num2 = ((arg0 >> 4) * 11400714819323198485uL) ^ ((arg1 >> 4) * 14029467366897019727uL);
		return num ^ nidHash * 11400714819323198485uL ^ num2;
	}

	private void RecordImportLoopSignature(ulong nidHash, ulong returnRip, ulong signature)
	{
		_importLoopSignatures[_importLoopSignatureWriteIndex] = signature;
		_importLoopNidHashes[_importLoopSignatureWriteIndex] = nidHash;
		_importLoopReturnRips[_importLoopSignatureWriteIndex] = returnRip;
		_importLoopSignatureWriteIndex = (_importLoopSignatureWriteIndex + 1) % _importLoopSignatures.Length;
		if (_importLoopSignatureCount < _importLoopSignatures.Length)
		{
			_importLoopSignatureCount++;
		}
	}

	private bool HasRepeatingImportLoopPattern()
	{
		int num = _importLoopSignatureCount;
		if (num < 96)
		{
			return false;
		}
		int num2 = Math.Min(48, num / 4);
		for (int i = 6; i <= num2; i++)
		{
			if (HasRepeatingImportLoopPattern(i, 4))
			{
				return true;
			}
		}
		return false;
	}

	private bool HasRepeatingImportLoopPattern(int period, int repeats)
	{
		int num = period * repeats;
		if (period <= 0 || repeats < 2 || _importLoopSignatureCount < num)
		{
			return false;
		}
		for (int i = 0; i < period; i++)
		{
			ulong importLoopSignatureFromTail = GetImportLoopSignatureFromTail(i);
			for (int j = 1; j < repeats; j++)
			{
				if (GetImportLoopSignatureFromTail(i + j * period) != importLoopSignatureFromTail)
				{
					return false;
				}
			}
		}
		return IsSevereImportLoopPattern(num);
	}

	private ulong GetImportLoopSignatureFromTail(int offset)
	{
		int num = _importLoopSignatureWriteIndex - 1 - offset;
		while (num < 0)
		{
			num += _importLoopSignatures.Length;
		}
		return _importLoopSignatures[num % _importLoopSignatures.Length];
	}

	private bool IsSevereImportLoopPattern(int sampleCount)
	{
		int num = CountDistinctImportLoopValuesFromTail(_importLoopNidHashes, sampleCount, 3);
		if (num > 2)
		{
			return false;
		}
		int num2 = CountDistinctImportLoopValuesFromTail(_importLoopReturnRips, sampleCount, 3);
		if (num2 > 2)
		{
			return false;
		}
		int num3 = Math.Min(_importLoopSignatureCount, Math.Max(sampleCount * 8, ImportLoopWideDiversityWindow));
		if (num3 <= sampleCount)
		{
			return true;
		}
		if (CountDistinctImportLoopValuesFromTail(_importLoopNidHashes, num3, 3) > 2)
		{
			return false;
		}
		return CountDistinctImportLoopValuesFromTail(_importLoopReturnRips, num3, 3) <= 2;
	}

	private int CountDistinctImportLoopValuesFromTail(ulong[] source, int sampleCount, int stopAfter)
	{
		int num = Math.Min(sampleCount, _importLoopSignatureCount);
		int num2 = 0;
		for (int i = 0; i < num; i++)
		{
			ulong importLoopValueFromTail = GetImportLoopValueFromTail(source, i);
			bool flag = false;
			for (int j = 0; j < i; j++)
			{
				if (GetImportLoopValueFromTail(source, j) == importLoopValueFromTail)
				{
					flag = true;
					break;
				}
			}
			if (!flag && ++num2 >= stopAfter)
			{
				return num2;
			}
		}
		return num2;
	}

	private ulong GetImportLoopValueFromTail(ulong[] source, int offset)
	{
		int num = _importLoopSignatureWriteIndex - 1 - offset;
		while (num < 0)
		{
			num += source.Length;
		}
		return source[num % source.Length];
	}

	private bool ShouldSuppressStrlenTrace(string nid)
	{
		return string.Equals(nid, "j4ViWNHEgww", StringComparison.Ordinal) && !_logStrlenImports;
	}

	private void TrackDistinctImportNid(string nid)
	{
		if (string.IsNullOrWhiteSpace(nid) || string.Equals(_lastDistinctImportNid, nid, StringComparison.Ordinal))
		{
			return;
		}
		_lastDistinctImportNid = nid;
		_distinctImportNidHistory[_distinctImportNidHistoryWriteIndex] = nid;
		_distinctImportNidHistoryWriteIndex = (_distinctImportNidHistoryWriteIndex + 1) % _distinctImportNidHistory.Length;
		if (_distinctImportNidHistoryCount < _distinctImportNidHistory.Length)
		{
			_distinctImportNidHistoryCount++;
		}
	}

	private void TrackStrlenPrelude(string nid, long dispatchIndex, ulong returnRip)
	{
		if (!string.Equals(nid, "j4ViWNHEgww", StringComparison.Ordinal))
		{
			_consecutiveStrlenImports = 0;
			_strlenPreludeLogged = false;
			return;
		}
		_consecutiveStrlenImports++;
		if (_strlenPreludeLogged || _consecutiveStrlenImports < 24)
		{
			return;
		}
		_strlenPreludeLogged = true;
		List<string> list = GetRecentDistinctImportPrelude(maxCount: 5, skipNid: "j4ViWNHEgww");
		if (list.Count == 0)
		{
			Console.Error.WriteLine($"[LOADER][WARNING] Import#{dispatchIndex}: detected strlen burst (count={_consecutiveStrlenImports}) ret=0x{returnRip:X16}; no prelude NIDs recorded.");
			return;
		}
		Console.Error.WriteLine($"[LOADER][WARNING] Import#{dispatchIndex}: detected strlen burst (count={_consecutiveStrlenImports}) ret=0x{returnRip:X16}; last5_nids={string.Join(" -> ", list)}");
	}

	private List<string> GetRecentDistinctImportPrelude(int maxCount, string skipNid)
	{
		List<string> list = new List<string>(maxCount);
		if (maxCount <= 0 || _distinctImportNidHistoryCount == 0)
		{
			return list;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		for (int i = 0; i < _distinctImportNidHistoryCount && list.Count < maxCount; i++)
		{
			int num = _distinctImportNidHistoryWriteIndex - 1 - i;
			while (num < 0)
			{
				num += _distinctImportNidHistory.Length;
			}
			string text = _distinctImportNidHistory[num % _distinctImportNidHistory.Length];
			if (string.IsNullOrWhiteSpace(text) || string.Equals(text, skipNid, StringComparison.Ordinal) || !hashSet.Add(text))
			{
				continue;
			}
			if (_moduleManager.TryGetExport(text, out ExportedFunction export))
			{
				list.Add($"{export.LibraryName}:{export.Name}({text})");
			}
			else
			{
				list.Add(text);
			}
		}
		list.Reverse();
		return list;
	}

	private static ulong StableHash64(string text)
	{
		ulong num = 14695981039346656037uL;
		for (int i = 0; i < text.Length; i++)
		{
			num ^= text[i];
			num *= 1099511628211uL;
		}
		return num;
	}

	private OrbisGen2Result DispatchKernelDynlibDlsym()
	{
		var cpuContext = ActiveCpuContext;
		if (cpuContext == null)
		{
			return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
		}
		ulong symbolNameAddress = cpuContext[CpuRegister.Rsi];
		ulong outputAddress = cpuContext[CpuRegister.Rdx];
		if (!TryReadAsciiZ(symbolNameAddress, 512, out var symbolName))
		{
			cpuContext[CpuRegister.Rax] = 18446744073709551615uL;
			return OrbisGen2Result.ORBIS_GEN2_OK;
		}
		var moduleHandle = unchecked((int)cpuContext[CpuRegister.Rdi]);
		if (!TryResolveModuleSymbolAddress(moduleHandle, symbolName, out var resolvedAddress) &&
			!TryResolveRuntimeSymbolAddress(symbolName, out resolvedAddress) &&
			!TryResolveRuntimeSymbolAddress(ComputePsNid(symbolName), out resolvedAddress) &&
			!TryResolveRuntimeSymbolAlias(symbolName, out resolvedAddress))
		{
			Console.Error.WriteLine(
				$"[LOADER][WARN] sceKernelDlsym failed: handle=0x{cpuContext[CpuRegister.Rdi]:X} symbol='{symbolName}'");
			cpuContext[CpuRegister.Rax] = 18446744073709551615uL;
			return OrbisGen2Result.ORBIS_GEN2_OK;
		}
		if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_DLSYM"), "1", StringComparison.Ordinal))
		{
			Console.Error.WriteLine(
				$"[LOADER][TRACE] sceKernelDlsym: handle=0x{moduleHandle:X} symbol='{symbolName}' -> 0x{resolvedAddress:X16}");
		}
		if (outputAddress == 0L || !TryWriteUInt64Compat(outputAddress, resolvedAddress))
		{
			cpuContext[CpuRegister.Rax] = 18446744073709551615uL;
			return OrbisGen2Result.ORBIS_GEN2_OK;
		}
		cpuContext[CpuRegister.Rax] = 0uL;
		return OrbisGen2Result.ORBIS_GEN2_OK;
	}

	private static bool TryResolveModuleSymbolAddress(int moduleHandle, string symbolName, out ulong address)
	{
		if (KernelModuleRegistry.TryResolveModuleSymbol(moduleHandle, symbolName, out address))
		{
			return true;
		}

		var nid = ComputePsNid(symbolName);
		return KernelModuleRegistry.TryResolveModuleSymbol(moduleHandle, nid, out address);
	}

	private static string ComputePsNid(string symbolName)
	{
		ReadOnlySpan<byte> salt =
		[
			0x51, 0x8D, 0x64, 0xA6, 0x35, 0xDE, 0xD8, 0xC1,
			0xE6, 0xB0, 0x39, 0xB1, 0xC3, 0xE5, 0x52, 0x30,
		];
		var nameBytes = Encoding.UTF8.GetBytes(symbolName);
		var input = new byte[nameBytes.Length + salt.Length];
		nameBytes.CopyTo(input, 0);
		salt.CopyTo(input.AsSpan(nameBytes.Length));
		Span<byte> digest = stackalloc byte[20];
		SHA1.HashData(input, digest);
		var value = BinaryPrimitives.ReadUInt64LittleEndian(digest);
		Span<byte> bigEndianValue = stackalloc byte[sizeof(ulong)];
		BinaryPrimitives.WriteUInt64BigEndian(bigEndianValue, value);
		return Convert.ToBase64String(bigEndianValue).TrimEnd('=').Replace('/', '-');
	}

	private bool TryResolveRuntimeSymbolAlias(string symbolName, out ulong address)
	{
		address = 0;
		var alias = symbolName switch
		{
			"scriptingGetMem" => "malloc",
			"scriptingFreeMem" => "free",
			"scriptingRealloc" => "realloc",
			"scriptingCalloc" => "calloc",
			_ => null,
		};

		return alias != null && TryResolveRuntimeSymbolAddress(alias, out address);
	}

	private OrbisGen2Result DispatchIl2CppApiLookupSymbol()
	{
		var cpuContext = ActiveCpuContext;
		if (cpuContext == null)
		{
			return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
		}

		var symbolNameAddress = cpuContext[CpuRegister.Rdi];
		var outputAddress = cpuContext[CpuRegister.Rsi];
		if (!TryReadAsciiZ(symbolNameAddress, 512, out var symbolName) ||
			outputAddress == 0 ||
			!TryResolveIl2CppApiAddress(symbolName, out var resolvedAddress) ||
			!TryWriteUInt64Compat(outputAddress, resolvedAddress))
		{
			Console.Error.WriteLine(
				$"[LOADER][WARN] il2cpp_api_lookup_symbol failed: name='{symbolName}' out=0x{outputAddress:X16}");
			if (outputAddress != 0)
			{
				_ = TryWriteUInt64Compat(outputAddress, 0);
			}

			cpuContext[CpuRegister.Rax] = ulong.MaxValue;
			return OrbisGen2Result.ORBIS_GEN2_OK;
		}

		cpuContext[CpuRegister.Rax] = 0;
		return OrbisGen2Result.ORBIS_GEN2_OK;
	}

	private bool TryResolveIl2CppApiAddress(string symbolName, out ulong address)
	{
		if (TryResolveRuntimeSymbolAddress(symbolName, out address))
		{
			return true;
		}

		return Aerolib.Instance.TryGetByExportName(symbolName, out var symbol) &&
			TryResolveRuntimeSymbolAddress(symbol.Nid, out address);
	}

	private OrbisGen2Result DispatchBootstrapBridge()
	{
		var cpuContext = ActiveCpuContext;
		if (cpuContext == null)
		{
			return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
		}

		ulong bridgeHandle = cpuContext[CpuRegister.Rdi];
		ulong symbolNameAddress = cpuContext[CpuRegister.Rsi];
		ulong outputAddress = cpuContext[CpuRegister.Rdx];
		_ = TryReadAsciiZ(symbolNameAddress, 512, out var symbolName);

		OrbisGen2Result result = DispatchKernelDynlibDlsym();
		if (result != OrbisGen2Result.ORBIS_GEN2_OK)
		{
			return result;
		}
		if (_logBootstrap)
		{
			Console.Error.WriteLine(
				$"[LOADER][TRACE] bootstrap_dispatch: handle=0x{bridgeHandle:X16} symbol='{symbolName}' out=0x{outputAddress:X16} rax=0x{cpuContext[CpuRegister.Rax]:X16}");
		}

		if (cpuContext[CpuRegister.Rax] == 0uL)
		{
			return OrbisGen2Result.ORBIS_GEN2_OK;
		}

		Console.Error.WriteLine(
			$"[LOADER][WARN] bootstrap_bridge unresolved: handle=0x{bridgeHandle:X} symbol='{symbolName}' out=0x{outputAddress:X16}");
		return OrbisGen2Result.ORBIS_GEN2_OK;
	}

	private bool TryResolveRuntimeSymbolAddress(string symbolName, out ulong address)
	{
		address = 0uL;
		if (string.IsNullOrWhiteSpace(symbolName))
		{
			return false;
		}
		if (_runtimeSymbolsByName.TryGetValue(symbolName, out var value) && IsRuntimeSymbolAddressUsable(value))
		{
			address = value;
			return true;
		}
		if (symbolName.StartsWith("_", StringComparison.Ordinal) && _runtimeSymbolsByName.TryGetValue(symbolName[1..], out value) && IsRuntimeSymbolAddressUsable(value))
		{
			address = value;
			return true;
		}
		if (_runtimeSymbolsByName.TryGetValue("_" + symbolName, out value) && IsRuntimeSymbolAddressUsable(value))
		{
			address = value;
			return true;
		}
		return false;
	}

	private static bool IsRuntimeSymbolAddressUsable(ulong value)
	{
		return value != 0 && !IsUnresolvedSentinel(value);
	}

	private bool TryReadAsciiZ(ulong address, int maxLength, out string value)
	{
		value = string.Empty;
		if (ActiveCpuContext == null || address == 0L || maxLength <= 0)
		{
			return false;
		}

		const int StackBufferLength = 512;
		byte[]? rented = maxLength > StackBufferLength
			? System.Buffers.ArrayPool<byte>.Shared.Rent(maxLength)
			: null;
		Span<byte> buffer = rented is null ? stackalloc byte[StackBufferLength] : rented;
		try
		{
			for (var i = 0; i < maxLength; i++)
			{
				if (!TryReadByteCompat(address + (ulong)i, buffer.Slice(i, 1)))
				{
					return false;
				}
				if (buffer[i] == 0)
				{
					value = System.Text.Encoding.ASCII.GetString(buffer[..i]);
					return true;
				}
			}
			value = System.Text.Encoding.ASCII.GetString(buffer[..maxLength]);
			return true;
		}
		finally
		{
			if (rented is not null)
			{
				System.Buffers.ArrayPool<byte>.Shared.Return(rented);
			}
		}
	}

	private bool TryReadByteCompat(ulong address, Span<byte> destination)
	{
		var cpuContext = ActiveCpuContext;
		if (cpuContext == null || destination.Length == 0)
		{
			return false;
		}
		if (cpuContext.Memory.TryRead(address, destination))
		{
			return true;
		}
		try
		{
			destination[0] = Marshal.ReadByte((nint)address);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private bool TryReadUInt64Compat(ulong address, out ulong value)
	{
		value = 0;
		var cpuContext = ActiveCpuContext;
		if (cpuContext == null || address == 0L)
		{
			return false;
		}
		if (cpuContext.TryReadUInt64(address, out value))
		{
			return true;
		}
		try
		{
			value = unchecked((ulong)Marshal.ReadInt64((nint)address));
			return true;
		}
		catch
		{
			value = 0;
			return false;
		}
	}

	private bool TryWriteUInt64Compat(ulong address, ulong value)
	{
		var cpuContext = ActiveCpuContext;
		if (cpuContext == null || address == 0L)
		{
			return false;
		}
		if (cpuContext.TryWriteUInt64(address, value))
		{
			return true;
		}
		try
		{
			Marshal.WriteInt64((nint)address, unchecked((long)value));
			return true;
		}
		catch
		{
			return false;
		}
	}

	private unsafe void TryPatchEa020eLookupCall(long dispatchIndex, ulong returnRip)
	{
		if (_patchedEa020eLookupCall || returnRip != 0x0000000800EA01A6uL)
		{
			return;
		}
		const ulong num = 0x0000000800EA020EuL;
		nint num2 = unchecked((nint)num);
		uint flNewProtect = default(uint);
		try
		{
			if (Marshal.ReadByte(num2) != 232 || !VirtualProtect((void*)num, 5u, 64u, &flNewProtect))
			{
				return;
			}
			for (int i = 0; i < 5; i++)
			{
				Marshal.WriteByte(num2 + i, 144);
			}
			FlushInstructionCache(GetCurrentProcess(), (void*)num, 5u);
			_patchedEa020eLookupCall = true;
			Console.Error.WriteLine($"[LOADER][WARNING] Import#{dispatchIndex}: patched hash-lookup call at 0x{num:X16} -> NOP*5");
		}
		catch
		{
		}
		finally
		{
			if (flNewProtect != 0)
			{
				VirtualProtect((void*)num, 5u, flNewProtect, &flNewProtect);
			}
		}
	}
}
