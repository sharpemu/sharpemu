// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Numerics;

namespace SharpEmu.Libs.Gpu.GpuCommands;

public sealed class ShaderRegisterMirror : IReadOnlyDictionary<uint, uint>
{
    private const int BitsPerWord = 64;

    private readonly uint[] _values = new uint[RegisterBankLayout.ShaderRegisterCount];
    private readonly ulong[] _written = new ulong[RegisterBankLayout.ShaderRegisterCount / BitsPerWord];
    private Dictionary<uint, uint>? _outsideBank;
    private int _bankCount;

    public int Count => _bankCount + (_outsideBank?.Count ?? 0);

    public uint this[uint offset]
    {
        get => TryGetValue(offset, out var value) ? value : throw new KeyNotFoundException($"The shader register 0x{offset:X} was never written.");
        set
        {
            if (offset >= RegisterBankLayout.ShaderRegisterCount)
            {
                (_outsideBank ??= new())[offset] = value;
                return;
            }

            MarkWritten(offset);
            _values[offset] = value;
        }
    }

    public IEnumerable<uint> Keys
    {
        get
        {
            foreach (var entry in this)
            {
                yield return entry.Key;
            }
        }
    }

    public IEnumerable<uint> Values
    {
        get
        {
            foreach (var entry in this)
            {
                yield return entry.Value;
            }
        }
    }

    public void SetRange(uint firstOffset, ReadOnlySpan<uint> values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            MarkWritten(firstOffset + (uint)index);
        }

        values.CopyTo(_values.AsSpan((int)firstOffset, values.Length));
    }

    public bool ContainsKey(uint offset) => TryGetValue(offset, out _);

    public bool TryGetValue(uint offset, out uint value)
    {
        if (offset >= RegisterBankLayout.ShaderRegisterCount)
        {
            value = 0;
            return _outsideBank is { } outside && outside.TryGetValue(offset, out value);
        }

        if ((_written[offset / BitsPerWord] & (1ul << (int)(offset % BitsPerWord))) == 0)
        {
            value = 0;
            return false;
        }

        value = _values[offset];
        return true;
    }

    public void Clear()
    {
        if (_bankCount != 0)
        {
            Array.Clear(_written);
            _bankCount = 0;
        }

        _outsideBank?.Clear();
    }

    public IEnumerator<KeyValuePair<uint, uint>> GetEnumerator()
    {
        for (var word = 0; word < _written.Length; word++)
        {
            var bits = _written[word];
            while (bits != 0)
            {
                var offset = (uint)(word * BitsPerWord + BitOperations.TrailingZeroCount(bits));
                bits &= bits - 1;
                yield return new KeyValuePair<uint, uint>(offset, _values[offset]);
            }
        }

        if (_outsideBank is { } outside)
        {
            foreach (var entry in outside)
            {
                yield return entry;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void MarkWritten(uint offset)
    {
        ref var word = ref _written[offset / BitsPerWord];
        var bit = 1ul << (int)(offset % BitsPerWord);
        if ((word & bit) == 0)
        {
            word |= bit;
            _bankCount++;
        }
    }
}
