using System.Globalization;

namespace RttProbe;

// Environment.TickCount64 quantises to the system timer interval — ~15.6 ms on
// Windows unless something has raised the timer resolution. That is invisible at
// 2 fps and decisive at 30: a 33 ms gate rounds up to ~47 ms, so asking for 30 fps
// delivered 20, and asking for 15 delivered 12.8. Both measured exactly.
//
// Stopwatch is backed by QueryPerformanceCounter, so the gate lands where it is put.
// Only the per-frame gates need this; the 2 s arm/config polls do not care.
internal static class Clock
{
    private static readonly System.Diagnostics.Stopwatch Sw = System.Diagnostics.Stopwatch.StartNew();
    public static long Ms => Sw.ElapsedMilliseconds;
}

// Live-tunable knobs, read from output\feed-config.txt and re-read every couple of
// seconds. Tuning by editing a constant and rebuilding wastes a hot-reload cycle per
// experiment; this makes it a file edit.
//
// Anything missing or malformed falls back to the default, so a half-written file
// cannot break the feed.
//
// This used to carry 106 knobs. Sixty-two were bisect switches for the probe scene
// render, which no longer exists — each had a default nobody had changed in weeks and
// a consumer that has been deleted. They are gone, along with the 300-character
// boolean chain that compared them all: the whole-scene group now change-detects on a
// signature string, which is shorter and cannot be forgotten when a knob is added.
internal static class FeedConfig
{
    private static readonly string Path_ = System.IO.Path.Combine(RttLog.OutDir, "feed-config.txt");

    private static long _lastRead;
    private static long _lastStamp;
    private static bool _firstPoll = true;

    public static int EffectivePanelMs => PanelMs > 0 ? PanelMs : IntervalMs;

    public static int IntervalMs { get; private set; } = 66;      // ~15 fps

    // Panel update period, deliberately separate from the camera pass. RequestRender
    // makes the ENGINE run a full DrawOne (borrow, clear, replay UI batches, mipmap,
    // copy, return) for our target — so the two rates cost very different things and
    // must be testable independently. 0 = follow IntervalMs.
    public static int PanelMs { get; private set; }

    // Grace period after the logic loads before any GPU work is issued. Several
    // crashes were "on world load", when the renderer is still settling — pooled
    // targets resizing, panels acquiring their render targets, streaming catching up.
    // Starting into that is asking for trouble.
    public static int StartupDelayMs { get; private set; } = 2000;

    // Point the engine's two per-frame camera constant buffers at ours for the pass.
    //
    // SettingsGroup._jitteredCameraSettings / _nonjitteredCameraSettings are read by ~92
    // methods. Passes that take a cameraCb parameter already use ours; everything else
    // reads these. The immediate defect it fixes is ClusteringJob.DoWork, which builds
    // our cluster grid from the PLAYER'S frustum and therefore bins every clustered local
    // light into the wrong screen-space cluster.
    //
    // It is also the prerequisite for AtmosphereMultiplyJob, AmbientLightJob and HBAOJob.
    // Off by default until proven: if the restore is ever skipped, the engine's remaining
    // passes render the player's screen from our 512x512 orbit camera.
    public static bool SwapCameraCb { get; private set; }

    // Stage 2: construct a second ScreenBuffers and do nothing with it. Proves the
    // public parameterless constructor and InitializeBuffers work before anything is
    // swapped. Costs a second set of screen-sized targets, so not free — but far less
    // than finding out mid-render that it could not be built.
    public static bool WholeSceneBuildBuffers { get; private set; }

    // Stage 3: swap the globals and actually call Draw. The real experiment.
    //
    // Named Enabled rather than WholeSceneRender because that is the CLASS that does the
    // work, and a property with the same name shadows it inside this type — which is
    // exactly the kind of collision that produces a confusing compile error at the worst
    // moment.
    public static bool WholeSceneEnabled { get; private set; }

    // Resolution of the second render. The whole point of this route is that Draw takes
    // its size from the buffer it is handed, so this is a real knob rather than a wish.
    public static int WholeSceneWidth { get; private set; } = 512;

    public static int WholeSceneHeight { get; private set; } = 512;

    // Stage 3b: swap the camera too, so the second render is OUR viewpoint.
    //
    // Off for the first test on purpose. With it off the second render is the player's
    // view at our resolution — wrong, but visually verifiable and with only ONE global
    // moved. Turning it on is then a single attributable change rather than part of a
    // failure nobody can read.
    public static bool WholeSceneCamera { get; private set; }

    // Disable raytracing for the duration of OUR render.
    //
    // Draw builds acceleration structures and steps ReSTIR / IR-cache accumulators that
    // integrate over frames in WORLD space, none of which live in ScreenBuffers — so
    // owning a ScreenBuffers does not isolate them. Running the pipeline twice per frame
    // advances them twice, which showed up immediately as patchy, shifting GI in the
    // PLAYER'S world render.
    //
    // Costs our feed raytraced GI, which was excluded from scope anyway. A feed without
    // RT GI is worth more than a main view with corrupted GI.
    // 0 = leave raytracing alone. 1 = clear Enabled + all accumulators (stops
    // RaytraceGIJob, so ComputeGI's UNCLEARED DiffuseGIBuffer borrow reaches
    // AmbientLightJob as recycled garbage — the ambient flashing on shadowed sides).
    // 2 = clear only the world-space accumulators, keeping Enabled so the GI buffers
    // are still written. 2 is the intended setting if the player's view stays clean.
    public static int WholeSceneDisableRaytracing { get; private set; } = 1;

    // GOAL 11 — COVER-FIT THE SQUARE RENDER ONTO ANY PANEL SHAPE.
    //
    // One square scene render per feed, then ONE textured quad per distinct panel ASPECT into
    // a derived target of that shape, cover-cropped so the long axis fills and the short axis
    // runs off the edge. The panel then samples a texture whose aspect already equals its own,
    // which is what makes the result independent of the user's PreserveAspectRatio toggle:
    // contain has nothing to letterbox and stretch has nothing to stretch.
    //
    // NEVER a second scene render — `request = drawOne(ours) = copies` is the regression test
    // and drawOne must not track panel count. A resample is a quad; a re-render is the world.
    //
    // Costs one small target and one quad per distinct SHAPE, not per panel. Square panels
    // are skipped entirely (the render already matches them).
    //
    // OFF by default so it can be graded against the current behaviour without a rebuild.
    public static bool PanelCoverFit { get; private set; } = false;

    // OWN A SECOND RAYTRACING SCENE (the TLAS) for the feed camera.
    //
    // The raytraced half of ambient is the last thing still anchored to the player. Probe cubes
    // are ours (measured 2026-08-05: CloseIBL/FarIBL both our own captures), RTGIContext and its
    // ReSTIR reservoirs were never shared, but stage 0 — ExecuteAccelerationStructuresBuilding —
    // is skipped for our pass because RayTracingSceneManager.CreateTLAS is camera-dependent and
    // world-space shared. So our rays trace a structure selected for the PLAYER's position.
    //
    // Owning a second manager is the only thing that fixes what the feed CONSUMES; suppressing
    // stage 0 can only ever stop us corrupting the player's. Same shape as wholeSceneOwnProbes
    // (goal 4.4) and per-feed eye adaptation, both of which work.
    //
    // COUPLED TO STAGE 0 IN CODE, not left to the config file. Owning a TLAS and leaving stage 0
    // in wholeSceneSkipStages would give the feed a manager that is never built — silently, with
    // every log line reporting success. That is exactly the bug that cost 2026-08-05: we owned
    // the EnvironmentProbeManager for a goal-4.4 release and left stage 2 suppressed, so the
    // atlas was never rendered and get_CloseIBL served an unrendered cube without complaint.
    // EffectiveSkipStages enforces the pairing so the two settings cannot disagree.
    //
    // REQUIRES A RESTART to adopt: the park lives in the bootstrap. Refuses to arm without it
    // rather than leak _tlasBuffers and the entity pools once per hot reload.
    public static bool WholeSceneOwnRayTracingScene { get; private set; } = false;

    // Own CoreSystems.IRCacheResources for our pass. THE FIX FOR CONTAMINATION POINTING AT
    // THE PLAYER: stage 30 populates a world-space irradiance grid from whichever camera the
    // pass runs, and without ownership our entries land in the shared grid where the player's
    // frame reads them — confirmed 2026-08-06, the feed camera's AIM relighting the player's
    // cockpit once the two cameras shared cells. Defaults ON because the current state is a
    // known bug; refuses to arm (logging loudly) if the bootstrap has no ParkedIRCaches, so
    // an un-restarted game degrades to today's behaviour rather than leaking the buffer set.
    // Deliberately OUTSIDE the rebuild signature — it is a per-pass field swap, like the
    // probe manager and the TLAS.
    public static bool WholeSceneOwnIRCache { get; private set; } = true;

    // Populate our irradiance grid (run stage 30 in our pass). OFF until the TLAS has real
    // content: tracing an EMPTY TLAS writes sky-miss irradiance into every cell the camera
    // visits, which underground is a blinding ambient blast (confirmed 2026-08-07, cave,
    // angle-dependent). Installed-but-empty is the correct interim — isolated both ways,
    // ambient from the probe cubes at the camera, zero RTGI trace cost.
    public static bool WholeSceneIrCachePopulate { get; private set; } = false;

    // Take the engine's own no-RT ambient branch for OUR pass (ComputeGI's else path — the
    // one every RT-off player runs). Kills the sky-miss GI from tracing our empty TLAS: the
    // cave floodlight, angle-flipping, confirmed 2026-08-07 with the cache proven empty.
    // Ambient becomes IBL from the probe cubes at the feed camera. Needs the 2026-08-07
    // bootstrap (NestedRenderRtOff); inert with one log line on an older one.
    public static bool WholeSceneIblOnlyAmbient { get; private set; } = true;

    // The flat linear grey the feed's diffuse GI buffer is cleared to when the trace is
    // skipped. This IS the feed's ambient term until the TLAS is populated: content-only,
    // per-pass, feeds no snapshot and no PSO — the one ambient mechanism that cannot flash
    // (see the 2026-08-07 postmortems: state-input variance strobed both worlds twice).
    // Live-tunable. 0 = black shadows.
    public static double FeedAmbientFloor { get; private set; } = 0.02;

    // Localise SCREEN-SPACE REFLECTIONS to the feed camera.
    //
    // Clears SSSRSettings.EnableTemporalAccumulation for our pass only, which gates the whole
    // denoiser block in ScreenSpaceReflections.DoWork. That block reads the shared radiance /
    // variance / sample-count history, writes our radiance back into it, and stamps our
    // camera's view-projection into _previousViewProjection for the player's next frame — so
    // one flag removes the contamination in BOTH directions.
    //
    // The feed keeps its reflections (the false path still runs _applyReflectionsJob on the
    // raw intersection result); it loses temporal denoising, so they are noisy. That is the
    // agreed trade, and it is CHEAPER than the status quo because the denoiser dispatches are
    // skipped with it.
    //
    // Safe to scope per-pass, unlike RaytracingSettings: DoWork reads CoreSystems.Settings.SSSR
    // directly with no LazyJobSnapshotHandler and no shader defines, so there is no PSO rebuild.
    // Deliberately NOT in the rebuild signature — a scoped setting, not an allocation.
    public static bool WholeSceneSsrLocal { get; private set; } = false;

    // Disable EYE ADAPTATION for our render.
    //
    // ComputeExposure drives EyeAdaptationJob, which ping-pongs a shared auto-exposure
    // history — this project already recorded a second call per frame as unsafe. Our
    // 512x512 view of the same scene has a different average luminance, so the player's
    // adaptation oscillates between the two exposures and their world lighting flickers
    // at exactly our render cadence.
    //
    // Exposure itself stays ON so our image is still exposed; only the TEMPORAL
    // adaptation is cut, which for a fixed-purpose camera feed is arguably correct.
    public static bool WholeSceneDisableEyeAdaptation { get; private set; } = true;

    // Clear HZBOSettings.MainViewEnabled for OUR render only — hierarchical-Z occlusion
    // culling off in the feed. The test for "shadows of trees with no tree": occlusion
    // culling is the one mechanism that can drop an object from the main view while its
    // shadow still draws, and the same flag is handed to RenderGrass as enableHiZ, so it
    // would remove all grass rather than thin it. Costs feed draw calls only; scoped, so the
    // player's occlusion culling is untouched. NOT in the rebuild signature — live A/B.
    // DANGEROUS AND KNOWN-BAD — see the note in WholeSceneRender.ScopeSharedState. Whited out
    // the feed and made the PLAYER'S world flicker on 2026-08-02. Kept only so the failure is
    // recorded where someone would otherwise retry it. Use WholeSceneGrassNoHiZ instead.
    public static bool WholeSceneNoHzbo { get; private set; }

    // v2 of the occlusion scope — the sound one. Occlusion culling disabled ONLY for
    // culling-job compositions whose RenderViewSlim RESOLUTION matches the feed's, the
    // verdict carried in a [ThreadStatic] bracketed around the job's own DoWork. No shared
    // setting is touched (unlike wholeSceneNoHzbo) and no ambient in-our-render flag is
    // trusted across threads (unlike the retired skip id 31, whose misclassification
    // whited the feed and strobed the player's lighting on 2026-08-03).
    //
    // DELIBERATELY NOT IN THE REBUILD SIGNATURE: consulted per call, allocates nothing,
    // so it is safe to flip live — one of the few knobs that genuinely is.
    public static bool WholeSceneNoOcclusion { get; private set; }

    // THE DISTANT-FLORA VISIBILITY THRESHOLD. -1 = leave the engine's value alone.
    //
    // Decoded from FloraSubSectorMesh.UpdateVisibility (the DISTANT merged tier, and the
    // only thing in the feed that has ever flickered):
    //     threshold = Raytracing.FloraMaxDistance * 1.2f
    //     vis       = boundingBox.Distance(camera) < threshold
    //     if (vis != _isVisible) { _isVisible = vis;
    //                              vis ? Update()               // REBUILD the merged mesh
    //                                  : EvictFromRaytracingScene(); }
    //
    // Stock is 250, so the boundary sits at 300 m — INSIDE the band the feed draws (the
    // flora cap runs to 900 m, far clip 2500 m). Every metre of orbit drags meshes across
    // it, and each crossing rebuilds or evicts a whole merged patch: measured at 33 flips
    // per 15 s window, which is exactly the "batchy, sub-second to a second" cadence.
    //
    // Raising it moves the boundary BEYOND anything the feed can see, so crossings stop
    // happening where they are visible. GLOBAL (a RaytracingSettings field), so the
    // player's world gets the same threshold — cost is more distant merged meshes resident,
    // and it is a RAYTRACING setting, so with RT off the extra cost is small.
    //
    // Set it comfortably past wholeSceneFloraMaxMetres: threshold = value * 1.2.
    public static double FeedFloraMaxDistance { get; private set; } = -1;

    // Force RenderGrass's enableHiZ ARGUMENT false inside our render. This is the safe half of
    // the HZBO question: GrassRendering selects its NoHiZ pipeline from that argument alone, so
    // this reaches the grass generator and nothing else — no shared setting, no other consumer,
    // and it cannot reproduce the flicker wholeSceneNoHzbo caused. Needs the bootstrap patch on
    // SceneDrawSystem.RenderGrass, so a game restart to adopt.
    public static bool WholeSceneGrassNoHiZ { get; private set; }

    // Stop our render UPDATING the shared environment-probe atlas.
    //
    // The engine refreshes probe faces round-robin across frames into a shared atlas,
    // and that atlas supplies ambient and reflections. Our second Draw calls
    // RenderEnvironmentProbe too, advancing the queue at double rate and writing faces
    // with our settings. The player then samples it — which presents as indirect
    // lighting misbehaving while direct lights look fine.
    public static bool WholeSceneDisableProbeUpdates { get; private set; } = true;

    // Swap CoreSystems.DrawContexts for our own DrawContextManager during our render.
    //
    // The stage bisect ruled out every skippable stage and the flashing persisted, so
    // the cause is in what remains — and everything that remains culls and ranges
    // through DrawContexts: visibility lists, occlusion, geometry buffers, the shared
    // GPU counters ScenePreparation clears every frame, LOD transitions. The
    // experimental branch recorded the signature exactly: a second cull writing the
    // engine's visibility lists made the player's ship lights flicker.
    //
    // Also the PREREQUISITE for wholeSceneCamera — culling from the orbit camera into
    // the player's contexts would be strictly worse than today's perturbation.
    public static bool WholeSceneOwnDrawContexts { get; private set; }

    // Draw sub-stages skipped INSIDE our render only. Comma-separated ids:
    //
    //   0 ExecuteAccelerationStructuresBuilding    raytracing scene / TLAS
    //   1 ExecuteRaytracingPrepareAndSceneFinalize raytracing prepare
    //   2 RenderEnvironmentProbe                   shared probe atlas (ambient + reflections)
    //   3 RenderShadows                            shadow cascades
    //   4 ComputeExposure                          auto-exposure history  ** SEE BELOW **
    //   5 UpdateSurfels                            water surfels
    //
    // STAGE 4 IS NOT SAFELY SKIPPABLE. ComputeExposure has OUT PARAMETERS:
    //
    //     ComputeExposure(cl, lBuffer, out ITexture2DView exposure, out Nullable<..>)
    //
    // A prefix that skips it leaves those null, and ApplyBloom and ApplyToneMapping
    // consume them immediately — instant NullReferenceException. It is listed so the
    // distinction is on record: the other stages only have SIDE EFFECTS, this one
    // PRODUCES something Draw needs. Use WholeSceneDisableEyeAdaptation for the
    // exposure-history problem instead.
    //
    // This mechanism exists because settings flags cannot reach everything.
    // ExecuteAccelerationStructuresBuilding is called unconditionally at the top of Draw
    // and checks only EnableGPUParallelization, so clearing RaytracingSettings.Enabled
    // never stopped it — three rounds of settings scoping missed it for that reason.
    //
    // Default is the two raytracing stages plus the probe atlas: all world-space, all
    // read by the player's next frame, none of them needed for a camera feed.
    public static int[] WholeSceneSkipStages { get; private set; } = { 0, 1, 2 };

    // Rebuild the whole-scene RenderView through the engine's SetResolution +
    // SetCameraParameters instead of three raw field overrides. Fixes the squashed
    // 16:9-into-1:1 aspect and the stationary sky (ViewAt0/InvViewAt0) — but its first
    // outing ended in a GPU page fault in a bloom chain after ~45 renders, with three
    // sub-changes landed at once. OFF = the proven squashed-but-stable baseline;
    // flip live to re-test and bisect.
    public static int WholeSceneCameraRebuild { get; private set; }

