using System.Buffers;
using WinBulkTranscript.Core.Domain;
using WinBulkTranscript.Core.Ports;

namespace WinBulkTranscript.Core.Audio;

/// <summary>Configures the dependency-free adaptive RMS energy voice activity detector.</summary>
public sealed record AdaptiveEnergyVadOptions
{
    /// <summary>Gets the nominal PCM sample count in one analysis frame.</summary>
    public int FrameSamples { get; init; } = 320;

    /// <summary>Gets the initial dBFS estimate used until confident silence updates the noise floor.</summary>
    public double InitialNoiseFloorDbfs { get; init; } = -60d;

    /// <summary>Gets the lowest allowed adaptive noise-floor estimate.</summary>
    public double MinimumNoiseFloorDbfs { get; init; } = -90d;

    /// <summary>Gets the highest allowed adaptive noise-floor estimate.</summary>
    public double MaximumNoiseFloorDbfs { get; init; } = -20d;

    /// <summary>Gets the per-frame fraction used to move the noise floor toward confident silence.</summary>
    public double NoiseFloorAdaptation { get; init; } = 0.05d;

    /// <summary>Gets the dB margin above the noise floor required to begin speech.</summary>
    public double OnThresholdAboveNoiseFloorDb { get; init; } = 12d;

    /// <summary>Gets the dB margin above the noise floor required to remain in speech.</summary>
    public double OffThresholdAboveNoiseFloorDb { get; init; } = 7d;

    /// <summary>Gets the absolute lower clamp for the speech-on threshold.</summary>
    public double MinimumOnThresholdDbfs { get; init; } = -45d;

    /// <summary>Gets the absolute lower clamp for the speech-off threshold.</summary>
    public double MinimumOffThresholdDbfs { get; init; } = -50d;

    /// <summary>Gets the deterministic segment-state timing rules.</summary>
    public HysteresisSegmenterOptions Segmenter { get; init; } = new();

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(FrameSamples, 0);
        ValidateDbfs(InitialNoiseFloorDbfs, nameof(InitialNoiseFloorDbfs));
        ValidateDbfs(MinimumNoiseFloorDbfs, nameof(MinimumNoiseFloorDbfs));
        ValidateDbfs(MaximumNoiseFloorDbfs, nameof(MaximumNoiseFloorDbfs));
        ValidateDbfs(MinimumOnThresholdDbfs, nameof(MinimumOnThresholdDbfs));
        ValidateDbfs(MinimumOffThresholdDbfs, nameof(MinimumOffThresholdDbfs));
        ValidateFinite(NoiseFloorAdaptation, nameof(NoiseFloorAdaptation));
        ValidateFinite(OnThresholdAboveNoiseFloorDb, nameof(OnThresholdAboveNoiseFloorDb));
        ValidateFinite(OffThresholdAboveNoiseFloorDb, nameof(OffThresholdAboveNoiseFloorDb));

        if (MinimumNoiseFloorDbfs > MaximumNoiseFloorDbfs)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumNoiseFloorDbfs), "The minimum noise floor cannot exceed the maximum noise floor.");
        }

        if (InitialNoiseFloorDbfs < MinimumNoiseFloorDbfs || InitialNoiseFloorDbfs > MaximumNoiseFloorDbfs)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialNoiseFloorDbfs), "The initial noise floor must be within the configured bounds.");
        }

        if (NoiseFloorAdaptation is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(NoiseFloorAdaptation), "Noise-floor adaptation must be between zero and one.");
        }

        if (OnThresholdAboveNoiseFloorDb <= OffThresholdAboveNoiseFloorDb)
        {
            throw new ArgumentOutOfRangeException(nameof(OnThresholdAboveNoiseFloorDb), "The on threshold margin must exceed the off threshold margin.");
        }

        if (MinimumOnThresholdDbfs < MinimumOffThresholdDbfs)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumOnThresholdDbfs), "The on threshold clamp must not be below the off threshold clamp.");
        }

        ArgumentNullException.ThrowIfNull(Segmenter);
        Segmenter.Validate();
    }

    private static void ValidateDbfs(double value, string parameterName)
    {
        ValidateFinite(value, parameterName);
        if (value > 0d)
        {
            throw new ArgumentOutOfRangeException(parameterName, "dBFS values cannot exceed full scale.");
        }
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, "The value must be finite.");
        }
    }
}

/// <summary>
/// Scans validated PCM16 WAVE data with adaptive energy thresholds and emits source-sample speech intervals.
/// </summary>
public sealed class AdaptiveEnergyVoiceActivityDetector : IVoiceActivityDetector
{
    private const int FramesPerRead = 128;
    private const int ProgressIntervalMilliseconds = 500;
    private readonly AdaptiveEnergyVadOptions _options;

    /// <summary>Initializes a detector with the production default tuning values.</summary>
    public AdaptiveEnergyVoiceActivityDetector()
        : this(null)
    {
    }

