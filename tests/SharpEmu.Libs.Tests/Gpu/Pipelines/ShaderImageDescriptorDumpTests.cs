// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class ShaderImageDescriptorDumpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImageCapture_UsesCleanReadsAndDistinguishesUnresolvedDescriptors(bool unreadableSecond)
    {
        var plan = Extract(Program(
            ScalarLoad(0, 0, 8, 8), Image(8, "ImageLoad", 8),
            ScalarLoad(16, 0, 8, 8, immediateOffset: 32), Image(24, "ImageLoad", 8), EndProgram(32)),
            userDataCount: 2);
        var capture = ShaderImageDescriptorDump.Capture(plan, new ResourceRuntimeInputs
        {
            UserData = [0x1000, 0],
            ReadMemory = (ulong address, out uint word) => throw new InvalidOperationException("Unexpected synchronized read."),
            ReadCleanMemory = (ulong address, out uint word) =>
            {
                word = (uint)((address - 0x1000) / 4 % 8) + 1;
                return address >= 0x1000 && address < (unreadableSecond ? 0x1020ul : 0x1040ul);
            },
        });
        Assert.Equal(2, capture.Accesses.Count);
        Assert.Equal(unreadableSecond ? 1 : 2, capture.ResolvedAccesses);
        Assert.Equal(1, capture.UniqueDescriptorWords);
        Assert.Equal(1, capture.UniqueImageBindings);
        Assert.False(capture.MemoryLimitReached);
        Assert.False(capture.AccessLimitReached);
        if (unreadableSecond) Assert.All(capture.Accesses[1].Words, word => Assert.Null(word));
        else Assert.Equal(capture.Accesses[0].Words, capture.Accesses[1].Words);
    }
}
