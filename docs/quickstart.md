# Windows quickstart

This guide takes you from the WinBulkTranscript README to your first WebVTT transcript on Windows 11. WinBulkTranscript is distributed as a portable ZIP: there is no installer, and the application must remain beside the files included in its extracted folder.

## Before you begin

You need:

- A Windows 11 24H2 or later PC.
- Internet access for the first transcription, when the speech model is downloaded.
- One or more `.mp4` files to transcribe.
- Permission to run an unsigned preview application on the PC.

WinBulkTranscript is currently an unsigned version-zero preview for academic research use. Review the [license](../LICENSE) and the release notes before using it. A work or school PC may enforce a policy that prevents unsigned applications from running; do not disable or bypass that policy.

## 1. Download the correct ZIP

1. Open the [WinBulkTranscript Releases page](https://github.com/JamiesonLabUTSW/win-bulk-transcript/releases).
2. Open the newest release. Version-zero releases are labeled **Pre-release**.
3. Expand **Assets** if the asset list is collapsed.
4. Download one application ZIP:

   | Your PC | Download |
   |---|---|
   | Most Intel or AMD Windows PCs | `WinBulkTranscript-<version>-win-x64.zip` |
   | Windows on Arm PC | `WinBulkTranscript-<version>-win-arm64.zip` |

5. Download the matching checksum sidecar, whose name is the ZIP name followed by `.sha256`.

If you do not know which PC type you have, open **Settings > System > About** and find **System type** under **Device specifications**:

- Choose `win-x64` when it says **x64-based processor**.
- Choose `win-arm64` when it says **ARM-based processor**.

Microsoft documents this location in [32-bit and 64-bit Windows: frequently asked questions](https://support.microsoft.com/en-us/windows/32-bit-and-64-bit-windows-frequently-asked-questions-c6ca9541-8dce-4d48-0415-94a3faa2e13d).

Do not download the automatically generated **Source code** archives. They contain source code, not the ready-to-run Windows application.

## 2. Verify the download

Verification confirms that the ZIP you downloaded has the same SHA-256 checksum produced by the release workflow. This check is especially important before choosing **Run anyway** for an unsigned application.

1. In File Explorer, open the folder containing the downloaded ZIP and `.zip.sha256` file, normally **Downloads**.
2. Right-click an empty area in the folder and select **Open in Terminal**.
3. Paste the following commands into PowerShell:

   ```powershell
   $zip = @(Get-ChildItem .\WinBulkTranscript-*-win-*.zip)
   if ($zip.Count -ne 1) {
     throw 'Keep exactly one WinBulkTranscript release ZIP in this folder, then retry.'
   }

   $sidecar = "$($zip[0].FullName).sha256"
   if (-not (Test-Path -LiteralPath $sidecar -PathType Leaf)) {
     throw "Matching checksum file not found: $sidecar"
   }

   $expected = ((Get-Content -LiteralPath $sidecar -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
   $actual = (Get-FileHash -LiteralPath $zip[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
   if ($actual -ne $expected) {
     throw 'Checksum verification failed. Delete these files and download them again from GitHub Releases.'
   }

   "Checksum verified: $($zip[0].Name)"
   ```

4. Continue only if PowerShell prints `Checksum verified`.

If verification fails, do not run the application. Delete the ZIP and sidecar, download them again from the official Releases page, and repeat the check.

## 3. Extract the entire ZIP

1. In File Explorer, right-click the verified ZIP.
2. Select **Extract All...**.
3. Choose a normal user folder, such as `Documents\WinBulkTranscript\<version>`.
4. Select **Extract**.
5. Open the newly extracted folder.

Microsoft describes the same operation in [Zip and unzip files](https://support.microsoft.com/en-us/windows/zip-and-unzip-files-f6dde0a7-0fec-8294-e1d3-703ed85e7ebc).

The extracted folder contains `WinBulkTranscript.exe` along with many supporting files and folders. Keep them together.

Do not:

- Run `WinBulkTranscript.exe` from inside the ZIP preview.
- Copy only the EXE to another folder or the desktop.
- Delete DLLs or other files from the extracted folder.

After confirming the application works, you may create a shortcut to `WinBulkTranscript.exe`; the shortcut can live elsewhere, but the actual EXE and its supporting files must stay together.

## 4. Start the unsigned application

1. Double-click `WinBulkTranscript.exe` in the extracted folder.
2. Windows may display **Windows protected your PC** because this preview is not code-signed.
3. Confirm that the listed app is `WinBulkTranscript.exe` and that you downloaded and verified it using the steps above.
4. Select **More info**.
5. Select **Run anyway**.

Microsoft explains that unsigned applications with no established reputation can show this warning and require **Run anyway** in [SmartScreen reputation for Windows app developers](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).

Stop instead of continuing when:

- The file came from anywhere other than this repository's GitHub Releases page.
- Checksum verification failed or was not completed.
- Windows or antivirus software identifies the file as malicious rather than merely unrecognized.
- Your organization prohibits unsigned applications.
- There is no **Run anyway** option.

Some PCs use Smart App Control or organization-managed application control. Those controls may intentionally provide no override for unknown unsigned code. Do not turn off Windows security features to run WinBulkTranscript; use an approved personal/test device or contact your IT administrator. See Microsoft's [Smart App Control overview](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/overview).

## 5. Prepare input and output folders

Before starting a batch, create or identify:

- An **input folder** containing the MP4 files you want to transcribe. MP4 files may be organized in subfolders.
- An **output folder** where WinBulkTranscript can create `.vtt` files. A separate, initially empty output folder is easiest to understand.

For example:

```text
Documents\Transcription job\
├── Input videos\
│   ├── interview-01.mp4
│   └── Session 2\
│       └── interview-02.mp4
└── Output transcripts\
```

Use folders where your Windows account can read and write files. Avoid read-only media and organization-controlled locations unless you know you have permission.

## 6. Run your first transcription

1. In WinBulkTranscript, select **Choose input folder…** and choose the input folder.
2. Select **Choose output folder…** and choose the output folder.
3. Confirm that the setup panel reports the expected number of MP4 files.
4. Select **Start** in the upper-right corner.
5. Keep the application open while it works.

On the first transcription, WinBulkTranscript downloads and loads its configured speech model. Keep the PC connected to the internet. This first run can remain on the model-loading stage longer than later runs; the model is not included in the application ZIP.

Files are processed one at a time. The **Batch progress** area shows the active file and current processing stage, while the file list shows the state of each MP4.

If matching transcripts already exist, one dialog applies your choice to the entire batch:

- **Skip existing** keeps every existing matching VTT and processes the others.
- **Overwrite all** replaces matching VTT files.
- **Cancel** stops before processing the batch.

## 7. Find the results

Each MP4 produces a WebVTT (`.vtt`) text file under the output folder. The input folder structure is preserved:

```text
Input videos\Session 2\interview-02.mp4
                         ↓
Output transcripts\Session 2\interview-02.vtt
```

You can open a VTT file in a text editor or import it into software that supports WebVTT captions. A file containing only a `WEBVTT` header is a successful result when no speech was detected.

Review the file rows when the batch completes. A failed file does not prevent every other file from being processed; its row and the message bar provide failure information.

## Cancel safely

Select **Cancel** to stop an active batch. Wait for cancellation and cleanup to finish before starting another batch or closing the application. If you close the window during a batch, choose either **Cancel batch and close** or **Keep working** when prompted.

## Start the application again later

Open the extracted application folder and double-click `WinBulkTranscript.exe`, or use a shortcut that points to it. Do not launch the original EXE inside the downloaded ZIP.

For a newer release, download and extract the new ZIP into its own folder instead of mixing it into the previous version's folder.

## Troubleshooting

### Start is disabled

Confirm that both selected folders still exist, the input folder contains at least one readable `.mp4` file, and the output folder is writable.

### Windows says the application cannot run on this PC

Check **Settings > System > About > System type** and make sure you downloaded the matching x64 or ARM64 ZIP.

### More info or Run anyway is unavailable

Smart App Control or organization policy may be enforcing a block. Do not disable the control. Contact your IT administrator or use an approved device.

### The first model load fails

Confirm that the PC is connected to the internet, then close and reopen WinBulkTranscript and retry the batch. Record the exact message if it fails again.

### A VTT file is empty except for its header

This means no speech was detected in that MP4. Confirm that the file has an audible speech track.

### Output files cannot be written

Choose a normal folder under your user profile, such as a new folder under **Documents**, and confirm that files can be created there.

### You still need help

Open an issue in the [WinBulkTranscript issue tracker](https://github.com/JamiesonLabUTSW/win-bulk-transcript/issues). Include:

- The WinBulkTranscript version and x64/ARM64 download used.
- Your Windows version from **Settings > System > About**.
- The exact error text or a screenshot with sensitive information removed.
- The processing stage and whether this was the first model download.

Do not attach confidential recordings, participant information, credentials, or other sensitive data.
