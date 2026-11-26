// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>Which DualSense the host can reach for controller audio: exactly one per connection kind, or none.</summary>
[Flags]
internal enum HostControllerPads : byte
{
    None = 0,

    /// <summary>Exactly one DualSense is connected by cable.</summary>
    WiredDualSense = 1,

    /// <summary>Exactly one DualSense is connected over Bluetooth.</summary>
    BluetoothDualSense = 2,
}

/// <summary>
/// What the controller's audio path needs from whoever owns the pad. Outside the sandbox that is SDL in this process
/// (<see cref="Sdl.SdlControllerLink"/>); in the AppContainer sandbox the process sees no gamepad, so the launcher owns it
/// and the bridge's client answers (see HostControllerLink.Connect).
/// </summary>
internal interface IHostControllerLink
{
    /// <summary>The DualSense pads there are right now; a kind is set only when exactly one pad of it is connected.</summary>
    HostControllerPads QueryPads();

    /// <summary>
    /// Sends a raw DualSense output (effect) report to the pad of that connection kind. True when it was sent or handed
    /// to the owner of the pad.
    /// </summary>
    bool SendEffect(HostControllerPads pad, ReadOnlySpan<byte> report);

    /// <summary>
    /// A four-channel float stream played by the pad over Bluetooth (speaker on the first pair, actuators on the
    /// second), or null when there is none.
    /// </summary>
    IHostAudioStream? TryOpenBluetoothStream(uint sampleRate, int maxQueuedBytes, bool routeSpeaker);
}

/// <summary>The link the sandboxed child installs; null means the process talks to SDL itself.</summary>
internal static class HostControllerLink
{
    private static IHostControllerLink? _current;

    public static IHostControllerLink? Current => Volatile.Read(ref _current);

    public static void Connect(IHostControllerLink? link) => Volatile.Write(ref _current, link);
}

/// <summary>The DualSense output report that sets the audio routing.</summary>
internal static class DualSenseAudioRoute
{
    public const int ReportLength = 38;
    private const byte SpeakerValid = 0x20;
    private const byte AudioControlValid = 0x80;

    /// <summary>
    /// With the speaker, the right channel of the front pair plays on the speaker and the headphone jack is muted (audio
    /// control and speaker volume valid, speaker volume 0x64, output path 0x30, speaker preamp +6 dB: the values Linux's
    /// driver uses without headphones); without it, the front pair goes back to the headphone jack. Plugged-in
    /// headphones are not detected.
    /// </summary>
    public static byte[] Report(bool speaker)
    {
        var report = new byte[ReportLength];
        report[0] = AudioControlValid;
        if (speaker)
        {
            report[0] |= SpeakerValid;
            report[1] = 0x80;
            report[5] = 0x64;
            report[7] = 0x30;
            report[37] = 0x02;
        }

        return report;
    }

    /// <summary>True for a report <see cref="Report"/> built with the speaker on.</summary>
    public static bool IsSpeakerRoute(ReadOnlySpan<byte> report) =>
        report.Length >= ReportLength && (report[0] & SpeakerValid) != 0 && report[37] == 0x02;
}

/// <summary>The choice of controller output for the pads a link reported.</summary>
internal enum ControllerOutputKind
{
    None,
    WiredDevice,
    Bluetooth,
}

internal static class ControllerAudioSelection
{
    /// <summary>
    /// A wired pad needs its four-channel playback device to be found; the Bluetooth path (tried when the wired one is
    /// not usable) needs the link to open a stream, which <paramref name="bluetoothAvailable"/> reports.
    /// </summary>
    public static ControllerOutputKind Choose(HostControllerPads pads, bool wiredDeviceFound, bool bluetoothAvailable)
    {
        if ((pads & HostControllerPads.WiredDualSense) != 0 && wiredDeviceFound)
        {
            return ControllerOutputKind.WiredDevice;
        }

        if ((pads & HostControllerPads.BluetoothDualSense) != 0 && bluetoothAvailable)
        {
            return ControllerOutputKind.Bluetooth;
        }

        return ControllerOutputKind.None;
    }
}
