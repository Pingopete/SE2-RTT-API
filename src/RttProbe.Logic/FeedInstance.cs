using Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render.Contracts;

namespace RttProbe;

// PHASE C1a — THE INSTANCING SEAM.
//
// Everything the feed owns used to be a private static. That was correct while there
// was exactly one feed and it made the ownership rules easy to see, but it is the one
// thing standing between here and goals 3, 5 and 6: a second camera needs a second
// ScreenBuffers, a second DrawContextManager, a second LDR ring and a second panel
// binding, and none of that is expressible in a static field.
//
// WHY THIS SHAPE, and not a conventional "make the classes instance classes" refactor.
// The per-feed state is ~55 fields spread across seven files and roughly 7,800 lines,
// and it is read from several hundred call sites. Threading an instance parameter
// through all of them is a diff nobody can review and nothing can bisect: every one of
// those call sites is a chance to touch the wrong feed's state silently. So instead the
// FIELD becomes a same-named static PROPERTY over an instance field:
//
//     private static object _ourScreenBuffers;                              // before
//     private static object _ourScreenBuffers                               // after
//     { get => Feeds.Cur.OurScreenBuffers; set => Feeds.Cur.OurScreenBuffers = value; }
//
// Every existing read and write compiles and behaves identically, untouched. The whole
// refactor is then "delete a field, add a property" per item — mechanical, greppable,
// and reviewable field by field rather than site by site.
//
// WHAT IS NOT HERE, deliberately:
//
//   - Reflection caches (MethodInfo, FieldInfo, Type, PropertyInfo). These describe the
//     ENGINE'S types, not our feed. They are correctly process-global and resolving them
//     once per feed would be pure waste.
//   - Log latches and diagnostic counters (_xxxLogged, _errLogs, the diag HashSets).
//     These mean "we have already said this about the engine", which is a statement
//     about the process, not about a feed. Per-feed latches would multiply the log
//     volume by N for no information.
//   - Engine handles shared by every feed: the UISystem, the RenderContracts, the mip
//     job, the LCD material definitions that emissivity and the FSR mask are scoped
//     onto (those are SHARED DEFINITIONS — see FeedConfig.Emissivity; per-feed values
//     there are not possible without a per-feed material, which is not on the roadmap).
//   - Perf's buckets. The budget is global BY DESIGN (see docs/phase2-design.md): the
//     fixed total is the invariant, so the instrument that measures it must aggregate
//     across feeds, not per feed. Per-feed attribution is a phase E concern.
//
// C1a KEEPS EXACTLY ONE INSTANCE. That is the point: the seam lands and gets proven at
// parity (C2) before selection exists, so if the numbers move it is this transform and
// nothing else. The pump that chooses an instance per render is C1b.
internal sealed class FeedInstance
{
    // Stable identity for logs and, later, the scheduler's rotation order.
    public readonly int Id;
    public FeedInstance(int id) { Id = id; }

    // ---- WholeSceneRender: the second renderer's own globals ---------------------
    //
    // These are the objects that make the nested Draw a SECOND view rather than a
    // corruption of the player's. Rule 25 applies to every one of them: our teardown
    // may dispose only what this instance allocated.
    public object OurScreenBuffers;
    public bool SbBuilt;
    public object OurDrawContexts;
    public bool DcBuilt;

    // Consecutive failed attempts to build DcBuilt's manager. Per-feed so one feed's broken
    // build cannot latch another out of ever trying. See WholeSceneRender.NoteDcFailure.
    public int DcFailures;
    public object OurFreshShadowResources;
    public object OurFreshFlares;
    public object PanelSourceTex;
    public bool LdrResized;

    // 0 untried, 1 observed, -1 unavailable. Per-feed: one feed faulting must not take
    // the others down with it, which is precisely the graceful-cut contract in goal 7.
    public int RouteState;

    // Cadence. LastRenderMs and SettleFrames are what the phase E slot scheduler will
    // drive; RenderCount is the "has this feed ever produced an image" test PanelSource
    // depends on.
    public long LastRenderMs;
    public int SettleFrames;
    public int RenderCount;

    // THIS FEED'S OWN FRAME RATE — renders per second, sampled over ~1 s.
    //
    // Every other rate instrument in the mod is an aggregate: the PERF window, the stats
    // panel's headline fps and the watchdog's secondRenders all count what the MOD did, so
    // with two feeds they read the same whether the work is split evenly, split 8:1, or
    // being done entirely by one feed while the other is frozen. That is not a hypothetical
    // failure mode — it is the one this phase exists to catch, and on 2026-08-01 it also
    // sent me chasing a phantom uneven split that turned out to be a sampling artefact of
    // the per-feed status line riding a process-global 5 s timer.
    //
    // Sampled rather than instantaneous because a feed's turn comes round every N frames by
    // design: measured per frame it would read 0 or the full engine rate and never the truth
    // in between.
    public double RenderFps;
    private int _fpsLastCount;
    private long _fpsLastMs;

    internal void SampleFps(long now)
    {
        if (_fpsLastMs == 0) { _fpsLastMs = now; _fpsLastCount = RenderCount; return; }

        long dt = now - _fpsLastMs;
        if (dt < 1000) return;

        // RenderCount is zeroed by WholeSceneRender.Reset, so the delta can go negative
        // across a teardown. Report nothing rather than a nonsense spike.
        int d = RenderCount - _fpsLastCount;
        RenderFps = d < 0 ? 0.0 : d * 1000.0 / dt;
        _fpsLastMs = now;
        _fpsLastCount = RenderCount;
    }

    // Our own environment probe manager (goal 4.4). NOT disposed on a config change —
    // three device removals established that, see WholeSceneRender.Reset.
    public object OurProbes;
    public int ProbeState;
    public bool ProbeLogged;

