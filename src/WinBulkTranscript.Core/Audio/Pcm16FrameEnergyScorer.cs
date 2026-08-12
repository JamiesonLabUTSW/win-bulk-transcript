using System.Buffers.Binary;

namespace WinBulkTranscript.Core.Audio;

/// <summary>Represents the energy of one signed PCM16 frame.</summary>
public readonly record struct Pcm16FrameEnergy(double Rms, double DecibelsFullScale)
{
    /// <summary>Gets whether the frame contains no non-zero samples.</summary>
    public bool IsSilent => Rms == 0d;

    /// <summary>Gets the energy value for a frame containing only zero samples.</summary>
    public static Pcm16FrameEnergy Silence { get; } = new(0d, double.NegativeInfinity);
}

/// <summary>Calculates RMS and dBFS values from little-endian signed PCM16 frames.</summary>
public static class Pcm16FrameEnergyScorer
{
    private const double FullScale = 32_768d;

    /// <summary>
    /// Calculates the RMS amplitude and dBFS value for an interleaved little-endian PCM16 byte span.
    /// </summary>
    /// <param name="pcmBytes">A whole number of signed 16-bit little-endian samples.</param>
    /// <returns>The calculated frame energy, or <see cref="Pcm16FrameEnergy.Silence"/> for an empty or silent frame.</returns>
    /// <exception cref="ArgumentException"><paramref name="pcmBytes"/> has an odd byte length.</exception>
    public static Pcm16FrameEnergy Score(ReadOnlySpan<byte> pcmBytes)
    {
        if ((pcmBytes.Length & 1) != 0)
        {
            throw new ArgumentException("A PCM16 frame must contain a whole number of samples.", nameof(pcmBytes));
        }

        var sampleCount = pcmBytes.Length / sizeof(short);
        if (sampleCount == 0)
        {
            return Pcm16FrameEnergy.Silence;
        }

        double sumOfSquares = 0d;
        for (var byteIndex = 0; byteIndex < pcmBytes.Length; byteIndex += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcmBytes.Slice(byteIndex, sizeof(short)));
            sumOfSquares += (double)sample * sample;
        }

        if (sumOfSquares == 0d)
        {
            return Pcm16FrameEnergy.Silence;
        }

        var rms = Math.Sqrt(sumOfSquares / sampleCount);
        var decibelsFullScale = 20d * Math.Log10(rms / FullScale);
        return new Pcm16FrameEnergy(rms, decibelsFullScale);
    }

    /// <summary>Calculates the RMS amplitude and dBFS value for signed PCM16 samples.</summary>
    /// <param name="samples">Signed PCM16 samples.</param>
    /// <returns>The calculated frame energy, or <see cref="Pcm16FrameEnergy.Silence"/> for an empty or silent frame.</returns>
    public static Pcm16FrameEnergy Score(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return Pcm16FrameEnergy.Silence;
        }

        double sumOfSquares = 0d;
        foreach (var sample in samples)
        {
            sumOfSquares += (double)sample * sample;
        }

        if (sumOfSquares == 0d)
        {
            return Pcm16FrameEnergy.Silence;
        }

        var rms = Math.Sqrt(sumOfSquares / samples.Length);
        var decibelsFullScale = 20d * Math.Log10(rms / FullScale);
        return new Pcm16FrameEnergy(rms, decibelsFullScale);
    }
}
