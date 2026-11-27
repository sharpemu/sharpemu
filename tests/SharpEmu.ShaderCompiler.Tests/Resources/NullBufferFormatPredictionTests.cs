// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

[CollectionDefinition("NullBufferFormatPrediction", DisableParallelization = true)]
public sealed class NullBufferFormatPredictionCollection;

[Collection("NullBufferFormatPrediction")]
public sealed class NullBufferFormatPredictionTests
{
    private const uint FormattedWord3 = (20u << 12) | 516u;
    private static readonly BufferSpecialization Sibling = new(20, 516);
    private static readonly BufferSpecialization Unformatted = new(0, 0);

    [Theory]
    [InlineData(1u, true, true)]
    [InlineData(3u, true, true)]
    [InlineData(4u, true, false)]
    [InlineData(1u, false, false)]
    public void ANullFormattedBufferTakesTheFormatOfItsSiblings(uint components, bool enabled, bool predicted)
    {
        var specialization = WithPrediction(enabled, () => Materialize(Plan(components), [0, 0, 0, 0]));
        Assert.Equal(Sibling, specialization.Buffers[0]);
        Assert.Equal(predicted ? Sibling : Unformatted, specialization.Buffers[1]);
    }

    [Fact]
    public void ABoundBufferWithoutAFormatKeepsItsSpecializationAfterANullOne()
    {
        var plan = Plan(1);
        var (nullBuffer, boundBuffer) = WithPrediction(true, () =>
            (Materialize(plan, [0, 0, 0, 0]), Materialize(plan, [0x2000, 0, 64, 0])));
        Assert.Equal(Sibling, nullBuffer.Buffers[1]);
        Assert.Equal(Unformatted, boundBuffer.Buffers[1]);
    }

    private static ShaderResourcePlan Plan(uint components)
    {
        var plan = Extract(Program(
            BufferLoad(0, 0, formatted: true),
            BufferLoad(8, 4, dwords: components, formatted: true),
            EndProgram(16)), userDataCount: 8);
        Assert.Equal(2, plan.Info.Buffers.Count);
        return plan;
    }

    private static ResourceSpecialization Materialize(ShaderResourcePlan plan, uint[] second)
    {
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0, 64, FormattedWord3, .. second]),
            ref snapshot, ref specialization));
        return specialization;
    }

    private static T WithPrediction<T>(bool enabled, Func<T> body)
    {
        var previous = ResourceMaterializer.PredictNullBufferFormats;
        ResourceMaterializer.PredictNullBufferFormats = enabled;
        try
        {
            return body();
        }
        finally
        {
            ResourceMaterializer.PredictNullBufferFormats = previous;
        }
    }
}
