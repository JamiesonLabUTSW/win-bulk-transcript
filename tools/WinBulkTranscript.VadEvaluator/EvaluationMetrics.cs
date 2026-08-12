namespace WinBulkTranscript.VadEvaluator;

/// <summary>A half-open sample interval used in the evaluator's JSON evidence.</summary>
internal readonly record struct SampleRange(long StartSample, long EndSample)
{
    /// <summary>Gets the interval duration in samples.</summary>
    public long LengthSamples => EndSample - StartSample;
}

/// <summary>Records the best overlapping detected interval for one expected utterance.</summary>
internal sealed record BoundaryMatch(
    int UtteranceIndex,
    SampleRange Expected,
    SampleRange? Detected,
    long OverlapSamples,
    long? StartErrorSamples,
    long? EndErrorSamples,
    long? AbsoluteStartErrorSamples,
    long? AbsoluteEndErrorSamples,
    bool IsWithinManifestTimingTolerance);

/// <summary>Per-fixture VAD measurements in source sample coordinates.</summary>
internal sealed record VadMetricResult(
    int ExpectedUtteranceCount,
    int MatchedUtteranceCount,
    int UnmatchedUtteranceCount,
    int DetectedIntervalCount,
    int DetectedIntervalsAtConfiguredMaximumLength,
    long ExpectedSpeechSamples,
    long DetectedSpeechSamples,
    long TruePositiveSamples,
    long MissedSpeechSamples,
    double MissedSpeechPercent,
    long FalsePositiveSamples,
    double FalsePositivePercentOfDetected,
    double FalsePositivePercentOfExpected,
    double UtteranceRecallPercent,
    int BothBoundariesWithinManifestTimingToleranceCount,
    double? MedianAbsoluteStartBoundaryErrorSamples,
    double? MedianAbsoluteEndBoundaryErrorSamples,
    IReadOnlyList<BoundaryMatch> BoundaryMatches);

/// <summary>Aggregate VAD measurements across all completed fixtures.</summary>
internal sealed record AggregateVadMetricResult(
    int FixtureCount,
    int ExpectedUtteranceCount,
    int MatchedUtteranceCount,
    int UnmatchedUtteranceCount,
    int DetectedIntervalCount,
    int DetectedIntervalsAtConfiguredMaximumLength,
    long ExpectedSpeechSamples,
    long DetectedSpeechSamples,
    long TruePositiveSamples,
    long MissedSpeechSamples,
    double MissedSpeechPercent,
    long FalsePositiveSamples,
    double FalsePositivePercentOfDetected,
    double FalsePositivePercentOfExpected,
    double UtteranceRecallPercent,
    int BothBoundariesWithinManifestTimingToleranceCount,
    double? MedianAbsoluteStartBoundaryErrorSamples,
    double? MedianAbsoluteEndBoundaryErrorSamples);

