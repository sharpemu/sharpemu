// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.ShaderCompiler.Vulkan;

public enum SpirvOp : ushort
{
    Nop = 0,
    Name = 5,
    Extension = 10,
    ExtInstImport = 11,
    ExtInst = 12,
    MemoryModel = 14,
    EntryPoint = 15,
    ExecutionMode = 16,
    Capability = 17,
    TypeVoid = 19,
    TypeBool = 20,
    TypeInt = 21,
    TypeFloat = 22,
    TypeVector = 23,
    TypeImage = 25,
    TypeSampler = 26,
    TypeSampledImage = 27,
    TypeArray = 28,
    TypeRuntimeArray = 29,
    TypeStruct = 30,
    TypePointer = 32,
    TypeFunction = 33,
    ConstantTrue = 41,
    ConstantFalse = 42,
    Constant = 43,
    ConstantComposite = 44,
    ConstantNull = 46,
    Function = 54,
    FunctionParameter = 55,
    FunctionEnd = 56,
    FunctionCall = 57,
    Variable = 59,
    ImageTexelPointer = 60,
    Load = 61,
    Store = 62,
    AccessChain = 65,
    ArrayLength = 68,
    Decorate = 71,
    VectorExtractDynamic = 77,
    VectorInsertDynamic = 78,
    VectorShuffle = 79,
    CompositeConstruct = 80,
    CompositeExtract = 81,
    CompositeInsert = 82,
    CopyObject = 83,
    SampledImage = 86,
    ImageSampleImplicitLod = 87,
    ImageSampleExplicitLod = 88,
    ImageSampleDrefImplicitLod = 89,
    ImageSampleDrefExplicitLod = 90,
    ImageFetch = 95,
    ImageGather = 96,
    ImageDrefGather = 97,
    ImageRead = 98,
    ImageWrite = 99,
    Image = 100,
    ImageQuerySizeLod = 103,
    ImageQuerySize = 104,
    ImageQueryLod = 105,
    ImageQueryLevels = 106,
    ImageQuerySamples = 107,
    ConvertFToU = 109,
    ConvertFToS = 110,
    ConvertSToF = 111,
    ConvertUToF = 112,
    UConvert = 113,
    SConvert = 114,
    FConvert = 115,
    Bitcast = 124,
    SNegate = 126,
    FNegate = 127,
    IAdd = 128,
    FAdd = 129,
    ISub = 130,
    FSub = 131,
    IMul = 132,
    FMul = 133,
    UDiv = 134,
    SDiv = 135,
    FDiv = 136,
    UMod = 137,
    SRem = 138,
    SMod = 139,
    FRem = 140,
    FMod = 141,
    IAddCarry = 149,
    ISubBorrow = 150,
    UMulExtended = 151,
    SMulExtended = 152,
    Any = 154,
    All = 155,
    IsNan = 156,
    IsInf = 157,
    LogicalEqual = 164,
    LogicalNotEqual = 165,
    LogicalOr = 166,
    LogicalAnd = 167,
    LogicalNot = 168,
    Select = 169,
    IEqual = 170,
    INotEqual = 171,
    UGreaterThan = 172,
    SGreaterThan = 173,
    UGreaterThanEqual = 174,
    SGreaterThanEqual = 175,
    ULessThan = 176,
    SLessThan = 177,
    ULessThanEqual = 178,
    SLessThanEqual = 179,
    FOrdEqual = 180,
    FUnordEqual = 181,
    FOrdNotEqual = 182,
    FUnordNotEqual = 183,
    FOrdLessThan = 184,
    FUnordLessThan = 185,
    FOrdGreaterThan = 186,
    FUnordGreaterThan = 187,
    FOrdLessThanEqual = 188,
    FUnordLessThanEqual = 189,
    FOrdGreaterThanEqual = 190,
    FUnordGreaterThanEqual = 191,
    ShiftRightLogical = 194,
    ShiftRightArithmetic = 195,
    ShiftLeftLogical = 196,
    BitwiseOr = 197,
    BitwiseXor = 198,
    BitwiseAnd = 199,
    Not = 200,
    BitFieldInsert = 201,
    BitFieldSExtract = 202,
    BitFieldUExtract = 203,
    BitReverse = 204,
    BitCount = 205,
    ControlBarrier = 224,
    MemoryBarrier = 225,
    AtomicExchange = 229,
    AtomicCompareExchange = 230,
    AtomicIIncrement = 232,
    AtomicIDecrement = 233,
    AtomicIAdd = 234,
    AtomicISub = 235,
    AtomicSMin = 236,
    AtomicUMin = 237,
    AtomicSMax = 238,
    AtomicUMax = 239,
    AtomicAnd = 240,
    AtomicOr = 241,
    AtomicXor = 242,
    Phi = 245,
    LoopMerge = 246,
    SelectionMerge = 247,
    Label = 248,
    Branch = 249,
    BranchConditional = 250,
    Switch = 251,
    Kill = 252,
    Return = 253,
    ReturnValue = 254,
    Unreachable = 255,
    GroupNonUniformElect = 333,
    GroupNonUniformAll = 334,
    GroupNonUniformAny = 335,
    GroupNonUniformAllEqual = 336,
    GroupNonUniformBroadcast = 337,
    GroupNonUniformBroadcastFirst = 338,
    GroupNonUniformBallot = 339,
    GroupNonUniformShuffle = 345,
    GroupNonUniformShuffleXor = 346,
    GroupNonUniformShuffleUp = 347,
    GroupNonUniformShuffleDown = 348,
}

