// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Pad;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class HostWindowInputTests
{
    [Theory]
    [InlineData(short.MinValue, 0)]
    [InlineData(0, 128)]
    [InlineData(short.MaxValue, 255)]
    public void ToStickByteMapsFullSdlRange(short value, int expected)
    {
        Assert.Equal((byte)expected, HostWindowInput.ToStickByte(value));
    }

    [Fact]
    public void ConnectPublishesWindowKeyboardState()
    {
        HostWindowInput.Connect();
        try
        {
            HostWindowInput.SetKey(0x57, true);

            var source = Assert.IsAssignableFrom<IHostWindowInputSource>(HostWindowInputSource.Current);
            Assert.True(source.HasKeyboardFocus);
            Assert.True(source.IsKeyDown(0x57));
        }
        finally
        {
            HostWindowInput.Disconnect();
        }

        Assert.Null(HostWindowInputSource.Current);
    }

    [Fact]
    public void ConnectedGamepadStateIsExposedByWindowSource()
    {
        var expected = new HostGamepadState(
            true,
            HostGamepadButtons.Cross | HostGamepadButtons.Options,
            64,
            128,
            192,
            128,
            0,
            255);
        HostWindowInput.Connect();
        try
        {
            HostWindowInput.SetGamepad("SDL test pad", expected);
            Span<HostGamepadState> states = stackalloc HostGamepadState[1];

            var source = Assert.IsAssignableFrom<IHostWindowInputSource>(HostWindowInputSource.Current);
            Assert.Equal(1, source.GetGamepadStates(states));
            Assert.Equal(expected, states[0]);
            Assert.Equal("SDL test pad", source.DescribeConnectedGamepad());
        }
        finally
        {
            HostWindowInput.Disconnect();
        }
    }

    [Fact]
    public void ControllerOutputIsForwardedToSdlOwner()
    {
        var output = new RecordingGamepadOutput();
        var effect = new HostAdaptiveTriggerEffect(
            0x21, 0x04, 0, 0x07, 0, 0, 0, 0, 0, 0, 0, 255);
        HostWindowInput.Connect(output);
        try
        {
            var source = Assert.IsAssignableFrom<IHostWindowInputSource>(HostWindowInputSource.Current);
            source.SetRumble(10, 20);
            source.SetTriggerRumble(30, 40);
            source.SetAdaptiveTriggerEffect(effect, null);
            source.SetLightbar(50, 60, 70);
            source.ResetLightbar();

            Assert.Equal((10, 20), output.Rumble);
            Assert.Equal(((byte?)30, (byte?)40), output.TriggerRumble);
            Assert.Equal(effect, output.LeftTriggerEffect);
            Assert.Null(output.RightTriggerEffect);
            Assert.Equal((50, 60, 70), output.Lightbar);
            Assert.True(output.LightbarReset);
        }
        finally
        {
            HostWindowInput.Disconnect();
        }
    }

    [Fact]
    public void CommonHostInputUsesPublishedWindowSource()
    {
        var output = new RecordingGamepadOutput();
        var expected = new HostGamepadState(
            true,
            HostGamepadButtons.Cross,
            32,
            64,
            96,
            128,
            160,
            192);
        var input = new WindowHostInput();
        HostWindowInput.Connect(output);
        try
        {
            HostWindowInput.SetKey(0x57, true);
            HostWindowInput.SetGamepad("SDL test pad", expected);
            Span<HostGamepadState> states = stackalloc HostGamepadState[1];

            Assert.Equal(1, input.GetGamepadStates(states));
            Assert.Equal(expected, states[0]);
            Assert.Equal("SDL test pad", input.DescribeConnectedGamepad());
            Assert.True(input.IsHostWindowFocused());
            Assert.True(input.IsKeyDown(0x57));

            input.SetRumble(10, 20);
            input.SetLightbar(30, 40, 50);
            Assert.Equal((10, 20), output.Rumble);
            Assert.Equal((30, 40, 50), output.Lightbar);
        }
        finally
        {
            HostWindowInput.Disconnect();
        }
    }

    private sealed class RecordingGamepadOutput : IHostGamepadOutput
    {
        public (byte Large, byte Small) Rumble { get; private set; }

        public (byte? Left, byte? Right) TriggerRumble { get; private set; }

        public HostAdaptiveTriggerEffect? LeftTriggerEffect { get; private set; }

        public HostAdaptiveTriggerEffect? RightTriggerEffect { get; private set; }

        public (byte Red, byte Green, byte Blue) Lightbar { get; private set; }

        public bool LightbarReset { get; private set; }

        public void SetRumble(byte largeMotor, byte smallMotor) =>
            Rumble = (largeMotor, smallMotor);

        public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger) =>
            TriggerRumble = (leftTrigger, rightTrigger);

        public void SetAdaptiveTriggerEffect(
            HostAdaptiveTriggerEffect? leftTrigger,
            HostAdaptiveTriggerEffect? rightTrigger)
        {
            LeftTriggerEffect = leftTrigger;
            RightTriggerEffect = rightTrigger;
        }

        public void SetLightbar(byte red, byte green, byte blue) =>
            Lightbar = (red, green, blue);

        public void ResetLightbar() => LightbarReset = true;
    }
}
