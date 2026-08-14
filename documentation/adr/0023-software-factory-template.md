# ADR-0023: The software factory — extracting the engineering scaffold as an Apache-2.0 template

**Status:** Proposed · pending maintainer decision (gh#84) · **Date:** 2026-08-14 · **Deciders:** Adam (operator/maintainer)
**Relates to:** PRD `R-14`; [ADR-0015](0015-distribution-licensing-governance.md) (Apache-2.0, AI-first authorship,
seams-as-product-property — this ADR applies its licensing decision to the extracted artifacts),
[ADR-0012](0012-containerization-local-dev.md) (the image/compose scaffold being templatized),
[ADR-0018](0018-image-registry-ghcr.md); `CONTRIBUTING.md`, the four `AGENTS.md` role contracts; gh#84.

## Context

The reusable engineering machinery in this repo — the agent **role-contracts** (`AGENTS.md`), the **ADR discipline**,
the **docs-as-memory** pattern, and the **CI/CD + container scaffolding** — is a *software factory*: it is not
trading-specific, and it is meant to seed *other* products. gh#84 asks to extract it into a standalone **Apache-2.0**
template so a new product starts from it and it can evolve on its own.

Three facts shape the decision:

1. **This is packaging, not licensing.** The *method* (role decomposition, ADR immutability, docs-as-memory,
   test-first) is a procedure — outside copyright (17 USC §102(b)) and already the maintainer's to reuse freely. A
   licence covers only the **artifacts** (the actual files). So the task is not "how do we license the idea"; it is
   "how do we package the files so the next product starts clean." Licensing is already settled by
   [ADR-0015](0015-distribution-licensing-governance.md) — Apache-2.0, with a `NOTICE` carrying attribution and the
   AI-authorship disclosure into every fork.
2. **The split runs *through* files, not just between them.** The crown-jewel assets (the `AGENTS.md` contracts, the
   ADR index, `CONTRIBUTING.md`) interleave factory structure with trading content. gh#84's inventory already sorts
   the surface into three tiers: **Tier 1** copy-as-is (zero trading content — `branch-policy.yml`,
   `Directory.Build.props`, `.editorconfig`/`.gitattributes`/`.gitignore`/`.dockerignore`, the `CLAUDE.md` shims,
   `LICENSE`); **Tier 2** templatize (product values → placeholders — `ci.yml`, `docker-compose*.yml`, the
   `Dockerfile`, `CONTRIBUTING.md`, `.env.example`, `Directory.Packages.props`, `NOTICE`); **Tier 3** split-within-file
   (the `AGENTS.md` contracts and ADR system, where factory and product prose are interwoven).
3. **It is half-scaffolded already.** The `repo-memory` skill ships a `templates/` folder (starter `AGENTS.md` /
   `CLAUDE.md` / `README.md`) — a lighter, skill-level scaffold that the full factory template should subsume, not
   duplicate.

## Decision (proposed)

**Extract the factory as an Apache-2.0 template, packaged so a new product is a fill-in-the-placeholders bootstrap,
not a fork-and-delete-the-trading-parts exercise.** Concretely:

1. **Where it lives — RECOMMEND a standalone repository** (`software-factory`, Apache-2.0), not an in-repo `template/`
   folder. gh#84's own framing ("start from it and it can **evolve on its own**") argues for independent history: a
   product that adopts the factory tracks the template's releases, and the template is not held hostage to
   trading-copilot's roadmap. **This is the one call worth your steer before any files move** — the in-repo
   alternative (below) is cheaper to start but couples the two forever. *(Open for decision.)*
2. **A documented `{{PLACEHOLDER}}` scheme + a `bootstrap` script.** A small, fixed vocabulary —
   `{{ProductName}}`, `{{RootNamespace}}`, `{{SolutionName}}`, `{{ApiProject}}`, `{{ImageName}}`,
   `{{RegistryOwner}}` — filled by `scripts/bootstrap.sh <ProductName>` (the venue-client pattern gh#66/#571 already
   uses `bootstrap.sh` for). Placeholders live only in Tier 2/3 files; Tier 1 is byte-for-byte.
3. **Tier 3 is the real work: refactor-into-template-with-placeholders.** Each interwoven file (`AGENTS.md`,
   the ADR index) is split into a factory skeleton (the role decomposition, the discipline, the same-PR rule) with
   the trading specifics extracted to placeholder sections or dropped. The `repo-memory` skill's `templates/AGENTS.md`
   is the starting point for the root contract, deepened to carry the subtree/role split this repo proved out.
4. **Carry ADR-0015 into the template unchanged in spirit:** Apache-2.0 `LICENSE`, a `NOTICE` with the AI-authorship
   disclosure and Apache boilerplate (trading no-warranty line dropped), and the seams-as-product-property principle
   stated as a template invariant (new integrations go behind an interface).
5. **Keep the platform gates that make the factory honest** — the promotion-ladder `branch-policy.yml`, the
   env-forwarding and doc-duplication checks, LF-everywhere — because a factory whose CI does not enforce its own
   discipline is a README, not a factory.

## Alternatives considered

- **In-repo `template/` folder.** Cheaper to start and co-located, but the template then evolves only when
  trading-copilot does, its files duplicate the live ones and drift, and "standalone" is not achieved. Viable as a
  *first* step (extract into `template/`, promote to a repo later) if you would rather defer the new-repo decision.
- **A `degit`-style extraction script over THIS repo** (no separate template artifact; the script strips trading
  content on `bootstrap`). Single source of truth, but the strip logic *is* the Tier 3 split encoded imperatively —
  harder to review and to evolve than declarative template files, and it makes trading-copilot's layout a permanent
  dependency of every downstream product.
- **Do nothing / rely on the `repo-memory` skill's `templates/` alone.** Rejected as the endpoint: that scaffold
  covers the docs contracts but not the CI/CD, container, or CPM machinery — the half that is most reused and most
  error-prone to re-derive by hand.

## Consequences

- A new product starts from `bootstrap.sh` with a green pipeline, the role contracts, and the ADR discipline on day
  one — the machinery this repo spent its whole history proving out, reused rather than re-derived.
- **Cost of the split:** Tier 3 is genuine refactoring, and the template acquires its own maintenance surface (its CI
  must stay green independently). The `repo-memory` skill and the template overlap on the docs contracts and must be
  reconciled (the template subsumes the skill's `templates/`, or they share one source).
- Licensing/governance is unaffected — ADR-0015 already decided Apache-2.0 and fork-first; this ADR only applies it.

## Follow-ups / open for your steer

- [ ] **The where-it-lives call (Decision 1):** standalone repo now, in-repo `template/` first, or the extraction
      script. Everything mechanical (Tier 1/2) waits on this.
- [ ] Confirm the placeholder vocabulary (Decision 2) — the set above is a starting proposal.
- [ ] Whether the `repo-memory` skill's `templates/` becomes the template's source of truth or is retired into it.
