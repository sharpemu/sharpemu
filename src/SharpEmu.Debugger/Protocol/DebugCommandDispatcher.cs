// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Debugger.Breakpoints;
using SharpEmu.Debugger.Session;
using SharpEmu.HLE;

namespace SharpEmu.Debugger.Protocol;

/// <summary>
/// Translates parsed <see cref="DebugRequest"/> verbs into operations on an
/// <see cref="IDebuggerSession"/> and packages the outcome as a
/// <see cref="DebugResponse"/>. This is the single place command semantics live,
/// so it is shared by every connection and independent of the wire format.
/// </summary>
public sealed class DebugCommandDispatcher
{
    private readonly IDebuggerSession _session;

    public DebugCommandDispatcher(IDebuggerSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public DebugResponse Dispatch(DebugRequest request)
    {
        return request.Command switch
        {
            JsonLineDebugProtocol.ParseErrorCommand => ParseError(request),
            "ping" => DebugResponse.Success(request.Command),
            "status" or "info" => Status(request),
            "state" => DebugResponse.Success(request.Command, new Dictionary<string, object?>
            {
                ["state"] = _session.State.ToString(),
            }),
            "registers" or "regs" => Registers(request),
            "set-register" or "set-reg" => SetRegister(request),
            "read-memory" or "read-mem" => ReadMemory(request),
            "write-memory" or "write-mem" => WriteMemory(request),
            "list-breakpoints" or "breakpoints" => ListBreakpoints(request),
            "add-breakpoint" or "break" => AddBreakpoint(request),
            "remove-breakpoint" or "delete-breakpoint" => RemoveBreakpoint(request),
            "enable-breakpoint" => EnableBreakpoint(request),
            "continue" or "cont" or "c" => Simple(request, _session.Continue(), "Target is not paused."),
            "step" or "s" => Simple(request, _session.StepFrame(), "Target is not paused."),
            "pause" => Pause(request),
            _ => DebugResponse.Failure(request.Command, $"Unknown command '{request.Command}'."),
        };
    }

    private static DebugResponse ParseError(DebugRequest request)
    {
        var message = request.TryGetString("message", out var text) ? text : "Malformed request.";
        return DebugResponse.Failure(request.Command, message);
    }

    private DebugResponse Status(DebugRequest request)
    {
        var data = new Dictionary<string, object?>
        {
            ["state"] = _session.State.ToString(),
            ["breakpoints"] = _session.Breakpoints.Snapshot().Count,
        };

        if (_session.LastStop is { } stop)
        {
            data["lastStop"] = DescribeStop(stop);
        }

        return DebugResponse.Success(request.Command, data);
    }

    private DebugResponse Registers(DebugRequest request)
    {
        if (!_session.TryGetRegisters(out var registers))
        {
            return NotPaused(request);
        }

        return DebugResponse.Success(request.Command, new Dictionary<string, object?>
        {
            ["registers"] = DescribeRegisters(registers),
        });
    }

    private DebugResponse SetRegister(DebugRequest request)
    {
        if (!request.TryGetString("register", out var name) || !TryParseRegister(name, out var id))
        {
            return DebugResponse.Failure(request.Command, "Expected a valid 'register' name.");
        }

        if (!request.TryGetUInt64("value", out var value))
        {
            return DebugResponse.Failure(request.Command, "Expected a 'value'.");
        }

        return _session.TrySetRegister(id, value)
            ? DebugResponse.Success(request.Command)
            : DebugResponse.Failure(request.Command, "Register is not writable or target is not paused.");
    }

    private DebugResponse ReadMemory(DebugRequest request)
    {
        if (!request.TryGetUInt64("address", out var address))
        {
            return DebugResponse.Failure(request.Command, "Expected an 'address'.");
        }

        if (!request.TryGetInt32("length", out var length) || length <= 0 || length > MaxMemoryChunk)
        {
            return DebugResponse.Failure(request.Command, $"Expected a 'length' between 1 and {MaxMemoryChunk}.");
        }

        var buffer = new byte[length];
        if (!_session.TryReadMemory(address, buffer))
        {
            return DebugResponse.Failure(request.Command, "Memory is unreadable or target is not paused.");
        }

        return DebugResponse.Success(request.Command, new Dictionary<string, object?>
        {
            ["address"] = FormatAddress(address),
            ["length"] = length,
            ["bytes"] = Convert.ToHexString(buffer),
        });
    }

    private DebugResponse WriteMemory(DebugRequest request)
    {
        if (!request.TryGetUInt64("address", out var address))
        {
            return DebugResponse.Failure(request.Command, "Expected an 'address'.");
        }

        if (!request.TryGetString("bytes", out var hex) || hex.Length == 0 || (hex.Length & 1) != 0)
        {
            return DebugResponse.Failure(request.Command, "Expected 'bytes' as an even-length hex string.");
        }

        byte[] data;
        try
        {
            data = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return DebugResponse.Failure(request.Command, "'bytes' is not valid hex.");
        }

        if (data.Length > MaxMemoryChunk)
        {
            return DebugResponse.Failure(request.Command, $"Cannot write more than {MaxMemoryChunk} bytes at once.");
        }

        return _session.TryWriteMemory(address, data)
            ? DebugResponse.Success(request.Command, new Dictionary<string, object?> { ["written"] = data.Length })
            : DebugResponse.Failure(request.Command, "Memory is unwritable or target is not paused.");
    }

    private DebugResponse ListBreakpoints(DebugRequest request)
    {
        var breakpoints = _session.Breakpoints.Snapshot()
            .OrderBy(breakpoint => breakpoint.Id)
            .Select(DescribeBreakpoint)
            .ToArray();

        return DebugResponse.Success(request.Command, new Dictionary<string, object?>
        {
            ["breakpoints"] = breakpoints,
        });
    }

    private DebugResponse AddBreakpoint(DebugRequest request)
    {
        if (!request.TryGetUInt64("address", out var address))
        {
            return DebugResponse.Failure(request.Command, "Expected an 'address'.");
        }

        var kind = BreakpointKind.Execute;
        if (request.TryGetString("kind", out var kindText) && !TryParseBreakpointKind(kindText, out kind))
        {
            return DebugResponse.Failure(request.Command, $"Unknown breakpoint kind '{kindText}'.");
        }

        var length = 1UL;
        if (request.TryGetUInt64("length", out var requestedLength) && requestedLength > 0)
        {
            length = requestedLength;
        }

        var breakpoint = _session.Breakpoints.Add(kind, address, length);
        return DebugResponse.Success(request.Command, new Dictionary<string, object?>
        {
            ["breakpoint"] = DescribeBreakpoint(breakpoint),
        });
    }

    private DebugResponse RemoveBreakpoint(DebugRequest request)
    {
        if (!request.TryGetInt32("id", out var id))
        {
            return DebugResponse.Failure(request.Command, "Expected an 'id'.");
        }

        return _session.Breakpoints.Remove(id)
            ? DebugResponse.Success(request.Command)
            : DebugResponse.Failure(request.Command, $"No breakpoint with id {id}.");
    }

    private DebugResponse EnableBreakpoint(DebugRequest request)
    {
        if (!request.TryGetInt32("id", out var id))
        {
            return DebugResponse.Failure(request.Command, "Expected an 'id'.");
        }

        var enabled = !request.TryGetBool("enabled", out var requested) || requested;
        return _session.Breakpoints.SetEnabled(id, enabled)
            ? DebugResponse.Success(request.Command)
            : DebugResponse.Failure(request.Command, $"No breakpoint with id {id}.");
    }

    private DebugResponse Pause(DebugRequest request)
    {
        _session.RequestPause();
        return DebugResponse.Success(request.Command);
    }

    private static DebugResponse Simple(DebugRequest request, bool succeeded, string failureMessage)
        => succeeded ? DebugResponse.Success(request.Command) : DebugResponse.Failure(request.Command, failureMessage);

    private static DebugResponse NotPaused(DebugRequest request)
        => DebugResponse.Failure(request.Command, "Target is not paused.");

    internal static IReadOnlyDictionary<string, object?> DescribeStop(DebugStopEvent stop)
    {
        var data = new Dictionary<string, object?>
        {
            ["reason"] = stop.Reason.ToString(),
            ["address"] = FormatAddress(stop.Address),
            ["frameKind"] = stop.FrameKind.ToString(),
            ["frameLabel"] = stop.FrameLabel,
            ["registers"] = DescribeRegisters(stop.Registers),
        };

        if (stop.Breakpoint is { } breakpoint)
        {
            data["breakpoint"] = DescribeBreakpoint(breakpoint);
        }

        if (stop.Result is { } result)
        {
            data["result"] = result.ToString();
        }

        if (stop.Detail is { } detail)
        {
            data["detail"] = detail;
        }

        if (stop.OpcodeBytes is { } opcodeBytes)
        {
            data["opcodeBytes"] = opcodeBytes;
        }

        if (stop.StallInfo is { } stall)
        {
            data["stall"] = new Dictionary<string, object?>
            {
                ["kind"] = stall.Kind.ToString(),
                ["nid"] = stall.Nid,
                ["instructionPointer"] = FormatAddress(stall.InstructionPointer),
                ["dispatchIndex"] = stall.DispatchIndex,
                ["argument0"] = FormatAddress(stall.Argument0),
                ["argument1"] = FormatAddress(stall.Argument1),
                ["resolved"] = stall.IsResolved,
                ["library"] = stall.LibraryName,
                ["function"] = stall.FunctionName,
            };
        }

        return data;
    }

    private static IReadOnlyDictionary<string, object?> DescribeRegisters(DebugRegisterFile registers)
    {
        var result = new Dictionary<string, object?>(20);
        for (var i = 0; i < 16; i++)
        {
            result[((CpuRegister)i).ToString().ToLowerInvariant()] = FormatAddress(registers[(CpuRegister)i]);
        }

        result["rip"] = FormatAddress(registers.Rip);
        result["rflags"] = FormatAddress(registers.Rflags);
        result["fs_base"] = FormatAddress(registers.FsBase);
        result["gs_base"] = FormatAddress(registers.GsBase);
        return result;
    }

    private static IReadOnlyDictionary<string, object?> DescribeBreakpoint(Breakpoint breakpoint)
        => new Dictionary<string, object?>
        {
            ["id"] = breakpoint.Id,
            ["kind"] = breakpoint.Kind.ToString(),
            ["address"] = FormatAddress(breakpoint.Address),
            ["length"] = breakpoint.Length,
            ["enabled"] = breakpoint.Enabled,
        };

    private static string FormatAddress(ulong value) => $"0x{value:X16}";

    private static bool TryParseRegister(string name, out DebugRegisterId id)
    {
        var normalized = name.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "rip":
                id = DebugRegisterId.Rip;
                return true;
            case "rflags":
                id = DebugRegisterId.Rflags;
                return true;
            case "fs_base" or "fsbase":
                id = DebugRegisterId.FsBase;
                return true;
            case "gs_base" or "gsbase":
                id = DebugRegisterId.GsBase;
                return true;
        }

        return Enum.TryParse(normalized, ignoreCase: true, out id) && Enum.IsDefined(id);
    }

    private static bool TryParseBreakpointKind(string text, out BreakpointKind kind)
        => Enum.TryParse(text.Trim(), ignoreCase: true, out kind) && Enum.IsDefined(kind);

    private const int MaxMemoryChunk = 64 * 1024;
}
