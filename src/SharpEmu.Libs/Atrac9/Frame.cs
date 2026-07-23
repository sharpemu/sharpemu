#nullable disable
namespace LibAtrac9
{
    internal class Frame
    {
        public Atrac9Config Config { get; }
        public int FrameIndex { get; set; }
        public Block[] Blocks { get; }

        public Frame(Atrac9Config config)
        {
            Config = config;
            Blocks = new Block[config.ChannelConfig.BlockCount];

            for (int i = 0; i < config.ChannelConfig.BlockCount; i++)
            {
                Blocks[i] = new Block(this, i);
            }
        }
    }
}
