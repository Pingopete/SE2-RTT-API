using System;
using System.IO;

namespace RttProbe;

// One authority for "is this mod doing anything at all right now".
//
// The point is a clean A/B against vanilla WITHOUT restarting the game. Turn the tagged
// panel off and everything this mod does stops: no second Draw, no probe pass, no
// parking, no panel material changes, and every GPU resource we own is released. Turn it
// back on and the whole pipeline rebuilds from scratch.
//
// That matters more than convenience. Every conclusion in this project rests on
// "compared to what?", and until now the only way to get a mod-free frame was to quit,
// edit, and reload — which changes the world state, the VRAM residency and the streaming
// set at the same time. Those are exactly the variables that produced two of today's
// false diagnoses.
//
// THE SIGNAL is the tagged panel's own LCD tick. CameraFeed.OnLcdTick fires from the
// engine's LCD render component for every panel it draws, so a panel that is switched
// off, unpowered, destroyed or out of the render set simply stops ticking. No block-state
// reflection, no power API, no polling of the sim from the render thread — the absence of
// a signal IS the signal. A staleness window covers the gap between ticks.
//
// The Harmony patches stay installed while dormant. They cannot be removed safely
// mid-session, and they do not need to be: every hook consults this gate and returns
// immediately, and the stage-skip hooks already no-op outside our render. Dormant means
// the engine runs its own code with our patches present but inert.
internal static class FeedGate
{
    private static readonly string PausePath = Path.Combine(RttLog.OutDir, "feed-paused.marker");

    // PER-FEED (phase C1a). Liveness is the definition of a feed: "panel A was ground
    // down, feed B is untouched" is this gate and nothing else. See FeedInstance.cs for
    // why these are properties rather than instance fields reached through a parameter.
    private static long _lastPanelMs
    { get => Feeds.Cur.LastPanelMs; set => Feeds.Cur.LastPanelMs = value; }
    private static bool _active
    { get => Feeds.Cur.GateActive; set => Feeds.Cur.GateActive = value; }
    private static bool _everActive
    { get => Feeds.Cur.GateEverActive; set => Feeds.Cur.GateEverActive = value; }
    private static int _cycles
    { get => Feeds.Cur.GateCycles; set => Feeds.Cur.GateCycles = value; }

    // PROCESS-GLOBAL. The pause marker is a file on disk that stops the WHOLE mod — it
    // is the safety protocol's lever, not a per-feed property — and the poll cadence is
    // one filesystem stat shared by everyone.
    private static bool _paused;
    private static long _lastPollMs;

    // True while a tagged panel is alive and the mod should be doing its work.
    // Starts FALSE: until a tagged panel has ticked at least once, there is nothing to
    // draw to and no reason to build anything.
    public static bool Active => _active;

    // PROCESS-WIDE, and deliberately not per-feed: the marker stops the WHOLE mod. Read by
    // anything that is a statement about the mod rather than about a feed — the stats panel
    // is the first, since gating it on a feed made it go dark exactly when a feed died.
    public static bool Paused => _paused;

    public static void Reset()
    {
        _lastPanelMs = 0;
        _active = false;
        _everActive = false;
        _lastPollMs = 0;
        _cycles = 0;
        _teardownIn = -1;
        _pendingStartupLog = false;

        // READ THE PAUSE MARKER NOW, not on the first throttled poll.
        //
        // This used to be `_paused = false`, and that one line leaked a whole feed's
        // resources on EVERY DEPLOY — including every deploy made under the pause protocol
        // that exists to prevent exactly this.
        //
        // A hot reload gives the new assembly fresh statics, so the gate woke up believing
        // it was unpaused. Tagged panels never stop ticking, so it went ACTIVE, built a full
        // ScreenBuffers set, and only then read the marker and went dormant again:
        //
        //     20:39:00.868  === logic installed ===
        //     20:39:00.924  [feed 0] FEED GATE: ACTIVE
        //     20:39:00.940  [feed 0] SECOND ScreenBuffers built — InitializeBuffers(1024x1024)
        //     20:39:01.138  [feed 0] === FEED PAUSED by marker ===        <- 214 ms too late
        //     20:39:01.690  [feed 0] Whole-scene Reset: VRAM 12060 -> 12060 MB (0 MB)
        //     20:39:11.030  [feed 0] SECOND ScreenBuffers built           <- a SECOND set
        //
        // The teardown freed nothing (0 MB) because it ran against half-built state, so the
        // resume rebuilt on top of an orphan. Measured at ~570 MB per deploy, and it is why
        // VRAM ratcheted 12.05 -> 12.23 -> 12.79 -> 13.58 GB across an evening of deploys
        // while every STEADY-STATE window in between was dead flat.
        //
        // It also means my own workflow was the largest contributor to every VRAM number
        // taken tonight, and that the "two feeds are near the ceiling" reading was an
        // artefact of measuring right after a deploy. Steady state at two feeds was flat at
        // 12.79 GB for fifteen consecutive samples.
        //
        // Failing CLOSED is the right default besides: if the marker cannot be read we stay
        // paused, because the marker's whole purpose is "do not touch GPU resources now".
        try { _paused = FileWatch.Exists(PausePath); }
        catch { _paused = true; }
    }

