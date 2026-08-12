using System.Buffers.Binary;
using System.Text;
using WinBulkTranscript.Core.Domain;

namespace WinBulkTranscript.Core.Wave;

/// <summary>Reads and validates the narrowly supported PCM RIFF/WAVE representation.</summary>
public static class WaveFileReader
{
    private const int RiffHeaderLength = 12;
    private const int ChunkHeaderLength = 8;
    private const int PcmFormatLength = 16;
    private const ushort PcmFormatTag = 1;

    /// <summary>
    /// Walks a RIFF/WAVE file, validates its PCM format, and locates its sole audio data chunk.
    /// </summary>
    /// <param name="path">The path of the WAVE file to inspect.</param>
    /// <returns>A description of the validated PCM data without loading the audio into memory.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="WaveFileFormatException">The file is not a supported, complete PCM16 WAVE file.</exception>
    public static PcmWaveFile Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4 * 1024,
            FileOptions.SequentialScan);

        return ReadCore(stream, path);
    }

    private static PcmWaveFile ReadCore(FileStream stream, string path)
    {
        var fileLength = stream.Length;
        if (fileLength < RiffHeaderLength)
        {
            throw Invalid("The file is shorter than the RIFF/WAVE header.");
        }

        Span<byte> riffHeader = stackalloc byte[RiffHeaderLength];
        stream.ReadExactly(riffHeader);

        if (!riffHeader[..4].SequenceEqual("RIFF"u8))
        {
            if (riffHeader[..4].SequenceEqual("RF64"u8))
            {
                throw Invalid("RF64 WAVE files are not supported.");
            }

            throw Invalid("The file does not begin with a RIFF header.");
        }

        if (!riffHeader[8..].SequenceEqual("WAVE"u8))
        {
            throw Invalid("The RIFF form type is not WAVE.");
        }

        var declaredRiffSize = BinaryPrimitives.ReadUInt32LittleEndian(riffHeader[4..8]);
        var riffEnd = checked((long)declaredRiffSize + 8L);
        if (riffEnd < RiffHeaderLength || riffEnd > fileLength)
        {
            throw Invalid("The RIFF container is truncated.");
        }

        PcmFormat? format = null;
        long dataOffset = -1;
        long dataLength = -1;
        var offset = (long)RiffHeaderLength;

        Span<byte> chunkHeader = stackalloc byte[ChunkHeaderLength];
        while (offset < riffEnd)
        {
            if (riffEnd - offset < ChunkHeaderLength)
            {
                throw Invalid("A RIFF chunk header is truncated.");
            }

            stream.Position = offset;
            stream.ReadExactly(chunkHeader);

            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            var chunkDataOffset = checked(offset + ChunkHeaderLength);
            var chunkDataEnd = checked(chunkDataOffset + (long)chunkLength);
            if (chunkDataEnd > riffEnd)
            {
                throw Invalid($"The {DescribeChunk(chunkHeader[..4])} chunk is truncated.");
            }

            var nextOffset = checked(chunkDataEnd + (chunkLength & 1u));
            if (nextOffset > riffEnd)
            {
                throw Invalid($"The {DescribeChunk(chunkHeader[..4])} chunk is missing its alignment byte.");
            }

            if (chunkHeader[..4].SequenceEqual("fmt "u8))
            {
                if (format is not null)
                {
                    throw Invalid("The WAVE file contains more than one fmt chunk.");
                }

                format = ReadFormat(stream, chunkDataOffset, chunkLength);
            }
            else if (chunkHeader[..4].SequenceEqual("data"u8))
            {
                if (dataOffset >= 0)
                {
                    throw Invalid("The WAVE file contains more than one data chunk.");
                }

                dataOffset = chunkDataOffset;
                dataLength = chunkLength;
            }

            offset = nextOffset;
        }

        if (format is null)
        {
            throw Invalid("The WAVE file has no fmt chunk.");
        }

        if (dataOffset < 0)
        {
            throw Invalid("The WAVE file has no data chunk.");
        }

        if (dataLength % format.Value.BlockAlign != 0)
        {
            throw Invalid("The data chunk length is not aligned to complete PCM samples.");
        }

        return new PcmWaveFile(path, format.Value, dataOffset, dataLength);
    }

    private static PcmFormat ReadFormat(FileStream stream, long chunkDataOffset, uint chunkLength)
    {
        if (chunkLength < PcmFormatLength)
        {
            throw Invalid("The fmt chunk is shorter than a PCM WAVE format.");
        }

        Span<byte> formatBytes = stackalloc byte[PcmFormatLength];
        stream.Position = chunkDataOffset;
        stream.ReadExactly(formatBytes);

        var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes[..2]);
        if (formatTag != PcmFormatTag)
        {
            throw Invalid("Only uncompressed PCM WAVE format tag 1 is supported.");
        }

        var channels = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.Slice(2, 2));
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(formatBytes.Slice(4, 4));
        var byteRate = BinaryPrimitives.ReadUInt32LittleEndian(formatBytes.Slice(8, 4));
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.Slice(12, 2));
        var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.Slice(14, 2));

        if (channels == 0 || channels > short.MaxValue || sampleRate == 0 || sampleRate > int.MaxValue || blockAlign == 0 || blockAlign > short.MaxValue || bitsPerSample > short.MaxValue)
        {
            throw Invalid("The fmt chunk contains values outside the supported range.");
        }

        var expectedBlockAlign = checked((uint)channels * bitsPerSample / 8u);
        if (expectedBlockAlign == 0 || blockAlign != expectedBlockAlign)
        {
            throw Invalid("The fmt chunk has an inconsistent block alignment.");
        }

        var expectedByteRate = checked((ulong)sampleRate * blockAlign);
        if (byteRate != expectedByteRate)
        {
            throw Invalid("The fmt chunk has an inconsistent byte rate.");
        }

        var format = new PcmFormat((int)sampleRate, (short)channels, (short)bitsPerSample, (short)blockAlign);
        if (!format.IsRequired)
        {
            throw Invalid("Only 16 kHz, mono, signed PCM16 WAVE audio is supported.");
        }

        return format;
    }

    private static string DescribeChunk(ReadOnlySpan<byte> chunkId)
    {
        foreach (var value in chunkId)
        {
            if (value is < 0x20 or > 0x7e)
            {
                return "non-text";
            }
        }

        return Encoding.ASCII.GetString(chunkId);
    }

    private static WaveFileFormatException Invalid(string message) => new(message);
}
/// <summary>Indicates that a RIFF/WAVE file is malformed or outside the supported PCM contract.</summary>
public sealed class WaveFileFormatException : IOException
{
    /// <summary>Initializes an exception with a human-readable validation failure.</summary>
    public WaveFileFormatException(string message)
        : base(message)
    {
    }
}
