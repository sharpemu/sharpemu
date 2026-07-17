// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.DualSense;

/// <summary>
/// One open connection to a DualSense, abstracted over the host's HID stack:
/// Win32 HID device files, Linux hidraw, or macOS IOKit. Implementations move
/// raw HID reports only — the wire format lives in <see cref="DualSenseProtocol"/>.
/// </summary>
internal abstract class DualSenseTransport : IDisposable
{
    /// <summary>
    /// True when the pad is attached over Bluetooth, which needs the 0x31
    /// report wrapper on output. Valid once the transport is open; a
    /// transport that cannot tell reports the USB layout and lets the first
    /// parsed input report correct it (see <see cref="ReportsTransport"/>).
    /// </summary>
    internal abstract bool Bluetooth { get; }

    /// <summary>
    /// True when <see cref="Bluetooth"/> is authoritative at open time. When
    /// false the reader infers the transport from the first input report id.
    /// </summary>
    internal virtual bool ReportsTransport => true;

    /// <summary>
    /// Blocks until the next input report arrives and copies it into
    /// <paramref name="buffer"/>, returning its length. Zero or negative
    /// means the device is gone and the reader should reconnect.
    /// </summary>
    internal abstract int Read(Span<byte> buffer);

    /// <summary>Sends an output report; false when the device rejected it or vanished.</summary>
    internal abstract bool Write(ReadOnlySpan<byte> report);

    /// <summary>
    /// Corrects <see cref="Bluetooth"/> from an observed input report id, for
    /// transports that cannot determine it at open time.
    /// </summary>
    internal virtual void ObserveTransport(bool bluetooth)
    {
    }

    public abstract void Dispose();
}
