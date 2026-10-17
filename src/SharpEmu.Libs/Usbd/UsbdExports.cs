// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Usbd;

public static class UsbdExports
{
    private const int SceUsbdErrorInvalidParam = unchecked((int)0x80240002);

    [SysAbiExport(
        Nid = "TOhg7P6kTH4",
        ExportName = "sceUsbdInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUsbd")]
    public static int UsbdInit(CpuContext ctx)
    {
        // SharpEmu does not expose host USB devices yet. Initializing an empty
        // backend still succeeds, allowing games to probe optional peripherals
        // and fall back to their normal controller path.
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "Fq6+0Fm55xU",
        ExportName = "sceUsbdExit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUsbd")]
    public static int UsbdExit(CpuContext ctx)
    {
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "8qB9Ar4P5nc",
        ExportName = "sceUsbdGetDeviceList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUsbd")]
    public static int UsbdGetDeviceList(CpuContext ctx)
    {
        var listAddress = ctx[CpuRegister.Rdi];
        if (listAddress == 0 || !ctx.TryWriteUInt64(listAddress, 0))
        {
            return ctx.SetReturn(SceUsbdErrorInvalidParam);
        }

        // libusb_get_device_list returns a non-negative device count. Report an
        // empty list until a USB backend exists so wheel probing remains
        // optional instead of receiving an unresolved-import error sentinel.
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "EQ6SCLMqzkM",
        ExportName = "sceUsbdFreeDeviceList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUsbd")]
    public static int UsbdFreeDeviceList(CpuContext ctx)
    {
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "+wU6CGuZcWk",
        ExportName = "sceUsbdHandleEventsTimeout",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUsbd")]
    public static int UsbdHandleEventsTimeout(CpuContext ctx)
    {
        var timeoutAddress = ctx[CpuRegister.Rdi];
        if (timeoutAddress == 0 ||
            !ctx.TryReadUInt64(timeoutAddress, out var secondsValue) ||
            !ctx.TryReadUInt64(timeoutAddress + sizeof(ulong), out var microsecondsValue))
        {
            return ctx.SetReturn(SceUsbdErrorInvalidParam);
        }

        var seconds = unchecked((long)secondsValue);
        var microseconds = unchecked((long)microsecondsValue);
        if (seconds < 0 || microseconds is < 0 or >= 1_000_000)
        {
            return ctx.SetReturn(SceUsbdErrorInvalidParam);
        }

        // There is no USB backend yet. Match libusb's no-event timeout behavior
        // closely enough to keep guest event threads paced instead of turning
        // this import into a hot loop. With no devices/transfers to dispatch,
        // a timeout completes successfully after the requested interval.
        long totalMicroseconds;
        try
        {
            totalMicroseconds = checked(seconds * 1_000_000 + microseconds);
        }
        catch (OverflowException)
        {
            return ctx.SetReturn(SceUsbdErrorInvalidParam);
        }

        HostTiming.SleepMicroseconds(totalMicroseconds);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
