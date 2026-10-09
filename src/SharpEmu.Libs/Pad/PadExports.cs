// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using System.Buffers.Binary;
using System.Diagnostics;

namespace SharpEmu.Libs.Pad;

public static class PadExports
{
    private const int OrbisPadErrorInvalidHandle = unchecked((int)0x80920003);
    private const int OrbisPadErrorInvalidArgument = unchecked((int)0x80920001);
    private const int OrbisPadErrorAlreadyOpened = unchecked((int)0x80920004);
    private const int OrbisPadErrorNotInitialized = unchecked((int)0x80920005);
    private const int OrbisPadErrorDeviceNotConnected = unchecked((int)0x80920007);
    private const int OrbisPadErrorDeviceNoHandle = unchecked((int)0x80920008);
    // Keep the pad session on the same retail user id returned by
    // libSceUserService.  A mismatched emulator-local id makes games pass a
    // valid 0x10000000 user to scePadOpen and receive DEVICE_NOT_CONNECTED,
    // leaving every later keyboard/gamepad read on an invalid handle.
    private const int PrimaryUserId = 0x10000000;
    private const int SystemUserId = 0xFF;
    private const int StandardPortType = 0;
    private const int SpecialPortType = 2;
    private const int RemotePortType = 16;
    private const int ControllerInformationSize = 0x1C;
    private const int PadDataSize = 0x78;
    private const float StandardGravity = 9.80665f;

    // Monster Truck Championship reads pad state with handle 0 before it opens a pad,
    // and rejecting that leaves its controller/FFB init path polling forever. After a
    // title opens a pad, handle 0 is a slot it never opened: Unreal Engine polls all four
    // user slots and treats every valid handle as a connected controller, so accepting
    // 0 there reports phantom pads that repeat the primary pad's buttons.
    private static int _padOpened;
    private static readonly object PadHandleGate = new();
    private static readonly Dictionary<PadOpenKey, int> PadHandleByKey = new();
    private static readonly Dictionary<int, PadOpenKey> PadKeyByHandle = new();
    private static int _nextPadHandle = 1;

    private readonly record struct PadOpenKey(int UserId, int Type, int Index);

    private static bool IsOpenPadHandle(int handle)
    {
        lock (PadHandleGate)
        {
            return PadKeyByHandle.ContainsKey(handle) || (handle == 0 && _padOpened == 0);
        }
    }

    internal static void ResetOpenedPadForTests()
    {
        lock (PadHandleGate)
        {
            PadHandleByKey.Clear();
            PadKeyByHandle.Clear();
            _nextPadHandle = 1;
            _padOpened = 0;
            _initialized = false;
        }
    }
    private static readonly long InputSampleIntervalTicks = Math.Max(1, Stopwatch.Frequency / 1000);

    [ThreadStatic]
    private static long _lastInputSampleTicks;

    [ThreadStatic]
    private static PadState _cachedInputState;

    private static bool _initialized;
    private static readonly bool LogPadState =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PAD_STATE"), "1", StringComparison.Ordinal);
    private static long _loggedPadSignature = -1;
    // Motion data is reported until a title turns it off: Astro Bot reads it for
    // shake/tilt without ever importing scePadSetMotionSensorState.
    private static int _motionSensorEnabled = 1;
    private static int _controlsAnnouncementLogged;

