// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Pad;

// Minimal libSceBluetoothHid stub: no host Bluetooth passthrough, so report
// success and let the Pad path provide input (SHARPEMU_BTHID_UNAVAILABLE=1 fails instead).
public static class BluetoothHidExports
{
    private const int BluetoothHidUnavailable = unchecked((int)0x80960001);

    private static readonly bool _reportUnavailable = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_BTHID_UNAVAILABLE"),
        "1",
        StringComparison.Ordinal);

    // EXPERIMENT: fire the registered callback once with a zeroed event struct.
    // Direct execution shares the host address space with the guest, so an
    // AllocHGlobal buffer is directly readable by guest code.
    // SHARPEMU_BTHID_FIRE_CALLBACK=1 enables; SHARPEMU_BTHID_EVENT_CODE and
    // SHARPEMU_BTHID_EVENT_SIZE (default 0 / 256) shape the synthetic event.
    private static readonly bool _fireCallback = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_BTHID_FIRE_CALLBACK"),
        "1",
        StringComparison.Ordinal);

    private static ulong _callbackFunction;
    private static int _fired;

    private static int Result(CpuContext ctx) =>
        ctx.SetReturn(_reportUnavailable ? BluetoothHidUnavailable : 0);

    [SysAbiExport(
        Nid = "tul3-GzejQc",
        ExportName = "sceBluetoothHidInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceBluetoothHid")]
    public static int BluetoothHidInit(CpuContext ctx) => Result(ctx);

    [SysAbiExport(
        Nid = "4FUZ+c52d2k",
        ExportName = "sceBluetoothHidRegisterDevice",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceBluetoothHid")]
    public static int BluetoothHidRegisterDevice(CpuContext ctx)
    {
        var result = Result(ctx);
        if (_fireCallback && _callbackFunction >= 0x10000 &&
            Interlocked.Exchange(ref _fired, 1) == 0)
        {
            FireEnumerationComplete(ctx);
        }

        return result;
    }

    [SysAbiExport(
        Nid = "4Ypfo9RIwfM",
        ExportName = "sceBluetoothHidRegisterCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceBluetoothHid")]
    public static int BluetoothHidRegisterCallback(CpuContext ctx)
    {
        _callbackFunction = ctx[CpuRegister.Rdi];

        // EXPERIMENT: failing ONLY the callback registration (with a generic kernel
        // error, unlike the BT-specific unavailable code) may make wheel/FFB
        // middleware disable its Bluetooth search loop instead of polling forever.
        // SHARPEMU_BTHID_CB_FAIL=nf -> NOT_FOUND, =ni -> NOT_IMPLEMENTED.
        var mode = Environment.GetEnvironmentVariable("SHARPEMU_BTHID_CB_FAIL");
        if (string.Equals(mode, "nf", StringComparison.OrdinalIgnoreCase))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (string.Equals(mode, "ni", StringComparison.OrdinalIgnoreCase))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED);
        }

        return Result(ctx);
    }

    private static void FireEnumerationComplete(CpuContext ctx)
    {
        var scheduler = GuestThreadExecution.Scheduler;
        if (scheduler is null)
        {
            return;
        }

        var eventCode = ParseEnvUInt64("SHARPEMU_BTHID_EVENT_CODE", 0);
        var eventSize = (int)ParseEnvUInt64("SHARPEMU_BTHID_EVENT_SIZE", 256);
        if (eventSize < 1)
        {
            eventSize = 256;
        }

        // Leaked by design: the guest may retain the pointer past the callback.
        var eventStruct = Marshal.AllocHGlobal(eventSize);
        for (var offset = 0; offset < eventSize; offset++)
        {
            Marshal.WriteByte(eventStruct, offset, 0);
        }

        Console.Error.WriteLine(
            $"[BTHID][EXPERIMENT] firing callback=0x{_callbackFunction:X} code={eventCode} size={eventSize} struct=0x{eventStruct.ToInt64():X}");
        if (!scheduler.TryCallGuestFunction(
                ctx,
                _callbackFunction,
                unchecked((ulong)eventStruct.ToInt64()),
                eventCode,
                0,
                0,
                "sceBluetoothHid synthetic enumeration event",
                out var error))
        {
            Console.Error.WriteLine($"[BTHID][EXPERIMENT] callback failed: {error}");
        }
        else
        {
            Console.Error.WriteLine("[BTHID][EXPERIMENT] callback returned OK");
        }
    }

    private static ulong ParseEnvUInt64(string name, ulong fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        value = value.Trim();
        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return ulong.TryParse(
            hex ? value[2..] : value,
            hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
            null,
            out var parsed) ? parsed : fallback;
    }
}
