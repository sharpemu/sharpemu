// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

// The tiled block shapes the tiler shaders address, in the tile block kind order.
public enum TilerBlockShape
{
    Standard256B,
    Standard4KB,
    Standard4KB3D,
    Standard64KB,
    Standard64KB3D,
    Prt64KB,
    Prt64KB3D,
    RenderTarget64KB,
    Depth64KB,
}

// Assembles the tiler compute shaders: block copies per shape and element size,
// the 16-bit depth conversions and the BGRA16 channel swap.
public static class TilerShaders
{
    public const int ArgumentCount = 13;

    private enum Axis
    {
        X,
        Y,
        Z,
    }

    private readonly record struct Term(Axis Source, int Shift, uint Mask);

    private static Term X(int shift, uint mask) => new(Axis.X, shift, mask);

    private static Term Y(int shift, uint mask) => new(Axis.Y, shift, mask);

    private static Term Z(int shift, uint mask) => new(Axis.Z, shift, mask);

    // Bindings 0 and 1 are the dword arrays; binding 2 or the push block holds the arguments.
    private sealed class ShaderModuleContext
    {
        public readonly SpirvModuleBuilder Builder = new();
        public uint VoidType;
        public uint BooleanType;
        public uint UintType;
        public uint FloatType;
        public uint UnsignedVector3Type;
        public uint UintStoragePointer;
        public uint InputBufferVariable;
        public uint OutputBufferVariable;
        public uint ArgumentsVariable;
        public uint ArgumentPointerType;
        public uint InvocationVariable;
        public uint GlslInstructionSet;
        private readonly Dictionary<uint, uint> _constants = new();

        public ShaderModuleContext(bool pushConstants)
        {
            var module = Builder;
            module.AddCapability(SpirvCapability.Shader);
            GlslInstructionSet = module.ImportExtInst("GLSL.std.450");
            VoidType = module.TypeVoid();
            BooleanType = module.TypeBool();
            UintType = module.TypeInt(32, signed: false);
            FloatType = module.TypeFloat(32);
            UnsignedVector3Type = module.TypeVector(UintType, 3);

            var runtimeArray = module.TypeRuntimeArray(UintType);
            module.AddDecoration(runtimeArray, SpirvDecoration.ArrayStride, 4);
            var bufferStruct = module.TypeStruct(runtimeArray);
            module.AddDecoration(bufferStruct, SpirvDecoration.Block);
            module.AddMemberDecoration(bufferStruct, 0, SpirvDecoration.Offset, 0);
            var bufferPointer = module.TypePointer(SpirvStorageClass.StorageBuffer, bufferStruct);
            UintStoragePointer = module.TypePointer(SpirvStorageClass.StorageBuffer, UintType);
            InputBufferVariable = module.AddGlobalVariable(bufferPointer, SpirvStorageClass.StorageBuffer);
            module.AddName(InputBufferVariable, "input_buffer");
            module.AddDecoration(InputBufferVariable, SpirvDecoration.DescriptorSet, 0);
            module.AddDecoration(InputBufferVariable, SpirvDecoration.Binding, 0);
            module.AddDecoration(InputBufferVariable, SpirvDecoration.NonWritable);
            OutputBufferVariable = module.AddGlobalVariable(bufferPointer, SpirvStorageClass.StorageBuffer);
            module.AddName(OutputBufferVariable, "output_buffer");
            module.AddDecoration(OutputBufferVariable, SpirvDecoration.DescriptorSet, 0);
            module.AddDecoration(OutputBufferVariable, SpirvDecoration.Binding, 1);

            var members = new uint[ArgumentCount];
            Array.Fill(members, UintType);
            var argumentsStruct = module.TypeStruct(members);
            module.AddDecoration(argumentsStruct, SpirvDecoration.Block);
            for (uint member = 0; member < ArgumentCount; member++)
            {
                module.AddMemberDecoration(argumentsStruct, member, SpirvDecoration.Offset, member * 4);
            }

            var storageClass = pushConstants ? SpirvStorageClass.PushConstant : SpirvStorageClass.Uniform;
            var argumentsPointer = module.TypePointer(storageClass, argumentsStruct);
            ArgumentPointerType = module.TypePointer(storageClass, UintType);
            ArgumentsVariable = module.AddGlobalVariable(argumentsPointer, storageClass);
            module.AddName(ArgumentsVariable, "params");
            if (!pushConstants)
            {
                module.AddDecoration(ArgumentsVariable, SpirvDecoration.DescriptorSet, 0);
                module.AddDecoration(ArgumentsVariable, SpirvDecoration.Binding, 2);
            }
        }

        public uint GetUnsignedConstant(uint value)
        {
            if (!_constants.TryGetValue(value, out var constantIdentifier))
            {
                constantIdentifier = Builder.Constant(UintType, value);
                _constants[value] = constantIdentifier;
            }

            return constantIdentifier;
        }