public enum SpirvCapability : uint
{
    Shader = 1,
    Float16 = 9,
    Float64 = 10,
    Int64 = 11,
    Int16 = 22,
    ImageGatherExtended = 25,
    StorageImageExtendedFormats = 49,
    ImageQuery = 50,
    StorageImageReadWithoutFormat = 55,
    StorageImageWriteWithoutFormat = 56,
    GroupNonUniform = 61,
    GroupNonUniformVote = 62,
    GroupNonUniformBallot = 64,
    GroupNonUniformShuffle = 65,
    RuntimeDescriptorArray = 5302,
}

public enum SpirvStorageClass : uint
{
    UniformConstant = 0,
    Input = 1,
    Uniform = 2,
    Output = 3,
    Workgroup = 4,
    Private = 6,
    Function = 7,
    PushConstant = 9,
    Image = 11,
    StorageBuffer = 12,
}

public enum SpirvExecutionModel : uint
{
    Vertex = 0,
    Fragment = 4,
    GLCompute = 5,
}

public enum SpirvExecutionMode : uint
{
    OriginUpperLeft = 7,
    DepthReplacing = 12,
    LocalSize = 17,
}

public enum SpirvDecoration : uint
{
    Block = 2,
    ArrayStride = 6,
    BuiltIn = 11,
    NoPerspective = 13,
    Flat = 14,
    Location = 30,
    Binding = 33,
    DescriptorSet = 34,
    Offset = 35,
}

public enum SpirvBuiltIn : uint
{
    Position = 0,
    VertexIndex = 42,
    InstanceIndex = 43,
    FragCoord = 15,
    FrontFacing = 17,
    WorkgroupId = 26,
    LocalInvocationId = 27,
    GlobalInvocationId = 28,
    LocalInvocationIndex = 29,
    SubgroupLocalInvocationId = 41,
}

public enum SpirvImageDim : uint
{
    Dim1D = 0,
    Dim2D = 1,
    Dim3D = 2,
    Cube = 3,
    Buffer = 5,
}

