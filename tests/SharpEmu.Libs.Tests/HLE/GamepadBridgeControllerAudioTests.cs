// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.HLE;

/// <summary>The controller audio side of the gamepad bridge: published pads, the effect command, the PCM ring and its pump.</summary>
public sealed unsafe class GamepadBridgeControllerAudioTests : IDisposable
{
    private readonly byte* _block = (byte*)NativeMemory.AllocZeroed(GamepadBridgeMemory.Size);
    private readonly GamepadBridgeMemory _memory;

    public GamepadBridgeControllerAudioTests()
    {
        _memory = new GamepadBridgeMemory(_block);
        _memory.Initialize();
    }

    public void Dispose() => NativeMemory.Free(_block);

    private sealed class RecordingSink : IHostControllerEffectSink
    {
        public List<byte[]> Reports { get; } = [];

        public void SendControllerEffect(ReadOnlySpan<byte> report) => Reports.Add(report.ToArray());
    }

    private sealed class NoGamepadOutput : IHostGamepadOutput
    {
        public void SetRumble(byte largeMotor, byte smallMotor) { }

        public void SetHapticRumble(byte largeMotor, byte smallMotor, uint durationMs) { }

        public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger) { }

        public void SetAdaptiveTriggerEffect(HostAdaptiveTriggerEffect? leftTrigger, HostAdaptiveTriggerEffect? rightTrigger) { }

        public void SetLightbar(byte red, byte green, byte blue) { }

