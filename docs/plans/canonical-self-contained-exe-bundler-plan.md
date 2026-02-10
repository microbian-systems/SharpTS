# Canonical Self-Contained EXE Bundler Plan

## Overview

This plan defines how to evolve SharpTS single-file EXE generation so it is:

- SDK-independent at user runtime (no `dotnet publish`, no external build tools required)
- close to standard .NET single-file bundling semantics
- more robust against transient file locking and antivirus scanning races
- deterministic and testable across platforms

The core strategy is to replace ad-hoc bundling paths with an internal, canonical bundling pipeline that mirrors `Microsoft.NET.HostModel` behavior.

## Goals

1. Keep current self-contained EXE support (`sharpts --compile ... -t exe`).
2. Avoid requiring the .NET SDK or external tooling for bundling at runtime.
3. Make bundle layout and metadata as close as practical to standard .NET host model behavior.
4. Improve reliability under AV/file lock contention.
5. Preserve backward compatibility in CLI behavior and defaults.

## Non-Goals

1. Full `dotnet publish` parity for every SDK feature in v1.
2. Solving AV reputation entirely via bundling changes alone.
3. Introducing mandatory code-signing in this project phase.

## Current Gaps

1. `SdkBundler` currently reconstructs bundler behavior via reflection and a minimal file set (`dll + runtimeconfig`) instead of full canonical filtering/classification.
2. `ManualBundler` performs direct byte construction and deterministic ID generation that may diverge from standard manifest behavior.
3. I/O resilience (retry/backoff/atomic replacement) is limited.
4. AppHost dependency discovery currently leans on SDK/packs discovery in manual mode.

## Target Architecture

### New Internal Components

1. `CanonicalBundler` (new)
- Single source of truth for file classification, filtering, embedding, manifest writing, and final patching.
- Replaces custom logic duplication across `SdkBundler` and `ManualBundler`.

2. `CanonicalHostWriter` (new)
- AppHost patching logic aligned with `HostWriter` behavior (placeholder rewrite, optional PE adjustments as needed).

3. `CanonicalManifest` and `CanonicalFileEntry` (new)
- Deterministic manifest and file entry encoding consistent with HostModel structure.

4. `CanonicalTargetInfo` (new)
- RID/OS/arch rules, alignment rules, and bundle-version options in one place.

5. `BundlingIoPolicy` (new)
- Retry + jitter for transient I/O errors.
- Temp file output + atomic replace + cleanup policy.

### Existing Component Changes

1. `BundlerFactory`
- Default to canonical implementation for auto mode.
- Keep legacy/manual as compatibility fallback behind explicit opt-in.

2. `Program.cs`
- Keep CLI surface stable.
- Add richer diagnostic mode for bundler internals (opt-in).

3. `IBundler` / bundle result types
- Extend result metadata (optional) for diagnostics and test assertions.

## Implementation Phases

### Phase 0: Baseline and Design Freeze

Deliverables:
1. Capture current behavior snapshots for existing bundlers (file outputs, launch behavior, bundle structure checks).
2. Define parity checklist against .NET HostModel source behavior.
3. Lock binary format requirements for bundle header, manifest, and file entry ordering.

Acceptance criteria:
1. Baseline tests recorded and reproducible in CI.
2. Design doc with explicit "must match" vs "acceptable divergence" list.

### Phase 1: Canonical Data Model and Manifest

Deliverables:
1. Implement `CanonicalManifest` and `CanonicalFileEntry`.
2. Implement deterministic bundle ID generation compatible with manifest data.
3. Implement header/offset patching using canonical placeholder rules.

Files (expected):
- `Compilation/Bundling/Canonical/CanonicalManifest.cs`
- `Compilation/Bundling/Canonical/CanonicalFileEntry.cs`
- `Compilation/Bundling/Canonical/CanonicalBundleFormat.cs`

Acceptance criteria:
1. Unit tests validate byte-level manifest determinism.
2. Header offset patching works for generated bundles on all supported OSes.

### Phase 2: Canonical Host and File Classification

Deliverables:
1. Implement `CanonicalHostWriter` for apphost patching logic.
2. Implement file-type inference and filtering rules (assembly/deps/runtimeconfig/native/symbol/other).
3. Support explicit bundle options for include/exclude behavior.

Files (expected):
- `Compilation/Bundling/Canonical/CanonicalHostWriter.cs`
- `Compilation/Bundling/Canonical/CanonicalFileClassifier.cs`
- `Compilation/Bundling/Canonical/CanonicalBundleOptions.cs`

Acceptance criteria:
1. File inclusion/exclusion behavior is deterministic and covered by tests.
2. Host patching succeeds with path-length validations and cleanup on failure.