        public uint LoadArgument(uint index)
        {
            var pointer = Builder.AddInstruction(SpirvOp.AccessChain, ArgumentPointerType, ArgumentsVariable, GetUnsignedConstant(index));
            return Builder.AddInstruction(SpirvOp.Load, UintType, pointer);
        }

        public uint GetWordPointer(uint bufferVariable, uint index) =>
            Builder.AddInstruction(SpirvOp.AccessChain, UintStoragePointer, bufferVariable, GetUnsignedConstant(0), index);

        public uint LoadWord(uint bufferVariable, uint index) => Builder.AddInstruction(SpirvOp.Load, UintType, GetWordPointer(bufferVariable, index));

        public uint ShiftLeft(uint value, uint bits) => bits == 0 ? value : Builder.AddInstruction(SpirvOp.ShiftLeftLogical, UintType, value, GetUnsignedConstant(bits));

        public uint ShiftRight(uint value, uint bits) => bits == 0 ? value : Builder.AddInstruction(SpirvOp.ShiftRightLogical, UintType, value, GetUnsignedConstant(bits));

        public uint And(uint left, uint right) => Builder.AddInstruction(SpirvOp.BitwiseAnd, UintType, left, right);

        public uint Or(uint left, uint right) => Builder.AddInstruction(SpirvOp.BitwiseOr, UintType, left, right);

        public uint Xor(uint left, uint right) => Builder.AddInstruction(SpirvOp.BitwiseXor, UintType, left, right);

        public uint Add(uint left, uint right) => Builder.AddInstruction(SpirvOp.IAdd, UintType, left, right);

        public uint Multiply(uint left, uint right) => Builder.AddInstruction(SpirvOp.IMul, UintType, left, right);

        public uint Divide(uint left, uint right) => Builder.AddInstruction(SpirvOp.UDiv, UintType, left, right);

        public uint Invocation()
        {
            var inputPointer = Builder.TypePointer(SpirvStorageClass.Input, UnsignedVector3Type);
            InvocationVariable = Builder.AddGlobalVariable(inputPointer, SpirvStorageClass.Input);
            Builder.AddName(InvocationVariable, "gl_GlobalInvocationID");
            Builder.AddDecoration(InvocationVariable, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.GlobalInvocationId);
            return InvocationVariable;
        }

        // Ends the invocation when the condition holds; the body continues in a new block.
        public void ReturnWhen(uint condition)
        {
            var exitLabel = Builder.AllocateId();
            var bodyLabel = Builder.AllocateId();
            Builder.AddStatement(SpirvOp.SelectionMerge, bodyLabel, 0);
            Builder.AddStatement(SpirvOp.BranchConditional, condition, exitLabel, bodyLabel);
            Builder.AddLabel(exitLabel);
            Builder.AddStatement(SpirvOp.Return);
            Builder.AddLabel(bodyLabel);
        }

        public byte[] Finish(uint main, uint localX, uint localY)
        {
            Builder.AddExecutionMode(main, SpirvExecutionMode.LocalSize, localX, localY, 1);
            Builder.AddEntryPoint(SpirvExecutionModel.GLCompute, main, "main", [InvocationVariable, InputBufferVariable, OutputBufferVariable, ArgumentsVariable]);
            return Builder.Build();
        }
    }

    private static uint Log2(uint value) => (uint)System.Numerics.BitOperations.TrailingZeroCount(value);

    private static Term[] StandardTerms(uint bytes) => bytes switch
    {
        1 => [Y(4, 0x1f0), Y(5, 0x400), X(0, 0x00f), X(5, 0x200), X(6, 0x800)],
        2 => [Y(4, 0x070), Y(5, 0x100), Y(6, 0x400), X(1, 0x00e), X(4, 0x080), X(5, 0x200), X(6, 0x800)],
        4 => [Y(4, 0x070), Y(5, 0x100), Y(6, 0x400), X(2, 0x00c), X(5, 0x080), X(6, 0x200), X(7, 0x800)],
        8 => [Y(4, 0x030), Y(6, 0x100), Y(7, 0x400), X(3, 0x008), X(5, 0x0c0), X(6, 0x200), X(7, 0x800)],
        _ => [Y(4, 0x030), Y(6, 0x100), Y(7, 0x400), X(6, 0x0c0), X(7, 0x200), X(8, 0x800)],
    };

