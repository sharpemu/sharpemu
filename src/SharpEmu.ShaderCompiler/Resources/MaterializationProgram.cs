// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;

namespace SharpEmu.ShaderCompiler.Resources;

internal sealed class MaterializationProgram
{
    private const ulong AddressMask = 0x0000_FFFF_FFFF_FFFFul;
    private const int MinimumRunWords = 2;
    private const int MaximumRunBytes = 64 * 1024;

    private enum StepKind : byte
    {
        UserData,
        ShaderBase,
        Select,
        Operation,
    }

    private readonly record struct Step(StepKind Kind, int Target, int First, int Second, int Third, ScalarOperation Operation, int[]? Operands);

    private readonly record struct ReadStep(int Target, ScalarValueKind Kind, int Low, int High, int Records, int Word3, int Offset, uint ConstantOffset,
        long Immediate, bool Absolute);

    private sealed class Level
    {
        public ReadStep[] Reads = [];
        public Step[] Steps = [];
    }

    private readonly int _registerCount;
    private readonly ulong[] _constants;
    private readonly bool[] _constantValid;
    private readonly Level[] _levels;
    private readonly int[][] _sourceWords;
    private readonly int[] _tableWords;
    private readonly uint[] _tableOffsets;
    private readonly int[] _rangeLow;
    private readonly int[] _rangeHigh;

    [ThreadStatic]
    private static Scratch? _scratch;

    private sealed class Scratch
    {
        public ulong[] Values = new ulong[256];
        public bool[] Valid = new bool[256];
        public ulong[] Addresses = new ulong[64];
        public int[] Targets = new int[64];
        public ulong[] NullBases = new ulong[64];
        public bool[] Absolute = new bool[64];
        public byte[] Bytes = new byte[1024];
        public int Count;

        public void Ensure(int registers)
        {
            if (Values.Length < registers)
            {
                Values = new ulong[Math.Max(registers, Values.Length * 2)];
                Valid = new bool[Values.Length];
            }
        }

        public void Add(ulong address, int target, ulong baseAddress, bool absolute)
        {
            if (Count == Addresses.Length)
            {
                Array.Resize(ref Addresses, Count * 2);
                Array.Resize(ref Targets, Count * 2);
                Array.Resize(ref NullBases, Count * 2);
                Array.Resize(ref Absolute, Count * 2);
            }

            Addresses[Count] = address;
            Targets[Count] = target;
            NullBases[Count] = baseAddress;
            Absolute[Count] = absolute;
            Count++;
        }
    }

    private MaterializationProgram(int registerCount, ulong[] constants, bool[] constantValid, Level[] levels, int[][] sourceWords, int[] tableWords,
        uint[] tableOffsets, int[] rangeLow, int[] rangeHigh)
    {
        _registerCount = registerCount;
        _constants = constants;
        _constantValid = constantValid;
        _levels = levels;
        _sourceWords = sourceWords;
        _tableWords = tableWords;
        _tableOffsets = tableOffsets;
        _rangeLow = rangeLow;
        _rangeHigh = rangeHigh;
    }

    internal static MaterializationProgram? Build(ShaderResourcePlan plan, out string reason)
    {
        reason = plan.IndirectImages.Count != 0 ? "indirect_images" : plan.BufferCandidateTables.Count != 0 ? "candidate_tables" : "";
        if (reason.Length != 0)
        {
            return null;
        }

        foreach (var clean in plan.CleanFlatSlots)
        {
            if (clean != 0)
            {
                reason = "clean_slots";
                return null;
            }
        }

        foreach (var source in plan.DescriptorSources)
        {
            if (source.IndirectImage is not null)
            {
                reason = "indirect_source";
                return null;
            }
        }

        var builder = new Builder(plan);
        var program = Build(plan, builder);
        reason = program is null ? builder.Reason ?? "layout" : "";
        return program;
    }