### Phase 3: Canonical Bundler Engine

Deliverables:
1. Implement `CanonicalBundler` write pipeline:
- copy host
- embed files with alignment/compression rules
- write manifest
- patch header
- finalize output
2. Introduce resilient I/O policy with retry/backoff for transient locks.
3. Add temp-output + atomic replace semantics.

Files (expected):
- `Compilation/Bundling/Canonical/CanonicalBundler.cs`
- `Compilation/Bundling/Canonical/BundlingIoPolicy.cs`

Acceptance criteria:
1. EXE runs for representative scripts in CI.
2. Transient lock simulation tests pass with retries.
3. Partial/corrupt output is not left behind on failure.

### Phase 4: SDK-Independent Host Template Strategy

Deliverables:
1. Introduce packaged apphost template assets per supported RID.
2. Add template versioning and validation checks.
3. Remove runtime dependency on SDK pack discovery for canonical mode.

Files (expected):
- `Compilation/Bundling/Templates/*`
- `Compilation/Bundling/Canonical/AppHostTemplateResolver.cs`
- project file updates for asset packing

Acceptance criteria:
1. EXE bundling works on machines without SDK installed.
2. Resolver selects the correct template for target RID with clear errors on unsupported targets.

### Phase 5: Integration, Compatibility, and Migration

Deliverables:
1. Wire `BundlerFactory` to use canonical bundler by default.
2. Keep legacy/manual implementation available behind explicit option for one transition release.
3. Update CLI help/docs and migration notes.

Files (expected):
- `Compilation/Bundling/BundlerFactory.cs`
- `Cli/CommandLineParser.cs` (if option semantics need extension)
- `Program.cs`
- `docs/execution-modes.md`
- `README.md`

Acceptance criteria:
1. Existing CLI commands continue to work unchanged.
2. Integration tests cover default/explicit bundler selection paths.

## Test Plan

### Unit Tests

1. Manifest serialization/parsing determinism.
2. File type inference and exclusion rules.
3. Bundle ID stability across equivalent inputs.
4. Header placeholder search/patch behavior.
5. I/O retry logic with simulated transient failures.

### Integration Tests

1. Compile to EXE and execute on Windows/Linux/macOS.
2. Compare canonical output behavior with SDK-generated bundle behavior for sample scripts.
3. Validate behavior without SDK installed (template-based path).
4. Validate failure modes (missing template, invalid RID, output locked).

### Regression Tests

1. Existing `CliBundlerTests` remain green.
2. Add new canonical parity tests:
- manifest structure checks
- file list checks
- technique reporting checks

## Rollout Strategy

1. Release N:
- ship canonical bundler behind feature flag or opt-in mode (`--bundler canonical`)
- collect real-world feedback

2. Release N+1:
- make canonical bundler the default for `auto`
- keep legacy/manual as explicit compatibility mode

3. Release N+2:
- remove or deprecate legacy path if no critical regressions

## Risks and Mitigations

1. Risk: binary-format mismatch with host expectations.
- Mitigation: byte-level tests + parity checks against known-good bundles.

2. Risk: platform-specific apphost/template drift.
- Mitigation: template version pinning and CI coverage per RID.

3. Risk: AV false positives persist.
- Mitigation: canonicalize structure, improve metadata, and track AV matrix results; document that signing remains a separate control.

4. Risk: maintenance overhead from mirroring HostModel behavior.
- Mitigation: isolate compatibility logic in dedicated canonical namespace and include source references in comments.

## Proposed Milestones

1. Milestone 1 (1-2 weeks): Phase 0 + Phase 1
2. Milestone 2 (1-2 weeks): Phase 2 + Phase 3
3. Milestone 3 (1 week): Phase 4
4. Milestone 4 (1 week): Phase 5 + documentation + stabilization

## Success Metrics

1. EXE generation success rate in CI and local test matrix >= current baseline.
2. No regression in existing CLI bundler tests.
3. Successful EXE bundling on SDK-absent environments.
4. Reduced issue volume for file-lock/partial-write bundling failures.
5. Improved AV reproducibility metrics in controlled test runs.

## References

- .NET HostModel Bundler source: `dotnet/runtime` `Microsoft.NET.HostModel/Bundle/Bundler.cs`
- .NET HostWriter source: `dotnet/runtime` `Microsoft.NET.HostModel/AppHost/HostWriter.cs`
- SDK GenerateBundle task: `dotnet/sdk` `Microsoft.NET.Build.Tasks/GenerateBundle.cs`
- SharpTS issue context: `https://github.com/nickna/SharpTS/issues/4`
