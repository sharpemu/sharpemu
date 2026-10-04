// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

[Collection(AudioOutStateCollection.Name)]
public sealed class AudioOut2PcmSnapshotTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ContextParamAddress = MemoryBase + 0x100;
    private const ulong ContextOutAddress = MemoryBase + 0x200;
    private const ulong PortParamAddress = MemoryBase + 0x300;
    private const ulong FirstPortOutAddress = MemoryBase + 0x400;
    private const ulong SecondPortOutAddress = MemoryBase + 0x408;
    private const ulong AttributeAddress = MemoryBase + 0x500;
    private const ulong PcmAttributeValueAddress = MemoryBase + 0x600;
    private const ulong ContextMemoryAddress = MemoryBase + 0x1000;
    private const ulong SharedPcmAddress = MemoryBase + 0x4000;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x10000);
    private readonly CpuContext _ctx;

    public AudioOut2PcmSnapshotTests()
    {
        AudioOut2Exports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void PortSetAttributes_SnapshotsPcmWhenPortsReuseGuestBuffer()
    {
        var context = CreateContext(grainSamples: 64);
        var firstPort = CreateSigned16StereoPort(context, FirstPortOutAddress);
        var secondPort = CreateSigned16StereoPort(context, SecondPortOutAddress);
        WritePcmAttribute();

        var firstPcm = Enumerable.Range(0, 64 * 2 * sizeof(short))
            .Select(static value => unchecked((byte)(value + 1)))
            .ToArray();
        var secondPcm = Enumerable.Range(0, firstPcm.Length)
            .Select(static value => unchecked((byte)(255 - value)))
            .ToArray();

        Assert.True(_memory.TryWrite(SharedPcmAddress, firstPcm));
        SetPcmAttribute(firstPort);

        Assert.True(_memory.TryWrite(SharedPcmAddress, secondPcm));
        SetPcmAttribute(secondPort);

        Assert.True(_memory.TryWrite(SharedPcmAddress, new byte[firstPcm.Length]));

        Assert.Equal(firstPcm, AudioOut2Exports.GetPortPcmSnapshotForTests(firstPort));
        Assert.Equal(secondPcm, AudioOut2Exports.GetPortPcmSnapshotForTests(secondPort));

        Assert.Equal(firstPcm, AudioOut2Exports.TakePortPcmSnapshotForTests(firstPort));
        Assert.Null(AudioOut2Exports.GetPortPcmSnapshotForTests(firstPort));
        Assert.Null(AudioOut2Exports.TakePortPcmSnapshotForTests(firstPort));

        // Consuming one port must not discard another port's pending grain.
        Assert.Equal(secondPcm, AudioOut2Exports.TakePortPcmSnapshotForTests(secondPort));
        Assert.Null(AudioOut2Exports.TakePortPcmSnapshotForTests(secondPort));
    }

    [Theory]
    [InlineData((ushort)0x0100)]
    [InlineData((ushort)6)]
    public void ContextPush_DoesNotSubmitObjectOrVibrationPorts(ushort portType)
    {
        var stream = new RecordingAudioStream();
        var openCalls = 0;
        AudioOut2Exports.SetStreamFactoryForTests(_ =>
        {
            openCalls++;
            return stream;
        });

        var context = CreateContext(grainSamples: 64);
        var port = CreateSigned16StereoPort(context, FirstPortOutAddress, portType);
        WritePcmAttribute();
        var pcm = Enumerable.Range(0, 64 * 2 * sizeof(short))
            .Select(static value => unchecked((byte)(value + 1)))
            .ToArray();
        Assert.True(_memory.TryWrite(SharedPcmAddress, pcm));
        SetPcmAttribute(port);

        _ctx[CpuRegister.Rdi] = context;
        _ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextPush(_ctx));

        Assert.Equal(0, openCalls);
        Assert.Empty(stream.Submissions);
        Assert.Null(AudioOut2Exports.GetPortPcmSnapshotForTests(port));
    }

    [Fact]
    public void ContextPush_SubmitsDevicePortSnapshotOnlyOnce()
    {
        var stream = new RecordingAudioStream();
        AudioOut2Exports.SetStreamFactoryForTests(_ => stream);

        var context = CreateContext(grainSamples: 64);
        var port = CreateSigned16StereoPort(context, FirstPortOutAddress);
        WritePcmAttribute();
        Assert.True(_memory.TryWrite(
            SharedPcmAddress,
            Enumerable.Range(0, 64 * 2 * sizeof(short))
                .Select(static value => unchecked((byte)(value + 1)))
                .ToArray()));
        SetPcmAttribute(port);

        _ctx[CpuRegister.Rdi] = context;
        _ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextPush(_ctx));
        Assert.Single(stream.Submissions);
        Assert.Equal(64 * 2 * sizeof(short), stream.Submissions[0].Length);

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextPush(_ctx));
        Assert.Single(stream.Submissions);
    }

    [Fact]
    public void ContextAdvance_SubmitsDevicePortSnapshotOnlyOnce()
    {
        var stream = new RecordingAudioStream();
        AudioOut2Exports.SetStreamFactoryForTests(_ => stream);

        var context = CreateContext(grainSamples: 64);
        var port = CreateSigned16StereoPort(context, FirstPortOutAddress);
        WritePcmAttribute();
        Assert.True(_memory.TryWrite(
            SharedPcmAddress,
            Enumerable.Range(0, 64 * 2 * sizeof(short))
                .Select(static value => unchecked((byte)(value + 1)))
                .ToArray()));
        SetPcmAttribute(port);

        _ctx[CpuRegister.Rdi] = context;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextAdvance(_ctx));
        Assert.Single(stream.Submissions);
        Assert.Equal(64 * 2 * sizeof(short), stream.Submissions[0].Length);

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextAdvance(_ctx));
        Assert.Single(stream.Submissions);
    }

    [Fact]
    public void ContextDestroy_RemovesOwnedPortsAndPendingSnapshots()
    {
        var firstContext = CreateContext(grainSamples: 64);
        var secondContext = CreateContext(grainSamples: 64);
        var firstPort = CreateSigned16StereoPort(firstContext, FirstPortOutAddress);
        var secondPort = CreateSigned16StereoPort(secondContext, SecondPortOutAddress);
        WritePcmAttribute();

        byte[] firstPcm = new byte[64 * 2 * sizeof(short)];
        byte[] secondPcm = Enumerable.Repeat((byte)0x5A, firstPcm.Length).ToArray();
        Assert.True(_memory.TryWrite(SharedPcmAddress, firstPcm));
        SetPcmAttribute(firstPort);
        Assert.True(_memory.TryWrite(SharedPcmAddress, secondPcm));
        SetPcmAttribute(secondPort);

        _ctx[CpuRegister.Rdi] = firstContext;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextDestroy(_ctx));

        Assert.False(AudioOut2Exports.HasPortForTests(firstPort));
        Assert.Null(AudioOut2Exports.GetPortPcmSnapshotForTests(firstPort));
        Assert.Null(AudioOut2Exports.TakePortPcmSnapshotForTests(firstPort));
        Assert.True(AudioOut2Exports.HasPortForTests(secondPort));
        Assert.Equal(secondPcm, AudioOut2Exports.GetPortPcmSnapshotForTests(secondPort));
    }

    public void Dispose() => AudioOut2Exports.ResetForTests();

    private ulong CreateContext(uint grainSamples)
    {
        Span<byte> param = stackalloc byte[0x40];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x10..], grainSamples);
        Assert.True(_memory.TryWrite(ContextParamAddress, param));

        _ctx[CpuRegister.Rdi] = ContextParamAddress;
        _ctx[CpuRegister.Rsi] = ContextMemoryAddress;
        _ctx[CpuRegister.Rdx] = 0x2000;
        _ctx[CpuRegister.Rcx] = ContextOutAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextCreate(_ctx));
        return ReadUInt64(ContextOutAddress);
    }

    private ulong CreateSigned16StereoPort(ulong context, ulong outAddress, ushort portType = 0)
    {
        Span<byte> param = stackalloc byte[0x40];
        param.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(param, portType);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x04..], 0x201);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x08..], 48000);
        Assert.True(_memory.TryWrite(PortParamAddress, param));

        _ctx[CpuRegister.Rdi] = context;
        _ctx[CpuRegister.Rsi] = PortParamAddress;
        _ctx[CpuRegister.Rdx] = outAddress;
        _ctx[CpuRegister.Rcx] = 0;
        Assert.Equal(0, AudioOut2Exports.AudioOut2PortCreate(_ctx));
        return ReadUInt64(outAddress);
    }

    private void WritePcmAttribute()
    {
        Span<byte> pcm = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(pcm, SharedPcmAddress);
        Assert.True(_memory.TryWrite(PcmAttributeValueAddress, pcm));

        Span<byte> attribute = stackalloc byte[0x18];
        attribute.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(attribute[0x08..], PcmAttributeValueAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(attribute[0x10..], 8);
        Assert.True(_memory.TryWrite(AttributeAddress, attribute));
    }

    private void SetPcmAttribute(ulong port)
    {
        _ctx[CpuRegister.Rdi] = port;
        _ctx[CpuRegister.Rsi] = AttributeAddress;
        _ctx[CpuRegister.Rdx] = 1;
        Assert.Equal(0, AudioOut2Exports.AudioOut2PortSetAttributes(_ctx));
    }

    private ulong ReadUInt64(ulong address)
    {
        Span<byte> value = stackalloc byte[8];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }

    private sealed class RecordingAudioStream : IHostAudioStream
    {
        public List<byte[]> Submissions { get; } = [];

        public bool Submit(ReadOnlySpan<byte> stereoPcm16)
        {
            Submissions.Add(stereoPcm16.ToArray());
            return true;
        }

        public void Dispose()
        {
        }
    }
}