    private static MaterializationProgram? Build(ShaderResourcePlan plan, Builder builder)
    {
        var sourceWords = new int[plan.MaterializationSources.Count][];
        for (var index = 0; index < sourceWords.Length; index++)
        {
            var source = plan.MaterializationSources[index];
            if (source >= plan.DescriptorSources.Count)
            {
                return null;
            }

            var dwords = plan.DescriptorSources[(int)source].Dwords;
            sourceWords[index] = new int[dwords.Length];
            for (var word = 0; word < dwords.Length; word++)
            {
                if (!builder.Register(dwords[word], out sourceWords[index][word]))
                {
                    return null;
                }
            }
        }

        var tableWords = new int[plan.TableReads.Count];
        var tableOffsets = new uint[plan.TableReads.Count];
        for (var index = 0; index < tableWords.Length; index++)
        {
            var read = plan.TableReads[index];
            if (read.FlatOffset >= plan.TableReads.Count || !builder.Register(read.Value, out tableWords[index]))
            {
                return null;
            }

            tableOffsets[index] = read.FlatOffset;
        }

        var rangeLow = new int[plan.DeviceAddressRanges.Count];
        var rangeHigh = new int[plan.DeviceAddressRanges.Count];
        for (var index = 0; index < rangeLow.Length; index++)
        {
            var range = plan.DeviceAddressRanges[index];
            if (!range.Plannable)
            {
                rangeLow[index] = -1;
                rangeHigh[index] = -1;
                continue;
            }

            if (!builder.Register(range.BaseLow, out rangeLow[index]) || !builder.Register(range.BaseHigh, out rangeHigh[index]))
            {
                return null;
            }
        }

        return builder.Finish(sourceWords, tableWords, tableOffsets, rangeLow, rangeHigh);
    }

    private sealed class Builder(ShaderResourcePlan plan)
    {
        private readonly Dictionary<ScalarValue, int> _registers = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<ScalarValue> _visiting = new(ReferenceEqualityComparer.Instance);
        private readonly List<ulong> _constants = [];
        private readonly List<bool> _constantValid = [];
        private readonly List<int> _registerLevels = [];
        private readonly List<(int Level, Step Step)> _steps = [];
        private readonly List<(int Level, ReadStep Read)> _reads = [];

        public string? Reason;

        private bool Refuse(string reason)
        {
            Reason ??= reason;
            return false;
        }

        private int NewRegister(int level, ulong constant = 0, bool constantValid = false)
        {
            _constants.Add(constant);
            _constantValid.Add(constantValid);
            _registerLevels.Add(level);
            return _constants.Count - 1;
        }

        private int LevelOf(int register) => _registerLevels[register];

        public bool Register(ScalarValue value, out int register)
        {
            if (_registers.TryGetValue(value, out register))
            {
                return true;
            }

            if (!_visiting.Add(value))
            {
                return Refuse("cycle");
            }

            try
            {
                if (!Lower(value, out register))
                {
                    return false;
                }
            }
            finally
            {
                _visiting.Remove(value);
            }

            _registers[value] = register;
            return true;
        }

