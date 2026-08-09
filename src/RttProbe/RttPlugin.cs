using System.Reflection;
using System.Runtime.Loader;
using Keen.VRage.Core.Plugins;

namespace RttProbe;

// Static handoff between the bootstrap (loaded once, holds the Harmony patches)
// and the hot-reloadable logic assembly. The bootstrap never references logic
// types directly — that would pin the collectible load context.
public static class RttBridge
{
    // ---- PARKED PROBE MANAGERS (goal 4.4, CTD 2026-07-30 18:46) --------------------
    //
    // Per-feed EnvironmentProbeManager instances, held HERE rather than in the logic
    // assembly. This is not a convenience — it is the only place they can live.
    //
    // The manager owns eight cube textures, each six faces of RTV descriptors. Those come
    // from DescriptorHeapPool, a small FIXED pool that exhausts long before VRAM does. The
    // manager is deliberately never disposed (three device removals established that
    // disposing it mid-session removes the device), so the design depends on "kept" really
    // meaning kept.
    //
    // It did not. The logic assembly is COLLECTIBLE. A field there is gone on every hot
    // reload, so each reload built a fresh manager and left the previous one unreachable
    // from any code that could free it. Not disposing it was the deliberate choice; losing
    // the reference to it was not. Four reloads in one session ran the pool dry:
    //
    //     Assertion Failure: Out of the descriptor heap
    //       at DescriptorHeapPool.BorrowRTV()
    //       at RenderTargetCubeTexture.FaceMips.Initialize()
    //       at EnvironmentProbeManager.RecreateProbes()
    //       at WholeSceneRender.InstallProbes()
    //     [Watchdog]: application froze, RenderThreadFreeze.
    //
    // The bootstrap is loaded once and never unloaded, so a reference here survives every
    // reload and the SAME manager is reused instead of a new one being built beside it.
    //
    // TYPED AS object ON PURPOSE. The bootstrap must not reference engine render types any
    // more than it references logic types — resolving them here would drag Render12 type
    // loading into plugin init, which has already poisoned a type once (the
    // ConfigurationNotFoundException in a CoreSystems cctor). The logic side reflects over
    // these; the bootstrap only holds them alive.
    //
    // Sized to Feeds.MaxFeeds. A mismatch is not a crash — the logic side bounds-checks —
    // but it would silently stop parking the feeds past the end, so keep them in step.
    public static readonly object[] ParkedProbeManagers = new object[4];

    // PER-FEED EYE ADAPTATION, parked for exactly the reason above.
    //
    // EyeAdaptationJob holds the auto-exposure history as INSTANCE state — a
    // RenderTargetTexture[] ping-pong pair plus a histogram RWBuffer — which is what makes
    // per-feed adaptation possible at all: give our render its own instance and its history
    // stops fighting the player's. That is the same shape as the probe manager, the cascade
    // shadows and the draw contexts.
    //
    // AND IT CARRIES THE SAME HAZARD, which is why this array exists before the feature does.
    // Logic statics die on every hot reload, so a logic-owned instance is rebuilt each time
    // and the previous one becomes unreachable from any code that could dispose it. Its
    // RENDER TARGET VIEWS leak — and RTV descriptors come from a small fixed pool that
    // exhausts long before VRAM does. That is not a prediction: own-probes CTD'd on
    // 2026-07-30 with "Assertion Failure: Out of the descriptor heap at
    // DescriptorHeapPool.BorrowRTV()" after four reloads, while VRAM sat flat at 12.2 GB.
    // Parking the instance here is the fix that made own-probes safe, and it is a
    // prerequisite for this feature rather than a hardening pass to do afterwards.
    //
    // Typed object and sized to Feeds.MaxFeeds, both for the reasons given above.
    public static readonly object[] ParkedEyeAdaptation = new object[4];

    // ---- THE RATCHET FIX (2026-08-02): PARK THE TWO BIG GPU-RESOURCE OWNERS -----------
    //
    // MEASURED, and this is why it is the top priority rather than hygiene. Reading the
    // engine's own StreamingStatManager across a session:
    //     fresh session (0 hot reloads):  KnownNonStreaming 6024 MB, RealAvailableStreaming +6505 MB
    //     after SIX hot reloads:          KnownNonStreaming 12781 MB, RealAvailableStreaming -1240 MB
    // That is ~1.1 GB of NON-EVICTABLE memory stranded PER RELOAD. Once
    // RealAvailableStreaming goes negative the streaming system is clamped to a floor and
    // thrashes — evict, immediately re-need, re-fetch — which shows up as textures and LODs
    // cycling AND as frame hitches, in the player's world and the feed alike, because it is
    // one global pool.
    //
    // WHY IT LEAKS. _ourScreenBuffers and _ourDrawContexts hang off Feeds.Cur, which lives
    // in the COLLECTIBLE logic assembly. A hot reload discards the whole registry, so those
    // objects become unreachable while still owning depth buffers, the GBuffer array, the
    // final LDR texture, visibility lists and occlusion contexts. Nothing can dispose them
    // afterwards because nothing can reach them. "Kept" silently became "leaked", once per
    // reload — exactly the failure ParkedProbeManagers was created to fix, in two more places
    // that never got the same treatment.
    //
    // One slot per feed, same as the probe park. Untyped object[] on purpose: the bootstrap
    // must never reference an engine type. The logic side type-checks on adoption so a slot
    // parked by an older, incompatible build is discarded rather than trusted.
    public static readonly object[] ParkedScreenBuffers = new object[4];
    public static readonly object[] ParkedDrawContexts = new object[4];

    // PER-FEED RAYTRACING SCENE (the TLAS), parked for the same reason as everything above.
    //
    // WHY IT IS NEEDED, established 2026-08-05. Stage 0 (ExecuteAccelerationStructuresBuilding)
    // is skipped for our pass because RayTracingSceneManager.CreateTLAS is camera-dependent AND
    // world-space shared — building it from the feed camera corrupted the player's. So our rays
    // trace against a structure selected for the PLAYER's position, and the raytraced half of
    // ambient stays the player's however correct the probe cubes are. Owning a second manager
    // is what lets stage 0 run for us without touching theirs; suppression can never fix
    // something the feed has to CONSUME.
    //
    // AND THE HAZARD IS THE LARGEST OF THE SET, which is why the park comes first rather than
    // afterwards. RayTracingSceneManager owns _tlasBuffers, InstanceDataBuffer,
    // GeometryDataBuffer, ResultBuffer and eight entity Buffer<T> pools — all GPU allocations.
    // A logic-owned instance dies unreachable on every hot reload with those still allocated,
    // which is the VRAM ratchet (~1.1 GB/reload measured) and the descriptor-heap exhaustion
    // that CTD'd own-probes after four reloads. Adoption order on the logic side is parked slot
    // first, this load's field second, construct only if both are empty.
    //
    // Typed object and sized to Feeds.MaxFeeds, both for the reasons given above.
    public static readonly object[] ParkedRayTracingScenes = new object[4];

    // THE IRRADIANCE CACHE, parked for exactly the same reasons — and it fixes a bug pointing
    // the OTHER way. CoreSystems.IRCacheResources is a WORLD-SPACE hash grid keyed by cell,
    // and stage 30 (RaytracingPrepare -> IRCacheTraceJob) populates it from whatever camera
    // the pass is running. We un-skipped stage 30 so the FEED's ambient would be computed at
    // the feed camera, but we never owned the cache — so our entries land in the SHARED grid
    // and the PLAYER's frame samples them.
    //
    // Harmless while the feed was 4000 km away (different cells, no collision) and exactly
    // why this went unnoticed. Confirmed 2026-08-06 the moment the camera flew near the test
    // base: identical player position, aim and time of day, and moving ONLY the feed camera's
    // aim visibly changed the player's cockpit lighting. Our own config note predicted this
    // outcome verbatim when stage 30 was un-skipped.
    //
    // IRCacheResourcesManager allocates ~14 RWBuffers plus a container built lazily by
    // CreateAndInitializeContainer, so a logic-owned instance would strand that whole set on
    // every hot reload — the same ratchet ParkedProbeManagers exists to prevent.
    public static readonly object[] ParkedIRCaches = new object[4];

    // TRUE only on the thread executing the feed's nested Draw, only for its duration. Read
    // by the prefix on RaytraceGIJob.DoWork, which clears the GI buffers and skips the trace
    // for OUR pass — so the feed's ambient is IBL-only (probe cubes at the camera) instead of
    // the sky-miss GI from tracing an empty TLAS. That sky-miss GI was the cave floodlight of
    // 2026-08-07.
    //
    // WHY THE JOB AND NOT THE SETTINGS GATE — the light-flashing postmortem, same day. The
    // first version postfixed SettingsManager.get_IsRaytracingSupportedAndEnabled to answer
    // false under this flag. ThreadStatic was supposed to protect the player's consumers on
    // other threads — but scene ENTITY JOBS run inside Draw ON THE RENDER THREAD (every crash
    // stack this week shows Draw -> Scene.Tick -> RunJob), so during our nested Draw the
    // player's own DistanceTagManagerComponent.OnUpdateRaytracingTag consulted the lied-to
    // gate and STRIPPED RT tags from whatever entities it processed in our window; the next
    // player frame re-added them. Per-entity RT membership churning at feed cadence = rapid
    // light flashing in BOTH worlds, reported in game within minutes. The lesson is the
    // house-bug shape yet again: the lie was scoped by thread and time, but its consumers
    // write PERSISTENT per-entity state, and a per-pass flip of a latched input oscillates
    // everything downstream of the latch. Skipping the one dispatch we actually want gone
    // leaves every latch reading a constant.
    // PLAIN VOLATILE, NOT ThreadStatic — and that distinction cost a session hour. The flag
    // was [ThreadStatic] out of design-1 paranoia (when the RT GATE had many consumers on
    // many threads). Its only consumers NOW are the two job prefixes, which can only execute
    // inside a Draw on the render thread — the player's ComputeGI runs BEFORE our postfix
    // opens the bracket, so a process-global bool is already correctly scoped. ThreadStatic
    // additionally broke the WRITE: the logic side sets this via FieldInfo.SetValue, and
    // reflection writes to thread-static fields do not reliably land in the reading thread's
    // slot — 4,100 second renders with the bracket "set" and neither prefix ever saw true.
    public static volatile bool NestedRenderRtOff;

    // THE THIRD DESIGN IS DELETED, AND THE REASON IS A LAW OF THIS ENGINE, so it is recorded
    // where the next attempt will look. Scoping the RT-gate lie to ComputeGI only (a bracket
    // flag that lived here) still flashed both worlds' ambient, because AmbientLightJob's
    // DoWork is `_lightPass.Update(LightStates.get_Current()).Draw(...)` where _lightPass is
    // a LazyJobSnapshotHandler — the EXACT type the RaytracingSettings post-mortem names as
    // per-pass-unsafe. Update() rebuilds the snapshot (async PSO compile) whenever the state
    // differs from last time, and the job is SHARED, so alternating RT-on (player) / RT-off
    // (us) at feed cadence kept the snapshot in permanent rebuild and the ambient blinked
    // for everyone. THE LAW: a shared job's STATE INPUT must be constant; only per-pass
    // BUFFER CONTENT may vary. Which is why the ambient floor below is a buffer clear.
    public static volatile float FeedAmbientFloor;   // linear grey the diffuse GI buffer is cleared to for the feed

    // 0 = not attempted, 1 = floor colour built, -1 = NO USABLE CTOR (floor is black). Render-
    // thread boot-logs proved unreliable, so the verdict is a field the logic side reports.
    public static volatile int FloorColorState;

    // (renderer, batch, surfaceContext) — the renderer is needed to rebuild a panel's
    // screen material, which is how Phase 2 points a panel at our own render target.
    // ORDERLY HANDOVER ACROSS A HOT RELOAD — the leak the parks were a workaround for.
    //
    // The reload path was: load the new assembly, call Install(), Unload() the old one. The
    // outgoing assembly was never ASKED to release anything, so everything it owned — the
    // second ScreenBuffers, the DrawContextManager with its cascade set and scene-sized
    // culling buffers — became unreachable while still holding GPU memory. FeedGate.Shutdown
    // disposes all of it correctly; it simply never ran.
    //
    // That is what killed the session at 21:40 on 2026-08-02: VideoMemoryInfo CurrentUsage
    // 17,574,268,928 bytes against a ~14.8 GiB budget, then
    //     CommandListReplayException: OffscreenUIRenderer ... failed to replay
    //       SharpGenException HRESULT 0x80070057 (E_INVALIDARG) at CopyCommandList.Replay
    // after six hot reloads in one session. Rapid iterate-while-live manufactures exactly
    // the exhaustion it is trying to measure.
    //
    // TWO FLAGS, because disposal has a THREAD requirement this bootstrap cannot satisfy.
    // The render thread is the only place the gate may release GPU resources (disposing from
    // the LCD tick once raced the frame recorder and page-faulted), and this runs on the
    // bootstrap's worker thread. So the worker REQUESTS and waits; the logic performs the
    // teardown from its own render-thread hook and confirms.
    //
    // Both live here rather than in the logic assembly for the obvious reason: the logic
    // assembly is the thing being unloaded, and a handshake cannot live inside one of its
    // participants.
    public static volatile bool ReloadRequested;
    public static volatile bool ReloadQuiesced;

    public static volatile Action<object, object, object> PanelRenderHook;
    public static volatile Action<object> TickHook;

    // Fires from inside the render frame with the live SceneDrawSystem and the
    // command list the pass was given. The int identifies which patched method
    // fired: 0 = ExecuteEnvironmentProbeUpdate (the foreign-view pass we imitate),
    // 1 = a per-frame pass.
    public static volatile Action<object, object, int> SceneDrawHook;

    // Fires from inside the UI stage, right after the engine has legally copied
    // into an offscreen render target — the one point in the frame where that
    // resource is in the right state to be written.
    public static volatile Action<object[]> OffscreenUiDrawHook;

    // (sceneDrawSystem, finalLDRBuffer) — fires AFTER the engine's whole frame.
    //
    // SceneDrawSystem.Draw is the top of the pipeline: public, and it takes both its
    // destination buffer and (through that buffer's Resolution) its render size as
    // parameters. Everything else it needs comes from CoreSystems statics, and those
    // are public FIELDS rather than readonly properties — so a second render is a
    // matter of swapping them around a second call, not of finding a second renderer.
    //
    // POSTFIX, not prefix. After the engine's frame the temporal state is settled and
    // we are conceptually between frames; running ahead of it would interleave our
    // render with the one the player sees.
    //
    // Draw has ZERO managed callers — it is invoked from engine glue — which is what
    // makes this a usable site at all. The probe hook could never host a second Draw
    // because it already sits inside one.
    //
    // Re-entrancy is the logic side's problem: our own nested Draw will fire this hook
    // again, and the handler must return immediately when it does.
    public static volatile Action<object, object> WholeSceneHook;

    // (sceneDrawSystem, finalLDRBuffer) — fires at the TOP of Draw, BEFORE the player's
    // frame is recorded. The start-of-frame submission position.
    //
    // Same patch site as WholeSceneHook, opposite end. Recording our render here puts our
    // GPU work ahead of the player's in the queue, so it EXECUTES while the CPU is still
    // recording the player's frame — instead of sitting between the player's work and the
    // present copy, where it delays the swap by its full duration.
    //
    // Safe because Draw's prefix is downstream of ALL of DrawInternal's frame prep:
    // FinalizeResources, UpdateImmediateHeap, CreateDirectCommandList, RefreshTables,
    // Settings.OnBeginDraw, CommonResources.OnBeginDraw, ScreenBuffers.Update and
    // DrawContextManager.OnBeginDraw have all run by the time we get here (verified
    // against DrawInternal's call order — Draw is the 86th call, that prep is calls
    // 13-73). Everything a nested Draw consumes is live, same frame, same frame span.
    // That is what makes this cheaper in risk than the post-present position, which
    // crosses the boundary where spans close and transients recycle.
    //
    // Re-entrancy: our own nested Draw fires this too, so the handler must return
    // immediately when it does — same rule as WholeSceneHook.
    public static volatile Action<object, object> WholeSceneEarlyHook;

    // TRUE while the logic side is inside its nested second Draw. Read by the log-only
    // probes (CopyJob / ScreenBuffers.InitializeBuffers) so their lines say which render
    // an engine call fired in. Absent/null on an old logic assembly — probes then log
    // inOurRender=false, which is still useful.
    public static volatile Func<bool> InOurRenderHook;

    // Returns TRUE to skip a Draw sub-stage entirely. The int identifies which — see
    // RttPlugin.SkippableStages.
    //
    // Settings flags cannot reach every stage. ExecuteAccelerationStructuresBuilding is
    // the case that forced this: it is called unconditionally at the top of Draw and
    // checks only EnableGPUParallelization, so clearing RaytracingSettings.Enabled never
    // stopped it — we rebuilt the raytracing acceleration structures on every second
    // render, and RayTracingSceneManager.CreateTLAS is camera-dependent and world-space
    // shared.
    //
    // A prefix returning false skips the original outright, which is the only lever that
    // reaches a stage the settings do not gate.
    public static volatile Func<int, bool> SkipStageHook;

    // ---- GPU PROFILER FORWARDER (task #65) --------------------------------------------
    //
    // The engine's GPUProfiler raises OnWatchesReady when a frame's GPU timestamp reports
    // have resolved. The LOGIC side wants that signal, but a logic delegate subscribed
    // directly to an engine event outlives its collectible assembly across hot reloads —
    // so the BOOTSTRAP owns the one subscription and forwards through this field, which
    // each logic install overwrites. Same pattern as every other hook on this bridge.
    public static volatile Action GpuWatchesReadyHook;

    // ---- PER-STAGE TIMING (perf sprint 2026-08-07, task #63) --------------------------
    //
    // Wall ticks and run counts per skippable-stage id, accumulated ONLY while the logic
    // says we are inside our nested Draw (InOurRenderHook). The logic side reads these
    // reflectively on its 15 s report clock and prints deltas — reflective so an old
    // logic assembly ignores them and an old bootstrap degrades to "no table" with the
    // logic saying so once. Index = stage id; slot 33 = everything-else remainder unused
    // for now. Written from the render thread (and job threads for the job-typed stages);
    // Interlocked keeps cross-thread sums honest without a lock in a per-stage hot path.
    //
    // KNOWN ATTRIBUTION NOISE, accepted for a diagnostic: InOurRenderHook is scoped by
    // TIME not thread, so a player-frame job overlapping our ~10% duty cycle can donate
    // its stage time to our table. Job-typed stages (17/22/23/24) are mostly skipped in
    // our pass anyway; read their rows with that caveat.
    public static readonly long[] StageTicks = new long[34];
    public static readonly int[] StageRuns = new int[34];

    // ---- MANAGED WORLD AREA REGISTRATIONS (goal 10 tier 2, 2026-08-01) -----------------
    //
    // (area, sessionComponent) pairs captured from ManagedWorldArea.OnRegistered — the
    // SERVER-side objects, which is the entire point. The client's mirror component
    // (ClientManagedWorldAreaSessionComponent) exposes the same area list, but calling
    // TryLoad on a client-scene area throws KeyNotFoundException from
    // Scene.FinishBefore<SpawnSyncPoint> and CRASHED THE GAME TWICE on 2026-08-01:
    // loading is a server concern and only the server scene registers the spawn sync
    // point. OnRegistered hands us the server component as a parameter, so whatever is
    // in this list is by construction from the scene where TryLoad is legal.
    //
    // Lives in the BOOTSTRAP for two reasons: registrations fire DURING world load,
    // before the logic assembly has attached its hooks, so a forwarding hook would miss
    // them all; and logic statics die on every hot reload while these references must
    // not. Entries are (area, session) object pairs, typed object for the same reason
    // ParkedProbeManagers is. Appended only — a world reload appends a new batch with a
    // NEW session component, and the logic side keys on the LAST entry's session to
    // ignore stale worlds. The lock covers the racy append-during-load window.
    public static readonly List<object[]> ManagedAreaRegistrations = new();
    public static readonly object ManagedAreaLock = new();

    // ---- PER-BODY CLIPMAP CAMERA (goal 10, the terrain fix) ---------------------------
    //
    // (voxelRenderComponent, boxedWorldTransform) -> replacement boxed WorldTransform, or
    // null to leave the engine's choice alone.
    //
    // VoxelRenderUpdateSessionComponent.UpdateClipmaps() reads ONE global camera transform
    // per frame and calls UpdateClipmap(body, camera, loading) for EVERY voxel body — so
    // terrain meshes are built around the player and nowhere else. That is the entire
    // reason a remote feed sees a smooth blob where the player sees boulders, proven by
    // positive control on 2026-08-01.
    //
    // But the camera is a PER-CALL ARGUMENT, not a global the callee re-reads. When the
    // player and the feed camera are near DIFFERENT bodies (player on Kemik, camera on
    // Verdure), each body can be driven by whichever viewer is actually near it with no
    // contention at all — this is emphatically NOT the single-slot tug-of-war that swapping
    // RenderSettings.CameraTransform would be, because each clipmap still gets exactly one
    // camera; we only change WHICH one, per body.
    //
    // Typed as object on purpose: the bootstrap stays ignorant of engine types and the
    // logic assembly (reloadable) owns every decision. The transform is a STRUCT, so it
    // arrives boxed and the logic returns a modified box.
    public static volatile Func<object, object, object> ClipmapCameraHook;

    // The VoxelRenderUpdateSessionComponent that owns the clipmap update loop, captured from
    // the patch's __instance. It is NOT reachable from the session-components entity (the
    // logic looked and it is genuinely absent from that roster), and it owns _lodDistances —
    // the 16-slot LODData array whose sharing across bodies is the current prime suspect for
    // the mid-LOD plateau. Handing it over here costs nothing and saves guessing at its home.
    public static volatile object VoxelUpdateComponent;

    // ---- THE SIM-PUMP SEAT (goal 10, the server half) ---------------------------------
    //
    // Everything the trigger census proved wants a presence entity in the SERVER scene:
    // flora sectors, managed world areas and voxel sectors all constrain on
    // DynamicTag + WorldTransform + BoundingBoxData (census 2026-08-01, constraints read
    // from the live TriggerArgs). But structural scene mutation is only safe on the thread
    // that pumps that scene — the TryLoad freeze (FinishBefore<SpawnSyncPoint> from OUR
    // thread) is the standing proof of what happens otherwise.
    //
    // So the bootstrap provides a SEAT rather than an action: a Harmony prefix on a method
    // that provably runs on every scene's own pump each frame hands the logic a callback
    // IN that seat, and the logic decides per-invocation whether this is the scene it
    // wants (it probes the job tables for SpawnSyncPoint, same discriminator as ever).
    // The bootstrap stays ignorant of what the logic does there, exactly like every other
    // hook on this bridge.
    //
    // (component) -> void, invoked at the top of SpatialTriggerSystemSessionComponent's
    // per-frame pending-trigger processing, on that component's scene's pump thread.
    public static volatile Action<object> SimPumpHook;

