using System.Buffers.Binary;
using System.Text;

namespace WinBulkTranscript.CorpusGenerator;

/// <summary>
/// Holds mono PCM16 audio at the corpus master sample rate.
/// </summary>
internal sealed class Pcm16Audio
{
    /// <summary>The fixed corpus master sample rate.</summary>
    public const int SampleRate = 16_000;

    /// <summary>The fixed corpus channel count.</summary>
    public const int Channels = 1;

    /// <summary>The fixed corpus sample width.</summary>
    public const int BitsPerSample = 16;

    /// <summary>
    /// Initializes an audio buffer after validating its PCM frame alignment.
    /// </summary>
    /// <param name="samples">The little-endian PCM16 sample bytes.</param>
    public Pcm16Audio(byte[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if ((samples.Length & 1) != 0)
        {
            throw new ArgumentException("PCM16 data must contain complete two-byte samples.", nameof(samples));
        }

        Samples = samples;
    }

    /// <summary>Gets the little-endian PCM16 sample bytes.</summary>
    public byte[] Samples { get; }

    /// <summary>Gets the count of mono PCM samples.</summary>
    public long SampleCount => Samples.Length / sizeof(short);

    /// <summary>Gets the exact duration in seconds.</summary>
    public double DurationSeconds => SampleCount / (double)SampleRate;

    /// <summary>
    /// Creates a zero-valued silence buffer.
    /// </summary>
    /// <param name="sampleCount">The number of silent mono samples.</param>
    /// <returns>The requested silent audio.</returns>
    public static Pcm16Audio CreateSilence(long sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);

        return new Pcm16Audio(new byte[checked((int)(sampleCount * sizeof(short)))]);
    }

    /// <summary>
    /// Concatenates audio parts without changing their sample representation.
    /// </summary>
    /// <param name="parts">The ordered audio parts.</param>
    /// <returns>The concatenated master audio.</returns>
    public static Pcm16Audio Concatenate(IEnumerable<Pcm16Audio> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var materialized = parts.ToArray();
        var byteCount = checked(materialized.Sum(part => part.Samples.Length));
        var result = new byte[byteCount];
        var destinationOffset = 0;
        foreach (var part in materialized)
        {
            Buffer.BlockCopy(part.Samples, 0, result, destinationOffset, part.Samples.Length);
            destinationOffset += part.Samples.Length;
        }

        return new Pcm16Audio(result);
    }
}

/// <summary>
/// Reads and writes the narrow PCM WAVE form used as the MediaTranscoder bridge.
/// </summary>
internal static class PcmWaveFile
{
    private const int WaveHeaderSize = 44;

    /// <summary>
    /// Writes a standards-compliant PCM16 mono WAVE file.
    /// </summary>
    /// <param name="path">The output WAVE path.</param>
    /// <param name="audio">The master PCM audio.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An asynchronous task.</returns>
    public static async Task WriteAsync(string path, Pcm16Audio audio, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(audio);

        var header = new byte[WaveHeaderSize];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), checked((uint)(36 + audio.Samples.Length)));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22, 2), Pcm16Audio.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), Pcm16Audio.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28, 4), Pcm16Audio.SampleRate * Pcm16Audio.Channels * (Pcm16Audio.BitsPerSample / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32, 2), Pcm16Audio.Channels * (Pcm16Audio.BitsPerSample / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34, 2), Pcm16Audio.BitsPerSample);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), checked((uint)audio.Samples.Length));

        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(audio.Samples, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a WAVE byte stream and accepts only the generator's PCM16/16 kHz/mono contract.
    /// </summary>
    /// <param name="waveBytes">The complete WAVE file bytes.</param>
    /// <returns>Validated master-format PCM audio.</returns>
    public static Pcm16Audio ReadMasterPcm16Mono(ReadOnlySpan<byte> waveBytes)
    {
        if (waveBytes.Length < 12 || !waveBytes[..4].SequenceEqual("RIFF"u8) || !waveBytes.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("The transcoded speech stream is not a RIFF/WAVE file.");
        }

        ushort formatTag = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort bitsPerSample = 0;
        ReadOnlySpan<byte> data = default;
        var foundFormat = false;
        var offset = 12;

        while (offset <= waveBytes.Length - 8)
        {
            var chunkId = waveBytes.Slice(offset, 4);
            var chunkSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(waveBytes.Slice(offset + 4, 4)));
            var chunkDataOffset = offset + 8;
            if (chunkSize < 0 || chunkDataOffset > waveBytes.Length || chunkSize > waveBytes.Length - chunkDataOffset)
            {
                throw new InvalidDataException("The WAVE file contains an invalid chunk length.");
            }

            var chunk = waveBytes.Slice(chunkDataOffset, chunkSize);
            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunk.Length < 16)
                {
                    throw new InvalidDataException("The WAVE fmt chunk is too short.");
                }

                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(chunk[..2]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(14, 2));
                foundFormat = true;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                data = chunk;
            }

            offset = checked(chunkDataOffset + chunkSize + (chunkSize & 1));
        }

        if (!foundFormat || data.IsEmpty)
        {
            throw new InvalidDataException("The WAVE file is missing a fmt or data chunk.");
        }

        if (formatTag != 1 || channels != Pcm16Audio.Channels || sampleRate != Pcm16Audio.SampleRate || bitsPerSample != Pcm16Audio.BitsPerSample)
        {
            throw new InvalidDataException(
                $"Expected PCM16/16 kHz/mono from MediaTranscoder but received format={formatTag}, channels={channels}, sampleRate={sampleRate}, bits={bitsPerSample}.");
        }

        if ((data.Length & 1) != 0)
        {
            throw new InvalidDataException("The WAVE data chunk has an incomplete PCM16 sample.");
        }

        return new Pcm16Audio(data.ToArray());
    }
}
