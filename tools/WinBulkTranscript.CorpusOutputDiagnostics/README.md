# Corpus-output diagnostics

This development-only .NET 10 tool compares an existing workflow VTT output tree with the checked-in synthetic-corpus manifest. It does not invoke Foundry, transcode media, write outputs, or change any quality criterion.

It verifies the selected source MP4 hashes, that every expected VTT agrees with its manifest utterance text/timestamps, input-to-output mapping, UTF-8 decoding, and the strict WebVTT structure emitted by the production formatter. It then records two informational measurements:

- normalized token edit distance between each fixture's manifest text and the concatenated output cue text; and
- whether every produced cue overlaps a manifest utterance and every manifest utterance overlaps a produced cue.

The implementation plan defines no text score or cue-timing tolerance. The tool therefore has no quality-pass result and must not be used to turn these measurements into a new acceptance threshold.

## Run it

Run it after a successful `WinBulkTranscript.WorkflowIntegrationProbe` corpus workflow. Use the same source layout that produced the VTT tree.

```powershell
dotnet run --project tools/WinBulkTranscript.CorpusOutputDiagnostics -- `
  --manifest test-assets\synthetic\corpus-manifest.json `
  --corpus-root test-assets\synthetic `
  --source nested `
  --output-root artifacts\workflow-probe\x64-nested-output `
  --report artifacts\workflow-probe\x64-nested-corpus-diagnostics.json
```

The report path must be outside both the corpus and output roots. It is written with an owned temporary file followed by an atomic move, and it refuses to replace an existing report unless `--overwrite` is supplied.

## Evidence scope

The JSON report records the manifest SHA-256, selected MP4 SHA-256 values, expected-VTT SHA-256 values, and produced-VTT SHA-256 values, plus per-fixture and aggregate measurements. It is evidence for repeatable corpus-output diagnostics only. The separate workflow-probe report remains the evidence for production extractor/VAD/Foundry/coordinator/writer execution, output preservation, and cancellation.

The synthetic corpus uses TTS source material and master-PCM chunk bounds. This tool does not supply independent acoustic labels, representative real recordings, an agreed VAD threshold, or an agreed ASR text/cue threshold.
