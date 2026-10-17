// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime;
using System.Runtime.InteropServices;

namespace SharpEmu.GUI;

internal static partial class GuiMemory
{
    public static void Release()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
            }
            else if (OperatingSystem.IsLinux())
            {
                MallocTrim(0);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);

    [LibraryImport("libc", EntryPoint = "malloc_trim")]
    private static partial int MallocTrim(nuint pad);
}
