// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class TouchContactIdsTests
{
    // Lifting the finger and touching again is a new stroke, so it needs a new id.
    [Fact]
    public void EachNewContactGetsANewId_AndKeepsItWhileDown()
    {
        var ids = new TouchContactIds();
        var first = ids.Track(0, down: true);
        Assert.Equal(first, ids.Track(0, down: true));
        ids.Track(0, down: false);
        var second = ids.Track(0, down: true);

        Assert.NotEqual(0, first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TwoFingersGetDifferentIds()
    {
        var ids = new TouchContactIds();
        Assert.NotEqual(ids.Track(0, down: true), ids.Track(1, down: true));
    }

    [Fact]
    public void IdsWrapFrom127BackTo1()
    {
        var ids = new TouchContactIds();
        byte last = 0;
        for (var contact = 0; contact < 128; contact++)
        {
            last = ids.Track(0, down: true);
            ids.Track(0, down: false);
        }

        Assert.Equal(1, last);
    }
}