    private static Term[] Standard3DTerms(uint bytes) => bytes switch
    {
        1 => [X(0, 3), X(4, 0x40), X(6, 0x200), Y(3, 8), Y(4, 0x20), Y(6, 0x100), Y(8, 0x800), Z(2, 4), Z(3, 0x10), Z(5, 0x80), Z(7, 0x400)],
        2 => [X(1, 2), X(5, 0x40), X(7, 0x200), Y(3, 8), Y(4, 0x20), Y(6, 0x100), Y(8, 0x800), Z(2, 4), Z(3, 0x10), Z(5, 0x80), Z(7, 0x400)],
        4 => [X(2, 4), X(5, 0x40), X(7, 0x200), Y(3, 8), Y(4, 0x20), Y(6, 0x100), Y(8, 0x800), Z(4, 0x10), Z(6, 0x80), Z(8, 0x400)],
        8 => [X(3, 8), X(5, 0x40), X(7, 0x200), Y(5, 0x20), Y(7, 0x100), Y(9, 0x800), Z(4, 0x10), Z(6, 0x80), Z(8, 0x400)],
        _ => [X(6, 0x40), X(8, 0x200), Y(5, 0x20), Y(7, 0x100), Y(9, 0x800), Z(4, 0x10), Z(6, 0x80), Z(8, 0x400)],
    };

    private static Term[] Standard64KBTerms(uint bytes) => bytes switch
    {
        1 => [X(0, 0x0f), X(5, 0x200), X(6, 0x800), X(7, 0x2000), X(8, 0x8000), Y(4, 0x1f0), Y(5, 0x400), Y(6, 0x1000), Y(7, 0x4000)],
        2 => [X(1, 0x00e), X(4, 0x080), X(5, 0x200), X(6, 0x800), X(7, 0x2000), X(8, 0x8000), Y(4, 0x070), Y(5, 0x100), Y(6, 0x400), Y(7, 0x1000), Y(8, 0x4000)],
        4 => [X(2, 0x00c), X(5, 0x080), X(6, 0x200), X(7, 0x800), X(8, 0x2000), X(9, 0x8000), Y(4, 0x070), Y(5, 0x100), Y(6, 0x400), Y(7, 0x1000), Y(8, 0x4000)],
        8 => [X(3, 0x008), X(5, 0x0c0), X(6, 0x200), X(7, 0x800), X(8, 0x2000), X(9, 0x8000), Y(4, 0x030), Y(6, 0x100), Y(7, 0x400), Y(8, 0x1000), Y(9, 0x4000)],
        _ => [X(6, 0x0c0), X(7, 0x200), X(8, 0x800), X(9, 0x2000), X(10, 0x8000), Y(4, 0x030), Y(6, 0x100), Y(7, 0x400), Y(8, 0x1000), Y(9, 0x4000)],
    };

    private static Term[] RenderTargetTerms(uint bytes) => bytes switch
    {
        1 => [Y(2, 0x008), Y(4, 0x010), Y(3, 0x0a0), Y(5, 0xf00), Y(6, 0x1000), Y(7, 0x4000), X(0, 7), X(3, 0x040), X(5, 0x300), X(4, 0x400), X(6, 0x800), X(7, 0x2000), X(8, 0x8000)],
        2 => [Y(4, 0x070), Y(5, 0xf00), Y(8, 0x5000), X(1, 0x00e), X(4, 0x480), X(5, 0x300), X(6, 0x800), X(7, 0x2000), X(8, 0x8000)],
        4 => [Y(4, 0x070), Y(5, 0xf00), Y(9, 0x1000), Y(8, 0x4000), X(2, 0x00c), X(5, 0x380), X(4, 0x400), X(6, 0x800), X(9, 0xa000)],
        8 => [Y(4, 0x010), Y(6, 0x080), Y(5, 0xf00), Y(10, 0x5000), X(3, 0x008), X(4, 0x460), X(5, 0x300), X(6, 0x800), X(10, 0x2000), X(9, 0x8000)],
        _ => [X(4, 0x410), X(5, 0x340), X(6, 0x800), X(11, 0xa000), Y(5, 0xf20), Y(6, 0x080), Y(10, 0x1000), Y(11, 0x4000)],
    };

    private static Term[] DepthTerms(uint bytes) => bytes switch
    {
        1 => [X(0, 1), X(1, 0x004), X(2, 0x010), X(3, 0x040), X(5, 0x300), X(4, 0x400), X(6, 0x800), X(7, 0x2000), X(8, 0x8000), Y(1, 0x002), Y(2, 0x008), Y(3, 0x0a0), Y(5, 0xf00), Y(6, 0x1000), Y(7, 0x4000)],
        2 => [X(1, 0x002), X(2, 0x008), X(3, 0x020), X(4, 0x480), X(5, 0x300), X(6, 0x800), X(7, 0x2000), X(8, 0x8000), Y(2, 0x004), Y(3, 0x010), Y(4, 0x040), Y(5, 0xf00), Y(8, 0x5000)],
        4 => [X(2, 0x004), X(3, 0x010), X(4, 0x440), X(5, 0x300), X(6, 0x800), X(9, 0xa000), Y(3, 0x008), Y(4, 0x020), Y(5, 0xf80), Y(9, 0x1000), Y(8, 0x4000)],
        _ => [X(3, 0x008), X(4, 0x420), X(5, 0x380), X(6, 0x800), X(10, 0x2000), X(9, 0x8000), Y(4, 0x010), Y(5, 0xf40), Y(10, 0x5000)],
    };