    // ---- SAVE HOLD: no marker of ours may exist while a save collects the world ------
    //
    // The presence markers are RAW component-assembled entities (no definition, no
    // ObjectBuilder). Whether the save serializer includes such entities could not be
    // proven either way — VR3B stores type IDs, not names, so save-file inspection is
    // blind, and the one direct observation (a save completing with both markers alive)
    // only shows they do not CRASH the writer. The user asked for certainty, and the only
    // honest certainty is structural: when a save begins, the markers are despawned before
    // the next sim frame and stay gone for 8 s, so nothing of ours can be in the set the
    // save walks. They recreate themselves automatically afterwards (the drive loop
    // creates on null), at the cost of a brief presence gap around each save.
    public static long SaveHoldUntilMs;

    // ---- THE INPUT SEAT: raw key state for manual camera control (2026-08-03) ---------
    //
    // GameInputProcessorComponent.ProcessInput() is the engine's own per-frame input pump,
    // so a prefix there runs on the right thread at the right moment with the device state
    // already current. We only READ: no input is consumed, cancelled or re-mapped, so the
    // seat, the grid and the UI keep every key they would otherwise get. If the camera ever
    // needs to STEAL a binding that has to be a separate, deliberate change.
    //
    // WHY A DISCOVERY STEP RATHER THAN A KEY TABLE. The UI layer maps actions to
    // Avalonia.Input.Key (verified in IL: 23=Left, 24=Up, 25=Right, 26=Down), but the DEVICE
    // layer takes InputId(index, InputType, deviceClass) and nothing states that InputId.Index
    // is the same number. InputKeyProbe publishes whatever is actually held, with the Avalonia
    // name for each index when one matches, so ONE key press settles the encoding instead of
    // a guessed table that silently binds the wrong keys.
    //
    // Held keys are published as raw indices; the logic side owns all interpretation.
    public static volatile int InputHeldCount;
    public static readonly int[] InputHeld = new int[16];
    public static volatile float MouseDX, MouseDY, MouseWheel;

    // Mouse discovery: which InputIds change when the mouse moves or the wheel turns, and
    // what analog value each carries. Set MouseProbeWanted=false once the encoding is bound.
    public static volatile bool MouseProbeWanted = true;
    public static volatile int MouseChangedCount;
    public static readonly int[] MouseChanged = new int[16];
    public static readonly float[] MouseAnalog = new float[16];

    // Running total of wheel notches. The consumer stores its own last-seen value and acts on
    // the difference — see the accumulator note at the publish site.
    public static volatile float MouseWheelAccum;
    public static volatile bool InputSeatAlive;
    public static volatile Action InputProbeHook;

    // ---- WHICH INPUT LAYERS ARE ACTIVE (seat gating, freelook gating) -----------------
    //
    // GameInputProcessorComponent exposes a PUBLIC ListReader<InputContext> ActiveContexts,
    // and our ProcessInput prefix already holds that very instance — so this needs no extra
    // Harmony patch. Each InputContext carries a Layer string; the engine's own layer table
    // (GameData/.../Input/GameInputLayers.def) names them "Ship Movement", "Character
    // Movement", "Camera FreeLook", "Camera Controller", and so on.
    //
    // PUBLISHED AS RAW NAMES, DECIDED ON THE LOGIC SIDE. Same discipline as InputHeld: the
    // bootstrap reports what the engine says, and every judgement about what counts as
    // "seated" lives in config where it can be retuned without a rebuild. That matters here
    // more than usual, because the user's seat sits on a STATIC grid that the game does not
    // treat as a vehicle — so "Ship Movement" may well never activate, and the layer that
    // does have to be read off a live log rather than assumed.
    //
    // LayersReadable is separate from the names on purpose: an empty list because the reader
    // is blind and an empty list because nothing is active are opposite facts, and gating a
    // feature off the first one would look exactly like broken controls.
    public static volatile bool InputLayersReadable;
    public static volatile string InputLayers = "";

    // ---- FLORA SECTOR CAMERA (goal 10, the client-visibility half) --------------------
    //
    // FloraSectorEntityComponent.UpdateCameraPosition and .UpdateVisibility both read
    // CoreSystems.Settings.RenderView.CameraPosition — ONE global camera — express it in
    // the sector root's frame, and hand it to InstanceSparseOctree.UpdateCamera /
    // .UpdateVisibility. The octree culls by distance (_maxCullingDistance,
    // _minDistanceToOctree, _isVisible), so with the player 3,912 km away every flora
    // sector near a remote feed camera is marked INVISIBLE. The content exists; the
    // renderer is told to hide it. Same single-viewer disease as the clipmap.
    //
    // The hook fires as a POSTFIX, deliberately: the engine's own update runs first and
    // completely (the player can never lose flora), then the logic re-points the octree of
    // sectors it claims at the feed camera. Last write wins, nothing is suppressed, and a
    // fault in our half leaves the engine's result standing.
    //
    // PREFIX, NOT POSTFIX, and the difference is the whole feature. InstanceSparseOctree
    // .UpdateCamera early-outs on `coords == _cameraCoords`, so a postfix that overwrites
    // after the engine leaves _cameraCoords flipping between the player and the feed every
    // frame: UpdateSubdivision() re-runs forever, cells never settle, and the flora that
    // does appear is sparse (observed 2026-08-02 — thin foliage, no grass, 1.5M claims in
    // 15 s). Suppressing the engine's call for sectors we claim lets the octree settle on
    // ONE camera, which is what it is built to expect.
    //
    // (component, boxedArgs, isVisibilityJob) -> true if the logic handled this sector and
    // the original must be skipped. Typed as object so the bootstrap stays ignorant of
    // VRage.Render12 types.
    public static volatile Func<object, object[], bool, bool> FloraCameraHook;

    // ---- THE NEAREST-VIEWER DISTANCE (2026-08-02) -------------------------------------
    //
    // THE ONE NUMBER BEHIND THREE SYMPTOMS. RenderUtilities.CalculateDistanceToCamera reads
    // CoreSystems.Settings.RenderView.CameraPosition — the single global camera — and returns
    // the distance from it to an entity's bounding box. DistanceTagManagerComponent
    // .OnUpdateDistanceToCamera caches that ONE float per entity as DistanceRangeData, and a
    // whole family of jobs then reads nothing but that cached number:
    //
    //     OnUpdateRootEntityStreamingTag  -> ResourceStreamingComponent.StreamingTag
    //                                        (threshold RootResourceStreamingComponent
    //                                        .RootStreamingDistance, which is 200 m)
    //     OnUpdateImpostorTag             -> ImpostorComponent Near/FarDistanceTag
    //                                        (threshold ImpostorSettings.SwapDistance)
    //     OnUpdateShadowTrackingTag       -> ShadowSettings.LocalLights.DirtyAreaTracking...
    //     OnUpdateRaytracingTag           -> RaytracingSettings.Scene Near/FarDistance
    //     OnUpdateTag                     -> geometry-dirty tags
    //
    // So a remote feed camera 3,906 km from the player puts EVERY entity it looks at in the
    // farthest distance bucket, no matter that our camera is standing on top of them. That is
    // one mechanism producing the whole remaining fidelity gap at once: trees resolving low
    // up close, foliage thinner than local, and grass — whose model arrives solely through
    // the streaming path (GrassEntityComponent.UpdateModel(handle, materials, lod)) — never
    // appearing at all.
    //
    // THE FIX IS THE ENGINE'S OWN SEMANTIC, not a duplicate of it. "Distance to the camera"
    // with several viewers means distance to the NEAREST one; the engine already spells that
    // out elsewhere (ManagedTexturePrioritizerComponent/ClosestDistanceCollector). The hook
    // returns min(engineAnswer, ourAnswer), which is monotone: a distance can only get
    // SMALLER, so no entity the player is near can ever be demoted by us. Overlap between
    // the two viewers' bubbles is not a conflict — min() is idempotent.
    //
    // (x, y, z of the entity's world position, the engine's own answer) -> the answer to use.
    // Primitives only: the logic assembly never sees an engine type and this stays allocation
    // free on a path that runs over every root entity in the render scene.
    public static volatile Func<double, double, double, float, float> ViewerDistanceHook;

    // ---- TEXTURE MIP SELECTION, GIVEN THE FEED CAMERA A VOTE (2026-08-02) --------------
    //
    // WHY THIS IS A SECOND HOOK AND NOT COVERED BY ViewerDistanceHook ABOVE. There are TWO
    // independent distance paths and conflating them wasted a session:
    //
    //   RenderUtilities.CalculateDistanceToCamera  -> StreamingTag (which streaming BUCKET
    //                                                 an entity is in), impostor swap,
    //                                                 shadow tracking, RT near/far tags.
    //                                                 <- ViewerDistanceHook patches THIS.
    //   ManagedTexturePrioritizerComponent
    //     .OnCollectStandardsRoot -> CollectStandards -> ClosestDistanceCollector
    //                                              -> WHICH MIP IS RESIDENT.
    //                                                 <- nothing patched this. Hence the
    //                                                 "flat, almost textureless" feed.
    //
    // The second path reads Settings.RenderView.CameraPosition directly, so it always saw
    // the PLAYER. And it runs from a DCS job stub (ApplyPriorities_InvocationStub) on the
    // scene's schedule, NOT inside our nested Draw — so per-pass settings scoping is inert
    // here and a hook is the only route. Same shape as the flora spawn radius.
    //
    // SAFE BY CONSTRUCTION, for the same reason ViewerDistanceHook is. CollectStandards uses
    // the camera position for a DISTANCE AND NOTHING ELSE (verified in IL: either
    // boundingBox.Distance(cameraPositionRS), or (cameraPositionRS - relativePosition)
    // .Length()), then multiplies by StandardResourceDistanceMultiplier and feeds a
    // ClosestDistanceCollector. Moving the camera closer can only DEMAND MORE resolution; it
    // can never demote a texture the player needs. We therefore only ever substitute our
    // camera when it is genuinely nearer BY THE ENGINE'S OWN METRIC — see the prefix.
    //
    // The delta is carried as three plain floats rather than a delegate returning a vector:
    // this runs per root entity per collection pass, and the logic assembly must never see
    // an engine type. A torn read costs one entity one slightly wrong distance for one pass,
    // which is invisible in a texture LOD and not worth a lock.
    public static volatile bool TextureCameraActive;

    // ---- THE ENGAGE MUST BE CYCLE-ALIGNED --------------------------------------------
    //
    // TWO ACTIVATION CRASHES (21:18 and 21:52 on 2026-08-04, 2 of 3 feed activations),
    // both IndexOutOfRange in ManagedTexturePrioritizerComponent.CollectStandardMaterials
    // — 173 ms after FEED GATE: ACTIVE the second time — while the SAME build engaged
    // cleanly at 21:34 and then ran for 13 minutes. An intermittent fault pinned to the
    // moment of engagement, surviving the atomic-eye fix, is a RACE ON THE FLIP ITSELF:
    // the prioritiser builds its per-cycle helper state from one camera, and flipping
    // TextureCameraActive mid-cycle hands the rest of that cycle a different frame of
    // reference than the helper's lists were sized and ordered for.
    //
    // So the logic no longer writes TextureCameraActive directly. It writes ARMED, and the
    // cycle-start prefix (PrepareStandardMaterials) copies Armed -> Active — the one moment
    // when no helper state exists yet, on the thread that is about to build it. Mid-cycle
    // the flag is now constant by construction.
    //
    // COMPAT, stated precisely: THIS bootstrap copies Armed -> Active every cycle, so a
    // logic DLL that writes only Active would be stomped a frame later — the logic ships
    // with this change and writes Armed. The direction that can actually occur in the field
    // is NEW logic on an OLD bootstrap (hot reload before a restart): the logic detects the
    // missing Armed field and falls back to writing Active directly — the old behaviour,
    // with the old activation-race risk, and it says so in the log rather than silently
    // losing the feature.
    public static volatile bool TextureCameraArmed;
    public static float TextureCameraDX, TextureCameraDY, TextureCameraDZ;

    // ---- IS THE OVERRIDE RATE SPATIAL OR TEMPORAL? (2026-08-03) -----------------------
    //
    // THE QUESTION THIS EXISTS TO SETTLE. The override fires on ~30% of calls. That is
    // either SPATIAL (a stable set of near-feed entities, every pass, and the other 70% are
    // simply farther) or TEMPORAL (the same entity overridden on one pass and not the next).
    // Only the second can flash: an entity whose demanded mip alternates makes the streaming
    // system load and drop the same texture forever, and a texture that is not resident is
    // not drawn. The aggregate override count cannot tell these apart, which is why three
    // theories were argued from it and none survived.
    //
    // IDENTITY WITHOUT AN ENTITY. The prefix is handed no id — only geometry. relativePosition
    // (__3) is stable per entity within a frame and distinct between entities, so its bits
    // make a serviceable key. Collisions cost a false alternation on a shared slot, so read
    // the number as an UPPER bound and treat "near zero" as the trustworthy direction.
    //
    // A FIXED TABLE, NEVER GROWN. This runs per root entity per collection pass on scene job
    // threads: no allocation, no lock, no resize. Races cost a sample, which is the same
    // trade the NearestSeen min already makes. Power of two so the mask is an AND.
    internal const int TexSlots = 1 << 14;
    public static long TextureCameraDecisions, TextureCameraAlternations;

    // THE BASE-INSTABILITY CHECK. Our nested Draw parks OUR camera in the global RenderView,
    // and TextureCamera.cs asserts the collector always sees the PLAYER's. Those cannot both
    // be true. If the handed base takes two clusters ~|delta| apart, the prefix is adding a
    // player-to-feed offset to a base that is already the feed. Tracked as a span on one axis
    // because that is enough to separate two clusters and costs two compares.
    public static float TextureCameraBaseMinX = float.MaxValue, TextureCameraBaseMaxX = float.MinValue;

    // ---- THE HYSTERESIS THAT STOPS THE CHATTER (2026-08-03) ---------------------------
    //
    // MEASURED FIRST, THEN FIXED: 4.2% of repeat decisions alternated — ~4000 entities a
    // second changing which camera picks their mip, which is a texture loading and dropping
    // at exactly the cadence of the reported flashing.
    //
    // ONE-SIDED ON PURPOSE, AND THE ASYMMETRY IS A SAFETY CONSTRAINT, NOT A PREFERENCE.
    // Ordinary hysteresis would also make us STICKY past dPlayer, and that would write a
    // LARGER distance than the engine's own — demoting a texture the player needs, the one
    // direction this prefix must never move. So: entering costs a clear margin, leaving
    // happens the instant our camera stops being strictly nearer. Chatter at the boundary
    // dies because re-entry is expensive; the safety invariant is untouched.
    //
    // LIVE, so the margin can be tuned without another restart: the bootstrap cannot be hot
    // reloaded, and paying a world load per tuning step is how a whole evening disappears.
    // 1.0 disables the hysteresis and restores the old strict comparison.
    public static volatile float TextureCameraEnterRatio = 0.85f;

    // ---- COLLECTION-SET DROPOUT: THE THING THE ALTERNATION COUNTER CANNOT SEE ---------
    //
    // WHY THE PREVIOUS INSTRUMENT WAS BLIND. ClosestDistanceCollector is Prepare()d — reset —
    // every cycle, and each material's demanded mip is rebuilt from whatever entities were
    // OFFERED to CollectStandards that cycle. The alternation counter only ever compared
    // entities that WERE offered. An entity that silently drops out of the collection set for
    // a cycle registers as nothing at all, yet its material loses our near-distance vote,
    // falls back to the player's far distance, and its texture unloads. Foliage is alpha-
    // tested, so a texture that unloads is not "blurry", it is GONE. That is a flash no
    // visibility flag and no alternation count can show — which fits the measured facts:
    // cutting alternation 65% changed nothing visible.
    //
    // A DROPOUT is an entity seen in cycle N and again in cycle N+k for k>1: it went missing
    // and came back. Counting per window gives the rate directly.
    public static long TextureCameraCycle, TextureCameraDropouts, TextureCameraContinuous;

    // ---- TEXTURE TIER CHURN: THE CAUSAL QUANTITY, NOT A PROXY FOR IT -------------------
    //
    // WHY THIS AND NOT ANOTHER DEMAND-SIDE COUNTER. Six demand-side theories died measuring
    // clean while the flashing continued: subsector visibility, batch visibility, bucket
    // membership, mip-decision alternation, working-set capacity, collection-set dropout.
    // The whole CPU-side REQUEST path is provably stable. So stop measuring what we ask for
    // and measure what actually happens: FileTexture.RequestUpdateTier(tier) is the call that
    // moves a texture's resident mip up or down.
    //
    // A REVERSAL is the signature to look for — the same texture going up, then down, then up.
    // Foliage is alpha-tested, so a texture dropping tier does not blur, it vanishes. A high
    // reversal rate IS the flashing, measured at the layer where it physically happens.
    // A near-zero rate exonerates streaming entirely and points at the draw.
    //
    // Identity is the instance's reference hash, so no engine type has to be named. Ungated by
    // feedTextureCamera on purpose: the OFF case is the control, and a counter that only runs
    // in one arm cannot compare them.
    // ---- THE DISTANCE LATCH: HYSTERESIS ON THE RIGHT QUANTITY -------------------------
    //
    // MEASURED CAUSE (2026-08-03). Tier requests per 15 s window: ~9000-16500 with the
    // override ON, 172-532 with it OFF — a 25-30x amplification that is ours. Up and down are
    // balanced and ~88% of movements are direction REVERSALS, which is oscillation, not a
    // world streaming in and settling.
    //
    // WHY THE FIRST HYSTERESIS ATTEMPT FAILED, and it is worth remembering: it damped WHICH
    // CAMERA WINS — a boolean — and cut that alternation 4.2% -> 1.4% with no visible change.
    // The churn is driven by the DISTANCE VALUE the winning camera produces. Our camera
    // ORBITS, so every entity's distance slides continuously, target tiers cross boundaries,
    // and thousands of textures re-tier every second. No amount of damping on the boolean
    // touches that.
    //
    // SO LATCH THE VALUE. Each entity keeps the distance it was last presented at, and only
    // moves to a new one when the true distance departs by more than this fraction. Tiers
    // then change in deliberate steps instead of sliding with the orbit.
    //
    // THE SAFETY INVARIANT IS UNCHANGED AND IS RE-CHECKED AGAINST THE REAL METRIC: the
    // substituted position is only applied when the engine's OWN box distance to it is
    // strictly smaller than the engine's own box distance to the camera it was handed. A
    // latched distance can never demote a texture the player needs, because the comparison
    // that gates it is measured after the latch is applied, not before.
    //
    // 0 disables the latch and restores the raw orbiting distance.
    public static volatile float TextureCameraDistanceStep = 0.20f;

    // ---- THE SATURATION FLOOR: THE ACTUAL FIX ----------------------------------------
    //
    // DECODED FROM ApplyStandardMaterials, not guessed:
    //
    //     texelRatio = (GetPixelsPerSurfaceMeterBase() / distance) / Streaming.DefaultTexelDensity
    //     priority   = MathF.Min(texelRatio, 2.0f)                      <- SATURATES
    //     mip        = clamp(log2(1 / texelRatio), SkipMipLevels, 255)  <- 0 once ratio >= 1
    //
    // So with P = pixelsPerSurfaceMeterBase and D = DefaultTexelDensity:
    //
    //     distance >= P/D    -> reduced resolution, graded priority
    //     P/2D .. P/D        -> FULL resolution AND graded priority   <- the band we want
    //     distance <  P/2D   -> full resolution, priority PINNED AT 2.0, tied with every
    //                           other saturated texture in the game
    //
    // PRESENTING CLOSER THAN P/2D BUYS NOTHING. The mip is already at full resolution from
    // P/D inward, so the extra closeness cannot improve the image — all it does is collapse
    // our priority into a tie with hundreds of other textures. When the shared pool fills,
    // which of those tied entries survives is decided by tie-breaking, and it can differ
    // every cycle: textures form, drop, and re-form. That is the flashing.
    //
    // This also explains the grace period Pete reported: for the first 5-10 s after a load
    // there is headroom, every saturated texture fits, and nothing churns. The moment the
    // pool is full, the tie-break starts reshuffling.
    //
    // AND IT EXPLAINS WHY BOTH HYSTERESIS ATTEMPTS FAILED. Above the clamp the distance VALUE
    // IS DISCARDED. Stabilising an input that gets thrown away cannot change the output —
    // which is exactly what the measurements said: 100% latch hold, churn barely moved.
    //
    // SAFE BY CONSTRUCTION: this only ever moves our presented distance FARTHER, which makes
    // our camera LESS likely to win the closest-wins test. It cannot demote a player texture.
    //
    // 0 disables the floor.
    public static volatile float TextureCameraMinDist;
    public static long FloorApplied, FloorNotNeeded;

    // ---- THE CEILING: the UNTESTED direction of the priority hypothesis ---------------
    //
    // priority = (pixelsPerSurfaceMeterBase / distance) / DefaultTexelDensity, so priority
    // rises as the presented distance FALLS. Our foliage at 900 m scores ~0.0026 against the
    // player's nearby content at ~0.46 — bottom of a single global ordering, sitting on the
    // eviction cut.
    //
    // WHAT WAS ACTUALLY TESTED, and why it proved less than I said: only the FLOOR, which
    // pushes distance UP and priority DOWN. Churn did not move and I called the hypothesis
    // dead. That was an overclaim — a null result in the direction that makes things worse is
    // weak evidence. Had the theory been right, pushing further down should have driven our
    // textures clean BELOW the cut (stay evicted, churn falls, foliage goes); instead they
    // stayed present-but-degraded and still churned, i.e. never left the contested zone.
    //
    // This clamps the presented distance from ABOVE, which RAISES priority — the direction
    // that would actually fix it. 900 m -> 20 m is roughly 44x more priority.
    //
    // *** VRAM WARNING — WARN, NEVER ACT AUTOMATICALLY. *** Raising priority also drops the
    // demanded mip, and a mip step is ~4x the bytes for a 2D texture. This machine is
    // GPU- and VRAM-bound before the mod adds anything. Set this deliberately, watch the
    // budget, and treat a low value as an experiment rather than a setting.
    //
    // 0 disables the ceiling.
    public static volatile float TextureCameraMaxDist;
    public static long CeilingApplied;

    // ---- THE DELTA MUST BE ROTATED INTO EACH ENTITY'S OWN FRAME -----------------------
    //
    // THE BUG THIS FIXES, read straight out of OnCollectStandardsRoot's IL:
    //
    //     V_0 = Settings.RenderView.CameraPosition                 // world
    //     V_3 = V_0 - entity.WorldTransform.Position               // world-space delta
    //     V_1 = Inverse(entity.Orientation) * (Vector3)V_3         // ENTITY-LOCAL, ROTATED
    //           CollectStandards(ref V_1, ...)
    //
    // So cameraPositionRS is NOT world space and NOT a translation of world space — it is the
    // camera expressed in each entity's own rotated frame. This prefix has been adding a
    // WORLD-space delta to it since the feature was written, which is only correct when the
    // entity's orientation is identity. On a planet every tree and rock is oriented to its
    // local terrain normal, so the delta was mis-rotated DIFFERENTLY FOR EVERY ENTITY.
    //
    // The old comment claimed "render space is world space minus an origin — a pure
    // translation — so the vector BETWEEN two points is identical in both". True of render
    // space; false of this parameter. That single wrong assumption is why the substituted
    // camera landed in an arbitrary direction per entity, and why the demanded mips moved
    // unpredictably as the player's camera moved.
    //
    // It also fits what killed every other theory: clamping the DISTANCE changed the picture
    // but not the churn (5000x range, no effect), because the DIRECTION stayed wrong and the
    // direction is what is unstable. Turning the override off changed everything, because
    // that removes the mis-rotated substitution entirely.
    //
    // NO SAFETY CHANGE: closest-wins still compares real box distances, so the player could
    // never be demoted — the bug made our substitution arbitrary, not dangerous.
    //
    // ThreadStatic because CollectStandards runs on scene job threads and each thread walks
    // its own entities; this is the same argument-derived bracket pattern used for the
    // culling-view scope, not an ambient flag (that mistake whited out the feed once).
    [ThreadStatic] public static float RotDX, RotDY, RotDZ;
    [ThreadStatic] public static bool RotValid;
    public static long RotHits, RotMisses;

