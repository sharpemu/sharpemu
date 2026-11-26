// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Pipelines;
using Xunit;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ShaderRegisterOffset;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands.Registers;

public sealed class DeferredDrawSnapshotTests
{
    private static RegisterBanks Banks() => new(static message => new InvalidOperationException(message));

    private static void Scalars(RegisterBanks banks, uint offset, uint word)
    {
        banks.UserDataMarker = UserScalarKind.Region;
        RegisterWriteTable.WriteShaderPacket(banks, default, offset, new ReadOnlySpan<uint>(in word));
    }

    [Fact]
    public void DeferredSnapshotsStayIsolatedAcrossRetirement()
    {
        var live = Banks();
        int slots = 0;
        var first = new AgcExports.DeferredDraw();
        var second = new AgcExports.DeferredDraw();
        first.CaptureBanks(live, ref slots);
        Assert.Equal(0, slots);
        Scalars(live, SpiShaderUserDataGs0 + 31, 17);
        Scalars(live, SpiShaderUserDataPs0, 19);
        Scalars(live, SpiShaderUserDataHs0, 23);
        live.Shader.Vertex.LocalAddress = 31;
        first.CaptureBanks(live, ref slots);
        var vertex = first.Banks.Shader.Vertex;
        Assert.NotSame(live.Shader.Vertex, vertex);
        Assert.Same(live.Shader.Vertex.HullUserScalars, vertex.HullUserScalars);
        Scalars(live, SpiShaderUserDataHs0, 29);
        Scalars(live, SpiShaderUserDataGs0 + 31, 37);
        Scalars(live, SpiShaderUserDataPs0, 41);
        second.CaptureBanks(live, ref slots);
        Assert.Same(live.Shader.Vertex, second.Banks.Shader.Vertex);
        Assert.Equal(23u, vertex.HullUserScalars.Values[0]);
        Assert.Equal(17u, vertex.GeometryUserScalars.Values[31]);
        Assert.Equal(32u, vertex.GeometryUserScalars.Count);
        Assert.Equal(UserScalarKind.Region, vertex.GeometryUserScalars.Kinds[31]);
        Assert.Equal(19u, first.Banks.Shader.Pixel.UserScalars.Values[0]);
        Assert.Equal(31ul, vertex.LocalAddress);
        Scalars(live, SpiShaderUserDataGs0 + 31, 43);
        Scalars(live, SpiShaderUserDataPs0, 47);
        first.CaptureBanks(live, ref slots);
        Assert.Same(vertex, first.Banks.Shader.Vertex);
        Assert.Equal(37u, second.Banks.Shader.Vertex.GeometryUserScalars.Values[31]);
        Assert.Equal(41u, second.Banks.Shader.Pixel.UserScalars.Values[0]);
        Assert.Equal(43u, vertex.GeometryUserScalars.Values[31]);
        Assert.Equal(47u, first.Banks.Shader.Pixel.UserScalars.Values[0]);
        var ordinary = live.Snapshot();
        second.CaptureBanks(live, ref slots);
        Scalars(live, SpiShaderUserDataGs0 + 31, 53);
        Assert.Equal(43u, ordinary.Shader.Vertex.GeometryUserScalars.Values[31]);
        int full = AgcExports.DeferredDraw.MaxOwnedSnapshots;
        var overflow = new AgcExports.DeferredDraw();
        overflow.CaptureBanks(live, ref full);
        Scalars(live, SpiShaderUserDataGs0 + 31, 59);
        overflow.CaptureBanks(live, ref full);
        Assert.Same(live.Shader.Vertex, overflow.Banks.Shader.Vertex);
        Assert.Equal(AgcExports.DeferredDraw.MaxOwnedSnapshots, full);
    }