        public void ResetLightbar() { }
    }

    private sealed class RecordingStream : IHostAudioStream
    {
        public List<byte> Received { get; } = [];

        public bool Disposed { get; private set; }

        public bool Submit(ReadOnlySpan<byte> pcm)
        {
            Received.AddRange(pcm.ToArray());
            return true;
        }

        public void Dispose() => Disposed = true;
    }

    // ---- layout and header -------------------------------------------------------------------------------------

    [Fact]
    public void LayoutKeepsTheGroupsAndTheAudioSlotsApart()
    {
        Assert.True(
            GamepadBridgeMemory.OutputOffset + GamepadBridgeMemory.OutputGroupCount * GamepadBridgeMemory.OutputGroupSize <=
            GamepadBridgeMemory.PcmOffset);
        Assert.True(sizeof(ControllerEffectCommand) + GamepadBridgeMemory.SlotHeaderSize <= GamepadBridgeMemory.OutputGroupSize);
        Assert.Equal(
            GamepadBridgeMemory.PcmOffset + GamepadBridgeMemory.PcmSlotCount * GamepadBridgeMemory.PcmSlotStride,
            GamepadBridgeMemory.Size);
        Assert.True(GamepadBridgeMemory.PcmConsumedOffset + sizeof(ulong) <= GamepadBridgeMemory.PcmRingOffset);
        Assert.True(GamepadBridgeMemory.AudioPadsOffset >= 12 && GamepadBridgeMemory.AudioPadsOffset < GamepadBridgeMemory.HeaderSize);
        Assert.Equal(0, GamepadBridgeMemory.PcmRingBytes % GamepadBridgeMemory.PcmFrameBytes);
        Assert.Equal(0, GamepadBridgeMemory.PcmWriteChunkBytes % GamepadBridgeMemory.PcmFrameBytes);
    }

    [Fact]
    public void AudioPadsStartEmptyAndRoundTripWithoutDisturbingTheHeader()
    {
        Assert.Equal(HostControllerPads.None, _memory.AudioPads);

        _memory.AudioPads = HostControllerPads.WiredDualSense;
        Assert.Equal(HostControllerPads.WiredDualSense, _memory.AudioPads);
        _memory.AudioPads = HostControllerPads.BluetoothDualSense;
        Assert.Equal(HostControllerPads.BluetoothDualSense, _memory.AudioPads);
        Assert.True(_memory.IsValid);
    }

    // ---- selection ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData((int)HostControllerPads.WiredDualSense, true, false, (int)ControllerOutputKind.WiredDevice)]
    [InlineData((int)HostControllerPads.WiredDualSense, true, true, (int)ControllerOutputKind.WiredDevice)]
    [InlineData((int)HostControllerPads.WiredDualSense, false, false, (int)ControllerOutputKind.None)]
    [InlineData((int)HostControllerPads.None, true, true, (int)ControllerOutputKind.None)]
    [InlineData((int)HostControllerPads.BluetoothDualSense, false, true, (int)ControllerOutputKind.Bluetooth)]
    [InlineData((int)HostControllerPads.BluetoothDualSense, true, true, (int)ControllerOutputKind.Bluetooth)]
    [InlineData((int)HostControllerPads.BluetoothDualSense, true, false, (int)ControllerOutputKind.None)]
    // A wired pad whose device is missing falls back to Bluetooth when there is exactly one such pad as well.
    [InlineData((int)(HostControllerPads.WiredDualSense | HostControllerPads.BluetoothDualSense), false, true, (int)ControllerOutputKind.Bluetooth)]
    public void TheChosenOutputFollowsThePadsAndTheDevice(
        int pads, bool wiredDeviceFound, bool bluetoothAvailable, int expected)
    {
        Assert.Equal(
            (ControllerOutputKind)expected,
            ControllerAudioSelection.Choose((HostControllerPads)pads, wiredDeviceFound, bluetoothAvailable));
    }

    [Fact]
    public void TheBridgeLinkAnswersWithWhatTheLauncherPublished()
    {
        var link = new GamepadBridgeControllerLink(_memory, new GamepadBridgeOutputWriter(_memory));
        Assert.Equal(HostControllerPads.None, link.QueryPads());
        Assert.Equal(
            ControllerOutputKind.None,
            ControllerAudioSelection.Choose(link.QueryPads(), wiredDeviceFound: true, bluetoothAvailable: true));

        _memory.AudioPads = HostControllerPads.WiredDualSense;
        Assert.Equal(HostControllerPads.WiredDualSense, link.QueryPads());
        Assert.Equal(
            ControllerOutputKind.WiredDevice,
            ControllerAudioSelection.Choose(link.QueryPads(), wiredDeviceFound: true, bluetoothAvailable: false));

        link.Close();
        Assert.Equal(HostControllerPads.None, link.QueryPads());
    }

    // ---- routing command ---------------------------------------------------------------------------------------

    [Fact]
    public void TheRoutingReportsAreRecognised()
    {
        Assert.Equal(DualSenseAudioRoute.ReportLength, DualSenseAudioRoute.Report(true).Length);
        Assert.True(DualSenseAudioRoute.IsSpeakerRoute(DualSenseAudioRoute.Report(speaker: true)));
        Assert.False(DualSenseAudioRoute.IsSpeakerRoute(DualSenseAudioRoute.Report(speaker: false)));
        Assert.False(DualSenseAudioRoute.IsSpeakerRoute(new byte[10]));
    }

    [Fact]
    public void ARoutingReportCrossesTheBridgeOnceAndIntact()
    {
        var writer = new GamepadBridgeOutputWriter(_memory);
        var reader = new GamepadBridgeOutputReader(_memory);
        var sink = new RecordingSink();
        var target = new NoGamepadOutput();
        Assert.Equal(0, reader.Poll(target, sink));

        writer.SendControllerEffect(DualSenseAudioRoute.Report(speaker: true));
        Assert.Equal(1, reader.Poll(target, sink));
        Assert.Equal([DualSenseAudioRoute.Report(speaker: true)], sink.Reports);
        Assert.Equal(0, reader.Poll(target, sink));

        writer.SendControllerEffect(DualSenseAudioRoute.Report(speaker: false));
        Assert.Equal(1, reader.Poll(target, sink));
        Assert.Equal(DualSenseAudioRoute.Report(speaker: false), sink.Reports[1]);
        Assert.Equal(2, sink.Reports.Count);
    }

    [Fact]
    public void ALongestReportFitsAndATooLongOneIsRefused()
    {
        var writer = new GamepadBridgeOutputWriter(_memory);
        var reader = new GamepadBridgeOutputReader(_memory);
        var sink = new RecordingSink();
        var longest = new byte[ControllerEffectCommand.MaxReportBytes];
        for (var index = 0; index < longest.Length; index++)
        {
            longest[index] = (byte)(index + 1);
        }

        writer.SendControllerEffect(longest);
        reader.Poll(new NoGamepadOutput(), sink);
        Assert.Equal(longest, Assert.Single(sink.Reports));
        Assert.Throws<ArgumentException>(() => writer.SendControllerEffect(new byte[ControllerEffectCommand.MaxReportBytes + 1]));
    }

    [Fact]
    public void WithoutASinkTheEffectGroupIsLeftAlone()
    {
        var writer = new GamepadBridgeOutputWriter(_memory);
        var reader = new GamepadBridgeOutputReader(_memory);
        writer.SendControllerEffect(DualSenseAudioRoute.Report(true));

        Assert.Equal(0, reader.Poll(new NoGamepadOutput()));
        var sink = new RecordingSink();
        Assert.Equal(1, reader.Poll(new NoGamepadOutput(), sink));
    }

    [Fact]
    public void TheLinkSendsAReportOnlyToAKindThePublishedPadsHave()
    {
        var link = new GamepadBridgeControllerLink(_memory, new GamepadBridgeOutputWriter(_memory));
        var reader = new GamepadBridgeOutputReader(_memory);
        var sink = new RecordingSink();
        var route = DualSenseAudioRoute.Report(true);

        Assert.False(link.SendEffect(HostControllerPads.WiredDualSense, route));
        _memory.AudioPads = HostControllerPads.WiredDualSense;
        Assert.False(link.SendEffect(HostControllerPads.BluetoothDualSense, route));
        Assert.Equal(0, reader.Poll(new NoGamepadOutput(), sink));

        Assert.True(link.SendEffect(HostControllerPads.WiredDualSense, route));
        Assert.Equal(1, reader.Poll(new NoGamepadOutput(), sink));
        Assert.Equal(route, Assert.Single(sink.Reports));

        link.Close();
        Assert.False(link.SendEffect(HostControllerPads.WiredDualSense, route));
    }

    // ---- PCM ring ----------------------------------------------------------------------------------------------

    // Frames whose four floats all carry the frame number, so any tear or misplacement shows.
    private static byte[] Frames(int firstFrame, int count)
    {
        var floats = new float[count * 4];
        for (var frame = 0; frame < count; frame++)
        {
            for (var channel = 0; channel < 4; channel++)
            {
                floats[frame * 4 + channel] = firstFrame + frame;
            }
        }

        return MemoryMarshal.AsBytes(floats.AsSpan()).ToArray();
    }

    private static int FrameNumber(ReadOnlySpan<byte> bytes, int frame) =>
        (int)MemoryMarshal.Cast<byte, float>(bytes)[frame * 4];

    [Fact]
    public void PcmComesOutInOrderAcrossTheWrap()
    {
        ulong read = 0;
        var frameBytes = GamepadBridgeMemory.PcmFrameBytes;
        var framesPerRing = GamepadBridgeMemory.PcmRingBytes / frameBytes;
        var next = 0;
        var expected = 0;
        var buffer = new byte[300 * frameBytes];
        // Several laps in uneven pieces: every write ends somewhere else in the ring.
        for (var round = 0; round < 40; round++)
        {
            var count = 100 + round * 7 % 190;
            _memory.WritePcm(0, Frames(next, count));
            next += count;
            int got;
            while ((got = _memory.ReadPcm(0, ref read, buffer)) > 0)
            {
                for (var frame = 0; frame < got / frameBytes; frame++)
                {
                    Assert.Equal(expected++, FrameNumber(buffer, frame));
                }
            }
        }

        Assert.Equal(next, expected);
        Assert.True(next > 3 * framesPerRing);
    }

    [Fact]
    public void AFullRingDropsItsOldestAudioAndKeepsTheNewest()
    {
        ulong read = 0;
        var frameBytes = GamepadBridgeMemory.PcmFrameBytes;
        var total = GamepadBridgeMemory.PcmRingBytes / frameBytes * 2 + 37;
        _memory.WritePcm(0, Frames(0, total));

        var buffer = new byte[GamepadBridgeMemory.PcmRingBytes];
        var got = _memory.ReadPcm(0, ref read, buffer);

        Assert.Equal(GamepadBridgeMemory.PcmReadableBytes, got);
        var frames = got / frameBytes;
        // Consecutive, and ends with the newest frame.
        for (var frame = 0; frame < frames; frame++)
        {
            Assert.Equal(total - frames + frame, FrameNumber(buffer, frame));
        }
    }

    [Fact]
    public void PartialFramesAreNotWrittenAndTheQueueDepthFollowsTheConsumer()
    {
        var frameBytes = GamepadBridgeMemory.PcmFrameBytes;
        var data = Frames(0, 10);
        _memory.WritePcm(0, data.AsSpan(0, 10 * frameBytes - 5));
        Assert.Equal(9 * frameBytes, _memory.PcmQueuedBytes(0));

        ulong read = 0;
        var buffer = new byte[4 * frameBytes];
        Assert.Equal(4 * frameBytes, _memory.ReadPcm(0, ref read, buffer));
        _memory.PublishPcmConsumed(0, read);
        Assert.Equal(5 * frameBytes, _memory.PcmQueuedBytes(0));
    }

    [Fact]
    public void SlotsDoNotShareAudio()
    {
        var frameBytes = GamepadBridgeMemory.PcmFrameBytes;
        _memory.WritePcm(0, Frames(100, 4));
        _memory.WritePcm(1, Frames(200, 4));
        ulong first = 0, second = 0;
        var buffer = new byte[4 * frameBytes];

        Assert.Equal(buffer.Length, _memory.ReadPcm(1, ref second, buffer));
        Assert.Equal(200, FrameNumber(buffer, 0));
        Assert.Equal(buffer.Length, _memory.ReadPcm(0, ref first, buffer));
        Assert.Equal(100, FrameNumber(buffer, 0));
    }

    [Fact]
    public void ACopyTheProducerOverwroteIsDiscardedNotDelivered()
    {
        var frameBytes = GamepadBridgeMemory.PcmFrameBytes;
        _memory.WritePcm(0, Frames(0, 500));
        ulong read = 0;
        var buffer = new byte[400 * frameBytes];
        var interfered = false;

        // While the consumer copies, the producer laps the ring: the copy must not be handed out.
        var got = _memory.ReadPcm(
            0,
            ref read,
            buffer,
            afterCopy: () =>
            {
                if (!interfered)
                {
                    interfered = true;
                    _memory.WritePcm(0, Frames(500, GamepadBridgeMemory.PcmRingBytes / frameBytes));
                }
            });

        Assert.True(interfered);
        var frames = got / frameBytes;
        Assert.True(frames > 0);
        for (var frame = 1; frame < frames; frame++)
        {
            Assert.Equal(FrameNumber(buffer, frame - 1) + 1, FrameNumber(buffer, frame));
        }

        Assert.True(FrameNumber(buffer, 0) >= 500, "a frame from before the producer lapped the ring was delivered");
    }

    [Fact]
    public void AProducerRacingAConsumerNeverShowsATornFrame()
    {
        var frameBytes = GamepadBridgeMemory.PcmFrameBytes;
        const int totalFrames = 400_000;
        var failure = (string?)null;
        var producerDone = false;

        var producer = new Thread(() =>
        {
            var next = 0;
            while (next < totalFrames)
            {
                var count = Math.Min(totalFrames - next, 64 + next % 700);
                _memory.WritePcm(0, Frames(next, count));
                next += count;
            }

            Volatile.Write(ref producerDone, true);
        });

        var consumer = new Thread(() =>
        {
            ulong read = 0;
            var last = -1;
            var buffer = new byte[1000 * frameBytes];
            while (true)
            {
                var finished = Volatile.Read(ref producerDone);
                var got = _memory.ReadPcm(0, ref read, buffer);
                var floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, got));
                for (var frame = 0; frame < got / frameBytes; frame++)
                {
                    var number = (int)floats[frame * 4];
                    if (floats[frame * 4 + 1] != number || floats[frame * 4 + 2] != number || floats[frame * 4 + 3] != number)
                    {
                        failure = $"torn frame {number}";
                        return;
                    }

                    if (number <= last)
                    {
                        failure = $"frame {number} came after {last}";
                        return;
                    }

                    last = number;
                }

                if (finished && got == 0)
                {
                    break;
                }
            }

            if (last != totalFrames - 1)
            {
                failure = $"the newest frame {last} was not delivered";
            }
        });

        consumer.Start();
        producer.Start();
        Assert.True(producer.Join(TimeSpan.FromSeconds(60)));
        Assert.True(consumer.Join(TimeSpan.FromSeconds(60)));
        Assert.Null(failure);
    }

    // ---- stream and pump ---------------------------------------------------------------------------------------

    [Fact]
    public void TheChildStreamNeedsABluetoothPadAndASlotAndFreesTheSlotOnDispose()
    {
        var link = new GamepadBridgeControllerLink(_memory, new GamepadBridgeOutputWriter(_memory));
        Assert.Null(link.TryOpenBluetoothStream(48_000, 19_200, routeSpeaker: true));

        _memory.AudioPads = HostControllerPads.WiredDualSense;
        Assert.Null(link.TryOpenBluetoothStream(48_000, 19_200, routeSpeaker: true));

        _memory.AudioPads = HostControllerPads.BluetoothDualSense;
        var first = link.TryOpenBluetoothStream(48_000, 19_200, routeSpeaker: true);
        var second = link.TryOpenBluetoothStream(44_100, 19_200, routeSpeaker: false);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(link.TryOpenBluetoothStream(48_000, 19_200, routeSpeaker: true));
        Assert.Null(link.TryOpenBluetoothStream(100, 19_200, routeSpeaker: true));

        var control = _memory.ReadPcmControl(0);
        Assert.True(control.Active);
        Assert.Equal(48_000u, control.SampleRate);
        Assert.Equal(19_200, control.MaxQueuedBytes);
        Assert.True(control.Speaker);
        Assert.False(_memory.ReadPcmControl(1).Speaker);

        first!.Dispose();
        Assert.False(_memory.ReadPcmControl(0).Active);
        Assert.NotNull(link.TryOpenBluetoothStream(48_000, 19_200, routeSpeaker: true));
        second!.Dispose();
    }

    [Fact]
    public void TheChildStreamReportsItsQueueInMillisecondsAndStopsWhenTheLinkCloses()
    {
        var link = new GamepadBridgeControllerLink(_memory, new GamepadBridgeOutputWriter(_memory));
        _memory.AudioPads = HostControllerPads.BluetoothDualSense;
        var stream = link.TryOpenBluetoothStream(48_000, 19_200, routeSpeaker: true)!;

        Assert.Equal(0, stream.QueuedMilliseconds);
        Assert.True(stream.Submit(Frames(0, 1500))); // 31 ms at 48 kHz, more than one chunk
        Assert.InRange(stream.QueuedMilliseconds, 30, 31);

        link.Close();
        Assert.False(stream.Submit(Frames(0, 10)));
        Assert.Equal(-1, stream.QueuedMilliseconds);
        stream.Dispose(); // after the section is gone: must not touch it
    }

    [Fact]
    public void ThePumpFollowsTheChildsStreamAndFeedsTheLaunchersStream()
    {
        var link = new GamepadBridgeControllerLink(_memory, new GamepadBridgeOutputWriter(_memory));
        _memory.AudioPads = HostControllerPads.BluetoothDualSense;
        var opened = new List<(uint Rate, int Max, bool Speaker, RecordingStream Stream)>();
        var now = 0L;
        var pump = new GamepadBridgePcmPump(
            _memory,
            0,
            (rate, max, speaker) =>
            {
                var stream = new RecordingStream();
                opened.Add((rate, max, speaker, stream));
                return stream;
            },
            () => now);

        Assert.Equal(0, pump.Poll());
        Assert.Empty(opened);

        var child = link.TryOpenBluetoothStream(48_000, 19_200, routeSpeaker: true)!;
        child.Submit(Frames(0, 10));
        // Audio written between the open and the launcher's first look is the new stream's: it is played.
        Assert.Equal(10 * GamepadBridgeMemory.PcmFrameBytes, pump.Poll());

        var (rate, max, speaker, launcherStream) = Assert.Single(opened);
        Assert.Equal((48_000u, 19_200, true), (rate, max, speaker));

        child.Submit(Frames(10, 20));
        Assert.Equal(20 * GamepadBridgeMemory.PcmFrameBytes, pump.Poll());
        Assert.Equal(30, launcherStream.Received.Count / GamepadBridgeMemory.PcmFrameBytes);
        Assert.Equal(0, FrameNumber(launcherStream.Received.ToArray(), 0));
        Assert.Equal(29, FrameNumber(launcherStream.Received.ToArray(), 29));
        Assert.Equal(0, _memory.PcmQueuedBytes(0));
        Assert.True(pump.IsPlaying);

        child.Dispose();
        Assert.Equal(0, pump.Poll());
        Assert.True(launcherStream.Disposed);
        Assert.False(pump.IsPlaying);
    }

    [Fact]
    public void ThePumpRetriesAFailedOpenAfterADelayAndDropsAudioMeanwhile()
    {
        var link = new GamepadBridgeControllerLink(_memory, new GamepadBridgeOutputWriter(_memory));
        _memory.AudioPads = HostControllerPads.BluetoothDualSense;
        var attempts = 0;
        var now = 0L;
        RecordingStream? stream = null;
        var pump = new GamepadBridgePcmPump(
            _memory,
            0,
            (_, _, _) =>
            {
                attempts++;
                return attempts < 3 ? null : stream = new RecordingStream();
            },
            () => now);
        var child = link.TryOpenBluetoothStream(48_000, 19_200, routeSpeaker: false)!;

        child.Submit(Frames(0, 10));
        pump.Poll();
        Assert.Equal(1, attempts);
        pump.Poll();
        Assert.Equal(1, attempts); // too soon to try again

        now += System.Diagnostics.Stopwatch.Frequency + 1;
        child.Submit(Frames(10, 10));
        pump.Poll();
        Assert.Equal(2, attempts);
        now += System.Diagnostics.Stopwatch.Frequency + 1;
        child.Submit(Frames(20, 10));
        pump.Poll();
        Assert.Equal(3, attempts);

        // What piled up while nothing played (frames 0-19) was dropped; the stream plays from where it opened.
        Assert.Equal(20, FrameNumber(stream!.Received.ToArray(), 0));
        Assert.Equal(10, stream.Received.Count / GamepadBridgeMemory.PcmFrameBytes);
        child.Submit(Frames(30, 5));
        Assert.Equal(5 * GamepadBridgeMemory.PcmFrameBytes, pump.Poll());
        Assert.Equal(34, FrameNumber(stream.Received.ToArray(), 14));
    }

    [Fact]
    public void AStreamReopenedInTheSlotDoesNotPlayTheOldOnesLeftovers()
    {
        var link = new GamepadBridgeControllerLink(_memory, new GamepadBridgeOutputWriter(_memory));
        _memory.AudioPads = HostControllerPads.BluetoothDualSense;
        var streams = new List<RecordingStream>();
        var pump = new GamepadBridgePcmPump(
            _memory,
            0,
            (_, _, _) =>
            {
                var stream = new RecordingStream();
                streams.Add(stream);
                return stream;
            });

        var first = link.TryOpenBluetoothStream(48_000, 19_200, routeSpeaker: true)!;
        pump.Poll();
        first.Submit(Frames(0, 8));
        first.Dispose();
        // Closed and reopened before the launcher polled again: the 8 frames are the old stream's, not played.
        var second = link.TryOpenBluetoothStream(44_100, 19_200, routeSpeaker: true)!;
        second.Submit(Frames(100, 3));
        Assert.Equal(3 * GamepadBridgeMemory.PcmFrameBytes, pump.Poll());

        Assert.Equal(2, streams.Count);
        Assert.True(streams[0].Disposed);
        Assert.Empty(streams[0].Received);
        second.Submit(Frames(200, 2));
        pump.Poll();
        Assert.Equal(100, FrameNumber(streams[1].Received.ToArray(), 0));
        Assert.Equal(200, FrameNumber(streams[1].Received.ToArray(), 3));
    }

    [Fact]
    public void ReleasingThePumpClosesTheLaunchersStreamAndTheNextPollReopensIt()
    {
        var link = new GamepadBridgeControllerLink(_memory, new GamepadBridgeOutputWriter(_memory));
        _memory.AudioPads = HostControllerPads.BluetoothDualSense;
        var streams = new List<RecordingStream>();
        var pump = new GamepadBridgePcmPump(
            _memory, 0, (_, _, _) => { var stream = new RecordingStream(); streams.Add(stream); return stream; });
        var child = link.TryOpenBluetoothStream(48_000, 19_200, routeSpeaker: true)!;
        pump.Poll();

        pump.Release();
        Assert.True(streams[0].Disposed);
        Assert.False(pump.IsPlaying);

        child.Submit(Frames(0, 2));
        pump.Poll();
        Assert.Equal(2, streams.Count);
        Assert.True(pump.IsPlaying);
    }
}
