using System.Buffers.Binary;
using System.Text;
using WinBulkTranscript.Core.Domain;
using WinBulkTranscript.Core.Wave;

namespace WinBulkTranscript.Core.Tests;

internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "WinBulkTranscript.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string CreateDirectory(params string[] pathSegments)
    {
        var path = Combine(pathSegments);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string relativePath, ReadOnlySpan<byte> content)
    {
        var path = Combine(relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries));
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, content.ToArray());
        return path;
    }

    public string CreateTextFile(string relativePath, string content = "")
    {
        var path = Combine(relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries));
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private string Combine(IReadOnlyList<string> pathSegments)
    {
        var path = Root;
        foreach (var segment in pathSegments)
        {
            path = Path.Combine(path, segment);
        }

        return path;
    }
}

internal sealed class InlineProgress<T> : IProgress<T>
{
    public List<T> Values { get; } = [];

    public void Report(T value) => Values.Add(value);
}

internal sealed record WaveChunk(string Id, byte[] Data)
{
    public static WaveChunk RequiredPcmFormat(
        ushort formatTag = 1,
        ushort channels = 1,
        uint sampleRate = 16_000,
        uint? byteRate = null,
        ushort blockAlign = 2,
        ushort bitsPerSample = 16)
    {
        var data = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0, 2), formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2, 2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), byteRate ?? sampleRate * blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14, 2), bitsPerSample);
        return new WaveChunk("fmt ", data);
    }

    public static WaveChunk DataFromSamples(params short[] samples)
    {
        var data = new byte[checked(samples.Length * sizeof(short))];
        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(index * sizeof(short), sizeof(short)), samples[index]);
        }

        return new WaveChunk("data", data);
    }
}

internal static class WaveFixture
{
    public static byte[] Create(
        IReadOnlyList<WaveChunk> chunks,
        string riffId = "RIFF",
        string formType = "WAVE",
        uint? declaredRiffSize = null)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (riffId.Length != 4 || formType.Length != 4)
        {
            throw new ArgumentException("RIFF identifiers must be exactly four ASCII characters.");
        }

        var bytes = new List<byte>(64);
        AddAscii(bytes, riffId);
        AddUInt32(bytes, 0);
        AddAscii(bytes, formType);

        foreach (var chunk in chunks)
        {
            ArgumentNullException.ThrowIfNull(chunk);
            if (chunk.Id.Length != 4)
            {
                throw new ArgumentException("Chunk identifiers must be exactly four ASCII characters.", nameof(chunks));
            }

            AddAscii(bytes, chunk.Id);
            AddUInt32(bytes, checked((uint)chunk.Data.Length));
            bytes.AddRange(chunk.Data);
            if ((chunk.Data.Length & 1) != 0)
            {
                bytes.Add(0);
            }
        }

        var result = bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), declaredRiffSize ?? checked((uint)(result.Length - 8)));
        return result;
    }

    public static PcmWaveFile WriteRequiredPcmWave(TestWorkspace workspace, string relativePath, params short[] samples)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var path = workspace.CreateFile(relativePath, Create([WaveChunk.RequiredPcmFormat(), WaveChunk.DataFromSamples(samples)]));
        return WaveFileReader.Open(path);
    }

    private static void AddAscii(List<byte> destination, string value)
    {
        foreach (var character in value)
        {
            destination.Add(checked((byte)character));
        }
    }

    private static void AddUInt32(List<byte> destination, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        destination.AddRange(bytes.ToArray());
    }
}
