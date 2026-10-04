// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.ShaderCompiler.Vulkan;

// Expand each three-vertex rectangle after guest vertex execution.
public static class RectangleGeometryShader
{
    private sealed record Interface(uint Type, uint Variable, uint? Location, uint? BuiltIn);

    public static byte[] Create(byte[] vertexCode, byte[]? fragmentCode)
        => Create(vertexCode, fragmentCode, out _);

    public static byte[] Create(byte[] vertexCode, byte[]? fragmentCode, out byte[] rectangleVertexCode)
        => Create(vertexCode, fragmentCode, out rectangleVertexCode, out _);

    public static byte[] Create(byte[] vertexCode, byte[]? fragmentCode,
        out byte[] rectangleVertexCode, out byte[]? rectangleFragmentCode)
    {
        var types = new Dictionary<uint, uint[]>();
        var decorations = new Dictionary<uint, Dictionary<uint, uint>>();
        var variables = new List<uint[]>();
        Read(vertexCode, types, decorations, variables);
        var flatLocations = new HashSet<uint>();
        var fragmentDecorations = new Dictionary<uint, Dictionary<uint, uint>>();
        if (fragmentCode is not null)
        {
            Read(fragmentCode, new(), fragmentDecorations, new());
            foreach (var item in fragmentDecorations.Values)
                if (item.ContainsKey(14) && item.TryGetValue(30, out var location)) flatLocations.Add(location);
        }

        var outputs = variables.Where(v => v[3] == (uint)SpirvStorageClass.Output).Select(v =>
        {
            decorations.TryGetValue(v[2], out var attributes);
            return new Interface(types[v[1]][3], v[2],
                attributes?.GetValueOrDefault(30u, uint.MaxValue) is { } location && location != uint.MaxValue ? location : null,
                attributes?.GetValueOrDefault(11u, uint.MaxValue) is { } builtin && builtin != uint.MaxValue ? builtin : null);
        }).ToArray();
        if (!outputs.Any(o => o.BuiltIn == 0)) throw new NotSupportedException("Rectangle expansion needs a position output.");

        uint LocationCount(uint typeId)
        {
            var type = types[typeId];
            return (SpirvOp)(type[0] & 0xFFFF) == SpirvOp.TypeArray
                ? checked(types[type[3]][3] * LocationCount(type[2])) : 1;
        }
        var nextLocation = outputs.Where(output => output.Location.HasValue)
            .Select(output => checked(output.Location!.Value + LocationCount(output.Type)))
            .DefaultIfEmpty(0u).Max();
        var forwardedLocations = new Dictionary<uint, uint>();
        foreach (var output in outputs.Where(output => output.BuiltIn is 9 or 10))
            forwardedLocations.Add(output.Variable, nextLocation++);

        var originalVertexLocations = new Dictionary<uint, uint>();
        var fragmentLocations = new Dictionary<uint, uint>();
        var barycentricLocations = new Dictionary<uint, (uint Location, bool Linear)>();
        foreach (var (variable, attributes) in fragmentDecorations)
        {
            if (attributes.ContainsKey((uint)SpirvDecoration.PerVertexKhr) && attributes.TryGetValue(30, out var location))
            {
                if (!originalVertexLocations.TryGetValue(location, out var target))
                {
                    target = nextLocation;
                    var output = outputs.Single(output => output.Location == location);
                    nextLocation = checked(nextLocation + 3 * LocationCount(output.Type));
                    originalVertexLocations.Add(location, target);
                }
                fragmentLocations.Add(variable, target);
            }
            if (attributes.TryGetValue(11, out var builtIn) && builtIn is 5286 or 5287)
                barycentricLocations.Add(variable, (nextLocation++, builtIn == 5287));
        }
        rectangleFragmentCode = fragmentCode is null ? null : RewriteFragment(fragmentCode, fragmentLocations, barycentricLocations);

        // Layer and ViewportIndex are output-only built-ins before rasterization.
        // Pass their values to the geometry stage through separate user locations.
        rectangleVertexCode = (byte[])vertexCode.Clone();
        var vertexWords = MemoryMarshal.Cast<byte, uint>(rectangleVertexCode.AsSpan());
        for (var offset = 5; offset < vertexWords.Length; offset += (int)(vertexWords[offset] >> 16))
        {
            if ((SpirvOp)(vertexWords[offset] & 0xFFFF) == SpirvOp.Decorate &&
                vertexWords[offset + 2] == (uint)SpirvDecoration.BuiltIn &&
                forwardedLocations.TryGetValue(vertexWords[offset + 1], out var location))
            {
                vertexWords[offset + 2] = (uint)SpirvDecoration.Location;
                vertexWords[offset + 3] = location;
            }
        }

        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        module.AddCapability((SpirvCapability)2); // Geometry.
        module.SetLogicalGlsl450MemoryModel();
        var uintType = module.TypeInt(32, false);
        var copiedTypes = new Dictionary<uint, uint>();
        uint CopyType(uint id)
        {
            if (copiedTypes.TryGetValue(id, out var existing)) return existing;
            var type = types[id];
            var copied = (SpirvOp)(type[0] & 0xFFFF) switch
            {
                SpirvOp.TypeFloat when type[2] == 32 => module.TypeFloat(32),
                SpirvOp.TypeInt when type[2] == 32 => module.TypeInt(32, type[3] != 0),
                SpirvOp.TypeVector => module.TypeVector(CopyType(type[2]), type[3]),
                SpirvOp.TypeArray => module.TypeArray(CopyType(type[2]), types[type[3]][3]),
                _ => throw new NotSupportedException("Rectangle expansion has an unsupported vertex output type."),
            };
            copiedTypes.Add(id, copied);
            return copied;
        }

        var interfaces = new List<uint>();
        var inputs = new List<uint>();
        var destinations = new List<uint>();
        foreach (var output in outputs)
        {
            if (output.Location is null && output.BuiltIn is null)
                throw new NotSupportedException("Rectangle expansion needs explicit vertex output locations.");
            if (output.BuiltIn is { } builtin && builtin is not (0 or 1 or 3 or 4 or 9 or 10))
                throw new NotSupportedException($"Rectangle expansion does not support built-in {builtin}.");
            if (output.BuiltIn == 3) module.AddCapability(SpirvCapability.ClipDistance);
            if (output.BuiltIn == 4) module.AddCapability(SpirvCapability.CullDistance);
            if (output.BuiltIn == 10) module.AddCapability((SpirvCapability)57);
            var type = CopyType(output.Type);
            var input = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Input, module.TypeArray(type, 3)), SpirvStorageClass.Input);
            var destination = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, type), SpirvStorageClass.Output);
            foreach (var variable in new[] { input, destination })
            {
                interfaces.Add(variable);
                if (output.Location is { } location) module.AddDecoration(variable, SpirvDecoration.Location, location);
                if (variable == input && forwardedLocations.TryGetValue(output.Variable, out var forwardedLocation))
                    module.AddDecoration(variable, SpirvDecoration.Location, forwardedLocation);
                else if (output.BuiltIn is { } builtIn)
                    module.AddDecoration(variable, SpirvDecoration.BuiltIn, builtIn);
            }
            inputs.Add(input);
            destinations.Add(destination);
        }

        var originalVertexOutputs = new Dictionary<int, uint>();
        for (var index = 0; index < outputs.Length; index++)
        {
            if (outputs[index].Location is not { } location || !originalVertexLocations.TryGetValue(location, out var target)) continue;
            var type = module.TypeArray(CopyType(outputs[index].Type), 3);
            var variable = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, type), SpirvStorageClass.Output);
            module.AddDecoration(variable, SpirvDecoration.Location, target);
            module.AddDecoration(variable, SpirvDecoration.Flat);
            originalVertexOutputs.Add(index, variable);
            interfaces.Add(variable);
        }
        var barycentricOutputs = new List<(uint Variable, bool Linear)>();
        var barycentricType = module.TypeVector(module.TypeFloat(32), 3);
        foreach (var (location, linear) in barycentricLocations.Values)
        {
            var variable = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, barycentricType), SpirvStorageClass.Output);
            module.AddDecoration(variable, SpirvDecoration.Location, location);
            if (linear) module.AddDecoration(variable, SpirvDecoration.NoPerspective);
            barycentricOutputs.Add((variable, linear));
            interfaces.Add(variable);
        }

        var function = module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
        module.AddEntryPoint((SpirvExecutionModel)3, function, "main", interfaces);
        module.AddExecutionMode(function, (SpirvExecutionMode)22); // Input triangles.
        module.AddExecutionMode(function, (SpirvExecutionMode)29); // Output triangle strip.
        module.AddExecutionMode(function, SpirvExecutionMode.OutputVertices, 4);
        module.AddExecutionMode(function, (SpirvExecutionMode)0, 1); // One invocation.
        module.AddLabel();
        uint LoadVertexAt(int output, uint vertexIndex)
        {
            var type = CopyType(outputs[output].Type);
            var pointer = module.AddInstruction(SpirvOp.AccessChain, module.TypePointer(SpirvStorageClass.Input, type), inputs[output], vertexIndex);
            return module.AddInstruction(SpirvOp.Load, type, pointer);
        }
        uint LoadVertex(int output, uint vertex) => LoadVertexAt(output, module.Constant(uintType, vertex));

        var positionIndex = Array.FindIndex(outputs, output => output.BuiltIn == 0);
        var floatType = module.TypeFloat(32);
        var boolType = module.TypeBool();
        var coordinates = new uint[3, 2];
        for (uint vertex = 0; vertex < 3; vertex++)
        {
            var position = LoadVertex(positionIndex, vertex);
            for (uint axis = 0; axis < 2; axis++)
                coordinates[vertex, axis] = module.AddInstruction(SpirvOp.CompositeExtract, floatType, position, axis);
        }
        uint SharesAxis(int first, int second, int axis) => module.AddInstruction(
            SpirvOp.FOrdEqual, boolType, coordinates[first, axis], coordinates[second, axis]);
        uint IsRightAngle(int vertex)
        {
            var next = (vertex + 1) % 3;
            var previous = (vertex + 2) % 3;
            var horizontal = module.AddInstruction(SpirvOp.LogicalAnd, boolType,
                SharesAxis(vertex, next, 0), SharesAxis(vertex, previous, 1));
            var vertical = module.AddInstruction(SpirvOp.LogicalAnd, boolType,
                SharesAxis(vertex, next, 1), SharesAxis(vertex, previous, 0));
            return module.AddInstruction(SpirvOp.LogicalOr, boolType, horizontal, vertical);
        }
        // Rotate the input triangle without changing its winding or flat source.
        // The shared axis corner is opposite the corner that must be generated.
        var rightAngle = module.AddInstruction(SpirvOp.Select, uintType, IsRightAngle(0),
            module.Constant(uintType, 0), module.AddInstruction(SpirvOp.Select, uintType, IsRightAngle(1),
                module.Constant(uintType, 1), module.Constant(uintType, 2)));
        var orderedVertices = new uint[3];
        for (uint vertex = 0; vertex < 3; vertex++)
            orderedVertices[vertex] = module.AddInstruction(SpirvOp.UMod, uintType,
                module.AddInstruction(SpirvOp.IAdd, uintType, rightAngle, module.Constant(uintType, vertex)),
                module.Constant(uintType, 3));
        uint Corner(uint originalType, uint first, uint second, uint third)
        {
            var type = types[originalType];
            var copied = CopyType(originalType);
            if ((SpirvOp)(type[0] & 0xFFFF) == SpirvOp.TypeArray)
            {
                var elements = new uint[types[type[3]][3]];
                for (uint i = 0; i < elements.Length; i++)
                    elements[i] = Corner(type[2],
                        module.AddInstruction(SpirvOp.CompositeExtract, CopyType(type[2]), first, i),
                        module.AddInstruction(SpirvOp.CompositeExtract, CopyType(type[2]), second, i),
                        module.AddInstruction(SpirvOp.CompositeExtract, CopyType(type[2]), third, i));
                return module.AddInstruction(SpirvOp.CompositeConstruct, copied, elements);
            }
            var scalar = (SpirvOp)(type[0] & 0xFFFF) == SpirvOp.TypeVector ? types[type[2]] : type;
            if ((SpirvOp)(scalar[0] & 0xFFFF) != SpirvOp.TypeFloat) return first;
            return module.AddInstruction(SpirvOp.FSub, copied,
                module.AddInstruction(SpirvOp.FAdd, copied, second, third), first);
        }
        for (uint vertex = 0; vertex < 4; vertex++)
        {
            // Keep the guest triangle's values and weights across both host triangles.
            foreach (var (index, destination) in originalVertexOutputs)
                module.AddStatement(SpirvOp.Store, destination, module.AddInstruction(SpirvOp.CompositeConstruct,
                    module.TypeArray(CopyType(outputs[index].Type), 3), LoadVertex(index, 0), LoadVertex(index, 1), LoadVertex(index, 2)));
            foreach (var (destination, linear) in barycentricOutputs)
            {
                var weights = new uint[3];
                for (uint original = 0; original < 3; original++)
                {
                    var selected = module.AddInstruction(SpirvOp.IEqual, boolType,
                        vertex < 3 ? orderedVertices[vertex] : rightAngle, module.Constant(uintType, original));
                    weights[original] = module.AddInstruction(SpirvOp.Select, floatType, selected,
                        module.Constant(floatType, vertex < 3 ? 0x3F800000u : 0xBF800000u),
                        module.Constant(floatType, vertex < 3 ? 0u : 0x3F800000u));
                    if (linear && vertex == 3)
                    {
                        var originalW = module.AddInstruction(SpirvOp.CompositeExtract, floatType, LoadVertex(positionIndex, original), 3);
                        var cornerPosition = Corner(outputs[positionIndex].Type, LoadVertexAt(positionIndex, orderedVertices[0]),
                            LoadVertexAt(positionIndex, orderedVertices[1]), LoadVertexAt(positionIndex, orderedVertices[2]));
                        var cornerW = module.AddInstruction(SpirvOp.CompositeExtract, floatType, cornerPosition, 3);
                        weights[original] = module.AddInstruction(SpirvOp.FMul, floatType, weights[original],
                            module.AddInstruction(SpirvOp.FDiv, floatType, originalW, cornerW));
                    }
                }
                module.AddStatement(SpirvOp.Store, destination,
                    module.AddInstruction(SpirvOp.CompositeConstruct, barycentricType, weights));
            }
            for (var index = 0; index < outputs.Length; index++)
            {
                var output = outputs[index];
                var flat = output.Location is { } location && flatLocations.Contains(location);
                flat |= output.BuiltIn is 9 or 10;
                var value = flat ? LoadVertex(index, 0) : vertex < 3 ? LoadVertexAt(index, orderedVertices[vertex])
                    : Corner(output.Type, LoadVertexAt(index, orderedVertices[0]),
                        LoadVertexAt(index, orderedVertices[1]), LoadVertexAt(index, orderedVertices[2]));
                module.AddStatement(SpirvOp.Store, destinations[index], value);
            }
            module.AddStatement((SpirvOp)218); // EmitVertex.
        }
        module.AddStatement((SpirvOp)219); // EndPrimitive.
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        return module.Build();
    }

    private static byte[] RewriteFragment(byte[] code, Dictionary<uint, uint> originalVertices,
        Dictionary<uint, (uint Location, bool Linear)> barycentrics)
    {
        var words = MemoryMarshal.Cast<byte, uint>(code);
        var rewritten = new List<uint>(words[..5].ToArray());
        for (var offset = 5; offset < words.Length;)
        {
            var count = (int)(words[offset] >> 16);
            var instruction = words.Slice(offset, count).ToArray();
            if ((SpirvOp)(instruction[0] & 0xFFFF) == SpirvOp.Decorate)
            {
                if (originalVertices.TryGetValue(instruction[1], out var location))
                {
                    if (instruction[2] == (uint)SpirvDecoration.PerVertexKhr) instruction[2] = (uint)SpirvDecoration.Flat;
                    if (instruction[2] == (uint)SpirvDecoration.Location) instruction[3] = location;
                }
                if (instruction[2] == (uint)SpirvDecoration.BuiltIn && barycentrics.TryGetValue(instruction[1], out var barycentric))
                {
                    instruction[2] = (uint)SpirvDecoration.Location;
                    instruction[3] = barycentric.Location;
                    if (barycentric.Linear)
                        rewritten.AddRange([(3u << 16) | (uint)SpirvOp.Decorate, instruction[1], (uint)SpirvDecoration.NoPerspective]);
                }
            }
            rewritten.AddRange(instruction);
            offset += count;
        }
        return MemoryMarshal.AsBytes(rewritten.ToArray().AsSpan()).ToArray();
    }

    private static void Read(byte[] code, Dictionary<uint, uint[]> types,
        Dictionary<uint, Dictionary<uint, uint>> decorations, List<uint[]> variables)
    {
        var words = MemoryMarshal.Cast<byte, uint>(code);
        for (var offset = 5; offset < words.Length;)
        {
            var count = (int)(words[offset] >> 16);
            if (count == 0 || offset + count > words.Length) throw new ArgumentException("Invalid SPIR-V instruction.");
            var instruction = words.Slice(offset, count).ToArray();
            var opcode = (SpirvOp)(instruction[0] & 0xFFFF);
            if (opcode is SpirvOp.TypeFloat or SpirvOp.TypeInt or SpirvOp.TypeVector or SpirvOp.TypeArray or SpirvOp.TypePointer)
                types[instruction[1]] = instruction;
            else if (opcode == SpirvOp.Constant) types[instruction[2]] = instruction;
            else if (opcode == SpirvOp.Variable) variables.Add(instruction);
            else if (opcode == SpirvOp.Decorate)
            {
                if (!decorations.TryGetValue(instruction[1], out var values)) decorations.Add(instruction[1], values = new());
                values[instruction[2]] = count > 3 ? instruction[3] : 0;
            }
            offset += count;
        }
    }
}
