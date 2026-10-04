// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.Libs.Gpu.Pipelines;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// A saved driver cache loads only when its signature line and payload hash match.
public sealed class PipelineCacheSignatureTests
{
    private static readonly byte[] Uuid = Enumerable.Range(0, PipelineCacheSignature.UuidSize).Select(index => (byte)(index * 17)).ToArray();

    private static string Signature(uint driverVersion = 0x00401000, string? buildIdentity = null) =>
        buildIdentity is not null
            ? PipelineCacheSignature.Build(0x1002, 0x73BF, driverVersion, Uuid, buildIdentity)
            : PipelineCacheSignature.Build(0x1002, 0x73BF, driverVersion, Uuid);

    [Fact]
    public void Build_NamesTheBuildAndTheDevice()
    {
        var signature = Signature();

        Assert.StartsWith(
            $"SharpEmuPC2:{PipelineCacheSignature.BuildVersion}:{PipelineCacheSignature.BuildIdentity}:00001002:000073bf:00401000:",
            signature);
        Assert.EndsWith("\n", signature);
        Assert.Contains(Convert.ToHexString(Uuid).ToLowerInvariant(), signature);
    }

    [Fact]
    public void BuildIdentityDoesNotDependOnTheWholeHleLibraryModule()
    {
        Assert.Equal(2, PipelineCacheSignature.BuildIdentity.Split('-').Length);
        Assert.DoesNotContain(
            PipelineCacheSignature.BuildModuleVersionId.ToString("N"),
            PipelineCacheSignature.BuildIdentity,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExactBuildIdentityChangesSignatureAndStorageNamespace()
    {
        var first = Signature(buildIdentity: "11111111111111111111111111111111");
        var second = Signature(buildIdentity: "22222222222222222222222222222222");

        Assert.NotEqual(first, second);
        Assert.NotEqual(
            PipelineCacheSignature.StorageKey(first),
            PipelineCacheSignature.StorageKey(second));
    }

    [Fact]
    public void Pc1FileDoesNotLoadAsPc2()
    {
        var pc1 = $"SharpEmuPC1:{PipelineCacheSignature.BuildVersion}:00001002:000073bf:00401000:{Convert.ToHexString(Uuid).ToLowerInvariant()}\n";
        var file = PipelineCacheSignature.Wrap(pc1, [1, 2, 3, 4]);

        Assert.False(PipelineCacheSignature.TryUnwrap(Signature(), file, out var loaded));
        Assert.Empty(loaded);
    }

    [Fact]
    public void ValidFile_Loads()
    {
        var payload = Encoding.ASCII.GetBytes("driver cache bytes");

        var file = PipelineCacheSignature.Wrap(Signature(), payload);

        Assert.True(PipelineCacheSignature.TryUnwrap(Signature(), file, out var loaded));
        Assert.Equal(payload, loaded);
    }

    [Fact]
    public void SignatureMismatch_Invalidates()
    {
        var file = PipelineCacheSignature.Wrap(Signature(), [1, 2, 3, 4]);

        Assert.False(PipelineCacheSignature.TryUnwrap(Signature(driverVersion: 0x00402000), file, out var loaded));
        Assert.Empty(loaded);
    }

    [Fact]
    public void DeviceCompatibleFile_LoadsAcrossBuildIdentityChanges()
    {
        var oldSignature = Signature(buildIdentity: "11111111111111111111111111111111");
        var newSignature = Signature(buildIdentity: "22222222222222222222222222222222");
        var file = PipelineCacheSignature.Wrap(oldSignature, [1, 2, 3, 4]);

        Assert.True(PipelineCacheSignature.TryUnwrapDeviceCompatible(newSignature, file, out var loaded));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, loaded);
    }

    [Fact]
    public void DeviceCompatibleFile_RejectsDriverUuidAndHashMismatches()
    {
        var oldSignature = Signature(buildIdentity: "11111111111111111111111111111111");
        var differentDriver = Signature(
            driverVersion: 0x00402000,
            buildIdentity: "22222222222222222222222222222222");
        var file = PipelineCacheSignature.Wrap(oldSignature, [1, 2, 3, 4]);

        Assert.False(PipelineCacheSignature.TryUnwrapDeviceCompatible(differentDriver, file, out _));
        file[^1] ^= 0xFF;
        Assert.False(PipelineCacheSignature.TryUnwrapDeviceCompatible(oldSignature, file, out _));
    }

    [Fact]
    public void HashMismatch_Invalidates()
    {
        var file = PipelineCacheSignature.Wrap(Signature(), [1, 2, 3, 4]);
        file[^1] ^= 0xFF;

        Assert.False(PipelineCacheSignature.TryUnwrap(Signature(), file, out _));
    }

    [Fact]
    public void ShortOrEmptyFile_Invalidates()
    {
        Assert.False(PipelineCacheSignature.TryUnwrap(Signature(), [], out _));
        Assert.False(PipelineCacheSignature.TryUnwrap(Signature(), Encoding.ASCII.GetBytes(Signature()), out _));
    }

    [Fact]
    public void EmptyPayload_RoundTrips()
    {
        var file = PipelineCacheSignature.Wrap(Signature(), []);

        Assert.True(PipelineCacheSignature.TryUnwrap(Signature(), file, out var loaded));
        Assert.Empty(loaded);
    }
}
