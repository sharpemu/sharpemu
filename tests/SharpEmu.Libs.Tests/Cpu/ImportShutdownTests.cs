// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class ImportShutdownTests
{
    [Theory]
    [InlineData("ordinary-import", false, false)]
    [InlineData("ordinary-import", true, false)]
    [InlineData("BNowx2l588E", true, false)] // Trivial leaf: process counter frequency.
    [InlineData("Q3VBxCXhUHs", true, false)] // Hot memory leaf: memcpy.
    [InlineData("BNowx2l588E", true, true)]
    public void Shutdown_EndsTheSliceWithoutCallingHle(string nid, bool leaf, bool alreadyExiting)
    {
        using var fixture = new ImportFixture(nid, leaf, shutdown: true, alreadyExiting);

        Assert.Equal(1UL, fixture.Dispatch());
        Assert.Equal(0, fixture.CallCount);
        Assert.Equal(1UL, fixture.Context[CpuRegister.Rax]);
        Assert.Equal(ImportFixture.HostExit, fixture.ReadImportReturn());
        Assert.True(fixture.Context.TryReadUInt64(ImportFixture.ReturnSlot, out var slot));
        Assert.Equal(ImportFixture.HostExit, slot);
        Assert.True(Assert.IsType<bool>(ImportFixture.Field("_activeForcedGuestExit").GetValue(null)));
        Assert.True(fixture.Context.TryReadUInt64(ImportFixture.Destination, out var destination));
        Assert.Equal(0UL, destination); // No copy may run after service teardown starts.
    }

    [Theory]
    [InlineData("ordinary-import", true)]
    [InlineData("BNowx2l588E", true)]
    public void Running_StillCallsTheExport(string nid, bool leaf)
    {
        using var fixture = new ImportFixture(nid, leaf, shutdown: false, alreadyExiting: false);

        Assert.Equal(0UL, fixture.Dispatch());
        Assert.Equal(1, fixture.CallCount);
        Assert.Equal(ImportFixture.GuestReturn, fixture.ReadImportReturn());
        Assert.True(fixture.Context.TryReadUInt64(ImportFixture.ReturnSlot, out var slot));
        Assert.Equal(ImportFixture.GuestReturn, slot);
    }

    private sealed class ImportFixture : IDisposable
    {
        public const ulong ReturnSlot = 0x10000;
        public const ulong Destination = 0x10020;
        public const ulong HostExit = 0x20000;
        public const ulong GuestReturn = 0x30000;
        private readonly DirectExecutionBackend _backend;
        private readonly Dictionary<FieldInfo, object?> _previous = new();
        private readonly nint _allocation;
        private readonly nint _pack;
        public CpuContext Context { get; }
        public int CallCount { get; private set; }

        public ImportFixture(string nid, bool leaf, bool shutdown, bool alreadyExiting)
        {
            _backend = (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(typeof(DirectExecutionBackend));
            Context = new CpuContext(new FakeCpuMemory(ReturnSlot, 0x1000), Generation.Gen5);
            Assert.True(Context.TryWriteUInt64(ReturnSlot, GuestReturn));
            Assert.True(Context.TryWriteUInt64(Destination + 8, 0xAABBCCDDUL));
            _allocation = Marshal.AllocHGlobal(1024);
            Marshal.Copy(new byte[1024], 0, _allocation, 1024);
            _pack = _allocation + 512; // Volatile register saves precede the argument pack.
            Marshal.WriteInt64(_pack, unchecked((long)Destination));
            Marshal.WriteInt64(_pack + 8, unchecked((long)(Destination + 8)));
            Marshal.WriteInt64(_pack + 16, 8);
            Marshal.WriteInt64(_pack + 96, unchecked((long)GuestReturn));
            foreach (var name in new[] { "_activeExecutionBackend", "_activeCpuContext", "_activeEntryReturnSentinelRip",
                         "_activeGuestReturnSlotAddress", "_activeForcedGuestExit", "_activeGuestThreadState",
                         "_importCounterOwner", "_nextImportDispatchIndex", "_importDispatchBlockEnd" })
            {
                var field = Field(name);
                _previous.Add(field, field.GetValue(null));
            }
            Field("_activeExecutionBackend").SetValue(null, _backend);
            Field("_activeCpuContext").SetValue(null, Context);
            Field("_activeEntryReturnSentinelRip").SetValue(null, HostExit);
            Field("_activeGuestReturnSlotAddress").SetValue(null, ReturnSlot);
            Field("_activeForcedGuestExit").SetValue(null, alreadyExiting);
            Field("_activeGuestThreadState").SetValue(null, null);
            typeof(DirectExecutionBackend).GetField("_forcedGuestExit", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_backend, shutdown);
            var export = new ExportedFunction("test", nid, "test", Generation.Gen5, _ => { CallCount++; return 0; });
            var entryType = typeof(DirectExecutionBackend).GetNestedType("ImportStubEntry", BindingFlags.NonPublic)!;
            var entry = Activator.CreateInstance(entryType, [0UL, nid, export, leaf, true, false, false, 1UL]);
            var entries = Array.CreateInstance(entryType, 1);
            entries.SetValue(entry, 0);
            typeof(DirectExecutionBackend).GetField("_importEntries", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_backend, entries);
        }

        public ulong Dispatch() => Assert.IsType<ulong>(typeof(DirectExecutionBackend).GetMethod("DispatchImport",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_backend, [0, _pack]));

        public ulong ReadImportReturn() => unchecked((ulong)Marshal.ReadInt64(_pack + 96));

        public static FieldInfo Field(string name) => typeof(DirectExecutionBackend).GetField(name,
            BindingFlags.Static | BindingFlags.NonPublic)!;

        public void Dispose()
        {
            foreach (var (field, value) in _previous)
                field.SetValue(null, value);
            Marshal.FreeHGlobal(_allocation);
        }
    }
}
