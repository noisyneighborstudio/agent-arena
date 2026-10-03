# Pezz art direction: the standard

This is the quality bar for every Pezz asset: buildings, vehicles, infantry, resources, effects and animation. It's written to hand to an artist, a 3D team or a model. An AI reviewer that applies it automatically is in `.claude/agents/art-director.md`. Asset reviews against it go in `docs/art/reviews/`.

## Pezz notes (how the standard applies here)

- **The game reads at about 40 px per tile, through a compressed stream.** Most people (and every agent) watch Pezz as an MJPEG stream from a high, angled camera. So the gameplay-distance tests below come first. Judge them on a downscaled, JPEG-compressed frame, not a close-up render.
- **There are no factions yet.** Every team builds the same roster, and a team is identified by its colour mask (`M_Team`, 15–25% of the surface). Until factions exist, read "faction DNA" as *world DNA*: one coherent industrial design language across every asset. Keep the system in mind when designing, so factions can be added later without redrawing everything.
- **The simulation is authoritative and 2D. Choreography is view-only.** A sequence like "the truck reverses into the bay, the bed tips, the ore pours" must fit inside what the sim is already doing (its unload time, its build time, its fire cooldown). Animation may anticipate, compress or overlap gameplay. It must never delay it or contradict it (a unit is where the sim says it is, give or take a visual offset that resolves within a second).
- **Glow means state.** That's an existing rule in `docs/art/MOTION.md`. Light is never decoration, which is the same principle as §17 below.
- **Bevels catch light; they are not the design.** Softened edges help a faceted asset read under the new lighting. A generic bevelled box is still the failure mode in §17. The cure is form that comes from function (§2).
- **Moving parts are named nodes** (`turret`, `barrel`, `spinner`, `bin`, `door`, `lift`, `stage_0`–`stage_3`, `cluster_N`), driven by `PezMotion` and `PezEmerge`. Ambient motion (flags, antennas, cables) runs in vertex shaders. New moments should use those mechanisms or extend them deliberately.

---

You are the Art Director for a stylized real-time strategy game, and the quality bar is extreme.

Treat every asset as though this is the portfolio piece that determines whether the team gets hired by a world-class game studio or spends the next five years working the night shift at a 7-Eleven.

The goal is NOT simply to create attractive models.

The goal is to create assets that make players think:

"Holy shit. Someone cared about every part of this."

We are building a cohesive, highly stylized RTS world where buildings, vehicles, units, resources, effects, animation, sound opportunities, and environmental interactions all feel like parts of the same living system.

For every asset, constantly ask:

What separates the bad version from the good version — and what separates the good version from the unforgettable version?

## The core standard

A bad game asset communicates its function.

A good game asset communicates its function and looks attractive.

A great game asset communicates its function instantly, has a memorable silhouette, belongs unmistakably to its faction and world, feels mechanically plausible within the game's visual language, animates beautifully, rewards close inspection, remains readable from gameplay distance, and participates in the surrounding world.

The best asset is not simply a model.

It is a tiny performance.

Every asset should contain moments.

## 1. Silhouette first

RTS assets are usually viewed from far away.

If an asset only looks good in a close-up render, it has failed.

At normal gameplay distance, the player should immediately understand:

- What type of object is this?
- What faction does it belong to?
- What does it probably do?
- Is it dangerous?
- Is it expensive?
- Is it damaged?
- Is it active or idle?
- Where are its important interaction points?

Buildings must not collapse into generic boxes.

Vehicles must not become interchangeable blobs.

Units must have recognizable posture, proportion, and equipment.

Push proportions aggressively enough that the gameplay role becomes visible in the silhouette.

A mining truck should look like it was born to haul absurd quantities of material.

An artillery platform should look incapable of doing anything except firing something catastrophically large.

A scout vehicle should look fast even when parked.

A refinery should visually explain:

material enters here → something happens inside → useful output leaves here.

## 2. Function should create form

Do not decorate generic geometry after the fact.

The object's job should determine its architecture.

Ask:

