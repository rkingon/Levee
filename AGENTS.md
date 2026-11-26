# Working on Levee

Read [docs/principles.md](docs/principles.md) first: what belongs here and what doesn't. [README.md](README.md) has the packages, roles and rules.

- Hold the shape every app converges on: what every product ends up writing gets one conventional shape here, so agents
  in every repo write it the same way.
- A rule belongs here only if leaving it out lets wrong code compile. A legitimate per-app choice goes behind a hook
  (`IAccessPolicy`, `IAdmission`, `IUnitOfWorkFactory`).
- A rule's message is the instructions: what's wrong, then `Fix:` and the conventional shape. Its section in
  `docs/rules.md` has why, the wrong shape and the right one. `MessageTests` holds both.
- A consumer's request ends up in `docs/decisions.md`, built, held or open, with its reason.
- Every rule has a test that proves it fires and one that proves it doesn't: `tests/Levee.Analyzers.Tests` for the build,
  `tests/Levee.IntegrationTests` for the database (run as the least-privilege role, never the owner).
- `tests/Sample` is a consumer in four roles; it must keep building with no suppressions.
- All packages ship in lockstep at `Directory.Build.props`'s `Version`; record every change in `CHANGELOG.md`.
- Public API is tracked (`PublicAPI.Unshipped.txt` per package): a change to it is a deliberate diff, and a release moves
  Unshipped into Shipped.
- Comments say why, never what.
