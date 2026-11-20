// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.FileSystem.Pkg;

// The publicly known RSA keys used by fake (fpkg) packages (modulus and private exponent, as hex).
// Ported from the shadPS4 key tables (src/core/crypto/keys.h).
// TODO: fill in from shadPS4 keys.h (FakeKeyset and PkgDerivedKey3Keyset); left empty until the
// key import is allowed in this environment.
internal static class PkgKeys
{
    public const string FakeModulus = "";
    public const string FakePrivateExponent = "";
    public const string DerivedKey3Modulus = "";
    public const string DerivedKey3PrivateExponent = "";
}
