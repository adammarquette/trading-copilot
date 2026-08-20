// task-lifecycle.js — advisory, dry-run orchestration over the GitHub Project board (gh#750, ADR-0028).
//
// WHAT IT IS. The board lifecycle (Backlog → Planning → Current ToDo → In Progress → Review → Done, with a
// Planning kickback and a Review↔Fix loop) is a directed graph: columns are states, the sanctioned transitions
// are edges, each edge has a guard and an owner. This workflow executes that graph as a READER and a PROPOSER —
// it reads the board, routes each actionable card to the node for its column, and asks the mapped role agent, at
// the card's Work-Estimate model tier, to PROPOSE the next transition and draft the artifact that would justify
// it. The output is a proposed-moves ledger a maintainer approves.
//
// THE INVARIANT (ADR-0028, decision 2). It is ADVISORY / DRY-RUN. Every forward gate on the board is human/agent
// judgment by design, so this layer PROPOSES and writes NOTHING to GitHub — the same "enforcement lives below
// the model" posture the risk gate takes, applied to the board. There is NO GitHub-write call anywhere below;
// `apply` is read only so a future apply-mode increment (an ADR-0028 follow-up) has a single, explicit door.
//
// Run it via the Workflow tool. It is safe to run at any time: it mutates neither the board, the issues, nor any
// branch. Its only cost is the model calls it makes to read and to propose, bounded by the per-card tier routing.

export const meta = {
  name: 'task-lifecycle',
  description: 'Advisory dry-run orchestration over the board: read state, route each card to its role agent at its Work-Estimate tier, propose the next move — writes nothing to GitHub (gh#750, ADR-0028)',
  phases: [
    { title: 'Read', detail: 'a board-reader returns structured board state: column, tags, container-vs-task, and claim liveness (live / stale / none)' },
    { title: 'Propose', detail: 'route each actionable card to its role agent at its Work-Estimate model tier; propose the next edge + the drafted artifact that justifies it' },
    { title: 'Ledger', detail: 'assemble the proposed-moves ledger and the board-hygiene report; emit them, write nothing' },
  ],
}

// ---------------------------------------------------------------------------------------------------------------
// Configuration. `args` is the value passed to the Workflow tool; everything has a safe default so a bare run
// works. NOTE: this script never calls Date/Math.random (they are unavailable in workflow scripts and would break
// resume) — the one time-dependent decision, claim staleness, is made inside the reader agent, which has a shell.
// ---------------------------------------------------------------------------------------------------------------
const REPO = (args && args.repo) || 'adammarquette/trading-copilot'
const PROJECT_NUMBER = (args && args.projectNumber) || 2          // github.com/users/adammarquette/projects/2
const STALE_AFTER_HOURS = (args && args.staleAfterHours) || 4     // CONTRIBUTING.md claim-staleness window
const APPLY = !!(args && args.apply === true)                     // dry-run unless EXPLICITLY enabled; see the invariant
const LIMIT = (args && Number.isInteger(args.limit) && args.limit >= 0) ? args.limit : null // cap routed cards for a cheaper bounded dry-run; null = the whole board

// The board's actionable columns — the ones from which a concrete next move exists. Backlog is the intake
// reservoir (nothing is pick-up-able there; the maintainer pulls items into Planning) and Done is terminal, so
// neither is routed as work; the reader still reports them so the ledger's board-hygiene view is complete.
const ACTIONABLE_COLUMNS = ['Planning', 'Current ToDo', 'In Progress', 'Review']

