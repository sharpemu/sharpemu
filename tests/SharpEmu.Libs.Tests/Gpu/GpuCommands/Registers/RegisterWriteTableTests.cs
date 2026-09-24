// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using Xunit;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ContextRegisterOffset;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ShaderRegisterOffset;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.UserConfigRegisterOffset;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands.Registers;

// The two-tier write contract: a direct writer per packet, else one indirect writer per value, else fatal.
public sealed class RegisterWriteTableTests
{
    private const ulong PacketAddress = 0x1_0000_1000;

    private static RegisterBanks NewBanks() => new(static message => new InvalidOperationException(message));

    private static PacketContext Packet(uint opcode, int valueCount) =>
        new(PacketHeader.Make((uint)valueCount + 2, opcode), PacketAddress, 0, 100, 100);

    private static uint WriteContext(RegisterBanks banks, uint offset, params uint[] values) =>
        RegisterWriteTable.WriteContextPacket(banks, Packet(PacketOpcode.SetContextRegister, values.Length), offset, values);

    private static uint WriteShader(RegisterBanks banks, uint offset, params uint[] values) =>
        RegisterWriteTable.WriteShaderPacket(banks, Packet(PacketOpcode.SetShaderRegister, values.Length), offset, values);

    private static uint WriteUserConfig(RegisterBanks banks, uint offset, params uint[] values) =>
        RegisterWriteTable.WriteUserConfigPacket(banks, Packet(PacketOpcode.SetUserConfigRegister, values.Length), offset, values);

    private static uint Float(float value) => BitConverter.SingleToUInt32Bits(value);

    [Fact]
    public void DirectWriters_DecodeDepthControlAndModeControl()
    {
        var banks = NewBanks();

        Assert.Equal(1u, WriteContext(banks, DbDepthControl, 0x0020_05B7));
        Assert.Equal(1u, WriteContext(banks, PaSuScModeCntl, 0x0008_0017));

        var depth = banks.Context.DepthTarget;
        Assert.True(depth.StencilTestEnabled);
        Assert.True(depth.DepthTestEnabled);
        Assert.True(depth.DepthWriteEnabled);
        Assert.False(depth.DepthBoundsEnabled);
        Assert.Equal(3u, depth.DepthCompare);
        Assert.True(depth.BackFaceEnabled);
        Assert.Equal(5u, depth.StencilCompare);
        Assert.Equal(2u, depth.StencilCompareBack);
        var mode = banks.Context.RasterMode;
        Assert.True(mode.CullFront);
        Assert.True(mode.CullBack);
        Assert.True(mode.FrontFaceClockwise);
        Assert.Equal(2, mode.PolygonMode);
        Assert.True(mode.ProvokingVertexLast);
    }

    [Fact]
    public void IndirectRun_WritesEveryRegisterAndAssemblesSplitAddresses()
    {
        var banks = NewBanks();

        Assert.Equal(4u, WriteContext(banks, DbZReadBase, 0x0012_3456, 0x0000_0001, 0x0000_0002, 0x0000_0003));
        Assert.Equal(1u, WriteContext(banks, DbZReadBaseHi, 0xAB));

        var depth = banks.Context.DepthTarget;
        Assert.Equal(0xAB00_1234_5600ul, depth.ZReadBase);
        Assert.Equal(0x100ul, depth.StencilReadBase);
        Assert.Equal(0x200ul, depth.ZWriteBase);
        Assert.Equal(0x300ul, depth.StencilWriteBase);
    }

    // The run is checked before any value is written, so a bad tail leaves the head untouched.
    [Fact]
    public void UnsupportedOffsetInARun_IsFatalBeforeAnyWrite()
    {
        var banks = NewBanks();

        var error = Assert.Throws<InvalidOperationException>(() => WriteContext(banks, SpiVsOutConfig, 0x1234, 0x5678));

        Assert.Contains("not supported", error.Message);
        Assert.Contains("unsupported=0x01B2", error.Message);
        Assert.Equal(0u, banks.Context.ShaderInterface.VertexOutputConfiguration);
        Assert.Contains("0x01B2", Assert.Throws<InvalidOperationException>(() => WriteContext(banks, 0x1B2, 1)).Message);
    }

