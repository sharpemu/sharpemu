#nullable disable
namespace LibAtrac9
{
    /// <summary>
    /// Describes the channel mapping for an ATRAC9 stream
    /// </summary>
    public class ChannelConfig
    {
        internal ChannelConfig(params BlockType[] blockTypes)
        {
            BlockCount = blockTypes.Length;
            BlockTypes = blockTypes;
            foreach (BlockType type in blockTypes)
            {
                ChannelCount += Block.BlockTypeToChannelCount(type);
            }
        }

        /// <summary>
        /// The number of blocks or substreams in the ATRAC9 stream
        /// </summary>
        public int BlockCount { get; }

        /// <summary>
        /// The type of each block or substream in the ATRAC9 stream
        /// </summary>
        public BlockType[] BlockTypes { get; }

        /// <summary>
        /// The number of channels in the ATRAC9 stream
        /// </summary>
        public int ChannelCount { get; }
    }
}
