// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

// One vertex parameter that the fixed rectangle-list stages carry from the
// guest vertex shader to the paired fragment shader.
public readonly record struct RectangleListShaderParameter(
    uint InputLocation,
    uint OutputLocation,
    bool Flat);

public readonly record struct RectangleListShaders(byte[] Control, byte[] Evaluation);

// PS5 rectangle lists contain three corners per primitive. The hardware
// reconstructs the fourth corner and its varyings. Vulkan has no rectangle
// primitive, so use a three-control-point patch and fixed TCS/TES stages, as
// required by the guest primitive, instead of reinterpreting it as a triangle strip.
public static class RectangleListShaderBuilder
{
    public static RectangleListShaders Build(
        IReadOnlyList<RectangleListShaderParameter> parameters)
    {
        var control = new Emitter(parameters, control: true).EmitControl();
        var evaluation = new Emitter(parameters, control: false).EmitEvaluation();
        return new RectangleListShaders(control, evaluation);
    }

    private sealed class Emitter
    {
        private readonly IReadOnlyList<RectangleListShaderParameter> _parameters;
        private readonly bool _control;
        private readonly SpirvModuleBuilder _module = new();
        private readonly List<uint> _interfaces = [];
        private readonly uint[] _inputs;
        private readonly uint[] _outputs;

        private readonly uint _voidType;
        private readonly uint _boolType;
        private readonly uint _intType;
        private readonly uint _floatType;
        private readonly uint _vec2BoolType;
        private readonly uint _vec2FloatType;
        private readonly uint _vec3FloatType;
        private readonly uint _vec4FloatType;
        private readonly uint _functionType;
        private readonly uint _perVertexType;
        private readonly uint _inputFloatPointer;
        private readonly uint _inputVec4Pointer;
        private readonly uint _outputFloatPointer;
        private readonly uint _outputVec4Pointer;

        private uint _glIn;
        private uint _glOut;
        private uint _tessInner;
        private uint _tessOuter;
        private uint _tessCoord;
        private uint _invocationId;

        public Emitter(
            IReadOnlyList<RectangleListShaderParameter> parameters,
            bool control)
        {
            _parameters = parameters;
            _control = control;
            _inputs = new uint[parameters.Count];
            _outputs = new uint[parameters.Count];

            _module.AddCapability(SpirvCapability.Shader);
            _module.AddCapability(SpirvCapability.Tessellation);
            _module.SetLogicalGlsl450MemoryModel();

            _voidType = _module.TypeVoid();
            _boolType = _module.TypeBool();
            _intType = _module.TypeInt(32, signed: true);
            _floatType = _module.TypeFloat(32);
            _vec2BoolType = _module.TypeVector(_boolType, 2);
            _vec2FloatType = _module.TypeVector(_floatType, 2);
            _vec3FloatType = _module.TypeVector(_floatType, 3);
            _vec4FloatType = _module.TypeVector(_floatType, 4);
            _functionType = _module.TypeFunction(_voidType);

            _perVertexType = _module.TypeStruct(_vec4FloatType);
            _module.AddMemberDecoration(
                _perVertexType,
                0,
                SpirvDecoration.BuiltIn,
                (uint)SpirvBuiltIn.Position);
            _module.AddDecoration(_perVertexType, SpirvDecoration.Block);

            _inputFloatPointer = _module.TypePointer(SpirvStorageClass.Input, _floatType);
            _inputVec4Pointer = _module.TypePointer(SpirvStorageClass.Input, _vec4FloatType);
            _outputFloatPointer = _module.TypePointer(SpirvStorageClass.Output, _floatType);
            _outputVec4Pointer = _module.TypePointer(SpirvStorageClass.Output, _vec4FloatType);
        }

