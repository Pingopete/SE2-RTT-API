# Perf sprint — 2026-08-07 (autonomous session)

Test bed: the user's valley save. Feed 0, manual camera FROZEN at 34953,-38102,-207519
(verified static across samples), looking across a valley of voxel mountains, grassy
meadows, trees, bushes and LOD foliage. Player seated in cockpit facing the panel,
game at 4K, RTX 5080. All soaks 90 s, game focused (the unfocused game throttles to
~14 fps and grades nothing — first thing verified).

Protocol: one knob at a time, restore to baseline between families, PERF lines
aggregated over exactly the soak window (`output/perf-sprint-results.csv`). Baseline
drift between windows is real (residency settling, heap): ±1-2 fps. Trust big deltas,
re-run anything inside the noise.

## The frame economics (measured first, before touching anything)

| condition | main fps | frame ms | notes |
|---|---|---|---|
| feed ON, baseline (1024x1024 SSAA, interval 0) | 41.2-43.6 | 23.0-24.2 | renders 43/s, delivered to panel ~21/s |
| MOD LOADED, feed dormant (feedsDisabled=1) | 53.9 | 18.5 | NOT a no-mod baseline — world-side machinery + player-frame patches still running |
| no mod at all | ~70 (user-reported) | ~14.3 | the true ceiling of this scene |

- **The feed costs ~5 ms/frame ≈ 12 fps** at baseline settings in this scene.
- **THE MOD'S AMBIENT MACHINERY COSTS ~4.2 ms/frame (~16 fps) with NO feed rendering**
  (corrected 2026-08-08 after the user flagged the mislabeled "ceiling": 70 → 53.9 with
  the feed dormant). That pool is BIGGER than the feed's own cost, and it is pure
  overhead while dormant: the per-frame/probe-pass hooks (~400 dispatches/s), the
  world-side residency work (flora camera claims ~6.5k/s, presence/trigger/preload),
  CollectStandards even slimmed (~110k/s), the tier-churn counter, censuses, per-stage
  patch prefixes on every PLAYER frame. Task #40 is therefore the TOP target now —
  ahead of all feed-side work — starting with a dormant-state profile: what still runs
  when nothing renders, and gating all of it on feed activity.
- Delivery to the panel runs at ~21/s, NOT the 30/s the 33 ms panel gate implies: the
  gate is sampled on ~23 ms frame ticks, so it beats down to every-2nd-frame. The
  panel's real refresh is 21 Hz today. (Credit-based gate fix designed — see #25.)

## Finding 1: cutting render rate alone buys NOTHING (the O(gap) stall)

interval50 (renders 43/s -> 17/s, a 2.6x cut): main fps UNCHANGED (42.6 vs 43.6
fresh baseline, inside noise). Feed fps fell to 17 as expected.

Why: per-render CPU submit inflated **2.5 ms -> 17.8 ms** (7x) and render-frame wall
stretched 23.8 -> 37 ms. The nested Draw flushes GPU queues and joins the present
queue internally; at every-frame cadence that flush meets a drained queue (steady
pipelining), but a sparse render lands mid-player-frame against a FULL queue and the
render thread sits inside our Draw waiting for the drain. Aggregate submit cost
TRIPLED (108 -> 303 ms/s).

