# WinBulkTranscript

WinBulkTranscript by Jamieson Lab is a Windows 11 desktop application that recursively transcribes MP4 files into WebVTT (`.vtt`) files.

> **Development/UAT status:** local x64 implementation evidence is green, but this repository is not yet a supported release. See [UAT evidence status](docs/validation/uat-evidence-status.md) and the [release test matrix](docs/release/release-test-matrix.md).

## Quick start: run from source

Run these commands in PowerShell from the repository root on Windows 11 24H2 or later. Developers need a .NET 10 SDK. The desktop app has no command-line file arguments.

```powershell
dotnet restore .\src\WinBulkTranscript.App\WinBulkTranscript.App.csproj --locked-mode
dotnet run --project .\src\WinBulkTranscript.App\WinBulkTranscript.App.csproj --configuration Debug
```

`dotnet run` opens the application window. After a successful build, add `--no-build --no-restore` for a faster repeat launch.

## Run a self-contained local folder

Use this to test the unpackaged, self-contained folder layout intended for distribution. It is a local developer publish, not a release artifact.

```powershell
$publish = Join-Path $PWD ("artifacts\local-publish\win-x64-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
dotnet restore .\src\WinBulkTranscript.App\WinBulkTranscript.App.csproj --locked-mode
dotnet publish .\src\WinBulkTranscript.App\WinBulkTranscript.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --no-restore `
  --property:WindowsAppSDKSelfContained=true `
  --property:PublishSingleFile=false `
  --output $publish

& "$publish\WinBulkTranscript.exe"
```

Replace `win-x64` with `win-arm64` on an ARM64 Windows machine. Keep every file in the published folder beside `WinBulkTranscript.exe`; do not copy or run the EXE by itself. Version 1 uses a folder/ZIP deployment rather than a single-file executable.

## Use the application

1. Select an **existing** input folder containing MP4 files. Discovery is recursive and Start stays disabled until it finds a readable MP4.
2. Select an **existing**, writable output folder. A separate output folder is recommended.
3. Select **Start transcription**. If the pinned CPU model is not cached, the app downloads and loads `nemotron-speech-streaming-en-0.6b-generic-cpu:3`; an uncached first run needs internet access.
4. Watch the progress and file rows. Files are processed sequentially.
5. If matching VTTs already exist, choose one batch-wide policy: **Skip existing**, **Overwrite all**, or **Cancel**.

An input file at `input\season-1\episode-01.mp4` writes to `output\season-1\episode-01.vtt`: the input hierarchy is mirrored below the output root. A header-only VTT is a successful result when no speech is detected.

Use **Cancel** to stop a batch cooperatively and wait for cleanup to finish before starting another batch or closing the app. Closing during a batch offers **Cancel batch and close** or **Keep working**.

## Troubleshooting and status

- **Start is disabled:** both folders must still exist, and the input tree must contain at least one readable `.mp4` file. Reparse points are not traversed.
- **First model load fails:** connect to the internet and retry. Cached-offline behavior is intended but is not yet release-validated on a clean machine.
- **Existing transcript files:** choose the batch-wide collision policy in the dialog; do not force-kill the app while it is processing.
- **Supported package:** do not treat `artifacts\publish-smoke` as a release. A final release ZIP does not exist yet; see the [release process](docs/release/README.md).

## License and permitted use

WinBulkTranscript is source-available software licensed for **academic research use only** under the [UT Southwestern academic research license](LICENSE). Commercial use or redistribution is prohibited, and any use or redistribution by a for-profit entity is treated as commercial use. The license does not grant patent rights. This restricted license is not an Open Source Initiative-approved open-source license.

Third-party packages, the .NET/Windows runtime payload, and the separately downloaded transcription model remain subject to their own license terms. Release artifacts include release-specific third-party, runtime, and model notices; see [Third-party licensing](docs/third-party-licenses.md) for the current dependency audit. Questions about permitted use or alternative licensing should be directed to the [UT Southwestern Office for Technology Development](https://www.utsouthwestern.edu/about-us/administrative-offices/technology-development/agreements/open-source-release-of-software.html) at technologydevelopment@utsouthwestern.edu.

Adding the required license notice does not itself authorize public release. UT Southwestern contributors must complete the institution's required disclosure and approval process before publishing this repository or a binary release.

## Related documentation

- [Design and implementation index](docs/README.md)
- [Implementation plan](docs/implementation-plan.md)
- [UAT evidence status](docs/validation/uat-evidence-status.md)
- [Release process](docs/release/README.md)
