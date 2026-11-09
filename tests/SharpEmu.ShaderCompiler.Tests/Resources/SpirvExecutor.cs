// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.ShaderCompiler.Vulkan;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// Runs one invocation of an emitted compute module over integer data, so a test can check
// what the shader computes instead of how the translator spelled it. Anything outside that
// subset throws. Memory reached through OpConvertUToPtr is the test's guest memory, and the
// page table maps every page but the null page onto itself.
internal sealed class SpirvExecutor
{
    private const int StepLimit = 2_000_000;
    private const string PageTable = "deviceAddressPageTable";

    private enum Kind : byte { Scalar, Vector, Aggregate, RuntimeArray, Pointer, Other }

    private sealed record TypeInfo(Kind Kind, int Bits = 0, uint Element = 0, int Count = 0, uint[]? Members = null);

    private sealed record Pointer(uint Root, string Name, string Path, uint Type, ulong Address = 0, bool Physical = false);

    private sealed record Function(List<uint> Parameters, List<uint[]> Body, Dictionary<uint, int> Labels);

    private readonly Dictionary<uint, TypeInfo> _types = [];
    private readonly Dictionary<uint, object> _constants = [];
    private readonly Dictionary<uint, uint> _resultTypes = [];
    private readonly Dictionary<uint, Pointer> _variables = [];
    private readonly Dictionary<uint, object> _initializers = [];
    private readonly Dictionary<uint, Function> _functions = [];
    private readonly Dictionary<uint, string> _names = [];
    private readonly uint _entry;
    private Dictionary<string, ulong> _state = [];
    private Func<ulong, uint> _read = _ => 0;
    private Action<ulong, uint> _write = (_, _) => { };
    private int _steps;

    public SpirvExecutor(byte[] spirv)
    {
        var words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        Function? current = null;
        for (var offset = 5; offset < words.Length;)
        {
            var count = (int)(words[offset] >> 16);
            var w = words[offset..(offset + count)];
            offset += count;
            var op = (SpirvOp)(w[0] & 0xFFFF);
            if (current is not null)
            {
                switch (op)
                {
                    case SpirvOp.FunctionParameter:
                        current.Parameters.Add(w[2]);
                        _resultTypes[w[2]] = w[1];
                        break;
                    case SpirvOp.FunctionEnd:
                        current = null;
                        break;
                    case SpirvOp.Label:
                        current.Labels[w[1]] = current.Body.Count;
                        break;
                    default:
                        current.Body.Add(w);
                        break;
                }

                continue;
            }

            switch (op)
            {
                case SpirvOp.EntryPoint: _entry = w[2]; break;
                case SpirvOp.Name: _names[w[1]] = Text(w, 2); break;
                case SpirvOp.TypeBool: _types[w[1]] = new TypeInfo(Kind.Scalar, 1); break;
                case SpirvOp.TypeInt or SpirvOp.TypeFloat: _types[w[1]] = new TypeInfo(Kind.Scalar, (int)w[2]); break;
                case SpirvOp.TypeVector: _types[w[1]] = new TypeInfo(Kind.Vector, Element: w[2], Count: (int)w[3]); break;
                case SpirvOp.TypeArray: _types[w[1]] = new TypeInfo(Kind.Aggregate, Element: w[2], Count: (int)(ulong)_constants[w[3]]); break;
                case SpirvOp.TypeRuntimeArray: _types[w[1]] = new TypeInfo(Kind.RuntimeArray, Element: w[2]); break;
                case SpirvOp.TypeStruct: _types[w[1]] = new TypeInfo(Kind.Aggregate, Members: w[2..]); break;
                case SpirvOp.TypePointer: _types[w[1]] = new TypeInfo(Kind.Pointer, Element: w[3]); break;
                case SpirvOp.TypeVoid or SpirvOp.TypeFunction or SpirvOp.TypeImage or SpirvOp.TypeSampler or SpirvOp.TypeSampledImage:
                    _types[w[1]] = new TypeInfo(Kind.Other);
                    break;
                case SpirvOp.Constant:
                    _constants[w[2]] = w[3] | (count > 4 ? (ulong)w[4] << 32 : 0);
                    _resultTypes[w[2]] = w[1];
                    break;
                case SpirvOp.ConstantTrue: _constants[w[2]] = 1ul; break;
                case SpirvOp.ConstantFalse: _constants[w[2]] = 0ul; break;
                case SpirvOp.ConstantNull: _constants[w[2]] = Zero(w[1]); break;
                case SpirvOp.ConstantComposite: _constants[w[2]] = w[3..].Select(id => _constants[id]).ToArray(); break;
                case SpirvOp.Variable:
                    _variables[w[2]] = new Pointer(w[2], _names.GetValueOrDefault(w[2], string.Empty), string.Empty, _types[w[1]].Element);
                    if (count > 4) _initializers[w[2]] = _constants[w[4]];
                    break;
                case SpirvOp.Function:
                    current = new Function([], [], []);
                    _functions[w[2]] = current;
                    break;
            }
        }
    }