    [Fact]
    public void DeferredStageCopyCapturesEveryRegisterField()
    {
        static object Populated(Type type)
        {
            if (type.IsEnum) return Enum.ToObject(type, 1);
            if (type.IsPrimitive) return Convert.ChangeType(1, type);
            var value = Activator.CreateInstance(type)!;
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                field.SetValue(value, Populated(field.FieldType));
            return value;
        }

        static void Check<T>(T source, T target, Action copy, string privateScalars) where T : class
        {
            var fields = typeof(T).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            foreach (var field in fields)
            {
                if (field.GetValue(source) is UserScalarRegisters scalars)
                    scalars.Set(31, 73, UserScalarKind.BufferResource);
                else field.SetValue(source, Populated(field.FieldType));
            }
            copy();
            foreach (var field in fields)
            {
                if (field.GetValue(source) is UserScalarRegisters scalars)
                {
                    var captured = Assert.IsType<UserScalarRegisters>(field.GetValue(target));
                    Assert.Equal(field.Name != privateScalars, ReferenceEquals(scalars, captured));
                    Assert.Equal(32u, captured.Count);
                    Assert.Equal(73u, captured.Values[31]);
                    Assert.Equal(UserScalarKind.BufferResource, captured.Kinds[31]);
                }
                else Assert.Equal(field.GetValue(source), field.GetValue(target));
            }
        }

        var vertex = new VertexStageRegisters();
        var vertexCopy = new VertexStageRegisters();
        Check(vertex, vertexCopy, () => vertex.CopyForDeferredDraw(vertexCopy), nameof(vertex.GeometryUserScalars));
        var pixel = new PixelStageRegisters();
        var pixelCopy = new PixelStageRegisters();
        Check(pixel, pixelCopy, () => pixel.CopyForDeferredDraw(pixelCopy), nameof(pixel.UserScalars));
    }

    [Fact]
    public void EqualEntriesKeepStampsAndSeparateIndexType()
    {
        var live = Banks();
        var prior = live.Snapshot();
        RegisterWriteTable.WriteContextEntry(live, ContextRegisterOffset.SpiPsInputAddr, 0, 0);
        RegisterWriteTable.WriteUserConfigPacket(live, default, UserConfigRegisterOffset.VgtIndexType, [6]);
        RegisterWriteTable.WriteUserConfigEntry(live, UserConfigRegisterOffset.VgtPrimitiveType, 0x40, 0);
        Assert.Same(prior.Context, live.Context);
        Assert.Same(prior.UserConfig, live.UserConfig);
        Assert.NotEqual(prior.Stamp(RegisterStamp.ShaderInterface), live.Stamp(RegisterStamp.ShaderInterface));
        Assert.NotEqual(prior.Stamp(RegisterStamp.UserConfig), live.Stamp(RegisterStamp.UserConfig));
        Assert.Equal(0u, prior.IndexTypeAndSize);
        Assert.Equal(2u, live.IndexTypeAndSize);
        RegisterWriteTable.WriteContextEntry(live, ContextRegisterOffset.SpiPsInputAddr, 7, 0);
        RegisterWriteTable.WriteUserConfigEntry(live, UserConfigRegisterOffset.VgtPrimitiveType, 4, 0);
        Assert.Equal(0u, prior.Context.ShaderInterface.PixelInputAddress);
        Assert.Equal(7u, live.Context.ShaderInterface.PixelInputAddress);
        Assert.Equal(4u, live.UserConfig.PrimitiveType);
        Assert.Throws<InvalidOperationException>(() =>
            RegisterWriteTable.WriteUserConfigPacket(live, default, UserConfigRegisterOffset.VgtIndexType, [1, 2]));
    }

    [Fact]
    public void CompilerKeepsOnlyStaticInputsWhenTheDrawIsBorrowed()
    {
        var stage = new ShaderStageResources(new ShaderProgramInfo(), null!, 37);
        var vertex = new VertexInputInfo { Stage = stage, BaseVertexUserData = 7 };
        var pixel = new PixelInputInfo { Stage = stage, IeeeMode = true };
        var vertexOptions = new KeptVertexInput { Info = vertex }.OptionsFor(vertex, 3, null, borrowed: true);
        var pixelOptions = new KeptPixelInput { Info = pixel }.OptionsFor(pixel, [], new(), borrowed: true);
        Assert.False(vertexOptions.VertexInfo!.Stage.IsValid);
        Assert.Equal(7, vertexOptions.VertexInfo.BaseVertexUserData);
        Assert.False(pixelOptions.PixelInfo!.Stage.IsValid);
        Assert.True(pixelOptions.PixelInfo.IeeeMode);
        Assert.True(vertex.Stage.IsValid);
        Assert.True(pixel.Stage.IsValid);
    }
}
