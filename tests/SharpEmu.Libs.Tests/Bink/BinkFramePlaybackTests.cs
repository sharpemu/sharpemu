// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Bink;
using Xunit;

namespace SharpEmu.Libs.Tests.Bink;

public sealed class BinkFramePlaybackTests
{
    [Fact]
    public void FramesAdvanceAccordingToMovieClock()
    {
        using var playback = new BinkFramePlayback(new SequenceDecoder(1, 2, 3));

        Assert.Equal(1, WaitForAdvancedFrame(playback)[0]);
        Assert.True(playback.TryGetFrame(true, out var heldFrame, out var advanced));
        Assert.False(advanced);
        Assert.Equal(1, heldFrame[0]);

        Assert.Equal(2, WaitForAdvancedFrame(playback)[0]);
        Assert.Equal(3, WaitForAdvancedFrame(playback)[0]);
    }

    private static byte[] WaitForAdvancedFrame(BinkFramePlayback playback)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (playback.TryGetFrame(true, out var frame, out var advanced) && advanced)
            {
                return frame;
            }

            Thread.Sleep(1);
        }

        throw new TimeoutException("The decoder did not produce a frame.");
    }

    [Fact]
    public void FirstFrameWaitsUntilPresentationStarts()
    {
        using var playback = new BinkFramePlayback(new SequenceDecoder(1, 2));

        var first = WaitForFrame(playback, advanceClock: false);
        Assert.Equal(1, first[0]);
        Thread.Sleep(100);

        Assert.True(playback.TryGetFrame(false, out var held, out var advanced));
        Assert.False(advanced);
        Assert.Equal(1, held[0]);

        Assert.True(playback.TryGetFrame(true, out held, out advanced));
        Assert.False(advanced);
        Assert.Equal(1, held[0]);
        Assert.Equal(2, WaitForAdvancedFrame(playback)[0]);
    }

    private static byte[] WaitForFrame(
        BinkFramePlayback playback,
        bool advanceClock)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (playback.TryGetFrame(advanceClock, out var frame, out _))
            {
                return frame;
            }

            Thread.Sleep(1);
        }

        throw new TimeoutException("The decoder did not produce a frame.");
    }

    private sealed class SequenceDecoder(params byte[] values) : IBinkFrameDecoder
    {
        private int _index;

        public uint Width => 1;

        public uint Height => 1;

        public uint FramesPerSecondNumerator => 20;

        public uint FramesPerSecondDenominator => 1;

        public bool TryDecodeNextFrame(Span<byte> destination)
        {
            if (_index >= values.Length)
            {
                return false;
            }

            destination.Fill(values[_index++]);
            return true;
        }

        public void Dispose()
        {
        }
    }
}