    // AA mode for the whole-scene render: -1 leave engine (FSR: temporal, ghosts at 5fps),
    // 0 none, 1 FXAA (spatial-only — the right choice for this feed), 2 FSR. Also forces
    // ScalingMode NativeAA and sharpening off while >= 0.
    public static int WholeSceneAAMode { get; private set; } = -1;

    // Force ScalingMode NativeAA + sharpening off for our render. SEPARATE from the AA
    // mode because bundling them CTD'd in the post chain: ScalingMode selects between
    // UpscaleTargetFSR and ApplyNonFSRUpscalingAndAA, which borrow differently-sized
    // targets against our fixed-geometry ScreenBuffers. The riskier half.
    // Scope PostProcessSettings.Bloom off for our render only. BloomJob retains its
    // cascade borrows on the SHARED SceneDrawSystem._bloomJob; see ScopeSharedState.
    public static bool WholeSceneNoBloom { get; private set; }

    // Far plane for OUR view only, metres. 0 = keep the player's far plane. The second
    // render's measured cost is CPU submit (draw-command building), so culled-in draw
    // COUNT is the lever — not pixels. VeryFarClipping is untouched, so the planet/sky
    // layer still renders and only asteroid/grid geometry beyond the clip is dropped.
    // Live-tunable; not part of the rebuild signature.
    public static double WholeSceneFarClip { get; private set; }

    // Let WholeSceneFarClip EXTEND the far plane, not just pull it in.
    //
    // Off by default because the min-only behaviour is correct for the perf lever this knob
    // was built as. On, the configured value wins in both directions — which is what the
    // remoteness investigation needs, because the engine's own FarClipping (~15 km) was
    // silently capping the feed and making "nothing is drawn out there" ambiguous between
    // our far plane and the engine's streaming. See docs/open-question-remote-streaming.md.
    //
    // Costs draw count: the far plane is what culling reads, so widening it widens the cull.
    // Live-tunable; not part of the rebuild signature.
    public static bool WholeSceneFarClipExtend { get; private set; }

    // A/B gate on the FinalLDR resize (the phantom-bleed fix). In the rebuild signature
    // so flipping it forces the rebuild that re-runs (or skips) the one-shot resize.
    public static bool WholeSceneLdrResize { get; private set; } = true;

    // START-OF-FRAME SUBMISSION. Record our render in Draw's PREFIX (before the player's
    // frame) instead of its postfix (between the player's frame and the present copy), so
    // our GPU work executes while the CPU is still recording the player's frame.
    //
    // The targeted fix for the session drift: our render's true GPU work is ~3ms, but an
    // ours-frame costs ~30ms because the GPU idles waiting, and that idle grows with engine
    // session age. This moves the waiting somewhere it costs nothing.
    //
    // Deliberately NOT in the rebuild signature: no resources change, so flipping it needs
    // no rebuild and no settle window — it is a clean live A/B. Costs one frame of feed
    // latency. Off by default so adopting the new bootstrap is inert until asked.
    public static bool WholeSceneSubmitEarly { get; private set; }

    // Render only when the delivery chain will consume the frame (task #25). The copy hook
    // sets RenderWanted when it lands a frame on the panel; TryRender consumes the flag and
    // declines the turn otherwise, with a heartbeat so warm-up and a stalled copier cannot
    // starve the pipeline. Renders then track the panel's true refresh instead of running
    // one per engine frame and discarding half of them undelivered.
    //
    // MEASURED PREREQUISITE (2026-08-07 sprint): sparse renders inflate their own CPU
    // submit 2.5ms -> 17.8ms because the nested Draw's internal GPU flush lands against a
    // full queue (interval50 A/B). Pair this with wholeSceneSubmitEarly=1, which moves the
    // render where the queue is empty — on-demand WITHOUT early-submit measured as a
    // zero-gain trade and is not worth flipping.
    public static bool WholeSceneRenderOnDemand { get; private set; }

    // Heartbeat for the on-demand gate, ms: render anyway if no copy consumed a frame for
    // this long. Keeps probes/adaptation warm across copier stalls; 0 disables renders
    // entirely between copies (do not — dormancy already has a real gate).
    public static int WholeSceneOnDemandHeartbeatMs { get; private set; } = 250;

    public static bool WholeSceneNativeScaling { get; private set; }

    // Feed exposure bias, in EV STOPS. Signed: +1 doubles brightness, -1 halves it.
    //
    // Confirmed against the shipped HLSL rather than guessed. With adaptation scoped off
    // the feed runs ConstantExposure.hlsl:
    //
    //     exposure = CalculateExposure(Post_.ConstantLuminance, Post_.LuminanceExposure)
    //     CalculateExposure: log2(keyValue(avgLum)/avgLum) + exposure   [Exposure.hlsli]
    //     GetExposure():     exp2(that)                                [GetExposureLinear]
    //
    // ConstantLuminance is 1, and LuminanceKeyValueCendos(1) == 1, so the base term is
    // log2(1/1) = 0 — LuminanceExposure is a pure EV offset on unity. The engine's live
    // value is 0, which is why 0 doubles as "leave it alone" here at no cost.
    //
    // Was gated `> 0`, which silently made the knob brighten-only. A 512px feed pointed
    // at a sunlit planet needs to come DOWN more often than up.
    public static double WholeSceneExposure { get; private set; }

    // DARKENING OFFSET ON THE ADAPTED EXPOSURE. Negative = darker. 0 = off.
    //
    // SEPARATE FROM WholeSceneExposure ON PURPOSE. That one is the FIXED-STOP FALLBACK used
    // when we are not adapting; giving it a second meaning would make "the feed is dark"
    // ambiguous between "adaptation settled there" and "someone pinned it". This one biases
    // the ADAPTED result and leaves adaptation running underneath.
    //
    // Live-tunable; NOT in the rebuild signature — it is a per-render settings scope, so
    // retuning it costs nothing and needs no gate cycle.
    public static double WholeSceneExposureOffset { get; private set; }

    // ---- MANUAL CAMERA FLIGHT ---------------------------------------------------------
    // 1 = the orbit is suspended and the feed camera flies on WASD / Space / Ctrl / Q / E.
    // Live: flipping it mid-session takes effect on the next render, and the flown position
    // survives in output/camera-state.<anchor>.txt — never in the world save.
    public static bool CameraManualControl { get; private set; }

    // Degrees per second of roll on Q/E.
    public static double CameraRollRate { get; private set; } = 45.0;

    // Metres per second. The live value is owned by CameraControl once flying (so it can be
    // changed in-game later); this is the starting value and the one a fresh site uses.
    public static double CameraSpeed { get; private set; } = 20.0;

    // Mouse-look sensitivity. Scaled by the zoom factor at use, so aiming stays proportional
    // when zoomed in rather than becoming unusably twitchy.
    public static double CameraLookSensitivity { get; private set; } = 1.0;

    // Per-axis look inversion, LIVE. Ship both as knobs because a sign is not something to
    // guess: yaw came back inverted in game and pitch could not be judged because it was dead.
    // On this machine a restart is the only safe deploy, so a sign question must be a config
    // edit, never a rebuild.
    // Despawn the presence markers while a save is collected. DEFAULTS OFF: it caused a
    // NullReferenceException in the engine's spatial-trigger removal (VoxelPhysicsComponent
    // .ReleaseChunk) when the marker was destroyed, and the save contamination it guards
    // against was never demonstrated. See WorldGrids.SaveHoldActive for the full evidence.
    public static bool MarkerDespawnOnSave { get; private set; }

    public static bool CameraInvertLookX { get; private set; }
    public static bool CameraInvertLookY { get; private set; }

    // ---- AUTO APERTURE (2026-08-01) ----------------------------------------------
    //
    // WholeSceneExposure alone is a FIXED stop, and a fixed stop cannot be right twice:
    // -2 EV matched the world at night and blew out over a sunlit flower meadow. The feed
    // needs to open and close like an eye — but it must NOT use the engine's eye adaptation,
    // whose history is shared with the player (that is what made feed brightness track where
    // the PLAYER stood), and owning a second EyeAdaptationJob removed the device twice.
    //
    // So: drive the stop from the SUN, on the CPU, with no new GPU resource at all. Sun
    // elevation against the subject's local up is the variable that actually separates the
    // two failing cases, we already compute planet-radial up for the orbit, and the whole
    // thing costs a dot product per render.
    //
    // What it deliberately does NOT do is meter the picture. Pointing the camera into a cave
    // will not open the aperture. That needs a real measurement (a readback of our own
    // smallest mip) and belongs behind this, once this proves the seam.
    //
    // EVERY KNOB HERE IS OUTSIDE THE REBUILD SIGNATURE, on purpose. Tuning an exposure curve
    // is exactly the kind of thing you do twenty times in a row, and the 15:08 device removal
    // was a signature key edited on a live feed. These are read fresh per render.
    // PARKED, DEFAULT OFF. Sun-driven exposure was built and then overtaken by events: with
    // stage 25 correctly skipped, LuminanceExposure is not read at all (ConstantExposure
    // returns the existing view), so nothing here can reach the image. Un-skipping 25 to make
    // it reach WOULD over-expose the player's whole world — see the note on wholeSceneExposure.
    //
    // The scaffolding and the scan diagnostics stay because the successor needs them: a real
    // per-feed aperture wants its own entry in EyeAdaptationJob._autoExposures (a COLLECTION,
    // and the engine already drives a separate _environmentProbeExposureJob), at which point
    // a curve like this one is what feeds it.
    public static bool FeedAutoExposure { get; private set; } = false;

    // EV at full day (sun overhead) and at night (sun below the horizon). Day is the more
    // negative number: a bright scene needs a smaller aperture.
    public static double FeedExposureDay { get; private set; } = -5.0;
    public static double FeedExposureNight { get; private set; } = -2.0;

    // Sun elevation, as dot(sunToward, localUp), over which the curve ramps. The default
    // spans a little below the horizon to well above it, so dawn and dusk are a glide
    // rather than a step.
    public static double FeedExposureDawnDot { get; private set; } = -0.15;
    public static double FeedExposureDayDot { get; private set; } = 0.25;

    // Seconds for the stop to travel most of the way to its target. This is the "aperture
    // has inertia" term; without it a cloud crossing the sun would step the panel.
    public static double FeedExposureAdaptSeconds { get; private set; } = 4.0;

    // Sign of the engine's sun vector. +1 if the field points TOWARD the sun, -1 if it is
    // the direction light travels. Config rather than an assumption: getting it backwards
    // makes the feed brightest at midnight, which is obvious on sight and would otherwise
    // cost a rebuild to flip.
    public static double FeedSunSign { get; private set; } = 1.0;

    // Explicit list of RaytracingSettings flags to clear during our render. Empty = use
    // the wholeSceneDisableRaytracing preset instead.
    //
    // RaytracingSettings has TWENTY booleans and the presets only ever touched six. The
    // symptom we are chasing ("subtle flashing that seems to come from light sources")
    // has two flags named for it — LocalLightsInIRCache and LocalLightsInRTXGI — that no
    // preset clears, and there is a master EnableReSTIR that the accumulator preset also
    // misses, so candidates keep getting written into the shared reservoirs.
    //
    // Bisecting twenty flags by editing an enum and rebuilding is the wrong shape. This
    // makes each hypothesis a config edit against a running game.
    //
    // CAUTION: RaytraceGIJob holds a LazyJobSnapshotHandler<RTGISettings, RTGISnapshot>
    // and builds SHADER DEFINES from those settings. Flags that feed a define will force
    // a pipeline rebuild when toggled — which is the mechanism behind the bright flashing
    // that clearing Enabled produced. Expect some flags to be free and others to cost a
    // visible rebuild.
    public static string[] WholeSceneRtFlags { get; private set; } = Array.Empty<string>();

    // Rebuild the planet environment (PlanetSpheres / PlanetEnvSetupFirst /
    // AllPlanetEnvSetups) from OUR camera before our Draw, and again from the player's
    // after. These per-frame CBs are built from the player's view in
    // PlanetEnvironmentGroup.OnBeginDraw and inherited by a nested render — which is why
    // the feed's planet atmosphere detached from the planet and moved with the player's
    // aim. Twenty-six jobs read them, atmosphere and volumetrics included.
    public static bool WholeScenePlanetEnv { get; private set; }

    // LENS FLARES IN THE FEED (roadmap goal 4.3). Default OFF — it changes which context
    // receives flare REGISTRATION, and the conservative arrangement it replaces was put
    // there deliberately.
    //
    // Today our render installs the ENGINE'S FlaresContext and skips stage 21. That is
    // correct but gives the feed no flares at all: registration goes through the global
    // (PointLightEntityComponent.Init / SetParameters / OnRemovedFromScene, the spot and
    // particle equivalents, SceneManager.UpdateFlareDefinitions), so whichever context is
    // installed receives it — and running RenderFlares against the shared context would
    // advance ProcessFinishedFrame / PrepareReadback twice per frame and corrupt the
    // player's flare occlusion.
    //
    // With this ON we keep OUR OWN FlaresContext installed and share only the four
    // DEFINITION members from the engine's: _flaresByGuid, _texturePinsByGuid,
    // _flaresBuffer and _flareDefinitionsAllocator. Verified offline with EngineQuery:
    //
    //   * those four are written ONLY by FlaresContext..ctor and UpdateFlaresBuffer —
    //     neither is in the render path, so our render cannot mutate them;
    //   * ProcessFinishedFrame and PrepareReadback touch ONLY _occlusionCounterBuffers,
    //     _drawCommandsCounterBuffers, _occlusionCount, _instancesCount and
    //     _flareDrawBuffersCapacity — all of which are then OURS, so the player's readback
    //     is untouchable;
    //   * the ctor allocates every RW buffer up front (CreateResizableRWBuffer x5 plus both
    //     counter arrays sized from FrameSpanManager.ResourceSpanCount), so our context is
    //     fully formed with no lazy path to trip over.
    //
    // KNOWN BOUNDED RISK. AddFlare / UpdateFlare / RemoveFlare call UpdateFlaresBuffer,
    // which REPLACES _flaresBuffer on whichever context is installed. Those run from
    // render-command-buffer replay, which happens before Draw and therefore outside our
    // window — but that is inference, not proof. If one ever did land inside our window it
    // would set OUR buffer and leave the engine's holding the previous one, so the player
    // would be stale by one flare until the next registration. Degraded and self-healing,
    // not broken. The definition members are re-read from the engine on EVERY render rather
    // than copied once, so nothing accumulates.
    //
    // Forces stage 21 to run, the same way WholeSceneOwnShadows forces stage 3: owning the
    // context is pointless if the pass that reads it never executes.
    //
    // ==========================================================================
    // BROKEN. DO NOT ENABLE. Two CTDs, and the SECOND one found the real flaw.
    // ==========================================================================
    //
    // Both crashes were the same NullReferenceException in FlaresContext.GetFlareConstants,
    // which does `_flaresBuffer.GPUBufferId` unguarded. The first was diagnosed as stage 21
    // being force-run before the mirror had happened, and fixed with the _flaresReady guard.
    // That fix is correct and is still in place — but it addressed the wrong half, because
    // the second CTD happened WITH the guard active.
    //
    // THE ACTUAL BUG IS LIFETIME, NOT READINESS. MirrorFlareDefinitions copies REFERENCES
    // out of the engine's context into ours, and one of them, _flaresBuffer, is a GPU
    // resource. Our FlaresContext hangs off OUR DrawContextManager. When the gate tears down
    // — a pause, a config change, a hot reload — Reset() disposes that DrawContextManager,
    // which disposes our FlaresContext, which disposes _flaresBuffer. That reference IS the
    // ENGINE'S buffer. We free the player's flare definitions, the engine's context keeps
    // pointing at the freed object, and its very next flare pass dereferences it.
    //
    // The timing is the proof. Both crashes landed immediately after a gate teardown:
    //   CTD 1  20:20:20  right after a pause/resume cycle
    //   CTD 2  20:59:52  one second after "FEED GATE: DORMANT" from a pause
    // Neither happened during steady-state rendering, which is why flares looked fine for
    // twelve minutes the first time.
    //
    // _flaresReady cannot help: it protects OUR pass from reading a null buffer. It says
    // nothing about us having freed the ENGINE'S.
    //
    // THE FIX, unbuilt: null the four mirrored fields on our context BEFORE our
    // DrawContextManager is disposed, so its dispose chain cannot reach objects we do not
    // own. That needs a teardown hook ordered strictly before the DC dispose, and getting
    // that order wrong is another crash — hence not attempted at the end of a session with
    // four CTDs in it.
    //
    // GENERAL LESSON, worth more than the feature: sharing a reference INTO an object you
    // will later dispose makes you the owner of something you did not allocate. Borrowing
    // read-only state is only safe if the borrower's lifetime is strictly shorter AND its
    // teardown cannot touch what it borrowed. Ours failed the second condition. Every other
    // shared object in this route is shared the other way round — we hand OUR resources to
    // nobody, and we read the engine's without holding them past a frame.
    public static bool WholeSceneOwnFlares { get; private set; }

    // OWN ENVIRONMENT PROBES (roadmap goal 4.4). Default OFF — this is the most expensive
    // feature on the roadmap and the first one expected to cost measurable frame time.
    //
    // WHY IT MATTERS. This is a REMOTE camera, and the feed currently samples the PLAYER'S
    // probe atlas, which is centred on the player. So the feed's reflections and ambient
    // bounce are correct for where the player is standing rather than where the camera is.
    // Subtle at a 100 m orbit, visibly wrong at real remote-camera distances, and worse the
    // further the camera goes. A correctness problem dressed as a fidelity one.
    //
    // WHY IT COULD NOT SIMPLY BE UNSKIPPED. Two facts, both verified offline:
    //   * the atlas is EIGHT cube textures held per-MANAGER on the global
    //     CoreSystems.EnvironmentProbeManager, so rendering probes without our own manager
    //     writes the PLAYER'S atlas from the orbit camera — the old "ambient and reflections
    //     from light sources but not the lights themselves" bug;
    //   * DrawContexts.EnvProbesToUpdate is written ONLY by DrawContextManager.OnBeginDraw,
    //     which runs in DrawInternal and never in our nested Draw. Our context's queue is
    //     therefore permanently empty, which is exactly why skipping stage 2 costs nothing
    //     today: we were never consuming it.
    //
    // WHAT THIS DOES. Owns the object, the pattern already proven for ScreenBuffers and
    // DrawContextManager: construct our own manager, swap the global for the duration of our
    // render, fill our own queue from our own PrepareProbes(), and let stage 2 run.
    // PrepareProbes on OUR instance is not a Rule 8 violation — Rule 8 forbids advancing a
    // GLOBAL manager's state twice per frame, and ours is shared with nobody.
    //
    // RISK, honestly. The ctor allocates NOTHING (it calls only Object..ctor), so
    // construction is free — the textures are created later in RecreateProbes, reached from
    // PrepareProbes. That is the allocation to respect, and it is also guaranteed to trip
    // _forceReprocess, so it must land inside the 30-frame settle window (Rule 19). Failure
    // is graceful rather than fatal: CloseIBL/FarIBL fall back to CommonResources.SkyboxIBL,
    // so a probe that is not ready degrades to the skybox term instead of binding null.
    //
    // Forces wholeSceneDisableProbeUpdates OFF in effect — see WholeSceneRender. Scoping
    // ProbeSettings.Enable off existed purely to stop us updating the SHARED atlas, and with
    // our own manager installed that reason is gone. Leaving it on would make PrepareProbes
    // do nothing and this feature would silently be a no-op.
    public static bool WholeSceneOwnProbes { get; private set; }