        private bool Lower(ScalarValue value, out int register)
        {
            register = -1;
            switch (value.Kind)
            {
                case ScalarValueKind.Constant:
                    register = NewRegister(0, value.Payload, constantValid: true);
                    return true;
                case ScalarValueKind.MemoryAperture:
                    register = NewRegister(0, Gen5InlineConstants.DecodeAperture64((uint)value.Payload) >> 32, constantValid: true);
                    return true;
                case ScalarValueKind.UserData:
                    if (value.UserDataRegister < plan.UserDataBase)
                    {
                        return Refuse("user_data_base");
                    }

                    register = NewRegister(0);
                    _steps.Add((0, new Step(StepKind.UserData, register, (int)(value.UserDataRegister - plan.UserDataBase), 0, 0, default, null)));
                    return true;
                case ScalarValueKind.ShaderBase:
                    register = NewRegister(0);
                    _steps.Add((0, new Step(StepKind.ShaderBase, register, 0, 0, 0, default, null)));
                    return true;
                case ScalarValueKind.Phi:
                    return plan.Graph.ResolveInvariantPhi(value) is { } invariant ? Register(invariant, out register) : Refuse("phi");
                case ScalarValueKind.ResourceTableWord:
                    return value.Payload < (ulong)plan.TableReads.Count && Register(plan.TableReads[(int)value.Payload].Value, out register);
                case ScalarValueKind.Select:
                {
                    if (value.Operands.Length != 3 || !Register(value.Operands[0], out var condition) || !Register(value.Operands[1], out var whenTrue) ||
                        !Register(value.Operands[2], out var whenFalse))
                    {
                        return false;
                    }

                    var level = Math.Max(LevelOf(condition), Math.Max(LevelOf(whenTrue), LevelOf(whenFalse)));
                    register = NewRegister(level);
                    _steps.Add((level, new Step(StepKind.Select, register, condition, whenTrue, whenFalse, default, null)));
                    return true;
                }
                case ScalarValueKind.Operation:
                {
                    if (!RuntimeValueValidator.IsUniformOperation(value.Operation))
                    {
                        return Refuse("operation_" + value.Operation);
                    }

                    var operands = new int[value.Operands.Length];
                    var level = 0;
                    for (var index = 0; index < operands.Length; index++)
                    {
                        if (!Register(value.Operands[index], out operands[index]))
                        {
                            return false;
                        }

                        level = Math.Max(level, LevelOf(operands[index]));
                    }

                    register = NewRegister(level);
                    _steps.Add((level, new Step(StepKind.Operation, register, 0, 0, 0, value.Operation, operands)));
                    return true;
                }
                case ScalarValueKind.ScalarAddressWord:
                case ScalarValueKind.ScalarBufferWord:
                {
                    var buffer = value.Kind == ScalarValueKind.ScalarBufferWord;
                    if ((uint)value.MemoryIndex >= (uint)plan.Memory.Count || value.Operands.Length < 2)
                    {
                        return Refuse("memory_index");
                    }

                    var handle = value.Operands[0];
                    if (handle.Operands.Length < 2 || (buffer && handle.Operands.Length != 4))
                    {
                        return Refuse("memory_handle");
                    }

                    var records = -1;
                    var word3 = -1;
                    if (!Register(handle.Operands[0], out var low) || !Register(handle.Operands[1], out var high) ||
                        (buffer && (!Register(handle.Operands[2], out records) || !Register(handle.Operands[3], out word3))))
                    {
                        return false;
                    }

                    var offsetValue = value.Operands[1];
                    var offset = -1;
                    if (!offsetValue.IsConstant && !Register(offsetValue, out offset))
                    {
                        return false;
                    }

                    var level = Math.Max(LevelOf(low), LevelOf(high));
                    if (buffer)
                    {
                        level = Math.Max(level, Math.Max(LevelOf(records), LevelOf(word3)));
                    }

                    if (offset >= 0)
                    {
                        level = Math.Max(level, LevelOf(offset));
                    }

                    level++;
                    register = NewRegister(level);
                    _reads.Add((level, new ReadStep(register, value.Kind, low, high, records, word3, offset, offsetValue.IsConstant ? (uint)offsetValue.Payload : 0,
                        (int)plan.Memory[value.MemoryIndex].Offset, RuntimeValueEvaluator.IsAbsoluteAddress(value.Kind, handle))));
                    return true;
                }
                default:
                    return Refuse(value.Kind.ToString());
            }
        }

        public MaterializationProgram Finish(int[][] sourceWords, int[] tableWords, uint[] tableOffsets, int[] rangeLow, int[] rangeHigh)
        {
            var levelCount = 1;
            foreach (var level in _registerLevels)
            {
                levelCount = Math.Max(levelCount, level + 1);
            }

            var reads = new List<ReadStep>[levelCount];
            var steps = new List<Step>[levelCount];
            for (var level = 0; level < levelCount; level++)
            {
                reads[level] = [];
                steps[level] = [];
            }

            foreach (var (level, read) in _reads)
            {
                reads[level].Add(read);
            }

            foreach (var (level, step) in _steps)
            {
                steps[level].Add(step);
            }

            var levels = new Level[levelCount];
            for (var level = 0; level < levelCount; level++)
            {
                levels[level] = new Level { Reads = [.. reads[level]], Steps = [.. steps[level]] };
            }

            return new MaterializationProgram(_constants.Count, [.. _constants], [.. _constantValid], levels, sourceWords, tableWords, tableOffsets,
                rangeLow, rangeHigh);
        }
    }

