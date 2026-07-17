// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcSubmissionQueueTests
{
    [Fact]
    public void DcbCompletionStaysBehindEarlierWorkOnTheGraphicsQueue()
    {
        const string queueName = "dcb.graphics";
        const ulong submissionId = 42;
        var executionOrder = new List<string>();
        var queuedActions = new Dictionary<string, Queue<Action>>
        {
            [queueName] = new Queue<Action>(),
            [VulkanGuestQueueIdentity.Default.Name] = new Queue<Action>(),
        };
        queuedActions[queueName].Enqueue(() => executionOrder.Add("release_mem"));

        VulkanGuestQueueIdentity? capturedQueue = null;
        var sequence = AgcExports.EnqueueSubmittedDcbCompletion(
            queueName,
            submissionId,
            () => executionOrder.Add("completion"),
            (action, debugName) =>
            {
                Assert.Equal($"agc submit completion {submissionId}", debugName);
                capturedQueue = GetSubmittingGuestQueue();
                var targetQueue = capturedQueue ?? VulkanGuestQueueIdentity.Default;
                queuedActions[targetQueue.Name].Enqueue(action);
                return 2;
            });

        Assert.Equal(2, sequence);
        Assert.Equal(
            new VulkanGuestQueueIdentity(queueName, submissionId),
            capturedQueue);
        Assert.Null(GetSubmittingGuestQueue());

        while (queuedActions[queueName].TryDequeue(out var action))
        {
            action();
        }

        Assert.Equal(["release_mem", "completion"], executionOrder);
        Assert.Empty(queuedActions[VulkanGuestQueueIdentity.Default.Name]);
    }

    private static VulkanGuestQueueIdentity? GetSubmittingGuestQueue()
    {
        var field = typeof(VulkanVideoPresenter).GetField(
            "_submittingGuestQueue",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(null) is VulkanGuestQueueIdentity queue
            ? queue
            : null;
    }
}
