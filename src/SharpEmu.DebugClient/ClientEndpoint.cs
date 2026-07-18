// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;

namespace SharpEmu.DebugClient;

/// <summary>
/// Parses the <c>host:port</c> the client connects to. Mirrors the server's
/// defaults (loopback, port 5714) so a bare invocation attaches to a local
/// emulator with no arguments.
/// </summary>
internal static class ClientEndpoint
{
    public const int DefaultPort = 5714;

    public static bool TryParse(string? text, out string host, out int port, out string error)
    {
        host = "127.0.0.1";
        port = DefaultPort;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var value = text.Trim();
        var separator = value.LastIndexOf(':');
        if (separator >= 0)
        {
            var portText = value[(separator + 1)..];
            if (portText.Length > 0 && (!int.TryParse(portText, out port) || port is <= 0 or > 65535))
            {
                error = $"Invalid port '{portText}'.";
                return false;
            }

            value = value[..separator];
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            host = string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : value;
        }

        if (!IPAddress.TryParse(host, out _) && !Uri.CheckHostName(host).Equals(UriHostNameType.Dns))
        {
            error = $"Invalid host '{host}'.";
            return false;
        }

        return true;
    }
}