    [Fact]
    public void PacketWithoutValues_IsFatalForShaderAndAcceptedForContext()
    {
        var banks = NewBanks();

        Assert.Equal(0u, WriteContext(banks, SpiVsOutConfig));
        Assert.Contains("count=0", Assert.Throws<InvalidOperationException>(() => WriteShader(banks, SpiShaderPgmLoPs)).Message);
        Assert.Contains("consumed no values", Assert.Throws<InvalidOperationException>(() => WriteContext(banks, DbCountControl)).Message);
    }

    [Fact]
    public void ColorBlock_UsesTheFifteenDwordSlotStrideAndFlatExtensionSlots()
    {
        var banks = NewBanks();
        const uint slot = 3;

        WriteContext(banks, CbColor0Info + (slot * 15), 0x28);
        WriteContext(banks, CbColor0Base + (slot * 15), 0x00AB_CDEF);
        WriteContext(banks, CbColor0BaseExt + slot, 0x12);
        WriteContext(banks, CbColor0ClearWord1 + (slot * 15), 0x77);
        WriteContext(banks, CbColor0Attrib2 + slot, (63u << 14) | 31u);

        var target = banks.Context.ColorTargets[slot];
        Assert.Equal(0x28u, target.Info);
        Assert.Equal((ChannelLayout)0xA, target.Layout);
        Assert.Equal(0x1200_ABCD_EF00ul, target.BaseAddress);
        Assert.Equal(63u, target.Width);
        Assert.Equal(31u, target.Height);
        Assert.Equal(0x77u, banks.Context.ColorClearWord1[slot]);
        Assert.Equal(0ul, banks.Context.ColorTargets[0].BaseAddress);
    }

    [Fact]
    public void ColorTargetMetadataConflicts_AreFatal()
    {
        var banks = NewBanks();

        Assert.Contains("attrib3", Assert.Throws<InvalidOperationException>(() => WriteContext(banks, CbColor0Attrib3, 1u << 26)).Message);
        Assert.Contains("dccControl", Assert.Throws<InvalidOperationException>(() => WriteContext(banks, CbColor0DccControl, (1u << 9) | (1u << 20))).Message);
        Assert.Equal(1u, WriteContext(banks, CbColor0Attrib3, (1u << 26) | (1u << 30)));
    }

    [Fact]
    public void UserScalars_TakeTheMarkerAndResetIt()
    {
        var banks = NewBanks();
        banks.UserDataMarker = UserScalarKind.Region;

        Assert.Equal(2u, WriteShader(banks, SpiShaderUserDataPs0 + 2, 7, 8));

        var scalars = banks.Shader.Pixel.UserScalars;
        Assert.Equal(7u, scalars.Values[2]);
        Assert.Equal(8u, scalars.Values[3]);
        Assert.Equal(UserScalarKind.Region, scalars.Kinds[2]);
        Assert.Equal(UserScalarKind.Region, scalars.Kinds[3]);
        Assert.Equal(4u, scalars.Count);
        Assert.Equal(UserScalarKind.Unknown, banks.UserDataMarker);

        WriteShader(banks, SpiShaderUserDataPs0, 1);
        Assert.Equal(UserScalarKind.Unknown, scalars.Kinds[0]);
        Assert.Equal(4u, scalars.Count);
    }

    [Fact]
    public void UserScalars_OutsideTheStageWindow_AreFatal()
    {
        var banks = NewBanks();

        Assert.Contains("stage window", Assert.Throws<InvalidOperationException>(() => WriteShader(banks, SpiShaderUserDataPs0 + 31, 1, 2)).Message);
        Assert.Contains("stage window", Assert.Throws<InvalidOperationException>(() => WriteShader(banks, ComputeUserData0 + 15, 1, 2)).Message);
        Assert.Equal(16u, WriteShader(banks, ComputeUserData0, new uint[16]));
    }

    [Fact]
    public void OrderedAppend_ControlSelectsTheCounterTheNextWritesLandIn()
    {
        var banks = NewBanks();

        WriteUserConfig(banks, GdsOaCntl, 5);
        WriteUserConfig(banks, GdsOaCounter, 0x10);
        WriteUserConfig(banks, GdsOaAddress, 0x8000_0020);

        var counter = banks.UserConfig.OrderedAppend.Counters[5];
        Assert.Equal(0x10u, counter.Counter);
        Assert.True(counter.IsCounterEnabled);
        Assert.Equal(0x20u, counter.AddressBytes);
        Assert.Equal(0u, banks.UserConfig.OrderedAppend.Counters[0].Counter);
    }

