// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

/// <summary>
/// Native fast-path stubs for the trivial per-thread imports that dominate the
/// HLE call count. Each stub answers from <see cref="GuestFastPath"/>'s
/// per-host-thread block and jumps to the normal import trampoline whenever it
/// cannot, so the managed implementation stays the single source of truth for
/// every case the stub does not handle.
/// </summary>
public sealed unsafe partial class DirectExecutionBackend
{
	internal enum GuestFastPathStub
	{
		None,
		PthreadSelf,
		PthreadGetspecific,
		MemoryCopy,
	}

	// scePthreadSelf / pthread_self.
	private const string PthreadSelfNid = "aI+OeCz8xrQ";
	private const string PosixPthreadSelfNid = "EotR8a3ASf4";

	// scePthreadGetspecific / pthread_getspecific.
	private const string PthreadGetspecificNid = "eoht7mQOCmo";
	private const string PosixPthreadGetspecificNid = "0-KXaS70xy4";

	// memcpy / memmove.
	private const string MemcpyNid = "Q3VBxCXhUHs";
	private const string MemmoveNid = "+P6FRGH4LfA";

	// Guest memory is identity-mapped and every fault the copy can take (write tracking, lazy
	// commit) is resolved from the fault address alone, so the copy may run as guest-side code.
	// SHARPEMU_HLE_FAST_MEMCPY=0 keeps it on the managed export.
	private static readonly bool GuestMemoryCopyStubEnabled =
		!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_HLE_FAST_MEMCPY"), "0", StringComparison.Ordinal);

	private uint _fastPathBlockTlsIndex = uint.MaxValue;
	private bool _guestFastPathEnabled;
	private int _guestFastPathStubCount;

