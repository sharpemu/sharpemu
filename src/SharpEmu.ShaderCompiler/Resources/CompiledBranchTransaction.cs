// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SharpEmu.ShaderCompiler.Resources;

// Values one call already evaluated before a compiled branch transaction runs: the branch selection's clean evaluator.
internal interface IEvaluatedValues
{
    bool TryGetEvaluated(ScalarValue value, out ulong result);
}

// The compiled form of a plan whose uniform branches select its descriptor sources (ResourceBranches), which
// CompiledResourceTransaction refuses. RuntimeValueEvaluator.EvaluateActiveSources still selects the sources, fresh on
// every call; this evaluates what the fallback evaluates after that selection, in its order and with its memo: the words
// of each active source in MaterializationSources order, then every table read. A value is evaluated in the first active
// region (a source or the table) that needs it, in the order the fallback's recursion meets it, and never again: an
// inactive source reads nothing, a value it shares with a later region is read there (inside the table phase when that
// region is the table), and a clean value the selection already evaluated is taken from the selection.
internal sealed class CompiledBranchTransaction
{
    private static readonly MethodInfo EvaluateOperation = typeof(ScalarOperationSemantics)
        .GetMethod(nameof(ScalarOperationSemantics.TryEvaluateFixed), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo ReadWord = typeof(CompiledResourceTransaction)
        .GetMethod(nameof(CompiledResourceTransaction.ReadWord), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo ReadUserData = typeof(CompiledResourceTransaction)
        .GetMethod(nameof(CompiledResourceTransaction.ReadUserData), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo SetTablePhase = typeof(CompiledResourceTransaction)
        .GetMethod(nameof(CompiledResourceTransaction.SetTablePhase), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo TryGetEvaluated = typeof(IEvaluatedValues)
        .GetMethod(nameof(IEvaluatedValues.TryGetEvaluated))!;

    private readonly Func<ResourceRuntimeInputs, bool[], IEvaluatedValues, List<DescriptorWords>, uint[], bool> _evaluate;
    private readonly int[] _descriptorSizes;
    private readonly int _tableWords;
    private readonly int _sourceCount;
    private readonly bool _requiresCleanReader;

    private CompiledBranchTransaction(Func<ResourceRuntimeInputs, bool[], IEvaluatedValues, List<DescriptorWords>, uint[], bool> evaluate,
        int[] descriptorSizes, int tableWords, int sourceCount, bool requiresCleanReader)
    {
        _evaluate = evaluate;
        _descriptorSizes = descriptorSizes;
        _tableWords = tableWords;
        _sourceCount = sourceCount;
        _requiresCleanReader = requiresCleanReader;
    }

    // As the fallback after its selection: on failure neither output changes and both stay empty.
    internal bool Evaluate(ResourceRuntimeInputs inputs, int additionalTableWords, bool[] active, IEvaluatedValues selection,
        out List<DescriptorWords> descriptors, out uint[] table)
    {
        using var profile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.CompiledBranchEvaluation);
        descriptors = [];
        table = [];
        if (active.Length != _sourceCount || (_requiresCleanReader && inputs.ReadCleanMemory is null)) return false;
        var nextDescriptors = new List<DescriptorWords>(_descriptorSizes.Length);
        foreach (var size in _descriptorSizes)
            nextDescriptors.Add(new DescriptorWords(new uint[size]));
        var nextTable = new uint[checked(_tableWords + additionalTableWords)];
        if (!_evaluate(inputs, active, selection, nextDescriptors, nextTable)) return false;
        descriptors = nextDescriptors;
        table = nextTable;
        return true;
    }

    internal static CompiledBranchTransaction? Build(ShaderResourcePlan plan)
    {
        if (plan.ResourceBranches.Count == 0 || !RuntimeFeature.IsDynamicCodeSupported ||
            Environment.GetEnvironmentVariable("SHARPEMU_SRT_NATIVE") == "0") return null;
        var builder = new Builder(plan);
        try
        {
            return builder.Build();
        }
        catch (NotSupportedException)
        {
            ResourceMaterializationCache.RecordTransactionBuild(plan, false, "branches:" + builder.Reason);
            return null;
        }
    }

    // One value as one evaluator context reads it: the general reader, or the clean one (a clean table slot and
    // everything below it). The fallback memoizes each pair once per call; the transaction gives each a local.
    private sealed class Pair(ScalarValue value, bool clean)
    {
        public readonly ScalarValue Value = value;
        public readonly bool Clean = clean;
        public ParameterExpression? Local;
        public ParameterExpression? Flag;
        public int FirstRegion = -1;
        public int LastRegion = -1;
        public int Regions;
        public bool Selection;
    }

    // A region the fallback evaluates in order: one MaterializationSources entry (Position) or the table.
    private readonly record struct Region(int Position, uint Source, bool Conditional, bool Table);

    private sealed class Builder(ShaderResourcePlan plan)
    {
        private const int MaximumDepth = 64;
        private const int MaximumLocals = 4096;
        private readonly Dictionary<(ScalarValue, bool), Pair> _pairs = new();
        private readonly List<ParameterExpression> _locals = [];
        private readonly ParameterExpression _input = Expression.Parameter(typeof(ResourceRuntimeInputs), "inputs");
        private readonly ParameterExpression _active = Expression.Parameter(typeof(bool[]), "active");
        private readonly ParameterExpression _selection = Expression.Parameter(typeof(IEvaluatedValues), "selection");
        private readonly ParameterExpression _descriptors = Expression.Parameter(typeof(List<DescriptorWords>), "descriptors");
        private readonly ParameterExpression _table = Expression.Parameter(typeof(uint[]), "table");
        private readonly LabelTarget _done = Expression.Label(typeof(bool));

        public string Reason { get; private set; } = "unsupported_shape_or_limit";

        public CompiledBranchTransaction Build()
        {
            var conditional = new HashSet<uint>();
            foreach (var block in plan.ResourceBranches)
                foreach (var source in block.Sources) conditional.Add(source);
            var regions = new List<Region>();
            for (var position = 0; position < plan.MaterializationSources.Count; position++)
            {
                var source = plan.MaterializationSources[position];
                // The fallback fails every call on such a source; it keeps serving them.
                if (source >= (uint)plan.DescriptorSources.Count) throw new NotSupportedException();
                regions.Add(new Region(position, source, conditional.Contains(source), false));
            }

            foreach (var read in plan.TableReads)
                if (read.FlatOffset >= (uint)plan.TableReads.Count) throw new NotSupportedException();
            regions.Add(new Region(-1, 0, false, true));

            // Each region's values in the order the fallback's recursion completes them: operands first, then the value.
            var orders = new List<Pair>[regions.Count];
            for (var region = 0; region < regions.Count; region++)
            {
                var order = orders[region] = [];
                var seen = new HashSet<Pair>();
                var visiting = new HashSet<(ScalarValue, bool)>();
                if (regions[region].Table)
                {
                    foreach (var read in plan.TableReads)
                        Visit(read.Value, IsCleanSlot((int)read.FlatOffset), region, seen, order, visiting);
                }
                else
                {
                    foreach (var word in plan.DescriptorSources[(int)regions[region].Source].Dwords)
                        Visit(word, false, region, seen, order, visiting);
                }
            }

            // The clean values the selection may already hold: everything its conditions evaluate.
            var selection = new HashSet<(ScalarValue, bool)>();
            foreach (var block in plan.ResourceBranches)
                if (block.Condition is { } condition) CollectSelection(condition, selection);
            foreach (var pair in _pairs.Values)
                pair.Selection = pair.Clean && selection.Contains((pair.Value, true));

            var body = new List<Expression>();
            // Evaluated by an unconditional region: every later region already has it.
            var settled = new HashSet<Pair>();
            for (var region = 0; region < regions.Count; region++)
            {
                var info = regions[region];
                var statements = new List<Expression>();
                foreach (var pair in orders[region])
                    if (!settled.Contains(pair)) EmitSite(pair, regions, statements);
                if (info.Table)
                {
                    foreach (var read in plan.TableReads)
                        statements.Add(Expression.Assign(Expression.ArrayAccess(_table, Expression.Constant((int)read.FlatOffset)),
                            Word(read.Value, IsCleanSlot((int)read.FlatOffset))));
                    statements.Add(Expression.Empty());
                    body.Add(Expression.Call(SetTablePhase, _input, Expression.Constant(true)));
                    body.Add(Expression.TryFinally(Expression.Block(statements),
                        Expression.Call(SetTablePhase, _input, Expression.Constant(false))));
                    continue;
                }

                var words = Expression.Property(Expression.Property(_descriptors, "Item", Expression.Constant(info.Position)),
                    nameof(DescriptorWords.Dwords));
                var dwords = plan.DescriptorSources[(int)info.Source].Dwords;
                for (var index = 0; index < dwords.Length; index++)
                    statements.Add(Expression.Assign(Expression.ArrayAccess(words, Expression.Constant(index)), Word(dwords[index], false)));
                if (info.Conditional)
                {
                    statements.Add(Expression.Empty());
                    body.Add(Expression.IfThen(Expression.ArrayIndex(_active, Expression.Constant((int)info.Source)),
                        Expression.Block(statements)));
                }
                else
                {
                    body.AddRange(statements);
                    foreach (var pair in orders[region]) settled.Add(pair);
                }
            }

            body.Add(Expression.Label(_done, Expression.Constant(true)));
            var lambda = Expression.Lambda<Func<ResourceRuntimeInputs, bool[], IEvaluatedValues, List<DescriptorWords>, uint[], bool>>(
                Expression.Block(_locals, body), _input, _active, _selection, _descriptors, _table);
            return new CompiledBranchTransaction(lambda.Compile(),
                plan.MaterializationSources.Select(index => plan.DescriptorSources[(int)index].Dwords.Length).ToArray(),
                plan.TableReads.Count, plan.DescriptorSources.Count, plan.CleanFlatSlots.Any(slot => slot != 0));
        }

        private bool IsCleanSlot(int slot) => slot < plan.CleanFlatSlots.Count && plan.CleanFlatSlots[slot] != 0;

        private void Visit(ScalarValue value, bool clean, int region, HashSet<Pair> seen, List<Pair> order,
            HashSet<(ScalarValue, bool)> visiting)
        {
            if (value.IsConstant) return;
            if (!_pairs.TryGetValue((value, clean), out var pair))
            {
                if (_pairs.Count >= MaximumLocals) throw new NotSupportedException();
                _pairs.Add((value, clean), pair = new Pair(value, clean));
            }

            if (seen.Contains(pair)) return;
            if (!visiting.Add((value, clean)) || visiting.Count > MaximumDepth) throw new NotSupportedException();
            if (!TryOperands(value, clean, out var operands, out var reason))
            {
                Reason = reason;
                throw new NotSupportedException();
            }

            foreach (var (operand, operandClean) in operands)
                Visit(operand, operandClean, region, seen, order, visiting);
            visiting.Remove((value, clean));
            seen.Add(pair);
            order.Add(pair);
            if (pair.LastRegion == region) return;
            if (pair.FirstRegion < 0) pair.FirstRegion = region;
            pair.LastRegion = region;
            pair.Regions++;
        }

        // The values the selection's clean evaluator meets below a condition, as far as the transaction can name them.
        private void CollectSelection(ScalarValue condition, HashSet<(ScalarValue, bool)> selection)
        {
            var pending = new Stack<ScalarValue>();
            pending.Push(condition);
            while (pending.TryPop(out var value))
            {
                if (value.IsConstant || !selection.Add((value, true)) || !TryOperands(value, true, out var operands, out _)) continue;
                foreach (var (operand, _) in operands) pending.Push(operand);
            }
        }

        // The operands a value's evaluation evaluates, in the order RuntimeValueEvaluator and CompiledResourceTransaction
        // evaluate them, with the reader each uses.
        private bool TryOperands(ScalarValue value, bool clean, out (ScalarValue, bool)[] operands, out string reason)
        {
            operands = [];
            reason = "unsupported_shape_or_limit";
            switch (value.Kind)
            {
                case ScalarValueKind.UserData:
                    return value.UserDataRegister >= plan.UserDataBase;
                case ScalarValueKind.ShaderBase:
                case ScalarValueKind.MemoryAperture:
                    return true;
                case ScalarValueKind.ResourceTableWord:
                    if (value.Payload >= (ulong)plan.TableReads.Count) return false;
                    operands = [(plan.TableReads[(int)value.Payload].Value, clean || IsCleanSlot((int)value.Payload))];
                    return true;
                case ScalarValueKind.ScalarAddressWord:
                case ScalarValueKind.ScalarBufferWord:
                {
                    if (value.Operands.Length < 2 || (uint)value.MemoryIndex >= (uint)plan.Memory.Count) return false;
                    var handle = value.Operands[0];
                    if (value.Kind == ScalarValueKind.ScalarAddressWord)
                    {
                        if (handle.Operands.Length < 2) return false;
                        operands = [(handle.Operands[0], clean), (handle.Operands[1], clean), (value.Operands[1], clean)];
                        return true;
                    }

                    if (handle.Operands.Length != 4) return false;
                    operands = [(handle.Operands[0], clean), (handle.Operands[1], clean), (value.Operands[1], clean),
                        (handle.Operands[2], clean), (handle.Operands[3], clean)];
                    return true;
                }
                case ScalarValueKind.Phi:
                    if (plan.Graph.ResolveInvariantPhi(value) is not { } invariant) return false;
                    operands = [(invariant, clean)];
                    return true;
                case ScalarValueKind.Select:
                    if (value.Operands.Length != 3) return false;
                    operands = [(value.Operands[0], clean), (value.Operands[1], clean), (value.Operands[2], clean)];
                    return true;
                case ScalarValueKind.Operation:
                    if (!RuntimeValueValidator.IsUniformOperation(value.Operation) || value.Operands.Length > 4)
                    {
                        reason = $"operation:{value.Operation}";
                        return false;
                    }

                    operands = value.Operands.Select(operand => (operand, clean)).ToArray();
                    return true;
                default:
                    reason = $"node:{value.Kind}";
                    return false;
            }
        }

        private ParameterExpression Local(Type type)
        {
            var local = Expression.Variable(type);
            _locals.Add(local);
            if (_locals.Count > MaximumLocals) throw new NotSupportedException();
            return local;
        }

        private Expression Operand(ScalarValue value, bool clean) =>
            value.IsConstant ? Expression.Constant(value.Payload) : _pairs[(value, clean)].Local!;

        private Expression Word(ScalarValue value, bool clean) =>
            value.IsConstant ? Expression.Constant(unchecked((uint)value.Payload)) : Expression.Convert(Operand(value, clean), typeof(uint));

        private Expression Require(Expression condition) =>
            Expression.IfThen(Expression.Not(condition), Expression.Return(_done, Expression.Constant(false)));

        // One place a region evaluates a value. A value several regions need, first needed by a conditional one, is
        // evaluated by the first of them that runs; a clean value the selection holds is taken from it instead.
        private void EmitSite(Pair pair, List<Region> regions, List<Expression> statements)
        {
            pair.Local ??= Local(typeof(ulong));
            var compute = new List<Expression>();
            Compute(pair, compute);
            compute.Add(Expression.Empty());
            Expression site = Expression.Block(compute);
            if (pair.Selection)
                site = Expression.IfThen(Expression.Not(Expression.Call(_selection, TryGetEvaluated, Expression.Constant(pair.Value),
                    pair.Local)), site);
            if (pair.Regions >= 2 && regions[pair.FirstRegion].Conditional)
            {
                pair.Flag ??= Local(typeof(bool));
                site = Expression.IfThen(Expression.Not(pair.Flag), Expression.Block(site, Expression.Assign(pair.Flag, Expression.Constant(true))));
            }

            statements.Add(site);
        }

        // As CompiledResourceTransaction compiles each node kind; the operands are already in their locals.
        private void Compute(Pair pair, List<Expression> compute)
        {
            var value = pair.Value;
            var clean = pair.Clean;
            var local = pair.Local!;
            switch (value.Kind)
            {
                case ScalarValueKind.UserData:
                    compute.Add(Require(Expression.Call(ReadUserData, _input,
                        Expression.Constant((ulong)(value.UserDataRegister - plan.UserDataBase)), local)));
                    break;
                case ScalarValueKind.ShaderBase:
                    compute.Add(Expression.Assign(local, Expression.Property(_input, nameof(ResourceRuntimeInputs.ShaderBase))));
                    break;
                case ScalarValueKind.ResourceTableWord:
                {
                    var slot = (int)value.Payload;
                    compute.Add(Expression.Assign(local, Operand(plan.TableReads[slot].Value, clean || IsCleanSlot(slot))));
                    break;
                }
                case ScalarValueKind.ScalarAddressWord:
                case ScalarValueKind.ScalarBufferWord:
                {
                    var handle = value.Operands[0];
                    var buffer = value.Kind == ScalarValueKind.ScalarBufferWord;
                    compute.Add(Require(Expression.Call(ReadWord, _input, Expression.Constant(clean), Expression.Constant(value.Kind),
                        Operand(handle.Operands[0], clean), Operand(handle.Operands[1], clean), Operand(value.Operands[1], clean),
                        buffer ? Operand(handle.Operands[2], clean) : Expression.Constant(0UL),
                        Expression.Constant((long)(int)plan.Memory[value.MemoryIndex].Offset),
                        Expression.Constant(RuntimeValueEvaluator.IsAbsoluteAddress(value.Kind, handle)), local,
                        Expression.Constant(plan), Expression.Constant(value))));
                    break;
                }
                case ScalarValueKind.MemoryAperture:
                    compute.Add(Expression.Assign(local, Expression.Constant(Gen5InlineConstants.DecodeAperture64((uint)value.Payload) >> 32)));
                    break;
                case ScalarValueKind.Phi:
                    compute.Add(Expression.Assign(local, Operand(plan.Graph.ResolveInvariantPhi(value)!, clean)));
                    break;
                case ScalarValueKind.Select:
                    // Both arms are evaluated, as the scalar evaluator does.
                    compute.Add(Expression.Assign(local, Expression.Condition(
                        Expression.NotEqual(Operand(value.Operands[0], clean), Expression.Constant(0UL)),
                        Operand(value.Operands[1], clean), Operand(value.Operands[2], clean))));
                    break;
                case ScalarValueKind.Operation:
                {
                    var operands = new Expression[4];
                    for (var index = 0; index < operands.Length; index++)
                        operands[index] = index < value.Operands.Length ? Operand(value.Operands[index], clean) : Expression.Constant(0UL);
                    compute.Add(Require(Expression.Call(EvaluateOperation, Expression.Constant(value.Operation),
                        operands[0], operands[1], operands[2], operands[3], local)));
                    break;
                }
                default:
                    throw new NotSupportedException();
            }
        }
    }
}
