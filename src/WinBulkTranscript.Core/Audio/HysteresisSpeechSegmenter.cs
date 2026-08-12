using WinBulkTranscript.Core.Domain;

namespace WinBulkTranscript.Core.Audio;

/// <summary>Configures deterministic hysteresis, padding, merging, and maximum-length behavior.</summary>
public sealed record HysteresisSegmenterOptions
{
    /// <summary>Gets the number of consecutive on-threshold frames required to begin speech.</summary>
    public int OnsetFrames { get; init; } = 4;

    /// <summary>Gets the number of consecutive below-off-threshold frames required to end speech.</summary>
    public int SilenceFrames { get; init; } = 25;

    /// <summary>Gets the number of samples included before a confirmed speech onset.</summary>
    public long PreRollSamples { get; init; } = 3_200;

    /// <summary>Gets the number of samples included after the last non-silent frame.</summary>
    public long PostRollSamples { get; init; } = 3_200;

    /// <summary>Gets the unpadded speech duration required to keep a naturally closed segment.</summary>
    public long MinimumSpeechSamples { get; init; } = 4_800;

    /// <summary>Gets the largest gap between output intervals that may be merged.</summary>
    public long MergeGapSamples { get; init; } = 3_200;

    /// <summary>Gets the largest output interval duration before a continuing segment is split.</summary>
    public long MaximumSegmentSamples { get; init; } = 400_000;

    /// <summary>Gets the trailing search window used to choose a low-energy maximum-length split boundary.</summary>
    public long SplitSearchSamples { get; init; } = 16_000;

    /// <summary>
    /// Gets optional analysis overlap when restarting after a forced split. Returned source intervals remain non-overlapping.
    /// </summary>
    public long SplitOverlapSamples { get; init; }

    internal void Validate()
    {
        if (OnsetFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(OnsetFrames), "At least one onset frame is required.");
        }

        if (SilenceFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SilenceFrames), "At least one silence frame is required.");
        }

        if (PreRollSamples < 0 || PostRollSamples < 0 || MinimumSpeechSamples < 0 || MergeGapSamples < 0 || SplitSearchSamples < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PreRollSamples), "Sample durations cannot be negative.");
        }

        if (MaximumSegmentSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSegmentSamples), "The maximum segment duration must be positive.");
        }

        if (SplitOverlapSamples < 0 || SplitOverlapSamples >= MaximumSegmentSamples)
        {
            throw new ArgumentOutOfRangeException(nameof(SplitOverlapSamples), "Split overlap must be non-negative and shorter than the maximum segment duration.");
        }
    }
}

/// <summary>Describes one contiguous scored frame supplied to <see cref="HysteresisSpeechSegmenter"/>.</summary>
public readonly record struct VadFrame(
    long StartSample,
    long EndSample,
    double DecibelsFullScale,
    bool IsAboveOnThreshold,
    bool IsBelowOffThreshold)
{
    /// <summary>Gets the frame duration in source samples.</summary>
    public long LengthSamples => EndSample - StartSample;
}

/// <summary>
/// Converts thresholded frame scores into deterministic, padded source-sample speech intervals.
/// </summary>
public sealed class HysteresisSpeechSegmenter
{
    private readonly HysteresisSegmenterOptions _options;
    private readonly List<SpeechInterval> _segments = [];
    private readonly List<SplitCandidate> _splitCandidates = [];

    private long _expectedNextSample;
    private int _onsetFrameCount;
    private long _onsetStartSample;
    private bool _inSpeech;
    private long _speechCoreStartSample;
    private long _speechOutputStartSample;
    private long _lastNonSilentEndSample;
    private int _silenceFrameCount;
    private bool _currentSegmentFollowsForcedSplit;
    private bool _completed;
    private IReadOnlyList<SpeechInterval>? _completedSegments;

    /// <summary>Initializes a segmenter with the supplied timing options.</summary>
    public HysteresisSpeechSegmenter(HysteresisSegmenterOptions? options = null)
    {
        _options = options ?? new HysteresisSegmenterOptions();
        _options.Validate();
    }

    /// <summary>Gets whether onset has been confirmed and a speech segment is currently open.</summary>
    public bool IsInSpeech => _inSpeech;