    // For the health watcher: a one-line machine-readable state dump.
    public static string StatusLine =>
        $"gate={(_paused ? "PAUSED" : _active ? "ACTIVE" : "dormant")} cycles={_cycles} " +
        $"teardownIn={_teardownIn}";

    // Called from CameraFeed whenever a panel carrying the tag is seen ticking.
    public static void NotePanelAlive()
    {
        _lastPanelMs = Clock.Ms;
        // The SESSION'S first panel tick starts the load-settling grace period — see
        // CameraFeed.EffectiveIdleMs. Session-first deliberately, not cycle-first: the grace
        // exists for the post-load window only, and a panel that dies mid-play must still
        // expire on the normal 1.5 s window.
        if (CameraFeed.FirstPanelTickMs == 0) CameraFeed.FirstPanelTickMs = _lastPanelMs;
    }

    // Safe from ANY hook, on any thread. It only flips a bool and arms a countdown; it
    // never touches a GPU resource.
    //
    // The first version called Shutdown() straight from here, and here is reached from
    // BlitProbe.OnTick — the LCD tick. That disposed our ScreenBuffers and
    // DrawContextManager while the frame referencing them was still being recorded, and
    // the game died with PageFaultVA 0x0 partway through ScenePreparation + Render. A
    // dropped tick during any hitch was enough to trigger it, which makes it a bug that
    // fires exactly when the game is already struggling.
    //
    // Flipping Active is enough to STOP all work immediately, everywhere, because every
    // hook gates on it. Releasing what that work owned is a separate question and has to
    // happen somewhere it cannot race the recorder.
    public static void Poll()
    {
        long now = Clock.Ms;

        // DUMPED FROM POLL, NOT FROM THE TICK. A census emitted from OnLcdTick would fall
        // silent exactly when a surface stops ticking — which is the observation the whole
        // instrument exists to make. Poll runs for every feed whether or not any panel is
        // alive, so the ages keep being reported as they climb. Self-throttled internally,
        // so calling it once per feed per poll costs one clock comparison.
        PanelTickCensus.MaybeDump();

        // THE THROTTLE COVERS THE FILE STAT ONLY — not the per-feed decision below.
        //
        // This used to be `if (now - _lastPollMs < 250) return;` at the top, throttling the
        // whole method. That was correct with one feed and BROKE THE SECOND ONE the instant
        // feedCount went to 2: _lastPollMs is process-global, so whichever feed polled first
        // consumed the window and every other feed returned here without ever computing its
        // own `alive`. Feed 1's gate therefore never went ACTIVE, the whole-scene hook's
        // `if (!FeedGate.Active) return` fired on its every slot, and it never built or
        // rendered anything — while feed 0 carried on looking perfectly healthy.
        //
        // Observed 2026-07-30 on the first two-feed run. The split is the fix: the expensive
        // part is the filesystem stat, which genuinely is shared and stays throttled; the
        // per-feed part is two timestamp comparisons and must run on every call, for every
        // feed. Cost is nil and it removes a whole class of "feed N is silently dormant".
        if (now - _lastPollMs >= 250)
        {
            _lastPollMs = now;
            PollPauseMarker();
        }

        PollFeed(now);
    }

