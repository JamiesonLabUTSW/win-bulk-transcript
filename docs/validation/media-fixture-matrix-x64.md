# Generated media fixture matrix — x64 evidence

Status: **x64 extraction and final-output evidence is green for a locally generated candidate matrix; it is not ARM64 or legal-release completion.** This record preserves the observed result without changing the matrix in [media-fixture-matrix.md](../../test-assets/media-fixture-matrix.md).

## Generation and provenance

On 2026-08-11, `WinBulkTranscript.CorpusGenerator --media-fixture-matrix` created the following fresh local artifacts under `artifacts/media-fixture-matrix/x64-zero-reference-20260811` using the explicitly selected Microsoft Zira voice. The adjacent [provenance sidecar](../../artifacts/media-fixture-matrix/x64-zero-reference-20260811/media-fixture-matrix.provenance.json) records the host, voice, construction method, SHA-256, and empty-track post-mutation proof for every binary.

| Fixture | SHA-256 | Construction / provenance |
|---|---|---|
| `malformed-truncated.mp4` | `01F6C3975902559A9C7A200A718959BFC9DADE113C8C6C0CD93F24EE9E966DD8` | Fixed ISO-BMFF `ftyp` header followed by an `mdat` box with a deliberately truncated payload. |
| `no-audio-video-only.mp4` | `424553F69A911D0CBC3FE171C7C62166A05CC5CF380807DEB49C0909519AF216` | Native `MediaComposition` solid-colour MP4 rendered with `Audio = null`; Windows inspection recorded one video track and zero audio tracks before probe. |
| `empty-audio-track.mp4` | `F88394C6187E2813DF5DA6FB8D5A08EC95282A339D726F382D915F816F0CDBFB` | Native silent AAC audio-only source retaining one audio description, with movie/track/media durations plus all sample references deterministically zeroed and post-write validated. |
| `unsupported-audio-codec.mp4` | `F24077A50FE09042BA2F757253A456C75B5EEBE46FB93EADB85801C6124A87F2` | Byte-preserving copy of the native AAC control, except its `stsd` audio sample-entry four-character code changes from `mp4a` to `wbtx`. |
| `valid-short-control.mp4` | `C983736012469127FE8D688753AA374FD98AFB55B1A9A00611AEA8C1B20A0E6F` | One locally synthesized spoken utterance encoded as native audio-only AAC/MP4. |

No external media, image, or codec sample was used. That is reproducible technical provenance, but it is **not itself a legal determination** that generated Windows TTS output may be redistributed. Before calling the artifacts “rights-cleared” in a release record, retain the applicable internal artifact-store authorization and any necessary Windows voice/output licensing determination.

The empty-track sidecar's structural validation records one audio track, zero video tracks, one sample description, zero movie/track/media durations, zero `stts`/`stsc`/`stsz`/`stco` entry counts, no optional sample references, and `hasNoSampleReferences: true`; it also retains the 4,869-byte native source hash `973E432600701F269706E94448DD7415CF85A975471D38FEAC9B7DF1CAA4BD05`.

## Production extraction evidence

[The fresh x64 matrix report](../../artifacts/media-probe/x64-matrix-zero-reference-20260811.json) passed 5/5 with the production `WindowsMediaAudioExtractor`:

- The probe enforced each matrix case's required diagnostic terms rather than accepting a raw native `Unknown` or a merely nonempty error.
- Malformed input reported unreadable/corrupt media; video-only reported no usable audio; the structurally empty track reported zero samples and zero decoded duration; and the altered codec reported unsupported/codec.
- The malformed, no-audio, empty-track, and unknown-codec cases each raised an actionable `MediaExtractionException` and left no owned temporary WAV or input-stage artifact.
- The valid control returned PCM16, 16 kHz, mono, block-aligned audio with 117,760 samples and cleaned up its owned WAV.

## Cancellation evidence

[Prepare](../../artifacts/media-probe/x64-boundary-prepare-zero-reference-20260811.json), [transcode](../../artifacts/media-probe/x64-boundary-transcode-zero-reference-20260811.json), and [validation](../../artifacts/media-probe/x64-boundary-validation-zero-reference-20260811.json) each passed on the fresh valid short control: the requested and observed boundaries matched, extraction was cancelled, and no owned WAV or input-stage artifact remained. These three reports satisfy the matrix's literal valid-control prepare/transcode/validation collection rule.
[The delayed true-in-flight report](../../artifacts/media-probe/x64-cancellation-delayed-zero-reference-20260811.json) separately passed after observed extraction progress (0.310728) on the retained long valid fixture. It is complementary Phase 1 native in-flight cancellation evidence, not an additional matrix collection criterion.

## Workflow evidence

[The fresh valid-control workflow report](../../artifacts/workflow-probe/x64-matrix-zero-reference-valid.json) completed 1/1 job using the production extractor, VAD, Foundry recognizer, coordinator, and atomic WebVTT writer. It committed one valid non-header-only VTT and left no new temporary media artifact or VTT file.

[The fresh extraction-failure workflow report](../../artifacts/workflow-probe/x64-matrix-zero-reference-failures.json) intentionally has a false generic `Success`-scenario summary because that scenario expects completed jobs. Its retained facts are the relevant matrix evidence: all four supplied failure fixtures reached `Failed`, no final VTT existed for any of them, no new temporary media artifact/VTT remained, and no fatal batch error occurred. The empty-track result was an observed extraction failure, which is a permitted matrix outcome.

## Still pending without changing criteria

- Repeat the generated/provenance-bound matrix and the production probe on ARM64.
- Transfer the approved binaries and sidecar to the required internal evidence store, with rights-clearance/legal authorization recorded there.

Do not mark the overall media matrix, Phase 1 gate, or release matrix as complete until those items are evidenced on both architectures.