    /// <summary>
    /// Adds one contiguous frame. Frames must cover source samples in order, without gaps or overlaps.
    /// </summary>
    /// <param name="frame">The energy score and threshold classifications for one frame.</param>
    public void ProcessFrame(VadFrame frame)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The segmenter has already been completed.");
        }

        ValidateFrame(frame);

        if (!_inSpeech)
        {
            ProcessPotentialOnset(frame);
            return;
        }

        ProcessOpenSpeech(frame);
    }

    /// <summary>
    /// Flushes an open segment at end of file and returns the immutable interval sequence.
    /// </summary>
    /// <param name="totalSamples">The source PCM sample count represented by the processed frames.</param>
    /// <returns>Chronological speech intervals expressed as half-open sample ranges.</returns>
    public IReadOnlyList<SpeechInterval> Complete(long totalSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalSamples);

        if (_completed)
        {
            if (totalSamples != _expectedNextSample)
            {
                throw new InvalidOperationException("Completion sample count cannot change after completion.");
            }

            return _completedSegments!;
        }

        if (totalSamples != _expectedNextSample)
        {
            throw new ArgumentException("Processed frames must exactly cover the supplied source sample count.", nameof(totalSamples));
        }

        if (_inSpeech)
        {
            SplitUntilWithinMaximum(totalSamples);
            CloseOpenSpeech(totalSamples);
        }

        _completed = true;
        _completedSegments = _segments.Count == 0 ? Array.Empty<SpeechInterval>() : _segments.ToArray();
        return _completedSegments;
    }

    private void ProcessPotentialOnset(VadFrame frame)
    {
        if (!frame.IsAboveOnThreshold)
        {
            _onsetFrameCount = 0;
            _splitCandidates.Clear();
            return;
        }

        if (_onsetFrameCount == 0)
        {
            _onsetStartSample = frame.StartSample;
            _splitCandidates.Clear();
        }

        _onsetFrameCount++;
        AddSplitCandidate(frame);

        if (_onsetFrameCount < _options.OnsetFrames)
        {
            return;
        }

        _inSpeech = true;
        _speechCoreStartSample = _onsetStartSample;
        _speechOutputStartSample = Math.Max(0L, _speechCoreStartSample - _options.PreRollSamples);
        _lastNonSilentEndSample = frame.EndSample;
        _silenceFrameCount = 0;
        _currentSegmentFollowsForcedSplit = false;
        _onsetFrameCount = 0;
        SplitUntilWithinMaximum(frame.EndSample);
    }

    private void ProcessOpenSpeech(VadFrame frame)
    {
        AddSplitCandidate(frame);

        if (frame.IsBelowOffThreshold)
        {
            _silenceFrameCount++;
        }
        else
        {
            _silenceFrameCount = 0;
            _lastNonSilentEndSample = frame.EndSample;
        }

        SplitUntilWithinMaximum(frame.EndSample);

        if (_silenceFrameCount >= _options.SilenceFrames)
        {
            CloseOpenSpeech(frame.EndSample);
        }
    }

    private void CloseOpenSpeech(long availableEndSample)
    {
        var rawSpeechDuration = _lastNonSilentEndSample - _speechCoreStartSample;
        var outputEnd = Math.Min(
            Math.Min(availableEndSample, AddSaturating(_lastNonSilentEndSample, _options.PostRollSamples)),
            AddSaturating(_speechOutputStartSample, _options.MaximumSegmentSamples));
        var interval = SpeechInterval.Clamp(_speechOutputStartSample, outputEnd, availableEndSample);

        if (interval.IsValid && (rawSpeechDuration >= _options.MinimumSpeechSamples || _currentSegmentFollowsForcedSplit))
        {
            AddNaturalInterval(interval, !_currentSegmentFollowsForcedSplit);
        }

        ResetOpenSpeech();
    }

    private void SplitOpenSpeech()
    {
        var limit = AddSaturating(_speechOutputStartSample, _options.MaximumSegmentSamples);
        var splitEnd = ChooseSplitBoundary(limit);
        if (splitEnd <= _speechOutputStartSample)
        {
            splitEnd = limit;
        }

        AddForcedInterval(new SpeechInterval(_speechOutputStartSample, splitEnd));

        var restartSample = Math.Max(_speechOutputStartSample, splitEnd - _options.SplitOverlapSamples);
        _speechCoreStartSample = restartSample;
        _speechOutputStartSample = restartSample;
        _currentSegmentFollowsForcedSplit = true;
        _splitCandidates.Clear();
    }

    private void SplitUntilWithinMaximum(long availableEndSample)
    {
        var effectiveOutputEnd = Math.Min(
            availableEndSample,
            AddSaturating(_lastNonSilentEndSample, _options.PostRollSamples));
        while (effectiveOutputEnd > AddSaturating(_speechOutputStartSample, _options.MaximumSegmentSamples))
        {
            SplitOpenSpeech();
        }
    }

    private long ChooseSplitBoundary(long limit)
    {
        var searchStart = limit > _options.SplitSearchSamples ? limit - _options.SplitSearchSamples : 0L;
        var foundCandidate = false;
        var bestEnergy = double.PositiveInfinity;
        var bestBoundary = limit;

        foreach (var candidate in _splitCandidates)
        {
            var boundary = candidate.StartSample;
            if (boundary <= _speechOutputStartSample || boundary > limit || boundary < searchStart)
            {
                continue;
            }

            if (!foundCandidate || candidate.DecibelsFullScale < bestEnergy || (candidate.DecibelsFullScale == bestEnergy && boundary > bestBoundary))
            {
                foundCandidate = true;
                bestEnergy = candidate.DecibelsFullScale;
                bestBoundary = boundary;
            }
        }

        return bestBoundary;
    }

    private void AddNaturalInterval(SpeechInterval interval, bool allowMerge)
    {
        if (_segments.Count == 0)
        {
            _segments.Add(interval);
            return;
        }

        var previous = _segments[^1];
        if (allowMerge && CanMerge(previous, interval))
        {
            var merged = new SpeechInterval(previous.StartSample, Math.Max(previous.EndSample, interval.EndSample));
            if (merged.LengthSamples <= _options.MaximumSegmentSamples)
            {
                _segments[^1] = merged;
                return;
            }
        }

        if (interval.StartSample < previous.EndSample)
        {
            interval = new SpeechInterval(previous.EndSample, interval.EndSample);
        }

        if (interval.IsValid)
        {
            _segments.Add(interval);
        }
    }

    private void AddForcedInterval(SpeechInterval interval)
    {
        if (_segments.Count > 0 && interval.StartSample < _segments[^1].EndSample)
        {
            interval = new SpeechInterval(_segments[^1].EndSample, interval.EndSample);
        }

        if (!interval.IsValid)
        {
            throw new InvalidOperationException("A maximum-duration split produced an invalid interval.");
        }

        _segments.Add(interval);
    }

    private bool CanMerge(SpeechInterval previous, SpeechInterval current)
    {
        if (current.StartSample <= previous.EndSample)
        {
            return true;
        }

        return current.StartSample - previous.EndSample <= _options.MergeGapSamples;
    }

    private void AddSplitCandidate(VadFrame frame)
    {
        _splitCandidates.Add(new SplitCandidate(frame.StartSample, frame.DecibelsFullScale));

        var cutoff = frame.StartSample > _options.SplitSearchSamples
            ? frame.StartSample - _options.SplitSearchSamples
            : 0L;
        var removeCount = 0;
        while (removeCount < _splitCandidates.Count && _splitCandidates[removeCount].StartSample < cutoff)
        {
            removeCount++;
        }

        if (removeCount > 0)
        {
            _splitCandidates.RemoveRange(0, removeCount);
        }
    }

    private void ResetOpenSpeech()
    {
        _inSpeech = false;
        _onsetFrameCount = 0;
        _silenceFrameCount = 0;
        _currentSegmentFollowsForcedSplit = false;
        _splitCandidates.Clear();
    }

    private void ValidateFrame(VadFrame frame)
    {
        if (frame.StartSample != _expectedNextSample)
        {
            throw new ArgumentException("Frames must be contiguous and ordered by source sample.", nameof(frame));
        }

        if (frame.EndSample <= frame.StartSample)
        {
            throw new ArgumentException("Frames must have a positive duration.", nameof(frame));
        }

        if (double.IsNaN(frame.DecibelsFullScale) || double.IsPositiveInfinity(frame.DecibelsFullScale))
        {
            throw new ArgumentException("Frame energy must be finite or negative infinity.", nameof(frame));
        }

        _expectedNextSample = frame.EndSample;
    }

    private static long AddSaturating(long value, long increment)
    {
        return value > long.MaxValue - increment ? long.MaxValue : value + increment;
    }

    private readonly record struct SplitCandidate(long StartSample, double DecibelsFullScale);
}
