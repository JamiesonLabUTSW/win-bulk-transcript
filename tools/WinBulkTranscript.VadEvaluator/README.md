# VAD corpus evaluator

This development-only Windows tool runs the production default `AdaptiveEnergyVoiceActivityDetector` in two deliberate evidence modes. It is deliberately outside `WinBulkTranscript.sln`, like the corpus generator and media integration probe: Core unit tests cannot prove Windows media decoding or encoded-media timing behavior.

No synthetic MP4 corpus or retained WAV corpus is committed to this repository. A missing input is an error, never a passing measurement.

## Evidence modes

`--pcm-root` is the **quality-measurement mode**. It finds exactly one `fixture-###.wav` below the supplied directory for every manifest fixture, validates it through the production WAV reader, and verifies both its PCM data SHA-256 and sample count against the manifest. Only then does it calculate missed-speech, false-positive, recall, and boundary metrics, because expected and detected intervals share the retained master PCM coordinate system.

The manifest intervals delimit assembled Windows TTS synthesis chunks, including any configured `SpeechAppendedSilence.Default`; they are not independently derived acoustic speech onset/offset labels. Hash binding makes the retained-master metrics coordinate-valid against those chunks, but the report records `phase2QualityGateStatus: "pending"`: these exploratory metrics must not pass or complete the Phase 2 quality gate.

`--corpus-root` is the **encoded-media diagnostic mode**. It selects each manifest-referenced flat or nested MP4, verifies its SHA-256, calls the production [`WindowsMediaAudioExtractor`](../../src/WinBulkTranscript.App/Media/WindowsMediaAudioExtractor.cs), then calls the production adaptive-energy VAD on the temporary decoded PCM WAV. It reports decoded PCM hashes, sample counts, and raw intervals, but does not intersect or score decoded intervals against master-PCM expectations. AAC delay, trim, and decode rounding make those distinct timelines.

Use `--source both` to process the byte-identical flat/nested pair together. The report then contains an automatic per-fixture comparison of encoded-input hashes, decoded PCM hashes/sample counts, and returned VAD interval sequences.

Generate the hash-bound master WAV evidence with the corpus generator's `--retain-master-pcm` option. Ordinary generation still removes its `.working` transcode WAVs, and a separately retained decoded WAV cannot be used as a quality-measurement input because it will not match the manifest's master PCM hash.

## Run it

First generate the ignored corpus on a Windows 11 machine with an explicitly recorded voice:

```powershell
dotnet run --project tools/WinBulkTranscript.CorpusGenerator -- `
  --voice-id '<installed voice ID>' `
  --seed 20260806 `
  --retain-master-pcm
```

Run the hash-bound retained-master measurement first:

```powershell
dotnet run --project tools/WinBulkTranscript.VadEvaluator -- `
  --manifest test-assets\synthetic\corpus-manifest.json `
  --pcm-root test-assets\synthetic\retained-master-pcm `
  --report artifacts\vad-evaluation\retained-master.json
```

Then run the production media diagnostic and automatic flat/nested comparison:

```powershell
dotnet run --project tools/WinBulkTranscript.VadEvaluator -- `
  --manifest test-assets\synthetic\corpus-manifest.json `
  --corpus-root test-assets\synthetic `
  --source both `
  --report artifacts\vad-evaluation\flat-nested-diagnostics.json
```

The evaluator refuses to replace a report unless `--overwrite` is supplied. Reports must be outside the corpus and retained-PCM roots, and may not replace the manifest, an evaluated input, or a manifest-referenced MP4/VTT artifact.

## Report contents

The JSON report contains no run timestamp, wall-clock duration, absolute input path, or host name. It deliberately records the actual OS architecture and process architecture so evidence is not implicitly labeled x64 or ARM64 by the report filename. Fixtures are evaluated in ordinal fixture-ID order, and source paths are recorded relative to their supplied root.

For every fixture the report records manifest and input SHA-256 provenance, the manifest master-PCM data SHA-256, evaluated PCM format/data hash/sample count, and raw expected and detected half-open intervals.

In retained-master mode, `qualityMetrics` and `qualityMetricsAggregate` contain expected, detected, true-positive, missed-speech, and false-positive totals; percentage/recall results; deterministic best-overlap boundary matches; tolerance counts; and `detectedIntervalsAtConfiguredMaximumLength`. The top-level limitation marks the expected intervals as `synthesizedTtsChunkBounds` and the Phase 2 gate as pending.

In encoded-media mode, `qualityMetrics` and `qualityMetricsAggregate` are `null`. `encodedMediaDiagnostics` instead records decoded sample-count deltas, detected-interval counts, maximum-length interval counts, and raw decoded intervals. Its expected master intervals are provenance/context only, never a metric operand. When invoked with `--source both`, `flatNestedComparison` automatically records whether every pair matches at each diagnostic level.

The last count is intentionally not labelled “forced split count.” The VAD API returns intervals, not causal split events, so an interval exactly equal to the configured maximum can be observed but cannot prove why it was emitted. If the production VAD returns an invalid, regressing, overlapping, or out-of-range interval, the evaluator fails instead of writing a misleading successful report.

Expected intervals always use the manifest master-PCM coordinate system. The [VAD tuning record](../../docs/validation/vad-tuning.md) keeps its Phase 2 gate pending until independently labeled acoustic boundaries exist. Use encoded-media reports to diagnose production extraction and compare byte-identical flat/nested inputs, not to infer quality percentages or boundary error.

## Evidence limits

This evaluator measures a controlled Windows TTS corpus. It does not prove acoustic-boundary accuracy, real-world VAD robustness in noise, music, clipped speech, or conversational overlap; ASR quality; coordinator behavior; VTT writing; or a completed Phase 2 gate. Retain generated input provenance and the report's recorded runtime architecture alongside any threshold decision.
