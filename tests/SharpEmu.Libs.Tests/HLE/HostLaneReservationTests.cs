// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.HLE;

public sealed class HostLaneReservationTests
{
    [Fact]
    public void TheReservedCoreAndTheGuestLanesSplitTheMachine()
    {
        if (!HostLaneReservation.Active)
        {
            Assert.Equal(0ul, HostLaneReservation.GuestMask);
            return;
        }

        var lanes = Environment.ProcessorCount;
        var all = lanes == 64 ? ulong.MaxValue : (1UL << lanes) - 1;
        Assert.Equal(0ul, HostLaneReservation.ReservedMask & HostLaneReservation.GuestMask);
        Assert.Equal(0ul, HostLaneReservation.FrontEndMask & HostLaneReservation.GuestMask);
        Assert.Equal(0ul, HostLaneReservation.ReservedMask & HostLaneReservation.FrontEndMask);
        Assert.Equal(all, HostLaneReservation.ReservedMask | HostLaneReservation.FrontEndMask | HostLaneReservation.GuestMask);
        Assert.InRange(BitOperations.PopCount(HostLaneReservation.ReservedMask), 1, 2);
        Assert.InRange(BitOperations.PopCount(HostLaneReservation.FrontEndMask), lanes >= 12 ? 1 : 0, 2);
    }
}