    // ---- THE FEED EYE IN WORLD SPACE: the stable replacement for the delta ------------
    //
    // WHY THE DELTA HAD TO GO. `ours = handedCamera + delta` makes our answer depend on WHICH
    // camera the collector was handed — and our own nested draw parks OUR camera in
    // Settings.RenderView, which is exactly what OnCollectStandardsRoot reads to build every
    // entity's frame. Cycles that run inside our draw therefore see a different base from
    // those outside, and EVERY material in a cycle gets the same treatment.
    //
    // THAT IS THE LOCKSTEP THE OFFENDER LIST SHOWED: 810 foliage textures — OliveTree
    // impostors, Grass01..13 — all sitting at 259-260 reversals. Independent eviction at a
    // budget boundary would spread those counts out; identical counts mean the whole set is
    // promoted and demoted together, which is a global switch, not a competition.
    //
    // It also explains why every stability counter stayed quiet: they tracked the DECISION BIT
    // (does our camera win), and our camera can win in both regimes. The bit holds still while
    // the underlying distance swings by the whole player-to-feed gap.
    //
    // THE FIX IS TO STOP BEING RELATIVE. OnCollectStandardsRoot builds its frame from WORLD
    // coordinates, so we can supply the feed eye in world space and compute
    // Inverse(orientation) * (eyeWorld - entityPosition) outright. No base, no delta, no
    // render-space assumption. If a cycle does run inside our draw, the engine already holds
    // our camera and the closest-wins test simply declines to substitute — the regime flip
    // stops mattering rather than having to be prevented.
    public static volatile bool TexEyeValid;
    public static double TexEyeX, TexEyeY, TexEyeZ;

    // ---- THE EYE MUST BE PUBLISHED ATOMICALLY, AND THESE THREE DOUBLES ARE NOT -----------
    //
    // TWO CONFIRMED CTDs, 2026-08-04, both from torn reads of a fast-moving camera position:
    //
    //   IndexOutOfRangeException at PooledList.get_Item
    //     <- ManagedTexturePrioritizerComponent.CollectStandardMaterials   (world load)
    //   IndexOutOfRangeException at Dictionary.GetValueRefOrAddDefault
    //     <- VoxelPhysicsComponent.MaterializeChunk <- SectoredTrigger.UpdateEntity  (in space)
    //
    // TexEyeX/Y/Z are three SEPARATE fields, written one at a time on the logic thread and
    // read on engine JOB threads. A reader can take X from the new position and Y/Z from the
    // old — a coordinate that never existed. A double is not even guaranteed atomic on its
    // own. That garbage eye becomes a garbage distance, which becomes an out-of-range mip
    // index; on the marker side it becomes an absurd chunk coordinate.
    //
    // WHY IT ONLY BIT NOW, which is the part worth remembering: on the orbit the camera crept,
    // so a torn read mixed two nearly identical positions and the error was metres. Under
    // manual flight at 750 m/s — and a 277 km jump when presence started following the camera
    // — a torn read is hundreds of kilometres wrong. The bug was always there; the input had
    // to start varying fast before it could kill anything. Same shape as the fire-and-forget
    // preload: correct-looking code that only fails once a constant becomes a variable.
    //
    // THE FIX IS A SNAPSHOT PUBLISHED BY REFERENCE. Reference assignment IS atomic in .NET,
    // so a reader either sees the whole old position or the whole new one, never a mixture.
    // The three loose doubles stay for compatibility with an older logic DLL across a hot
    // reload; readers prefer the snapshot and fall back only if it is absent.
    public sealed class EyeSnapshot
    {
        public readonly double X, Y, Z;
        public EyeSnapshot(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    public static volatile EyeSnapshot TexEye;

    public static long TierCalls, TierUp, TierDown, TierReversals, TierRepeat;
    public static long LatchHeld, LatchMoved;
    internal static readonly int[] TierSlotKey = new int[TexSlots];
    internal static readonly int[] TierSlotTier = new int[TexSlots];
    internal static readonly sbyte[] TierSlotDir = new sbyte[TexSlots];

    // ---- WHICH TEXTURES ARE THRASHING -------------------------------------------------
    //
    // Eight mechanisms have now been proposed and measured against aggregate counts, and each
    // one died. The counts say ~1000 tier movements a second, ~88% of them reversals, 20-60x
    // the control arm — but never WHAT is moving. Naming the worst offenders answers in one
    // line what another round of theorising would not: whether these are foliage materials at
    // all, or terrain, or something nobody has considered.
    //
    // WeakReference, not a strong one: holding ~16k engine textures alive would change the
    // very eviction behaviour being measured, which would be an instrument that breaks its
    // own experiment.
    internal static readonly WeakReference[] TierSlotObj = new WeakReference[TexSlots];
    internal static readonly int[] TierSlotRev = new int[TexSlots];

    // ---- THE PER-MATERIAL FIGHT: the granularity everything else missed --------------
    //
    // Pete's framing, and it is correct: we force the engine to evaluate the feed's
    // neighbourhood as if it were near the player, so the two cameras COMPETE for the same
    // entry. ClosestDistanceCollector keeps the MINIMUM distance PER MATERIAL. Common foliage
    // materials exist both near the player and near the feed, so each cycle the winner is
    // whichever camera is nearer to its own nearest instance — and as the player moves and
    // our camera orbits, both the winner and the winning value change.
    //
    // WHY THIS WAS NEVER MEASURED. Every stability counter tonight keyed on ENTITY (via
    // relativePosition). The collector keys on MATERIAL. Materials are shared across thousands
    // of entities, so per-entity decisions can be perfectly stable while the per-material
    // WINNER flips every cycle. The 4.2%, the 1.4%, the zero dropouts — all measured at a
    // granularity that cannot see this. That is a strong candidate for why every fix aimed at
    // per-entity stability changed nothing.
    //
    // MinValue(Int32 id, Single distance) takes only primitives, so it hooks without naming a
    // single engine type. Tracked per material id: the winning distance, and how far it moves
    // between cycles. A SWING is a change large enough to cross a mip boundary (a factor of
    // ~2), which is exactly a load/unload — a pop.

    // ---- GRASS WITHOUT HiZ, FOR OUR PASS ONLY (2026-08-02) ----------------------------
    //
    // WHY THIS RATHER THAN THE SETTING. Clearing HZBOSettings.MainViewEnabled around our
    // render whited out the feed AND made the PLAYER'S world flicker: six render paths read
    // IsOcclusionCullingAllowed and expect one value for the whole frame, and SceneFinalize
    // gates the second visible-entity update on it while RenderGBuffer still runs that pass.
    // Scoping a field the pipeline snapshots is the documented RaytracingSettings hazard
    // wearing a new hat.
    //
    // RenderGrass(DirectCommandList, bool enableHiZ) takes it as an ARGUMENT. GrassRendering
    // then picks _triplanarSingleGenNoHiZPSO over _triplanarSingleGenPSO from that argument
    // alone. So forcing the parameter false reaches exactly the grass generator and nothing
    // else — per-pass by construction, no shared state touched, and it CANNOT reproduce the
    // flicker because no other consumer sees it.
    //
    // The question it answers: grass instances are occlusion-tested against a depth pyramid.
    // If that pyramid does not match our camera, every instance is rejected and the feed has
    // no grass at all rather than thin grass — which is exactly what the feed shows.
    public static volatile Func<bool> GrassNoHiZHook;

    // FRAME END, at the engine's own disposal point.
    //
    // Every nested render displaces N+3 transient constant buffers that nothing then frees;
    // the logic side reclaims them, but one render late, so ~5 are still alive when
    // BindableBufferManager asserts 'AliveConstantBufferCount == 0'. That assert fires once
    // per frame and FirstAssertionException promotes it into the exit-to-menu crash.
    //
    // Render12EngineComponent.IRender_Present is what CALLS OnFrameEndDisposal — it is the
    // frame directly above the assert in the crash stack. A prefix here runs after the
    // recorder is done with the frame and before the engine counts, which is the only moment
    // where freeing our buffers is both safe and early enough to matter.
    public static volatile Action FrameEndHook;

    // THE FLORA EXECUTION CENSUS. Every STATE instrument reads static while the feed's
    // distant flora visibly blinks — but state sampling cannot see EXECUTIONS:
    // BuildRenderData re-running over unchanged component state rebuilds the GPU render
    // data each time, and each rebuild window is a blink. These count invocations; the
    // logic reports them as rates. A steady non-zero BuildRenderData rate against provably
    // static state is the CPU-side answer; all-zero rates push the blink to GPU-side
    // culling inputs by elimination. Plain increments, no interlock: diagnostics.
    public static long FloraBuildRenderDataCalls;
    public static long FloraUpdateRenderDataCalls;
    public static long FloraSsUpdateCalls;
    // The census's fourth counter, added after the first three read 0.0/s in steady state:
    // UploadEntitiesOnGpu writes the per-entity, scene-wide GPU instance state (including
    // LastVisibleFrame) that culling shaders consume — the last flora writer NOT counted,
    // and after ten eliminations the only remaining path that can change what the GPU
    // draws while every managed instrument reads static.
    public static long FloraUploadGpuCalls;

    // The EPISODE counters (user's redirect, 2026-08-03): the feed's flashing foliage is
    // the impostor/texture-LOD tier, and it fires in occasional episodes SYNCED with the
    // main world's object-LOD blips. Both consume the per-entity cached distance, so an
    // episodic GLOBAL re-bucket — DistanceThresholdContainer.FullRefresh sweeping every
    // entity — would produce exactly that pairing. Correlate FullRefresh ticks with
    // user-observed episodes; the impostor/distance rates are the baseline around them.
    public static long DistanceFullRefreshCalls;
    public static long ImpostorTagCalls;
    public static long DistanceToCameraUpdateCalls;

    // Classifies a boxed RenderViewSlim as the feed's view (resolution match) — the
    // logic side owns the reflection and the feed dimensions. Consulted from the culling
    // jobs' DoWork prefixes; see PatchCullingViewBracket.
    public static volatile Func<object, bool> CullingViewIsOursHook;

    // Call/override counters for the above, written by the postfix and read by the logic's
    // reporter. Plain longs, incremented without interlock on purpose: this is a per-entity
    // per-frame path and an occasional lost increment costs a diagnostic nothing, while a
    // lock or an Interlocked would cost the engine real time.
    public static long ViewerDistanceCalls;
    public static long ViewerDistanceOverrides;

    // Same instrument, for the texture path. The RATIO is the diagnostic: a healthy override
    // count next to a flat-looking feed means the substitution is happening and the cause is
    // elsewhere; a zero count means the feed camera never wins the distance test, which is a
    // different bug entirely (wrong delta, or the feed is genuinely farther from everything).
    public static long TextureCameraOverrides;

    // THE OTHER HALF OF THE CENSUS, and the reason the first read was uninterpretable.
    // "0 overrides" is ambiguous between "the prefix never ran for entities near our camera"
    // and "it ran and our camera never won" — completely different bugs. Calls disambiguates.
    //
    // NearestSeen is the diagnostic that decides between them: it records the smallest
    // PLAYER distance the prefix was ever shown. If every entity offered to us is already
    // metres from the player, the collection set simply does not contain our neighbourhood,
    // and no amount of camera substitution can help until something puts it there.
    public static long TextureCameraCalls;
    public static float TextureCameraNearestSeen = float.MaxValue;
}

public sealed class RttPlugin : IPlugin
{
    private const string LogicPath = @"D:\SE2Rtt\RttProbe.Logic.dll";
    private const string LogPath = @"D:\Projects\Space Engineers Stuff\RTT Camera\output\rtt.log";

    private AssemblyLoadContext _logicContext;
    private MethodInfo _tick;
    private DateTime _loadedStamp;

    // THE OFF SWITCH, and it has to live HERE rather than in the file system.
    //
    // The obvious way to run a mod-off control is to rename RttProbe.dll so the loader
    // cannot find it. That was tried on 2026-08-02 and the game refuses to start: SE2's
    // -plugins: loader treats a missing path as a hard error and dies on the splash screen
    // with an incorrect-dll-path message. It does not skip absent plugins.
    //
    // So the assembly must be present and must decide for itself. With this marker in place
    // NOTHING happens: no Harmony patches, no logic assembly loaded, no worker thread, not
    // even the rtt.log banner. The type is constructed and returns, which is as close to
    // "not installed" as an assembly the host insists on loading can get — and unlike a
    // rename it cannot leave the game unbootable.
    //
    // The marker sits next to the DLL rather than in output/ on purpose: it belongs to the
    // DEPLOYED mod, so it survives a rebuild (which overwrites the DLLs) and one glance at
    // D:\SE2Rtt says whether the mod is armed.
    private const string DisableMarker = @"D:\SE2Rtt\DISABLED.marker";