    // EVERY feed, every engine frame. Call from OUTSIDE the render-slot scope.
    //
    // THE SECOND HALF OF THE STARVATION BUG (phase F1). Poll() above is reached from two
    // places: BlitProbe.OnTick, which is PANEL-driven — so it only ever polls the gate of a
    // feed whose panel is still ticking — and the whole-scene hook, which polled INSIDE
    // `using (Feeds.Enter(NextForRender()))`, i.e. only the feed holding the render slot.
    //
    // Between them those two cover every feed only while every feed is healthy. A feed
    // whose panel has just gone away is exactly the one neither reaches: its own ticks have
    // stopped, and it can only be handed the render slot if it is already eligible, which
    // it no longer is. Its gate would sit ACTIVE with nothing behind it, and nothing would
    // ever arm its teardown.
    //
    // The per-feed half of a poll is two timestamp comparisons (see PollFeed), so running
    // it for all four slots every frame costs nothing measurable and removes the whole
    // class of "that feed's state can only change while it is winning".
    public static void PollAll()
    {
        long now = Clock.Ms;
        if (now - _lastPollMs >= 250)
        {
            _lastPollMs = now;
            PollPauseMarker();
        }

        // Cached delegate, not a lambda: this runs every engine frame, and a closure over
        // `now` would allocate a display class per frame. Small, but the GC-churn hunt
        // (task #18) is live and self-inflicted garbage on the render thread is exactly what
        // it is trying to account for.
        Feeds.ForEachSlot(_pollOne);

        // GPU report probe (task #65): lazy arm + 15s dump check, one branch when idle.
        GpuReportProbe.Poll();

        // MASTER DORMANCY (task #40): recompute the any-feed-live flag once per frame and
        // announce the edges. The consumers gate themselves on Feeds.AnyLive; nothing here
        // reaches into them, so a consumer that is mid-operation finishes its step and
        // simply declines the next one — the same graceful-cut contract the per-feed gate
        // already honours.
        bool wasLive = Feeds.AnyLive;
        Feeds.RecomputeAnyLive();
        if (wasLive != Feeds.AnyLive)
        {
            RttLog.Global(Feeds.AnyLive
                ? "MASTER GATE: a feed went live — world-side systems (clipmap camera/budget, " +
                  "flora override, viewer distance, presence/preload) re-arm on their next pass."
                : "MASTER GATE: no feed is live — world-side systems stand down and scoped " +
                  "engine globals restore. The mod should now cost ~nothing per frame.");

            // The viewer-distance delegate is the switch on a ~107k calls/s path, and the
            // config poll only re-evaluates it when the FILE changes — so gate edges must
            // drive it here or a dormant mod keeps paying the dispatch forever.
            try
            {
                ViewerDistance.SetHook((FeedConfig.ViewerDistanceOverride || FeedConfig.FixLodCycling)
                                       && Feeds.AnyLive);
            }
            catch (Exception e) { RttLog.Error("master gate viewer hook", e); }
        }
    }

    private static readonly Action _pollOne = () => PollFeed(Clock.Ms);

    // Process-global: the pause marker is one file that stops the WHOLE mod.
    private static void PollPauseMarker()
    {
        // THE PAUSE MARKER. A file is the right mechanism here precisely because it is
        // outside the game: it can be created before a rebuild, by a script, or by a
        // human, and it takes effect without the config parser, the panel, or anything
        // else in the mod having to be healthy.
        //
        // The workflow it exists for: pause -> wait for DORMANT in the log -> swap the
        // DLL or edit anything risky -> unpause. A hot reload that lands while our nested
        // Draw is recording has caused several of tonight's crashes, and a dormant mod
        // has nothing in flight to land on.
        bool paused = false;
        try { paused = FileWatch.Exists(PausePath); } catch { }
        if (paused != _paused)
        {
            _paused = paused;
            // Global: the marker stops the WHOLE mod, and PollAll reaches here with no
            // ambient set — RttLog.Line would trip the unscoped-access detector and stamp a
            // process-wide statement with one arbitrary feed's tag.
            RttLog.Global(paused
                ? "=== FEED PAUSED by marker (output/feed-paused.marker). Going dormant; safe to " +
                  "rebuild or edit anything. Delete the marker to resume. ==="
                : "=== FEED UNPAUSED (marker removed). Resuming. ===");
        }

    }

    // PER-FEED, every call. Two timestamp comparisons — cheap enough that throttling it was
    // never buying anything, and expensive in a way nothing measured: a feed whose gate is
    // never evaluated is a feed that never runs.
    // ---- the quiesced rebuild (CTD 2026-07-30 20:54) ------------------------------
    //
    // Set when something needs EVERY feed rebuilt from a stopped renderer rather than in
    // place — today only a feed-count change, which re-routes which panel each feed owns.
    // While it is set every feed reads as not-alive, so they all go dormant, run their
    // normal 30-frame teardown and release everything through the proven Shutdown path.
    // Once the last one has finished, this clears itself and the panels bring the feeds
    // straight back up against clean state.
    //
    // This is the pause protocol, applied by the code instead of by whoever remembered to
    // create the marker. It is process-global for the same reason _paused is: the whole mod
    // quiesces together, and a half-quiesced rebuild is the thing being avoided.
    private static bool _quiesceRebuild;
    private static long _quiesceStartedMs;
    private static int _holdLogs;   // activation-grace announcements, capped