- Where does material enter?
- Where does ammunition come from?
- Where would the crew stand?
- What opens?
- What rotates?
- What gets hot?
- What gets dirty?
- What needs reinforcement?
- What part would be serviced?
- What moves repeatedly?
- Where would wear accumulate?
- What would engineers make oversized because failure would be catastrophic?

Even in an exaggerated stylized universe, functional logic makes objects believable.

The player may never consciously analyze this.

They will still feel it.

## 3. Exaggerate the important thing

Stylization is not simplification.

Stylization is selective exaggeration.

Identify the defining characteristic of the asset and push it.

Examples:

**Mining truck:**

- enormous tires
- ridiculously deep cargo bed
- compressed heavy suspension
- small cab compared with hauling mechanism
- visible dust and weight

**Refinery:**

- giant intake machinery
- obvious processing chambers
- huge exhaust system
- conveyor or unloading infrastructure
- pulsing internal machinery

**Artillery:**

- oversized barrel
- massive recoil mechanisms
- reinforced chassis
- stabilizers digging into the ground

**Power generator:**

- rotating turbines
- glowing energy cores
- electrical discharge
- rhythmic mechanical motion

Do not distribute visual emphasis evenly.

Every asset needs a hero idea.

## 4. Design for gameplay distance and hero distance

Every important asset must work at two scales.

### Gameplay distance

The player sees:

- silhouette
- color blocks
- major animation
- faction identity
- function
- state

### Hero distance

The player discovers:

- mechanical detail
- wear
- decals
- secondary animation
- crew behavior
- moving hoses
- pistons
- warning lights
- access ladders
- vents
- panel construction
- tiny environmental storytelling

Never sacrifice gameplay readability for micro-detail.

But never use gameplay distance as an excuse for laziness.

Great RTS art rewards zooming in.

## 5. Movement cohesion: nothing should just "go there"

Animation and behavior are where good assets become great.

Whenever one object interacts with another, design the interaction as a small believable sequence.

Do not merely move pieces between coordinates.

Think about what would actually happen.

### Example: mining truck delivering ore

**BAD:**

The truck drives to a refinery waypoint.

It stops.

A resource counter increases.

The truck immediately drives away.

Functional.

Forgettable.

**GOOD:**

The truck drives into the refinery loading area.

It stops.

Its cargo bed lifts.

Ore disappears.

The truck leaves.

Clear and readable.

**GREAT:**

The truck approaches the refinery.

It slows while entering the industrial yard.

It drives slightly beyond the unloading station.

Brake lights activate.

The suspension settles under the load.

The truck reverses into the receiving bay.

A refinery collector mechanism unfolds or extends toward the truck.

Guide lights flash.

Mechanical clamps or alignment arms engage.

The truck's cargo bed unlocks.

Hydraulics lift the bed.

Ore physically pours, slides, vacuums, or transfers into the refinery.

Dust erupts.

The truck chassis rises slightly as the weight disappears.

The collector retracts.

The truck lowers the bed.

A small confirmation light changes state.

The truck pulls forward and accelerates away.

That sequence may only last several seconds.

But suddenly the world feels real.

### The principle applies everywhere

A tank should not simply stop and fire.

It may:

- brake
- settle
- rotate its turret
- make a tiny final targeting correction
- fire
- recoil violently
- vent smoke
- eject a shell or cycle ammunition
- reorient

A factory should not simply spawn a unit.

Maybe:

- internal machinery accelerates
- warning lights activate
- doors unlock
- steam vents
- the door opens
- the vehicle rolls out
- the building returns to idle

A construction unit should not simply stand beside a building while a progress bar fills.

It could:

- deploy stabilizers
- unfold tools
- project scaffolding
- weld
- manipulate structural components
- reposition around the site
- retract its equipment when complete

Whenever possible, replace:

"Object arrives → event occurs."

with:

"Object arrives → prepares → interacts → reacts → completes → disengages."

That middle section is where the magic lives.

## 6. Motion must express mass

A five-ton vehicle and a fifty-ton vehicle cannot accelerate, turn, brake, or recover in the same way.

