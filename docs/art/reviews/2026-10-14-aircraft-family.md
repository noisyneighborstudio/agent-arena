# Aircraft family: six models and their real acts

Owner review pending. This batch covers recon drone, long-range drone, transport chopper, gunship, stealth bomber and Reaper. It starts from main `31f77b5`; no gameplay statistics, weapon timing, fuel capacity, stealth rule or landing-slot rule changes.

## One world

The reference is the shipped candy chassis, candy trooper and airfield: rounded cream plastic, a substantial team crown, wrapper bands, smoke-plastic connectors and restrained spring steel. A shared `candy_airframe` establishes the body and mounts; the crown, guarded fan, wing and skid helpers retain the same defaults across variants. Caps stay shut where a dispenser act has no functional purpose. There is no new decorative emission.

The kitchen sink places all six beside a full-size rifleman, light tank and barracks. Blueberry and Cherry rows use the same actual camera, materials and pixel scale. Each also has a real landing/refuelling/takeoff loop; the aircraft crash slot cycles all six and boards six troops before destroying a chopper.

## Role, states and story

| Aircraft | Hero and real action | Rounded triangles |
|---|---|---:|
| Recon drone | A candy sensor head carried by four guarded fans; accelerating and braking tilt the existing body, its ground-survey head looks around, and actual landing stops the fans. Factory handoff remains intact. | 5,384 |
| Long-range drone | A dedicated endurance airframe with fuel saddles and a longer stem. Its distinct fan stance stays inside the existing landing envelope. It uses its own model, without changing sight or range. | 7,256 |
| Transport chopper | The APC case takes flight: six full-size seated hints behind clear panes, an actual rear aperture and a hoist. Ground approach → full-size lift → concealed threshold handoff → seated hint; reversed seat by seat on unload. | 8,948 |
| Gunship | An armored candy rotorcraft with two visible wrapped rocket cartridges. One authoritative event launches one rocket from the selected mouth, followed by recoil/backblast and the actual cooldown's reload. | 6,860 |
| Stealth bomber | A swept candy wing opens its underside shutters around a permanent release throat. One candy becomes the actual bomb projectile, then the replacement feeds from the concealed cavity. No invented salvo or minimum-speed rule. | 3,892 |
| Reaper | A separate straight-wing, pusher-propeller hunter with a sensor chin and two hellfire rails. Actual shots alternate mouths, and the fired rail reloads under its pylon. Its five-tile visual altitude and high-altitude targeting rule remain intact. | 4,356 |

All six show airborne movement, idle/hover, real landed/refuelling state, production handoff and destruction. Armed variants use actual events and cooldowns. The transport's simulation boards and unloads while airborne; its view-only hoist adds no simulated landing or gameplay delay. The last of six resolves its visual handoff in 2.18 seconds. The transport exceeds an approximate 8k light-aircraft budget to accommodate a hollow full-height cabin, six occupancy hints and full-size infantry clearance.

## Ownership and visibility

WorldView alone retains root altitude, interpolation, spawn and body banking. ScoutAircraftWorks owns only its prefixed fans and sensor joints. RotorcraftWorks owns its private doors, hoist and occupancy hints; PezMotion retains the generic main rotors and gunship recoil. StrikeAircraftWorks owns payload, shutter, elevon, propeller and sensor joints. Model-key dispatch preserves these acts when an invention borrows a chassis.

The transport's private occupancy is restored per camera and erased before its shared hulk detaches. Moving unload paths follow the stern; losing the carrier preserves hoist timing and missing-carrier recovery clears stale paths. Strike muzzle caching lasts only through the firing frame, so fresh and rebound snapshots with existing projectiles use a current authored mouth.

Deferred aircraft effects now retain an audience snapshot through falling fire/smoke, hulk rendering, impact, cookoff, smoulder, debris and flash lights. Per-camera light suppression does not make a live pooled light available for reuse. Null audience preserves unrestricted effects; empty audience shows none. These fixes preserve the existing particle recipes and physics.

## Verification

- Native Unity player builds succeed with zero compiler errors.
- The full headless suite and final six-minute kitchen-sink test pass. New scenario checks require all six to land/refuel, all six to emit their destruction event, and all six transport seats to fill.
- Ninety-six native actual-model regression checks pass for dedicated models/heights, inventions, snapshot muzzles, projectile size/alternation, scout landing, visibility, six occupants, camera privacy, hoist scale, moving/missing carriers and wreck normalization.
- Thirty-eight native crash-audience regressions pass after reproducing the baseline particle, deferred-effect and hulk visibility failures.
- Scouts sample rounded rotor/sensor geometry; the strike pair samples 362 hardware states and 56,589 convex pairs, plus 15,552 relative landing-yaw cases. Rotorcraft sample 1,444 rotor phases, 202 door poses, 242 reload poses, seven full-size infantry envelopes and seated pairs. No unexpected contacts remain; bearing, hinge and concealed feed contacts are documented in the review pack.
- Continuous horizontal envelopes fit the unchanged 1.5-tile pad spacing at arbitrary relative headings, including the full rotor sweeps. This replaced the initial insufficient width-only check.
- All unrelated existing builder functions/classes are unchanged. Sixty other GLBs and 64 other icons remain byte-identical to the base. Motion splices, Python syntax, three browser-client syntax checks and whitespace checks pass.

## Limits and owner decision

Clearance is sampled geometry plus conservative envelope reasoning, not a continuous physics proof for every moving-unit arrangement. The transport's bounded visual hoist offset is deliberate and requires the owner's visual judgment. Browser prototype model aliases now use the dedicated assets; their separate animation implementations were not visually verified here. WebGL deployment remains a shipping check after the required GIF approval.

The owner should judge six actual native action GIFs, the same-scale family scene, before/after models and icons, state contact sheets, and the shared flight/refuelling/launch/crash clips before approving these models to ship.

## Pass 2: owner feedback, a middle between old and new

The owner found the first pass too far from the originals for the recon drone, stealth bomber and gunship, and asked for a
middle. The shared cream box airframe and team crown read as one generic brick on all three. Pass 2 restores each
original silhouette and keeps the first pass's engineering:

- **Recon drone:** the original rounded team pod over a smoke belly, with a big chin eye, now on the guarded candy fans.
  The long-range drone follows as its stretched sibling, with fuel saddles.
- **Stealth bomber:** a dark faceted licorice flying wing again, with team leading edges and elevons and a low smoke
  canopy. The belly bay, shutters and single candy bomb are unchanged.
- **Gunship:** the original slim smoke tandem fuselage, team nose and boom fin, a stepped canopy, and cream stub wings
  carrying the two open rocket cartridges at 0.6 scale. The three-blade rotor and tail rotor are kept.

Node names and Works ownership are unchanged. The scout, strike and rotorcraft clearance sweeps pass, and so do all 96
native aircraft probes.
