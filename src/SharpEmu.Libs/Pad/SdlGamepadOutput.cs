// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SDL;
using static SDL.SDL3;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// The output side of one open SDL gamepad: rumble, trigger rumble, DualSense adaptive triggers and the lightbar.
/// The SDL window drives it directly; the gamepad bridge's launcher side drives it from the child's commands.
/// </summary>
internal sealed unsafe class SdlGamepadOutput : IHostGamepadOutput, IHostControllerEffectSink
{
    private const uint OutputDurationMs = 5_000;

    private readonly object _gate;
    private byte _leftTriggerRumble;
    private byte _rightTriggerRumble;
    private byte _padLargeRumble;
    private byte _padSmallRumble;
    private byte _hapticLargeRumble;
    private byte _hapticSmallRumble;
    private long _hapticRumbleExpires;
    private bool _speakerRouted;

    /// <param name="gate">The lock that also guards the owner's use of <see cref="Gamepad"/>.</param>
    public SdlGamepadOutput(object gate)
    {
        _gate = gate;
    }

    /// <summary>The open gamepad the commands go to; set under the gate by the owner.</summary>
    public SDL_Gamepad* Gamepad { get; set; }

    public HostGamepadType Type { get; set; }

    /// <summary>Forgets the commands of the gamepad that went away (call under the gate).</summary>
    public void Reset()
    {
        Gamepad = null;
        Type = HostGamepadType.Generic;
        _leftTriggerRumble = 0;
        _rightTriggerRumble = 0;
        _padLargeRumble = 0;
        _padSmallRumble = 0;
        _hapticLargeRumble = 0;
        _hapticSmallRumble = 0;
        _hapticRumbleExpires = 0;
        _speakerRouted = false;
    }

    public void SetRumble(byte largeMotor, byte smallMotor)
    {
        lock (_gate)
        {
            _padLargeRumble = largeMotor;
            _padSmallRumble = smallMotor;
            ApplyRumble();
        }
    }

    public void SetHapticRumble(byte largeMotor, byte smallMotor, uint durationMs)
    {
        lock (_gate)
        {
            _hapticLargeRumble = largeMotor;
            _hapticSmallRumble = smallMotor;
            _hapticRumbleExpires = Environment.TickCount64 + durationMs;
            ApplyRumble();
        }
    }

    private void ApplyRumble()
    {
        if (!IsGamepadConnected())
        {
            return;
        }

        var hapticRemaining = _hapticRumbleExpires - Environment.TickCount64;
        var hapticActive = hapticRemaining > 0;
        var large = Math.Max(_padLargeRumble, hapticActive ? _hapticLargeRumble : (byte)0);
        var small = Math.Max(_padSmallRumble, hapticActive ? _hapticSmallRumble : (byte)0);
        var duration = (_padLargeRumble | _padSmallRumble) == 0 && hapticActive
            ? (uint)hapticRemaining
            : OutputDurationMs;
        SDL_RumbleGamepad(
            Gamepad,
            ExpandByte(large),
            ExpandByte(small),
            duration);
    }

    public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger)
    {
        lock (_gate)
        {
            if (IsGamepadConnected())
            {
                if (leftTrigger is { } left)
                {
                    _leftTriggerRumble = left;
                }
                if (rightTrigger is { } right)
                {
                    _rightTriggerRumble = right;
                }

                SDL_RumbleGamepadTriggers(
                    Gamepad,
                    ExpandByte(_leftTriggerRumble),
                    ExpandByte(_rightTriggerRumble),
                    OutputDurationMs);
            }
        }
    }

    public void SetAdaptiveTriggerEffect(
        HostAdaptiveTriggerEffect? leftTrigger,
        HostAdaptiveTriggerEffect? rightTrigger)
    {
        lock (_gate)
        {
            if (!IsGamepadConnected())
            {
                return;
            }

            if (Type != HostGamepadType.DualSense)
            {
                if (leftTrigger is { } fallbackLeft)
                {
                    _leftTriggerRumble = fallbackLeft.FallbackStrength;
                }
                if (rightTrigger is { } fallbackRight)
                {
                    _rightTriggerRumble = fallbackRight.FallbackStrength;
                }

                SDL_RumbleGamepadTriggers(
                    Gamepad,
                    ExpandByte(_leftTriggerRumble),
                    ExpandByte(_rightTriggerRumble),
                    OutputDurationMs);
                return;
            }

            Span<byte> state = stackalloc byte[47];
            state.Clear();
            if (rightTrigger is { } nativeRight)
            {
                state[0] |= 0x04;
                nativeRight.CopyTo(state[10..21]);
            }
            if (leftTrigger is { } nativeLeft)
            {
                state[0] |= 0x08;
                nativeLeft.CopyTo(state[21..32]);
            }

            if (state[0] == 0)
            {
                return;
            }

            fixed (byte* data = state)
            {
                SDL_SendGamepadEffect(Gamepad, (nint)data, state.Length);
            }
        }
    }

    public void SetLightbar(byte red, byte green, byte blue)
    {
        lock (_gate)
        {
            if (IsGamepadConnected())
            {
                SDL_SetGamepadLED(Gamepad, red, green, blue);
            }
        }
    }

    public void ResetLightbar() => SetLightbar(0, 0, 64);

    /// <summary>
    /// Sends a raw DualSense output report the child built (the controller audio routing) to the open pad. The routing
    /// state is remembered so <see cref="RestoreAudioRoute"/> can undo a speaker route the child never took back.
    /// </summary>
    public void SendControllerEffect(ReadOnlySpan<byte> report)
    {
        lock (_gate)
        {
            if (!IsGamepadConnected() || report.IsEmpty)
            {
                return;
            }

            bool sent;
            fixed (byte* data = report)
            {
                sent = SDL_SendGamepadEffect(Gamepad, (nint)data, report.Length);
            }

            if (!sent)
            {
                Console.Error.WriteLine("[LOADER][WARN] gamepad bridge: the controller effect report was refused");
            }
            else if ((report[0] & 0x80) != 0)
            {
                _speakerRouted = DualSenseAudioRoute.IsSpeakerRoute(report);
            }
        }
    }

    /// <summary>Puts the front audio pair back on the headphone jack when the speaker route is still on (call under the gate).</summary>
    public void RestoreAudioRoute()
    {
        if (_speakerRouted && IsGamepadConnected())
        {
            var report = DualSenseAudioRoute.Report(speaker: false);
            fixed (byte* data = report)
            {
                SDL_SendGamepadEffect(Gamepad, (nint)data, report.Length);
            }
        }

        _speakerRouted = false;
    }

    private bool IsGamepadConnected() => Gamepad is not null && SDL_GamepadConnected(Gamepad);

    private static ushort ExpandByte(byte value) => (ushort)(value * 257);
}
