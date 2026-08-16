// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

/// <summary>
/// Tracks depth metadata clears until the matching depth layer consumes them.
/// AMD fast clears can write HTILE through DMA or compute without setting the
/// direct depth-clear register on the later draw. This tracker handles depth
/// only. SharpEmu does not model stencil metadata yet.
/// </summary>
internal sealed class AgcHtileMetadataTracker
{
    private readonly object _gate = new();
    private readonly HashSet<ulong> _knownAddresses = [];
    private readonly Dictionary<ulong, uint> _clearedLayers = [];

    public void Register(ulong address)
    {
        if (address == 0)
        {
            return;
        }

        lock (_gate)
        {
            _knownAddresses.Add(address);
        }
    }

    public bool TryMarkAllLayersCleared(ulong address)
    {
        if (address == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_knownAddresses.Contains(address))
            {
                return false;
            }

            _clearedLayers[address] = uint.MaxValue;
            return true;
        }
    }

    public bool IsRegistered(ulong address)
    {
        if (address == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return _knownAddresses.Contains(address);
        }
    }

    public int UnregisterRange(ulong address, ulong length)
    {
        if (address == 0 || length == 0 || ulong.MaxValue - address < length)
        {
            return 0;
        }

        var endAddress = address + length;
        lock (_gate)
        {
            var staleAddresses = _knownAddresses
                .Where(candidate => candidate >= address && candidate < endAddress)
                .ToArray();
            foreach (var staleAddress in staleAddresses)
            {
                _knownAddresses.Remove(staleAddress);
                _clearedLayers.Remove(staleAddress);
            }

            return staleAddresses.Length;
        }
    }

    public bool TryConsumeClearedLayer(ulong address, uint layer)
    {
        if (address == 0 || layer >= 32)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_clearedLayers.TryGetValue(address, out var mask))
            {
                return false;
            }

            var layerMask = 1u << checked((int)layer);
            if ((mask & layerMask) == 0)
            {
                return false;
            }

            mask &= ~layerMask;
            if (mask == 0)
            {
                _clearedLayers.Remove(address);
            }
            else
            {
                _clearedLayers[address] = mask;
            }

            return true;
        }
    }
}