    // Escape hatch. AllQuiesced needs every slot to report itself released, and a feed whose
    // panel stopped ticking at the exact moment the quiesce began would keep its stale
    // _active and never be re-polled — leaving the mod dormant forever. That is a soft-lock
    // rather than a crash, but "the feed silently never came back" is the least debuggable
    // failure this project produces, so it gets a bound and a loud line rather than patience.
    private const long QuiesceTimeoutMs = 10000;

    public static void RequestQuiescedRebuild()
    {
        _quiesceRebuild = true;
        _quiesceStartedMs = Clock.Ms;

        // FORCE every slot dormant, rather than waiting for each feed's own poll to
        // notice. The first version waited, and the very first downward count change
        // proved that wrong (2026-07-30 22:05): shrinking feedCount changes Feeds.Count
        // IMMEDIATELY, so the retired feed's panel stops routing to it on the same poll —
        // and a feed nobody polls can never see itself go dormant. Its _active stayed
        // true, AllQuiesced stayed false for the full 10 s, the timeout escape hatch
        // released the hold (doing exactly its job), and the retired feed's ScreenBuffers
        // and DrawContextManager were left stranded resident — the same orphan shape as
        // the deploy leak, arriving through the mechanism built to prevent it.
        //
        // Going UP never hit this, because no ACTIVE slot leaves Count in that direction.
        //
        // Same thread as PollFeed's own transitions (this is called from FeedConfig.Poll),
        // and the same two writes PollFeed's dormant branch makes.
        Feeds.ForEachSlot(ForceDormant);
    }

    private static void ForceDormant()
    {
        if (!_active) return;
        _active = false;
        _teardownIn = TeardownDelayFrames;
        RttLog.Line("=== FEED GATE: DORMANT (forced by the quiesced rebuild — a slot being retired by a " +
                    "count change is unreachable by polling from the moment the count moves, so it is told " +
                    $"directly). Releasing resources in {TeardownDelayFrames} frames. ===");
    }

    // Every slot released and none active. Checked after each Shutdown rather than on a
    // timer, so the wait is exactly as long as the teardowns actually take.
    private static bool AllQuiesced()
    {
        bool all = true;
        Feeds.ForEachSlot(() => { if (_active || _teardownIn >= 0) all = false; });
        return all;
    }