    // Goal 11 cover-fit: derived render targets, one per distinct panel ASPECT, holding the
    // square feed resampled to that shape. PER FEED because the square they resample FROM is
    // per feed — sharing them across feeds would show one camera on another's panels.
    // Released in BlitProbe.Reset alongside Rt, which is the lifetime they must match.
    public Dictionary<int, BlitProbe.DerivedTarget> CoverTargets;

    // Our own RayTracingSceneManager — the feed's TLAS, built around the FEED camera by
    // stage 0 while it is installed. Same lifetime rules as OurProbes: NOT disposed on a
    // config change, and the authoritative copy lives in RttBridge.ParkedRayTracingScenes so
    // it survives hot reloads. This field is the per-load cache of that parked instance; a
    // reload starts it null and adoption refills it from the park.
    public object OurRtScene;
    // PER-FEED, discovered the hard way (2026-08-06): as a shared static, feed 1's stage-0
    // NRE would have read as "RT scene unavailable" for feed 0 too. 0 untried, 1 armed,
    // -1 unavailable/disarmed for this feed.
    public int RtSceneState;
    // Our own irradiance cache — per feed for the same reason the probe atlas is: the grid is
    // sampled around the camera, and two feeds in two places need two grids.
    public object OurIrCache;
    public int IrCacheState;
    // Whole-scene render faults this session, for the bounded retry before the route latch.
    public int WholeSceneFaults;

    // The flare mirror (goal 4.3). EngineFlares is a BORROWED reference — the engine's
    // context, re-read before every render. FlareOriginals holds our context's ctor
    // values so ScrubMirroredFlareRefs can put them back before we dispose: sharing a
    // reference INTO an object you later dispose makes you the owner of something you
    // did not allocate, which is Rule 25 and cost two crashes to learn.
    public object EngineFlares;
    public bool FlaresReady;
    public object[] FlareOriginals;

    // ---- CameraRender: the view, the ring and the panel target -------------------
    public object WsRenderView;
    public object WsResolution;

    // Previous-frame camera, for motion vectors. Unambiguously per-feed: feed B's
    // previous frame is not feed A's, and mixing them is a smear artefact.
    public object WsPrevCamPos;
    public object WsPrevCameraSettings;
    public object CbRenderView;

    // The LDR ring. Session-owned, three deep so the UI stage is never handed the slot
    // we are writing. RingIndex starts at -1 = nothing handed over yet; LdrMips defaults
    // to 1 and is raised to the panel's real mip count once known (the mip-chain fix —
    // mips 1..n used to hold recycled pool content).
    public readonly object[] LdrRing = new object[3];
    public object LdrReady;
    public int RingIndex = -1;
    public int LdrMips = 1;

    // Last reported power state of THIS feed's panel, so the log line fires on a change.
    //
    // PER-FEED, and the SIXTH instance of the same defect shape on this route (gate poll
    // throttle, startup flag, DC failure count, resumed-intact, delivery throttle, now this).
    // As a process-global string it worked perfectly until two feeds were in DIFFERENT power
    // states — which is exactly the state the whole graceful-cut contract creates. Feed 0
    // POWERED and feed 1 PowerOff then flipped the shared latch on every tick, so a
    // log-once-on-change line wrote ~97 lines/second into an 81 MB log, forever. Observed
    // 2026-08-01 11:53 during the panel-off test.
    public string PowerLog;

    // The panel we deliver to, and its shape.
    public object FeedTexture;
    public object FeedRes;
    public object FeedFormat;
    public object FeedComponent;
    public int FeedState;
    public string ResolvedPanelId;

    // Camera continuity.
    public object LastCamWorld;
    public object LastViewD;
    public Vector3D LastEye;
    public bool HaveLastEye;
    public long LastRender;
    public long FeedStartTicks;

    // ---- CameraFeed: what this feed is pointed at --------------------------------
    //
    // Volatile because the tick side publishes it and the render side reads it. A
    // reference assignment is atomic, which is why Target is a class — as a struct it
    // was torn mid-write and produced a one-frame fallback to the player's view.
    public volatile CameraFeed.Target Target;
    public object PanelRt;

    // Memoised grid bounds, keyed on BoundsGrid + BoundsAt. Two feeds on two grids
    // must not share one slot: the orbit radius is derived from Extent.
    public object BoundsGrid;
    public (Vector3D Centre, double Extent) BoundsCache;
    public long BoundsAt;

    // ---- FeedHandover: the parked frame ------------------------------------------
    public volatile object PendingFrame;      // Borrowed<T>
    public volatile object PendingResource;   // the texture itself
    public int ParkGeneration;
    public string PanelHandleText;

    // Last RequestPanelRender, for the per-TARGET rate limit. Per-feed because each feed
    // owns its own offscreen target and the throttle exists to keep ONE target from being
    // re-requested faster than the panel rate — not to admit one request per window across
    // the whole mod. As a process-global it made the two feeds compete for the window, and
    // since both cadences are locked to the engine frame clock the same feed kept winning
    // until a hitch shifted the phase: one panel live, one frozen, swapping in minute-long
    // stretches. User-observed 2026-07-30 23:2x; the FIFTH process-global-starves-a-feed
    // bug of the day (gate poll throttle, startup flag, DC failure count, resumed-intact,
    // now this).
    public long LastRequestRender;

    // DELIVERY PROOF, per feed (phase C3). These were process-global "log once" latches,
    // classified during the C1a inventory as statements about the ENGINE. They are not —
    // "this feed's frames reached its panel" is the most feed-specific fact in the mod, and
    // as a global latch feed 0 fires it first and feed 1's copy is swallowed forever.
    //
    // That is not merely untidy: it is what made the first black-panel diagnosis a guessing
    // game. A silent feed and a working feed produced identical logs.
    public bool HandoverSurvivedLogged;
    public int Handovers;
    public bool HandoverArgsLogged;
    public bool PanelRtDiag;
    public int CopyLogs;

