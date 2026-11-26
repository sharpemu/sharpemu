// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// The sandboxed child's end of the gamepad bridge: reads the launcher's pad state from the shared section and writes
/// the output commands back. Present only when <see cref="EnvironmentVariable"/> names the inherited section handle.
/// </summary>
internal sealed class GamepadBridgeClient : IDisposable
{
    public const string EnvironmentVariable = "SHARPEMU_GAMEPAD_BRIDGE";

    private readonly IDisposable? _section;
    private ulong _readIndex;

    internal GamepadBridgeClient(GamepadBridgeMemory memory, IDisposable? section = null)
    {
        Memory = memory;
        _section = section;
        Output = new GamepadBridgeOutputWriter(memory);
        // Events from before the child started are not replayed.
        _readIndex = memory.WrittenEvents;
    }

    /// <summary>True when the launcher asked for the bridge: the window then opens no SDL gamepad of its own.</summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public GamepadBridgeMemory Memory { get; }

    public IHostGamepadOutput Output { get; }

    public static GamepadBridgeClient? TryOpenFromEnvironment()
    {
        var text = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(text) || !OperatingSystem.IsWindows() ||
            !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var handle))
        {
            return null;
        }

        var section = GamepadBridgeSection.Open((nint)handle);
        return section is null ? null : new GamepadBridgeClient(section.Memory, section);
    }

    public bool TryReadState(out HostGamepadState state, out string? name) => Memory.TryReadInput(out state, out name);

    /// <summary>Copies the events since the last call, in order; 0 when there are none.</summary>
    public int ReadEvents(Span<BridgeEvent> destination) => Memory.ReadEvents(ref _readIndex, destination);

    public void Dispose() => _section?.Dispose();
}
