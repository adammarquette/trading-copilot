# Wiki SCHEMA — how this knowledge base works

**What this is.** A design-time **domain-knowledge wiki** for the trading co-pilot — trading methodologies,
order-flow theory, instrument/venue specifics, and vendor-API references that inform *how the platform should
behave*. It is **not part of the product**: the running system never reads it. It follows Andrej Karpathy's
[LLM Wiki pattern](https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f) and the repo's
cross-referencing discipline (see [`../trading-platform-engineering.md`](../trading-platform-engineering.md) §11).

Readers are both humans and LLMs — favor **dense cross-links and explicit context** over prose.

## Files & layout
- **`index.md`** — the front door; catalog of every page (read first at query time, updated every ingest).
- **`log.md`** — append-only, chronological ingest/lint history (grep-able `## [YYYY-MM-DD] <op> | <title>`).
- **`SCHEMA.md`** — this file; conventions + workflows, co-evolves with use.
- **`ingest/`** — drop-zone for new/updated sources awaiting ingestion (see *The ingest drop-zone* below).
- **`sources/`** — snapshot of each source *as last ingested*; diff against `ingest/` to see what's pending.
- **`pages/`** — concept, entity, and reference pages.

## Page types
- **Reference** — an external entity distilled: a vendor API, an exchange spec, an instrument (e.g. `pages/projectx-gateway-api.md`).
- **Concept** — a domain idea: order flow, footprint/delta, volume profile, expectancy.
- **Index / source** — catalogs or source pointers.

## Every page carries a header
```
> **Trust tier:** authoritative | curated | unverified
> **Verified:** <how / when> · **Sources:** <primary URLs>
> **Informs:** <R-# / Q-# it grounds>
```

## Trust tiers (load-bearing — never mix silently)
- **authoritative** — vendor docs, exchange specs, filings. Citable as grounds.
- **curated** — vetted methodologies, Adam's own tested notes. Citable, but state the basis.
- **unverified** — social / influencer, un-checked intake. **Quarantined:** readable, but *not* citable as
  grounds for another page until checked against a primary source. Keeps a hype-video "methodology" from
  silently hardening into a design assumption.

## Operations
- **Ingest.** Read the source(s) from `ingest/` (see *The ingest drop-zone* below). The ingesting LLM **follows
  the links it judges relevant, extracts what matters (not a complete scrape), and indexes / points to the
  relevant parts** — writing or updating a reference page, updating `index.md`, appending `log.md`, and
  snapshotting the source to `sources/`. One source may touch several pages.
- **Query.** Read `index.md` first, drill into relevant pages, answer with citations + trust tier. File
  valuable answers back as pages so explorations compound.
- **Lint.** Periodic health check: contradictions, stale claims, orphan pages, missing cross-references,
  dangling `R-#` / `Q-#` links.

## The ingest drop-zone (`ingest/` → `sources/`)
`ingest/` holds sources dropped for ingestion. A source is either:
- a **URL-list file** — plain text, one URL per line. `ingest/urls` is the default; more specifically named
  lists (e.g. `ingest/projectx-urls`) are allowed and treated identically.
- a **document** — an actual file dropped in to be ingested (PDF, markdown, etc.).

**These files are living and editable — never delete or move them.** A URL list may gain or lose URLs; a
document may be replaced with a newer version. The `ingest/` copy is always the current statement of intent.

**After ingesting an item, record it in `sources/` under the same name** — `sources/<name>` reflects the source
*as last ingested*. **URL-list files** are *copied* (they stay in `ingest/` as living, editable lists);
**documents** (PDFs, etc.) are *moved* to `sources/` — they're static and ingested once, so no copy is left in
`ingest/`. Either way:
1. To decide what to ingest, **diff `ingest/<name>` against `sources/<name>`** — anything with no `sources/`
   counterpart (new) or that differs (edited) still needs (re-)ingesting. For a URL list, that's the URLs not
   yet present in the `sources/` copy.
2. Ingest the new/changed items (fetch → extract → index, per *Operations · Ingest*).
3. **Only once ingestion completes, update `sources/<name>`** to match `ingest/<name>`. If ingestion is
   interrupted, `sources/` stays behind so the pending work remains visible.

**To force a re-ingest**, remove the item from `sources/` — delete a URL line from `sources/<name>` (or the
whole `sources/<name>` file). The next diff then treats it as pending and re-ingests it even though `ingest/` is
unchanged — the manual override for "re-scan this URL because the page changed or the last extract was thin."

**Cost:** ingestion is fetch-and-summarize work — **hand it off to a cheaper model (Haiku or Sonnet)** rather
than the top tier. The orchestrating agent delegates the fetch / extract / index and reviews the result.

## Linking rules
- Link liberally; prefer stable IDs / `§`-anchors over bare files.
- Reference pages link to the product `R-#` / `Q-#` they inform (in [`../trading-platform-prd.md`](../trading-platform-prd.md)).
- One authoritative home per concept; link, don't duplicate.

## New page vs. edit
New page when the subject is a distinct entity / concept with its own identity (a venue, a methodology). Edit
when it's a fact about an existing subject. When unsure, prefer a small new page and link it.
