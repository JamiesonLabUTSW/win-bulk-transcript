# Release input dossiers

Each tag release is built only from a completed, reviewed dossier committed at `release-inputs/v<version>/` in the commit being tagged. This makes a tag an auditable release request instead of allowing mutable repository secrets or ad hoc runner files to decide what ships.

Create the following files from the templates and instructions in [`docs/release`](../docs/release/README.md):

```text
release-inputs/v1.2.3/
├── model-license.txt
├── model-provenance.json
├── release-notes.md
├── win-x64/
│   ├── release-test-matrix.md
│   └── runtime-framework-notices.txt
└── win-arm64/
    ├── release-test-matrix.md
    └── runtime-framework-notices.txt
```

The shared release notes must name both exact ZIP assets. Complete the model information, model provenance, shared release notes, and each runtime/framework information file before hashing them and filling the two matrices; this avoids circular or stale bindings. Each architecture matrix uses `Release source: ` followed by the exact tag, such as `v1.2.3`, and binds the shared/architecture inputs by filename and SHA-256 as described by the release process.

The release policy is derived from SemVer. A 0.x release is a preview and may use `Passed`, `Accepted risk`, or `Not applicable` in its applicable matrix column, with concrete rationale for every non-passing row. Whenever risk is accepted, the header records the approver, date, and decision. Preview model/runtime files disclose the current external components and terms references; their hashes bind exactly what was disclosed and do not assert production-grade review. A 1.x or later release is supported, requires `Passed` throughout, and uses the stricter reviewed-input policy. Tag/source binding, checksums, and artifact verification apply to both policies.

Never store signing keys, tokens, passwords, or unrelated private evidence here. A dossier contains only material approved for public inclusion in, or public metadata about, the corresponding binary release. Creating and pushing the tag still requires the institutional disclosure/release approval described in the root README.
