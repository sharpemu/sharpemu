using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcRasterStateTests
{
    [Fact]
    public void DecodeRasterStateUsesVisibleBackFacePolygonOffset()
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x205] = 1u | (1u << 11) | (1u << 12),
            [0x2E0] = FloatBits(32f),
            [0x2E1] = FloatBits(3f),
            [0x2E2] = FloatBits(48f),
            [0x2E3] = FloatBits(5f),
            [0x2DF] = FloatBits(2f),
            [0x2DE] = 0x100u | 0xE9u,
        };

        var state = AgcExports.DecodeRasterState(registers);

        Assert.True(state.CullFront);
        Assert.True(state.DepthBiasEnable);
        Assert.Equal(5f, state.DepthBiasConstantFactor);
        Assert.Equal(2f, state.DepthBiasClamp);
        Assert.Equal(3f, state.DepthBiasSlopeFactor);
        Assert.Equal(-23, state.DepthBiasNegNumDbBits);
        Assert.True(state.DepthBiasIsFloatFormat);
    }

    [Fact]
    public void DecodeRasterStateDisablesOffsetForCulledEnabledFace()
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x205] = 1u | (1u << 11),
            [0x2E0] = FloatBits(32f),
            [0x2E1] = FloatBits(3f),
        };

        var state = AgcExports.DecodeRasterState(registers);

        Assert.False(state.DepthBiasEnable);
    }

    [Fact]
    public void FixedPointPolygonOffsetScalesForUnormDepth()
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x205] = 1u << 11,
            [0x2DE] = 0xE8u,
            [0x2E0] = FloatBits(16f),
            [0x2E1] = FloatBits(2f),
        };

        var state = AgcExports.DecodeRasterState(registers);

        Assert.False(state.DepthBiasIsFloatFormat);
        Assert.Equal(-24, state.DepthBiasNegNumDbBits);
        Assert.Equal(2f / 256f, state.ResolveDepthBiasConstantFactor(16));
        Assert.Equal(2f, state.ResolveDepthBiasConstantFactor(0));
    }

    private static uint FloatBits(float value) =>
        unchecked((uint)BitConverter.SingleToInt32Bits(value));
}