public enum SpirvImageFormat : uint
{
    Unknown = 0,
    Rgba32f = 1,
    Rgba16f = 2,
    R32f = 3,
    Rgba8 = 4,
    Rgba8Snorm = 5,
    Rg32f = 6,
    Rg16f = 7,
    R11fG11fB10f = 8,
    R16f = 9,
    Rgba16 = 10,
    Rgb10A2 = 11,
    Rg16 = 12,
    Rg8 = 13,
    R16 = 14,
    R8 = 15,
    Rgba16Snorm = 16,
    Rg16Snorm = 17,
    Rg8Snorm = 18,
    R16Snorm = 19,
    R8Snorm = 20,
    Rgba32i = 21,
    Rgba16i = 22,
    Rgba8i = 23,
    R32i = 24,
    Rg32i = 25,
    Rg16i = 26,
    Rg8i = 27,
    R16i = 28,
    R8i = 29,
    Rgba32ui = 30,
    Rgba16ui = 31,
    Rgba8ui = 32,
    R32ui = 33,
    Rgb10A2ui = 34,
    Rg32ui = 35,
    Rg16ui = 36,
    Rg8ui = 37,
    R16ui = 38,
    R8ui = 39,
}

public sealed class SpirvModuleBuilder
{
    private const uint Magic = 0x07230203;
    private const uint Version15 = 0x00010500;
    private const uint Generator = 0x53504500; // "SPE"

    private readonly List<uint> _capabilities = [];
    private readonly List<uint> _extensions = [];
    private readonly List<uint> _imports = [];
    private readonly List<uint> _memoryModel = [];
    private readonly List<uint> _entryPoints = [];
    private readonly List<uint> _executionModes = [];
    private readonly List<uint> _debug = [];
    private readonly List<uint> _annotations = [];
    private readonly List<uint> _typesConstantsGlobals = [];
    private readonly List<uint> _functions = [];
    private readonly Dictionary<(uint Width, bool Signed), uint> _integerTypes = [];
    private readonly Dictionary<uint, uint> _floatTypes = [];
    private readonly Dictionary<(uint Component, uint Count), uint> _vectorTypes = [];
    private readonly Dictionary<
        (
            uint SampledType,
            SpirvImageDim Dimension,
            bool Depth,
            bool Arrayed,
            bool Multisampled,
            uint Sampled,
            SpirvImageFormat Format
        ),
        uint> _imageTypes = [];
    private readonly Dictionary<uint, uint> _sampledImageTypes = [];
    private readonly Dictionary<(SpirvStorageClass Storage, uint Type), uint> _pointerTypes = [];
    private readonly Dictionary<(uint Element, uint Count), uint> _arrayTypes = [];
    private readonly Dictionary<uint, uint> _runtimeArrayTypes = [];
    private readonly Dictionary<string, uint> _functionTypes = [];
    private readonly Dictionary<(uint Type, ulong Value), uint> _constants = [];
    private readonly HashSet<SpirvCapability> _declaredCapabilities = [];
    private readonly Dictionary<string, uint> _extInstImports = [];
    private uint _nextId = 1;
    private uint? _voidType;
    private uint? _boolType;

    public uint AllocateId() => _nextId++;

    public void AddCapability(SpirvCapability capability)
    {
        if (_declaredCapabilities.Add(capability))
        {
            Emit(_capabilities, SpirvOp.Capability, (uint)capability);
        }
    }

    public void AddExtension(string extension) =>
        EmitWithString(_extensions, SpirvOp.Extension, [], extension);

