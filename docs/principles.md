# Principles

Levee is a .NET framework for building platforms whose rules hold by construction: wrong code fails the build, or fails
at the database, instead of getting caught in review. It extracts what several apps built on the same architecture
converged on, so the next app starts with the guardrails on day one, and every agent in every repo hits the same build
errors.

The name follows Oxbow's river theme: a levee doesn't decide where the water goes, it keeps it in the channel.

## What belongs

Levee doesn't hold every pattern. It holds enough that the correct patterns follow from it:

- **Include** a pattern if leaving it out lets wrong code compile.
- **Leave out** anything that's a legitimate per-app choice, behind a hook.
- **Hold the shape every app converges on.** What every product ends up writing (a client for its operations, retries
  that don't repeat a write, stale-edit conflicts, contract changes that strand clients) gets one conventional shape, so
  an agent in any repo writes it the same way.

[decisions.md](decisions.md) records each request against these: built, held, or open, with why.

## The keystone

**The platform is a catalog of declared operations, and every call goes through one pipeline.** The pipeline authorizes,
scopes, opens the unit of work, runs the handler, audits and commits. HTTP and MCP are generated projections of the
catalog with no logic of their own; an app's public API is its own, built on the same dispatcher.

Own that, and several rules come for free:

- authorization in one place;
- undeclared writes refused by the database;
- thin clients by construction;
- every write audited.

## Jobs

Every app needs jobs eventually, so **Oxbow is enlisted in Levee's unit of work**. A job commits or rolls back with the
data it belongs to, which also gives you an outbox: post-commit side effects become jobs, with no window to lose them.
Jobs run as operations, so they get the same authorization, scoping and audit as requests. Oxbow stays its own package,
usable without Levee.

## DDD structure, enforced

Each project declares a role as an MSBuild property. The build checks the project's references against the role, and
role-aware analyzers check its code. The type system covers the rest: repositories exist only for aggregate roots, and
aggregates change state only through their own methods. Levee enforces DDD's structure, not good modeling: no analyzer
can tell you where an aggregate boundary belongs.

## The build is the instructions

Every rule's message says what's wrong, then `Fix:` and the conventional shape, so an agent that has never seen the
codebase writes it right from the build output alone. `dotnet levee explain` has the long form.

## Risks

- Breaking changes fanning out across every app that uses it.
- The framework becoming the project instead of the apps.

The mitigation is small releases, each recorded in [decisions.md](decisions.md), and public API
tracked so every change to it is a deliberate diff.
