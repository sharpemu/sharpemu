#nullable disable
using LibAtrac9.Utilities;

namespace LibAtrac9
{
    internal class Channel
    {
        public Atrac9Config Config { get; }
        public int ChannelIndex { get; }
        public bool IsPrimary => Block.PrimaryChannelIndex == ChannelIndex;
        public Block Block { get; }
        public Mdct Mdct { get; }

        public double[] Pcm { get; } = new double[256];
        public double[] Spectra { get; } = new double[256];

        public int CodedQuantUnits { get; set; }
        public int ScaleFactorCodingMode { get; set; }
        public int[] ScaleFactors { get; } = new int[31];
        public int[] ScaleFactorsPrev { get; } = new int[31];

        public int[] Precisions { get; } = new int[30];
        public int[] PrecisionsFine { get; } = new int[30];
        public int[] PrecisionMask { get; } = new int[30];

        public int[] SpectraValuesBuffer { get; } = new int[16];
        public int[] CodebookSet { get; } = new int[30];

        public int[] QuantizedSpectra { get; } = new int[256];
        public int[] QuantizedSpectraFine { get; } = new int[256];

        public int BexMode { get; set; }
        public int BexValueCount { get; set; }
        public int[] BexValues { get; } = new int[4];
        public double[] BexScales { get; } = new double[6];
        public Atrac9Rng Rng { get; set; }

        public Channel(Block parentBlock, int channelIndex)
        {
            Block = parentBlock;
            ChannelIndex = channelIndex;
            Config = parentBlock.Config;
            Mdct = new Mdct(Config.FrameSamplesPower, Tables.ImdctWindow[Config.FrameSamplesPower - 6]);
        }

        public void UpdateCodedUnits() =>
            CodedQuantUnits = IsPrimary ? Block.QuantizationUnitCount : Block.StereoQuantizationUnit;
    }
}
