// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Runtime.InteropServices;

namespace SharpEmu.Core.Cpu.Native;

// On macOS the runtime receives EXC_BAD_ACCESS on each of its threads through a Mach exception
// port before the kernel raises SIGSEGV or SIGBUS. It calls any fault in the page holding RSP, or
// the page below it, a stack overflow and aborts, assuming RSP is on a stack it owns. Guest code
// runs on guest stacks, and those pages fault whenever GPU or image write tracking has protected
// them, so a thread running guest code must leave its access faults to the signal handler. The
// handler resolves guest faults and chains every other one to the runtime's own signal handler,
// the same path the runtime uses on Linux.
public sealed unsafe partial class DirectExecutionBackend
{
	private const uint ExceptionMaskBadAccess = 1u << 1;
	private const int ExceptionDefault = 1;
	private const int ThreadStateNone = 13;

	[ThreadStatic]
	private static bool _guestFaultsUseSignals;

	private static int _machExceptionPortWarning;

	[DllImport("libSystem.dylib", EntryPoint = "mach_thread_self")]
	private static extern uint MachThreadSelf();

	[DllImport("libSystem.dylib", EntryPoint = "thread_set_exception_ports")]
	private static extern int ThreadSetExceptionPorts(uint thread, uint exceptionMask, uint newPort, int behavior, int newFlavor);

	[DllImport("libSystem.dylib", EntryPoint = "mach_port_deallocate")]
	private static extern int MachPortDeallocate(uint task, uint name);

	private static readonly uint _machTaskSelf = OperatingSystem.IsMacOS()
		? (uint)Marshal.ReadInt32(NativeLibrary.GetExport(NativeLibrary.Load("libSystem.dylib"), "mach_task_self_"))
		: 0;

	private static void RouteGuestAccessFaultsToSignals()
	{
		if (_guestFaultsUseSignals || !OperatingSystem.IsMacOS())
			return;
		_guestFaultsUseSignals = true;
		var thread = MachThreadSelf();
		var result = ThreadSetExceptionPorts(thread, ExceptionMaskBadAccess, 0, ExceptionDefault, ThreadStateNone);
		_ = MachPortDeallocate(_machTaskSelf, thread);
		if (result != 0 && System.Threading.Interlocked.Exchange(ref _machExceptionPortWarning, 1) == 0)
			Console.Error.WriteLine($"[LOADER][WARN] Cannot hand guest access faults to the signal handler: thread_set_exception_ports={result}.");
	}
}
