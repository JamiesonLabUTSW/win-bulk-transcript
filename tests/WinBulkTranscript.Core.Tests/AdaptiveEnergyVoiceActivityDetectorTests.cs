using WinBulkTranscript.Core.Audio;
using WinBulkTranscript.Core.Domain;

namespace WinBulkTranscript.Core.Tests;

public sealed class AdaptiveEnergyVoiceActivityDetectorTests
{
    [Fact]
    public async Task DetectAsync_ProducesSampleAccurateIntervalAfterConfirmedOnsetAndSilence()
    {
        using var workspace = new TestWorkspace();
        var wave = WaveFixture.WriteRequiredPcmWave(
            workspace,
            "speech.wav",
            [
                0, 0, 0, 0,
                0, 0, 0, 0,
                4_000, 4_000, 4_000, 4_000,
                4_000, 4_000, 4_000, 4_000,
                4_000, 4_000, 4_000, 4_000,
                0, 0, 0, 0,
                0, 0, 0, 0,
            ]);
        var detector = new AdaptiveEnergyVoiceActivityDetector(CreateOptions(onsetFrames: 2, silenceFrames: 2));
        var progress = new InlineProgress<double>();

        var intervals = await detector.DetectAsync(wave, progress, CancellationToken.None);

        Assert.Equal([new SpeechInterval(8, 20)], intervals);
        Assert.Equal(0d, progress.Values[0]);
        Assert.Equal(1d, progress.Values[^1]);
        Assert.True(IsMonotonic(progress.Values));
    }

    [Fact]
    public async Task DetectAsync_UsesAbsoluteThresholdClampToIgnoreTinyInitialNoise()
    {
        using var workspace = new TestWorkspace();
        var wave = WaveFixture.WriteRequiredPcmWave(
            workspace,
            "tiny-noise.wav",
            [
                100, 100, 100, 100,
                100, 100, 100, 100,
                100, 100, 100, 100,
            ]);
        var options = CreateOptions(onsetFrames: 1, silenceFrames: 2) with
        {
            InitialNoiseFloorDbfs = -90,
            MinimumNoiseFloorDbfs = -90,
            MaximumNoiseFloorDbfs = -20,
            MinimumOnThresholdDbfs = -45,
            MinimumOffThresholdDbfs = -50,
        };
        var detector = new AdaptiveEnergyVoiceActivityDetector(options);

        var intervals = await detector.DetectAsync(wave, progress: null, CancellationToken.None);

        Assert.Empty(intervals);
    }

    [Fact]
    public async Task DetectAsync_HandlesFinalPartialAnalysisFrameAndFlushesAtEndOfFile()
    {
        using var workspace = new TestWorkspace();
        var wave = WaveFixture.WriteRequiredPcmWave(
            workspace,
            "partial-frame.wav",
            [4_000, 4_000, 4_000, 4_000, 4_000, 4_000, 4_000, 4_000, 4_000, 4_000]);
        var detector = new AdaptiveEnergyVoiceActivityDetector(CreateOptions(onsetFrames: 1, silenceFrames: 4));

        var intervals = await detector.DetectAsync(wave, progress: null, CancellationToken.None);

        Assert.Equal([new SpeechInterval(0, 10)], intervals);
    }

    [Fact]
    public async Task DetectAsync_EmptyPcm_ReportsBoundedProgressAndReturnsNoIntervals()
    {
        using var workspace = new TestWorkspace();
        var wave = WaveFixture.WriteRequiredPcmWave(workspace, "empty.wav");
        var detector = new AdaptiveEnergyVoiceActivityDetector(CreateOptions(onsetFrames: 1, silenceFrames: 1));
        var progress = new InlineProgress<double>();

        var intervals = await detector.DetectAsync(wave, progress, CancellationToken.None);

        Assert.Empty(intervals);
        Assert.Equal(0d, progress.Values[0]);
        Assert.Equal(1d, progress.Values[^1]);
        Assert.True(IsMonotonic(progress.Values));
    }

