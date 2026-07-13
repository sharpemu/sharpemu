#nullable disable
namespace LibAtrac9
{
    internal static class Stereo
    {
        public static void ApplyIntensityStereo(Block block)
        {
            if (block.BlockType != BlockType.Stereo) return;

            int totalUnits = block.QuantizationUnitCount;
            int stereoUnits = block.StereoQuantizationUnit;
            if (stereoUnits >= totalUnits) return;

            Channel source = block.PrimaryChannel;
            Channel dest = block.SecondaryChannel;

            for (int i = stereoUnits; i < totalUnits; i++)
            {
                int sign = block.JointStereoSigns[i];
                for (int sb = Tables.QuantUnitToCoeffIndex[i]; sb < Tables.QuantUnitToCoeffIndex[i + 1]; sb++)
                {
                    if (sign > 0)
                    {
                        dest.Spectra[sb] = -source.Spectra[sb];
                    }
                    else
                    {
                        dest.Spectra[sb] = source.Spectra[sb];
                    }
                }
            }
        }
    }
}
