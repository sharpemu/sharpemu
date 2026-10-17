// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// Evaluated values by ScalarValue.Id, reset once per draw. An open-addressing table whose
// slots carry the generation that filled them, so a reset only bumps the generation: a
// Dictionary.Clear zeroed its whole capacity on every draw.
internal sealed class ScalarValueCache
{
    private int[] _ids = new int[64];
    private uint[] _generations = new uint[64];
    private ulong[] _values = new ulong[64];
    private uint _generation = 1;
    private int _count;

    public int Count => _count;

    public int Capacity => _ids.Length;

    public bool TryGetValue(ScalarValue key, out ulong value)
    {
        var mask = _ids.Length - 1;
        for (var slot = Slot(key.Id, mask); _generations[slot] == _generation; slot = (slot + 1) & mask)
        {
            if (_ids[slot] == key.Id)
            {
                value = _values[slot];
                return true;
            }
        }

        value = 0;
        return false;
    }

    public ulong this[ScalarValue key]
    {
        set
        {
            if ((_count + 1) * 2 > _ids.Length)
            {
                Grow();
            }

            Insert(key.Id, value);
        }
    }

    public void Reset()
    {
        _count = 0;
        if (++_generation == 0)
        {
            Array.Clear(_generations);
            _generation = 1;
        }
    }

    private void Insert(int id, ulong value)
    {
        var mask = _ids.Length - 1;
        var slot = Slot(id, mask);
        for (; _generations[slot] == _generation; slot = (slot + 1) & mask)
        {
            if (_ids[slot] == id)
            {
                _values[slot] = value;
                return;
            }
        }

        _generations[slot] = _generation;
        _ids[slot] = id;
        _values[slot] = value;
        _count++;
    }

    private void Grow()
    {
        var ids = _ids;
        var generations = _generations;
        var values = _values;
        var generation = _generation;
        _ids = new int[ids.Length * 2];
        _generations = new uint[ids.Length * 2];
        _values = new ulong[ids.Length * 2];
        _generation = 1;
        _count = 0;
        for (var index = 0; index < ids.Length; index++)
        {
            if (generations[index] == generation)
            {
                Insert(ids[index], values[index]);
            }
        }
    }

    private static int Slot(int id, int mask) => (int)(((uint)id * 2654435769u) >> 7) & mask;
}
