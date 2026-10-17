// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace SharpEmu.ShaderCompiler.Resources;

// Compile the immutable SRT graph once, not the draw's pointers or memory contents.
// Small methods bound JIT cost; dependency calls use constant indices and a dense cache.
internal sealed class CompiledResourceEvaluator
{
    private const int NodesPerMethod = 64;
    private delegate bool EvaluateFunction(RuntimeValueEvaluator evaluator, int index, out ulong result);
    private readonly Dictionary<ScalarValue, int> _indices;
    private readonly EvaluateFunction[] _functions;
    internal ScalarValue[] Values { get; }
    internal int[][] SourceWords { get; }
    internal int[] TableWords { get; }
    internal int Count => Values.Length;

    private static readonly Dictionary<string, MethodInfo> Helpers = new[]
    {
        nameof(RuntimeValueEvaluator.BeginCompiled), nameof(RuntimeValueEvaluator.EndCompiled),
        nameof(RuntimeValueEvaluator.StoreCompiled), nameof(RuntimeValueEvaluator.EvaluateCompiled),
        nameof(RuntimeValueEvaluator.EvaluateCompiledSpecial), nameof(RuntimeValueEvaluator.IsCompiledActiveMask),
        nameof(RuntimeValueEvaluator.ReadUserData), nameof(RuntimeValueEvaluator.ReadShaderBase), nameof(RuntimeValueEvaluator.ReadRawWord),
    }.ToDictionary(name => name, name => typeof(RuntimeValueEvaluator).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!);
    private static readonly MethodInfo EvaluateOperation = typeof(ScalarOperationSemantics)
        .GetMethod(nameof(ScalarOperationSemantics.TryEvaluateFixed), BindingFlags.NonPublic | BindingFlags.Static)!;

    private CompiledResourceEvaluator(ShaderResourcePlan plan, List<ScalarValue> values, Dictionary<ScalarValue, int> indices,
        Dictionary<ScalarValue, ScalarValue?> phis)
    {
        Values = [.. values];
        _indices = indices;
        SourceWords = plan.DescriptorSources.Select(source => source.Dwords.Select(word => indices[word]).ToArray()).ToArray();
        TableWords = plan.TableReads.Select(read => indices[read.Value]).ToArray();
        _functions = new EvaluateFunction[(Count + NodesPerMethod - 1) / NodesPerMethod];
        for (var chunk = 0; chunk < _functions.Length; chunk++)
            _functions[chunk] = Compile(plan, chunk * NodesPerMethod, phis);
    }

    private static readonly System.Collections.Concurrent.ConcurrentQueue<ShaderResourcePlan> Pending = new();
    private static readonly SemaphoreSlim PendingSignal = new(0);
    private static int _compilerStarted;

    internal static void Enqueue(ShaderResourcePlan plan)
    {
        Pending.Enqueue(plan);
        if (Interlocked.Exchange(ref _compilerStarted, 1) == 0)
        {
            new Thread(CompilePending) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "SRT compiler" }.Start();
        }

