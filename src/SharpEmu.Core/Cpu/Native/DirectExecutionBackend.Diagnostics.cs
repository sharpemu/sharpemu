// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.Core.Cpu.Disasm;
using SharpEmu.HLE;
using SharpEmu.Logging;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
	private static readonly ConcurrentDictionary<ulong, byte> _knownExecutablePages = new();

	private static readonly bool _perfHleHistogram =
		string.Equals(System.Environment.GetEnvironmentVariable("SHARPEMU_PERF_HLE"), "1", System.StringComparison.Ordinal);
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _perfHleCounts = new();
	private static long _perfHleTotal;
	private static long _perfHleDispatchTicks;

	private static void RecordPerfHleDispatchTime(long ticks)
	{
		var total = System.Threading.Interlocked.Add(ref _perfHleDispatchTicks, ticks);
		var calls = System.Threading.Interlocked.Read(ref _perfHleTotal);
		if (calls > 0 && calls % 500000 == 0)
		{
			var avgUs = (double)total / System.Diagnostics.Stopwatch.Frequency * 1_000_000.0 / calls;
			System.Console.Error.WriteLine($"[PERF][HLE] managed_dispatch_avg={avgUs:F3}us total_managed_s={(double)total / System.Diagnostics.Stopwatch.Frequency:F2}");
		}
	}

	private static readonly bool _perfHleNoDict =
		string.Equals(System.Environment.GetEnvironmentVariable("SHARPEMU_PERF_HLE_NODICT"), "1", System.StringComparison.Ordinal);

	private static void RecordPerfHleCall(string name)
	{
		var total = System.Threading.Interlocked.Increment(ref _perfHleTotal);
		if (!_perfHleNoDict)
		{
			_perfHleCounts.AddOrUpdate(name, 1, static (_, v) => v + 1);
		}

		if (total % 500000 == 0 && !_perfHleNoDict)
		{
			// Snapshot via foreach (a safe moving enumerator) before sorting.
			// LINQ over a ConcurrentDictionary uses ICollection.CopyTo, which
			// throws ArgumentException if another thread adds a key between the
			// Count read and the copy — that exception was being swallowed into
			// a CPU_TRAP return and crashing the guest.
			var snapshot = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, long>>(_perfHleCounts.Count + 16);
			foreach (var kvp in _perfHleCounts)
			{
				snapshot.Add(kvp);
			}

			var top = snapshot
				.OrderByDescending(kvp => kvp.Value)
				.Take(20)
				.Select(kvp => $"{kvp.Key}={kvp.Value}");
			System.Console.Error.WriteLine($"[PERF][HLE] total={total} top: {string.Join(", ", top)}");
		}
	}

	private void RecordRecentImportTrace(
		long dispatchIndex,
		string nid,
		ulong returnRip,
		ulong arg0,
		ulong arg1,
		ulong arg2)
	{
		var trace = _recentImportTrace;
		trace[_recentImportTraceWriteIndex] = new RecentImportTraceEntry(
			dispatchIndex,
			nid,
			returnRip,
			arg0,
			arg1,
			arg2,
			GuestThreadExecution.CurrentGuestThreadHandle,
			Environment.CurrentManagedThreadId);
		_recentImportTraceWriteIndex = (_recentImportTraceWriteIndex + 1) % trace.Length;
		if (_recentImportTraceCount < trace.Length)
		{
			_recentImportTraceCount++;
		}
	}

	private void DumpRecentImportTrace()
	{
		var trace = _recentImportTrace;
		if (trace is null || _recentImportTraceCount == 0)
		{
			return;
		}
		Log.Info($"   Recent import calls for managed={Environment.CurrentManagedThreadId} guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} ({_recentImportTraceCount}):");
		int num = (_recentImportTraceWriteIndex - _recentImportTraceCount + trace.Length) % trace.Length;
		for (int i = 0; i < _recentImportTraceCount; i++)
		{
			int num2 = (num + i) % trace.Length;
			var entry = trace[num2];
			if (!string.IsNullOrEmpty(entry.Nid))
			{
				Log.Info(
					$"     #{entry.DispatchIndex} managed={entry.ManagedThreadId} guest=0x{entry.GuestThreadHandle:X16} nid={entry.Nid} ret=0x{entry.ReturnRip:X16} " +
					$"rdi=0x{entry.Arg0:X16} rsi=0x{entry.Arg1:X16} rdx=0x{entry.Arg2:X16}");
			}
		}
	}

	private unsafe static List<ulong> ScanSuspiciousResolverPointers(ulong scanStart, ulong scanEnd)
	{
		if (scanEnd <= scanStart)
		{
			return new List<ulong>(0);
		}
		int num = 0;
		int num2 = 0;
		List<ulong> list = new List<ulong>(16);
		ulong num3 = scanStart;
		MEMORY_BASIC_INFORMATION64 lpBuffer;
		while (num3 < scanEnd && VirtualQuery((void*)num3, out lpBuffer, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) != 0)
		{
			ulong baseAddress = lpBuffer.BaseAddress;
			ulong num4 = baseAddress + lpBuffer.RegionSize;
			if (num4 <= num3)
			{
				break;
			}
			ulong value = Math.Max(num3, baseAddress);
			ulong num5 = Math.Min(num4, scanEnd);
			if (lpBuffer.State == 4096 && IsReadableProtection(lpBuffer.Protect) && !IsExecutableProtection(lpBuffer.Protect))
			{
				ulong num6 = AlignUp(value, 8uL);
				for (ulong num7 = num6; num7 + 8 <= num5; num7 += 8)
				{
					ulong value2 = *(ulong*)num7;
					if (IsUnresolvedSentinel(value2))
					{
						num++;
						list.Add(num7);
						if (num2 < 32)
						{
							Log.Info($"Suspicious unresolved pointer: slot=0x{num7:X16} value=0x{value2:X16}");
							num2++;
						}
						if (num >= 16384)
						{
							Log.Warning($"Suspicious unresolved pointer scan reached cap ({16384}); truncating.");
							return list;
						}
					}
				}
			}
			num3 = num5;
		}
		if (num != 0)
		{
			Log.Warning($"Suspicious unresolved pointer hits: {num}");
		}
		return list;
	}

	private void ProbeReturnRip(ulong returnRip, long dispatchIndex)
	{
		var cpuContext = ActiveCpuContext;
		if (cpuContext == null || returnRip == 0)
		{
			return;
		}
		const int preludeSize = 192;
		Span<byte> prelude = stackalloc byte[preludeSize];
		if (returnRip >= preludeSize && cpuContext.Memory.TryRead(returnRip - preludeSize, prelude))
		{
			Console.Error.WriteLine(
				$"[LOADER][TRACE] Import#{dispatchIndex} pre-return bytes @0x{returnRip - preludeSize:X16}: " +
				BitConverter.ToString(prelude.ToArray()).Replace("-", " "));

			List<DecodedInst>? bestCallChain = null;
			var preludeAddress = returnRip - preludeSize;
			for (var startOffset = 0; startOffset < preludeSize; startOffset++)
			{
				var cursor = preludeAddress + (ulong)startOffset;
				var candidate = new List<DecodedInst>();
				while (cursor < returnRip && candidate.Count < 96 &&
					IcedDecoder.TryReadGuestBytes(cpuContext.Memory, cursor, 15, out var instructionBytes) &&
					IcedDecoder.TryDecode(cursor, instructionBytes, out var instruction) &&
					instruction.Length > 0 &&
					cursor + (ulong)instruction.Length <= returnRip)
				{
					candidate.Add(instruction);
					cursor += (ulong)instruction.Length;
				}

				if (cursor == returnRip &&
					candidate.Count > 0 &&
					string.Equals(candidate[^1].Mnemonic, "Call", StringComparison.OrdinalIgnoreCase) &&
					(bestCallChain is null || candidate.Count > bestCallChain.Count))
				{
					bestCallChain = candidate;
				}
			}

			if (bestCallChain is not null)
			{
				Console.Error.WriteLine($"[LOADER][TRACE] Import#{dispatchIndex} pre-return disassembly:");
				foreach (var instruction in bestCallChain.TakeLast(32))
				{
					Console.Error.WriteLine(
						$"[LOADER][TRACE]   0x{instruction.Rip:X16}: {instruction.Text} " +
						$"bytes={IcedDecoder.FormatBytes(instruction.Bytes)}");
				}
			}
		}
		Span<byte> destination = stackalloc byte[128];
		if (!cpuContext.Memory.TryRead(returnRip, destination))
		{
			Log.Debug($"Import#{dispatchIndex} return-rip probe: unreadable @0x{returnRip:X16}");
			return;
		}
		string value = BitConverter.ToString(destination.ToArray()).Replace("-", " ");
		Log.Debug($"Import#{dispatchIndex} return-rip bytes @0x{returnRip:X16}: {value}");
		if (destination[0] == byte.MaxValue && (destination[1] == 21 || destination[1] == 37))
		{
			int num = BitConverter.ToInt32(destination.Slice(2, 4));
			ulong num2 = returnRip + 6 + (ulong)num;
			if (cpuContext.TryReadUInt64(num2, out var value2))
			{
				Log.Debug($"Import#{dispatchIndex} return-rip slot: [0x{num2:X16}] = 0x{value2:X16}");
			}
		}
		if (destination[0] == 72 && destination[1] == 139 && destination[2] == 5)
		{
			int num3 = BitConverter.ToInt32(destination.Slice(3, 4));
			ulong num4 = returnRip + 7 + (ulong)num3;
			if (cpuContext.TryReadUInt64(num4, out var value3))
			{
				Log.Debug($"Import#{dispatchIndex} return-rip mov-slot: [0x{num4:X16}] = 0x{value3:X16}");
			}
		}
		for (int i = 0; i + 6 <= destination.Length; i++)
		{
			if (destination[i] == byte.MaxValue && (destination[i + 1] == 21 || destination[i + 1] == 37))
			{
				int num5 = BitConverter.ToInt32(destination.Slice(i + 2, 4));
				ulong num6 = returnRip + (ulong)i;
				ulong num7 = num6 + 6 + (ulong)num5;
				if (cpuContext.TryReadUInt64(num7, out var value4))
				{
					Log.Debug($"Import#{dispatchIndex} near-indirect @{num6:X16}: slot=0x{num7:X16} val=0x{value4:X16}");
				}
			}
		}
		Span<byte> targetBytes = stackalloc byte[32];
		for (int i = 0; i + 5 <= destination.Length; i++)
		{
			if (destination[i] != 0xE8)
			{
				continue;
			}

			int rel32 = BitConverter.ToInt32(destination.Slice(i + 1, 4));
			ulong callRip = returnRip + (ulong)i;
			ulong target = unchecked((ulong)((long)(callRip + 5) + rel32));
			Log.Debug($"Import#{dispatchIndex} near-call @{callRip:X16}: target=0x{target:X16}");
			for (int importIndex = 0; importIndex < _importEntries.Length; importIndex++)
			{
				if (_importEntries[importIndex].Address != target)
				{
					continue;
				}

				string nid = _importEntries[importIndex].Nid;
				if (_moduleManager.TryGetExport(nid, out var export))
				{
					Log.Debug(
						$"Import#{dispatchIndex} near-call import: index={importIndex} {export.LibraryName}:{export.Name} ({nid})");
				}
				else
				{
					Log.Debug(
						$"Import#{dispatchIndex} near-call import: index={importIndex} nid={nid}");
				}
				break;
			}

			if (cpuContext.Memory.TryRead(target, targetBytes))
			{
				Log.Debug(
					$"Import#{dispatchIndex} near-call target bytes @0x{target:X16}: " +
					BitConverter.ToString(targetBytes.ToArray()).Replace("-", " "));
				if (targetBytes[0] == 0xFF && targetBytes[1] == 0x25)
				{
					int slotRel32 = BitConverter.ToInt32(targetBytes.Slice(2, 4));
					ulong slot = unchecked((ulong)((long)(target + 6) + slotRel32));
					if (cpuContext.TryReadUInt64(slot, out var slotTarget))
					{
						Log.Debug(
							$"Import#{dispatchIndex} near-call PLT slot: [0x{slot:X16}] = 0x{slotTarget:X16}");
						for (int importIndex = 0; importIndex < _importEntries.Length; importIndex++)
						{
							if (_importEntries[importIndex].Address != slotTarget)
							{
								continue;
							}

							string nid = _importEntries[importIndex].Nid;
							if (_moduleManager.TryGetExport(nid, out var export))
							{
								Log.Debug(
									$"Import#{dispatchIndex} near-call PLT import: index={importIndex} {export.LibraryName}:{export.Name} ({nid})");
							}
							else
							{
								Log.Debug(
									$"Import#{dispatchIndex} near-call PLT import: index={importIndex} nid={nid}");
							}
							break;
						}
					}
				}
			}
		}
	}

	private static bool IsUnresolvedSentinel(ulong value)
	{
		return value == 65534 || value == 4294967294u || value == 18446744073709551614uL;
	}

	private static ulong ParseOptionalHexAddress(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return 0;
		}

		var text = value.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			text = text[2..];
		}

		return ulong.TryParse(
			text,
			System.Globalization.NumberStyles.HexNumber,
			System.Globalization.CultureInfo.InvariantCulture,
			out var address)
			? address
			: 0;
	}

	private static bool IsPlausibleReturnAddress(ulong address)
	{
		return address >= 12884901888L && address < 17592186044416L && !IsUnresolvedSentinel(address);
	}

	private static bool TryGetPlausibleReturnFromStack(ulong rsp, out ulong returnRip, out ulong nextRsp)
	{
		returnRip = 0uL;
		nextRsp = rsp;
		if (rsp <= 65536 || rsp >= 140737488355328L)
		{
			return false;
		}
		ulong num = rsp & 0xFFFFFFFFFFFFFFF8uL;
		ulong num2 = ((num >= 8) ? (num - 8) : num);
		for (int i = 0; i < 24; i++)
		{
			ulong num3 = num2 + (ulong)((long)i * 8L);
			if (TryReadStackU64(num3, out var value) && IsLikelyReturnAddress(value))
			{
				returnRip = value;
				nextRsp = num3 + 8;
				return true;
			}
		}
		for (ulong num4 = 1uL; num4 < 8; num4++)
		{
			for (int j = 0; j < 24; j++)
			{
				ulong num5 = rsp + num4 + (ulong)((long)j * 8L);
				if (TryReadStackU64(num5, out var value2) && IsLikelyReturnAddress(value2))
				{
					returnRip = value2;
					ulong num6 = num5 + 8;
					nextRsp = (num6 + 7) & 0xFFFFFFFFFFFFFFF8uL;
					return true;
				}
			}
		}
		return false;
	}

	private unsafe static bool TryReadStackU64(ulong address, out ulong value)
	{
		value = 0uL;
		if (address <= 65536 || address >= 140737488355328L)
		{
			return false;
		}
		if (VirtualQuery((void*)address, out var lpBuffer, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) == 0)
		{
			return false;
		}
		ulong num = lpBuffer.BaseAddress + lpBuffer.RegionSize;
		if (num < lpBuffer.BaseAddress || address > num - 8)
		{
			return false;
		}
		if (lpBuffer.State != 4096 || !IsReadableProtection(lpBuffer.Protect))
		{
			return false;
		}
		try
		{
			value = *(ulong*)address;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsLikelyReturnAddress(ulong address)
	{
		if (!IsPlausibleReturnAddress(address))
		{
			return false;
		}
		return IsExecutableAddress(address);
	}

	private unsafe static bool IsExecutableAddress(ulong address)
	{
		var pageAddress = address & ~0xFFFUL;
		if (_knownExecutablePages.ContainsKey(pageAddress))
		{
			return true;
		}

		if (VirtualQuery((void*)address, out var lpBuffer, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) == 0)
		{
			return false;
		}

		var executable = lpBuffer.State == 4096 && IsExecutableProtection(lpBuffer.Protect);
		if (executable)
		{
			_knownExecutablePages.TryAdd(pageAddress, 0);
		}

		return executable;
	}

	private static ulong AlignUp(ulong value, ulong alignment)
	{
		if (alignment == 0)
		{
			return value;
		}
		ulong num = alignment - 1;
		return (value + num) & ~num;
	}

	private static bool IsReadableProtection(uint protect)
	{
		if ((protect & 0x100) != 0 || (protect & 1) != 0)
		{
			return false;
		}
		return (protect & 0xEE) != 0;
	}

	private static bool IsExecutableProtection(uint protect)
	{
		return (protect & 0xF0) != 0;
	}
}
