// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// The pad's orientation: the gyro's rotation accumulated since the controller connected or the game last reset it,
/// in the pad's own axes. With tilt correction on, gravity pulls pitch and roll back toward the measured down
/// direction; heading has no such reference and keeps the gyro's drift.
/// </summary>
internal sealed class MotionOrientation
{
    // Readings further apart than this are a gap in the reports, not one long rotation.
    private const ulong MaximumStepNanoseconds = 100_000_000;

    private Quaternion _orientation = Quaternion.Identity;
    private Vector3 _gravity;
    private Vector3 _up;
    private bool _upKnown;
    private ulong _lastGyroTimestamp;

    public bool TiltCorrection { get; set; }

    public Quaternion Orientation => _orientation;

    public void Reset()
    {
        _orientation = Quaternion.Identity;
        _upKnown = false;
        _lastGyroTimestamp = 0;
    }

    // An accelerometer reading in G. The first one near 1 G after a reset fixes the up direction tilt correction
    // returns to.
    public void ApplyAcceleration(Vector3 gravity)
    {
        _gravity = gravity;
        var magnitude = gravity.Length();
        if (!_upKnown && magnitude is > 0.9f and < 1.1f)
        {
            _up = Vector3.Transform(gravity / magnitude, _orientation);
            _upKnown = true;
        }
    }

    // A gyro reading in radians per second, rotating the orientation over the time since the previous reading.
    public void ApplyAngularVelocity(Vector3 rate, ulong timestampNanoseconds)
    {
        var last = _lastGyroTimestamp;
        _lastGyroTimestamp = timestampNanoseconds;
        if (last == 0 || timestampNanoseconds <= last || timestampNanoseconds - last > MaximumStepNanoseconds)
        {
            return;
        }

        var magnitude = _gravity.Length();
        if (TiltCorrection && _upKnown && magnitude is > 0.8f and < 1.2f)
        {
            // Turn toward the measured gravity from where the orientation expects it, both in the pad's axes. Readings
            // far from 1 G carry the pad's own acceleration and are left out.
            var expected = Vector3.Transform(_up, Quaternion.Conjugate(_orientation));
            rate += Vector3.Cross(_gravity / magnitude, expected);
        }

        var angle = rate.Length() * ((timestampNanoseconds - last) * 1e-9f);
        if (angle > 0)
        {
            _orientation = Quaternion.Normalize(_orientation * Quaternion.CreateFromAxisAngle(Vector3.Normalize(rate), angle));
        }
    }
}
