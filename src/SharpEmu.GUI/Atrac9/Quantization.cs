#nullable disable
using System;

namespace LibAtrac9
{
    internal static class Quantization
    {
        public static void DequantizeSpectra(Block block)
        {
            foreach (Channel channel in block.Channels)
            {
                Array.Clear(channel.Spectra, 0, channel.Spectra.Length);

                for (int i = 0; i < channel.CodedQuantUnits; i++)
                {
                    DequantizeQuantUnit(channel, i);
                }
            }
        }

        private static void DequantizeQuantUnit(Channel channel, int band)
        {
            int subBandIndex = Tables.QuantUnitToCoeffIndex[band];
            int subBandCount = Tables.QuantUnitToCoeffCount[band];
            double stepSize = Tables.QuantizerStepSize[channel.Precisions[band]];
            double stepSizeFine = Tables.QuantizerFineStepSize[channel.PrecisionsFine[band]];

            for (int sb = 0; sb < subBandCount; sb++)
            {
                double coarse = channel.QuantizedSpectra[subBandIndex + sb] * stepSize;
                double fine = channel.QuantizedSpectraFine[subBandIndex + sb] * stepSizeFine;
                channel.Spectra[subBandIndex + sb] = coarse + fine;
            }
        }

        public static void ScaleSpectrum(Block block)
        {
            foreach (Channel channel in block.Channels)
            {
                ScaleSpectrum(channel);
            }
        }

        private static void ScaleSpectrum(Channel channel)
        {
            int quantUnitCount = channel.Block.QuantizationUnitCount;
            double[] spectra = channel.Spectra;

            for (int i = 0; i < quantUnitCount; i++)
            {
                for (int sb = Tables.QuantUnitToCoeffIndex[i]; sb < Tables.QuantUnitToCoeffIndex[i + 1]; sb++)
                {
                    spectra[sb] *= Tables.SpectrumScale[channel.ScaleFactors[i]];
                }
            }
        }
    }
}
