# WinUI 3 UX and deployment research

Research snapshot: 2026-08-06.

## Screen design

Use one page. This is a submit-and-monitor utility, so `NavigationView`, tabs, a dashboard, and a persistent command bar would add hierarchy the product does not have.

Suggested vertical layout:

1. Title and one-sentence explanation.
2. Setup card with Input folder and Output folder rows.
3. Found MP4 count plus validation messages.
4. Primary Start transcription button and secondary Cancel button.
5. Current-work area with file name, stage text, one progress bar, and batch count.
6. Job `ListView` with file name, status, and concise detail.
7. Collapsible or dismissible `InfoBar` for fatal setup/model errors and the final batch summary.

Each folder row uses a read-only `TextBox` and Browse button. The output field is not editable free-form; paths should come from a picker so validation and permissions are predictable. Disable folder changes and Start while a batch is running.

Microsoft's [form guidance](https://learn.microsoft.com/en-us/windows/apps/design/controls/forms) recommends a clear submission action and preventing invalid submission. Start remains disabled until both folders are valid and at least one MP4 is present. Validation text should appear near the relevant row rather than only in a popup.

## Folder pickers

Use the Windows App SDK picker APIs introduced for desktop apps: `Microsoft.Windows.Storage.Pickers.FolderPicker` initialized with `AppWindow.Id`. The [current file and folder picker guidance](https://learn.microsoft.com/en-us/windows/apps/develop/files/using-file-folder-pickers) returns path-based results and avoids legacy `IInitializeWithWindow` plumbing.

Picker cancellation is not an error and must preserve the existing field value. Revalidate both directories immediately before starting because they may have changed since selection.

## Job list

Use `ListView`, not a hand-built stack of rows. Microsoft describes `ListView` as appropriate for text-heavy vertical collections and its default panel supports UI virtualization in the [ListView and GridView guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/listview-and-gridview).

Each row contains:

- MP4 file name as the primary text;
- status icon plus status text;
- a short secondary detail such as “Detecting speech”, “12 cues”, or a trimmed failure reason.

Do not put a progress bar in each row. Microsoft's [progress control guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/progress-controls) specifically cautions against progress indicators in every item of a virtualized collection. Only one file runs, so one prominent current-file bar is both simpler and more truthful.

State must never be color-only. Pair standard symbolic icons with Pending, Transcribing, Complete, Failed, and Cancelled text. Expose accessible names for picker/action buttons, announce important state changes through appropriate automation/live-region behavior, and preserve usable keyboard focus after picker closure and batch completion.

If output preflight finds existing VTT files, show one `ContentDialog` before model loading. State the collision count and offer Skip existing as the default primary action, Overwrite all as the secondary action, and Cancel. The dialog selects one batch-wide policy and must never recur per file.

## Progress and messages

Use a determinate `ProgressBar` when the stage exposes measurable work. Use indeterminate only for short operations such as model load when no percentage exists. Stage text carries meaning that a percentage cannot: “Downloading speech model”, “Extracting audio”, “Detecting speech”, “Transcribing 4 of 12”, and “Writing VTT”.

Use `InfoBar` for important nonmodal errors or the batch result, not for routine per-file progress. Microsoft's [InfoBar guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar) positions it as a persistent, attention-getting app-level message. Per-file failures remain visible in their job rows.

Cancel should request cancellation and change to a disabled “Cancelling…” presentation until cleanup finishes. Do not close the window abruptly while a model session or output commit is active; intercept close, request cancellation, and complete bounded cleanup or ask for confirmation if shutdown cannot finish promptly.

## Framework and target

At implementation time, pin the then-current stable Windows App SDK after the Phase 0 compatibility test. As of this snapshot, Microsoft's [stable channel](https://learn.microsoft.com/en-sg/windows/apps/windows-app-sdk/stable-channel) lists Windows App SDK 2.3.1. Avoid preview packages.

Proposed target settings:

- target framework: `net10.0-windows10.0.26100.0`;
- minimum supported OS: Windows 11 24H2 / build 26100;
- platforms: `x64;ARM64`;
- runtime identifiers: `win-x64;win-arm64`;
- x86 is intentionally absent.

The current [Windows platform versioning guidance](https://learn.microsoft.com/en-us/windows/apps/get-started/versioning-overview) describes encoding the Windows SDK contract version in the TFM. Windows 11 24H2/build 26100 is the confirmed minimum; earlier Windows 11 releases are outside the supported and tested matrix.

## Recommended deployment shape

Set `WindowsPackageType=None` and publish self-contained, architecture-specific outputs. Self-contained deployment copies the Windows App SDK runtime with the application and avoids requiring the user to install a matching runtime. Microsoft's [self-contained deployment guide](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps) documents this xcopy-style output and its larger size/serviceability tradeoff.

Produce two release artifacts:

```text
WinBulkTranscript-<version>-win-x64.zip
WinBulkTranscript-<version>-win-arm64.zip
```

Each archive contains an unpackaged executable and everything it needs except the downloadable Foundry model. The user extracts the matching archive and launches the executable. No installer, registry registration, MSIX identity, elevation, or automatic updater is planned initially.

This folder/ZIP form is the safest first target because Foundry includes native files whose probing and extraction behavior must be tested. A literal single distributable EXE can be evaluated after that baseline works. Windows App SDK supports `PublishSingleFile` for unpackaged self-contained apps, but it extracts bundled content to a temporary directory at first launch; it is not a zero-extraction process. See Microsoft's [unpackaged WinUI distribution guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app).

The confirmed initial artifact is the self-contained ZIP/folder form. A literal one-file executable is not a version 1 requirement and can be reconsidered later if its extraction behavior offers a real distribution benefit.

### Why not unsigned MSIX for version 1?

Windows 11 can register a deliberately unsigned MSIX with `Add-AppxPackage -AllowUnsigned`, but [Microsoft documents that mechanism as a testing convenience](https://learn.microsoft.com/en-us/windows/msix/package/unsigned-package) and says not to use it for broad distribution. An unsigned package with executable content generally requires elevated installation for all users and a special manifest identity. A self-signed MSIX is also possible, but each target device must first trust its certificate. Neither provides a clean double-click install for an external unsigned release.

MSIX becomes attractive when the project has a trusted signing identity or Store channel: it then provides package identity, clean install/uninstall, architecture bundles, and update options. Until that operational prerequisite exists, the unsigned self-contained ZIP has less user/admin friction and matches the already selected clean-machine test surface. GitHub artifact attestations may supplement ZIP provenance but do not replace Authenticode/MSIX signing or SmartScreen reputation.

## Unsigned distribution consequences

The initial artifacts will have no Authenticode publisher signature. That is technically runnable, but it is not frictionless distribution. Microsoft says an unsigned download commonly triggers “Windows protected your PC”; the user must choose Run anyway, and enterprise policy may prohibit bypass. Unsigned files also start reputation from zero for every new build. See [SmartScreen reputation for Windows app developers](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).

Document this honestly in release notes and only distribute through a controlled trusted channel during the unsigned phase. Do not instruct users to disable security controls. Add signing later without changing the application architecture; Microsoft's current [code-signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options) include Artifact Signing and other trusted-certificate paths.

## Deployment validation

Test published artifacts on clean Windows 11 machines or VMs, not only developer systems:

- x64 artifact on x64 Windows;
- ARM64 artifact on ARM64 Windows;
- paths containing spaces and non-ASCII characters;
- first launch with no Foundry model cache;
- launch offline with and without a cached model;
- standard user account with no elevation;
- ZIP downloaded from the internet, including Mark-of-the-Web/SmartScreen behavior;
- no Visual Studio, .NET SDK, Windows App SDK runtime, or Foundry CLI installed;
- repeated app and model-session launches after update/extraction.
