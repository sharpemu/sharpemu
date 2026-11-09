// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class ShaderCalleeTiersTests
{
    private const ulong Caller = 0x0123_4567_89AB_CDEF;
    private const ulong First = 0x5000_0000_0000;
    private const ulong Second = 0x5000_0001_0000;
    private static readonly byte[] FirstCode = [1];
    private static readonly byte[] SecondCode = [2];
    private static readonly byte[] OtherCode = [3];

    private static ShaderCallLibrary Named(params (ulong Address, byte[] Code)[] callees) =>
        new("identity", [], "layout", []) { CurrentCodeHashes = callees.ToDictionary(static callee => callee.Address, static callee => callee.Code) };

    private static UnitedCallee Callee(ulong address, byte[] code) =>
        new(new ShaderCallTarget(address, 0, new Gen5ShaderProgram(address, [])), code, []);

    private static Dictionary<uint, Dictionary<ulong, UnitedCallee>> Union(params UnitedCallee[] callees) =>
        new() { [0x40] = callees.ToDictionary(static callee => callee.Target.Address) };

    private static FakePipelineHost Host() => new(new FakeCpuMemory(0x10000, 0x1000)) { CallMarksSupported = true };

    // A mark counts only from the third snapshot after the caller named the callee's code at that address: the copy in flight
    // then and the one after it can still hold the marks of dispatches recorded before.
    [Fact]
    public void A_mark_counts_once_it_is_newer_than_the_naming()
    {
        var host = Host();
        var tiers = new ShaderCalleeTiers(new Dictionary<uint, Dictionary<ulong, UnitedCallee>>());
        host.CallMarkSnapshots = 5;
        Assert.Equal(0, tiers.Observe(Named((First, FirstCode), (Second, SecondCode)), Caller, host));
        host.MarkedBits[CallMarks.Bit(First, Caller)] = 7;
        Assert.Equal(0, tiers.Observe(Named((First, FirstCode), (Second, SecondCode)), Caller, host));
        host.MarkedBits[CallMarks.Bit(First, Caller)] = 8;
        Assert.Equal(1, tiers.Observe(Named((First, FirstCode), (Second, SecondCode)), Caller, host));
        Assert.Equal(1, tiers.Called);
        // Another caller's marks are its own.
        Assert.Equal(0, new ShaderCalleeTiers(new Dictionary<uint, Dictionary<ulong, UnitedCallee>>()).Observe(Named((First, FirstCode)), Caller + 1, host));
    }

    // New code at an address is named again: the marks of the code that was there before do not make it called.
    [Fact]
    public void New_code_at_an_address_needs_its_own_mark()
    {
        var host = Host();
        var tiers = new ShaderCalleeTiers(new Dictionary<uint, Dictionary<ulong, UnitedCallee>>());
        tiers.Observe(Named((First, FirstCode)), Caller, host);
        host.CallMarkSnapshots = 10;
        tiers.Observe(Named((First, OtherCode)), Caller, host);
        host.MarkedBits[CallMarks.Bit(First, Caller)] = 9;
        Assert.Equal(0, tiers.Observe(Named((First, OtherCode)), Caller, host));
        host.MarkedBits[CallMarks.Bit(First, Caller)] = 13;
        Assert.Equal(1, tiers.Observe(Named((First, OtherCode)), Caller, host));
    }

    // The program holds the called callees; the choice stays while the program of the last choice is not ready, so the caller
    // compiles one program at a time.
    [Fact]
    public void The_choice_waits_for_the_program_of_the_last_choice()
    {
        var host = Host();
        var first = Callee(First, FirstCode);
        var second = Callee(Second, SecondCode);
        var union = Union(first, second);
        var tiers = new ShaderCalleeTiers(Union(first));
        Assert.Equal([First], tiers.Choose(union, fallbackGeneration: -1, snapshots: 0)[0x40].Keys);

        tiers.Observe(Named((First, FirstCode), (Second, SecondCode)), Caller, host);
        host.MarkedBits[CallMarks.Bit(Second, Caller)] = 3;
        host.CallMarkSnapshots = 3;
        Assert.Equal(1, tiers.Observe(Named((First, FirstCode), (Second, SecondCode)), Caller, host));
        Assert.Equal([First], tiers.Choose(union, fallbackGeneration: -1, snapshots: 20)[0x40].Keys);

        tiers.LinkedGeneration = 10;
        Assert.Equal([First], tiers.Choose(union, fallbackGeneration: 9, snapshots: 20)[0x40].Keys);
        Assert.Equal([First, Second], tiers.Choose(union, fallbackGeneration: 10, snapshots: 20)[0x40].Keys.Order());
        Assert.Null(tiers.LinkedGeneration);

        // The same choice keeps its program.
        tiers.LinkedGeneration = 11;
        Assert.Equal([First, Second], tiers.Choose(union, fallbackGeneration: 11, snapshots: 21)[0x40].Keys.Order());
        Assert.Equal(11, tiers.LinkedGeneration);
    }

    // The first program holds exactly the callees an earlier session kept, whose pipeline the linked cache holds. The same
    // code at another address joins a choice only once a dispatch finds that copy unserved: a moved copy needs no new program.
    [Fact]
    public void The_first_choice_is_the_kept_program()
    {
        var union = Union(Callee(First, FirstCode), Callee(Second, FirstCode));
        var tiers = new ShaderCalleeTiers(Union(union[0x40][First]));
        Assert.Equal([First], tiers.Choose(union, fallbackGeneration: -1, snapshots: 0)[0x40].Keys);
        tiers.LinkedGeneration = 10;
        Assert.Equal([First], tiers.Choose(union, fallbackGeneration: 9, snapshots: 1)[0x40].Keys);
        Assert.Equal([First], tiers.Choose(union, fallbackGeneration: 10, snapshots: 20)[0x40].Keys);
        tiers.NoteUnservedCopy(snapshots: 21);
        Assert.Equal([First, Second], tiers.Choose(union, fallbackGeneration: 10, snapshots: 22)[0x40].Keys.Order());
    }

    // Callees that become called over a few frames join one choice: a new choice waits until no callee has become called for
    // a few snapshots, and no longer than a bound after the first one the chosen program lacks.
    [Fact]
    public void A_new_choice_waits_for_the_calls_to_settle()
    {
        var host = Host();
        var first = Callee(First, FirstCode);
        var second = Callee(Second, SecondCode);
        var union = Union(first, second);
        var tiers = new ShaderCalleeTiers(new Dictionary<uint, Dictionary<ulong, UnitedCallee>>());
        Assert.Empty(tiers.Choose(union, fallbackGeneration: -1, snapshots: 0)[0x40]);
        tiers.LinkedGeneration = 1;
        tiers.Observe(Named((First, FirstCode), (Second, SecondCode)), Caller, host);
        host.MarkedBits[CallMarks.Bit(First, Caller)] = 3;
        host.CallMarkSnapshots = 3;
        Assert.Equal(1, tiers.Observe(Named((First, FirstCode), (Second, SecondCode)), Caller, host));
        Assert.Empty(tiers.Choose(union, fallbackGeneration: 1, snapshots: 5)[0x40]);
        host.MarkedBits[CallMarks.Bit(Second, Caller)] = 9;
        host.CallMarkSnapshots = 9;
        Assert.Equal(1, tiers.Observe(Named((First, FirstCode), (Second, SecondCode)), Caller, host));
        Assert.Empty(tiers.Choose(union, fallbackGeneration: 1, snapshots: 16)[0x40]);
        Assert.Equal([First, Second], tiers.Choose(union, fallbackGeneration: 1, snapshots: 17)[0x40].Keys.Order());

        // Calls that never settle still reach a choice.
        var third = new ShaderCalleeTiers(new Dictionary<uint, Dictionary<ulong, UnitedCallee>>());
        Assert.Empty(third.Choose(union, fallbackGeneration: -1, snapshots: 0)[0x40]);
        third.LinkedGeneration = 1;
        host.MarkedBits.Clear();
        host.CallMarkSnapshots = 100;
        third.Observe(Named((First, FirstCode), (Second, SecondCode)), Caller, host);
        host.MarkedBits[CallMarks.Bit(First, Caller)] = 103;
        host.CallMarkSnapshots = 103;
        third.Observe(Named((First, FirstCode), (Second, SecondCode)), Caller, host);
        host.MarkedBits[CallMarks.Bit(Second, Caller)] = 165;
        host.CallMarkSnapshots = 165;
        third.Observe(Named((First, FirstCode), (Second, SecondCode)), Caller, host);
        Assert.Equal([First, Second], third.Choose(union, fallbackGeneration: 1, snapshots: 167)[0x40].Keys.Order());
    }
}