    /// <summary>Initializes a detector with explicit adaptive-energy and segmentation options.</summary>
    public AdaptiveEnergyVoiceActivityDetector(AdaptiveEnergyVadOptions? options)
    {
        _options = options ?? new AdaptiveEnergyVadOptions();
        _options.Validate();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SpeechInterval>> DetectAsync(
        PcmWaveFile waveFile,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(waveFile);
        cancellationToken.ThrowIfCancellationRequested();

        if (!waveFile.Format.IsRequired)
        {
            throw new ArgumentException("Voice activity detection requires 16 kHz, mono, signed PCM16 audio.", nameof(waveFile));
        }

        if (waveFile.DataOffset < 0 || waveFile.DataLength < 0 || waveFile.DataLength % waveFile.Format.BlockAlign != 0)
        {
            throw new ArgumentException("The supplied PCM data range is invalid.", nameof(waveFile));
        }

        var totalSamples = waveFile.SampleCount;
        var frameBytes = checked(_options.FrameSamples * waveFile.Format.BlockAlign);
        var requestedBufferLength = checked(frameBytes * FramesPerRead);
        var buffer = ArrayPool<byte>.Shared.Rent(requestedBufferLength);

        try
        {
            using var stream = waveFile.OpenRead();
            var dataEnd = AddChecked(waveFile.DataOffset, waveFile.DataLength);
            if (dataEnd > stream.Length)
            {
                throw new InvalidDataException("The PCM data chunk is truncated.");
            }

            stream.Position = waveFile.DataOffset;
            progress?.Report(0d);

            var segmenter = new HysteresisSpeechSegmenter(_options.Segmenter);
            var noiseFloorDbfs = _options.InitialNoiseFloorDbfs;
            var remainingBytes = waveFile.DataLength;
            var bufferedBytes = 0;
            var scannedSamples = 0L;
            var progressIntervalSamples = Math.Max(1L, waveFile.Format.SampleRate * ProgressIntervalMilliseconds / 1_000L);
            var nextProgressSample = progressIntervalSamples;

            while (remainingBytes > 0 || bufferedBytes > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (remainingBytes > 0)
                {
                    var availableBufferBytes = buffer.Length - bufferedBytes;
                    if (availableBufferBytes <= 0)
                    {
                        throw new InvalidOperationException("The VAD read buffer could not retain a partial frame.");
                    }

                    var requestedBytes = (int)Math.Min((long)availableBufferBytes, remainingBytes);
                    var readBytes = await stream.ReadAsync(buffer.AsMemory(bufferedBytes, requestedBytes), cancellationToken).ConfigureAwait(false);
                    if (readBytes == 0)
                    {
                        throw new EndOfStreamException("The PCM data chunk ended before its declared length.");
                    }

                    bufferedBytes += readBytes;
                    remainingBytes -= readBytes;
                }

                var processableBytes = remainingBytes == 0
                    ? bufferedBytes
                    : bufferedBytes - bufferedBytes % frameBytes;
                if ((processableBytes & 1) != 0)
                {
                    throw new InvalidDataException("The PCM data chunk ended between signed 16-bit samples.");
                }

                var bufferOffset = 0;
                while (bufferOffset < processableBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var currentFrameBytes = Math.Min(frameBytes, processableBytes - bufferOffset);
                    var energy = Pcm16FrameEnergyScorer.Score(buffer.AsSpan(bufferOffset, currentFrameBytes));
                    var onThresholdDbfs = Math.Max(
                        _options.MinimumOnThresholdDbfs,
                        noiseFloorDbfs + _options.OnThresholdAboveNoiseFloorDb);
                    var offThresholdDbfs = Math.Max(
                        _options.MinimumOffThresholdDbfs,
                        noiseFloorDbfs + _options.OffThresholdAboveNoiseFloorDb);
                    var frameSamples = currentFrameBytes / waveFile.Format.BlockAlign;
                    var frameStartSample = scannedSamples;
                    var frameEndSample = checked(frameStartSample + frameSamples);
                    var isAboveOnThreshold = energy.DecibelsFullScale >= onThresholdDbfs;
                    var isBelowOffThreshold = energy.DecibelsFullScale < offThresholdDbfs;

                    segmenter.ProcessFrame(new VadFrame(
                        frameStartSample,
                        frameEndSample,
                        energy.DecibelsFullScale,
                        isAboveOnThreshold,
                        isBelowOffThreshold));

                    if (!segmenter.IsInSpeech && isBelowOffThreshold)
                    {
                        noiseFloorDbfs = AdaptNoiseFloor(noiseFloorDbfs, energy.DecibelsFullScale);
                    }

                    scannedSamples = frameEndSample;
                    bufferOffset += currentFrameBytes;

                    if (scannedSamples >= nextProgressSample || scannedSamples == totalSamples)
                    {
                        progress?.Report((double)scannedSamples / totalSamples);
                        nextProgressSample = AddSaturating(nextProgressSample, progressIntervalSamples);
                    }
                }

                var remainingBufferedBytes = bufferedBytes - processableBytes;
                if (remainingBufferedBytes > 0)
                {
                    buffer.AsSpan(processableBytes, remainingBufferedBytes).CopyTo(buffer);
                }

                bufferedBytes = remainingBufferedBytes;
            }

            if (scannedSamples != totalSamples)
            {
                throw new InvalidDataException("PCM scanning did not consume the declared sample count.");
            }

            var intervals = segmenter.Complete(totalSamples);
            progress?.Report(1d);
            return intervals;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private double AdaptNoiseFloor(double currentNoiseFloorDbfs, double frameDbfs)
    {
        var observedDbfs = double.IsNegativeInfinity(frameDbfs) ? _options.MinimumNoiseFloorDbfs : frameDbfs;
        var adapted = currentNoiseFloorDbfs + (_options.NoiseFloorAdaptation * (observedDbfs - currentNoiseFloorDbfs));
        return Math.Clamp(adapted, _options.MinimumNoiseFloorDbfs, _options.MaximumNoiseFloorDbfs);
    }

    private static long AddChecked(long value, long increment)
    {
        try
        {
            return checked(value + increment);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("The PCM data range exceeds the supported file range.", nameof(value), exception);
        }
    }

    private static long AddSaturating(long value, long increment)
    {
        return value > long.MaxValue - increment ? long.MaxValue : value + increment;
    }
}
