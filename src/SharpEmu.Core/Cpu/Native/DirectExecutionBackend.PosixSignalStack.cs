// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Runtime.InteropServices;
using Iced.Intel;
using SharpEmu.HLE;
using static Iced.Intel.AssemblerRegisters;

namespace SharpEmu.Core.Cpu.Native;

// Guest faults are delivered on the guest stack. The runtime only knows the thread's own stack,
// so managed code that runs on the guest stack can make a garbage collection misread the frames
// of that thread. Windows swaps the stack bounds in the TEB while guest code runs; POSIX cannot,
// so the signal handler moves to the host stack below the frames that entered guest code first.
public sealed unsafe partial class DirectExecutionBackend
{
	// Host stack below the saved host RSP that is still treated as the host frames in use.
	private const ulong HostStackSearchBytes = 0x100_0000;
	private const int SignalStackGap = 0x1000;

	// [0] the host RSP TLS slot (uint.MaxValue while unknown), [8] the Win64 TlsGetValue stub.
	private static nint _signalStackCells;

	private static unsafe nint CreatePosixSignalTrampoline()
	{
		_signalStackCells = (nint)NativeMemory.AllocZeroed(16);
		*(uint*)_signalStackCells = uint.MaxValue;

		var a = new Assembler(64);
		var direct = a.CreateLabel();
		var call = a.CreateLabel();
		var done = a.CreateLabel();
		a.push(rbx);
		a.push(r12);
		a.push(r13);
		a.push(r14);
		a.push(r15);
		a.mov(r12d, edi);
		a.mov(r13, rsi);
		a.mov(r14, rdx);
		a.mov(rbx, rsp);

		// The saved host RSP of this thread, when it entered guest code.
		a.mov(rax, (ulong)_signalStackCells);
		a.mov(ecx, __dword_ptr[rax]);
		a.cmp(ecx, -1);
		a.je(direct);
		a.mov(rax, __qword_ptr[rax + 8]);
		a.test(rax, rax);
		a.je(direct);
		a.sub(rsp, 48);
		a.call(rax);
		a.add(rsp, 48);
		a.test(rax, rax);
		a.je(direct);
		a.mov(r15, __qword_ptr[rax]);
		a.test(r15, r15);
		a.je(direct);

		// Already on the host stack below the saved RSP: an import or host code faulted.
		a.cmp(rbx, r15);
		a.jae(call);
		a.mov(rax, r15);
		a.sub(rax, rbx);
		a.cmp(rax, (int)HostStackSearchBytes);
		a.jb(direct);

		a.Label(ref call);
		a.lea(rsp, __[r15 - SignalStackGap]);
		a.and(rsp, -16);
		a.Label(ref direct);
		a.mov(edi, r12d);
		a.mov(rsi, r13);
		a.mov(rdx, r14);
		a.mov(rax, (ulong)(nint)(delegate* unmanaged<int, nint, nint, void>)&HandlePosixSignal);
		a.call(rax);
		a.Label(ref done);
		a.mov(rsp, rbx);
		a.pop(r15);
		a.pop(r14);
		a.pop(r13);
		a.pop(r12);
		a.pop(rbx);
		a.ret();

		var page = (byte*)HostMemory.Alloc(null, 4096, HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE, HostMemory.PAGE_EXECUTE_READWRITE);
		if (page == null)
		{
			throw new OutOfMemoryException("Failed to allocate the POSIX signal trampoline page");
		}

		var writer = new SpanCodeWriter(new Span<byte>(page, 4096));
		a.Assemble(writer, (ulong)page);
		if (!HostMemory.Protect(page, 4096, HostMemory.PAGE_EXECUTE_READ, out _))
		{
			throw new InvalidOperationException("Failed to protect the POSIX signal trampoline page");
		}

		return (nint)page;
	}

	private unsafe void PublishSignalStackState()
	{
		if (_signalStackCells == 0)
		{
			return;
		}

		*(nint*)(_signalStackCells + 8) = _tlsGetValueAddress;
		*(uint*)_signalStackCells = _hostRspSlotTlsIndex;
	}

	private sealed unsafe class SpanCodeWriter : CodeWriter
	{
		private readonly byte* _start;
		private readonly int _length;
		private int _offset;

		public SpanCodeWriter(Span<byte> destination)
		{
			fixed (byte* start = destination)
			{
				_start = start;
			}

			_length = destination.Length;
		}

		public override void WriteByte(byte value)
		{
			if (_offset >= _length)
			{
				throw new InvalidOperationException("The POSIX signal trampoline does not fit its page.");
			}

			_start[_offset++] = value;
		}
	}
}
