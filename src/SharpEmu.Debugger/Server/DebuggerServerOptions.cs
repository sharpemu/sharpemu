// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;

namespace SharpEmu.Debugger.Server;

/// <summary>Network configuration for a <see cref="DebuggerServer"/>.</summary>
public sealed class DebuggerServerOptions
{
    /// <summary>The default TCP port the debug server listens on.</summary>
    public const int DefaultPort = 5714;

    /// <summary>
    /// The address to bind. Defaults to loopback so the debug surface is not
    /// exposed off-box; a caller must opt in to a routable address explicitly.
    /// </summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>The TCP port to listen on.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// The maximum number of simultaneous client connections. Additional
    /// connections wait in the accept backlog.
    /// </summary>
    public int MaxClients { get; init; } = 4;

    /// <summary>
    /// Parses a <c>host:port</c>, bare <c>port</c>, or bare host into options.
    /// Returns false when the text cannot be interpreted.
    /// </summary>
    public static bool TryParseEndpoint(string? text, out DebuggerServerOptions options, out string error)
    {
        options = new DebuggerServerOptions();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var value = text.Trim();
        var host = value;
        var port = DefaultPort;

        var separator = value.LastIndexOf(':');
        if (separator >= 0)
        {
            var portText = value[(separator + 1)..];
            if (portText.Length > 0)
            {
                if (!int.TryParse(portText, out port) || port is <= 0 or > 65535)
                {
                    error = $"Invalid port '{portText}'.";
                    return false;
                }
            }

            host = value[..separator];
        }

        var address = IPAddress.Loopback;
        if (!string.IsNullOrWhiteSpace(host) &&
            !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
            !IPAddress.TryParse(host, out address!))
        {
            error = $"Invalid bind address '{host}'.";
            return false;
        }

        options = new DebuggerServerOptions
        {
            BindAddress = address ?? IPAddress.Loopback,
            Port = port,
        };
        return true;
    }
}