        public byte[] EmitControl()
        {
            if (!_control)
            {
                throw new InvalidOperationException("The rectangle-list emitter is not a control stage.");
            }

            DefineInputs();
            DefineOutputs();
            var main = BeginEntry(SpirvExecutionModel.TessellationControl);
            _module.AddExecutionMode(main, SpirvExecutionMode.OutputVertices, 4);

            var one = _module.ConstantFloat(_floatType, 1f);
            for (var index = 0; index < 4; index++)
            {
                Store(Access(_outputFloatPointer, _tessOuter, Int(index)), one);
            }

            for (var index = 0; index < 2; index++)
            {
                Store(Access(_outputFloatPointer, _tessInner, Int(index)), one);
            }

            var positions = new uint[3];
            for (var index = 0; index < positions.Length; index++)
            {
                positions[index] = Load(
                    _vec4FloatType,
                    Access(_inputVec4Pointer, _glIn, Int(index), Int(0)));
            }

            var coordinateEqual = new uint[3];
            for (var index = 0; index < coordinateEqual.Length; index++)
            {
                var next = (index + 1) % 3;
                var left = Result(
                    SpirvOp.VectorShuffle,
                    _vec2FloatType,
                    positions[index],
                    positions[index],
                    0,
                    1);
                var right = Result(
                    SpirvOp.VectorShuffle,
                    _vec2FloatType,
                    positions[next],
                    positions[next],
                    0,
                    1);
                coordinateEqual[index] = Result(
                    SpirvOp.FOrdEqual,
                    _vec2BoolType,
                    left,
                    right);
            }

            var barycentric = new uint[3];
            var edgeVertex = new uint[3];
            var minusOne = _module.ConstantFloat(_floatType, -1f);
            for (var index = 0; index < edgeVertex.Length; index++)
            {
                var previous = (index + 2) % 3;
                var xy = Result(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    Result(SpirvOp.CompositeExtract, _boolType, coordinateEqual[index], 0),
                    Result(SpirvOp.CompositeExtract, _boolType, coordinateEqual[previous], 1));
                var yx = Result(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    Result(SpirvOp.CompositeExtract, _boolType, coordinateEqual[index], 1),
                    Result(SpirvOp.CompositeExtract, _boolType, coordinateEqual[previous], 0));
                edgeVertex[index] = Result(SpirvOp.LogicalOr, _boolType, xy, yx);
                barycentric[index] = Result(
                    SpirvOp.Select,
                    _floatType,
                    edgeVertex[index],
                    minusOne,
                    one);
            }

            var vertexIndex = Result(
                SpirvOp.Select,
                _intType,
                edgeVertex[2],
                Int(2),
                Int(0));
            vertexIndex = Result(
                SpirvOp.Select,
                _intType,
                edgeVertex[1],
                Int(1),
                vertexIndex);
            var invocation = Load(_intType, _invocationId);
            var isFourth = Result(SpirvOp.IEqual, _boolType, invocation, Int(3));
            var inputIndex = Result(
                SpirvOp.SMod,
                _intType,
                Result(SpirvOp.IAdd, _intType, vertexIndex, invocation),
                Int(3));

            var fourthPosition = Interpolate(
                positions[0],
                positions[1],
                positions[2],
                barycentric);
            var position = Result(
                SpirvOp.Select,
                _vec4FloatType,
                isFourth,
                fourthPosition,
                Load(_vec4FloatType, Access(_inputVec4Pointer, _glIn, inputIndex, Int(0))));
            Store(Access(_outputVec4Pointer, _glOut, invocation, Int(0)), position);

            for (var parameterIndex = 0; parameterIndex < _parameters.Count; parameterIndex++)
            {
                var input0 = Load(
                    _vec4FloatType,
                    Access(_inputVec4Pointer, _inputs[parameterIndex], Int(0)));
                if (_parameters[parameterIndex].Flat)
                {
                    Store(
                        Access(_outputVec4Pointer, _outputs[parameterIndex], invocation),
                        input0);
                    continue;
                }

                var input1 = Load(
                    _vec4FloatType,
                    Access(_inputVec4Pointer, _inputs[parameterIndex], Int(1)));
                var input2 = Load(
                    _vec4FloatType,
                    Access(_inputVec4Pointer, _inputs[parameterIndex], Int(2)));
                var input3 = Interpolate(input0, input1, input2, barycentric);
                var value = Result(
                    SpirvOp.Select,
                    _vec4FloatType,
                    isFourth,
                    input3,
                    Load(
                        _vec4FloatType,
                        Access(_inputVec4Pointer, _inputs[parameterIndex], inputIndex)));
                Store(
                    Access(_outputVec4Pointer, _outputs[parameterIndex], invocation),
                    value);
            }

            _module.AddStatement(SpirvOp.Return);
            _module.EndFunction();
            return _module.Build();
        }

        public byte[] EmitEvaluation()
        {
            if (_control)
            {
                throw new InvalidOperationException("The rectangle-list emitter is not an evaluation stage.");
            }

            DefineInputs();
            DefineOutputs();
            var main = BeginEntry(SpirvExecutionModel.TessellationEvaluation);
            _module.AddExecutionMode(main, SpirvExecutionMode.Quads);
            _module.AddExecutionMode(main, SpirvExecutionMode.SpacingEqual);
            _module.AddExecutionMode(main, SpirvExecutionMode.VertexOrderCw);

            var x = Load(
                _floatType,
                Access(_inputFloatPointer, _tessCoord, Int(0)));
            var y = Load(
                _floatType,
                Access(_inputFloatPointer, _tessCoord, Int(1)));
            var index = Result(
                SpirvOp.IAdd,
                _intType,
                Result(
                    SpirvOp.IMul,
                    _intType,
                    Result(SpirvOp.ConvertFToS, _intType, y),
                    Int(2)),
                Result(SpirvOp.ConvertFToS, _intType, x));

            var position = Load(
                _vec4FloatType,
                Access(_inputVec4Pointer, _glIn, index, Int(0)));
            Store(Access(_outputVec4Pointer, _glOut, Int(0)), position);

            for (var parameterIndex = 0; parameterIndex < _parameters.Count; parameterIndex++)
            {
                Store(
                    _outputs[parameterIndex],
                    Load(
                        _vec4FloatType,
                        Access(_inputVec4Pointer, _inputs[parameterIndex], index)));
            }

            _module.AddStatement(SpirvOp.Return);
            _module.EndFunction();
            return _module.Build();
        }

