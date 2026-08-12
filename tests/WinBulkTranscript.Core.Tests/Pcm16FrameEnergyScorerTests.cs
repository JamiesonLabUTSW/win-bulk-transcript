using System.Buffers.Binary;
using WinBulkTranscript.Core.Audio;

namespace WinBulkTranscript.Core.Tests;

public sealed class Pcm16FrameEnergyScorerTests
{
    [Fact]
    public void Score_EmptyAndSilentFrames_ReturnSilence()
    {
        var emptyBytes = Pcm16FrameEnergyScorer.Score(ReadOnlySpan<byte>.Empty);
        var silentBytes = Pcm16FrameEnergyScorer.Score(new byte[8]);
        var emptySamples = Pcm16FrameEnergyScorer.Score(ReadOnlySpan<short>.Empty);

        Assert.Equal(Pcm16FrameEnergy.Silence, emptyBytes);
        Assert.Equal(Pcm16FrameEnergy.Silence, silentBytes);
        Assert.Equal(Pcm16FrameEnergy.Silence, emptySamples);
        Assert.True(silentBytes.IsSilent);
        Assert.True(double.IsNegativeInfinity(silentBytes.DecibelsFullScale));
    }

    [Fact]
    public void Score_ByteAndSampleOverloads_ProduceTheSameRmsAndDbfs()
    {
        short[] samples = [1_000, -1_000, 1_000, -1_000];
        var bytes = new byte[samples.Length * sizeof(short)];
        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * sizeof(short), sizeof(short)), samples[index]);
        }

        var fromSamples = Pcm16FrameEnergyScorer.Score(samples);
        var fromBytes = Pcm16FrameEnergyScorer.Score(bytes);
        var expectedDbfs = 20d * Math.Log10(1_000d / 32_768d);

        Assert.Equal(1_000d, fromSamples.Rms, precision: 12);
        Assert.Equal(expectedDbfs, fromSamples.DecibelsFullScale, precision: 12);
        Assert.Equal(fromSamples.Rms, fromBytes.Rms, precision: 12);
        Assert.Equal(fromSamples.DecibelsFullScale, fromBytes.DecibelsFullScale, precision: 12);
    }

    [Fact]
    public void Score_NegativeFullScaleSample_HasZeroDbfs()
    {
        var energy = Pcm16FrameEnergyScorer.Score(new short[] { short.MinValue });

        Assert.Equal(32_768d, energy.Rms, precision: 12);
        Assert.Equal(0d, energy.DecibelsFullScale, precision: 12);
        Assert.False(energy.IsSilent);
    }

    [Fact]
    public void Score_OddByteCount_RejectsIncompletePcm16Sample()
    {
        var exception = Assert.Throws<ArgumentException>(() => Pcm16FrameEnergyScorer.Score(new byte[] { 1 }));

        Assert.Equal("pcmBytes", exception.ParamName);
    }
}