    // RENDER-ON-DEMAND HANDSHAKE (task #25). The copy hook raises RenderWanted when it
    // lands a frame on the panel; TryRender consumes it and declines turns nobody will
    // deliver. Per-feed by construction — a process-global flag would be the SIXTH
    // global-starves-a-feed bug (see LastRequestRender above for the fifth).
    // MEASURED VERDICT (2026-08-07 stage table): leave the knob OFF at today's fps.
    // Any skipped frame between renders makes dispatch-stage recording go cache-cold
    // (MainView 0.91 -> 3.8 ms at gap-1), so on-demand's sparser cadence costs MORE
    // total CPU than every-frame. Revisit only if the engine's binding caches ever
    // become warm across gaps.
    public bool RenderWanted;
    public long LastCopyConsumedMs;

    // TLAS BUILD-ONCE (perf sprint): the owned RT scene is EMPTY by design, so stage 0
    // needs to run exactly once per adopted/constructed manager, not per render
    // (measured 0.39 ms/render rebuilt for nothing). Reset where _ourRtScene is
    // assigned; survives feed rebuilds because the parked manager they re-adopt does.
    public bool OwnTlasBuilt;

    // CONSECUTIVE view-lookup failures in CopyToFeed, and its own log budget. Per-feed for
    // the same reason CopyLogs is: feed 1 starts its render AFTER feed 0 is already warm, so
    // its "source not ready yet" window is a normal part of its startup and must not be
    // graded against feed 0's. A streak, not a count — one good pass zeroes it.
    public int ViewLookupFails;
    public int ViewLookupLogs;

    // ---- BlitProbe: the target and its batch -------------------------------------
    public OffscreenRenderTarget? Rt;
    public bool RtTried;
    public volatile bool FeedOwnsTarget;
    public PersistentDrawBatch PersistentBatch;
    public bool BatchRetired;

    // ---- FeedGate: this feed's liveness ------------------------------------------
    //
    // Per-feed from the start, because "panel A was ground down, feed B is untouched"
    // (goal 7 / phase F1) is exactly a per-feed gate and nothing else.
    public long LastPanelMs;
    public bool GateActive;
    public bool GateEverActive;
    public int GateCycles;
    public int TeardownIn = -1;

    // "This feed went active; log its startup from the render thread." Per-feed because
    // Startup() writes GateCycles and GateEverActive, which are per-feed — as one global
    // flag the first feed to drain it consumed every other feed's startup, so feed 1's
    // shutdown then reported "(Nothing had been started yet.)" while it was demonstrably
    // rendering. Caught by the unscoped-access detector the moment PumpAll moved outside
    // the render scope, which is precisely the job that detector exists to do.
    public bool PendingStartupLog;

    // "Came back before the teardown ran, so nothing was released." Per-feed for the same
    // reason PendingStartupLog is: Startup() runs once per feed under its own scope, and a
    // global flag would be consumed by the first feed and read false by every other — which
    // is the exact bug shape already fixed three times on this route tonight.
    public bool ResumedIntact;

    // ---- PanelBinding: the material binding --------------------------------------
    //
    // PHASE E2 FAN-OUT: a feed can display on SEVERAL panels, so the binding is a LIST
    // of weak (renderer, ctx) pairs — one entry per claiming panel — instead of the
    // single latch + pair it used to be. Weak for the same reason the pair was: a
    // destroyed panel must not keep its LCD material alive through us.
    //
    // A pair is appended at bind ATTEMPT (not success), preserving the old "one attempt
    // per panel per activation" semantics: the old code set _bound=true and stored the
    // pair before invoking the engine, so a failed bind was never retried and Unbind
    // still swept it. Same here, per panel.
    public readonly List<(WeakReference Renderer, WeakReference Ctx)> BoundPanels = new();

    // WHICH PANEL THIS FEED'S CAMERA FOLLOWS. First claimant wins: with two panels on
    // one feed, letting every tick publish the orbit target made the camera thrash
    // between the two panels' grids (last-claimant-wins, twice per frame). The feed's
    // identity — orbit target, captured panel RT, LastRenderComponent — follows the
    // panel that claimed it FIRST; later claimants are display-only mirrors. Cleared by
    // CameraFeed.Reset so a gate cycle re-elects from whatever is actually ticking, and
    // by CameraFeed.ExpireClaims the moment this panel itself stops ticking (phase F3).
    public string PrimaryPanelName;

    // Tagged panel names currently claiming this feed, each stamped with the last tick
    // that renewed it. Drives WantsRepaint: binding runs inside the content-render hook,
    // which an idle panel never enters, so repaints are forced while any claimant is
    // unbound.
    //
    // A DICTIONARY RATHER THAN A SET, because a claim has to be able to DIE (phase F3).
    // As a set, entries only ever left at a full gate cycle — so a mirror panel that was
    // ground down while the primary kept the feed alive stayed "claimed" for the rest of
    // the session, and WantsRepaint (alive binds < claimants) drove forced repaints
    // forever. Worse in the other direction: a destroyed PRIMARY kept the feed following
    // a panel that no longer exists, with no cycle coming to re-elect. The stamp is what
    // lets both heal without a teardown.
    public readonly Dictionary<string, long> ClaimedPanels = new(StringComparer.OrdinalIgnoreCase);

    // The render component of the panel this feed's identity follows. PER-FEED (phase F2)
    // — it was a CameraFeed static, so with two feeds the last one to tick owned it and
    // PanelBinding.TryBind refreshed the material replacements on somebody else's block.
    public object LastRenderComponent;