	/// <summary>
	/// Decides whether native fast-path stubs may be installed for this run and
	/// arms <see cref="GuestFastPath"/> if so. Any diagnostic mode that must
	/// observe every import call keeps the fast path off, because a stub answers
	/// without ever reaching the managed gateway that does the tracing.
	/// </summary>
	private void ConfigureGuestFastPath()
	{
		_guestFastPathEnabled = false;
		_guestFastPathStubCount = 0;

		// Emission uses the macOS pthread gs-indexed key array. Windows and
		// Linux keep the managed gateway until their TLS read is emitted too.
		if (!OperatingSystem.IsMacOS() ||
			RuntimeInformation.ProcessArchitecture != Architecture.X64)
		{
			return;
		}

		if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_HLE_FAST_PATH"), "0", StringComparison.Ordinal))
		{
			Console.Error.WriteLine("[LOADER][INFO] HLE native fast path disabled by SHARPEMU_HLE_FAST_PATH=0.");
			return;
		}

		if (_logAllImports ||
			!string.IsNullOrEmpty(_importFilter) ||
			string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PTHREADS"), "1", StringComparison.Ordinal))
		{
			Console.Error.WriteLine(
				"[LOADER][INFO] HLE native fast path disabled: import/pthread tracing needs every call in the gateway.");
			return;
		}

		if (_fastPathBlockTlsIndex == uint.MaxValue)
		{
			_fastPathBlockTlsIndex = TlsAlloc();
			if (_fastPathBlockTlsIndex == uint.MaxValue)
			{
				return;
			}
		}

		var tlsIndex = _fastPathBlockTlsIndex;
		GuestFastPath.Enable(block => TlsSetValue(tlsIndex, block));
		_guestFastPathEnabled = true;
	}

	private static GuestFastPathStub ClassifyGuestFastPathStub(string nid) => nid switch
	{
		PthreadSelfNid or PosixPthreadSelfNid => GuestFastPathStub.PthreadSelf,
		PthreadGetspecificNid or PosixPthreadGetspecificNid => GuestFastPathStub.PthreadGetspecific,
		MemcpyNid or MemmoveNid => GuestFastPathStub.MemoryCopy,
		_ => GuestFastPathStub.None,
	};

	/// <summary>
	/// Builds the native stub for <paramref name="nid"/>, falling back to
	/// <paramref name="fallbackTrampoline"/> (the import's managed gateway
	/// trampoline) for every case the stub declines.
	/// </summary>
	private unsafe bool TryCreateGuestFastPathStub(string nid, nint fallbackTrampoline, out nint address)
	{
		address = 0;
		if (!_guestFastPathEnabled || fallbackTrampoline == 0)
		{
			return false;
		}

		var kind = ClassifyGuestFastPathStub(nid);
		if (kind == GuestFastPathStub.None ||
			// The write watch inspects every managed guest write, which a native copy never reaches.
			(kind == GuestFastPathStub.MemoryCopy && (!GuestMemoryCopyStubEnabled || GuestWriteWatch.Armed)))
		{
			return false;
		}

		var code = EmitGuestFastPathStub(kind, _fastPathBlockTlsIndex, fallbackTrampoline);
		const uint stubAllocationSize = 128u;
		if (code.Count > stubAllocationSize)
		{
			return false;
		}

		void* memory = VirtualAlloc(null, stubAllocationSize, 12288u, 64u);
		if (memory == null)
		{
			return false;
		}

		for (var i = 0; i < code.Count; i++)
		{
			((byte*)memory)[i] = code[i];
		}

		uint oldProtect = 0;
		if (!VirtualProtect(memory, stubAllocationSize, 32u, &oldProtect))
		{
			VirtualFree(memory, 0u, 32768u);
			return false;
		}

		FlushInstructionCache(GetCurrentProcess(), memory, (nuint)code.Count);
		address = (nint)memory;
		_importHandlerTrampolines.Add(address);
		_guestFastPathStubCount++;
		return true;
	}

	/// <summary>
	/// Emits one fast-path stub. The stubs obey the SysV ABI the guest calls
	/// them with: the answer goes in RAX, only RAX and the arithmetic flags are
	/// clobbered (both caller-saved across a call), and on the slow path every
	/// register - including the incoming RAX that carries the variadic vector
	/// count - is restored before jumping to the trampoline.
	/// </summary>
	internal static List<byte> EmitGuestFastPathStub(
		GuestFastPathStub kind,
		uint blockTlsIndex,
		nint fallbackTrampoline)
	{
		if (kind == GuestFastPathStub.MemoryCopy)
		{
			return EmitGuestMemoryCopyStub(fallbackTrampoline);
		}

		var code = new List<byte>(48);
		var slowPathFixups = new List<int>();

		void Emit(params byte[] bytes) => code.AddRange(bytes);

		// Emits a short conditional branch to the slow path; 0x74 is JE, 0x73 is JAE.
		void EmitBranchToSlowPath(byte opcode)
		{
			Emit(opcode, 0x00); // rel8, patched once the slow-path offset is known
			slowPathFixups.Add(code.Count - 1);
		}

		void EmitJumpToSlowPath() => EmitBranchToSlowPath(0x74);

		// push rax: the guest's incoming RAX/AL must survive a slow-path bail.
		Emit(0x50);

		// mov rax, gs:[blockTlsIndex * 8] - the macOS pthread key array is the
		// gs-based thread specific data array, so a key read is a single load.
		Emit(0x65, 0x48, 0x8B, 0x04, 0x25);
		code.AddRange(BitConverter.GetBytes(checked(blockTlsIndex * 8u)));

		// test rax, rax / je slow: no block published on this host thread.
		Emit(0x48, 0x85, 0xC0);
		EmitJumpToSlowPath();

		switch (kind)
		{
			case GuestFastPathStub.PthreadSelf:
				// cmp qword [rax + SelfHandleOffset], 0 / je slow: no guest
				// thread bound here, and a host thread's pthread_self has to
				// reach the managed export for its scheduler registration.
				Emit(0x48, 0x83, 0x78, checked((byte)GuestFastPath.BlockSelfHandleOffset), 0x00);
				EmitJumpToSlowPath();
				// inc qword [rax + SelfHitsOffset]
				Emit(0x48, 0xFF, 0x40, checked((byte)GuestFastPath.BlockSelfHitsOffset));
				// mov rax, [rax + SelfHandleOffset]
				Emit(0x48, 0x8B, 0x40, checked((byte)GuestFastPath.BlockSelfHandleOffset));
				break;

			case GuestFastPathStub.PthreadGetspecific:
				// cmp qword [rax + TlsValuesOffset], 0 / je slow: this thread
				// has no value table published.
				Emit(0x48, 0x83, 0x78, checked((byte)GuestFastPath.BlockTlsValuesOffset), 0x00);
				EmitJumpToSlowPath();
				// cmp rdi, TlsSlotCount / jae slow. An out-of-range or
				// sign-extended negative key is answered by the managed path,
				// which truncates it to int and consults the key registry.
				Emit(0x48, 0x81, 0xFF);
				code.AddRange(BitConverter.GetBytes(GuestFastPath.TlsSlotCount));
				EmitBranchToSlowPath(0x73);
				// inc qword [rax + GetspecificHitsOffset]
				Emit(0x48, 0xFF, 0x40, checked((byte)GuestFastPath.BlockGetspecificHitsOffset));
				// mov rax, [rax + TlsValuesOffset] / mov rax, [rax + rdi*8]
				Emit(0x48, 0x8B, 0x40, checked((byte)GuestFastPath.BlockTlsValuesOffset));
				Emit(0x48, 0x8B, 0x04, 0xF8);
				break;

			default:
				throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown fast-path stub kind.");
		}

		// add rsp, 8 (drop the saved RAX - the result replaces it) / ret
		Emit(0x48, 0x83, 0xC4, 0x08);
		Emit(0xC3);

		var slowPathOffset = code.Count;

		// pop rax / jmp qword [rip+0] - the absolute target follows the
		// instruction, so nothing else is clobbered on the way out.
		Emit(0x58);
		Emit(0xFF, 0x25, 0x00, 0x00, 0x00, 0x00);
		code.AddRange(BitConverter.GetBytes((long)fallbackTrampoline));

		foreach (var fixup in slowPathFixups)
		{
			var delta = slowPathOffset - (fixup + 1);
			if (delta is < 0 or > 127)
			{
				throw new InvalidOperationException("Fast-path stub slow-path branch is out of rel8 range.");
			}

			code[fixup] = (byte)delta;
		}

		return code;
	}

	// SHARPEMU_MEMCPY_MANAGED_MIN overrides the size from which guest memcpy takes the managed path.
	internal static readonly int ManagedCopyMinimumBytes =
		int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_MEMCPY_MANAGED_MIN"), out var minimum) && minimum > 0 ? minimum : 4096;

	/// <summary>
	/// memcpy/memmove(dst, src, n) as a forward <c>rep movsb</c>, returning dst. The libc
	/// export copies with memmove semantics, so a destination that starts inside the source
	/// (where a forward copy would read bytes it already overwrote) takes the managed path.
	/// So does a copy of at least <see cref="ManagedCopyMinimumBytes"/>: the native copy takes
	/// one write-tracking fault per GPU-watched page it writes, a signal round trip of about
	/// 0.2 ms under Rosetta, while the managed copy marks the whole destination written first.
	/// Only RAX, RCX, RSI, RDI and the flags change - all caller-saved - and the direction
	/// flag is clear on entry per the SysV ABI.
	/// </summary>
	internal static List<byte> EmitGuestMemoryCopyStub(nint fallbackTrampoline)
	{
		var code = new List<byte>(48);
		var slowPathFixups = new List<int>();
		// mov rcx, rdi / sub rcx, rsi / cmp rcx, rdx / jb slow: dst - src < n (unsigned) is
		// exactly a destination in (src, src + n) or equal to src with a non-empty copy.
		code.AddRange([0x48, 0x89, 0xF9, 0x48, 0x29, 0xF1, 0x48, 0x39, 0xD1, 0x72, 0x00]);
		slowPathFixups.Add(code.Count - 1);
		// cmp rdx, imm32 / jae slow
		code.AddRange([0x48, 0x81, 0xFA]);
		code.AddRange(BitConverter.GetBytes(ManagedCopyMinimumBytes));
		code.AddRange([0x73, 0x00]);
		slowPathFixups.Add(code.Count - 1);
		// mov rax, rdi / mov rcx, rdx / rep movsb / ret
		code.AddRange([0x48, 0x89, 0xF8, 0x48, 0x89, 0xD1, 0xF3, 0xA4, 0xC3]);
		foreach (var fixup in slowPathFixups)
		{
			code[fixup] = checked((byte)(code.Count - (fixup + 1)));
		}

		// jmp qword [rip+0] with the absolute trampoline address after it.
		code.AddRange([0xFF, 0x25, 0x00, 0x00, 0x00, 0x00]);
		code.AddRange(BitConverter.GetBytes((long)fallbackTrampoline));
		return code;
	}
}
