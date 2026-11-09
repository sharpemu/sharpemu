// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Pad;

internal static class PadInputChangeTrace
{
    private const int MaximumLoggedChanges = 256;
    private const int MaximumLoggedHandleEvents = 64;
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_PAD_INPUT"), "1", StringComparison.Ordinal);
    private static readonly object TraceGate = new();
    private static readonly Dictionary<int, PadInputSnapshot> LastSnapshots = [];
    private static readonly HashSet<PadHandleLookupResult> ObservedGetHandleResults = [];
    private static int _loggedChanges;
    private static int _loggedHandleEvents;

    public static void RecordHandleResult(string exportName, int userId, int portType, int portIndex, int result)
    {
        if (!Enabled)
        {
            return;
        }

        lock (TraceGate)
        {
            if (_loggedHandleEvents >= MaximumLoggedHandleEvents)
            {
                return;
            }

            if (exportName == "scePadGetHandle" &&
                !ObservedGetHandleResults.Add(new PadHandleLookupResult(userId, portType, portIndex, result)))
            {
                return;
            }

            WriteHandleEvent(
                $"export={exportName} user=0x{unchecked((uint)userId):X8} " +
                $"port_type={portType} port_index={portIndex} result=0x{unchecked((uint)result):X8}");
        }
    }

    public static void RecordClose(int handle, int result)
    {
        if (!Enabled)
        {
            return;
        }

        lock (TraceGate)
        {
            if (_loggedHandleEvents >= MaximumLoggedHandleEvents)
            {
                return;
            }

            WriteHandleEvent($"export=scePadClose handle={handle} result=0x{unchecked((uint)result):X8}");
        }
    }

    private static void WriteHandleEvent(string details)
    {
        if (_loggedHandleEvents >= MaximumLoggedHandleEvents)
        {
            return;
        }

        _loggedHandleEvents++;
        Console.Error.WriteLine($"[LOADER][TRACE] pad_handle {details}");
        if (_loggedHandleEvents == MaximumLoggedHandleEvents)
        {
            Console.Error.WriteLine("[LOADER][TRACE] pad_handle trace limit reached");
        }
    }

    public static void Record(string exportName, int handle, PadState input, bool guestWriteSucceeded)
    {
        if (!Enabled || handle < 0)
        {
            return;
        }

        var snapshot = new PadInputSnapshot(
            input.HostWindowFocused,
            input.KeyboardButtons,
            input.ControllerButtons,
            input.Buttons,
            guestWriteSucceeded);

        lock (TraceGate)
        {
            if (_loggedChanges >= MaximumLoggedChanges)
            {
                return;
            }

            if (LastSnapshots.TryGetValue(handle, out var previousSnapshot) && previousSnapshot == snapshot)
            {
                return;
            }

            LastSnapshots[handle] = snapshot;
            _loggedChanges++;
            Console.Error.WriteLine(
                $"[LOADER][TRACE] pad_input export={exportName} handle={handle} " +
                $"focused={snapshot.HostWindowFocused} keyboard=0x{snapshot.KeyboardButtons:X8} " +
                $"controller=0x{snapshot.ControllerButtons:X8} guest=0x{snapshot.GuestButtons:X8} " +
                $"write={(snapshot.GuestWriteSucceeded ? "ok" : "failed")}");
            if (_loggedChanges == MaximumLoggedChanges)
            {
                Console.Error.WriteLine("[LOADER][TRACE] pad_input change trace limit reached");
            }
        }
    }

    private readonly record struct PadInputSnapshot(
        bool HostWindowFocused,
        uint KeyboardButtons,
        uint ControllerButtons,
        uint GuestButtons,
        bool GuestWriteSucceeded);

    private readonly record struct PadHandleLookupResult(int UserId, int PortType, int PortIndex, int Result);
}
