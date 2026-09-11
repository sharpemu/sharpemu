// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

public readonly record struct StencilOperations(StencilOp FailOperation, StencilOp PassOperation, StencilOp DepthFailOperation, CompareOp Compare)
{
    public static StencilOperations Default { get; } = new(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep, CompareOp.Never);
}

public readonly record struct StencilMasks(uint CompareMask, uint WriteMask, uint Reference);

// The depth and stencil pipeline state of a draw, resolved from the context bank.
public readonly record struct DepthStencilState(
    bool DepthClearEnabled,
    float DepthClearValue,
    bool DepthTestEnabled,
    bool DepthWriteEnabled,
    CompareOp DepthCompare,
    bool DepthBoundsTestEnabled,
    float DepthMinBounds,
    float DepthMaxBounds,
    bool StencilClearEnabled,
    byte StencilClearValue,
    bool StencilTestEnabled,
    StencilOperations FrontOperations,
    StencilMasks FrontMasks,
    StencilOperations BackOperations,
    StencilMasks BackMasks)
{
    // The aspects the draw can write; a clear counts, a test without a possible write does not.
    public ImageAspectFlags AttachmentWriteAspects(Format format)
    {
        if (format == Format.Undefined)
        {
            return ImageAspectFlags.None;
        }

        var available = ViewFormatRules.DepthAspects(format);
        var writes = ImageAspectFlags.None;
        if ((available & ImageAspectFlags.DepthBit) != 0 && (DepthClearEnabled || (DepthTestEnabled && DepthWriteEnabled)))
        {
            writes |= ImageAspectFlags.DepthBit;
        }

        if ((available & ImageAspectFlags.StencilBit) == 0)
        {
            return writes;
        }

        if (StencilClearEnabled || (StencilTestEnabled && (FaceWrites(FrontOperations, FrontMasks) || FaceWrites(BackOperations, BackMasks))))
        {
            writes |= ImageAspectFlags.StencilBit;
        }

        return writes;
    }

    public bool IsReadOnly(Format format) => AttachmentWriteAspects(format) == ImageAspectFlags.None;

    public ImageLayout AttachmentLayout(Format format)
    {
        var available = ViewFormatRules.DepthAspects(format);
        var writes = AttachmentWriteAspects(format);
        var hasDepth = (available & ImageAspectFlags.DepthBit) != 0;
        var hasStencil = (available & ImageAspectFlags.StencilBit) != 0;
        var depthWrite = (writes & ImageAspectFlags.DepthBit) != 0;
        var stencilWrite = (writes & ImageAspectFlags.StencilBit) != 0;
        if (!hasStencil)
        {
            return depthWrite ? ImageLayout.DepthAttachmentOptimal : ImageLayout.DepthReadOnlyOptimal;
        }

        if (!hasDepth)
        {
            return stencilWrite ? ImageLayout.StencilAttachmentOptimal : ImageLayout.StencilReadOnlyOptimal;
        }

        if (depthWrite && stencilWrite)
        {
            return ImageLayout.DepthStencilAttachmentOptimal;
        }

        if (depthWrite)
        {
            return ImageLayout.DepthAttachmentStencilReadOnlyOptimal;
        }

        return stencilWrite ? ImageLayout.DepthReadOnlyStencilAttachmentOptimal : ImageLayout.DepthStencilReadOnlyOptimal;
    }

    private bool FaceWrites(in StencilOperations operations, in StencilMasks masks)
    {
        if (masks.WriteMask == 0)
        {
            return false;
        }

        var canPass = operations.Compare != CompareOp.Never;
        var canFail = operations.Compare != CompareOp.Always;
        if (masks.CompareMask == 0)
        {
            switch (operations.Compare)
            {
                case CompareOp.Equal:
                case CompareOp.LessOrEqual:
                case CompareOp.GreaterOrEqual:
                case CompareOp.Always:
                    canPass = true;
                    canFail = false;
                    break;
                case CompareOp.Never:
                case CompareOp.Less:
                case CompareOp.Greater:
                case CompareOp.NotEqual:
                    canPass = false;
                    canFail = true;
                    break;
            }
        }

        var depthPass = !DepthTestEnabled || DepthCompare != CompareOp.Never;
        var depthFail = DepthTestEnabled && DepthCompare != CompareOp.Always;
        return (canFail && operations.FailOperation != StencilOp.Keep) ||
               (canPass && depthPass && operations.PassOperation != StencilOp.Keep) ||
               (canPass && depthFail && operations.DepthFailOperation != StencilOp.Keep);
    }
}

public readonly record struct DepthTargetState(DepthTargetResolution Target, DepthStencilState State);

// Resolves the depth target of a draw: the image request through the request builder, then the pipeline state.
public static class DepthTargetResolver
{
    private const byte ReplaceWithOperationValue = 0x04;
    private const byte ExclusiveOr = 0x0C;

    // Null means no depth or stencil state is active for the draw.
    public static DepthTargetState? Resolve(ContextRegisters context, IImageFormatSupport device, Func<string, Exception> fatal)
    {
        if (ImageRequestBuilders.DepthTarget(in context.DepthTarget, device) is not { } target)
        {
            return null;
        }

        return new DepthTargetState(target, ResolveState(context, target.HasStencil, fatal));
    }

