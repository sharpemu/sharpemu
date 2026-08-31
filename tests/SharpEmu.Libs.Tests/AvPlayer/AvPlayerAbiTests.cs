// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerAbiTests
{
    [Theory]
    [InlineData(Generation.Gen4, false, 108UL)]
    [InlineData(Generation.Gen5, false, 108UL)]
    [InlineData(Generation.Gen4, true, 164UL)]
    [InlineData(Generation.Gen5, true, 116UL)]
    public void InitAutoStartOffsetMatchesGeneration(
        Generation generation,
        bool extended,
        ulong expected)
    {
        Assert.Equal(expected, AvPlayerExports.GetAutoStartOffset(generation, extended));
    }

    [Theory]
    [InlineData(Generation.Gen4, false, 104UL)]
    [InlineData(Generation.Gen5, false, 104UL)]
    [InlineData(Generation.Gen4, true, 600UL)]
    [InlineData(Generation.Gen5, true, 552UL)]
    public void InitVideoBufferCountOffsetMatchesGeneration(
        Generation generation,
        bool extended,
        ulong expected)
    {
        Assert.Equal(
            expected,
            AvPlayerExports.GetOutputVideoFrameBufferCountOffset(
                generation,
                extended));
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(7, 7)]
    [InlineData(16, 16)]
    [InlineData(17, 2)]
    public void VideoBufferCountUsesTheDocumentedRange(int requested, int expected)
    {
        Assert.Equal(
            expected,
            AvPlayerExports.NormalizeOutputVideoFrameBufferCount(requested));
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(1u, true)]
    [InlineData(2u, false)]
    [InlineData(uint.MaxValue, false)]
    public void AvSyncModeRejectsUnknownValues(uint mode, bool expected)
    {
        Assert.Equal(expected, AvPlayerExports.IsValidAvSyncMode(mode));
    }

    [Fact]
    public void DefaultSyncUsesDeliveredAudioAsTheVideoClock()
    {
        Assert.Equal(
            0,
            AvPlayerExports.CalculateExpectedDefaultSyncVideoFrame(
                hasAudio: true,
                deliveredAudioFrameCount: 0,
                internalClockSeconds: 10,
                framesPerSecond: 60));
        Assert.Equal(
            1,
            AvPlayerExports.CalculateExpectedDefaultSyncVideoFrame(
                hasAudio: true,
                deliveredAudioFrameCount: 1,
                internalClockSeconds: 10,
                framesPerSecond: 60));
    }

    [Fact]
    public void DefaultVideoOnlySyncUsesTheInternalClock()
    {
        Assert.Equal(
            90,
            AvPlayerExports.CalculateExpectedDefaultSyncVideoFrame(
                hasAudio: false,
                deliveredAudioFrameCount: 0,
                internalClockSeconds: 3,
                framesPerSecond: 30));
    }

    [Theory]
    [InlineData(Generation.Gen4, 40)]
    [InlineData(Generation.Gen5, 32)]
    public void LegacyStreamInfoSizeMatchesGeneration(
        Generation generation,
        int expected)
    {
        Assert.Equal(expected, AvPlayerExports.GetLegacyStreamInfoSize(generation));
    }

    [Theory]
    [InlineData(Generation.Gen4, 0u, 0u)]
    [InlineData(Generation.Gen4, 1u, 1u)]
    [InlineData(Generation.Gen5, 0u, 1u)]
    [InlineData(Generation.Gen5, 1u, 2u)]
    public void StreamTypeMatchesGeneration(
        Generation generation,
        uint streamIndex,
        uint expected)
    {
        Assert.Equal(expected, AvPlayerExports.GetStreamType(generation, streamIndex));
    }

    [Fact]
    public void Gen5StreamInfoExCarriesVideoColorMetadata()
    {
        var info = new byte[104];

        AvPlayerExports.WriteGen5StreamInfoEx(
            info,
            streamType: 1,
            width: 3840,
            height: 2160,
            framesPerSecond: 59.94,
            durationMilliseconds: 8_508,
            aspectRatio: 16f / 9f,
            videoFullRange: true,
            colorPrimaries: 9,
            transferCharacteristics: 16);

        Assert.Equal(16f / 9f, BinaryPrimitives.ReadSingleLittleEndian(info.AsSpan(24)));
        Assert.Equal(1, info[58]);
        Assert.Equal(59.94, BinaryPrimitives.ReadDoubleLittleEndian(info.AsSpan(0x40)));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(0x48)));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(0x4C)));
        Assert.Equal(8_508UL, BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(0x60)));
    }

    [Fact]
    public void Gen5StreamInfoExCarriesAudioFormat()
    {
        var info = new byte[104];

        AvPlayerExports.WriteGen5AudioStreamInfoEx(
            info,
            streamType: 2,
            channelCount: 2,
            sampleRate: 48_000,
            durationMilliseconds: 38_767);

        Assert.Equal(104UL, BinaryPrimitives.ReadUInt64LittleEndian(info));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(8)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(16)));
        Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(20)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(24)));
        Assert.Equal(38_767UL, BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(0x60)));
    }

    [Fact]
    public void Gen5FrameInfoExCarriesPitchCropAndFrameRate()
    {
        var info = new byte[104];

        AvPlayerExports.WriteVideoFrameInfo(
            info,
            Generation.Gen5,
            extended: true,
            bufferAddress: 0x1234_5000,
            timestamp: 2_903,
            width: 378,
            visibleWidth: 378,
            height: 150,
            pitch: 512,
            framesPerSecond: 29.97,
            aspectRatio: 2.52f,
            videoFullRange: true,
            colorPrimaries: 1,
            transferCharacteristics: 16);

        Assert.Equal(0x1234_5000UL, BinaryPrimitives.ReadUInt64LittleEndian(info));
        Assert.Equal(2_903UL, BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(16)));
        Assert.Equal(378u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(24)));
        Assert.Equal(150u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(28)));
        Assert.Equal(2.52f, BinaryPrimitives.ReadSingleLittleEndian(info.AsSpan(32)));
        Assert.Equal(134u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(48)));
        Assert.Equal(512u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(60)));
        Assert.Equal(8, info[64]);
        Assert.Equal(8, info[65]);
        Assert.Equal(1, info[66]);
        Assert.Equal(29.97, BinaryPrimitives.ReadDoubleLittleEndian(info.AsSpan(0x48)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(0x50)));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(0x54)));
    }

    [Fact]
    public void FallbackFrameValidationUsesTheDecodedDimensions()
    {
        var fullHdFrame = new byte[1920 * 1080 * 4];

        Assert.True(AvPlayerExports.IsValidBgraFrame(fullHdFrame, 1920, 1080));
        Assert.False(AvPlayerExports.IsValidBgraFrame(fullHdFrame, 3840, 2160));
        Assert.False(AvPlayerExports.IsValidBgraFrame(fullHdFrame, 0, 1080));
    }

    [Fact]
    public void PosterSuppressesTheDuplicateFirstHostDecodedFrame()
    {
        var skipFirstDecodedFrame = true;

        Assert.False(AvPlayerExports.ShouldPublishFallbackPlaybackFrame(
            advanced: true,
            hasPresentation: true,
            ref skipFirstDecodedFrame));
        Assert.False(skipFirstDecodedFrame);

        Assert.True(AvPlayerExports.ShouldPublishFallbackPlaybackFrame(
            advanced: true,
            hasPresentation: true,
            ref skipFirstDecodedFrame));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void CompletedFallbackIsReleasedAtGuestEndOfStream(
        bool fallbackCompleted,
        bool guestEndOfStream,
        bool expected)
    {
        var completedTicks = Stopwatch.GetTimestamp();
        Assert.Equal(
            expected,
            AvPlayerExports.ShouldReleaseCompletedFallback(
                fallbackCompleted,
                guestEndOfStream,
                completedTicks,
                completedTicks));
    }

    /// <summary>
    /// A title that pauses its player after the poster frame never reaches end
    /// of stream, so the hold must expire on its own; otherwise the last movie
    /// image stays pinned over everything the game renders next.
    /// </summary>
    [Fact]
    public void CompletedFallbackHoldExpiresWithoutGuestEndOfStream()
    {
        var completedTicks = Stopwatch.GetTimestamp();
        Assert.False(
            AvPlayerExports.ShouldReleaseCompletedFallback(
                fallbackPlaybackCompleted: true,
                guestEndOfStream: false,
                completedTicks,
                completedTicks + (Stopwatch.Frequency / 10)));
        Assert.True(
            AvPlayerExports.ShouldReleaseCompletedFallback(
                fallbackPlaybackCompleted: true,
                guestEndOfStream: false,
                completedTicks,
                completedTicks + (Stopwatch.Frequency * 2)));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void PendingFallbackCompletionWaitsForLoopingToStop(
        bool completionPending,
        bool looping,
        bool expected)
    {
        Assert.Equal(
            expected,
            AvPlayerExports.ShouldCompleteGuestPlaybackAfterFallback(
                completionPending,
                looping));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void FallbackPresentationFollowsGuestPlaybackState(
        bool started,
        bool paused,
        bool expected)
    {
        Assert.Equal(
            expected,
            AvPlayerExports.ShouldPresentFallback(started, paused));
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    public void CompletedFallbackCannotCreateFrozenPoster(
        bool fallbackAttempted,
        bool fallbackRunning,
        bool hasPresentation,
        bool expected)
    {
        Assert.Equal(
            expected,
            AvPlayerExports.ShouldCreateFallbackPoster(
                fallbackAttempted,
                fallbackRunning,
                hasPresentation));
    }
}