    // Flare intensity for the FEED ONLY, scoped like exposure and bloom. Negative = leave
    // the engine's value alone (default).
    //
    // Reported once flares went live: the sun flare is "extremely bright" and shows "a faint
    // square box around the edges of the square flare texture". Both are the same symptom.
    // The feed runs a FIXED exposure — eye adaptation is scoped off because its history is
    // shared, and stage 25 makes exposure read-only — so it has no auto-adaptation to pull a
    // blown highlight back, and on top of that the panel material multiplies by
    // EmissivityMultiplier (10 by default). A flare quad that saturates then shows its
    // texture's near-zero alpha border as a visible rectangle.
    //
    // FlaresIntensity is the right lever rather than emissivity: GetFlareConstants reads it
    // straight into FlaresConstantData.IntensityMultiplier, so scoping it touches ONLY the
    // flare pass, and only during our render. Lowering emissivity instead would dim the
    // whole feed, and it lives on a material shared with every other LCD in the world.
    //
    // Deliberately NOT in the rebuild signature: it is a pure per-render scope, so retuning
    // it takes effect on the next render with no rebuild and no settle window. That makes
    // finding the right value a live sweep instead of a series of gate cycles.
    public static double WholeSceneFlareIntensity { get; private set; } = -1;

    // ---- THE SCATTER CONTROL SURFACE (2026-08-02) -------------------------------------
    //
    // Everything that decides how much flora, clutter and grass the feed sees, and how far
    // out. Found by reading the CONSUMERS rather than by guessing at settings names, which
    // is what finally separated the live knobs from the dead ones:
    //
    //   Flora.RenderingDistanceMultiplier -> InstanceBatch.ComputeCullingDistance, whose
    //                                        entire body is `Settings.Flora.RenderingDistance
    //                                        Multiplier * arg`. THE SPAWN RADIUS.
    //   Flora.LODDistanceMultiplier       -> LODSetup.Compose -> GlobalFloraDistanceMult
    //   Flora.FadeDistancePercentage      -> InstanceBatch.UpdateRenderData
    //   LOD.MainView.{LODShift,MinLOD,FloraMinLOD} -> CullingJob.DoWork
    //   Grass.{DrawDistance,Density}      -> RenderGrass
    //
    // THE SPLIT THAT MATTERS IS *WHEN* EACH IS READ, and it decides which of these can be
    // feed-only and which cannot:
    //
    //   PER PASS — read inside our nested Draw, so ScopeSetValues gives the FEED its own
    //   value and the player's frame is untouched. LODSetup.Compose is reached from
    //   SceneDrawSystem.MainViewCulling, which our probe already reports as present in our
    //   pass. These are the `wholeSceneFlora*` / `wholeSceneGrass*` / `wholeSceneLod*` knobs.
    //
    //   AT BATCH ALLOCATION — ComputeCullingDistance is called from InstanceBatch.Initialize
    //   <- AllocateInstanceBatch <- AddInstanceInInstanceBatches. The radius is BAKED INTO
    //   THE BATCH when flora is first added to the octree, which happens during the sector
    //   update, NOT during our Draw. A per-pass scope would miss it entirely. So the radius
    //   knob is GLOBAL and is named `world...` rather than `wholeScene...` to say so.
    //
    // Naming is the documentation here: `wholeScene*` = our render only; `world*` = the
    // player's view changes too, and so does VRAM.

    // Flora LOD distance multiplier, OUR RENDER ONLY. -1 = do not scope.
    //
    // LODSetup.Compose computes:
    //     GlobalFloraDistanceMult = MathF.Min(1, 1080 / SwapChain.Resolution.Y) * this
    // and SwapChain is the PLAYER'S swapchain — the only one the engine has.
    //
    // LOWER = MORE FLORA, and this is the opposite of what the name suggests. MEASURED
    // 2026-08-02, in game, on a frozen orbit: 2.4 cut visible trees from ~15-20 to ~4.
    // The value scales the MEASURED DISTANCE that LOD selection consumes, not the LOD
    // threshold distances — so raising it makes every plant read as farther away, pick a
    // coarser LOD, and fall off the end of the LOD chain entirely.
    //
    // Which re-reads the engine's own term: at 4K, min(1, 1080/2160) = 0.5 makes flora read
    // as HALF as far, so it picks finer LODs and more of it survives. That is the engine
    // being GENEROUS at high resolution, not penalising it — and our feed already inherits
    // that generous value. The first version of this comment had the sign backwards and
    // called 2.4 a "parity fix"; the in-game A/B refuted it in one shot.
    //
    // The "for parity" figure the probe prints is what the feed's OWN height would earn.
    // That is STINGIER than what it inherits, so it is a reference point, not a target.
    public static double WholeSceneFloraLodMult { get; private set; } = -1;

    // Per-pass LOD floors, OUR RENDER ONLY. LODSettings.MainView is a PassLODSettings, and
    // the engine already treats LOD as a per-pass concept — these are its own fields, not
    // something we invented. -999 = do not scope (0 and negatives are meaningful values:
    // LODShift = -1 asks for one level MORE detail than the distance would pick).
    public static int WholeSceneLodShift    { get; private set; } = -999;

    // HARD METRE CAP ON FLORA DRAW DISTANCE. -1 = off, use whatever the engine baked.
    //
    // The separate distance cull, kept separate from LOD ON PURPOSE. worldFloraRadiusMult
    // multiplies each model's own LOD distance, so it yields metres nobody chose and a
    // different answer per model; wholeSceneLodShift trades foreground detail for background
    // cost across the board. This clamps InstanceBatch._cullingDistance — the field
    // UpdateVisibility actually tests — so near flora keeps full detail and far flora simply
    // is not drawn.
    //
    // PICK IT AGAINST THE HORIZON. From ~20 m up on a 60 km planet the geometric horizon is
    // sqrt(2*60000*20) ~= 1550 m. Anything past that is flora drawn over the curvature with
    // no ground under it, which is what the feed was doing at a 2500 m far clip.
    //
    // Safe to change live: the clamp is idempotent and re-applied on a cadence, so a NEW
    // value only ever takes effect downward. Raising it back does NOT restore batches
    // already clamped — those keep the lower distance until they cycle out — so treat an
    // increase as needing a reload, the same way the radius multiplier does.
    public static double WholeSceneFloraMaxMetres { get; private set; } = -1;
    public static int WholeSceneFloraMinLod { get; private set; } = -999;

    // General object LOD distance, OUR RENDER ONLY. -1 = do not scope. These reach every
    // entity in the culling pass, not just flora, so they are the lever for "the feed's
    // distant grids and rocks are one LOD too coarse" as distinct from the flora-specific
    // knobs above.
    public static double WholeSceneObjectDistanceMult { get; private set; } = -1;
    public static int    WholeSceneSmallObjectMult    { get; private set; } = -999;

    // Grass, OUR RENDER ONLY. -1 = do not scope. Measured engine defaults are DrawDistance
    // 1000 and Density 3; MAX_GRASS_RENDERING_DISTANCE is a static clamp, so asking for more
    // than it will simply be ignored by the engine rather than misbehave.
    public static double WholeSceneGrassDrawDistance { get; private set; } = -1;
    public static double WholeSceneGrassDensity      { get; private set; } = -1;

    // Parallax occlusion mapping, OUR RENDER ONLY. This is the "close-up ground is flat"
    // knob, and it is a MATERIAL SHADER effect, not geometry: it fakes depth in the surface
    // by ray-marching a height map in the pixel shader. That is why the LOD work did nothing
    // for the reported symptom — no amount of geometry LOD adds relief to a flat texture.
    //
    // ParallaxSettings sits on SettingsManager as `_parallax`, alongside `_grass`, `_flora`
    // and `_lod`, and carries {Enabled, FadeoutDistance, EnableSelfShadow, ShadowMaxLength,
    // MaxStepCount}. Same family, same ScopeSetValues path.
    //
    // WHAT IS NOT YET PROVEN, and the reason the probe below exists: whether the consumer
    // reads it per-pass (inside our nested Draw) or bakes it into a shader define. If it is
    // a define, this scope will be inert at best — see the RaytracingSettings note in
    // WholeSceneRender.ScopeScatter for why a define-driven setting must NOT be toggled per
    // frame. So DO NOT raise these until the probe has reported the consumer.
    //
    // Tri-state on the bools: -1 = do not scope, 0 = force off, 1 = force on. -1 for the
    // numbers likewise, except MaxStepCount where -999 is the sentinel (0 is meaningful).
    public static int    WholeSceneParallax             { get; private set; } = -1;
    public static double WholeSceneParallaxFadeout      { get; private set; } = -1;
    public static int    WholeSceneParallaxSelfShadow   { get; private set; } = -1;
    public static double WholeSceneParallaxShadowLength { get; private set; } = -1;
    public static int    WholeSceneParallaxSteps        { get; private set; } = -999;

    // Give the feed camera a vote in TEXTURE MIP selection. See TextureCamera.cs for the
    // mechanism and the safety argument; the short version is that CollectStandards feeds a
    // CLOSEST-distance collector, so our camera can only ever demand HIGHER resolution and
    // can never demote a texture the player needs.
    //
    // THIS IS NOT viewerDistance. That one patches CalculateDistanceToCamera, which sets the
    // streaming BUCKET. This one sets WHICH MIP IS RESIDENT. They are different code paths
    // with different consumers, and treating them as one thing is what left the feed looking
    // flat while viewerDistance was reported as "no visible difference".
    //
    // BOOTSTRAP FEATURE: the prefix lives in RttProbe.dll, so a GAME RESTART is required to
    // adopt it. Until then this knob does nothing however it is set, and TextureCamera says
    // so out loud rather than failing silently.
    //
    // COST: the prefix runs per root entity per collection pass on scene job threads. It is
    // branch-and-arithmetic only, no allocation, and early-outs on one static bool when off.
    // VRAM: resident textures go UP when this works — that is the entire point — so watch
    // headroom and do not arm it while already over budget.
    public static bool FeedTextureCamera { get; private set; }

    // HOW MUCH NEARER OUR CAMERA MUST BE TO TAKE AN ENTITY OVER. 1.0 = the old strict
    // comparison, i.e. no hysteresis.
    //
    // MEASURED, NOT GUESSED: with the strict comparison, 4.2% of repeat decisions alternated
    // — ~4000 entities a second swapping which camera picks their mip. A demanded mip that
    // oscillates is a texture that loads and drops, and a texture that is not resident is not
    // drawn, which is the distant-foliage flashing. Proven by the user's own A/B: with
    // feedTextureCamera off, that foliage is not merely flatter, it is ABSENT.
    //
    // ONLY THE ENTRY SIDE IS DAMPED. Holding an entity past the point where our camera stops
    // being nearer would write a LARGER distance than the engine's own and demote a texture
    // the player needs — the one direction this path must never move. So entry costs a
    // margin, exit is immediate.
    //
    // Read the effect in the FEED TEXTURE CAMERA line's STABILITY field: this is aimed at
    // the alternation percentage, and a fix that does not move it did not work.
    public static double FeedTextureCameraEnterRatio { get; private set; } = 0.85;

    // HOW FAR AN ENTITY'S DISTANCE MUST MOVE BEFORE WE RE-PRESENT IT. 0 = no latch.
    //
    // THIS IS THE FIX; the enter ratio above is not. Measured: tier requests per 15 s window
    // ran 9000-16500 with the mip override ON versus 172-532 with it OFF — a 25-30x
    // amplification that is ours — with up and down balanced and ~88% of movements being
    // direction REVERSALS. That is oscillation, not a world settling.
    //
    // The cause is that our camera ORBITS: every entity's distance slides continuously, so
    // target tiers cross boundaries constantly and thousands of textures re-tier every
    // second. Foliage is alpha-tested, so a texture dropping a tier does not soften, it
    // vanishes. Latching the presented distance makes tiers change in deliberate steps
    // instead of sliding with the orbit.
    //
    // Judge it on TEXTURE TIER CHURN, specifically the REVERSAL count — that is the number
    // this exists to move, and the control arm (override off) is the floor to aim at.
    public static double FeedTextureCameraDistanceStep { get; private set; } = 0.20;

    // ---- THE SATURATION FLOOR: the fix the mechanism actually points at ---------------
    //
    // ApplyStandardMaterials clamps priority at 2.0, which every texture presented closer
    // than P/(2D) hits — P = pixelsPerSurfaceMeterBase, D = DefaultTexelDensity. Full
    // resolution is already reached at P/D, so presenting closer than P/2D CANNOT improve the
    // image; it only collapses our textures into one tied priority band in the single global
    // streaming pool. When that pool fills, which tied entry survives is decided by
    // tie-breaking and can change every cycle — textures form, drop, re-form. The log line
    // TEXTURE PRIORITY SATURATION prints the measured floor.
    //
    // MULT is applied to that computed floor. 1.0 sits exactly on the clamp; slightly above 1
    // buys a graded priority, so the cut falls at a defined point in OUR set (nearest kept,
    // farthest dropped) instead of reshuffling. Needs the bootstrap floor — a RESTART.
    public static double FeedTextureCameraMinDistMult { get; private set; } = 1.0;

    // The HOT-RELOADABLE approximation of the same thing: pull the virtual texture-camera
    // back along the centre->eye ray, in metres, so near content is presented farther and
    // lifts out of the clamp. Coarser than the per-entity floor because it is one global
    // offset, but it needs no restart. 0 = off.
    public static double FeedTextureCameraBackoff { get; private set; }

    // CEILING on the presented distance, metres. 0 = off. THE UNTESTED DIRECTION of the
    // priority hypothesis: priority = P/(distance*D), so clamping distance DOWN pushes our
    // foliage UP the single global priority ordering, off the eviction cut it currently sits
    // on. 900 m -> 20 m is roughly 44x more priority.
    //
    // Only the floor was ever tested (priority DOWN, no change), and calling the hypothesis
    // dead on that was an overclaim: a null result in the direction that makes things worse
    // proves little.
    //
    // *** VRAM: raising priority also drops the demanded mip, ~4x the bytes per step, on a
    // machine already VRAM-bound before the mod loads. Warn, watch, never set this
    // automatically. ***
    public static double FeedTextureCameraMaxDist { get; private set; }

    // GLOBAL flora LOD distance multiplier. -1 = leave the engine's value alone.
    //
    // THE PER-PASS VERSION COULD NEVER WORK, and this is the measured reason rather than a
    // design preference. The OCTREE decides which instances a sector supplies OUTSIDE our
    // render, reading FloraSettings.LODDistanceMultiplier as it stands then; our draw filters
    // that set INSIDE our render. A per-pass scope therefore guarantees disagreement, and
    // instances in the gap are supplied-by-one-half-and-rejected-by-the-other — which
    // flickers as the orbit shifts distances slightly.
    //
    // A/B IN GAME 2026-08-02, both directions, user-observed:
    //     scoped 0.85 (octree 1.2 / draw 0.85) -> finer detail, MORE popping
    //     unscoped     (octree 1.2 / draw 1.2) -> coarser detail, LESS popping
    // Agreement reduced the popping. So agree at the value we WANT, globally.
    //
    // SIGN (got wrong twice, so state it plainly): this scales the MEASURED DISTANCE that LOD
    // selection consumes. LOWER = plants read as CLOSER = finer meshes = more detail and more
    // cost. It does NOT extend range — below 1 makes plants lie about their distance and draw
    // close-up LODs far away, which is what tanked fps at 0.5.
    //
    // GLOBAL: the player's flora LOD changes too, exactly like worldFloraRadiusMult. One
    // setting, two viewers.
    public static double WorldFloraLodMult { get; private set; } = -1;

    // THE MAIN-WORLD LOD CYCLING FIX. On by default: this repairs a bug in the PLAYER'S
    // world that our feed causes, so it is not an enhancement to opt into.
    //
    // While our nested Draw holds our camera in CoreSystems.Settings.RenderView, any engine
    // job calling RenderUtilities.CalculateDistanceToCamera for a PLAYER-side entity measures
    // it against a camera 3906 km away. DistanceTagManagerComponent CACHES that float per
    // entity, and StreamingTag / impostor swap / shadow tracking / raytracing near-far all
    // read only the cache — so one poisoned read demotes an object until something recomputes
    // it. Measured exposure: our swap is installed 12.4% of wall clock.
    //
    // The guard uses [ThreadStatic] _inOurRender to tell our render thread from the job
    // threads, and hands the PLAYER'S camera to everyone who is not us.
    //
    // Keep the knob so the A/B stays possible: this is a long-standing symptom and being able
    // to switch the fix off live is how we prove it was the cause.
    public static bool FixLodCycling { get; private set; } = true;

    // ---- GLOBAL. THE PLAYER'S VIEW AND VRAM CHANGE TOO. -------------------------------
    //
    // Flora.RenderingDistanceMultiplier — the scatter SPAWN RADIUS, the thing behind the
    // reported "strict circular radius around the camera where objects spawn". -1 = leave
    // the engine's value alone.
    //
    // WHY THIS CANNOT BE FEED-ONLY: the value is read in ComputeCullingDistance at BATCH
    // ALLOCATION time and stored on the batch. Batches are allocated as sectors stream in,
    // outside our Draw. So this has to be set globally and left set.
    //
    // TWO CONSEQUENCES WORTH KNOWING BEFORE TURNING IT UP:
    //   1. It is retroactive only for NEW batches. Flora already allocated keeps the radius
    //      it was born with, so raising this changes the world gradually as sectors cycle,
    //      not instantly. Judging it after five seconds will read as "no effect".
    //   2. It multiplies a per-definition base distance, so it scales EVERY flora layer at
    //      once — the dense near ground cover and the sparse far trees together.
    //
    // VRAM: more resident flora is more VRAM, and the world-residency knobs sit outside
    // every automatic guard by the user's explicit choice ("warn loudly, never act"). A CTD
    // on 2026-08-02 came from exactly this class of knob left high after a sweep. The
    // warning at the apply site is the whole safety mechanism; there is no cap.
    public static double WorldFloraRadiusMult { get; private set; } = -1;

