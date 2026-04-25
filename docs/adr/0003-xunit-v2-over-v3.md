# 0003. xUnit v2 over v3

## Status

Accepted (April 2026)

## Context

xUnit v3 is the current major line. It uses the Microsoft Testing Platform (MTP) for test discovery and execution rather than VSTest. Stryker.NET, which the build plan mandates for mutation testing on the Domain project, relies on VSTest for test discovery and execution.

Stryker GitHub issue #3117 documents that running Stryker against a v3 test project produces unreliable mutation results: the runner does not correctly interpret MTP output, and one reporter saw mutation scores collapse from 100% on v2 to 3.09% on v3. The issue is open as of April 2026 with no merged fix and no maintainer-confirmed timeline for one. Issue #3094 tracks the underlying work to add MTP support to Stryker, also unmerged.

Chapter 16 includes mutation testing on the Domain project as one of the eleven test patterns. The pattern depends on Stryker producing accurate mutation scores, which currently requires xUnit v2.

## Decision

Pin all test projects uniformly to xUnit v2 (currently 2.9.3) in `Directory.Packages.props`. The choice applies to:

- `Domain.Tests`
- `Application.Tests`
- `ProcessManagers.Tests`
- `Projections.Tests`
- `Infrastructure.Tests`
- `IntegrationTests`
- `PropertyTests`

Use `xunit.runner.visualstudio` for VSTest discovery, which works against both v2 and v3 hosts.

## Consequences

- Test code uses xUnit v2 idioms. Theory data, fixture, and collection patterns from v2 are the canonical style for the repo.
- xUnit v3-specific features, including the explicit MTP runner and the v3 changes to `IAsyncLifetime`, are unavailable.
- Upgrade trigger: a Stryker release lands that runs xUnit v3 with mutation results comparable to v2. Specifically, all of the following must hold:
  - Stryker issue #3117 closes with a working v3 runner.
  - A Stryker release exposes that runner as a stable, default option.
  - A spike on `Domain.Tests` shows mutation scores comparable to the current v2 baseline.
- Migration is a single phase across all test projects, not phase-by-phase. Mixing v2 and v3 in one repo is a maintenance trap.
