# Command plan UI review — 2026-10-14

P7 gives a commander a readable record of their agent's strategy, current objectives, and the latest guard interventions. Spectators see every team's record through the existing delayed feeds. The record never executes commands. This change adds text panels only: no model, icon, effect, animation, dependency, or gameplay judgement.

## Existing family and interaction

The Plan tab shares the existing side drawer with Base. It uses the shipped Chakra/Plex fonts, dark panels, cream selection, muted section headings, and amber paused states. Native spectator team tabs remain; the classic spectator selects a team inside Plan. Base retains production, stockpile, standing orders, and the commander editor.

The hero idea is the link between recorded intent and the instrument carrying it out: plan version first, then strategy, objectives, and interventions. The memorable interaction is finding a paused objective's exact reason beside its guard history. No secondary motion or world interaction was added; this is a read-only record.

Strategy and priorities render literally. Unity uses `richText=false`, measures that same style, and clips inside a real scroll viewport. HTML escapes every authored field and wraps long words. Both retain full text for scrolling. Objective summaries identify the current patrol waypoint after assembly releases, explicitly label the route start before release, and show escort radius.

## Evidence

All images use synthetic local records. No private view link, production room, ops key, or real human standing order was used.

- Desktop classic: [before](command-plan-ui/desktop-before.png), [empty](command-plan-ui/desktop-empty.png), [populated](command-plan-ui/desktop-populated.png), [paused](command-plan-ui/desktop-paused.png), [long](command-plan-ui/desktop-long.png), [markup](command-plan-ui/desktop-malicious.png).
- Phone classic: [before](command-plan-ui/phone-before.png), [empty](command-plan-ui/phone-empty.png), [populated](command-plan-ui/phone-populated.png), [paused](command-plan-ui/phone-paused.png), [long](command-plan-ui/phone-long.png), [markup](command-plan-ui/phone-malicious.png).
- Classic spectator: [team selection](command-plan-ui/desktop-spectator.png), [current patrol point](command-plan-ui/desktop-patrol.png).
- Actual native Unity HUD: [current Base-tab controls](command-plan-ui/native-before.jpg), [empty](command-plan-ui/native-empty.jpg), [populated](command-plan-ui/native-populated.jpg), [paused](command-plan-ui/native-paused.jpg), [long](command-plan-ui/native-long.jpg), [markup](command-plan-ui/native-malicious.jpg), [sanitized public spectator record](command-plan-ui/native-spectator.jpg).
- [Recorded browser viewport and clipping checks](command-plan-ui/capture-checks.json).
- [Sanitized spectator privacy assertions](command-plan-ui/spectator-privacy-check.json): two teams; private standing orders/config orders/marker/API section absent; public strategy retained.

The HTML captures execute the actual `mcp/viewer.html`, served by a local synthetic API, at 1920×1080 and 390×844 CSS pixels. Long phone content ends above the command feed and remains scrollable. During review, a phone overlap was found and corrected by sizing the drawer against the measured bottom stack. Expanding the command feed also reduces the available drawer height. Switching back to Base restores the editor; a synthetic send succeeds. Spectator team selection shows the second team's paused objective and the existing 45-second delay label, with no editor or private standing-order text.

The native Base-tab reference already contains the new Plan tab; it is a controls check, not the pre-P7 page. Native captures run the actual shipped HUD drawing code in an isolated macOS app, built from an APFS project clone. A scratch-only harness supplies records, selects the own-team tab, and requests screenshots. The public-spectator case restores `ApiServer.ViewSnapshot(game, -1)` into the native renderer: it is a sanitized mirror, not the raw operator world. Native long and markup states are inspected at normal game distance; clipping contains text inside the side panel.

The privileged native host operator keeps the pre-existing standing-order editor and read workflow. That is different from a public spectator. Public viewers use delayed, sanitized snapshots/frames; standing orders, config orders, and private orders-event text remain stripped. Live player frames expose only `you.planning`; rivals' `planning` is null. Public spectator frames expose every team's planning only after the gateway delay.

## Checks and limits

- `node --test mcp/planning-view.test.js`: 4/4 passed. Checks empty/old records, paused reason and spending, literal long/malicious fields, bounded guard history, assembly, escort radius, and current patrol position.
- `node --test mcp/strategy.test.js`: 3/3 passed, including real gateway delay: own record immediate, rivals absent, public record old until the two-second test delay passes.
- Viewer inline JavaScript parses; `git diff --check` passes.
- Root's required Unity clone compile passes. The isolated native app build succeeds with zero errors.
- Root owns full headless verification and final deployment; the phase handoff records those results.
- Actual Unity WebGL phone rendering and its HTML commander overlay were not captured. Those paths were inspected statically and compiled: Plan returns with the overlay rectangle cleared, and Base computes its overlay from the rendered layout. Classic phone captures do not establish Unity WebGL phone visual parity.
- The legacy three.js `/3d` surface retains its link to the main viewer; planning is inspected there through that link.

## Art-director assessment

An independent GPT-6.1 review inspected the classic state capture set and all seven actual native screenshots against `ASSET_GUIDE.md` and `ART_DIRECTION.md`, including a compressed native empty-state check. Final visual/privacy signoff found no blocker; the sanitized spectator assertions independently confirm the private-data boundary.

**Bad:** an opaque wall of text, unclear ownership, or displayed intent treated as executable instructions. **Good:** a clear own-team record, separate Base controls, literal text, and an amber pause reason. **Great:** exact recorded intent and executor state inside the existing restrained HUD family, with useful history and no invented world animation.

UI-applicable scores: readability 3, function 3, states 3, interaction 2, cohesion 3, detail 2, restraint 3 — **19/21**. Interaction loses a point because the HTML tabs can scroll out of view and must be recovered by scrolling up. Detail is intentionally limited to concise intervention summaries. Physical silhouette, mass, environmental effects, transform clearance, and model/icon pipeline criteria are inapplicable to this text-only change.

| Criterion | Score | Evidence |
|---|---:|---|
| Readability | 3 | Phone and native captures preserve section hierarchy and readable literal text inside bounded panels. |
| Function | 3 | Current patrol waypoint/index and escort radius match source fields; the patrol capture identifies point 2/2. |
| States | 3 | Empty records name what is missing; paused objectives show their reason in amber. |
| Interaction | 2 | Native tabs remain outside the scroll; HTML tabs can be recovered by scrolling up. |
| Cohesion | 3 | Cream selections, dark panels, and the existing fonts sit beside the shipped Base, minimap, and feed. |
| Detail | 2 | Intervention rows retain time, rule, action, and reason; the UI deliberately summarizes deeper facts. |
| Restraint | 3 | No models, icons, effects, or animations were added. |

No owner's rule breach was found in the changed UI. The empty native Plan viewport leaves unused space; that is a small polish opportunity, not a readability or gameplay defect. There is no model approval gate for this scoped, explicitly authorized text-panel shipment.

## Reproduce the classic review

Run `node docs/art/reviews/command-plan-ui/fixture.mjs`. It prints its own PID and free loopback port. Open `/view/fixture/populated?c=review` on that port; substitute `empty`, `paused`, `long`, or `malicious`. `/watch/fixture/populated` is the synthetic spectator. `?before=1` loads the committed pre-P7 page for comparison. These paths contain public fixture words, never live seat tokens.

Use T3's collaborative preview, resize to the intended viewport, open the side drawer, and select Plan. Check `screen.width` as well as `innerWidth`: this preview reset the emulated screen width after navigation, so toggling resize from fill to freeform before each capture was necessary. Stop only the printed fixture PID.
