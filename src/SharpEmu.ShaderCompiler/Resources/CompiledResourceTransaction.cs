// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SharpEmu.ShaderCompiler.Resources;

internal sealed class CompiledResourceTransaction
{
    private static readonly MethodInfo EvaluateOperation = typeof(ScalarOperationSemantics)
        .GetMethod(nameof(ScalarOperationSemantics.TryEvaluateFixed), BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly Func<ResourceRuntimeInputs, List<DescriptorWords>, uint[], bool> _evaluate;
    private readonly int[] _descriptorSizes;
    private readonly int _tableWords;
    private readonly bool _requiresCleanReader;
    private readonly bool _tableOnly;

    private CompiledResourceTransaction(Func<ResourceRuntimeInputs, List<DescriptorWords>, uint[], bool> evaluate,
        int[] descriptorSizes, int tableWords, bool requiresCleanReader, bool tableOnly)
    {
        _evaluate = evaluate;
        _descriptorSizes = descriptorSizes;
        _tableWords = tableWords;
        _requiresCleanReader = requiresCleanReader;
        _tableOnly = tableOnly;
    }

    internal bool Evaluate(ResourceRuntimeInputs inputs, int additionalTableWords,
        out List<DescriptorWords> descriptors, out uint[] table)
    {
        using var profile = ResourceMaterializationProfile.Measure(_tableOnly
            ? ResourceMaterializationProfile.Phase.CompiledTableEvaluation : ResourceMaterializationProfile.Phase.CompiledEvaluation);
        descriptors = [];
        table = [];
        if (_requiresCleanReader && inputs.ReadCleanMemory is null) return false;
        List<DescriptorWords> nextDescriptors;
        uint[] nextTable;
        using (ResourceMaterializationProfile.MeasureSampled(ResourceMaterializationProfile.Phase.CompiledOutputAllocation))
        {
            nextDescriptors = new List<DescriptorWords>(_descriptorSizes.Length);
            foreach (var size in _descriptorSizes)
                nextDescriptors.Add(new DescriptorWords(new uint[size]));
            nextTable = new uint[checked(_tableWords + additionalTableWords)];
        }
        // No scratch array enters a published snapshot. A failed call retains empty outputs.
        using (ResourceMaterializationProfile.MeasureSampled(ResourceMaterializationProfile.Phase.CompiledBody))
            if (!_evaluate(inputs, nextDescriptors, nextTable)) return false;
        descriptors = nextDescriptors;
        table = nextTable;
        return true;
    }

    [InlineArray(16)]
    private struct ReadGroupWords
    {
        private uint _first;
    }

    private sealed record ReadSite(ParameterExpression Low, ParameterExpression High,
        long Relative, bool Clean, ParameterExpression Result, ScalarValue Value);

    internal static CompiledResourceTransaction? Build(ShaderResourcePlan plan, bool tableOnly = false)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported || Environment.GetEnvironmentVariable("SHARPEMU_SRT_NATIVE") == "0") return null;
        if (!tableOnly && RuntimeValueEvaluator.HasResourceStructure(plan))
        {
            ResourceMaterializationCache.RecordTransactionBuild(plan, tableOnly, "structured_plan");
            return null;
        }
        var rejectionReason = "unsupported_shape_or_limit";
        var input = Expression.Parameter(typeof(ResourceRuntimeInputs));
        var output = Expression.Parameter(typeof(uint[]));
        var descriptorOutput = Expression.Parameter(typeof(List<DescriptorWords>));
        var done = Expression.Label(typeof(bool));
        var locals = new List<ParameterExpression>();
        var descriptors = new List<Expression>();
        var table = new List<Expression>();
        var body = descriptors;
        var known = new Dictionary<(ScalarValue, bool), ParameterExpression>();
        var visiting = new HashSet<(ScalarValue, bool)>();
        var readSites = new Dictionary<Expression, ReadSite>();
        var batch = Expression.Variable(typeof(ReadGroupWords));
        var groups = 0;
        Expression Call(string name, params Expression[] arguments) => Expression.Call(
            typeof(CompiledResourceTransaction).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!, arguments);
        void Require(Expression condition) => body.Add(Expression.IfThen(Expression.Not(condition),
            Expression.Return(done, Expression.Constant(false))));
        ParameterExpression CompileValue(ScalarValue value, bool clean)
        {
            if (known.TryGetValue((value, clean), out var existing)) return existing;
            if (!visiting.Add((value, clean)) || visiting.Count > 64 || locals.Count > 4096) throw new NotSupportedException();
            var local = Expression.Variable(typeof(ulong));
            locals.Add(local);
            switch (value.Kind)
            {
                case ScalarValueKind.Constant:
                    body.Add(Expression.Assign(local, Expression.Constant(value.Payload)));
                    break;
                case ScalarValueKind.UserData:
                    if (value.UserDataRegister < plan.UserDataBase) throw new NotSupportedException();
                    Require(Call(nameof(ReadUserData), input, Expression.Constant((ulong)(value.UserDataRegister - plan.UserDataBase)), local));
                    break;
                case ScalarValueKind.ShaderBase:
                    body.Add(Expression.Assign(local, Expression.Property(input, nameof(ResourceRuntimeInputs.ShaderBase))));
                    break;
                case ScalarValueKind.ResourceTableWord:
                    if (value.Payload >= (ulong)plan.TableReads.Count) throw new NotSupportedException();
                    var slot = (int)value.Payload;
                    body.Add(Expression.Assign(local, CompileValue(plan.TableReads[slot].Value,
                        clean || (slot < plan.CleanFlatSlots.Count && plan.CleanFlatSlots[slot] != 0))));
                    break;
                case ScalarValueKind.ScalarAddressWord:
                case ScalarValueKind.ScalarBufferWord:
                    if (value.Operands.Length < 2 || (uint)value.MemoryIndex >= plan.Memory.Count) throw new NotSupportedException();
                    var handle = value.Operands[0];
                    if (handle.Operands.Length < 2) throw new NotSupportedException();
                    var low = CompileValue(handle.Operands[0], clean);
                    var high = CompileValue(handle.Operands[1], clean);
                    var offset = CompileValue(value.Operands[1], clean);
                    Expression records = Expression.Constant(0UL);
                    if (value.Kind == ScalarValueKind.ScalarBufferWord)
                    {
                        if (handle.Operands.Length != 4) throw new NotSupportedException();
                        records = CompileValue(handle.Operands[2], clean);
                        CompileValue(handle.Operands[3], clean);
                    }
                    Require(Call(nameof(ReadWord), input, Expression.Constant(clean), Expression.Constant(value.Kind), low, high,
                        offset, records, Expression.Constant((long)(int)plan.Memory[value.MemoryIndex].Offset),
                        Expression.Constant(RuntimeValueEvaluator.IsAbsoluteAddress(value.Kind, handle)), local,
                        Expression.Constant(plan), Expression.Constant(value)));
                    if (value.Kind == ScalarValueKind.ScalarAddressWord && value.Operands[1].IsConstant)
                        readSites.Add(body[^1], new ReadSite(low, high,
                            ((long)(int)plan.Memory[value.MemoryIndex].Offset & ~3L) + ((uint)value.Operands[1].Payload & ~3u), clean, local, value));
                    break;
                case ScalarValueKind.MemoryAperture:
                    body.Add(Expression.Assign(local, Expression.Constant(Gen5InlineConstants.DecodeAperture64((uint)value.Payload) >> 32)));
                    break;
                case ScalarValueKind.Phi:
                    var invariant = plan.Graph.ResolveInvariantPhi(value) ?? throw new NotSupportedException();
                    body.Add(Expression.Assign(local, CompileValue(invariant, clean)));
                    break;
                case ScalarValueKind.Select:
                {
                    if (value.Operands.Length != 3) throw new NotSupportedException();
                    // Evaluate both arms, as the scalar evaluator does.
                    var condition = CompileValue(value.Operands[0], clean);
                    var whenTrue = CompileValue(value.Operands[1], clean);
                    var whenFalse = CompileValue(value.Operands[2], clean);
                    body.Add(Expression.Assign(local, Expression.Condition(
                        Expression.NotEqual(condition, Expression.Constant(0UL)), whenTrue, whenFalse)));
                    break;
                }
                case ScalarValueKind.Operation:
                {
                    if (!RuntimeValueValidator.IsUniformOperation(value.Operation) || value.Operands.Length > 4)
                    {
                        rejectionReason = $"operation:{value.Operation}";
                        throw new NotSupportedException();
                    }
                    var operands = new Expression[4];
                    for (var index = 0; index < operands.Length; index++)
                        operands[index] = index < value.Operands.Length ? CompileValue(value.Operands[index], clean) : Expression.Constant(0UL);
                    Require(Expression.Call(EvaluateOperation, Expression.Constant(value.Operation),
                        operands[0], operands[1], operands[2], operands[3], local));
                    break;
                }
                default:
                    rejectionReason = $"node:{value.Kind}";
                    throw new NotSupportedException();
            }
            visiting.Remove((value, clean));
            known.Add((value, clean), local);
            return local;
        }
        void GroupReads(List<Expression> statements)
        {
            for (var first = 0; first < statements.Count; first++)
            {
                if (!readSites.TryGetValue(statements[first], out var start)) continue;
                var reads = new List<ReadSite> { start };
                var last = first;
                for (var next = first + 1; next < statements.Count && reads.Count < 16; next++)
                {
                    var statement = statements[next];
                    if (readSites.TryGetValue(statement, out var read))
                    {
                        if (read.Low != start.Low || read.High != start.High || read.Clean != start.Clean ||
                            read.Relative != start.Relative + reads.Count * 4) break;
                        reads.Add(read);
                        last = next;
                    }
                    else if (statement is not BinaryExpression { NodeType: ExpressionType.Assign } assignment ||
                        !(assignment.Right is ConstantExpression || assignment.Left is IndexExpression)) break;
                }
                if (reads.Count < 2) continue;
                var original = statements.GetRange(first, last - first + 1);
                var success = new List<Expression>();
                for (var index = 0; index < reads.Count; index++)
                    success.Add(Expression.Assign(reads[index].Result,
                        Expression.Convert(Call(nameof(ReadGroupWord), batch, Expression.Constant(index)), typeof(ulong))));
                success.AddRange(original.Where(statement => !readSites.ContainsKey(statement)));
                success.Add(Expression.Empty());
                original.Add(Expression.Empty());
                var replacement = Expression.IfThenElse(Call(nameof(ReadGroup), input, Expression.Constant(start.Clean),
                    start.Low, start.High, Expression.Constant(start.Relative), Expression.Constant(reads.Count), batch,
                    Expression.Constant(plan), Expression.Constant(reads.Select(read => read.Value).ToArray())),
                    Expression.Block(success), Expression.Block(original));
                statements.RemoveRange(first, last - first + 1);
                statements.Insert(first, replacement);
                groups++;
            }
        }
        try
        {
            var sourceIndex = 0;
            foreach (var source in tableOnly ? Array.Empty<uint>() : plan.MaterializationSources)
            {
                var descriptor = Expression.Property(descriptorOutput, "Item", Expression.Constant(sourceIndex++));
                var words = Expression.Property(descriptor, nameof(DescriptorWords.Dwords));
                var wordIndex = 0;
                foreach (var word in plan.DescriptorSources[(int)source].Dwords)
                {
                    var value = CompileValue(word, false);
                    body.Add(Expression.Assign(Expression.ArrayAccess(words, Expression.Constant(wordIndex++)),
                        Expression.Convert(value, typeof(uint))));
                }
            }
            body = table;
            foreach (var read in plan.TableReads)
            {
                if (read.FlatOffset >= plan.TableReads.Count) return null;
                var value = CompileValue(read.Value, read.FlatOffset < plan.CleanFlatSlots.Count && plan.CleanFlatSlots[(int)read.FlatOffset] != 0);
                body.Add(Expression.Assign(Expression.ArrayAccess(output, Expression.Constant((int)read.FlatOffset)),
                    Expression.Convert(value, typeof(uint))));
            }
            // Group only ordered reads with one base and reader policy. Keep parent reads outside the group.
            GroupReads(descriptors);
            GroupReads(table);
            if (groups > 0) locals.Add(batch);
            table.Add(Expression.Empty());
            descriptors.Add(Call(nameof(SetTablePhase), input, Expression.Constant(true)));
            descriptors.Add(Expression.TryFinally(Expression.Block(table), Call(nameof(SetTablePhase), input, Expression.Constant(false))));
            descriptors.Add(Expression.Label(done, Expression.Constant(true)));
            return new CompiledResourceTransaction(
                Expression.Lambda<Func<ResourceRuntimeInputs, List<DescriptorWords>, uint[], bool>>(
                    Expression.Block(locals, descriptors), input, descriptorOutput, output).Compile(),
                tableOnly ? [] : plan.MaterializationSources.Select(index => plan.DescriptorSources[(int)index].Dwords.Length).ToArray(),
                plan.TableReads.Count, plan.CleanFlatSlots.Any(value => value != 0), tableOnly);
        }
        catch (NotSupportedException)
        {
            ResourceMaterializationCache.RecordTransactionBuild(plan, tableOnly, rejectionReason);
            return null;
        }
    }

    private static void SetTablePhase(ResourceRuntimeInputs inputs, bool table) => inputs.TablePhase?.Invoke(table);

    private static bool ReadGroup(ResourceRuntimeInputs inputs, bool clean, ulong low, ulong high, long relative, int count,
        out ReadGroupWords values, ShaderResourcePlan plan, ScalarValue[] nodes)
    {
        values = default;
        ResourceMaterializationProfile.CountRead(ResourceMaterializationProfile.ReadCounter.GroupAttempt);
        var reader = inputs.ReadResidentMemory;
        var addressBase = ((high << 32) | (uint)low) & 0x0000FFFFFFFFFFFFUL;
        if (count < 2 || count > 16 || reader is null || (clean ? inputs.ReadCleanMemory : inputs.ReadMemory) is null || (addressBase & ~3UL) == 0 ||
            RuntimeValueEvaluator.ResolveRawAddress(ScalarValueKind.ScalarAddressWord, addressBase, high, 0, relative, 0, out var first) != RuntimeValueEvaluator.RawAddress.Read ||
            RuntimeValueEvaluator.ResolveRawAddress(ScalarValueKind.ScalarAddressWord, addressBase, high, 0, relative + (count - 1) * 4, 0, out var last) != RuntimeValueEvaluator.RawAddress.Read ||
            last - first != (ulong)(count - 1) * 4)
        {
            ResourceMaterializationProfile.CountRead(ResourceMaterializationProfile.ReadCounter.GroupIneligible);
            return false;
        }
        Span<byte> bytes = stackalloc byte[count * 4];
        using (ResourceMaterializationProfile.MeasureSampled(ResourceMaterializationProfile.Phase.CompiledGroupRead))
            if (!reader(first, bytes, clean || inputs.ReadsClean))
            {
                ResourceMaterializationProfile.CountRead(ResourceMaterializationProfile.ReadCounter.GroupRefused);
                return false;
            }
        ResourceMaterializationProfile.CountRead(ResourceMaterializationProfile.ReadCounter.GroupSucceeded);
        ResourceMaterializationProfile.CountRead(ResourceMaterializationProfile.ReadCounter.GroupWords, count);
        for (var index = 0; index < count; index++)
        {
            values[index] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[(index * 4)..]);
            ResourceMaterializationCache.RecordReadConsumer(plan, nodes[index], first + (ulong)index * 4);
        }
        return true;
    }

    private static uint ReadGroupWord(ref ReadGroupWords words, int index) => words[index];

    private static bool ReadUserData(ResourceRuntimeInputs inputs, ulong index, out ulong value)
    {
        value = 0;
        if (index >= (ulong)inputs.UserData.Count) return false;
        value = inputs.UserData[(int)index];
        return true;
    }

    private static bool ReadWord(ResourceRuntimeInputs inputs, bool clean, ScalarValueKind kind, ulong low, ulong high,
        ulong offset, ulong records, long immediate, bool absolute, out ulong value, ShaderResourcePlan plan, ScalarValue node)
    {
        value = 0;
        var addressBase = ((high << 32) | (uint)low) & 0x0000FFFFFFFFFFFFUL;
        ResourceMaterializationProfile.CountRead(kind == ScalarValueKind.ScalarBufferWord
            ? ResourceMaterializationProfile.ReadCounter.ScalarBuffer : ResourceMaterializationProfile.ReadCounter.ScalarAddress);
        var state = RuntimeValueEvaluator.ResolveRawAddress(kind, addressBase, high, records, immediate, (uint)offset, out var address);
        if (state == RuntimeValueEvaluator.RawAddress.Failed) return false;
        if (state == RuntimeValueEvaluator.RawAddress.Zero) return true;
        var reader = clean ? inputs.ReadCleanMemory : inputs.ReadMemory;
        uint word;
        // As in the evaluator: a null base, or an absolute address the shader carries that nothing maps, reads zero.
        using (ResourceMaterializationProfile.MeasureSampled(ResourceMaterializationProfile.Phase.CompiledWordRead))
            if (reader is null || !reader(address, out word)) return (addressBase & ~3UL) == 0 || (absolute && reader is not null && !clean && !inputs.ReadsClean);
        value = word;
        ResourceMaterializationCache.RecordReadConsumer(plan, node, address);
        return true;
    }
}