    // Supplies a named block word the module reads before writing it, or null for zero.
    public Func<string, string, ulong?>? HostWord { get; init; }

    // Pages, besides the null page, that the page table leaves unmapped.
    public IReadOnlySet<ulong>? UnmappedPages { get; init; }

    // A word of a named global variable after Run, by access path.
    public ulong ReadVariable(string name, string path) =>
        _state.GetValueOrDefault(Key(_variables.Values.Single(variable => variable.Name == name) with { Path = path }));

    public void Run(Func<ulong, uint> read, Action<ulong, uint> write)
    {
        _read = read;
        _write = write;
        _state = [];
        _steps = 0;
        foreach (var (id, value) in _initializers)
        {
            StoreValue(_variables[id], value);
        }

        Call(_functions[_entry], []);
    }

    private object? Call(Function function, object[] arguments)
    {
        var values = new Dictionary<uint, object>(function.Body.Count);
        for (var index = 0; index < arguments.Length; index++)
        {
            values[function.Parameters[index]] = arguments[index];
        }

        var previous = 0u;
        var block = 0u;
        var at = 0;
        while (true)
        {
            if (++_steps > StepLimit)
            {
                throw new InvalidOperationException("the module did not finish");
            }

            var w = function.Body[at++];
            var op = (SpirvOp)(w[0] & 0xFFFF);
            switch (op)
            {
                case SpirvOp.SelectionMerge or SpirvOp.LoopMerge or SpirvOp.Nop or SpirvOp.MemoryBarrier or SpirvOp.ControlBarrier:
                    continue;
                case SpirvOp.Branch:
                    (previous, block, at) = (block, w[1], function.Labels[w[1]]);
                    continue;
                case SpirvOp.BranchConditional:
                    var target = Scalar(values, w[1]) != 0 ? w[2] : w[3];
                    (previous, block, at) = (block, target, function.Labels[target]);
                    continue;
                case SpirvOp.Switch:
                    var selector = Scalar(values, w[1]);
                    var chosen = w[2];
                    for (var index = 3; index + 1 < w.Length; index += 2)
                    {
                        if (w[index] == selector) chosen = w[index + 1];
                    }

                    (previous, block, at) = (block, chosen, function.Labels[chosen]);
                    continue;
                case SpirvOp.Return or SpirvOp.Kill or SpirvOp.Unreachable:
                    return null;
                case SpirvOp.ReturnValue:
                    return Value(values, w[1]);
                case SpirvOp.Phi:
                    for (var index = 3; index + 1 < w.Length; index += 2)
                    {
                        if (w[index + 1] == previous)
                        {
                            values[w[2]] = Value(values, w[index]);
                            _resultTypes[w[2]] = w[1];
                            break;
                        }
                    }

                    continue;
                case SpirvOp.Variable:
                    var local = new Pointer(w[2], string.Empty, $"#{_steps}", _types[w[1]].Element);
                    values[w[2]] = local;
                    if (w.Length > 4) StoreValue(local, Value(values, w[4]));
                    continue;
                case SpirvOp.Store:
                    StoreValue((Pointer)Value(values, w[1]), Value(values, w[2]));
                    continue;
                case SpirvOp.FunctionCall:
                    var result = Call(_functions[w[3]], w[4..].Select(id => Value(values, id)).ToArray());
                    if (result is not null) values[w[2]] = result;
                    continue;
            }

            values[w[2]] = Evaluate(op, w, values);
            _resultTypes[w[2]] = w[1];
        }
    }