    // ---- the feed's OWN FinalLDR (2026-08-01) -------------------------------------
    //
    // OwnFinalLdr is a pool borrow at the FEED's resolution, under a per-feed name so it
    // cannot alias the player's swapchain-sized targets — which is what the phantom ghost
    // travelled through. Allocated once per feed and reused across gate cycles, because a
    // per-cycle borrow would just be a slower version of the realloc churn it replaces
    // (measured at 56 MB/min on the resize path).
    //
    // EngineFinalLdr is the buffer ScreenBuffers made for itself, held so it can be put back
    // before Dispose — otherwise Dispose frees OUR borrow and leaks the engine's.
    public object OwnFinalLdr;
    public object EngineFinalLdr;
}

// The registry, and the ambient that decides WHOSE state `Feeds.Cur` means.
//
// PHASE C1b. C1a moved the state onto instances; this decides which instance a given hook
// call refers to. That mapping is NOT uniform across our ten entry points, and assuming it
// was is the mistake this design exists to avoid — see docs/phase2-design.md:
//
//   - PANEL-driven (BlitProbe.OnTick, CameraFeed/StatsPanel.OnLcdTick, the two
//     OnPanelRender hooks): the ENGINE hands us a specific LCD component, renderer or
//     surface context, on its own schedule. The feed is whoever OWNS that panel — a
//     LOOKUP. A rotation here would hand panel A's tick to feed B.
//   - TARGET-driven (FeedHandover.OnOffscreenUiDraw): the engine hands us the offscreen
//     target being drawn. Also a lookup.
//   - SCHEDULER-driven (the whole-scene hooks, and the probe pass nested inside them):
//     nothing external names a feed, so WE choose. Phase E1's render slot, in embryo.
//
// STILL EXACTLY ONE INSTANCE. Every lookup and every pick resolves to it, so this stage
// builds the mechanism and leaves the answer alone — it cannot change behaviour. C3 adds
// the second instance, and the mechanism starts mattering.
internal static class Feeds
{
    // THE REGISTRY (phase C3). Slots are ALLOCATED eagerly and ACTIVATED by config.
    //
    // Allocating is free — a FeedInstance is plain fields, it touches no engine type, and
    // its ctor cannot run engine code. That matters more than it looks: reading a
    // CoreSystems static forces that type's cctor, and doing so during plugin load once
    // threw ConfigurationNotFoundException and permanently poisoned the type (see the
    // comment in WholeSceneRender.Reset). So the array is built from nothing at load, and
    // FeedConfig is never consulted at static-init time.
    //
    // Count is therefore a CONFIG READ, not an array length: feedCount clamps into the
    // slots that exist. That keeps N=1 the shipped default and makes the second feed a
    // live knob to switch off if it misbehaves, rather than a rebuild to revert.
    private const int MaxFeeds = 4;

    private static readonly FeedInstance[] All = CreateAll();

    private static FeedInstance[] CreateAll()
    {
        var a = new FeedInstance[MaxFeeds];
        for (int i = 0; i < MaxFeeds; i++) a[i] = new FeedInstance(i);
        return a;
    }

    // The feed that owns work not attributable to any specific one.
    internal static FeedInstance Primary => All[0];

    // ACTIVE feed count. Clamped hard: a typo in the config must not index past the slots
    // or drop to zero, because zero feeds means NextForRender has nothing to return and
    // every lookup would have to invent an answer.
    //
    // ALSO clamped by the VRAM admission cap (phase E1) — see UpdateResidentCap. The user
    // asks for N feeds; they get min(N, what fits). Deliberately the ONLY automatic
    // throttle in the system: quality stays a manual lever, by explicit user decision
    // ("i dont want the quality setting to be adjusted as an automatic thorttle"), so the
    // one thing the mod may decide on its own is how many feeds it will hold at once.
    internal static int Count
    {
        get
        {
            int n = FeedConfig.FeedCount;
            if (n < 1) n = 1;
            if (n > MaxFeeds) n = MaxFeeds;
            return n < _residentCap ? n : _residentCap;
        }
    }

    // ---- the VRAM admission cap (phase E1) ---------------------------------------
    //
    // WHY THIS EXISTS, from measurement rather than principle. Two feeds at 1024 SSAA were
    // run on 2026-07-30 and the game device-removed 40 s later, with UsedVRAM at 13.70 GB
    // against an AvailableVRAM budget of 13.61 GB. Nothing in the mod noticed. The analytic
    // resource walk then put a feed at 384.7 MiB, so "will the next feed fit" is arithmetic
    // we can do BEFORE building it instead of a crash we discover afterwards.
    //
    // The cap only ever bounds Count. It never tears a resident feed down on its own: the
    // config asking for fewer feeds is the user's decision and is honoured instantly, but a
    // VRAM dip must not start a teardown storm on the render thread. It bites where it is
    // cheap — at the moment a feed would be ADMITTED.
    private static int _residentCap = MaxFeeds;
    private static int _capRaiseVotes;
    private static int _lastLoggedCap = MaxFeeds;

    internal static int ResidentCap => _residentCap;

    // Per-feed footprint, split by what it scales with. ScreenBuffers (60.0 MiB) and the
    // RTGI temporal histories (32.0 MiB) scale with OUR pixel count; everything else —
    // entity instance buffers, light clustering, the rest of the DrawContextManager, the
    // panel-sized LDR ring — does not.
    //
    // THE TOTAL IS THE MEASURED MARGINAL COST, NOT THE ANALYTIC SUM. The resource walk in
    // docs/feed-resources-1024-charshadow256.txt adds up to 384.7 MiB and says in its own
    // output that it is a LOWER BOUND, because it prints an UNSIZED list of types it cannot
    // measure. The observed cost of switching feedCount 1 -> 2 on 2026-07-30 was
    // 12.20 -> 12.78 GB, i.e. ~580 MB. Using the analytic figure here would let the cap
    // admit feeds ~1.5x smaller than they really are, which is the precise failure this cap
    // exists to prevent — so the number that decides admission is the one that was watched
    // happening, per Rule 26. The walk stays valuable for finding WHAT to cut; it is just
    // not the right input for "does another one fit".
    //
    // The structural remainder is the important number for the roadmap: no quality preset
    // can remove it, because owning a second culling context means owning scene-sized
    // buffers. It is the floor under max-resident-feeds.
    private const double ResScaledMbAt1024 = 92.0;
    private const double StructuralMb      = 488.0;

