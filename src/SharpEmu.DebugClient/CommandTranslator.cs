// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace SharpEmu.DebugClient;

/// <summary>
/// Turns a friendly REPL line (<c>mem 0x1000 64</c>) into the JSON request the
/// server understands. Local-only verbs (help, quit) are reported back to the
/// caller instead of producing a request.
/// </summary>
internal static class CommandTranslator
{
    public enum ActionKind
    {
        SendRequest,
        ShowHelp,
        Quit,
        Ignore,
        Error,
    }

    public readonly record struct Result(ActionKind Kind, string? Payload = null, string? Error = null);

    public static Result Translate(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return new Result(ActionKind.Ignore);
        }

        var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();
        switch (verb)
        {
            case "help" or "?":
                return new Result(ActionKind.ShowHelp);
            case "quit" or "exit" or "q":
                return new Result(ActionKind.Quit);
            case "raw":
                var json = trimmed[verb.Length..].Trim();
                return json.Length == 0
                    ? Error("raw requires a JSON object argument.")
                    : Send(json);

            case "ping":
                return Request("ping");
            case "status" or "info":
                return Request("status");
            case "state":
                return Request("state");
            case "regs" or "registers":
                return Request("registers");
            case "continue" or "cont" or "c":
                return Request("continue");
            case "step" or "s":
                return Request("step");
            case "pause" or "p":
                return Request("pause");
            case "bp" or "breakpoints" or "bl":
                return Request("list-breakpoints");

            case "setreg":
                return parts.Length >= 3
                    ? Request("set-register", ("register", parts[1]), ("value", parts[2]))
                    : Error("Usage: setreg <register> <value>");
            case "mem" or "read":
                return parts.Length >= 3
                    ? Request("read-memory", ("address", parts[1]), ("length", parts[2]))
                    : Error("Usage: mem <address> <length>");
            case "write":
                return parts.Length >= 3
                    ? Request("write-memory", ("address", parts[1]), ("bytes", parts[2]))
                    : Error("Usage: write <address> <hex-bytes>");
            case "break" or "b":
                if (parts.Length < 2)
                {
                    return Error("Usage: break <address> [kind] [length]");
                }

                var breakArgs = new List<(string, string)> { ("address", parts[1]) };
                if (parts.Length >= 3)
                {
                    breakArgs.Add(("kind", parts[2]));
                }

                if (parts.Length >= 4)
                {
                    breakArgs.Add(("length", parts[3]));
                }

                return Request("add-breakpoint", breakArgs.ToArray());
            case "del" or "rm" or "delete":
                return parts.Length >= 2
                    ? Request("remove-breakpoint", ("id", parts[1]))
                    : Error("Usage: del <id>");
            case "enable":
                return parts.Length >= 2
                    ? Request("enable-breakpoint", ("id", parts[1]), ("enabled", "true"))
                    : Error("Usage: enable <id>");
            case "disable":
                return parts.Length >= 2
                    ? RequestWithBool("enable-breakpoint", ("id", parts[1]), enabledName: "enabled", enabled: false)
                    : Error("Usage: disable <id>");

            default:
                return Error($"Unknown command '{verb}'. Type 'help' for the command list.");
        }
    }

    private static Result Request(string command, params (string Name, string Value)[] args)
    {
        var payload = new Dictionary<string, object?> { ["command"] = command };
        foreach (var (name, value) in args)
        {
            payload[name] = value;
        }

        return Send(JsonSerializer.Serialize(payload));
    }

    private static Result RequestWithBool(string command, (string Name, string Value) idArg, string enabledName, bool enabled)
    {
        var payload = new Dictionary<string, object?>
        {
            ["command"] = command,
            [idArg.Name] = idArg.Value,
            [enabledName] = enabled,
        };
        return Send(JsonSerializer.Serialize(payload));
    }

    private static Result Send(string json) => new(ActionKind.SendRequest, json);

    private static Result Error(string message) => new(ActionKind.Error, Error: message);

    public const string HelpText = """
        SharpEmu debug client commands:
          status | info            Show target state and last stop
          state                    Show run state only
          regs | registers         Dump integer registers (paused only)
          setreg <reg> <value>     Set a register (rip/rflags/gp, paused only)
          mem <addr> <len>         Read guest memory as hex (paused only)
          write <addr> <hex>       Write guest memory from hex (paused only)
          break <addr> [kind] [len]  Add a breakpoint (kind: execute/readwatch/writewatch/accesswatch)
          bp | breakpoints         List breakpoints
          del <id>                 Remove a breakpoint
          enable <id> / disable <id>  Toggle a breakpoint
          continue | c             Resume the target
          step | s                 Resume and stop at the next frame
          pause                    Ask a running target to stop
          ping                     Round-trip check
          raw <json>               Send a literal JSON request
          help | ?                 Show this help
          quit | exit              Disconnect and exit
        Addresses and values accept decimal or 0x-prefixed hex.
        """;
}
