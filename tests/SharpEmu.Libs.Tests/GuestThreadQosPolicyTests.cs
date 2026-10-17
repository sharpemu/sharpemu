// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestThreadQosPolicyTests
{
    private const string Experiment =
        "BPE JobWorkerThread CPU0=UserInteractive;BPE JobWorkerThread CPU9=UserInteractive;" +
        "BPE JobWorkerThread CPU11=UserInteractive;BPE JobWorkerThread *=Utility";

    [Theory]
    [InlineData("BPE JobWorkerThread CPU0", HostThreadQos.UserInteractive)]
    [InlineData("BPE JobWorkerThread CPU9", HostThreadQos.UserInteractive)]
    [InlineData("BPE JobWorkerThread CPU10", HostThreadQos.Utility)]
    [InlineData("BPE JobWorkerThread CPU1", HostThreadQos.Utility)]
    public void FirstMatchingRuleWins(string name, HostThreadQos expected)
    {
        Assert.True(GuestThreadQosPolicy.TryParseRules(Experiment, out var rules, out var error), error);
        Assert.True(GuestThreadQosPolicy.TryResolve(rules, name, out var qos));
        Assert.Equal(expected, qos);
    }

    [Fact]
    public void UnmatchedNameResolvesNothing()
    {
        Assert.True(GuestThreadQosPolicy.TryParseRules(Experiment, out var rules, out _));
        Assert.False(GuestThreadQosPolicy.TryResolve(rules, "GameLoop", out _));
    }

    [Theory]
    [InlineData("*", "anything", true)]
    [InlineData("a*c", "abbbc", true)]
    [InlineData("a*c", "abbbd", false)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("abc", "ABC", false)]
    [InlineData("*CPU?", "BPE CPU7", true)]
    [InlineData("*CPU?", "BPE CPU12", false)]
    [InlineData("**x", "x", true)]
    [InlineData("", "", true)]
    [InlineData("", "a", false)]
    public void GlobMatchesCaseSensitively(string pattern, string text, bool expected)
    {
        Assert.Equal(expected, GuestThreadQosPolicy.GlobMatch(pattern, text));
    }

    [Fact]
    public void InvalidClassNameIsRejectedWithClearError()
    {
        Assert.False(GuestThreadQosPolicy.TryParseRules("Foo*=Turbo", out _, out var error));
        Assert.Contains("unknown class 'Turbo'", error);
        Assert.Contains("Utility", error);
    }

    [Fact]
    public void RuleWithoutSeparatorIsRejected()
    {
        Assert.False(GuestThreadQosPolicy.TryParseRules("Foo*", out _, out var error));
        Assert.Contains("PATTERN=CLASS", error);
    }

    [Fact]
    public void QosThreadRunsBodyAtRequestedClassAndKeepsName()
    {
        // Utility lowers the thread, which every host allows without a privilege.
        var atClass = false;
        string? name = null;
        var thread = HostQosThread.TryStart("qos-test", HostThreadQos.Utility, 1024 * 1024, () =>
        {
            atClass = HostThreadQualityOfService.IsCurrentThreadAt(HostThreadQos.Utility);
            name = Thread.CurrentThread.Name;
        }, out var errno);

        Assert.NotNull(thread);
        Assert.True(thread!.Join(5000));
        Assert.True(atClass);
        Assert.Equal("qos-test", name);
        Assert.Equal(0, errno);
    }
}
