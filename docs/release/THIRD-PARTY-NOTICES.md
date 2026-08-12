# Third-party and model notices

This file is a release gate and template, not a shipping legal notice. The publisher runs [`scripts/New-ThirdPartyNotices.ps1`](../../scripts/New-ThirdPartyNotices.ps1) only against the staged final `.deps.json`, matching `PUBLISH-PAYLOAD.json`, exact App lock, selected model provenance/license, and reviewed runtime/framework notice input. Retain the generated result with the matching ZIP.

The generator intentionally includes only package libraries that the staged `.deps.json` actually declares. It verifies lock-backed package versions/content hashes against the lock and restored NuGet metadata, then separately records declared runtime frameworks and any non-lock framework packages. A package lock is not a complete inventory of a self-contained runtime payload.

The runtime/framework notice input must name the discovered framework/package versions and carry reviewed legal attribution for the copied runtime/native payload. Automation cannot establish whether that legal text is complete, so legal review remains mandatory before distribution.

Expected direct runtime dependencies from the current candidate lock:

- Microsoft Windows App SDK `2.3.1`.
- Microsoft AI Foundry Local `1.2.4`, including its transitive dependencies.

The initial candidate model is `nemotron-speech-streaming-en-0.6b-generic-cpu:3`. Its provenance record must bind the exact artifact identifier, source URI, source license file name, and license SHA-256. Its NVIDIA model license and all package license texts must be included or referenced exactly as supplied by the version that passed the architecture test matrix. Do not infer that the application code license grants redistribution rights for the model.
