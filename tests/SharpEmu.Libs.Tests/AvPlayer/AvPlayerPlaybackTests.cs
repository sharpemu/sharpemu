// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class AvPlayerPlaybackTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CatchUpReachesVideoEndAfterAudioStops(bool looping, bool extended)
    {
        using var playback = new DecodedPlayback(178, 10, 288, 6, looping);
        Assert.Equal(1, playback.ReadVideo(extended));
        Assert.Equal(181L, playback.GetState<long>("NextFrameIndex"));
        Assert.Equal(6144, playback.CurrentTime());

        for (var audioBlock = 0; audioBlock < 5; audioBlock++)
        {
            Assert.Equal(1, playback.ReadAudio());
        }
        Assert.Equal(1, playback.ReadVideo(extended));
        Assert.Equal(1, playback.ReadAudio());
        Assert.Equal(1, playback.ReadVideo(extended));
        Assert.Equal(0, playback.ReadAudio());
        Assert.Equal(294L, playback.GetState<long>("NextAudioFrameIndex"));

        for (var attempt = 0; attempt < 10 && !playback.ReachedVideoEnd; attempt++)
        {
            playback.ReadVideo(extended);
        }

        Assert.True(playback.ReachedVideoEnd, "The audio clock must not strand eligible video frames.");
        Assert.Equal(looping ? 1 : 0, playback.IsActive());
        if (looping)
        {
            Assert.Equal(0L, playback.GetState<long>("NextFrameIndex"));
            Assert.Equal(0L, playback.GetState<long>("NextAudioFrameIndex"));
            Assert.Null(playback.GetState<object?>("VideoDecoder"));
            Assert.Null(playback.GetState<object?>("AudioDecoderOutput"));
        }
    }

    [Fact]
    public void VideoWaitsForAudioUnlessSynchronizationIsDisabled()
    {
        using var playback = new DecodedPlayback(0, 4, 0, 2);
        Assert.Equal(1, playback.ReadVideo());
        Assert.Equal(0, playback.ReadVideo());
        Assert.Equal(1, playback.ReadAudio());
        Assert.Equal(0, playback.ReadVideo());
        Assert.Equal(1, playback.ReadAudio());
        Assert.Equal(1, playback.ReadVideo());
        Assert.Equal(0, playback.ReadVideo());

        playback.SetSynchronizationMode(1);
        Assert.Equal(1, playback.ReadVideo());
        Assert.Equal(3L, playback.GetState<long>("NextFrameIndex"));
    }

    [Fact]
    public void PauseKeepsPendingFramesUntilResume()
    {
        using var playback = new DecodedPlayback(0, 3, 2, 1);
        playback.Pause();
        Assert.Equal(0, playback.ReadVideo());
        Assert.Equal(0, playback.ReadAudio());
        Assert.Equal(0L, playback.GetState<long>("NextFrameIndex"));
        Assert.Equal(2L, playback.GetState<long>("NextAudioFrameIndex"));
        Assert.Equal(1, playback.IsActive());

        playback.Resume();
        Assert.Equal(1, playback.ReadVideo());
        Assert.Equal(1, playback.ReadAudio());
    }

    [Fact]
    public void AudioEndDoesNotReleaseVideoAheadOfTheAudioClock()
    {
        using var playback = new DecodedPlayback(0, 3, 2, 0);
        Assert.Equal(1, playback.ReadVideo());
        Assert.Equal(0, playback.ReadAudio());
        Assert.Equal(0, playback.ReadVideo());
        Assert.Equal(2L, playback.GetState<long>("NextFrameIndex"));
        Assert.Equal(1, playback.IsActive());
    }

    private sealed class DecodedPlayback : IDisposable
    {
        private const ulong Handle = 0xA0_0000_2300;
        private const ulong MemoryAddress = 0x1_0000_0000;
        private const ulong InfoAddress = MemoryAddress + 0x100;
        private const int FrameByteCount = 16 * 16 * 3 / 2;
        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly FakeCpuMemory _memory = new(MemoryAddress, 0x10000);
        private readonly CpuContext _context;
        private readonly object _player;
        private readonly Thread _decoderWorker;

        public DecodedPlayback(
            int firstVideoFrame,
            int videoFrameCount,
            int deliveredAudioBlocks,
            int remainingAudioBlocks,
            bool looping = false)
        {
            _context = new CpuContext(_memory, Generation.Gen5);
            AvPlayerExports.RegisterPlayerForTest(Handle, 16, 16, 6267, hasAudio: true);
            var players = (IDictionary)typeof(AvPlayerExports)
                .GetField("Players", PrivateStatic)!.GetValue(null)!;
            var stateGate = typeof(AvPlayerExports).GetField("StateGate", PrivateStatic)!.GetValue(null)!;
            lock (stateGate)
            {
                _player = players[Handle]!;
            }
            SetState("SourcePath", "decoded-test-stream");
            SetState("Started", true);
            SetState("Looping", looping);
            SetState("NextFrameIndex", (long)firstVideoFrame);
            SetState("NextAudioFrameIndex", (long)deliveredAudioBlocks);
            SetState("AudioBufferBase", MemoryAddress + 0x6000);
            SetState("RawAudioFrame", new byte[4096]);
            SetState("AudioDecoderOutput", new MemoryStream(new byte[remainingAudioBlocks * 4096]));
            var buffers = GetState<ulong[]>("GuestBuffers");
            for (var bufferIndex = 0; bufferIndex < buffers.Length; bufferIndex++)
            {
                buffers[bufferIndex] = MemoryAddress + 0x1000 + (ulong)bufferIndex * 0x2000;
            }

            var videoBytes = new byte[videoFrameCount * FrameByteCount];
            for (var frameIndex = 0; frameIndex < videoFrameCount; frameIndex++)
            {
                videoBytes.AsSpan(frameIndex * FrameByteCount, FrameByteCount)
                    .Fill(checked((byte)(firstVideoFrame + frameIndex)));
            }
            var queueType = typeof(AvPlayerExports).GetNestedType("VideoFrameQueue", BindingFlags.NonPublic)!;
            var decoder = Activator.CreateInstance(queueType, new MemoryStream(videoBytes), FrameByteCount, 16)!;
            SetState("VideoDecoder", decoder);
            _decoderWorker = (Thread)queueType.GetField("_worker", PrivateInstance)!.GetValue(decoder)!;
            Assert.True(_decoderWorker.Join(TimeSpan.FromSeconds(5)), "The test decoder did not finish.");
        }

        public bool ReachedVideoEnd =>
            GetState<bool>("EndOfStream") || GetState<object?>("VideoDecoder") is null;

        public int ReadVideo(bool extended = true)
        {
            SetArguments();
            var result = extended
                ? AvPlayerExports.AvPlayerGetVideoDataEx(_context)
                : AvPlayerExports.AvPlayerGetVideoData(_context);
            if (result == 1)
            {
                Span<byte> frameInfo = stackalloc byte[40];
                Assert.True(_memory.TryRead(InfoAddress, frameInfo));
                var address = BinaryPrimitives.ReadUInt64LittleEndian(frameInfo);
                var timestamp = BinaryPrimitives.ReadUInt64LittleEndian(frameInfo[16..]);
                Span<byte> firstPixel = stackalloc byte[1];
                Assert.True(_memory.TryRead(address, firstPixel));
                Assert.Equal((byte)(GetState<long>("NextFrameIndex") - 1), firstPixel[0]);
                Assert.Equal((ulong)Math.Round(firstPixel[0] * 1000.0 / 30), timestamp);
            }
            return result;
        }

        public int ReadAudio()
        {
            SetArguments();
            return AvPlayerExports.AvPlayerGetAudioData(_context);
        }

        public int CurrentTime()
        {
            SetArguments();
            return AvPlayerExports.AvPlayerCurrentTime(_context);
        }

        public int IsActive()
        {
            SetArguments();
            return AvPlayerExports.AvPlayerIsActive(_context);
        }

        public void Pause()
        {
            SetArguments();
            Assert.Equal(0, AvPlayerExports.AvPlayerPause(_context));
        }

        public void Resume()
        {
            SetArguments();
            Assert.Equal(0, AvPlayerExports.AvPlayerResume(_context));
        }

        public void SetSynchronizationMode(uint mode)
        {
            SetArguments();
            _context[CpuRegister.Rsi] = mode;
            Assert.Equal(0, AvPlayerExports.AvPlayerSetAvSyncMode(_context));
        }

        public TValue GetState<TValue>(string propertyName) =>
            (TValue)_player.GetType().GetProperty(propertyName)!.GetValue(_player)!;

        private void SetState(string propertyName, object value) =>
            _player.GetType().GetProperty(propertyName)!.SetValue(_player, value);

        private void SetArguments()
        {
            _context[CpuRegister.Rdi] = Handle;
            _context[CpuRegister.Rsi] = InfoAddress;
        }

        public void Dispose()
        {
            AvPlayerExports.RemovePlayerForTest(Handle);
            Assert.True(_decoderWorker.Join(TimeSpan.FromSeconds(5)));
        }
    }
}
