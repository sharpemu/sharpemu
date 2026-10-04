// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class StageStaticKeyTests
{
    [Fact]
    public void VertexWaveSizeParticipatesInKey()
    {
        var wave32 = BuildVertexKey(new VertexInputInfo { WaveSize = 32 });
        var wave64 = BuildVertexKey(new VertexInputInfo { WaveSize = 64 });

        Assert.False(wave32.AsSpan().SequenceEqual(wave64));
    }

    [Fact]
    public void PixelWaveSizeParticipatesInKey()
    {
        var wave32 = BuildPixelKey(new PixelInputInfo { WaveSize = 32 }, []);
        var wave64 = BuildPixelKey(new PixelInputInfo { WaveSize = 64 }, []);

        Assert.False(wave32.AsSpan().SequenceEqual(wave64));
    }

    [Fact]
    public void PixelBindingKindParticipatesInKey()
    {
        var info = PixelInfo(outputMode: 5);
        var floatKey = BuildPixelKey(
            info,
            [new(0, 0, Gen5PixelOutputKind.Float, Gen5ColorComponentMapping.Identity, 5)]);
        var uintKey = BuildPixelKey(
            info,
            [new(0, 0, Gen5PixelOutputKind.Uint, Gen5ColorComponentMapping.Identity, 5)]);

        Assert.False(floatKey.AsSpan().SequenceEqual(uintKey));
    }

    [Fact]
    public void ActivePixelBindingLayoutParticipatesInKey()
    {
        var info = PixelInfo(outputMode: 7);
        var firstSlot = BuildPixelKey(
            info,
            [new(0, 0, Gen5PixelOutputKind.Uint, Gen5ColorComponentMapping.Identity, 7)]);
        var secondSlot = BuildPixelKey(
            info,
            [new(1, 0, Gen5PixelOutputKind.Uint, Gen5ColorComponentMapping.Identity, 7)]);
        var noBindings = BuildPixelKey(info, []);

        Assert.False(firstSlot.AsSpan().SequenceEqual(secondSlot));
        Assert.False(firstSlot.AsSpan().SequenceEqual(noBindings));
        Assert.False(secondSlot.AsSpan().SequenceEqual(noBindings));
    }

    [Fact]
    public void PixelAncillaryInputParticipatesInKey()
    {
        var withoutAncillary = BuildPixelKey(new PixelInputInfo(), []);
        var withAncillary = BuildPixelKey(
            new PixelInputInfo { Ancillary = true },
            []);

        Assert.False(withoutAncillary.AsSpan().SequenceEqual(withAncillary));
    }

    private static PixelInputInfo PixelInfo(byte outputMode)
    {
        var modes = new byte[PixelInputInfo.TargetCount];
        modes[0] = outputMode;
        modes[1] = outputMode;
        return new PixelInputInfo { TargetOutputModes = modes };
    }

    private static uint[] BuildVertexKey(VertexInputInfo info)
    {
        var key = new List<uint>();
        StageStaticKey.Build(info, requiredOutputCount: 0, key);
        return key.ToArray();
    }

    private static uint[] BuildPixelKey(
        PixelInputInfo info,
        IReadOnlyList<Gen5PixelOutputBinding> outputs)
    {
        var key = new List<uint>();
        StageStaticKey.Build(info, outputs, key);
        return key.ToArray();
    }
}