    [SysAbiExport(
        Nid = "hv1luiJrqQM",
        ExportName = "scePadInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadInit(CpuContext ctx)
    {
        _initialized = true;
        HostPlatform.Current.Input.EnsureStarted();
        return ctx.SetReturn(0);
    }

    #pragma warning disable SHEM006
    [SysAbiExport(
        Nid = "n3kSX62fgNo",
        ExportName = "scePadUnknownN3kSX62fgNo",
        Target = Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadUnknownN3kSX62fgNo(CpuContext ctx)
    {
        return ctx.SetReturn(0);
    }
    #pragma warning restore SHEM006

    [SysAbiExport(
        Nid = "xk0AcarP3V4",
        ExportName = "scePadOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadOpen(CpuContext ctx) => PadOpenCore(ctx, extended: false);

    [SysAbiExport(
        Nid = "WFIiSfXGUq8",
        ExportName = "scePadOpenExt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadOpenExt(CpuContext ctx) => PadOpenCore(ctx, extended: true);

    [SysAbiExport(
        Nid = "u1GRHp+oWoY",
        ExportName = "scePadGetHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetHandle(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var index = unchecked((int)ctx[CpuRegister.Rdx]);
        if (!_initialized)
        {
            return ctx.SetReturn(OrbisPadErrorNotInitialized);
        }

        if (userId == -1)
        {
            return ctx.SetReturn(OrbisPadErrorDeviceNoHandle);
        }

        lock (PadHandleGate)
        {
            return PadHandleByKey.TryGetValue(new PadOpenKey(userId, type, index), out var handle)
                ? ctx.SetReturn(handle)
                : ctx.SetReturn(OrbisPadErrorDeviceNoHandle);
        }
    }

    private static int PadOpenCore(CpuContext ctx, bool extended)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var index = unchecked((int)ctx[CpuRegister.Rdx]);
        if (!_initialized)
        {
            return ctx.SetReturn(OrbisPadErrorNotInitialized);
        }

        if (userId == -1)
        {
            return ctx.SetReturn(OrbisPadErrorDeviceNoHandle);
        }

        if (!IsSupportedOpenTuple(userId, type, index, extended))
        {
            return ctx.SetReturn(OrbisPadErrorDeviceNotConnected);
        }

        var input = HostPlatform.Current.Input;
        input.EnsureStarted();
        if (Interlocked.Exchange(ref _controlsAnnouncementLogged, 1) == 0)
        {
            Console.Error.WriteLine(input.DescribeConnectedGamepad() is { } gamepadName
                ? $"[LOADER][INFO] Controls: {gamepadName} connected (keyboard fallback also active)."
                : "[LOADER][INFO] Keyboard controls: Arrow keys = D-pad, WASD = left stick, IJKL = right stick, Z/Enter = Cross, X/Esc = Circle, C = Square, V = Triangle, Q = L1, E = R1, R = L2, F = R2, Tab/Backspace = Options. A DualSense or Xbox controller will be used automatically when plugged in.");
        }

        var key = new PadOpenKey(userId, type, index);
        lock (PadHandleGate)
        {
            if (PadHandleByKey.ContainsKey(key))
            {
                return ctx.SetReturn(OrbisPadErrorAlreadyOpened);
            }

            var handle = _nextPadHandle++;
            PadHandleByKey.Add(key, handle);
            PadKeyByHandle.Add(handle, key);
            _padOpened = 1;
            return ctx.SetReturn(handle);
        }
    }

    private static bool IsSupportedOpenTuple(int userId, int type, int index, bool extended)
    {
        if (index != 0)
        {
            return false;
        }

        if (userId == SystemUserId && type == RemotePortType)
        {
            return true;
        }

        if (userId != PrimaryUserId)
        {
            return false;
        }

        return extended
            ? type is 0 or 1 or 2
            : type is StandardPortType or SpecialPortType;
    }

    [SysAbiExport(
        Nid = "6ncge5+l5Qs",
        ExportName = "scePadClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadClose(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (PadHandleGate)
        {
            if (!PadKeyByHandle.Remove(handle, out var key))
            {
                return ctx.SetReturn(OrbisPadErrorInvalidHandle);
            }

            PadHandleByKey.Remove(key);
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(
        Nid = "clVvL4ZDntw",
        ExportName = "scePadSetMotionSensorState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetMotionSensorState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsOpenPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        Volatile.Write(ref _motionSensorEnabled, ctx[CpuRegister.Rsi] != 0 ? 1 : 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "vDLMoJLde8I",
        ExportName = "scePadSetTiltCorrectionState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetTiltCorrectionState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return IsOpenPadHandle(handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisPadErrorInvalidHandle);
    }

    [SysAbiExport(
        Nid = "r44mAxdSG+U",
        ExportName = "scePadSetAngularVelocityDeadbandState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetAngularVelocityDeadbandState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return IsOpenPadHandle(handle)
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
        if (!IsOpenPadHandle(handle))
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
        Nid = "PZSoY8j0Pko",
        ExportName = "scePadGetFeatureReport",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetFeatureReport(CpuContext ctx) =>
        ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        Nid = "hGbf2QTBmqc",
        ExportName = "scePadGetExtControllerInformation",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetExtControllerInformation(CpuContext ctx) =>
        ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        Nid = "AcslpN1jHR8",
        ExportName = "scePadDeviceClassGetExtendedInformation",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadDeviceClassGetExtendedInformation(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var informationAddress = ctx[CpuRegister.Rsi];
        if (!IsOpenPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (informationAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // ScePadDeviceClassExtendedInformation: deviceClass 0 = standard pad
        // (DualSense). We emulate no special peripheral (guitar/drums/wheel), so
        // the class-data union stays zeroed — the guest treats it as a plain
        // controller with no extended capabilities.
        Span<byte> information = stackalloc byte[0x14];
        information.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(information[0x00..], 0);

        return ctx.Memory.TryWrite(informationAddress, information)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "IHPqcbc0zCA",
        ExportName = "scePadDeviceClassParseData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadDeviceClassParseData(CpuContext ctx)
    {
        const int deviceClassDataSize = 24;
        const int deviceUniqueDataLengthOffset = 0x6B;
        const int deviceUniqueDataOffset = 0x6C;
        const int maximumDeviceUniqueDataLength = 12;

        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var padDataAddress = ctx[CpuRegister.Rsi];
        var classDataAddress = ctx[CpuRegister.Rdx];
        if (!IsOpenPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (padDataAddress == 0 || classDataAddress == 0)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidArgument);
        }

        Span<byte> padData = stackalloc byte[PadDataSize];
        if (!ctx.Memory.TryRead(padDataAddress, padData))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Span<byte> classData = stackalloc byte[deviceClassDataSize];
        classData.Clear();
        classData[0x04] = padData[0x4C] != 0 ? (byte)1 : (byte)0;

        var uniqueDataLength = Math.Min(
            (int)padData[deviceUniqueDataLengthOffset],
            maximumDeviceUniqueDataLength);
        if (uniqueDataLength > 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(classData[0x00..], -1);
            classData[0x08] = (byte)uniqueDataLength;
            padData.Slice(deviceUniqueDataOffset, uniqueDataLength)
                .CopyTo(classData.Slice(0x0C, uniqueDataLength));
        }

        return ctx.Memory.TryWrite(classDataAddress, classData)
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
        if (!IsOpenPadHandle(handle))
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
        if (!IsOpenPadHandle(handle))
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
        Nid = "2JgFB2n9oUM",
        ExportName = "scePadSetTriggerEffect",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetTriggerEffect(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (!IsOpenPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (parameterAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> parameter = stackalloc byte[120];
        if (!ctx.Memory.TryRead(parameterAddress, parameter))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var triggerMask = parameter[0];
        HostPlatform.Current.Input.SetAdaptiveTriggerEffect(
            (triggerMask & 0x01) != 0 ? DecodeTriggerEffect(parameter[8..64]) : null,
            (triggerMask & 0x02) != 0 ? DecodeTriggerEffect(parameter[64..120]) : null);
        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // Size taken from the caller's own frame rather than assumed: the guest
    // reserves 0x10 bytes, points the out-param at rbp-0x30, and stores its
    // stack cookie at rbp-0x28, so only eight bytes belong to the state. A
    // sixteen-byte write would land on the cookie and fail the stack check.
    private const int TriggerEffectStateSize = 8;

    [SysAbiExport(
        Nid = "znaWI0gpuo8",
        ExportName = "scePadGetTriggerEffectState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetTriggerEffectState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var stateAddress = ctx[CpuRegister.Rsi];
        if (!IsOpenPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (stateAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // No host pad exposes DualSense adaptive-trigger feedback, so every
        // trigger reports the neutral "no effect engaged" state. Reporting it
        // as success is what lets the caller take its normal path instead of
        // falling back to a cached button bitmask every poll.
        Span<byte> state = stackalloc byte[TriggerEffectStateSize];
        state.Clear();
        return ctx.Memory.TryWrite(stateAddress, state)
            ? ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static HostAdaptiveTriggerEffect DecodeTriggerEffect(ReadOnlySpan<byte> command)
    {
        var mode = BinaryPrimitives.ReadUInt32LittleEndian(command);
        var parameters = command[8..];
        Span<byte> native = stackalloc byte[11];
        native.Clear();
        byte fallbackStrength = 0;
        switch (mode)
        {
            case 1:
                EncodeFeedback(native, parameters[0], parameters[1]);
                fallbackStrength = ScaleTriggerStrength(parameters[1]);
                break;
            case 2:
                EncodeWeapon(native, parameters[0], parameters[1], parameters[2]);
                fallbackStrength = ScaleTriggerStrength(parameters[2]);
                break;
            case 3:
                EncodeZonedEffect(native, 0x26, parameters[0], parameters[1], parameters[2]);
                fallbackStrength = ScaleTriggerStrength(parameters[1]);
                break;
            case 4:
                EncodeZonedStrengths(native, 0x21, parameters[..10], 0);
                fallbackStrength = ScaleTriggerStrength(Max(parameters[..10]));
                break;
            case 5:
                EncodeSlope(native, parameters[0], parameters[1], parameters[2], parameters[3]);
                fallbackStrength = ScaleTriggerStrength(Math.Max(parameters[2], parameters[3]));
                break;
            case 6:
                EncodeZonedStrengths(native, 0x26, parameters[1..11], parameters[0]);
                fallbackStrength = parameters[0] == 0 ? (byte)0 : ScaleTriggerStrength(Max(parameters[1..11]));
                break;
            default:
                native[0] = 0x05;
                break;
        }

        return HostAdaptiveTriggerEffect.FromBytes(native, fallbackStrength);
    }

    private static void EncodeFeedback(Span<byte> destination, byte position, byte strength)
    {
        if (position > 9 || strength is 0 or > 8)
        {
            destination[0] = 0x05;
            return;
        }

        Span<byte> strengths = stackalloc byte[10];
        strengths[position..].Fill(strength);
        EncodeZonedStrengths(destination, 0x21, strengths, 0);
    }

    private static void EncodeWeapon(Span<byte> destination, byte start, byte end, byte strength)
    {
        if (start is < 2 or > 7 || end <= start || end > 8 || strength is 0 or > 8)
        {
            destination[0] = 0x05;
            return;
        }

        var zones = (ushort)((1 << start) | (1 << end));
        destination[0] = 0x25;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..], zones);
        destination[3] = (byte)(strength - 1);
    }

    private static void EncodeZonedEffect(
        Span<byte> destination,
        byte nativeMode,
        byte position,
        byte strength,
        byte frequency)
    {
        if (position > 9 || strength is 0 or > 8 || frequency == 0)
        {
            destination[0] = 0x05;
            return;
        }

        Span<byte> strengths = stackalloc byte[10];
        strengths[position..].Fill(strength);
        EncodeZonedStrengths(destination, nativeMode, strengths, frequency);
    }

    private static void EncodeSlope(
        Span<byte> destination,
        byte startPosition,
        byte endPosition,
        byte startStrength,
        byte endStrength)
    {
        if (startPosition > 8 || endPosition <= startPosition || endPosition > 9 ||
            startStrength is 0 or > 8 || endStrength is 0 or > 8)
        {
            destination[0] = 0x05;
            return;
        }

        Span<byte> strengths = stackalloc byte[10];
        var distance = endPosition - startPosition;
        for (var index = startPosition; index < strengths.Length; index++)
        {
            strengths[index] = index <= endPosition
                ? (byte)Math.Round(startStrength + ((endStrength - startStrength) * (index - startPosition) / (double)distance))
                : endStrength;
        }

        EncodeZonedStrengths(destination, 0x21, strengths, 0);
    }

    private static void EncodeZonedStrengths(
        Span<byte> destination,
        byte nativeMode,
        ReadOnlySpan<byte> strengths,
        byte frequency)
    {
        ushort activeZones = 0;
        uint packedStrengths = 0;
        for (var index = 0; index < Math.Min(strengths.Length, 10); index++)
        {
            var strength = strengths[index];
            if (strength is 0 or > 8)
            {
                continue;
            }

            activeZones |= (ushort)(1 << index);
            packedStrengths |= (uint)(strength - 1) << (index * 3);
        }

        if (activeZones == 0 || (nativeMode == 0x26 && frequency == 0))
        {
            destination[0] = 0x05;
            return;
        }

        destination[0] = nativeMode;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..], activeZones);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[3..], packedStrengths);
        destination[9] = frequency;
    }

    private static byte Max(ReadOnlySpan<byte> values)
    {
        byte result = 0;
        foreach (var value in values)
        {
            result = Math.Max(result, value);
        }

        return result;
    }

    private static byte ScaleTriggerStrength(byte strength) =>
        (byte)(Math.Min(strength, (byte)8) * 255 / 8);

    [SysAbiExport(
        Nid = "yFVnOdGxvZY",
        ExportName = "scePadSetVibration",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetVibration(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (!IsOpenPadHandle(handle))
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

        HostPlatform.Current.Input.SetRumble(parameter[0], parameter[1]);
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
        if (!IsOpenPadHandle(handle))
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

        HostPlatform.Current.Input.SetLightbar(color[0], color[1], color[2]);
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
        if (!IsOpenPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        HostPlatform.Current.Input.ResetLightbar();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "rIZnR6eSpvk",
        ExportName = "scePadResetOrientation",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadResetOrientation(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return IsOpenPadHandle(handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisPadErrorInvalidHandle);
    }

    [SysAbiExport(
        Nid = "fCWdlnmB1Ks",
        ExportName = "scePadIsRemoteController",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadIsRemoteController(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return IsOpenPadHandle(handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisPadErrorInvalidHandle);
    }

    private static bool WriteNeutralPadData(CpuContext ctx, ulong dataAddress)
    {
        Span<byte> data = stackalloc byte[PadDataSize];
        data.Clear();
        var input = ReadHostInputState();
        var buttons = input.Buttons;
        var leftX = input.LeftX;
        var leftY = input.LeftY;
        var rightX = input.RightX;
        var rightY = input.RightY;
        var l2 = input.L2;
        var r2 = input.R2;

        BinaryPrimitives.WriteUInt32LittleEndian(data[0x00..], buttons);
        data[0x04] = leftX;
        data[0x05] = leftY;
        data[0x06] = rightX;
        data[0x07] = rightY;
        data[0x08] = l2;
        data[0x09] = r2;
        BinaryPrimitives.WriteSingleLittleEndian(data[0x18..], 1.0f);
        if (Volatile.Read(ref _motionSensorEnabled) != 0 && input.Motion.Available)
        {
            // Host acceleration is m/s^2 (SDL); ScePadData.acceleration is in G.
            BinaryPrimitives.WriteSingleLittleEndian(data[0x1C..], input.Motion.AccelerationX / StandardGravity);
            BinaryPrimitives.WriteSingleLittleEndian(data[0x20..], input.Motion.AccelerationY / StandardGravity);
            BinaryPrimitives.WriteSingleLittleEndian(data[0x24..], input.Motion.AccelerationZ / StandardGravity);
            BinaryPrimitives.WriteSingleLittleEndian(data[0x28..], input.Motion.AngularVelocityX);
            BinaryPrimitives.WriteSingleLittleEndian(data[0x2C..], input.Motion.AngularVelocityY);
            BinaryPrimitives.WriteSingleLittleEndian(data[0x30..], input.Motion.AngularVelocityZ);
        }

        WriteTouchData(data, input.Touch);
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

    private static PadState ReadHostInputState()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastInputSampleTicks != 0 && now - _lastInputSampleTicks < InputSampleIntervalTicks)
        {
            return _cachedInputState;
        }

        var input = HostPlatform.Current.Input;
        var acceptsKeyboardInput = input.IsHostWindowFocused();
        var buttons = acceptsKeyboardInput ? ReadKeyboardButtons(input) : 0;
        var leftX = acceptsKeyboardInput ? ReadAnalogStick(input.IsKeyDown(0x41), input.IsKeyDown(0x44)) : (byte)128;
        var leftY = acceptsKeyboardInput ? ReadAnalogStick(input.IsKeyDown(0x57), input.IsKeyDown(0x53)) : (byte)128;
        var rightX = acceptsKeyboardInput ? ReadAnalogStick(input.IsKeyDown(0x4A), input.IsKeyDown(0x4C)) : (byte)128;
        var rightY = acceptsKeyboardInput ? ReadAnalogStick(input.IsKeyDown(0x49), input.IsKeyDown(0x4B)) : (byte)128;
        var l2 = acceptsKeyboardInput && input.IsKeyDown(0x52) ? (byte)255 : (byte)0;
        var r2 = acceptsKeyboardInput && input.IsKeyDown(0x46) ? (byte)255 : (byte)0;
        var gamepadType = HostGamepadType.Generic;
        var connection = HostGamepadConnection.Unknown;
        var motion = default(HostMotionState);
        var touch = default(HostTouchState);

        Span<HostGamepadState> gamepads = stackalloc HostGamepadState[2];
        var gamepadCount = input.GetGamepadStates(gamepads);
        for (var index = 0; index < gamepadCount; index++)
        {
            var pad = gamepads[index];
            buttons |= ToOrbisButtons(pad.Buttons);
            // The controller stick wins whenever it is deflected past a
            // small deadzone; otherwise any keyboard value stays.
            leftX = MergeAxis(pad.LeftX, leftX);
            leftY = MergeAxis(pad.LeftY, leftY);
            rightX = MergeAxis(pad.RightX, rightX);
            rightY = MergeAxis(pad.RightY, rightY);
            l2 = Math.Max(l2, pad.LeftTrigger);
            r2 = Math.Max(r2, pad.RightTrigger);
            if (index == 0)
            {
                gamepadType = pad.Type;
                connection = pad.Connection;
                motion = pad.Motion;
                touch = pad.Touch;
            }
        }

        if (IsAutoCrossActive())
        {
            buttons |= 0x4000;
        }

        if (LogPadState)
        {
            var signature = ((ulong)buttons << 8) | ((ulong)(acceptsKeyboardInput ? 1 : 0) << 4) | (uint)gamepadCount;
            if (Interlocked.Exchange(ref _loggedPadSignature, (long)signature) != (long)signature)
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] pad state: focused={acceptsKeyboardInput} gamepads={gamepadCount} buttons=0x{buttons:X8} " +
                    $"left={leftX},{leftY} thread={Environment.CurrentManagedThreadId}");
            }
        }

        _cachedInputState = new PadState(
            Connected: true,
            Buttons: buttons,
            LeftX: leftX,
            LeftY: leftY,
            RightX: rightX,
            RightY: rightY,
            L2: l2,
            R2: r2,
            Type: gamepadType,
            Connection: connection,
            Motion: motion,
            Touch: touch);
        _lastInputSampleTicks = now;
        return _cachedInputState;
    }

    private static readonly long PadStartTimestamp = Stopwatch.GetTimestamp();
    private static readonly double[] AutoCrossTimes = ParseAutoCrossTimes();

    private static double[] ParseAutoCrossTimes()
    {
        // SHARPEMU_AUTO_CROSS="40,52,64": presses Cross for 0.4s at each
        // second offset from process start. Debug aid for unattended runs.
        var raw = Environment.GetEnvironmentVariable("SHARPEMU_AUTO_CROSS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var values = new List<double>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(token, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                values.Add(value);
            }
        }

        return values.ToArray();
    }

    private static bool IsAutoCrossActive()
    {
        var times = AutoCrossTimes;
        if (times.Length == 0)
        {
            return false;
        }

        var elapsed = (Stopwatch.GetTimestamp() - PadStartTimestamp) / (double)Stopwatch.Frequency;
        foreach (var time in times)
        {
            if (elapsed >= time && elapsed < time + 0.4)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Maps the host seam's neutral button flags onto SCE_PAD_BUTTON bits.</summary>
    private static uint ToOrbisButtons(HostGamepadButtons buttons)
    {
        uint result = 0;
        if ((buttons & HostGamepadButtons.Create) != 0) result |= OrbisPadButton.Share;
        if ((buttons & HostGamepadButtons.Up) != 0) result |= OrbisPadButton.Up;
        if ((buttons & HostGamepadButtons.Down) != 0) result |= OrbisPadButton.Down;
        if ((buttons & HostGamepadButtons.Left) != 0) result |= OrbisPadButton.Left;
        if ((buttons & HostGamepadButtons.Right) != 0) result |= OrbisPadButton.Right;
        if ((buttons & HostGamepadButtons.Cross) != 0) result |= OrbisPadButton.Cross;
        if ((buttons & HostGamepadButtons.Circle) != 0) result |= OrbisPadButton.Circle;
        if ((buttons & HostGamepadButtons.Square) != 0) result |= OrbisPadButton.Square;
        if ((buttons & HostGamepadButtons.Triangle) != 0) result |= OrbisPadButton.Triangle;
        if ((buttons & HostGamepadButtons.L1) != 0) result |= OrbisPadButton.L1;
        if ((buttons & HostGamepadButtons.R1) != 0) result |= OrbisPadButton.R1;
        if ((buttons & HostGamepadButtons.L2) != 0) result |= OrbisPadButton.L2;
        if ((buttons & HostGamepadButtons.R2) != 0) result |= OrbisPadButton.R2;
        if ((buttons & HostGamepadButtons.L3) != 0) result |= OrbisPadButton.L3;
        if ((buttons & HostGamepadButtons.R3) != 0) result |= OrbisPadButton.R3;
        if ((buttons & HostGamepadButtons.Options) != 0) result |= OrbisPadButton.Options;
        if ((buttons & HostGamepadButtons.TouchPad) != 0) result |= OrbisPadButton.TouchPad;
        return result;
    }

    private static void WriteTouchData(Span<byte> data, HostTouchState touch)
    {
        Span<HostTouchPoint> active = stackalloc HostTouchPoint[2];
        var count = 0;
        if (touch.First.Active)
        {
            active[count++] = touch.First;
        }
        if (touch.Second.Active)
        {
            active[count++] = touch.Second;
        }

        data[0x34] = (byte)count;
        for (var index = 0; index < count; index++)
        {
            var offset = 0x3C + (index * 8);
            var point = active[index];
            BinaryPrimitives.WriteUInt16LittleEndian(
                data[offset..],
                (ushort)Math.Round(Math.Clamp(point.X, 0, 1) * 1919));
            BinaryPrimitives.WriteUInt16LittleEndian(
                data[(offset + 2)..],
                (ushort)Math.Round(Math.Clamp(point.Y, 0, 1) * 942));
            data[offset + 4] = point.Id;
        }
    }

    private static uint ReadKeyboardButtons(IHostInput input)
    {
        uint buttons = 0;
        // D-pad
        if (input.IsKeyDown(0x25)) buttons |= OrbisPadButton.Left;
        if (input.IsKeyDown(0x27)) buttons |= OrbisPadButton.Right;
        if (input.IsKeyDown(0x26)) buttons |= OrbisPadButton.Up;
        if (input.IsKeyDown(0x28)) buttons |= OrbisPadButton.Down;
        // Face buttons
        if (input.IsKeyDown(0x5A) || input.IsKeyDown(0x0D)) buttons |= OrbisPadButton.Cross;    // Z / Enter
        if (input.IsKeyDown(0x58) || input.IsKeyDown(0x1B)) buttons |= OrbisPadButton.Circle;   // X / Escape
        if (input.IsKeyDown(0x43)) buttons |= OrbisPadButton.Square;                            // C
        if (input.IsKeyDown(0x56)) buttons |= OrbisPadButton.Triangle;                          // V
        // Shoulder buttons
        if (input.IsKeyDown(0x51)) buttons |= OrbisPadButton.L1;                                // Q
        if (input.IsKeyDown(0x45)) buttons |= OrbisPadButton.R1;                                // E
        if (input.IsKeyDown(0x52)) buttons |= OrbisPadButton.L2;                                // R (digital)
        if (input.IsKeyDown(0x46)) buttons |= OrbisPadButton.R2;                                // F (digital)
        // Options (Start)
        if (input.IsKeyDown(0x09) || input.IsKeyDown(0x08)) buttons |= OrbisPadButton.Options;  // Tab / Backspace
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
