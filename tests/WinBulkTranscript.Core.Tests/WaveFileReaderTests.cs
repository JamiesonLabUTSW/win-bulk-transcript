using WinBulkTranscript.Core.Domain;
using WinBulkTranscript.Core.Wave;

namespace WinBulkTranscript.Core.Tests;

public sealed class WaveFileReaderTests
{
    [Fact]
    public void Open_ValidRequiredPcmWithUnknownOddChunk_ReturnsDataChunkMetadata()
    {
        using var workspace = new TestWorkspace();
        var bytes = WaveFixture.Create(
        [
            new WaveChunk("JUNK", [1, 2, 3]),
            WaveChunk.RequiredPcmFormat(),
            WaveChunk.DataFromSamples(1, -2, 3),
        ]);
        var path = workspace.CreateFile("audio.wav", bytes);

        var wave = WaveFileReader.Open(path);

        Assert.Equal(path, wave.Path);
        Assert.Equal(PcmFormat.Required, wave.Format);
        Assert.Equal(3, wave.SampleCount);
        Assert.Equal(3L * PcmFormat.Required.BlockAlign, wave.DataLength);
        Assert.Equal(56, wave.DataOffset);
    }

    [Fact]
    public void Open_DataBeforeFormat_IsAcceptedWhenBothChunksAreValid()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.CreateFile(
            "audio.wav",
            WaveFixture.Create([WaveChunk.DataFromSamples(10, 20), WaveChunk.RequiredPcmFormat()]));

        var wave = WaveFileReader.Open(path);

        Assert.Equal(20, wave.DataOffset);
        Assert.Equal(4, wave.DataLength);
        Assert.Equal(2, wave.SampleCount);
    }

    [Theory]
    [InlineData("RF64", "WAVE", "RF64")]
    [InlineData("RIFX", "WAVE", "RIFF")]
    [InlineData("RIFF", "AVI ", "WAVE")]
    public void Open_InvalidContainerHeader_ThrowsUsefulFormatException(string riffId, string formType, string expectedMessage)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.CreateFile(
            "invalid.wav",
            WaveFixture.Create([WaveChunk.RequiredPcmFormat(), WaveChunk.DataFromSamples(1)], riffId, formType));

        var exception = Assert.Throws<WaveFileFormatException>(() => WaveFileReader.Open(path));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_ContainerDeclaredLargerThanFile_RejectsTruncation()
    {
        using var workspace = new TestWorkspace();
        var valid = WaveFixture.Create([WaveChunk.RequiredPcmFormat(), WaveChunk.DataFromSamples(1)]);
        var path = workspace.CreateFile("truncated.wav", valid);
        var bytes = File.ReadAllBytes(path);
        bytes[4] = 0xff;
        bytes[5] = 0xff;
        bytes[6] = 0xff;
        bytes[7] = 0x7f;
        File.WriteAllBytes(path, bytes);

        var exception = Assert.Throws<WaveFileFormatException>(() => WaveFileReader.Open(path));

        Assert.Contains("truncated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData((ushort)3, (ushort)1, 16_000u, 32_000u, (ushort)2, (ushort)16, "uncompressed PCM")]
    [InlineData((ushort)1, (ushort)2, 16_000u, 64_000u, (ushort)4, (ushort)16, "16 kHz")]
    [InlineData((ushort)1, (ushort)1, 8_000u, 16_000u, (ushort)2, (ushort)16, "16 kHz")]
    [InlineData((ushort)1, (ushort)1, 16_000u, 12u, (ushort)2, (ushort)16, "byte rate")]
    [InlineData((ushort)1, (ushort)1, 16_000u, 32_000u, (ushort)1, (ushort)16, "block alignment")]
    public void Open_UnsupportedOrInconsistentFormat_ThrowsUsefulFormatException(
        ushort formatTag,
        ushort channels,
        uint sampleRate,
        uint byteRate,
        ushort blockAlign,
        ushort bitsPerSample,
        string expectedMessage)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.CreateFile(
            "invalid-format.wav",
            WaveFixture.Create(
            [
                WaveChunk.RequiredPcmFormat(formatTag, channels, sampleRate, byteRate, blockAlign, bitsPerSample),
                WaveChunk.DataFromSamples(1),
            ]));

        var exception = Assert.Throws<WaveFileFormatException>(() => WaveFileReader.Open(path));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-fmt")]
    [InlineData("missing-data")]
    [InlineData("duplicate-fmt")]
    [InlineData("duplicate-data")]
    [InlineData("unaligned-data")]
    public void Open_MalformedChunkLayout_ThrowsFormatException(string scenario)
    {
        using var workspace = new TestWorkspace();
        IReadOnlyList<WaveChunk> chunks = scenario switch
        {
            "missing-fmt" => [WaveChunk.DataFromSamples(1)],
            "missing-data" => [WaveChunk.RequiredPcmFormat()],
            "duplicate-fmt" => [WaveChunk.RequiredPcmFormat(), WaveChunk.RequiredPcmFormat(), WaveChunk.DataFromSamples(1)],
            "duplicate-data" => [WaveChunk.RequiredPcmFormat(), WaveChunk.DataFromSamples(1), WaveChunk.DataFromSamples(2)],
            "unaligned-data" => [WaveChunk.RequiredPcmFormat(), new WaveChunk("data", [1])],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var path = workspace.CreateFile("invalid-layout.wav", WaveFixture.Create(chunks));

        Assert.Throws<WaveFileFormatException>(() => WaveFileReader.Open(path));
    }

    [Fact]
    public void Open_EmptyPathAndMissingFile_UseAppropriateExceptions()
    {
        Assert.Throws<ArgumentException>(() => WaveFileReader.Open(""));

        using var workspace = new TestWorkspace();
        Assert.Throws<FileNotFoundException>(() => WaveFileReader.Open(Path.Combine(workspace.Root, "missing.wav")));
    }
}
