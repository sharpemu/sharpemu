// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Threading;

namespace SharpEmu.HLE;

// This tool monitors guest-memory writes only when a watch mode is active.
public static class GuestWriteWatch
{
    private const ulong WatchBytes = 8;
    private const int MaxBulkReports = 64;

    private static readonly ulong WatchBase = Parse(
        Environment.GetEnvironmentVariable("SHARPEMU_WATCH_WRITE"));

    private static readonly bool WatchPoolHeaders = IsEnabled("SHARPEMU_WATCH_POOL_HEADER");

    private static readonly ulong[] PoolSlots = new ulong[64];
    private static int _poolSlotCount;

    private static readonly bool WatchValuePattern = IsEnabled("SHARPEMU_WATCH_VALUE_PATTERN");

    private static readonly bool WatchValue1 = IsEnabled("SHARPEMU_WATCH_VALUE1");

    private const ulong DirectBandLow = 0x100_0000_0000;
    private const ulong DirectBandHigh = 0x1000_0000_0000;
    private static int _value1Reports;

    private static readonly bool WatchBulkTorn = IsEnabled("SHARPEMU_WATCH_BULK_TORN");

    private static readonly ulong BulkDestHigh = Parse(
        Environment.GetEnvironmentVariable("SHARPEMU_WATCH_BULK_DEST_HI"));
    private static int _bulkTornReports;
    private static int _bulkShiftReports;

    public static bool Armed =>
        WatchBase != 0 || WatchPoolHeaders || WatchValuePattern || WatchValue1 || WatchBulkTorn;

    public static void OnDirectMapping(ulong mappedAddress, ulong length, int protection)
    {
        if (!WatchPoolHeaders || !IsPoolMapping(length, protection))
        {
            return;
        }

        var index = Interlocked.Increment(ref _poolSlotCount) - 1;
        if (index < PoolSlots.Length)
        {
            Volatile.Write(ref PoolSlots[index], mappedAddress + 0x40);
            Console.Error.WriteLine(
                $"[LOADER][WARN] watch_write armed on pool header slot 0x{mappedAddress + 0x40:X16}");
        }
    }

    public static void Check(ulong address, ReadOnlySpan<byte> data)
    {
        if (WatchBulkTorn &&
            data.Length >= 8 &&
            (BulkDestHigh != 0
                ? (address >> 32) == BulkDestHigh
                : address >= DirectBandLow && address < DirectBandHigh))
        {
            for (var offset = FirstAlignedOffset(address); offset + 8 <= data.Length; offset += 8)
            {
                var qword = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
                var kind = ClassifyBulkValue(qword);
                if (kind is not null && ReserveBulkReport(kind))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] watch_bulk_torn HIT ({kind}) " +
                        $"dest=0x{address + (ulong)offset:X16} (base=0x{address:X16}+0x{offset:X}) " +
                        $"len={data.Length} qword=0x{qword:X16}{Environment.NewLine}{Environment.StackTrace}");
                    Console.Error.Flush();
                    return;
                }
            }
        }

        if (WatchValue1 &&
            address >= DirectBandLow && address < DirectBandHigh &&
            data.Length is >= 1 and <= 8 &&
            LittleEndianValue(data) == 1 &&
            Interlocked.Increment(ref _value1Reports) <= 128)
        {
            Report(address, data);
            return;
        }

        if (WatchValuePattern && data.Length == 8)
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(data);
            if ((value & 0xFFFFFFFF) == 1 && value >> 32 is > 0 and <= 0xFFFF)
            {
                Report(address, data);
                return;
            }
        }

        if (WatchBase != 0 && Overlaps(address, data.Length, WatchBase))
        {
            Report(address, data);
            return;
        }

        var slots = Math.Min(Volatile.Read(ref _poolSlotCount), PoolSlots.Length);
        for (var i = 0; i < slots; i++)
        {
            var slot = Volatile.Read(ref PoolSlots[i]);
            if (slot != 0 && Overlaps(address, data.Length, slot))
            {
                Report(address, data);
                return;
            }
        }
    }

    internal static string? ClassifyBulkValue(ulong qword)
    {
        var low32 = qword & 0xFFFFFFFF;
        var high32 = qword >> 32;
        if (low32 == 1 && high32 is > 0 and <= 0xFFFF)
        {
            return "torn";
        }

        var prefix = low32 & 0xFF00_0000;
        var hasShiftedPointerPrefix = prefix is 0x0800_0000 or 0x8000_0000;
        return high32 == 0 && hasShiftedPointerPrefix && (low32 & 0xFF) == 0
            ? "shift"
            : null;
    }

    internal static int FirstAlignedOffset(ulong address) =>
        (int)((8 - (address & 7)) & 7);

    internal static bool IsPoolMapping(ulong length, int protection) =>
        length == 0x10000 && protection == 0xF2;

    internal static bool Overlaps(ulong address, int length, ulong slot)
    {
        if (length <= 0)
        {
            return false;
        }

        var writeLength = (ulong)length - 1;
        var writeEnd = address > ulong.MaxValue - writeLength
            ? ulong.MaxValue
            : address + writeLength;
        var slotEnd = slot > ulong.MaxValue - (WatchBytes - 1)
            ? ulong.MaxValue
            : slot + WatchBytes - 1;
        return address <= slotEnd && slot <= writeEnd;
    }

    private static ulong LittleEndianValue(ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        for (var i = 0; i < data.Length; i++)
        {
            value |= (ulong)data[i] << (i * 8);
        }

        return value;
    }

    private static void Report(ulong address, ReadOnlySpan<byte> data)
    {
        Console.Error.WriteLine(
            $"[LOADER][WARN] watch_write HIT addr=0x{address:X16} len={data.Length} " +
            $"first_qword=0x{LittleEndianValue(data):X16}{Environment.NewLine}{Environment.StackTrace}");
        Console.Error.Flush();
    }

    private static bool IsEnabled(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);

    private static bool ReserveBulkReport(string kind) =>
        kind == "torn"
            ? Interlocked.Increment(ref _bulkTornReports) <= MaxBulkReports
            : Interlocked.Increment(ref _bulkShiftReports) <= MaxBulkReports;

    internal static ulong Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }
}