    private object Evaluate(SpirvOp op, uint[] w, Dictionary<uint, object> values)
    {
        var type = _types[w[1]];
        ulong Arg(int index) => Scalar(values, w[index]);
        ulong Mask(ulong value) => type.Bits is 0 or 64 ? value : value & ((1ul << type.Bits) - 1);
        long Signed(int index)
        {
            var bits = _types[_resultTypes[w[index]]].Bits;
            return bits == 64 ? (long)Arg(index) : (long)(Arg(index) << (64 - bits)) >> (64 - bits);
        }

        switch (op)
        {
            case SpirvOp.Load or SpirvOp.AtomicLoad: return LoadValue((Pointer)Value(values, w[3]));
            case SpirvOp.AccessChain: return Chain((Pointer)Value(values, w[3]), w[4..].Select(id => Scalar(values, id)));
            case SpirvOp.ConvertUToPtr: return new Pointer(0, string.Empty, string.Empty, _types[w[1]].Element, Arg(3), Physical: true);
            case SpirvOp.ArrayLength: return ((Pointer)Value(values, w[3])).Name == PageTable ? 1ul << 24 : 1ul << 16;
            case SpirvOp.CopyObject or SpirvOp.Bitcast: return Value(values, w[3]);
            case SpirvOp.UConvert: return Mask(Arg(3));
            case SpirvOp.SConvert: return Mask(unchecked((ulong)Signed(3)));
            case SpirvOp.SNegate: return Mask(unchecked(0 - Arg(3)));
            case SpirvOp.IAdd: return Mask(unchecked(Arg(3) + Arg(4)));
            case SpirvOp.ISub: return Mask(unchecked(Arg(3) - Arg(4)));
            case SpirvOp.IMul: return Mask(unchecked(Arg(3) * Arg(4)));
            case SpirvOp.UDiv: return Mask(Arg(3) / Arg(4));
            case SpirvOp.UMod: return Mask(Arg(3) % Arg(4));
            case SpirvOp.BitwiseAnd: return Mask(Arg(3) & Arg(4));
            case SpirvOp.BitwiseOr: return Mask(Arg(3) | Arg(4));
            case SpirvOp.BitwiseXor: return Mask(Arg(3) ^ Arg(4));
            case SpirvOp.Not: return Mask(~Arg(3));
            case SpirvOp.ShiftLeftLogical: return Mask(Arg(3) << (int)Arg(4));
            case SpirvOp.ShiftRightLogical: return Mask(Arg(3) >> (int)Arg(4));
            case SpirvOp.ShiftRightArithmetic: return Mask(unchecked((ulong)(Signed(3) >> (int)Arg(4))));
            case SpirvOp.BitFieldUExtract: return Mask((Arg(3) >> (int)Arg(4)) & ((1ul << (int)Arg(5)) - 1));
            case SpirvOp.BitFieldSExtract:
                var shift = 64 - (int)Arg(5);
                return Mask(unchecked((ulong)((long)((Arg(3) >> (int)Arg(4)) << shift) >> shift)));
            case SpirvOp.IEqual or SpirvOp.LogicalEqual: return Bool(Arg(3) == Arg(4));
            case SpirvOp.INotEqual or SpirvOp.LogicalNotEqual: return Bool(Arg(3) != Arg(4));
            case SpirvOp.ULessThan: return Bool(Arg(3) < Arg(4));
            case SpirvOp.ULessThanEqual: return Bool(Arg(3) <= Arg(4));
            case SpirvOp.UGreaterThan: return Bool(Arg(3) > Arg(4));
            case SpirvOp.UGreaterThanEqual: return Bool(Arg(3) >= Arg(4));
            case SpirvOp.SLessThan: return Bool(Signed(3) < Signed(4));
            case SpirvOp.SLessThanEqual: return Bool(Signed(3) <= Signed(4));
            case SpirvOp.SGreaterThan: return Bool(Signed(3) > Signed(4));
            case SpirvOp.SGreaterThanEqual: return Bool(Signed(3) >= Signed(4));
            case SpirvOp.LogicalAnd: return Bool(Arg(3) != 0 && Arg(4) != 0);
            case SpirvOp.LogicalOr: return Bool(Arg(3) != 0 || Arg(4) != 0);
            case SpirvOp.LogicalNot: return Bool(Arg(3) == 0);
            case SpirvOp.Select: return Value(values, Arg(3) != 0 ? w[4] : w[5]);
            case SpirvOp.CompositeConstruct: return w[3..].Select(id => Value(values, id)).ToArray();
            case SpirvOp.CompositeExtract:
                var composite = Value(values, w[3]);
                foreach (var index in w[4..]) composite = ((object[])composite)[index];
                return composite;
            case SpirvOp.CompositeInsert:
                var copy = (object[])((object[])Value(values, w[4])).Clone();
                copy[w[5]] = Value(values, w[3]);
                return copy;
            case SpirvOp.AtomicIAdd or SpirvOp.AtomicOr or SpirvOp.AtomicAnd or SpirvOp.AtomicExchange or SpirvOp.AtomicCompareExchange:
                var pointer = (Pointer)Value(values, w[3]);
                var old = (ulong)LoadValue(pointer);
                var next = op switch
                {
                    SpirvOp.AtomicIAdd => unchecked(old + Arg(6)),
                    SpirvOp.AtomicOr => old | Arg(6),
                    SpirvOp.AtomicAnd => old & Arg(6),
                    SpirvOp.AtomicExchange => Arg(6),
                    _ => old == Arg(8) ? Arg(7) : old,
                };
                StoreValue(pointer, Mask(next));
                return old;
        }

