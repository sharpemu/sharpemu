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

    // The layout for an attachment the draw does not also sample: writable in every aspect the
    // format has, so draws that toggle depth or stencil writes keep one layout (and one rendering
    // scope). Only a sampled attachment needs the read-only layouts of AttachmentLayout.
    public static ImageLayout WritableAttachmentLayout(Format format)
    {
        var available = ViewFormatRules.DepthAspects(format);
        var hasDepth = (available & ImageAspectFlags.DepthBit) != 0;
        var hasStencil = (available & ImageAspectFlags.StencilBit) != 0;
        return hasDepth && hasStencil ? ImageLayout.DepthStencilAttachmentOptimal
            : hasStencil ? ImageLayout.StencilAttachmentOptimal
            : ImageLayout.DepthAttachmentOptimal;
    }

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
                (depth.BackFaceEnabled && depth.StencilCompareBack > (uint)CompareOp.Always))
            {
                throw fatal(
                    $"The stencil compare or replacement state is not supported: depthControl=0x{depth.DepthControl:X8} " +
                    $"front=(fail={control.Fail} pass={control.Pass} depthFail={control.DepthFail} test=0x{masks.TestValue:X2} operation=0x{masks.OperationValue:X2} write=0x{frontWriteMask:X2}) " +
                    $"back=(fail={control.FailBack} pass={control.PassBack} depthFail={control.DepthFailBack} test=0x{masks.TestValueBack:X2} operation=0x{masks.OperationValueBack:X2} write=0x{backWriteMask:X2}).");
            }

            frontMasks = new StencilMasks(masks.Mask, frontWriteMask, masks.TestValue);
            front = ConvertFace(control.Fail, control.Pass, control.DepthFail,
                (CompareOp)depth.StencilCompare, masks.OperationValue, ref frontMasks, fatal);
            if (depth.BackFaceEnabled)
            {
                backMasks = new StencilMasks(masks.MaskBack, backWriteMask, masks.TestValueBack);
                back = ConvertFace(control.FailBack, control.PassBack, control.DepthFailBack,
                    (CompareOp)depth.StencilCompareBack, masks.OperationValueBack, ref backMasks, fatal);
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

    private static StencilOperations ConvertFace(byte fail, byte pass, byte depthFail, CompareOp compare,
        byte operationValue, ref StencilMasks masks, Func<string, Exception> fatal)
    {
        ReadOnlySpan<byte> operations = [fail, pass, depthFail];
        Span<StencilOp> converted = stackalloc StencilOp[3];
        var reference = masks.Reference;
        var requiredBits = compare is CompareOp.Always or CompareOp.Never ? 0u : masks.CompareMask;
        for (var index = 0; index < operations.Length; index++)
        {
            converted[index] = ConvertOperation(operations[index], (byte)masks.WriteMask, operationValue, fatal);
            if (converted[index] != StencilOp.Replace)
            {
                continue;
            }

            var replacement = operations[index] == ReplaceWithOperationValue ? operationValue : masks.Reference;
            // Vulkan shares one reference between comparison and all replacements on this face.
            if (((reference ^ replacement) & requiredBits & masks.WriteMask) != 0)
            {
                throw fatal($"The stencil replacement references conflict: compare={compare} compareMask=0x{masks.CompareMask:X2} " +
                    $"writeMask=0x{masks.WriteMask:X2} operation=0x{operationValue:X2} test=0x{masks.Reference:X2}.");
            }

            reference = (reference & ~masks.WriteMask) | (replacement & masks.WriteMask);
            requiredBits |= masks.WriteMask;
        }

        masks = masks with { Reference = reference };
        return new StencilOperations(converted[0], converted[1], converted[2], compare);
    }

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
                return StencilOp.Replace;
            case ReplaceWithOperationValue:
                return (operationValue & writeMask) == 0 ? StencilOp.Zero : StencilOp.Replace;
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