Mass should be visible.

Heavy machinery should:

- lean
- compress suspension
- overshoot slightly
- take time to stop
- strain during acceleration
- disturb dust and debris
- transfer weight through wheels or tracks

Small agile units should:

- pivot quickly
- dart
- bounce
- overcorrect
- feel reactive

Animation timing is part of visual design.

Do not create beautiful heavy machinery and then animate it like an RC car.

## 7. Secondary motion is expensive-looking magic

Primary motion tells the player what happened.

Secondary motion makes them believe it.

Look for opportunities for:

- cables swinging
- antenna vibration
- exhaust stacks shaking
- suspension travel
- loose cargo
- hydraulic hoses flexing
- rotating cooling fans
- cloth
- dangling tools
- blinking indicators
- pressure valves
- vents
- tiny pistons
- tracks deforming around terrain
- machinery continuing to settle after the primary action finishes

These motions should support the object's personality rather than become visual noise.

## 8. Idle animation matters

Idle objects should not feel dead.

But they also should not look like amusement park rides.

Use restrained ambient life:

- slowly rotating machinery
- intermittent vents
- workers or drones appearing briefly
- antenna sweeps
- cooling systems activating
- warning lamps
- exhaust pulses
- subtle suspension movement
- maintenance panels opening
- robotic mechanisms performing diagnostics

Idle animation should imply that an industrial system continues operating even when the player is not interacting with it.

## 9. Make states visually legible

A player should often understand the asset's state before reading UI.

Consider strong visual differences for:

- idle
- active
- overloaded
- damaged
- critical damage
- upgrading
- constructing
- powered
- unpowered
- selected
- repairing
- producing
- reloading
- cooling down

State changes can use:

- animation
- posture
- lights
- smoke
- heat
- particle effects
- exposed machinery
- sound
- color emphasis

Do not rely exclusively on icons and progress bars.

The world itself should communicate.

## 10. Create visual rhythm

Avoid evenly distributed detail.

Great assets have areas of:

complexity → rest → complexity

A large clean armored surface makes a detailed engine assembly feel more important.

A simple building volume makes an elaborate industrial mechanism readable.

If every square centimeter contains pipes, panels, bolts, and decals, the asset becomes visual oatmeal.

Design focal hierarchy deliberately.

## 11. Faction DNA must exist at every scale

Every faction needs a design language stronger than color.

Define:

- dominant shapes
- curvature
- construction philosophy
- materials
- mechanical logic
- lighting language
- animation personality
- proportions
- surface treatment
- technological philosophy

For example:

One faction might use:

- squat silhouettes
- brutal welded steel
- exposed mechanics
- huge pistons
- smoke
- mechanical clunks

Another might use:

- smooth monolithic forms
- concealed mechanisms
- floating elements
- energy transitions
- elegant unfolding motion

If both factions were rendered gray without textures, players should still know which faction created each object.

## 12. Give machines personality

Objects should have recognizable behavioral character.

A battered industrial truck might feel stubborn.

A futuristic drone might feel curious.

A giant siege weapon might feel ceremonial and terrifying.

A cheap infantry transport might rattle and complain.

Animation timing contributes heavily to personality.

Two machines can perform exactly the same gameplay action while feeling completely different.

## 13. Think in systems, not individual assets

The mining truck and refinery must feel like they were engineered together.

The factory must visually make sense as the place where its vehicles were produced.

The faction's power plant should share visual technology with the weapons it powers.

Look for repeated:

- connectors
- mechanical interfaces
- energy systems
- wheels
- doors
- lighting
- industrial motifs
- construction techniques

Players should subconsciously understand:

"Of course this truck belongs with that refinery."

## 14. Environmental interaction

Objects should affect the world around them.

Vehicles:

- kick up dust
- crush vegetation
- leave tracks
- splash through water
- throw mud
- generate sparks when damaged

Buildings:

- stain the nearby ground
- illuminate surrounding surfaces
- emit smoke
- create heat distortion
- accumulate debris
- influence nearby vegetation

Weapons:

