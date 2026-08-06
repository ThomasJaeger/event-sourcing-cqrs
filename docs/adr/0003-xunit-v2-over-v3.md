# 0003. xUnit v2 over v3

## Status

Accepted (April 2026)

## Context

xUnit v3 is the current major line. It uses the Microsoft Testing Platform (MTP) for test discovery and execution rather than VSTest. Stryker.NET, which the build plan mandates for mutation testing on the Domain project, relies on VSTest for test discovery and execution.

Stryker GitHub issue #3117 documents that running Stryker against a v3 test project produces unreliable mutation results: the runner does not correctly interpret MTP output, and one reporter saw mutation scores collapse from 100% on v2 to 3.09% on v3. The issue is open as of April 2026 with no merged fix and no maintainer-confirmed timeline for one. Issue #3094 tracks the underlying work to add MTP support to Stryker, also unmerged.

Chapter 16 includes mutation testing on the Domain project as one of its test patterns. The pattern depends on Stryker producing accurate mutation scores, which currently requires xUnit v2.

## Decision

Pin all test projects uniformly to xUnit v2 (currently 2.9.3) in `Directory.Packages.props`. The choice applies to:

- `Application.Tests`
- `Domain.Tests`
- `EventStore.ContractTests`
- `Infrastructure.Tests`
- `Projections.Tests`
- `ProcessManagers.Tests`
- `TestInfrastructure`
- `Workers.Tests`
- `IntegrationTests`
- `Hosts.Web.Tests`
- `Hosts.AdminConsole.Tests`
- `PropertyTests`
- `Migration.Tests`

Use `xunit.runner.visualstudio` for VSTest discovery, which works against both v2 and v3 hosts.

## Consequences

- Test code uses xUnit v2 idioms. Theory data, fixture, and collection patterns from v2 are the canonical style for the repo.
- xUnit v3-specific features, including the explicit MTP runner and the v3 changes to `IAsyncLifetime`, are unavailable.
- Upgrade trigger: a Stryker release lands that runs xUnit v3 with mutation results comparable to v2. Specifically, all of the following must hold:
  - Stryker issue #3117 closes with a working v3 runner.
  - A Stryker release exposes that runner as a stable, default option.
  - A spike on `Domain.Tests` shows mutation scores comparable to the current v2 baseline.
- Migration is a single phase across all test projects, not phase-by-phase. Mixing v2 and v3 in one repo is a maintenance trap.

## Amendment (August 2026): the reason of record is replaced

This section replaces the reason this decision rests on. It does not add detail to the original
reasoning, and it does not change the decision. The pin stands, the status stays Accepted, and
nothing supersedes this ADR.

**The Stryker justification is void.** The Context above rests on Stryker.NET throughout, and the
tool exists in no configuration file, no package reference, no tool manifest, and no run target
anywhere in this repository. Nothing depends on it, so nothing about mutation testing constrains
the choice of test framework here.

**The pin stands on a live constraint the original did not name.** `Directory.Packages.props`
declares it beside the FsCheck versions:

> Property-based tests (FsCheck). FsCheck.Xunit 3.3.3 is the latest FsCheck.Xunit and targets
> xUnit v2 (xunit.extensibility.execution >= 2.4.1 && < 3.0.0), which the pinned xunit 2.9.3
> satisfies; the separate FsCheck.Xunit.v3 package is the xUnit v3 integration and is not used.

That constraint is live rather than notional. `tests/PropertyTests/PropertyTests.csproj`
references `FsCheck.Xunit`, and five `[Property]` facts across three files in that project run
through it.

**Migrating is a package swap rather than a version bump.** Moving to xUnit v3 means replacing
FsCheck.Xunit with FsCheck.Xunit.v3, a different package with its own surface, rather than taking
a higher version of the one in use.

**The upgrade trigger is void and nothing replaces it.** The three conditions in the Consequences
above are all Stryker events, so they go with the tool they name. No trigger takes their place,
because none currently applies: the v3 integration package exists today, which makes the
constraint a cost rather than a block, and whether to pay that cost is a decision someone makes on
its own merits rather than one this ADR pre-authorizes.

**The project enumeration is corrected in place.** The Decision above listed seven test projects.
The solution declares thirteen, and thirteen project files carry an `xunit` reference, derived both
ways. The pin is central in `Directory.Packages.props`, so its effect always covered every test
project as each one landed; only the enumeration lagged. The correction is made in the Decision
itself rather than here, following commit `1045862`, which corrected stale phase numbers inside
ADR 0025 in place while reasoning was appended as this section is.