    private static void PollFeed(long now)
    {
        // feedsDisabled is a PER-FEED off switch that takes the ordinary dormancy path
        // (phase F4): gate dormant -> 30-frame teardown -> resources released, while the
        // other feeds carry on untouched. Deliberately here rather than in the rebuild
        // signature — switching one feed off must not quiesce the whole mod, and the point
        // of the lever is to exercise exactly the "one of N goes away" contract that a
        // destroyed panel exercises, repeatably and without grinding a block down.
        // PAUSE-AWARE (2026-08-03): judge panel silence against the SIM, not the wall clock.
        // This is the same rule as CameraFeed.ExpireClaims, and it is the crash fix for the
        // settings-menu / exit / load-save CTD family: the wall-clock window read a pause as
        // "panel destroyed", tore the feed down under peak load, and the teardown removed
        // the device. While the sim is not ticking, the gate HOLDS its current state; when a
        // stall ends, the panel gets a fresh idle window before silence can mean dormancy.
        bool panelFresh = _lastPanelMs != 0
                          && (now - CameraFeed.EffectiveStamp(_lastPanelMs))
                             < CameraFeed.EffectiveIdleMs(FeedConfig.PanelIdleMs);
        if (!panelFresh && _active && !CameraFeed.PanelSimTickingNow)
            panelFresh = true;                     // sim is stalled: silence is not death
        bool alive = !_paused && !_quiesceRebuild
                     && !FeedConfig.IsFeedDisabled(Feeds.Cur.Id)
                     && panelFresh;

        // ACTIVATION GRACE (2026-08-06, the world-load RenderThreadFreeze). Expiry has grace
        // everywhere — stalls hold the gate, loads widen the idle window — but ACTIVATION had
        // none: a dormant feed could arm in the first seconds of a session, and tonight it
        // did. World load completed 20:16:35.046; we bound the panel, created the presence
        // markers, fired preload #1 and queued render requests by .23; at .227 the engine's
        // texture prioritizer died on 'index >= 0 && index < _count' in its FIRST collection
        // walk — the same family as the three 2026-08-04 crashes, one of which was also at
        // world load. Injecting a second viewer's worth of streaming demand into the most
        // fragile 200 ms a session has is our contribution to that race, and it is entirely
        // avoidable: nothing about a feed needs to exist in second zero.
        //
        // So a DORMANT feed may not go ACTIVE until the session has been up — and the panel
        // scene un-stalled — for activationGraceMs. Anchored on BOTH the session's first
        // panel tick (the world-up moment) and the last stall end (so a save/menu/load resume
        // gets the same settling before a rearm — the rebuild-under-load family). Mid-play
        // gate cycles see both anchors long past and arm instantly; deactivation is untouched
        // by construction, because this term only suppresses a dormant->active transition.
        if (alive && !_active && FeedConfig.ActivationGraceMs > 0)
        {
            long grace = FeedConfig.ActivationGraceMs;
            long first = CameraFeed.FirstPanelTickMs;
            bool settled = first != 0
                           && now - first >= grace
                           && now - CameraFeed.LastStallEndMs >= grace
                           && CameraFeed.PanelSimTickingNow;
            if (!settled)
            {
                alive = false;
                if (_holdLogs++ < 6)
                    RttLog.Line($"FEED GATE: activation HELD for feed {Feeds.Cur.Id} — inside the " +
                                $"{grace} ms settling window after world-up or a stall. The panel stays " +
                                "on its current material until the engine's first streaming walks are " +
                                "done; arming here is what raced the texture prioritizer on 2026-08-06.");
            }
        }

        if (alive == _active) return;

        _active = alive;
        if (_active)
        {
            // Did we get back before the teardown ran? If so NOTHING was released, and the
            // startup line must not claim a rebuild that is not happening — see Startup().
            _resumedIntact = _teardownIn >= 0;
            _teardownIn = -1;              // cancel a pending teardown: we are back
            _pendingStartupLog = true;

            // A FEED MAY NOT BUILD ITS GPU RESOURCES WHILE ANOTHER FEED IS RENDERING.
            //
            // Bought with two device removals in twelve minutes, 2026-08-01 11:30:24 and
            // 11:41:38, both DXGI_ERROR_DEVICE_REMOVED with a NON-ZERO PageFaultVA — the GPU
            // dereferencing memory that had been freed or recycled under it, not the null
            // bind of the 2026-07-29 fault. VRAM had 1.5 GB spare, so it is not exhaustion.
            //
            // The correlation is with the REBUILD and it is tight: crash 5-6 s after a feed
            // rebuilt, 27-71 s after any teardown. And the rebuilds that did NOT kill it —
            // two world loads and a hot reload — all built every feed together while nothing
            // of ours was rendering. Building a second ScreenBuffers and DrawContextManager
            // takes blocks from the engine's shared borrowed pool, and doing that while
            // another feed's frames are in flight appears to hand one of them out twice; the
            // fault lands a few seconds later, once the aliased block is actually written.
            //
            // The settle window does not cover this. It defers the new feed's first DRAW,
            // which is necessary and was verified firing — the feed rendered cleanly for four
            // seconds before the device died. The ALLOCATION is the hazard, and it happens
            // before any window can help.
            //
            // So a returning feed takes the quiesced rebuild instead: every feed stops, every
            // feed releases, and they all come back together from a stopped renderer, which
            // is the one arrangement observed never to fault. That is the same path a
            // feedCount change has taken since 2026-07-30, for the same underlying reason.
            //
            // The cost is real and is accepted deliberately: bringing one feed back briefly
            // interrupts the others. Losing a feed stays cheap — no quiesce, the survivors
            // never stop — which is the direction that matters when a panel is destroyed
            // under fire. Paying a blink to come back is a fair trade for not crashing.
            if (!_resumedIntact && !Feeds.Cur.SbBuilt && Feeds.AnyOtherRendering())
            {
                RttLog.Line("=== FEED GATE: this feed is back and must REBUILD its GPU resources, but " +
                            "another feed is rendering. Allocating into a live renderer device-removed " +
                            "the game twice on 2026-08-01, so the whole mod quiesces first: every feed " +
                            "releases, then all of them rebuild together from a stopped renderer. Expect " +
                            "a second or so with no feeds. ===");
                RequestQuiescedRebuild();
            }
        }
        else
        {
            // Work has already stopped, this frame. Give the frames that were mid-flight
            // when it stopped time to be submitted and retired before anything is freed.
            _teardownIn = TeardownDelayFrames;

            // SAY WHICH OF THE REASONS IT WAS. All four end here — the panel was destroyed
            // or ground down, it was switched off, the mod was paused, or this feed was
            // disabled — and they call for completely different next moves. A single
            // "no panel has ticked" line for all of them is how a deliberately disabled feed
            // reads as a broken one.
            string why = FeedConfig.IsFeedDisabled(Feeds.Cur.Id)
                ? $"feedsDisabled lists feed {Feeds.Cur.Id + 1}"
                : _paused ? "the mod is paused by marker"
                : _quiesceRebuild ? "a quiesced rebuild is in progress"
                : $"no tagged panel has ticked for {FeedConfig.PanelIdleMs} ms";

            RttLog.Line($"=== FEED GATE: DORMANT — {why}. This feed's work has stopped; releasing its " +
                        $"resources in {TeardownDelayFrames} frames. Other feeds are unaffected and " +
                        $"absorb its share of the render slot. ===");
        }
    }

