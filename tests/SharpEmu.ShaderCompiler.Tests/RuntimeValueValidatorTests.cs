// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class RuntimeValueValidatorTests
{
    // The host evaluates runtime descriptor values with ScalarOperationSemantics, so the validator must
    // not reject an operation the evaluator can compute (Silent Hill's compute plans failed that way).
    [Fact]
    public void EveryEvaluableOperationIsAcceptedAsUniform()
    {
        ulong[] operands = [1, 2, 3, 4];
        foreach (var operation in Enum.GetValues<ScalarOperation>())
        {
            bool evaluable;
            try
            {
                evaluable = ScalarOperationSemantics.TryEvaluate(operation, operands, out _);
            }
            catch (Exception)
            {
                evaluable = false;
            }

            if (evaluable)
            {
                Assert.True(RuntimeValueValidator.IsUniformOperation(operation), $"{operation} is evaluable but not uniform.");
            }
        }
    }
}
