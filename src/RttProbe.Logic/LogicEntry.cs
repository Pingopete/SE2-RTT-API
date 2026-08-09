namespace RttProbe;

public static class LogicEntry
{
    // Called by the bootstrap on every (re)load. The bridge is reached by
    // reflection rather than a compile-time reference: the logic assembly lives
    // in a collectible load context, and a hard reference would pin it.
    public static void Install()
    {
        RttLog.Line("=== logic installed ===");
        try
        {
            // Prove the unscoped-access detector can actually fire, BEFORE the scope below
            // makes every later access legitimate. This runs on the load thread with no pump
            // having claimed it, so it is genuinely unscoped — see Feeds.SelfTest for why a
            // silent detector is worthless evidence at Count == 1.
            Feeds.SelfTest();

            // SCOPED (phase C1b). Every Reset below writes per-feed state, and on plugin
            // load no pump has claimed this thread — so without a scope the very first
            // thing a reload does is trip the unscoped-access diagnostic, dozens of times,
            // before anything has gone wrong.
            //
            // Scoped to Primary rather than swept across the registry with ForEach, because
            // these methods are a MIX: FeedGate.Reset is purely per-feed, while
            // BlitProbe.Reset also clears the arm markers and CameraRender.Reset drops
            // reflection caches — process-global work that running once per feed would
            // repeat pointlessly and, for the marker files, incorrectly. C3 has to split
            // each Reset into its global and per-feed halves before this becomes ForEach.
            // At Count == 1 the two are identical, which is exactly why this is safe now
            // and is a documented C3 prerequisite rather than a hidden one.
            // Panel->feed claims belong to the previous assembly's instances. Cleared here
            // so a reload re-derives ownership from what is actually in the world, rather
            // than inheriting a map pointing at objects that no longer exist.
            FeedRouter.Reset();

            // BEFORE anything can route a panel. Feeds.Count is read by the router on the very
            // first tick, and until the config has been read it answers 1 — at which point
            // every [RTCn] panel claims feed 0. See FeedConfig.PrimeFeedCount.
            FeedConfig.PrimeFeedCount();

            using (Feeds.Enter(Feeds.Primary))
            {
                FeedGate.Reset();
                BlitProbe.Reset();
                PanelTickCensus.Reset();   // ages are meaningless across a reload
                ScenePassHook.Reset();
                CameraRender.Reset();
                CameraFeed.Reset();
                StatsPanel.Reset();
                WholeSceneRender.Reset();
                CameraCbSwap.Reset();
                FeedHandover.Reset();
                PanelBinding.Reset();
                GpuReportProbe.Reset();   // clears the bridge delegate before this assembly is replaced
                ImpostorProbe.Reset();
            }

            var bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
            if (bridge == null)
            {
                RttLog.Line("RttBridge not found — bootstrap too old? Restart the game to adopt the new bootstrap.");
                return;
            }
            bridge.GetField("TickHook")?.SetValue(null, (Action<object>)BlitProbe.OnTick);
            bridge.GetField("PanelRenderHook")?.SetValue(null, (Action<object, object, object>)BlitProbe.OnPanelRender);
            bridge.GetField("SceneDrawHook")?.SetValue(null, (Action<object, object, int>)ScenePassHook.OnSceneDraw);
            bridge.GetField("OffscreenUiDrawHook")?.SetValue(null, (Action<object[]>)FeedHandover.OnOffscreenUiDraw);

            // For the bootstrap's log-only probes: whether an engine call fired inside our
            // nested Draw. Null-safe on an older bootstrap.
            bridge.GetField("InOurRenderHook")?.SetValue(null, (Func<bool>)(() => WholeSceneRender.InOurRender));

            // Per-body clipmap camera. Absent on an older bootstrap, which degrades to
            // "terrain always follows the player" — the behaviour before this existed.
            bridge.GetField("ClipmapCameraHook")?.SetValue(null,
                (Func<object, object, object>)WorldGrids.ChooseClipmapCamera);

            // Per-sector flora camera. Absent on an older bootstrap, which degrades to
            // "remote feeds never see flora" — the state before 2026-08-02.
            var flora = bridge.GetField("FloraCameraHook");
            if (flora != null)
            {
                flora.SetValue(null, (Func<object, object[], bool, bool>)WorldGrids.OnFloraSectorUpdate);
                RttLog.Line("Flora camera hook registered — floraCameraOverride is armable.");
            }
            else
            {
                RttLog.Line("FloraCameraHook not on this bootstrap — restart the game to adopt it. " +
                            "floraCameraOverride will have NO EFFECT until then.");
            }

            // The nearest-viewer distance. Absent on an older bootstrap, which degrades to
            // "every entity is measured from the player" — the state before 2026-08-02, in
            // which a remote feed's trees never resolve and its grass never draws at all.
            var viewer = bridge.GetField("ViewerDistanceHook");
            if (viewer != null)
            {
                // INSTALLED ONLY IF THE KNOB ASKS FOR IT, unlike every other hook here.
                //
                // This one is postfixed onto CalculateDistanceToCamera, which the engine calls
                // for EVERY root entity in the render scene — measured at ~107,000 calls a
                // second. An installed-but-idle delegate is not free: the postfix still reads
                // the hook field, increments a shared counter from a job thread and pays a
                // dispatch, just to reach a method that returns its argument. So the delegate
                // itself is the switch, and FeedConfig keeps it in step with the knob on every
                // poll (SetHook is idempotent).
                ViewerDistance.SetHook(FeedConfig.ViewerDistanceOverride || FeedConfig.FixLodCycling);
                RttLog.Line($"Nearest-viewer distance hook available; viewerDistance is " +
                            $"{(FeedConfig.ViewerDistanceOverride ? "ON" : "OFF")}. When on it rewrites the ONE " +
                            "cached float behind StreamingTag, the impostor swap, shadow tracking and the " +
                            "raytracing tags. When off the delegate is REMOVED, not merely idle.");
            }
            else
            {
                RttLog.Line("ViewerDistanceHook not on this bootstrap — restart the game to adopt it. " +
                            "viewerDistance will have NO EFFECT until then.");
            }

            // The frame-end constant-buffer drain. Freed one render late by the render bracket
            // itself, which fixed the LEAK; this hook is what takes the residual ~5 to zero so
            // 'AliveConstantBufferCount == 0' stops asserting every frame — and with it the
            // exit-to-menu CTD that assertion is promoted into.
            //
            // On an older bootstrap this field is absent and the leak stays FIXED but the
            // assert stays LIVE. Said out loud, because "the leak is gone" and "the crash is
            // gone" are different claims and I have already conflated them once.
            var frameEnd = bridge.GetField("FrameEndHook");
            if (frameEnd != null)
            {
                frameEnd.SetValue(null, new Action(WholeSceneRender.DrainAllStagedCbs));
                RttLog.Line("FrameEndHook armed — transient constant buffers we displace are now freed " +
                            "inside IRender_Present, immediately before the engine's own OnFrameEndDisposal " +
                            "counts them. Acceptance test: the exit log should no longer carry " +
                            "'AliveConstantBufferCount == 0'.");
            }
            else
            {
                RttLog.Line("FrameEndHook not on this bootstrap — restart the game to adopt it. The " +
                            "constant-buffer LEAK is still fixed (one-render-late reclaim), but ~5 stay " +
                            "alive at frame end, so the per-frame assertion and the exit CTD REMAIN.");
            }

            // Grass without HiZ, for our pass only. Absent on an older bootstrap, in which case
            // wholeSceneGrassNoHiZ is inert — worth saying, because a silently ignored flag is
            // exactly how the HZBO A/B would get misread a second time.
            var grassHiz = bridge.GetField("GrassNoHiZHook");
            if (grassHiz != null)
            {
                // THE CENSUS RIDES THE HOOK THAT ALREADY EXISTS.
                //
                // This delegate is called from the bootstrap's RenderGrass PREFIX, so it fires
                // once per RenderGrass invocation whether or not wholeSceneGrassNoHiZ is armed.
                // That makes it a free call counter for the one question no instrument has ever
                // answered: does the grass draw RUN during our nested Draw, or not at all?
                //
                // Everything measured so far is STATE (settings readable, buffers present,
                // 51/51 cells under the camera carrying valid grass entities). State cannot
                // distinguish "the draw ran and produced nothing" from "the draw never ran".
                // Those have completely different fixes, so guessing between them is how this
                // question has burned three sessions.
                //
                // Counting here needs no bootstrap change and therefore NO GAME RESTART.
                grassHiz.SetValue(null, (Func<bool>)(() =>
                {
                    var ours = WholeSceneRender.InOurRender;
                    GrassCallCensus.Note(ours);
                    return ours && FeedConfig.WholeSceneGrassNoHiZ;
                }));
                RttLog.Line("Grass HiZ hook registered — wholeSceneGrassNoHiZ is armable. It forces " +
                            "RenderGrass's enableHiZ ARGUMENT false inside our render only, which is the " +
                            "per-pass version of the HZBO test that whited out the feed.");
            }
            else
            {
                RttLog.Line("GrassNoHiZHook not on this bootstrap — restart the game to adopt it. " +
                            "wholeSceneGrassNoHiZ will have NO EFFECT until then.");
            }

            // The sim-pump seat (server presence entity). Absent on an older bootstrap,
            // which degrades to "no server-side materialization" — the state of the world
            // before 2026-08-02. Say so out loud, because serverPresenceEntity = 1 with a
            // stale bootstrap would otherwise read as a silent null result.
            var pump = bridge.GetField("SimPumpHook");
            if (pump != null)
            {
                pump.SetValue(null, (Action<object>)WorldGrids.OnSimPump);
                RttLog.Line("Sim-pump seat hook registered — serverPresenceEntity is armable.");
            }
            else
            {
                RttLog.Line("SimPumpHook not on this bootstrap — restart the game to adopt it. " +
                            "serverPresenceEntity will have NO EFFECT until then.");
            }

            // The whole-scene route. Null on an older bootstrap, which is the expected
            // state until the game is restarted to adopt the new one — the field is
            // looked up rather than assumed so a stale bootstrap degrades to "this
            // route is off" instead of throwing and taking the other hooks with it.
            var wholeScene = bridge.GetField("WholeSceneHook");
            if (wholeScene != null)
            {
                wholeScene.SetValue(null, (Action<object, object>)WholeSceneRender.OnWholeScene);
                RttLog.Line("Whole-scene hook registered (SceneDrawSystem.Draw postfix).");

                // The start-of-frame submission position. Null on an older bootstrap, in
                // which case the postfix keeps doing the render and wholeSceneSubmitEarly
                // is simply inert — worth saying out loud, because a silently ignored flag
                // is how an A/B gets misread.
                var early = bridge.GetField("WholeSceneEarlyHook");
                if (early != null)
                {
                    early.SetValue(null, (Action<object, object>)WholeSceneRender.OnWholeSceneEarly);
                    RttLog.Line("Start-of-frame hook registered (SceneDrawSystem.Draw PREFIX) — " +
                                "set wholeSceneSubmitEarly=1 to move the render there and overlap our " +
                                "GPU work with the player's frame recording.");
                }
                else
                {
                    RttLog.Line("WholeSceneEarlyHook not on this bootstrap — restart to adopt it. " +
                                "wholeSceneSubmitEarly will have NO EFFECT until then.");
                }

                // The culling-view classifier (occlusion scope v2). Absent on an older
                // bootstrap -> wholeSceneNoOcclusion is inert, said out loud.
                var cullView = bridge.GetField("CullingViewIsOursHook");
                if (cullView != null)
                {
                    cullView.SetValue(null, (Func<object, bool>)WholeSceneRender.CullingViewIsOurs);
                    RttLog.Line("Culling-view hook registered — wholeSceneNoOcclusion is armable.");
                }
                else
                {
                    RttLog.Line("CullingViewIsOursHook not on this bootstrap — restart to adopt it. " +
                                "wholeSceneNoOcclusion will have NO EFFECT until then.");
                }

                var skip = bridge.GetField("SkipStageHook");
                if (skip != null)
                {
                    skip.SetValue(null, (Func<int, bool>)WholeSceneRender.ShouldSkipStage);
                    RttLog.Line("Stage-skip hook registered — Draw sub-stages can now be suppressed " +
                                "inside our render only. This is the only lever that reaches stages the " +
                                "settings do not gate, such as acceleration-structure building.");
                }
            }
            else
            {
                RttLog.Line("WholeSceneHook not on this bootstrap — restart the game to adopt it. " +
                            "The probe-based feed is unaffected.");
            }
            RttLog.Line("Hooks registered. Scene-draw recon is read-only and runs on its own.");
            RttLog.Line("Tag a panel [RTT] for blit stages 1-3, [RTT!] to also arm the blit.");
        }
        catch (Exception e) { RttLog.Error("bridge hookup", e); }
    }
}

