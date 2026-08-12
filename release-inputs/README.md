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

The shared release notes must name both exact ZIP assets. Finalize the model license, model provenance, shared release notes, and each runtime/framework notice before hashing them and filling the two matrices; this avoids circular or stale bindings. Each architecture matrix uses `Release source: ` followed by the exact tag, such as `v1.2.3`, and binds the shared/architecture inputs by filename and SHA-256 as described by the release process.

The release policy is derived from SemVer. A 0.x release is a preview and may use `Passed`, `Accepted risk`, or `Not applicable` in its applicable matrix column, with concrete rationale for every non-passing row. Whenever risk is accepted, the header must use `Approver: <name>; Date: <YYYY-MM-DD>; Decision: <explicit decision>`. A 1.x or later release is supported and requires `Passed` throughout. Preview status never relaxes final model/runtime licensing, provenance, tag/source binding, checksum, or artifact-verification requirements.

Never store signing keys, tokens, passwords, or unrelated private evidence here. A dossier contains only material approved for public inclusion in, or public metadata about, the corresponding binary release. Creating and pushing the tag still requires the institutional disclosure/release approval described in the root README.
