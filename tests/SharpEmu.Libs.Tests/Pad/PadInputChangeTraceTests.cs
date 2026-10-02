// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

[CollectionDefinition("Pad trace", DisableParallelization = true)]
public sealed class PadTraceCollection;

[Collection("Pad trace")]
public sealed class PadInputChangeTraceTests
{
    private static FieldInfo Field(string name) => typeof(PadInputChangeTrace).GetField(
        name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static int Count(string name) => (int)Field(name).GetValue(null)!;

    private static void Reset()
    {
        Field("ObservedGetHandleResults").GetValue(null)!.GetType().GetMethod("Clear")!
            .Invoke(Field("ObservedGetHandleResults").GetValue(null), null);
        ((IDictionary)Field("LastSnapshots").GetValue(null)!).Clear();
        Field("_loggedChanges").SetValue(null, 0);
        Field("_loggedHandleEvents").SetValue(null, 0);
    }

    [Fact]
    public void HandleLimit_StopsLookupStorageAndOutput()
    {
        if (!(bool)Field("Enabled").GetValue(null)!) return;
        Reset();
        var previous = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetError(output);
            for (var index = 0; index < 64; index++)
                PadInputChangeTrace.RecordHandleResult("scePadGetHandle", 1, 0, 0, index * 4 + 1);
            var boundedOutput = output.ToString();
            for (var index = 64; index < 1024; index++)
            {
                PadInputChangeTrace.RecordHandleResult("scePadGetHandle", 1, 0, 0, index * 4 + 1);
                PadInputChangeTrace.RecordClose(index, 0);
            }
            var lookups = Field("ObservedGetHandleResults").GetValue(null)!;
            Assert.Equal(64, (int)lookups.GetType().GetProperty("Count")!.GetValue(lookups)!);
            Assert.Equal(64, Count("_loggedHandleEvents"));
            Assert.Equal(boundedOutput, output.ToString());
            Assert.Contains("trace limit reached", boundedOutput);
        }
        finally { Console.SetError(previous); Reset(); }
    }

    [Fact]
    public void ReopenedHandles_RecordChangesWithoutDuplicatingEqualState()
    {
        if (!(bool)Field("Enabled").GetValue(null)!) return;
        Reset();
        var previous = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetError(output);
            var input = default(PadState) with { Buttons = 0x4000 };
            foreach (var handle in new[] { 1, 5, 9 })
            {
                PadInputChangeTrace.Record("scePadReadState", handle, input, true);
                PadInputChangeTrace.Record("scePadReadState", handle, input, true);
                Assert.Contains($"handle={handle} ", output.ToString());
            }
            Assert.Equal(3, Count("_loggedChanges"));
            PadInputChangeTrace.Record("scePadReadState", 5, input, false);
            Assert.Equal(4, Count("_loggedChanges"));
            Assert.Contains("write=failed", output.ToString());
        }
        finally { Console.SetError(previous); Reset(); }
    }

    [Fact]
    public void InputLimit_StopsSnapshotStorageAndOutput()
    {
        if (!(bool)Field("Enabled").GetValue(null)!) return;
        Reset();
        var previous = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetError(output);
            for (var index = 0; index < 256; index++)
                PadInputChangeTrace.Record("scePadRead", index * 4 + 1, default, true);
            var boundedOutput = output.ToString();
            for (var index = 256; index < 1024; index++)
                PadInputChangeTrace.Record("scePadRead", index * 4 + 1, default, true);
            Assert.Equal(256, ((IDictionary)Field("LastSnapshots").GetValue(null)!).Count);
            Assert.Equal(256, Count("_loggedChanges"));
            Assert.Equal(boundedOutput, output.ToString());
            Assert.Contains("trace limit reached", boundedOutput);
        }
        finally { Console.SetError(previous); Reset(); }
    }
}
