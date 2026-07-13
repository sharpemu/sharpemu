// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Loader;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
	private readonly object _importResultLogSampleGate = new();
	private readonly Dictionary<string, int> _importResultLogSamples = new(StringComparer.Ordinal);

	private static ulong ImportDispatchGatewayManaged(nint backendHandle, int importIndex, nint argPackPtr)
	{
		if (LogThreadMode)
		{
			_threadModeGatewayCalls++;
			_threadModeGatewayDepth++;
			if (!_threadModeGatewayFirstLogged)
			{
				_threadModeGatewayFirstLogged = true;
				TraceThreadMode($"gateway_first import={importIndex} total={_threadModeGatewayCalls}");
			}
		}
		try
		{
			if (!(GCHandle.FromIntPtr(backendHandle).Target is DirectExecutionBackend directExecutionBackend))
			{
				Console.Error.WriteLine(
					$"[LOADER][ERROR] ImportDispatchGatewayManaged: invalid backend handle 0x{backendHandle:X16}");
				return 18446744071562199042uL;
			}

			return directExecutionBackend.DispatchImport(importIndex, argPackPtr);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine(
				$"[LOADER][ERROR] ImportDispatchGatewayManaged exception: {ex.GetType().Name}: {ex.Message}");
			return 18446744071562199298uL;
		}
		finally
		{
			if (LogThreadMode)
			{
				_threadModeGatewayDepth--;
			}
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
			if (LogThreadMode)
			{
				TraceThreadMode(
					$"sentinel_recover rip=0x{value:X16} -> 0x{returnRip:X16} rsp=0x{rsp:X16} -> 0x{nextRsp:X16} " +
					$"gateway_depth={_threadModeGatewayDepth}");
			}
			return -1;
		}
		return 0;
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
		var importEntries = _importEntries;
		if ((uint)importIndex >= (uint)importEntries.Length)
		{
			LastError = $"Import dispatch index out of range: {importIndex}";
			return 18446744071562199042uL;
		}
		ImportStubEntry importStubEntry = importEntries[importIndex];
		int num2 = Volatile.Read(in _rawSentinelRecoveries);
		if (num2 != _lastReportedRawSentinelRecoveries)
		{
			Console.Error.WriteLine($"[LOADER][TRACE] Raw sentinel recoveries: {num2} (last import index={importIndex})");
			_lastReportedRawSentinelRecoveries = num2;
		}
		// Leaf imports take a scalar-only fast path that reads its own operands and
		// intentionally bypasses the SysV variadic XMM marshalling below. This is safe
		// only while the leaf set contains no float-variadic or float-returning function.
		// The constraint and the audited list live on IsLeafImport — do not add a
		// function that consumes xmm0-7 args or returns in xmm0 to the leaf set;
		// such functions must stay on the full gateway path below.
		if (IsLeafImport(importStubEntry.Nid) &&
			TryDispatchLeafImport(cpuContext, importStubEntry, argPackPtr, num, out var leafResult))
		{
			return leafResult;
		}

		cpuContext.Rip = importStubEntry.Address;
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
		// The trampoline spills the SysV variadic XMM save area (xmm0..xmm7) into the
		// 0x80 bytes immediately below the GP argpack, so variadic float args (printf
		// %f, and powf/logf inputs) reach the handler. xmm{i} is at argPackPtr-0x80+i*16.
		for (var xmmIndex = 0; xmmIndex < 8; xmmIndex++)
		{
			var xmmSlot = argPackPtr - 0x80 + (xmmIndex * 16);
			cpuContext.SetXmmRegister(
				xmmIndex,
				*(ulong*)xmmSlot,
				*(ulong*)(xmmSlot + 8));
		}
		cpuContext[CpuRegister.Rsp] = (ulong)argPackPtr + 96uL;
		if (string.Equals(importStubEntry.Nid, RuntimeStubNids.BootstrapBridge, StringComparison.Ordinal))
		{
			NormalizeKernelDynlibDlsymArguments(cpuContext, out _, out _);
			*(ulong*)argPackPtr = cpuContext[CpuRegister.Rdi];
			*(ulong*)(argPackPtr + 8) = cpuContext[CpuRegister.Rsi];
			*(ulong*)(argPackPtr + 16) = cpuContext[CpuRegister.Rdx];
		}

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
		if (_activeGuestThreadState is { } activeGuestThreadState)
		{
			Interlocked.Increment(ref activeGuestThreadState.ImportCount);
			Volatile.Write(ref activeGuestThreadState.LastImportNid, importStubEntry.Nid);
			Volatile.Write(ref activeGuestThreadState.LastReturnRip, num7);
		}
		if (_logStrlenBursts)
		{
			TrackDistinctImportNid(importStubEntry.Nid);
			TrackStrlenPrelude(importStubEntry.Nid, num, num7);
		}
		if (!string.IsNullOrWhiteSpace(_probeImportReturn) &&
			(string.Equals(_probeImportReturn, "*", StringComparison.Ordinal) ||
			 string.Equals(_probeImportReturn, importStubEntry.Nid, StringComparison.Ordinal)))
		{
			ProbeReturnRip(num7, num);
		}
		if (_logGuestContext)
		{
			TraceGuestContext(
				$"import dispatch={num} nid={importStubEntry.Nid} ret=0x{num7:X16} managed={Environment.CurrentManagedThreadId} guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} fiber=0x{GuestThreadExecution.CurrentFiberAddress:X16} active={HasActiveExecutionThread}");
		}
		if (!isGuestWorker &&
			!ActiveForcedGuestExit &&
			ShouldForceGuestExitOnImportLoop(importStubEntry.Nid, num7, num, value, value2) &&
			TryForceGuestExitToHostStub(argPackPtr, num, num7, importStubEntry.Nid))
		{
			cpuContext[CpuRegister.Rax] = 1uL;
			return 1uL;
		}
		// Backend teardown in progress: unwind this worker to the host instead of
		// dispatching. RunGuestThread parks it as Blocked with no wake handler, its
		// host thread exits, and RequestGuestThreadTeardown can finish before any
		// executable memory is freed.
		if (isGuestWorker &&
			_guestTeardownRequested &&
			TryYieldGuestThreadToHostStub(argPackPtr, num, num7, importStubEntry.Nid, "backend teardown"))
		{
			cpuContext[CpuRegister.Rax] = 0uL;
			return 0uL;
		}
		bool flag0 = ShouldSuppressStrlenTrace(importStubEntry.Nid);
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
				var savedGuardAddress = value4 >= 0x10 ? value4 - 0x10 : 0;
				var guardKnown = TryReadUInt64Compat(value3, out var guardValue);
				var savedKnown = TryReadUInt64Compat(savedGuardAddress, out var savedGuardValue);
				Console.Error.WriteLine(
					$"[LOADER][TRACE] stack_chk_diag#{num}: ret=0x{num7:X16} guard_ptr=0x{value3:X16} " +
					$"guard={(guardKnown ? $"0x{guardValue:X16}" : "?")} saved@0x{savedGuardAddress:X16}={(savedKnown ? $"0x{savedGuardValue:X16}" : "?")} " +
					$"rbp=0x{value4:X16} rsp=0x{((ulong)argPackPtr + 96uL):X16}");
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
					if (_logBootstrap)
					{
						RecordDeferredBootstrapTrace(num, value, value2, num3, num7);
					}

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
                else if (importStubEntry.Export is { } mismatchedExport)
                {
                    dispatchResolved = true;
                    orbisGen2Result = OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED;
                    cpuContext[CpuRegister.Rax] = unchecked((ulong)(int)orbisGen2Result);
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Import#{num} not implemented for generation {cpuContext.TargetGeneration}: " +
                        $"nid={importStubEntry.Nid} targets={mismatchedExport.Target} ret=0x{num7:X16}");
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
			if (dispatchResolved &&
				orbisGen2Result == OrbisGen2Result.ORBIS_GEN2_OK &&
				string.Equals(importStubEntry.Nid, "BohYr-F7-is", StringComparison.Ordinal))
			{
				RegisterPrtLazyCommitRange(value2, num3);
			}
			if (!dispatchResolved)
			{
				LastError = "Missing HLE export for NID: " + importStubEntry.Nid;
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
				if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_FIBER"), "1", StringComparison.Ordinal))
				{
					Console.Error.WriteLine(
						$"[LOADER][TRACE] fiber.context-transfer rip=0x{transferTarget.Rip:X16} " +
						$"rsp=0x{transferTarget.Rsp:X16} guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} " +
						$"fiber=0x{GuestThreadExecution.CurrentFiberAddress:X16}");
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
					out var blockResumeHandler,
					out var blockWakeHandler,
					out var blockDeadlineTimestamp) &&
				TryYieldGuestThreadToHostStub(argPackPtr, num, num7, importStubEntry.Nid, blockReason))
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
			if (flag || flag2 || flag3)
			{
				Console.Error.WriteLine($"[LOADER][TRACE] ImportRet#{num}: nid={importStubEntry.Nid} result={orbisGen2Result} rax=0x{cpuContext[CpuRegister.Rax]:X16}");
				if (flag3)
				{
					Console.Error.Flush();
				}
			}
			// Publish the handler's XMM0 back into the argpack's xmm0 save slot; the
			// trampoline epilogue reloads it into the guest's XMM0, delivering float/double
			// return values (powf/logf/wcstod). Harmless for int/pointer returns (XMM is
			// volatile across a SysV call, so the guest never relies on a preserved XMM0).
			cpuContext.GetXmmRegister(0, out var returnXmm0Low, out var returnXmm0High);
			*(ulong*)(argPackPtr - 0x80) = returnXmm0Low;
			*(ulong*)(argPackPtr - 0x80 + 8) = returnXmm0High;
			DrainDeferredBootstrapTraces();
			return cpuContext[CpuRegister.Rax];
		}
		catch (Exception ex)
		{
			LastError = $"HLE dispatch error for {importStubEntry.Nid}: {ex.GetType().Name}: {ex.Message}";
			Console.Error.WriteLine($"[LOADER][ERROR] {LastError}");
			Console.Error.WriteLine($"[LOADER][ERROR] {ex.StackTrace}");
			cpuContext[CpuRegister.Rax] = 18446744071562199298uL;
			DrainDeferredBootstrapTraces();
			return 18446744071562199298uL;
		}
	}

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
		cpuContext.Rip = importStubEntry.Address;
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
			Volatile.Write(ref activeGuestThreadState.LastImportNid, importStubEntry.Nid);
			Volatile.Write(ref activeGuestThreadState.LastReturnRip, returnRip);
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
		if (IsNoBlockLeafImport(importStubEntry.Nid))
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

		if (GuestThreadExecution.TryConsumeCurrentThreadBlock(
				out var blockReason,
				out var blockContinuation,
				out var hasBlockContinuation,
				out var blockWakeKey,
				out var blockResumeHandler,
				out var blockWakeHandler,
				out var blockDeadlineTimestamp) &&
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

		result = cpuContext[CpuRegister.Rax];
		return true;
	}

	// Subset of the leaf set that additionally skips the import-call-frame
	// bookkeeping. The same scalar-only (no XMM args, no XMM return) constraint
	// as IsLeafImport applies — see the note there before adding entries.
	// NOTE: this filter is only consulted after IsLeafImport accepts the NID;
	// entries listed here but not in IsLeafImport (scePadRead, scePadOpen,
	// sceUserServiceGetEvent, sceUserServiceGetPlatformPrivacySetting,
	// pthread_mutex_trylock) currently take the full gateway path.
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
			"H896Pt-yB4I" or // sceAmprCommandBufferWriteKernelEventQueue_04_00
			"sJXyWHjP-F8" or // sceAmprCommandBufferWriteAddressOnCompletion
			"ASoW5WE-UPo" or // sceKernelAprSubmitCommandBufferAndGetResult
			"rqwFKI4PAiM" or // sceKernelAprWaitCommandBuffer
			"eE4Szl8sil8" or // sceKernelAprSubmitCommandBuffer
			"qvMUCyyaCSI" or // sceKernelAprSubmitCommandBufferAndGetId
			"Q2V+iqvjgC0" or // vsnprintf
			"AV6ipCNa4Rw" or // strcasecmp
			"q1cHNfGycLI" or // scePadRead
			"xk0AcarP3V4" or // scePadOpen
			"yH17Q6NWtVg" or // sceUserServiceGetEvent
			"D-CzAxQL0XI" or // sceUserServiceGetPlatformPrivacySetting
			"K-jXhbt2gn4";   // pthread_mutex_trylock (scePthreadMutexTrylock is upoVrzMHFeE)

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

	// LEAF-IMPORT REGISTRATION — scalar-only constraint.
	//
	// TryDispatchLeafImport is a fast path that never copies the trampoline's XMM
	// save area into CpuContext and never publishes an XMM0 return back to the
	// guest (see DispatchImport). Every NID listed here must therefore satisfy BOTH:
	//   1. no float/double parameters (nothing passed in xmm0..xmm7), and
	//   2. no float/double return value (nothing returned in xmm0).
	// Integer/pointer arguments and returns only. va_list-based functions
	// (vsnprintf) are safe: SysV va_list floats are read from the caller-built
	// reg_save_area in guest memory, not from the callee's incoming XMM registers.
	//
	// If a function that consumes XMM arguments or returns in xmm0 (e.g. powf,
	// logf, wcstod, sceVideoOutColorSettingsSetGamma_) is ever added here, its
	// float arguments will silently arrive stale and its return value will be
	// dropped. Such functions must stay on the full gateway path — do not list them.
	//
	// Audited 2026-07-11 (PR #59): every NID below maps to a registered
	// SysAbiExport whose handler neither reads nor writes CpuContext XMM state
	// and whose known guest signature is integer/pointer only.
	private bool IsLeafImport(string nid)
	{
		if (nid == "1jfXLRVzisc") // sceKernelUsleep
		{
			return !_logUsleep;
		}

		// Only mutex/rwlock *lock* is excluded: it may block a contended acquire, which the
		// leaf path can't. unlock never blocks and stays here — routing it off the fast path
		// slows guest spinlocks enough to livelock (Demon's Souls).
		return nid is
			"tn3VlD0hG60" or // scePthreadMutexUnlock
			"2Z+PpY6CaJg" or // pthread_mutex_unlock
			"EgmLo6EWgso" or // pthread_rwlock_unlock
			"+L98PIbGttk" or // scePthreadRwlockUnlock
			"8aI7R7WaOlc" or // sceAmprCommandBufferConstructor
			"zgXifHT9ErY" or // sceVideoOutIsFlipPending
			"V++UgBtQhn0" or // sceAgcGetDataPacketPayloadAddress
			"qj7QZpgr9Uw" or // sceAgcUnknownQj7QZpgr9Uw (unknown NID; observed scalar: command-buffer pointer in, packet address out)
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
			"0fWWK5uG9rQ" or // sceAgcQueueEndOfPipeActionPatchAddress
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
			"H896Pt-yB4I" or // sceAmprCommandBufferWriteKernelEventQueue_04_00
			"sJXyWHjP-F8" or // sceAmprCommandBufferWriteAddressOnCompletion
			"ASoW5WE-UPo" or // sceKernelAprSubmitCommandBufferAndGetResult
			"rqwFKI4PAiM" or // sceKernelAprWaitCommandBuffer
			"eE4Szl8sil8" or // sceKernelAprSubmitCommandBuffer
			"qvMUCyyaCSI" or // sceKernelAprSubmitCommandBufferAndGetId
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
			"AV6ipCNa4Rw" or // strcasecmp
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
			"kwGyyjohI50" or // sceKernelGetEventData
			// _Getpctype is a per-character ctype table lookup; the embedded mcpp preprocessor
			// re-fetches the table pointer on every isdigit()/isalpha() check, so classifying a
			// large input legitimately calls it hundreds of thousands of times back-to-back -
			// real, finite work that would otherwise trip the loop guard.
			"sUP1hBaouOw";   // _Getpctype
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

	private bool ShouldForceGuestExitOnImportLoop(string nid, ulong returnRip, long dispatchIndex, ulong arg0, ulong arg1)
	{
		if (dispatchIndex < 1200)
		{
			return false;
		}
		if (_disableImportLoopGuard || _importLoopGuardSeconds <= 0)
		{
			return false;
		}
		if (IsImportLoopGuardBoundary(nid))
		{
			ResetImportLoopPattern();
			return false;
		}
		if (!_importNidHashCache.TryGetValue(nid, out var value))
		{
			value = StableHash64(nid);
			_importNidHashCache[nid] = value;
		}
		RecordImportLoopSignature(value, returnRip, BuildImportLoopSignature(value, returnRip, arg0, arg1));
		if ((dispatchIndex & 0x3F) != 0)
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
		string.Equals(nid, "1jfXLRVzisc", StringComparison.Ordinal);

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

		NormalizeKernelDynlibDlsymArguments(cpuContext, out var symbolNameAddress, out var outputAddress);
		try
		{
			if (!TryReadAsciiZ(symbolNameAddress, 512, out var symbolName) ||
				!TryResolveDlsymGuestAddress(symbolName, out var resolvedAddress) ||
				outputAddress == 0 ||
				!TryWriteUInt64Compat(outputAddress, resolvedAddress))
			{
				return CompleteKernelDynlibDlsymFailure(cpuContext, outputAddress);
			}
		}
		catch
		{
			return CompleteKernelDynlibDlsymFailure(cpuContext, outputAddress);
		}

		cpuContext[CpuRegister.Rax] = 0uL;
		return OrbisGen2Result.ORBIS_GEN2_OK;
	}

	private static void NormalizeKernelDynlibDlsymArguments(
		CpuContext cpuContext,
		out ulong symbolNameAddress,
		out ulong outputAddress)
	{
		var handle = cpuContext[CpuRegister.Rdi];
		symbolNameAddress = cpuContext[CpuRegister.Rsi];
		outputAddress = cpuContext[CpuRegister.Rdx];

		// Standalone bootstrap loaders sometimes call through the bridge with
		// (symbol_ptr, handle, out) while sceKernelDlsym is (handle, symbol_ptr, out).
		// Heuristic only: valid when RSI looks like a small handle and RDI is a guest pointer.
		if (symbolNameAddress < 0x10000 &&
			IsPlausibleDynlibSymbolPointer(handle))
		{
			symbolNameAddress = handle;
			handle = cpuContext[CpuRegister.Rsi];
			cpuContext[CpuRegister.Rdi] = handle;
			cpuContext[CpuRegister.Rsi] = symbolNameAddress;
		}
	}

	private static bool IsPlausibleDynlibSymbolPointer(ulong address)
	{
		return address >= 0x10000 && address < 0x0000_8000_0000_0000UL;
	}

	private OrbisGen2Result CompleteKernelDynlibDlsymFailure(CpuContext cpuContext, ulong outputAddress)
	{
		if (outputAddress != 0)
		{
			_ = TryWriteUInt64Compat(outputAddress, 0);
		}

		cpuContext[CpuRegister.Rax] = ulong.MaxValue;
		return OrbisGen2Result.ORBIS_GEN2_OK;
	}

	private void ResetLazyDlsymStubState()
	{
		lock (_lazyDlsymStubGate)
		{
			_lazyDlsymStubCache.Clear();
			_lazyImportStubPoolMapped = false;
			_lazyImportStubPoolBase = 0;
			_lazyImportStubNextSlot = 0;
			_lazyImportStubPoolLimit = 0;
		}
	}

	private bool TryResolveDlsymGuestAddress(string symbolName, out ulong guestAddress)
	{
		guestAddress = 0;
		if (string.IsNullOrWhiteSpace(symbolName))
		{
			return false;
		}

		// Tier 0: ELF runtime symbols and aliases.
		if (TryResolveRuntimeSymbolAddress(symbolName, out guestAddress) ||
			TryResolveRuntimeSymbolAlias(symbolName, out guestAddress))
		{
			return true;
		}

		// Tier 1: existing import stub entries (duplicate prevention).
		if (TryFindImportStubGuestAddress(symbolName, out guestAddress))
		{
			return true;
		}

		var hasAerolibSymbol = Aerolib.Instance.TryGetByExportName(symbolName, out var hleSymbol);
		if (hasAerolibSymbol)
		{
			if (HleDataSymbols.TryGetAddress(hleSymbol.Nid, out _))
			{
				return false;
			}

			if (TryResolveRuntimeSymbolAddress(hleSymbol.Nid, out guestAddress) ||
				TryResolveRuntimeSymbolAddress(hleSymbol.ExportName, out guestAddress) ||
				TryFindImportStubGuestAddress(hleSymbol.Nid, out guestAddress) ||
				TryFindImportStubGuestAddress(hleSymbol.ExportName, out guestAddress))
			{
				return true;
			}
		}
		else if (Aerolib.Instance.TryGetByNid(symbolName, out hleSymbol))
		{
			if (HleDataSymbols.TryGetAddress(hleSymbol.Nid, out _))
			{
				return false;
			}

			if (TryFindImportStubGuestAddress(hleSymbol.Nid, out guestAddress) ||
				TryFindImportStubGuestAddress(hleSymbol.ExportName, out guestAddress))
			{
				return true;
			}

			hasAerolibSymbol = true;
		}

		// Tier 3: lazy materialization for callable HLE symbols missing from ELF imports.
		if (!TryResolveLazyDispatchTarget(symbolName, hasAerolibSymbol, in hleSymbol, out var dispatchNid, out var export))
		{
			return false;
		}

		return TryGetOrCreateLazyImportStub(
			dispatchNid,
			symbolName,
			hasAerolibSymbol ? hleSymbol : null,
			export,
			out guestAddress);
	}

	private bool TryFindImportStubGuestAddress(string identifier, out ulong guestAddress)
	{
		guestAddress = 0;
		if (string.IsNullOrWhiteSpace(identifier))
		{
			return false;
		}

		var importEntries = _importEntries;
		for (var i = 0; i < importEntries.Length; i++)
		{
			var entry = importEntries[i];
			if (!ImportStubEntryMatchesIdentifier(entry, identifier))
			{
				continue;
			}

			if (entry.Address >= 0x10000)
			{
				guestAddress = entry.Address;
				return true;
			}
		}

		return false;
	}

	private static bool ImportStubEntryMatchesIdentifier(in ImportStubEntry entry, string identifier)
	{
		if (string.Equals(entry.Nid, identifier, StringComparison.Ordinal))
		{
			return true;
		}

		if (entry.Export is not { } export)
		{
			return false;
		}

		return string.Equals(export.Name, identifier, StringComparison.Ordinal) ||
			string.Equals(export.Nid, identifier, StringComparison.Ordinal);
	}

	private static bool IsKernelDynlibDlsymIdentifier(string identifier)
	{
		return string.Equals(identifier, "sceKernelDlsym", StringComparison.Ordinal) ||
			string.Equals(identifier, KernelDynlibDlsymAerolibNid, StringComparison.Ordinal) ||
			string.Equals(identifier, RuntimeStubNids.KernelDynlibDlsym, StringComparison.Ordinal);
	}

	private bool TryResolveLazyDispatchTarget(
		string symbolName,
		bool hasAerolibSymbol,
		in SysAbiSymbol hleSymbol,
		out string dispatchNid,
		out ExportedFunction? export)
	{
		export = null;
		dispatchNid = string.Empty;

		if (IsKernelDynlibDlsymIdentifier(symbolName))
		{
			dispatchNid = KernelDynlibDlsymAerolibNid;
			_ = _moduleManager.TryGetExport(dispatchNid, out export);
			return true;
		}

		if (!hasAerolibSymbol)
		{
			return false;
		}

		if (HleDataSymbols.TryGetAddress(hleSymbol.Nid, out _))
		{
			return false;
		}

		if (_moduleManager.TryGetExport(hleSymbol.Nid, out export))
		{
			dispatchNid = hleSymbol.Nid;
			return true;
		}

		return false;
	}

	private bool TryGetOrCreateLazyImportStub(
		string dispatchNid,
		string symbolName,
		SysAbiSymbol? hleSymbol,
		ExportedFunction? export,
		out ulong guestAddress)
	{
		guestAddress = 0;
		if (string.IsNullOrWhiteSpace(dispatchNid))
		{
			return false;
		}

		lock (_lazyDlsymStubGate)
		{
			if (TryGetCachedLazyDlsymAddress(dispatchNid, symbolName, out guestAddress))
			{
				return true;
			}

			if (!EnsureLazyImportStubPoolMapped())
			{
				LogLazyImportStubMaterializationFailure(
					"import stub region unresolved",
					dispatchNid,
					symbolName);
				return false;
			}

			if (!TryAllocateLazyImportStubSlot(out guestAddress))
			{
				LogLazyImportStubMaterializationFailure(
					"lazy import stub pool exhausted",
					dispatchNid,
					symbolName);
				return false;
			}

			var previousEntries = _importEntries;
			var importIndex = previousEntries.Length;
			var newEntries = new ImportStubEntry[importIndex + 1];
			if (importIndex > 0)
			{
				Array.Copy(previousEntries, newEntries, importIndex);
			}

			newEntries[importIndex] = new ImportStubEntry(guestAddress, dispatchNid, export);

			var hostTrampoline = CreateImportHandlerTrampoline(importIndex);
			if (hostTrampoline == 0 ||
				!PatchImportStub((nint)(long)guestAddress, hostTrampoline))
			{
				guestAddress = 0;
				LogLazyImportStubMaterializationFailure(
					"failed to patch lazy import stub trampoline",
					dispatchNid,
					symbolName);
				return false;
			}

			_importEntries = newEntries;

			RegisterDlsymRuntimeSymbolKeys(symbolName, dispatchNid, hleSymbol, guestAddress);
			return true;
		}
	}

	private bool TryGetCachedLazyDlsymAddress(string dispatchNid, string symbolName, out ulong guestAddress)
	{
		if (_lazyDlsymStubCache.TryGetValue(dispatchNid, out guestAddress) ||
			_lazyDlsymStubCache.TryGetValue(symbolName, out guestAddress))
		{
			return guestAddress >= 0x10000;
		}

		return false;
	}

	private void RegisterDlsymRuntimeSymbolKeys(
		string symbolName,
		string dispatchNid,
		SysAbiSymbol? hleSymbol,
		ulong guestAddress)
	{
		RegisterDlsymRuntimeSymbolKey(symbolName, guestAddress);
		RegisterDlsymRuntimeSymbolKey(dispatchNid, guestAddress);

		if (IsKernelDynlibDlsymIdentifier(symbolName) || IsKernelDynlibDlsymIdentifier(dispatchNid))
		{
			RegisterDlsymRuntimeSymbolKey(RuntimeStubNids.KernelDynlibDlsym, guestAddress);
			RegisterDlsymRuntimeSymbolKey(KernelDynlibDlsymAerolibNid, guestAddress);
			RegisterDlsymRuntimeSymbolKey("sceKernelDlsym", guestAddress);
		}

		if (hleSymbol.HasValue)
		{
			RegisterDlsymRuntimeSymbolKey(hleSymbol.Value.Nid, guestAddress);
			RegisterDlsymRuntimeSymbolKey(hleSymbol.Value.ExportName, guestAddress);
		}
	}

	private void RegisterDlsymRuntimeSymbolKey(string key, ulong guestAddress)
	{
		if (string.IsNullOrWhiteSpace(key) || !IsRuntimeSymbolAddressUsable(guestAddress))
		{
			return;
		}

		_runtimeSymbolsByName[key] = guestAddress;
		_lazyDlsymStubCache[key] = guestAddress;
	}

	private void LogLazyImportStubMaterializationFailure(string reason, string dispatchNid, string symbolName)
	{
		Console.Error.WriteLine(
			$"[LOADER][WARN] Lazy import stub materialization failed ({reason}): " +
			$"nid={dispatchNid} symbol='{symbolName}'");
	}

	private unsafe bool TryResolveImportStubRegionBounds(
		ImportStubEntry[] importEntries,
		out ulong regionBase,
		out ulong regionLimit)
	{
		regionBase = 0;
		regionLimit = 0;

		for (var candidateIndex = 0; candidateIndex < 64; candidateIndex++)
		{
			var candidateBase = ImportStubRegionCanonicalBase -
				(ulong)candidateIndex * ImportStubRegionAddressStride;
			if (VirtualQuery((void*)candidateBase, out var memoryInfo, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) == 0 ||
				memoryInfo.RegionSize == 0 ||
				memoryInfo.State != 4096)
			{
				continue;
			}

			var candidateLimit = candidateBase + memoryInfo.RegionSize;
			var hasStub = false;
			for (var i = 0; i < importEntries.Length; i++)
			{
				var entryAddress = importEntries[i].Address;
				if (entryAddress < candidateBase || entryAddress >= candidateLimit)
				{
					continue;
				}

				if ((entryAddress - candidateBase) % LazyImportStubSlotSize != 0)
				{
					continue;
				}

				hasStub = true;
				break;
			}

			if (!hasStub)
			{
				continue;
			}

			regionBase = candidateBase;
			regionLimit = candidateLimit;
			return true;
		}

		ulong maxStubEnd = 0;
		for (var i = 0; i < importEntries.Length; i++)
		{
			var entryAddress = importEntries[i].Address;
			if (entryAddress < ImportStubRegionCanonicalBase)
			{
				continue;
			}

			if ((entryAddress - ImportStubRegionCanonicalBase) % LazyImportStubSlotSize != 0)
			{
				continue;
			}

			var entryEnd = entryAddress + LazyImportStubSlotSize;
			if (entryEnd > maxStubEnd)
			{
				maxStubEnd = entryEnd;
				regionBase = ImportStubRegionCanonicalBase;
			}
		}

		if (regionBase == 0 || maxStubEnd <= regionBase)
		{
			return false;
		}

		var spanBytes = maxStubEnd - regionBase;
		regionLimit = regionBase + AlignUp(spanBytes, ImportStubRegionPageSize);
		return regionLimit > regionBase;
	}

	private bool EnsureLazyImportStubPoolMapped()
	{
		if (_lazyImportStubPoolMapped && _lazyImportStubPoolBase != 0)
		{
			return true;
		}

		var importEntries = _importEntries;
		if (!TryResolveImportStubRegionBounds(importEntries, out var importStubRegionBase, out var importStubRegionLimit))
		{
			return false;
		}

		ulong nextSlot = importStubRegionBase;
		for (var i = 0; i < importEntries.Length; i++)
		{
			var entryAddress = importEntries[i].Address;
			if (entryAddress < importStubRegionBase || entryAddress >= importStubRegionLimit)
			{
				continue;
			}

			var entryEnd = entryAddress + LazyImportStubSlotSize;
			if (entryEnd > nextSlot)
			{
				nextSlot = entryEnd;
			}
		}

		if (nextSlot >= importStubRegionLimit)
		{
			return false;
		}

		_lazyImportStubPoolBase = importStubRegionBase;
		_lazyImportStubNextSlot = nextSlot;
		_lazyImportStubPoolLimit = importStubRegionLimit;
		_lazyImportStubPoolMapped = true;
		return true;
	}

	private bool TryAllocateLazyImportStubSlot(out ulong guestAddress)
	{
		guestAddress = 0;
		if (!_lazyImportStubPoolMapped ||
			_lazyImportStubNextSlot < _lazyImportStubPoolBase ||
			_lazyImportStubNextSlot + LazyImportStubSlotSize > _lazyImportStubPoolLimit)
		{
			return false;
		}

		guestAddress = _lazyImportStubNextSlot;
		_lazyImportStubNextSlot += LazyImportStubSlotSize;
		return true;
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

		ulong outputAddress = cpuContext[CpuRegister.Rdx];
		try
		{
			OrbisGen2Result result = DispatchKernelDynlibDlsym();
			if (result != OrbisGen2Result.ORBIS_GEN2_OK)
			{
				return result;
			}
		}
		catch
		{
			return CompleteKernelDynlibDlsymFailure(cpuContext, outputAddress);
		}

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

		List<byte> list = new List<byte>(Math.Min(maxLength, 256));
		Span<byte> destination = stackalloc byte[1];
		for (int i = 0; i < maxLength; i++)
		{
			if (!TryReadByteCompat(address + (ulong)i, destination))
			{
				return false;
			}
			if (destination[0] == 0)
			{
				value = System.Text.Encoding.ASCII.GetString(list.ToArray());
				return true;
			}
			list.Add(destination[0]);
		}
		value = System.Text.Encoding.ASCII.GetString(list.ToArray());
		return true;
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