/// <summary>Calculates deterministic sample-overlap and boundary measurements for the production VAD.</summary>
internal static class VadMetricCalculator
{
    /// <summary>Validates chronological non-overlapping intervals against a known timeline length.</summary>
    public static void ValidateIntervals(
        IReadOnlyList<SampleRange> intervals,
        long timelineSamples,
        string description)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentOutOfRangeException.ThrowIfNegative(timelineSamples);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var previousEnd = 0L;
        for (var index = 0; index < intervals.Count; index++)
        {
            var interval = intervals[index];
            if (interval.StartSample < 0 || interval.EndSample <= interval.StartSample || interval.EndSample > timelineSamples)
            {
                throw new InvalidDataException(
                    $"{description} interval {index} is outside [0, {timelineSamples}) or has no positive duration.");
            }

            if (index > 0 && interval.StartSample < previousEnd)
            {
                throw new InvalidDataException($"{description} intervals overlap or regress at interval {index}.");
            }

            previousEnd = interval.EndSample;
        }
    }

    /// <summary>Computes per-fixture metrics after both interval sequences have been validated.</summary>
    public static VadMetricResult Evaluate(
        IReadOnlyList<SampleRange> expectedIntervals,
        IReadOnlyList<SampleRange> detectedIntervals,
        long expectedTimelineSamples,
        long detectedTimelineSamples,
        int sampleRate,
        int timingToleranceMilliseconds,
        long maximumSegmentSamples)
    {
        ArgumentNullException.ThrowIfNull(expectedIntervals);
        ArgumentNullException.ThrowIfNull(detectedIntervals);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedTimelineSamples);
        ArgumentOutOfRangeException.ThrowIfNegative(detectedTimelineSamples);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(timingToleranceMilliseconds);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumSegmentSamples, 0);

        ValidateIntervals(expectedIntervals, expectedTimelineSamples, "Manifest speech");
        ValidateIntervals(detectedIntervals, detectedTimelineSamples, "Production VAD");

        var expectedSpeechSamples = SumLengths(expectedIntervals);
        var detectedSpeechSamples = SumLengths(detectedIntervals);
        var truePositiveSamples = ComputeOverlapSamples(expectedIntervals, detectedIntervals);
        var missedSpeechSamples = checked(expectedSpeechSamples - truePositiveSamples);
        var falsePositiveSamples = checked(detectedSpeechSamples - truePositiveSamples);
        var timingToleranceSamples = checked((long)timingToleranceMilliseconds * sampleRate / 1_000L);
        var matches = CreateBoundaryMatches(expectedIntervals, detectedIntervals, timingToleranceSamples);
        var matchedUtteranceCount = matches.Count(match => match.Detected is not null);
        var bothBoundariesWithinTolerance = matches.Count(match => match.IsWithinManifestTimingTolerance);

        return new VadMetricResult(
            expectedIntervals.Count,
            matchedUtteranceCount,
            expectedIntervals.Count - matchedUtteranceCount,
            detectedIntervals.Count,
            detectedIntervals.Count(interval => interval.LengthSamples == maximumSegmentSamples),
            expectedSpeechSamples,
            detectedSpeechSamples,
            truePositiveSamples,
            missedSpeechSamples,
            Percentage(missedSpeechSamples, expectedSpeechSamples),
            falsePositiveSamples,
            Percentage(falsePositiveSamples, detectedSpeechSamples),
            Percentage(falsePositiveSamples, expectedSpeechSamples),
            Percentage(matchedUtteranceCount, expectedIntervals.Count),
            bothBoundariesWithinTolerance,
            Median(matches.Where(match => match.AbsoluteStartErrorSamples is not null).Select(match => match.AbsoluteStartErrorSamples!.Value)),
            Median(matches.Where(match => match.AbsoluteEndErrorSamples is not null).Select(match => match.AbsoluteEndErrorSamples!.Value)),
            matches);
    }

    /// <summary>Combines completed fixture metrics without averaging percentages of unequal durations.</summary>
    public static AggregateVadMetricResult Aggregate(IReadOnlyList<VadMetricResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var expectedUtteranceCount = 0;
        var matchedUtteranceCount = 0;
        var detectedIntervalCount = 0;
        var atMaximumCount = 0;
        var expectedSpeechSamples = 0L;
        var detectedSpeechSamples = 0L;
        var truePositiveSamples = 0L;
        var missedSpeechSamples = 0L;
        var falsePositiveSamples = 0L;
        var bothBoundariesWithinTolerance = 0;
        var startErrors = new List<long>();
        var endErrors = new List<long>();

        foreach (var result in results)
        {
            expectedUtteranceCount = checked(expectedUtteranceCount + result.ExpectedUtteranceCount);
            matchedUtteranceCount = checked(matchedUtteranceCount + result.MatchedUtteranceCount);
            detectedIntervalCount = checked(detectedIntervalCount + result.DetectedIntervalCount);
            atMaximumCount = checked(atMaximumCount + result.DetectedIntervalsAtConfiguredMaximumLength);
            expectedSpeechSamples = checked(expectedSpeechSamples + result.ExpectedSpeechSamples);
            detectedSpeechSamples = checked(detectedSpeechSamples + result.DetectedSpeechSamples);
            truePositiveSamples = checked(truePositiveSamples + result.TruePositiveSamples);
            missedSpeechSamples = checked(missedSpeechSamples + result.MissedSpeechSamples);
            falsePositiveSamples = checked(falsePositiveSamples + result.FalsePositiveSamples);
            bothBoundariesWithinTolerance = checked(bothBoundariesWithinTolerance + result.BothBoundariesWithinManifestTimingToleranceCount);

            foreach (var match in result.BoundaryMatches)
            {
                if (match.AbsoluteStartErrorSamples is long startError)
                {
                    startErrors.Add(startError);
                }

                if (match.AbsoluteEndErrorSamples is long endError)
                {
                    endErrors.Add(endError);
                }
            }
        }

        return new AggregateVadMetricResult(
            results.Count,
            expectedUtteranceCount,
            matchedUtteranceCount,
            expectedUtteranceCount - matchedUtteranceCount,
            detectedIntervalCount,
            atMaximumCount,
            expectedSpeechSamples,
            detectedSpeechSamples,
            truePositiveSamples,
            missedSpeechSamples,
            Percentage(missedSpeechSamples, expectedSpeechSamples),
            falsePositiveSamples,
            Percentage(falsePositiveSamples, detectedSpeechSamples),
            Percentage(falsePositiveSamples, expectedSpeechSamples),
            Percentage(matchedUtteranceCount, expectedUtteranceCount),
            bothBoundariesWithinTolerance,
            Median(startErrors),
            Median(endErrors));
    }

    private static long SumLengths(IReadOnlyList<SampleRange> intervals)
    {
        var total = 0L;
        foreach (var interval in intervals)
        {
            total = checked(total + interval.LengthSamples);
        }

        return total;
    }

    private static long ComputeOverlapSamples(
        IReadOnlyList<SampleRange> expectedIntervals,
        IReadOnlyList<SampleRange> detectedIntervals)
    {
        var expectedIndex = 0;
        var detectedIndex = 0;
        var total = 0L;

        while (expectedIndex < expectedIntervals.Count && detectedIndex < detectedIntervals.Count)
        {
            var expected = expectedIntervals[expectedIndex];
            var detected = detectedIntervals[detectedIndex];
            var overlapStart = Math.Max(expected.StartSample, detected.StartSample);
            var overlapEnd = Math.Min(expected.EndSample, detected.EndSample);
            if (overlapEnd > overlapStart)
            {
                total = checked(total + overlapEnd - overlapStart);
            }

            if (expected.EndSample <= detected.EndSample)
            {
                expectedIndex++;
            }
            else
            {
                detectedIndex++;
            }
        }

        return total;
    }

    private static List<BoundaryMatch> CreateBoundaryMatches(
        IReadOnlyList<SampleRange> expectedIntervals,
        IReadOnlyList<SampleRange> detectedIntervals,
        long timingToleranceSamples)
    {
        var matches = new List<BoundaryMatch>(expectedIntervals.Count);
        for (var expectedIndex = 0; expectedIndex < expectedIntervals.Count; expectedIndex++)
        {
            var expected = expectedIntervals[expectedIndex];
            SampleRange? bestDetected = null;
            var bestOverlap = 0L;

            foreach (var detected in detectedIntervals)
            {
                if (detected.StartSample >= expected.EndSample)
                {
                    break;
                }

                if (detected.EndSample <= expected.StartSample)
                {
                    continue;
                }

                var overlap = Math.Min(expected.EndSample, detected.EndSample) - Math.Max(expected.StartSample, detected.StartSample);
                if (overlap > bestOverlap
                    || (overlap == bestOverlap && bestDetected is not null && IsEarlier(detected, bestDetected.Value)))
                {
                    bestDetected = detected;
                    bestOverlap = overlap;
                }
            }

            if (bestDetected is null)
            {
                matches.Add(new BoundaryMatch(
                    expectedIndex + 1,
                    expected,
                    null,
                    0,
                    null,
                    null,
                    null,
                    null,
                    false));
                continue;
            }

            var startError = bestDetected.Value.StartSample - expected.StartSample;
            var endError = bestDetected.Value.EndSample - expected.EndSample;
            var absoluteStartError = Math.Abs(startError);
            var absoluteEndError = Math.Abs(endError);
            matches.Add(new BoundaryMatch(
                expectedIndex + 1,
                expected,
                bestDetected,
                bestOverlap,
                startError,
                endError,
                absoluteStartError,
                absoluteEndError,
                absoluteStartError <= timingToleranceSamples && absoluteEndError <= timingToleranceSamples));
        }

        return matches;
    }

    private static bool IsEarlier(SampleRange candidate, SampleRange current)
    {
        return candidate.StartSample < current.StartSample
            || (candidate.StartSample == current.StartSample && candidate.EndSample < current.EndSample);
    }

    private static double Percentage(long portion, long total)
    {
        return total == 0 ? 0d : portion * 100d / total;
    }

    private static double Percentage(int portion, int total)
    {
        return total == 0 ? 0d : portion * 100d / total;
    }

    private static double? Median(IEnumerable<long> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] / 2d) + (ordered[middle] / 2d);
    }
}