// Work Estimate → model tier (project-board-workflow "Tagging & model routing"; work-estimate-rubric.md). The
// `safety-critical` label floors the tier at top regardless of the nominal score — safety can only RAISE it.
// Tiers are named as tiers, not model ids, so the mapping survives model changes; the model id is resolved here.
function tierFor(issue) {
  if (issue.safetyCritical) return { model: 'opus', effort: 'high', tier: 'top (safety-critical floor ≥4)' }
  switch (issue.workEstimate) {
    case 1:  return { model: 'haiku',  effort: 'low',    tier: '1 — trivial/mechanical' }
    case 2:  return { model: 'haiku',  effort: 'medium', tier: '2 — simple' }
    case 3:  return { model: 'sonnet', effort: 'medium', tier: '3 — moderate' }
    case 4:  return { model: 'opus',   effort: 'high',   tier: '4 — complex' }
    case 5:  return { model: 'opus',   effort: 'max',    tier: '5 — critical/deep' }
    // An actionable card with no Work Estimate is itself a board defect (it cannot be routed by the rubric); read
    // it at a mid tier and let the proposal flag the missing estimate rather than guessing high or low.
    default: return { model: 'sonnet', effort: 'medium', tier: 'unestimated (defect — propose adding one)' }
  }
}

// The role that owns a card, from its work:* label (project-board-workflow "Tagging & model routing").
function roleFor(issue) {
  if (issue.labels.includes('work:qa')) return 'QA Agent'
  if (issue.labels.includes('work:platform')) return 'Platform Agent'
  if (issue.labels.includes('work:docs')) return 'any (docs)'
  if (issue.labels.includes('work:code')) return 'Coding Agent'
  return 'unrouted (no work:* label — propose tagging it in Planning)'
}

// ---------------------------------------------------------------------------------------------------------------
// Schemas. Validated at the tool-call layer, so a subagent retries on mismatch and the script gets clean data.
// ---------------------------------------------------------------------------------------------------------------
const BOARD_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['issues', 'hygiene'],
  properties: {
    issues: {
      type: 'array',
      items: {
        type: 'object',
        additionalProperties: false,
        required: ['number', 'title', 'column', 'labels', 'safetyCritical', 'isContainer', 'claim'],
        properties: {
          number: { type: 'integer' },
          title: { type: 'string' },
          column: { type: 'string', description: 'the board Status column, or "(none)" if the card carries no Status' },
          labels: { type: 'array', items: { type: 'string' } },
          workEstimate: { type: ['integer', 'null'], description: 'the Work Estimate 1–5, or null if untagged' },
          safetyCritical: { type: 'boolean' },
          // Explicit container discrimination (ADR-0028 decision 4): an epic, OR any task that has decomposed into
          // sub-issues, is a container — it is never routed as a task; its children are the units that flow.
          isContainer: { type: 'boolean' },
          containerReason: { type: ['string', 'null'], description: '"epic" | "has sub-issues" | null' },
          // Explicit claim discrimination (ADR-0028 decision 4): the pushed <type>/<id>_… branch is the claim; a
          // tip unmoved past the staleness window is "stale", not "live" and not "none".
          claim: {
            type: 'object',
            additionalProperties: false,
            required: ['state'],
            properties: {
              state: { type: 'string', enum: ['none', 'live', 'stale'] },
              branch: { type: ['string', 'null'] },
              ageHours: { type: ['number', 'null'] },
            },
          },
          hasLinkedPr: { type: 'boolean' },
          prNumber: { type: ['integer', 'null'] },
          prState: { type: ['string', 'null'], description: 'open / merged / approved+green / changes-requested, as the reader can determine read-only' },
        },
      },
    },
    // The three board-hygiene shapes Adam's gh#750 pass found by hand, surfaced explicitly (ADR-0028 decision 4).
    hygiene: {
      type: 'object',
      additionalProperties: false,
      required: ['redundantContainers', 'driftedEpics', 'staleClaims'],
      properties: {
        redundantContainers: { type: 'array', items: { type: 'string' }, description: 'a container whose only child restates it' },
        driftedEpics: { type: 'array', items: { type: 'string' }, description: 'an epic whose body checklist and sub-issue tree disagree' },
        staleClaims: { type: 'array', items: { type: 'string' }, description: 'a claim branch whose tip has not moved in the staleness window' },
      },
    },
  },
}

