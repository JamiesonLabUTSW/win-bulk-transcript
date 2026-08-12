# VAD tuning and evaluation record

Status: implementation baseline selected; representative-corpus measurements were recorded on x64 Windows from the fixed-seed `test-assets/synthetic` corpus using Microsoft Zira (`en-US`). The Phase 2 quality gate remains pending independently labeled acoustic speech boundaries: current manifest intervals delimit synthesized TTS chunks (including `SpeechAppendedSilence.Default`), not verified acoustic onset/offset.

The values below are the shipped defaults in `AdaptiveEnergyVadOptions` and `HysteresisSegmenterOptions`. They are deliberately recorded here so future corpus results change values by evidence rather than by an undocumented code edit.

| Setting | Production default | Timeline effect |
|---|---:|---|
| Analysis frame | 320 samples | 20 ms at 16 kHz |
| Initial / min / max noise floor | -60 / -90 / -20 dBFS | Adaptive baseline clamp |
| Noise-floor adaptation | 0.05 per confident-silence frame | Gradual noise tracking |
| On / off margin over noise | 12 / 7 dB | Hysteresis |
| Minimum on / off clamp | -45 / -50 dBFS | Reject codec-floor noise |
| Onset confirmation | 4 frames | 80 ms |
| End-of-speech confirmation | 25 frames | 500 ms |
| Pre-roll / post-roll | 3,200 / 3,200 samples | 200 / 200 ms |
| Minimum speech | 4,800 samples | 300 ms |
| Merge gap | 3,200 samples | 200 ms |
| Maximum segment | 400,000 samples | 25 s |
| Low-energy split search | 16,000 samples | trailing 1 s |
| Returned-interval split overlap | 0 samples | source intervals remain non-overlapping |

## Corpus evaluation protocol

1. Generate the fixed-seed corpus using [the generator](../../tools/WinBulkTranscript.CorpusGenerator/README.md) with `--retain-master-pcm`; retain `corpus-manifest.json`, `retained-master-pcm`, and their recorded voice/hash provenance.
2. Run the development-only [VAD evaluator](../../tools/WinBulkTranscript.VadEvaluator/README.md) on the hash-bound retained master PCM. It calls `AdaptiveEnergyVoiceActivityDetector` directly and produces coordinate-valid **synthesis-chunk** measurements only. The report must retain `phase2QualityGateStatus: "pending"`; do not use these values to pass the Phase 2 gate.

   ```powershell
   dotnet run --project tools/WinBulkTranscript.VadEvaluator -- `
     --manifest test-assets\synthetic\corpus-manifest.json `
     --pcm-root test-assets\synthetic\retained-master-pcm `
     --report artifacts\vad-evaluation\retained-master.json
   ```

3. Run the production extraction/VAD path with `--source both`. It hashes both byte-identical media inputs, decodes them through the production extractor, and automatically records a per-fixture flat/nested comparison of input hashes, decoded PCM hashes/sample counts, and VAD interval sequences. This report has no missed-speech, false-positive, recall, or boundary metrics.

   ```powershell
   dotnet run --project tools/WinBulkTranscript.VadEvaluator -- `
     --manifest test-assets\synthetic\corpus-manifest.json `
     --corpus-root test-assets\synthetic `
     --source both `
     --report artifacts\vad-evaluation\flat-nested-diagnostics.json
   ```

4. Retain both reports, including their actual `runtime.operatingSystemArchitecture` and `runtime.processArchitecture` fields. Investigate any false value in `flatNestedComparison.allPairsMatch`. The report's `detectedIntervalsAtConfiguredMaximumLength` is an observable maximum-length-segment proxy, not a claimed causal forced-split count.
5. Exercise the separate malformed/no-audio fixture matrix; those files are failure-path checks and are excluded from speech metrics.

## Future Phase 2 gate (pending acoustic ground truth)

These proposed targets are not currently valid pass/fail criteria. Before using them, derive or independently annotate acoustic speech onset/offset labels and re-baseline the measurements against those labels. The present retained-master report only measures behavior relative to synthesized chunk boundaries.

| Metric | Provisional target |
|---|---:|
| Manifest utterances overlapping a detected interval | at least 95% |
| Median absolute start/end boundary error | at most 250 ms |
| Detected speech outside all manifest intervals | at most 10% of detected duration |
| Timestamp regressions / overlapping returned intervals | 0 |
| EOF, short file, silent file, and max-split deterministic tests | all pass |

The synthetic corpus contains clean Windows TTS speech, so it establishes repeatability rather than real-world robustness. Once acoustic labels exist, record the same metrics for rights-cleared recordings with quiet, music/noise, clipped speech, and long pauses before changing defaults or promoting the product. If the agreed threshold cannot be met, follow the Phase 2 decision gate and choose a neural VAD dependency deliberately rather than silently relaxing the metric.

## Current evidence

- Deterministic scorer, segmenter, adaptive-detector, and interval-invariant tests pass in the Core suite.
- The evaluator implementation produces hash-bound retained-master synthesis-chunk measurements and encoded-media flat/nested diagnostics; neither completes the Phase 2 gate without acoustic ground truth.
- `artifacts\vad-evaluation\x64-retained-master.json` records 30 retained-master fixtures on x64 (`runtime.operatingSystemArchitecture` and `runtime.processArchitecture` are both `X64`). All 229 manifest utterances overlap a detected interval (100% recall); missed-speech is 8.8964%, false-positive speech is 1.2731% of detected duration, and no returned interval is at the configured maximum length.
- The same report has median absolute synthesized-chunk start/end deviations of 1,530/8,868 samples (95.625/554.25 ms). These values are observations, not a pass/fail result: an utterance can contain multiple VAD segments, and chunk ends can include `SpeechAppendedSilence.Default`. In particular, the 554.25 ms end value must not be used to retune the production VAD or to claim that the future 250 ms acoustic-boundary target passes or fails.
- `artifacts\vad-evaluation\x64-flat-nested-diagnostics.json` records the production extractor/VAD path for 60 inputs (30 flat/nested pairs). All 30 pairs have matching encoded-input hashes, decoded PCM hashes/sample counts, and returned VAD interval sequences (`allPairsMatch: true`); it records 0 maximum-length intervals. The aggregate decoded sample-count delta is +26,240 samples across the two layouts, which is diagnostic-only and is not compared to master-PCM timing metrics.
- Consequently, this document does **not** claim Phase 2 quality-gate completion. Independent acoustic onset/offset labels—and representative rights-cleared recordings beyond clean Windows TTS—are required before the existing Phase 2 acceptance criterion can be evaluated without changing it.
