// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

// IMAGE_BVH_INTERSECT_RAY tests one ray against one BVH node; the guest shader owns the
// traversal loop and stack. This is the GFX10 (RT IP 1.1) node test as AMD's GPURT
// software fallback defines it (IntersectCommon.hlsl, image_bvh64_intersect_ray_base),
// with node layouts from GPURT's gfx10 TriangleNode1_0 and BoxNode1_0.
public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private const uint InvalidNode = 0xFFFF_FFFF;

        // Node pointer: the byte offset divided by eight above bits 2:0, which hold the type.
        private const uint NodeTypeTriangle1 = 1;
        private const uint NodeTypeBoxFloat16 = 4;
        private const uint NodeTypeBoxFloat32 = 5;

        // Triangle node: four float3 vertices, then flags/indices, the triangle id at dword 15.
        private const uint TriangleIdDword = 15;
        private const int TriangleIdBitStride = 8;
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

            var present = _module.AddInstruction(SpirvOp.INotEqual, _boolType, baseUnits, ULong(0));
            var inRange = _module.AddInstruction(SpirvOp.ULessThanEqual, _boolType, nodeIndex, lastNode);
            // A float32 box node spans two 64-byte units.
            var wideInRange = _module.AddInstruction(SpirvOp.ULessThan, _boolType, nodeIndex, lastNode);
            var valid = LogicalAnd(present, inRange);
            var isTriangle = LogicalAnd(valid, _module.AddInstruction(SpirvOp.ULessThanEqual, _boolType, nodeType, UInt(NodeTypeTriangle1)));
            var isBox16 = LogicalAnd(valid, _module.AddInstruction(SpirvOp.IEqual, _boolType, nodeType, UInt(NodeTypeBoxFloat16)));
            var isBox32 = LogicalAnd(LogicalAnd(present, wideInRange),
                _module.AddInstruction(SpirvOp.IEqual, _boolType, nodeType, UInt(NodeTypeBoxFloat32)));

            var extent = Bitcast(_floatType, LoadV(ray.GetAddressRegister(bvh64 ? 2 : 1)));
            var origin = new uint[3];
            for (var axis = 0; axis < 3; axis++)
                origin[axis] = Bitcast(_floatType, LoadV(ray.GetAddressRegister((bvh64 ? 3 : 2) + axis)));
            var direction = LoadRayVector(ray, bvh64, inverse: false);
            var inverseDirection = LoadRayVector(ray, bvh64, inverse: true);

            // Other node types (user nodes 6 and 7, unused 2 and 3), a null BVH or an
            // out-of-range node return four invalid dwords.
            foreach (var variable in result)
                Store(variable, UInt(InvalidNode));
            EmitConditional(isTriangle, () =>
            {
                var values = EmitRayTriangle(nodeAddress, nodeType, barycentrics, origin, direction);
                for (var component = 0; component < values.Length; component++)
                    Store(result[component], values[component]);
            });
            // Each box format reads only its own node bytes.
            void EmitBoxes(bool fp16)
            {
                var children = EmitRayBoxes(nodeAddress, fp16, boxGrow, boxSort, extent, origin, inverseDirection);
                for (var component = 0; component < children.Length; component++)
                    Store(result[component], children[component]);
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

        // fast_intersect_triangle and SwizzleBarycentrics. Node type 0 tests (v0, v1, v2),
        // type 1 tests (v1, v3, v2). A miss returns t_num = +inf over t_denom = 1. The
        // barycentric return mode yields the i/j numerators the builder's rotation maps
        // back; the other mode yields the triangle id and a hit flag.
        private uint[] EmitRayTriangle(uint nodeAddress, uint nodeType, uint barycentrics,
            IReadOnlyList<uint> origin, IReadOnlyList<uint> direction)
        {
            var second = _module.AddInstruction(SpirvOp.IEqual, _boolType, nodeType, UInt(NodeTypeTriangle1));
            var v1 = new uint[3];
            var v2 = new uint[3];
            var v3 = new uint[3];
            for (var axis = 0u; axis < 3; axis++)
            {
                var first = LoadNodeFloat(nodeAddress, axis);
                var vertex1 = LoadNodeFloat(nodeAddress, 3 + axis);
                var vertex2 = LoadNodeFloat(nodeAddress, 6 + axis);
                var vertex3 = LoadNodeFloat(nodeAddress, 9 + axis);
                v1[axis] = SelectF(second, vertex1, first);
                v2[axis] = SelectF(second, vertex3, vertex1);
                v3[axis] = vertex2;
            }

            var e1 = Subtract(v2, v1);
            var e2 = Subtract(v3, v1);
            var e3 = Subtract(origin, v1);
            var s1 = Cross(direction, e2);
            var s2 = Cross(e3, e1);
            var tNum = Dot(e2, s2);
            var tDenom = Dot(s1, e1);
            var iNum = Dot(e3, s1);
            var jNum = Dot(direction, s2);

            var t = FDiv(tNum, tDenom);
            var u = FDiv(iNum, tDenom);
            var v = FDiv(jNum, tDenom);
            var zero = Float(0f);
            var one = Float(1f);
            var missed = Any(
                FCompare(SpirvOp.FOrdLessThan, u, zero),
                FCompare(SpirvOp.FOrdGreaterThan, u, one),
                FCompare(SpirvOp.FOrdLessThan, v, zero),
                FCompare(SpirvOp.FOrdGreaterThan, FAdd(u, v), one),
                FCompare(SpirvOp.FOrdLessThan, t, zero));
            tNum = SelectF(missed, Float(float.PositiveInfinity), tNum);
            tDenom = SelectF(missed, one, tDenom);

            var triangleId = LoadNodeDword(nodeAddress, TriangleIdDword);
            var shift = ShiftLeftLogical(nodeType, UInt(3));
            uint Barycentric(int sourceShift)
            {
                var source = BitwiseAnd(ShiftRightLogical(triangleId, IAdd(shift, UInt((uint)sourceShift))), UInt(3));
                return SelectF(_module.AddInstruction(SpirvOp.IEqual, _boolType, source, UInt(1)), iNum,
                    SelectF(_module.AddInstruction(SpirvOp.IEqual, _boolType, source, UInt(2)), jNum,
                        FSub(FSub(tDenom, iNum), jNum)));
            }

            var hit = LogicalNot(missed);
            return
            [
                Bitcast(_uintType, tNum),
                Bitcast(_uintType, tDenom),
                SelectU(barycentrics, Bitcast(_uintType, Barycentric(TriangleIdISourceShift)), triangleId),
                SelectU(barycentrics, Bitcast(_uintType, Barycentric(TriangleIdJSourceShift)), SelectU(hit, UInt(1), UInt(0))),
            ];
        }

        // IntersectNodeBvh4 with fast_intersect_bbox: slab test clipped to [0, extent], a
        // NaN interval misses, box_grow_value widens the exit time by that many 2^-24
        // steps, and the optional sort orders hit children by entry time.
        private uint[] EmitRayBoxes(uint nodeAddress, bool fp16, uint boxGrow, uint boxSort, uint extent,
            IReadOnlyList<uint> origin, IReadOnlyList<uint> inverseDirection)
        {
            var zero = Float(0f);
            var growFactor = FAdd(Float(1f), FMul(
                _module.AddInstruction(SpirvOp.ConvertUToF, _floatType, boxGrow), Float(5.960464478e-8f)));
            var children = new uint[4];
            var keys = new uint[4];
            for (var child = 0u; child < 4; child++)
            {
                var pointer = LoadNodeDword(nodeAddress, child);
                var enter = Float(float.NegativeInfinity);
                var leave = Float(float.PositiveInfinity);
                for (var axis = 0u; axis < 3; axis++)
                {
                    var min = BoxBound(nodeAddress, fp16, child, axis);
                    var max = BoxBound(nodeAddress, fp16, child, axis + 3);
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
                var minT = SelectF(nan, Float(float.PositiveInfinity), Ext(40, _floatType, enter, zero));
                var maxT = SelectF(nan, Float(float.NegativeInfinity), Ext(37, _floatType, leave, extent));
                var hit = FCompare(SpirvOp.FOrdLessThanEqual, minT, FMul(maxT, growFactor));
                children[child] = SelectU(hit, pointer, UInt(InvalidNode));
                keys[child] = minT;
            }

            // The hardware sorting network; an invalid child always sinks.
            var sorted = (uint[])children.Clone();
            (int, int)[] network = [(0, 2), (1, 3), (0, 1), (2, 3), (1, 2)];
            foreach (var (a, b) in network)
            {
                var bValid = _module.AddInstruction(SpirvOp.INotEqual, _boolType, sorted[b], UInt(InvalidNode));
                var aInvalid = _module.AddInstruction(SpirvOp.IEqual, _boolType, sorted[a], UInt(InvalidNode));
                var swap = _module.AddInstruction(SpirvOp.LogicalOr, _boolType,
                    LogicalAnd(bValid, FCompare(SpirvOp.FOrdLessThan, keys[b], keys[a])), aInvalid);
                (sorted[a], sorted[b]) = (SelectU(swap, sorted[b], sorted[a]), SelectU(swap, sorted[a], sorted[b]));
                (keys[a], keys[b]) = (SelectF(swap, keys[b], keys[a]), SelectF(swap, keys[a], keys[b]));
            }

            for (var child = 0; child < 4; child++)
                children[child] = SelectU(boxSort, sorted[child], children[child]);
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

        private uint[] Subtract(IReadOnlyList<uint> left, IReadOnlyList<uint> right) =>
            [FSub(left[0], right[0]), FSub(left[1], right[1]), FSub(left[2], right[2])];

        private uint[] Cross(IReadOnlyList<uint> a, IReadOnlyList<uint> b) =>
        [
            FSub(FMul(a[1], b[2]), FMul(a[2], b[1])),
            FSub(FMul(a[2], b[0]), FMul(a[0], b[2])),
            FSub(FMul(a[0], b[1]), FMul(a[1], b[0])),
        ];

        private uint Dot(IReadOnlyList<uint> a, IReadOnlyList<uint> b) =>
            FAdd(FAdd(FMul(a[0], b[0]), FMul(a[1], b[1])), FMul(a[2], b[2]));

        private uint Any(params uint[] conditions)
        {
            var result = conditions[0];
            for (var index = 1; index < conditions.Length; index++)
                result = _module.AddInstruction(SpirvOp.LogicalOr, _boolType, result, conditions[index]);
            return result;
        }

        private uint FCompare(SpirvOp op, uint left, uint right) => _module.AddInstruction(op, _boolType, left, right);
        private uint FAdd(uint left, uint right) => _module.AddInstruction(SpirvOp.FAdd, _floatType, left, right);
        private uint FSub(uint left, uint right) => _module.AddInstruction(SpirvOp.FSub, _floatType, left, right);
        private uint FMul(uint left, uint right) => _module.AddInstruction(SpirvOp.FMul, _floatType, left, right);
        private uint FDiv(uint left, uint right) => _module.AddInstruction(SpirvOp.FDiv, _floatType, left, right);
        private uint SelectF(uint condition, uint whenTrue, uint whenFalse) =>
            _module.AddInstruction(SpirvOp.Select, _floatType, condition, whenTrue, whenFalse);
    }
}
