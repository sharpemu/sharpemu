// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Pad;

public static class PadExports
{
    private const int OrbisPadErrorInvalidHandle = unchecked((int)0x80920003);
    private const int OrbisPadErrorNotInitialized = unchecked((int)0x80920005);
    private const int OrbisPadErrorDeviceNotConnected = unchecked((int)0x80920007);
    private const int OrbisPadErrorDeviceNoHandle = unchecked((int)0x80920008);
    private const int PrimaryUserId = 1;
    private const int StandardPortType = 0;
    private const int PrimaryPadHandle = 1;
    private const int ControllerInformationSize = 0x1C;
    private const int PadDataSize = 0x78;

    private static bool _initialized;

    [SysAbiExport(
        Nid = "hv1luiJrqQM",
        ExportName = "scePadInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadInit(CpuContext ctx)
    {
        _initialized = true;
        DualSenseReader.EnsureStarted();
        XInputReader.EnsureStarted();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "xk0AcarP3V4",
        ExportName = "scePadOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadOpen(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var index = unchecked((int)ctx[CpuRegister.Rdx]);
        var parameterAddress = ctx[CpuRegister.Rcx];
        if (!_initialized)
        {
            return ctx.SetReturn(OrbisPadErrorNotInitialized);
        }

        if (userId == -1)
        {
            return ctx.SetReturn(OrbisPadErrorDeviceNoHandle);
        }

        if (userId != PrimaryUserId || type != StandardPortType || index != 0 || parameterAddress != 0)
        {
            return ctx.SetReturn(OrbisPadErrorDeviceNotConnected);
        }

        DualSenseReader.EnsureStarted();
        XInputReader.EnsureStarted();
        Console.Error.WriteLine(DualSenseReader.TryGetState(out _)
            ? "[LOADER][INFO] Controls: DualSense connected (keyboard fallback also active)."
            : XInputReader.TryGetState(out _)
                ? "[LOADER][INFO] Controls: Xbox controller connected (keyboard fallback also active)."
                : "[LOADER][INFO] Keyboard controls: Arrow keys = D-pad, WASD = left stick, IJKL = right stick, Z/Enter = Cross, X/Esc = Circle, C = Square, V = Triangle, Q = L1, E = R1, R = L2, F = R2, Tab/Backspace = Options. A DualSense or Xbox controller will be used automatically when plugged in.");
        return ctx.SetReturn(PrimaryPadHandle);
    }

    [SysAbiExport(
        Nid = "clVvL4ZDntw",
        ExportName = "scePadSetMotionSensorState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetMotionSensorState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return handle == PrimaryPadHandle
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisPadErrorInvalidHandle);
    }

    [SysAbiExport(
        Nid = "gjP9-KQzoUk",
        ExportName = "scePadGetControllerInformation",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetControllerInformation(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var informationAddress = ctx[CpuRegister.Rsi];
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (informationAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> information = stackalloc byte[ControllerInformationSize];
        BinaryPrimitives.WriteSingleLittleEndian(information[0x00..], 44.86f);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x04..], 1920);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x06..], 943);
        information[0x08] = 30;
        information[0x09] = 30;
        information[0x0A] = StandardPortType;
        information[0x0B] = 1;
        information[0x0C] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(information[0x10..], 0);

        return ctx.Memory.TryWrite(informationAddress, information)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "YndgXqQVV7c",
        ExportName = "scePadReadState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadReadState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (dataAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WriteNeutralPadData(ctx, dataAddress)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "q1cHNfGycLI",
        ExportName = "scePadRead",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadRead(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        var count = unchecked((int)ctx[CpuRegister.Rdx]);
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (dataAddress == 0 || count < 1 || count > 64)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WriteNeutralPadData(ctx, dataAddress)
            ? ctx.SetReturn(1)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
    Nid = "W2G-yoyMF5U",
    ExportName = "scePadSetVibrationMode",
    Target = Generation.Gen4 | Generation.Gen5,
    LibraryName = "libScePad")]
    public static int PadSetVibrationMode(CpuContext ctx)
    {
        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "yFVnOdGxvZY",
        ExportName = "scePadSetVibration",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetVibration(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (parameterAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // ScePadVibrationParam: { uint8_t largeMotor; uint8_t smallMotor; }
        Span<byte> parameter = stackalloc byte[2];
        if (!ctx.Memory.TryRead(parameterAddress, parameter))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        DualSenseReader.SetRumble(parameter[0], parameter[1]);
        XInputReader.SetRumble(parameter[0], parameter[1]);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "RR4novUEENY",
        ExportName = "scePadSetLightBar",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetLightBar(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (parameterAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // ScePadColor: { uint8_t r; uint8_t g; uint8_t b; uint8_t reserved; }
        Span<byte> color = stackalloc byte[4];
        if (!ctx.Memory.TryRead(parameterAddress, color))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        DualSenseReader.SetLightbar(color[0], color[1], color[2]);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "DscD1i9HX1w",
        ExportName = "scePadResetLightBar",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadResetLightBar(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        DualSenseReader.ResetLightbar();
        return ctx.SetReturn(0);
    }

    private static bool WriteNeutralPadData(CpuContext ctx, ulong dataAddress)
    {
        Span<byte> data = stackalloc byte[PadDataSize];
        data.Clear();
        var acceptsKeyboardInput = IsEmulatorWindowFocused();
        var buttons = acceptsKeyboardInput ? ReadKeyboardButtons() : 0;
        var leftX = acceptsKeyboardInput ? ReadAnalogStick(IsKeyDown(0x41), IsKeyDown(0x44)) : (byte)128;
        var leftY = acceptsKeyboardInput ? ReadAnalogStick(IsKeyDown(0x57), IsKeyDown(0x53)) : (byte)128;
        var rightX = acceptsKeyboardInput ? ReadAnalogStick(IsKeyDown(0x4A), IsKeyDown(0x4C)) : (byte)128;
        var rightY = acceptsKeyboardInput ? ReadAnalogStick(IsKeyDown(0x49), IsKeyDown(0x4B)) : (byte)128;
        var l2 = acceptsKeyboardInput && IsKeyDown(0x52) ? (byte)255 : (byte)0;
        var r2 = acceptsKeyboardInput && IsKeyDown(0x46) ? (byte)255 : (byte)0;

        if (DualSenseReader.TryGetState(out var pad))
        {
            buttons |= pad.Buttons;
            // The controller stick wins whenever it is deflected past a
            // small deadzone; otherwise any keyboard value stays.
            leftX = MergeAxis(pad.LeftX, leftX);
            leftY = MergeAxis(pad.LeftY, leftY);
            rightX = MergeAxis(pad.RightX, rightX);
            rightY = MergeAxis(pad.RightY, rightY);
            l2 = Math.Max(l2, pad.L2);
            r2 = Math.Max(r2, pad.R2);
        }

        if (XInputReader.TryGetState(out var xpad))
        {
            buttons |= xpad.Buttons;
            leftX = MergeAxis(xpad.LeftX, leftX);
            leftY = MergeAxis(xpad.LeftY, leftY);
            rightX = MergeAxis(xpad.RightX, rightX);
            rightY = MergeAxis(xpad.RightY, rightY);
            l2 = Math.Max(l2, xpad.L2);
            r2 = Math.Max(r2, xpad.R2);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(data[0x00..], buttons);
        data[0x04] = leftX;
        data[0x05] = leftY;
        data[0x06] = rightX;
        data[0x07] = rightY;
        data[0x08] = l2;
        data[0x09] = r2;
        BinaryPrimitives.WriteSingleLittleEndian(data[0x18..], 1.0f);
        data[0x4C] = 1;
        var timestampTicks = Stopwatch.GetTimestamp();
        var timestampMicroseconds =
            ((ulong)(timestampTicks / Stopwatch.Frequency) * 1_000_000UL) +
            ((ulong)(timestampTicks % Stopwatch.Frequency) * 1_000_000UL / (ulong)Stopwatch.Frequency);
        BinaryPrimitives.WriteUInt64LittleEndian(
            data[0x50..],
            timestampMicroseconds);
        data[0x68] = 1;

        return ctx.Memory.TryWrite(dataAddress, data);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    private static bool IsKeyDown(int vk) =>
        (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsEmulatorWindowFocused()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == 0)
        {
            return false;
        }

        GetWindowThreadProcessId(foregroundWindow, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private static uint ReadKeyboardButtons()
    {
        uint buttons = 0;
        // D-pad
        if (IsKeyDown(0x25)) buttons |= 0x0080; // Left
        if (IsKeyDown(0x27)) buttons |= 0x0020; // Right
        if (IsKeyDown(0x26)) buttons |= 0x0010; // Up
        if (IsKeyDown(0x28)) buttons |= 0x0040; // Down
        // Face buttons
        if (IsKeyDown(0x5A) || IsKeyDown(0x0D)) buttons |= 0x4000; // Z / Enter = Cross
        if (IsKeyDown(0x58) || IsKeyDown(0x1B)) buttons |= 0x2000; // X / Escape = Circle
        if (IsKeyDown(0x43)) buttons |= 0x8000; // C = Square
        if (IsKeyDown(0x56)) buttons |= 0x1000; // V = Triangle
        // Shoulder buttons
        if (IsKeyDown(0x51)) buttons |= 0x0400; // Q = L1
        if (IsKeyDown(0x45)) buttons |= 0x0800; // E = R1
        if (IsKeyDown(0x52)) buttons |= 0x0100; // R = L2 (digital)
        if (IsKeyDown(0x46)) buttons |= 0x0200; // F = R2 (digital)
        // Options (Start)
        if (IsKeyDown(0x09) || IsKeyDown(0x08)) buttons |= 0x0008; // Tab / Backspace = Options
        return buttons;
    }

    private static byte ReadAnalogStick(bool negative, bool positive)
    {
        if (negative && !positive) return 0;
        if (positive && !negative) return 255;
        return 128;
    }

    private static byte MergeAxis(byte controller, byte keyboard)
    {
        const int Deadzone = 10;
        return Math.Abs(controller - 128) > Deadzone ? controller : keyboard;
    }
}
