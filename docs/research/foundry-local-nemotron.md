# Foundry Local and Nemotron research

Research snapshot: 2026-08-06. Sources are current official Microsoft or NVIDIA material.

## CPU model contract

The initial research candidate is:

```text
nemotron-speech-streaming-en-0.6b-generic-cpu:3
```

The version comes from the public [Foundry Local model catalog](https://www.foundrylocal.ai/models), which can change independently of the application. Phase 0 therefore records the tested catalog ID in the release configuration rather than discovering an arbitrary latest version at runtime.

The `:3` suffix is not a permanent product requirement. Each application release should pin whichever CPU variant version has passed its x64 and ARM64 acceptance matrix. The app must not select an arbitrary newest version at runtime; upgrades happen intentionally with a new release.

The Foundry C# SDK differentiates `GetModelAsync(alias)` from `GetModelVariantAsync(uniqueModelId)`. The release-pinned ID therefore belongs in `GetModelVariantAsync`; alias resolution could allow a hardware-dependent choice and weaken the CPU-only contract. See the [official C# SDK README](https://github.com/microsoft/Foundry-Local/tree/main/sdk/cs) and the [Foundry Local CLI model-ID behavior](https://learn.microsoft.com/en-us/azure/foundry-local/reference/reference-cli).

The host should verify the returned ID and CPU execution-provider metadata before download/load. There is no fallback to another version or accelerator. A catalog change becomes an explicit compatibility error rather than silently changing transcription behavior.

## Package choice

Recommended starting package:

```text
Microsoft.AI.Foundry.Local 1.2.4
```

The [current SDK reference](https://learn.microsoft.com/en-us/azure/foundry-local/reference/reference-sdk-current) identifies `Microsoft.AI.Foundry.Local` as the cross-platform package and says it can be used on Windows without WinML. It targets .NET 8 and is documented as forward-compatible with .NET 9 and .NET 10. NuGet currently lists [version 1.2.4](https://www.nuget.org/packages/Microsoft.AI.Foundry.Local).

This is preferable to `Microsoft.AI.Foundry.Local.WinML` for the first CPU-only release because the WinML package exists to add Windows GPU/NPU execution-provider behavior that this product deliberately excludes. It also reduces overlap between Windows App SDK native components and a second Windows ML integration layer.

This remains a validation-gated recommendation. Phase 0 must identify and prove one CPU variant version that the core package can download, load, and run on both Windows x64 and ARM64. If it cannot, swap only the app adapter to [`Microsoft.AI.Foundry.Local.WinML`](https://www.nuget.org/packages/Microsoft.AI.Foundry.Local.WinML), keep release-pinned exact-variant lookup, and do not download/register accelerator EPs.

The Foundry package brings transitive dependencies; the dependency goal is to avoid optional product-level packages, not to pretend the SDK has no internal dependencies.

## Runtime lifecycle

The SDK is in-process and does not require installing or invoking the Foundry Local CLI. The proposed lifecycle is:

1. create one `FoundryLocalManager`;
2. resolve the release-pinned CPU model variant;
3. download it with visible preflight progress when absent;
4. load it once before the first job;
5. create/dispose live sessions per VAD segment; and
6. unload during shutdown.

Foundry Local uses the CPU as the universal fallback, according to the [architecture overview](https://learn.microsoft.com/en-us/azure/foundry-local/concepts/foundry-local-architecture). Pinning the CPU catalog package makes that behavior deterministic instead of relying on fallback ranking.

Model weights remain a first-run or cache-managed download. A self-contained application executable does not imply that the Nemotron model is embedded in that executable. The UI must explain the initial download and should fail clearly when offline and the model is not cached.

## Live transcription API

The SDK offers a live-transcription session with `StartAsync`, `AppendAsync`, a response stream, `StopAsync`, and asynchronous disposal. The official [live-transcription guide](https://learn.microsoft.com/en-us/azure/foundry-local/how-to/how-to-live-transcribe-audio) uses Nemotron with 16,000 Hz, one-channel raw PCM and 16-bit signed little-endian input.

Important implementation rules:

- Start consuming the response stream before appending enough audio to fill a bounded queue.
- Await every `AppendAsync`; the SDK documents it as thread-safe and internally bounded.
- Append 100 ms chunks for the baseline, matching the official sample's granularity.
- Do not sleep between chunks in an offline batch unless testing proves pacing is required.
- Call `StopAsync` even when the last audio chunk is short so the session flushes.
- Dispose the session in all paths.
- Join non-empty final text chunks in emission order.

The model is a 0.6-billion-parameter English streaming ASR model based on cache-aware FastConformer/RNNT. NVIDIA describes it as streaming-only and lists supported streaming chunk configurations in the [Nemotron model card](https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b). NVIDIA's [deployment guide](https://docs.nvidia.com/nim/speech/latest/asr/deploy-asr-models/nemotron-asr-streaming.html) likewise treats files as streams of chunks. This supports using a live session for offline segments rather than looking for a separate batch transcription API.

## Segment-session decision

The product request calls for VAD segmentation followed by ASR of each segment. The simplest semantic match is one live session per segment:

- the decoder never sends long silent spans;
- each returned text maps directly to one VTT cue;
- session-relative model timestamps cannot blur original-file timing; and
- failure can identify a specific segment.

The risk is session startup overhead when a file contains hundreds of tiny segments. Phase 0 should compare per-segment sessions with a single per-file session that streams segments plus controlled silence. Keep per-segment sessions unless measured overhead is material; correctness and simple cue mapping win by default.

## Platform and model validation matrix

The SDK and Windows App SDK support Windows x64 and ARM64, but the dynamic model catalog does not provide enough static evidence that any particular catalog version behaves identically on both architectures. Treat these as release gates for the version selected for a release:

| Check | x64 | ARM64 |
|---|---|---|
| Exact variant resolves | Required | Required |
| Download and cache | Required | Required |
| CPU model loads | Required | Required |
| 16 kHz PCM session completes | Required | Required |
| Offline append without delay | Required | Required |
| Cancellation/disposal recover | Required | Required |
| Second session works after failure | Required | Required |
| Published self-contained build locates native SDK files | Required | Required |

## Licensing and privacy notes

The model card identifies the NVIDIA Open Model License. Before public distribution, record the model/package notices actually installed by the pinned Foundry version and have the intended distribution reviewed against those terms. This document is a technical plan, not a license opinion.

Audio and recognition run locally after the model download. The UI and privacy statement should avoid claiming that the app is permanently offline: first-run model acquisition requires network access unless the model has already been provisioned in the Foundry cache.
