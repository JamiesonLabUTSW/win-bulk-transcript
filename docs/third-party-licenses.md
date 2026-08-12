# Third-party licensing audit

This audit describes the dependency graph locked on 2026-08-12. It is a source-review aid, not a substitute for the release-specific `THIRD-PARTY-NOTICES.md` generated from the actual staged `.deps.json`, the reviewed .NET runtime notices, or legal review.

## Application dependencies

| Component | Version | License family | Distribution notes |
|---|---:|---|---|
| Microsoft.AI.Foundry.Local | 1.2.4 | MIT | The managed SDK wrapper is permissively licensed; retain its copyright and MIT terms. Its Core package and models have separate terms. |
| Microsoft.AI.Foundry.Local.Core | 1.2.4 | Microsoft proprietary redistributable terms | Review the exact package license before binary release. Its object-code redistribution, protective end-user terms, indemnity, export, no-endorsement, and source-disclosure restrictions are not replaced by the WinBulkTranscript license. Confirm the package's referenced distributables scope with counsel or Microsoft. |
| Microsoft.WindowsAppSDK | 2.3.1 | Microsoft proprietary redistributable terms plus third-party notices | Retain the package license and notices for the exact staged Windows App SDK family. Flow-down and indemnity terms require legal review. |
| Betalgo.Ranul.OpenAI | 9.1.0 | MIT | Retain the copyright and MIT terms if present in the staged payload. |
| Microsoft.Extensions.*, Bcl.*, and System.* libraries | Locked versions | Predominantly MIT | Retain the license terms and any package notices for libraries in the staged payload. |
| Microsoft.ML.OnnxRuntime Foundry/Managed/Gpu.Linux | 1.26.0 | MIT plus third-party notices | Retain the MIT license and the package `ThirdPartyNotices` files. Only packages actually staged for the selected Windows RID belong in a release notice. |
| Microsoft.ML.OnnxRuntimeGenAI Foundry/Managed | 0.14.1 | MIT plus third-party notices | Retain the MIT license and package third-party notices. |
| Windows App SDK component packages | Locked 2.x versions | Microsoft proprietary terms plus notices | Includes AI, Base, DWrite, Foundation, InteractiveExperiences, ML, Runtime, Widgets, and WinUI packages. Preserve exact-version license and notice files. |
| Microsoft.Web.WebView2 | 1.0.3719.77 | BSD-3-Clause-style license plus NOTICE | Binary distributions must reproduce its copyright, conditions, disclaimer, and notice material. |
| Microsoft.Windows.SDK.BuildTools and MSIX | Locked versions | Microsoft proprietary terms plus notices | Primarily build/package tooling; include terms only for components present in the distributed payload and retain applicable notices. |

No GPL, LGPL, AGPL, or other reciprocal open-source dependency was found in the locked graph. The main compatibility risk is compliance with proprietary Microsoft redistribution terms, not copyleft. The project's academic-research restriction does not relicense any third-party component.

## Development and test dependencies

`Microsoft.NET.Test.Sdk` 17.14.1, `coverlet.collector` 6.0.4, VSTest/code-coverage components, and Newtonsoft.Json are MIT-licensed. xUnit 2.9.3, xUnit runner 3.1.4, and their components are Apache-2.0 licensed. These are normally not part of the application payload. If distributed, retain their full license texts and notices; Apache-2.0 also requires preservation of applicable notices and marking modified files.

## Runtime and model boundaries

Self-contained releases include .NET runtime/framework files that are not fully represented by `packages.lock.json`. The release process must retain the exact runtime `LICENSE` and `THIRD-PARTY-NOTICES` material and bind it to the staged runtime/framework inventory. Pinning the release SDK with `global.json` is recommended to prevent unnoticed runtime-license drift.

The configured `nemotron-speech-streaming-en-0.6b-generic-cpu:3` model is downloaded separately and is not licensed under the WinBulkTranscript project license. Before first download/use, users should be told that NVIDIA's separate model terms apply. Do not redistribute a model or cache until its exact catalog artifact, license version, provenance, required notice, and redistribution rights have been reviewed. The release pipeline's separate `MODEL-LICENSE.txt` and `MODEL-PROVENANCE.json` are mandatory evidence.

## Release requirements

- Ship the root `LICENSE` with every source and binary distribution.
- Generate package notices from the actual staged payload, not only direct dependencies or a lock file.
- Include the complete text for SPDX-expression licenses and every applicable package `NOTICE` or `ThirdPartyNotices` file.
- Keep the WinBulkTranscript project license, third-party package terms, .NET/runtime terms, and model terms clearly separated.
- Complete legal review of proprietary Microsoft redistribution terms, model terms, and the final generated notices before release.

Authoritative references: [UT Southwestern release terms](https://www.utsouthwestern.edu/about-us/administrative-offices/technology-development/agreements/open-source-release-of-software.html), [Foundry Local repository and licensing boundary](https://github.com/microsoft/foundry-local), [Microsoft.AI.Foundry.Local 1.2.4](https://www.nuget.org/packages/Microsoft.AI.Foundry.Local/1.2.4), [Microsoft.AI.Foundry.Local.Core 1.2.4](https://www.nuget.org/packages/Microsoft.AI.Foundry.Local.Core/1.2.4), [Microsoft.WindowsAppSDK 2.3.1](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.3.1), [ONNX Runtime](https://github.com/microsoft/onnxruntime), [WebView2 1.0.3719.77](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3719.77), and [NVIDIA Open Model License](https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/).