    // ---- PER-FEED EYE ADAPTATION (2026-08-02) -----------------------------------------
    //
    // CAMERA-LOCAL AUTO EXPOSURE, closed loop. The feed has been on a FIXED stop for its whole
    // life, which is why remote daylight washes out: a fixed stop cannot be right twice, and
    // the feed's camera sees a different average luminance from the player's.
    //
    // There is already a sun-elevation model here (feedAutoExposure / TryAutoExposureEv) but
    // it is OPEN LOOP — it predicts exposure from time of day and cannot react to the camera
    // sitting in shadow or pointing at bright desert versus dark forest. It also never
    // engages on this build: "Auto aperture: no sun direction ... stays OFF". This replaces
    // the idea rather than tuning it.
    //
    // THE MECHANISM. ExecuteForwardAndPostProcess -> ExecutePostPasses ->
    // SceneDrawSystem.ComputeExposure -> EyeAdaptationJob.DynamicExposure, all INSIDE our
    // nested Draw. EyeAdaptationJob keeps the adaptation history as instance state
    // (RenderTargetTexture[] _autoExposures, RWBuffer _histogram), so a second instance
    // installed for our pass adapts to OUR image and leaves the player's alone. Same shape as
    // the probe manager, the cascade shadows and the draw contexts.
    //
    // WHY THE SHARED ONE COULD NEVER JUST BE ENABLED: it ping-pongs one history. Running it
    // twice a frame against two different average luminances made the PLAYER's lighting
    // oscillate at our render cadence, which is why wholeSceneDisableEyeAdaptation exists.
    // Owning the state is what removes that objection.
    //
    // REQUIRES A BOOTSTRAP RESTART. The instance is parked in RttBridge.ParkedEyeAdaptation
    // because it owns RenderTargetTextures: a logic-owned instance is rebuilt on every hot
    // reload and the orphan's RTV descriptors leak from a small fixed pool. Own-probes CTD'd
    // that exact way ("Out of the descriptor heap at DescriptorHeapPool.BorrowRTV") after
    // four reloads with VRAM flat. If the park is missing the feature refuses to arm and says
    // so, rather than running and leaking.
    public static bool WholeSceneOwnEyeAdaptation { get; private set; }

    // ---- THE RESIDENCY CONE (2026-08-02) ----------------------------------------------
    //
    // Stop making world resident BEHIND the camera. Every other residency mechanism here is
    // omnidirectional — the flora claim and clipmap override are distance tests, preload is a
    // cube — while the camera sees roughly a 70 degree frustum, about 11% of a sphere.
    //
    // MEASURED FIRST, by a counting-only study that culled nothing (commit c54d9bd), so the
    // decision rests on numbers rather than on the argument above being persuasive:
    //
    //     outside a  70 deg cone   98.8%   <- the frustum itself; no margin, unusable
    //     outside a 140 deg cone   77.4%   <- the candidate: a 4.4x reduction
    //     outside a 200 deg cone    8.6%   <- the prize collapses past ~150
    //
    // That collapse between 140 and 200 is the important shape: the sectors sit in a BAND
    // just outside the frustum (a 45 degree downward look wraps the surface around the
    // camera), so this is a sharp knob. 140-150 is the sweet spot; 180+ buys nothing.
    //
    // TOTAL cone angle in degrees, not the half-angle. 0 or >=360 = off, the pre-cone
    // behaviour. Default OFF: this changes what the world loads, and it earns its way in on
    // measurement like everything else here.
    // 0 = DERIVE FROM THE CAMERA'S OWN FOV, which is the default and the intended mode.
    // A positive value forces an absolute cone and is for A/B testing only.
    public static double ResidencyConeDegrees { get; private set; }

    // FORGIVENESS, in degrees, added to the feed's DIAGONAL field of view when deriving.
    //
    // WHY THE DIAGONAL AND NOT THE NOMINAL FOV. For a square frustum the corners sit further
    // off-axis than the FOV number suggests: 75 degrees vertical is ~95 across the diagonal.
    // Sizing the cone to 90 for a 75-degree camera — which reads as generous — would actually
    // cull the frame CORNERS. The margin is therefore added to the diagonal, so the cone
    // always contains the frustum by construction and the knob only controls the slack.
    //
    // 15 degrees total (7.5 a side) covers the two things the frustum itself does not:
    // shadow casters just outside the view, and the ground a rotating orbit is about to turn
    // onto. Raise it if flora visibly materialises at the leading edge as the camera turns.
    //
    // Deriving rather than configuring is the point: the cone tracks whatever sets the FOV,
    // including on-the-fly FOV changes later. A fixed angle would be correct only until the
    // first time the FOV moved, and would then be silently wrong.
    public static double ResidencyConeMarginDegrees { get; private set; } = 15;

    // THE NEAR SHELL, exempt from the cone at any angle. Two things need world that the view
    // direction does not cover, and both are visible immediately if this is too small:
    //
    //   SHADOW CASTERS. The sun cascade pass samples geometry outside the view frustum; a
    //   tree behind the camera can legitimately cast into frame.
    //   ORBIT MOTION. A turning camera sweeps into space that was outside the cone a moment
    //   ago. The 140 degree width already leaves a frustum of margin, and this covers the
    //   rest at close range where pop-in is most obvious.
    //
    // 300 m by default, comfortably outside the 200 m RootStreamingDistance the engine uses
    // for the player's own bubble.
    public static double ResidencyConeNearMetres { get; private set; } = 300;

    // THE STATS PANEL (goal 9 / plan phase A1). Tag a panel [RTS] to get perf numbers on
    // it in world. Draws into the panel's OWN batch — no target, no binding, no handover —
    // so it is independent of the feed and cannot interfere with it.
    public static bool StatsPanel { get; private set; } = true;

    // Repaint period. The panel is re-recorded by marking its content dirty, so this is
    // how often that happens. 500 ms is plenty for numbers and keeps the rebuild cost
    // (and its FSR-mask re-arm) negligible.
    public static int StatsPanelMs { get; private set; } = 500;

    // THE BUDGET CONSTANT (design: budget lock v2). The measured warm cost of ONE render
    // at the current global quality preset — the per-frame slice the whole phase-2
    // scheduler holds constant. The stats panel flags sustained submit above it.
    //
    // Default is today's measured reference (~2.1-2.5 ms at 1024 with probes and flares
    // on). Phase B replaces this with a per-preset table. It is a TRIPWIRE THRESHOLD and a
    // calibration number, never a per-frame gate — enforcement is structural (one render
    // slot per engine frame), because metering with skip-to-repay would manufacture the
    // bimodal frame pattern that this project already identified as the felt choppiness.
    public static double RttBudgetMs { get; private set; } = 2.5;

    // Hold the tagged panel in FSR's REACTIVE mask. Default ON — it fixes a real, long-lived
    // visual bug and the whole change is one float property plus one int field per tick.
    //
    // Without it the player's FSR accumulates temporal history over a surface whose content
    // we replace every frame, because the engine only marks a panel reactive for 5 frames
    // after RebuildSurfaceContent and our feed bypasses that path entirely. The result is
    // the accumulating smear along the stars' apparent motion, worse the further the player
    // stands back and briefly cleared by player movement. See PanelBinding.ApplyFsrMask.
    //
    // Set to 0 to get the old behaviour back — worth having as an A/B, and worth turning off
    // if the player's AA is not FSR, since the reactive mask then buys nothing while still
    // touching the shared LCD material.
    public static bool PanelFsrMask { get; private set; } = true;

    // Rebuild the panel target's mips 1..N from our frame after the handover copy.
    //
    // CopyTextureSubresource writes one subresource, so without this only mip 0 is ours and
    // the lower levels keep whatever DrawOne left on a recycled pool texture — making the
    // feed correct up close and progressively wrong as the player backs away. Default ON:
    // it uses the engine's own MipMapJob instance, creates nothing, and fails soft to the
    // previous behaviour if any member is missing. Needs the bootstrap that appends
    // __instance to the offscreen-UI hook args, so it is inert until the game is restarted.
    // See FeedHandover.RegenerateMips.
    public static bool PanelMipRegen { get; private set; } = true;

    // PER-SURFACE TICK CENSUS (task #26). Records, for every TAGGED surface, when it last
    // ticked and whether that tick passed the powered check — the one measurement that
    // separates "feed N's heartbeat is mis-keyed" from "the engine really stopped ticking
    // that surface". Diagnostic only: it observes, it changes nothing. Silent until at least
    // two tagged surfaces exist, so a single-panel session never sees it.
    // See PanelTickCensus and the 2026-08-06 black-panel repro in task #26.
    public static bool PanelTickCensus { get; private set; } = true;
    public static int PanelTickCensusMs { get; private set; } = 5000;

    // How long the session (and the panel scene, after any stall) must have been running
    // before a DORMANT feed may go ACTIVE. The world-load RenderThreadFreeze fix: our
    // activation injected markers, preloads and render requests into the engine's first
    // texture-prioritizer walk ~200 ms after load-complete. Deactivation is never delayed.
    // 0 disables the grace.
    public static int ActivationGraceMs { get; private set; } = 4000;

    // How long after world-up (and after any sim stall) before WORLD-SIDE residency work may
    // start: preloads, the server presence entity, the camera trigger marker. All three drive
    // server-side planet-sector materialisation, which is where the engine's world-load NRE
    // family lives (Scene.GetDataPointer null under AsyncInitPlanet). Not our bug — it
    // reproduced with the plugin disabled — but that control was one sample, and none of this
    // work is worth anything during a loading screen. 0 disables the gate.
    public static int ResidencySettleMs { get; private set; } = 30000;

    // How long a tagged panel may go without ticking before the mod shuts itself down.
    //
    // The LCD render component ticks every panel it draws, so a panel switched off,
    // unpowered or destroyed simply stops arriving — the absence of the signal is the
    // signal. Long enough to survive a stall or a streaming hitch, short enough that
    // toggling the panel is a usable A/B against vanilla.
    public static int PanelIdleMs { get; private set; } = 1500;

    // How often Perf emits its frame-interval report. 0 disables the instrument.
    public static int PerfReportMs { get; private set; } = 5000;

    // Cascade resolution and count for OUR shadow set. 0 = use the player's settings.
    //
    // Our set is sized from the player's graphics options, which are chosen for a 4K
    // screen. At 4096px x 8 that is half a gigabyte of depth textures and eight full
    // geometry passes per second render — to shade a 512x512 panel. Scoped during our
    // render only, and our CascadeShadowsContext resizes itself to match on its next
    // flush, so the engine's own set is untouched.
    public static int WholeSceneCascadeResolution { get; private set; } = 1024;

    public static int WholeSceneCascadeCount { get; private set; } = 3;

    // Character shadow map size for OUR render only. 0 = leave the player's value alone.
    //
    // Found by the resource report: two 2048x2048 depth sets — first-person and third-person
    // — were 32 MiB of a 444 MiB feed, allocated because CharacterShadowsContext sizes itself
    // from ShadowSettings.DirectionalLight.CharacterShadowResolution and nothing scoped it.
    // The feed is an orbital camera; the player's own character is not in the shot, and the
    // first-person set is meaningless to it by definition.
    //
    // NOT zero-able: the context allocates whatever it is told, and a zero-size depth texture
    // is a device removal waiting to happen. 256 keeps the machinery valid and costs 0.5 MiB
    // instead of 16 — if a character ever IS in a feed's shot, their shadow is simply coarse.
    public static int WholeSceneCharacterShadowResolution { get; private set; } = 0;

    // HOW MANY INDEPENDENT FEEDS ARE ACTIVE (plan phase C3). 1 = the shipped behaviour.
    //
    // IN the rebuild signature, and the first attempt had this exactly backwards.
    //
    // The original reasoning was "adding a feed must not drag the OTHERS through a rebuild
    // and a 30-frame settle". That sounds right and is wrong, because changing this value
    // changes WHICH PANEL EACH FEED OWNS. Every feed caches its panel resolution — the
    // resolved panel id, the offscreen target, the handle text the handover matches on, the
    // render target itself — and re-routing underneath those caches leaves each feed holding
    // another panel's identity. Observed 2026-07-30 on the first two-feed run: routing was
    // correct in the log, both panels claimed the right feeds, and the picture froze, because
    // feed 0 was still matching the handle of the panel that had just been reassigned to
    // feed 1. drawOne(ours) went to 0.0 and copies stopped, with no error anywhere.
    //
    // A rebuild is exactly the thing that clears those caches, so this belongs in the
    // signature. The cost is one gate cycle when you change the feed COUNT, which is a rare,
    // deliberate act — not the per-frame cadence tuning A2 freed from the signature.
    //
    // Feeds.Count clamps this into the slots that exist, so a typo degrades to a valid count
    // instead of an index fault.
    //
    // A second feed needs a second tagged panel to point at. With feedCount=2 and only one
    // tagged panel, feed 1 simply never finds a target and stays dormant — which is the
    // graceful-cut contract (goal 7) doing its job, not a failure.
    public static int FeedCount { get; private set; } = 1;

    // ---- the VRAM admission cap (phase E1) ---------------------------------------
    //
    // feedCount is what the user ASKS for; these three decide what they GET. See
    // Feeds.UpdateResidentCap for the arithmetic and the crash that motivated it.

    // Hard user ceiling, independent of memory. Set it to 1 to pin single-feed behaviour
    // regardless of how much VRAM is free.
    public static int MaxResidentFeeds { get; private set; } = 4;

    // Headroom held back from the admission arithmetic. 512 MB because VRAM was measured
    // swinging +/-200 MB frame to frame during the D3 sweeps, and because the device
    // removal that motivated this happened only ~90 MB over budget — the margin between
    // "tight" and "dead" is smaller than the noise, so the reserve has to clear both.
    public static int FeedVramReserveMb { get; private set; } = 512;

    // Off = trust maxResidentFeeds alone and never consult VRAM. Kept as an escape hatch:
    // if the cap ever misjudges and refuses a feed that would have been fine, the user can
    // switch the automatic half off without losing the manual ceiling.
    public static bool FeedVramGuard { get; private set; } = true;

    // ---- the per-feed off switch (phase F4) --------------------------------------
    //
    // `feedsDisabled = 2` or `feedsDisabled = 1,3` — ONE-BASED, matching the [RTCn] tags on
    // the panels rather than the internal ids, because the number the user types on a block
    // is the number they should be able to type here.
    //
    // WHY IT IS NOT feedCount. Lowering feedCount RE-ROUTES which panel each feed owns, so
    // it has to drag every feed through a quiesced rebuild (see FeedCount's comment). This
    // knob changes nothing about routing: feed n stays feed n, it simply stops being alive.
    // That makes it the only lever that reproduces what actually happens in game when a
    // panel is destroyed or switched off — one feed leaves, the others carry on — and it
    // does it repeatably, from outside the game, without grinding a block down.
    //
    // It is also the seam the connection framework will use later: "this feed has lost its
    // link" and "this feed is switched off" both want precisely this graceful stop.
    //
    // Stored as a MASK so the hot path (FeedGate.PollFeed, every feed every frame) is a bit
    // test rather than a string parse.
    private static int _disabledMask;

    public static bool IsFeedDisabled(int feedId) =>
        feedId >= 0 && feedId < 32 && (_disabledMask & (1 << feedId)) != 0;

    // READ feedCount BEFORE THE FIRST PANEL TICK, and nothing else.
    //
    // FeedCount defaults to 1 and only becomes the configured value on the first Poll, which
    // is a couple of seconds into a load. Every tagged panel that ticks inside that window
    // sees Feeds.Count == 1, and at one feed the router sends EVERY [RTCn] to feed 0 — by
    // design, that is what "asked for a feed that is not active" means. So on every load,
    // briefly, feed 1's panel is claimed and bound by feed 0. Feed 0's next teardown then
    // restores that panel to its stock material, taking away the binding feed 1 had correctly
    // made, and the screen shows stale or torn content. Observed 2026-08-01 as a corrupted
    // static image on the second feed's panel, and earlier the same day as the second panel
    // "mirroring" the first.
    //
    // Deliberately NOT the whole of Poll. Poll calls Feeds.UpdateResidentCap, which samples
    // VRAM through engine types, and this runs during plugin install — the exact situation
    // that once threw ConfigurationNotFoundException and permanently poisoned a type (see the
    // note in Feeds). This touches a file and an int, nothing else.
    internal static void PrimeFeedCount()
    {
        try
        {
            var kv = Read();
            FeedCount = Int(kv, "feedCount", FeedCount);
            ReadDisabledFeeds(kv);
            RttLog.Global($"Config primed at install: feedCount={FeedCount}. Read before the first " +
                          "panel tick so routing is correct from the first one — at the default of 1, " +
                          "every tagged panel would briefly claim feed 0.");
        }
        catch { }   // the full Poll is moments away and will report anything real
    }

    private static void ReadDisabledFeeds(Dictionary<string, string> kv)
    {
        int mask = 0;
        foreach (int oneBased in Ints(kv, "feedsDisabled", System.Array.Empty<int>()))
        {
            int id = oneBased - 1;
            if (id >= 0 && id < 32) mask |= 1 << id;
        }
        if (mask == _disabledMask) return;

        int was = _disabledMask;
        _disabledMask = mask;

        // LOUD, in both directions. A silently disabled feed is a black panel with every
        // counter reading healthy, which is the single most expensive failure shape this
        // project has produced — so the lever that can cause it announces itself.
        RttLog.Global($"Config: feedsDisabled {Describe(was)} -> {Describe(mask)}. " +
                      "A disabled feed goes DORMANT by the ordinary path — gate off, teardown 30 " +
                      "frames later, resources released — and the remaining feeds absorb its share " +
                      "of the render slot. No rebuild, and nothing else is disturbed.");
    }

