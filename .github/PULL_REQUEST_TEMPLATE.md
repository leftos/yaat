<!--
Thanks for sending a PR! Fill the sections below — anything genuinely not applicable can be deleted, but please leave the Test plan in.
See CONTRIBUTING.md for the development setup and CHANGELOG conventions.
-->

## Summary

<!-- One or two sentences: what changes, and why. -->

## Linked issues

<!-- e.g., "Closes #123" or "Refs #456". Use the full URL form for cross-repo links: "Closes https://github.com/leftos/yaat/issues/123" -->

## Test plan

<!--
What did you actually do to verify this works?
- Unit/integration tests added or updated (point at them)
- Manual smoke: commands run, scenarios loaded, what you saw on screen
- For UI changes: include before/after screenshots if reasonable
-->

- [ ] `dotnet build -p:TreatWarningsAsErrors=true` passes
- [ ] `dotnet test` passes
- [ ] `prek run` passes (or pre-commit hooks ran on commit)
- [ ] For cross-repo changes affecting `Yaat.Sim`: ran `pwsh tools/test-all.ps1` to verify yaat-server still builds

## Risk and rollback

<!--
- Anything risky about this change? (data-shape changes, protocol changes, perf-sensitive paths, ground-routing logic, etc.)
- How would we roll it back if it goes wrong in a release? Revert is fine for most things — call out anything that isn't.
-->

## Aviation realism

<!--
If this PR touches aviation logic — flight physics, pilot AI, ATC rules, radio comms, aircraft performance,
airspace rules, phase transitions, command dispatch, ground ops, conflict detection, or any automatic aircraft behavior —
note that you ran it past the `aviation-sim-expert` review (per CLAUDE.md). Cite the FAA 7110.65 / AIM sections you relied on.

Delete this section for PRs that don't touch aviation logic.
-->

## CHANGELOG

<!--
Did you add a bullet under `## Unreleased` in CHANGELOG.md? Required for user-visible changes.
Internal refactors and tests are exempt.
-->