    [Fact]
    public void Viewports_DecodeScaleScissorAndDepthRange()
    {
        var banks = NewBanks();

        WriteContext(banks, PaClViewportXScale + 12, Float(2f), Float(3f));
        WriteContext(banks, PaScViewportScissor0Tl + 2, 5u | (7u << 16), 100u | (200u << 16));
        WriteContext(banks, PaScViewportScissor0Tl + 4, 0x8000_0001);
        WriteContext(banks, PaScViewportZMin0 + 6, Float(0.25f), Float(0.75f));

        var viewports = banks.Context.ScreenViewport.Viewports;
        Assert.Equal(2f, viewports[2].XScale);
        Assert.Equal(3f, viewports[2].XOffset);
        Assert.Equal(5, viewports[1].ScissorLeft);
        Assert.Equal(7, viewports[1].ScissorTop);
        Assert.Equal(100, viewports[1].ScissorRight);
        Assert.Equal(200, viewports[1].ScissorBottom);
        Assert.True(viewports[1].ScissorWindowOffsetEnable);
        Assert.False(viewports[2].ScissorWindowOffsetEnable);
        Assert.Equal(0.25f, viewports[3].MinDepth);
        Assert.Equal(0.75f, viewports[3].MaxDepth);
        Assert.Contains("outside its range", Assert.Throws<InvalidOperationException>(() => WriteContext(banks, PaScViewportScissor15Br, 1, 2)).Message);
    }

    [Fact]
    public void TableEntries_UseOnlyTheIndirectWriters()
    {
        var banks = NewBanks();

        RegisterWriteTable.WriteContextEntry(banks, CbTargetMask, 0xF, 0x5000);
        RegisterWriteTable.WriteShaderEntry(banks, SpiShaderPgmLoPs, 0x100, 0x5000);
        RegisterWriteTable.WriteUserConfigEntry(banks, VgtIndexType, 1, 0x5000);

        Assert.Equal(0xFu, banks.Context.RenderTargetMask);
        Assert.Equal(0x1_0000ul, banks.Shader.Pixel.Address);
        Assert.Equal(1u, banks.IndexTypeAndSize);
        RegisterWriteTable.WriteContextEntry(banks, 0x024A, 0, 0x5000);
        Assert.True(banks.Context.UnmodeledTableRegisters.TryGetValue(0x024A, out var value));
        Assert.Equal(0u, value);

        var clone = banks.Clone();
        RegisterWriteTable.WriteContextEntry(banks, 0x024A, 1, 0x5000);
        Assert.Equal(0u, clone.Context.UnmodeledTableRegisters[0x024A]);
        Assert.Equal(1u, banks.Context.UnmodeledTableRegisters[0x024A]);

        banks.ApplyContextState(ContextStateOperation.Push);
        RegisterWriteTable.WriteContextEntry(banks, 0x024A, 2, 0x5000);
        banks.ApplyContextState(ContextStateOperation.Pop);
        Assert.Equal(1u, banks.Context.UnmodeledTableRegisters[0x024A]);
    }

    // Registers without a decoded field are stored so a title that writes them does not stop.
    [Fact]
    public void RegistersWithoutAReferenceEntry_AreStored()
    {
        var banks = NewBanks();

        WriteContext(banks, DbReservedRegister2, 0x1234);
        WriteShader(banks, SpiShaderPgmLoVs, 0x100, 0x2, 0xA, 0xB);
        WriteShader(banks, SpiShaderUserDataVs0 + 1, 9);
        WriteShader(banks, SpiShaderUserDataEs0 + 2, 11);

        Assert.Equal(0x1234u, banks.Context.ReservedDepthRegister);
        var vertex = banks.Shader.Vertex;
        Assert.Equal(0x0200_0001_0000ul, vertex.LegacyVertexAddress);
        Assert.Equal(0xAu, vertex.LegacyVertexResource1);
        Assert.Equal(0xBu, vertex.LegacyVertexResource2);
        Assert.Equal(9u, vertex.LegacyVertexUserScalars.Values[1]);
        Assert.Equal(11u, vertex.ExportUserScalars.Values[2]);
    }