    private static string Describe(int mask)
    {
        if (mask == 0) return "(none)";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 32; i++)
            if ((mask & (1 << i)) != 0) { if (sb.Length > 0) sb.Append(','); sb.Append(i + 1); }
        return sb.ToString();
    }

    // Last EFFECTIVE feed count seen by Poll, for detecting the re-route that has to take the
    // dormant path. Starts at -1 so the first poll can never read as a change (the first poll
    // rebuilds everything anyway, from a gate that has not started).
    private static int _lastCount = -1;
    private static int _lastCountLogged = 1;

    // Render our OWN sun-shadow cascades, around OUR camera.
    //
    // 0 = off: our DrawContextManager borrows the ENGINE'S DirectionalLightShadowResources
    //     read-only and stage 3 stays skipped. The feed samples cascades fitted around the
    //     PLAYER, so shadows degrade with camera distance and whole objects read as fully
    //     shadowed once the camera leaves that volume — the "ship goes dark at some orbit
    //     points" report.
    // 1 = own: keep our manager's own resources, flush OUR CascadeShadowsContext against
    //     OUR installed view, and run stage 3. Engine-adaptive update policy
    //     (CascadesUpdateCount per render, priority-sorted, forced when the camera shifts).
    // 2 = own + force every cascade every render. Costs more; removes any dependence on
    //     an update policy tuned for 60fps continuous motion rather than a 10fps orbit.
    //
    // The cascade textures already exist either way: CascadeShadowsContext's ctor calls
    // CheckShadowSettingChanged, which sees _cascades.Length == 0 != CascadesCount and
    // allocates the full set. We have been paying that VRAM since the second manager was
    // built and rendering nothing into it.
    public static int WholeSceneOwnShadows { get; private set; }

    // Show OUR render on the panel instead of the probe feed.
    //
    // The probe pass keeps running (it drives the orbit transform); it just stops
    // parking frames while this is on, and the whole-scene render parks its
    // FinalLDRTexture instead. Flipping this off restores the probe feed within one
    // panel tick — instant A/B comparison between the two pipelines.
    public static bool WholeSceneToPanel { get; private set; }

    // Minimum gap between second renders. Draw is a WHOLE FRAME: ungated at 53 fps this
    // would roughly halve the game's frame rate before teaching us anything, and a fault
    // would repeat 53 times a second while the log is being read.
    public static int WholeSceneIntervalMs { get; private set; } = 200;

    // Explicit resource barriers around the handover copy, one switch per end.
    //
    // Adding the destination barrier killed the game on the first copy, ~0.5 s in —
    // so the engine's AutoResourceState tracker evidently transitions the CopyResource
    // destination itself, and forcing CopyDest on top of that desynchronises it. The
    // source barrier has run for hundreds of copies without incident. Default off /
    // on respectively, and switchable so the pair can be bisected in one session
    // instead of one launch per hypothesis.
    public static bool SrcTransition { get; private set; } = true;

    public static bool DestTransition { get; private set; }

    // Replace the test pattern's persistent batch with an empty one once the feed
    // takes over, so DrawOne has nothing to draw before our copy lands.
    public static bool RetireTestPattern { get; private set; } = true;

    // The CopyResource itself. Off by default while the copy is under investigation:
    // three launches died on copy #1 into a target we created, where hundreds of
    // copies into the LCD system's own target ran clean. With this off the handover
    // describes both ends into copy-diag.txt and copies nothing, so the game survives
    // and the file can be read with the session still live. Set to 1 to re-enable.
    public static bool CopyEnabled { get; private set; }

    // Run the camera pass from the per-frame hook (DrawUnlit) instead of the probe
    // hook. The probe hook is inside the engine's own environment-probe work, which
    // is where borrowing the main view's post passes corrupted the player's render.
    // Off by default: the probe hook is the one with hours of proven runtime.
    public static bool PassOnFrameHook { get; private set; }

    // Swap SettingsManager._renderView to OUR camera for the pass. GBufferPassJob and
    // ExecuteLighting take no camera parameter, so without this they rasterise and light
    // from the player's viewpoint — the feed showed the inside of the ship.
    public static bool SwapCamera { get; private set; } = true;

    // The panel binds our feed as ColorMetalTexture: RGB is colour, ALPHA IS METALNESS.
    // Our HDR source has no alpha channel, so blitting all four channels wrote ~1.0
    // into metalness — a fully metallic panel, which has almost no diffuse response and
    // flattens the range regardless of exposure. Default: RGB only, slot pre-cleared so
    // alpha reads 0 (non-metal). Flip blitAlpha to 1 to reproduce the old behaviour.
    public static bool BlitAlpha { get; private set; }

    // 2. Stamp OUR render resolution into the camera CB's Screen.Resolution.
    //    CameraSettings -> TrackedCameraSettings copies ScreenBuffers.PreUpscaleResolution
    //    (the player's 3840x2160) while we rasterise 512x512, and shaders reconstruct view
    //    rays with rcp(Screen_.Resolution) — a 7.5x error on every one of them. That is
    //    the over-zoomed sky, and also wrong view vectors and specular in the geometry pass.
    public static bool FixScreenRes { get; private set; } = true;

    // 3. Build the camera CB from a real RenderView via
    //    CameraSettings.CreateNonjitteredCameraSettings instead of the RenderViewSlim
    //    conversion, which leaves ViewTransform, InvViewTransform, TanFOV, FOVScaleFactor
    //    and CameraFlags at zero — so shaders believe the camera is at the world origin —
    //    and stamps the PLAYER's position into MainViewCameraPos, which is what planet
    //    curvature and triplanar (voxel) texturing read.
    public static bool FullCameraCb { get; private set; } = true;

    // 4. LCD material EmissivityMultiplier. 0 leaves the base material's value alone
    //    (10 on LCDScreen_On). The panel's emissive term is added to the MAIN view's HDR
    //    buffer before its bloom and tonemap, so this is the display-side gain — the one
    //    axis that can put the feed above white. Live-tunable: change it and the material
    //    is updated in place, so a sweep costs a file save.
    //
    //    DEFAULT IS STOCK (10), and deliberately. It was 500 — a 50x gain from when the
    //    feed was dim and needed rescuing at the display end — and because these statics
    //    live in the collectible load context, EVERY hot reload reset it to 500 and
    //    ApplyEmissivity ran with that value before the first Poll(). So a config of 10
    //    still produced a 50x floodlight for the first frames after every reload. A
    //    default that overrides the config file until the config is read is a bug, not a
    //    convenience: this is a SHARED LCDMaterialDefinition, so it applies to every LCD
    //    panel in the world, and the panel's emissive term lands in the MAIN view's HDR
    //    buffer before bloom and tonemap.
    public static double Emissivity { get; private set; } = 10.0;

    // 5b. EnvironmentProbeSettings.DimDistance. Pass_Pixel_Indirect.hlsli multiplies all
    //     shaded output by clamp(ZDepth/DimDistance, 0, 1) SQUARED, and it ships as 5 m —
    //     so geometry 1 m from the camera is multiplied by 0.04. It exists so a probe does
    //     not contaminate itself with the hull it sits inside.
    //
    //     Unlike 5a this canNOT be a scoped swap: DimDistance reaches the shader through
    //     CommonResources.SettingsGroup.CreateFrameSettings, which builds the frame's
    //     GlobalSettings CB once in OnBeginDraw — long before our hook runs. It has to be
    //     set persistently, which also affects the engine's OWN probes (slightly brighter
    //     ambient for the player). -1 leaves it alone.
    //
    //     Note this does nothing at the default 100 m orbit — everything is already past
    //     5 m. It matters for a camera mounted close to structure.
    public static double DimDistance { get; private set; } = -1.0;

    // Orbit radius is a FLOOR, not a fixed distance: the effective radius is
    // max(orbitRadius, gridExtent * orbitClearance). Orbiting a fixed 100 m around a
    // ship whose half-diagonal is 80 m flies the camera through the hull, which is
    // what the feed was showing.
    public static double OrbitClearance { get; private set; } = 2.2;

    // Orbit the grid's centre (default) or the tagged panel itself. Panel-centred is
    // the close-up shot; grid-centred is the one that looks like a drone camera.
    public static bool OrbitGrid { get; private set; } = true;

    // ORBIT SOMEWHERE ELSE (phase J, 2026-08-01). Empty = today's behaviour, orbit the
    // panel's OWN grid.
    //
    // Set it to part of another grid's display name (case-insensitive substring) and the
    // orbit centre moves to that grid instead. This is the instrument goal 10 has been
    // blocked on: every render this project has ever done was ~100 m from the player, so
    // "nothing ever disappeared" was never evidence of anything. Point the camera at a grid
    // the player is NOWHERE near and the streaming/materialization question answers itself.
    //
    // Run `worldGridSurvey = 1` first — it writes output/world-grids.txt with every grid's
    // name, id, position and distance, which is where the string for this knob comes from.
    //
    // DELIBERATELY OUTSIDE THE WHOLE-SCENE REBUILD SIGNATURE: moving the orbit centre
    // allocates nothing and resizes nothing, so it is safe to edit on a running feed, the
    // same way orbitRadius and orbitPeriod are. Keep it that way — a signature knob edited
    // live is what removed the device at 15:08 on 2026-08-01.
    public static string OrbitAnchor { get; private set; } = "";

    // One-shot world inventory. Self-clearing: the dump runs once and the flag is forgotten
    // until the file is edited again, so leaving `= 1` in the file does not re-dump every
    // poll. Writes output/world-grids.txt.
    public static bool WorldGridSurvey { get; private set; }
    private static bool _surveyArmedLast;

    // One-shot spatial trigger census — every EntityTrigger in every reachable scene, with
    // tag constraints and current occupants. The instrument for the client/server trigger
    // split (ClientTriggerTag vs DynamicTag). Same edge discipline as worldGridSurvey;
    // read-only, so an accidental re-fire costs a report, not a world. Writes
    // output/trigger-census.txt.
    public static bool TriggerCensus { get; private set; }
    private static bool _censusArmedLast;

    public static bool TakeTriggerCensusRequest()
    {
        var r = TriggerCensus;
        TriggerCensus = false;
        return r;
    }

    // One-shot inventory of every VoxelRenderComponent in the clipmap update list — the
    // instrument for "where does near-mode terrain exist". Same edge discipline; read-only.
    // Writes output/voxel-bodies.txt.
    public static bool VoxelBodySurvey { get; private set; }
    private static bool _bodySurveyArmedLast;

    // One-shot SERVER-scene entity census near the camera, walked on the sim-pump seat —
    // the spawned-vs-never-spawned fork for the flora chain. Writes output/server-flora.txt.
    public static bool ServerFloraSurvey { get; private set; }
    private static bool _floraSurveyArmedLast;

    public static bool TakeServerFloraSurveyRequest()
    {
        var r = ServerFloraSurvey;
        ServerFloraSurvey = false;
        return r;
    }

    public static bool TakeVoxelBodySurveyRequest()
    {
        var r = VoxelBodySurvey;
        VoxelBodySurvey = false;
        return r;
    }

    // PRELOAD AROUND THE FEED CAMERA (goal 10 tier 1). Off by default: it asks the engine
    // to make world data resident somewhere no player is, which costs memory, and the VRAM
    // cap is already the binding constraint on this route.
    //
    // All three are OUTSIDE the whole-scene rebuild signature — they allocate nothing of
    // ours and resize nothing, so they are safe to edit on a running feed.
    // PER-BODY CLIPMAP CAMERA — the terrain fix. Off by default until proven in game.
    //
    // ClipmapMinPlayerDistance is the safety floor: a body the player is closer than this to
    // is NEVER taken over, whatever the camera is doing. 100 km comfortably exceeds a planet
    // radius (Verdure is 60 km), so "the player is on this planet" can never satisfy it.
    // Add ClientTriggerTag to the anchor GRID so the planet materializes environment sectors
    // around it. Requires orbitAnchor to name a grid (coordinates have no entity to tag).
    // Off by default: it mutates a world entity.
    public static bool TagAnchorForClutter { get; private set; }

    // THE CAMERA TRIGGER ENTITY — the same job as tagAnchorForClutter, done the way the
    // ENGINE does it rather than by mutating one of the player's grids.
    //
    // VoxelObserverSessionComponent.OnAddedToScene spawns exactly this and nothing more:
    //     Scene.AddEntity<ClientTriggerTag, WorldTransform, BoundingBoxData>(
    //         default, RenderSettings.CameraTransform, BoundingBox(-1, 1))
    // an invisible 2 m marker that IS the engine's voxel streaming observer, moved every
    // tick to wherever the viewer is. We build the same thing and move it to the feed
    // camera. Strictly better than tagging a grid: it touches nothing the player owns, it
    // works at bare coordinates where there is no entity to tag, it will work unchanged
    // when the camera is a block on a moving ship, and it deletes cleanly.
    //
    // Off by default, and mutually exclusive in practice with tagAnchorForClutter — run one
    // or the other so a result attributes to something. The A/B that grades this is:
    // clutter appeared with the tag in ~2 min, so marker ON + tag OFF must reproduce it.
    public static bool CameraTriggerEntity { get; private set; }

    // Add DynamicTag to the CLIENT marker. The census decoded the client voxel-sector
    // triggers (Voxel : Block / Voxel : Prediction) as must:DynamicTag+WorldTransform+
    // BoundingBoxData — ClientTriggerTag does not move voxel data; DynamicTag does.
    // Applied at marker CREATION only: toggle cameraTriggerEntity off/on to change it live.
    public static bool CameraTriggerDynamicTag { get; private set; } = true;

    // HALF-EXTENT of both presence markers, metres. THE RADIUS OF THE MATERIALIZATION BUBBLE.
    //
    // The PlanetEnvironment triggers are SECTORED: they activate the sectors an entity's
    // bounding box overlaps. 1.375 (the engine's own voxel-observer size) overlaps about one
    // sector, which is why scatter objects stop at a hard circle around the anchor and the
    // ground beyond is bare — spotted from the raised orbit camera on 2026-08-02.
    //
    // Sectors are ref-counted, so a big bubble overlapping a player's is safe by design and
    // cannot double-spawn. The cost is RESIDENT WORLD, roughly with the cube of this — watch
    // VRAM, since the feed cap is already the binding constraint.
    //
    // Applied at marker CREATION: toggle cameraTriggerEntity/serverPresenceEntity off and on
    // to rebuild at a new size.
    public static double CameraTriggerExtent { get; private set; } = 1.375;

    // THE SERVER HALF. A DynamicTag+WorldTransform+BoundingBoxData entity in the SERVER
    // scene, created and moved exclusively on that scene's own pump (the bootstrap's
    // sim-pump seat) — never from our threads. This is the archetype every censused
    // server trigger tests: flora sectors, managed world areas, encounters' spatial layer.
    // Off by default: it is a world mutation in the scene that spawns things.
    public static bool ServerPresenceEntity { get; private set; }

    // PER-SECTOR FLORA CAMERA — the client-visibility half. Flora sectors are culled by
    // distance from ONE global render camera, so a remote feed sees none of the flora the
    // server generates around it. Same rule as the clipmap override (2x margin + the
    // ClipmapMinPlayerDistance floor), so the player's flora can never regress.
    public static bool FloraCameraOverride { get; private set; }

    // THE NEAREST-VIEWER DISTANCE. Every distance-driven tag in the render scene — resource
    // streaming, the impostor swap, shadow tracking, raytracing near/far — reads ONE cached
    // float per entity, and that float is the distance to the single global camera. This makes
    // it the distance to the nearest of {player, feed camera} instead. See ViewerDistance.cs.
    //
    // Off by default because it widens what the engine holds resident. It cannot degrade the
    // player's view under any failure: the override is a min(), so it only ever lowers a
    // distance, and the engine's own answer stands whenever ours is not better.
    public static bool ViewerDistanceOverride { get; private set; }

    // The bubble's half-extent, metres. 200 is RootResourceStreamingComponent
    // .RootStreamingDistance exactly — the feed camera is granted the same streaming bubble the
    // player already carries, and no more, which is fidelity parity rather than a new budget.
    // Raising it costs resident memory roughly with the cube; it also does nothing at all past
    // the largest threshold in DistanceThresholdContainer.FullRefresh, since beyond that every
    // entity is in the same last bucket whichever viewer measured it.
    public static double ViewerDistanceRadius { get; private set; } = 200.0;

    // ---- THE DISTANCE CURVE (task #42): decouple range from LOD selection ----------------
    // Inside Full the feed reports true distances (full fidelity). Between Full and Radius
    // the reported distance is inflated by a smoothstep gain rising to FarBias, so the
    // engine's own tier ladders step content down BEFORE the bubble boundary instead of
    // popping at it — and mid-range content gets CHEAPER, not richer. Full <= 0 means
    // "= Radius" and FarBias 1.0 is the curve off: both defaults reproduce the pre-curve
    // behaviour bit for bit. FarBias is the perf lever for the optimisation phase: raising
    // it degrades the 'Full..Radius' band earlier and harder with no change to reach.
    public static double ViewerDistanceFull { get; private set; }          // 0 = no curve zone
    public static double ViewerDistanceFarBias { get; private set; } = 1.0;

    // Print the live grass gate from INSIDE our nested pass, every 15 s. Level-triggered, not
    // edge-triggered, because unlike the survey dumps this is a read of volatile state that is
    // only meaningful while our contexts are installed — there is nothing to consume once.
    public static bool GrassProbe { get; private set; }

    // Re-bind the panel when our screen material is found to have been replaced.
    //
    // ON by default because the failure it fixes is total — the feed's picture is gone until
    // a park cycle — and the detection is exact rather than heuristic (we compare the runtime
    // material handle the engine created for us against what is on the panel now). It is a
    // knob at all because the re-bind goes through SetNewScreenMaterialHandle, the call
    // implicated in the [RTS] mirror leak, so there has to be a way to switch it off without
    // a rebuild if it ever destabilises. A blind reader never re-binds regardless.
    public static bool PanelRebindOnLoss { get; private set; } = true;

    // WHAT WE TELL THE ENGINE THE SCREEN'S ASPECT IS, at bind time (task #26, the multi-panel
    // console block). The feed always renders 1024x1024 SQUARE; the user wants it projected
    // with NO distortion — uniform scale until the panel's long axis is filled, the short
    // axis overrunning off the display ("cover"). The bind's aspectRatio parameter lands in
    // LCDMaterialDefinition.ScreenAspectRatio (read in IL), which is shader-visible — but its
    // per-pixel semantics for an OVERRIDE texture are untested, so this is a knob, not a
    // hard-coded guess:
    //   -1  = pass the panel's own Definition.AspectRatio (today's behaviour)
    //    0  = pass 1.0 — "the content is square"; the shader then crops, letterboxes or
    //         ignores it, and the log + the eye decide which
    //   >0  = pass exactly this value (the escape hatch while mapping the semantics)
    // Every bind logs the definition aspect AND what was passed, so each panel on the block
    // becomes one data point. Changing this re-binds on the next park cycle — no restart.
    public static double PanelBindAspect { get; private set; } = -1.0;

    // Fill the panel edge-to-edge with no bars and no distortion, by rendering the square
    // target ANAMORPHIC (projection widened to the panel's aspect) so the panel's own stretch
    // undoes the squeeze. See the anamorphic-fit note in CameraRender.
    // OFF gives the previous square projection, which is the control for judging it.
    // REJECTED BY DESIGN (2026-08-04): rendering the projection wider than the target made the
    // panel's own stretch cancel out, but it changes WHAT IS CAPTURED — a squeezed wide-FOV
    // image, not a true 1:1 view. The render portal must always capture a genuine square; all
    // fitting belongs at DISPLAY time. Kept only as a comparison control.
    public static bool PanelAspectFit { get; private set; }

    // Bind each feed panel to a PRIVATE clone of the screen material instead of the shared
    // one. Prerequisite for ANY per-panel display fitting: the engine borrows runtime LCD
    // materials from a store keyed on {MaterialDefinition, AspectRatio, Orientation}, so with
    // a shared definition every framing change reaches other panels — and our texture override
    // is visible to them (the [RTS] mirror, task #31).
    //
    // DEFAULT OFF, deliberately. This touches the material path that sits behind several of
    // this project's device removals, and three "this is safe now" claims were wrong on
    // 2026-08-04 alone. It gets switched on as a deliberate test, not as a silent default.
    public static bool PanelPrivateMaterial { get; private set; }

    // WHEN THE MANUAL CAMERA IS ALLOWED TO LISTEN TO INPUT.
    //
    // Names are engine INPUT LAYERS, published by the bootstrap from
    // GameInputProcessorComponent.ActiveContexts and listed in the game's own layer table
    // (GameInputLayers.def): "Ship Movement", "Character Movement", "Camera FreeLook",
    // "Camera Controller", "Hot Keys", "UI", and so on.
    //
    // These are STRINGS IN CONFIG rather than constants in code for one specific reason: the
    // seat in question sits on a STATIC grid that the game does not treat as a vehicle, so
    // the layer that actually means "seated" here is an empirical question. The bootstrap
    // logs "INPUT LAYERS -> [...]" on every change; read it while sitting down and standing
    // up, then put the right name here — no rebuild.
    //
    // Empty require-list = require nothing (controls always on, subject to the block list).
    public static string[] CameraRequireLayers { get; private set; } = { "Ship Movement" };
    public static string[] CameraBlockLayers { get; private set; } = Array.Empty<string>();

    // FREELOOK IS NOT A LAYER — measured, not assumed (2026-08-04).
    //
    // The engine's layer table declares "Camera FreeLook", and the obvious implementation was
    // to block on it. In game it NEVER activates. Searching the game's own definition data
    // says why: that layer's GUID appears in GameInputLayers.def and NOWHERE ELSE — no input
    // context is assigned to it, so nothing can ever raise it. The cockpit look context
    // ("Camera First Person") sits on the "Camera Controller" layer, which is active the whole
    // time you are seated, and its only actions are MouseDelta and ResetFreeLook. There is no
    // layer that means "freelook is being held".
    //
    // So block on the KEY instead. That reuses the one input path already proven to work — the
    // bootstrap's held-key capture — instead of a second piece of engine archaeology. Windows
    // VIRTUAL-KEY codes, the same encoding CameraControl's movement keys use (learned the hard
    // way: Enum.GetName on the UI key enum returned plausible WRONG names for three codes).
    //
    // Default 18 = VK_MENU (Alt), the usual freelook modifier. If yours is bound elsewhere,
    // put its VK code here — several may be listed. Blank disables key blocking entirely.
    public static int[] CameraBlockKeys { get; private set; } = { 18 };

    public static bool PerBodyClipmapCamera { get; private set; }
    public static double ClipmapMinPlayerDistance { get; private set; } = 100000.0;

    // And the camera must be WITHIN this of the body. "Closer than the player" alone let a
    // rock 561 km from the camera qualify while the player was 3,900 km away — re-centring
    // clipmaps nobody is looking at. 80 km covers a planet the camera is orbiting (Verdure's
    // radius is 60 km) without reaching neighbours.
    public static double ClipmapMaxFeedDistance { get; private set; } = 80000.0;

    // Per-frame budget for ALL clipmap updates. The engine ships 0.5 ms. 0 = leave alone.
    // GLOBAL: it cannot be scoped to the feed, so it spends the player's frame time too.
    public static double ClipmapUpdateBudgetMs { get; private set; }

    // Metered loadingPhase pulses for planet-scale bodies after the camera jumps — the
    // spawn-speed meshing path, fed in sips (500 ms on / 2500 ms off, 60 s window, VRAM
    // abort). The un-metered version device-removed the GPU on 2026-08-02 00:28; see
    // WorldGrids.ArrivalBurst before widening any of its bounds.
    public static bool ClipmapArrivalBurst { get; private set; }

    public static bool PreloadAroundCamera { get; private set; }

    // Preload through the SERVER session's space probe — the side that generates flora
    // sectors. Shares preloadRadius/IntervalMs/Precision with the client knob.
    public static bool ServerPreload { get; private set; }
    public static double PreloadRadius { get; private set; } = 500.0;
    public static int PreloadIntervalMs { get; private set; } = 5000;
    public static string PreloadPrecision { get; private set; } = "Medium";   // Low | Medium | High

    // MATERIALIZE ONE MANAGED AREA BY NAME (goal 10 / tier 2 pathfinder, 2026-08-01).
    // `loadArea = Vallis Reach` calls TryLoad() on the matching ManagedWorldArea — the
    // engine's own load path, the one its spatial trigger fires — so the content spawns
    // exactly as if a player had walked in. Edge-triggered and consumed once, same
    // discipline as worldGridSurvey: a name left in the file must not re-fire on every
    // poll, because TryLoad is a WORLD MUTATION, not a report.
    public static string LoadAreaRequest { get; private set; } = "";
    private static string _loadAreaLast = "";

    public static string TakeLoadAreaRequest()
    {
        var r = LoadAreaRequest;
        LoadAreaRequest = "";
        return r;
    }

    private static string _loadAreaMarkerPath;
    private static string LoadAreaMarkerPath =>
        _loadAreaMarkerPath ??= System.IO.Path.Combine(RttLog.OutDir, "loadarea-consumed.marker");

    private static string ReadLoadAreaMarker()
    {
        try { return FileWatch.Exists(LoadAreaMarkerPath)
                  ? System.IO.File.ReadAllText(LoadAreaMarkerPath).Trim() : ""; }
        catch { return ""; }
    }

    private static void WriteLoadAreaMarker(string value)
    {
        try { System.IO.File.WriteAllText(LoadAreaMarkerPath, value); FileWatch.Invalidate(LoadAreaMarkerPath); }
        catch { /* an unwritable marker means a re-fire after restart; log-worthy but not fatal */ }
    }

    // Consumed-and-cleared by the surveyor, so the "run once" decision lives in ONE place
    // rather than being re-derived by every caller. Returns true at most once per edit.
    public static bool TakeWorldGridSurveyRequest()
    {
        if (!WorldGridSurvey) return false;
        WorldGridSurvey = false;
        return true;
    }

    public static double OrbitRadius { get; private set; } = 100.0;

    public static double OrbitPeriod { get; private set; } = 30.0;

    public static double OrbitHeight { get; private set; } = 15.0;


    // ---------------------------------------------------------------- polling

    public static void Poll()
    {
        var now = System.Environment.TickCount64;
        if (now - _lastRead < 2000) return;
        _lastRead = now;

        try
        {
            if (!FileWatch.Exists(Path_))
            {
                // Write the defaults out once so the knobs are discoverable rather than
                // something you have to read the source to find.
                File.WriteAllText(Path_,
                    "# RTT camera feed — edit and save; picked up within ~2s.\n" +
                    $"intervalMs   = {IntervalMs}\n" +
                    $"orbitRadius  = {OrbitRadius}\n" +
                    $"orbitPeriod  = {OrbitPeriod}\n" +
                    $"orbitHeight  = {OrbitHeight}\n");
                return;
            }

            var stamp = FileWatch.StampTicks(Path_);
            if (stamp == _lastStamp) return;
            _lastStamp = stamp;

            var kv = Read();
            if (kv.Count == 0) return;      // unreadable or empty: keep the last good values

            IntervalMs     = Int(kv, "intervalMs", IntervalMs);
            PanelMs        = Int(kv, "panelMs", PanelMs);
            StartupDelayMs = Int(kv, "startupDelayMs", StartupDelayMs);
            PanelIdleMs    = Int(kv, "panelIdleMs", PanelIdleMs);
            PerfReportMs   = Int(kv, "perfReportMs", PerfReportMs);

            OrbitRadius    = Dbl(kv, "orbitRadius", OrbitRadius);
            OrbitPeriod    = Dbl(kv, "orbitPeriod", OrbitPeriod);
            OrbitHeight    = Dbl(kv, "orbitHeight", OrbitHeight);
            OrbitClearance = Dbl(kv, "orbitClearance", OrbitClearance);
            OrbitGrid      = Bool(kv, "orbitGrid", OrbitGrid);

            // ANCHOR CHANGES ARE ANNOUNCED. Moving the orbit to another grid changes what
            // the feed shows completely, so a silent pickup would look like a bug in the
            // camera rather than a config edit that took. Compare before assigning.
            var anchorWas = OrbitAnchor;
            OrbitAnchor = Str(kv, "orbitAnchor", "");
            if (!string.Equals(anchorWas, OrbitAnchor, StringComparison.Ordinal))
                RttLog.Global($"Config: orbitAnchor \"{anchorWas}\" -> \"{OrbitAnchor}\". " +
                    (OrbitAnchor.Length == 0
                        ? "Empty — the orbit returns to the panel's OWN grid."
                        : "The orbit will re-centre on the first grid whose display name contains " +
                          "this, once the world walk resolves it. Watch for \"ORBIT ANCHOR:\" in the log; " +
                          "if it does not appear, the name matched nothing and the feed stays on its own grid."));

            // Edge-triggered, not level-triggered: only a false->true transition arms it.
            // Level-triggered would re-dump on every config poll for as long as the line
            // said 1, which is this project's house bug wearing a survey hat.
            var floraWas = FloraCameraOverride;
            FloraCameraOverride = Bool(kv, "floraCameraOverride", FloraCameraOverride);
            if (floraWas != FloraCameraOverride)
                RttLog.Global($"Config: floraCameraOverride {floraWas} -> {FloraCameraOverride}" +
                    (FloraCameraOverride
                        ? ". Flora sectors the feed camera is near now LOD and cull around the CAMERA instead " +
                          "of the player — the octree that hides them takes one camera, and this chooses which. " +
                          "Sectors the player is near are untouched. Watch for \"FLORA CAMERA:\" in the log."
                        : ". Every flora sector goes back to culling around the player."));

            var viewerWas = ViewerDistanceOverride;
            ViewerDistanceOverride = Bool(kv, "viewerDistance", ViewerDistanceOverride);
            ViewerDistanceRadius   = Dbl(kv, "viewerDistanceRadius", ViewerDistanceRadius);
            ViewerDistanceFull     = Dbl(kv, "viewerDistanceFull", ViewerDistanceFull);
            ViewerDistanceFarBias  = Dbl(kv, "viewerDistanceFarBias", ViewerDistanceFarBias);
            // Cleared here rather than only on the transition: the bubble is a LEASE the camera
            // pass renews, and the pass stops renewing it the moment the knob goes off — but a
            // config poll is the earliest we can know, and leaving it to the 5 s lease would
            // keep world pinned for five seconds after the operator asked for it to stop.
            if (!ViewerDistanceOverride) ViewerDistance.Clear();
            // AND take the delegate out, which Clear() does not do. Emptying the bubble makes
            // Nearest() return on its first line but leaves the postfix running for every root
            // entity in the scene — ~107,000 calls a second still paying a delegate dispatch
            // and a contended counter increment to reach a method that does nothing. Only
            // removing the hook lets the postfix early-out. Idempotent, so it is safe here.
            // MASTER GATE (task #40): the delegate IS the switch on a ~107k calls/s path —
            // with no feed live it comes out entirely, and the next poll after a feed
            // re-arms puts it back (SetHook is idempotent).
            ViewerDistance.SetHook((ViewerDistanceOverride || FixLodCycling) && Feeds.AnyLive);
            if (viewerWas != ViewerDistanceOverride)
                RttLog.Global($"Config: viewerDistance {viewerWas} -> {ViewerDistanceOverride}" +
                    (ViewerDistanceOverride
                        ? $" (radius {ViewerDistanceRadius:F0} m). Entities within that of the feed camera are " +
                          "now measured from the CAMERA rather than the player, which is the single input to " +
                          "StreamingTag, the impostor near/far swap, shadow tracking and the raytracing tags. " +
                          "Expect trees to resolve, foliage to thicken and grass to appear together — they are " +
                          "one mechanism. Watch \"VIEWER DISTANCE:\" and VRAM."
                        : ". Every entity goes back to being measured from the player alone."));

            GrassProbe = Bool(kv, "grassProbe", GrassProbe);
            PanelRebindOnLoss = Bool(kv, "panelRebindOnLoss", PanelRebindOnLoss);
            PanelBindAspect = Dbl(kv, "panelBindAspect", PanelBindAspect);
            PanelAspectFit = Bool(kv, "panelAspectFit", PanelAspectFit);
            PanelPrivateMaterial = Bool(kv, "panelPrivateMaterial", PanelPrivateMaterial);
            CameraRequireLayers = Strs(kv, "cameraRequireLayers", CameraRequireLayers);
            CameraBlockLayers = Strs(kv, "cameraBlockLayers", CameraBlockLayers);
            CameraBlockKeys = Ints(kv, "cameraBlockKeys", CameraBlockKeys);

            var clipWas = PerBodyClipmapCamera;
            PerBodyClipmapCamera     = Bool(kv, "perBodyClipmapCamera", PerBodyClipmapCamera);
            ClipmapMinPlayerDistance = Dbl(kv, "clipmapMinPlayerDistance", ClipmapMinPlayerDistance);
            ClipmapMaxFeedDistance   = Dbl(kv, "clipmapMaxFeedDistance", ClipmapMaxFeedDistance);
            ClipmapUpdateBudgetMs    = Dbl(kv, "clipmapUpdateBudgetMs", ClipmapUpdateBudgetMs);
            var burstWas = ClipmapArrivalBurst;
            ClipmapArrivalBurst      = Bool(kv, "clipmapArrivalBurst", ClipmapArrivalBurst);
            if (burstWas != ClipmapArrivalBurst)
                RttLog.Global($"Config: clipmapArrivalBurst {burstWas} -> {ClipmapArrivalBurst}" +
                    (ClipmapArrivalBurst
                        ? " (METERED: planet-scale bodies, 500 ms pulses, 60 s window, VRAM abort at 1.8 GB headroom). " +
                          "Watch for \"CLIPMAP ARRIVAL BURST\" and keep an eye on VRAM — the un-metered version " +
                          "removed the device."
                        : ". Pulses stop; the steady-state override continues."));
            TagAnchorForClutter      = Bool(kv, "tagAnchorForClutter", TagAnchorForClutter);

            var markerWas = CameraTriggerEntity;
            {
                var manWas = CameraManualControl;
                CameraManualControl = Bool(kv, "cameraManualControl", CameraManualControl);
                CameraRollRate = Dbl(kv, "cameraRollRate", CameraRollRate);
                CameraSpeed = Dbl(kv, "cameraSpeed", CameraSpeed);
                CameraLookSensitivity = Dbl(kv, "cameraLookSensitivity", CameraLookSensitivity);
                MarkerDespawnOnSave = Bool(kv, "markerDespawnOnSave", MarkerDespawnOnSave);
                CameraInvertLookX = Bool(kv, "cameraInvertLookX", CameraInvertLookX);
                CameraInvertLookY = Bool(kv, "cameraInvertLookY", CameraInvertLookY);
                if (manWas != CameraManualControl)
                    RttLog.Global($"Config: cameraManualControl {manWas} -> {CameraManualControl}. " +
                        (CameraManualControl
                            ? "The orbit is SUSPENDED and the camera flies on WASD / Space / Ctrl / Q / E. It resumes " +
                              "from where the orbit left it, or from the saved sidecar position if there is one."
                            : "Back to the orbit. The flown position is kept in the sidecar and restored when you re-arm."));
            }

            CameraTriggerEntity = Bool(kv, "cameraTriggerEntity", CameraTriggerEntity);
            CameraTriggerDynamicTag = Bool(kv, "cameraTriggerDynamicTag", CameraTriggerDynamicTag);
            var extWas = CameraTriggerExtent;
            CameraTriggerExtent = Dbl(kv, "cameraTriggerExtent", CameraTriggerExtent);
            if (Math.Abs(extWas - CameraTriggerExtent) > 0.001)
                RttLog.Global($"Config: cameraTriggerExtent {extWas:F2} -> {CameraTriggerExtent:F2} m. " +
                    "This is the RADIUS OF THE MATERIALIZATION BUBBLE — the sectored PlanetEnvironment " +
                    "triggers activate whatever sectors the marker's bounding box overlaps, so a small " +
                    "box buys a small disc of world however long it sits there. Applied at marker " +
                    "CREATION: the markers are rebuilt on the next pump pass. Resident world grows " +
                    "roughly with the cube of this — WATCH VRAM.");

            var presenceWas = ServerPresenceEntity;
            ServerPresenceEntity = Bool(kv, "serverPresenceEntity", ServerPresenceEntity);
            if (presenceWas != ServerPresenceEntity)
                RttLog.Global($"Config: serverPresenceEntity {presenceWas} -> {ServerPresenceEntity}" +
                    (ServerPresenceEntity
                        ? ". A DynamicTag presence entity will be created in the SERVER scene at the camera, " +
                          "on that scene's own pump — the archetype flora sectors, managed areas and voxel " +
                          "sectors all trigger on. Watch for \"SERVER PRESENCE:\" in the log."
                        : ". The server marker is removed on the next pump pass."));
            if (markerWas != CameraTriggerEntity)
                RttLog.Global($"Config: cameraTriggerEntity {markerWas} -> {CameraTriggerEntity}" +
                    (CameraTriggerEntity
                        ? ". A ClientTriggerTag marker entity now rides the feed camera — the same "
                          + "construction the engine's own voxel observer uses. Environment sectors should "
                          + "materialize around the camera whether or not anything is anchored to a grid. "
                          + "Watch for \"CAMERA TRIGGER:\" in the log; for a clean A/B set tagAnchorForClutter = 0."
                        : ". The marker is deleted on the next tick and the camera stops triggering sectors."));

            if (clipWas != PerBodyClipmapCamera)
                RttLog.Global($"Config: perBodyClipmapCamera {clipWas} -> {PerBodyClipmapCamera}" +
                    (PerBodyClipmapCamera
                        ? $" (safety floor {ClipmapMinPlayerDistance / 1000.0:F0} km). Voxel bodies the " +
                          "player is far from, and the camera is nearer to, will now LOD their terrain " +
                          "around the CAMERA. Bodies the player is nearer to are untouched. Watch for " +
                          "\"CLIPMAP CAMERA:\" in the log."
                        : ". Every body goes back to LODing around the player."));

            var preloadWas = PreloadAroundCamera;
            PreloadAroundCamera = Bool(kv, "preloadAroundCamera", PreloadAroundCamera);
            var srvPreloadWas = ServerPreload;
            ServerPreload = Bool(kv, "serverPreload", ServerPreload);
            if (srvPreloadWas != ServerPreload)
                RttLog.Global($"Config: serverPreload {srvPreloadWas} -> {ServerPreload}" +
                    (ServerPreload
                        ? ". Preload requests now ALSO go through the SPAWNING session's space probe — " +
                          "the data the flora pipeline's tasks await. Watch \"SERVER PRELOAD\" and VRAM."
                        : ". Server-side requests stop; pinned data stays until the engine reclaims it."));
            PreloadRadius       = Dbl(kv, "preloadRadius", PreloadRadius);
            PreloadIntervalMs   = Int(kv, "preloadIntervalMs", PreloadIntervalMs);
            PreloadPrecision    = Str(kv, "preloadPrecision", PreloadPrecision);
            if (preloadWas != PreloadAroundCamera)
                RttLog.Global($"Config: preloadAroundCamera {preloadWas} -> {PreloadAroundCamera}" +
                    (PreloadAroundCamera
                        ? $" ({PreloadRadius:F0} m radius, every {PreloadIntervalMs} ms, {PreloadPrecision} precision). " +
                          "The feed camera now asks the space probe to make terrain and flora resident around it. " +
                          "Watch for \"PRELOAD #\" in the log, and watch VRAM — residency somewhere no player " +
                          "stands is not free."
                        : ". The camera stops asking; anything already resident stays until the engine reclaims it."));

            var surveyWanted = Bool(kv, "worldGridSurvey", false);
            if (surveyWanted && !_surveyArmedLast && !_firstPoll) WorldGridSurvey = true;
            _surveyArmedLast = surveyWanted;

            var censusWanted = Bool(kv, "triggerCensus", false);
            if (censusWanted && !_censusArmedLast && !_firstPoll) TriggerCensus = true;
            _censusArmedLast = censusWanted;

            // FIRST POLL ARMS NOTHING. An edge-triggered flag left = 1 in the file re-fires
            // on a FRESH BOOT, because the "last" state starts false — that is how loadArea
            // CTD'd a world load on 2026-08-01 and how voxelBodySurvey did it again on
            // 2026-08-02, walking clipmap dictionaries while the clipmap was still building.
            // Seeding every edge from the first poll makes a stale 1 inert until a human
            // actually toggles it.
            var bodySurveyWanted = Bool(kv, "voxelBodySurvey", false);
            if (bodySurveyWanted && !_bodySurveyArmedLast && !_firstPoll) VoxelBodySurvey = true;
            _bodySurveyArmedLast = bodySurveyWanted;

            var floraSurveyWanted = Bool(kv, "serverFloraSurvey", false);
            if (floraSurveyWanted && !_floraSurveyArmedLast && !_firstPoll) ServerFloraSurvey = true;
            _floraSurveyArmedLast = floraSurveyWanted;

            // DURABLE consume for the area loader, not the in-memory edge the survey uses.
            // The in-memory version re-fired after a game restart — fresh statics saw the
            // name sitting in the file as a new edge and pulled the same trigger during
            // world load, which was CTD #2 of 2026-08-01. A WORLD MUTATION request must
            // survive-compare across process lifetimes, so the last consumed value lives in
            // a marker file: same value = already done, no matter how many boots ago.
            var loadWanted = Str(kv, "loadArea", "");
            if (loadWanted.Length > 0
                && !string.Equals(loadWanted, _loadAreaLast, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(loadWanted, ReadLoadAreaMarker(), StringComparison.OrdinalIgnoreCase))
            {
                LoadAreaRequest = loadWanted;
                WriteLoadAreaMarker(loadWanted);
            }
            _loadAreaLast = loadWanted;

            CopyEnabled       = Bool(kv, "copyEnabled", CopyEnabled);
            SrcTransition     = Bool(kv, "srcTransition", SrcTransition);
            DestTransition    = Bool(kv, "destTransition", DestTransition);
            RetireTestPattern = Bool(kv, "retireTestPattern", RetireTestPattern);
            BlitAlpha         = Bool(kv, "blitAlpha", BlitAlpha);
            PassOnFrameHook   = Bool(kv, "passOnFrameHook", PassOnFrameHook);

            SwapCamera   = Bool(kv, "swapCamera", SwapCamera);
            SwapCameraCb = Bool(kv, "swapCameraCb", SwapCameraCb);
            FixScreenRes = Bool(kv, "fixScreenRes", FixScreenRes);
            FullCameraCb = Bool(kv, "fullCameraCb", FullCameraCb);

            Emissivity  = Dbl(kv, "emissivity", Emissivity);
            DimDistance = Dbl(kv, "dimDistance", DimDistance);

            // NOT read here. FeedCount is part of the whole-scene signature (see the field
            // comment: re-routing panels invalidates every feed's cached panel identity), so
            // it is read inside the signature block below, between the two snapshots.

            // DELIBERATELY OUTSIDE THE SIGNATURE WINDOW (phase F4). Disabling a feed must
            // take the ordinary dormancy path — that feed's gate goes dormant, its teardown
            // runs 30 frames later, its neighbours never stop rendering — and NOT the
            // quiesced rebuild a feedCount change triggers, which stops every feed in the
            // mod. The whole point of the lever is to exercise "one of N goes away while the
            // rest keep running", so it must not be able to take the rest with it.
            ReadDisabledFeeds(kv);

            // ---- the whole-scene route -------------------------------------------
            string before = WholeSceneSignature();

            FeedCount               = Int(kv, "feedCount", FeedCount);
            MaxResidentFeeds        = Int(kv, "maxResidentFeeds", MaxResidentFeeds);
            FeedVramReserveMb       = Int(kv, "feedVramReserveMb", FeedVramReserveMb);
            FeedVramGuard           = Bool(kv, "feedVramGuard", FeedVramGuard);

            // INSIDE the signature window, and after the three knobs above are read, so a
            // cap change is detected as a signature change and re-routes panels through the
            // same rebuild a feedCount change uses. Evaluating it before the `before`
            // snapshot would make a cap move invisible to the comparison.
            Feeds.UpdateResidentCap();
            WholeSceneEnabled       = Bool(kv, "wholeSceneRender", WholeSceneEnabled);
            WholeSceneBuildBuffers  = Bool(kv, "wholeSceneBuildBuffers", WholeSceneBuildBuffers);
            WholeSceneWidth         = Int(kv, "wholeSceneWidth", WholeSceneWidth);
            WholeSceneHeight        = Int(kv, "wholeSceneHeight", WholeSceneHeight);
            WholeSceneCamera        = Bool(kv, "wholeSceneCamera", WholeSceneCamera);
            WholeSceneCameraRebuild = Int(kv, "wholeSceneCameraRebuild", WholeSceneCameraRebuild);
            WholeSceneToPanel       = Bool(kv, "wholeSceneToPanel", WholeSceneToPanel);
            WholeSceneIntervalMs    = Int(kv, "wholeSceneIntervalMs", WholeSceneIntervalMs);
            WholeSceneAAMode        = Int(kv, "wholeSceneAAMode", WholeSceneAAMode);
            WholeSceneExposure      = Dbl(kv, "wholeSceneExposure", WholeSceneExposure);
            {
                var offWas = WholeSceneExposureOffset;
                WholeSceneExposureOffset = Dbl(kv, "wholeSceneExposureOffset", WholeSceneExposureOffset);
                if (Math.Abs(offWas - WholeSceneExposureOffset) > 1e-9)
                    RttLog.Global($"Config: wholeSceneExposureOffset {offWas:0.###} -> {WholeSceneExposureOffset:0.###} EV. " +
                        (WholeSceneExposureOffset == 0
                            ? "Off — the feed's adapted exposure is used unbiased."
                            : (WholeSceneExposureOffset < 0 ? "Darker" : "Brighter") +
                              " by that many stops on top of the feed's own adaptation, which keeps running underneath."));
            }
            // Auto aperture — read INSIDE the signature window is harmless because none of
            // these are put INTO the signature (see WholeSceneSignature). Tuning is free.
            FeedAutoExposure         = Bool(kv, "feedAutoExposure", FeedAutoExposure);
            FeedExposureDay          = Dbl(kv, "feedExposureDay", FeedExposureDay);
            FeedExposureNight        = Dbl(kv, "feedExposureNight", FeedExposureNight);
            FeedExposureDawnDot      = Dbl(kv, "feedExposureDawnDot", FeedExposureDawnDot);
            FeedExposureDayDot       = Dbl(kv, "feedExposureDayDot", FeedExposureDayDot);
            FeedExposureAdaptSeconds = Dbl(kv, "feedExposureAdaptSeconds", FeedExposureAdaptSeconds);
            FeedSunSign              = Dbl(kv, "feedSunSign", FeedSunSign);
            WholeSceneNativeScaling = Bool(kv, "wholeSceneNativeScaling", WholeSceneNativeScaling);
            WholeSceneNoBloom       = Bool(kv, "wholeSceneNoBloom", WholeSceneNoBloom);
            WholeSceneLdrResize     = Bool(kv, "wholeSceneLdrResize", WholeSceneLdrResize);
            WholeSceneSubmitEarly   = Bool(kv, "wholeSceneSubmitEarly", WholeSceneSubmitEarly);
            WholeSceneRenderOnDemand = Bool(kv, "wholeSceneRenderOnDemand", WholeSceneRenderOnDemand);
            WholeSceneOnDemandHeartbeatMs = Int(kv, "wholeSceneOnDemandHeartbeatMs", WholeSceneOnDemandHeartbeatMs);
            WholeSceneFarClip       = Dbl(kv, "wholeSceneFarClip", WholeSceneFarClip);
            WholeSceneFarClipExtend = Bool(kv, "wholeSceneFarClipExtend", WholeSceneFarClipExtend);
            WholeSceneOwnDrawContexts   = Bool(kv, "wholeSceneOwnDrawContexts", WholeSceneOwnDrawContexts);
            WholeSceneOwnShadows        = Int(kv, "wholeSceneOwnShadows", WholeSceneOwnShadows);
            WholeSceneCascadeResolution = Int(kv, "wholeSceneCascadeResolution", WholeSceneCascadeResolution);
            WholeSceneCascadeCount      = Int(kv, "wholeSceneCascadeCount", WholeSceneCascadeCount);
            WholeSceneCharacterShadowResolution =
                Int(kv, "wholeSceneCharacterShadowResolution", WholeSceneCharacterShadowResolution);
            WholeScenePlanetEnv         = Bool(kv, "wholeScenePlanetEnv", WholeScenePlanetEnv);
            WholeSceneOwnFlares         = Bool(kv, "wholeSceneOwnFlares", WholeSceneOwnFlares);
            PanelFsrMask                = Bool(kv, "panelFsrMask", PanelFsrMask);
            PanelMipRegen               = Bool(kv, "panelMipRegen", PanelMipRegen);
            PanelTickCensus             = Bool(kv, "panelTickCensus", PanelTickCensus);
            PanelTickCensusMs           = Int(kv, "panelTickCensusMs", PanelTickCensusMs);
            ActivationGraceMs           = Int(kv, "activationGraceMs", ActivationGraceMs);
            ResidencySettleMs           = Int(kv, "residencySettleMs", ResidencySettleMs);
            StatsPanel                  = Bool(kv, "statsPanel", StatsPanel);
            StatsPanelMs                = Int(kv, "statsPanelMs", StatsPanelMs);
            RttBudgetMs                 = Dbl(kv, "rttBudgetMs", RttBudgetMs);
            WholeSceneFlareIntensity    = Dbl(kv, "wholeSceneFlareIntensity", WholeSceneFlareIntensity);

            // The scatter control surface. All per-pass, all outside the rebuild signature:
            // they are pure ScopeSetValues calls read fresh on every render, so a sweep is
            // live rather than a series of gate cycles.
            WholeSceneFloraLodMult      = Dbl(kv, "wholeSceneFloraLodMult", WholeSceneFloraLodMult);

            var eyeWas = WholeSceneOwnEyeAdaptation;
            WholeSceneOwnEyeAdaptation = Bool(kv, "wholeSceneOwnEyeAdaptation", WholeSceneOwnEyeAdaptation);
            if (eyeWas != WholeSceneOwnEyeAdaptation)
                RttLog.Global($"Config: wholeSceneOwnEyeAdaptation {eyeWas} -> {WholeSceneOwnEyeAdaptation}. " +
                    (WholeSceneOwnEyeAdaptation
                        ? "The feed gets its OWN auto-exposure history, so it adapts to what the CAMERA sees " +
                          "instead of holding a fixed stop. Needs the parked instance from the bootstrap — if the " +
                          "log says the park is missing, restart the game; it will refuse to arm rather than leak " +
                          "RTV descriptors. Also implies dropping the EyeAdaptation scope for our pass."
                        : "Back to a fixed stop (wholeSceneExposure)."));

            var coneWas = ResidencyConeDegrees;
            ResidencyConeDegrees    = Dbl(kv, "residencyConeDegrees", ResidencyConeDegrees);
            ResidencyConeNearMetres = Dbl(kv, "residencyConeNearMetres", ResidencyConeNearMetres);
            ResidencyConeMarginDegrees = Dbl(kv, "residencyConeMarginDegrees", ResidencyConeMarginDegrees);
            if (Math.Abs(coneWas - ResidencyConeDegrees) > 1e-9)
                RttLog.Global($"Config: residencyConeDegrees {coneWas:F0} -> {ResidencyConeDegrees:F0}. " +
                    (ResidencyConeDegrees > 0 && ResidencyConeDegrees < 360
                        ? $"Flora sectors and voxel bodies more than {ResidencyConeDegrees / 2:F0} degrees off the " +
                          $"camera's view axis are no longer claimed for the feed, except within " +
                          $"{ResidencyConeNearMetres:F0} m. The study measured 77.4% of sector updates outside 140 " +
                          "degrees. WATCH FOR: shadows disappearing from objects that should cast into frame, and " +
                          "pop-in as the orbit turns — those are the two failure modes, and both are visible in the " +
                          "feed rather than only in a counter."
                        : "OFF — residency is omnidirectional again, as it was before the cone."));
            WholeSceneLodShift          = Int(kv, "wholeSceneLodShift", WholeSceneLodShift);
            var floraMaxWas = WholeSceneFloraMaxMetres;
            WholeSceneFloraMaxMetres    = Dbl(kv, "wholeSceneFloraMaxMetres", WholeSceneFloraMaxMetres);
            if (Math.Abs(floraMaxWas - WholeSceneFloraMaxMetres) > 1e-9)
                RttLog.Global($"Config: wholeSceneFloraMaxMetres {floraMaxWas:0.#} -> {WholeSceneFloraMaxMetres:0.#}. " +
                    (WholeSceneFloraMaxMetres > 0
                        ? $"Flora batches are now clamped to draw no further than {WholeSceneFloraMaxMetres:0} m, " +
                          "independent of LOD — near detail is untouched. Applied on a cadence and idempotent, so " +
                          "newly allocated batches get caught too. LOWERING takes effect within a second; RAISING " +
                          "does not restore batches already clamped (the engine bakes _cullingDistance once, at " +
                          "allocation) — reload to go back up."
                        : "OFF — flora draws to whatever distance the engine baked from its LOD multiplier."));
            WholeSceneFloraMinLod       = Int(kv, "wholeSceneFloraMinLod", WholeSceneFloraMinLod);
            WholeSceneObjectDistanceMult= Dbl(kv, "wholeSceneObjectDistanceMult", WholeSceneObjectDistanceMult);
            WholeSceneSmallObjectMult   = Int(kv, "wholeSceneSmallObjectMult", WholeSceneSmallObjectMult);
            WholeSceneGrassDrawDistance = Dbl(kv, "wholeSceneGrassDrawDistance", WholeSceneGrassDrawDistance);
            WholeSceneGrassDensity      = Dbl(kv, "wholeSceneGrassDensity", WholeSceneGrassDensity);
            WholeSceneParallax             = Int(kv, "wholeSceneParallax", WholeSceneParallax);
            WholeSceneParallaxFadeout      = Dbl(kv, "wholeSceneParallaxFadeout", WholeSceneParallaxFadeout);
            WholeSceneParallaxSelfShadow   = Int(kv, "wholeSceneParallaxSelfShadow", WholeSceneParallaxSelfShadow);
            WholeSceneParallaxShadowLength = Dbl(kv, "wholeSceneParallaxShadowLength", WholeSceneParallaxShadowLength);
            WholeSceneParallaxSteps        = Int(kv, "wholeSceneParallaxSteps", WholeSceneParallaxSteps);

            var texCamWas = FeedTextureCamera;
            FeedTextureCamera = Bool(kv, "feedTextureCamera", FeedTextureCamera);
            if (texCamWas != FeedTextureCamera)
            {
                RttLog.Global($"Config: feedTextureCamera {texCamWas} -> {FeedTextureCamera}" +
                    (FeedTextureCamera
                        ? ". Texture mips for entities NEARER THE FEED than the player are now chosen from " +
                          "the feed camera. Expect resident texture memory to RISE — that is the feature " +
                          "working, not a leak. Watch headroom."
                        : ". Texture priority is the player's alone again."));
                if (!FeedTextureCamera) TextureCamera.Stand_Down();
            }
            {
                var ratioWas = FeedTextureCameraEnterRatio;
                FeedTextureCameraEnterRatio = Dbl(kv, "feedTextureCameraEnterRatio", FeedTextureCameraEnterRatio);
                if (Math.Abs(ratioWas - FeedTextureCameraEnterRatio) > 1e-9)
                    RttLog.Global($"Config: feedTextureCameraEnterRatio {ratioWas:0.###} -> {FeedTextureCameraEnterRatio:0.###}. " +
                        (FeedTextureCameraEnterRatio >= 1.0
                            ? "1.0 or above disables the hysteresis — this is the OLD strict comparison that measured 4.2% alternation."
                            : "Our camera must now be this much nearer to TAKE an entity over; it keeps it until we stop being nearer at all. " +
                              "Watch the STABILITY field in the FEED TEXTURE CAMERA line — that alternation percentage is what this moves."));
            }
            {
                var stepWas = FeedTextureCameraDistanceStep;
                FeedTextureCameraDistanceStep = Dbl(kv, "feedTextureCameraDistanceStep", FeedTextureCameraDistanceStep);
                if (Math.Abs(stepWas - FeedTextureCameraDistanceStep) > 1e-9)
                    RttLog.Global($"Config: feedTextureCameraDistanceStep {stepWas:0.###} -> {FeedTextureCameraDistanceStep:0.###}. " +
                        (FeedTextureCameraDistanceStep <= 0
                            ? "0 DISABLES the latch — entities are presented at the raw orbiting distance, which is the arrangement that measured 25-30x tier churn."
                            : "An entity keeps its presented distance until the true distance departs by this fraction, so tiers step instead of sliding with the orbit. " +
                              "Judge it on the REVERSAL count in TEXTURE TIER CHURN, against the override-off control arm as the floor."));
            }
            {
                FeedTextureCameraMinDistMult = Dbl(kv, "feedTextureCameraMinDistMult", FeedTextureCameraMinDistMult);
                var backWas = FeedTextureCameraBackoff;
                FeedTextureCameraBackoff = Dbl(kv, "feedTextureCameraBackoff", FeedTextureCameraBackoff);
                if (Math.Abs(backWas - FeedTextureCameraBackoff) > 1e-9)
                    RttLog.Global($"Config: feedTextureCameraBackoff {backWas:0.#} -> {FeedTextureCameraBackoff:0.#} m. " +
                        (FeedTextureCameraBackoff <= 0
                            ? "0 — the virtual texture-camera sits exactly at the feed eye again."
                            : "The virtual texture-camera is pulled back along the centre->eye ray by this much, so near content " +
                              "is presented farther and lifts out of the priority clamp at P/2D. Compare the computed floor in the " +
                              "TEXTURE PRIORITY SATURATION line, and judge on the REVERSAL count in TEXTURE TIER CHURN."));
                var maxWas = FeedTextureCameraMaxDist;
                FeedTextureCameraMaxDist = Dbl(kv, "feedTextureCameraMaxDist", FeedTextureCameraMaxDist);
                if (Math.Abs(maxWas - FeedTextureCameraMaxDist) > 1e-9)
                    RttLog.Global($"Config: feedTextureCameraMaxDist {maxWas:0.#} -> {FeedTextureCameraMaxDist:0.#} m. " +
                        (FeedTextureCameraMaxDist <= 0
                            ? "0 — no ceiling; presented distance is whatever the feed camera actually sees."
                            : "Distant content is now presented as if this close, which RAISES its streaming priority off the " +
                              "eviction cut. *** THIS COSTS VRAM: a lower demanded mip is ~4x the bytes per step. Watch the " +
                              "budget — it is not throttled automatically. ***"));
            }

            // GLOBAL — announced on every change, because unlike everything above this one
            // changes the PLAYER'S world and its VRAM. Logged here as well as at the apply
            // site so the transition is in the log even if the apply is what fails.
            WorldFloraLodMult = Dbl(kv, "worldFloraLodMult", WorldFloraLodMult);
            FixLodCycling     = Bool(kv, "fixLodCycling", FixLodCycling);

            var floraRadiusWas = WorldFloraRadiusMult;
            WorldFloraRadiusMult = Dbl(kv, "worldFloraRadiusMult", WorldFloraRadiusMult);
            if (Math.Abs(floraRadiusWas - WorldFloraRadiusMult) > 1e-9)
                RttLog.Global($"Config: worldFloraRadiusMult {floraRadiusWas:0.###} -> {WorldFloraRadiusMult:0.###}. " +
                    "GLOBAL, not feed-only: Flora.RenderingDistanceMultiplier is baked into each flora " +
                    "instance batch at ALLOCATION time, so this changes the player's view too and only " +
                    "affects batches allocated from now on — existing flora keeps the radius it was born " +
                    "with. Expect the world to change over the next minute or two as sectors cycle, NOT " +
                    "instantly. More resident flora is more VRAM and this knob has no automatic guard.");
            WholeSceneOwnProbes         = Bool(kv, "wholeSceneOwnProbes", WholeSceneOwnProbes);
            WholeSceneDisableRaytracing    = Int(kv, "wholeSceneDisableRaytracing", WholeSceneDisableRaytracing);
            WholeSceneSsrLocal          = Bool(kv, "wholeSceneSsrLocal", WholeSceneSsrLocal);
            WholeSceneOwnRayTracingScene = Bool(kv, "wholeSceneOwnRayTracingScene", WholeSceneOwnRayTracingScene);
            WholeSceneOwnIRCache        = Bool(kv, "wholeSceneOwnIRCache", WholeSceneOwnIRCache);
            WholeSceneIrCachePopulate   = Bool(kv, "wholeSceneIrCachePopulate", WholeSceneIrCachePopulate);
            WholeSceneIblOnlyAmbient    = Bool(kv, "wholeSceneIblOnlyAmbient", WholeSceneIblOnlyAmbient);
            FeedAmbientFloor            = Dbl(kv, "feedAmbientFloor", FeedAmbientFloor);
            PanelCoverFit               = Bool(kv, "panelCoverFit", PanelCoverFit);
            WholeSceneDisableEyeAdaptation = Bool(kv, "wholeSceneDisableEyeAdaptation", WholeSceneDisableEyeAdaptation);

            var grassHizWas = WholeSceneGrassNoHiZ;
            WholeSceneGrassNoHiZ = Bool(kv, "wholeSceneGrassNoHiZ", WholeSceneGrassNoHiZ);
            if (grassHizWas != WholeSceneGrassNoHiZ)
                RttLog.Global($"Config: wholeSceneGrassNoHiZ {grassHizWas} -> {WholeSceneGrassNoHiZ}" +
                    (WholeSceneGrassNoHiZ
                        ? ". Grass generation in the feed now runs the NoHiZ pipeline — instances are no " +
                          "longer occlusion-tested against a depth pyramid. If grass appears, the pyramid " +
                          "was rejecting every instance because it does not match our camera."
                        : ". Grass generation goes back to occlusion-testing against HiZ."));

            var floraMaxWasD = FeedFloraMaxDistance;
            FeedFloraMaxDistance = Dbl(kv, "feedFloraMaxDistance", FeedFloraMaxDistance);
            if (Math.Abs(floraMaxWasD - FeedFloraMaxDistance) > 1e-9)
                RttLog.Global($"Config: feedFloraMaxDistance {floraMaxWasD:0.#} -> {FeedFloraMaxDistance:0.#}. " +
                    (FeedFloraMaxDistance > 0
                        ? $"Distant merged flora meshes now stay visible to {FeedFloraMaxDistance * 1.2:0} m " +
                          "(threshold = value x1.2). GLOBAL — the player's world too. Watch DISTANT TIER " +
                          "flips: they should fall to ~0 once the boundary is past what the feed draws."
                        : "Left at the engine's own value (stock 250 -> a 300 m boundary)."));

            var noOccWas = WholeSceneNoOcclusion;
            WholeSceneNoOcclusion = Bool(kv, "wholeSceneNoOcclusion", WholeSceneNoOcclusion);
            if (noOccWas != WholeSceneNoOcclusion)
                RttLog.Global($"Config: wholeSceneNoOcclusion {noOccWas} -> {WholeSceneNoOcclusion}" +
                    (WholeSceneNoOcclusion
                        ? ". Culling compositions whose view is the FEED'S resolution now run WITHOUT " +
                          "occlusion culling — no HiZ test, no shared LastVisibleFrame classifier. " +
                          "Player compositions untouched by construction (argument-derived, per call)."
                        : ". Feed compositions occlusion-cull normally again."));

            var hzboWas = WholeSceneNoHzbo;
            WholeSceneNoHzbo = Bool(kv, "wholeSceneNoHzbo", WholeSceneNoHzbo);
            if (hzboWas != WholeSceneNoHzbo)
                RttLog.Global($"Config: wholeSceneNoHzbo {hzboWas} -> {WholeSceneNoHzbo}" +
                    (WholeSceneNoHzbo
                        ? ". Hierarchical-Z occlusion culling is OFF for the feed's pass only. If the " +
                          "shadows-without-objects and the missing grass BOTH resolve, the depth pyramid " +
                          "was culling against the wrong camera and that was one bug, not two. Costs feed " +
                          "draw calls; watch ourDraw in the PERF line."
                        : ". Occlusion culling restored in the feed."));
            WholeSceneDisableProbeUpdates  = Bool(kv, "wholeSceneDisableProbeUpdates", WholeSceneDisableProbeUpdates);
            WholeSceneSkipStages    = Ints(kv, "wholeSceneSkipStages", WholeSceneSkipStages);
            WholeSceneRtFlags       = Strs(kv, "wholeSceneRtFlags", WholeSceneRtFlags);

            // A resolution or buffer-mode change needs a full rebuild of the second
            // ScreenBuffers and DrawContexts; anything else just needs the one-strike
            // disable cleared, so an experiment is a config save rather than a rebuild.
            //
            // The signature covers every value the rebuild depends on. Comparing one
            // string rather than thirty fields is what stops a newly-added knob silently
            // missing the comparison — the failure mode of the chain this replaced, where
            // editing the config appeared to do nothing at all.
            // SWEPT ACROSS EVERY FEED (phase C3 prerequisite). Poll() runs under whichever
            // feed holds the render slot at that instant, so a bare Reset() would rebuild
            // exactly one of them and silently leave the rest at the old resolution. Quality
            // is GLOBAL by design — the signature describes buffer identity, which every
            // feed shares — so the rebuild is global too. At Count == 1 this is identical to
            // what it replaced. (Reset defers itself when called from inside a render; the
            // drain in RunSecondRender's finally sweeps all feeds the same way.)
            string after = WholeSceneSignature();
            int countAfter = Feeds.Count;

            // A COUNT CHANGE TAKES THE DORMANT PATH. Everything else rebuilds in place.
            //
            // CTD 2026-07-30 20:54, on the cleanest baseline this project has had (52.4 fps,
            // >50ms=0, 1.5 GB of VRAM headroom): feedCount was flipped 1 -> 2 with feed 0
            // live, and the game device-removed 2.5 s later with a NULL BIND inside culling —
            // PageFaultVA 0x0, ExistingAllocations 0, RecentFreedAllocations 0, and 1.6 GB
            // still free. Not memory.
            //
            //     20:53:58.915  [feed 0] Whole-scene Reset: 12511 -> 12385 MB
            //     20:54:00.425  [feed 0] SECOND ScreenBuffers built, secondRenders=0
            //                   <<< DEVICE_REMOVED, no DC build, no settle line, no ERROR >>>
            //
            // The exact null resource was never identified, and this fix does not depend on
            // identifying it. What IS established is the window: a count change re-routes
            // every panel and rebuilds every feed AT ONCE, underneath a render that is still
            // running. This project now has CTDs from that same window on three separate
            // knobs — wholeSceneAAMode (4), wholeSceneOwnProbes (3), and now feedCount — and
            // both of the other two were ultimately resolved by refusing to change them under
            // a live render rather than by making the change safe.
            //
            // The dormant path is already proven: the relaunch after this crash came up with
            // feedCount=2 and built both feeds from a dormant gate with no trouble, and every
            // pause-protocol deploy all evening did the same. So instead of asking the
            // operator to remember the pause protocol for this knob, take it automatically.
            //
            // Deliberately scoped to COUNT changes only. Resolution and layer changes have
            // rebuilt in place many times without incident, and widening this would be
            // changing two things at once on the most crash-prone path in the codebase.
            bool countChanged = !_firstPoll && countAfter != _lastCount;
            _lastCount = countAfter;

            if (countChanged)
            {
                RttLog.Line($"Config: feed count {_lastCountLogged} -> {countAfter}. Re-routing panels " +
                            "requires rebuilding EVERY feed, so the gate goes dormant first and rebuilds " +
                            "from a quiesced renderer — changing this under a live render device-removed " +
                            "the game on 2026-07-30. The feed returns on its own in about a second.");
                _lastCountLogged = countAfter;
                FeedGate.RequestQuiescedRebuild();
                // No Reset here on purpose: the gate's Shutdown already calls
                // WholeSceneRender.Reset per feed, on the render thread, with nothing in
                // flight. Doing it here as well would be the very in-place teardown this is
                // avoiding.
            }
            // Reset RELEASES, so it sweeps EVERY slot — shrinking feedCount (or the VRAM cap
            // clamping it) is precisely the case where a slot leaves Count while still owning
            // a ScreenBuffers and a DrawContextManager, and a Count-bounded sweep would skip
            // it at the one moment it needed freeing. Rearm only re-arms latches on feeds
            // that will run, so it stays bounded by Count.
            else if (_firstPoll || before != after) Feeds.ForEachSlot(RttProbe.WholeSceneRender.Reset);
            else Feeds.ForEach(RttProbe.WholeSceneRender.Rearm);
            _firstPoll = false;

            RttLog.Line($"Config: intervalMs={IntervalMs} (~{1000.0 / Math.Max(1, IntervalMs):F0} fps) " +
                        $"orbit radius>={OrbitRadius} clearance={OrbitClearance}x grid={OrbitGrid} " +
                        $"period={OrbitPeriod}s height={OrbitHeight} " +
                        $"| emissivity={Emissivity} dimDistance={DimDistance} " +
                        $"| whole-scene: render={WholeSceneEnabled} {WholeSceneWidth}x{WholeSceneHeight} " +
                        $"@{WholeSceneIntervalMs}ms camera={WholeSceneCamera} rebuild={WholeSceneCameraRebuild} " +
                        $"ownDc={WholeSceneOwnDrawContexts} ownShadows={WholeSceneOwnShadows} " +
                        $"({WholeSceneCascadeCount}x{WholeSceneCascadeResolution}) planetEnv={WholeScenePlanetEnv} " +
                        $"aa={WholeSceneAAMode} skip=[{string.Join(",", WholeSceneSkipStages)}] " +
                        $"rtFlags=[{string.Join(",", WholeSceneRtFlags)}] " +
                        // ssrLocal belongs in the SUMMARY, not only in its own scope line.
                        // The scope line prints from inside our render and only once, so its
                        // ABSENCE is ambiguous — it cannot distinguish "the knob is off" from
                        // "the render never got that far". Reporting the parsed value here
                        // makes the config side answerable on its own, which is exactly the
                        // gap that made the first deploy ungradeable.
                        $"ssrLocal={WholeSceneSsrLocal}");
        }
        catch { /* keep the last good values */ }
        finally
        {
            // AFTER the body, and in a finally so a throw mid-poll cannot leave the process
            // permanently "first" and silently disable every one-shot for the session.
            _firstPoll = false;
        }
    }

    // Everything the second ScreenBuffers / DrawContexts build depends on.
    private static string WholeSceneSignature() =>
        string.Join("|", WholeSceneBuildBuffers, WholeSceneWidth, WholeSceneHeight,
                         // THE EFFECTIVE COUNT is here, not the raw feedCount. Changing it
                         // re-routes which panel each feed owns, and every feed caches its
                         // panel's identity (resolved id, offscreen target, handle text,
                         // render target). Without a rebuild those caches survive the
                         // re-route and each feed keeps matching another panel's handle —
                         // which is a silent frozen picture, not an error. See the FeedCount
                         // field comment for the observed failure.
                         //
                         // Feeds.Count rather than FeedCount so the phase-E1 VRAM cap gets
                         // the identical treatment: a cap that clamps 2 feeds to 1 is the
                         // same re-route as a user editing feedCount from 2 to 1, and it
                         // would leave the same stale caches behind if it were invisible here.
                         Feeds.Count,
                         // WholeSceneIntervalMs is deliberately ABSENT (plan phase A2).
                         // Its only consumer is TryRender's rate gate, which reads
                         // FeedConfig fresh every frame, so a change needs no rebuild:
                         // nothing about buffer or context IDENTITY depends on the render
                         // period. Leaving it in the signature meant every cadence tweak
                         // cost a full gate cycle plus a 30-frame settle — and cadence is
                         // exactly what the phase-E slot scheduler will be tuning, so it
                         // has to be free to change. Class (a), verified by consumer.
                         WholeSceneCamera, WholeSceneToPanel,
                         WholeSceneCameraRebuild, WholeSceneAAMode, WholeSceneExposure,
                         WholeSceneNativeScaling, WholeSceneNoBloom, WholeSceneLdrResize, WholeSceneDisableRaytracing,
                         WholeSceneDisableEyeAdaptation, WholeSceneDisableProbeUpdates,
                         WholeSceneOwnDrawContexts, WholeSceneOwnShadows,
                         WholeSceneCascadeResolution, WholeSceneCascadeCount,
                         // WholeSceneOwnProbes is deliberately ABSENT — see below.
                         WholeScenePlanetEnv, WholeSceneOwnFlares,
                         string.Join(",", WholeSceneSkipStages),
                         string.Join(",", WholeSceneRtFlags));

    // ---------------------------------------------------------------- parsing

    private static Dictionary<string, string> Read()
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var raw in File.ReadAllLines(Path_))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1);

                // Strip a TRAILING comment. Without this, `orbitGrid = 1  # orbit the
                // grid` parses as the literal "1  # orbit the grid", every TryParse
                // fails, and the knob silently reverts to its default — which is not a
                // hypothetical: documenting the file inline took the whole-scene route
                // down for twenty minutes, and the only visible symptom was a config log
                // line reading grid=False.
                int hash = val.IndexOf('#');
                if (hash >= 0) val = val.Substring(0, hash);

                kv[key] = val.Trim();
            }
        }
        catch { }
        return kv;
    }

    // Trimmed, never null. An absent key and an empty value mean the same thing to every
    // caller here ("not set"), so they are collapsed rather than distinguished.
    private static string Str(Dictionary<string, string> kv, string key, string fallback) =>
        kv.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

    private static int Int(Dictionary<string, string> kv, string key, int fallback) =>
        kv.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var r) ? r : fallback;

    private static double Dbl(Dictionary<string, string> kv, string key, double fallback) =>
        kv.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var r) ? r : fallback;

    // Accepts 1/0, true/false, yes/no — the file has been written all three ways.
    private static bool Bool(Dictionary<string, string> kv, string key, bool fallback)
    {
        if (!kv.TryGetValue(key, out var v)) return fallback;
        v = v.Trim();
        if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                     || v.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)
                     || v.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
        return fallback;
    }

    // An empty value is a legitimate EMPTY LIST, not "missing". `wholeSceneRtFlags =`
    // with nothing after it is how the RT scoping was turned off, and treating that as
    // absent would silently restore the previous flags.
    private static int[] Ints(Dictionary<string, string> kv, string key, int[] fallback)
    {
        if (!kv.TryGetValue(key, out var v)) return fallback;
        var outv = new List<int>();
        foreach (var tok in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(tok.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                outv.Add(n);
        return outv.ToArray();
    }

    private static string[] Strs(Dictionary<string, string> kv, string key, string[] fallback)
    {
        if (!kv.TryGetValue(key, out var v)) return fallback;
        var outv = new List<string>();
        foreach (var tok in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = tok.Trim();
            if (t.Length > 0) outv.Add(t);
        }
        return outv.ToArray();
    }
}
