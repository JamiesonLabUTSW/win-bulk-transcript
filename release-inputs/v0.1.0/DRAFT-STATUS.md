# WinBulkTranscript v0.1.0 dossier status

Status: **Draft preview dossier - not yet approved for release.**

This directory prepares the required shape and records facts already supported by repository evidence. Version 0.1.0 uses the preview policy: technical validation gaps may be accepted explicitly, while final licensing/provenance, source binding, checksums, and artifact verification remain mandatory. This draft is not ready to tag or distribute until its remaining mandatory inputs and risk-acceptance headers are finalized.

## Prefilled facts

- Intended release tag: `v0.1.0`
- Intended assets: `WinBulkTranscript-0.1.0-win-x64.zip` and `WinBulkTranscript-0.1.0-win-arm64.zip`
- Configured model: `nemotron-speech-streaming-en-0.6b-generic-cpu:3`
- Catalog artifact observed in retained Foundry metadata: `azureml://registries/azureml/models/nemotron-speech-streaming-en-0.6b-generic-cpu/versions/3`
- Preliminary x64 and ARM64 self-contained manifests declare `Microsoft.NETCore.App` version `10.0.11`.
- Partial local x64 implementation evidence is linked from the draft matrices.

## Release blockers

- Obtain legal approval for the exact model license/notices and replace `model-license.txt` in full.
- Confirm an immutable provenance source for the exact Foundry catalog artifact, then finalize `model-provenance.json`.
- Replace the draft runtime/framework notice sections with legally reviewed text based on fresh preliminary publishes from the tagged source candidate.
- For clean-machine, Mark-of-the-Web/SmartScreen, online/offline, manual UI, resilience, representative-acoustic, and ARM64 execution gaps: either complete the gate or document and explicitly accept the preview risk in each matrix.
- Resolve the final release date and support contact.

## Finalization order

1. Finalize `model-license.txt`, `model-provenance.json`, `release-notes.md`, and both runtime/framework notice files.
2. Compute each completed file's lowercase SHA-256.
3. Put the exact filenames and hashes into the matching matrix headers.
4. Resolve every `TBD`. Use only `Passed`, `Accepted risk`, or `Not applicable` in the applicable architecture column. Each non-passing status needs concrete rationale, and any accepted risk requires a named approver, date, and decision in the matrix header. This preview acceptance does not replace institutional disclosure/release approval.
5. Review and commit the completed dossier to the release candidate commit, then create `v0.1.0` on that exact commit.

Do not put credentials, private participant data, restricted evidence, or unapproved license text in this public-release input directory.
