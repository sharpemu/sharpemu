// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class ShaderCallUnionStoreTests
{
    private const ulong Caller = 0xC0FFEE, FunctionAddress = 0x2000, OtherAddress = 0x3000;

    // A recorded library is the one a session links first when its callees, site by site, are the ones a session kept,
    // as the next session's store reads them back.
    [Fact]
    public void ARecordedLibraryIsTheKeptOneOnlyWhenItsSiteCalleesMatch()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sharpemu-callees-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "callees.bin");
        try
        {
            new ShaderCallUnionStore(path).Save(ShaderStage.Compute, Caller,
                new Dictionary<uint, UnitedCallee[]> { [0] = [Callee(FunctionAddress)], [8] = [] });
            var store = new ShaderCallUnionStore(path);

            Assert.True(store.KeepsCalleesOf(ShaderStage.Compute, Caller, Library((0, FunctionAddress))));
            Assert.False(store.KeepsCalleesOf(ShaderStage.Compute, Caller, Library((0, OtherAddress))));
            Assert.False(store.KeepsCalleesOf(ShaderStage.Compute, Caller, Library((0, FunctionAddress), (8, FunctionAddress))));
            Assert.False(store.KeepsCalleesOf(ShaderStage.Compute, Caller + 1, Library((0, FunctionAddress))));
            Assert.False(store.KeepsCalleesOf(ShaderStage.Pixel, Caller, Library((0, FunctionAddress))));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static UnitedCallee Callee(ulong address)
    {
        uint[] words = [0xBF800000, 0xBE80200E];
        var function = ShaderFunctionReader.Read(address, (ulong wordAddress, out uint word) =>
        {
            var offset = wordAddress - address;
            word = offset < 8 ? words[(int)(offset / 4)] : 0;
            return offset < 8;
        });
        Assert.True(function.Complete, function.Error);
        return new UnitedCallee(new ShaderCallTarget(address, 0, function.Program), SHA256.HashData(function.Bytes), function.Runs);
    }

    private static LinkedShaderPrewarm Library(params (uint Pc, ulong Target)[] calls)
    {
        var functions = calls.Select(static call => call.Target).Distinct()
            .Select(static address => new LinkedPrewarmFunction(address, [new CodeRange(address, new byte[8])])).ToArray();
        var sites = calls.GroupBy(static call => call.Pc).Select(static site => new LinkedPrewarmSite(site.Key, 14, 16, 14,
            Caller + site.Key + 4, [.. site.Select(static call => new LinkedPrewarmTarget(call.Target, 0))])).ToArray();
        return new LinkedShaderPrewarm(true, false, false, functions, sites);
    }
}
