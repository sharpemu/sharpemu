// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>
/// When guest code faults on an unpatched TLS access the crash dump is supposed to say so. The
/// check matched only the first byte of the instruction, so it never fired for the encoding
/// shipping titles actually emit — LLVM's TLS general-dynamic relaxation pads the sequence with
/// operand-size prefixes, putting the segment override at index 3.
///
/// The consequence was not cosmetic: every such crash in every user-submitted log was reported as
/// an ordinary unmapped-memory read, which is why the underlying patcher defect went unnoticed.
/// </summary>
public sealed class SegmentOverrideDetectionTests
{
    /// <summary>
    /// The exact bytes from God of War Ragnarök, EA UFC 5 and Minecraft — all three crash on a
    /// byte-identical <c>mov rax, fs:[0]</c>.
    /// </summary>
    [Fact]
    public void DetectsTheFsPrefixInTheEncodingRealTitlesEmit()
    {
        byte[] code =
        [
            0x66, 0x66, 0x66, 0x64, 0x48, 0x8B, 0x04, 0x25,
            0x00, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x0D, 0x00,
        ];

        Assert.True(DirectExecutionBackend.TryDetectSegmentOverride(code, out var segment, out var index));
        Assert.Equal("FS", segment);
        Assert.Equal(3, index);
    }

    [Fact]
    public void DetectsAnUnpaddedFsPrefix()
    {
        byte[] code = [0x64, 0x48, 0x8B, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0];

        Assert.True(DirectExecutionBackend.TryDetectSegmentOverride(code, out var segment, out var index));
        Assert.Equal("FS", segment);
        Assert.Equal(0, index);
    }

    [Fact]
    public void DetectsAPaddedGsPrefix()
    {
        byte[] code = [0x66, 0x65, 0x48, 0x8B, 0x04, 0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.True(DirectExecutionBackend.TryDetectSegmentOverride(code, out var segment, out var index));
        Assert.Equal("GS", segment);
        Assert.Equal(1, index);
    }

    /// <summary>
    /// An ordinary instruction must not be misreported as a TLS fault — the dump would then send
    /// the next person chasing a patcher bug that is not there.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x48, 0x8B, 0x04, 0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })] // mov rax,[abs]
    [InlineData(new byte[] { 0xC5, 0xF8, 0x57, 0xC0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })] // vxorps (AVX)
    [InlineData(new byte[] { 0x66, 0x66, 0x66, 0x90, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })] // padded nop
    public void DoesNotReportASegmentOverrideThatIsNotThere(byte[] code)
    {
        Assert.False(DirectExecutionBackend.TryDetectSegmentOverride(code, out var segment, out var index));
        Assert.Equal(string.Empty, segment);
        Assert.Equal(-1, index);
    }

    /// <summary>All-prefix input must not run off the end of the buffer.</summary>
    [Fact]
    public void HandlesAnInstructionWindowThatIsEntirelyPrefixes()
    {
        var code = new byte[16];
        Array.Fill(code, (byte)0x66);

        Assert.False(DirectExecutionBackend.TryDetectSegmentOverride(code, out _, out var index));
        Assert.Equal(-1, index);
    }
}
