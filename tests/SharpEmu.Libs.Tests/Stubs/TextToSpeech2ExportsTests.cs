// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Stubs;
using Xunit;

namespace SharpEmu.Libs.Tests.Stubs;

public sealed class TextToSpeech2ExportsTests
{
    public static TheoryData<string, string, string> ExportCases => new()
    {
        { nameof(GameServiceStubs.TextToSpeech2Speak), "sceTextToSpeech2Speak", "8ntsRd07EQA" },
        { nameof(GameServiceStubs.TextToSpeech2Cancel), "sceTextToSpeech2Cancel", "2jiIxUmcsGo" },
        { nameof(GameServiceStubs.TextToSpeech2GetSpeechStatus), "sceTextToSpeech2GetSpeechStatus", "08JSg9p6bgQ" },
    };

    [Theory]
    [MemberData(nameof(ExportCases))]
    public void Export_IsRegisteredForGen5TextToSpeech2(
        string methodName,
        string exportName,
        string nid)
    {
        var method = typeof(GameServiceStubs).GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var export = Assert.Single(method!.GetCustomAttributes<SysAbiExportAttribute>());

        Assert.Equal(exportName, export.ExportName);
        Assert.Equal(nid, export.Nid);
        Assert.Equal("libSceTextToSpeech2", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    [Theory]
    [InlineData(nameof(GameServiceStubs.TextToSpeech2Speak))]
    [InlineData(nameof(GameServiceStubs.TextToSpeech2Cancel))]
    [InlineData(nameof(GameServiceStubs.TextToSpeech2GetSpeechStatus))]
    public void CompatibilityExport_ReturnsSuccessWithoutTouchingGuestMemory(string methodName)
    {
        var memory = new RejectingCpuMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rax] = ulong.MaxValue;
        var method = typeof(GameServiceStubs).GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var result = Assert.IsType<int>(method!.Invoke(null, new object[] { context }));

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(0, memory.ReadAttempts);
        Assert.Equal(0, memory.WriteAttempts);
    }

    private sealed class RejectingCpuMemory : ICpuMemory
    {
        public int ReadAttempts { get; private set; }
        public int WriteAttempts { get; private set; }

        public bool TryRead(ulong address, Span<byte> destination)
        {
            ReadAttempts++;
            return false;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            WriteAttempts++;
            return false;
        }
    }
}