    private static (uint Width, uint Height, uint Depth) ThinExtent(uint blockBytes, uint bytes, uint widthForOneOrTwoByteElements, uint widthForFourOrEightByteElements, uint widthForSixteenByteElements)
    {
        var width = bytes <= 2 ? widthForOneOrTwoByteElements : bytes <= 8 ? widthForFourOrEightByteElements : widthForSixteenByteElements;
        return (width, blockBytes / (width * bytes), 1);
    }

    private static (uint Width, uint Height, uint Depth) Thick4KBExtent(uint bytes) => bytes switch
    {
        1 => (16, 16, 16),
        2 => (8, 16, 16),
        4 => (8, 16, 8),
        8 => (8, 8, 8),
        _ => (4, 8, 8),
    };

    private static (uint Width, uint Height, uint Depth) Thick64KBExtent(uint bytes) => bytes switch
    {
        1 => (64, 32, 32),
        2 => (32, 32, 32),
        4 => (32, 32, 16),
        8 => (32, 16, 16),
        _ => (16, 16, 16),
    };

    private static uint XorTerms(ShaderModuleContext shaderModule, uint x, uint y, uint z, Term[] terms)
    {
        uint? result = null;
        foreach (var term in terms)
        {
            var source = term.Source switch { Axis.X => x, Axis.Y => y, _ => z };
            var value = shaderModule.And(shaderModule.ShiftLeft(source, (uint)term.Shift), shaderModule.GetUnsignedConstant(term.Mask));
            result = result is { } current ? shaderModule.Xor(current, value) : value;
        }

        return result!.Value;
    }

    private static uint Bit(ShaderModuleContext shaderModule, uint value, uint sourceBit, uint destinationBit) =>
        shaderModule.ShiftLeft(shaderModule.And(shaderModule.ShiftRight(value, sourceBit), shaderModule.GetUnsignedConstant(1)), destinationBit);

    private static uint Standard64KB3DOffset(ShaderModuleContext shaderModule, uint x, uint y, uint z, uint bytes)
    {
        uint[] s = bytes switch { 1 => [4, 4, 4, 5], 2 => [3, 4, 4, 4], 4 => [3, 3, 4, 4], 8 => [3, 3, 3, 4], _ => [2, 3, 3, 3] };
        var offset = XorTerms(shaderModule, x, y, z, Standard3DTerms(bytes));
        return shaderModule.Xor(shaderModule.Xor(shaderModule.Xor(shaderModule.Xor(offset, Bit(shaderModule, x, s[0], 12)), Bit(shaderModule, z, s[1], 13)), Bit(shaderModule, y, s[2], 14)), Bit(shaderModule, x, s[3], 15));
    }

    private static uint ZSpread(ShaderModuleContext shaderModule, uint z) =>
        shaderModule.Xor(shaderModule.Xor(shaderModule.Xor(shaderModule.ShiftLeft(shaderModule.And(z, shaderModule.GetUnsignedConstant(8)), 5), shaderModule.ShiftLeft(shaderModule.And(z, shaderModule.GetUnsignedConstant(4)), 7)), shaderModule.ShiftLeft(shaderModule.And(z, shaderModule.GetUnsignedConstant(2)), 9)), shaderModule.ShiftLeft(shaderModule.And(z, shaderModule.GetUnsignedConstant(1)), 11));

