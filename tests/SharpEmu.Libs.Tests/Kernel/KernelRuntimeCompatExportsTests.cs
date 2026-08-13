// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// sceKernelGetTscFrequency must describe the same clock that sceKernelReadTsc returns. ReadTsc
// only returns the CPU's RDTSC when the host RDTSC reader is available (64-bit Windows) and
// otherwise falls back to the QPC-based Stopwatch, so the frequency selection has to follow suit.
public sealed class KernelRuntimeCompatExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong UnwindInfoAddress = MemoryBase + 0x400;
    private const int UnwindInfoSize = 0x130;

    private static KernelRuntimeCompatExports.TryGetFrequency Yields(ulong hz) =>
        (out ulong frequencyHz) =>
        {
            frequencyHz = hz;
            return true;
        };

    private static readonly KernelRuntimeCompatExports.TryGetFrequency Fails =
        (out ulong frequencyHz) =>
        {
            frequencyHz = 0;
            return false;
        };

    [Fact]
    public void WithoutHostRdtsc_ReportsStopwatchFrequency_NotHardwareTsc()
    {
        // Regression: on Linux/macOS ReadTsc returns the Stopwatch counter, so the reported
        // frequency must be the Stopwatch's, never the CPU's much larger hardware TSC frequency.
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: false,
            overrideHzText: null,
            tryCalibrate: Yields(2_400_000_000UL),
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(10_000_000UL, frequencyHz);
        Assert.Equal("qpc", source);
    }

    [Fact]
    public void WithHostRdtsc_PrefersCalibratedFrequency()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: true,
            overrideHzText: null,
            tryCalibrate: Yields(2_400_000_000UL),
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(2_400_000_000UL, frequencyHz);
        Assert.Equal("calibrated-rdtsc", source);
    }

    [Fact]
    public void WithHostRdtsc_FallsBackToCpuid_WhenCalibrationFails()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: true,
            overrideHzText: null,
            tryCalibrate: Fails,
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(3_000_000_000UL, frequencyHz);
        Assert.Equal("cpuid", source);
    }

    [Fact]
    public void WithHostRdtsc_UsesStopwatch_WhenRdtscFrequencyUnknown()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: true,
            overrideHzText: null,
            tryCalibrate: Fails,
            tryResolveCpuid: Fails,
            stopwatchFrequency: 10_000_000);

        Assert.Equal(10_000_000UL, frequencyHz);
        Assert.Equal("qpc", source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnvOverride_Wins_WhenSane(bool rdtscAvailable)
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable,
            overrideHzText: "1500000000",
            tryCalibrate: Yields(2_400_000_000UL),
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(1_500_000_000UL, frequencyHz);
        Assert.Equal("env", source);
    }

    [Fact]
    public void EnvOverride_BelowMinimum_IsIgnored()
    {
        // 500 kHz is below the sanity floor, so it is dropped; with rdtsc unavailable the
        // hardware-TSC path is gated off and the Stopwatch frequency is used.
        var (frequencyHz, _) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: false,
            overrideHzText: "500000",
            tryCalibrate: Fails,
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(10_000_000UL, frequencyHz);
    }

    [Fact]
    public void NonPositiveStopwatchFrequency_FallsBackToDefault()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: false,
            overrideHzText: null,
            tryCalibrate: Fails,
            tryResolveCpuid: Fails,
            stopwatchFrequency: 0);

        Assert.Equal(10_000_000UL, frequencyHz); // DefaultKernelTscFrequency
        Assert.Equal("qpc", source);
    }

    [Fact]
    public void ConvertUtcToLocaltime_AllowsNullLocalOutputAndWritesTimesec()
    {
        const long utcSeconds = 1_786_262_400;
        const ulong timesecAddress = MemoryBase + 0x100;
        const ulong dstSecondsAddress = MemoryBase + 0x200;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var timesec = new byte[17];
        Array.Fill(timesec, (byte)0xCC);
        Assert.True(memory.TryWrite(timesecAddress, timesec));

        context[CpuRegister.Rdi] = unchecked((ulong)utcSeconds);
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = timesecAddress;
        context[CpuRegister.Rcx] = dstSecondsAddress;

        var result = KernelRuntimeCompatExports.KernelConvertUtcToLocaltime(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.True(memory.TryRead(timesecAddress, timesec));
        Assert.Equal(utcSeconds, BinaryPrimitives.ReadInt64LittleEndian(timesec));
        Assert.Equal(
            unchecked((uint)GetStandardOffsetSeconds()),
            BinaryPrimitives.ReadUInt32LittleEndian(timesec.AsSpan(8)));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(timesec.AsSpan(12)));
        Assert.Equal(0xCC, timesec[16]);

        Span<byte> dstSeconds = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(dstSecondsAddress, dstSeconds));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(dstSeconds));
    }

    [Fact]
    public void ConvertLocaltimeToUtc_WritesTimezoneAndDstOutputs()
    {
        const long localSeconds = 1_786_240_800;
        const ulong utcAddress = MemoryBase + 0x100;
        const ulong timezoneAddress = MemoryBase + 0x200;
        const ulong dstSecondsAddress = MemoryBase + 0x300;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = unchecked((ulong)localSeconds);
        context[CpuRegister.Rsi] = 0xDEAD_BEEF;
        context[CpuRegister.Rdx] = utcAddress;
        context[CpuRegister.Rcx] = timezoneAddress;
        context[CpuRegister.R8] = dstSecondsAddress;

        var result = KernelRuntimeCompatExports.KernelConvertLocaltimeToUtc(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Span<byte> utc = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(utcAddress, utc));
        Assert.Equal(
            localSeconds - GetStandardOffsetSeconds(),
            BinaryPrimitives.ReadInt64LittleEndian(utc));

        Span<byte> timezone = stackalloc byte[sizeof(int) * 2];
        Assert.True(memory.TryRead(timezoneAddress, timezone));
        Assert.Equal(GetMinutesWest(), BinaryPrimitives.ReadInt32LittleEndian(timezone));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(timezone[sizeof(int)..]));

        Span<byte> dstSeconds = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(dstSecondsAddress, dstSeconds));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(dstSeconds));
    }

    [Theory]
    [InlineData(360, 0, -21_600)]
    [InlineData(360, 3_600, -18_000)]
    [InlineData(-345, 0, 20_700)]
    public void TimeConversionArithmetic_HandlesOffsetsAndDst(
        int minutesWest,
        int dstSeconds,
        long expectedLocalDelta)
    {
        const long utcSeconds = 1_786_262_400;

        var localSeconds = KernelRuntimeCompatExports.ConvertUtcToLocaltimeSeconds(
            utcSeconds,
            minutesWest,
            dstSeconds);
        var roundTrip = KernelRuntimeCompatExports.ConvertLocaltimeToUtcSeconds(
            localSeconds,
            minutesWest,
            dstSeconds);

        Assert.Equal(utcSeconds + expectedLocalDelta, localSeconds);
        Assert.Equal(utcSeconds, roundTrip);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetModuleInfoForUnwind_HostAddressWritesSyntheticBoundary(bool useSysmoduleAlias)
    {
        const ulong queriedAddress = 0x00000243_A84C_FFFF;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var payload = new byte[UnwindInfoSize + 1];
        Array.Fill(payload, (byte)0xCC);
        Assert.True(memory.TryWrite(UnwindInfoAddress, payload));
        Assert.True(context.TryWriteUInt64(UnwindInfoAddress, UnwindInfoSize));
        context[CpuRegister.Rdi] = queriedAddress;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = UnwindInfoAddress;

        var result = useSysmoduleAlias
            ? KernelRuntimeCompatExports.SysmoduleGetModuleInfoForUnwind(context)
            : KernelRuntimeCompatExports.KernelGetModuleInfoForUnwind(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.True(memory.TryRead(UnwindInfoAddress, payload));
        Assert.Equal((ulong)UnwindInfoSize, BinaryPrimitives.ReadUInt64LittleEndian(payload));
        Assert.Equal("SharpEmuHostBoundary", ReadUtf8Z(payload.AsSpan(0x08, 0x100)));
        Assert.All(payload.AsSpan(0x108, 0x18).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(
            queriedAddress & ~0xF_FFFFUL,
            BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(0x120)));
        Assert.Equal(0x10_0000UL, BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(0x128)));
        Assert.Equal(0xCC, payload[UnwindInfoSize]);
    }

    [Fact]
    public void GetModuleInfoForUnwind_MissingGuestAddressReturnsNotFound()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(context.TryWriteUInt64(UnwindInfoAddress, UnwindInfoSize));
        context[CpuRegister.Rdi] = 0x00000008_7FFF_1234;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = UnwindInfoAddress;

        var result = KernelRuntimeCompatExports.KernelGetModuleInfoForUnwind(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }

    [Fact]
    public void GetModuleInfoForUnwind_InvalidFlagsReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(context.TryWriteUInt64(UnwindInfoAddress, UnwindInfoSize));
        context[CpuRegister.Rdi] = 0x00000243_A84C_FFFF;
        context[CpuRegister.Rsi] = 3;
        context[CpuRegister.Rdx] = UnwindInfoAddress;

        var result = KernelRuntimeCompatExports.KernelGetModuleInfoForUnwind(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void IsSignalReturn_ReportsNoSyntheticSignalFrame()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 0x00000243_A84C_FFFF;

        var result = KernelRuntimeCompatExports.KernelIsSignalReturn(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private static int GetMinutesWest() =>
        unchecked((int)-TimeZoneInfo.Local.BaseUtcOffset.TotalMinutes);

    private static int GetStandardOffsetSeconds() =>
        unchecked(-GetMinutesWest() * 60);

    private static string ReadUtf8Z(ReadOnlySpan<byte> value)
    {
        var length = value.IndexOf((byte)0);
        Assert.True(length >= 0);
        return Encoding.UTF8.GetString(value[..length]);
    }
}
