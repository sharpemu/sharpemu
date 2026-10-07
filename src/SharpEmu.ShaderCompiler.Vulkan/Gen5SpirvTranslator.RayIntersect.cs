// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

// Each instruction tests one node. The guest shader controls traversal.
public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private const uint InvalidNode = 0xFFFF_FFFF;

        // Node pointer: the byte offset divided by eight above bits 2:0, which hold the type.
        private const uint LastTriangleNodeType = 3;
        private const uint NodeTypeBoxFloat16 = 4;
        private const uint NodeTypeBoxFloat32 = 5;

        // Triangle fans contain five float3 vertices and one ID/remapping word.
        private const uint TriangleIdDword = 15;
        private const int TriangleIdISourceShift = 0;
        private const int TriangleIdJSourceShift = 2;

        // Box nodes: four child pointers, then per child a float3 min/max pair (six floats)
        // or six packed halves (three dwords).
        private const uint BoxBoundsDword = 4;

        private uint[]? _rayResult;

        private void EmitRayIntersect(Gen5RayIntersectControl ray, bool bvh64)
        {
            _rayResult ??= DeclareRayResult();
            EmitExecConditional(() => EmitRayIntersectActive(ray, bvh64));
        }

        private uint[] DeclareRayResult()
        {
            var result = new uint[Gen5RayIntersectControl.ResultDwords];
            for (var component = 0; component < result.Length; component++)
            {
                result[component] = _module.AddGlobalVariable(_privateUintPointer, SpirvStorageClass.Private, UInt(InvalidNode));
                _module.AddName(result[component], $"rayIntersectResult{component}");
                _interfaces.Add(result[component]);
            }

            return result;
        }

        private void EmitRayIntersectActive(Gen5RayIntersectControl ray, bool bvh64)
        {
            var result = _rayResult!;
            var nodeLow = LoadV(ray.GetAddressRegister(0));
            var nodePointer = bvh64 ? Pair64(nodeLow, LoadV(ray.GetAddressRegister(1))) : Widen(nodeLow);
            var nodeType = BitwiseAnd(nodeLow, UInt(7));
            var nodeIndex = ShiftRightLogical64(nodePointer, ULong(3));

            // The BVH T#: base_address[39:0] in 256-byte units, box_grow_value [62:55],
            // box_sort_en [63], size [105:64] (the last valid 64-byte node index),
            // triangle_return_mode [120].
            var word0 = LoadS(ray.ScalarResource);
            var word1 = LoadS(ray.ScalarResource + 1);
            var word2 = LoadS(ray.ScalarResource + 2);
            var word3 = LoadS(ray.ScalarResource + 3);
            var baseUnits = Pair64(word0, BitwiseAnd(word1, UInt(0xFF)));
            var bvhBase = ShiftLeftLogical64(baseUnits, ULong(8));
            var lastNode = Pair64(word2, BitwiseAnd(word3, UInt(0x3FF)));
            var boxGrow = BitwiseAnd(ShiftRightLogical(word1, UInt(23)), UInt(0xFF));
            var boxSort = IsNotZero(BitwiseAnd(word1, UInt(0x8000_0000)));
            var barycentrics = IsNotZero(BitwiseAnd(word3, UInt(1u << 24)));
            var nodeAddress = IAdd64(bvhBase, ShiftLeftLogical64(And64(nodePointer, ULong(~7ul)), ULong(3)));

            var descriptorTypeValid = _module.AddInstruction(SpirvOp.IEqual, _boolType,
                ShiftRightLogical(word3, UInt(28)), UInt(8));
            _module.AddName(descriptorTypeValid, "rayDescriptorTypeValid");
            // Check the last byte before any node load, including the second box unit.
            var fullBox = _module.AddInstruction(SpirvOp.IEqual, _boolType, nodeType, UInt(NodeTypeBoxFloat32));
            var lastByte = IAdd64(nodeAddress,
                _module.AddInstruction(SpirvOp.Select, _ulongType, fullBox, ULong(127), ULong(63)));
            var addressValid = _module.AddInstruction(SpirvOp.ULessThan, _boolType, lastByte, ULong(1UL << 48));
            _module.AddName(addressValid, "rayNodeAddressValid");
            // A zero base is valid when the node pointer supplies the absolute address.
            var present = LogicalAnd(descriptorTypeValid, addressValid);
            var inRange = _module.AddInstruction(SpirvOp.ULessThanEqual, _boolType, nodeIndex, lastNode);
            // A float32 box node spans two 64-byte units.
            var wideInRange = _module.AddInstruction(SpirvOp.ULessThan, _boolType, nodeIndex, lastNode);
            var valid = LogicalAnd(present, inRange);
            var isTriangle = LogicalAnd(valid, _module.AddInstruction(SpirvOp.ULessThanEqual, _boolType, nodeType, UInt(LastTriangleNodeType)));
            var isBox16 = LogicalAnd(valid, _module.AddInstruction(SpirvOp.IEqual, _boolType, nodeType, UInt(NodeTypeBoxFloat16)));
            var isBox32 = LogicalAnd(LogicalAnd(present, wideInRange),
                _module.AddInstruction(SpirvOp.IEqual, _boolType, nodeType, UInt(NodeTypeBoxFloat32)));

            var extent = Bitcast(_floatType, LoadV(ray.GetAddressRegister(bvh64 ? 2 : 1)));
            var origin = new uint[3];
            for (var axis = 0; axis < 3; axis++)
                origin[axis] = Bitcast(_floatType, LoadV(ray.GetAddressRegister((bvh64 ? 3 : 2) + axis)));
            var direction = LoadRayVector(ray, bvh64, inverse: false);
            var inverseDirection = LoadRayVector(ray, bvh64, inverse: true);

            // User nodes and out-of-range nodes do not load node memory.
            foreach (var variable in result)
                Store(variable, UInt(InvalidNode));
            EmitConditional(isTriangle, () =>
            {
                Store(result[0], UInt(0x7F80_0000));
                Store(result[1], UInt(0x3F80_0000));
                Store(result[3], UInt(0));
                var (_, mapped) = ResolveDeviceAddress(nodeAddress);
                EmitConditional(mapped, () =>
                {
                    var values = EmitRayTriangle(nodeAddress, nodeType, barycentrics, origin, direction);
                    for (var component = 0; component < values.Length; component++)
                        Store(result[component], values[component]);
                });
            });
            // Each box format reads only its own node bytes.
            void EmitBoxes(bool fp16)
            {
                var (_, mapped) = ResolveDeviceAddress(nodeAddress);
                if (!fp16)
                {
                    var (_, secondMapped) = ResolveDeviceAddress(IAdd64(nodeAddress, ULong(64)));
                    mapped = LogicalAnd(mapped, secondMapped);
                }
                EmitConditional(mapped, () =>
                {
                    var children = EmitRayBoxes(nodeAddress, fp16, boxGrow, boxSort, extent, origin, inverseDirection);
                    for (var component = 0; component < children.Length; component++)
                        Store(result[component], children[component]);
                });
            }

            EmitConditional(isBox16, () => EmitBoxes(fp16: true));
            EmitConditional(isBox32, () => EmitBoxes(fp16: false));

            for (var component = 0u; component < Gen5RayIntersectControl.ResultDwords; component++)
                StoreV(ray.VectorData + component, Load(_uintType, result[component]));
        }

        // Direction and inverse direction follow the origin; A16 packs both as six halves
        // into three dwords.
        private uint[] LoadRayVector(Gen5RayIntersectControl ray, bool bvh64, bool inverse)
        {
            var first = bvh64 ? 6 : 5;
            var values = new uint[3];
            if (!ray.A16)
            {
                for (var axis = 0; axis < 3; axis++)
                    values[axis] = Bitcast(_floatType, LoadV(ray.GetAddressRegister(first + (inverse ? 3 : 0) + axis)));
                return values;
            }

            for (var axis = 0; axis < 3; axis++)
            {
                var half = (inverse ? 3 : 0) + axis;
                var word = LoadV(ray.GetAddressRegister(first + half / 2));
                var bits = half % 2 == 0 ? BitwiseAnd(word, UInt(0xFFFF)) : ShiftRightLogical(word, UInt(16));
                values[axis] = Bitcast(_floatType, EmitHalfToFloat(bits));
            }

            return values;
        }

        private uint LoadNodeDword(uint nodeAddress, uint dword) =>
            LoadDeviceDword(IAdd64(nodeAddress, ULong(dword * 4ul)));

        private uint LoadNodeFloat(uint nodeAddress, uint dword) =>
            Bitcast(_floatType, LoadNodeDword(nodeAddress, dword));

        // Project onto the dominant ray axis. Shared edges use a top-left rule.
        private uint[] EmitRayTriangle(uint nodeAddress, uint nodeType, uint barycentrics,
            IReadOnlyList<uint> origin, IReadOnlyList<uint> direction)
        {
            _module.AddName(nodeType, "rayTriangleType");
            _module.AddName(barycentrics, "rayTriangleBarycentrics");
            for (var axis = 0; axis < 3; axis++)
            {
                _module.AddName(origin[axis], $"rayOrigin{axis}");
                _module.AddName(direction[axis], $"rayDirection{axis}");
            }
            uint IsType(uint type) => _module.AddInstruction(SpirvOp.IEqual, _boolType, nodeType, UInt(type));
            var second = IsType(1);
            var third = IsType(2);
            var fourth = IsType(3);
            var v1 = new uint[3];
            var v2 = new uint[3];
            var v3 = new uint[3];
            for (var axis = 0u; axis < 3; axis++)
            {
                var first = LoadNodeFloat(nodeAddress, axis);
                var vertex1 = LoadNodeFloat(nodeAddress, 3 + axis);
                var vertex2 = LoadNodeFloat(nodeAddress, 6 + axis);
                var vertex3 = LoadNodeFloat(nodeAddress, 9 + axis);
                var vertex4 = LoadNodeFloat(nodeAddress, 12 + axis);
                var inputs = new[] { first, vertex1, vertex2, vertex3, vertex4 };
                for (var vertex = 0; vertex < inputs.Length; vertex++)
                    _module.AddName(inputs[vertex], $"rayTriangleVertex{vertex}Axis{axis}");
                v1[axis] = SelectF(IsType(0), first, SelectF(second, vertex1, vertex2));
                v2[axis] = SelectF(IsType(0), vertex1, SelectF(fourth, vertex4, vertex3));
                v3[axis] = SelectF(third, vertex4, SelectF(fourth, first, vertex2));
            }

            var useY = FCompare(SpirvOp.FOrdLessThan, Ext(4, _floatType, direction[0]), Ext(4, _floatType, direction[1]));
            var useZ = FCompare(SpirvOp.FOrdLessThan,
                SelectF(useY, Ext(4, _floatType, direction[1]), Ext(4, _floatType, direction[0])), Ext(4, _floatType, direction[2]));
            uint[] Rotate(IReadOnlyList<uint> values) => Enumerable.Range(0, 3)
                .Select(axis => SelectF(useZ, values[axis], SelectF(useY, values[(axis + 2) % 3], values[(axis + 1) % 3])))
                .ToArray();
            var ray = Rotate(direction);
            var rayOrigin = Rotate(origin);
            var projected = new uint[3][];
            var vertices = new[] { v1, v2, v3 };
            for (var vertex = 0; vertex < 3; vertex++)
            {
                var rotated = Rotate(vertices[vertex]);
                var relative = Enumerable.Range(0, 3).Select(axis => RayArithmetic(SpirvOp.FSub, rotated[axis], rayOrigin[axis])).ToArray();
                projected[vertex] =
                [
                    RayArithmetic(SpirvOp.FSub, RayArithmetic(SpirvOp.FMul, relative[0], ray[2]), RayArithmetic(SpirvOp.FMul, ray[0], relative[2])),
                    RayArithmetic(SpirvOp.FSub, RayArithmetic(SpirvOp.FMul, relative[1], ray[2]), RayArithmetic(SpirvOp.FMul, ray[1], relative[2])),
                    relative[2],
                ];
            }
            var edges = new uint[3];
            var weights = new uint[3];
            for (var vertex = 0; vertex < 3; vertex++)
            {
                var next = projected[(vertex + 1) % 3];
                var last = projected[(vertex + 2) % 3];
                edges[vertex] = RayArithmetic(SpirvOp.FSub,
                    RayArithmetic(SpirvOp.FMul, last[0], next[1]), RayArithmetic(SpirvOp.FMul, last[1], next[0]));
                weights[vertex] = RayArithmetic(SpirvOp.FMul, edges[vertex], ray[2]);
            }
            var tNum = RayArithmetic(SpirvOp.FAdd,
                RayArithmetic(SpirvOp.FAdd, RayArithmetic(SpirvOp.FMul, edges[0], projected[0][2]),
                    RayArithmetic(SpirvOp.FMul, edges[1], projected[1][2])),
                RayArithmetic(SpirvOp.FMul, edges[2], projected[2][2]));
            var tDenom = RayArithmetic(SpirvOp.FAdd, RayArithmetic(SpirvOp.FAdd, weights[0], weights[1]), weights[2]);
            var zero = Float(0f);
            var one = Float(1f);
            var winding = FCompare(SpirvOp.FOrdGreaterThan, tDenom, zero);
            var missed = Any(
                LogicalAnd(Any(edges.Select(edge => FCompare(SpirvOp.FOrdLessThan, edge, zero)).ToArray()),
                    Any(edges.Select(edge => FCompare(SpirvOp.FOrdGreaterThan, edge, zero)).ToArray())),
                FCompare(SpirvOp.FOrdEqual, tDenom, zero),
                _module.AddInstruction(SpirvOp.IsNan, _boolType, tNum),
                FCompare(SpirvOp.FOrdLessThan, SelectF(winding, tNum, _module.AddInstruction(SpirvOp.FNegate, _floatType, tNum)), zero));
            for (var vertex = 0; vertex < 3; vertex++)
            {
                var nextY = projected[(vertex + 1) % 3][1];
                var lastY = projected[(vertex + 2) % 3][1];
                var nextZero = FCompare(SpirvOp.FOrdEqual, nextY, zero);
                var horizontal = LogicalAnd(nextZero, FCompare(SpirvOp.FOrdEqual, lastY, zero));
                var below = Any(FCompare(SpirvOp.FOrdLessThan, nextY, zero),
                    LogicalAnd(nextZero, FCompare(SpirvOp.FOrdGreaterThan, lastY, zero)));
                var rightEdge = _module.AddInstruction(SpirvOp.LogicalNotEqual, _boolType, below, winding);
                var excluded = _module.AddInstruction(SpirvOp.Select, _boolType, horizontal,
                    FCompare(SpirvOp.FOrdGreaterThan, projected[vertex][1], zero), rightEdge);
                missed = Any(missed, LogicalAnd(FCompare(SpirvOp.FOrdEqual, edges[vertex], zero), excluded));
            }
            tNum = SelectF(missed, Float(float.PositiveInfinity), tNum);
            tDenom = SelectF(missed, one, tDenom);

            var triangleId = LoadNodeDword(nodeAddress, TriangleIdDword);
            _module.AddName(triangleId, "rayTriangleId");
            var shift = ShiftLeftLogical(nodeType, UInt(3));
            uint Barycentric(int sourceShift)
            {
                var source = BitwiseAnd(ShiftRightLogical(triangleId, IAdd(shift, UInt((uint)sourceShift))), UInt(3));
                return SelectF(_module.AddInstruction(SpirvOp.IEqual, _boolType, source, UInt(1)), weights[1],
                    SelectF(_module.AddInstruction(SpirvOp.IEqual, _boolType, source, UInt(2)), weights[2], weights[0]));
            }

            var hit = LogicalNot(missed);
            uint[] results =
            [
                Bitcast(_uintType, tNum),
                Bitcast(_uintType, tDenom),
                SelectU(barycentrics, Bitcast(_uintType, Barycentric(TriangleIdISourceShift)), IAdd(triangleId, nodeType)),
                SelectU(barycentrics, Bitcast(_uintType, Barycentric(TriangleIdJSourceShift)), SelectU(hit, UInt(1), UInt(0))),
            ];
            for (var component = 0; component < results.Length; component++)
                _module.AddName(results[component], $"rayTriangleOutput{component}");
            return results;
        }

        private uint RayArithmetic(SpirvOp opcode, uint left, uint right)
        {
            var result = _module.AddInstruction(opcode, _floatType, left, right);
            _module.AddDecoration(result, SpirvDecoration.NoContraction);
            return result;
        }

        // Grow only the exit distance. The ray extent remains an exclusive limit.
        private uint[] EmitRayBoxes(uint nodeAddress, bool fp16, uint boxGrow, uint boxSort, uint extent,
            IReadOnlyList<uint> origin, IReadOnlyList<uint> inverseDirection)
        {
            var label = fp16 ? "rayBox16" : "rayBox32";
            _module.AddName(boxGrow, "rayBoxGrow");
            _module.AddName(boxSort, "rayBoxSort");
            _module.AddName(extent, "rayExtent");
            for (var axis = 0; axis < 3; axis++)
                _module.AddName(inverseDirection[axis], $"rayInverseDirection{axis}");
            var zero = Float(0f);
            var children = new uint[4];
            var keys = new uint[4];
            for (var child = 0u; child < 4; child++)
            {
                var pointer = LoadNodeDword(nodeAddress, child);
                _module.AddName(pointer, $"{label}Child{child}");
                var enter = Float(float.NegativeInfinity);
                var leave = Float(float.PositiveInfinity);
                for (var axis = 0u; axis < 3; axis++)
                {
                    var min = BoxBound(nodeAddress, fp16, child, axis);
                    var max = BoxBound(nodeAddress, fp16, child, axis + 3);
                    _module.AddName(min, $"{label}Child{child}Min{axis}");
                    _module.AddName(max, $"{label}Child{child}Max{axis}");
                    var planeMin = FMul(FSub(min, origin[(int)axis]), inverseDirection[(int)axis]);
                    var planeMax = FMul(FSub(max, origin[(int)axis]), inverseDirection[(int)axis]);
                    var positive = FCompare(SpirvOp.FOrdGreaterThanEqual, inverseDirection[(int)axis], zero);
                    var near = SelectF(positive, planeMin, planeMax);
                    var far = SelectF(positive, planeMax, planeMin);
                    // HLSL max3/min3: NaN operands propagate, so the NaN check below sees them.
                    enter = axis == 0 ? near : NanMax(enter, near);
                    leave = axis == 0 ? far : NanMin(leave, far);
                }

                var nan = _module.AddInstruction(SpirvOp.LogicalOr, _boolType,
                    _module.AddInstruction(SpirvOp.IsNan, _boolType, enter),
                    _module.AddInstruction(SpirvOp.IsNan, _boolType, leave));
                // Normalize negative zero before adding ULPs and saturate at infinity.
                var exitBits = Bitcast(_uintType, SelectF(FCompare(SpirvOp.FOrdEqual, leave, zero), zero, leave));
                var grownExit = Bitcast(_floatType, Ext(38, _uintType, IAdd(exitBits, boxGrow), UInt(0x7F80_0000)));
                var hit = LogicalAnd(LogicalNot(nan), LogicalAnd(
                    FCompare(SpirvOp.FOrdGreaterThanEqual, leave, zero), LogicalAnd(
                        FCompare(SpirvOp.FOrdLessThan, enter, extent),
                        FCompare(SpirvOp.FOrdLessThanEqual, enter, grownExit))));
                children[child] = SelectU(hit, pointer, UInt(InvalidNode));
                keys[child] = SelectF(hit, Ext(40, _floatType, enter, zero), Float(float.PositiveInfinity));
            }

            // Misses sort after finite hits. Equal depths do not swap.
            var sorted = (uint[])children.Clone();
            (int, int)[] network = [(0, 1), (2, 3), (0, 2), (1, 3), (1, 2)];
            foreach (var (a, b) in network)
            {
                var swap = FCompare(SpirvOp.FOrdLessThan, keys[b], keys[a]);
                (sorted[a], sorted[b]) = (SelectU(swap, sorted[b], sorted[a]), SelectU(swap, sorted[a], sorted[b]));
                (keys[a], keys[b]) = (SelectF(swap, keys[b], keys[a]), SelectF(swap, keys[a], keys[b]));
            }

            for (var child = 0; child < 4; child++)
            {
                children[child] = SelectU(boxSort, sorted[child], children[child]);
                _module.AddName(children[child], $"{label}Output{child}");
            }
            return children;
        }

        // Bound 0-2 is the minimum, 3-5 the maximum of one child box.
        private uint BoxBound(uint nodeAddress, bool fp16, uint child, uint bound)
        {
            var index = child * 6 + bound;
            if (!fp16)
                return LoadNodeFloat(nodeAddress, BoxBoundsDword + index);

            var packed = LoadNodeDword(nodeAddress, BoxBoundsDword + index / 2);
            var bits = index % 2 == 0 ? BitwiseAnd(packed, UInt(0xFFFF)) : ShiftRightLogical(packed, UInt(16));
            return Bitcast(_floatType, EmitHalfToFloat(bits));
        }

        private uint NanMax(uint left, uint right) => SelectF(
            _module.AddInstruction(SpirvOp.LogicalOr, _boolType,
                _module.AddInstruction(SpirvOp.IsNan, _boolType, left),
                FCompare(SpirvOp.FOrdGreaterThan, left, right)), left, right);

        private uint NanMin(uint left, uint right) => SelectF(
            _module.AddInstruction(SpirvOp.LogicalOr, _boolType,
                _module.AddInstruction(SpirvOp.IsNan, _boolType, left),
                FCompare(SpirvOp.FOrdLessThan, left, right)), left, right);

        private uint Any(params uint[] conditions)
        {
            var result = conditions[0];
            for (var index = 1; index < conditions.Length; index++)
                result = _module.AddInstruction(SpirvOp.LogicalOr, _boolType, result, conditions[index]);
            return result;
        }

        private uint FCompare(SpirvOp op, uint left, uint right) => _module.AddInstruction(op, _boolType, left, right);
        private uint FSub(uint left, uint right) => _module.AddInstruction(SpirvOp.FSub, _floatType, left, right);
        private uint FMul(uint left, uint right) => _module.AddInstruction(SpirvOp.FMul, _floatType, left, right);
        private uint SelectF(uint condition, uint whenTrue, uint whenFalse) =>
            _module.AddInstruction(SpirvOp.Select, _floatType, condition, whenTrue, whenFalse);
    }
}
