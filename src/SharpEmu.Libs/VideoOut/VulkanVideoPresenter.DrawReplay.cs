// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
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

        private static readonly bool DispatchReplayCapture = Environment.GetEnvironmentVariable("SHARPEMU_DISPATCH_REPLAY") != "0";

        private readonly record struct HeldStorage(ulong Address, ulong Size, BufferResource Resource, GpuBuffer Buffer, ulong Offset);

        // The open preparation bound a stream copy: a read-only range copied into the slice arena as it stood at this item.
        private bool _preparationStreamCopied;

        private readonly List<HeldStorage> _stageStorage = new();
        private bool _holdingStorage;
        private HeldStorage[] _dispatchStorage = new HeldStorage[16];
        private int _dispatchStorageCount;
        private readonly uint[] _dispatchPushData = new uint[PushData.DwordCount];
        private bool _dispatchCaptured;
        private bool _dispatchSealed;
        private bool _dispatchHasPush;
        private PipelineLayout _dispatchLayout;
        private BindingLayout? _dispatchStageLayout;
        private uint _dispatchUserDataBase;
        private ulong _dispatchProgramHash;
        private RecordingBuffer? _dispatchRecording;
        private long _dispatchGeneration;
        private long _dispatchEpoch;
        private SliceSegment? _dispatchSegment;
        private DrawScopeToken _dispatchScope;
        private long _dispatchOwners;
        private static readonly string[] DispatchHostMissNames =
            ["unsealed", "recording", "generation", "epoch", "segment", "scope", "owners", "layout", "patch", "written", "dirty", "texel",
             "table_set", "table_layout", "table_shadow"];
        private static readonly long[] _dispatchHostMisses = new long[DispatchHostMissNames.Length];

        internal static string TakeDispatchHostMisses()
        {
            var parts = new List<string>();
            for (var index = 0; index < DispatchHostMissNames.Length; index++)
            {
                var count = Interlocked.Exchange(ref _dispatchHostMisses[index], 0);
                if (count != 0)
                {
                    parts.Add(FormattableString.Invariant($"{DispatchHostMissNames[index]}:{count}"));
                }
            }

            return parts.Count == 0 ? "none" : string.Join(",", parts);
        }

        private static bool DispatchHostMiss(int reason)
        {
            Interlocked.Increment(ref _dispatchHostMisses[reason]);
            return false;
        }

        private void CaptureDispatchReplay(PipelineBindPoint bindPoint, RenderPipelineEntry entry, ReadOnlySpan<IPreparedBindings> stages, uint* pushData,
            bool hasPushData)
        {
            if (bindPoint != PipelineBindPoint.Compute)
            {
                return;
            }

            _dispatchCaptured = false;
            _dispatchSealed = false;
            _holdingStorage = false;
            if (!DispatchReplayCapture || entry.UsesBindlessHeap || stages.Length != 1 || _preparation?.StencilStorageImages is not null)
            {
                return;
            }

            var stage = (PreparedStageBindings)stages[0];
            if (stage.Descriptors.GlobalDataShare.Buffer.Handle != 0 || stage.Layout.MipStatisticsCount != 0 ||
                stage.Layout.Find(DescriptorBindingKind.ShaderData) is not null)
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

            if (_dispatchStorage.Length < _stageStorage.Count)
            {
                _dispatchStorage = new HeldStorage[Math.Max(_stageStorage.Count, _dispatchStorage.Length * 2)];
            }

            for (var index = 0; index < _stageStorage.Count; index++)
            {
                var held = _stageStorage[index];
                if (!held.Resource.Written && held.Buffer is GpuRingBuffer)
                {
                    return;
                }

                _dispatchStorage[index] = held;
            }

            // A replay keeps the sealed descriptors, so a later item would read this item's stream copy.
            if (_preparationStreamCopied)
            {
                return;
            }

            _dispatchStorageCount = _stageStorage.Count;
            new ReadOnlySpan<uint>(pushData, (int)PushData.DwordCount).CopyTo(_dispatchPushData);
            _dispatchHasPush = hasPushData;
            _dispatchLayout = entry.Layout;
            _dispatchStageLayout = stage.Layout;
            _dispatchUserDataBase = stage.Program.UserDataBase;
            _dispatchProgramHash = stage.Program.Hash;
            _dispatchCaptured = true;
        }

        void IRenderHost.SealDispatchReplay()
        {
            _dispatchSealed = false;
            if (!_dispatchCaptured)
            {
                return;
            }

            var recording = CurrentRecording();
            _dispatchRecording = recording;
            _dispatchGeneration = recording.Generation;
            _dispatchEpoch = NativeBindingEpoch.Value;
            _dispatchSegment = _segment;
            _dispatchScope = DrawScope;
            _dispatchOwners = _worker ? 0 : _bufferCache.OwnerGeneration;
            _dispatchSealed = true;
        }

        bool IRenderHost.TryReplayDispatch(in PipelineHandle pipeline, in ShaderStageResources stage, bool replaceTable)
        {
            if (!_dispatchSealed)
            {
                return DispatchHostMiss(0);
            }

            _dispatchSealed = false;
            var recording = CurrentRecording();
            if (!ReferenceEquals(recording, _dispatchRecording))
            {
                return DispatchHostMiss(1);
            }

            if (recording.Generation != _dispatchGeneration)
            {
                return DispatchHostMiss(2);
            }

            if (NativeBindingEpoch.Value != _dispatchEpoch)
            {
                return DispatchHostMiss(3);
            }

            if (!ReferenceEquals(_segment, _dispatchSegment))
            {
                return DispatchHostMiss(4);
            }

            if (DrawScope != _dispatchScope)
            {
                return DispatchHostMiss(5);
            }

            var entry = RequirePipelineEntry(in pipeline);
            if (entry.Layout.Handle != _dispatchLayout.Handle || entry.UsesBindlessHeap)
            {
                return DispatchHostMiss(7);
            }

            if (!_worker)
            {
                if (_bufferCache.OwnerGeneration != _dispatchOwners)
                {
                    return DispatchHostMiss(6);
                }

                for (var index = 0; index < _dispatchStorageCount; index++)
                {
                    var held = _dispatchStorage[index];
                    if (!held.Resource.Written && !held.Resource.Formatted)
                    {
                        if (_bufferCache.MayHaveCpuDirtyPages(held.Address, held.Size))
                        {
                            return DispatchHostMiss(10);
                        }

                        continue;
                    }

                    var (buffer, offset, _) = ObtainStorageBuffer(held.Address, held.Size, held.Resource, default, _dispatchProgramHash);
                    if (!ReferenceEquals(buffer, held.Buffer) || offset != held.Offset)
                    {
                        return DispatchHostMiss(held.Resource.Written ? 9 : 11);
                    }
                }

                if (_bufferCache.OwnerGeneration != _dispatchOwners)
                {
                    return DispatchHostMiss(6);
                }
            }

            // Only a pushed set can take one binding again. When the native state is shadowed, the shadow must still hold the
            // sealed set, so the new descriptor can replace its entry there.
            var tableBinding = 0u;
            var tableEntry = -1;
            DescriptorShadow? tableShadow = null;
            if (replaceTable)
            {
                if (!entry.UsesPushDescriptors)
                {
                    return DispatchHostMiss(12);
                }

                if (stage.Program is not { } tableProgram || _dispatchStageLayout?.Find(DescriptorBindingKind.FlattenedResourceTable) is not { } table ||
                    DescriptorWriter.DescriptorCount(table) != 1)
                {
                    return DispatchHostMiss(13);
                }

                tableBinding = BindingLayout.NativeBindingIndex(StageOf(tableProgram), DescriptorBindingKind.FlattenedResourceTable);
                if (SyncNativeShadow(BeginBatchedGuestStateCommands()))
                {
                    tableShadow = _computeDescriptorShadow;
                    if (tableShadow is not { Valid: true } || tableShadow.Layout.Handle != _dispatchLayout.Handle ||
                        (tableEntry = FindShadowBufferEntry(tableShadow, tableBinding)) < 0)
                    {
                        return DispatchHostMiss(14);
                    }
                }
            }

            var push = stackalloc uint[(int)PushData.DwordCount];
            var data = new Span<uint>(push, (int)PushData.DwordCount);
            if (_dispatchHasPush)
            {
                _dispatchPushData.CopyTo(data);
                var layout = _dispatchStageLayout;
                if (!PatchReplayUserData(data, layout, _dispatchUserDataBase, stage.Resources.UserData))
                {
                    return DispatchHostMiss(8);
                }

                if (layout is { UsesPushData: true, UsesDispatchThreadLimits: true } && !stage.GpuThreadLimits)
                {
                    stage.WriteDispatchThreadLimits(data.Slice((int)layout.PushDataStartDword, (int)layout.ShaderDataDwordCount));
                }
            }

            if (replaceTable)
            {
                PushTableBinding(in stage, tableBinding, tableShadow, tableEntry);
            }

            if (!_dispatchHasPush)
            {
                return true;
            }

            data.CopyTo(_dispatchPushData);
            PushConstantsShadowed(BeginBatchedGuestStateCommands(), _dispatchLayout, ShaderStageFlags.ComputeBit, push);
            return true;
        }

        // Uploads the table as the full path does and pushes only its descriptor; the other pushed descriptors of the sealed
        // dispatch stay bound. The shadow's entry takes the new descriptor, so the next full commit compares against it.
        private void PushTableBinding(in ShaderStageResources stage, uint binding, DescriptorShadow? shadow, int entry)
        {
            var view = UploadDwords(stage.Resources.FlattenedResourceTable, "flattened resource table", stage.Program!, byIdentity: true);
            var info = new DescriptorBufferInfo { Buffer = view.Buffer, Offset = view.Offset, Range = view.Range };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstBinding = binding,
                DescriptorType = DescriptorWriter.DescriptorType(DescriptorBindingKind.FlattenedResourceTable),
                DescriptorCount = 1,
                PBufferInfo = &info,
            };
            var command = BeginBatchedGuestStateCommands();
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _presenter._pushDescriptorApi.CmdPushDescriptorSet(command, PipelineBindPoint.Compute, _dispatchLayout, 0, 1, &write);
            }

            if (shadow is not null && entry >= 0)
            {
                shadow.Key[entry] = info.Buffer.Handle;
                shadow.Key[entry + 1] = info.Offset;
                shadow.Key[entry + 2] = info.Range;
            }
        }

        // The key index of a binding's one buffer descriptor in a shadow (DescriptorKey's layout), or -1.
        private static int FindShadowBufferEntry(DescriptorShadow shadow, uint binding)
        {
            var key = shadow.Key;
            var index = 0;
            while (index + 2 <= shadow.Length)
            {
                var flags = key[index + 1];
                var count = (int)(uint)flags;
                var hasBuffer = (flags & (1UL << 32)) != 0;
                var described = hasBuffer || (flags & (1UL << 33)) != 0;
                if ((uint)key[index] == binding)
                {
                    return hasBuffer && count == 1 && index + 5 <= shadow.Length ? index + 2 : -1;
                }

                index += 2 + (described ? count * 3 : 0);
            }

            return -1;
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
