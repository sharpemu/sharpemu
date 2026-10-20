// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        // ---- runtime descriptors ----
        //
        // An access marked MemoryAccessInfo.RuntimeDescriptor reads its T# and S# words from the scalar
        // registers, as the hardware does, and asks the host's table (RuntimeDescriptorTable) which slot
        // of the persistent descriptor heap holds a view or sampler for those words. The heap is a
        // second descriptor set of partially bound, update-after-bind arrays, so a draw binds nothing
        // per image however many a material table holds.

        private uint _runtimeDescriptorTable;
        private uint _runtimeDescriptorMisses;
        private uint _runtimeSamplerArray;
        private uint _runtimeSamplerPointer;
        private uint _runtimeImageNumericClassTable;
        private readonly Dictionary<(ImageDimension Dimension, ImageNumericClass NumericClass), RuntimeImageArray> _runtimeImageArrays = [];

        // One shared lookup function per table (image, sampler), emitted after the program.
        private readonly uint[] _runtimeLookupFunctions = new uint[2];

        private readonly record struct RuntimeImageArray(
            uint Variable,
            uint ImageType,
            uint ElementPointer,
            uint ComponentType,
            uint VectorType,
            ImageComponentKind Kind,
            SpirvImageDim Dimension,
            bool Arrayed);

        private bool TryEmitRuntimeDescriptorImage(Gen5ShaderInstruction instruction, Gen5ImageControl image, MemoryAccessInfo entry, out string error)
        {
            if (_runtimeDescriptorTable == 0 || _runtimeDescriptorMisses == 0)
            {
                error = "runtime image descriptors without their table bindings";
                return false;
            }

            var imageKey = new uint[1 + RuntimeDescriptorTable.ImageWordCount];
            for (uint word = 0; word < RuntimeDescriptorTable.ImageWordCount; word++)
            {
                // A 128-bit descriptor has four words; the rest of the key is zero.
                imageKey[1 + word] = entry.ImageR128 && word >= 4 ? UInt(0) : LoadS(image.ScalarResource + word);
            }

            _module.AddCapability(SpirvCapability.ShaderNonUniform);
            _module.AddCapability(SpirvCapability.SampledImageArrayNonUniformIndexing);
            if (!entry.NeedsSampler)
            {
                // An image load needs a view of the numeric type the descriptor's format implies, so
                // the class is part of the key: an R32_UINT and an R32_SINT view of one image must not
                // alias each other in the table.
                var numericClass = RuntimeImageNumericClass(imageKey[2]);
                imageKey[0] = BitwiseOr(
                    UInt(RuntimeDescriptorTable.ViewClass(entry.ImageDimension, ImageNumericClass.Float, entry.ImageR128)),
                    ShiftLeftLogical(numericClass, UInt(RuntimeDescriptorTable.ViewClassNumericShift)));
                var runtimeImageSlot = LookupRuntimeDescriptor(imageKey, image: true);
                _module.AddDecoration(runtimeImageSlot, SpirvDecoration.NonUniform);

                // An element of the heap has one numeric view. Fetch only through the array declared
                // for that view rather than through the others, which would be invalid. A format the
                // table does not classify uses the float view and is never in the table.
                string? branchError = null;
                foreach (var candidate in new[] { ImageNumericClass.Float, ImageNumericClass.Uint, ImageNumericClass.Sint })
                {
                    var code = RuntimeDescriptorTable.NumericClassCode(candidate);
                    EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, numericClass, UInt(code)), () =>
                        branchError ??= EmitRuntimeImageFetch(instruction, image, entry, runtimeImageSlot, candidate));
                }

                error = branchError ?? string.Empty;
                return branchError is null;
            }

            var view = RuntimeImageArrayOf(entry.ImageDimension, ImageNumericClass.Float);
            imageKey[0] = UInt(RuntimeDescriptorTable.ViewClass(entry.ImageDimension, ImageNumericClass.Float, entry.ImageR128));
            var samplerKey = new uint[RuntimeDescriptorTable.SamplerWordCount];
            for (uint word = 0; word < samplerKey.Length; word++)
            {
                samplerKey[word] = LoadS(image.ScalarSampler + word);
            }

            if ((entry.ImageSampleFlags & ImageSampleFlags.Adjust) != 0)
            {
                // The adjusted forms carry their adjustment in reserved sampler bits; the sampler ignores them.
                samplerKey[3] = BitwiseAnd(samplerKey[3], UInt(~DescriptorConstants.SamplerDword3ReservedMask));
            }

            var imageSlot = LookupRuntimeDescriptor(imageKey, image: true);
            var samplerSlot = LookupRuntimeDescriptor(samplerKey, image: false);

            var imagePointer = _module.AddInstruction(SpirvOp.AccessChain, view.ElementPointer, view.Variable, imageSlot);
            var imageValue = Load(view.ImageType, imagePointer);
            var samplerArray = RuntimeSamplerArray();
            var samplerPointer = _module.AddInstruction(SpirvOp.AccessChain, _runtimeSamplerPointer, samplerArray, samplerSlot);
            var sampler = Load(_samplerType, samplerPointer);
            var objectType = _module.TypeSampledImage(view.ImageType);
            var imageObject = _module.AddInstruction(SpirvOp.SampledImage, objectType, imageValue, sampler);
            foreach (var value in new[] { imageSlot, imagePointer, imageValue, samplerSlot, samplerPointer, sampler, imageObject })
            {
                _module.AddDecoration(value, SpirvDecoration.NonUniform);
            }

            // The host's view applies the descriptor's swizzle and format.
            var resource = new SpirvImageResource(
                imagePointer,
                view.ImageType,
                objectType,
                view.ComponentType,
                view.VectorType,
                view.Kind,
                IsStorage: false,
                view.Arrayed,
                Dimension: view.Dimension,
                ShaderSwizzle: DescriptorConstants.IdentityImageSwizzle);
            return EmitImageOperation(instruction, image, resource, imageObject, DescriptorConstants.IdentityImageSwizzle, UInt(0), out error);
        }

        private string? EmitRuntimeImageFetch(Gen5ShaderInstruction instruction, Gen5ImageControl image,
            MemoryAccessInfo entry, uint imageSlot, ImageNumericClass numericClass)
        {
            var view = RuntimeImageArrayOf(entry.ImageDimension, numericClass);
            var imagePointer = _module.AddInstruction(SpirvOp.AccessChain, view.ElementPointer, view.Variable, imageSlot);
            var imageValue = Load(view.ImageType, imagePointer);
            _module.AddDecoration(imagePointer, SpirvDecoration.NonUniform);
            _module.AddDecoration(imageValue, SpirvDecoration.NonUniform);
            var resource = new SpirvImageResource(
                imagePointer,
                view.ImageType,
                view.ImageType,
                view.ComponentType,
                view.VectorType,
                view.Kind,
                IsStorage: false,
                view.Arrayed,
                Dimension: view.Dimension,
                ShaderSwizzle: DescriptorConstants.IdentityImageSwizzle);
            var mipLevel = instruction.Opcode == "ImageLoadMip"
                ? LoadImageIntegerAddress(image, (int)ImageCoordinateComponentCount(resource))
                : UInt(0);
            return EmitImageOperation(instruction, image, resource, imageValue,
                DescriptorConstants.IdentityImageSwizzle, mipLevel, out var error) ? null : error;
        }

        // The heap's sampled images of one dimension and numeric view, declared once per module. All of
        // them alias the one heap binding; a slot only ever holds a view of the class it was made for.
        private RuntimeImageArray RuntimeImageArrayOf(ImageDimension dimension, ImageNumericClass numericClass)
        {
            var key = (dimension, numericClass);
            if (_runtimeImageArrays.TryGetValue(key, out var declared))
            {
                return declared;
            }

            var spirvDimension = dimension switch
            {
                ImageDimension.Dim1D or ImageDimension.Dim1DArray => SpirvImageDim.Dim1D,
                ImageDimension.Dim3D => SpirvImageDim.Dim3D,
                _ => SpirvImageDim.Dim2D,
            };
            if (spirvDimension == SpirvImageDim.Dim1D)
            {
                _module.AddCapability(SpirvCapability.Sampled1D);
            }

            var arrayed = dimension is ImageDimension.Dim1DArray or ImageDimension.Dim2DArray;
            var kind = numericClass switch
            {
                ImageNumericClass.Uint => ImageComponentKind.Uint,
                ImageNumericClass.Sint => ImageComponentKind.Sint,
                _ => ImageComponentKind.Float,
            };
            var componentType = kind switch
            {
                ImageComponentKind.Uint => _uintType,
                ImageComponentKind.Sint => _intType,
                _ => _floatType,
            };
            var vectorType = _module.TypeVector(componentType, 4);
            var imageType = _module.TypeImage(componentType, spirvDimension, depth: false, arrayed, multisampled: false, sampled: 1u, SpirvImageFormat.Unknown);
            _module.AddCapability(SpirvCapability.RuntimeDescriptorArray);
            var arrayType = _module.TypeRuntimeArray(imageType);
            var elementPointer = _module.TypePointer(SpirvStorageClass.UniformConstant, imageType);
            var variable = _module.AddGlobalVariable(_module.TypePointer(SpirvStorageClass.UniformConstant, arrayType), SpirvStorageClass.UniformConstant);
            _module.AddName(variable, $"runtimeImages{dimension}{numericClass}");
            _module.AddDecoration(variable, SpirvDecoration.DescriptorSet, RuntimeDescriptorTable.HeapDescriptorSet);
            _module.AddDecoration(variable, SpirvDecoration.Binding, RuntimeDescriptorTable.HeapSampledImageBinding);
            _interfaces.Add(variable);
            declared = new RuntimeImageArray(variable, imageType, elementPointer, componentType, vectorType, kind, spirvDimension, arrayed);
            _runtimeImageArrays.Add(key, declared);
            return declared;
        }

        // The numeric view an image load needs, from the descriptor's format field, as float = 0,
        // uint = 1, sint = 2. It uses the classification the host applies when it makes the view.
        private uint RuntimeImageNumericClass(uint descriptorWord1)
        {
            if (_runtimeImageNumericClassTable == 0)
            {
                var entries = new uint[GuestImageFormat.MaxFormat + 1];
                for (uint format = 0; format < entries.Length; format++)
                {
                    entries[format] = RuntimeDescriptorTable.NumericClassCode(GuestImageFormat.SampledNumericClass(format));
                }

                var type = _module.TypeArray(_uintType, (uint)entries.Length);
                _runtimeImageNumericClassTable = _module.AddGlobalVariable(
                    _module.TypePointer(SpirvStorageClass.Private, type), SpirvStorageClass.Private,
                    _module.ConstantComposite(type, entries.Select(UInt).ToArray()));
                _module.AddName(_runtimeImageNumericClassTable, "runtimeImageNumericClasses");
                _interfaces.Add(_runtimeImageNumericClassTable);
            }

            var descriptorFormat = BitwiseAnd(ShiftRightLogical(descriptorWord1, UInt(20)), UInt(0x1ff));
            var inRange = _module.AddInstruction(SpirvOp.ULessThan, _boolType, descriptorFormat, UInt(GuestImageFormat.MaxFormat + 1));
            var safeFormat = _module.AddInstruction(SpirvOp.Select, _uintType, inRange, descriptorFormat, UInt(0));
            return Load(_uintType, _module.AddInstruction(SpirvOp.AccessChain, _privateUintPointer,
                _runtimeImageNumericClassTable, safeFormat));
        }

        private uint RuntimeSamplerArray()
        {
            if (_runtimeSamplerArray != 0)
            {
                return _runtimeSamplerArray;
            }

            _samplerType = _module.TypeSampler();
            _runtimeSamplerPointer = _module.TypePointer(SpirvStorageClass.UniformConstant, _samplerType);
            _module.AddCapability(SpirvCapability.RuntimeDescriptorArray);
            var arrayType = _module.TypeRuntimeArray(_samplerType);
            _runtimeSamplerArray = _module.AddGlobalVariable(_module.TypePointer(SpirvStorageClass.UniformConstant, arrayType), SpirvStorageClass.UniformConstant);
            _module.AddName(_runtimeSamplerArray, "runtimeSamplers");
            _module.AddDecoration(_runtimeSamplerArray, SpirvDecoration.DescriptorSet, RuntimeDescriptorTable.HeapDescriptorSet);
            _module.AddDecoration(_runtimeSamplerArray, SpirvDecoration.Binding, RuntimeDescriptorTable.HeapSamplerBinding);
            _interfaces.Add(_runtimeSamplerArray);
            return _runtimeSamplerArray;
        }

        // The heap slot of a descriptor key. Every access calls one shared function: inlined, the hash
        // and the probes of each lookup would make a shader with many runtime accesses several times
        // larger and its pipeline many times slower to compile.
        //
        // The function carries no inlining hint (SpirvFunctionControl has no DontInline): on NVIDIA a
        // DontInline lookup returned the slot of an earlier call when the calls were repeated in a
        // uniform loop, so a color ramp sampled the base texture.
        private uint LookupRuntimeDescriptor(uint[] key, bool image)
        {
            var kind = image ? 0 : 1;
            if (_runtimeLookupFunctions[kind] == 0)
            {
                _runtimeLookupFunctions[kind] = _module.AllocateId();
            }

            var active = _module.AddInstruction(SpirvOp.Select, _uintType, Load(_boolType, _exec), UInt(1), UInt(0));
            return _module.AddInstruction(SpirvOp.FunctionCall, _uintType, [_runtimeLookupFunctions[kind], .. key, active]);
        }

        private void EmitRuntimeLookupFunctions()
        {
            for (var kind = 0; kind < _runtimeLookupFunctions.Length; kind++)
            {
                var function = _runtimeLookupFunctions[kind];
                if (function == 0)
                {
                    continue;
                }

                var image = kind == 0;
                var keyWords = image ? 1 + (int)RuntimeDescriptorTable.ImageWordCount : (int)RuntimeDescriptorTable.SamplerWordCount;
                var type = _module.TypeFunction(_uintType, [.. Enumerable.Repeat(_uintType, keyWords + 1)]);
                _module.BeginFunction(_uintType, type, function, SpirvFunctionControl.None);
                _module.AddName(function, image ? "lookupRuntimeImage" : "lookupRuntimeSampler");
                var key = new uint[keyWords];
                for (var word = 0; word < keyWords; word++)
                {
                    key[word] = _module.AddFunctionParameter(_uintType);
                }

                var active = _module.AddFunctionParameter(_uintType);
                _module.AddLabel();
                _module.AddStatement(SpirvOp.ReturnValue, EmitRuntimeDescriptorProbe(key, image,
                    _module.AddInstruction(SpirvOp.INotEqual, _boolType, active, UInt(0))));
                _module.EndFunction();
            }
        }

        // A bounded linear probe of the host table; slot 0 and a recorded miss when the table does not
        // hold the key. The host places a key in the first empty entry of its probe sequence and never
        // removes one, so the probe stops at the key or at the first empty entry.
        //
        // The probes are one loop body rather than ProbeCount unrolled copies: the driver inlines this
        // function at every access (it must not be DontInline, see LookupRuntimeDescriptor), and eight
        // copies of a ten-word compare per image access made a pixel shader with a few hundred samples
        // megabytes of SPIR-V and its pipeline tens of seconds to compile.
        private uint EmitRuntimeDescriptorProbe(uint[] key, bool image, uint active)
        {
            var table = _runtimeDescriptorTable;
            var tableLength = StorageBlockLength(table);
            uint LoadTableWord(uint dwordIndex)
            {
                var inRange = _module.AddInstruction(SpirvOp.ULessThan, _boolType, dwordIndex, tableLength);
                var safeIndex = _module.AddInstruction(SpirvOp.Select, _uintType, inRange, dwordIndex, UInt(0));
                var value = Load(_uintType, BlockWordPointer(table, safeIndex));
                return _module.AddInstruction(SpirvOp.Select, _uintType, inRange, value, UInt(0));
            }

            var capacity = LoadTableWord(UInt(image ? RuntimeDescriptorTable.ImageCapacityDword : RuntimeDescriptorTable.SamplerCapacityDword));
            var entries = LoadTableWord(UInt(image ? RuntimeDescriptorTable.ImageEntriesDword : RuntimeDescriptorTable.SamplerEntriesDword));
            var entryDwords = image ? RuntimeDescriptorTable.ImageEntryDwords : RuntimeDescriptorTable.SamplerEntryDwords;
            var mask = _module.AddInstruction(SpirvOp.ISub, _uintType, capacity, UInt(1));
            var hash = UInt(RuntimeDescriptorTable.HashSeed);
            foreach (var word in key)
            {
                hash = _module.AddInstruction(SpirvOp.IMul, _uintType,
                    _module.AddInstruction(SpirvOp.BitwiseXor, _uintType, hash, word), UInt(RuntimeDescriptorTable.HashPrime));
            }

            var empty = _module.AddInstruction(SpirvOp.IEqual, _boolType, capacity, UInt(0));
            var preheader = _module.AllocateId();
            var header = _module.AllocateId();
            var body = _module.AllocateId();
            var continueLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            var nextProbe = _module.AllocateId();
            var nextSlotPlusOne = _module.AllocateId();
            var nextDone = _module.AllocateId();
            _module.AddStatement(SpirvOp.Branch, preheader);
            _module.AddLabel(preheader);
            _module.AddStatement(SpirvOp.Branch, header);
            _module.AddLabel(header);
            var probe = _module.AddInstruction(SpirvOp.Phi, _uintType, UInt(0), preheader, nextProbe, continueLabel);
            var slotPlusOne = _module.AddInstruction(SpirvOp.Phi, _uintType, UInt(0), preheader, nextSlotPlusOne, continueLabel);
            var done = _module.AddInstruction(SpirvOp.Phi, _boolType, empty, preheader, nextDone, continueLabel);
            var more = LogicalAnd(LogicalNot(done),
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, probe, UInt(RuntimeDescriptorTable.ProbeCount)));
            _module.AddStatement(SpirvOp.LoopMerge, mergeLabel, continueLabel, LoopControlDontUnroll);
            _module.AddStatement(SpirvOp.BranchConditional, more, body, mergeLabel);
            _module.AddLabel(body);
            var index = BitwiseAnd(IAdd(hash, probe), mask);
            var entry = IAdd(entries, _module.AddInstruction(SpirvOp.IMul, _uintType, index, UInt(entryDwords)));
            // One bounds check per entry: an entry the table does not hold whole reads as vacant.
            var inRange = _module.AddInstruction(SpirvOp.ULessThan, _boolType, IAdd(entry, UInt(entryDwords - 1)), tableLength);
            var safeEntry = _module.AddInstruction(SpirvOp.Select, _uintType, inRange, entry, UInt(0));
            var stored = _module.AddInstruction(SpirvOp.Select, _uintType, inRange,
                Load(_uintType, BlockWordPointer(table, safeEntry)), UInt(0));
            var vacant = _module.AddInstruction(SpirvOp.IEqual, _boolType, stored, UInt(0));
            var match = LogicalNot(vacant);
            for (var word = 0; word < key.Length; word++)
            {
                var tableWord = Load(_uintType, BlockWordPointer(table, IAdd(safeEntry, UInt((uint)word + 1))));
                match = LogicalAnd(match, _module.AddInstruction(SpirvOp.IEqual, _boolType, tableWord, key[word]));
            }

            _module.AddStatement(SpirvOp.Select, _uintType, nextSlotPlusOne, match, stored, UInt(0));
            _module.AddStatement(SpirvOp.LogicalOr, _boolType, nextDone, match, vacant);
            _module.AddStatement(SpirvOp.Branch, continueLabel);
            _module.AddLabel(continueLabel);
            _module.AddStatement(SpirvOp.IAdd, _uintType, nextProbe, probe, UInt(1));
            _module.AddStatement(SpirvOp.Branch, header);
            _module.AddLabel(mergeLabel);

            var missing = _module.AddInstruction(SpirvOp.IEqual, _boolType, slotPlusOne, UInt(0));
            // Only an active lane reads its descriptor; an inactive one may hold anything.
            EmitConditional(_module.AddInstruction(SpirvOp.LogicalAnd, _boolType, missing, active),
                () => RecordRuntimeDescriptorMiss(key, image, hash));
            return _module.AddInstruction(SpirvOp.Select, _uintType, missing, UInt(0),
                _module.AddInstruction(SpirvOp.ISub, _uintType, slotPlusOne, UInt(1)));
        }

        // SPIR-V LoopControl: a hint that the loop is not to be unrolled.
        private const uint LoopControlDontUnroll = 0x2;

        private void RecordRuntimeDescriptorMiss(uint[] key, bool image, uint hash)
        {
            var misses = _runtimeDescriptorMisses;
            // Record a key once per buffer: the invocation that sets the key's bit first records it.
            var seenBase = UInt(image ? RuntimeDescriptorTable.ImageSeenOffset : RuntimeDescriptorTable.SamplerSeenOffset);
            var seenWord = IAdd(seenBase, BitwiseAnd(ShiftRightLogical(hash, UInt(5)), UInt(RuntimeDescriptorTable.SeenDwords - 1)));
            var seenBit = ShiftLeftLogical(UInt(1), BitwiseAnd(hash, UInt(31)));
            var previous = _module.AddInstruction(SpirvOp.AtomicOr, _uintType, BlockWordPointer(misses, seenWord), UInt(1), UInt(0), seenBit);
            var first = _module.AddInstruction(SpirvOp.IEqual, _boolType, BitwiseAnd(previous, seenBit), UInt(0));
            EmitConditional(first, () => RecordNewRuntimeDescriptorMiss(key, image));
        }

        private void RecordNewRuntimeDescriptorMiss(uint[] key, bool image)
        {
            var misses = _runtimeDescriptorMisses;
            var counter = BlockWordPointer(misses, UInt(image ? RuntimeDescriptorTable.ImageMissCountDword : RuntimeDescriptorTable.SamplerMissCountDword));
            // Device scope, relaxed: the host only reads the buffer once the submission has completed.
            var record = _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, counter, UInt(1), UInt(0), UInt(1));
            var capacity = image ? RuntimeDescriptorTable.ImageMissCapacity : RuntimeDescriptorTable.SamplerMissCapacity;
            var recordDwords = image ? RuntimeDescriptorTable.ImageMissRecordDwords : RuntimeDescriptorTable.SamplerMissRecordDwords;
            var offset = image ? RuntimeDescriptorTable.ImageMissOffset : RuntimeDescriptorTable.SamplerMissOffset;
            EmitConditional(_module.AddInstruction(SpirvOp.ULessThan, _boolType, record, UInt(capacity)), () =>
            {
                var first = IAdd(UInt(offset), _module.AddInstruction(SpirvOp.IMul, _uintType, record, UInt(recordDwords)));
                // The record is contiguous: its last dword in range puts every one of them in range.
                EmitConditional(IsBlockWordInRange(misses, IAdd(first, UInt((uint)key.Length - 1))), () =>
                {
                    for (var word = 0; word < key.Length; word++)
                    {
                        Store(BlockWordPointer(misses, IAdd(first, UInt((uint)word))), key[word]);
                    }
                });
            });
        }
    }
}
