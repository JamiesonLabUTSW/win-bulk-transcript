# x64 single-file Foundry experiment

Date: 2026-08-11
Status: informational only; this does not change the version-1 folder/ZIP deployment decision or satisfy clean-machine, ARM64, or release acceptance.

## Host and input

- Host: Windows 11 Education build 26200, x64, non-clean development machine.
- Fixture: `test-assets/phase0-known-en.pcm`, SHA-256 `6E951994EA10F9EE88EF4D2376190900ED52FA80C20997E8279136460F52F467`.
- Model: `nemotron-speech-streaming-en-0.6b-generic-cpu:3`, resolved as CPU.
- The model cache was already available locally. `MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY` was not set for the executed process.

## Publish command

```powershell
dotnet publish tools\WinBulkTranscript.CompatibilitySpike\WinBulkTranscript.CompatibilitySpike.csproj `
  --configuration Release --runtime win-x64 --self-contained true --no-restore `
  --property:PublishSingleFile=true `
  --property:IncludeNativeLibrariesForSelfExtract=true `
  --property:EnableMsixTooling=true `
  --property:Version=0.0.0-single-file-experiment `
  --output artifacts\phase0\single-file-x64-20260811 --nologo
```

The initial literal single-file publish was rejected until `EnableMsixTooling=true` was supplied by the Windows App SDK target. The successful publish still warned that single-file is recommended only with `WindowsAppSDKSelfContained=true` and that the Windows App SDK target expects `MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY` before program entry. Those warnings, and this host's installed Windows App SDK, mean the result must not be treated as a dependency-free or clean-machine launch.

## Observed result

The output contains one executable plus PDB files. `WinBulkTranscript.CompatibilitySpike.exe` is 182,838,124 bytes with SHA-256 `60A2F1DF160AF401E7FA0240836928F7F2F1C7D9BC05B48CE3906BB867784315`.

Running the literal executable without setting the base-directory variable exited `0` and wrote [the report](../../artifacts/phase0/x64-single-file-experiment-no-base.json). It resolved the exact CPU model, produced nonempty baseline/recovery transcripts, completed 20/20 short sessions, observed bounded append waits, and passed prompt/append/response cancellation cleanup. The report SHA-256 is `FAFE32D1AEAC2327460EEB90912E81E44005F0179077CE056985E84D3E5235E4`.

This proves only that the local x64 single-file experiment could perform Foundry native probing on this host. ARM64, clean-machine launch, first-run download, cached/uncached offline behavior, a downloaded ZIP/MOTW test, release inputs, and legal provenance remain separate required evidence.