        throw new NotSupportedException($"SPIR-V operation {op}");
    }

    private static ulong Bool(bool value) => value ? 1ul : 0ul;

    private object Value(Dictionary<uint, object> values, uint id)
    {
        if (values.TryGetValue(id, out var local)) return local;
        if (_constants.TryGetValue(id, out var constant)) return constant;
        if (_variables.TryGetValue(id, out var variable)) return variable;
        throw new InvalidOperationException($"id {id} ({_names.GetValueOrDefault(id)}) has no value");
    }

    private ulong Scalar(Dictionary<uint, object> values, uint id) => (ulong)Value(values, id);

    private Pointer Chain(Pointer pointer, IEnumerable<ulong> indices)
    {
        var path = new StringBuilder(pointer.Path);
        var type = pointer.Type;
        foreach (var index in indices)
        {
            path.Append('.').Append(index);
            var info = _types[type];
            type = info.Members is { } members ? members[index] : info.Element;
        }

        return pointer with { Path = path.ToString(), Type = type };
    }

    private object LoadValue(Pointer pointer)
    {
        if (pointer.Physical)
        {
            return (ulong)_read(pointer.Address);
        }

        var info = _types[pointer.Type];
        if (info.Kind is Kind.Vector or Kind.Aggregate)
        {
            var count = info.Members?.Length ?? info.Count;
            return Enumerable.Range(0, count).Select(index => LoadValue(Chain(pointer, [(ulong)index]))).ToArray();
        }

        if (pointer.Name == PageTable)
        {
            var page = ulong.Parse(pointer.Path[(pointer.Path.LastIndexOf('.') + 1)..]);
            return page == 0 || UnmappedPages?.Contains(page) == true ? 0ul : page << Gen5SpirvTranslator.DeviceAddressPageBits;
        }

        if (_state.TryGetValue(Key(pointer), out var stored)) return stored;
        // Host-provided words of a named block (by variable name and access path) the test supplies.
        return HostWord?.Invoke(pointer.Name, pointer.Path) ?? 0ul;
    }

    private void StoreValue(Pointer pointer, object value)
    {
        if (pointer.Physical)
        {
            _write(pointer.Address, (uint)(ulong)value);
        }
        else if (value is object[] composite)
        {
            for (var index = 0; index < composite.Length; index++)
            {
                StoreValue(Chain(pointer, [(ulong)index]), composite[index]);
            }
        }
        else
        {
            _state[Key(pointer)] = (ulong)value;
        }
    }

    private static string Key(Pointer pointer) => $"{pointer.Root}{pointer.Path}";

    private object Zero(uint type)
    {
        var info = _types[type];
        if (info.Kind is Kind.Vector or Kind.Aggregate)
        {
            var count = info.Members?.Length ?? info.Count;
            return Enumerable.Range(0, count).Select(index => Zero(info.Members?[index] ?? info.Element)).ToArray();
        }

        return 0ul;
    }

    private static string Text(uint[] words, int start)
    {
        var bytes = new byte[(words.Length - start) * sizeof(uint)];
        Buffer.BlockCopy(words, start * sizeof(uint), bytes, 0, bytes.Length);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }
}
