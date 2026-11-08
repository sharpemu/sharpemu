// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Xml.Linq;
using SharpEmu.Core;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed partial class GamePatchTests
{
    private static byte[] Read(ICpuMemory memory, ulong address, int size)
    {
        var bytes = new byte[size];
        Assert.True(memory.TryRead(address, bytes));
        return bytes;
    }

    [Theory]
    [InlineData("+1", 0x11)]
    [InlineData("-1", 0x0f)]
    public void PatternOffsetsAndWildcardsResolveWithinTheLoadedImage(string offset, int expectedOffset)
    {
        var patch = WritePatch($"<Line Type=\"mask\" Address=\"aa ? cc ?? ee\" Value=\"1234\" Offset=\"{offset}\"/>");
        Assert.Null(patch.Error);
        var memory = Memory();
        Assert.True(memory.TryWrite(0x8_0000_0010, [0xAA, 0xBB, 0xCC, 0xDD, 0xEE]));
        Assert.True(patch.TryApply(memory, [Segment()], 0x8_0000_0000, out var error), error);
        Assert.Equal(new byte[] { 0x12, 0x34 }, Read(memory, 0x8_0000_0000 + (ulong)expectedOffset, 2));
    }

    [Fact]
    public void PatternVersionIsUniversalOnlyForPatternBasedPatches()
    {
        const string line = "<Line Type=\"mask\" Address=\"aa bb\" Value=\"90\"/>";
        File.WriteAllText(XmlPath, Xml(line, "mask"));
        var patch = Assert.Single(Repository.Load("PPSA00001"));
        Assert.Null(patch.Error);
        Assert.True(patch.MatchesVersion("01.004.000"));
        Assert.True(patch.MatchesVersion("01.007.000"));
        Assert.True(patch.MatchesVersion(null));
        Repository.SetEnabled("PPSA00001", patch, true);
        Assert.Single(Repository.LoadActive("PPSA00001", "01.007.000", _ => Assert.Fail("Pattern version should match.")));

        var exact = patch with { AppVersion = "01.004.000" };
        Assert.False(exact.MatchesVersion("01.007.000"));
        File.WriteAllText(XmlPath, Xml(line + "<Line Type=\"byte\" Address=\"0x10000010\" Value=\"1\"/>", "mask"));
        var unsafePatch = Assert.Single(Repository.Load("PPSA00001"));
        Assert.NotNull(unsafePatch.Error);
        Assert.Empty(unsafePatch.Writes);
        Assert.False(unsafePatch.MatchesVersion("01.007.000"));
    }

    [Theory]
    [InlineData("", "", "mask", "")]
    [InlineData("?? ?", "", "mask", "")]
    [InlineData("aa zz", "", "mask", "")]
    [InlineData("a? bb", "", "mask", "")]
    [InlineData("aa bb", "0x10", "mask", "")]
    [InlineData("aa bb", "9223372036854775808", "mask", "")]
    [InlineData("aa bb", "0", "mask_jump32", "Target=\"cc dd\" Size=\"4\"")]
    [InlineData("aa bb", "0", "mask_jump32", "Target=\"cc dd\" Size=\"257\"")]
    [InlineData("aa bb", "0", "mask_jump32", "Target=\"??\" Size=\"5\"")]
    [InlineData("aa bb", "0", "mask_jump32", "Size=\"5\"")]
    [InlineData("aa bb", "0", "mask", "Target=\"cc dd\"")]
    public void MalformedPatternsAndBranchParametersRejectTheWholePatch(string signature, string offset, string type, string extra)
    {
        var patch = WritePatch($"<Line Type=\"{type}\" Address=\"{signature}\" Value=\"90\" Offset=\"{offset}\" {extra}/>");
        Assert.NotNull(patch.Error);
        Assert.Empty(patch.Writes);
    }

    [Fact]
    public void OversizedPatternsAreRejected()
    {
        var patch = WritePatch($"<Line Type=\"mask\" Address=\"{string.Join(' ', Enumerable.Repeat("aa", 256))}\" Value=\"90\"/>");
        Assert.NotNull(patch.Error);
    }

    [Theory]
    [InlineData(65534)]
    [InlineData(65536)]
    public void PatternScanningIncludesChunkBoundariesAndTheFinalPossibleMatch(int position)
    {
        var bytes = new byte[position + 4];
        new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }.CopyTo(bytes, position);
        var memory = new VirtualMemory();
        memory.Map(0x8_0000_0000, (ulong)bytes.Length, 0, bytes, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var patch = WritePatch("<Line Type=\"mask\" Address=\"aa bb cc dd\" Value=\"90\"/>");
        Assert.True(patch.TryApply(memory, [Segment(size: (ulong)bytes.Length)], 0x8_0000_0000, out var error), error);
        Assert.Equal(0x90, Read(memory, 0x8_0000_0000 + (ulong)position, 1)[0]);
    }

    [Fact]
    public void LaterPatternLinesSeeEarlierStagedReplacements()
    {
        var patch = WritePatch("""
            <Line Type="mask" Address="aa bb" Value="cccc"/>
            <Line Type="mask" Address="aa bb" Value="dddd"/>
            <Line Type="mask" Address="cc cc" Value="eeee"/>
            """);
        var memory = Memory();
        Assert.True(memory.TryWrite(0x8_0000_0010, [0xAA, 0xBB]));
        Assert.True(memory.TryWrite(0x8_0000_0020, [0xAA, 0xBB]));
        Assert.True(patch.TryApply(memory, [Segment()], 0x8_0000_0000, out var error), error);
        Assert.Equal(new byte[] { 0xEE, 0xEE }, Read(memory, 0x8_0000_0010, 2));
        Assert.Equal(new byte[] { 0xDD, 0xDD }, Read(memory, 0x8_0000_0020, 2));
    }

    [Fact]
    public void MissingPatternsNeverApplyEarlierWrites()
    {
        var patch = WritePatch("""
            <Line Type="bytes" Address="0x10000010" Value="aa"/>
            <Line Type="mask" Address="cc dd" Value="90"/>
            """);
        var memory = new WriteCountingMemory(Memory());
        Assert.False(patch.TryApply(memory, [Segment()], 0x8_0000_0000, out var error));
        Assert.Contains("not found", error);
        Assert.Equal(0, memory.WriteCount);
        Assert.Equal(new byte[0x100], Read(memory, 0x8_0000_0000, 0x100));
    }

    [Theory]
    [InlineData("-17")]
    [InlineData("240")]
    [InlineData("9223372036854775807")]
    [InlineData("-9223372036854775808")]
    public void PatternOffsetsCannotWriteOutsideTheExecutable(string offset)
    {
        var patch = WritePatch($"<Line Type=\"mask\" Address=\"aa bb\" Value=\"90\" Offset=\"{offset}\"/>");
        var memory = Memory();
        Assert.True(memory.TryWrite(0x8_0000_0010, [0xAA, 0xBB]));
        var original = Read(memory, 0x8_0000_0000, 0x100);
        Assert.False(patch.TryApply(memory, [Segment()], 0x8_0000_0000, out _));
        Assert.Equal(original, Read(memory, 0x8_0000_0000, 0x100));
    }

    [Fact]
    public void PatternsNeverMatchAnUnrelatedMappedRegion()
    {
        var memory = Memory();
        memory.Map(0x9_0000_0000, 4, 0, [0xAA, 0xBB, 0xCC, 0xDD], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var patch = WritePatch("<Line Type=\"mask\" Address=\"aa bb cc dd\" Value=\"90\"/>");
        Assert.False(patch.TryApply(memory, [Segment()], 0x8_0000_0000, out _));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, Read(memory, 0x9_0000_0000, 4));
    }

    [Fact]
    public void PatternsWithLeadingWildcardsCanMatchTheEndOfASegment()
    {
        var memory = Memory();
        Assert.True(memory.TryWrite(0x8_0000_00fe, [0xAA, 0xBB]));
        var patch = WritePatch("<Line Type=\"mask\" Address=\"?? ? aa bb\" Value=\"90\" Offset=\"+2\"/>");
        Assert.True(patch.TryApply(memory, [Segment()], 0x8_0000_0000, out var error), error);
        Assert.Equal(0x90, Read(memory, 0x8_0000_00fe, 1)[0]);
    }

    [Fact]
    public void PatternsCannotSpanSeparateLoadableSegments()
    {
        var memory = new VirtualMemory();
        memory.Map(0x8_0000_0000, 1, 0, [0xAA], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        memory.Map(0x8_0000_0001, 1, 0, [0xBB], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var patch = WritePatch("<Line Type=\"mask\" Address=\"aa bb\" Value=\"90\"/>");
        Assert.False(patch.TryApply(memory, [Segment(size: 1), Segment(1, 1)], 0x8_0000_0000, out _));
        Assert.Equal(0xAA, Read(memory, 0x8_0000_0000, 1)[0]);
        Assert.Equal(0xBB, Read(memory, 0x8_0000_0001, 1)[0]);
    }

    [Fact]
    public void BranchPatchesRequireExecutableSegmentsButOrdinaryMasksCanPatchData()
    {
        var memory = JumpMemory();
        var original = Read(memory, 0x8_0000_0000, 0x100);
        var data = Segment(flags: ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var branch = WritePatch("<Line Type=\"mask_jump32\" Address=\"aa bb cc dd ee\" Target=\"cc cc cc cc cc cc cc\" Size=\"8\" Value=\"31c0\"/>");
        Assert.False(branch.TryApply(memory, [data], 0x8_0000_0000, out _));
        Assert.Equal(original, Read(memory, 0x8_0000_0000, 0x100));
        var mask = WritePatch("<Line Type=\"mask\" Address=\"aa bb cc dd ee\" Value=\"90\"/>");
        Assert.True(mask.TryApply(memory, [data], 0x8_0000_0000, out var error), error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-2)]
    public void MaskJumpWritesThePayloadAndBothRelativeBranches(int offset)
    {
        var patch = WritePatch($"<Line Type=\"mask_jump32\" Address=\"aa bb cc dd ee\" Target=\"cc cc cc cc cc cc cc\" Size=\"8\" Offset=\"{offset}\" Value=\"31c0\"/>");
        var memory = JumpMemory();
        Assert.Null(patch.Error);
        Assert.True(patch.TryApply(memory, [Segment()], 0x8_0000_0000, out var error), error);
        var sourceAddress = (ulong)((long)0x8_0000_0010 + offset);
        var source = Read(memory, sourceAddress, 8);
        Assert.Equal(0xE9, source[0]);
        Assert.Equal(0x8_0000_0060UL, (ulong)((long)(sourceAddress + 5) + BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(1))));
        Assert.Equal(new byte[] { 0x90, 0x90, 0x90 }, source[5..]);
        var cave = Read(memory, 0x8_0000_0060, 7);
        Assert.Equal(new byte[] { 0x31, 0xC0, 0xE9 }, cave[..3]);
        Assert.Equal(sourceAddress + 8, (ulong)((long)0x8_0000_0067 + BinaryPrimitives.ReadInt32LittleEndian(cave.AsSpan(3))));
    }

    [Theory]
    [InlineData("aa bb cc dd ee", "aa bb cc dd ee", "+0")]
    [InlineData("aa bb cc dd ee", "11 22 33", "+0")]
    [InlineData("aa bb cc dd ee", "cc cc cc cc cc cc cc", "+240")]
    public void InvalidJumpResolutionLeavesTheEntireImageUnchanged(string source, string target, string offset)
    {
        var patch = WritePatch($"<Line Type=\"mask_jump32\" Address=\"{source}\" Target=\"{target}\" Size=\"8\" Offset=\"{offset}\" Value=\"31c0\"/>");
        var memory = JumpMemory();
        var original = Read(memory, 0x8_0000_0000, 0x100);
        Assert.False(patch.TryApply(memory, [Segment()], 0x8_0000_0000, out _));
        Assert.Equal(original, Read(memory, 0x8_0000_0000, 0x100));
    }

    [Fact]
    public void FailedPayloadWriteRestoresBothTheJumpSiteAndCodeCave()
    {
        var underlying = JumpMemory();
        var original = Read(underlying, 0x8_0000_0000, 0x100);
        var memory = new FailingMemory(underlying);
        var patch = WritePatch("<Line Type=\"mask_jump32\" Address=\"aa bb cc dd ee\" Target=\"cc cc cc cc cc cc cc\" Size=\"8\" Value=\"31c0\"/>");
        Assert.False(patch.TryApply(memory, [Segment()], 0x8_0000_0000, out var error));
        Assert.Contains("restored", error);
        Assert.Equal(original, Read(underlying, 0x8_0000_0000, 0x100));
    }

    [Fact]
    public void RelativeJumpsCannotTruncateADisplacementLargerThanTwoGiB()
    {
        var memory = JumpMemory();
        memory.Map(0x9_0000_0000, 0x100, 0, new byte[0x100], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute);
        Assert.True(memory.TryWrite(0x9_0000_0000, [0x99, 0x88, 0x77]));
        var patch = WritePatch("<Line Type=\"mask_jump32\" Address=\"aa bb cc dd ee\" Target=\"99 88 77\" Size=\"8\" Value=\"31c0\"/>");
        var original = Read(memory, 0x8_0000_0000, 0x100);
        Assert.False(patch.TryApply(memory, [Segment(), Segment(0x1_0000_0000)], 0x8_0000_0000, out var error));
        Assert.Contains("32-bit", error);
        Assert.Equal(original, Read(memory, 0x8_0000_0000, 0x100));
    }

    private sealed class WriteCountingMemory(ICpuMemory memory) : ICpuMemory
    {
        public int WriteCount { get; private set; }
        public bool TryRead(ulong address, Span<byte> destination) => memory.TryRead(address, destination);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            WriteCount++;
            return memory.TryWrite(address, source);
        }
    }

    private static VirtualMemory JumpMemory()
    {
        var memory = Memory();
        Assert.True(memory.TryWrite(0x8_0000_0010, [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x90, 0x90, 0x90]));
        Assert.True(memory.TryWrite(0x8_0000_0060, [0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC]));
        return memory;
    }
}
