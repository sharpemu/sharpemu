// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Ngs2;

public sealed partial class Ngs2MixerTests
{
    [Theory]
    [InlineData(0xfe7017e0u)]
    [InlineData(0xe01770feu)]
    public void Atrac9SamplerDecodesAndReportsCompressedBytes(uint config)
    {
        SetupAtrac9(config); SilentAtrac9();
        Block(Blocks, 0, 192, 256);
        QueueAtrac9(); Play();
        Assert.All(Render(), sample => Assert.Equal(0f, sample));
        _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = 56;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetState(_ctx));
        Assert.Equal(4UL, Read64(Params + 24));
        Assert.Equal(192UL, Read64(Params + 32));
        for (var i = 1; i < 64; i++) Render();
        _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetStateFlags(_ctx));
        Assert.Equal(0u, Read32(Params));
    }

    [Fact]
    public void RdrMono24000SuperframeSupportsSampleWindowsAndResampling()
    {
        SetupAtrac9(0xf00540fe, 24000); SilentAtrac9(frames: 4);
        Block(Blocks, 0, 192, 4, skip: 416);
        QueueAtrac9(); Play();
        Render();
        _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = 56;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetState(_ctx));
        Assert.Equal(2UL, Read64(Params + 24));
        Assert.Equal(192UL, Read64(Params + 32));
        Assert.Equal(3u, Read32(Params));
        Render();
        _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetStateFlags(_ctx));
        Assert.Equal(0u, Read32(Params));
    }

    [Fact]
    public void Atrac9LoopsReuseDecodedSamplesWithoutCountingPcmAsCompressedData()
    {
        SetupAtrac9(); SilentAtrac9();
        Block(Blocks, 0, 192, 4, repeats: 1, skip: 20);
        QueueAtrac9(); Play(); Render(); Render();
        _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = 56;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetState(_ctx));
        Assert.Equal(8UL, Read64(Params + 24));
        Assert.Equal(384UL, Read64(Params + 32));
        Assert.Equal(0u, Read32(Params));
    }

    [Fact]
    public void ContinuousAtrac9BlocksRetainTheDecoderBetweenRenders()
    {
        SetupAtrac9(); SilentAtrac9();
        Block(Blocks, 0, 192, 4);
        Block(Blocks + 40, 0, 192, 4);
        QueueAtrac9(count: 2, flags: 1); Play(); Render(); Render();
        _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = 56;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetState(_ctx));
        Assert.Equal(384UL, Read64(Params + 32));
        Assert.Equal(0u, Read32(Params));
    }

    [Fact]
    public void Atrac9OnlyReportsTheSuperframesNeededForPlayback()
    {
        SetupAtrac9();
        var data = new byte[384];
        new byte[] { 0, 0x84, 0, 0x40 }.CopyTo(data, 0);
        new byte[] { 0, 0x84, 0, 0x40 }.CopyTo(data, 192);
        Write(Samples, data); Block(Blocks, 0, 384, 512);
        QueueAtrac9(); Play(); Render();
        _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = 56;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetState(_ctx));
        Assert.Equal(192UL, Read64(Params + 32));
        for (var i = 1; i < 128; i++) Render();
        _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = 56;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetState(_ctx));
        Assert.Equal(384UL, Read64(Params + 32));
        Assert.Equal(0u, Read32(Params));
    }

    [Fact]
    public void QueuedAtrac9RetainsItsInputWhenTheGuestReusesTheBuffer()
    {
        SetupAtrac9(); SilentAtrac9();
        Block(Blocks, 0, 192, 256); QueueAtrac9();
        Write(Samples, Enumerable.Repeat((byte)0xff, 192).ToArray());
        Play();
        for (var i = 0; i < 64; i++) Assert.All(Render(), sample => Assert.Equal(0f, sample));
        _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = 56;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetState(_ctx));
        Assert.Equal(0u, Read32(Params));
        Assert.Equal(0u, Read32(Params + 4));
    }

    [Fact]
    public void LongAtrac9LoopCanInterpolateBackToItsFirstSample()
    {
        _ctx[CpuRegister.Rdi] = _system; _ctx[CpuRegister.Rsi] = 96000;
        Assert.Equal(0, Ngs2Exports.Ngs2SystemSetSampleRate(_ctx));
        SetupAtrac9();
        var data = new byte[576];
        for (var i = 0; i < 3; i++) new byte[] { 0, 0x84, 0, 0x40 }.CopyTo(data, i * 192);
        Write(Samples, data); Block(Blocks, 0, 576, 768, repeats: 1);
        QueueAtrac9(); Play();
        for (var i = 0; i < 385; i++) Assert.All(Render(), sample => Assert.Equal(0f, sample));
        _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = 56;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetState(_ctx));
        Assert.Equal(3u, Read32(Params));
        Assert.Equal(768UL, Read64(Params + 32));
    }

    [Theory]
    [InlineData(191ul, 1u)]
    [InlineData(8ul * 1024 * 1024 + 1, 1u)]
    public void InvalidAtrac9BlockIsRejectedWithoutReadingItsPayload(ulong bytes, uint samples)
    {
        SetupAtrac9();
        Block(Blocks, 0, bytes, samples);
        Write(Params, Parameter(0x10000001, 32, data =>
        {
            U64(data, 8, Samples); U32(data, 20, 1); U64(data, 24, Blocks);
        }));
        Assert.Equal(unchecked((int)0x804A8431), InvokeControl(_voice, Params));
    }

    [Theory]
    [InlineData(257u)]
    [InlineData(0xffffff7fu)]
    public void OpenEndedAtrac9DurationStopsAtTheEndOfTheAvailableData(uint samples)
    {
        SetupAtrac9(); SilentAtrac9();
        Block(Blocks, 0, 192, samples, skip: 128);
        QueueAtrac9(flags: 17); Play();
        for (var i = 0; i < 32; i++) Render();
        _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = 56;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetState(_ctx));
        Assert.Equal(128UL, Read64(Params + 24));
        Assert.Equal(192UL, Read64(Params + 32));
        Assert.Equal(0u, Read32(Params));
    }

    [Fact]
    public void InvalidAtrac9ConfigurationDoesNotReplaceAWorkingPcmVoice()
    {
        Setup(); Pcm(8192, 8192, 8192, 8192); Add(); Play();
        var format = Format(0x40, 1, 48000);
        Write(Params, Parameter(0x10000000, 40, data => format.CopyTo(data, 8)));
        Assert.Equal(unchecked((int)0x804A8431), InvokeControl(_voice, Params));
        Assert.All(Render(), sample => Assert.Equal(.25f, sample));
    }

    [Fact]
    public void CorruptAtrac9ReportsAVoiceErrorWithoutFailingTheSystemRender()
    {
        SetupAtrac9(); Write(Samples, Enumerable.Repeat((byte)0xff, 192).ToArray());
        Block(Blocks, 0, 192, 256); QueueAtrac9(); Play();
        Assert.All(Render(), sample => Assert.Equal(0f, sample));
        _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = 56;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetState(_ctx));
        Assert.Equal(0x19u, Read32(Params));
        Assert.Equal(0x804a8431u, Read32(Params + 4));
        Control(_voice, Parameter(6, 12, data => U32(data, 8, 8)));
        _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = 56;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetState(_ctx));
        Assert.Equal(0u, Read32(Params + 4));
    }

    [Fact]
    public void EightChannelSubmixerPreservesARearChannelRoute()
    {
        var bus = CreateVoice(0x2000);
        Control(bus, Parameter(0x20000000, 16, data => U32(data, 8, 8)));
        Control(bus, Parameter(6, 12, data => U32(data, 8, 1)));
        Control(bus, Parameter(5, 24, data => U64(data, 16, _master)));
        Control(_master, Parameter(0x30000000, 16, data => U32(data, 8, 8)));
        Control(_voice, Parameter(5, 24, data => U64(data, 16, bus)));
        var levels = new byte[32]; BinaryPrimitives.WriteSingleLittleEndian(levels.AsSpan(28), 1);
        Write(Params + 512, levels);
        Control(_voice, Parameter(1, 24, data =>
        {
            U32(data, 12, 8); U64(data, 16, Params + 512);
        }));
        Control(_voice, Parameter(3, 16));
        Setup(); Pcm(8192, 8192, 8192, 8192); Add(); Play();
        var descriptor = new byte[24]; U64(descriptor, 0, Output); U64(descriptor, 8, 128);
        U32(descriptor, 16, 0x18); U32(descriptor, 20, 8); Write(Params, descriptor);
        _ctx[CpuRegister.Rdi] = _system; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = 1;
        Assert.Equal(0, Ngs2Exports.Ngs2SystemRender(_ctx));
        var pcm = new byte[128]; Assert.True(_memory.TryRead(Output, pcm));
        for (var i = 0; i < 32; i++)
            Assert.Equal(i % 8 == 7 ? .25f : 0f, BinaryPrimitives.ReadSingleLittleEndian(pcm.AsSpan(i * 4)));
    }

    [Fact]
    public void Ps5ResetOptionWritesThe144ByteLayoutAndDefaultLimits()
    {
        Write(Params, Enumerable.Repeat((byte)0xa5, 152).ToArray());
        _ctx[CpuRegister.Rdi] = Params;
        Assert.Equal(0, Ngs2Exports.Ngs2SystemResetOption(_ctx));
        Assert.Equal(144UL, Read64(Params));
        Assert.Equal(0UL, Read64(Params + 72));
        Assert.Equal(512u, Read32(Params + 108));
        Assert.Equal(256u, Read32(Params + 112));
        Assert.Equal(48000u, Read32(Params + 116));
        Assert.Equal(0u, Read32(Params + 120));
        Assert.Equal(0xa5a5a5a5a5a5a5a5UL, Read64(Params + 144));
        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(unchecked((int)0x804A8012), Ngs2Exports.Ngs2SystemResetOption(_ctx));
    }

    [Fact]
    public void SystemCreateReadsTheGrainAfterThePs5SchedulerOptions()
    {
        var options = new byte[144]; U64(options, 0, 144);
        U32(options, 32, 4096); U32(options, 112, 8); U32(options, 116, 24000); Write(Params, options);
        _ctx[CpuRegister.Rdi] = Params; _ctx[CpuRegister.Rdx] = Base;
        Assert.Equal(0, Ngs2Exports.Ngs2SystemCreate(_ctx));
        var system = Read64(Base);
        try
        {
            var descriptor = new byte[24]; U64(descriptor, 0, Output); U64(descriptor, 8, 64);
            U32(descriptor, 16, 0x18); U32(descriptor, 20, 2); Write(Params, descriptor);
            Write(Output, Enumerable.Repeat((byte)0xa5, 72).ToArray());
            _ctx[CpuRegister.Rdi] = system; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = 1;
            Assert.Equal(0, Ngs2Exports.Ngs2SystemRender(_ctx));
            var data = new byte[72]; Assert.True(_memory.TryRead(Output, data));
            Assert.All(data[..64], value => Assert.Equal((byte)0, value));
            Assert.All(data[64..], value => Assert.Equal((byte)0xa5, value));
        }
        finally { _ctx[CpuRegister.Rdi] = system; Assert.Equal(0, Ngs2Exports.Ngs2SystemDestroy(_ctx)); }
    }

    private void SetupAtrac9(uint config = 0xe01770fe, uint rate = 48000)
    {
        var format = Format(0x40, 1, rate); U32(format, 12, config);
        Control(_voice, Parameter(0x10000000, 40, data => format.CopyTo(data, 8)));
    }

    private void SilentAtrac9(int frames = 1)
    {
        var data = new byte[192];
        // These silent mono blocks consume 12 bytes; only frame zero carries the first-in-superframe bit.
        for (var i = 0; i < frames; i++) new byte[] { i == 0 ? (byte)0 : (byte)0x80, 0x84, 0, 0x40 }.CopyTo(data, i * 12);
        Write(Samples, data);
    }

    private void QueueAtrac9(uint count = 1, uint flags = 0) =>
        Control(_voice, Parameter(0x10000001, 32, data =>
        {
            U64(data, 8, Samples); U32(data, 16, flags); U32(data, 20, count); U64(data, 24, Blocks);
        }));
}