    // Called ONLY from the whole-scene hook, which is the SceneDrawSystem.Draw postfix on
    // the render thread — the same place a config change has been safely disposing and
    // rebuilding these objects all session. This is where anything owning GPU memory is
    // allowed to be released.
    private const int TeardownDelayFrames = 30;

    // PER-FEED: each feed counts down to its own teardown. -1 = not armed. The default
    // lives on FeedInstance so a newly created feed starts disarmed like this one did.
    private static int _teardownIn
    { get => Feeds.Cur.TeardownIn; set => Feeds.Cur.TeardownIn = value; }

    private static bool _pendingStartupLog
    { get => Feeds.Cur.PendingStartupLog; set => Feeds.Cur.PendingStartupLog = value; }

    // EVERY feed's countdown, every engine frame. Call from OUTSIDE the render-slot scope.
    //
    // THE BUG THIS FIXES, and it is the most expensive one the two-feed work has produced.
    // This used to be called from inside `using (Feeds.Enter(Feeds.NextForRender()))`, so
    // only the feed holding the render slot counted down. AdvanceSlot() runs only after a
    // render COMPLETES — and a dormant feed never completes one. So the moment both feeds
    // went dormant the slot froze, whichever feed it happened to be pointing at reached zero
    // and released, and every other feed's countdown stayed pinned at 30 forever.
    //
    // Observed 2026-07-30 19:20:51, two feeds dormant together:
    //
    //     [feed 1] Feed gate: releasing resources now.
    //     [feed 1] Whole-scene Reset: VRAM 12847 MB -> 12720 MB (-127 MB)
    //     ...and nothing at all from feed 0.
    //
    // The next gate cycle then rebuilt BOTH feeds — allocating a fresh ScreenBuffers and
    // DrawContextManager for feed 0 on top of the set that was never freed. VRAM went
    // 12.78 -> 13.70 GB across that one cycle and stayed flat there, which is retention and
    // not churn. AvailableVRAM was 13.61 GB. The device was removed 40 seconds later.
    //
    // It reads as a VRAM-ceiling problem and it is not: two feeds cost +580 MB and fit with
    // ~850 MB to spare. What did not fit was two feeds plus an orphaned copy of one of them.
    //
    // The principle, which is the part worth keeping: a teardown countdown is per-feed
    // BOOKKEEPING, not per-feed RENDERING. Scheduling it on the render slot tied the freeing
    // of resources to the very activity that had just stopped.
    //
    // ForEachSlot, not ForEach: a slot dropped out of Count by a feedCount change or the
    // VRAM cap is the one most in need of releasing, and it is invisible to every
    // Count-bounded sweep from the instant it is retired.
    public static void PumpAll()
    {
        Feeds.ForEachSlot(_pumpOne);

        // The rotation watchdog rides the same once-per-frame pump, for the same reason the
        // countdowns do: it is bookkeeping ABOUT the render slot, so it must not be
        // scheduled ON it. Unscoped by design — see Feeds.TickRenderSlot.
        Feeds.TickRenderSlot();

        // Release the quiesce only once every slot has actually finished. Checked AFTER the
        // sweep, so the Shutdown that completed this frame is included — releasing a frame
        // early would let a panel tick re-arm a feed while another was still tearing down,
        // which is the overlap this whole mechanism exists to prevent.
        if (!_quiesceRebuild) return;

        if (AllQuiesced())
        {
            _quiesceRebuild = false;
            RttLog.Global("Feed gate: quiesced rebuild complete — every feed released its resources " +
                        "with the renderer stopped. Feeds will re-arm from clean state on the next panel tick.");
        }
        else if (Clock.Ms - _quiesceStartedMs > QuiesceTimeoutMs)
        {
            _quiesceRebuild = false;
            RttLog.Global($"!!! Feed gate: quiesced rebuild TIMED OUT after {QuiesceTimeoutMs} ms with a slot " +
                        "still reporting active or mid-teardown. Releasing the hold so the feed can come back, " +
                        "but the rebuild it was protecting may now overlap a live render — the exact condition " +
                        "that device-removed the game on 2026-07-30. If this line ever appears, find out WHICH " +
                        "slot never quiesced before trusting the feed again.");
        }
    }

