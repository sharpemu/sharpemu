// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Tls;

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
            Assert.True(staticOffset <= GuestTlsTemplate.StartupStaticTlsReservation);
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }

    [Fact]
    public void StartupReservationExpandsForGtaVStaticTlsSpan()
    {
        try
        {
            GuestTlsTemplate.Reset();

            var staticOffset = GuestTlsTemplate.RegisterModule(
                moduleId: 1,
                initImage: new byte[0x20],
                memorySize: 0x13550,
                alignment: 0x10);

            Assert.Equal(0x13550UL, staticOffset);
            Assert.Equal(0x14000UL, GuestTlsTemplate.StartupStaticTlsReservation);
            Assert.True(
                GuestTlsTemplate.StartupStaticTlsReservation >
                GuestTlsTemplate.MinimumStartupStaticTlsReservation);
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }
}
