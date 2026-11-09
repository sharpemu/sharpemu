// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Ngs2;

public sealed class Ngs2PanningTests
{
    private const ulong Base = 0x100000000;
    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _context;

    public Ngs2PanningTests() => _context = new CpuContext(_memory, Generation.Gen5);

    [Fact]
    public void InitUsesFloatArgumentAndWritesOnlyTheFortyByteWorkArea()
    {
        Assert.True(_memory.TryWrite(Base, Enumerable.Repeat((byte)0xa5, 48).ToArray()));
        Init(7, 360);
        var work = new byte[48]; Assert.True(_memory.TryRead(Base, work));
        Assert.Equal(360, BinaryPrimitives.ReadSingleLittleEndian(work.AsSpan(32)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(work.AsSpan(36)));
        Assert.All(work[40..], value => Assert.Equal((byte)0xa5, value));
    }

    [Theory]
    [InlineData(360f, 0f)]
    [InlineData(6.283185307f, 0f)]
    public void CenterStereoIsSymmetricAndPreservesEnergy(float unit, float angle)
    {
        Init(2, unit);
        var result = Matrix(2, (angle, 1f, 1f, 0f));
        Assert.Equal(result[0], result[1], 5);
        Assert.Equal(1f, result.Sum(value => value * value), 5);
    }

    [Fact]
    public void SevenPointOneRowsRespectSpeakerPositionsAndTheLfeSlot()
    {
        Init(7, 360);
        var result = Matrix(8, (-30f, 1f, .5f, .25f), (30f, 1f, 1f, .125f));
        Assert.Equal(.5f, result[0], 5);
        Assert.Equal(.25f, result[3]);
        Assert.Equal(1f, result[9], 5);
        Assert.Equal(.125f, result[11]);
        Assert.InRange(Math.Abs(result[1]), 0, 0.00001f);
        Assert.InRange(Math.Abs(result[8]), 0, 0.00001f);
    }

    [Fact]
    public void NanInputDoesNotWriteAnyPartialMatrix()
    {
        Init(7, 360);
        Assert.True(_memory.TryWrite(Base + 0x200, Enumerable.Repeat((byte)0xa5, 64).ToArray()));
        WriteParameters((0f, 1f, 1f, 0f), (float.NaN, 1f, 1f, 0f));
        _context[CpuRegister.Rdi] = Base; _context[CpuRegister.Rsi] = Base + 0x100;
        _context[CpuRegister.Rdx] = 2; _context[CpuRegister.Rcx] = 8; _context[CpuRegister.R8] = Base + 0x200;
        Assert.Equal(unchecked((int)0x804A8454), Ngs2Exports.Ngs2PanGetVolumeMatrix(_context));
        var data = new byte[64]; Assert.True(_memory.TryRead(Base + 0x200, data));
        Assert.All(data, value => Assert.Equal((byte)0xa5, value));
    }

    private void Init(uint speakers, float unit)
    {
        _context[CpuRegister.Rdi] = Base; _context[CpuRegister.Rsi] = 0; _context[CpuRegister.Rdx] = speakers;
        _context.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(unit), 0);
        Assert.Equal(0, Ngs2Exports.Ngs2PanInit(_context));
    }
    private float[] Matrix(uint channels, params (float Angle, float Distance, float Level, float Lfe)[] parameters)
    {
        WriteParameters(parameters);
        _context[CpuRegister.Rdi] = Base; _context[CpuRegister.Rsi] = Base + 0x100;
        _context[CpuRegister.Rdx] = (ulong)parameters.Length; _context[CpuRegister.Rcx] = channels; _context[CpuRegister.R8] = Base + 0x200;
        Assert.Equal(0, Ngs2Exports.Ngs2PanGetVolumeMatrix(_context));
        var bytes = new byte[parameters.Length * channels * 4]; Assert.True(_memory.TryRead(Base + 0x200, bytes));
        return Enumerable.Range(0, bytes.Length / 4).Select(i => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4))).ToArray();
    }
    private void WriteParameters(params (float Angle, float Distance, float Level, float Lfe)[] parameters)
    {
        var bytes = new byte[parameters.Length * 16];
        for (var i = 0; i < parameters.Length; i++)
        {
            var values = new[] { parameters[i].Angle, parameters[i].Distance, parameters[i].Level, parameters[i].Lfe };
            for (var value = 0; value < values.Length; value++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 16 + value * 4), values[value]);
        }
        Assert.True(_memory.TryWrite(Base + 0x100, bytes));
    }
}
