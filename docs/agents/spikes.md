# Spikes

A spike answers one question with evidence before anything ships. Use one when the right design isn't obvious: a new mechanic, a balance change, a transport or rendering choice. Don't use one for a fix whose shape is already clear.

This is how spikes were run in the simstream work (WebSocket vs WebRTC data channel vs RTP, measured side by side) and in Pez (agent-invented tech, the rendering pass, the economy and endgame balance runs).

## Shape

1. **State the question and what would settle it,** before writing code. Example: "Can players invent units without power creep? Settled if no random design beats its base unit per unit of cost."
2. **Work on a branch** named `spike/<topic>`. Commit as you go, one step per commit, with the message prefix `spike:`. The history should show how the answer was reached, including dead ends ("loss emulation didn't take effect").
3. **Measure; don't eyeball.**
   - Commit the harness: headless tests, `--selftest` runs, `arena/record_unit.py`, PerfProbe, bench scripts.
   - Commit the raw results too (`.jsonl` or `.json` beside the write-up), not only the summary.
4. **Control the comparison.** Use the same machine, the same seed or scene, and several runs. Alternate the order of A and B to cancel drift, and report mean, p50 and p95 rather than a single run. Say what was held constant.
5. **Write the results in `docs/spikes/<TOPIC>.md`:**
   - the question;
   - the setup;
   - tables with the numbers;
   - **where it didn't win**;
   - the verdict, with its limits stated plainly (e.g. "a deadlock in our stall handling, not a transport verdict");
   - a recommendation and a phased plan, if it's a go.
6. **Show it.**
   - Make side-by-side recordings or before/after stills of the same scene (`arena/record_unit.py` for MP4s and contact sheets).
   - Publish them to the preview gallery (`arena/preview.sh`).
   - For assets and visual states, check the kitchen sink (`arena/kitchen-sink.sh open`).
7. **Hand off.** For a spike that runs past one session, keep a `continue.md` on the branch:
   - the user's request, quoted;
   - plan and status with timestamps;
   - how to pick it up on another machine, with machine-specific values in a gitignored env file and a committed `.example` template;
   - what isn't pushed, and why.
8. **Close it with a decision.**
   - **Go:** merge the code behind the verdict, or turn the plan into issues.
   - **No-go:** keep the write-up, and don't merge the code.
   - **Unsure:** a draft PR that can switch between the top candidates, with the verdict proposed in its description.

## Pez rules that apply to every spike

- **Never experiment on the live arena.** That means ports 7777/7778, the gateway on 7790 and `unity/Build/`. Use test copies on spare ports, a test gateway with temp state files, and headless selftests. Ports already taken: preview 7957–7959, kitchen sink 7947–7949, test copies 7967/7977/7987, test gateways 7893/7894.
- **Judge game changes against the design principle:** the game is won and lost by strategy, not friction. A spike that adds a mechanic reports what new decision it creates for players.
- **Balance spikes report before/after numbers** from several AI-vs-AI seeds: game length, ore mined, and how games end.
