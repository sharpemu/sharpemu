// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// The sandboxed child's <see cref="IHostControllerLink"/>: the launcher owns the pad, publishes which DualSense it has
/// in the bridge section, sends the effect reports the child hands it and plays the PCM the child writes to a slot's ring
/// (the Bluetooth DualSense takes its audio as HID reports, which only the launcher can write).
/// </summary>
internal sealed class GamepadBridgeControllerLink : IHostControllerLink
{
    private readonly GamepadBridgeMemory _memory;
    private readonly GamepadBridgeOutputWriter _output;
    private readonly bool[] _slotsInUse = new bool[GamepadBridgeMemory.PcmSlotCount];
    private readonly object _slotGate = new();
    private volatile bool _closed;

    public GamepadBridgeControllerLink(GamepadBridgeMemory memory, GamepadBridgeOutputWriter output)
    {
        _memory = memory;
        _output = output;
    }

    public bool IsClosed => _closed;

    /// <summary>Called before the section is unmapped: later calls (a stream closing after the window) do nothing.</summary>
    public void Close() => _closed = true;

    public HostControllerPads QueryPads() => _closed ? HostControllerPads.None : _memory.AudioPads;

    public bool SendEffect(HostControllerPads pad, ReadOnlySpan<byte> report)
    {
        // The launcher has one pad; a report for a kind it does not have would hit the wrong one.
        if (_closed || (_memory.AudioPads & pad) == 0 || report.Length > ControllerEffectCommand.MaxReportBytes)
        {
            return false;
        }

        _output.SendControllerEffect(report);
        return true;
    }

    public IHostAudioStream? TryOpenBluetoothStream(uint sampleRate, int maxQueuedBytes, bool routeSpeaker)
    {
        if (_closed || (_memory.AudioPads & HostControllerPads.BluetoothDualSense) == 0 ||
            sampleRate is < 8_000 or > 384_000)
        {
            return null;
        }

        int slot;
        lock (_slotGate)
        {
            slot = Array.IndexOf(_slotsInUse, false);
            if (slot < 0)
            {
                return null;
            }

            _slotsInUse[slot] = true;
        }

        _memory.OpenPcm(slot, sampleRate, maxQueuedBytes, routeSpeaker);
        return new GamepadBridgePcmStream(this, _memory, slot, sampleRate);
    }

    internal void ReleaseSlot(int slot)
    {
        if (!_closed)
        {
            _memory.ClosePcm(slot);
        }

        lock (_slotGate)
        {
            _slotsInUse[slot] = false;
        }
    }
}