    private static double PerFeedMb()
    {
        double px = (double)FeedConfig.WholeSceneWidth * FeedConfig.WholeSceneHeight;
        return StructuralMb + ResScaledMbAt1024 * (px / (1024.0 * 1024.0));
    }

    // Feeds actually holding GPU resources right now. SbBuilt is the honest test — a slot
    // that is configured but has never built its ScreenBuffers costs nothing, so counting
    // configured feeds instead would reserve memory for feeds that are not there.
    private static int ResidentCount()
    {
        int n = 0;
        for (int i = 0; i < MaxFeeds; i++) if (All[i].SbBuilt) n++;
        return n;
    }

    // Called from FeedConfig.Poll (every 2 s), INSIDE the rebuild-signature window so a cap
    // change re-routes panels through exactly the same machinery a feedCount change does.
    internal static void UpdateResidentCap()
    {
        int userCap = FeedConfig.MaxResidentFeeds;
        if (userCap < 1) userCap = 1;
        if (userCap > MaxFeeds) userCap = MaxFeeds;

        if (!FeedConfig.FeedVramGuard) { ApplyCap(userCap, "guard off"); return; }

        long usedMb = Perf.SampleVramMb(), availMb = Perf.SampleVramAvailMb();

        // NO READING IS NOT ZERO HEADROOM. Perf returns 0 before the first frame and
        // whenever VideoMemoryMonitor cannot be resolved; treating that as "nothing fits"
        // would clamp every feed away during startup, when the cap has nothing useful to
        // say anyway. Fall back to the user's ceiling and let them own the decision.
        if (usedMb <= 0 || availMb <= 0) { ApplyCap(userCap, "no VRAM reading"); return; }

        int resident = ResidentCount();
        double perFeed = PerFeedMb();
        long headroom = availMb - usedMb - FeedConfig.FeedVramReserveMb;
        int extra = headroom <= 0 ? 0 : (int)(headroom / perFeed);

        int fits = resident + extra;
        if (fits < 1) fits = 1;              // never cap the last feed away
        int want = fits < userCap ? fits : userCap;

        // ASYMMETRIC HYSTERESIS. Lowering is immediate — it is the safety direction, and a
        // late clamp is the crash it exists to prevent. Raising needs three consecutive
        // polls (~6 s) to agree, because VRAM swings +/-200 MiB frame to frame (measured
        // during the failed B1/D3 sweeps) and a cap that flaps across the requested count
        // would trigger a rebuild every time it moved.
        if (want < _residentCap) { _capRaiseVotes = 0; ApplyCap(want, Why(headroom, perFeed, resident, availMb, usedMb)); }
        else if (want > _residentCap)
        {
            if (++_capRaiseVotes >= 3) { _capRaiseVotes = 0; ApplyCap(want, Why(headroom, perFeed, resident, availMb, usedMb)); }
        }
        else _capRaiseVotes = 0;
    }

    private static string Why(long headroom, double perFeed, int resident, long availMb, long usedMb) =>
        $"used {usedMb} MB of a {availMb} MB budget, reserve {FeedConfig.FeedVramReserveMb} MB, " +
        $"headroom {headroom} MB, {resident} feed(s) resident at ~{perFeed:F0} MB each";

    private static void ApplyCap(int cap, string why)
    {
        _residentCap = cap;
        if (cap == _lastLoggedCap) return;
        _lastLoggedCap = cap;

        // Loud on the way down, because a silently reduced feed count is indistinguishable
        // from a broken feed — a black panel with every counter reading healthy is the
        // single most expensive failure shape this project has produced.
        RttLog.Line($"FEED VRAM CAP: max resident feeds = {cap} ({why}). " +
                    (cap < FeedConfig.FeedCount
                        ? $"feedCount={FeedConfig.FeedCount} is being CLAMPED to {cap} — the extra feed(s) will not be built."
                        : "not currently limiting anything."));
    }

    // Enumerate the ACTIVE feeds. Anything sweeping the registry uses this, so shrinking
    // feedCount stops touching the retired slots immediately — their resources are then
    // released by the same gate-shutdown path a dormant panel uses, which is the only
    // quiesced moment the renderer offers.
    internal static FeedInstance At(int i) => All[i];

    // THE AMBIENT. ThreadStatic because the LCD tick can legitimately be on feed A while
    // the render thread is on feed B, at the same instant — which is exactly why C1a (where
    // Cur was a constant and both threads saw one object) was graded at parity BEFORE this
    // landed. Null means "no pump has claimed this thread": a bug to be found, not a state
    // to rely on. See Unscoped().
    [ThreadStatic] private static FeedInstance _ambient;

    // AGGRESSIVELY INLINED, and not cargo-culted. This sits in front of state that hot paths
    // touch — ShouldSkipStage runs per stage per render, the LDR ring is indexed through it,
    // and the render path reads a dozen of these per frame. A ThreadStatic read plus a null
    // test is a few nanoseconds and the JIT folds the accessor away, but the attribute makes
    // that a guarantee rather than a hope. Measured context: on the C1a build ourDraw held
    // 2.4-2.7 ms across a 3x swing in engine frame time, so the seam was already free — this
    // keeps it free now that a real indirection has replaced the constant.
    internal static FeedInstance Cur
    {
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get => _ambient ?? Unscoped();
    }