    public uint ImportExtInst(string name)
    {
        if (_extInstImports.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var id = AllocateId();
        EmitWithString(_imports, SpirvOp.ExtInstImport, [id], name);
        _extInstImports.Add(name, id);
        return id;
    }

    public void SetLogicalGlsl450MemoryModel() =>
        Emit(_memoryModel, SpirvOp.MemoryModel, 0, 1);

    public void AddEntryPoint(
        SpirvExecutionModel model,
        uint function,
        string name,
        IReadOnlyList<uint> interfaces)
    {
        var prefix = new uint[2 + interfaces.Count];
        prefix[0] = (uint)model;
        prefix[1] = function;
        for (var index = 0; index < interfaces.Count; index++)
        {
            prefix[index + 2] = interfaces[index];
        }

        EmitWithString(_entryPoints, SpirvOp.EntryPoint, prefix, name, stringBeforeTailCount: 2);
    }

    public void AddExecutionMode(uint function, SpirvExecutionMode mode, params uint[] operands)
    {
        var values = new uint[2 + operands.Length];
        values[0] = function;
        values[1] = (uint)mode;
        operands.CopyTo(values, 2);
        Emit(_executionModes, SpirvOp.ExecutionMode, values);
    }

    public void AddName(uint target, string name) =>
        EmitWithString(_debug, SpirvOp.Name, [target], name);

    public void AddDecoration(uint target, SpirvDecoration decoration, params uint[] operands)
    {
        var values = new uint[2 + operands.Length];
        values[0] = target;
        values[1] = (uint)decoration;
        operands.CopyTo(values, 2);
        Emit(_annotations, SpirvOp.Decorate, values);
    }

    public void AddMemberDecoration(
        uint target,
        uint member,
        SpirvDecoration decoration,
        params uint[] operands)
    {
        var values = new uint[3 + operands.Length];
        values[0] = target;
        values[1] = member;
        values[2] = (uint)decoration;
        operands.CopyTo(values, 3);
        EmitRaw(_annotations, 72, values);
    }

    public uint TypeVoid()
    {
        if (_voidType is { } existing)
        {
            return existing;
        }

        var id = AllocateId();
        Emit(_typesConstantsGlobals, SpirvOp.TypeVoid, id);
        _voidType = id;
        return id;
    }

    public uint TypeBool()
    {
        if (_boolType is { } existing)
        {
            return existing;
        }

        var id = AllocateId();
        Emit(_typesConstantsGlobals, SpirvOp.TypeBool, id);
        _boolType = id;
        return id;
    }

    public uint TypeInt(uint width, bool signed)
    {
        var key = (width, signed);
        if (_integerTypes.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var id = AllocateId();
        Emit(_typesConstantsGlobals, SpirvOp.TypeInt, id, width, signed ? 1u : 0u);
        _integerTypes.Add(key, id);
        return id;
    }

    public uint TypeFloat(uint width)
    {
        if (_floatTypes.TryGetValue(width, out var existing))
        {
            return existing;
        }

        var id = AllocateId();
        Emit(_typesConstantsGlobals, SpirvOp.TypeFloat, id, width);
        _floatTypes.Add(width, id);
        return id;
    }

    public uint TypeVector(uint componentType, uint count)
    {
        var key = (componentType, count);
        if (_vectorTypes.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var id = AllocateId();
        Emit(_typesConstantsGlobals, SpirvOp.TypeVector, id, componentType, count);
        _vectorTypes.Add(key, id);
        return id;
    }

    public uint TypeImage(
        uint sampledType,
        SpirvImageDim dimension,
        bool depth,
        bool arrayed,
        bool multisampled,
        uint sampled,
        SpirvImageFormat format)
    {
        var key = (
            sampledType,
            dimension,
            depth,
            arrayed,
            multisampled,
            sampled,
            format);
        if (_imageTypes.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var id = AllocateId();
        Emit(
            _typesConstantsGlobals,
            SpirvOp.TypeImage,
            id,
            sampledType,
            (uint)dimension,
            depth ? 1u : 0u,
            arrayed ? 1u : 0u,
            multisampled ? 1u : 0u,
            sampled,
            (uint)format);
        _imageTypes.Add(key, id);
        return id;
    }

    public uint TypeSampledImage(uint imageType)
    {
        if (_sampledImageTypes.TryGetValue(imageType, out var existing))
        {
            return existing;
        }

        var id = AllocateId();
        Emit(_typesConstantsGlobals, SpirvOp.TypeSampledImage, id, imageType);
        _sampledImageTypes.Add(imageType, id);
        return id;
    }

    public uint TypeArray(uint elementType, uint count)
    {
        var key = (elementType, count);
        if (_arrayTypes.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var length = Constant(TypeInt(32, false), count);
        var id = AllocateId();
        Emit(_typesConstantsGlobals, SpirvOp.TypeArray, id, elementType, length);
        _arrayTypes.Add(key, id);
        return id;
    }

    public uint TypeRuntimeArray(uint elementType)
    {
        if (_runtimeArrayTypes.TryGetValue(elementType, out var existing))
        {
            return existing;
        }

        var id = AllocateId();
        Emit(_typesConstantsGlobals, SpirvOp.TypeRuntimeArray, id, elementType);
        _runtimeArrayTypes.Add(elementType, id);
        return id;
    }

    public uint TypeStruct(params uint[] memberTypes)
    {
        var id = AllocateId();
        var operands = new uint[memberTypes.Length + 1];
        operands[0] = id;
        memberTypes.CopyTo(operands, 1);
        Emit(_typesConstantsGlobals, SpirvOp.TypeStruct, operands);
        return id;
    }

    public uint TypePointer(SpirvStorageClass storageClass, uint type)
    {
        var key = (storageClass, type);
        if (_pointerTypes.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var id = AllocateId();
        Emit(_typesConstantsGlobals, SpirvOp.TypePointer, id, (uint)storageClass, type);
        _pointerTypes.Add(key, id);
        return id;
    }

    public uint TypeFunction(uint returnType, params uint[] parameterTypes)
    {
        var key = returnType + ":" + string.Join(',', parameterTypes);
        if (_functionTypes.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var id = AllocateId();
        var operands = new uint[parameterTypes.Length + 2];
        operands[0] = id;
        operands[1] = returnType;
        parameterTypes.CopyTo(operands, 2);
        Emit(_typesConstantsGlobals, SpirvOp.TypeFunction, operands);
        _functionTypes.Add(key, id);
        return id;
    }

    public uint ConstantBool(bool value)
    {
        var type = TypeBool();
        var key = (type, value ? 1UL : 0UL);
        if (_constants.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var id = AllocateId();
        Emit(
            _typesConstantsGlobals,
            value ? SpirvOp.ConstantTrue : SpirvOp.ConstantFalse,
            type,
            id);
        _constants.Add(key, id);
        return id;
    }

    public uint Constant(uint type, uint value)
    {
        var key = (type, (ulong)value);
        if (_constants.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var id = AllocateId();
        Emit(_typesConstantsGlobals, SpirvOp.Constant, type, id, value);
        _constants.Add(key, id);
        return id;
    }

    public uint Constant64(uint type, ulong value)
    {
        var key = (type, value);
        if (_constants.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var id = AllocateId();
        Emit(
            _typesConstantsGlobals,
            SpirvOp.Constant,
            type,
            id,
            (uint)value,
            (uint)(value >> 32));
        _constants.Add(key, id);
        return id;
    }

    public uint ConstantFloat(uint type, float value) =>
        Constant(type, BitConverter.SingleToUInt32Bits(value));

    public uint ConstantComposite(uint type, params uint[] constituents)
    {
        var id = AllocateId();
        var operands = new uint[constituents.Length + 2];
        operands[0] = type;
        operands[1] = id;
        constituents.CopyTo(operands, 2);
        Emit(_typesConstantsGlobals, SpirvOp.ConstantComposite, operands);
        return id;
    }

    public uint ConstantNull(uint type)
    {
        var key = (type, ulong.MaxValue);
        if (_constants.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var id = AllocateId();
        Emit(_typesConstantsGlobals, SpirvOp.ConstantNull, type, id);
        _constants.Add(key, id);
        return id;
    }

    public uint AddGlobalVariable(
        uint pointerType,
        SpirvStorageClass storageClass,
        uint? initializer = null)
    {
        var id = AllocateId();
        if (initializer.HasValue)
        {
            Emit(
                _typesConstantsGlobals,
                SpirvOp.Variable,
                pointerType,
                id,
                (uint)storageClass,
                initializer.Value);
        }
        else
        {
            Emit(_typesConstantsGlobals, SpirvOp.Variable, pointerType, id, (uint)storageClass);
        }

        return id;
    }

    public uint BeginFunction(uint returnType, uint functionType)
    {
        var id = AllocateId();
        Emit(_functions, SpirvOp.Function, returnType, id, 0, functionType);
        return id;
    }

    public uint AddFunctionParameter(uint type) =>
        EmitResult(_functions, SpirvOp.FunctionParameter, type);

    public uint AddLabel(uint? id = null)
    {
        var result = id ?? AllocateId();
        Emit(_functions, SpirvOp.Label, result);
        return result;
    }

    public uint AddFunctionVariable(uint pointerType, uint? initializer = null)
    {
        var id = AllocateId();
        if (initializer.HasValue)
        {
            Emit(
                _functions,
                SpirvOp.Variable,
                pointerType,
                id,
                (uint)SpirvStorageClass.Function,
                initializer.Value);
        }
        else
        {
            Emit(
                _functions,
                SpirvOp.Variable,
                pointerType,
                id,
                (uint)SpirvStorageClass.Function);
        }

        return id;
    }

    public uint AddInstruction(SpirvOp opcode, uint resultType, params uint[] operands) =>
        EmitResult(_functions, opcode, resultType, operands);

    public void AddStatement(SpirvOp opcode, params uint[] operands) =>
        Emit(_functions, opcode, operands);

    public void EndFunction() => Emit(_functions, SpirvOp.FunctionEnd);

    public byte[] Build()
    {
        if (_memoryModel.Count == 0)
        {
            SetLogicalGlsl450MemoryModel();
        }

        var wordCount =
            5 +
            _capabilities.Count +
            _extensions.Count +
            _imports.Count +
            _memoryModel.Count +
            _entryPoints.Count +
            _executionModes.Count +
            _debug.Count +
            _annotations.Count +
            _typesConstantsGlobals.Count +
            _functions.Count;
        var words = new uint[wordCount];
        var offset = 0;
        WriteWord(Magic);
        WriteWord(Version15);
        WriteWord(Generator);
        WriteWord(_nextId);
        WriteWord(0);
        WriteSection(_capabilities);
        WriteSection(_extensions);
        WriteSection(_imports);
        WriteSection(_memoryModel);
        WriteSection(_entryPoints);
        WriteSection(_executionModes);
        WriteSection(_debug);
        WriteSection(_annotations);
        WriteSection(_typesConstantsGlobals);
        WriteSection(_functions);
        var bytes = new byte[wordCount * sizeof(uint)];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
        return bytes;

        void WriteWord(uint value)
        {
            words[offset++] = value;
        }

        void WriteSection(List<uint> section)
        {
            foreach (var value in section)
            {
                WriteWord(value);
            }
        }
    }

    private uint EmitResult(
        List<uint> section,
        SpirvOp opcode,
        uint resultType,
        params uint[] operands)
    {
        var result = AllocateId();
        var values = new uint[operands.Length + 2];
        values[0] = resultType;
        values[1] = result;
        operands.CopyTo(values, 2);
        Emit(section, opcode, values);
        return result;
    }

    private static void Emit(List<uint> section, SpirvOp opcode, params uint[] operands) =>
        EmitRaw(section, (ushort)opcode, operands);

    private static void EmitRaw(List<uint> section, ushort opcode, params uint[] operands)
    {
        section.Add(((uint)(operands.Length + 1) << 16) | opcode);
        section.AddRange(operands);
    }

    private static void EmitWithString(
        List<uint> section,
        SpirvOp opcode,
        IReadOnlyList<uint> prefix,
        string value,
        int stringBeforeTailCount = -1)
    {
        var encoded = EncodeString(value);
        if (stringBeforeTailCount < 0)
        {
            var operands = new uint[prefix.Count + encoded.Length];
            for (var index = 0; index < prefix.Count; index++)
            {
                operands[index] = prefix[index];
            }

            encoded.CopyTo(operands, prefix.Count);
            Emit(section, opcode, operands);
            return;
        }

        var result = new uint[prefix.Count + encoded.Length];
        for (var index = 0; index < stringBeforeTailCount; index++)
        {
            result[index] = prefix[index];
        }

        encoded.CopyTo(result, stringBeforeTailCount);
        for (var index = stringBeforeTailCount; index < prefix.Count; index++)
        {
            result[index + encoded.Length] = prefix[index];
        }

        Emit(section, opcode, result);
    }

    private static uint[] EncodeString(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value) + 1;
        var words = new uint[(byteCount + 3) / 4];
        Encoding.UTF8.GetBytes(value, System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()));
        return words;
    }
}
