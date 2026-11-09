// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Ngs2;

[CollectionDefinition("Ngs2State", DisableParallelization = true)]
public sealed class Ngs2StateCollection;

[Collection("Ngs2State")]
public sealed partial class Ngs2MixerTests : IDisposable
{
    private const ulong Base = 0x100000000;
    private const ulong Params = Base + 0x1000;
    private const ulong Blocks = Base + 0x3000;
    private const ulong Samples = Base + 0x4000;
    private const ulong Output = Base + 0x8000;
    private readonly FakeCpuMemory _memory = new(Base, 0x20000);
    private readonly CpuContext _ctx;
    private readonly ulong _system;
    private readonly ulong _voice;
    private readonly ulong _master;

    public Ngs2MixerTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        var options = new byte[64];
        U64(options, 0, 64);
        U32(options, 32, 4);
        U32(options, 36, 48000);
        Write(Params, options);
        _ctx[CpuRegister.Rdi] = Params;
        _ctx[CpuRegister.Rdx] = Base;
        Assert.Equal(0, Ngs2Exports.Ngs2SystemCreate(_ctx));
        _system = Read64(Base);
        _voice = CreateVoice(0x1000);
        _master = CreateVoice(0x3000);
        Control(_master, Parameter(0x30000000, 16, data => U32(data, 8, 2)));
        Control(_master, Parameter(6, 12, data => U32(data, 8, 1)));
        Control(_voice, Parameter(5, 24, data => U64(data, 16, _master)));
    }

    [Theory]
    [InlineData(0x10000000u)]
    [InlineData(0x40010000u)]
    public void RawPcmRendersThroughSamplerAndCustomSampler(uint setup)
    {
        Setup(setup, channels: 1);
        Pcm(8192, 16384, -16384, 0);
        Add();
        Play();
        Assert.Equal(new[] { .25f, .25f, .5f, .5f, -.5f, -.5f, 0, 0 }, Render());
    }

    [Fact]
    public void VoiceCommandsUseCountedSixteenByteRecordsNotControlHeaders()
    {
        Write(Params + 0x400, Format(0x12, 1, 48000));
        Pcm(8192, 16384, -16384, 0);
        Block(Blocks, 0, 8, samples: 4);
        var commands = new byte[4 * 16];
        Command(commands, 0, 0x10000, 0x42, Params + 0x400);
        Command(commands, 1, 0x10002, 6, Samples);
        Command(commands, 2, 0x10001, 0x81, Blocks, count: 1);
        Command(commands, 3, 2, 4, 1);
        RunCommands(_voice, commands);
        Assert.Equal(new[] { .25f, .25f, .5f, .5f, -.5f, -.5f, 0, 0 }, Render());
    }

    [Fact]
    public void LinkedControlsFollowSignedNextOffsetsIncludingNonContiguousBlocks()
    {
        Pcm(8192, 16384, -16384, 0);
        Block(Blocks, 0, 8, 4);
        var setup = Parameter(0x10000000, 40, data => Format(0x12, 1, 48000).CopyTo(data, 8));
        BinaryPrimitives.WriteInt16LittleEndian(setup.AsSpan(2), -128);
        Write(Params + 128, setup);
        var add = Parameter(0x10000001, 32, data =>
        {
            U64(data, 8, Samples); U32(data, 20, 1); U64(data, 24, Blocks);
        });
        BinaryPrimitives.WriteInt16LittleEndian(add.AsSpan(2), 64);
        Write(Params, add);
        Write(Params + 64, Parameter(6, 12, data => U32(data, 8, 1)));
        Assert.Equal(0, InvokeControl(_voice, Params + 128));
        Assert.Equal(.25f, Render()[0]);
    }

    [Fact]
    public void MalformedControlDoesNotReadAnEventPastItsDeclaredSize()
    {
        Write(Params, Parameter(6, 8));
        Assert.Equal(unchecked((int)0x804A8361), InvokeControl(_voice, Params));
        var cycle = Parameter(6, 12, data => U32(data, 8, 0));
        BinaryPrimitives.WriteInt16LittleEndian(cycle.AsSpan(2), 16);
        Write(Params, cycle);
        BinaryPrimitives.WriteInt16LittleEndian(cycle.AsSpan(2), -16);
        Write(Params + 16, cycle);
        Assert.Equal(unchecked((int)0x804A8361), InvokeControl(_voice, Params));
    }

    [Fact]
    public void StateFlagsWriteOnlyFourBytesAndPauseDoesNotConsumeSamples()
    {
        Setup(); Pcm(8192, 8192, 8192, 8192); Add(); Play();
        Write(Params, new byte[] { 0, 0, 0, 0, 0xa5, 0xa5, 0xa5, 0xa5 });
        _ctx[CpuRegister.Rdi] = _voice;
        _ctx[CpuRegister.Rsi] = Params;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetStateFlags(_ctx));
        Assert.Equal(3u, Read32(Params));
        Assert.Equal(0xa5a5a5a5u, Read32(Params + 4));
        Control(_voice, Parameter(6, 12, data => U32(data, 8, 16)));
        Assert.All(Render(), sample => Assert.Equal(0f, sample));
        Control(_voice, Parameter(6, 12, data => U32(data, 8, 32)));
        Assert.Equal(.25f, Render()[0]);
    }

    [Fact]
    public void GrainAndRateOptionsResamplePcmWithoutRestartingEachRender()
    {
        Setup(rate: 24000);
        Pcm(0, 8192, 16384, 24576); Add(); Play();
        Assert.Equal(new[] { 0f, 0f, .125f, .125f, .25f, .25f, .375f, .375f }, Render());
        Assert.Equal(.5f, Render()[0]);
    }

    [Theory]
    [InlineData(1u, .375f)]
    [InlineData(17u, .25f)]
    public void ContinuousStreamingResamplesAcrossBlocksUnlessNoJointIsRequested(uint flags, float boundary)
    {
        Setup(rate: 24000); Pcm(0, 8192, 16384, 24576);
        Block(Blocks, 0, 4, 2);
        Block(Blocks + 40, 4, 4, 2);
        Control(_voice, Parameter(0x10000001, 32, data =>
        {
            U64(data, 8, Samples); U32(data, 16, flags); U32(data, 20, 2); U64(data, 24, Blocks);
        }));
        Play();
        Assert.Equal(new[] { 0f, 0f, .125f, .125f, .25f, .25f, boundary, boundary }, Render());
        Assert.Equal(.5f, Render()[0]);
    }

    [Fact]
    public void RepeatingWaveformInterpolatesAcrossTheLoopBoundary()
    {
        Setup(rate: 24000); Pcm(0, 16384);
        Block(Blocks, 0, 4, 2, repeats: 1);
        Control(_voice, Parameter(0x10000001, 32, data =>
        {
            U64(data, 8, Samples); U32(data, 20, 1); U64(data, 24, Blocks);
        }));
        Play();
        Assert.Equal(new[] { 0f, 0f, .25f, .25f, .5f, .5f, .25f, .25f }, Render());
        Assert.Equal(0f, Render()[0]);
    }

    [Fact]
    public void StreamingAt44100HzMatchesAnUnsplitWaveformAcrossGrains()
    {
        Setup(rate: 44100);
        var pcm = Enumerable.Range(0, 16).Select(i => (short)(Math.Sin(i * .8) * 16000)).ToArray();
        Pcm(pcm);
        Block(Blocks, 0, 32, 16);
        Control(_voice, Parameter(0x10000001, 32, data =>
        {
            U64(data, 8, Samples); U32(data, 16, 1); U32(data, 20, 1); U64(data, 24, Blocks);
        }));
        Play();
        var expected = Enumerable.Range(0, 4).SelectMany(_ => Render()).ToArray();
        Setup(rate: 44100);
        for (var i = 0; i < 4; i++) Block(Blocks + (ulong)(i * 40), (ulong)(i * 8), 8, 4);
        Control(_voice, Parameter(0x10000001, 32, data =>
        {
            U64(data, 8, Samples); U32(data, 16, 1); U32(data, 20, 4); U64(data, 24, Blocks);
        }));
        Play();
        var actual = Enumerable.Range(0, 4).SelectMany(_ => Render()).ToArray();
        for (var i = 0; i < actual.Length; i++) Assert.Equal(expected[i], actual[i], 5);
    }

    [Fact]
    public void FourBufferStreamingRingPreservesPrefetchedTailsAcrossRenders()
    {
        Setup(rate: 44100);
        var source = new List<short>();
        void Enqueue(int slot, int count)
        {
            var data = new byte[count * 2];
            for (var i = 0; i < count; i++)
            {
                var value = (short)(Math.Sin(source.Count * .4) * 16000);
                source.Add(value);
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), value);
            }
            Write(Samples + (ulong)(slot * 128), data);
            Block(Blocks, (ulong)(slot * 128), (ulong)data.Length, (uint)count);
            Control(_voice, Parameter(0x10000001, 32, parameter =>
            {
                U64(parameter, 8, Samples); U32(parameter, 16, 1);
                U32(parameter, 20, 1); U64(parameter, 24, Blocks);
            }));
        }
        Enqueue(0, 16); Play();
        ulong previous = 0;
        for (var grain = 0; grain < 20; grain++)
        {
            var actual = Render();
            for (var frame = 0; frame < 4; frame++)
            {
                var position = (grain * 4 + frame) * 44100.0 / 48000;
                var index = (int)position;
                var expected = (source[index] + (source[index + 1] - source[index]) * (position - index)) / 32768.0;
                Assert.Equal((float)expected, actual[frame * 2], 5);
                Assert.Equal(actual[frame * 2], actual[frame * 2 + 1]);
            }
            _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = 56;
            Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetState(_ctx));
            var consumed = Read64(Params + 32);
            Enqueue((grain + 1) % 4, (int)((consumed - previous) / 2));
            previous = consumed;
        }
    }

    [Fact]
    public void QueuedBlocksHonorOffsetsSkipsAndFiniteRepeats()
    {
        Setup(); Pcm(0, 8192, 16384, 24576);
        Block(Blocks, 2, 4, 1, repeats: 1, skip: 1);
        Block(Blocks + 40, 6, 2, 1);
        Add(count: 2); Play();
        Assert.Equal(new[] { .5f, .5f, .5f, .5f, .75f, .75f, 0f, 0f }, Render());
        _ctx[CpuRegister.Rdi] = _voice;
        _ctx[CpuRegister.Rsi] = Params;
        _ctx[CpuRegister.Rdx] = 56;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetState(_ctx));
        Assert.Equal(3UL, Read64(Params + 24));
        Assert.Equal(0u, Read32(Params));
    }

    [Fact]
    public void SamplerStateUsesAlignedCountersAfterTheBaseStateErrorCode()
    {
        Setup(); Pcm(8192, 8192, 8192, 8192, 16384, 16384, 16384, 16384);
        Block(Blocks, 0, 16, 8);
        _ctx.TryWriteUInt64(Blocks + 32, 0x12345678);
        Control(_voice, Parameter(0x10000001, 32, data =>
        {
            U64(data, 8, Samples); U32(data, 20, 1); U64(data, 24, Blocks);
        }));
        Play(); Render();
        Write(Params, Enumerable.Repeat((byte)0xa5, 64).ToArray());
        _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = 56;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetState(_ctx));
        Assert.Equal(3u, Read32(Params));
        Assert.Equal(0u, Read32(Params + 4));
        Assert.Equal(BitConverter.SingleToUInt32Bits(1f), Read32(Params + 8));
        Assert.Equal(0UL, Read64(Params + 16));
        Assert.Equal(4UL, Read64(Params + 24));
        Assert.Equal(8UL, Read64(Params + 32));
        Assert.Equal(0x12345678UL, Read64(Params + 40));
        Assert.Equal(Samples, Read64(Params + 48));
        Assert.Equal(0xa5a5a5a5a5a5a5a5UL, Read64(Params + 56));
    }

    [Fact]
    public void PortMatrixIsAnIndexNotAnAudioGain()
    {
        Setup(); Pcm(8192, 8192, 8192, 8192); Add(); Play();
        var levels = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(levels.AsSpan(4), 1);
        Write(Params + 0x400, levels);
        Control(_voice, Parameter(1, 24, data =>
        {
            U32(data, 8, 0); U32(data, 12, 2); U64(data, 16, Params + 0x400);
        }));
        Control(_voice, Parameter(3, 16, data => U32(data, 12, 0)));
        Assert.Equal(new[] { 0f, .25f, 0f, .25f, 0f, .25f, 0f, .25f }, Render());
    }

    [Fact]
    public void StereoIntoAMonoBusWeightsBothChannelsEqually()
    {
        Control(_master, Parameter(0x30000000, 16, data => U32(data, 8, 1)));
        Setup(channels: 2);
        Pcm(8192, 24576, 8192, 24576, 8192, 24576, 8192, 24576);
        Block(Blocks, 0, 16, 4);
        Control(_voice, Parameter(0x10000001, 32, data =>
        {
            U64(data, 8, Samples); U32(data, 20, 1); U64(data, 24, Blocks);
        }));
        Play();
        Assert.All(Render(), sample => Assert.Equal(.5f, sample));
    }

    [Theory]
    [InlineData(0x12u)]
    [InlineData(0x13u)]
    [InlineData(0x18u)]
    [InlineData(0x19u)]
    public void OutputEncodingAndBufferTailArePreserved(uint type)
    {
        Setup(); Pcm(8192, 8192, 8192, 8192); Add(); Play();
        var sentinel = Enumerable.Repeat((byte)0xa5, 96).ToArray();
        Write(Output, sentinel);
        Render(type);
        var bytes = new byte[96]; Assert.True(_memory.TryRead(Output, bytes));
        var size = type is 0x12 or 0x13 ? 2 : 4;
        if (type == 0x12) Assert.Equal(8192, BinaryPrimitives.ReadInt16LittleEndian(bytes));
        if (type == 0x13) Assert.Equal(8192, BinaryPrimitives.ReadInt16BigEndian(bytes));
        if (type == 0x18) Assert.Equal(.25f, BinaryPrimitives.ReadSingleLittleEndian(bytes));
        if (type == 0x19) Assert.Equal(.25f, BinaryPrimitives.ReadSingleBigEndian(bytes));
        Assert.All(bytes[(8 * size)..], value => Assert.Equal((byte)0xa5, value));
    }

    [Fact]
    public void TwoOutputsConsumeTheVoiceOnlyOnce()
    {
        Setup(); Pcm(8192, 8192, 8192, 8192, 16384, 16384, 16384, 16384);
        Add(samples: 8); Play();
        var otherMaster = CreateVoice(0x3000);
        Control(otherMaster, Parameter(0x30000005, 16, data => U32(data, 8, 1)));
        Control(otherMaster, Parameter(6, 12, data => U32(data, 8, 1)));
        Control(_voice, Parameter(5, 24, data => { U32(data, 8, 1); U64(data, 16, otherMaster); }));
        Render(count: 2);
        var bytes = new byte[32]; Assert.True(_memory.TryRead(Output + 0x100, bytes));
        Assert.Equal(.25f, BinaryPrimitives.ReadSingleLittleEndian(bytes));
        Assert.Equal(.5f, Render()[0]);
    }

    [Fact]
    public void BlockEndCallbackCanQueueTheNextStreamingBuffer()
    {
        var previous = GuestThreadExecution.Scheduler;
        var calls = 0;
        try
        {
            GuestThreadExecution.Scheduler = new CallbackScheduler((context, address) =>
            {
                calls++;
                Assert.Equal(0x1234UL, Read64(address));
                Assert.Equal(_voice, Read64(address + 8));
                Assert.Equal(1u, Read32(address + 16));
                Assert.Equal(Samples, Read64(address + 32));
                if (calls == 1) { Pcm(16384, 16384, 16384, 16384); Add(); }
            });
            Setup(0x40010000); Pcm(8192, 8192, 8192, 8192); Add(); Play();
            Control(_voice, Parameter(7, 32, data =>
            {
                U64(data, 8, 0x12345678); U64(data, 16, 0x1234); U32(data, 24, 1);
            }));
            Assert.Equal(.25f, Render()[0]);
            Assert.Equal(.5f, Render()[0]);
            Assert.Equal(2, calls);
        }
        finally { GuestThreadExecution.Scheduler = previous; }
    }

    [Fact]
    public void RawVagBlocksKeepTheExistingAdpcmDecoderAvailable()
    {
        Control(_voice, Parameter(0x10000000, 40, data => Format(0x1c, 1, 48000).CopyTo(data, 8)));
        var adpcm = Enumerable.Repeat((byte)0x11, 16).ToArray();
        adpcm[0] = adpcm[1] = 0;
        Write(Samples, adpcm); Block(Blocks, 0, 16, 28);
        Control(_voice, Parameter(0x10000001, 32, data =>
        {
            U64(data, 8, Samples); U32(data, 20, 1); U64(data, 24, Blocks);
        }));
        Play();
        Assert.All(Render(), sample => Assert.Equal(.125f, sample));
    }

    [Theory]
    [InlineData(0x10u, "C0")]
    [InlineData(0x11u, "40")]
    [InlineData(0x12u, "00C0")]
    [InlineData(0x13u, "C000")]
    [InlineData(0x14u, "0000C0")]
    [InlineData(0x15u, "C00000")]
    [InlineData(0x16u, "000000C0")]
    [InlineData(0x17u, "C0000000")]
    [InlineData(0x18u, "000000BF")]
    [InlineData(0x19u, "BF000000")]
    [InlineData(0x1au, "000000000000E0BF")]
    [InlineData(0x1bu, "BFE0000000000000")]
    public void InputPcmEncodingsPreserveSignAndEndianOrder(uint type, string sample)
    {
        Control(_voice, Parameter(0x10000000, 40, data => Format(type, 1, 48000).CopyTo(data, 8)));
        var value = Convert.FromHexString(sample);
        Write(Samples, Enumerable.Range(0, 4).SelectMany(_ => value).ToArray());
        Block(Blocks, 0, (ulong)(value.Length * 4), 4);
        Control(_voice, Parameter(0x10000001, 32, data =>
        {
            U64(data, 8, Samples); U32(data, 20, 1); U64(data, 24, Blocks);
        }));
        Play();
        Assert.All(Render(), output => Assert.Equal(-.5f, output));
        _ctx[CpuRegister.Rdi] = _voice; _ctx[CpuRegister.Rsi] = Params;
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetStateFlags(_ctx));
        Assert.Equal(0u, Read32(Params));
    }

    private sealed class CallbackScheduler(Action<CpuContext, ulong> callback) : IGuestThreadScheduler
    {
        public bool SupportsGuestContextTransfer => false;
        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context) { }
        public bool TryStartThread(CpuContext context, GuestThreadStartRequest request, out string? error) { error = null; return false; }
        public bool TryJoinThread(CpuContext context, ulong handle, out ulong value, out string? error) { value = 0; error = null; return false; }
        public void Pump(CpuContext context, string reason) { }
        public int WakeBlockedThreads(string key, int maxCount = int.MaxValue) => 0;
        public bool HasPendingGuestExceptionForCurrentThread() => false;
        public bool TrySetGuestThreadPriority(ulong handle, int priority) => false;
        public bool TrySetGuestThreadAffinity(ulong handle, ulong affinity) => false;
        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => [];
        public bool TryCallGuestFunction(CpuContext context, ulong entryPoint, ulong arg0, ulong arg1,
            ulong stackAddress, ulong stackSize, string reason, out string? error)
        {
            callback(context, arg0); error = null; return true;
        }
        public bool TryCallGuestFunction(CpuContext context, ulong entryPoint, ulong arg0, ulong arg1, ulong arg2,
            ulong stackAddress, ulong stackSize, string reason, out ulong value, out string? error)
        {
            callback(context, arg0); value = 0; error = null; return true;
        }
        public bool TryCallGuestFunction(CpuContext context, ulong entryPoint, ulong arg0, ulong arg1, ulong arg2, ulong arg3,
            ulong stackAddress, ulong stackSize, string reason, out ulong value, out string? error)
        {
            callback(context, arg0); value = 0; error = null; return true;
        }
        public bool TryCallGuestContinuation(CpuContext context, GuestCpuContinuation continuation, string reason, out string? error)
        { error = null; return false; }
        public bool TryRaiseGuestException(CpuContext context, ulong handle, ulong handler, int type, out string? error)
        { error = null; return false; }
    }

    private ulong CreateVoice(uint rack)
    {
        _ctx[CpuRegister.Rdi] = _system; _ctx[CpuRegister.Rsi] = rack; _ctx[CpuRegister.R8] = Base;
        Assert.Equal(0, Ngs2Exports.Ngs2RackCreate(_ctx));
        _ctx[CpuRegister.Rdi] = Read64(Base); _ctx[CpuRegister.Rsi] = 0; _ctx[CpuRegister.Rdx] = Base;
        Assert.Equal(0, Ngs2Exports.Ngs2RackGetVoiceHandle(_ctx));
        return Read64(Base);
    }

    private void Setup(uint id = 0x10000000, int channels = 1, uint rate = 48000) =>
        Control(_voice, Parameter(id, 40, data => Format(0x12, (uint)channels, rate).CopyTo(data, 8)));
    private void Play() => Control(_voice, Parameter(6, 12, data => U32(data, 8, 1)));
    private void Add(uint count = 1, uint samples = 4)
    {
        if (count == 1) Block(Blocks, 0, samples * 2, samples);
        Control(_voice, Parameter(0x10000001, 32, data =>
        {
            U64(data, 8, Samples); U32(data, 20, count); U64(data, 24, Blocks);
        }));
    }
    private void Block(ulong address, ulong offset, ulong size, uint samples, uint repeats = 0, uint skip = 0)
    {
        var data = new byte[40]; U64(data, 0, offset); U64(data, 8, size);
        U32(data, 16, repeats); U32(data, 20, skip); U32(data, 24, samples); Write(address, data);
    }
    private void Pcm(params short[] samples)
    {
        var data = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), samples[i]);
        Write(Samples, data);
    }
    private float[] Render(uint type = 0x18, uint count = 1)
    {
        var descriptor = new byte[24 * count];
        for (var i = 0; i < count; i++)
        {
            U64(descriptor, i * 24, Output + (ulong)(i * 0x100)); U64(descriptor, i * 24 + 8, 96);
            U32(descriptor, i * 24 + 16, type); U32(descriptor, i * 24 + 20, 2);
        }
        Write(Params, descriptor);
        _ctx[CpuRegister.Rdi] = _system; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = count;
        Assert.Equal(0, Ngs2Exports.Ngs2SystemRender(_ctx));
        var bytes = new byte[32]; Assert.True(_memory.TryRead(Output, bytes));
        return Enumerable.Range(0, 8).Select(i => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4))).ToArray();
    }
    private void Control(ulong voice, byte[] data) { Write(Params, data); Assert.Equal(0, InvokeControl(voice, Params)); }
    private int InvokeControl(ulong voice, ulong address)
    {
        _ctx[CpuRegister.Rdi] = voice; _ctx[CpuRegister.Rsi] = address;
        return Ngs2Exports.Ngs2VoiceControl(_ctx);
    }
    private void RunCommands(ulong voice, byte[] data)
    {
        Write(Params, data); _ctx[CpuRegister.Rdi] = voice; _ctx[CpuRegister.Rsi] = Params; _ctx[CpuRegister.Rdx] = (ulong)(data.Length / 16);
        Assert.Equal(0, Ngs2Exports.Ngs2VoiceRunCommands(_ctx));
    }
    private static byte[] Parameter(uint id, int size, Action<byte[]>? fill = null)
    {
        var data = new byte[size]; BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)size);
        U32(data, 4, id); fill?.Invoke(data); return data;
    }
    private static byte[] Format(uint type, uint channels, uint rate)
    {
        var data = new byte[24]; U32(data, 0, type); U32(data, 4, channels); U32(data, 8, rate); return data;
    }
    private static void Command(byte[] data, int index, uint id, byte type, ulong value, ushort count = 0)
    {
        var item = data.AsSpan(index * 16, 16); BinaryPrimitives.WriteUInt32LittleEndian(item, id);
        item[5] = type; BinaryPrimitives.WriteUInt16LittleEndian(item[6..], count); BinaryPrimitives.WriteUInt64LittleEndian(item[8..], value);
    }
    private static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void U64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
    private void Write(ulong address, byte[] bytes) => Assert.True(_memory.TryWrite(address, bytes));
    private uint Read32(ulong address) { Span<byte> data = stackalloc byte[4]; Assert.True(_memory.TryRead(address, data)); return BinaryPrimitives.ReadUInt32LittleEndian(data); }
    private ulong Read64(ulong address) { Span<byte> data = stackalloc byte[8]; Assert.True(_memory.TryRead(address, data)); return BinaryPrimitives.ReadUInt64LittleEndian(data); }
    public void Dispose() { _ctx[CpuRegister.Rdi] = _system; Assert.Equal(0, Ngs2Exports.Ngs2SystemDestroy(_ctx)); }
}
