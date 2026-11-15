// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class RecordingHost
    {
        private static readonly bool DrawReplayCapture = Environment.GetEnvironmentVariable("SHARPEMU_DRAW_REPLAY") != "0";
        private readonly uint[] _replayPushData = new uint[PushData.DwordCount];
        private bool _replayCaptured;
        private bool _replaySealed;
        private bool _replayHasPush;
        private PipelineLayout _replayLayout;
        private ShaderStageFlags _replayPushStages;
        private BindingLayout? _replayVertexLayout;
        private uint _replayVertexBase;
        private BindingLayout? _replayPixelLayout;
        private uint _replayPixelBase;
        private RecordingBuffer? _replayRecording;
        private long _replayGeneration;
        private long _replayEpoch;
        private SliceSegment? _replaySegment;
        private DrawScopeToken _replayScope;
        private long _replayOwners;
        private static readonly string[] ReplayHostMissNames = ["unsealed", "recording", "generation", "epoch", "segment", "scope", "owners", "layout", "patch"];
        private static readonly long[] _replayHostMisses = new long[ReplayHostMissNames.Length];

        internal static string TakeReplayHostMisses()
        {
            var parts = new List<string>();
            for (var index = 0; index < ReplayHostMissNames.Length; index++)
            {
                var count = Interlocked.Exchange(ref _replayHostMisses[index], 0);
                if (count != 0)
                {
                    parts.Add(FormattableString.Invariant($"{ReplayHostMissNames[index]}:{count}"));
                }
            }

            return parts.Count == 0 ? "none" : string.Join(",", parts);
        }

        private static bool ReplayHostMiss(int reason)
        {
            Interlocked.Increment(ref _replayHostMisses[reason]);
            return false;
        }

        long IRenderHost.CurrentItemSequence => _worker ? _itemSequence : _presenter._commandThread is { } thread ? thread.Executed + 1 : 0;

        private void CaptureDrawReplay(PipelineBindPoint bindPoint, RenderPipelineEntry entry, ReadOnlySpan<IPreparedBindings> stages, uint* pushData,
            bool hasPushData, ShaderStageFlags pushStages)
        {
            _replayCaptured = false;
            _replaySealed = false;
            if (!DrawReplayCapture || bindPoint != PipelineBindPoint.Graphics || entry.UsesBindlessHeap || stages.Length is < 1 or > 2 ||
                _preparation?.StencilStorageImages is not null)
            {
                return;
            }

            _replayVertexLayout = null;
            _replayPixelLayout = null;
            foreach (var prepared in stages)
            {
                var stage = (PreparedStageBindings)prepared;
                if (stage.Descriptors.GlobalDataShare.Buffer.Handle != 0)
                {
                    return;
                }

                foreach (var texture in stage.Textures)
                {
                    if (texture.IsHostMovie)
                    {
                        return;
                    }
                }

                if (StageOf(stage.Program) == ShaderStage.Pixel)
                {
                    _replayPixelLayout = stage.Layout;
                    _replayPixelBase = stage.Program.UserDataBase;
                }
                else
                {
                    _replayVertexLayout = stage.Layout;
                    _replayVertexBase = stage.Program.UserDataBase;
                }
            }

            new ReadOnlySpan<uint>(pushData, (int)PushData.DwordCount).CopyTo(_replayPushData);
            _replayHasPush = hasPushData;
            _replayLayout = entry.Layout;
            _replayPushStages = pushStages;
            _replayCaptured = true;
        }

        void IRenderHost.SealDrawReplay()
        {
            _replaySealed = false;
            if (!_replayCaptured || !_renderingActive)
            {
                return;
            }

            var recording = CurrentRecording();
            _replayRecording = recording;
            _replayGeneration = recording.Generation;
            _replayEpoch = NativeBindingEpoch.Value;
            _replaySegment = _segment;
            _replayScope = DrawScope;
            _replayOwners = _worker ? 0 : _bufferCache.OwnerGeneration;
            _replaySealed = true;
        }

        bool IRenderHost.TryReplayDraw(in PipelineHandle pipeline, ReadOnlySpan<uint> vertexUserData, ReadOnlySpan<uint> pixelUserData)
        {
            if (!_replaySealed)
            {
                return ReplayHostMiss(0);
            }

            _replaySealed = false;
            var recording = CurrentRecording();
            if (!ReferenceEquals(recording, _replayRecording))
            {
                return ReplayHostMiss(1);
            }

            if (recording.Generation != _replayGeneration)
            {
                return ReplayHostMiss(2);
            }

            if (NativeBindingEpoch.Value != _replayEpoch)
            {
                return ReplayHostMiss(3);
            }

            if (!ReferenceEquals(_segment, _replaySegment))
            {
                return ReplayHostMiss(4);
            }

            if (DrawScope != _replayScope)
            {
                return ReplayHostMiss(5);
            }

            if (!_worker && _bufferCache.OwnerGeneration != _replayOwners)
            {
                return ReplayHostMiss(6);
            }

            var entry = RequirePipelineEntry(in pipeline);
            if (entry.Layout.Handle != _replayLayout.Handle || entry.UsesBindlessHeap)
            {
                return ReplayHostMiss(7);
            }

            if (!_replayHasPush)
            {
                return true;
            }

            var push = stackalloc uint[(int)PushData.DwordCount];
            var data = new Span<uint>(push, (int)PushData.DwordCount);
            _replayPushData.CopyTo(data);
            if (!PatchReplayUserData(data, _replayVertexLayout, _replayVertexBase, vertexUserData) ||
                !PatchReplayUserData(data, _replayPixelLayout, _replayPixelBase, pixelUserData))
            {
                return ReplayHostMiss(8);
            }

            PushConstantsShadowed(BeginBatchedGuestStateCommands(), _replayLayout, _replayPushStages, push);
            return true;
        }

        private static bool PatchReplayUserData(Span<uint> push, BindingLayout? layout, uint userDataBase, ReadOnlySpan<uint> userData)
        {
            if (layout is not { UsesPushData: true })
            {
                return true;
            }

            var registers = layout.UserDataRegisters;
            for (var index = 0; index < registers.Count; index++)
            {
                var register = registers[index];
                var userIndex = (long)register - userDataBase;
                if (userIndex < 0 || userIndex >= userData.Length)
                {
                    return false;
                }

                push[(int)layout.PushDataStartDword + index] = userData[(int)userIndex];
            }

            return true;
        }
    }
}