    // ---- the unscoped-access diagnostic ------------------------------------------
    //
    // Falling back to Primary is SAFE while Count == 1 (it is the same object every lookup
    // would return) and WRONG the moment C3 adds a feed, because an unscoped path would
    // silently operate on feed 0's state whichever feed it actually belongs to.
    //
    // So the fallback is deliberately both things at once. It keeps the mod running — a null
    // here would NRE on the render thread every frame, and "never device-remove" outranks
    // "fail loudly" — and it reports itself. The FIRST occurrence captures a stack trace,
    // which turns "some path is unscoped" into the exact file and line needing a scope.
    // That log line IS the C3 to-do list, written by the code instead of by me guessing
    // which of the ten entry points I missed.
    private static bool _unscopedLogged;
    private static int _unscopedCount;
    private static bool _selfTesting;
    internal static int UnscopedCount => _unscopedCount;

    private static FeedInstance Unscoped()
    {
        _unscopedCount++;
        if (!_unscopedLogged)
        {
            _unscopedLogged = true;
            string where;
            try { where = new System.Diagnostics.StackTrace(1, true).ToString(); }
            catch { where = "(stack unavailable)"; }
            RttLog.Line(_selfTesting
                ? "Feeds: (SELF-TEST) deliberate unscoped access — this is the diagnostic proving " +
                  "it can fire, NOT a real finding. The stack below is the self-test's own.\n" + where
                : "Feeds: per-feed state touched with NO ambient instance set — falling " +
                  "back to feed 0. HARMLESS while there is one feed, since every lookup " +
                  "returns that same object; WRONG as soon as there are two, because this " +
                  "path would operate on feed 0 whichever feed it belongs to. Scope it " +
                  "before C3. Logged once; running total in Feeds.UnscopedCount.\n" + where);
        }
        return All[0];
    }

    // PROVE THE DIAGNOSTIC WORKS, every load, before trusting its silence.
    //
    // At Count == 1 a scoped access and an unscoped one are behaviourally IDENTICAL — both
    // resolve to feed 0 — so "the log is clean" is equally consistent with "every path is
    // scoped" and with "the detector is broken". Trusting the second reading is how C3 would
    // start with false confidence, and this project has already paid for the general version
    // of that mistake: a mechanism is only real once it has been observed FIRING (Rule 26,
    // which is also what condemned the dead probe-dispose queue).
    //
    // So: call this from Install, which runs BEFORE any pump has claimed the thread and is
    // therefore genuinely unscoped. It exercises the whole path — the null test, the counter,
    // the StackTrace capture and RttLog — then re-arms so a real unscoped access still gets
    // reported with its own stack.
    internal static void SelfTest()
    {
        _selfTesting = true;
        int before = _unscopedCount;
        _ = Cur;
        bool fired = _unscopedCount > before;
        _selfTesting = false;

        _unscopedLogged = false;
        _unscopedCount = 0;

        RttLog.Line(fired
            ? "Feeds: unscoped-access diagnostic SELF-TEST PASSED — an unscoped read was " +
              "detected and reported, so a SILENT log from here on is real evidence that every " +
              "per-feed access is properly scoped. Counter re-armed."
            : "Feeds: unscoped-access diagnostic SELF-TEST FAILED — an access with no ambient " +
              "set was NOT detected. The detector is broken, so its silence means nothing and " +
              "C3 must not rely on it. Fix this before adding a second feed.");
    }

    // ---- scoping -----------------------------------------------------------------

    // Restores the PREVIOUS ambient rather than null, so nesting is safe — the probe pass
    // fires inside the whole-scene render, which is already scoped.
    internal readonly struct Scope : IDisposable
    {
        private readonly FeedInstance _prev;
        internal Scope(FeedInstance f) { _prev = _ambient; _ambient = f; }
        public void Dispose() => _ambient = _prev;
    }

    internal static Scope Enter(FeedInstance f) => new Scope(f ?? All[0]);

    // Run body once per ACTIVE feed, each under its own ambient. For whole-registry sweeps
    // — config rebuilds, teardowns. Snapshots Count first so a config edit landing mid-sweep
    // cannot change the bound underneath the loop.
    internal static void ForEach(Action body)
    {
        int n = Count;
        for (int i = 0; i < n; i++)
            using (Enter(All[i]))
                body();
    }

    // EVERY slot, active or not. The distinction is not cosmetic:
    //
    //   sweeping to RUN something covers the ACTIVE feeds  -> ForEach
    //   sweeping to RELEASE something covers ALL slots     -> ForEachSlot
    //
    // A slot that has just been retired (feedCount 2 -> 1, or the VRAM cap clamping) is
    // precisely the one still holding a ScreenBuffers and a DrawContextManager that nothing
    // will ever ask for again. Sweeping it with ForEach skips it at exactly the moment its
    // resources became garbage, and there is no later pass that would catch it — the feed is
    // outside Count from then on, so it is invisible to every subsequent sweep.
    //
    // Untouched slots cost nothing to visit: their state is null and their countdowns are
    // -1, so every release path returns immediately.
    internal static void ForEachSlot(Action body)
    {
        for (int i = 0; i < MaxFeeds; i++)
            using (Enter(All[i]))
                body();
    }

    // ---- the two selectors --------------------------------------------------------

    // THE RENDER SLOT (phase E1, in embryo): at most one render per engine frame, strict
    // cyclic rotation. Peek and advance are SEPARATE on purpose — the rotation must move
    // when a render actually happens, not on every frame, or a feed that declines its turn
    // (rate-gated) would hand its slot away permanently.
    private static int _slot;

