// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host;

public static unsafe class HostLaneReservation
{
    private const int RelationProcessorCore = 0;
    private const int ThreadPriorityHighest = 2;

    public static readonly ulong ReservedMask;
    public static readonly ulong GuestMask;

    static HostLaneReservation()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("SHARPEMU_RENDER_CORE") == "0")
        {
            return;
        }

        var lanes = Environment.ProcessorCount;
        if (lanes < 8 || lanes > 64)
        {
            return;
        }

        var core = SelectCore();
        var all = lanes == 64 ? ulong.MaxValue : (1UL << lanes) - 1;
        if (core == 0 || (core & ~all) != 0 || (all & ~core) == 0)
        {
            return;
        }

        ReservedMask = core;
        GuestMask = all & ~core;
    }

    public static bool Active => ReservedMask != 0;

    public static void ApplyToRenderThread()
    {
        if (!Active)
        {
            return;
        }

        var thread = GetCurrentThread();
        _ = SetThreadAffinityMask(thread, (nuint)ReservedMask);
        _ = SetThreadPriority(thread, ThreadPriorityHighest);
        Console.Error.WriteLine($"[LOADER][INFO] Render thread reserved host lanes 0x{ReservedMask:X}; guest threads use 0x{GuestMask:X}.");
    }

    public static void ApplyToGuestThread()
    {
        if (Active)
        {
            _ = SetThreadAffinityMask(GetCurrentThread(), (nuint)GuestMask);
        }
    }

    private static ulong SelectCore()
    {
        uint length = 0;
        _ = GetLogicalProcessorInformationEx(RelationProcessorCore, null, &length);
        if (length == 0)
        {
            return 0;
        }

        var buffer = new byte[length];
        fixed (byte* start = buffer)
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, start, &length))
            {
                return 0;
            }

            ulong selected = 0;
            var selectedClass = -1;
            for (uint offset = 0; offset + 48 <= length;)
            {
                var entry = start + offset;
                var size = *(uint*)(entry + 4);
                if (size == 0)
                {
                    break;
                }

                var efficiencyClass = entry[9];
                var groupCount = *(ushort*)(entry + 30);
                var mask = *(ulong*)(entry + 32);
                var group = *(ushort*)(entry + 40);
                if (*(int*)entry == RelationProcessorCore && groupCount == 1 && group == 0 && mask != 0 && efficiencyClass >= selectedClass)
                {
                    selected = mask;
                    selectedClass = efficiencyClass;
                }

                offset += size;
            }

            return selected;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, byte* buffer, uint* returnedLength);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint SetThreadAffinityMask(nint thread, nuint mask);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(nint thread, int priority);
}