const PROPOSAL_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['number', 'column', 'proposedEdge', 'guardMet', 'owner', 'tier', 'rationale', 'draftedArtifact'],
  properties: {
    number: { type: 'integer' },
    column: { type: 'string' },
    // The edge proposed, in "From → To" form, or "stay" (guard not yet met) or "kickback → Planning" (the one
    // sanctioned backward edge) or "escalate → maintainer" (the Review↔Fix loop cap).
    proposedEdge: { type: 'string' },
    guardMet: { type: 'boolean', description: 'does the edge guard hold right now?' },
    owner: { type: 'string', description: 'who is allowed to make this move (worker / maintainer / reviewer)' },
    tier: { type: 'string', description: 'the model tier this proposal was produced at' },
    rationale: { type: 'string' },
    draftedArtifact: { type: 'string', description: 'the artifact that would justify the move — a readiness assessment, a plan sketch, a kickback question list — NOT the full implementation' },
  },
}

// ---------------------------------------------------------------------------------------------------------------
// Phase 1 — read the board. One reader agent, read-only, returns the whole structured state.
// ---------------------------------------------------------------------------------------------------------------
phase('Read')
const board = await agent(
  `You are the board-reader for the task-lifecycle workflow (gh#750, ADR-0028). Read the GitHub Project board and
every open issue for repo ${REPO}, project #${PROJECT_NUMBER}, and return the structured board state.

READ-ONLY, ABSOLUTELY. Use only read commands: \`gh issue list\`, \`gh api\` GET, \`gh api graphql\` queries,
\`git ls-remote\`, \`git log\`. Never a POST/PUT/PATCH/DELETE, never a board mutation, never a push. You are
reporting the board, not changing it.

For EVERY open issue, resolve:
- its board Status column (Backlog / Planning / Current ToDo / In Progress / Review / Done, or "(none)"),
- its labels, its Work Estimate (1–5, or null), and whether it carries \`safety-critical\`,
- **container vs task** (ADR-0028): it is a container if it carries the \`epic\` label OR it has sub-issues — a
  container is NOT a unit of work; set isContainer=true and containerReason. Everything else is a task.
- **claim liveness** (ADR-0028): the claim is the pushed branch \`<type>/<id>_…\` (list remote heads, match
  \`^[a-z]+/<number>_\`). If none → state "none". If one exists, get its tip commit time and compute its age; a
  tip older than ${STALE_AFTER_HOURS} hours is "stale", otherwise "live". Compute the age with the shell's own
  clock (e.g. \`date -u +%s\` minus the tip's committer epoch) — do the arithmetic yourself and report ageHours.
- whether a linked PR is open, and its state as far as a read can tell (open / merged / approved+green /
  changes-requested).

Also assemble a **board-hygiene** report of the three drift shapes gh#750 exists to catch: containers redundant
with their only child; epics whose body checklist and sub-issue tree disagree; and stale claim branches.

Return the full structured object. gh needs its PATH — the caller's environment already has it.`,
  { label: 'board-reader', schema: BOARD_SCHEMA },
)

const all = (board && board.issues) || []
log(`board read: ${all.length} open issue(s); ${all.filter(i => i.isContainer).length} container(s); ` +
    `stale claims: ${board.hygiene.staleClaims.length}, drifted epics: ${board.hygiene.driftedEpics.length}, ` +
    `redundant containers: ${board.hygiene.redundantContainers.length}`)

