// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class MotionOrientationTests
{
    private const ulong StepNanoseconds = 10_000_000;

    [Fact]
    public void AccumulatesTheGyroRotation()
    {
        var motion = new MotionOrientation();
        Turn(motion, new Vector3(0, MathF.PI / 2, 0), 101, 0);
        AssertSameRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), motion.Orientation);
    }

    [Fact]
    public void ResetReturnsToIdentity()
    {
        var motion = new MotionOrientation();
        Turn(motion, new Vector3(1, 0, 0), 20, 0);
        motion.Reset();
        AssertSameRotation(Quaternion.Identity, motion.Orientation);
    }

    [Fact]
    public void AGapBetweenReadingsIsNotIntegrated()
    {
        var motion = new MotionOrientation();
        motion.ApplyAngularVelocity(new Vector3(1, 0, 0), 1_000_000_000);
        motion.ApplyAngularVelocity(new Vector3(1, 0, 0), 2_000_000_000);
        AssertSameRotation(Quaternion.Identity, motion.Orientation);
    }

    [Theory]
    [InlineData(false, 0.25f)]
    [InlineData(true, 0f)]
    public void TiltCorrectionTurnsPitchDriftBackToGravity(bool correction, float expectedAngle)
    {
        var motion = new MotionOrientation();
        motion.ApplyAcceleration(new Vector3(0, 1, 0));
        var timestamp = Turn(motion, new Vector3(0.5f, 0, 0), 51, 0);
        Assert.Equal(0.25f, Angle(motion.Orientation), 3);

        motion.TiltCorrection = correction;
        Turn(motion, Vector3.Zero, 500, timestamp);
        Assert.Equal(expectedAngle, Angle(motion.Orientation), 2);
    }

    [Fact]
    public void TiltCorrectionLeavesHeadingDrift()
    {
        var motion = new MotionOrientation { TiltCorrection = true };
        motion.ApplyAcceleration(new Vector3(0, 1, 0));
        var timestamp = Turn(motion, new Vector3(0, 0.5f, 0), 51, 0);
        Turn(motion, Vector3.Zero, 500, timestamp);
        Assert.Equal(0.25f, Angle(motion.Orientation), 3);
    }

    private static ulong Turn(MotionOrientation motion, Vector3 rate, int readings, ulong timestamp)
    {
        for (var reading = 0; reading < readings; reading++)
        {
            timestamp += StepNanoseconds;
            motion.ApplyAngularVelocity(rate, timestamp);
        }

        return timestamp;
    }

    private static float Angle(Quaternion orientation) => 2 * MathF.Acos(MathF.Min(1, MathF.Abs(orientation.W)));

    private static void AssertSameRotation(Quaternion expected, Quaternion actual) =>
        Assert.True(MathF.Abs(Quaternion.Dot(expected, actual)) > 0.99999f, $"expected {expected}, got {actual}");
}