    private static readonly Action _pumpOne = PumpOne;

    // INSIDE the per-feed scope, both halves of it. Startup() writes GateCycles and
    // GateEverActive — per-feed state — so draining a global flag here would have written
    // feed 0's counters on every feed's behalf.
    private static void PumpOne()
    {
        if (_pendingStartupLog) { _pendingStartupLog = false; Startup(); }

        // PER-FRAME BOOKKEEPING BELONGS HERE, not on the render slot (phase F1). Both of
        // these used to advance only while their feed was winning turns:
        //
        //   the settle countdown ticked inside TryRender, so a window specified in ENGINE
        //   frames actually took 30 x N of them — and it is the engine's probe reprocess it
        //   is waiting on, which drains on engine frames and cares nothing for our rotation;
        //
        //   the claim stamps were only ever cleared by a full gate cycle, so a feed kept
        //   alive by one panel never noticed another of its panels being destroyed.
        //
        // This pump visits every slot every engine frame regardless of who is rendering,
        // which is exactly the schedule both of them actually want.
        WholeSceneRender.TickSettle();
        CameraFeed.ExpireClaims();

        if (_teardownIn < 0) return;
        if (--_teardownIn > 0) return;
        _teardownIn = -1;
        Shutdown();
    }

    // SAYS WHICH OF THE TWO THINGS ACTUALLY HAPPENED.
    //
    // This line used to claim "Everything rebuilds from scratch" unconditionally, and that
    // is FALSE for the common case: a panel that misses a couple of ticks (a hitch, a save)
    // arms the 30-frame countdown and then cancels it on the way back, so nothing is ever
    // released and nothing is rebuilt. Observed 2026-07-30 23:20:23 — both feeds dormant and
    // active again 11 ms later, with the log announcing a full rebuild that did not occur.
    //
    // Harmless to the feed, corrosive to debugging: this project has repeatedly lost time to
    // log lines that described intent rather than events, and a spurious "rebuilds from
    // scratch" is exactly what someone hunting a resource leak would anchor on.
    private static bool _resumedIntact
    { get => Feeds.Cur.ResumedIntact; set => Feeds.Cur.ResumedIntact = value; }

    private static void Startup()
    {
        _cycles++;
        RttLog.Line(_resumedIntact
            ? $"=== FEED GATE: ACTIVE (cycle {_cycles}). A tagged panel is ticking again. Resumed " +
              "INTACT — the panel came back before the teardown ran, so nothing was released and " +
              "nothing is being rebuilt. ==="
            : $"=== FEED GATE: ACTIVE (cycle {_cycles}). A tagged panel is ticking again. " +
              "Everything rebuilds from scratch: second ScreenBuffers, second " +
              "DrawContextManager, cascade set, LDR ring, panel binding. ===");
        _resumedIntact = false;
        _everActive = true;
    }