        PendingSignal.Release();
    }

    private static void CompilePending()
    {
        while (true)
        {
            PendingSignal.Wait();
            while (Pending.TryDequeue(out var plan)) plan.CompileEvaluatorNow();
        }
    }

    internal bool TryGetIndex(ScalarValue value, out int index) => _indices.TryGetValue(value, out index);
    internal bool Evaluate(RuntimeValueEvaluator evaluator, int index, out ulong result) =>
        _functions[index / NodesPerMethod](evaluator, index, out result);

    internal static CompiledResourceEvaluator? Build(ShaderResourcePlan plan)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported || Environment.GetEnvironmentVariable("SHARPEMU_SRT_NATIVE") == "0") return null;

        var values = new List<ScalarValue>();
        var indices = new Dictionary<ScalarValue, int>();
        var phis = new Dictionary<ScalarValue, ScalarValue?>();
        void Add(ScalarValue value)
        {
            if (indices.TryAdd(value, values.Count)) values.Add(value);
        }
        foreach (var source in plan.DescriptorSources)
            foreach (var word in source.Dwords) Add(word);
        foreach (var read in plan.TableReads) Add(read.Value);
        foreach (var read in plan.DynamicReads) Add(read);
        foreach (var branch in plan.ResourceBranches)
            if (branch.Condition is { } condition) Add(condition);
        foreach (var range in plan.DeviceAddressRanges)
        {
            Add(range.BaseLow);
            Add(range.BaseHigh);
        }
        foreach (var image in plan.IndirectImages) Add(image.Key);
        foreach (var candidates in plan.BufferCandidateTables)
            if (candidates.Limit is { } limit) Add(limit);

        // An iterative walk also admits cycles. At run time, an in-progress dense slot
        // fails just like the interpreter's visiting set, without recursing forever.
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (value.Kind == ScalarValueKind.Phi)
            {
                var invariant = plan.Graph.ResolveInvariantPhi(value);
                phis[value] = invariant;
                if (invariant is not null) Add(invariant);
            }
            else if (value.Kind == ScalarValueKind.FirstLane)
            {
                Add(value.Operands[0]);
            }
            else if (value.Kind == ScalarValueKind.ResourceTableWord)
            {
                if (value.Payload < (ulong)plan.TableReads.Count) Add(plan.TableReads[(int)value.Payload].Value);
            }
            else if (value.Kind is ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord)
            {
                if ((uint)value.MemoryIndex >= (uint)plan.Memory.Count || value.Operands.Length < 2) continue;
                foreach (var word in value.Operands[0].Operands) Add(word);
                Add(value.Operands[1]);
            }
            else if (value.Kind == ScalarValueKind.Select ||
                (value.Kind == ScalarValueKind.Operation && RuntimeValueValidator.IsUniformOperation(value.Operation)))
            {
                foreach (var operand in value.Operands) Add(operand);
            }
        }
        return values.Count == 0 ? null : new(plan, values, indices, phis);
    }

    private EvaluateFunction Compile(ShaderResourcePlan plan, int first, Dictionary<ScalarValue, ScalarValue?> phis)
    {
        var count = Math.Min(NodesPerMethod, Count - first);
        var method = new DynamicMethod($"Srt_{plan.Hash:X16}_{first}", typeof(bool),
            [typeof(RuntimeValueEvaluator), typeof(int), typeof(ulong).MakeByRefType()], typeof(CompiledResourceEvaluator).Module, true);
        var il = method.GetILGenerator();
        var result = il.DeclareLocal(typeof(ulong));
        var success = il.DeclareLocal(typeof(bool));
        var status = il.DeclareLocal(typeof(int));
        var operands = Enumerable.Range(0, 5).Select(_ => il.DeclareLocal(typeof(ulong))).ToArray();
        var failed = il.DefineLabel();
        var store = il.DefineLabel();
        var exit = il.DefineLabel();
        var cases = Enumerable.Range(0, count).Select(_ => il.DefineLabel()).ToArray();

        void Call(string helper) => il.Emit(OpCodes.Call, Helpers[helper]);
        void Constant(ulong value) => il.Emit(OpCodes.Ldc_I8, unchecked((long)value));
        void Operand(ScalarValue value, LocalBuilder destination)
        {
            if (value.IsConstant)
            {
                Constant(value.Payload);
                il.Emit(OpCodes.Stloc, destination);
                return;
            }
            var index = _indices[value];
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, index);
            il.Emit(OpCodes.Ldloca, destination);
            if (index >= first && index < first + count) il.Emit(OpCodes.Call, method);
            else Call(nameof(RuntimeValueEvaluator.EvaluateCompiled));
            il.Emit(OpCodes.Brfalse, failed);
        }
        void Load(int index, bool narrow = false)
        {
            il.Emit(OpCodes.Ldloc, operands[index]);
            if (narrow) il.Emit(OpCodes.Conv_U4);
        }
        void Special(int index)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, index);
            il.Emit(OpCodes.Ldloca, result);
            Call(nameof(RuntimeValueEvaluator.EvaluateCompiledSpecial));
            il.Emit(OpCodes.Brfalse, failed);
        }

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        Call(nameof(RuntimeValueEvaluator.BeginCompiled));
        il.Emit(OpCodes.Stloc, status);
        var compute = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, status);
        il.Emit(OpCodes.Brfalse, compute);
        il.Emit(OpCodes.Ldloc, status);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Cgt);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(compute);
        il.BeginExceptionBlock();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4, first);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Switch, cases);
        il.Emit(OpCodes.Br, failed);

        for (var offset = 0; offset < count; offset++)
        {
            var index = first + offset;
            var value = Values[index];
            il.MarkLabel(cases[offset]);
            switch (value.Kind)
            {
                case ScalarValueKind.Constant:
                    Constant(value.Payload);
                    il.Emit(OpCodes.Stloc, result);
                    break;
                case ScalarValueKind.MemoryAperture:
                    Constant(Gen5InlineConstants.DecodeAperture64((uint)value.Payload) >> 32);
                    il.Emit(OpCodes.Stloc, result);
                    break;
                case ScalarValueKind.UserData:
                    if (value.UserDataRegister < plan.UserDataBase)
                    {
                        il.Emit(OpCodes.Br, failed);
                        break;
                    }
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, unchecked((int)(value.UserDataRegister - plan.UserDataBase)));
                    il.Emit(OpCodes.Ldloca, result);
                    Call(nameof(RuntimeValueEvaluator.ReadUserData));
                    il.Emit(OpCodes.Brfalse, failed);
                    break;
                case ScalarValueKind.ShaderBase:
                    il.Emit(OpCodes.Ldarg_0);
                    Call(nameof(RuntimeValueEvaluator.ReadShaderBase));
                    il.Emit(OpCodes.Stloc, result);
                    break;
                case ScalarValueKind.Phi:
                    if (phis[value] is { } invariant) Operand(invariant, result);
                    else il.Emit(OpCodes.Br, failed);
                    break;
                case ScalarValueKind.FirstLane:
                case ScalarValueKind.ResourceTableWord:
                    // These cross evaluator contexts: active-lane cache or clean reader.
                    Special(index);
                    break;
                case ScalarValueKind.Select:
                {
                    var ordinary = il.DefineLabel();
                    var chosen = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, _indices[value.Operands[0]]);
                    Call(nameof(RuntimeValueEvaluator.IsCompiledActiveMask));
                    il.Emit(OpCodes.Brfalse, ordinary);
                    Operand(value.Operands[1], result);
                    il.Emit(OpCodes.Br, store);
                    il.MarkLabel(ordinary);
                    for (var operand = 0; operand < 3; operand++) Operand(value.Operands[operand], operands[operand]);
                    // Ordinary selects are eager, including reads on the inactive arm.
                    Load(0);
                    il.Emit(OpCodes.Brfalse, chosen);
                    Load(1);
                    il.Emit(OpCodes.Stloc, result);
                    il.Emit(OpCodes.Br, store);
                    il.MarkLabel(chosen);
                    Load(2);
                    il.Emit(OpCodes.Stloc, result);
                    break;
                }
                case ScalarValueKind.ScalarAddressWord:
                case ScalarValueKind.ScalarBufferWord:
                {
                    var buffer = value.Kind == ScalarValueKind.ScalarBufferWord;
                    if ((uint)value.MemoryIndex >= (uint)plan.Memory.Count || value.Operands.Length < 2 ||
                        value.Operands[0].Operands.Length < 2 || (buffer && value.Operands[0].Operands.Length != 4))
                    {
                        il.Emit(OpCodes.Br, failed);
                        break;
                    }
                    var handle = value.Operands[0];
                    Operand(handle.Operands[0], operands[0]);
                    Operand(handle.Operands[1], operands[1]);
                    Operand(value.Operands[1], operands[2]);
                    if (buffer)
                    {
                        Operand(handle.Operands[2], operands[3]);
                        Operand(handle.Operands[3], operands[4]);
                    }
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, (int)value.Kind);
                    Load(0);
                    Load(1);
                    Load(2, narrow: true);
                    if (buffer) Load(3);
                    else Constant(0);
                    Constant(unchecked((ulong)(long)(int)plan.Memory[value.MemoryIndex].Offset));
                    il.Emit(OpCodes.Ldloca, result);
                    Call(nameof(RuntimeValueEvaluator.ReadRawWord));
                    il.Emit(OpCodes.Brfalse, failed);
                    break;
                }
                case ScalarValueKind.Operation:
                    if (!RuntimeValueValidator.IsUniformOperation(value.Operation))
                    {
                        il.Emit(OpCodes.Br, failed);
                        break;
                    }
                    if (value.Operands.Length > 4)
                    {
                        Special(index);
                        break;
                    }
                    for (var operand = 0; operand < 4; operand++)
                    {
                        if (operand < value.Operands.Length) Operand(value.Operands[operand], operands[operand]);
                        else
                        {
                            Constant(0);
                            il.Emit(OpCodes.Stloc, operands[operand]);
                        }
                    }
                    if (EmitArithmetic(il, value.Operation, operands)) il.Emit(OpCodes.Stloc, result);
                    else
                    {
                        il.Emit(OpCodes.Ldc_I4, (int)value.Operation);
                        for (var operand = 0; operand < 4; operand++) Load(operand);
                        il.Emit(OpCodes.Ldloca, result);
                        il.Emit(OpCodes.Call, EvaluateOperation);
                        il.Emit(OpCodes.Brfalse, failed);
                    }
                    break;
                default:
                    il.Emit(OpCodes.Br, failed);
                    break;
            }
            il.Emit(OpCodes.Br, store);
        }

        il.MarkLabel(store);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, result);
        Call(nameof(RuntimeValueEvaluator.StoreCompiled));
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Stind_I8);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, success);
        il.Emit(OpCodes.Leave, exit);
        il.MarkLabel(failed);
        il.Emit(OpCodes.Leave, exit);
        il.BeginFinallyBlock();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        Call(nameof(RuntimeValueEvaluator.EndCompiled));
        il.EndExceptionBlock();
        il.MarkLabel(exit);
        il.Emit(OpCodes.Ldloc, success);
        il.Emit(OpCodes.Ret);
        var function = method.CreateDelegate<EvaluateFunction>();
        RuntimeHelpers.PrepareDelegate(function);
        return function;
    }

    private static bool EmitArithmetic(ILGenerator il, ScalarOperation operation, LocalBuilder[] operands)
    {
        if (operation is ScalarOperation.Construct64 or ScalarOperation.AddCarry32 or ScalarOperation.UMulHi32)
        {
            for (var operand = 0; operand < 2; operand++)
            {
                il.Emit(OpCodes.Ldloc, operands[operand]);
                il.Emit(OpCodes.Conv_U4);
                il.Emit(OpCodes.Conv_U8);
                if (operand == 1 && operation == ScalarOperation.Construct64)
                {
                    il.Emit(OpCodes.Ldc_I4, 32);
                    il.Emit(OpCodes.Shl);
                }
            }
            il.Emit(operation == ScalarOperation.Construct64 ? OpCodes.Or :
                operation == ScalarOperation.AddCarry32 ? OpCodes.Add : OpCodes.Mul);
            if (operation == ScalarOperation.UMulHi32)
            {
                il.Emit(OpCodes.Ldc_I4, 32);
                il.Emit(OpCodes.Shr_Un);
            }
            return true;
        }
        var narrow = operation is ScalarOperation.IAdd32 or ScalarOperation.ISub32 or ScalarOperation.IMul32 or
            ScalarOperation.And32 or ScalarOperation.Or32 or ScalarOperation.Xor32 or ScalarOperation.Not32 or
            ScalarOperation.ShiftLeft32 or ScalarOperation.ShiftRightLogical32 or ScalarOperation.ShiftRightArithmetic32;
        OpCode opcode;
        switch (operation)
        {
            case ScalarOperation.IAdd32: case ScalarOperation.IAdd64: opcode = OpCodes.Add; break;
            case ScalarOperation.ISub32: case ScalarOperation.ISub64: opcode = OpCodes.Sub; break;
            case ScalarOperation.IMul32: case ScalarOperation.IMul64: opcode = OpCodes.Mul; break;
            case ScalarOperation.And32: case ScalarOperation.And64: opcode = OpCodes.And; break;
            case ScalarOperation.Or32: opcode = OpCodes.Or; break;
            case ScalarOperation.Xor32: opcode = OpCodes.Xor; break;
            case ScalarOperation.Not32: opcode = OpCodes.Not; break;
            case ScalarOperation.ShiftLeft32: case ScalarOperation.ShiftLeft64: opcode = OpCodes.Shl; break;
            case ScalarOperation.ShiftRightLogical32: case ScalarOperation.ShiftRightLogical64: opcode = OpCodes.Shr_Un; break;
            case ScalarOperation.ShiftRightArithmetic32: case ScalarOperation.ShiftRightArithmetic64: opcode = OpCodes.Shr; break;
            default: return false;
        }
        il.Emit(OpCodes.Ldloc, operands[0]);
        if (narrow) il.Emit(OpCodes.Conv_U4);
        if (operation != ScalarOperation.Not32)
        {
            il.Emit(OpCodes.Ldloc, operands[1]);
            if (opcode == OpCodes.Shl || opcode == OpCodes.Shr || opcode == OpCodes.Shr_Un)
            {
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Ldc_I4, narrow ? 31 : 63);
                il.Emit(OpCodes.And);
            }
            else if (narrow) il.Emit(OpCodes.Conv_U4);
        }
        il.Emit(opcode);
        if (narrow) il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Conv_U8);
        return true;
    }
}
