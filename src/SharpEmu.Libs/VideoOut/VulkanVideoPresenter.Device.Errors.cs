// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using Silk.NET.Vulkan;

// This partial identifies Vulkan device-loss failures.
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private static void Check(Result result, string operation)
        {
            if (result == Result.ErrorDeviceLost)
            {
                throw new VulkanDeviceLostException(operation);
            }

            if (result != Result.Success)
            {
                throw new InvalidOperationException($"{operation} failed with {result}.");
            }
        }

        // Typed so the frame-boundary catch can recognize device loss without
        // depending on the exact wording of the exception message.
        private sealed class VulkanDeviceLostException(string operation)
            : InvalidOperationException($"{operation} failed with {Result.ErrorDeviceLost}.");

        private bool TryMarkDeviceLost(Exception exception)
        {
            // Prefer the typed signal; fall back to the message for losses that
            // surface through other layers (e.g. Silk.NET bindings).
            if (exception is not VulkanDeviceLostException &&
                !exception.Message.Contains(nameof(Result.ErrorDeviceLost), StringComparison.Ordinal))
            {
                return false;
            }

            _deviceLost = true;
            if (!_deviceLostLogged)
            {
                _deviceLostLogged = true;
                var work = !string.IsNullOrEmpty(_activeGuestWorkLabel)
                    ? $"work={_activeGuestWorkLabel}"
                    : !string.IsNullOrEmpty(_lastGuestWorkLabel)
                        ? $"last_work={_lastGuestWorkLabel}"
                        : "work=<none>";
                var submit = string.IsNullOrEmpty(_lastSubmitDebugName)
                    ? string.Empty
                    : $" last_submit={_lastSubmitDebugName}";
                Console.Error.WriteLine(
                    "[LOADER][ERROR] Vulkan device lost; dropping subsequent guest GPU work. " +
                    $"{work}{submit} {exception.Message}");
            }

            return true;
        }
    }
}