    public static DepthStencilState ResolveState(ContextRegisters context, bool hasStencil, Func<string, Exception> fatal)
    {
        var depth = context.DepthTarget;
        var control = context.StencilControl;
        var masks = context.StencilMask;
        var stencilTest = hasStencil && depth.StencilTestEnabled;
        var front = StencilOperations.Default;
        var frontMasks = default(StencilMasks);
        var back = front;
        var backMasks = frontMasks;
        if (stencilTest)
        {
            // A stencil clear or a write-disabled view leaves the operations without effect.
            var operationsDisabled = depth.StencilClearEnabled || depth.StencilWriteDisabled;
            var frontWriteMask = operationsDisabled ? (byte)0 : masks.WriteMask;
            var backWriteMask = operationsDisabled ? (byte)0 : masks.WriteMaskBack;
            if (depth.StencilCompare > (uint)CompareOp.Always ||
                (depth.BackFaceEnabled && depth.StencilCompareBack > (uint)CompareOp.Always) ||
                (frontWriteMask != 0 && UsesOperationValue(control.Fail, control.Pass, control.DepthFail) && masks.OperationValue != masks.TestValue) ||
                (depth.BackFaceEnabled && backWriteMask != 0 && UsesOperationValue(control.FailBack, control.PassBack, control.DepthFailBack) && masks.OperationValueBack != masks.TestValueBack))
            {
                throw fatal(
                    $"The stencil compare or replacement state is not supported: depthControl=0x{depth.DepthControl:X8} " +
                    $"front=(fail={control.Fail} pass={control.Pass} depthFail={control.DepthFail} test=0x{masks.TestValue:X2} operation=0x{masks.OperationValue:X2} write=0x{frontWriteMask:X2}) " +
                    $"back=(fail={control.FailBack} pass={control.PassBack} depthFail={control.DepthFailBack} test=0x{masks.TestValueBack:X2} operation=0x{masks.OperationValueBack:X2} write=0x{backWriteMask:X2}).");
            }

            front = new StencilOperations(
                ConvertOperation(control.Fail, frontWriteMask, masks.OperationValue, fatal),
                ConvertOperation(control.Pass, frontWriteMask, masks.OperationValue, fatal),
                ConvertOperation(control.DepthFail, frontWriteMask, masks.OperationValue, fatal),
                (CompareOp)depth.StencilCompare);
            frontMasks = new StencilMasks(masks.Mask, frontWriteMask, masks.TestValue);
            if (depth.BackFaceEnabled)
            {
                back = new StencilOperations(
                    ConvertOperation(control.FailBack, backWriteMask, masks.OperationValueBack, fatal),
                    ConvertOperation(control.PassBack, backWriteMask, masks.OperationValueBack, fatal),
                    ConvertOperation(control.DepthFailBack, backWriteMask, masks.OperationValueBack, fatal),
                    (CompareOp)depth.StencilCompareBack);
                backMasks = new StencilMasks(masks.MaskBack, backWriteMask, masks.TestValueBack);
            }
            else
            {
                back = front;
                backMasks = frontMasks;
            }
        }

        return new DepthStencilState(
            depth.DepthClearEnabled,
            context.DepthClearValue,
            depth.DepthTestEnabled,
            depth.DepthWriteEnabled && !depth.DepthWriteDisabled,
            (CompareOp)depth.DepthCompare,
            depth.DepthBoundsEnabled,
            context.DepthBoundsMin,
            context.DepthBoundsMax,
            hasStencil && depth.StencilClearEnabled && !depth.StencilWriteDisabled,
            context.StencilClearValue,
            stencilTest,
            front,
            frontMasks,
            back,
            backMasks);
    }

    private static bool UsesOperationValue(byte fail, byte pass, byte depthFail) =>
        fail == ReplaceWithOperationValue || pass == ReplaceWithOperationValue || depthFail == ReplaceWithOperationValue;

    // A zero write mask makes every operation a keep; XOR maps to invert only over the written bits.
    private static StencilOp ConvertOperation(byte operation, byte writeMask, byte operationValue, Func<string, Exception> fatal)
    {
        if (writeMask == 0)
        {
            return StencilOp.Keep;
        }

        switch (operation)
        {
            case 0x00:
                return StencilOp.Keep;
            case 0x01:
                return StencilOp.Zero;
            case 0x03:
            case ReplaceWithOperationValue:
                return StencilOp.Replace;
            case 0x05:
                return StencilOp.IncrementAndClamp;
            case 0x06:
                return StencilOp.DecrementAndClamp;
            case 0x07:
                return StencilOp.Invert;
            case 0x08:
                return StencilOp.IncrementAndWrap;
            case 0x09:
                return StencilOp.DecrementAndWrap;
            case ExclusiveOr:
                if ((writeMask & operationValue) == 0)
                {
                    return StencilOp.Keep;
                }

                if ((writeMask & ~operationValue) != 0)
                {
                    throw fatal($"The stencil XOR operands are not supported: writeMask=0x{writeMask:X2} operationValue=0x{operationValue:X2}.");
                }

                return StencilOp.Invert;
            default:
                throw fatal($"The stencil operation is not supported: operation=0x{operation:X2}.");
        }
    }
}
