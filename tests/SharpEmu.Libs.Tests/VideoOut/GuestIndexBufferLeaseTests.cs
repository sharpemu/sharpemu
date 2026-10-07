// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class GuestIndexBufferLeaseTests
{
    [Fact]
    public void RecordCopiesReturnPoolLeaseOnlyOnce()
    {
        var data = GuestDataPool.Shared.Rent(12);
        var lease = new GuestIndexBufferLease(data);
        var buffer = new GuestIndexBuffer(
            data,
            Length: 12,
            Is32Bit: false,
            Pooled: true,
            lease);
        var copy = buffer with { };

        try
        {
            Assert.True(copy.TryReturnPooledData());
            Assert.True(buffer.LeaseReturned);
            Assert.False(buffer.TryReturnPooledData());
        }
        finally
        {
            buffer.TryReturnPooledData();
        }
    }
}