        private uint BeginEntry(SpirvExecutionModel model)
        {
            var main = _module.BeginFunction(_voidType, _functionType);
            _module.AddEntryPoint(model, main, "main", _interfaces);
            _module.AddLabel();
            return main;
        }

        private void DefineInputs()
        {
            if (_control)
            {
                _invocationId = AddInterface(SpirvStorageClass.Input, _intType);
                _module.AddDecoration(
                    _invocationId,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.InvocationId);
            }
            else
            {
                _tessCoord = AddInterface(SpirvStorageClass.Input, _vec3FloatType);
                _module.AddDecoration(
                    _tessCoord,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.TessCoord);
            }

            _glIn = AddInterface(
                SpirvStorageClass.Input,
                _module.TypeArray(_perVertexType, _control ? 3u : 4u));

            var sharedInputs = new Dictionary<uint, uint>();
            for (var index = 0; index < _parameters.Count; index++)
            {
                var location = _control
                    ? _parameters[index].InputLocation
                    : _parameters[index].OutputLocation;
                if (_control && sharedInputs.TryGetValue(location, out var existing))
                {
                    _inputs[index] = existing;
                    continue;
                }

                var input = AddInterface(
                    SpirvStorageClass.Input,
                    _module.TypeArray(_vec4FloatType, _control ? 3u : 4u));
                _module.AddDecoration(input, SpirvDecoration.Location, location);
                _inputs[index] = input;
                if (_control)
                {
                    sharedInputs.Add(location, input);
                }
            }
        }

        private void DefineOutputs()
        {
            if (_control)
            {
                _glOut = AddInterface(
                    SpirvStorageClass.Output,
                    _module.TypeArray(_perVertexType, 4));
                _tessInner = AddInterface(
                    SpirvStorageClass.Output,
                    _module.TypeArray(_floatType, 2));
                _module.AddDecoration(
                    _tessInner,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.TessLevelInner);
                _module.AddDecoration(_tessInner, SpirvDecoration.Patch);
                _tessOuter = AddInterface(
                    SpirvStorageClass.Output,
                    _module.TypeArray(_floatType, 4));
                _module.AddDecoration(
                    _tessOuter,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.TessLevelOuter);
                _module.AddDecoration(_tessOuter, SpirvDecoration.Patch);
            }
            else
            {
                _glOut = AddInterface(SpirvStorageClass.Output, _perVertexType);
            }

            for (var index = 0; index < _parameters.Count; index++)
            {
                var output = AddInterface(
                    SpirvStorageClass.Output,
                    _control
                        ? _module.TypeArray(_vec4FloatType, 4)
                        : _vec4FloatType);
                _module.AddDecoration(
                    output,
                    SpirvDecoration.Location,
                    _parameters[index].OutputLocation);
                _outputs[index] = output;
            }
        }

        private uint AddInterface(SpirvStorageClass storage, uint type)
        {
            var variable = _module.AddGlobalVariable(
                _module.TypePointer(storage, type),
                storage);
            _interfaces.Add(variable);
            return variable;
        }

        private uint Access(uint pointerType, uint basePointer, params uint[] indices)
        {
            var operands = new uint[indices.Length + 1];
            operands[0] = basePointer;
            indices.CopyTo(operands, 1);
            return Result(SpirvOp.AccessChain, pointerType, operands);
        }

        private uint Load(uint type, uint pointer) =>
            Result(SpirvOp.Load, type, pointer);

        private void Store(uint pointer, uint value) =>
            _module.AddStatement(SpirvOp.Store, pointer, value);

        private uint Result(SpirvOp opcode, uint type, params uint[] operands) =>
            _module.AddInstruction(opcode, type, operands);

        private uint Int(int value) =>
            _module.Constant(_intType, unchecked((uint)value));

        private uint Interpolate(
            uint value0,
            uint value1,
            uint value2,
            IReadOnlyList<uint> barycentric)
        {
            var part0 = Result(
                SpirvOp.VectorTimesScalar,
                _vec4FloatType,
                value0,
                barycentric[0]);
            var part1 = Result(
                SpirvOp.VectorTimesScalar,
                _vec4FloatType,
                value1,
                barycentric[1]);
            var part2 = Result(
                SpirvOp.VectorTimesScalar,
                _vec4FloatType,
                value2,
                barycentric[2]);
            return Result(
                SpirvOp.FAdd,
                _vec4FloatType,
                part0,
                Result(SpirvOp.FAdd, _vec4FloatType, part1, part2));
        }
    }
}