    internal bool TryRun(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, int additionalTableWords, out List<DescriptorWords> sources,
        out uint[] table, out DeviceAddressRange[] ranges, out bool[] activeSources)
    {
        activeSources = plan.ResourceBranches.Count != 0 ? RuntimeValueEvaluator.EvaluateActiveSources(plan, inputs) : [];
        var scratch = _scratch ?? new Scratch();
        _scratch = null;
        try
        {
            return Run(plan, inputs, additionalTableWords, scratch, activeSources, out sources, out table, out ranges);
        }
        finally
        {
            _scratch = scratch;
        }
    }

    // The words of one evaluation, held by the thread that runs it and reused by the next.
    internal sealed class Output
    {
        public uint[] Words = new uint[256];
        public int[] Offsets = new int[33];
        public uint[] Table = new uint[64];
        public int TableLength;
        public DeviceAddressRange[] Ranges = new DeviceAddressRange[8];
        public int RangeCount;

        public Span<uint> Source(int index) => Words.AsSpan(Offsets[index], Offsets[index + 1] - Offsets[index]);

        public Span<uint> TableWords => Table.AsSpan(0, TableLength);

        public ReadOnlySpan<DeviceAddressRange> RangeValues => Ranges.AsSpan(0, RangeCount);
    }

    internal bool TryRunInto(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, int additionalTableWords, Output output)
    {
        var activeSources = plan.ResourceBranches.Count != 0 ? RuntimeValueEvaluator.EvaluateActiveSources(plan, inputs) : [];
        var scratch = _scratch ?? new Scratch();
        _scratch = null;
        try
        {
            Evaluate(inputs, scratch);
            return Emit(plan, additionalTableWords, scratch, activeSources, output);
        }
        finally
        {
            _scratch = scratch;
        }
    }

    private void Evaluate(ResourceRuntimeInputs inputs, Scratch scratch)
    {
        scratch.Ensure(_registerCount);
        var values = scratch.Values;
        var valid = scratch.Valid;
        Array.Copy(_constants, values, _registerCount);
        Array.Copy(_constantValid, valid, _registerCount);
        Span<ulong> operands = stackalloc ulong[8];
        foreach (var level in _levels)
        {
            if (level.Reads.Length != 0)
            {
                ReadLevel(level.Reads, inputs, scratch);
            }

            foreach (var step in level.Steps)
            {
                Execute(step, inputs, values, valid, operands);
            }
        }
    }

    private bool Emit(ShaderResourcePlan plan, int additionalTableWords, Scratch scratch, bool[] activeSources, Output output)
    {
        var values = scratch.Values;
        var valid = scratch.Valid;
        var total = 0;
        foreach (var registers in _sourceWords)
        {
            total += registers.Length;
        }

        if (output.Words.Length < total)
        {
            output.Words = new uint[Math.Max(total, output.Words.Length * 2)];
        }

        if (output.Offsets.Length < _sourceWords.Length + 1)
        {
            output.Offsets = new int[_sourceWords.Length + 1];
        }

        var cursor = 0;
        for (var source = 0; source < _sourceWords.Length; source++)
        {
            var registers = _sourceWords[source];
            output.Offsets[source] = cursor;
            var inactive = activeSources.Length != 0 && !activeSources[plan.MaterializationSources[source]];
            for (var word = 0; word < registers.Length; word++)
            {
                var register = registers[word];
                if (inactive)
                {
                    output.Words[cursor++] = 0;
                    continue;
                }

                if (!valid[register])
                {
                    return false;
                }

                output.Words[cursor++] = (uint)values[register];
            }
        }

        output.Offsets[_sourceWords.Length] = cursor;
        var tableLength = checked(_tableWords.Length + additionalTableWords);
        if (output.Table.Length < tableLength)
        {
            output.Table = new uint[Math.Max(tableLength, output.Table.Length * 2)];
        }

        Array.Clear(output.Table, 0, tableLength);
        output.TableLength = tableLength;
        for (var index = 0; index < _tableWords.Length; index++)
        {
            var register = _tableWords[index];
            if (!valid[register])
            {
                return false;
            }

            output.Table[_tableOffsets[index]] = (uint)values[register];
        }

        var planned = plan.DeviceAddressRanges;
        if (output.Ranges.Length < planned.Count)
        {
            output.Ranges = new DeviceAddressRange[Math.Max(planned.Count, output.Ranges.Length * 2)];
        }

        output.RangeCount = planned.Count;
        for (var index = 0; index < planned.Count; index++)
        {
            output.Ranges[index] = RangeOf(planned[index], index, values, valid);
        }

        return true;
    }

