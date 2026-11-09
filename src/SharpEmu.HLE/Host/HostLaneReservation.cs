// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host;

public static unsafe class HostLaneReservation
{
    private const int RelationProcessorCore = 0;
    private const int ThreadPriorityHighest = 2;
    private const int ThreadPriorityAboveNormal = 1;
    private const int LanesForTwoCores = 12;

    public static readonly ulong ReservedMask;
    public static readonly ulong FrontEndMask;
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

        var cores = SelectCores(lanes >= LanesForTwoCores ? 2 : 1);
        var render = cores.Count > 0 ? cores[0] : 0;
        var front = cores.Count > 1 ? cores[1] : 0;
        var reserved = render | front;
        var all = lanes == 64 ? ulong.MaxValue : (1UL << lanes) - 1;
        if (render == 0 || (reserved & ~all) != 0 || (all & ~reserved) == 0)
        {
            return;
        }

        ReservedMask = render;
        FrontEndMask = front;
        GuestMask = all & ~reserved;
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
        Console.Error.WriteLine($"[LOADER][INFO] Render thread reserved host lanes 0x{ReservedMask:X}; front end 0x{FrontEndMask:X}; guest threads use 0x{GuestMask:X}.");
    }

    public static void ApplyToFrontEndThread()
    {
        if (FrontEndMask == 0)
        {
            ApplyToGuestThread();
            return;
        }

        var thread = GetCurrentThread();
        _ = SetThreadAffinityMask(thread, (nuint)FrontEndMask);
        _ = SetThreadPriority(thread, ThreadPriorityAboveNormal);
    }

    public static void ApplyToGuestThread()
    {
        if (Active)
        {
            _ = SetThreadAffinityMask(GetCurrentThread(), (nuint)GuestMask);
        }
    }

    private static readonly bool HostWorkersRaised = Environment.GetEnvironmentVariable("SHARPEMU_HOST_WORKER_PRIORITY") != "0";

    public static void ApplyToHostWorkerThread()
    {
        if (!Active || !HostWorkersRaised)
        {
            return;
        }

        var thread = GetCurrentThread();
        _ = SetThreadAffinityMask(thread, (nuint)(GuestMask | FrontEndMask));
        _ = SetThreadPriority(thread, ThreadPriorityAboveNormal);
    }

    private static List<ulong> SelectCores(int count)
    {
        var selected = new List<ulong>();
        uint length = 0;
        _ = GetLogicalProcessorInformationEx(RelationProcessorCore, null, &length);
        if (length == 0)
        {
            return selected;
        }

        var buffer = new byte[length];
        fixed (byte* start = buffer)
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, start, &length))
            {
                return selected;
            }

            var cores = new List<(ulong Mask, int EfficiencyClass, int Order)>();
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
                if (*(int*)entry == RelationProcessorCore && groupCount == 1 && group == 0 && mask != 0)
                {
                    cores.Add((mask, efficiencyClass, cores.Count));
                }

                offset += size;
            }

            cores.Sort(static (left, right) => right.EfficiencyClass != left.EfficiencyClass
                ? right.EfficiencyClass.CompareTo(left.EfficiencyClass)
                : right.Order.CompareTo(left.Order));
            foreach (var core in cores)
            {
                if (selected.Count == count)
                {
                    break;
                }

                selected.Add(core.Mask);
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
