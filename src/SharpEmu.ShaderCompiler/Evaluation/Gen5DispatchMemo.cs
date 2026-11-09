// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;

namespace SharpEmu.ShaderCompiler.Evaluation;

public enum Gen5MemoLookup : byte
{
    None,
    Hit,
    Absent,
    Changed,
}

// Earlier results by program, user data and dispatch shape. A result is reused while every input word its
// evaluation read still holds the value it read, so it equals what a new evaluation would produce.
public sealed class Gen5DispatchMemo(int entriesPerProgram = Gen5DispatchMemo.DefaultEntriesPerProgram, long byteCapacity = Gen5DispatchMemo.DefaultByteCapacity)
{
    public const int DefaultEntriesPerProgram = 8;
    public const long DefaultByteCapacity = 64L * 1024 * 1024;
    private const int EntryBytes = 256;
    private const int PageBytes = 4096;

    private readonly record struct Shape(
        Gen5ComputeSystemRegisters SystemRegisters,
        uint ThreadsX,
        uint ThreadsY,
        uint ThreadsZ,
        uint GroupsX,
        uint GroupsY,
        uint GroupsZ,
        bool LimitsThreads,
        uint ThreadLimitX,
        uint ThreadLimitY,
        uint ThreadLimitZ,
        uint WaveSize,
        bool PreservesFloatSpecials,
        long LaneOperationBudget,
        long ThreadBudget,
        int OutputDwordBudget)
    {
        public static Shape Of(Gen5DispatchRequest request) => new(
            request.SystemRegisters, request.ThreadsX, request.ThreadsY, request.ThreadsZ, request.GroupsX, request.GroupsY, request.GroupsZ,
            request.LimitsThreads, request.ThreadLimitX, request.ThreadLimitY, request.ThreadLimitZ, request.WaveSize, request.PreservesFloatSpecials,
            request.LaneOperationBudget, request.ThreadBudget, request.OutputDwordBudget);
    }