    // ---- ELIGIBILITY, and the bug it exists to kill (phase F1) --------------------
    //
    // "Keep your turn until you render" is right for a feed that declines TRANSIENTLY and
    // catastrophic for one that cannot render AT ALL. The slot advances only inside
    // TryRender, after a completed render — and OnWholeSceneScoped returns above TryRender
    // on a dormant gate or a faulted route. So the moment ANY feed went dormant — its panel
    // destroyed, ground down, unpowered, switched off — the slot parked on it and EVERY
    // OTHER FEED STOPPED RENDERING, permanently, with no error anywhere. The survivors'
    // panels simply froze on their last delivered frame while every counter read healthy.
    //
    // That is the exact failure shape this project keeps paying for: a process-global
    // arbiter that a single feed can hold forever. The fix is to take the feeds that
    // CANNOT render out of the rotation rather than let one of them own it.
    //
    // The two ways a feed is structurally unable to take a turn:
    //   - its gate is dormant (no tagged panel ticking — the whole graceful-cut contract)
    //   - its route faulted (RouteState -1: the hook threw and latched itself off)
    //
    // NOT in the list, and each for a reason worth keeping:
    //
    //   the RATE GATE is time-based and per-feed, so a rate-gated feed's turn comes back on
    //   its own schedule and nobody is starved by it;
    //
    //   "has not built its ScreenBuffers yet" — a new feed needs slots precisely IN ORDER to
    //   build them; the build runs in the slot-scoped hook, above TryRender;
    //
    //   SETTLING, which was in this list for one draft and was wrong twice over. The build
    //   is lazy (EnsureScreenBuffers, in the slot-scoped hook), so a settling feed that gets
    //   no slots does not rebuild either — the settle window would elapse BEFORE the rebuild
    //   and the first render would land immediately after it, which is exactly the ordering
    //   the window exists to prevent. And the probe reprocess it waits for is the SHARED
    //   EnvironmentProbeManager's: no feed should render into it, so a settling feed holding
    //   the rotation is protective rather than rude. See TryRender's Feeds.AnySettling.
    private static bool Eligible(FeedInstance f) =>
        f.GateActive && f.RouteState != -1;

    // Is any LIVE feed inside its post-rebuild settle window? The probe reprocess a rebuild
    // triggers is engine-wide, so the answer that matters to "may a second render run right
    // now" is global, even though the countdown itself is per feed.
    //
    // GATE-ACTIVE IS PART OF THE QUESTION, and leaving it out deadlocked the whole mod
    // (2026-08-01 11:36, secondRenders stuck at 0 with both panels frozen). FeedConfig's
    // first poll resets EVERY SLOT — ForEachSlot, correctly, since a retired slot is exactly
    // the one holding resources nobody will ask for again — and Reset arms the settle window.
    // So slots 2 and 3, which have never held a feed and never will unless feedCount rises,
    // sat at 30 frames forever: they cannot drain, because TickSettle only drains a feed
    // whose gate is active, and their gate never is.
    //
    // Two changes made in the same commit, each right on its own, combining into a permanent
    // false answer. A dormant slot is not settling — it is parked, it renders nothing and it
    // triggers no reprocess — so it has no vote here.
    internal static bool AnySettling()
    {
        for (int i = 0; i < MaxFeeds; i++)
            if (All[i].GateActive && All[i].SettleFrames > 0) return true;
        return false;
    }

    // THE MASTER DORMANCY FLAG (task #40, the zero-dormant-overhead mandate). True while
    // ANY feed's gate is active; read by every world-side system and hot hook that must
    // stop costing anything when no panel displays a feed: the clipmap camera redirection
    // and budget, the flora-camera override, the nearest-viewer distance delegate, the
    // presence/trigger/preload residency work. A volatile recomputed once per PollAll —
    // hot paths pay one field read, transitions land within a poll period. Distinct from
    // FeedGate.Paused (the whole-mod file lever) and from per-feed GateActive: this is
    // "is there any consumer at all".
    internal static volatile bool AnyLive;

    internal static void RecomputeAnyLive()
    {
        bool any = false;
        for (int i = 0; i < MaxFeeds; i++)
            if (All[i].GateActive) { any = true; break; }
        AnyLive = any;
    }

    // Scan forward from the rotation origin for the first feed that can actually use the
    // slot. Pure: same answer for the prefix, the camera pass and the postfix of one frame,
    // which is the invariant those three hooks rely on. If nobody is eligible the origin is
    // returned unchanged — the callers all early-out on their own gate checks a moment
    // later, and inventing a different answer would only move the no-op somewhere less
    // obvious.
    internal static FeedInstance NextForRender()
    {
        int n = Count;
        int from = _slot % n;
        for (int i = 0; i < n; i++)
        {
            var f = All[(from + i) % n];
            if (Eligible(f)) return f;
        }
        return All[from];
    }

    // Modulo the LIVE Count, so shrinking feedCount cannot strand the rotation on a slot
    // that is no longer active.
    internal static void AdvanceSlot() => _slot = (_slot + 1) % Count;

    // ---- the rotation watchdog (phase F1) ----------------------------------------
    //
    // Eligibility covers the three causes above. This covers the ones not yet imagined: any
    // state where the effective holder keeps its turn indefinitely without rendering —
    // ScreenBuffers that never build, a DrawContextManager that fails forever, a rate gate
    // misconfigured past sanity. It cannot fix the underlying stall, but it stops that stall
    // spreading from one feed to all of them, which is the difference between a degraded
    // feed and a dead mod.
    //
    // Deliberately silent when only ONE feed is eligible: a single feed legitimately holds
    // every frame, and warning about that would be noise on the common case.
    private const long StallFloorMs = 2000;
    private static int _heldBy = -1;
    private static long _heldSince;
    private static int _stallLogs, _lastRotation = -1;

