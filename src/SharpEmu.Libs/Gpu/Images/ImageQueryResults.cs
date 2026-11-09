// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.Buffers;

namespace SharpEmu.Libs.Gpu.Images;

// A query snapshot stays valid when a caller removes images from the owner index.
internal struct ImageQueryResults
{
    private const int InlineCapacity = 16;

    [InlineArray(InlineCapacity)]
    private struct InlineResults
    {
        private ResourceSlotIdentifier _first;
    }

    private InlineResults _inline;
    private List<ResourceSlotIdentifier>? _overflow;
    public int Count { get; private set; }

    public ResourceSlotIdentifier this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _overflow is null ? _inline[index] : _overflow[index];
        }
    }

    public void Add(ResourceSlotIdentifier imageIdentifier)
    {
        if (_overflow is null && Count < InlineCapacity)
        {
            _inline[Count++] = imageIdentifier;
            return;
        }

        if (_overflow is null)
        {
            _overflow = new List<ResourceSlotIdentifier>(InlineCapacity * 2);
            for (var index = 0; index < Count; index++) _overflow.Add(_inline[index]);
        }
        _overflow.Add(imageIdentifier);
        Count++;
    }

    public Enumerator GetEnumerator() => new(this);

    public struct Enumerator
    {
        private ImageQueryResults _results;
        private int _index;

        internal Enumerator(ImageQueryResults results)
        {
            _results = results;
            _index = -1;
        }

        public ResourceSlotIdentifier Current => _results[_index];
        public bool MoveNext() => ++_index < _results.Count;
    }
}