    private DeviceAddressRange RangeOf(DeviceAddressRangePlan range, int index, ulong[] values, bool[] valid)
    {
        var low = _rangeLow[index];
        var high = _rangeHigh[index];
        var plannedRange = low >= 0 && valid[low] && valid[high];
        var handleBase = plannedRange ? ((uint)values[low] | ((ulong)(uint)values[high] << 32)) & AddressMask : 0;
        var firstByte = range.Bounded ? range.FirstByte : Math.Min(range.FirstByte, 0);
        var baseAddress = plannedRange ? unchecked(handleBase + (ulong)firstByte) & AddressMask : 0;
        var size = range.Bounded ? range.Extent : DeviceAddressRangePlanner.MaxRangeBytes;
        return new DeviceAddressRange(range.Handle, baseAddress, plannedRange ? size : 0, plannedRange, range.Written);
    }

    private bool Run(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, int additionalTableWords, Scratch scratch, bool[] activeSources,
        out List<DescriptorWords> sources, out uint[] table, out DeviceAddressRange[] ranges)
    {
        sources = null!;
        table = null!;
        ranges = null!;
        Evaluate(inputs, scratch);
        var values = scratch.Values;
        var valid = scratch.Valid;
        var evaluated = new List<DescriptorWords>(_sourceWords.Length);
        for (var source = 0; source < _sourceWords.Length; source++)
        {
            var registers = _sourceWords[source];
            var words = new uint[registers.Length];
            if (activeSources.Length != 0 && !activeSources[plan.MaterializationSources[source]])
            {
                evaluated.Add(new DescriptorWords(words));
                continue;
            }

            for (var word = 0; word < words.Length; word++)
            {
                var register = registers[word];
                if (!valid[register])
                {
                    return false;
                }

                words[word] = (uint)values[register];
            }

            evaluated.Add(new DescriptorWords(words));
        }

        var flattened = new uint[checked(_tableWords.Length + additionalTableWords)];
        for (var index = 0; index < _tableWords.Length; index++)
        {
            var register = _tableWords[index];
            if (!valid[register])
            {
                return false;
            }

            flattened[_tableOffsets[index]] = (uint)values[register];
        }

        var planned = plan.DeviceAddressRanges;
        var result = new DeviceAddressRange[planned.Count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = RangeOf(planned[index], index, values, valid);
        }

        sources = evaluated;
        table = flattened;
        ranges = result;
        return true;
    }

    private static void Execute(in Step step, ResourceRuntimeInputs inputs, ulong[] values, bool[] valid, Span<ulong> operands)
    {
        switch (step.Kind)
        {
            case StepKind.UserData:
                if ((uint)step.First < (uint)inputs.UserData.Count)
                {
                    values[step.Target] = inputs.UserData[step.First];
                    valid[step.Target] = true;
                }
                else
                {
                    valid[step.Target] = false;
                }

                return;
            case StepKind.ShaderBase:
                values[step.Target] = inputs.ShaderBase;
                valid[step.Target] = true;
                return;
            case StepKind.Select:
                valid[step.Target] = valid[step.First] && valid[step.Second] && valid[step.Third];
                values[step.Target] = values[step.First] != 0 ? values[step.Second] : values[step.Third];
                return;
            case StepKind.Operation:
            {
                var registers = step.Operands!;
                var count = registers.Length;
                var span = count <= operands.Length ? operands[..count] : new ulong[count];
                for (var index = 0; index < count; index++)
                {
                    if (!valid[registers[index]])
                    {
                        valid[step.Target] = false;
                        return;
                    }

                    span[index] = values[registers[index]];
                }

                valid[step.Target] = ScalarOperationSemantics.TryEvaluate(step.Operation, span, out var result);
                values[step.Target] = result;
                return;
            }
        }
    }