    // Once per engine frame, from FeedGate.PumpAll — OUTSIDE every feed scope, so every
    // line it writes is Global. Ambient-free by construction: Eligible reads the instances
    // directly rather than through Cur.
    internal static void TickRenderSlot()
    {
        int n = Count, mask = 0, live = 0;
        for (int i = 0; i < n; i++)
            if (Eligible(All[i])) { mask |= 1 << i; live++; }

        // Every slot, not just the active ones: a feed that has just stopped needs one more
        // sample to fall to zero rather than freezing at its last good rate.
        long sampleAt = Clock.Ms;
        for (int i = 0; i < MaxFeeds; i++) All[i].SampleFps(sampleAt);

        // THE ROTATION SET CHANGING IS THE HEADLINE EVENT of a feed being lost or regained,
        // so it is stated once, plainly, with the reason per feed. Without this line the
        // only symptom of a feed leaving is a panel that quietly stops updating — which is
        // indistinguishable from a bug in everything else we have built.
        if (mask != _lastRotation)
        {
            int was = _lastRotation;
            _lastRotation = mask;
            if (was >= 0)
                RttLog.Global($"FEED ROTATION: {RotationLine()}. " +
                    (live == 0
                        ? "No feed can take a render slot — the mod is idle until one comes back."
                        : $"{live} feed(s) now share the render slot, so each renders every {n}/{live} " +
                          "engine frame(s): whatever the departed feed was using is absorbed by the rest."));
        }

        var f = NextForRender();
        if (f.Id != _heldBy) { _heldBy = f.Id; _heldSince = Clock.Ms; return; }

        // Scaled by the render period: at wholeSceneIntervalMs = 500 a feed legitimately
        // holds its turn for half a second, and a watchdog that fired inside that window
        // would be fighting the cadence knob rather than a stall.
        long limit = Math.Max(StallFloorMs, 3L * FeedConfig.WholeSceneIntervalMs);
        if (Clock.Ms - _heldSince < limit) return;
        _heldSince = Clock.Ms;
        if (live < 2) return;                     // nobody else wants it

        if (_stallLogs++ < 5)
            RttLog.Global($"!!! FEED ROTATION STALL: feed {f.Id} has held the render slot for " +
                          $"{limit} ms without completing a render while {live - 1} other eligible " +
                          "feed(s) waited. Rotating past it so the others keep running — but it is " +
                          "eligible and not rendering, which is a fault of its own worth finding. " +
                          RotationLine());
        AdvanceSlot();
    }

    // "0=render 1=dormant" — the per-feed state of the rotation, for the lines above and
    // for the health watcher.
    internal static string RotationLine()
    {
        var sb = new System.Text.StringBuilder();
        int n = Count;
        for (int i = 0; i < n; i++)
        {
            var f = All[i];
            if (i > 0) sb.Append(' ');
            sb.Append(f.Id).Append('=').Append(
                !f.GateActive ? (FeedConfig.IsFeedDisabled(f.Id) ? "disabled" : "dormant")
                : f.RouteState == -1 ? "faulted"
                : f.SettleFrames > 0 ? "settling"
                : "render");
        }
        return sb.ToString();
    }

    // EACH FEED'S OWN FRAME RATE, short enough for the stats panel: "0:26.6  1:25.9".
    //
    // A feed that is not rendering shows WHY instead of a number, because 0.0 does not
    // distinguish "switched off" from "faulted" from "stuck", and those want different
    // reactions. The states are the same ones RotationLine reports to the log.
    internal static string FeedFpsLine()
    {
        var sb = new System.Text.StringBuilder();
        int n = Count;
        for (int i = 0; i < n; i++)
        {
            var f = All[i];
            if (i > 0) sb.Append("  ");
            sb.Append(f.Id).Append(':');
            if (!f.GateActive) sb.Append(FeedConfig.IsFeedDisabled(f.Id) ? "dis" : "off");
            else if (f.RouteState == -1) sb.Append("ERR");
            else if (f.SettleFrames > 0) sb.Append("set");
            else sb.Append(f.RenderFps.ToString("F1"));
        }
        return sb.ToString();
    }

    // Is any feed OTHER than the current one actually PRODUCING FRAMES right now?
    //
    // Not "resident" and not "gate active" — those are both true of a feed that is built but
    // still settling, which is the state every feed is in during the normal all-feeds-start-
    // together burst at world load. The question this answers is narrower and is the one that
    // matters for admitting a rebuild: is another feed's work in flight on the GPU.
    //
    // RenderCount is cleared by Reset, so after a quiesce every feed reads false here and the
    // rebuild burst that follows cannot re-trigger the quiesce that produced it.
    internal static bool AnyOtherRendering()
    {
        var me = Cur;
        for (int i = 0; i < MaxFeeds; i++)
        {
            var f = All[i];
            if (!ReferenceEquals(f, me) && f.SbBuilt && f.RenderCount > 0) return true;
        }
        return false;
    }

    // Is any feed OTHER than the current one still live or still holding GPU resources?
    //
    // The question SHARED engine state has to ask before a single feed's shutdown undoes it
    // (phase F2). SbBuilt is included as well as the gate, because a feed that went dormant
    // this frame still owns its buffers until its own teardown runs — it is coming back or
    // being released, and either way it is not gone yet.
    internal static bool OthersLive()
    {
        var me = Cur;
        for (int i = 0; i < MaxFeeds; i++)
        {
            var f = All[i];
            if (!ReferenceEquals(f, me) && (f.GateActive || f.SbBuilt)) return true;
        }
        return false;
    }

    // LOOKUP for the panel- and target-driven hooks (phase C3). The C1b stubs returned
    // All[0]; these now resolve real ownership. See FeedRouter for why claims are made on
    // the FIRST tick rather than settling, and why they are keyed on the panel NAME rather
    // than on a component reference the engine recreates.
    internal static FeedInstance ForPanel(object renderComponent) =>
        Count == 1 ? All[0] : FeedRouter.ForComponent(renderComponent);

    internal static FeedInstance ForTarget(object targetComponent) =>
        Count == 1 ? All[0] : FeedRouter.ForTargetComponent(targetComponent);

    // The panel-render hook is handed a surface context, which has no name to parse — it is
    // claimed during discovery instead. See FeedRouter.ClaimSurface.
    internal static FeedInstance ForSurface(object surfaceCtx) =>
        Count == 1 ? All[0] : FeedRouter.ForSurface(surfaceCtx);
}
