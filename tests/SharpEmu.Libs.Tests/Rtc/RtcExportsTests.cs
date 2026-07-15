// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Rtc;
using Xunit;

namespace SharpEmu.Libs.Tests.Rtc;

// libSceRtc is pure calendar/tick arithmetic, so it can be exercised end to end without a live
// guest: the exports read their operands from CPU registers and guest memory and write results
// back the same way. A "tick" is microseconds since 0001-01-01 (i.e. DateTime.Ticks / 10), which
// is what these tests assert against.
public sealed class RtcExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong TimeAddress = Base + 0x100;
    private const ulong OutAddress = Base + 0x200;
    private const ulong TickAddress = Base + 0x300;
    private const ulong SecondTickAddress = Base + 0x400;

    // Reference constants (verified against System.DateTime): microseconds since 0001-01-01.
    private const ulong UnixEpochTick = 62_135_596_800_000_000UL; // 1970-01-01T00:00:00
    private const ulong Y2KTick = 63_082_281_600_000_000UL;       // 2000-01-01T00:00:00

    private readonly FakeCpuMemory _memory = new(Base, 0x10000);
    private readonly CpuContext _ctx;

    public RtcExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void GetTick_UnixEpoch_MatchesReferenceTick()
    {
        WriteRtc(TimeAddress, 1970, 1, 1, 0, 0, 0, 0);
        _ctx[CpuRegister.Rdi] = TimeAddress;
        _ctx[CpuRegister.Rsi] = TickAddress;

        Assert.Equal(0, RtcExports.RtcGetTick(_ctx));
        Assert.True(_ctx.TryReadUInt64(TickAddress, out var tick));
        Assert.Equal(UnixEpochTick, tick);
    }

    [Fact]
    public void GetTick_Y2K_MatchesReferenceTick()
    {
        WriteRtc(TimeAddress, 2000, 1, 1, 0, 0, 0, 0);
        _ctx[CpuRegister.Rdi] = TimeAddress;
        _ctx[CpuRegister.Rsi] = TickAddress;

        Assert.Equal(0, RtcExports.RtcGetTick(_ctx));
        Assert.True(_ctx.TryReadUInt64(TickAddress, out var tick));
        Assert.Equal(Y2KTick, tick);
    }

    [Fact]
    public void GetTickThenSetTick_RoundTripsWithMicroseconds()
    {
        // A leap-day timestamp with sub-second precision stresses both the calendar math and the
        // microsecond field surviving the tick <-> struct conversion.
        WriteRtc(TimeAddress, 2020, 2, 29, 13, 45, 30, 123_456);
        _ctx[CpuRegister.Rdi] = TimeAddress;
        _ctx[CpuRegister.Rsi] = TickAddress;
        Assert.Equal(0, RtcExports.RtcGetTick(_ctx));

        _ctx[CpuRegister.Rdi] = OutAddress;
        _ctx[CpuRegister.Rsi] = TickAddress;
        Assert.Equal(0, RtcExports.RtcSetTick(_ctx));

        AssertRtc(OutAddress, 2020, 2, 29, 13, 45, 30, 123_456);
    }

    [Fact]
    public void GetTimeT_ConvertsToUnixSeconds()
    {
        WriteRtc(TimeAddress, 2000, 1, 1, 0, 0, 0, 0);
        _ctx[CpuRegister.Rdi] = TimeAddress;
        _ctx[CpuRegister.Rsi] = OutAddress;

        Assert.Equal(0, RtcExports.RtcGetTimeT(_ctx));
        Assert.True(_ctx.TryReadUInt64(OutAddress, out var unixSeconds));
        Assert.Equal(946_684_800UL, unixSeconds); // well-known 2000-01-01 UTC unix timestamp
    }

    [Fact]
    public void GetTimeT_BeforeUnixEpoch_ClampsToZero()
    {
        WriteRtc(TimeAddress, 1960, 1, 1, 0, 0, 0, 0);
        _ctx[CpuRegister.Rdi] = TimeAddress;
        _ctx[CpuRegister.Rsi] = OutAddress;

        Assert.Equal(0, RtcExports.RtcGetTimeT(_ctx));
        Assert.True(_ctx.TryReadUInt64(OutAddress, out var unixSeconds));
        Assert.Equal(0UL, unixSeconds);
    }

    [Fact]
    public void SetTimeT_ConvertsUnixSecondsToDate()
    {
        _ctx[CpuRegister.Rdi] = TimeAddress;
        _ctx[CpuRegister.Rsi] = 946_684_800UL;

        Assert.Equal(0, RtcExports.RtcSetTimeT(_ctx));
        AssertRtc(TimeAddress, 2000, 1, 1, 0, 0, 0, 0);
    }

    [Fact]
    public void GetWin32FileTime_UnixEpoch_MatchesKnownFileTime()
    {
        WriteRtc(TimeAddress, 1970, 1, 1, 0, 0, 0, 0);
        _ctx[CpuRegister.Rdi] = TimeAddress;
        _ctx[CpuRegister.Rsi] = OutAddress;

        Assert.Equal(0, RtcExports.RtcGetWin32FileTime(_ctx));
        Assert.True(_ctx.TryReadUInt64(OutAddress, out var fileTime));
        Assert.Equal(116_444_736_000_000_000UL, fileTime); // FILETIME of the unix epoch (100ns units)
    }

    [Fact]
    public void GetDosTime_PacksFields()
    {
        WriteRtc(TimeAddress, 2021, 6, 15, 13, 45, 30, 0);
        _ctx[CpuRegister.Rdi] = TimeAddress;
        _ctx[CpuRegister.Rsi] = OutAddress;

        Assert.Equal(0, RtcExports.RtcGetDosTime(_ctx));
        Assert.True(_ctx.TryReadUInt32(OutAddress, out var dosTime));
        Assert.Equal(1_389_325_743U, dosTime);
    }

    [Fact]
    public void SetDosTimeThenGetDosTime_RoundTripsFieldsAndValue()
    {
        const uint dosValue = 1_389_325_743U; // 2021-06-15 13:45:30, even second => no 2s-resolution loss
        _ctx[CpuRegister.Rdi] = TimeAddress;
        _ctx[CpuRegister.Rsi] = dosValue;
        Assert.Equal(0, RtcExports.RtcSetDosTime(_ctx));
        AssertRtc(TimeAddress, 2021, 6, 15, 13, 45, 30, 0);

        _ctx[CpuRegister.Rdi] = TimeAddress;
        _ctx[CpuRegister.Rsi] = OutAddress;
        Assert.Equal(0, RtcExports.RtcGetDosTime(_ctx));
        Assert.True(_ctx.TryReadUInt32(OutAddress, out var packed));
        Assert.Equal(dosValue, packed);
    }

    [Fact]
    public void GetTickResolution_IsOneMicrosecond()
    {
        Assert.Equal(1_000_000, RtcExports.RtcGetTickResolution(_ctx));
    }

    [Theory]
    [InlineData(2000, 1)] // divisible by 400
    [InlineData(2004, 1)] // divisible by 4
    [InlineData(2021, 0)]
    [InlineData(1900, 0)] // divisible by 100 but not 400
    public void IsLeapYear_MatchesGregorianRule(int year, int expected)
    {
        _ctx[CpuRegister.Rdi] = (ulong)year;
        Assert.Equal(expected, RtcExports.RtcIsLeapYear(_ctx));
    }

    [Fact]
    public void IsLeapYear_OutOfRange_ReturnsInvalidYear()
    {
        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(unchecked((int)0x80B50008), RtcExports.RtcIsLeapYear(_ctx));
    }

    [Theory]
    [InlineData(2021, 2, 28)]
    [InlineData(2020, 2, 29)]
    [InlineData(2021, 4, 30)]
    [InlineData(2021, 12, 31)]
    public void GetDaysInMonth_ReturnsCalendarLength(int year, int month, int expected)
    {
        _ctx[CpuRegister.Rdi] = (ulong)year;
        _ctx[CpuRegister.Rsi] = (ulong)month;
        Assert.Equal(expected, RtcExports.RtcGetDaysInMonth(_ctx));
    }

    [Fact]
    public void GetDaysInMonth_InvalidMonth_ReturnsInvalidMonthCode()
    {
        _ctx[CpuRegister.Rdi] = 2021;
        _ctx[CpuRegister.Rsi] = 13;
        Assert.Equal(unchecked((int)0x80B50009), RtcExports.RtcGetDaysInMonth(_ctx));
    }

    [Fact]
    public void GetDayOfWeek_ReturnsSundayZeroBasedIndex()
    {
        // 2021-06-15 is a Tuesday; DayOfWeek numbers Sunday as 0, so Tuesday == 2.
        _ctx[CpuRegister.Rdi] = 2021;
        _ctx[CpuRegister.Rsi] = 6;
        _ctx[CpuRegister.Rdx] = 15;
        Assert.Equal(2, RtcExports.RtcGetDayOfWeek(_ctx));
    }

    [Fact]
    public void GetDayOfWeek_InvalidDate_ReturnsError()
    {
        _ctx[CpuRegister.Rdi] = 2021;
        _ctx[CpuRegister.Rsi] = 2;
        _ctx[CpuRegister.Rdx] = 30; // February never has 30 days
        Assert.Equal(unchecked((int)0x80B50004), RtcExports.RtcGetDayOfWeek(_ctx));
    }

    [Fact]
    public void CheckValid_ValidDate_ReturnsOk()
    {
        WriteRtc(TimeAddress, 2021, 6, 15, 13, 45, 30, 500_000);
        _ctx[CpuRegister.Rdi] = TimeAddress;
        Assert.Equal(0, RtcExports.RtcCheckValid(_ctx));
    }

    [Theory]
    [InlineData(0, 6, 15, 12, 0, 0, 0, 0x80B50008)]   // year out of range
    [InlineData(2021, 13, 15, 12, 0, 0, 0, 0x80B50009)] // month out of range
    [InlineData(2021, 2, 30, 12, 0, 0, 0, 0x80B5000A)]  // day exceeds month length
    [InlineData(2021, 6, 15, 24, 0, 0, 0, 0x80B5000B)]  // hour out of range
    [InlineData(2021, 6, 15, 12, 60, 0, 0, 0x80B5000C)] // minute out of range
    [InlineData(2021, 6, 15, 12, 0, 60, 0, 0x80B5000D)] // second out of range
    [InlineData(2021, 6, 15, 12, 0, 0, 1_000_000, 0x80B5000E)] // microsecond out of range
    public void CheckValid_InvalidField_ReturnsMatchingErrorCode(
        int year, int month, int day, int hour, int minute, int second, uint microsecond, long expected)
    {
        WriteRtc(TimeAddress, year, month, day, hour, minute, second, microsecond);
        _ctx[CpuRegister.Rdi] = TimeAddress;
        Assert.Equal(unchecked((int)expected), RtcExports.RtcCheckValid(_ctx));
    }

    [Fact]
    public void CompareTick_OrdersByValue()
    {
        Assert.True(_ctx.TryWriteUInt64(TickAddress, 100));
        Assert.True(_ctx.TryWriteUInt64(SecondTickAddress, 200));

        _ctx[CpuRegister.Rdi] = TickAddress;
        _ctx[CpuRegister.Rsi] = SecondTickAddress;
        Assert.Equal(-1, RtcExports.RtcCompareTick(_ctx));

        _ctx[CpuRegister.Rdi] = SecondTickAddress;
        _ctx[CpuRegister.Rsi] = TickAddress;
        Assert.Equal(1, RtcExports.RtcCompareTick(_ctx));

        _ctx[CpuRegister.Rsi] = SecondTickAddress;
        _ctx[CpuRegister.Rdi] = SecondTickAddress;
        Assert.Equal(0, RtcExports.RtcCompareTick(_ctx));
    }

    [Fact]
    public void TickAddDays_AdvancesByWholeDay()
    {
        Assert.True(_ctx.TryWriteUInt64(TickAddress, Y2KTick));
        _ctx[CpuRegister.Rdi] = OutAddress;   // destination
        _ctx[CpuRegister.Rsi] = TickAddress;  // source
        _ctx[CpuRegister.Rdx] = 1;            // +1 day

        Assert.Equal(0, RtcExports.RtcTickAddDays(_ctx));
        Assert.True(_ctx.TryReadUInt64(OutAddress, out var result));
        Assert.Equal(Y2KTick + 86_400_000_000UL, result);
    }

    // Regression test for the sceRtcConvertLocalTimeToUtc DateTimeKind bug: the guest tick was
    // decoded as Utc and then handed to TimeZoneInfo.ConvertTimeToUtc together with the (non-UTC)
    // local zone, which throws ArgumentException, so the export always returned INVALID_ARGUMENT on
    // any host not set to UTC. It must succeed and round-trip against sceRtcConvertUtcToLocalTime.
    [Fact]
    public void ConvertUtcToLocalTimeThenBack_RoundTrips()
    {
        // Noon in mid-June is never an invalid/ambiguous wall-clock time in any real zone, so the
        // conversion is an exact inverse regardless of the CI machine's local time zone.
        WriteRtc(TimeAddress, 2021, 6, 15, 12, 0, 0, 0);
        _ctx[CpuRegister.Rdi] = TimeAddress;
        _ctx[CpuRegister.Rsi] = TickAddress;
        Assert.Equal(0, RtcExports.RtcGetTick(_ctx));
        Assert.True(_ctx.TryReadUInt64(TickAddress, out var utcTick));

        // UTC tick -> local tick
        _ctx[CpuRegister.Rdi] = TickAddress;        // utc in
        _ctx[CpuRegister.Rsi] = SecondTickAddress;  // local out
        Assert.Equal(0, RtcExports.RtcConvertUtcToLocalTime(_ctx));

        // local tick -> UTC tick (the previously broken direction)
        _ctx[CpuRegister.Rdi] = SecondTickAddress;  // local in
        _ctx[CpuRegister.Rsi] = OutAddress;         // utc out
        Assert.Equal(0, RtcExports.RtcConvertLocalTimeToUtc(_ctx));

        Assert.True(_ctx.TryReadUInt64(OutAddress, out var roundTripTick));
        Assert.Equal(utcTick, roundTripTick);
    }

    [Fact]
    public void ConvertLocalTimeToUtc_NullPointer_ReturnsError()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = OutAddress;
        Assert.Equal(unchecked((int)0x80B50002), RtcExports.RtcConvertLocalTimeToUtc(_ctx));
    }

    private void WriteRtc(ulong address, int year, int month, int day, int hour, int minute, int second, uint microsecond)
    {
        Span<byte> buffer = stackalloc byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[0..2], (ushort)year);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..4], (ushort)month);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..6], (ushort)day);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..8], (ushort)hour);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[8..10], (ushort)minute);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[10..12], (ushort)second);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..16], microsecond);
        Assert.True(_memory.TryWrite(address, buffer));
    }

    private void AssertRtc(ulong address, int year, int month, int day, int hour, int minute, int second, uint microsecond)
    {
        Span<byte> buffer = stackalloc byte[16];
        Assert.True(_memory.TryRead(address, buffer));
        Assert.Equal(year, BinaryPrimitives.ReadUInt16LittleEndian(buffer[0..2]));
        Assert.Equal(month, BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..4]));
        Assert.Equal(day, BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..6]));
        Assert.Equal(hour, BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..8]));
        Assert.Equal(minute, BinaryPrimitives.ReadUInt16LittleEndian(buffer[8..10]));
        Assert.Equal(second, BinaryPrimitives.ReadUInt16LittleEndian(buffer[10..12]));
        Assert.Equal(microsecond, BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..16]));
    }
}
