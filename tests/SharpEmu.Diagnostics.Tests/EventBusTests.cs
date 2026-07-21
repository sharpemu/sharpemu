// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using Xunit;

using SharpEmu.Diagnostics.Core;
using SharpEmu.Diagnostics.Util;

namespace SharpEmu.Diagnostics.Tests;

public class EventBusTests
{
    [Fact]
    public void Publish_DispatchesToAllPlugins()
    {
        var bus = new EventBus();
        var plugin1 = new TestPlugin("P1");
        var plugin2 = new TestPlugin("P2");
        bus.Register(plugin1);
        bus.Register(plugin2);

        bus.Publish(new TestEvent("test"));

        Assert.Equal(1, plugin1.EventCount);
        Assert.Equal(1, plugin2.EventCount);
    }

    [Fact]
    public void PluginFailure_DoesNotCrashBus()
    {
        var bus = new EventBus();
        bus.Register(new ThrowingPlugin());
        bus.Register(new TestPlugin("safe"));

        // Should not throw
        bus.Publish(new TestEvent("test"));

        Assert.Equal(1, bus.EventCount);
    }

    [Fact]
    public void EventCount_TracksTotal()
    {
        var bus = new EventBus();
        for (int i = 0; i < 100; i++)
            bus.Publish(new TestEvent($"evt{i}"));
        Assert.Equal(100, bus.EventCount);
    }

    private record TestEvent(string Data) : Contracts.IDiagnosticEvent
    {
        public long Timestamp => 0;
        public int Version => 1;
        public string Category => "test";
        public string Type => "unit";
    }

    private class TestPlugin : Contracts.IDiagnosticPlugin
    {
        public int EventCount;
        public Contracts.PluginMetadata Metadata => new() { Name = Name_, Version = "1.0", Description = "", EnvVar = "" };
        private readonly string Name_;
        public TestPlugin(string name) => Name_ = name;
        public void Initialize(Contracts.IDiagnosticContext context) { }
        public void OnEvent(Contracts.IDiagnosticEvent e) => EventCount++;
        public object? Shutdown() => null;
    }

    private class ThrowingPlugin : Contracts.IDiagnosticPlugin
    {
        public Contracts.PluginMetadata Metadata => new() { Name = "throw", Version = "1.0", Description = "", EnvVar = "" };
        public void Initialize(Contracts.IDiagnosticContext context) { }
        public void OnEvent(Contracts.IDiagnosticEvent e) => throw new InvalidOperationException("test");
        public object? Shutdown() => null;
    }
}