    [Fact]
    public void BlendColor_FollowsTheHardwareRegisterOrder()
    {
        var banks = NewBanks();

        WriteContext(banks, CbBlendRed, Float(1f), Float(2f), Float(3f), Float(4f));

        var color = banks.Context.BlendColor;
        Assert.Equal(1f, color.Red);
        Assert.Equal(2f, color.Green);
        Assert.Equal(3f, color.Blue);
        Assert.Equal(4f, color.Alpha);
    }

    [Fact]
    public void RenderControl_CopyToColor_IsFatal()
    {
        var banks = NewBanks();

        Assert.Equal(1u, WriteContext(banks, DbRenderControl, 0x3));
        Assert.True(banks.Context.DepthTarget.DepthClearEnabled);
        Assert.Contains("copy to color", Assert.Throws<InvalidOperationException>(() => WriteContext(banks, DbRenderControl, 1u << 2)).Message);
    }

    [Fact]
    public void DepthSizeAndZInfoWrites_ClearTheCompositeExtent()
    {
        var banks = NewBanks();
        banks.CompositeDepthSizeXy = 5;

        WriteContext(banks, DbDepthSizeXy, 0x003F_007F);

        Assert.True(banks.Context.DepthTarget.DepthSizeValid);
        Assert.Equal(0x7Fu, banks.Context.DepthTarget.XMax);
        Assert.Equal(0x3Fu, banks.Context.DepthTarget.YMax);
        Assert.Null(banks.CompositeDepthSizeXy);
        banks.CompositeDepthSizeXy = 5;
        WriteContext(banks, DbZInfo, 1);
        Assert.Null(banks.CompositeDepthSizeXy);
    }

    [Fact]
    public void ComputeRegisters_Decode()
    {
        var banks = NewBanks();

        WriteShader(banks, ComputePgmRsrc1, 5u | (2u << 10) | (0xC0u << 12) | (1u << 21) | (1u << 29));
        WriteShader(banks, ComputePgmRsrc2, 1u | (9u << 1) | (1u << 7) | (1u << 10) | (2u << 11) | (0x1FFu << 15));
        WriteShader(banks, ComputeNumThreadX, 64, 2, 3);
        WriteShader(banks, ComputePgmLo, 0x1000, 0x1);
        WriteShader(banks, ComputeShaderChecksum, 0xDEAD_BEEF);

        var compute = banks.Shader.Compute;
        Assert.Equal(0xDEAD_BEEFu, compute.ProgramChecksum);
        Assert.Equal(5, compute.VectorRegisterCount);
        Assert.Equal(2, compute.Priority);
        Assert.Equal(0xC0, compute.FloatMode);
        Assert.True(compute.Dx10Clamp);
        Assert.True(compute.ThreadgroupConfiguration);
        Assert.False(compute.RequireForwardProgress);
        Assert.True(compute.ScratchEnable);
        Assert.Equal(9, compute.UserScalarCount);
        Assert.True(compute.ThreadGroupIdXEnable);
        Assert.False(compute.ThreadGroupIdYEnable);
        Assert.True(compute.ThreadGroupSizeEnable);
        Assert.Equal(2, compute.ThreadIdComponentCount);
        Assert.Equal(0x1FF, compute.LocalDataShareSize);
        Assert.Equal(64u, compute.ThreadsX);
        Assert.Equal(2u, compute.ThreadsY);
        Assert.Equal(3u, compute.ThreadsZ);
        Assert.Equal(0x0100_0010_0000ul, compute.Address);
        Assert.Equal(64, compute.WaveSize);
    }

