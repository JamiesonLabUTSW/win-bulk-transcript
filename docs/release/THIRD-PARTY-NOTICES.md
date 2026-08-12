# Third-party and model notices

This file documents the notice generator; it is not a shipping notice. The publisher runs [`scripts/New-ThirdPartyNotices.ps1`](../../scripts/New-ThirdPartyNotices.ps1) only against the staged final `.deps.json`, matching `PUBLISH-PAYLOAD.json`, exact App lock, selected model provenance/information, and runtime/framework input. Retain the generated result with the matching ZIP.

The generator intentionally includes only package libraries that the staged `.deps.json` actually declares. It verifies lock-backed package versions/content hashes against the lock and restored NuGet metadata, embeds package-supplied license files and `NOTICE`/`ThirdPartyNotices` files, and supplies reviewed canonical MIT text when an MIT-expression package has no embedded license file. Any other expression without an embedded license file blocks release until its canonical text is reviewed and supported. It separately records declared runtime frameworks and any non-lock framework packages. A package lock is not a complete inventory of a self-contained runtime payload.

The runtime/framework input must name the discovered framework/package versions. A 0.x preview records the current terms and attribution sources. The supported 1.x policy additionally requires reviewed legal completeness for the copied runtime/native payload.

Expected direct runtime dependencies from the current candidate lock:

- Microsoft Windows App SDK `2.3.1`.
- Microsoft AI Foundry Local `1.2.4`, including its transitive dependencies.

The initial candidate model is `nemotron-speech-streaming-en-0.6b-generic-cpu:3`. Its provenance record binds the artifact identifier, source URI, model-information file name, and SHA-256. The model is downloaded separately rather than included in the application ZIP. Do not infer that the application code license applies to the external model.
