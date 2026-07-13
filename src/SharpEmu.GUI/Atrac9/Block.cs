#nullable disable
namespace LibAtrac9
{
    internal class Block
    {
        public Atrac9Config Config { get; }
        public BlockType BlockType { get; }
        public int BlockIndex { get; }
        public Frame Frame { get; }

        public Channel[] Channels { get; }
        public int ChannelCount { get; }

        public bool FirstInSuperframe { get; set; }
        public bool ReuseBandParams { get; set; }

        public int BandCount { get; set; }
        public int StereoBand { get; set; }
        public int ExtensionBand { get; set; }
        public int QuantizationUnitCount { get; set; }
        public int StereoQuantizationUnit { get; set; }
        public int ExtensionUnit { get; set; }
        public int QuantizationUnitsPrev { get; set; }

        public int[] Gradient { get; } = new int[31];
        public int GradientMode { get; set; }
        public int GradientStartUnit { get; set; }
        public int GradientStartValue { get; set; }
        public int GradientEndUnit { get; set; }
        public int GradientEndValue { get; set; }
        public int GradientBoundary { get; set; }

        public int PrimaryChannelIndex { get; set; }
        public int[] JointStereoSigns { get; } = new int[30];
        public bool HasJointStereoSigns { get; set; }
        public Channel PrimaryChannel => Channels[PrimaryChannelIndex == 0 ? 0 : 1];
        public Channel SecondaryChannel => Channels[PrimaryChannelIndex == 0 ? 1 : 0];

        public bool BandExtensionEnabled { get; set; }
        public bool HasExtensionData { get; set; }
        public int BexDataLength { get; set; }
        public int BexMode { get; set; }

        public Block(Frame parentFrame, int blockIndex)
        {
            Frame = parentFrame;
            BlockIndex = blockIndex;
            Config = parentFrame.Config;
            BlockType = Config.ChannelConfig.BlockTypes[blockIndex];
            ChannelCount = BlockTypeToChannelCount(BlockType);
            Channels = new Channel[ChannelCount];
            for (int i = 0; i < ChannelCount; i++)
            {
                Channels[i] = new Channel(this, i);
            }
        }

        public static int BlockTypeToChannelCount(BlockType blockType)
        {
            switch (blockType)
            {
                case BlockType.Mono:
                    return 1;
                case BlockType.Stereo:
                    return 2;
                case BlockType.LFE:
                    return 1;
                default:
                    return 0;
            }
        }
    }

    /// <summary>
    /// An ATRAC9 block (substream) type
    /// </summary>
    public enum BlockType
    {
        /// <summary>
        /// Mono ATRAC9 block
        /// </summary>
        Mono = 0,
        /// <summary>
        /// Stereo ATRAC9 block
        /// </summary>
        Stereo = 1,
        /// <summary>
        /// Low-frequency effects ATRAC9 block
        /// </summary>
        LFE = 2
    }
}