// THE GRASS CALL CENSUS — does RenderGrass run inside our pass, or not at all?
//
// Fed from the bootstrap's RenderGrass prefix (see the GrassNoHiZHook registration above),
// so it counts every invocation the engine makes, split by whose render it happened in.
//
// WHY THIS IS THE QUESTION. Grass geometry is confirmed present — 51 of 51 clipmap cells
// within 150 m of the feed camera carry a VALID GrassEntity, nearest at 22 m, all at LOD 0
// (the engine refuses grass above LOD 4; verified in IL and reproduced in live memory). The
// settings are confirmed readable inside our pass. So the failure is downstream of state,
// and splits exactly two ways:
//
//   ours == 0     the draw never runs for us. The fix is upstream, in whatever decides the
//                 stage list for our pass — NOT in any grass setting, and no amount of
//                 density or draw distance would ever have helped.
//   ours  > 0     the draw runs and emits nothing, so the generator's input set is empty.
//                 That points at MainViewCulling.EntityProxies, which the bootstrap already
//                 names as the thing RenderGrass generates from.
//
// Both numbers are reported, because "ours = 0" only means something next to a healthy
// player count: if BOTH are zero the hook itself is detached and the reader is blind again,
// which is a different result and must not be read as a negative.
internal static class GrassCallCensus
{
    private static long _ours, _theirs;
    private static long _lastTicks;

    internal static void Note(bool ours)
    {
        if (ours) _ours++; else _theirs++;

        // Rate-limited: this is on the render thread, once per RenderGrass call.
        var now = Environment.TickCount64;
        if (now - _lastTicks < 15000) return;
        _lastTicks = now;

        // The census answers "does the draw run"; the probe answers "over what". Paired here
        // because reading either alone is what made this look like a mystery for two days.
        WholeSceneRender.GrassProbe();

        RttLog.Line($"GRASS CALL CENSUS: RenderGrass ran {_ours} time(s) in OUR pass, " +
                    $"{_theirs} time(s) in the player's (cumulative). " +
                    (_ours == 0 && _theirs == 0
                        ? "BOTH ZERO — the hook is detached, so this is a BLIND READER, not a negative."
                        : _ours == 0
                            ? "OURS IS ZERO while the player's render calls it: the grass draw is NOT " +
                              "RUNNING for the feed. No grass setting can matter until that changes."
                            : "The grass draw DOES run for the feed, so a blade-free picture means it is " +
                              "generating from an empty set — look at MainViewCulling.EntityProxies."));
    }
}