    private sealed class Entry(Gen5EvaluableProgram program, Shape shape, uint[] userData, Gen5MemoInput[] inputs, uint[] inputValues,
        ulong address, uint[] words, int writtenCount)
    {
        public Gen5EvaluableProgram Program { get; } = program;
        public Shape Shape { get; } = shape;
        public uint[] UserData { get; } = userData;
        public Gen5MemoInput[] Inputs { get; } = inputs;
        public uint[] InputValues { get; } = inputValues;
        public ulong Address { get; } = address;
        public uint[] Words { get; } = words;
        public int WrittenCount { get; } = writtenCount;
        public LinkedListNode<Entry>? Node { get; set; }

        public long Bytes => EntryBytes + ((long)UserData.Length + InputValues.Length + Words.Length) * sizeof(uint) +
                             (long)Inputs.Length * (EntryBytes / 8);

        public bool Matches(Shape shape, uint[] userData) => Shape == shape && UserData.AsSpan().SequenceEqual(userData);

        public Gen5DispatchPrediction Predict()
        {
            var count = Words.Length;
            var buffer = Gen5EvaluationWrites.IsPooled(count) ? System.Buffers.ArrayPool<uint>.Shared.Rent(count) : new uint[count];
            Words.CopyTo(buffer, 0);
            return new Gen5DispatchPrediction(Address, buffer, count, WrittenCount);
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Gen5EvaluableProgram, List<Entry>> _programs = new();
    private readonly LinkedList<Entry> _recent = new();
    private long _bytes;

    public long Bytes => Volatile.Read(ref _bytes);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _recent.Count;
            }
        }
    }

    public Gen5DispatchOutcome Evaluate(Gen5EvaluableProgram program, Gen5DispatchRequest request, IGen5EvaluationMemory memory)
    {
        var precheck = Gen5DispatchEvaluator.Precheck(program, request, out _);
        if (precheck != Gen5EvaluationRefusal.None)
        {
            return new Gen5DispatchOutcome(precheck, null, 0, 0);
        }

        var shape = Shape.Of(request);
        var entry = Find(program, shape, request.UserData);
        var lookup = Gen5MemoLookup.Absent;
        if (entry is not null)
        {
            if (InputsUnchanged(entry, memory))
            {
                Touch(entry);
                return new Gen5DispatchOutcome(Gen5EvaluationRefusal.None, entry.Predict(), 0, 0, Gen5MemoLookup.Hit);
            }

            lookup = Gen5MemoLookup.Changed;
        }

        var outcome = Gen5DispatchEvaluator.Evaluate(program, request, memory, out var reads);
        if (outcome.Prediction is { } prediction)
        {
            Store(program, shape, request.UserData, reads, prediction);
        }
        else if (entry is not null)
        {
            lock (_gate)
            {
                Unlink(entry);
            }
        }

        return outcome with { Memo = lookup };
    }

    private Entry? Find(Gen5EvaluableProgram program, Shape shape, uint[] userData)
    {
        lock (_gate)
        {
            if (!_programs.TryGetValue(program, out var entries))
            {
                return null;
            }

            foreach (var entry in entries)
            {
                if (entry.Matches(shape, userData))
                {
                    return entry;
                }
            }

            return null;
        }
    }

    private static bool InputsUnchanged(Entry entry, IGen5EvaluationMemory memory)
    {
        Span<byte> bytes = stackalloc byte[PageBytes];
        var values = entry.InputValues;
        foreach (var input in entry.Inputs)
        {
            var span = bytes[..(input.Words * sizeof(uint))];
            var words = MemoryMarshal.Cast<byte, uint>(span);
            if (input.Mask is not { } mask)
            {
                if (!memory.TryRead(input.Address, span) || !words.SequenceEqual(values.AsSpan(input.ValueOffset, input.Words)))
                {
                    return false;
                }

                continue;
            }

            if (!memory.TryRead(input.Address, span) && !TryReadRuns(memory, input.Address, mask, input.Words, span))
            {
                return false;
            }

            var next = input.ValueOffset;
            for (var block = 0; block < mask.Length; block++)
            {
                var bits = mask[block];
                while (bits != 0)
                {
                    var word = block * 64 + BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    if (words[word] != values[next++])
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    // The words between read words may belong to an owner the evaluation never asked.
    private static bool TryReadRuns(IGen5EvaluationMemory memory, ulong address, ulong[] mask, int words, Span<byte> bytes)
    {
        var word = 0;
        while (word < words)
        {
            if ((mask[word >> 6] & (1ul << (word & 63))) == 0)
            {
                word++;
                continue;
            }

            var end = word + 1;
            while (end < words && (mask[end >> 6] & (1ul << (end & 63))) != 0)
            {
                end++;
            }

            if (!memory.TryRead(address + (ulong)word * sizeof(uint), bytes[(word * sizeof(uint))..(end * sizeof(uint))]))
            {
                return false;
            }

            word = end;
        }

        return true;
    }

    private void Store(Gen5EvaluableProgram program, Shape shape, uint[] userData, Gen5EvaluationReads reads, Gen5DispatchPrediction prediction)
    {
        var inputs = new List<Gen5MemoInput>();
        var values = new List<uint>();
        reads.CollectInputs(inputs, values);
        var entry = new Entry(program, shape, userData.ToArray(), inputs.ToArray(), values.ToArray(), prediction.Address, prediction.Words.ToArray(),
            prediction.WrittenCount);
        if (entry.Bytes > byteCapacity / 4)
        {
            return;
        }

        lock (_gate)
        {
            if (!_programs.TryGetValue(program, out var entries))
            {
                entries = new List<Entry>(entriesPerProgram);
                _programs.Add(program, entries);
            }

            foreach (var existing in entries)
            {
                if (existing.Matches(shape, userData))
                {
                    Unlink(existing);
                    break;
                }
            }

            if (entries.Count >= entriesPerProgram)
            {
                Unlink(entries[^1]);
            }

            entries.Insert(0, entry);
            _programs[program] = entries;
            entry.Node = _recent.AddFirst(entry);
            Volatile.Write(ref _bytes, _bytes + entry.Bytes);
            while (_bytes > byteCapacity && _recent.Last is { } oldest)
            {
                Unlink(oldest.Value);
            }
        }
    }

    private void Touch(Entry entry)
    {
        lock (_gate)
        {
            if (entry.Node is not { } node || !_programs.TryGetValue(entry.Program, out var entries))
            {
                return;
            }

            _recent.Remove(node);
            _recent.AddFirst(node);
            var index = entries.IndexOf(entry);
            if (index > 0)
            {
                entries.RemoveAt(index);
                entries.Insert(0, entry);
            }
        }
    }

    private void Unlink(Entry entry)
    {
        if (entry.Node is not { } node)
        {
            return;
        }

        _recent.Remove(node);
        entry.Node = null;
        Volatile.Write(ref _bytes, _bytes - entry.Bytes);
        if (_programs.TryGetValue(entry.Program, out var entries))
        {
            entries.Remove(entry);
            if (entries.Count == 0)
            {
                _programs.Remove(entry.Program);
            }
        }
    }
}
