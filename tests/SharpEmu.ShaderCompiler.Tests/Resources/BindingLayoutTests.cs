// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class BindingLayoutTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(30u)]
    public void DispatchLimitsFollowPackedOffsetsAndTakePartInAllocation(uint cursor)
    {
        var info = new ShaderResourceInfo { Buffers = Enumerable.Range(0, 5).Select(_ => new BufferResource { Read = true }).ToList() };
        var plain = BindingLayout.Allocate(info, [0u], false, false, true, cursor);
        var limits = BindingLayout.Allocate(info, [0u], false, false, true, cursor, usesDispatchThreadLimits: true);
        Assert.Equal(3u, limits.MemoryOffsetDword);
        Assert.Equal(5u, limits.DispatchThreadLimitsDword);
        Assert.Equal(8u, limits.ShaderDataDwordCount);
        Assert.NotEqual(plain, limits);
        Assert.Equal(cursor == 0, limits.UsesPushData);
        BindingLayoutValidator.Validate(limits, info, [0u], false, false, true, Hash, ShaderStage.Compute);
        Assert.Throws<ResourcePlanException>(() =>
            BindingLayoutValidator.Validate(limits, info, [0u], false, false, true, Hash, ShaderStage.Pixel));
    }

    [Fact]
    public void ShaderInfoAndBindingLayout()
    {
        var program = Program(
            MoveScalarRegister(0, 8, 3),
            MoveScalarRegister(4, 9, 4),
            MoveScalar(8, 10, 64),
            MoveScalar(12, 11, 0),
            BufferLoad(16, 8),
            DataShareWrite(24, gds: true),
            EndProgram(32));
        var plan = Extract(program);
        var registers = BindingLayout.CollectUserDataRegisters(program, 0, 64);
        var layout = BindingLayout.Allocate(plan.Info, registers, BindingLayout.UsesGlobalDataShare(program), plan.TableReads.Count != 0, false);

        Assert.NotNull(layout.Find(DescriptorBindingKind.Buffers));
        Assert.NotNull(layout.Find(DescriptorBindingKind.GlobalDataShare));
        Assert.Null(layout.Find(DescriptorBindingKind.ShaderData));
        Assert.True(layout.UsesPushData);
        Assert.False(layout.UsesShaderBase);
        Assert.Equal(2u, layout.MemoryOffsetDword);
        Assert.Equal((uint)DescriptorBindingKind.Buffers, BindingLayout.NativeBindingIndex(ShaderStage.Compute, DescriptorBindingKind.Buffers));
        Assert.Equal((uint)DescriptorBindingKind.Buffers, BindingLayout.NativeBindingIndex(ShaderStage.Vertex, DescriptorBindingKind.Buffers));
        Assert.Equal((uint)DescriptorBindingKind.Count + (uint)DescriptorBindingKind.Buffers, BindingLayout.NativeBindingIndex(ShaderStage.Pixel, DescriptorBindingKind.Buffers));
        Assert.Equal([3u, 4u], layout.UserDataRegisters);
        BindingLayoutValidator.Validate(layout, plan.Info, registers, true, false, false, Hash, ShaderStage.Compute);
    }

    // s4 is written on one path and read on the other, so it is live at entry.
    [Fact]
    public void UserDataReadOnOneBranch_AfterAWriteOnTheOther_IsCollected()
    {
        var program = Program(
            Branch(0, "SCbranchScc0", 1),
            MoveScalar(4, 4, 0),
            MoveScalar(8, 5, 0),
            MoveScalar(12, 6, 64),
            MoveScalar(16, 7, 0),
            BufferLoad(20, 4),
            EndProgram(28));

        Assert.Equal([4u], BindingLayout.CollectUserDataRegisters(program, 0, 16));
    }

    [Fact]
    public void UserDataWrittenOnEveryPathBeforeTheRead_IsNotCollected()
    {
        var program = Program(
            Branch(0, "SCbranchScc0", 2),
            MoveScalar(4, 4, 0x1000),
            Branch(8, "SBranch", 1),
            MoveScalar(12, 4, 0x2000),
            MoveScalar(16, 5, 0),
            MoveScalar(20, 6, 64),
            MoveScalar(24, 7, 0),
            BufferLoad(28, 4),
            MoveScalarRegister(36, 8, 9),
            EndProgram(40));

        Assert.Equal([9u], BindingLayout.CollectUserDataRegisters(program, 0, 16));
    }

    // A four-dword load into s4..s7 defines those four only; s8..s11 stay user data.
    [Fact]
    public void MultiwordLoadDestinations_DefineOnlyTheLoadedRegisters()
    {
        var program = Program(
            ScalarLoad(0, 0, destination: 4, count: 4),
            BufferLoad(8, 8),
            Sop2(16, "SLshlB64", 12, Gen5Operand.Scalar(14), Operand(4)),
            MoveScalarRegister(20, 16, 13),
            EndProgram(24));

        Assert.Equal([0u, 1u, 8u, 9u, 10u, 11u, 14u, 15u], BindingLayout.CollectUserDataRegisters(program, 0, 32));
    }

    [Fact]
    public void UserDataReadInsideALoop_AfterTheLoopWritesIt_IsCollected()
    {
        var program = Program(
            MoveScalarRegister(0, 8, 4),
            Sop2(4, "SAddU32", 4, Gen5Operand.Scalar(4), Operand(1)),
            Sopc(8, "SCmpLgU32", Gen5Operand.Scalar(8), Operand(0)),
            Branch(12, "SCbranchScc1", -4),
            EndProgram(16));

        Assert.Equal([4u], BindingLayout.CollectUserDataRegisters(program, 0, 16));
    }

    [Fact]
    public void ShaderBaseReservation_FollowsTheUserDataRegisters()
    {
        var program = Program(
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sop1, "SGetpcB64", [0u], [], [Gen5Operand.Scalar(4)], null),
            Sop2(4, "SAddU32", 4, Gen5Operand.Scalar(4), Gen5Operand.Scalar(2)),
            Sop2(8, "SAddcU32", 5, Gen5Operand.Scalar(5), Operand(0)),
            MoveScalar(12, 6, 64),
            MoveScalar(16, 7, 0),
            BufferLoad(20, 4),
            EndProgram(28));
        var plan = Extract(program, userDataCount: 3);
        var registers = BindingLayout.CollectUserDataRegisters(program, 0, 3);

        Assert.True(BindingLayout.ReadsShaderBase(program));
        var layout = BindingLayout.Allocate(plan.Info, registers, false, false, BindingLayout.ReadsShaderBase(program));
        Assert.Equal([2u], registers);
        Assert.True(layout.UsesShaderBase);
        Assert.Equal(1u, layout.ShaderBaseDword);
        Assert.Equal(3u, layout.MemoryOffsetDword);
        Assert.Equal(4u, layout.ShaderDataDwordCount);
        BindingLayoutValidator.Validate(layout, plan.Info, registers, false, false, true, Hash, ShaderStage.Compute);

        var without = BindingLayout.Allocate(plan.Info, registers, false, false, false);
        Assert.False(without.UsesShaderBase);
        Assert.Equal(1u, without.MemoryOffsetDword);
        Assert.NotEqual(layout, without);
        Assert.False(BindingLayout.ReadsShaderBase(Program(BufferLoad(0, 4), EndProgram(8))));
    }

    [Fact]
    public void ImageBindingAbi()
    {
        Assert.Equal(45u, BindingLayout.ImageBindingCount);
        Assert.Equal(0u, (uint)DescriptorBindingKind.Buffers);
        Assert.Equal(46u, (uint)DescriptorBindingKind.Samplers);
        Assert.Equal(47u, (uint)DescriptorBindingKind.GlobalDataShare);
        Assert.Equal(48u, (uint)DescriptorBindingKind.DeviceAddressPageTable);
        Assert.Equal(49u, (uint)DescriptorBindingKind.FaultBuffer);
        Assert.Equal(50u, (uint)DescriptorBindingKind.FlattenedResourceTable);
        Assert.Equal(51u, (uint)DescriptorBindingKind.ShaderData);
        Assert.Equal(52u, (uint)DescriptorBindingKind.Count);

        ImageDimension[] sampledDimensions =
        [
            ImageDimension.Dim1D, ImageDimension.Dim1DArray, ImageDimension.Dim2D, ImageDimension.Dim2DArray,
            ImageDimension.Dim2DMsaa, ImageDimension.Dim2DMsaaArray, ImageDimension.Dim3D,
        ];
        ImageDimension[] storageDimensions =
        [
            ImageDimension.Dim1D, ImageDimension.Dim1DArray, ImageDimension.Dim2D, ImageDimension.Dim2DArray, ImageDimension.Dim3D,
        ];
        var index = 0u;
        void CheckBinding(ImageResourceClass resourceClass, ImageNumericClass numericClass, ImageDimension dimension, bool atomic, bool cube = false, bool depthCompare = false)
        {
            var image = new ImageResource { ResourceClass = resourceClass, NumericClass = numericClass, Dimension = dimension, Atomic = atomic, Cube = cube, DepthCompare = depthCompare };
            var kind = ImageDescriptorBinding.ForImage(image);
            Assert.NotNull(kind);
            Assert.Equal(BindingLayout.FirstImageBinding + index, (uint)kind!.Value);
            Assert.Equal(index, ImageDescriptorBinding.ArrayIndex(kind.Value));
            Assert.Equal(resourceClass, ImageDescriptorBinding.ResourceClass(kind.Value));
            Assert.Equal(BindingLayout.FirstImageBinding + index, BindingLayout.NativeBindingIndex(ShaderStage.Compute, kind.Value));
            Assert.Equal((uint)DescriptorBindingKind.Count + BindingLayout.FirstImageBinding + index, BindingLayout.NativeBindingIndex(ShaderStage.Pixel, kind.Value));
            index++;
        }

        foreach (var numericClass in new[] { ImageNumericClass.Float, ImageNumericClass.Uint, ImageNumericClass.Sint })
        {
            foreach (var dimension in sampledDimensions)
            {
                CheckBinding(ImageResourceClass.Sampled, numericClass, dimension, false);
            }
        }

        foreach (var numericClass in new[] { ImageNumericClass.Float, ImageNumericClass.Uint })
        {
            foreach (var dimension in storageDimensions)
            {
                CheckBinding(ImageResourceClass.Storage, numericClass, dimension, false);
            }
        }

        foreach (var dimension in storageDimensions)
        {
            CheckBinding(ImageResourceClass.Storage, ImageNumericClass.Uint, dimension, true);
        }

        foreach (var numericClass in new[] { ImageNumericClass.Float, ImageNumericClass.Uint, ImageNumericClass.Sint })
            CheckBinding(ImageResourceClass.Sampled, numericClass, ImageDimension.Dim2D, false, cube: true);
        foreach (var numericClass in new[] { ImageNumericClass.Float, ImageNumericClass.Uint })
            CheckBinding(ImageResourceClass.Storage, numericClass, ImageDimension.Dim2D, false, cube: true);
        CheckBinding(ImageResourceClass.Storage, ImageNumericClass.Uint, ImageDimension.Dim2D, true, cube: true);
        // Depth-reference sampling never shares an array with ordinary images.
        CheckBinding(ImageResourceClass.Sampled, ImageNumericClass.Float, ImageDimension.Dim2D, false, depthCompare: true);
        CheckBinding(ImageResourceClass.Sampled, ImageNumericClass.Float, ImageDimension.Dim2DArray, false, depthCompare: true);
        CheckBinding(ImageResourceClass.Sampled, ImageNumericClass.Float, ImageDimension.Dim2D, false, cube: true, depthCompare: true);

        Assert.Equal(BindingLayout.ImageBindingCount, index);

        static bool Invalid(ImageResource image) => ImageDescriptorBinding.ForImage(image) is null;
        var invalid = new ImageResource();
        Assert.True(Invalid(invalid));
        invalid.ResourceClass = ImageResourceClass.Sampled;
        invalid.NumericClass = ImageNumericClass.Float;
        invalid.Dimension = ImageDimension.Unknown;
        Assert.True(Invalid(invalid));
        invalid.Dimension = ImageDimension.Dim2D;
        invalid.NumericClass = ImageNumericClass.Unsupported;
        Assert.True(Invalid(invalid));
        invalid.NumericClass = (ImageNumericClass)255;
        Assert.True(Invalid(invalid));
        invalid.NumericClass = ImageNumericClass.Float;
        invalid.Dimension = (ImageDimension)255;
        Assert.True(Invalid(invalid));
        invalid.Dimension = ImageDimension.Dim2D;
        invalid.Atomic = true;
        Assert.True(Invalid(invalid));
        invalid.ResourceClass = ImageResourceClass.Storage;
        invalid.Atomic = false;
        invalid.NumericClass = ImageNumericClass.Sint;
        Assert.True(Invalid(invalid));
        invalid.NumericClass = ImageNumericClass.Float;
        invalid.Dimension = ImageDimension.Dim2DMsaa;
        Assert.True(Invalid(invalid));
        invalid.Dimension = ImageDimension.Dim2D;
        invalid.Atomic = true;
        Assert.True(Invalid(invalid));
    }

    [Fact]
    public void GraphicsPushConstantLayout()
    {
        static uint[] UserData(uint count) => Enumerable.Range(0, (int)count).Select(index => (uint)index).ToArray();
        uint cursor = 0;
        var pixel = BindingLayout.Allocate(new ShaderResourceInfo(), UserData(4), false, false, false, cursor);
        Assert.True(pixel.UsesPushData);
        Assert.Equal(0u, pixel.PushDataStartDword);
        Assert.Null(pixel.Find(DescriptorBindingKind.ShaderData));
        pixel.AdvancePushData(ref cursor);

        var vertex = BindingLayout.Allocate(new ShaderResourceInfo(), UserData(9), false, false, false, cursor);
        Assert.True(vertex.UsesPushData);
        Assert.Equal(4u, vertex.PushDataStartDword);
        vertex.AdvancePushData(ref cursor);
        Assert.Equal(13u, cursor);

        var edge = BindingLayout.Allocate(new ShaderResourceInfo(), UserData(PushData.DwordCount), false, false, false);
        Assert.True(edge.UsesPushData);
        Assert.Null(edge.Find(DescriptorBindingKind.ShaderData));

        var spill = BindingLayout.Allocate(new ShaderResourceInfo(), UserData(20), false, false, false, cursor);
        Assert.False(spill.UsesPushData);
        Assert.Equal(PushData.NoStart, spill.PushDataStartDword);
        Assert.NotNull(spill.Find(DescriptorBindingKind.ShaderData));
        spill.AdvancePushData(ref cursor);
        Assert.Equal(13u, cursor);

        var repeatedSpill = BindingLayout.Allocate(new ShaderResourceInfo(), UserData(20), false, false, false, 20);
        Assert.Equal(spill, repeatedSpill);

        // The shader base takes two dwords after the user data and moves the cursor.
        cursor = 0;
        var withBase = BindingLayout.Allocate(new ShaderResourceInfo(), UserData(4), false, false, true, cursor);
        Assert.Equal(4u, withBase.ShaderBaseDword);
        Assert.Equal(6u, withBase.MemoryOffsetDword);
        withBase.AdvancePushData(ref cursor);
        Assert.Equal(6u, cursor);
    }

    // A stage that spilled at cursor 13 is valid; validation must not reallocate at zero.
    [Fact]
    public void ValidatorAcceptsAStageThatSpilledToShaderData()
    {
        static uint[] UserData(uint count) => Enumerable.Range(0, (int)count).Select(index => (uint)index).ToArray();
        var info = new ShaderResourceInfo();
        var spill = BindingLayout.Allocate(info, UserData(20), false, false, false, 13);

        Assert.False(spill.UsesPushData);
        Assert.NotNull(spill.Find(DescriptorBindingKind.ShaderData));
        BindingLayoutValidator.Validate(spill, info, UserData(20), false, false, false, Hash, ShaderStage.Vertex);

        var pushed = BindingLayout.Allocate(info, UserData(20), false, false, false, 0);
        Assert.True(pushed.UsesPushData);
        BindingLayoutValidator.Validate(pushed, info, UserData(20), false, false, false, Hash, ShaderStage.Vertex);
    }

    [Fact]
    public void ValidatorRejectsALayoutThatDoesNotMatchItsResources()
    {
        var info = new ShaderResourceInfo();
        info.Buffers.Add(new BufferResource());
        var layout = BindingLayout.Allocate(info, [0], false, false, false);
        var error = Assert.Throws<ResourcePlanException>(() =>
            BindingLayoutValidator.Validate(layout, info, [0, 1], false, false, false, Hash, ShaderStage.Vertex));
        Assert.Contains("does not match", error.Message);

        var baseError = Assert.Throws<ResourcePlanException>(() =>
            BindingLayoutValidator.Validate(layout, info, [0], false, false, true, Hash, ShaderStage.Vertex));
        Assert.Contains("does not match", baseError.Message);
    }
}
