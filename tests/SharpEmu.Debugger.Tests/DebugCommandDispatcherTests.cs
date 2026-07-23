// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Debugger.Protocol;
using SharpEmu.Debugger.Session;
using Xunit;

namespace SharpEmu.Debugger.Tests;

public sealed class DebugCommandDispatcherTests
{
    private const int MaxMemoryChunk = 64 * 1024;

    [Fact]
    public void Dispatch_UnknownCommand_Fails()
    {
        var dispatcher = CreateDispatcher();

        var response = dispatcher.Dispatch(Parse("""{"command":"nope"}"""));

        Assert.False(response.Ok);
        Assert.Equal("nope", response.Command);
        Assert.Contains("Unknown command", response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispatch_Registers_WhenNotPaused_Fails()
    {
        var session = new FakeDebuggerSession(DebuggerRunState.Running);
        var dispatcher = new DebugCommandDispatcher(session);

        var response = dispatcher.Dispatch(Parse("""{"command":"registers"}"""));

        Assert.False(response.Ok);
        Assert.Equal("Target is not paused.", response.Error);
    }

    [Fact]
    public void Dispatch_ContinueAndStep_WhenNotPaused_Fail()
    {
        var session = new FakeDebuggerSession(DebuggerRunState.Running);
        var dispatcher = new DebugCommandDispatcher(session);

        var cont = dispatcher.Dispatch(Parse("""{"command":"continue"}"""));
        var step = dispatcher.Dispatch(Parse("""{"command":"step"}"""));

        Assert.False(cont.Ok);
        Assert.Equal("Target is not paused.", cont.Error);
        Assert.False(step.Ok);
        Assert.Equal("Target is not paused.", step.Error);
        Assert.Equal(1, session.ContinueCallCount);
        Assert.Equal(1, session.StepFrameCallCount);
    }

    [Fact]
    public void Dispatch_ReadMemory_RejectsLengthAboveCap()
    {
        var session = new FakeDebuggerSession();
        session.SeedMemory(0x1000, new byte[16]);
        var dispatcher = new DebugCommandDispatcher(session);

        var oversize = dispatcher.Dispatch(Parse(
            $$"""{"command":"read-memory","address":"0x1000","length":{{MaxMemoryChunk + 1}}}"""));
        var zero = dispatcher.Dispatch(Parse(
            """{"command":"read-memory","address":"0x1000","length":0}"""));

        Assert.False(oversize.Ok);
        Assert.Contains(MaxMemoryChunk.ToString(), oversize.Error);
        Assert.False(zero.Ok);
        Assert.Contains(MaxMemoryChunk.ToString(), zero.Error);
    }

    [Fact]
    public void Dispatch_WriteMemory_RejectsPayloadAboveCap()
    {
        var session = new FakeDebuggerSession();
        var dispatcher = new DebugCommandDispatcher(session);
        var hex = new string('A', (MaxMemoryChunk + 1) * 2);

        var response = dispatcher.Dispatch(Parse(
            $$"""{"command":"write-memory","address":"0x1000","bytes":"{{hex}}"}"""));

        Assert.False(response.Ok);
        Assert.Contains(MaxMemoryChunk.ToString(), response.Error);
    }

    [Fact]
    public void Dispatch_ReadMemory_WhenPaused_ReturnsHexBytes()
    {
        var session = new FakeDebuggerSession();
        session.SeedMemory(0x2000, [0xDE, 0xAD, 0xBE, 0xEF]);
        var dispatcher = new DebugCommandDispatcher(session);

        var response = dispatcher.Dispatch(Parse(
            """{"command":"read-memory","address":"0x2000","length":4}"""));

        Assert.True(response.Ok);
        Assert.NotNull(response.Data);
        Assert.Equal("DEADBEEF", GetString(response.Data!, "bytes"));
        Assert.Equal("0x0000000000002000", GetString(response.Data!, "address"));
    }

    [Fact]
    public void Dispatch_BreakpointCrud_HappyPath()
    {
        var session = new FakeDebuggerSession();
        var dispatcher = new DebugCommandDispatcher(session);

        var added = dispatcher.Dispatch(Parse(
            """{"command":"add-breakpoint","address":"0x401000","kind":"Execute","length":8}"""));
        Assert.True(added.Ok);
        Assert.NotNull(added.Data);

        var breakpoint = GetDict(added.Data!, "breakpoint");
        Assert.Equal(1, Convert.ToInt32(breakpoint["id"]));
        Assert.Equal("Execute", Assert.IsType<string>(breakpoint["kind"]));
        Assert.Equal(1UL, Convert.ToUInt64(breakpoint["length"]));
        Assert.True(Assert.IsType<bool>(breakpoint["enabled"]));

        var listed = dispatcher.Dispatch(Parse("""{"command":"list-breakpoints"}"""));
        Assert.True(listed.Ok);
        var list = Assert.IsAssignableFrom<System.Collections.IEnumerable>(listed.Data!["breakpoints"]);
        Assert.Single(list.Cast<object?>());

        var disabled = dispatcher.Dispatch(Parse(
            """{"command":"enable-breakpoint","id":1,"enabled":false}"""));
        Assert.True(disabled.Ok);
        Assert.False(session.Breakpoints.Snapshot().Single().Enabled);

        var removed = dispatcher.Dispatch(Parse(
            """{"command":"remove-breakpoint","id":1}"""));
        Assert.True(removed.Ok);
        Assert.Empty(session.Breakpoints.Snapshot());

        var missing = dispatcher.Dispatch(Parse(
            """{"command":"remove-breakpoint","id":1}"""));
        Assert.False(missing.Ok);
        Assert.Contains("No breakpoint", missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispatch_PingAndState_Succeed()
    {
        var session = new FakeDebuggerSession(DebuggerRunState.Running);
        var dispatcher = new DebugCommandDispatcher(session);

        var ping = dispatcher.Dispatch(Parse("""{"command":"ping"}"""));
        var state = dispatcher.Dispatch(Parse("""{"command":"state"}"""));

        Assert.True(ping.Ok);
        Assert.True(state.Ok);
        Assert.Equal("Running", GetString(state.Data!, "state"));
    }

    [Fact]
    public void Dispatch_Pause_RequestsPause()
    {
        var session = new FakeDebuggerSession(DebuggerRunState.Running);
        var dispatcher = new DebugCommandDispatcher(session);

        var response = dispatcher.Dispatch(Parse("""{"command":"pause"}"""));

        Assert.True(response.Ok);
        Assert.True(session.PauseRequested);
    }

    [Fact]
    public void Dispatch_ParseErrorCommand_SurfacesMessage()
    {
        var dispatcher = CreateDispatcher();

        var response = dispatcher.Dispatch(Parse(
            $$"""{"command":"{{JsonLineDebugProtocol.ParseErrorCommand}}","message":"bad line"}"""));

        Assert.False(response.Ok);
        Assert.Equal("bad line", response.Error);
    }

    private static DebugCommandDispatcher CreateDispatcher()
        => new(new FakeDebuggerSession());

    private static DebugRequest Parse(string json)
    {
        Assert.True(DebugRequest.TryParse(json, out var request, out var error), error);
        return request;
    }

    private static string GetString(IReadOnlyDictionary<string, object?> data, string key)
    {
        Assert.True(data.TryGetValue(key, out var value));
        Assert.NotNull(value);
        return Assert.IsType<string>(value);
    }

    private static IReadOnlyDictionary<string, object?> GetDict(
        IReadOnlyDictionary<string, object?> data,
        string key)
    {
        Assert.True(data.TryGetValue(key, out var value));
        Assert.NotNull(value);
        return Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(value);
    }
}