Implication chain:
- Render-on-demand (#25) pays only if the flush stall is fixed first.
- The existing `wholeSceneSubmitEarly=1` knob (record ours in Draw's PREFIX, GPU
  overlaps the player's CPU recording) is the designed counter — testing next.
- Transient-CB reclaim exonerated (bounded list swap, not O(gap)).

## Ladder results

| rung | main fps | feed fps | submit ms | verdict |
|---|---|---|---|---|
| baseline 1024² int0 (first) | 41.2 | 41.3 | 2.94 | reference; drifted to ~43.6 after the feed-off rebuild |
| feed OFF | 53.9 | — | — | the ceiling |
| interval50 (renders 17/s) | 42.6 | 17 | 17.74 (!) | ZERO gain — the O(gap) submit inflation eats it all |
| farclip 2500→1200 | 44.1 | 44.2 | 2.97 | zero cost — planet bodies are VeryFarClipping-exempt, so this scene has nothing in the cut band. Mountains still render (screenshot-verified) |
| submitEarly=1, int0 | 44.0 | 43.6 | 3.64 | neutral at every-frame cadence |
| submitEarly=1 + interval50 | **38.3** | 18.1 | 13.32 | WORSE — the early position aggravates sparse renders; O(gap) inflation is position-independent |
| grass 1000→300 (feed only) | 44.1 | 44.4 | 2.98 | zero cost — grass is GPU-light at 1024² |

**Reading so far**: every content-distance knob is ~free in this scene, and cadence
cuts are self-defeating until the O(gap) inflation is understood. The feed's ~5 ms is
FIXED pipeline overhead per render — the stage table (built this session, deploys at
the next restart) will name the stage. The one untested big lever is resolution
(pixels ÷4 at 512²) — gate-cycling to it now. Flora/viewer rungs skipped by
inference from the grass/farclip nulls (same family; viewer radius is a VRAM/quality
knob more than an fps knob).

New suspect for the O(gap) inflation, to check against the stage table: stage 2's
probe manager re-rendering ACCUMULATED dirty probe faces per sparse pass (a probe face
is a mini scene render — exactly submit-shaped cost that scales with elapsed frames).

## THE HEADLINE NULL: resolution

| rung | main fps | ours ms | verdict |
|---|---|---|---|
| res 1024→512 (gate cycle, ¼ the pixels) | 45.3 | 22.1 | +1.2-1.7 fps of a ~10 fps gap |

CORRECTED after a dateless-log misread (the first grade cited a PREVIOUS DAY's PERF
lines — rtt.log has no dates and spans sessions; the resume script's comments document
this exact trap and it still caught this session's tooling. Anchor log reads to line
offsets, never to time-of-day regexes). The window-matched soak is the valid number:
quartering the pixels buys at most ~1.5 fps. The conclusion survives: the feed's cost
is dominated by fixed per-render pipeline overhead, not pixel work. Restored to 1024
(512 shows visible alpha-test speckle on foliage for that ~1.5 fps — a preset choice,
not a default).

Observed during the cycles: the [RTS] stats panel lost its content after repeated
quiesced rebuilds (feed panel unaffected) — the known re-bind fragility family (#26/#31
neighborhood), restart restores it. (An earlier claim here that every config save
triggers a rebuild was the same dateless-log misread, retracted.)

## The stage table (instrumented build, session 2)

Per-render CPU submit at DENSE cadence (42/s): total 2.60 ms. MainView 0.91, Shadows
0.44, TLASBuild 0.39 (an EMPTY structure rebuilt every render — build-once fix landed),
EnvProbe 0.28, Lighting 0.17, everything else ≤0.08. No single dominant stage.

**The O(gap) mechanism, named**: at sparse cadence ONLY the dispatch-heavy stages
inflate — MainView 0.91→3.8 (gap-1)→5.5 ms (gap-2), Lighting 0.17→1.3→2.0,
DirLight/Exposure/ComputeGI 5-11x — while fixed-CPU stages (TLAS, shadow refit, probes,
SceneFinalize) stay flat. That is per-frame binding/state cache warmth: consecutive
passes record against hot caches; ANY gap goes cold. Probe-backlog theory dead.

**The cadence curve (total CPU/s)**: dense 109 ms/s, gap-1 202 ms/s, gap-2 190 ms/s.
Dense every-frame rendering is the GLOBAL optimum — one skipped frame already pays the
full cold penalty. This kills render-on-demand at any fps below ~2x panel rate (it
would manufacture the gap-1 regime), and it retro-explains the interval50 fps null.

Flora600 re-test under the table: MainView CPU unchanged — the metre caps were not
binding in this view. Distance knobs are null on fps AND submit.

Credit-gate verification: requests now run at 29.1/s (was 20-22) — the beat fix works.
Delivery (drawOne ours) still ~21.4/s: the OffscreenTargetManager's own servicing is
the next cap. Panel at 21 vs 30 Hz is a minor visual delta; deprioritized.

**TLAS build-once, deployed and verified (session 3)**: stage 0 ran once at arm, the
TLASBuild row VANISHED from the table, and per-render submit dropped 2.60 → 2.09 ms —
slightly more than the 0.39 predicted, because the empty rebuild's GPU dispatch went
with it. First measured-positive change of the sprint.

**THE CASCADE FPS CLAIM, RETRACTED THE SAME HOUR**: the sequence 31.4 fps (2 casc,
18:02) → 35.9 (1 casc, 18:08) → 42.1 (2 casc, 18:09) is a RISING SUN curve, not a
cascade effect — the third point, a control taken after restoring 2 cascades, EXCEEDS
the 1-cascade reading. During the dawn/dusk transition this scene swings ±10 fps over
single minutes, so the "+4.5 fps at dusk" published briefly here was the sun. What
survives is the clean CPU delta (Shadows row 0.44 → 0.29 ms, ~nothing) — consistent
with the whole sprint's theme: the knobs are not where the cost is. The earlier ladder
(17:26-17:48) is unaffected: it ran in the stable high-sun window (41-45 fps drift
band over 22 min). Lesson recorded twice tonight in different clothes: this scene's
fps has a large time-of-day term; A/Bs need a bracketing CONTROL POINT (A-B-A), not
just adjacency.

**THE SUN CONFOUND (session 3, the 32 fps scare)**: the post-fix soak read 31.4 fps —
a ~12 fps apparent regression that is NOT the fix (submit improved as designed, our
whole table reads 2.26 ms). Screenshot comparison shows the same camera and framing
with the SUN much lower: in-game time advances across the session chain (the game
re-saves at every load-complete), and dusk lighting is heavier for both renders (long
shadows = more caster geometry per cascade, denser atmosphere). RULE: fps comparisons
are valid only within one session's sun-window; cross-boot A/Bs must use the stage
table's CPU numbers, or the test save needs a pinned time of day.

## Day 2: the true baseline hunt (2026-08-08)

The launch args carry TWO plugins: `D:\SE2LcdCursor\LcdCursorApi.dll` AND RttProbe.
DISABLED.marker fully inerts RttProbe only (constructor-return before any patch/thread —
verified in code, zero RttProbe lines in the game log), so the morning "no-mod" boot was
really GAME + CURSOR API: engine-authoritative 57.6-57.9 Hz (the game's own *_Stats.log
`Render Frequency`, which REWRITES in place ~every few min — the trusted instrument; the
NVIDIA overlay read ~4 fps low and is ruled out per the user). The Steam-bounce trick
did NOT strip the args this time (documented behavior failed to reproduce — Steam
relaunched WITH plugins); the user removed the -plugins launch options in Steam instead.

**THE VERDICT (pure boot, args line empty, same midday window)**:
    game PURE:            57.50 Hz   (GPU 17.49 ms, VRAM 12.06 GiB)
    game + cursor API:    57.59-57.85 Hz
The pure game runs ~57.5 in this scene TODAY — the remembered ~70 did not reproduce.
Cursor API: no measurable cost. RttProbe-inert (marker): no cost, as designed. The
morning's inferred "~4.2 ms / 16 fps dormant hole" is RESOLVED as a cross-sun comparison
artifact — yesterday's 53.9 dormant tier was measured under a different sun than any
70-class reference. What stands: the FEED costs ~10-12 fps when active (internal
same-window ladder), and the precise mod-dormant-vs-pure delta needs one same-window
A-B (expected small). The zero-dormant-overhead mandate (#40) remains the design goal;
the emergency is off.

TO RESTORE THE MOD: re-add to Steam launch options:
    -plugins:D:\SE2LcdCursor\LcdCursorApi.dll;D:\SE2Rtt\RttProbe.dll
and delete D:\SE2Rtt\DISABLED.marker when RttProbe should arm.

## The activation-window CTD, third instance (2026-08-08 15:00)

User powered the LCD panels ~20 s after world-up; the gate went ACTIVE at 14:59:59.239
and 79 ms later the render thread hit the KNOWN prioritizer assert (`index >= 0 &&
index < _count`, FastBuffer, ManagedTexturePrioritizerComponent.CollectStandards) with
a 4.9 s sim stall IN PROGRESS at that moment. Our nested render had not run even once
(the 2000 ms hold was still counting), so the master-gate build is exonerated as
mechanism. ATTRIBUTION CORRECTED: the 2026-08-07 conclusion blamed feedTextureCamera as
"the only live input of ours in that component" — this crash fired with that knob OFF,
so the racy input is the ACTIVATION BURST itself (panel material rebind + buffer builds
churning collections the prioritizer walks) inside the post-load fragile window, not
any single knob. Mitigation: activationGraceMs 4000 -> 15000 (the 4 s grace cleared
the window it was sized for, not the post-load hitch storm). The durable fix remains
making activation not mutate render-side collections mid-walk — #41/#56's cycle-safe
arming neighborhood.

## THE MASTER GATE ACCEPTANCE TEST — PASSED (2026-08-08 15:11)

Same midday window, engine/cadence counters, user-focused game:

    game PURE (zero plugins):                57.5 fps
    MOD LOADED, feed dormant, master gate:   58.2 fps   <- AT the pure baseline
    feed ACTIVE:                             44.2 fps   (submit 2.44 ms, best yet)

The stand-down sequence fired within 12 ms of the dormant flip: gate DORMANT ->
"MASTER GATE: no feed is live" -> camera trigger destroyed -> presence removed ->
viewer delegate out -> clipmap budget restored. The mod's dormant cost is ZERO within
noise — the zero-dormant-overhead mandate is met, measured. The whole mod now costs
exactly its active feed (~14 fps in this scene) and nothing else.

## Where this leaves the 60 fps goal (rewritten 2026-08-08 after the baseline correction)

frame = scene's true ~14.3 ms (no mod, ~70 fps user-reported)
      + ~4.2 ms MOD AMBIENT overhead (runs even with the feed dormant)  ← BIGGEST POOL
      + ~5 ms feed render cost (2.1 CPU submit post-TLAS-fix + ~2.4 GPU + pacing)

60 fps (16.7 ms) with the feed live needs ~9 of those 9.2 added ms back. The order of
attack is now: (1) #40 dormant-overhead strip — profile what runs with feedsDisabled=1
and gate it on feed activity (worth up to ~16 fps, none of it visible on the panel);
(2) the feed's GPU ~2.4 ms via GPU timestamp queries; (3) the per-render CPU floor.
The cadence law still forbids saving via render rate.