    [Fact]
    public async Task DetectAsync_RejectsWrongFormatAndTruncatedDataRange()
    {
        using var workspace = new TestWorkspace();
        var wave = WaveFixture.WriteRequiredPcmWave(workspace, "valid.wav", 0, 0, 0, 0);
        var detector = new AdaptiveEnergyVoiceActivityDetector(CreateOptions(onsetFrames: 1, silenceFrames: 1));
        var wrongFormat = wave with { Format = new PcmFormat(8_000, 1, 16, 2) };
        var truncatedRange = wave with { DataLength = wave.DataLength + PcmFormat.Required.BlockAlign };

        await Assert.ThrowsAsync<ArgumentException>(() => detector.DetectAsync(wrongFormat, progress: null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => detector.DetectAsync(truncatedRange, progress: null, CancellationToken.None));
    }

    [Fact]
    public async Task DetectAsync_ObservesCancellationDuringScan()
    {
        using var workspace = new TestWorkspace();
        var wave = WaveFixture.WriteRequiredPcmWave(workspace, "cancel.wav", Enumerable.Repeat((short)4_000, 32).ToArray());
        var detector = new AdaptiveEnergyVoiceActivityDetector(CreateOptions(onsetFrames: 1, silenceFrames: 4));
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detector.DetectAsync(
            wave,
            new CancellingProgress(cancellation),
            cancellation.Token));
    }

    [Theory]
    [InlineData(0, -60, -90, -20, 0.05, 12, 7, -45, -50)]
    [InlineData(4, -60, -20, -90, 0.05, 12, 7, -45, -50)]
    [InlineData(4, -60, -90, -20, 1.1, 12, 7, -45, -50)]
    [InlineData(4, -60, -90, -20, 0.05, 7, 7, -45, -50)]
    [InlineData(4, -60, -90, -20, 0.05, 12, 7, -51, -50)]
    public void Constructor_RejectsInvalidAdaptiveThresholdConfiguration(
        int frameSamples,
        double initialNoiseFloorDbfs,
        double minimumNoiseFloorDbfs,
        double maximumNoiseFloorDbfs,
        double noiseFloorAdaptation,
        double onMargin,
        double offMargin,
        double minimumOnThresholdDbfs,
        double minimumOffThresholdDbfs)
    {
        var options = CreateOptions(onsetFrames: 1, silenceFrames: 1) with
        {
            FrameSamples = frameSamples,
            InitialNoiseFloorDbfs = initialNoiseFloorDbfs,
            MinimumNoiseFloorDbfs = minimumNoiseFloorDbfs,
            MaximumNoiseFloorDbfs = maximumNoiseFloorDbfs,
            NoiseFloorAdaptation = noiseFloorAdaptation,
            OnThresholdAboveNoiseFloorDb = onMargin,
            OffThresholdAboveNoiseFloorDb = offMargin,
            MinimumOnThresholdDbfs = minimumOnThresholdDbfs,
            MinimumOffThresholdDbfs = minimumOffThresholdDbfs,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveEnergyVoiceActivityDetector(options));
    }

    private static AdaptiveEnergyVadOptions CreateOptions(int onsetFrames, int silenceFrames)
        => new()
        {
            FrameSamples = 4,
            InitialNoiseFloorDbfs = -60,
            MinimumNoiseFloorDbfs = -90,
            MaximumNoiseFloorDbfs = -20,
            NoiseFloorAdaptation = 0,
            OnThresholdAboveNoiseFloorDb = 10,
            OffThresholdAboveNoiseFloorDb = 5,
            MinimumOnThresholdDbfs = -80,
            MinimumOffThresholdDbfs = -85,
            Segmenter = new HysteresisSegmenterOptions
            {
                OnsetFrames = onsetFrames,
                SilenceFrames = silenceFrames,
                PreRollSamples = 0,
                PostRollSamples = 0,
                MinimumSpeechSamples = 1,
                MergeGapSamples = 0,
                MaximumSegmentSamples = 100,
                SplitSearchSamples = 10,
            },
        };

    private static bool IsMonotonic(List<double> values)
    {
        for (var index = 1; index < values.Count; index++)
        {
            if (values[index] < values[index - 1])
            {
                return false;
            }
        }

        return true;
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation) : IProgress<double>
    {
        public void Report(double value) => cancellation.Cancel();
    }
}
