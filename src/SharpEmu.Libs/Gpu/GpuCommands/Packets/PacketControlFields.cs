// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands.Packets;

// Reads guest memory the way the host requires before a read-modify-write.
public delegate bool GuestReader(ulong address, Span<byte> destination);

public readonly record struct WriteDataControl(
    uint Destination,
    bool IncrementAddress,
    bool WriteConfirm,
    uint CachePolicy)
{
    // Native WRITE_DATA: DST_SEL 11:8, ADDR_INCR bit 16 (0 increments), WR_CONFIRM bit 20, policy 26:25.
    public static WriteDataControl DecodeNative(uint control) =>
        new(
            (control >> 8) & 0xFu,
            (control & (1u << 16)) == 0,
            (control & (1u << 20)) != 0,
            (control >> 25) & 0x3u);

    // The AGC library builder packs the same fields one byte each.
    public static WriteDataControl DecodeAgc(uint control) =>
        new(
            control & 0xFFu,
            ((control >> 16) & 0xFFu) == 0,
            ((control >> 24) & 0xFFu) != 0,
            (control >> 8) & 0xFFu);
}

public readonly record struct InterruptDecision(bool WritesData, bool RaisesInterrupt)
{
    // Selectors 5 and 6 raise only when the label at the destination is at or below the data.
    public static InterruptDecision Evaluate(
        uint interruptSelector,
        bool isAsyncCompute,
        uint dataSelection,
        bool conditionReadable,
        ulong conditionValue,
        ulong data)
    {
        var writesData = dataSelection != 0 && interruptSelector switch
        {
            0 or 2 or 3 => true,
            1 => isAsyncCompute,
            _ => false,
        };
        var raisesInterrupt = interruptSelector switch
        {
            1 or 2 or 4 => true,
            5 => conditionReadable && unchecked((uint)conditionValue) <= unchecked((uint)data),
            6 => conditionReadable && conditionValue <= data,
            _ => false,
        };
        return new InterruptDecision(writesData, raisesInterrupt);
    }
}

public static class WaitOperation
{
    public static bool IsValid(uint operation) => operation is 0 or 1 or 4;

    // Operation 4 runs only while a conditional-write scratch result enabled it.
    public static bool ShouldExecute(uint operation, bool conditionalWaitEnabled) =>
        operation != 4 || conditionalWaitEnabled;

    public static uint Decode(uint control, bool is64Bit) =>
        is64Bit
            ? ((control >> 8) & 0x1u) | ((control >> 5) & 0x6u)
            : ((control >> 8) & 0x3u) | ((control >> 4) & 0xCu);

    // Compare functions 0 to 6; 7 and above are unknown.
    public static bool TryCompare(ulong value, ulong reference, ulong mask, uint function, out bool satisfied)
    {
        var maskedValue = value & mask;
        satisfied = function switch
        {
            0 => true,
            1 => maskedValue < reference,
            2 => maskedValue <= reference,
            3 => maskedValue == reference,
            4 => maskedValue != reference,
            5 => maskedValue >= reference,
            6 => maskedValue > reference,
            _ => false,
        };
        return function <= 6;
    }
}