    public RttPlugin(PluginHost host)
    {
        if (File.Exists(DisableMarker))
        {
            // One line, straight to the game's own log, because rtt.log is exactly the thing
            // a control run must not be writing. Without this the only evidence of a
            // deliberate disable is silence, which is indistinguishable from a broken build.
            Console.WriteLine("[RttProbe] DISABLED.marker present in D:\\SE2Rtt - " +
                              "no patches, no logic assembly, no worker thread. Delete the marker to re-arm.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
        // Append, never truncate: relaunching after a crash must not destroy the
        // log that explains the crash.
        File.AppendAllText(LogPath, $"{Environment.NewLine}=== RttProbe bootstrap {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        Log("Bootstrap constructed. Hot-reload watching " + LogicPath);
        ApplyPatches();
        var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "RttProbeBootstrap" };
        worker.Start();
    }

    public RttPlugin() : this(null) { }

    // A DELIBERATELY STUPID CONFIG READER, and it stays that way.
    //
    // The real parser is FeedConfig, in the hot-reloadable logic assembly, and the bootstrap
    // must not depend on it — the whole point of the split is that the bootstrap is loaded
    // once and never again. This reads the same file for a handful of boolean keys, once,
    // at patch time. "key = value", value non-zero and not "false" means on. A missing file
    // or an unreadable one yields TRUE, because the historical behaviour was to patch
    // everything and a config we cannot read must not silently disarm the mod.
    private const string ConfigPath =
        @"D:\Projects\Space Engineers Stuff\RTT Camera\output\feed-config.txt";

    private static Dictionary<string, string> _cfg;

    private static bool Cfg(string key)
    {
        try
        {
            if (_cfg == null)
            {
                _cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!File.Exists(ConfigPath)) { Log($"Patch gating: {ConfigPath} not found — patching EVERYTHING (historical default)."); return true; }
                foreach (var raw in File.ReadAllLines(ConfigPath))
                {
                    var line = raw;
                    int hash = line.IndexOf('#');
                    if (hash >= 0) line = line.Substring(0, hash);
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    _cfg[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }

            if (!_cfg.TryGetValue(key, out var v)) return true;   // unknown key: historical default
            v = v.Trim();
            if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return !(double.TryParse(v, System.Globalization.NumberStyles.Any,
                                     System.Globalization.CultureInfo.InvariantCulture, out var d) && d == 0.0);
        }
        catch (Exception e) { Log($"Patch gating: could not read {key} ({e.Message}) — patching it anyway."); return true; }
    }

    private static void ApplyPatches()
    {
        try
        {
            var harmony = new HarmonyLib.Harmony("rttprobe.bootstrap");

            // The panel content recorder. Its IDrawBatch targets that panel's own
            // offscreen render target — which is exactly where the blit under test
            // has to land.
            var renderer = Type.GetType("Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd.LcdContentRendererSessionComponent, Game2.Client");
            var render = renderer?.GetMethod("Render", BindingFlags.Public | BindingFlags.Instance);
            if (render != null)
            {
                var post = typeof(RttPlugin).GetMethod(nameof(PanelRenderPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(render, postfix: new HarmonyLib.HarmonyMethod(post));
                Log("Patched LcdContentRendererSessionComponent.Render.");
            }
            else Log("FAILED: LcdContentRendererSessionComponent.Render not found.");

            // Per-frame tick, outside panel content recording. Creating our own
            // render target and drawing into it happens here rather than inside
            // Render, so we are never recording two batches at once.
            var rc = Type.GetType("Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd.LcdPanelSurfaceRenderComponent, Game2.Client");
            var tick = rc?.GetMethod("TickFsrMask", BindingFlags.NonPublic | BindingFlags.Instance);
            if (tick != null)
            {
                var post = typeof(RttPlugin).GetMethod(nameof(TickPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(tick, postfix: new HarmonyLib.HarmonyMethod(post));
                Log("Patched LcdPanelSurfaceRenderComponent.TickFsrMask.");
            }
            else Log("FAILED: TickFsrMask not found.");

            PatchSceneDraw(harmony);
            PatchOffscreenUi(harmony);
            PatchGhostProbes(harmony);
            PatchManagedAreas(harmony);
            PatchSimPumpSeat(harmony);

            // Ungated: pure defensive hygiene. See RttBridge.SaveHoldUntilMs.
            PatchSaveGuard(harmony);

            if (Cfg("cameraManualControl")) PatchInputSeat(harmony);
            else Log("cameraManualControl=0 — GameInputProcessorComponent NOT patched (no manual camera input).");

            // THE HOT FIVE ARE NOW OPT-IN, AND THAT IS A PERFORMANCE FIX, NOT TIDINESS.
            //
            // These five patch the engine's busiest per-entity paths. Measured from our own
            // logs on 2026-08-02: the flora prefixes saw 5,574,493 calls in ~80 s (~70k/s)
            // and the clipmap prefix's own comment records ~38,000/s. Every one of them
            // takes `object[] __args`, and that signature makes Harmony build an array and
            // BOX EVERY ARGUMENT — including structs like WorldTransform and RootData —
            // in the generated wrapper, BEFORE our prefix body runs. The `hook == null`
            // early-return inside the body cannot avoid a cost that has already been paid.
            //
            // So "the feature is off" was never free: it cost tens of thousands of boxed
            // argument arrays per second, on the terrain and flora update paths, including
            // throughout world load. That is the shape of the problem the mod-off control
            // exposed — 25.4 s and zero loading-fence blocks with the plugin disabled,
            // against ~323 s and 28 blocks with it on, WITH THE FEED GATE NEVER ACTIVE.
            //
            // Reading the config here means these are RESTART-SCOPED: toggling one of these
            // knobs in feed-config.txt now needs a game restart, where before it took effect
            // on the next poll. That is a deliberate trade — a knob that is off should cost
            // nothing at all, and the alternative is paying the boxing forever so that a
            // rarely-flipped switch can be flipped live. Every other knob is unaffected.
            if (Cfg("perBodyClipmapCamera")) PatchClipmapCamera(harmony);
            else Log("perBodyClipmapCamera=0 — UpdateClipmap NOT patched (saves ~38k boxed arg arrays/sec).");

            if (Cfg("floraCameraOverride")) PatchFloraCamera(harmony);
            else Log("floraCameraOverride=0 — FloraSectorEntityComponent NOT patched (saves ~70k boxed arg arrays/sec).");

            if (Cfg("viewerDistance") || Cfg("fixLodCycling")) PatchViewerDistance(harmony);
            else Log("viewerDistance=0 and fixLodCycling=0 — CalculateDistanceToCamera NOT patched.");

            if (Cfg("wholeSceneGrassNoHiZ")) PatchGrassHiZ(harmony);
            else Log("wholeSceneGrassNoHiZ=0 — RenderGrass NOT patched.");

            if (Cfg("feedTextureCamera")) PatchTextureCamera(harmony);
            else Log("feedTextureCamera=0 — ManagedTexturePrioritizerComponent NOT patched.");

            // UNGATED, AND THE PLACEMENT IS THE LESSON (2026-08-07): these three lived at
            // the TAIL OF PatchTextureCamera for a day, which made them silently conditional
            // on feedTextureCamera — a knob that flipped mid-day. Boots with it on applied
            // them; boots with it off did not; the instruments therefore contradicted the
            // reasoning boot by boot, and an entire inlining theory was built on a session
            // where the patches had simply never been applied. A patch registration's
            // enclosing method IS part of its gating, and these have nothing to do with the
            // texture camera.
            PatchIRCachePrepare(harmony);
            PatchComputeGIFloor(harmony);

            // Ungated, unlike every patch above: it does not add a feature that could be
            // A/B'd off, it removes an assertion we ourselves cause. A config that disabled
            // this would be a config that re-arms the exit CTD.
            PatchFrameEnd(harmony);

            // Ungated: three counter postfixes, each one increment. See the census fields.
            PatchFloraCensus(harmony);

            // GATED, but NOT by feedTextureCamera — by its own key, which defaults ON.
            //
            // The gate is not about the measurement, it is about BISECTION. This counter
            // patches a method the streaming system hammers during world load, and the first
            // load carrying it ended in a CTD (a KeyNotFoundException on
            // DCS.ObjectBuilders.EntityObjectBuilder — almost certainly unrelated, and a crash
            // class seen before this patch existed, but "almost certainly" is not a bisect).
            // Without a gate the only way to test that is another build cycle, which is how an
            // evening disappears.
            //
            // It must NOT be gated on feedTextureCamera: that flag being 0 is the CONTROL arm
            // for this measurement, and a counter that only runs in one arm cannot compare
            // the two. See RttBridge.TierCalls.
            // PatchCollectorMin REMOVED 2026-08-03: ClosestDistanceCollector.MinValue is a
            // two-line method, the JIT inlines it, and Harmony never intercepted a single call
            // (MinCalls stayed 0 all session). A patch that cannot fire is worse than none —
            // it reads as a measurement. The per-material question it was meant to answer was
            // settled instead by naming the offenders, which needed no hook at all.
            if (Cfg("feedTierChurnCounter")) PatchTierChurn(harmony);
            else Log("feedTierChurnCounter=0 — RequestUpdateTier NOT patched (tier churn unmeasurable this session).");

            // Ungated: the postfix costs one ThreadStatic read; the brackets cost a
            // delegate null-check unless the logic arms the hook. See OcclusionDisableId
            // and PatchCullingViewBracket for the v1 -> v2 history.
            PatchOcclusionScope(harmony);
            PatchCullingViewBracket(harmony);
        }
        catch (Exception e) { Log("Patching FAILED: " + e); }
    }

    // Id 31 — occlusion culling off for OUR pass, PER CALL. The safe version of the
    // known-bad wholeSceneNoHzbo: that knob mutated the SHARED HZBOSettings live and the
    // player's own culling raced the restore (whited the feed, flickered the player).
    // This one never touches shared state — CullingSetup.IsOcclusionCullingAllowed is
    // consulted per culling composition, so the player's calls keep the engine's answer
    // and only calls made inside our render see false.
    //
    // WHY: two-phase GPU occlusion classifies entities by GPUModelEntity.LastVisibleFrame,
    // a per-entity, SCENE-WIDE stamp that both views write and only GPU culling shaders
    // read. An entity visible only to the feed oscillates between "prime in pass one" and
    // "test against a pyramid that does not contain it" as the two views' frame counters
    // interleave. Forcing our pass single-phase removes the classifier from our path
    // entirely. Armed by adding 31 to wholeSceneSkipStages.
    private const int OcclusionDisableId = 31;

    private static void PatchOcclusionScope(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType("Keen.VRage.Render12.PrepareStage.CullingSetup, VRage.Render12");
            var mi = t?.GetMethod("IsOcclusionCullingAllowed",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (mi == null) { Log("CullingSetup.IsOcclusionCullingAllowed not found — occlusion scope inactive."); return; }
            var post = typeof(RttPlugin).GetMethod(nameof(OcclusionAllowedPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, postfix: new HarmonyLib.HarmonyMethod(post));
            Log("Patched CullingSetup.IsOcclusionCullingAllowed — id 31 in wholeSceneSkipStages now runs " +
                "the feed's culling single-phase (no HiZ, no shared LastVisibleFrame classifier). " +
                "Per-call, our pass only; the player's occlusion culling is untouched.");
        }
        catch (Exception e) { Log("Patching IsOcclusionCullingAllowed FAILED: " + e.Message); }
    }

    // v2 (2026-08-03): the id-31 path above is RETIRED — ShouldSkipStage's ambient
    // _inOurRender misclassifies calls arriving on parallel prepare job threads, and the
    // measured result was the known-bad HZBO signature (feed mostly white, player lighting
    // strobing). The discriminator must come from the CALL'S OWN ARGUMENTS.
    //
    // The culling jobs' DoWork methods receive the RenderViewSlim they compose for. A
    // prefix classifies that view (through the bridge hook — the logic compares its
    // resolution to the feed's), parks the verdict in a [ThreadStatic], and the postfix
    // restores the previous value via __state so nested DoWorks unwind correctly.
    // IsOcclusionCullingAllowed then consults the ThreadStatic: same thread, same dynamic
    // extent, sound on any thread the job system picks. Failure mode is "no effect" —
    // if a consult happens outside any bracketed DoWork, the engine's answer stands.
    [ThreadStatic] private static bool _cullingOurView;

    private static void OcclusionAllowedPostfix(ref bool __result)
    {
        try { if (__result && _cullingOurView) __result = false; }
        catch { }
    }

    private static void CullingViewPrefix(object __4, ref bool __state)
    {
        __state = _cullingOurView;
        try
        {
            var hook = RttBridge.CullingViewIsOursHook;
            if (hook != null) _cullingOurView = hook(__4);
        }
        catch { }
    }

    private static void CullingViewPostfix(bool __state) { _cullingOurView = __state; }

    private static void PatchCullingViewBracket(HarmonyLib.Harmony harmony)
    {
        int n = 0;
        foreach (var typeName in new[]
        {
            "Keen.VRage.Render12.PrepareStage.CullingEntityProxyJob, VRage.Render12",
            "Keen.VRage.Render12.PrepareStage.CullingGeometryJob, VRage.Render12",
        })
        {
            try
            {
                var t = Type.GetType(typeName);
                // Both DoWorks carry the RenderViewSlim as their FIFTH parameter (__4):
                //   CullingEntityProxyJob.DoWork(cl, targetContext, outputGeometryBuffers,
                //       visibilityListBufferContext, viewSlim, occlusionContext, ...)
                //   CullingGeometryJob.DoWork(cl, geometryContext, outputGeometryBuffers,
                //       visibilityListBufferContext, viewSlim, lodSettings, ...)
                var mi = t?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                          .FirstOrDefault(m => m.Name == "DoWork"
                              && m.GetParameters().Length > 4
                              && m.GetParameters()[4].ParameterType.Name == "RenderViewSlim");
                if (mi == null) { Log($"Culling bracket: {typeName.Split(',')[0]}.DoWork(viewSlim @4) not found — skipped."); continue; }
                harmony.Patch(mi,
                    prefix: new HarmonyLib.HarmonyMethod(typeof(RttPlugin).GetMethod(nameof(CullingViewPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(RttPlugin).GetMethod(nameof(CullingViewPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                n++;
            }
            catch (Exception e) { Log($"Culling bracket {typeName} FAILED: " + e.Message); }
        }
        Log($"Culling view bracket: {n}/2 DoWork(s) wrapped — occlusion culling can now be disabled " +
            "for compositions whose VIEW is the feed's, identified by resolution from the call's own " +
            "argument. Armed by wholeSceneNoOcclusion=1 (logic-side hook); inert otherwise.");
    }

    // The flora execution census — see the RttBridge counter fields for the reasoning.
    private static void PatchFloraCensus(HarmonyLib.Harmony harmony)
    {
        int n = 0;
        foreach (var (typeName, methodName, post) in new[]
        {
            ("Keen.VRage.Render12.SceneSystem.Components.FloraSectorEntityComponent, VRage.Render12",
             "BuildRenderData", nameof(FloraBuildRdPostfix)),
            ("Keen.VRage.Render12.SceneSystem.Components.FloraSectorEntityComponent+InstanceBatch, VRage.Render12",
             "UpdateRenderData", nameof(FloraUpdateRdPostfix)),
            ("Keen.VRage.Render12.PrepareStage.FloraSubSectorMesh, VRage.Render12",
             "Update", nameof(FloraSsUpdatePostfix)),
            ("Keen.VRage.Render12.SceneSystem.Components.FloraSectorEntityComponent, VRage.Render12",
             "UploadEntitiesOnGpu", nameof(FloraUploadGpuPostfix)),
            // NESTED type — the '+' matters; the first attempt used a plain namespace and
            // the patcher rightly skipped it.
            ("Keen.VRage.Render12.SceneSystem.Components.DistanceTagManagerComponent+DistanceThresholdContainer, VRage.Render12",
             "FullRefresh", nameof(DistanceFullRefreshPostfix)),
            ("Keen.VRage.Render12.SceneSystem.Components.DistanceTagManagerComponent, VRage.Render12",
             "OnUpdateImpostorTag", nameof(ImpostorTagPostfix)),
            ("Keen.VRage.Render12.SceneSystem.Components.DistanceTagManagerComponent, VRage.Render12",
             "OnUpdateDistanceToCamera", nameof(DistanceUpdatePostfix)),
        })
        {
            try
            {
                var t = Type.GetType(typeName);
                // Name-only match, first overload, INSTANCE AND STATIC: the tag-manager jobs
                // are static methods and the Instance-only flags here produced the night's
                // fourth silent miss. A census counts invocations; it has no business
                // filtering by shape at all.
                var mi = t?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.Static)
                          .FirstOrDefault(m => m.Name == methodName);
                if (mi == null) { Log($"Flora census: {typeName.Split(',')[0]}.{methodName} not found — skipped."); continue; }
                var pm = typeof(RttPlugin).GetMethod(post, BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(mi, postfix: new HarmonyLib.HarmonyMethod(pm));
                n++;
            }
            catch (Exception e) { Log($"Flora census patch {methodName} FAILED: " + e.Message); }
        }
        Log($"Flora execution census: {n}/7 counter(s) armed — BuildRenderData / batch UpdateRenderData / " +
            "subsector Update / UploadEntitiesOnGpu / DistanceFullRefresh / ImpostorTag / DistanceToCamera " +
            "invocation counts now visible to the logic's reports.");
    }

    private static void FloraBuildRdPostfix() { RttBridge.FloraBuildRenderDataCalls++; }
    private static void FloraUpdateRdPostfix() { RttBridge.FloraUpdateRenderDataCalls++; }
    private static void FloraSsUpdatePostfix() { RttBridge.FloraSsUpdateCalls++; }
    private static void FloraUploadGpuPostfix() { RttBridge.FloraUploadGpuCalls++; }
    private static void DistanceFullRefreshPostfix() { RttBridge.DistanceFullRefreshCalls++; }
    private static void ImpostorTagPostfix() { RttBridge.ImpostorTagCalls++; }
    private static void DistanceUpdatePostfix() { RttBridge.DistanceToCameraUpdateCalls++; }

    // The transient-CB reclaim's landing point — see RttBridge.FrameEndHook.
    //
    // NOT GATED BY CONFIG, deliberately. Every other patch here buys a feature and can be
    // switched off to A/B it; this one only prevents an assertion we ourselves cause, so a
    // config that turns it off is a config that re-arms a crash. It also costs nothing when
    // idle: the hook early-returns on an empty list.
    private static void PatchFrameEnd(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType("Keen.VRage.Render12.EngineComponents.Render12EngineComponent, VRage.Render12");
            if (t == null) { Log("Render12EngineComponent not found — frame-end CB drain inactive."); return; }

            // Matched by NAME ONLY: the parameter is `PresentStats&`, a type nested in
            // VRage.Render12 that this assembly deliberately does not reference, so it cannot
            // be named in a signature match. There is one IRender_Present.
            var mi = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                      .FirstOrDefault(m => m.Name == "IRender_Present");
            if (mi == null) { Log("Render12EngineComponent.IRender_Present not found — frame-end CB drain inactive."); return; }

            var pre = typeof(RttPlugin).GetMethod(nameof(FrameEndPrefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(pre));
            Log("Patched Render12EngineComponent.IRender_Present — transient constant buffers we " +
                "displaced are now freed at the engine's own frame-end disposal point, before " +
                "AliveConstantBufferCount is asserted. This is the last 5 of the leak that took " +
                "31417 -> 18; clearing them is what stops the per-frame assert and the exit CTD.");
        }
        catch (Exception e) { Log("Patching IRender_Present FAILED: " + e.Message); }
    }

    // No parameters requested: PresentStats is nested in VRage.Render12 and asking for it
    // would fail to bind. We need the timing, not the arguments.
    private static void FrameEndPrefix()
    {
        var hook = RttBridge.FrameEndHook;
        if (hook == null) return;
        try { hook(); } catch { }
    }

    // Grass-without-HiZ for our pass — see RttBridge.GrassNoHiZHook.
    private static void PatchGrassHiZ(HarmonyLib.Harmony harmony)
    {
        try
        {
            var sds = Type.GetType("Keen.VRage.Render12.Core.Systems.SceneDrawSystem, VRage.Render12");
            var mi = sds?.GetMethod("RenderGrass",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) { Log("SceneDrawSystem.RenderGrass not found — grass HiZ override inactive."); return; }
            var pre = typeof(RttPlugin).GetMethod(nameof(RenderGrassPrefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(pre));
            Log($"Patched SceneDrawSystem.RenderGrass({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) " +
                "— grass HiZ override armed (wholeSceneGrassNoHiZ).");
        }
        catch (Exception e) { Log("Patching RenderGrass FAILED: " + e.Message); }
    }

    // __1 is the SECOND parameter (enableHiZ); __0 is the command list. Positional injection
    // rather than by name so a parameter rename in a game update cannot silently detach this.
    //
    // ref, and writable: Harmony writes a modified `ref` parameter back into the call, which
    // is what lets the original run with our value instead of the caller's. A throw or a null
    // hook leaves the caller's argument untouched, so the failure mode is "no change".
    private static void RenderGrassPrefix(ref bool __1)
    {
        var hook = RttBridge.GrassNoHiZHook;
        if (hook == null) return;
        try { if (hook()) __1 = false; } catch { }
    }

    // The nearest-viewer distance — see RttBridge.ViewerDistanceHook for the mechanism.
    //
    // A POSTFIX, and that is the safety property: the engine computes its own answer in full
    // first, so a null hook, a disabled feature or a throw all leave the engine's number
    // exactly as it was. We only ever get to lower it.
    // See RttBridge.TextureCameraActive for the mechanism and the safety argument.
    private static void PatchTextureCamera(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType("Keen.VRage.Render12.SceneSystem.Components.ManagedTexturePrioritizerComponent, VRage.Render12");
            if (t == null) { Log("ManagedTexturePrioritizerComponent not found — feed texture camera inactive."); return; }
            var mi = t.GetMethod("CollectStandards",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (mi == null) { Log("ManagedTexturePrioritizerComponent.CollectStandards not found — feed texture camera inactive."); return; }
            var pre = typeof(RttPlugin).GetMethod(nameof(CollectStandardsPrefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(pre));
            Log($"Patched ManagedTexturePrioritizerComponent.CollectStandards({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) " +
                "— the feed camera now gets a vote in TEXTURE MIP selection. This is the path " +
                "ViewerDistanceHook does NOT reach: that one sets the streaming bucket, this one " +
                "sets which mip is resident.");

            // THE ORIENTATION BRACKET — without this the delta is added in the wrong frame
            // for every entity whose orientation is not identity, which on a planet is all of
            // them. If it fails to bind, CollectStandardsPrefix skips the substitution rather
            // than applying a mis-rotated one, so the feature degrades to "off" instead of
            // to "arbitrary".
            var root = t.GetMethod("OnCollectStandardsRoot",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (root == null)
                Log("OnCollectStandardsRoot not found — the texture camera CANNOT rotate its delta into entity " +
                    "space, so the substitution will be skipped entirely rather than applied in the wrong frame.");
            else
            {
                harmony.Patch(root, prefix: new HarmonyLib.HarmonyMethod(
                    typeof(RttPlugin).GetMethod(nameof(OnCollectStandardsRootPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                Log($"Patched OnCollectStandardsRoot({string.Join(", ", root.GetParameters().Select(p => p.ParameterType.Name))}) " +
                    "— our world delta is now rotated into each entity's own frame before being added. " +
                    "cameraPositionRS is Inverse(orientation) * (camera - entityPosition), NOT world space: adding a " +
                    "world delta to it was wrong for every rotated entity, which on a planet is all of them.");
            }

            // THE CYCLE TICK. Dropout detection needs to know where one collection cycle ends
            // and the next begins; without it "I have not seen this entity lately" cannot be
            // distinguished from "it was never offered". PrepareStandardMaterials runs once
            // per cycle, before the per-entity CollectStandards calls.
            //
            // Instrument only — it reads nothing and changes nothing. If it is absent the
            // dropout counter reports UNAVAILABLE rather than a zero that would read as clean.
            var prep = t.GetMethod("PrepareStandardMaterials",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prep == null) Log("PrepareStandardMaterials not found — collection-set dropout counter inactive.");
            else
            {
                harmony.Patch(prep, prefix: new HarmonyLib.HarmonyMethod(
                    typeof(RttPlugin).GetMethod(nameof(PrepareStandardMaterialsPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                Log("Patched PrepareStandardMaterials — collection-cycle tick armed, so entities that DROP OUT " +
                    "of the collection set between cycles can be counted. That is the failure the alternation " +
                    "counter is blind to: an entity nobody offers loses its near-distance vote entirely.");
            }
        }
        catch (Exception e) { Log("Patching CollectStandards FAILED: " + e.Message); }
    }

    // ---- THE AMBIENT FLOOR, AT THE DISPATCHER LEVEL (2026-08-07, design 5) -------------
    //
    // Designs 2-4 patched the job DoWorks (RaytraceGIJob, AmbientLightJob). Both patches
    // applied cleanly and NEITHER EVER EXECUTED — not once, not even for the player, proven
    // by unconditional entry logs that never printed across a whole session. Every stage
    // patch on SceneDrawSystem's big dispatcher methods works; the only silent detours this
    // project has ever produced are those two small job bodies. That is the tiered-JIT
    // inlining signature: the hot dispatcher re-JITs with the job bodies inlined, and a
    // detour on the standalone method never sees another call.
    //
    // So the patch moves to the dispatcher: ComputeGI itself, the same method class as every
    // patch that provably fires. For OUR pass it replaces the body with the minimal faithful
    // contract read from the IL (the CopyJob tail is debug-view-only):
    //
    //   borrow DiffuseGIBuffer + SpecularGIBuffer  (the exact borrow the original makes)
    //   resize both to PreUpscaleResolution
    //   clear diffuse to the ambient floor, specular to black    <- the one change
    //   _ambientLightJob.DoWork(cl, lBuffer, diffuse, specular)  <- REFLECTIVE invoke: runs
    //        the real compiled body regardless of inlining, under the PLAYER'S OWN state —
    //        same LazyJobSnapshot as every other frame, so nothing rebuilds and nothing
    //        can flash
    //   return both borrows (matched by IsInstanceOfType — seven Return overloads)
    //
    // The trace never runs for our pass, so the empty-TLAS sky-miss ambient and the
    // player-cache sampling are both gone, and the feed's ambient is the flat floor.
    private static void PatchComputeGIFloor(HarmonyLib.Harmony harmony)
    {
        try
        {
            var sds = Type.GetType("Keen.VRage.Render12.Core.Systems.SceneDrawSystem, VRage.Render12");
            var gi = sds?.GetMethod("ComputeGI", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (gi == null) { Log("ComputeGI not found — the ambient floor cannot apply."); return; }

            harmony.Patch(gi, prefix: new HarmonyLib.HarmonyMethod(
                typeof(RttPlugin).GetMethod(nameof(ComputeGIFloorPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            Log("Patched ComputeGI (dispatcher level) — for the feed's pass the RTGI trace is not dispatched " +
                "and the ambient job runs over a floor-cleared diffuse buffer. Job-level detours were dead " +
                "(inlined into this dispatcher); this is the level every working stage patch lives at.");
        }
        catch (Exception e) { Log("Patching ComputeGI FAILED: " + e.Message); }
    }

    private static bool _giEnteredLogged, _giOursLogged, _giErrLogged, _giResolved;
    private static System.Reflection.FieldInfo _fAmbientJob;
    private static System.Reflection.MethodInfo _miGiBorrow, _miGiResize, _miAmbientDo, _miGiClear;
    private static System.Reflection.PropertyInfo _piMaxPre, _piPreUp, _piBorrowRes;
    private static System.Reflection.FieldInfo _fScreenBuffers;
    private static object _giPool, _giFmt, _giFmtNull, _giClearNull;
    private static object _giFloorColor, _giBlackColor;
    private static float _giFloorBuilt = float.NaN;

    // __instance = SceneDrawSystem, __0 = DirectCommandList, __1 = the light-accumulation
    // target (IRenderTargetView), __2 = localLightDiffuse (trace-only, unused here).
    private static bool ComputeGIFloorPrefix(object __instance, object __0, object __1)
    {
        if (!_giEnteredLogged)
        {
            _giEnteredLogged = true;
            Log($"ComputeGI prefix ENTERED (call 1) — flag={RttBridge.NestedRenderRtOff}. Dispatcher-level " +
                "detour is live in the executing chain.");
        }
        if (!RttBridge.NestedRenderRtOff) return true;    // player's pass: original, untouched
        try
        {
            if (!_giResolved)
            {
                _giResolved = true;
                var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
                _giPool = core?.GetField("BindableTexturePool", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                _fScreenBuffers = core?.GetField("ScreenBuffers", BindingFlags.Public | BindingFlags.Static);
                var sb = _fScreenBuffers?.GetValue(null);
                _piMaxPre = sb?.GetType().GetProperty("MaxPreUpscaleResolution");
                _piPreUp = sb?.GetType().GetProperty("PreUpscaleResolution");
                _fAmbientJob = __instance.GetType().GetField("_ambientLightJob", BindingFlags.Instance | BindingFlags.NonPublic);
                _miGiBorrow = _giPool?.GetType().GetMethod("BorrowResizableRWRenderTargetTexture",
                                  BindingFlags.Public | BindingFlags.Instance);
                if (_miGiBorrow != null)
                {
                    var ps = _miGiBorrow.GetParameters();
                    var fmtType = ps[1].ParameterType;                       // Vortice.DXGI.Format
                    _giFmt = Enum.ToObject(fmtType, 26);                     // R11G11B10_Float — the original's literal
                    _giFmtNull = Activator.CreateInstance(ps[3].ParameterType);
                    _giClearNull = Activator.CreateInstance(ps[5].ParameterType);
                }
                foreach (var m in __0.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (m.Name == "ClearRenderTargetView" && m.GetParameters().Length == 2) { _miGiClear = m; break; }
            }

            var ambient = _fAmbientJob?.GetValue(__instance);
            if (_giPool == null || _miGiBorrow == null || _miGiClear == null || ambient == null
                || _piMaxPre == null || _piPreUp == null)
            {
                if (!_giErrLogged)
                {
                    _giErrLogged = true;
                    Log("ComputeGI floor: resolution incomplete (pool/borrow/clear/ambient/resolutions) — " +
                        "running the ORIGINAL for the feed's pass. Sky-miss ambient persists but nothing breaks.");
                }
                return true;
            }

            // Colours, rebuilt when the live knob moves.
            float floor = RttBridge.FeedAmbientFloor;
            if (_giFloorBuilt != floor || _giFloorColor == null)
            {
                // THE PARAMETER IS Nullable<Color>, AND THAT ONE FACT ATE THE WHOLE DAY.
                // Every ctor probe so far ran against Nullable`1 — which has no float or byte
                // constructor — so the floor colour was null on every design since the first,
                // and the fallback "black" was an EMPTY Nullable (a null clear value). The
                // floor was never applied once. Build the colour from the UNDERLYING type;
                // reflection's binder wraps a boxed T into Nullable<T> on invoke.
                var ct = Nullable.GetUnderlyingType(_miGiClear.GetParameters()[1].ParameterType)
                         ?? _miGiClear.GetParameters()[1].ParameterType;
                _giBlackColor ??= Activator.CreateInstance(ct);
                // Float ctor first (Color4-like), byte ctor second (Vortice Color is
                // byte-backed). The design-5 rewrite dropped the byte fallback and the floor
                // silently became black at every knob value — "bug gone, shadows dead" —
                // while no error said so. A byte colour saturates at 1.0, so floors above 1
                // clamp; the float path is the one that carries HDR values.
                var f4 = ct.GetConstructor(new[] { typeof(float), typeof(float), typeof(float), typeof(float) });
                if (f4 != null) _giFloorColor = f4.Invoke(new object[] { floor, floor, floor, 1f });
                else
                {
                    var b4 = ct.GetConstructor(new[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte) });
                    if (b4 != null)
                    {
                        byte v = (byte)Math.Clamp((int)Math.Round(floor * 255.0), 0, 255);
                        _giFloorColor = b4.Invoke(new object[] { v, v, v, (byte)255 });
                    }
                }
                RttBridge.FloorColorState = _giFloorColor != null ? 1 : -1;   // readable by the logic-side reporter
                _giFloorBuilt = floor;
            }

            // Re-read the ScreenBuffers INSTANCE each call — the engine can rebuild it, and a
            // stale instance's resolutions would size our borrows for a dead swapchain.
            var sbNow = _fScreenBuffers?.GetValue(null);
            if (sbNow == null) return true;
            object maxRes = _piMaxPre.GetValue(sbNow);
            object preRes = _piPreUp.GetValue(sbNow);

            object dBorrow = _miGiBorrow.Invoke(_giPool, new object[] { "DiffuseGIBuffer", _giFmt, maxRes, _giFmtNull, 1, _giClearNull, 0 });
            object sBorrow = _miGiBorrow.Invoke(_giPool, new object[] { "SpecularGIBuffer", _giFmt, maxRes, _giFmtNull, 1, _giClearNull, 0 });
            try
            {
                _piBorrowRes ??= dBorrow.GetType().GetProperty("Resource");
                object d = _piBorrowRes.GetValue(dBorrow);
                object s = _piBorrowRes.GetValue(sBorrow);

                _miGiResize ??= d.GetType().GetMethod("Resize", BindingFlags.Public | BindingFlags.Instance);
                _miGiResize?.Invoke(d, new[] { __0, preRes });
                _miGiResize?.Invoke(s, new[] { __0, preRes });

                _miGiClear.Invoke(__0, new[] { d, _giFloorColor ?? _giBlackColor });
                _miGiClear.Invoke(__0, new[] { s, _giBlackColor });

                _miAmbientDo ??= ambient.GetType().GetMethod("DoWork", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _miAmbientDo.Invoke(ambient, new[] { __0, __1, d, s });

                if (!_giOursLogged)
                {
                    _giOursLogged = true;
                    Log($"ComputeGI FLOOR LIVE: feed ambient = flat {floor} via the real AmbientLightJob under " +
                        "the player's own state — no trace, no state variance, nothing to flash.");
                }
            }
            finally
            {
                ReturnGiBorrow(dBorrow);
                ReturnGiBorrow(sBorrow);
            }
            return false;
        }
        catch (Exception e)
        {
            if (!_giErrLogged)
            {
                _giErrLogged = true;
                Log("ComputeGI floor THREW — " + e.GetBaseException().Message + " — running the ORIGINAL for " +
                    "the feed's pass from here on. This log line is the diagnosis.");
            }
            RttBridge.NestedRenderRtOff = false;   // fail safe for the rest of this pass
            return true;
        }
    }

    // Seven Return overloads, one per texture kind — match by instance type, never by name
    // alone. Matching by name cost a render-thread freeze on 2026-08-06.
    private static void ReturnGiBorrow(object borrowed)
    {
        if (borrowed == null || _giPool == null) return;
        try
        {
            foreach (var m in _giPool.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "Return") continue;
                var p = m.GetParameters();
                if (p.Length == 1 && p[0].ParameterType.IsInstanceOfType(borrowed)) { m.Invoke(_giPool, new[] { borrowed }); return; }
            }
        }
        catch { }
    }

    // ---- INITIALISE A PARKED IRRADIANCE CACHE ON DEMAND -------------------------------
    //
    // THE CRASH THIS FIXES (2026-08-06 21:46, world load):
    //
    //   Assertion Failure: '_container' was null  at IRCacheResourcesManager.cs:92
    //     at IRCachePrepareJob.DoWork  <-  our RunSecondRender
    //
    // We own CoreSystems.IRCacheResources for our pass so the feed's irradiance entries stop
    // polluting the player's shared world grid. But unlike the probe manager and the TLAS —
    // both of which build their GPU state lazily from inside our nested Draw —
    // IRCacheResourcesManager's container is created by CreateAndInitializeContainer, and its
    // ONLY caller is Render12EngineComponent.DrawInternal: one level ABOVE
    // SceneDrawSystem.Draw, which is where our postfix lives. A manager swapped in during a
    // nested Draw is therefore never offered initialisation, and stage 30 asserts on the null
    // container rather than skipping.
    //
    // IRCachePrepareJob.DoWork is the right place to fix that because it RECEIVES the
    // ComputeCommandList — the one thing our render path has no route to — and it is the very
    // method that asserts. So: if the manager about to be used has no container, build it
    // first, using the list it was about to use anyway.
    //
    // Costs nothing in the normal case: the engine's own manager is initialised long before
    // this ever runs, so the check is one property read per job dispatch and the branch is
    // taken exactly once per parked manager per session.
    private static void PatchIRCachePrepare(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType("Keen.VRage.Render12.LightingStage.IRCachePrepareJob, VRage.Render12");
            if (t == null) { Log("IRCachePrepareJob not found — wholeSceneOwnIRCache cannot self-initialise; it will decline to install."); return; }

            var work = t.GetMethod("DoWork", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (work == null) { Log("IRCachePrepareJob.DoWork not found — wholeSceneOwnIRCache will decline to install."); return; }

            harmony.Patch(work, prefix: new HarmonyLib.HarmonyMethod(
                typeof(RttPlugin).GetMethod(nameof(IRCachePreparePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            Log("Patched IRCachePrepareJob.DoWork — a parked irradiance cache whose container was never built " +
                "is now initialised on demand, using the ComputeCommandList the job was about to use. Without " +
                "this our own cache asserts '_container was null' on the render thread.");
        }
        catch (Exception e) { Log("Patching IRCachePrepareJob FAILED: " + e.Message); }
    }

    private static System.Reflection.PropertyInfo _piIrContainerReady;
    private static System.Reflection.MethodInfo _miIrCreateContainer;

    // INITIALISES THE PARKED MANAGERS, NOT THE INSTALLED ONE. The first version only looked
    // at whatever sat in CoreSystems.IRCacheResources, which deadlocked: the logic side
    // refuses to install an uninitialised manager (correctly — that assert is a render-thread
    // kill), so ours was never installed, so this prefix never saw it, so it was never
    // initialised. Armed and permanently inert, which the log said plainly and I had to be
    // shown twice.
    //
    // Walking the park breaks the cycle at the only point where the two requirements can both
    // be met: here we hold a live ComputeCommandList AND can reach a manager that is not
    // installed anywhere. The engine's own manager is initialised long before this runs, so
    // in the normal case this is a null check over a 4-slot array.
    private static void IRCachePreparePrefix(object __0)
    {
        if (__0 == null) return;
        try
        {
            var park = RttBridge.ParkedIRCaches;
            if (park == null) return;

            for (int i = 0; i < park.Length; i++)
            {
                var mgr = park[i];
                if (mgr == null) continue;

                _piIrContainerReady ??= mgr.GetType().GetProperty("ContainerIsInitialized",
                                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_piIrContainerReady?.GetValue(mgr) is bool ok && ok) continue;   // normal path

                _miIrCreateContainer ??= mgr.GetType().GetMethod("CreateAndInitializeContainer",
                                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_miIrCreateContainer == null) return;

                _miIrCreateContainer.Invoke(mgr, new[] { __0 });
                Log($"IR cache: initialised the container for parked feed {i}'s manager. It was constructed " +
                    "inside a nested Draw, which never reaches DrawInternal's initialisation — so this is the " +
                    "only place a live ComputeCommandList and an uninstalled manager meet.");
            }
        }
        catch { /* a failure here must degrade to the logic-side guard, never throw on the render thread */ }
    }

    private static System.Reflection.FieldInfo _fIrCacheField;
    private static object CoreSystemsIRCache()
    {
        try
        {
            _fIrCacheField ??= Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12")
                                   ?.GetField("IRCacheResources", BindingFlags.Public | BindingFlags.Static);
            return _fIrCacheField?.GetValue(null);
        }
        catch { return null; }
    }

    private static void PrepareStandardMaterialsPrefix()
    {
        RttBridge.TextureCameraCycle++;
        // The ONLY place Active may change — see RttBridge.TextureCameraArmed. Cycle start,
        // before any helper state exists, on the thread about to build it.
        RttBridge.TextureCameraActive = RttBridge.TextureCameraArmed;
    }

    // Runs immediately before the CollectStandards calls for one root entity, on the same
    // thread, and carries the WorldTransform those calls' frame is built from. Rotating our
    // world delta here — once per root — is both correct and cheaper than doing it per
    // material. See RttBridge.RotDX for why the unrotated delta was wrong.
    //
    // WorldTransform is Keen.VRage.Core.WorldTransform and the bootstrap already references
    // VRage.Core, so it can be named outright. The no-reference rule that kept
    // ResourceStreamingData out of the other prefix is specific to VRage.Render12.
    // NaN, infinity, or a delta larger than any sane solar-system span. 1e9 m is ~6.7 AU, far
    // beyond anything this save contains, so a value past it is corruption rather than a
    // legitimately distant object. Cheap enough for a path that runs ~110k times/sec.
    private static bool IsFiniteDelta(double x, double y, double z)
    {
        const double Limit = 1e9;
        return !double.IsNaN(x) && !double.IsNaN(y) && !double.IsNaN(z)
            && !double.IsInfinity(x) && !double.IsInfinity(y) && !double.IsInfinity(z)
            && Math.Abs(x) < Limit && Math.Abs(y) < Limit && Math.Abs(z) < Limit;
    }

    private static void OnCollectStandardsRootPrefix(ref Keen.VRage.Core.WorldTransform __1)
    {
        if (!RttBridge.TextureCameraActive || !RttBridge.TexEyeValid) { RttBridge.RotValid = false; return; }
        try
        {
            // ABSOLUTE, not relative — see RttBridge.TexEyeValid. This reproduces exactly what
            // the engine does for the player one line later in OnCollectStandardsRoot
            // (Inverse(orientation) * (Vector3)(cameraWorld - entityPosition)), but with OUR
            // eye, so the result cannot depend on which camera the collector was handed.
            // ONE read of a snapshot, not three reads of three fields — see EyeSnapshot. Taken
            // into a local FIRST so even the snapshot reference cannot change under us mid-use.
            var eye = RttBridge.TexEye;
            double ex, ey, ez;
            if (eye != null) { ex = eye.X; ey = eye.Y; ez = eye.Z; }
            else { ex = RttBridge.TexEyeX; ey = RttBridge.TexEyeY; ez = RttBridge.TexEyeZ; }

            var rel = new Keen.VRage.Library.Mathematics.Vector3D(
                ex - __1.Position.X,
                ey - __1.Position.Y,
                ez - __1.Position.Z);

            // A TORN OR ABSURD EYE MUST NOT REACH THE MIP MATH. This is belt-and-braces on top
            // of the snapshot: whatever the cause, a non-finite or wildly out-of-range delta
            // produces a mip index the engine indexes an array with, and that is precisely the
            // IndexOutOfRangeException that took the game down twice today. Bailing leaves the
            // entity on the PLAYER's tier, which is the pre-feature behaviour and always safe.
            if (!IsFiniteDelta(rel.X, rel.Y, rel.Z)) { RttBridge.RotValid = false; return; }
            var relF = new Keen.VRage.Library.Mathematics.Vector3((float)rel.X, (float)rel.Y, (float)rel.Z);
            var local = Keen.VRage.Library.Mathematics.Quaternion.Inverse(__1.Orientation) * relF;
            RttBridge.RotDX = local.X; RttBridge.RotDY = local.Y; RttBridge.RotDZ = local.Z;
            RttBridge.RotValid = true;
            RttBridge.RotHits++;
        }
        catch { RttBridge.RotValid = false; }
    }

    // EXPLICIT INTERFACE IMPLEMENTATION, so the name is mangled to
    // "Keen.VRage.Render12.SceneSystem.Components.ManagedTextureStreamingComponent.IListener.RequestUpdateTier"
    // and GetMethod("RequestUpdateTier") returns null. Match on the suffix instead — and if
    // the shape ever changes, say so out loud rather than leaving a counter reading zero,
    // because a silent zero here would read as "streaming is stable" and close the only live
    // line of enquiry. See RttBridge.TierCalls.

    private static void PatchTierChurn(HarmonyLib.Harmony harmony)
    {
        try
        {
            var ft = Type.GetType("Keen.VRage.Render12.Resources.ManagedResources.FileTexture, VRage.Render12");
            if (ft == null) { Log("FileTexture not found — TIER CHURN counter inactive."); return; }
            var mi = ft.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                       .FirstOrDefault(m => m.Name.EndsWith("RequestUpdateTier", StringComparison.Ordinal)
                                         && m.GetParameters().Length == 2
                                         && m.GetParameters()[0].ParameterType == typeof(int));
            if (mi == null) { Log("FileTexture.RequestUpdateTier(Int32, ContinuationQueue) not found — TIER CHURN counter inactive."); return; }
            harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(
                typeof(RttPlugin).GetMethod(nameof(RequestUpdateTierPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            Log($"Patched {mi.Name}(Int32 tier, ContinuationQueue) — TIER CHURN counter armed. This measures what " +
                "STREAMING ACTUALLY DID, not what we asked for: a texture going up then down then up is a resident " +
                "mip collapsing and recovering, which on alpha-tested foliage reads as gone-and-back.");
        }
        catch (Exception e) { Log("Patching RequestUpdateTier FAILED: " + e.Message); }
    }

    // __0 is the requested tier. __instance is taken as object so no engine type is named;
    // its reference hash is the texture identity. Racy by design, like the other job-thread
    // counters here — a lost sample costs one observation and a lock would cost more than the
    // information is worth.
    private static void RequestUpdateTierPrefix(object __instance, int __0)
    {
        try
        {
            RttBridge.TierCalls++;
            var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(__instance);
            if (key == 0) key = 1;
            var slot = (key & 0x7fffffff) & (RttBridge.TexSlots - 1);
            if (RttBridge.TierSlotKey[slot] != key)
            {
                RttBridge.TierSlotKey[slot] = key;
                RttBridge.TierSlotTier[slot] = __0;
                RttBridge.TierSlotDir[slot] = 0;
                return;                                   // first sighting is not a movement
            }
            var prev = RttBridge.TierSlotTier[slot];
            if (__0 == prev) { RttBridge.TierRepeat++; return; }
            sbyte dir = (sbyte)(__0 > prev ? 1 : -1);
            if (dir > 0) RttBridge.TierUp++; else RttBridge.TierDown++;
            // A reversal is the same texture changing direction: up then down, or down then up.
            var prevDir = RttBridge.TierSlotDir[slot];
            if (prevDir != 0 && prevDir != dir)
            {
                RttBridge.TierReversals++;
                // Remember WHICH texture, so the report can name the worst offenders. Only on
                // a reversal: settling traffic is normal and naming it would bury the signal.
                RttBridge.TierSlotRev[slot]++;
                var wr = RttBridge.TierSlotObj[slot];
                if (wr == null) RttBridge.TierSlotObj[slot] = new WeakReference(__instance);
                else wr.Target = __instance;
            }
            RttBridge.TierSlotDir[slot] = dir;
            RttBridge.TierSlotTier[slot] = __0;
        }
        catch { }
    }

    // Positional injection, and ONLY the parameters whose types the bootstrap can name:
    //   __0 cameraPositionRS  (Vector3, VRage.Library)   — the one we may rewrite
    //   __2 boundingBox       (BoundingBox, VRage.Library)
    //   __3 relativePosition  (Vector3, VRage.Library)
    // __1 (ResourceStreamingData) and the context are NOT requested — they are nested in
    // VRage.Render12, which this assembly deliberately does not reference. Harmony injects
    // only what a patch asks for, which is what makes that omission free.
    //
    // WE REPLICATE THE ENGINE'S OWN METRIC RATHER THAN APPROXIMATING IT. ViewerDistanceHook
    // can afford a centre-vs-box approximation because it feeds a min() the engine performs
    // afterwards. Here WE choose, so an approximation could pick our camera in a case where
    // the player's box distance was actually smaller — which would RAISE the recorded
    // distance and demote a texture the player needs. That is the one direction that must be
    // impossible, so both candidates are measured exactly as CollectStandards will measure
    // the winner, and we substitute only on a strict improvement.
    private static void CollectStandardsPrefix(
        ref Keen.VRage.Library.Mathematics.Vector3 __0,
        ref Keen.VRage.Library.Mathematics.BoundingBox __2,
        ref Keen.VRage.Library.Mathematics.Vector3 __3)
    {
        if (!RttBridge.TextureCameraActive) return;
        RttBridge.TextureCameraCalls++;
        try
        {
            // THE DELTA, ROTATED INTO THIS ENTITY'S FRAME. __0 is the camera in the entity's
            // own rotated space, so a world-space delta cannot simply be added to it — see
            // RttBridge.RotDX. The rotated value is published per root entity by
            // OnCollectStandardsRootPrefix on this same thread.
            //
            // FALLING BACK TO THE RAW DELTA WOULD BE FALLING BACK TO THE BUG, so a miss is
            // counted and the substitution is SKIPPED rather than done wrongly.
            if (!RttBridge.RotValid) { RttBridge.RotMisses++; return; }
            // NOT __0 + anything. RotD* is already OUR eye expressed in this entity's frame,
            // computed absolutely in OnCollectStandardsRootPrefix. Adding it to the handed
            // camera is what made the answer depend on which camera we were handed, and that
            // dependence is the lockstep. __0 is still used below — but only as the thing we
            // must beat, never as the thing we build from.
            var ours = new Keen.VRage.Library.Mathematics.Vector3(
                RttBridge.RotDX, RttBridge.RotDY, RttBridge.RotDZ);

            // The delta is a WORLD-space camera-to-camera offset applied in render space.
            // Valid because render space is world space minus an origin — a pure
            // translation — so the vector between two points is identical in both.
            float dPlayer, dOurs;
            if (__2.IsValid)
            {
                dPlayer = __2.Distance(__0);
                dOurs = __2.Distance(ours);
            }
            else
            {
                dPlayer = (__0 - __3).Length();
                dOurs = (ours - __3).Length();
            }

            // Unsynchronised min: a lost race costs one sample, and this is a diagnostic, not
            // a control input. A lock on a per-entity job-thread path would cost far more than
            // the information is worth.
            if (!float.IsNaN(dPlayer) && dPlayer < RttBridge.TextureCameraNearestSeen)
                RttBridge.TextureCameraNearestSeen = dPlayer;

            // ---- DIAGNOSTIC SCAFFOLDING REMOVED 2026-08-03 -------------------------------
            //
            // This prefix runs ~110,000 times a SECOND (1.65M calls per 15 s window). While the
            // flashing was being hunted it also carried, per call: a 3-way hash, slot lookups
            // across five arrays, alternation counting, collection-cycle dropout tracking,
            // base-span min/max, the enterRatio hysteresis, and a distance latch that computed
            // a SECOND bounding-box distance whenever it fired.
            //
            // None of it was the fix. The cause was a RELATIVE camera delta, corrected by
            // computing our eye absolutely in OnCollectStandardsRootPrefix. The hysteresis
            // knobs were measured and changed nothing visible (enterRatio moved alternation
            // 4.2% -> 1.4%; distanceStep did nothing at 0.20 and made things WORSE at 1.0), and
            // the floor was inert because priority only saturates below 1.2 m.
            //
            // What remains is the feature itself: our eye, the engine's own metric, and a
            // substitution that happens only when we are strictly nearer.
            var ourPos = ours;
            var dOursEff = dOurs;

            // THE CEILING IS KEPT, because it is the one lever never actually tested: clamping
            // the presented distance DOWN raises streaming priority, and only the opposite
            // direction was ever tried. Default 0 costs a single compare — the rebuild and the
            // second distance measurement only run if someone arms it.
            // *** COSTS VRAM: a lower demanded mip is ~4x the bytes per step. ***
            var ceil = RttBridge.TextureCameraMaxDist;
            if (ceil > 0f && dOurs > ceil && dOurs > 0.001f)
            {
                var scale = ceil / dOurs;
                ourPos = new Keen.VRage.Library.Mathematics.Vector3(
                    __3.X + (ours.X - __3.X) * scale,
                    __3.Y + (ours.Y - __3.Y) * scale,
                    __3.Z + (ours.Z - __3.Z) * scale);
                // Re-measure with the ENGINE'S metric: the gate below must compare the same
                // quantity the engine will, or the never-demote argument does not hold.
                dOursEff = __2.IsValid ? __2.Distance(ourPos) : (ourPos - __3).Length();
                RttBridge.CeilingApplied++;
            }

            if (float.IsNaN(dOursEff) || !(dOursEff < dPlayer)) return;
            __0 = ourPos;
            RttBridge.TextureCameraOverrides++;
        }
        catch { }
    }

    private static void PatchViewerDistance(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType("Keen.VRage.Render12.Utils.RenderUtilities, VRage.Render12");
            if (t == null) { Log("RenderUtilities not found — nearest-viewer distance inactive."); return; }
            var mi = t.GetMethod("CalculateDistanceToCamera",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (mi == null) { Log("RenderUtilities.CalculateDistanceToCamera not found — nearest-viewer distance inactive."); return; }
            var post = typeof(RttPlugin).GetMethod(nameof(DistanceToCameraPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, postfix: new HarmonyLib.HarmonyMethod(post));
            Log($"Patched RenderUtilities.CalculateDistanceToCamera({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) " +
                "— nearest-viewer distance armed. This is the single input to StreamingTag, " +
                "the impostor swap, shadow tracking and the raytracing near/far tags.");
        }
        catch (Exception e) { Log("Patching CalculateDistanceToCamera FAILED: " + e.Message); }
    }

    // RUNS FOR EVERY ROOT ENTITY IN THE RENDER SCENE, on the renderer's job threads. The cost
    // budget here is a few nanoseconds, so:
    //
    //   * __0 rather than a named parameter — positional injection cannot be broken by a
    //     parameter rename in a game update.
    //   * WorldTransform TYPED, not object[] — Harmony boxes __args, and boxing a 40-byte
    //     struct per entity per frame is exactly the allocation storm this project already
    //     measured once. VRage.Core is referenced by the bootstrap, so the type costs nothing.
    //   * the second argument (LocalBoundsData) is simply not requested: Harmony injects only
    //     what the patch asks for, and that type is nested inside VRage.Render12, which the
    //     bootstrap deliberately does not reference.
    //
    // Dropping boundsData means our answer is a CENTRE distance while the engine's is a
    // bounding-BOX distance, i.e. ours over-estimates for a large entity. Under min() an
    // over-estimate can only fail to help — it can never demote anything — so the
    // approximation is safe by construction rather than by luck. For the entities this
    // feature exists to fix (trees, boulders, grass cells) the box is metres wide and the
    // difference is noise.
    private static void DistanceToCameraPostfix(Keen.VRage.Core.WorldTransform __0, ref float __result)
    {
        var hook = RttBridge.ViewerDistanceHook;
        if (hook == null) return;
        RttBridge.ViewerDistanceCalls++;
        try
        {
            var p = __0.Position;
            var r = hook(p.X, p.Y, p.Z, __result);

            // A DISTANCE BECOMES AN ARRAY INDEX, so "smaller is safe" was wrong.
            //
            // This used to accept ANY r < __result, on the argument that under-estimating a
            // distance can only PROMOTE detail and never demote it. True for LOD selection —
            // and false for the one consumer that divides by it. ManagedTexturePrioritizerComponent
            // computes priority = (P / distance) / D and mip = log2(1 / ratio), then INDEXES a
            // PooledList with the result. A distance of zero (or a denormal, or a NaN that
            // fails every comparison) produces an infinite or negative index and the game dies
            // in CollectStandardMaterials — confirmed 2026-08-04 at 21:18, 21:52 and 23:03.
            //
            // I chased that crash to the feed texture camera twice and was wrong both times:
            // it fired again with feedTextureCamera = 0 and never armed. THIS is the shared
            // path — viewerDistance overrides the distance for the mip prioritiser, the
            // impostor swap, shadow tracking and the raytracing tags all at once.
            //
            // Why it only bit recently: presence used to be pinned to the orbit anchor, so the
            // bubble sat in open space and never produced a near-zero distance. Once residency
            // followed the flying camera, the camera routinely sits ON geometry — a hovering
            // camera a few centimetres from a boulder is an ordinary situation now, and it was
            // impossible before. Same shape as the other two failures today: correct-looking
            // code whose hidden precondition held only while an input stayed constant.
            //
            // 0.05 m is far below anything that changes a tier decision and far above the
            // range where the reciprocal explodes. NaN fails the range test and is rejected.
            const float MinDist = 0.05f;
            if (r >= MinDist && r < __result && !float.IsNaN(r) && !float.IsInfinity(r))
            {
                __result = r;
                RttBridge.ViewerDistanceOverrides++;
            }
            else if (r < MinDist && !float.IsNaN(r) && r >= 0f && __result > MinDist)
            {
                // Genuinely on top of the thing: clamp rather than discard, so the entity still
                // gets the nearest tier without handing the prioritiser a divide-by-zero.
                __result = MinDist;
                RttBridge.ViewerDistanceOverrides++;
            }
        }
        catch { }
    }

    // Per-body clipmap camera — see RttBridge.ClipmapCameraHook for the reasoning.
    //
    // __args rather than typed parameters: it keeps the bootstrap free of VRage.Voxels
    // types, and Harmony writes __args back for prefixes, which is what lets a boxed struct
    // argument be replaced. A prefix returning void never suppresses the original.
    private static void PatchClipmapCamera(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType("Keen.VRage.Voxels.Client.Components.VoxelRenderUpdateSessionComponent, VRage.Voxels.Client");
            var mi = t?.GetMethod("UpdateClipmap",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) { Log("VoxelRenderUpdateSessionComponent.UpdateClipmap not found — per-body clipmap camera inactive."); return; }
            var pre = typeof(RttPlugin).GetMethod(nameof(UpdateClipmapPrefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(pre));
            Log($"Patched VoxelRenderUpdateSessionComponent.UpdateClipmap({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) — per-body clipmap camera armed.");
        }
        catch (Exception e) { Log("Patching UpdateClipmap FAILED: " + e.Message); }
    }

    // Runs for EVERY voxel body EVERY frame. It must be cheap and it must never throw:
    // an exception here is an exception in the engine's terrain update loop.
    //
    // TYPED PARAMETERS, NOT `object[] __args`, and the difference is not stylistic.
    // Declaring __args makes Harmony's generated wrapper allocate an object[] and BOX every
    // argument — including the WorldTransform struct — on EVERY call, before this body runs.
    // The `hook == null` early return below could never avoid a cost already paid, so a
    // disabled feature still cost an array and three boxes ~38,000 times a second.
    //
    // UpdateClipmap(VoxelRenderComponent, WorldTransform, Boolean) is convertible because
    // all three types are reachable: the reference type costs nothing as `object`, and the
    // two value types go by ref. Now the allocation happens ONLY when we actually call the
    // hook — one box for the transform the hook's signature requires — instead of always.
    //
    // The flora prefixes CANNOT be converted the same way: they take
    // (ChildComponent.RootData&, ReadOnlyEntityData<WorldTransform>), RootData is nested and
    // private to VRage.Render12, and the hook genuinely needs both arguments. That is why
    // they were written against __args in the first place.
    private static void UpdateClipmapPrefix(object __instance, object __0,
                                            ref Keen.VRage.Core.WorldTransform __1, ref bool __2)
    {
        if (RttBridge.VoxelUpdateComponent == null) RttBridge.VoxelUpdateComponent = __instance;
        var hook = RttBridge.ClipmapCameraHook;
        if (hook == null) return;
        try
        {
            var replacement = hook(__0, __1);          // the one box, and only when armed
            if (replacement == null) return;
            // Two return shapes, so an old logic DLL keeps working against this bootstrap:
            //   boxed WorldTransform            -> replace the camera only
            //   object[]{ transform, bool }     -> replace the camera AND the loadingPhase
            //                                      flag (__args[2]) — the spawn-speed
            //                                      meshing path the sync-loader uses.
            // Unboxing on the way back in. The hook's contract is untyped so that an older
            // logic DLL keeps working against this bootstrap, so the returned transform
            // arrives boxed and is cast once, here, only on the frames we actually override.
            if (replacement is object[] { Length: >= 2 } pair)
            {
                if (pair[0] is Keen.VRage.Core.WorldTransform wt) __1 = wt;
                if (pair[1] is bool loading) __2 = loading;
            }
            else if (replacement is Keen.VRage.Core.WorldTransform only) __1 = only;
        }
        catch { }
    }

    // Flora sector camera — see RttBridge.FloraCameraHook for the reasoning.
    //
    // Both jobs take (ref RootData, ReadOnlyEntityData<WorldTransform>) whose types are
    // nested/private to VRage.Render12, so the postfixes take __args (Harmony boxes them)
    // rather than typed parameters — the same trick the clipmap prefix uses to stay free
    // of engine types.
    private static void PatchFloraCamera(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType(
                "Keen.VRage.Render12.SceneSystem.Components.FloraSectorEntityComponent, VRage.Render12");
            if (t == null) { Log("FloraSectorEntityComponent not found — flora camera inactive."); return; }
            int n = 0;
            foreach (var (name, pre) in new[]
            {
                ("UpdateCameraPosition", nameof(FloraCameraPrefix)),
                ("UpdateVisibility",     nameof(FloraVisibilityPrefix)),
            })
            {
                var mi = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (mi == null) { Log($"FloraSectorEntityComponent.{name} not found — skipped."); continue; }
                var pm = typeof(RttPlugin).GetMethod(pre, BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(pm));
                n++;
            }
            Log(n > 0
                ? $"Patched {n} FloraSectorEntityComponent job(s) — per-sector flora camera armed."
                : "Flora camera FAILED: no patchable job found.");
        }
        catch (Exception e) { Log("Patching flora camera FAILED: " + e.Message); }
    }

    // Per flora sector, per throttled frame. Cheap and never throwing: this sits inside the
    // renderer's scene update. Returning false skips the engine's own update for sectors
    // the logic has claimed — see RttBridge.FloraCameraHook for why suppression rather than
    // overwriting. A throw or a null hook always falls through to the original.
    private static bool FloraCameraPrefix(object __instance, object[] __args)
    {
        var hook = RttBridge.FloraCameraHook;
        if (hook == null) return true;
        try { return !hook(__instance, __args, false); } catch { return true; }
    }

    private static bool FloraVisibilityPrefix(object __instance, object[] __args)
    {
        var hook = RttBridge.FloraCameraHook;
        if (hook == null) return true;
        try { return !hook(__instance, __args, true); } catch { return true; }
    }

    // The sim-pump seat — see RttBridge.SimPumpHook.
    //
    // THIRD HOST, and the lesson is worth its line: the trigger system's methods
    // (OnTriggerPending, OnUpdateAddedOrMovedTrigger, even UpdateStats) are all
    // conditional — jobs that a quiet, unprofiled session never schedules — and two boots
    // produced a hook that provably never fired. Scene.Tick is the pump's HEARTBEAT: the
    // one method a live scene cannot avoid calling, once per frame, on its own thread.
    // The prefix costs one volatile read when the hook is unset.
    // THE INPUT SEAT — see RttBridge.InputHeld. A prefix on the engine's own per-frame input
    // pump. READ-ONLY by construction: it inspects device state and returns; it never touches
    // the processor's dictionaries, so no binding is consumed or shadowed.
    private static System.Reflection.PropertyInfo _piKeyboard, _piMouse;
    private static System.Reflection.MethodInfo _miFillActive, _miGetPointer, _miFillChanged, _miGetAnalog;
    private static object _inputMgr;
    private static object _activeSet;                 // HashSet<InputId>, reused every frame
    private static object _pointerKind;                // PointerStateKind.Delta, resolved by name
    private static System.Reflection.MethodInfo _miSetClear;
    private static System.Reflection.FieldInfo _fiInputIndex;
    private static bool _seatShapeLogged;

    private static void PatchInputSeat(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType("Keen.VRage.Input.GameInputProcessorComponent, VRage.Input");
            if (t == null) { Log("GameInputProcessorComponent not found — manual camera input INACTIVE."); return; }
            var mi = t.GetMethod("ProcessInput", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) { Log("GameInputProcessorComponent.ProcessInput not found — manual camera input INACTIVE."); return; }
            harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(
                typeof(RttPlugin).GetMethod(nameof(ProcessInputPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            Log("Patched GameInputProcessorComponent.ProcessInput — the camera input seat is live. READ-ONLY: " +
                "device state is inspected, never consumed, so the seat/grid/UI keep every binding.");
        }
        catch (Exception e) { Log("Patching ProcessInput FAILED: " + e.Message); }
    }

    // ---- ACTIVE INPUT LAYERS, read off the component we are already sitting on ---------
    //
    // GameInputProcessorComponent.ActiveContexts is PUBLIC (ListReader<InputContext>), and
    // ListReader exposes Count plus an indexer — so this walks it by index rather than
    // allocating an enumerator every frame. InputContext.Layer is the string the engine's own
    // layer table names ("Ship Movement", "Character Movement", "Camera FreeLook", ...).
    //
    // Runs once per frame, not per key, so the cost is a handful of reflection calls over a
    // list that is normally two or three entries long. The joined string is rebuilt only when
    // the SET changes, so the steady state allocates nothing.
    private static System.Reflection.PropertyInfo _piActiveContexts, _piCtxCount, _piCtxItem, _piCtxLayer;
    private static bool _layersBound, _layersBlindLogged;
    private static string _lastLayers = "";

    private static void ReadActiveLayers(object processor)
    {
        try
        {
            if (!_layersBound)
            {
                _layersBound = true;
                _piActiveContexts = processor.GetType().GetProperty("ActiveContexts",
                                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_piActiveContexts == null)
                {
                    Log("INPUT LAYERS: GameInputProcessorComponent exposes no ActiveContexts — seat and " +
                        "freelook gating cannot be evaluated. The camera falls back to ALWAYS ACTIVE, which " +
                        "is the pre-gating behaviour. This is a READER failure, not 'no layers are active'.");
                    return;
                }
            }
            if (_piActiveContexts == null) return;

            var list = _piActiveContexts.GetValue(processor);
            if (list == null) return;

            if (_piCtxCount == null)
            {
                var lt = list.GetType();
                _piCtxCount = lt.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
                _piCtxItem = lt.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public);
                if (_piCtxCount == null || _piCtxItem == null)
                {
                    if (!_layersBlindLogged)
                    {
                        _layersBlindLogged = true;
                        Log("INPUT LAYERS: ActiveContexts has no Count/indexer — cannot enumerate. Camera " +
                            "falls back to ALWAYS ACTIVE. Reader failure, not an empty layer set.");
                    }
                    return;
                }
            }

            int n = (int)_piCtxCount.GetValue(list);
            var sb = new System.Text.StringBuilder(64);
            for (int i = 0; i < n; i++)
            {
                var ctx = _piCtxItem.GetValue(list, new object[] { i });
                if (ctx == null) continue;
                _piCtxLayer ??= ctx.GetType().GetProperty("Layer",
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var layer = _piCtxLayer?.GetValue(ctx) as string;
                if (string.IsNullOrEmpty(layer)) continue;
                if (sb.Length > 0) sb.Append('|');
                sb.Append(layer);
            }

            var joined = sb.ToString();
            RttBridge.InputLayersReadable = true;
            if (joined != _lastLayers)
            {
                _lastLayers = joined;
                RttBridge.InputLayers = joined;
                // Edge-triggered, so sitting down, standing up and toggling freelook each
                // produce exactly one line. This is the trace that says which layer actually
                // means "seated" on a STATIC grid, which is not something to assume.
                Log("INPUT LAYERS -> [" + (joined.Length == 0 ? "<none>" : joined) + "]");
            }
        }
        catch (Exception e)
        {
            if (!_layersBlindLogged)
            {
                _layersBlindLogged = true;
                Log("INPUT LAYERS: read threw " + e.GetType().Name + " — camera falls back to ALWAYS ACTIVE.");
            }
        }
    }

    // __instance is taken as object so no VRage.Input type has to be named here.
    private static void ProcessInputPrefix(object __instance)
    {
        try
        {
            RttBridge.InputSeatAlive = true;
            ReadActiveLayers(__instance);

            // Bind once. The manager is not a field on the processor — it is reached through
            // whichever member exposes Keyboard/Mouse; find it by SHAPE rather than by name so
            // a rename does not silently disable the whole feature.
            if (_piKeyboard == null)
            {
                // WALK THE BASE TYPES. `_inputManager` is declared on
                // ActionInputProcessorBaseComponent, not on GameInputProcessorComponent, and
                // GetFields(NonPublic|Instance) deliberately does NOT return PRIVATE fields of
                // base classes — FlattenHierarchy does not change that either. The first
                // version searched only the concrete type, found nothing, and reported a shape
                // miss for a field that was there the whole time one level up.
                // AND MATCH ON THE INTERFACE, NOT THE CONCRETE TYPE. InputEngineComponent
                // implements IInputManager, but GetProperty("Keyboard") on the concrete type
                // returns null when the interface is implemented EXPLICITLY — the member is
                // then named "Keen.VRage.Input.IInputManager.Keyboard". The IL shows exactly
                // that asymmetry: get_DeviceManager() is called as an ordinary property while
                // Keyboard/Mouse appear only on the interface. Reading the PropertyInfo off
                // the INTERFACE works for both implicit and explicit implementations.
                var iface = Type.GetType("Keen.VRage.Input.IInputManager, VRage.Input");
                for (var t = __instance.GetType(); t != null && _inputMgr == null; t = t.BaseType)
                {
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    {
                        var v = f.GetValue(__instance);
                        if (v == null) continue;
                        if (iface != null && iface.IsInstanceOfType(v))
                        {
                            _inputMgr = v;
                            _piKeyboard = iface.GetProperty("Keyboard");
                            _piMouse = iface.GetProperty("Mouse");
                            break;
                        }
                        // Fallback for a build where it is implicit after all.
                        var kb = v.GetType().GetProperty("Keyboard");
                        if (kb != null) { _inputMgr = v; _piKeyboard = kb; _piMouse = v.GetType().GetProperty("Mouse"); break; }
                    }
                }
                if (_inputMgr != null && _piKeyboard != null)
                    Log($"INPUT SEAT: bound the input manager ({_inputMgr.GetType().Name}) — key state is now readable.");
                if (_piKeyboard == null)
                {
                    if (!_seatShapeLogged)
                    {
                        _seatShapeLogged = true;
                        Log("INPUT SEAT: no field on GameInputProcessorComponent OR ITS BASE TYPES exposes a Keyboard " +
                            "device — manual camera input cannot read keys on this build. This is a SHAPE MISS, not " +
                            "'no keys held'. Expected target: ActionInputProcessorBaseComponent._inputManager " +
                            "(InputEngineComponent, which carries Keyboard/Mouse/DeviceManager).");
                    }
                    return;
                }
            }

            var keyboard = _piKeyboard.GetValue(_inputMgr);
            if (keyboard == null) return;

            if (_miFillActive == null)
            {
                _miFillActive = keyboard.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "FillActive" && m.GetParameters().Length == 1);
                if (_miFillActive != null)
                {
                    var setType = _miFillActive.GetParameters()[0].ParameterType;
                    _activeSet = Activator.CreateInstance(setType);
                    _miSetClear = setType.GetMethod("Clear");
                }
            }
            if (_miFillActive == null || _activeSet == null) return;

            _miSetClear.Invoke(_activeSet, null);
            _miFillActive.Invoke(keyboard, new[] { _activeSet });

            int n = 0;
            foreach (var id in (System.Collections.IEnumerable)_activeSet)
            {
                if (n >= RttBridge.InputHeld.Length) break;
                _fiInputIndex ??= id.GetType().GetField("Index");
                if (_fiInputIndex?.GetValue(id) is int idx) RttBridge.InputHeld[n++] = idx;
            }
            RttBridge.InputHeldCount = n;

            // ---- MOUSE: PROBE FIRST, BIND SECOND ----------------------------------------
            //
            // Same discipline that settled the keyboard: the keys turned out to be Windows
            // virtual-key codes, and a guessed Avalonia table would have bound every one of
            // them wrong. Nothing states which InputId the mouse axes and wheel use, nor which
            // PointerStateKind carries a DELTA rather than an absolute position — so publish
            // what actually MOVES and let one mouse waggle and one scroll settle it.
            //
            // Only CHANGED inputs are collected, so a still mouse costs one call.
            if (_piMouse != null && RttBridge.MouseProbeWanted)
            {
                var mouse = _piMouse.GetValue(_inputMgr);
                if (mouse != null)
                {
                    _miFillChanged ??= mouse.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "FillChanged" && m.GetParameters().Length == 1);
                    _miGetAnalog ??= mouse.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "GetAnalogState" && m.GetParameters().Length == 1);
                    if (_miFillChanged != null && _activeSet != null)
                    {
                        _miSetClear.Invoke(_activeSet, null);
                        _miFillChanged.Invoke(mouse, new[] { _activeSet });
                        int mn = 0;
                        foreach (var id in (System.Collections.IEnumerable)_activeSet)
                        {
                            if (mn >= RttBridge.MouseChanged.Length) break;
                            _fiInputIndex ??= id.GetType().GetField("Index");
                            if (_fiInputIndex?.GetValue(id) is not int mi) continue;
                            float analog = 0f;
                            try { if (_miGetAnalog != null) analog = (float)_miGetAnalog.Invoke(mouse, new[] { id }); } catch { }
                            RttBridge.MouseChanged[mn] = mi;
                            RttBridge.MouseAnalog[mn] = analog;
                            mn++;
                        }
                        RttBridge.MouseChangedCount = mn;

                        // THE WHEEL AS A RUNNING TOTAL, not a per-frame value.
                        //
                        // The consumer previously de-duplicated by hashing (count, value),
                        // which silently swallowed CONSECUTIVE IDENTICAL notches — and every
                        // ordinary scroll is exactly that, so small movements did nothing and
                        // only erratic ones registered ("very coarse" in game). A monotonic
                        // accumulator makes the consumer's job a subtraction: it cannot lose
                        // an event and cannot apply one twice.
                        for (int wi = 0; wi < mn; wi++)
                            if (RttBridge.MouseChanged[wi] == 7) RttBridge.MouseWheelAccum += RttBridge.MouseAnalog[wi];

                        // POINTER DELTAS for mouse-look. Ids 1/2 change when the mouse moves
                        // but read 0.000 from GetAnalogState — measured in game — which is
                        // what identifies them as POINTER inputs. GetPointerState takes a
                        // PointerStateKind; the kind that carries a DELTA is resolved BY NAME
                        // off the enum rather than by a guessed ordinal, and if no such name
                        // exists the axes simply stay zero instead of feeding the camera a
                        // screen POSITION as though it were a movement.
                        if (_miGetPointer == null)
                        {
                            _miGetPointer = mouse.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                .FirstOrDefault(m => m.Name == "GetPointerState" && m.GetParameters().Length == 2);
                            if (_miGetPointer != null)
                            {
                                var kindType = _miGetPointer.GetParameters()[1].ParameterType;
                                foreach (var nm in new[] { "Delta", "Relative", "Movement", "Motion" })
                                    if (Enum.GetNames(kindType).Contains(nm)) { _pointerKind = Enum.Parse(kindType, nm); break; }
                                Log(_pointerKind != null
                                    ? $"INPUT SEAT: mouse pointer deltas armed (PointerStateKind.{_pointerKind})."
                                    : $"INPUT SEAT: no delta-like PointerStateKind found (have: {string.Join(",", Enum.GetNames(kindType))}) — " +
                                      "mouse look INACTIVE rather than fed absolute positions.");
                            }
                        }
                        if (_miGetPointer != null && _pointerKind != null)
                        {
                            // SUM BOTH AXES FROM EVERY POINTER INPUT — do NOT branch on the id.
                            //
                            // The first version assumed id 1 carried X and id 2 carried Y and
                            // took one component from each. In game that produced working yaw
                            // and DEAD pitch, which is the signature of a single pointer input
                            // returning BOTH axes in one Vector2: the Y of id 1 was thrown
                            // away, and id 2 is evidently not a pointer at all. Accumulating
                            // both components from whatever answers is correct either way and
                            // needs no assumption about which id is which.
                            float dx = 0, dy = 0;
                            foreach (var id in (System.Collections.IEnumerable)_activeSet)
                            {
                                try
                                {
                                    var v = _miGetPointer.Invoke(mouse, new[] { id, _pointerKind });
                                    if (v == null) continue;
                                    var vt = v.GetType();
                                    dx += Convert.ToSingle(vt.GetField("X")?.GetValue(v) ?? 0f);
                                    dy += Convert.ToSingle(vt.GetField("Y")?.GetValue(v) ?? 0f);
                                }
                                catch { }
                            }
                            RttBridge.MouseDX = dx;
                            RttBridge.MouseDY = dy;
                        }
                    }
                }
            }

            RttBridge.InputProbeHook?.Invoke();
        }
        catch { }
    }

    // The save-time marker despawn — see RttBridge.SaveHoldUntilMs for the whole argument.
    // Prefix takes no parameters, so it binds to every SaveGame overload regardless of
    // signature; the guard costs one static write per save.
    private static void PatchSaveGuard(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType("Keen.Game2.Simulation.RuntimeSystems.Saves.SaveSessionComponent, Game2.Simulation");
            if (t == null) { Log("SaveSessionComponent not found — save-time marker despawn INACTIVE (markers may exist during saves)."); return; }
            var pre = typeof(RttPlugin).GetMethod(nameof(SaveGamePrefix), BindingFlags.Static | BindingFlags.NonPublic);
            int n = 0;
            foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                .Where(m => m.Name == "SaveGame"))
            {
                harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(pre));
                n++;
            }
            Log(n > 0
                ? $"Patched SaveSessionComponent.SaveGame x{n} — presence markers are despawned around every save " +
                  "(8 s hold), so no raw marker entity of ours can be in the set a save collects."
                : "SaveSessionComponent.SaveGame not found by name — save-time marker despawn INACTIVE.");
        }
        catch (Exception e) { Log("Patching SaveGame FAILED: " + e.Message); }
    }

    private static void SaveGamePrefix()
    {
        RttBridge.SaveHoldUntilMs = Environment.TickCount64 + 8000;
        // SAY SO. This prefix used to set its field silently, which made "did the hold fire
        // for THIS save?" unanswerable from the log — and the 22:23 device removal happened
        // with the hold fix deployed, leaving open whether the seat-save even routes through
        // SaveSessionComponent.SaveGame. One line per save answers that permanently.
        Log("SAVE intercepted (SaveSessionComponent.SaveGame) — liveness hold armed for 8 s.");
    }

    private static void PatchSimPumpSeat(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType("Keen.VRage.DCS.Scenes.Scene, VRage.DCS");
            var mi = t?.GetMethod("Tick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) { Log("Scene.Tick not found — sim-pump seat inactive."); return; }
            var pre = typeof(RttPlugin).GetMethod(nameof(SimPumpPrefix), BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(pre));
            Log("Sim-pump seat armed on Scene.Tick — fires every frame for every scene, on that scene's own thread.");
        }
        catch (Exception e) { Log("Patching sim-pump seat FAILED: " + e.Message); }
    }

    // Runs at the top of EVERY scene's frame tick, on that scene's pump thread. Must be
    // near-free and must never throw — this is the hottest seat in the engine.
    private static void SimPumpPrefix(object __instance)
    {
        var hook = RttBridge.SimPumpHook;
        if (hook == null) return;
        try { hook(__instance); } catch { }
    }

    // Managed-area registration capture — see RttBridge.ManagedAreaRegistrations for why
    // this exists and why it must live in the bootstrap. The postfix does nothing but
    // stash references; every decision belongs to the logic side, which can be reloaded.
    private static void PatchManagedAreas(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType(
                "Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedWorldArea, VRage.Core.Game");
            var mi = t?.GetMethod("OnRegistered",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) { Log("ManagedWorldArea.OnRegistered not found — area capture inactive."); return; }
            var post = typeof(RttPlugin).GetMethod(nameof(ManagedAreaRegisteredPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, postfix: new HarmonyLib.HarmonyMethod(post));
            Log("Patched ManagedWorldArea.OnRegistered (server-side area capture).");
        }
        catch (Exception e) { Log("Patching ManagedWorldArea FAILED: " + e.Message); }
    }

    // Harmony binds `session` to OnRegistered's first parameter by name. Keep this body
    // trivial and exception-proof: it runs during world load, where a throw is a failed
    // load, not a log line.
    private static void ManagedAreaRegisteredPostfix(object __instance, object session)
    {
        try
        {
            lock (RttBridge.ManagedAreaLock)
                RttBridge.ManagedAreaRegistrations.Add(new[] { __instance, session });
        }
        catch { }
    }

    // The UI stage's offscreen renderer. Its signature is not known ahead of time,
    // so the postfix takes __args and the logic side inspects them.
    private static void PatchOffscreenUi(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType("Keen.VRage.Render12.UIStage.OffscreenUIRenderer, VRage.Render12");
            if (t == null) { Log("OffscreenUIRenderer type not found."); return; }

            var mi = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                  BindingFlags.Instance | BindingFlags.Static)
                      .FirstOrDefault(m => m.Name == "DrawOne");
            if (mi == null) { Log("OffscreenUIRenderer.DrawOne not found."); return; }

            var post = typeof(RttPlugin).GetMethod(nameof(OffscreenUiPostfix), BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, postfix: new HarmonyLib.HarmonyMethod(post));
            Log($"Patched OffscreenUIRenderer.DrawOne({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}).");
        }
        catch (Exception e) { Log("Patching OffscreenUIRenderer FAILED: " + e.Message); }
    }

    // __instance is APPENDED to the argument array rather than passed through a new bridge
    // field, deliberately. The logic side already locates what it needs by type name, so a
    // longer array costs it nothing — and a logic assembly running against an OLDER
    // bootstrap simply does not find an OffscreenUIRenderer in the args and degrades to "no
    // mip regeneration" with one log line, instead of a missing-field failure that would
    // take the whole handover down with it.
    //
    // Why the instance is wanted at all: OffscreenUIRenderer._mipMapJob is the engine's own
    // mip generator for this exact target, invoked one call earlier in DrawOne. Reusing it
    // creates nothing (Rule 11) and cannot fight another system for its descriptor table,
    // which borrowing CloudShadowJob's MipMapJob would have risked.
    private static void OffscreenUiPostfix(object __instance, object[] __args)
    {
        try
        {
            var withInstance = new object[__args.Length + 1];
            Array.Copy(__args, withInstance, __args.Length);
            withInstance[__args.Length] = __instance;
            RttBridge.OffscreenUiDrawHook?.Invoke(withInstance);
        }
        catch { }
    }

    // SceneDrawSystem lives in VRage.Render12 and is internal, so everything here
    // goes through reflection. The postfixes only capture `this` — the reconnaissance
    // itself runs in the hot-reloadable logic assembly.
    private static void PatchSceneDraw(HarmonyLib.Harmony harmony)
    {
        var sds = Type.GetType("Keen.VRage.Render12.Core.Systems.SceneDrawSystem, VRage.Render12");
        if (sds == null) { Log("SceneDrawSystem type not found — is VRage.Render12 loaded yet?"); return; }
        Log("SceneDrawSystem found: " + sds.FullName);

        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // The foreign-view pass we intend to model on, plus a per-frame pass as a
        // fallback in case probe updates are rare.
        foreach (var (name, id, hook) in new (string, int, string)[]
        {
            ("ExecuteEnvironmentProbeUpdate", 0, nameof(ProbePassPostfix)),
            ("DrawUnlit", 1, nameof(FramePassPostfix)),
        })
        {
            try
            {
                var mi = sds.GetMethod(name, Any);
                if (mi == null) { Log($"SceneDrawSystem.{name} not found."); continue; }
                var post = typeof(RttPlugin).GetMethod(hook, BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(mi, postfix: new HarmonyLib.HarmonyMethod(post));
                Log($"Patched SceneDrawSystem.{name} (id {id}).");
            }
            catch (Exception e) { Log($"Patching SceneDrawSystem.{name} FAILED: {e.Message}"); }
        }

        // Draw is the TOP of the pipeline, and the only site where a second whole-scene
        // render can be driven. Patched separately from the loop above because it takes
        // a different argument (the final LDR buffer, not a command list) and because it
        // is the one hook that calls back into the method it patches.
        try
        {
            var draw = sds.GetMethod("Draw", Any);
            if (draw == null)
            {
                Log("SceneDrawSystem.Draw not found — the whole-scene route has no hook site.");
            }
            else
            {
                var post = typeof(RttPlugin).GetMethod(nameof(WholeScenePostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                var pre = typeof(RttPlugin).GetMethod(nameof(WholeScenePrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);

                // BOTH ends of the same method. The postfix always runs the bookkeeping
                // (gate, buffers, Perf, logging); which end runs the RENDER is the logic
                // side's choice, live-switchable via wholeSceneSubmitEarly. Patching both
                // unconditionally keeps that a config flip rather than a restart.
                harmony.Patch(draw,
                    prefix: new HarmonyLib.HarmonyMethod(pre),
                    postfix: new HarmonyLib.HarmonyMethod(post));
                Log($"Patched SceneDrawSystem.Draw({string.Join(", ", draw.GetParameters().Select(p => p.ParameterType.Name))}) " +
                    "— the whole-scene render hook, BOTH ends (prefix = start-of-frame submission, " +
                    "postfix = bookkeeping and the legacy render position).");
            }
        }
        catch (Exception e) { Log("Patching SceneDrawSystem.Draw FAILED: " + e.Message); }

        PatchSkippableStages(harmony, sds);
        PatchFsrGate(harmony);
        PatchExposureGate(harmony);
    }

    // __0 is the ResizableRWRenderTargetTexture the engine just rendered the player's
    // frame into. We do not touch it — it is passed through so the logic side can read
    // its format and resolution, which is what a second target has to match.
    private static void WholeScenePostfix(object __instance, object __0)
    {
        try { RttBridge.WholeSceneHook?.Invoke(__instance, __0); } catch { }
    }

    // VOID prefix — it can never skip the original. Harmony only honours a skip from a
    // prefix returning bool, and making this one bool-returning would put the player's
    // entire frame one typo away from not being drawn.
    private static void WholeScenePrefix(object __instance, object __0)
    {
        try { RttBridge.WholeSceneEarlyHook?.Invoke(__instance, __0); } catch { }
    }

    // Draw sub-stages that a second render must be able to skip.
    //
    // These are all WORLD-SPACE or CROSS-FRAME: they update state the player's next
    // frame reads, so running them a second time per frame corrupts their view rather
    // than ours. Several cannot be reached by any settings flag —
    // ExecuteAccelerationStructuresBuilding checks only EnableGPUParallelization — which
    // is why this exists at all.
    //
    // The id is positional and is what the logic side switches on; keep the order
    // stable or the config's stage list silently means something else.
    // Type == null means SceneDrawSystem; anything else is an assembly-qualified name.
    //
    // Ids 17+ reach methods on OTHER types, and that is not decoration. The RT settings
    // route to "no ray tracing in our render" is CLOSED: RaytraceGIJob keys a
    // LazyJobSnapshotHandler<RTGISettings, RTGISnapshot> off RaytracingSettings and builds
    // SHADER DEFINES from it, so toggling any flag that reaches a define rebuilds
    // pipelines — ten times a second, which shows up as bright flashing across the
    // player's whole world. Confirmed for Enabled, and again for
    // RaytracedDiffuseGI/RaytracedSpecularGI. A Harmony prefix mutates nothing and is the
    // only lever that reaches the work without touching the settings.
    private static readonly (string Type, string Method)[] SkippableStages =
    {
        (null, "ExecuteAccelerationStructuresBuilding"),     // 0  raytracing scene / TLAS
        (null, "ExecuteRaytracingPrepareAndSceneFinalize"),  // 1  raytracing prepare
        (null, "RenderEnvironmentProbe"),                    // 2  shared probe atlas (ambient + reflections)
        (null, "RenderShadows"),                             // 3  shadow cascades
        (null, "ComputeExposure"),                           // 4  auto-exposure history  (UNSAFE: out params)
        (null, "UpdateSurfels"),                             // 5  water surfels
        (null, "PrepareClusters"),                           // 6  light cluster grid
        (null, "ProcessParticles"),                          // 7  particle SIMULATION state
        (null, "RenderDecals"),                              // 8  decal atlas
        (null, "ExecuteHBAO"),                               // 9  ambient occlusion
        (null, "ExecuteLighting"),                           // 10 whole lighting stage (our image dies without it)
        (null, "RenderMainView"),                            // 11 the geometry pass (ditto)
        (null, "ComputeDirectionalLighting"),                // 12 sun light + shadow mask
        (null, "ComputeLocalLights"),                        // 13 clustered point/spot lights
        (null, "ComputeCloudShadows"),                       // 14 writes SHARED CommonResources.CloudShadowmap
        (null, "UpdateAtmosphere"),                          // 15 atmosphere LUT updates
        (null, "DrawUI"),                                    // 16 the player's HUD, baked into the feed otherwise

        // 17 — the ray trace itself, and nothing else. ComputeGI runs
        // _raytraceGiJob.DoWork behind a settings gate and then _ambientLightJob.DoWork
        // unconditionally, so skipping HERE removes the RT work and KEEPS the feed's
        // ambient term. Skipping ComputeGI (18) would take both.
        ("Keen.VRage.Render12.LightingStage.RaytraceGIJob, VRage.Render12", "DoWork"),   // 17

        // 18 — the whole GI stage, ambient included. Blunter than 17; the feed's
        // shadowed areas go black. Kept as the fallback if 17 is not enough.
        (null, "ComputeGI"),                                 // 18

        // 19 — DO NOT USE. Kept so the ids below do not shift.
        //
        // The idea was to stop our render disposing the player's FSR history:
        //
        //     UpsamplingJob.PrepareResources:
        //       switch (Settings.DRS.AAMode) {
        //         case Bilinear: _bilinear.PrepareResources(); _fsr3_1.DisposeResources();
        //         case FSR:      _fsr3_1.PrepareResources(maxRes, displayRes);
        //                        _bilinear.DisposeResources();
        //       }
        //
        // With DRSSettings.AAMode scoped to 0 for our render, ScenePreparation takes the
        // Bilinear branch and disposes the SHARED FSR3 resources — ten times a second —
        // so the player's TAA restarts every frame and never accumulates. That much was
        // right, and it IS the fine-detail shimmer.
        //
        // But skipping it is not the fix, because PrepareResources does not only
        // dispose: each branch ALLOCATES its own side. Skip it and our render runs the
        // bilinear path with nothing allocated, because the player's frame disposed
        // bilinear when it prepared FSR. Device removed inside Upsampling, PageFaultVA
        // 0x0, on world load.
        //
        // Only ONE resource set is alive at a time, chosen by AAMode. That is also the
        // real mechanism behind the three original wholeSceneAAMode CTDs, which is worth
        // saying plainly: the model that got retracted as "wrong" was pointing here.
        //
        // The answer is not to skip anything in Upsampling — it is to stop scoping
        // AAMode at all, and disable FSR for our render at the only place that decides
        // it. See stage 20.
        ("Keen.VRage.Render12.PostProcessStage.Upsampling.UpsamplingJob, VRage.Render12",
         "PrepareResources"),                                // 19  DO NOT USE

        // 21 — the flare pass. Paired with sharing the engine's FlaresContext.
        //
        // Every light in the world registers its flare through the GLOBAL:
        // PointLightEntityComponent.Init / SetParameters / OnRemovedFromScene, the spot
        // and particle equivalents, and SceneManager.UpdateFlareDefinitions all read
        // CoreSystems.DrawContexts.LensFlares. Our nested Draw swaps that global ten
        // times a second, so any light created, retuned or removed inside one of those
        // windows talks to OUR context instead of the engine's — and a SetParameters
        // that lands on the wrong context leaves the engine's copy holding stale
        // parameters, i.e. a flare stuck at a position the light no longer occupies.
        //
        // That is the reported "planet's atmosphere appears, completely unattached to
        // the planet". Sharing the engine's context removes the window entirely.
        //
        // But sharing alone would be worse than the disease: RenderFlares calls
        // ProcessFinishedFrame and PrepareReadback, which advance the flare OCCLUSION
        // readback across frames. Running that twice per frame against one shared
        // context would corrupt the player's flare occlusion. So share the context AND
        // skip the pass — we read the definitions, we never advance the state.
        //
        // Costs the feed nothing it had: our own FlaresContext was created empty and
        // never received a single definition, because registration goes through the
        // global and the global is the engine's whenever a light is actually created.
        (null, null),                                        // 20 RESERVED — the FSR gate
                                                             //    (an override, not a skip)
        (null, "RenderFlares"),                              // 21

        // 22-24 — THE SHARED WORLD-SPACE WRITES.
        //
        // CommonResourcesManager owns CloudShadowmap, the per-planet AtmosphereLUTTables
        // and the WeatherMapTables. All three are world-space, shared, and written from
        // whatever camera happens to be rendering. Stages 14 (ComputeCloudShadows) and 15
        // (UpdateAtmosphere) run in our nested Draw, so ten times a second we recompute
        // the player's cloud shadows, weather maps and atmosphere LUTs FROM THE ORBIT
        // CAMERA. A cloud shadowmap is a pattern projected onto world surfaces, which is
        // very close to the reported "projection of what the camera is seeing, on the
        // walls of my ship".
        //
        // Skipping the STAGES was tried early on and page-faulted: each stage also
        // produces per-frame transients its own later consumers need. So skip the JOBS
        // instead — the stage still runs, still borrows, still clears, still produces its
        // transients, and only the write to the shared world-space resource is dropped.
        // Same shape as stage 17 for RaytraceGIJob, which worked.
        //
        // Cost to the feed: no cloud shadows and no atmosphere LUT refresh of its own —
        // it uses whatever the player's frame last computed. That is the RIGHT trade
        // while these are shared: an approximate feed beats a corrupted world.
        ("Keen.VRage.Render12.LightingStage.CloudShadowJob, VRage.Render12",     "DoWork"),   // 22
        ("Keen.VRage.Render12.LightingStage.CloudWeatherMapJob, VRage.Render12", "DoWork"),   // 23
        ("Keen.VRage.Render12.LightingStage.AtmosphereLUTJob, VRage.Render12",   "DoWork"),   // 24

        (null, null),                                        // 25 RESERVED — the exposure
                                                             //    read-only override

        // 26 — THE RESOLUTION-KEYED REALLOCATION. Confirmed cause of a device removal:
        // DRED breadcrumb [15] ForwardAndPostPasses 20/255, EventStack
        // [CloudShading, ForwardPasses, ForwardAndPostPasses], PageFaultVA 0x1B54406000
        // (a REAL address — a use-after-free, not a null bind), and 360 allocation nodes
        // in the dump of which every single one was CloudAccumulateLightAlpha.
        //
        // CloudJob.DoWork calls ValidateHalfResTemporalResource, which is:
        //
        //     var halfMax = CoreSystems.ScreenBuffers.MaxPreUpscaleResolution / 2;
        //     if (resource.PeekNext().MaxResolution != halfMax) {
        //         resource.Dispose();                                   // FREE
        //         resource = new TemporalResource<>(() =>
        //             BindableTextures.CreateRWResizableRenderTargetTexture(name, fmt, halfMax));
        //     }
        //
        // It keys off CoreSystems.ScreenBuffers — the global our render SWAPS. Ours is
        // 512x512 so halfMax is 256x256; the player's 3840x2160 gives 1920x1080. So every
        // one of our renders disposes the player's cloud history and rebuilds it at 256,
        // and the player's very next frame does it straight back. Twenty allocations and
        // frees of a multi-hundred-MB resource per second, which is also the +/-151MB
        // VRAM oscillation visible in every PERF line and a large share of the frame spike.
        //
        // This is the ONE resolution-keyed resource owner our DrawContextManager swap does
        // not already cover. VolumeRenderingContext, RTGIContext, StochasticTransparency-
        // Context and WaterContext all hang off DrawContextManager — which is ours — so
        // they resize against our resolution harmlessly. CloudJob hangs off
        // SceneDrawSystem._cloudPass, and SceneDrawSystem is a singleton we do not swap.
        //
        // Every other shared job that reads MaxPreUpscaleResolution (HBAOJob, HighlightJob,
        // TerrainBlendingJob, AtmosphereAdditiveJob) only calls Resize() on a borrowed pool
        // texture — the designed per-frame path, cheap and safe. CloudJob is alone in doing
        // a genuine Dispose + Create keyed on MaxResolution. (It also retro-explains the
        // undiagnosed stage-9 HBAO device removal: same family, same global.)
        //
        // Cost to the feed: no volumetric clouds of its own. User-confirmed as free —
        // "i dont need actual clouds rendering in the feed, just the planet atmospheres",
        // and the atmospheres come from AtmosphereAdditive/MultiplyJob plus the planet-env
        // rebuild, none of which is touched here.
        ("Keen.VRage.Render12.PostProcessStage.CloudJob, VRage.Render12", "DoWork"),         // 26

        // 27-28 — THE TWO GLOBALS INSIDE DrawContextManager.OnBeginDraw.
        //
        // Owning the DrawContextManager covers almost everything, but OnBeginDraw is:
        //
        //   (LocalLightsToUpdate, ShadowMasksToUpdate) = CoreSystems.LocalLights.FlushUpdates();
        //   CascadesToUpdate          = DrawContexts.CascadeShadows.FlushUpdates();
        //   CharacterCascadesToUpdate = DrawContexts.CharacterShadows.FlushUpdates();
        //   DrawContexts.DirectionalLightShadowResources.OnBeginDraw();
        //   EnvProbesToUpdate         = CoreSystems.EnvironmentProbeManager.PrepareProbes();
        //
        // The middle three read CoreSystems.DrawContexts, which is OURS during our render.
        // The first and last read CoreSystems statics, which are the ENGINE'S, and both are
        // drain/advance operations — so our nested Draw runs each of them a second time per
        // frame against shared state.
        //
        // 27 is a CONFIRMED device removal at wholeSceneIntervalMs=33 (2026-07-28): DRED
        // breadcrumb [13] "ScenePreparation + Render" 1010/1475, EventStack
        // [EnvironmentProbes, ScenePreparation + Render], dying on the Resourcebarrier just
        // after EnvProbe_Blending, PageFaultVA 0x0 with ExistingAllocations 0 and
        // RecentFreedAllocations 0 — a NULL BIND, the opposite signature to the CloudJob
        // use-after-free. PrepareProbes stores _lastSettings, _forceReprocess and _state,
        // calls UpdateLocalLightAmbient, and can DisposeTextures + RecreateProbes. Our
        // render advancing that state machine and then skipping stage 2 leaves the player's
        // ExecuteEnvironmentProbeUpdate binding a probe face that was never produced. At
        // 10 fps it desynced rarely enough to survive; at 30 fps it is every frame.
        //
        // Cost to the feed: NONE. Stage 2 (RenderEnvironmentProbe) is already skipped, so we
        // never consumed EnvProbesToUpdate in the first place — we were paying the shared
        // state mutation for a queue we then threw away.
        //
        // 28 is the same shape and is Rule 8's other named global, but is NOT in the default
        // skip list: it has no crash attached to it yet. Patched so it can be turned on from
        // the config without a rebuild if the probe fix alone is not enough. Its cost is that
        // the feed stops updating local-light shadows of its own and uses the player's.
        //
        // Both are parameterless and return STRUCTS (Buffer<Request>, and a ValueTuple of two
        // Buffers). A Harmony prefix returning false skips the original and leaves __result at
        // default(T) — which is a zero-count Buffer that iterates safely. That is Rule 8's
        // corollary, established when an unassigned LocalLightsToUpdate turned out to be a
        // missing feature rather than a crash. So these need no __result handling at all.
        ("Keen.VRage.Render12.LightingStage.EnvironmentProbeManager, VRage.Render12",
         "PrepareProbes"),                                                                  // 27
        ("Keen.VRage.Render12.LightingStage.LocalLightsManager, VRage.Render12",
         "FlushUpdates"),                                                                   // 28

        // 29 — THE PHANTOM BLEED. Same blind spot as CloudJob (26).
        //
        // The user's description is what identified it: the ghost is not a vague imprint,
        // it is "the scene from the feed camera including skybox, bright lights emanating
        // from planets' edges, the ship's grid and asteroids", it "moves and is animated
        // showing the perspective from that camera", and the speckles in it "are the
        // skybox". That is a full colour image of OUR render appearing on the player's
        // REFLECTIVE surfaces — which is screen-space reflections, not GI.
        //
        // Ruled out first, each by test rather than by argument:
        //   * IR cache / our GI trace — skipping 17 left the bleed untouched.
        //   * ALL of GI — skipping 18 (trace AND ambient) left it untouched too.
        //   * Inheriting the engine's contexts — ours is a fresh Activator.CreateInstance,
        //     and only DirectionalLightShadowResources and LensFlares are shared, both
        //     deliberately.
        //   * The gate A/B — fully dormant is clean, so it is definitely ours.
        //
        // ScreenSpaceReflections._dynamicResources is an INSTANCE field holding
        // AverageRadianceHistory, VarianceHistory and SampleCountHistory — the temporal
        // accumulation for the reflection denoiser. The job itself is
        // SceneDrawSystem._screenSpaceReflectionsJob, and SceneDrawSystem is a singleton we
        // do NOT swap. So that history is shared with the player, our render writes our
        // scene's radiance into it, and the player's next frame denoises its reflections
        // against our content.
        //
        // Being a temporal HISTORY is also why it can bleed at all. Within one engine frame
        // our commands are recorded AFTER the player's, so a shared write can only reach
        // them if it survives into the next frame. Accumulated history does exactly that —
        // and it explains why the ghost lingered briefly after wholeSceneCamera was flipped
        // to 0, which had looked like evidence the bleed was camera-independent.
        //
        // Cost to the feed: no screen-space reflections of its own. DoWork takes its
        // destination as a parameter, so nothing downstream loses a resource — the same
        // shape as CloudJob, which skipped cleanly. PrepareResources is deliberately NOT
        // touched: like UpsamplingJob's (see 19) it ALLOCATES, and skipping an allocator
        // is how that one removed the device.
        ("Keen.VRage.Render12.PostProcessStage.ScreenSpaceReflection.ScreenSpaceReflections, VRage.Render12",
         "DoWork"),                                                                         // 29

        // 30 — SPLITTING STAGE 1, and this one gives the feed back three things at once.
        //
        // Stage 1 is ExecuteRaytracingPrepareAndSceneFinalize, and its NAME is the whole bug:
        // it is TWO unrelated bodies behind one entry point.
        //
        //     RaytracingPrepare(cl)    world-space shared RT state — the reason 1 was skipped
        //     SceneFinalize(cl)        nothing to do with raytracing at all
        //
        // SceneFinalize, read in full, runs on OUR DrawContexts:
        //
        //     CascadeStatsJob                                        cascade shadow stats
        //     LODStateUpdateJob(DrawContexts.LODTransitions)         LOD state
        //     LODStateUpdateJob(DrawContexts.InstancedLODTransitions) INSTANCED LOD state
        //     VisibleEntitiesUpdateJob(MainViewCulling.FirstPass, MainOutputGeometryBuffers)
        //     VisibleInstancedEntitiesUpdateJob(MainViewCulling.FirstPass, ...)
        //     ...and the same two again for SecondPass when HZBO.MainViewEnabled
        //
        // So skipping stage 1 for a RAYTRACING reason silently cost the feed its LOD state
        // updates, its INSTANCED LOD state updates, and its visible-entity sets. That is a
        // one-to-one match with the three fidelity gaps that were being chased as separate
        // bugs after goal 10:
        //
        //     LOD state never updates            trees resolve to low-detail up close
        //     instanced LOD never updates        foliage thinner than the same biome locally
        //     visible-entity set never updates   RenderGrass generates from
        //                                        DrawContexts.MainViewCulling.EntityProxies,
        //                                        so grass generates for nothing and no grass
        //                                        appears AT ALL
        //
        // The grass probe is what closed it: inside our own pass Grass.Enabled=True,
        // DrawDistance=1000, Density=3, Is3DMapEnabled=False, our GrassBufferContext present
        // and MainViewCulling present. Every gate open, no grass — so the failure had to be
        // the SET being generated from, and the only thing that fills that set is the job we
        // were skipping.
        //
        // Both halves have exactly ONE caller each (checked), so patching RaytracingPrepare
        // as its own stage separates them cleanly with nothing else to consider. Put 30 in
        // wholeSceneSkipStages and take 1 OUT: the RT half stays suppressed exactly as before
        // — this is strictly LESS suppression than skipping all of stage 1 — while
        // SceneFinalize runs for our camera.
        ("Keen.VRage.Render12.Core.Systems.SceneDrawSystem, VRage.Render12",
         "RaytracingPrepare"),                                                              // 30
    };

    // Stage 20 is NOT a skip — it is a return-value override, so it lives outside the
    // table above.
    //
    // IsFSREnabledAndAllowed is `DRS.AAMode == 2 && debugViewOk`, and it is what
    // UpscaleTargetFSR, ExecuteForwardPasses and RenderMainView consult to decide
    // whether to run FSR and write its masks. Forcing it FALSE for the duration of our
    // render gets us off the shared upsampler — which is what stopped our geometry being
    // composited see-through — while changing NO state at all:
    //
    //   * AAMode keeps the player's value, so PrepareResources stays on the FSR branch
    //     and the FSR resources are neither disposed nor left unallocated.
    //   * UpscaleTargetFSR takes its own early-out, which correctly sets the
    //     toneMappingInput/toneMappingOutput out-params bloom and tonemap consume.
    //   * Nothing is written back, so nothing can leak into a stage we did not consider.
    //
    // Three settings scopes in a row leaked into code they were not aimed at (Enabled ->
    // RaytraceGIJob's shader defines, RaytracedDiffuseGI -> the same, AAMode ->
    // UpsamplingJob's resource lifetime). A patch that only changes what one caller SEES,
    // and only while our render is on the stack, cannot do that.
    private const int FsrDisableId = 20;

    private static void PatchFsrGate(HarmonyLib.Harmony harmony)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            var sm = Type.GetType("Keen.VRage.Render12.Core.Systems.SettingsManager, VRage.Render12");
            var mi = sm?.GetMethod("get_IsFSREnabledAndAllowed", Any);
            if (mi == null) { Log("IsFSREnabledAndAllowed not found — FSR gate unavailable."); return; }

            var post = typeof(RttPlugin).GetMethod(nameof(FsrAllowedPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, postfix: new HarmonyLib.HarmonyMethod(post));
            Log($"Patched SettingsManager.IsFSREnabledAndAllowed as override id {FsrDisableId}.");
        }
        catch (Exception e) { Log($"Patching the FSR gate FAILED: {e.Message}"); }
    }

    // Fail-open in the strictest sense: if the hook throws or is absent, the engine's own
    // answer stands.
    private static void FsrAllowedPostfix(ref bool __result)
    {
        try { if (RttBridge.SkipStageHook?.Invoke(FsrDisableId) == true) __result = false; }
        catch { }
    }

    // Id 25 — THE EXPOSURE BLEED FIX. A return-value override like 20, not a plain skip.
    //
    // Confirmed at a 2 s feed interval: the player's whole world darkens the instant our
    // render fires, then slowly re-adapts, and the bleed imprint rides the dark phase.
    // ComputeExposure runs in our nested Draw (stage 4 — not skippable, its out-params
    // feed bloom and tonemap, and skipping it NRE'd when tried). With EyeAdaptation
    // scoped off for our render it takes the ConstantExposure branch, and
    // ConstantExposure.hlsl writes float2(ConstantLuminance = a FIXED 1.0, exposure)
    // into the SHARED EyeAdaptationJob._autoExposures ping-pong — a constant stamped
    // into the player's adaptation history ten times a second. It also Resets and
    // re-primes the shared readback buffers while it is at it.
    //
    // The fix: for OUR render only, skip the method body entirely and hand back the
    // job's EXISTING Exposure view. Read, never write. No new job (async PSO compile
    // raced the recorder — device removed), no new render targets (outside the engine's
    // AutoResourceState tracking — device removed). This creates nothing, so there is
    // nothing to race and no lifecycle to get wrong.
    //
    // MUST return a valid view when skipping: ComputeExposure's callers consume the
    // out-param, so a null here is the same NRE as skipping stage 4. Hence fail-open —
    // if the getter yields nothing, the original runs and we keep today's bug over a
    // crash.
    //
    // Known trade, accepted: the feed's brightness now follows the player's live
    // adaptation rather than a constant, and the wholeSceneExposure EV knob is inert
    // while this is on (it fed the branch we now skip).
    private const int ExposureReadOnlyId = 25;
    private static MethodInfo _miExposureGetter;

    private static void PatchExposureGate(HarmonyLib.Harmony harmony)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            var t = Type.GetType("Keen.VRage.Render12.PostProcessStage.EyeAdaptationJob, VRage.Render12");
            var mi = t?.GetMethod("ConstantExposure", Any);
            _miExposureGetter = t?.GetProperty("Exposure", Any)?.GetGetMethod(true);
            if (mi == null || _miExposureGetter == null)
            {
                Log("EyeAdaptationJob.ConstantExposure/Exposure not found — exposure gate unavailable.");
                return;
            }

            var pre = typeof(RttPlugin).GetMethod(nameof(ConstantExposurePrefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(pre));
            Log($"Patched EyeAdaptationJob.ConstantExposure as read-only override id {ExposureReadOnlyId}.");
        }
        catch (Exception e) { Log($"Patching the exposure gate FAILED: {e.Message}"); }
    }

    // ------------------------------------------------------------- ghost probes
    //
    // LOG-ONLY Harmony prefixes, hunting the phantom bleed. The bleed is proven to be our
    // render target reaching the player's frame (it scales with our render resolution and
    // survives every stage skip, all delivery paths, and the panel binding), so the leak
    // is in whatever MOVES or REBUILDS texture content — and CopyJob is the engine's
    // converting blit. The game's own deferred-assert log already shows
    // "Source and destination should have the same resolution" (CopyJob.DoWork) and
    // "_usedMaxResolution == Vector2.Zero" (ScreenBuffers.InitializeBuffers) firing while
    // the feed runs, so both sites get identity logging.
    //
    // Neither prefix changes behaviour: no skips, no result overrides. Each unique
    // src->dst pair logs once, capped, so the cost after warm-up is one HashSet lookup.
    private static readonly HashSet<string> _copyProbeSeen = new();
    private static int _copyProbeLines;

    private static void PatchGhostProbes(HarmonyLib.Harmony harmony)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            var copy = Type.GetType("Keen.VRage.Render12.PostProcessStage.CopyJob, VRage.Render12");
            var doWork = copy?.GetMethods(Any).FirstOrDefault(m => m.Name == "DoWork" && m.GetParameters().Length == 8);
            if (doWork != null)
            {
                var pre = typeof(RttPlugin).GetMethod(nameof(CopyProbePrefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(doWork, prefix: new HarmonyLib.HarmonyMethod(pre));
                Log("[probe] Patched CopyJob.DoWork — every unique src->dst copy logs once as [copyprobe].");
            }
            else Log("[probe] CopyJob.DoWork(8 args) not found — copy probe unavailable.");

            var sb = Type.GetType("Keen.VRage.Render12.Core.Systems.ScreenBuffers, VRage.Render12");
            var init = sb?.GetMethod("InitializeBuffers", Any);
            if (init != null)
            {
                var pre = typeof(RttPlugin).GetMethod(nameof(InitBuffersProbePrefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(init, prefix: new HarmonyLib.HarmonyMethod(pre));
                Log("[probe] Patched ScreenBuffers.InitializeBuffers — every (re)initialisation logs as [initprobe].");
            }
            else Log("[probe] ScreenBuffers.InitializeBuffers not found — init probe unavailable.");
        }
        catch (Exception e) { Log("[probe] Patching the ghost probes FAILED: " + e.Message); }
    }

    // args: (commandList, destination IRenderTargetView, source ITexture2DView, ...)
    private static void CopyProbePrefix(object[] __args)
    {
        try
        {
            if (_copyProbeLines >= 80) return;
            string src = ProbeDescribe(__args.Length > 2 ? __args[2] : null);
            string dst = ProbeDescribe(__args.Length > 1 ? __args[1] : null);
            bool ours = false;
            try { ours = RttBridge.InOurRenderHook?.Invoke() == true; } catch { }

            string key = (ours ? "O|" : "P|") + src + ">" + dst;
            lock (_copyProbeSeen)
            {
                if (!_copyProbeSeen.Add(key)) return;
                if (++_copyProbeLines == 80) { Log("[copyprobe] cap reached; further unique pairs unlogged."); return; }
            }
            Log($"[copyprobe] inOurRender={ours} src={src} dst={dst}");
        }
        catch { }
    }

    private static void InitBuffersProbePrefix(object __instance, object[] __args)
    {
        try
        {
            bool ours = false;
            try { ours = RttBridge.InOurRenderHook?.Invoke() == true; } catch { }
            Log($"[initprobe] ScreenBuffers.InitializeBuffers({(__args != null && __args.Length > 0 ? __args[0] : null)}) " +
                $"instance=#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(__instance):x8} inOurRender={ours}");
        }
        catch { }
    }

    // "{X:512 Y:512}#1a2b3c4d" — resolution plus the RESOURCE object's identity hash, the
    // same format the logic side prints for its own blit, so lines correlate across the
    // two logs. Uncached reflection is fine: the cap check above makes the probe free once
    // 80 unique pairs have been seen.
    private static string ProbeDescribe(object view)
    {
        if (view == null) return "null";
        try
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var res = view.GetType().GetProperty("Resource", Any)?.GetValue(view) ?? view;
            var r = res.GetType().GetProperty("Resolution", Any)?.GetValue(res);
            return $"{r}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(res):x8}";
        }
        catch { return "?"; }
    }

    private static bool ConstantExposurePrefix(object __instance, ref object __result)
    {
        try
        {
            if (RttBridge.SkipStageHook?.Invoke(ExposureReadOnlyId) != true) return true;
            __result = _miExposureGetter?.Invoke(__instance, null);
            return __result == null;    // no view -> fail open: run the original
        }
        catch { return true; }
    }

    private static void PatchSkippableStages(HarmonyLib.Harmony harmony, Type sds)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (int i = 0; i < SkippableStages.Length; i++)
        {
            var (typeName, name) = SkippableStages[i];
            if (name == null) continue;     // reserved id, patched elsewhere
            try
            {
                var owner = sds;
                if (typeName != null)
                {
                    owner = Type.GetType(typeName);
                    if (owner == null) { Log($"Skippable stage {typeName} not found."); continue; }
                }

                var mi = owner.GetMethod(name, Any);
                if (mi == null) { Log($"Skippable stage {owner.Name}.{name} not found."); continue; }

                // One prefix per id. Harmony cannot pass extra arguments to a shared
                // prefix, so each stage gets its own tiny method rather than a lookup by
                // stack inspection.
                var pre = typeof(RttPlugin).GetMethod("SkipStage" + i,
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (pre == null) { Log($"No SkipStage{i} prefix for {name}."); continue; }

                // Postfix pairs with StageEnter for the per-stage timing table (#63).
                // Looked up per id like the prefix; absence is loud but non-fatal.
                var post = typeof(RttPlugin).GetMethod("StageDone" + i,
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(pre),
                    postfix: post != null ? new HarmonyLib.HarmonyMethod(post) : null);
                Log($"Patched {owner.Name}.{name} as skippable stage {i}" +
                    (post == null ? " (no timing postfix)." : "."));
            }
            catch (Exception e) { Log($"Patching skippable stage {name} FAILED: {e.Message}"); }
        }
    }

    // A prefix returning false skips the original. Any exception must fall through to
    // "run it" — silently skipping an engine stage because our hook threw would be a
    // very hard failure to attribute.
    private static bool Skip(int id)
    {
        try
        {
            bool run = RttBridge.SkipStageHook?.Invoke(id) != true;
            if (run) StageEnter(id);
            return run;
        }
        catch { return true; }
    }

    // ---- PER-STAGE TIMING (task #63) ---------------------------------------------------
    // Start stamps live in a ThreadStatic slot so the enter/done pair of one stage always
    // meets on the thread that ran it (job-typed stages run off the render thread). A
    // postfix ALWAYS runs, even when the prefix skipped the original — StageDone therefore
    // keys on "was a start stamped", which only happens for stages that actually ran
    // inside our pass. FieldInfo writes on [ThreadStatic] never reach other threads'
    // slots, but these are native reads/writes on the OWNING thread — the reflection trap
    // does not apply.
    [ThreadStatic] private static long[] _stageStart;

    private static void StageEnter(int id)
    {
        try
        {
            if (RttBridge.InOurRenderHook?.Invoke() != true) return;
            (_stageStart ??= new long[34])[id] = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        catch { }
    }

    private static void StageDone(int id)
    {
        try
        {
            var s = _stageStart;
            if (s == null || s[id] == 0) return;
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - s[id];
            s[id] = 0;
            System.Threading.Interlocked.Add(ref RttBridge.StageTicks[id], dt);
            System.Threading.Interlocked.Increment(ref RttBridge.StageRuns[id]);
        }
        catch { }
    }

    private static bool SkipStage0() => Skip(0);
    private static bool SkipStage1() => Skip(1);
    private static bool SkipStage2() => Skip(2);
    private static bool SkipStage3() => Skip(3);
    private static bool SkipStage4() => Skip(4);
    private static bool SkipStage5() => Skip(5);
    private static bool SkipStage6() => Skip(6);
    private static bool SkipStage7() => Skip(7);
    private static bool SkipStage8() => Skip(8);
    private static bool SkipStage9() => Skip(9);
    private static bool SkipStage10() => Skip(10);
    private static bool SkipStage11() => Skip(11);
    private static bool SkipStage12() => Skip(12);
    private static bool SkipStage13() => Skip(13);
    private static bool SkipStage14() => Skip(14);
    private static bool SkipStage15() => Skip(15);
    private static bool SkipStage16() => Skip(16);
    private static bool SkipStage17() => Skip(17);
    private static bool SkipStage18() => Skip(18);
    private static bool SkipStage19() => Skip(19);
    private static bool SkipStage21() => Skip(21);
    private static bool SkipStage22() => Skip(22);
    private static bool SkipStage23() => Skip(23);
    private static bool SkipStage24() => Skip(24);
    private static bool SkipStage26() => Skip(26);
    private static bool SkipStage27() => Skip(27);
    private static bool SkipStage28() => Skip(28);
    private static bool SkipStage29() => Skip(29);
    private static bool SkipStage30() => Skip(30);

    // Timing postfixes, one per patched stage (#63). Harmony runs a postfix even when the
    // prefix skipped the original; StageDone keys on the start stamp, so skipped stages
    // and player-frame runs cost one array read and return.
    private static void StageDone0() => StageDone(0);
    private static void StageDone1() => StageDone(1);
    private static void StageDone2() => StageDone(2);
    private static void StageDone3() => StageDone(3);
    private static void StageDone4() => StageDone(4);
    private static void StageDone5() => StageDone(5);
    private static void StageDone6() => StageDone(6);
    private static void StageDone7() => StageDone(7);
    private static void StageDone8() => StageDone(8);
    private static void StageDone9() => StageDone(9);
    private static void StageDone10() => StageDone(10);
    private static void StageDone11() => StageDone(11);
    private static void StageDone12() => StageDone(12);
    private static void StageDone13() => StageDone(13);
    private static void StageDone14() => StageDone(14);
    private static void StageDone15() => StageDone(15);
    private static void StageDone16() => StageDone(16);
    private static void StageDone17() => StageDone(17);
    private static void StageDone18() => StageDone(18);
    private static void StageDone19() => StageDone(19);
    private static void StageDone21() => StageDone(21);
    private static void StageDone22() => StageDone(22);
    private static void StageDone23() => StageDone(23);
    private static void StageDone24() => StageDone(24);
    private static void StageDone26() => StageDone(26);
    private static void StageDone27() => StageDone(27);
    private static void StageDone28() => StageDone(28);
    private static void StageDone29() => StageDone(29);
    private static void StageDone30() => StageDone(30);

    // __0 is the DirectCommandList both passes take as their first parameter.
    // Running in the postfix means the engine has finished with that pass, so the
    // list is still open but its work is recorded.
    private static void ProbePassPostfix(object __instance, object __0)
    {
        try { RttBridge.SceneDrawHook?.Invoke(__instance, __0, 0); } catch { }
    }

    private static void FramePassPostfix(object __instance, object __0)
    {
        try { RttBridge.SceneDrawHook?.Invoke(__instance, __0, 1); } catch { }
    }

    // __instance is the LcdContentRendererSessionComponent — required by
    // LcdPanelSurfaceContext.SetNewScreenMaterialHandle, which is how a panel is
    // pointed at a different render target.
    private static void PanelRenderPostfix(object __instance, object __0, object __1)
    {
        try { RttBridge.PanelRenderHook?.Invoke(__instance, __0, __1); } catch { }
    }

    private static void TickPostfix(object __instance)
    {
        try { RttBridge.TickHook?.Invoke(__instance); } catch { }
    }

    private void WorkerLoop()
    {
        Thread.Sleep(8000);
        while (true)
        {
            try { ReloadLogicIfChanged(); }
            catch (Exception e) { Log("ERROR worker: " + e.Message); }
            try { EnsureGpuProfilerSubscription(); }
            catch (Exception e) { if (_gpuSubErrs++ < 2) Log("ERROR gpu profiler subscribe: " + e.Message); }
            Thread.Sleep(2000);
        }
    }

    // ---- GPU PROFILER SUBSCRIPTION (task #65) -----------------------------------------
    // CoreSystems.GPUProfiler is populated during render init, after this plugin's ctor,
    // so the worker retries until it appears. The handler is a bootstrap static (this
    // assembly never unloads); the logic side receives the signal via the bridge field.
    private static bool _gpuSubscribed;
    private static int _gpuSubErrs;

    private static void EnsureGpuProfilerSubscription()
    {
        if (_gpuSubscribed) return;
        var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
        var prof = core?.GetField("GPUProfiler",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        if (prof == null) return;    // render not initialized yet; retry next tick
        var evt = prof.GetType().GetEvent("OnWatchesReady",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (evt == null) { _gpuSubscribed = true; Log("GPUProfiler.OnWatchesReady not found — no GPU forwarder."); return; }
        var handler = Delegate.CreateDelegate(evt.EventHandlerType, typeof(RttPlugin)
            .GetMethod(nameof(OnGpuWatchesReady), BindingFlags.Static | BindingFlags.NonPublic));
        evt.AddEventHandler(prof, handler);
        _gpuSubscribed = true;
        Log("GPU profiler forwarder subscribed (OnWatchesReady -> RttBridge.GpuWatchesReadyHook).");
    }

    private static void OnGpuWatchesReady()
    {
        try { RttBridge.GpuWatchesReadyHook?.Invoke(); } catch { }
    }

    private void ReloadLogicIfChanged()
    {
        if (!File.Exists(LogicPath))
        {
            if (_tick == null) Log("Waiting for logic dll to appear...");
            return;
        }
        var stamp = File.GetLastWriteTimeUtc(LogicPath);
        if (_tick != null && stamp == _loadedStamp) return;

        try
        {
            var old = _logicContext;

            // ASK THE OUTGOING ASSEMBLY TO LET GO FIRST. See RttBridge.ReloadRequested.
            //
            // Bounded, and it proceeds on timeout rather than blocking the reload forever: a
            // logic build whose render hook is not running (route disabled, feed dormant
            // before it ever rendered, an exception that disarmed it) would otherwise wedge
            // hot-reloading permanently. Leaking one generation of resources is recoverable;
            // a reload loop that never completes is not.
            if (old != null)
            {
                RttBridge.ReloadQuiesced = false;
                RttBridge.ReloadRequested = true;
                var until = DateTime.UtcNow.AddSeconds(3);
                while (!RttBridge.ReloadQuiesced && DateTime.UtcNow < until) Thread.Sleep(10);
                Log(RttBridge.ReloadQuiesced
                    ? "Hot reload: the outgoing logic released its GPU resources before unload."
                    : "!!! Hot reload: the outgoing logic did NOT confirm release within 3 s — unloading anyway. " +
                      "Its ScreenBuffers and DrawContextManager are now unreachable and will hold VRAM until " +
                      "the process exits. If this line repeats, expect a device removal.");
                RttBridge.ReloadRequested = false;
            }

            var ctx = new AssemblyLoadContext("RttProbeLogic_" + stamp.Ticks, isCollectible: true);
            Assembly asm;
            using (var ms = new MemoryStream(File.ReadAllBytes(LogicPath)))
            {
                var pdbPath = Path.ChangeExtension(LogicPath, ".pdb");
                if (File.Exists(pdbPath))
                {
                    using var pdb = new MemoryStream(File.ReadAllBytes(pdbPath));
                    asm = ctx.LoadFromStream(ms, pdb);
                }
                else asm = ctx.LoadFromStream(ms);
            }
            var entry = asm.GetType("RttProbe.LogicEntry");
            var install = entry?.GetMethod("Install", BindingFlags.Public | BindingFlags.Static);
            if (install == null)
            {
                Log("Logic dll loaded but RttProbe.LogicEntry.Install not found — keeping previous logic.");
                ctx.Unload();
                return;
            }
            install.Invoke(null, null);
            _logicContext = ctx;
            _tick = install;
            _loadedStamp = stamp;
            Log($"Logic loaded (build stamp {stamp:HH:mm:ss}). Hot-reload active.");
            old?.Unload();
        }
        catch (Exception e)
        {
            Log($"ERROR loading logic dll: {e.Message} — keeping previous logic.");
        }
    }

    private static readonly object LogGate = new();
    private static void Log(string msg)
    {
        try { lock (LogGate) File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] [boot] {msg}{Environment.NewLine}"); } catch { }
    }
}