    [Fact]
    public void SampleLocations_AcceptOneSlotOrTheWholeBlock()
    {
        var banks = NewBanks();
        var block = new uint[16];
        for (var index = 0; index < block.Length; index++)
        {
            block[index] = (uint)(index + 1);
        }

        WriteContext(banks, PaScAaSampleLocations0 + 5, 0x77);
        Assert.Equal(0x77u, banks.Context.SampleLocations.Locations[5]);
        Assert.Equal(16u, WriteContext(banks, PaScAaSampleLocations0, block));
        Assert.Equal(block, banks.Context.SampleLocations.Locations);
        Assert.Contains("not supported", Assert.Throws<InvalidOperationException>(() => WriteContext(banks, PaScAaSampleLocations0, 1, 2)).Message);
        WriteContext(banks, PaScCentroidPriority0, 0x1111_2222, 0x3333_4444);
        Assert.Equal(0x3333_4444_1111_2222ul, banks.Context.SampleLocations.CentroidPriority);
    }

    [Fact]
    public void UserClipPlanes_MustStayDisabled()
    {
        var banks = NewBanks();

        Assert.Equal(1u, WriteContext(banks, PaClUserClipPlane0X, 0));
        Assert.Contains("clip planes", Assert.Throws<InvalidOperationException>(() => WriteContext(banks, PaClUserClipPlane0X, Float(1f))).Message);
    }

    [Fact]
    public void ClipRectanglesScissorsAndOffsets_Decode()
    {
        var banks = NewBanks();

        WriteContext(banks, PaScClipRectRule, 0xABCD, 1u | (2u << 16), 3u | (4u << 16));
        WriteContext(banks, PaScGenericScissorTl, 0x8000_0005 | (6u << 16), 7u | (8u << 16));
        WriteContext(banks, PaScScreenScissorTl, 0xFFFF_FFFF, 640u | (480u << 16));
        WriteContext(banks, PaScWindowOffset, 0xFFFE_FFFF);
        WriteContext(banks, PaSuHardwareScreenOffset, 0x0003_0002);
        WriteContext(banks, PaSuLineCntl, 16);

        var viewport = banks.Context.ScreenViewport;
        Assert.Equal(0xABCD, viewport.ClipRectangleRule);
        Assert.Equal(1, viewport.ClipRectangleLeft[0]);
        Assert.Equal(2, viewport.ClipRectangleTop[0]);
        Assert.Equal(3, viewport.ClipRectangleRight[0]);
        Assert.Equal(4, viewport.ClipRectangleBottom[0]);
        Assert.Equal(5, viewport.GenericScissorLeft);
        Assert.Equal(6, viewport.GenericScissorTop);
        Assert.False(viewport.GenericScissorWindowOffsetEnable);
        Assert.Equal(8, viewport.GenericScissorBottom);
        Assert.Equal(-1, viewport.ScreenScissorLeft);
        Assert.Equal(640, viewport.ScreenScissorRight);
        Assert.Equal(480, viewport.ScreenScissorBottom);
        Assert.Equal(-1, viewport.WindowOffsetX);
        Assert.Equal(-2, viewport.WindowOffsetY);
        Assert.Equal(2u, viewport.HardwareOffsetX);
        Assert.Equal(3u, viewport.HardwareOffsetY);
        Assert.Equal(2f, banks.Context.LineWidth);
    }

    [Fact]
    public void PixelInterpolators_WriteFromTheFirstSlotOnly()
    {
        var banks = NewBanks();

        Assert.Equal(3u, WriteContext(banks, SpiPsInputCntl0, 1, 2, 3));
        WriteContext(banks, SpiPsInputCntl0 + 5, 9);

        var settings = banks.Context.ShaderInterface.PixelInterpolatorSettings;
        Assert.Equal(new uint[] { 1, 2, 3 }, settings[..3]);
        Assert.Equal(9u, settings[5]);
        Assert.Contains("invalid count", Assert.Throws<InvalidOperationException>(() => WriteContext(banks, SpiPsInputCntl0, new uint[33])).Message);
    }