    private static void ReadLevel(ReadStep[] reads, ResourceRuntimeInputs inputs, Scratch scratch)
    {
        var values = scratch.Values;
        var valid = scratch.Valid;
        scratch.Count = 0;
        foreach (var read in reads)
        {
            valid[read.Target] = false;
            var buffer = read.Kind == ScalarValueKind.ScalarBufferWord;
            if (!valid[read.Low] || !valid[read.High] || (buffer && (!valid[read.Records] || !valid[read.Word3])) || (read.Offset >= 0 && !valid[read.Offset]))
            {
                continue;
            }

            var high = values[read.High];
            var records = buffer ? values[read.Records] : 0;
            var offset = read.Offset >= 0 ? (uint)values[read.Offset] : read.ConstantOffset;
            var baseAddress = ((high << 32) | (uint)values[read.Low]) & AddressMask;
            switch (RuntimeValueEvaluator.ResolveRawAddress(read.Kind, baseAddress, high, records, read.Immediate, offset, out var address))
            {
                case RuntimeValueEvaluator.RawAddress.Failed:
                    continue;
                case RuntimeValueEvaluator.RawAddress.Zero:
                    values[read.Target] = 0;
                    valid[read.Target] = true;
                    continue;
            }

            scratch.Add(address, read.Target, baseAddress, read.Absolute);
        }

        var count = scratch.Count;
        if (count == 0)
        {
            return;
        }

        var addresses = scratch.Addresses;
        var targets = scratch.Targets;
        var bases = scratch.NullBases;
        var absolute = scratch.Absolute;
        for (var index = 1; index < count; index++)
        {
            var address = addresses[index];
            var target = targets[index];
            var baseAddress = bases[index];
            var isAbsolute = absolute[index];
            var position = index - 1;
            while (position >= 0 && addresses[position] > address)
            {
                addresses[position + 1] = addresses[position];
                targets[position + 1] = targets[position];
                bases[position + 1] = bases[position];
                absolute[position + 1] = absolute[position];
                position--;
            }

            addresses[position + 1] = address;
            targets[position + 1] = target;
            bases[position + 1] = baseAddress;
            absolute[position + 1] = isAbsolute;
        }

        var resident = inputs.ReadResidentMemory;
        var start = 0;
        while (start < count)
        {
            var end = start + 1;
            var words = 1;
            while (end < count && addresses[end] - addresses[end - 1] <= sizeof(uint) && addresses[end] - addresses[start] < MaximumRunBytes &&
                   (bases[end] & ~3ul) != 0 && (bases[start] & ~3ul) != 0)
            {
                if (addresses[end] != addresses[end - 1])
                {
                    words++;
                }

                end++;
            }

            if (words >= MinimumRunWords && resident is not null && inputs.ReadMemory is not null && ReadRun(resident, inputs.ReadsClean, scratch, start, end))
            {
                start = end;
                continue;
            }

            for (var index = start; index < end; index++)
            {
                ReadWord(inputs, addresses[index], bases[index], absolute[index], targets[index], values, valid);
            }

            start = end;
        }
    }

    private static bool ReadRun(ResidentGuestBytesReader reader, bool clean, Scratch scratch, int start, int end)
    {
        var addresses = scratch.Addresses;
        var first = addresses[start];
        var length = checked((int)(addresses[end - 1] - first) + sizeof(uint));
        if (scratch.Bytes.Length < length)
        {
            scratch.Bytes = new byte[Math.Max(length, scratch.Bytes.Length * 2)];
        }

        var bytes = scratch.Bytes.AsSpan(0, length);
        if (!reader(first, bytes, clean))
        {
            return false;
        }

        for (var index = start; index < end; index++)
        {
            var target = scratch.Targets[index];
            scratch.Values[target] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)(addresses[index] - first)..]);
            scratch.Valid[target] = true;
        }

        return true;
    }

    private static void ReadWord(ResourceRuntimeInputs inputs, ulong address, ulong baseAddress, bool absolute, int target, ulong[] values, bool[] valid)
    {
        if (inputs.ReadMemory is not null && inputs.ReadMemory(address, out var word))
        {
            values[target] = word;
            valid[target] = true;
            return;
        }

        values[target] = 0;
        valid[target] = (baseAddress & ~3ul) == 0 || (absolute && inputs.ReadMemory is not null && !inputs.ReadsClean);
    }
}
