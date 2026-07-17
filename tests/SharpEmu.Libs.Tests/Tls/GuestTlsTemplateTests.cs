// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Tls;

[CollectionDefinition("GuestTlsTemplateState", DisableParallelization = true)]
public sealed class GuestTlsTemplateStateCollection;

[Collection("GuestTlsTemplateState")]
public sealed class GuestTlsTemplateTests
{
    [Fact]
    public void StartupReservationAcceptsTlsSpansLargerThanOneHostPage()
    {
        try
        {
            GuestTlsTemplate.Reset();

            var staticOffset = GuestTlsTemplate.RegisterModule(
                moduleId: 1,
                initImage: new byte[0x20],
                memorySize: 0x1870,
                alignment: 0x10);

            Assert.Equal(0x1870UL, staticOffset);
            Assert.Equal(
                GuestTlsTemplate.MinimumStartupStaticTlsReservation,
                GuestTlsTemplate.GetStartupStaticTlsReservation());
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }

    [Fact]
    public void StartupReservationGrowsPastMinimumAndRoundsToGuestPage()
    {
        try
        {
            GuestTlsTemplate.Reset();

            var staticOffset = GuestTlsTemplate.RegisterModule(
                moduleId: 1,
                initImage: [0x11, 0x22],
                memorySize: 0x13570,
                alignment: 0x10);

            Assert.Equal(0x13570UL, staticOffset);
            Assert.Equal(0x14000UL, GuestTlsTemplate.GetStartupStaticTlsReservation());
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }

    [Fact]
    public void StartupReservationTracksCumulativeModuleLayout()
    {
        try
        {
            GuestTlsTemplate.Reset();

            Assert.Equal(
                0xF000UL,
                GuestTlsTemplate.RegisterModule(1, [0xA1], 0xF000, 0x1000));
            Assert.Equal(
                0x12000UL,
                GuestTlsTemplate.RegisterModule(2, [0xB2], 0x3000, 0x1000));
            Assert.Equal(0x12000UL, GuestTlsTemplate.GetStartupStaticTlsReservation());
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }

    [Fact]
    public void StartupReservationRejectsLayoutsThatWouldOverlapThreadSlots()
    {
        try
        {
            GuestTlsTemplate.Reset();

            var error = Assert.Throws<InvalidDataException>(() =>
                GuestTlsTemplate.RegisterModule(
                    moduleId: 1,
                    initImage: [],
                    memorySize: GuestTlsTemplate.MaximumStartupStaticTlsReservation + 1,
                    alignment: 1));

            Assert.Contains("exceeds the supported", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }

    [Fact]
    public void SeedThreadBlockUsesExpandedStaticReservation()
    {
        try
        {
            GuestTlsTemplate.Reset();

            var staticOffset = GuestTlsTemplate.RegisterModule(
                moduleId: 1,
                initImage: [0x31, 0x42],
                memorySize: 0x13570,
                alignment: 0x10);
            var reservation = GuestTlsTemplate.GetStartupStaticTlsReservation();
            const ulong memoryBase = 0x1_0000_0000;
            var threadPointer = memoryBase + reservation;
            var memory = new FakeCpuMemory(memoryBase, checked((int)reservation + 0x1000));
            var context = new CpuContext(memory, Generation.Gen5)
            {
                FsBase = threadPointer,
            };

            GuestTlsTemplate.SeedThreadBlock(context, threadPointer);

            var staticAddress = threadPointer - staticOffset;
            Span<byte> initialized = stackalloc byte[3];
            Assert.True(memory.TryRead(staticAddress, initialized));
            Assert.Equal((byte)0x31, initialized[0]);
            Assert.Equal((byte)0x42, initialized[1]);
            Assert.Equal((byte)0x00, initialized[2]);
            Assert.Equal(staticAddress + 1, GuestTlsTemplate.ResolveAddress(context, 1, 1));
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }
}