    [Fact]
    public void ShaderProgramRegisters_DecodeAddressesAndResources()
    {
        var banks = NewBanks();

        WriteShader(banks, SpiShaderPgmLoGs, 0x2000, 0x3, (1u << 24) | (3u << 29), (1u << 27) | (5u << 1));
        WriteShader(banks, SpiShaderPgmLoHs, 0x4000, 0x4, 1u << 28, 0x1FFu << 18);
        WriteShader(banks, SpiShaderPgmRsrc2Ps, (1u << 27) | (5u << 1) | (0xFFu << 8));
        WriteShader(banks, SpiShaderUserAccumPs0, 0x7F, 0x00);

        var vertex = banks.Shader.Vertex;
        Assert.Equal(0x0300_0020_0000ul, vertex.GeometryAddress);
        Assert.True(vertex.GeometryResource1.ComputeUnitGroupEnable);
        Assert.Equal(3, vertex.GeometryResource1.GeometryVectorComponentCount);
        Assert.Equal(37, vertex.GeometryResource2.UserScalarCount);
        Assert.Equal(0x0400_0040_0000ul, vertex.HullAddress);
        Assert.Equal(1, vertex.HullResource1.LocalVectorComponentCount);
        Assert.Equal(0x1FF, vertex.HullResource2.LocalDataShareSize);
        Assert.Equal(37, banks.Shader.Pixel.Resource2.UserScalarCount);
        Assert.Equal(0xFF, banks.Shader.Pixel.Resource2.ExtraLocalDataShareSize);
        Assert.Contains("accumulator", Assert.Throws<InvalidOperationException>(() => WriteShader(banks, SpiShaderUserAccumPs0, 0x80)).Message);
        Assert.Contains("accumulator", Assert.Throws<InvalidOperationException>(() => WriteShader(banks, SpiShaderUserAccumPs0 + 3, 1, 2)).Message);
    }

    [Fact]
    public void ClipControl_ZClipIsEnabledOnlyWhenNeitherPlaneIsDisabled()
    {
        var banks = NewBanks();

        WriteContext(banks, PaClClipCntl, 0x3u | (1u << 19) | (1u << 21));
        Assert.True(banks.Context.Clip.IsZClipEnabled);
        Assert.Equal(3, banks.Context.Clip.UserClipPlanes);
        Assert.True(banks.Context.Clip.DirectXClipSpace);
        Assert.True(banks.Context.Clip.VertexKillAny);

        WriteContext(banks, PaClClipCntl, 1u << 26);
        Assert.False(banks.Context.Clip.IsZClipEnabled);
        Assert.True(banks.Context.Clip.NearZClipDisable);
        Assert.False(banks.Context.Clip.FarZClipDisable);
        WriteContext(banks, PaClClipCntl, 1u << 27);
        Assert.False(banks.Context.Clip.IsZClipEnabled);
    }

    [Fact]
    public void UserConfigWriters_DecodePrimitiveStateAndRanges()
    {
        var banks = NewBanks();

        WriteUserConfig(banks, VgtPrimitiveType, 0x46);
        WriteUserConfig(banks, VgtIndexType, 0x7);
        WriteUserConfig(banks, GeIndexOffset, 12);
        WriteUserConfig(banks, GeMultiPrimIbResetEn, 1);
        RegisterWriteTable.WriteUserConfigEntry(banks, GeCntl, 3u | (7u << 9), 0);
        RegisterWriteTable.WriteUserConfigEntry(banks, GeUserVgprEn, 0x5, 0);
        Assert.Equal(2u, WriteUserConfig(banks, TextureGradientFactors, 1, 2));

        Assert.Equal(0x6u, banks.UserConfig.PrimitiveType);
        Assert.Equal(3u, banks.IndexTypeAndSize);
        Assert.Equal(12u, banks.UserConfig.IndexOffset);
        Assert.Equal(1u, banks.UserConfig.PrimitiveResetControl);
        Assert.Equal(3, banks.UserConfig.GeometryEngineControl.PrimitiveGroupSize);
        Assert.Equal(7, banks.UserConfig.GeometryEngineControl.VertexGroupSize);
        Assert.True(banks.UserConfig.GeometryEngineUserVectorEnable.VectorRegister1);
        Assert.False(banks.UserConfig.GeometryEngineUserVectorEnable.VectorRegister2);
        Assert.True(banks.UserConfig.GeometryEngineUserVectorEnable.VectorRegister3);
        Assert.Contains("outside its range", Assert.Throws<InvalidOperationException>(() => WriteUserConfig(banks, TextureGradientControl, 1, 2)).Message);
        Assert.Contains("one value", Assert.Throws<InvalidOperationException>(() => WriteUserConfig(banks, VgtPrimitiveType, 1, 2)).Message);
    }
}
