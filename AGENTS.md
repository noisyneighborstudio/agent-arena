# Agents

## Agent skills

### Issue tracker

Issues and specs live in GitHub Issues on noisyneighborstudio/agent-arena (gh CLI). See `docs/agents/issue-tracker.md`.

### Triage labels

The five default triage labels: needs-triage, needs-info, ready-for-agent, ready-for-human, wontfix. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `CONTEXT.md` and `docs/adr/` at the repo root (created lazily). See `docs/agents/domain.md`.

### Art and motion

Any change to how something in the game looks or moves (a model, an animation, an effect, a dock, an icon, an exhibit)
follows `docs/art/ASSET_GUIDE.md`: the house style, the owner's rules, the pipeline and tools in `art-src/tools/kit/`,
verification in the kitchen sink, the review pack the owner approves before anything ships, and how to ship. Read it
before starting, and check your work against its definition of done. The quality bar it applies is
`docs/art/ART_DIRECTION.md`; the art-director agent (`.claude/agents/art-director.md`) reviews against both.

### Spikes

One question per spike, answered with controlled, committed measurements and a results write-up in `docs/spikes/` that states where it didn't win and ends in a decision. Never on the live arena. See `docs/agents/spikes.md`.