    // Put the game back exactly as it would be without this mod loaded.
    //
    // Order matters: stop the things that ISSUE GPU work before releasing what that work
    // reads, or a pass already in flight can be handed a disposed resource. Every step is
    // independently guarded, because a shutdown that throws half way through is worse
    // than either state.
    // Internal rather than private since the hot-reload handover needs it: on reload the
    // gate is still ACTIVE, so no normal path would ever call this, and the resources it
    // disposes would be abandoned with the assembly. See WholeSceneRender.ReloadHandover.
    internal static void Shutdown()
    {
        // IS THIS THE LAST FEED OUT? (phase F2)
        //
        // Shutdown runs PER FEED, but four of its steps touch state that belongs to the
        // PROCESS: the LCD material's EmissivityMultiplier and FSRMaskAmount are on a shared
        // definition every panel in the world samples, the probe DimDistance is the engine's,
        // and CameraFeed's discovery sets and EverFound latch describe "has this mod found a
        // panel at all". Undoing those on ONE feed's teardown reaches straight into a
        // neighbour that is still running: the FSR reactive mask comes off, the star-smear it
        // fixes comes back, and EverFound going false lets the render pass fall back to the
        // PLAYER'S viewpoint on a live feed's panel.
        //
        // So the shared half of the teardown waits for the last feed out. The per-feed half —
        // our buffers, our contexts, our ring, our binding — runs unconditionally, because
        // that is this feed's own property and releasing it is the entire point.
        bool last = !Feeds.OthersLive();

        RttLog.Line("Feed gate: releasing resources now. " + (last
            ? "This is the LAST live feed, so the shared engine state goes back to stock too — " +
              "the game should render exactly as it does without this mod."
            // "still HOLDING RESOURCES", not "still live". OthersLive tests gate-or-resident,
            // while RotationLine reports the gate alone — so when two feeds tear down in the
            // same frame the first one printed "Other feed(s) are still live (0=dormant
            // 1=dormant)", contradicting itself inside one sentence. The test is right (a feed
            // that went dormant this frame still owns its buffers until its own teardown runs);
            // it was the word that was wrong.
            : $"Other feed(s) still hold resources ({Feeds.RotationLine()} — a feed that just went " +
              "dormant still owns its buffers until its own teardown runs), so the shared LCD " +
              "material, the probe settings and the panel-discovery state are left alone. Only " +
              "this feed's own resources are released."));

        // 1. Stop issuing work. The whole-scene route and the probe pass both gate on
        //    Active, so they are already inert by the time this runs; these calls release
        //    what they own.
        Try("whole-scene teardown", WholeSceneRender.Reset);
        Try("camera pass teardown", CameraRender.Reset);

        // 2. Stop delivering frames to the panel.
        Try("handover teardown", FeedHandover.Reset);
        Try("blit teardown", BlitProbe.Reset);

        // 3. Release the camera constant-buffer swap.
        Try("camera CB swap", CameraCbSwap.Reset);

        // 3a. Release our runtime screen material through the engine's own path BEFORE
        //     forgetting the binding — this is what stops the "Can't remove material"
        //     deferred assert at every gate cycle. Must precede PanelBinding.Reset, which
        //     drops the weak references Unbind needs.
        Try("panel material unbind", PanelBinding.Unbind);

        // 3b. Reset the panel BINDING, not just the material. Found the hard way: without
        //     this, _bound stays true across a gate cycle, so on restart BlitProbe builds
        //     a fresh offscreen target, the handover copies frames into it — every
        //     counter healthy — and the panel's screen material still points at the OLD
        //     target from the previous cycle. A black screen with park/copy rates
        //     climbing is exactly this signature.
        Try("panel binding teardown", () => PanelBinding.Reset(last));

        // 4. Undo the PERSISTENT engine mutations. These are the ones that would
        //    otherwise survive a dormant gate and quietly invalidate the comparison:
        //    the LCD material's emissive multiplier is a shared definition affecting
        //    every panel in the world, and DimDistance reaches the engine's own probes.
        //
        //    LAST FEED ONLY. They are not ours to put back while another feed is still
        //    relying on them being ours.
        if (last)
        {
            Try("restore panel material", PanelBinding.RestoreEngineState);
            Try("restore probe settings", CameraRender.RestoreEngineState);
        }

        // 5. Forget the panel, so coming back is a genuinely fresh discovery rather than
        //    a resumption with stale state. The per-feed half always; the shared discovery
        //    state (seen names, surface set, EverFound) only when nobody else needs it.
        Try("forget panel", () => CameraFeed.Reset(last));

        RttLog.Line("Feed gate: shutdown complete." +
                    (_everActive ? "" : " (Nothing had been started yet.)"));
    }

    private static void Try(string what, Action a)
    {
        try { a(); }
        catch (Exception e) { RttLog.Error("feed gate shutdown: " + what, e); }
    }
}