// ---------------------------------------------------------------------------------------------------------------
// Phase 2 — route + propose. Containers never flow (their children do), and only cards in an actionable column
// have a candidate next move — so those are what get routed. Each is dispatched to its role agent at its
// Work-Estimate tier to propose the next edge, read-only.
// ---------------------------------------------------------------------------------------------------------------
phase('Propose')
let routable = all.filter(i => !i.isContainer && ACTIONABLE_COLUMNS.includes(i.column))
if (LIMIT !== null && LIMIT < routable.length) {
  log(`limit=${LIMIT}: routing the first ${LIMIT} of ${routable.length} actionable card(s) — a bounded dry-run`)
  routable = routable.slice(0, LIMIT)
} else {
  log(`routing ${routable.length} actionable card(s) (skipping ${all.length - routable.length}: containers, Backlog, Done, unstatused)`)
}

// A barrier: every proposal is collected before the ledger is emitted. Each thunk that throws resolves to null,
// so the call never rejects — filter(Boolean) before use.
const proposals = (await parallel(routable.map(issue => () => {
  const t = tierFor(issue)
  const role = roleFor(issue)
  return agent(
    `You are the ${role} for issue #${issue.number} ("${issue.title}"), which sits in the board column
"${issue.column}". Propose its next task-lifecycle move per ADR-0028 and project-board-workflow.md.

This is ADVISORY and READ-ONLY. Read whatever you need with gh/git READ commands to judge the guard; then
PROPOSE — do not perform the work, and never write to GitHub.

The candidate edges out of each column (evaluate the guard for yours):
- Planning → Current ToDo: guard = scoped clearly enough to implement without a blocking question, AND tagged
  (work:* + Work Estimate; safety-critical floors the tier). If the guard fails, propose "stay" and draft the
  precise readiness gap. Owner of the actual move: maintainer.
- Current ToDo → In Progress: guard = unclaimed (no live claim branch) and matches a worker's role/tier. If a
  LIVE claim exists, propose "stay" (taken). If a STALE claim exists, propose "stay" but flag it for takeover
  after announcing on the issue. Draft a short plan sketch for the pickup. Owner: the worker.
- In Progress → Review: guard = a linked PR is open. If instead the card has too many unresolved questions to
  finish correctly, propose "kickback → Planning" with the blocking-question list as the artifact. Owner: the worker.
- Review → Done: guard = the linked PR is merged. If it is open with changes-requested, propose "stay" (address
  findings) — but this is a BOUNDED loop: if the card has clearly cycled Review↔Fix past a reasonable cap, propose
  "escalate → maintainer" instead of looping. Owner: maintainer (merge).

Your model tier for this card is ${t.tier}. Return the proposal object: the edge (in "From → To" form, or
"stay" / "kickback → Planning" / "escalate → maintainer"), whether the guard is met, the move's owner, your tier
"${t.tier}", the rationale, and the drafted artifact (the readiness assessment / plan sketch / question list —
NOT the implementation).`,
    { label: `propose:#${issue.number}`, phase: 'Propose', model: t.model, effort: t.effort, schema: PROPOSAL_SCHEMA },
  )
}))).filter(Boolean)

// ---------------------------------------------------------------------------------------------------------------
// Phase 3 — the ledger. Emit; write nothing. Apply-mode is a deferred ADR-0028 follow-up: even when the flag is
// set, this increment performs NO GitHub write — the flag exists only so that follow-up has one explicit door.
// ---------------------------------------------------------------------------------------------------------------
phase('Ledger')
const moves = proposals.filter(p => p.proposedEdge && p.proposedEdge !== 'stay')
log(`proposed ${proposals.length} evaluation(s); ${moves.length} propose a move, ${proposals.length - moves.length} propose "stay".`)
if (APPLY) {
  log('NOTE: apply=true was passed, but this increment performs NO GitHub writes (ADR-0028: apply-mode is a ' +
      'follow-up increment). The ledger is emitted; the board is untouched.')
}
log(`GitHub writes this run: 0 (dry-run${APPLY ? ', apply requested but not implemented' : ''}).`)

return {
  dryRun: true,             // this increment is always dry-run, by construction (ADR-0028)
  githubWrites: 0,
  proposedMoves: proposals,
  boardHygiene: board.hygiene,
}