    private static ((uint Width, uint Height, uint Depth) Extent, uint BlockBytes) Shape(TilerBlockShape shape, uint bytes) => shape switch
    {
        TilerBlockShape.Standard256B => (ThinExtent(256, bytes, 16, 8, 4), 256),
        TilerBlockShape.Standard4KB => (ThinExtent(4096, bytes, 64, 32, 16), 4096),
        TilerBlockShape.Standard4KB3D => (Thick4KBExtent(bytes), 4096),
        TilerBlockShape.Standard64KB or TilerBlockShape.Prt64KB or TilerBlockShape.RenderTarget64KB => (ThinExtent(65536, bytes, 256, 128, 64), 65536),
        TilerBlockShape.Standard64KB3D or TilerBlockShape.Prt64KB3D => (Thick64KBExtent(bytes), 65536),
        TilerBlockShape.Depth64KB => (ThinExtent(65536, bytes, 256, 128, 128), 65536),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static uint BlockOffset(ShaderModuleContext shaderModule, TilerBlockShape shape, uint bytes, uint x, uint y, uint z)
    {
        switch (shape)
        {
            case TilerBlockShape.Standard256B:
                return shaderModule.And(XorTerms(shaderModule, x, y, z, StandardTerms(bytes)), shaderModule.GetUnsignedConstant(0xff));
            case TilerBlockShape.Standard4KB:
                return XorTerms(shaderModule, x, y, z, StandardTerms(bytes));
            case TilerBlockShape.Standard4KB3D:
                return XorTerms(shaderModule, x, y, z, Standard3DTerms(bytes));
            case TilerBlockShape.Standard64KB:
                return XorTerms(shaderModule, x, y, z, Standard64KBTerms(bytes));
            case TilerBlockShape.Standard64KB3D:
                return Standard64KB3DOffset(shaderModule, x, y, z, bytes);
            case TilerBlockShape.Prt64KB:
            {
                uint[] s = bytes switch { 1 => [7, 7, 6, 6], 2 => [7, 6, 6, 5], 4 => [6, 6, 5, 5], 8 => [6, 5, 5, 4], _ => [5, 5, 4, 4] };
                var delta = shaderModule.Xor(shaderModule.Xor(shaderModule.Xor(Bit(shaderModule, x, s[0], 8), Bit(shaderModule, y, s[1], 9)), Bit(shaderModule, x, s[2], 10)), Bit(shaderModule, y, s[3], 11));
                return shaderModule.Xor(XorTerms(shaderModule, x, y, z, Standard64KBTerms(bytes)), delta);
            }
            case TilerBlockShape.Prt64KB3D:
            {
                uint[] s = bytes switch { 1 => [4, 5, 4, 4], 2 => [4, 4, 3, 4], 4 => [4, 4, 3, 3], 8 => [3, 4, 3, 3], _ => [3, 3, 2, 3] };
                var delta = shaderModule.Xor(shaderModule.Xor(shaderModule.Xor(Bit(shaderModule, y, s[0], 10), Bit(shaderModule, x, s[1], 10)), Bit(shaderModule, x, s[2], 11)), Bit(shaderModule, z, s[3], 11));
                return shaderModule.Xor(Standard64KB3DOffset(shaderModule, x, y, z, bytes), delta);
            }
            case TilerBlockShape.RenderTarget64KB:
                return shaderModule.Xor(XorTerms(shaderModule, x, y, z, RenderTargetTerms(bytes)), ZSpread(shaderModule, z));
            case TilerBlockShape.Depth64KB:
                return shaderModule.Xor(ZSpread(shaderModule, z), XorTerms(shaderModule, x, y, z, DepthTerms(bytes)));
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    private static void CopyElement(ShaderModuleContext shaderModule, uint source, uint destination, uint bytes)
    {
        if (bytes >= 4)
        {
            for (uint wordOffset = 0; wordOffset < bytes; wordOffset += 4)
            {
                var value = shaderModule.LoadWord(shaderModule.InputBufferVariable, shaderModule.ShiftRight(shaderModule.Add(source, shaderModule.GetUnsignedConstant(wordOffset)), 2));
                shaderModule.Builder.AddStatement(SpirvOp.Store, shaderModule.GetWordPointer(shaderModule.OutputBufferVariable, shaderModule.ShiftRight(shaderModule.Add(destination, shaderModule.GetUnsignedConstant(wordOffset)), 2)), value);
            }

            return;
        }

        var mask = shaderModule.GetUnsignedConstant(bytes == 1 ? 0xffu : 0xffffu);
        var loaded = shaderModule.LoadWord(shaderModule.InputBufferVariable, shaderModule.ShiftRight(source, 2));
        var sourceShift = shaderModule.Multiply(shaderModule.And(source, shaderModule.GetUnsignedConstant(3)), shaderModule.GetUnsignedConstant(8));
        var value2 = shaderModule.And(shaderModule.Builder.AddInstruction(SpirvOp.ShiftRightLogical, shaderModule.UintType, loaded, sourceShift), mask);
        var shift = shaderModule.Multiply(shaderModule.And(destination, shaderModule.GetUnsignedConstant(3)), shaderModule.GetUnsignedConstant(8));
        var target = shaderModule.GetWordPointer(shaderModule.OutputBufferVariable, shaderModule.ShiftRight(destination, 2));
        var clearMask = shaderModule.Builder.AddInstruction(SpirvOp.Not, shaderModule.UintType, shaderModule.Builder.AddInstruction(SpirvOp.ShiftLeftLogical, shaderModule.UintType, mask, shift));
        shaderModule.Builder.AddInstruction(SpirvOp.AtomicAnd, shaderModule.UintType, target, shaderModule.GetUnsignedConstant(1), shaderModule.GetUnsignedConstant(0), clearMask);
        shaderModule.Builder.AddInstruction(SpirvOp.AtomicOr, shaderModule.UintType, target, shaderModule.GetUnsignedConstant(1), shaderModule.GetUnsignedConstant(0), shaderModule.Builder.AddInstruction(SpirvOp.ShiftLeftLogical, shaderModule.UintType, value2, shift));
    }

    // Create one module for each block shape, element size and transfer direction.
    public static byte[] CreateBlockCopy(TilerBlockShape shape, uint elementBytes, bool toTiled)
    {
        if (elementBytes is not (1 or 2 or 4 or 8 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(elementBytes));
        }

        var shaderModule = new ShaderModuleContext(pushConstants: false);
        var builder = shaderModule.Builder;
        var (extent, blockBytes) = Shape(shape, elementBytes);
        var invocationVar = shaderModule.Invocation();
        var functionType = builder.TypeFunction(shaderModule.VoidType);
        var main = builder.BeginFunction(shaderModule.VoidType, functionType);
        builder.AddName(main, "main");
        builder.AddLabel();

        var invocation = builder.AddInstruction(SpirvOp.Load, shaderModule.UnsignedVector3Type, invocationVar);
        var px = builder.AddInstruction(SpirvOp.CompositeExtract, shaderModule.UintType, invocation, 0);
        var py = builder.AddInstruction(SpirvOp.CompositeExtract, shaderModule.UintType, invocation, 1);
        var pz = builder.AddInstruction(SpirvOp.CompositeExtract, shaderModule.UintType, invocation, 2);
        var outsideX = builder.AddInstruction(SpirvOp.UGreaterThanEqual, shaderModule.BooleanType, px, shaderModule.LoadArgument(2));
        var outsideY = builder.AddInstruction(SpirvOp.UGreaterThanEqual, shaderModule.BooleanType, py, shaderModule.LoadArgument(3));
        var outsideZ = builder.AddInstruction(SpirvOp.UGreaterThanEqual, shaderModule.BooleanType, pz, shaderModule.LoadArgument(4));
        var outside = builder.AddInstruction(SpirvOp.LogicalOr, shaderModule.BooleanType, builder.AddInstruction(SpirvOp.LogicalOr, shaderModule.BooleanType, outsideX, outsideY), outsideZ);
        shaderModule.ReturnWhen(outside);

        var tail = builder.AddInstruction(SpirvOp.INotEqual, shaderModule.BooleanType, shaderModule.LoadArgument(12), shaderModule.GetUnsignedConstant(0));
        var swizzleX = builder.AddInstruction(SpirvOp.Select, shaderModule.UintType, tail, shaderModule.Add(px, shaderModule.LoadArgument(10)), px);
        var swizzleY = builder.AddInstruction(SpirvOp.Select, shaderModule.UintType, tail, shaderModule.Add(py, shaderModule.LoadArgument(11)), py);
        var blockX = builder.AddInstruction(SpirvOp.Select, shaderModule.UintType, tail, shaderModule.GetUnsignedConstant(0), shaderModule.Divide(px, shaderModule.GetUnsignedConstant(extent.Width)));
        var blockY = builder.AddInstruction(SpirvOp.Select, shaderModule.UintType, tail, shaderModule.GetUnsignedConstant(0), shaderModule.Divide(py, shaderModule.GetUnsignedConstant(extent.Height)));
        var blockZ = builder.AddInstruction(SpirvOp.Select, shaderModule.UintType, tail, shaderModule.GetUnsignedConstant(0), shaderModule.Divide(pz, shaderModule.GetUnsignedConstant(extent.Depth)));
        var blockIndex = shaderModule.Add(shaderModule.Add(shaderModule.Multiply(blockZ, shaderModule.LoadArgument(9)), shaderModule.Multiply(blockY, shaderModule.LoadArgument(8))), blockX);
        var offset = BlockOffset(shaderModule, shape, elementBytes, swizzleX, swizzleY, shaderModule.Add(pz, shaderModule.LoadArgument(5)));
        var tiled = shaderModule.Add(shaderModule.Multiply(blockIndex, shaderModule.GetUnsignedConstant(blockBytes)), offset);
        var linear = shaderModule.Add(shaderModule.Add(shaderModule.Multiply(pz, shaderModule.LoadArgument(7)), shaderModule.Multiply(py, shaderModule.LoadArgument(6))), shaderModule.Multiply(px, shaderModule.GetUnsignedConstant(elementBytes)));
        var sourceBase = shaderModule.LoadArgument(0);
        var destinationBase = shaderModule.LoadArgument(1);
        if (toTiled)
        {
            CopyElement(shaderModule, shaderModule.Add(sourceBase, linear), shaderModule.Add(destinationBase, tiled), elementBytes);
        }
        else
        {
            CopyElement(shaderModule, shaderModule.Add(sourceBase, tiled), shaderModule.Add(destinationBase, linear), elementBytes);
        }

        builder.AddStatement(SpirvOp.Return);
        builder.EndFunction();
        return shaderModule.Finish(main, 8, 8);
    }

    private static (ShaderModuleContext ShaderModuleContext, uint Main, uint X, uint Y) BeginConversion()
    {
        var shaderModule = new ShaderModuleContext(pushConstants: true);
        var builder = shaderModule.Builder;
        var invocationVar = shaderModule.Invocation();
        var functionType = builder.TypeFunction(shaderModule.VoidType);
        var main = builder.BeginFunction(shaderModule.VoidType, functionType);
        builder.AddName(main, "main");
        builder.AddLabel();
        var invocation = builder.AddInstruction(SpirvOp.Load, shaderModule.UnsignedVector3Type, invocationVar);
        var x = builder.AddInstruction(SpirvOp.CompositeExtract, shaderModule.UintType, invocation, 0);
        var y = builder.AddInstruction(SpirvOp.CompositeExtract, shaderModule.UintType, invocation, 1);
        return (shaderModule, main, x, y);
    }

    private static void ReturnOutsideRectangle(ShaderModuleContext shaderModule, uint x, uint y)
    {
        var builder = shaderModule.Builder;
        var outsideX = builder.AddInstruction(SpirvOp.UGreaterThanEqual, shaderModule.BooleanType, x, shaderModule.LoadArgument(2));
        var outsideY = builder.AddInstruction(SpirvOp.UGreaterThanEqual, shaderModule.BooleanType, y, shaderModule.LoadArgument(3));
        shaderModule.ReturnWhen(builder.AddInstruction(SpirvOp.LogicalOr, shaderModule.BooleanType, outsideX, outsideY));
    }

    // Widens 16-bit depth to 24-bit fixed point or 32-bit float, one dword per element.
    // The float paths use reciprocal multiplication to preserve rounding.
    public static byte[] CreateDepthWiden(bool d32)
    {
        var (shaderModule, main, x, y) = BeginConversion();
        var builder = shaderModule.Builder;
        ReturnOutsideRectangle(shaderModule, x, y);
        var source = shaderModule.Add(shaderModule.Add(shaderModule.LoadArgument(0), shaderModule.Multiply(y, shaderModule.LoadArgument(6))), shaderModule.Multiply(x, shaderModule.GetUnsignedConstant(2)));
        var packed = shaderModule.LoadWord(shaderModule.InputBufferVariable, shaderModule.ShiftRight(source, 2));
        var shift = shaderModule.Multiply(shaderModule.And(source, shaderModule.GetUnsignedConstant(2)), shaderModule.GetUnsignedConstant(8));
        var value = shaderModule.And(builder.AddInstruction(SpirvOp.ShiftRightLogical, shaderModule.UintType, packed, shift), shaderModule.GetUnsignedConstant(0xffff));
        uint encoded;
        if (d32)
        {
            var asFloat = builder.AddInstruction(SpirvOp.ConvertUToF, shaderModule.FloatType, value);
            var normalized = builder.AddInstruction(SpirvOp.FMul, shaderModule.FloatType, asFloat, builder.ConstantFloat(shaderModule.FloatType, 1.0f / 65535.0f));
            encoded = builder.AddInstruction(SpirvOp.Bitcast, shaderModule.UintType, normalized);
        }
        else
        {
            encoded = shaderModule.Add(shaderModule.Multiply(value, shaderModule.GetUnsignedConstant(256)), shaderModule.Divide(shaderModule.Add(value, shaderModule.GetUnsignedConstant(128)), shaderModule.GetUnsignedConstant(257)));
        }

        var destination = shaderModule.Add(shaderModule.Add(shaderModule.LoadArgument(1), shaderModule.Multiply(y, shaderModule.LoadArgument(7))), shaderModule.Multiply(x, shaderModule.GetUnsignedConstant(4)));
        builder.AddStatement(SpirvOp.Store, shaderModule.GetWordPointer(shaderModule.OutputBufferVariable, shaderModule.ShiftRight(destination, 2)), encoded);
        builder.AddStatement(SpirvOp.Return);
        builder.EndFunction();
        return shaderModule.Finish(main, 64, 1);
    }

    // Narrows 24-bit fixed point or 32-bit float depth to 16 bits with a masked atomic write.
    public static byte[] CreateDepthNarrow(bool d32)
    {
        var (shaderModule, main, x, y) = BeginConversion();
        var builder = shaderModule.Builder;
        ReturnOutsideRectangle(shaderModule, x, y);
        var source = shaderModule.Add(shaderModule.Add(shaderModule.LoadArgument(0), shaderModule.Multiply(y, shaderModule.LoadArgument(6))), shaderModule.Multiply(x, shaderModule.GetUnsignedConstant(4)));
        var destination = shaderModule.Add(shaderModule.Add(shaderModule.LoadArgument(1), shaderModule.Multiply(y, shaderModule.LoadArgument(7))), shaderModule.Multiply(x, shaderModule.GetUnsignedConstant(2)));
        var shift = shaderModule.Multiply(shaderModule.And(destination, shaderModule.GetUnsignedConstant(2)), shaderModule.GetUnsignedConstant(8));
        var mask = builder.AddInstruction(SpirvOp.ShiftLeftLogical, shaderModule.UintType, shaderModule.GetUnsignedConstant(0xffff), shift);
        var word = shaderModule.GetWordPointer(shaderModule.OutputBufferVariable, shaderModule.ShiftRight(destination, 2));
        builder.AddInstruction(SpirvOp.AtomicAnd, shaderModule.UintType, word, shaderModule.GetUnsignedConstant(1), shaderModule.GetUnsignedConstant(0), builder.AddInstruction(SpirvOp.Not, shaderModule.UintType, mask));
        var raw = shaderModule.LoadWord(shaderModule.InputBufferVariable, shaderModule.ShiftRight(source, 2));
        uint normalized;
        if (d32)
        {
            var asFloat = builder.AddInstruction(SpirvOp.Bitcast, shaderModule.FloatType, raw);
            normalized = builder.AddInstruction(SpirvOp.ExtInst, shaderModule.FloatType, shaderModule.GlslInstructionSet, 43, asFloat, builder.ConstantFloat(shaderModule.FloatType, 0.0f), builder.ConstantFloat(shaderModule.FloatType, 1.0f));
        }
        else
        {
            var fixedPoint = builder.AddInstruction(SpirvOp.ConvertUToF, shaderModule.FloatType, shaderModule.And(raw, shaderModule.GetUnsignedConstant(0x00ffffff)));
            normalized = builder.AddInstruction(SpirvOp.FMul, shaderModule.FloatType, fixedPoint, builder.ConstantFloat(shaderModule.FloatType, 1.0f / 16777215.0f));
        }

        var scaled = builder.AddInstruction(SpirvOp.FMul, shaderModule.FloatType, normalized, builder.ConstantFloat(shaderModule.FloatType, 65535.0f));
        var rounded = builder.AddInstruction(SpirvOp.ExtInst, shaderModule.FloatType, shaderModule.GlslInstructionSet, 1, scaled);
        var decoded = builder.AddInstruction(SpirvOp.ConvertFToU, shaderModule.UintType, rounded);
        builder.AddInstruction(SpirvOp.AtomicOr, shaderModule.UintType, word, shaderModule.GetUnsignedConstant(1), shaderModule.GetUnsignedConstant(0), builder.AddInstruction(SpirvOp.ShiftLeftLogical, shaderModule.UintType, decoded, shift));
        builder.AddStatement(SpirvOp.Return);
        builder.EndFunction();
        return shaderModule.Finish(main, 64, 1);
    }

    // Swaps the 16-bit blue and red channels of BGRA16 pixels; the width argument counts pixels.
    public static byte[] CreateBgra16Swap()
    {
        var (shaderModule, main, x, _) = BeginConversion();
        var builder = shaderModule.Builder;
        shaderModule.ReturnWhen(builder.AddInstruction(SpirvOp.UGreaterThanEqual, shaderModule.BooleanType, x, shaderModule.LoadArgument(2)));
        var source = shaderModule.Add(shaderModule.ShiftRight(shaderModule.LoadArgument(0), 2), shaderModule.Multiply(x, shaderModule.GetUnsignedConstant(2)));
        var destination = shaderModule.Add(shaderModule.ShiftRight(shaderModule.LoadArgument(1), 2), shaderModule.Multiply(x, shaderModule.GetUnsignedConstant(2)));
        var bg = shaderModule.LoadWord(shaderModule.InputBufferVariable, source);
        var ra = shaderModule.LoadWord(shaderModule.InputBufferVariable, shaderModule.Add(source, shaderModule.GetUnsignedConstant(1)));
        var low = shaderModule.Or(shaderModule.And(ra, shaderModule.GetUnsignedConstant(0x0000ffff)), shaderModule.And(bg, shaderModule.GetUnsignedConstant(0xffff0000)));
        var high = shaderModule.Or(shaderModule.And(bg, shaderModule.GetUnsignedConstant(0x0000ffff)), shaderModule.And(ra, shaderModule.GetUnsignedConstant(0xffff0000)));
        builder.AddStatement(SpirvOp.Store, shaderModule.GetWordPointer(shaderModule.OutputBufferVariable, destination), low);
        builder.AddStatement(SpirvOp.Store, shaderModule.GetWordPointer(shaderModule.OutputBufferVariable, shaderModule.Add(destination, shaderModule.GetUnsignedConstant(1))), high);
        builder.AddStatement(SpirvOp.Return);
        builder.EndFunction();
        return shaderModule.Finish(main, 64, 1);
    }
}