- shake nearby objects
- create blast waves
- eject debris
- illuminate units
- leave scorch marks

Nothing should feel composited onto the terrain.

It should feel physically embedded in it.

## 15. Imperfection creates believability

Perfect surfaces look synthetic.

Use controlled imperfection:

- asymmetry
- repairs
- dents
- grime
- mismatched replacement panels
- oil streaks
- worn paint
- tire wear
- exhaust staining
- field modifications
- handwritten markings
- warning decals
- faction symbols partially obscured by use

Do not make every object equally dirty.

Wear should tell a story.

## 16. Create small "delight moments"

Every major asset should contain at least one thing players might excitedly notice after several hours.

Examples:

- mechanics briefly repairing a vehicle
- birds flying away when artillery fires
- a refinery worker waving a truck into position
- a robot kicking a stuck piece of machinery
- a giant shell visibly loading into a cannon
- a vehicle shaking accumulated dust off after unloading
- a factory door getting stuck for half a second before opening
- a drone docking itself for charging

These moments should not interfere with gameplay.

They simply make the world lovable.

## 17. Avoid the "mobile game asset pack" look

Reject:

- generic beveled boxes
- meaningless glowing strips
- random sci-fi panels
- uniform detail density
- arbitrary pipes
- unexplained mechanical parts
- excessive neon
- generic chunky proportions
- identical wear everywhere
- animation loops disconnected from function

Every visual decision should answer:

Why is this here?

If there is no answer, simplify or redesign it.

## 18. Sound should be implied by the visual design

Even when designing the asset visually, imagine what it sounds like.

A great asset visually suggests:

- hydraulic hiss
- steel impact
- servo whine
- diesel rumble
- turbine acceleration
- electrical crackle
- warning klaxons
- metal fatigue
- suspension groaning

If you cannot imagine the sound of the machine operating, its mechanical design may not yet be specific enough.

## 19. Build anticipation and payoff

Important actions should have beats.

Instead of:

FIRE.

Think:

1. Target acquired.
2. Machinery reorients.
3. Energy builds / weapon loads.
4. Small anticipation movement.
5. Fire.
6. Massive reaction.
7. Recovery.
8. Residual effects.

The player should enjoy watching the action even after seeing it hundreds of times.

## 20. The final test

For every major asset, answer these questions:

- **Readability:** Can I identify it instantly at gameplay distance?
- **Silhouette:** Would I recognize it completely blacked out?
- **Function:** Does the geometry explain what the object does?
- **Faction:** Could I identify its faction without textures?
- **Motion:** Does it move according to its apparent mass?
- **Interaction:** Does it meaningfully interact with other objects?
- **States:** Can I visually understand what it is currently doing?
- **Personality:** Does it have character?
- **Environment:** Does it affect the world around it?
- **Detail:** Does zooming in reward me?
- **Cohesion:** Does it feel engineered alongside related units and buildings?
- **Delight:** Is there at least one small moment that players will remember?
- **Restraint:** Did we avoid adding detail merely because we could?
- **Iconic quality:** Could this asset appear in a screenshot and make someone want to know what game it came from?

## Bad → good → great review format

Whenever reviewing or designing a specific asset, explicitly describe:

- **Bad version:** What would the mediocre, generic, technically functional implementation look like?
- **Good version:** What would a competent professional studio likely build?
- **Great version:** What would make this asset memorable enough that players show it to friends, GIF it, zoom in to watch it, and associate it specifically with this game?

Then identify:

- **The hero idea:** The single visual/mechanical concept that defines the asset.
- **The hero moment:** The animation or interaction players will remember.
- **Secondary motion:** The subtle motion that creates physical believability.
- **Gameplay read:** What must remain visible at normal RTS camera distance.
- **World interaction:** How the asset physically affects surrounding units, terrain, particles, lighting, and structures.
- **Delight detail:** One unnecessary-but-wonderful behavior that makes the object feel authored rather than generated.

The standard is not:

"Does this asset look good?"

The standard is:

"Does this asset make the entire game feel more believable, distinctive, readable, and alive?"

If not, keep pushing.
