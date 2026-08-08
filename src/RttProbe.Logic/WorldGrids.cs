using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Keen.VRage.Library.Mathematics;

namespace RttProbe;

// THE WORLD WALK — phase J's missing instrument.
//
// Every render this project has ever done sat ~100 m from the player, because the orbit
// centres on the tagged panel's own grid by construction. That is precisely why nothing has
// ever disappeared from a feed, and why docs/open-question-remote-streaming.md has been
// unable to close: "we saw only planets and stars at 50 km" was consistent with BOTH
// "streaming dropped it" and "there was nothing there to begin with".
//
// This file supplies the two pieces that let the question be asked properly:
//
//   DumpGrids()     — an inventory: every grid in the scene, with position and distance,
//                     so a target can be CHOSEN rather than guessed at.
//   ResolveAnchor() — turn a name from that inventory into an orbit centre.
//
// Read-only with respect to the engine. It enumerates and reflects; it never writes engine
// state, so it cannot participate in the shared-state bug class that has cost this project
// eight instances so far.
internal static class WorldGrids
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly string ReportPath = Path.Combine(RttLog.OutDir, "world-grids.txt");

    internal readonly struct GridInfo
    {
        public readonly string Name;
        public readonly Vector3D Position;
        public readonly double Extent;
        public readonly object Entity;

        public GridInfo(string name, Vector3D position, double extent, object entity)
        { Name = name; Position = position; Extent = extent; Entity = entity; }
    }

    // RESOLUTION IS CACHED, AND THE CACHE IS KEYED BY THE QUERY.
    //
    // Keying on the anchor string means an edited config invalidates the cache
    // automatically rather than relying on someone remembering to clear it. A resolved HIT
    // is then final: the position is refreshed each call from the CACHED ENTITY (a handful
    // of reflection reads), never by re-walking the scene. The first version re-walked all
    // 34k entities every 5 s on the tick thread as its idea of freshness — the user asked,
    // reasonably, why a discovery pass was running continuously for a camera that had
    // already found its target. Only a dead entity (position read fails) re-arms the walk,
    // and misses retry on a slow cadence in case the target simply has not spawned yet.
    private static string _cachedFor;
    private static GridInfo? _cached;
    private static long _lastWalkTicks;
    private static readonly HashSet<string> _missLogged = new(StringComparer.OrdinalIgnoreCase);

    private const int MissRetryMs = 30000;

    // Returns the anchor for the configured value, or null to mean "stay on our own grid".
    // Two forms: "x,y,z" is a fixed world position (no walk, no entity — the way to aim at
    // a planet surface point where no grid exists), anything else is a case-insensitive
    // substring of a grid display name. A miss is deliberately NOT an error: the target may
    // simply not have spawned yet, and staying on our own grid is the safe behaviour.
    internal static GridInfo? ResolveAnchor(object anyEntityInScene)
    {
        var want = FeedConfig.OrbitAnchor;
        if (string.IsNullOrWhiteSpace(want)) { _cachedFor = null; _cached = null; return null; }

        // Coordinate anchor. Parsed every call (it is three TryParses) so a live edit of
        // the numbers moves the camera on the next tick with no cache to invalidate.
        if (TryParseCoords(want, out var fixedPos))
        {
            if (_cachedFor != want)
            {
                _cachedFor = want; _cached = null;
                RttLog.Line($"ORBIT ANCHOR: fixed coordinates {fixedPos.X:F0},{fixedPos.Y:F0},{fixedPos.Z:F0}. " +
                            "The orbit centres on this world position — no grid needed. Edit the numbers " +
                            "live to nudge the camera; orbitHeight/orbitRadius tune the shot as usual.");
            }
            return new GridInfo("(coordinates)", fixedPos, 0, null);
        }

        // Name anchor, already resolved: refresh the position from the cached entity and
        // return. No walk. If the entity died, fall through and let the walk re-arm.
        if (_cachedFor == want && _cached.HasValue && _cached.Value.Entity != null)
        {
            try
            {
                var fresh = Describe(_cached.Value.Entity);
                // Re-attempt on every refresh, not only on first resolve: the knob can be
                // switched on while an anchor is already cached, and _taggedGrids makes the
                // repeat free.
                if (fresh.HasValue) { TagAnchorGridForEnvironment(fresh.Value.Entity); _cached = fresh; return fresh; }
            }
            catch { }
            RttLog.Line($"ORBIT ANCHOR: \"{_cached.Value.Name}\" no longer yields a position — the grid " +
                        "is gone or was rebuilt. Re-walking the scene for the name.");
            _cached = null;
        }

        // Unresolved (first attempt, or a prior miss). Walk at most every MissRetryMs.
        var now = Environment.TickCount64;
        if (_cachedFor == want && !_cached.HasValue && now - _lastWalkTicks < MissRetryMs)
            return null;

        try
        {
            _lastWalkTicks = now;
            var hit = Enumerate(anyEntityInScene)
                .Where(g => g.Name != null &&
                            g.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                // Largest match wins. Names are not unique and a station usually shares its
                // name with debris split off it; the big one is what a human meant.
                .OrderByDescending(g => g.Extent)
                .Select(g => (GridInfo?)g)
                .FirstOrDefault();

            if (hit == null)
            {
                // Log a miss ONCE per distinct query, not once per attempt — this runs on
                // the tick and would otherwise fill the log.
                if (_missLogged.Add(want))
                    RttLog.Line($"ORBIT ANCHOR: no grid matched \"{want}\". The feed stays on its own " +
                                "grid; retrying every 30 s in case it spawns. Run worldGridSurvey = 1 to " +
                                "list what actually exists — the name must be a substring of a grid's " +
                                "DISPLAY NAME, matched case-insensitively. \"x,y,z\" aims at coordinates instead.");
                _cachedFor = want; _cached = null;
                return null;
            }

            TagAnchorGridForEnvironment(hit.Value.Entity);
            RttLog.Line($"ORBIT ANCHOR: \"{want}\" -> \"{hit.Value.Name}\" at " +
                        $"{hit.Value.Position.X:F0},{hit.Value.Position.Y:F0},{hit.Value.Position.Z:F0} " +
                        $"(extent {hit.Value.Extent:F0} m). Resolved ONCE — the position now tracks the " +
                        "cached entity and the scene is not walked again unless the grid dies.");

            _cachedFor = want; _cached = hit;
            return hit;
        }
        catch (Exception e) { RttLog.Error("orbit anchor resolve", e); return null; }
    }

    private static bool TryParseCoords(string s, out Vector3D pos)
    {
        pos = default;
        var parts = s.Split(',');
        if (parts.Length != 3) return false;
        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return false;
        pos = new Vector3D(x, y, z);
        return true;
    }

    // The one-shot inventory. Triggered from the config via
    // FeedConfig.TakeWorldGridSurveyRequest().
    //
    // TWO SECTIONS: planets, then grids annotated with their nearest planet. The
    // annotation exists because "find a grid on planet X" was the first real question
    // asked of this file, and a bare grid list cannot answer it — 44 of the 45 grids in
    // this save read as "in space" until you know where the OTHER planets are.
    internal static void DumpGrids(object anyEntityInScene)
    {
        try
        {
            var player = CameraFeed.PresenceCentre;
            var sb = new StringBuilder();
            sb.AppendLine("=== WORLD SURVEY ===");
            sb.AppendLine("Written by WorldGrids.DumpGrids (worldGridSurvey = 1).");
            sb.AppendLine("Distances are from the feed camera's CURRENT position (PresenceCentre): the");
            sb.AppendLine("flying camera when manual control has it, otherwise the orbit anchor. This used");
            sb.AppendLine("to report from the anchor alone, which under manual flight was measured 277 km");
            sb.AppendLine("from where the camera actually was — every distance in the file was wrong.");
            sb.AppendLine("Use the grid NAME as orbitAnchor (substring, case-insensitive), or \"x,y,z\"");
            sb.AppendLine("to aim at raw coordinates (e.g. a planet surface point with no grid on it).");
            sb.AppendLine();

            var planets = EnumeratePlanets(anyEntityInScene)
                .OrderBy(p => (p.Position - player).Length())
                .ToList();

            sb.AppendLine($"--- {planets.Count} planet-like bodies ---");
            sb.AppendLine($"{"distance",12}  {"radius",9}  {"position",-34}  name");
            sb.AppendLine(new string('-', 100));
            foreach (var p in planets)
            {
                var d = (p.Position - player).Length();
                var pos = $"{p.Position.X:F0}, {p.Position.Y:F0}, {p.Position.Z:F0}";
                var r = p.Extent > 0 ? $"{p.Extent:F0}m" : "?";
                sb.AppendLine($"{d,11:F0}m  {r,9}  {pos,-34}  {p.Name}");
            }

            var all = Enumerate(anyEntityInScene)
                .OrderBy(g => (g.Position - player).Length())
                .ToList();

            sb.AppendLine();
            sb.AppendLine($"--- {all.Count} grid(s) ---");
            sb.AppendLine($"{"distance",12}  {"extent",8}  {"position",-34}  name  [nearest planet]");
            sb.AppendLine(new string('-', 100));
            foreach (var g in all)
            {
                var d = (g.Position - player).Length();
                var pos = $"{g.Position.X:F0}, {g.Position.Y:F0}, {g.Position.Z:F0}";
                // Nearest planet by SURFACE distance where a radius is known — centre
                // distance ranks a big close planet below a small far one.
                string near = "";
                if (planets.Count > 0)
                {
                    var np = planets.OrderBy(p =>
                        (g.Position - p.Position).Length() - Math.Max(0, p.Extent)).First();
                    var surf = (g.Position - np.Position).Length() - Math.Max(0, np.Extent);
                    near = $"  [{np.Name}: {surf / 1000.0:F1} km above nominal surface]";
                }
                sb.AppendLine($"{d,11:F0}m  {g.Extent,7:F0}m  {pos,-34}  {g.Name}{near}");
            }

            AppendManagedAreas(sb, anyEntityInScene, player);
            AppendObservers(sb, anyEntityInScene, player);
            AppendSpaceProbe(sb, anyEntityInScene);
            AppendEnvironmentSectors(sb, anyEntityInScene, player);

            Directory.CreateDirectory(RttLog.OutDir);
            File.WriteAllText(ReportPath, sb.ToString());
            RttLog.Line($"WORLD SURVEY: {planets.Count} planet-like bodies and {all.Count} grid(s) " +
                        $"written to {ReportPath}.");
        }
        catch (Exception e) { RttLog.Error("world grid survey", e); }
    }

    // MANAGED WORLD AREAS — the content that is in the SAVE but not in the WORLD.
    //
    // The grid walk above can only see instantiated entities, and the user's observation
    // that forced this section was sharp: the asteroid station 273 km out in SPACE was
    // loaded, while the planet surface showed nothing outside the player's bubble. Surface
    // POIs go through the managed-area system — serialized bundles behind a spatial
    // trigger, spawned when something with the right tag walks in. So "where are the
    // grids outside my bubble" is answered HERE, not by the entity walk:
    // ManagedWorldAreaSessionComponent._areas holds every area with its Name, its
    // BoundingBoxD and its LoadingState, whether or not its content currently exists.
    //
    // This is also the tier-2 recon for goal 10: each area carries the IEntityTrigger the
    // player trips, and the session component exposes the ISpatialTriggerSystem those
    // triggers register with — the exact surface a camera entity must reach to make the
    // world materialize around it.
    private static void AppendManagedAreas(StringBuilder sb, object anyEntityInScene, Vector3D player)
    {
        try
        {
            // The panel's entity is a BLOCK entity: it carries CubeBlockComponent, and the
            // grid component lives on the GRID entity behind block.Grid. Asking the panel
            // entity for CubeGridComponent directly returns null — which is how the first
            // run of this section reported "no Session reachable" while the world was fine.
            //
            // And the block is NOT in the panel entity's own Components array either (second
            // false "NULL"): TryGet<T> evidently searches beyond the array — it is the
            // mechanism CameraFeed.WorldPositionOf uses successfully on this same entity
            // every tick — so for THIS hop the generic dance is the proven road, not the
            // array walk. The array walk stays right for scene-wide enumeration, where
            // TryGet was the one that silently failed. Neither mechanism is universally
            // correct; each has one context where it is the only one that works.
            var tBlock = Type.GetType(
                "Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent, Game2.Simulation");
            var grid = ComponentOf(anyEntityInScene, "CubeGridComponent")
                       ?? Prop(TryGetViaGeneric(anyEntityInScene, tBlock), "Grid");
            var session = Prop(grid, "Session");
            if (session == null)
            {
                sb.AppendLine("\n--- managed world areas: no Session reachable from the panel's grid ---");
                sb.AppendLine($"    grid component: {(grid == null ? "NULL" : grid.GetType().Name)}");
                return;
            }

            // Find the session component by NAME across whatever collection the Session
            // exposes — the generic Get<T> dance is exactly what silently failed for grid
            // components, so it is not trusted here either.
            // Session.SessionComponents is an ENTITY, not a collection — session components
            // are ordinary components riding a dedicated entity (IL: `Entity
            // <SessionComponents>k__BackingField`). Two earlier attempts enumerated it as a
            // sequence and found nothing; the right move is the same Components-array walk
            // used for every other component in this file.
            object mwa = null;
            var scEntity = Prop(session, "SessionComponents");
            if (scEntity != null && _fComponents?.GetValue(scEntity) is IEnumerable scComps)
                foreach (var c in scComps)
                {
                    if (c == null) continue;
                    var n2 = c.GetType().Name;
                    // Exact core name preferred; any ManagedWorldArea-marked component
                    // accepted as fallback (a game-side subclass would still carry it).
                    if (n2 == "ManagedWorldAreaSessionComponent") { mwa = c; break; }
                    if (mwa == null && n2.Contains("ManagedWorldArea", StringComparison.Ordinal)
                                    && Prop(c, "_areas") != null)
                        mwa = c;
                }
            if (mwa == null)
            {
                // One diagnostic dump instead of a guess: name the session's members so the
                // next attempt is a lookup. The session type is stable; this fires once.
                sb.AppendLine("\n--- managed world areas: ManagedWorldAreaSessionComponent NOT FOUND on the session ---");
                // Name the components actually present on the session entity, so the next
                // attempt is a lookup instead of another guess-reload — reloads are priced
                // by the VRAM ratchet now, not free.
                try
                {
                    var names = new List<string>();
                    if (scEntity != null && _fComponents?.GetValue(scEntity) is IEnumerable cs2)
                        foreach (var item in cs2)
                        { if (names.Count >= 400) break; names.Add(item?.GetType().Name ?? "null"); }
                    // 60 was the original cap and the list hit it EXACTLY, making "not in
                    // the list" and "past the cap" indistinguishable — a truncation that
                    // reads as an answer, the same trap as head-limited greps. 400 is
                    // comfortably above any plausible session-component count.
                    sb.AppendLine("    session-entity components: " + string.Join(", ", names.Distinct()));
                }
                catch (Exception e2) { sb.AppendLine("    session entity unreadable: " + e2.Message); }
                return;
            }

            var areas = Prop(mwa, "_areas") as IEnumerable;
            var loadAll = Prop(mwa, "_loadAll");
            sb.AppendLine($"\n--- managed world areas (content in the SAVE, spawned by trigger; _loadAll={loadAll}) ---");
            if (areas == null) { sb.AppendLine("    _areas not readable"); return; }

            sb.AppendLine($"{"distance",12}  {"state",-14}  {"centre",-34}  name");
            sb.AppendLine(new string('-', 100));
            int n = 0;
            foreach (var a in areas)
            {
                if (a == null) continue;
                n++;
                var name = Prop(a, "Name")?.ToString() ?? "(unnamed)";
                var state = Prop(a, "_state")?.ToString() ?? "?";
                var bounds = Prop(a, "Bounds");
                var centre = bounds == null ? (Vector3D?)null
                    : (Prop(bounds, "Center") is Vector3D c ? c
                       : Prop(bounds, "Min") is Vector3D mn && Prop(bounds, "Max") is Vector3D mx
                         ? (mn + mx) * 0.5 : (Vector3D?)null);
                var pos = centre.HasValue
                    ? $"{centre.Value.X:F0}, {centre.Value.Y:F0}, {centre.Value.Z:F0}" : "?";
                var dist = centre.HasValue ? $"{(centre.Value - player).Length():F0}m" : "?";
                sb.AppendLine($"{dist,12}  {state,-14}  {pos,-34}  {name}");
            }
            sb.AppendLine($"{n} area(s).");
        }
        catch (Exception e)
        {
            sb.AppendLine($"\n--- managed world areas: FAILED ({e.GetType().Name}: {e.Message}) ---");
        }
    }

    // MATERIALIZE ONE MANAGED AREA — the goal-10 tier-2 pathfinder.
    //
    // Calls the engine's own ManagedWorldArea.TryLoad() on the named area: the exact method
    // its spatial trigger invokes when a player walks in, so the content spawns by the
    // supported path with all its own lifecycle. This is deliberately NOT a new trigger
    // registration yet — it answers the prior question: does the load path work at all when
    // something other than a player's proximity asks for it? If yes, the continuous version
    // (a trigger volume riding the feed camera) has a proven foundation; if it faults, we
    // learned that BEFORE building machinery on top of it.
    //
    // The mutation runs where the request is consumed — the panel-locate tick, the same
    // context the engine's own trigger callbacks use for their load kicks (TryLoad hands the
    // real work to an async task internally either way).
    internal static void LoadAreaByName(object anyEntityInScene, string wantName)
    {
        try
        {
            // SERVER-CAPTURED AREAS ONLY. The area list reachable from the panel's grid is
            // the CLIENT mirror, and TryLoad on a client-scene area throws
            // KeyNotFoundException from Scene.FinishBefore<SpawnSyncPoint> — it crashed the
            // game twice on 2026-08-01 (loading is a server concern; only the server scene
            // registers the spawn sync point). The bootstrap captures the server-side
            // (area, session) pairs at world load via its OnRegistered patch; this loader
            // refuses to fall back to the client path under any circumstance.
            //
            // Bridge fields are read by REFLECTION, not direct reference: a new logic
            // running against an old bootstrap must degrade to this log line, not die with
            // MissingFieldException the first time the method JITs.
            var bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
            var regsField = bridge?.GetField("ManagedAreaRegistrations");
            var lockField = bridge?.GetField("ManagedAreaLock");
            if (regsField == null || lockField == null)
            {
                RttLog.Line($"LOAD AREA \"{wantName}\": this bootstrap predates the server-side area " +
                            "capture. RESTART THE GAME to load the new bootstrap — the client-side " +
                            "fallback is disabled because it is a confirmed crash.");
                return;
            }

            // EVERY matching candidate, from EVERY captured session — then choose by PROOF.
            //
            // The "use the last batch's session" heuristic picked an area whose scene threw
            // the same KeyNotFoundException (CTD #3): OnRegistered fires for the client
            // mirror's areas too, and which batch lands last is an accident of load order.
            // Scene job tables (_jobSystemsIndex / _jobGroupToIndex) are sealed at scene
            // construction, so whether a given area is LOADABLE is a readable fact: its
            // scene's table either contains ManagedWorldArea+SpawnSyncPoint or it does not.
            // Read the dictionaries and only ever call TryLoad where the key exists — the
            // crash becomes a log line saying which scenes were probed and what they held.
            var candidates = new System.Collections.Generic.List<object>();
            int captured;
            var regs = (System.Collections.Generic.List<object[]>)regsField.GetValue(null);
            lock (lockField.GetValue(null))
            {
                captured = regs.Count;
                foreach (var pair in regs)
                {
                    var n = Prop(pair[0], "Name")?.ToString();
                    if (n != null && n.IndexOf(wantName, StringComparison.OrdinalIgnoreCase) >= 0)
                        candidates.Add(pair[0]);
                }
            }

            if (captured == 0)
            {
                RttLog.Line($"LOAD AREA \"{wantName}\": the bootstrap has captured NO area registrations. " +
                            "Registrations fire during world load — if the game started with the new " +
                            "bootstrap and a world is running, the patch failed; check the boot log for " +
                            "'Patched ManagedWorldArea.OnRegistered'.");
                return;
            }
            if (candidates.Count == 0)
            {
                RttLog.Line($"LOAD AREA \"{wantName}\": no captured area matched ({captured} registration(s) " +
                            "held). The survey's managed-areas section lists the valid names.");
                return;
            }

            var tSync = Type.GetType(
                "Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedWorldArea+SpawnSyncPoint, VRage.Core.Game");
            object area = null;
            var probeReport = new StringBuilder();
            foreach (var cand in candidates)
            {
                var scene = Prop(Prop(Prop(cand, "Session"), "Entity"), "Scene");
                bool loadable = false;
                if (scene != null && tSync != null)
                {
                    var sys = Prop(scene, "_jobSystemsIndex") as System.Collections.IDictionary;
                    var grp = Prop(scene, "_jobGroupToIndex") as System.Collections.IDictionary;
                    loadable = (sys?.Contains(tSync) ?? false) || (grp?.Contains(tSync) ?? false);
                    probeReport.Append($"\n    scene #{scene.GetHashCode():x8}: " +
                        $"jobSystems={(sys?.Contains(tSync) == true ? "HAS" : "lacks")} " +
                        $"jobGroups={(grp?.Contains(tSync) == true ? "HAS" : "lacks")} SpawnSyncPoint " +
                        $"(state={Prop(cand, "_state")})");
                }
                else probeReport.Append($"\n    scene unreadable for one candidate " +
                                        $"(scene={(scene == null ? "null" : "ok")}, syncType={(tSync == null ? "NOT FOUND" : "ok")})");
                if (loadable && area == null) area = cand;
            }

            if (area == null)
            {
                RttLog.Line($"LOAD AREA \"{wantName}\": {candidates.Count} candidate(s) across the captured " +
                            "sessions, and NO scene's job table contains ManagedWorldArea+SpawnSyncPoint — " +
                            "TryLoad would throw in every one of them (this exact throw has cost three CTDs " +
                            "today, so it is refused). Scenes probed:" + probeReport +
                            "\n    CONCLUSION if all scenes lack it: this session type was built without the " +
                            "area-load job group, and materialization needs a different route (the area's own " +
                            "spatial trigger, or the offloading component's LoadAsync).");
                return;
            }
            // THE CALL IS REFUSED. Read this before re-enabling it.
            //
            // With the right scene chosen, TryLoad DID work — no crash, state went
            // Unloaded -> Loading — and then the SIM THREAD DEADLOCKED while the render
            // thread kept running perfectly (cadence counters climbing, config poll
            // answering, game "frozen but responding" for the user).
            //
            // The IL says why. TryLoad is:
            //     var t = TryStartLoading(false); SkipWait(t);
            //     if (!t.IsCompleted) Session.Entity.Scene.FinishBefore<SpawnSyncPoint>(ref t);
            // and the load task itself parks on ContinueOnDCS<SpawnSyncPoint>(task, scene).
            // FinishBefore INSERTS A BLOCKING TASK into the scene scheduler's dependency
            // graph at that job group. Done from inside the sim frame that is a normal
            // ordering constraint; done from our panel tick it is a dependency the pump
            // never satisfies, and the sim stops.
            //
            // So the seat matters even when the scene is right. Two legitimate routes
            // remain, and both are the ORIGINAL tier-2 plan rather than this shortcut:
            //   1. a spatial trigger volume riding the feed camera, so the engine's OWN
            //      trigger callback fires the load from its own correct context;
            //   2. a Harmony hook on a method that genuinely runs inside the sim update,
            //      issuing the deferred request from there.
            // Four incidents (3 CTDs + this freeze) came from calling engine world
            // mutation off-thread. The probe above stays because it is READ-ONLY and it
            // is what makes route 1 verifiable; the call does not.
            RttLog.Line($"LOAD AREA \"{wantName}\": scene probe:{probeReport}" +
                        "\n    -> CALL REFUSED. TryLoad from this thread deadlocks the sim: it plants a " +
                        "blocking task in the scene scheduler (FinishBefore<SpawnSyncPoint>) that only the " +
                        "sim pump can clear. Verified 2026-08-01 — render thread stayed alive while the " +
                        "world froze. Materialization needs a spatial trigger on the camera, or a hook " +
                        "inside the sim update. See the comment at this line.");
            return;
#pragma warning disable CS0162 // unreachable — kept so the working call sequence is not lost


            var name = Prop(area, "Name")?.ToString();
            var state = Prop(area, "_state")?.ToString();
            if (string.Equals(state, "Loaded", StringComparison.OrdinalIgnoreCase))
            {
                RttLog.Line($"LOAD AREA: \"{name}\" is already Loaded — nothing to do.");
                return;
            }

            var tryLoad = area.GetType().GetMethod("TryLoad", Any, null, Type.EmptyTypes, null);
            if (tryLoad == null)
            {
                RttLog.Line($"LOAD AREA: \"{name}\" has no TryLoad() — engine shape changed; " +
                            "candidates: " + string.Join(", ", area.GetType()
                                .GetMethods(Any).Where(m => m.Name.Contains("Load"))
                                .Select(m => m.Name).Distinct()));
                return;
            }

            RttLog.Line($"LOAD AREA: \"{name}\" state={state} -> calling the engine's TryLoad(). " +
                        "This is the same path its own spatial trigger fires; content spawns " +
                        "asynchronously. Watch the feed if the orbit anchor is parked on this area.");
            tryLoad.Invoke(area, null);
            RttLog.Line($"LOAD AREA: TryLoad() returned for \"{name}\" — state now " +
                        $"{Prop(area, "_state")}. The spawn completes async; the area's own " +
                        "trigger/lifecycle owns it from here, including unloading it later if " +
                        "its rules say so.");
#pragma warning restore CS0162
        }
        catch (Exception e) { RttLog.Error($"load area \"{wantName}\"", e); }
    }

    // CLUTTER: TAG THE ANCHOR GRID, DO NOT SPAWN A DECOY.
    //
    // Environment sectors (trees, boulders, surface ore) materialize when an entity trips the
    // planet's sectored trigger, and the constraint read from the LIVE definition is exactly:
    //     MustHave(ClientTriggerTag)  +  MustNotHave(ProcedurallyGeneratedTag)
    //                                 +  MustNotHave(ManagedByWorldAreaTag)
    // One empty marker component. No player-ness, no identity, no special entity.
    //
    // THE SHORTCUT THAT AVOIDS SPAWNING ANYTHING: when the orbit is anchored to a GRID, that
    // grid is a real entity already sitting at the orbit centre — exactly where we want
    // clutter. Tagging it is enough. That is why this is not the "decoy entity" build: it
    // creates nothing, it adds one marker to something that already exists, and TryRemove
    // puts it back. A camera watching a remote base makes that base's surroundings
    // materialize, which is also the behaviour a player would expect.
    //
    // SEAT ESTABLISHED FIRST, as everything since the TryLoad freeze has been:
    // DEntityContext.Set<T> compiles to Scene.GetDataPointer<T>(entity, flags) and a store —
    // no Scene.FinishBefore, no ContinueOnDCS<SyncPoint>, so it plants nothing in the
    // scheduler's dependency graph. It IS a structural change if the archetype lacks the
    // component, which is why this runs ONCE per grid and checks Has<T> first rather than
    // writing every tick.
    //
    // Off by default. It mutates a world entity, and that class of work has been expensive.
    private static readonly HashSet<object> _taggedGrids = new();
    // WHICH GRIDS WE ACTUALLY WROTE TO, kept separate from _taggedGrids on purpose.
    // _taggedGrids means "considered, do not consider again" and includes grids that were
    // BORN with the tag; untagging one of those would delete a property of the player's
    // world that we never granted. Only what is in here may ever be removed.
    private static readonly HashSet<object> _tagAddedByUs = new();
    private static bool _tagShapeLogged;

    internal static void TagAnchorGridForEnvironment(object anchorEntity)
    {
        if (anchorEntity == null) return;

        // THE 1->0 EDGE. Turning the knob off has to actually undo the mutation, or the
        // "off" state is a lie and every A/B run after the first one is confounded by a tag
        // nobody can see. This is also the house bug's exact shape — a teardown reachable
        // only while the feature is enabled — so it lives BEFORE the enabled check.
        if (!FeedConfig.TagAnchorForClutter)
        {
            if (!_tagAddedByUs.Contains(anchorEntity)) { _taggedGrids.Remove(anchorEntity); return; }
            try
            {
                var t = Type.GetType(
                    "Keen.VRage.Core.Game.GameSystems.GamePruning.ClientTriggerTag, VRage.Core.Game");
                var d = Prop(anchorEntity, "Data");
                var rm = d?.GetType().GetMethods(Any).FirstOrDefault(
                    m => m.Name == "TryRemove" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (t != null && rm != null)
                {
                    var removed = (bool)rm.MakeGenericMethod(t).Invoke(d, null);
                    RttLog.Line($"CLUTTER TAG: removed ClientTriggerTag from the anchor grid (TryRemove -> {removed}). " +
                                "The grid stops being an environment trigger. Sectors it already materialized are " +
                                "the engine's to reclaim on its own schedule — if they persist, materialization is " +
                                "one-way and the tag is an arming switch rather than a sustaining one.");
                }
            }
            catch (Exception e) { RttLog.Error("clutter untag", e); }
            finally { _tagAddedByUs.Remove(anchorEntity); _taggedGrids.Remove(anchorEntity); ClearTagMarker(); }
            return;
        }

        if (_taggedGrids.Contains(anchorEntity)) return;
        try
        {
            var tTag = Type.GetType(
                "Keen.VRage.Core.Game.GameSystems.GamePruning.ClientTriggerTag, VRage.Core.Game");
            var data = Prop(anchorEntity, "Data");     // DEntityContext
            if (tTag == null || data == null)
            {
                if (!_tagShapeLogged)
                {
                    _tagShapeLogged = true;
                    RttLog.Line($"CLUTTER TAG: shape missing (ClientTriggerTag={(tTag == null ? "NOT FOUND" : "ok")}, " +
                                $"entity.Data={(data == null ? "NOT FOUND" : "ok")}) — clutter tagging inactive.");
                }
                _taggedGrids.Add(anchorEntity);
                return;
            }

            var ctxType = data.GetType();
            var has = ctxType.GetMethods(Any).FirstOrDefault(m => m.Name == "Has" && m.IsGenericMethodDefinition
                                                              && m.GetParameters().Length == 0);
            var set = ctxType.GetMethods(Any).FirstOrDefault(m => m.Name == "Set" && m.IsGenericMethodDefinition
                                                              && m.GetParameters().Length == 1);
            if (has == null || set == null)
            {
                if (!_tagShapeLogged)
                {
                    _tagShapeLogged = true;
                    RttLog.Line("CLUTTER TAG: DEntityContext.Has<T>/Set<T> not found — clutter tagging inactive. " +
                                "Members: " + string.Join(", ", ctxType.GetMethods(Any)
                                    .Where(m => m.IsGenericMethodDefinition).Select(m => m.Name).Distinct()));
                }
                _taggedGrids.Add(anchorEntity);
                return;
            }

            // Already tagged? Either the grid was BORN with it — in which case there is
            // nothing to do and, importantly, nothing to UNDO — or WE tagged it before a hot
            // reload dropped the statics that remembered so. The marker file is what tells
            // those two apart; without it, every reload silently converts one of our
            // mutations into a permanent one nobody is tracking.
            var already = (bool)has.MakeGenericMethod(tTag).Invoke(data, null);
            _taggedGrids.Add(anchorEntity);
            if (already)
            {
                // "*" is a deliberate human claim: I know this tag is mine, adopt it whatever
                // the handle says. It exists because the feature that WRITES the marker is
                // newer than the first tag it ever applied, so the very first grid this
                // project tagged can never match a key — and refusing to untag it would mean
                // restarting the game to get a clean control.
                var claim = ReadTagMarker();
                var ours = claim != null && (claim == "*" || claim == TagKey(anchorEntity, data));
                if (ours) _tagAddedByUs.Add(anchorEntity);
                RttLog.Line("CLUTTER TAG: the anchor grid ALREADY carries ClientTriggerTag" +
                            (ours
                                ? " — and the marker file says WE added it, before a reload. Adopted, so " +
                                  "tagAnchorForClutter = 0 can still take it back off."
                                : ", and no marker file claims it, so it was born with it. Left alone: " +
                                  "this code never removes a tag it did not add.") +
                            " If clutter is still absent around the camera, the tag is not the missing " +
                            "piece and the sector state in the survey is the next thing to read.");
                return;
            }

            set.MakeGenericMethod(tTag).Invoke(data, new[] { Activator.CreateInstance(tTag) });
            _tagAddedByUs.Add(anchorEntity);
            WriteTagMarker(TagKey(anchorEntity, data));
            RttLog.Line("CLUTTER TAG: added ClientTriggerTag to the anchor grid. The planet's sectored " +
                        "trigger tests exactly this (MustHave ClientTriggerTag, MustNotHave " +
                        "ProcedurallyGenerated/ManagedByWorldArea), so sectors should now materialize " +
                        "around the grid — which is the orbit centre, i.e. where the camera is looking. " +
                        "Watch the feed for trees and boulders; watch VRAM, because materialized sectors " +
                        "are real entities and GPU batches.");
        }
        catch (Exception e)
        {
            _taggedGrids.Add(anchorEntity);          // never retry a throwing path every tick
            RttLog.Error("clutter tag", e);
        }
    }

    // ── THE TRIGGER CENSUS — the instrument the marker's null result demanded ───────────
    //
    // 2026-08-01, late: the camera trigger entity carried ClientTriggerTag at a virgin site
    // for four minutes and NOTHING materialized (VRAM +0M, user-confirmed low-poly). The IL
    // then reorganized the model:
    //
    //   ProceduralGeneratorCLIENTSessionComponent.GetMustHaveTriggerTypeIds -> ClientTriggerTag
    //   ProceduralGeneratorSERVERSessionComponent.GetMustHaveTriggerTypeIds -> Physics.Data.DynamicTag
    //
    // The SERVER — where flora actually spawns (PlanetEnvironmentClientComponent is a pure
    // replication mirror) — triggers on DYNAMIC PHYSICS entities, not on camera markers. If
    // that holds in the live session, the fix is a DynamicTag presence in the server scene,
    // and every consumer of this machinery (flora sectors, encounters, managed world areas —
    // ManagedWorldArea registers with the SAME ISpatialTriggerSystem) starts working at the
    // camera natively, ref-counted, overlap-safe.
    //
    // This census reads, per scene (client via the panel's session; server via the sessions
    // the bootstrap stashed at world load), every EntityTrigger in the spatial trigger
    // system: debug name, bounds, tag constraints (TypeConstraints as raw TypeIds, resolved
    // to names by asking TypeId<T> for the known candidates), and the entities currently
    // inside. READ-ONLY: reflection over dictionaries the sim owns, so a torn enumeration is
    // possible — every loop catches and reports partial rather than aborting the report.
    private static readonly string TriggerCensusPath = Path.Combine(RttLog.OutDir, "trigger-census.txt");

    // The tag types whose TypeIds the census resolves to names. Read via TypeId<T>.Value
    // (already initialized in a running game — WithInitIfNeeded is the fallback for safety).
    private static readonly (string Label, string AqName)[] KnownTags =
    {
        ("ClientTriggerTag",       "Keen.VRage.Core.Game.GameSystems.GamePruning.ClientTriggerTag, VRage.Core.Game"),
        ("DynamicTag",             "Keen.VRage.Physics.Data.DynamicTag, VRage.Physics"),
        ("ProcedurallyGeneratedTag","Keen.VRage.Core.Game.GameSystems.ProceduralGeneration.ProcedurallyGeneratedTag, VRage.Core.Game"),
        ("ManagedByWorldAreaTag",  "Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedByWorldAreaTag, VRage.Core.Game"),
        ("StaticTag",              "Keen.VRage.Physics.Data.StaticTag, VRage.Physics"),
        ("CharacterTag",           "Keen.Game2.Simulation.WorldObjects.Characters.CharacterTag, Game2.Simulation"),
    };

    internal static void DumpTriggerCensus(object anyEntityInScene, Vector3D cameraPos)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SPATIAL TRIGGER CENSUS ===");
        sb.AppendLine("Written by WorldGrids.DumpTriggerCensus (triggerCensus = 1).");
        sb.AppendLine($"Camera (orbit centre): {cameraPos.X:F0}, {cameraPos.Y:F0}, {cameraPos.Z:F0}");
        sb.AppendLine("Every EntityTrigger in each reachable scene's SpatialTriggerSystemSessionComponent,");
        sb.AppendLine("sorted by distance to the camera. 'inside' counts entities currently in the trigger.");

        // THE DEBUG KILL-SWITCH. PlanetEnvironmentComponent.MaterializeSectors's only gate
        // before the sector storage is `if (!DebugEnableUpdates) return;` — a STATIC debug
        // bool. This save boots through a "altered with Debug Menu" warning every load; if
        // that menu left this off, every flora sector-entry callback in the world silently
        // returns, which is exactly the observed symptom.
        try
        {
            var tPec = Type.GetType("Keen.VRage.Voxels.Components.PlanetEnvironmentComponent, VRage.Voxels");
            var fDbg = tPec?.GetField("DebugEnableUpdates", Any);
            sb.AppendLine($"\nPlanetEnvironmentComponent.DebugEnableUpdates = {fDbg?.GetValue(null) ?? "(unreadable)"}" );
        }
        catch (Exception e) { sb.AppendLine($"\nDebugEnableUpdates read failed: {e.GetType().Name}"); }

        // The TypeId -> name table, rebuilt per dump into the shared map the per-session
        // census reads. Reading TypeId<T>.Value is a static-field read; WithInitIfNeeded is
        // the fallback and is what every engine consumer calls anyway.
        idToNameShared.Clear();
        sb.AppendLine("\n--- known tag TypeIds ---");
        foreach (var (label, aq) in KnownTags)
        {
            try
            {
                var t = Type.GetType(aq);
                if (t == null) { sb.AppendLine($"    {label,-26} TYPE NOT FOUND ({aq})"); continue; }
                var tid = Type.GetType("Keen.VRage.DCS.Internal.TypeId`1, VRage.DCS")?.MakeGenericType(t);
                var val = tid?.GetProperty("Value", Any)?.GetValue(null)
                       ?? tid?.GetMethod("WithInitIfNeeded", Any)?.Invoke(null, null);
                if (val is int i)
                {
                    idToNameShared[i] = label;
                    sb.AppendLine($"    {label,-26} id {i}");
                }
                else sb.AppendLine($"    {label,-26} TypeId unreadable");
            }
            catch (Exception e) { sb.AppendLine($"    {label,-26} FAILED: {e.GetType().Name}"); }
        }

        // Scene 1: the client (the panel's own session — same road AppendManagedAreas walks).
        var tBlock = Type.GetType(
            "Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent, Game2.Simulation");
        var grid = ComponentOf(anyEntityInScene, "CubeGridComponent")
                   ?? Prop(TryGetViaGeneric(anyEntityInScene, tBlock), "Grid");
        var clientSession = Prop(grid, "Session");
        CensusOneSession(sb, clientSession, "CLIENT (panel's session)", cameraPos);

        // Scene 2+: every distinct session the bootstrap captured at world load. These were
        // stashed by the managed-area patch DURING LOAD, before the logic attached — it is
        // how the server session was found for the TryLoad experiments. Distinct by
        // reference; the client session is skipped if it shows up again.
        //
        // The captured object is the ManagedWorldArea SESSION COMPONENT, not the Session —
        // the first census run printed "[ManagedWorldAreaSessionComponent]" as the session
        // type and then found nothing on it. One Prop hop fixes it, and the SERVER flavour
        // (the one without "Client" in its name) is exactly the session this census exists
        // to reach.
        try
        {
            var bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
            var regs = bridge?.GetField("ManagedAreaRegistrations")?.GetValue(null) as IEnumerable;
            var regsLock = bridge?.GetField("ManagedAreaLock")?.GetValue(null);
            var seen = new HashSet<object> { clientSession };
            if (regs != null && regsLock != null)
            {
                // Snapshot under the SAME lock the bootstrap's postfix takes — locking any
                // other object is synchronization theatre.
                var sessions = new List<(object Session, string Via)>();
                lock (regsLock)
                {
                    foreach (var pair in regs)
                        if (pair is object[] { Length: >= 2 } p && p[1] != null)
                        {
                            var cand = p[1];
                            // The stash holds whatever Harmony bound to `session` — on this
                            // build that is the session COMPONENT. Resolve to its Session.
                            var real = Prop(cand, "SessionComponents") != null ? cand : Prop(cand, "Session");
                            if (real != null) sessions.Add((real, cand.GetType().Name));
                        }
                }
                foreach (var (s, via) in sessions)
                    if (seen.Add(s))
                        CensusOneSession(sb, s, $"CAPTURED via {via}", cameraPos);
            }
            else sb.AppendLine("\n--- no captured sessions on the bridge (old bootstrap or none registered) ---");
        }
        catch (Exception e) { sb.AppendLine($"\n--- captured-session sweep FAILED: {e.GetType().Name}: {e.Message} ---"); }

        // The legend: every raw id the tables printed, resolved through the engine's own
        // reverse registry. This is what turns "must:3+80+519" into an archetype recipe.
        if (_censusIdsSeen.Count > 0)
        {
            sb.AppendLine("\n--- TypeId legend (RuntimeDataInfo.Of) ---");
            foreach (var id in _censusIdsSeen.OrderBy(i => i))
                sb.AppendLine($"    {id,6}  {(idToNameShared.TryGetValue(id, out var known) ? known + "  =  " : "")}{ResolveTypeId(id)}");
            _censusIdsSeen.Clear();
        }

        try
        {
            File.WriteAllText(TriggerCensusPath, sb.ToString());
            RttLog.Line($"TRIGGER CENSUS written to {TriggerCensusPath} ({sb.Length} chars).");
        }
        catch (Exception e) { RttLog.Error("trigger census write", e); }
    }

    private static void CensusOneSession(StringBuilder sb, object session, string label, Vector3D cameraPos)
    {
        try
        {
            if (session == null) { sb.AppendLine($"\n=== {label}: session NULL ==="); return; }
            sb.AppendLine($"\n=== {label}  [{session.GetType().Name}] ===");

            // Which scene is this? SpawnSyncPoint in the job tables marks the one that can
            // spawn — the discriminator the TryLoad saga established. Reached and tested the
            // way LoadAreaByName's probe does it (Session -> Entity -> Scene, Contains with
            // the TYPE key): the first census asked Prop(session, "Scene") and string-matched
            // key.ToString(), and reported "no" for scenes the type-key probe says HAVE it.
            var scene = Prop(Prop(session, "Entity"), "Scene") ?? Prop(session, "Scene");
            var tSync = Type.GetType(
                "Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedWorldArea+SpawnSyncPoint, VRage.Core.Game");
            var sys = Prop(scene, "_jobSystemsIndex") as IDictionary;
            var grp = Prop(scene, "_jobGroupToIndex") as IDictionary;
            bool canSpawn = tSync != null && ((sys?.Contains(tSync) ?? false) || (grp?.Contains(tSync) ?? false));
            sb.AppendLine($"    scene #{scene?.GetHashCode():x8} ({scene?.GetType().Name ?? "?"})  " +
                          $"SpawnSyncPoint: {(sys == null && grp == null ? "tables unreadable" : canSpawn ? "YES — this scene spawns" : "no")}");

            object triggerSystem = null;
            var scEntity = Prop(session, "SessionComponents");
            if (scEntity != null && _fComponents?.GetValue(scEntity) is IEnumerable scComps)
                foreach (var c in scComps)
                    if (c != null && c.GetType().Name == "SpatialTriggerSystemSessionComponent") { triggerSystem = c; break; }
            if (triggerSystem == null)
            {
                sb.AppendLine("    SpatialTriggerSystemSessionComponent NOT on this session's entity.");
                return;
            }

            // _attachedEntityIndex: entity -> its attached triggers. Enumerating the values
            // reaches every trigger that is attached to anything, which in this engine is
            // every trigger that matters (sector triggers ride the planet, area triggers
            // ride their area entity). Distinct by reference — one trigger can index twice.
            var index = Prop(triggerSystem, "_attachedEntityIndex") as IDictionary;
            if (index == null) { sb.AppendLine("    _attachedEntityIndex unreadable."); return; }

            var triggers = new List<object>();
            var seenT = new HashSet<object>();
            int indexEntries = 0;
            string shapeNote = null;

            // The second census run showed the real layout: entity -> HashSetDictionary ->
            // KeyValuePair -> HashSet -> EntityTrigger. Rather than hard-code four hops,
            // descend until something NAMED like a trigger appears. KeyValuePairs unwrap
            // through Value; anything else enumerable enumerates. At METHOD scope because
            // both the attached index and the occupancy map below feed through it.
            void Collect(object node, int depth)
            {
                if (node == null || depth > 4) return;
                var tn = node.GetType().Name;
                // Contains, not EndsWith: the live types are ShapedSectoredTrigger`1 /
                // ShapedEntityTrigger`1 — generic, so the name ends in the arity marker.
                if (tn.Contains("Trigger", StringComparison.Ordinal) && !tn.Contains("TriggerSystem", StringComparison.Ordinal))
                {
                    if (seenT.Add(node)) triggers.Add(node);
                    return;
                }
                if (tn.StartsWith("KeyValuePair", StringComparison.Ordinal))
                {
                    Collect(Prop(node, "Value"), depth + 1);
                    return;
                }
                if (node is IEnumerable seq && node is not string)
                    foreach (var child in seq) Collect(child, depth + 1);
            }

            try
            {
                foreach (DictionaryEntry de in index)
                {
                    indexEntries++;
                    shapeNote ??= $"index value: {de.Value?.GetType().Name ?? "null"}";
                    Collect(de.Value, 0);
                }
            }
            catch (Exception e)
            {
                sb.AppendLine($"    (index enumeration torn after {indexEntries} entries: {e.GetType().Name} — partial list below)");
            }

            sb.AppendLine($"    _attachedEntityIndex: {indexEntries} entit(ies), {triggers.Count} trigger(s) reachable through it. [{shapeNote ?? "empty"}]");

            // THE AUTHORITATIVE STORE. _triggerBroadphase maps SparseTriggerGroup -> volume
            // tree; the tree's Count is the number of live triggers in that group. The
            // attached index only covers triggers attached to entities, which the first two
            // censuses proved is a sparse subset (3 entities, empty sets, while 3 entities
            // were demonstrably INSIDE triggers).
            try
            {
                var broad = Prop(triggerSystem, "_triggerBroadphase") as IDictionary;
                if (broad == null) sb.AppendLine("    _triggerBroadphase unreadable.");
                else
                {
                    int total = 0, groups = 0;
                    foreach (DictionaryEntry de in broad)
                    {
                        groups++;
                        var count = Prop(de.Value, "Count") is int c ? c : -1;
                        if (count > 0) total += count;
                        var sparse = Prop(Prop(de.Key, "Definition"), "SparseUpdate");
                        sb.AppendLine($"    broadphase group #{groups}: {count} trigger(s), SparseUpdate={sparse}");

                        // The trigger OBJECTS. The tree's node pool is a plain array of Node
                        // structs; leaves carry the trigger in UserData. Internal-node slots
                        // and freed slots hold null/non-trigger UserData and are skipped by
                        // Collect's name test. Every hop is NAMED on failure — the last three
                        // censuses each died silently one hop short, and a reload is priced.
                        var pool = Prop(de.Value, "_nodes");
                        var arr = Prop(pool, "_list") as Array;
                        if (arr == null)
                        {
                            sb.AppendLine($"      harvest DEAD-ENDS: tree={de.Value.GetType().Name} " +
                                          $"_nodes={(pool == null ? "NULL" : pool.GetType().Name)} " +
                                          $"_list={(pool == null ? "-" : Prop(pool, "_list")?.GetType().Name ?? "NULL")}");
                        }
                        else
                        {
                            int before = triggers.Count; string firstTypes = null;
                            foreach (var nodeObj in arr)
                            {
                                var ud = Prop(nodeObj, "UserData");
                                if (ud != null && firstTypes == null) firstTypes = ud.GetType().FullName;
                                Collect(ud, 0);
                            }
                            if (triggers.Count == before)
                                sb.AppendLine($"      harvest EMPTY: array[{arr.Length}] of {arr.GetType().GetElementType()?.Name}, " +
                                              $"first non-null UserData: {firstTypes ?? "(all null)"}");
                        }
                    }
                    sb.AppendLine($"    _triggerBroadphase TOTAL: {total} trigger(s) in {groups} group(s); " +
                                  $"{triggers.Count} trigger object(s) harvested for the table below.");
                }
            }
            catch (Exception e) { sb.AppendLine($"    _triggerBroadphase unreadable: {e.GetType().Name}: {e.Message}"); }

            // Who is currently inside anything — and specifically, is OUR marker? The map is
            // entity -> containing triggers; its VALUES are live trigger objects, so they
            // also feed the detail table below (these are exactly the triggers that are
            // doing something right now).
            try
            {
                var containing = Prop(triggerSystem, "_containingTriggers") as IDictionary;
                if (containing != null)
                {
                    var markerHandle = _marker == null ? null : Prop(_marker, "Entity")?.ToString();
                    var serverHandle = _serverMarker == null ? null : Prop(_serverMarker, "Entity")?.ToString();
                    var occupied = 0; string markerLine = null, serverLine = null;
                    foreach (DictionaryEntry de in containing)
                    {
                        occupied++;
                        Collect(de.Value, 0);      // harvest the trigger objects themselves
                        var k = de.Key?.ToString();
                        if (markerHandle != null && k == markerHandle)
                            markerLine = $"CLIENT MARKER ({markerHandle}) IS INSIDE {(de.Value is IEnumerable l ? CountSafe(l) : 1)} trigger(s)";
                        if (serverHandle != null && k == serverHandle)
                            serverLine = $"SERVER MARKER ({serverHandle}) IS INSIDE {(de.Value is IEnumerable l2 ? CountSafe(l2) : 1)} trigger(s)";
                    }
                    sb.AppendLine($"    _containingTriggers: {occupied} entit(ies) inside at least one trigger. " +
                                  (markerLine ?? (markerHandle == null ? "(no client marker)" : $"client marker ({markerHandle}) inside NONE")) + "; " +
                                  (serverLine ?? (serverHandle == null ? "(no server marker)" : $"server marker ({serverHandle}) inside NONE")));
                }
            }
            catch (Exception e) { sb.AppendLine($"    _containingTriggers unreadable: {e.GetType().Name}"); }
            sb.AppendLine($"    {"distance",12}  {"size",7}  {"inside",6}  {"kind",-9}  {"constraints",-60}  name");
            sb.AppendLine("    " + new string('-', 130));

            var rows = new List<(double Dist, string Line)>();
            foreach (var t in triggers)
            {
                try
                {
                    var name = Prop(t, "_debugName")?.ToString() ?? "(unnamed)";
                    var kind = t.GetType().Name.Contains("Sectored", StringComparison.Ordinal) ? "SECTORED" : "entity";
                    var bounds = Prop(t, "ShapeBounds");
                    Vector3D? centre = null; double half = 0;
                    if (Prop(bounds, "Min") is Vector3D mn && Prop(bounds, "Max") is Vector3D mx)
                    { centre = (mn + mx) * 0.5; half = (mx - mn).Length() * 0.5; }
                    else if (Prop(bounds, "Center") is Vector3D c) centre = c;
                    var dist = centre.HasValue ? (centre.Value - cameraPos).Length() : double.MaxValue;
                    // CONTAINS beats distance-to-centre for reading this table: a planet-wide
                    // sector box 61 km from the camera by centre very much covers the camera.
                    var contains = centre.HasValue && half > 0 && dist <= half;
                    var inside = Prop(t, "Entities") is IEnumerable ents ? CountSafe(ents) : -1;
                    // Sectored triggers track per-sector membership in a SEPARATE set;
                    // an entity can be tracked without being in the flat Entities set.
                    var tracked = Prop(t, "TrackedEntities") is IEnumerable trk ? CountSafe(trk) : -1;
                    if (tracked > inside) inside = tracked;

                    // LAYOUT (read from TypeConstraintBuilder.AsSpan(out mustHaveCount), not
                    // guessed): each int[] is [count, ...count MustHave ids, ...MustNot ids].
                    // The first census printed the count as if it were a component id, which
                    // made ManagedByWorldAreaTag look like a MUST on the area triggers.
                    var cons = "?";
                    if (Prop(Prop(t, "TriggerArgs"), "TypeConstraints") is int[][] tc)
                    {
                        var parts = new List<string>();
                        foreach (var layer in tc)
                        {
                            if (layer == null || layer.Length == 0) continue;
                            var n2 = layer[0];
                            var must = layer.Skip(1).Take(n2).Select(FormatTagId);
                            var not = layer.Skip(1 + n2).Select(FormatTagId);
                            parts.Add($"must:{string.Join("+", must)}" +
                                      (layer.Length > 1 + n2 ? $" not:{string.Join("+", not)}" : ""));
                        }
                        cons = string.Join(" | ", parts);
                    }

                    var distStr = dist == double.MaxValue ? "?" : dist >= 1000 ? $"{dist / 1000:F0}km" : $"{dist:F0}m";
                    var sizeStr = half <= 0 ? "?" : half >= 1000 ? $"{half / 1000:F0}km" : $"{half:F0}m";
                    if (contains) distStr = "COVERS-CAM";
                    rows.Add((contains ? 0 : dist,
                        $"    {distStr,12}  {sizeStr,7}  {inside,6}  {kind,-9}  {Trunc(cons, 60),-60}  {Trunc(name, 60)}"));
                }
                catch { /* torn trigger — skip it, keep the census */ }
            }

            // THE FLORA CHAIN, link by link (2026-08-02: terrain landed, scatters did not).
            // For every environment/generation sectored trigger: is it tracking our
            // markers, and do any of its SECTORS currently hold an entity? A tracked
            // marker with zero occupied sectors means sector-entry never fired; occupied
            // sectors with no content means the generation/spawn side is the dead link.
            try
            {
                var mh = _marker == null ? null : Prop(_marker, "Entity")?.ToString();
                var sh = _serverMarker == null ? null : Prop(_serverMarker, "Entity")?.ToString();
                sb.AppendLine("    --- flora-chain detail (PlanetEnvironment* / Procedural Generation triggers) ---");
                foreach (var t in triggers)
                {
                    string nm;
                    try { nm = Prop(t, "_debugName")?.ToString() ?? ""; } catch { continue; }
                    if (!nm.Contains("PlanetEnvironment", StringComparison.Ordinal) &&
                        !nm.Contains("Procedural Generation", StringComparison.Ordinal)) continue;
                    try
                    {
                        var tracked = Prop(t, "TrackedEntities") as IEnumerable;
                        int tn = 0; bool hasM = false, hasS = false;
                        if (tracked != null)
                            foreach (var e2 in tracked)
                            {
                                tn++;
                                var k = e2?.ToString();
                                if (mh != null && k == mh) hasM = true;
                                if (sh != null && k == sh) hasS = true;
                            }
                        var counts = Prop(t, "SectorEntityCounts") as IDictionary;
                        var occupied = counts?.Count ?? -1;
                        sb.AppendLine($"      {Trunc(nm, 44),-44} tracked={tn}{(hasM ? " +CLIENT-MARKER" : "")}" +
                                      $"{(hasS ? " +SERVER-MARKER" : "")}  occupiedSectors={occupied}");

                        // THE STORAGE, through the callback's own target. The server planet
                        // entity's Components array does not carry PlanetEnvironmentComponent
                        // (a scene walk found none), but every sector callback is a delegate
                        // whose Target IS that component — the trigger hands us the exact
                        // instance its entries would notify. Its sector-storage collection
                        // counts are the pending-vs-materialized verdict.
                        if (nm.StartsWith("PlanetEnvironment", StringComparison.Ordinal))
                        {
                            // The trigger's callback target is a TriggerLayer wrapper; the
                            // component sits one hop further, behind the LAYER's Args
                            // delegates. Both tiers report: the layer's own counters, then
                            // the component's sector storage.
                            var cb = Prop(Prop(t, "SectorArgs") ?? Prop(t, "_args"), "OnFirstEntityEntered") as Delegate;
                            var layer = cb?.Target;
                            if (layer != null && layer.GetType().Name == "TriggerLayer")
                            {
                                var sc = Prop(layer, "SectorCounter") as IDictionary;
                                var cc = Prop(layer, "ChunkCounter") as IDictionary;
                                sb.AppendLine($"          layer: SectorCounter={sc?.Count ?? -1} ChunkCounter={cc?.Count ?? -1}");

                                object component = null;
                                var args = Prop(layer, "Args");
                                if (args != null)
                                    foreach (var f3 in args.GetType().GetFields(Any))
                                        if (f3.GetValue(args) is Delegate d3 && d3.Target != null
                                            && Prop(d3.Target, "EnvironmentSectorStorage") != null)
                                        { component = d3.Target; break; }
                                var storage = component == null ? null : Prop(component, "EnvironmentSectorStorage");
                                if (storage != null)
                                {
                                    var parts = new List<string>();
                                    for (var ty = storage.GetType(); ty != null && ty != typeof(object); ty = ty.BaseType)
                                        foreach (var f2 in ty.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                                        {
                                            object v2;
                                            try { v2 = f2.GetValue(storage); } catch { continue; }
                                            if (v2 is IDictionary dct) parts.Add($"{f2.Name}={dct.Count}");
                                            else if (v2 is ICollection col) parts.Add($"{f2.Name}={col.Count}");
                                        }
                                    sb.AppendLine($"          storage[{component.GetType().Name}]: {Trunc(string.Join(" ", parts), 110)}");
                                }
                                else sb.AppendLine("          (no Args delegate reaches an EnvironmentSectorStorage)");
                            }
                            else if (cb != null)
                                sb.AppendLine($"          callback target: {layer?.GetType().Name ?? "static"}");
                        }
                    }
                    catch (Exception e2) { sb.AppendLine($"      {Trunc(nm, 44),-44} unreadable: {e2.GetType().Name}"); }
                }
            }
            catch { }

            // The generator itself: is it running, is it blocked, is anything queued?
            try
            {
                object gen = null;
                var scEntity2 = Prop(session, "SessionComponents");
                if (scEntity2 != null && _fComponents?.GetValue(scEntity2) is IEnumerable comps2)
                    foreach (var c in comps2)
                        if (c != null && c.GetType().Name.StartsWith("ProceduralGenerator", StringComparison.Ordinal))
                        { gen = c; break; }
                if (gen != null)
                {
                    var q = Prop(gen, "MaterializationQueue");
                    sb.AppendLine($"    generator: {gen.GetType().Name}  IsActive={Prop(gen, "IsActive")}  " +
                                  $"_blocked={Prop(gen, "_blocked")}  blockedTasks={CountSafe(Prop(gen, "_blockedTasks") as IEnumerable)}  " +
                                  $"queue={(q == null ? "?" : Prop(q, "Count")?.ToString() ?? q.GetType().Name)}  " +
                                  $"volumeSectors={Prop(Prop(gen, "_volumeSectors"), "Count") ?? "?"}  " +
                                  $"entitySectors={Prop(Prop(gen, "_entitySectors"), "Count") ?? "?"}");
                }
                else sb.AppendLine("    generator: no ProceduralGenerator*SessionComponent on this session's entity.");
            }
            catch (Exception e) { sb.AppendLine($"    generator: unreadable ({e.GetType().Name})"); }

            // Every trigger within 100 km of the camera, then the nearest tail beyond it up
            // to a cap — a distant-but-relevant trigger (a managed area two valleys over)
            // should still show, but a thousand-line dump of the whole solar system is the
            // component-roster trap again.
            rows.Sort((a, b2) => a.Dist.CompareTo(b2.Dist));
            int printed = 0;
            foreach (var r in rows)
            {
                if (printed >= 250 && r.Dist > 100000) break;
                sb.AppendLine(r.Line);
                printed++;
            }
            if (printed < rows.Count) sb.AppendLine($"    ... {rows.Count - printed} more beyond 100 km (capped).");

            string FormatTagId(int id)
            {
                _censusIdsSeen.Add(id);
                return idToNameShared.TryGetValue(id, out var n2) ? n2 : id.ToString();
            }
        }
        catch (Exception e)
        {
            sb.AppendLine($"    CENSUS FAILED for this session: {e.GetType().Name}: {e.Message}");
        }
    }

    // Shared by CensusOneSession's local function — filled by DumpTriggerCensus before use.
    private static readonly Dictionary<int, string> idToNameShared = new();

    // Every raw TypeId the census prints, resolved to a legend at the end of the report via
    // the engine's own reverse registry: RuntimeDataInfo.Of(int) -> Info. The first censused
    // constraints were number soup (must:3+80+519...) precisely because the id->type mapping
    // is runtime-assigned and unknowable offline.
    private static readonly HashSet<int> _censusIdsSeen = new();

    private static string ResolveTypeId(int id)
    {
        try
        {
            var tRdi = Type.GetType("Keen.VRage.DCS.Internal.RuntimeDataInfo, VRage.DCS");
            var of = tRdi?.GetMethods(Any).FirstOrDefault(m => m.Name == "Of" && m.GetParameters().Length == 1
                                                            && m.GetParameters()[0].ParameterType == typeof(int));
            var info = of?.Invoke(null, new object[] { id });
            if (info == null) return "(unresolvable)";
            var t = Prop(info, "Type") ?? Prop(info, "DataType") ?? Prop(info, "RuntimeType");
            return (t as Type)?.FullName ?? t?.ToString() ?? info.ToString();
        }
        catch (Exception e) { return $"(resolve failed: {e.GetType().Name})"; }
    }

    private static int CountSafe(IEnumerable e)
    {
        try { int n = 0; foreach (var _ in e) n++; return n; }
        catch { return -1; }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    // ── THE SERVER PRESENCE ENTITY — goal 10's server half, on the engine's own seat ────
    //
    // The trigger census (2026-08-01, trigger-census.txt) decoded every relevant constraint:
    //
    //   flora sectors (server scene):   must DynamicTag+WorldTransform+BoundingBoxData
    //   managed world areas (server):   must DynamicTag+WorldTransform+BoundingBoxData
    //   voxel data sectors (client):    must DynamicTag+WorldTransform+BoundingBoxData
    //
    // One archetype makes the engine materialize EVERYTHING around a position, ref-counted
    // and overlap-safe, because it is the same input multiplayer presence uses. But the
    // server scene pumps on its own thread, and structural mutation from any other thread
    // is the TryLoad freeze waiting to recur — so all server-scene work happens INSIDE
    // OnSimPump, which the bootstrap invokes from a Harmony prefix on the trigger system's
    // own per-frame methods: by construction, the right thread for that scene.
    //
    // The callback fires for BOTH scenes' trigger systems; the SpawnSyncPoint probe (the
    // TryLoad saga's discriminator) picks the server one, cached per component instance.
    // CONCURRENT, and the plain Dictionary it replaces was a real bug with a real symptom.
    //
    // SEVERAL SCENES TICK THROUGH THIS SEAT ON DIFFERENT THREADS — client scenes and the
    // spawning scene — and they all classify themselves here. A plain Dictionary being read
    // and written from all of them eventually threw:
    //
    //   server presence: InvalidOperationException: Operations that change non-concurrent
    //                    collections must have exclusive access
    //
    // The catch that swallowed it set _serverDisarmed, so the SERVER marker was never created
    // for the rest of the session while the CLIENT marker carried on happily (418 moves).
    // That is precisely the reported symptom: "the whole spawned foliage area is locked" —
    // client-side sectors followed the camera, server-side flora never did.
    //
    // It is intermittent by nature, which is why goal 10 landed with this latent: it needs the
    // two pumps to collide on the same tick.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<object, bool> _pumpSceneIsServer = new();
    private static object _serverMarker;          // DEntityContext in the SERVER scene
    private static Vector3D _serverMarkerPos;
    private static int _serverMarkerMoves;
    private static bool _serverDisarmed;
    private static long _serverLogTicks;

    // THE STAGED BIRTH. Markers created with the final archetype never enter the trigger
    // system's candidate index (proven twice: constraints satisfied, volumes COVERS-CAM,
    // inside NONE — from the tick thread AND from the scene's own pump). The engine's own
    // mid-session spawns are born STAGED — StagingTag + ConcurrentInit, the exact tags in
    // every trigger's MustNot list — and de-staged once initialized; the archetype
    // TRANSITION is what the index's signals key on, not existence. So each marker is born
    // wearing StagingTag and has it removed one seat-tick later: the same edge the
    // engine's entities present, produced with the engine's own component.
    private static object _pendingDestage;        // marker ctx awaiting its de-stage edge
    private static object _pendingDestageServer;

    // MOTION IS PART OF THE ARCHETYPE — and the RIGHT motion is a PATROL, not a shuffle.
    // The 10 m drift version proved tracking (census: +SERVER-MARKER on the flora
    // triggers) and proved insufficiency: zero flora entities in the server scene after
    // minutes of presence. Sector-entry callbacks (SectorArgs.OnFirstEntityEntered — the
    // hook that enqueues generation) fire on sector BOUNDARY CROSSINGS, and a 10 m circle
    // lives inside one sector: one entry at birth, then silence. Players trip these
    // constantly because they travel. So the marker now patrols a 250 m ring at ~12 m/s —
    // a brisk vehicle pace, crossing flora-scale sectors every few seconds, the same
    // stimulus a driving player presents.
    // IN THE PLANET-TANGENT PLANE, not world X/Z — the user's "did the voxel-mesh fix
    // teach us anything" question caught this before it shipped wrong: at the Verdure base
    // the radial up is ~(0.97, 0.19, -0.14), mostly world-X, so a world-horizontal ring is
    // nearly VERTICAL there — a patrol into the sky and the bedrock, where no flora sector
    // lives. Same disease as corner-vs-centre: right numbers, wrong frame. The feed
    // publishes planet-radial up; the ring lives in the plane perpendicular to it.
    private static Vector3D DriftAround(Vector3D centre)
    {
        var t = Environment.TickCount64 * 0.00005;       // 0.05 rad/s -> one lap ~126 s
        var up = CameraFeed.OrbitUp;
        if (up.LengthSquared() < 0.5) up = new Vector3D(0, 1, 0);
        up.Normalize();
        var seed = Math.Abs(up.Y) < 0.9 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0);
        var t1 = Vector3D.Cross(up, seed); t1.Normalize();
        var t2 = Vector3D.Cross(up, t1);
        return centre + t1 * (Math.Sin(t) * 250.0) + t2 * (Math.Cos(t) * 250.0);
    }

    private static void Destage(object markerCtx)
    {
        var tStaging = Type.GetType("Keen.VRage.DCS.CoreData.StagingTag, VRage.DCS");
        if (tStaging == null || markerCtx == null) return;
        var rm = markerCtx.GetType().GetMethods(Any).FirstOrDefault(
            m => m.Name == "TryRemove" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        var removed = rm == null ? (object)"no TryRemove" : rm.MakeGenericMethod(tStaging).Invoke(markerCtx, null);
        RttLog.Line($"MARKER DE-STAGE: StagingTag removed -> {removed}. If the staged-birth theory is right, " +
                    "THIS is the moment the trigger index first sees the marker.");
    }

    private static void Stage(object markerCtx)
    {
        var tStaging = Type.GetType("Keen.VRage.DCS.CoreData.StagingTag, VRage.DCS");
        if (tStaging == null || markerCtx == null) return;
        var set = markerCtx.GetType().GetMethods(Any).FirstOrDefault(
            m => m.Name == "Set" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
        set?.MakeGenericMethod(tStaging).Invoke(markerCtx, new[] { Activator.CreateInstance(tStaging) });
    }

    internal static void OnSimPump(object sceneObj)
    {
        // Cheap early-outs first: this runs at the top of EVERY scene's Tick. The seat
        // serves BOTH markers, so the gate is "any marker work at all" — the per-branch
        // disarms are checked inside their branches. Since the third seat host, the
        // bootstrap hands us the SCENE itself (Scene.Tick prefix): the trigger-system
        // methods it patched first are all conditional jobs a quiet session never runs,
        // and two boots produced a seat that provably never fired.
        if (sceneObj == null) return;

        // BEFORE everything, even the null checks below: this stamp is what tells the gate
        // and the claim expiry that the SIM IS ACTUALLY RUNNING. Silence from a panel only
        // means "dead" while this heartbeat advances; a pause, the settings menu, a load or
        // a sim stall freezes it, and with it every liveness judgement. That freeze is the
        // fix for the settings/exit/load CTD family — see CameraFeed.NotePumpAlive.
        CameraFeed.NotePumpAlive(PanelScene != null && ReferenceEquals(sceneObj, PanelScene));

        // BEFORE the early-outs, deliberately. This drains panel unbinds that FeedGate
        // handed off because they arrived on the render thread (see PanelBinding.Unbind);
        // it has nothing to do with the marker knobs, and gating it behind them would mean
        // a feed torn down with every marker knob off never gets its panels restored. Costs
        // one uncontended lock and a count check per scene tick when the queue is empty.
        PanelBinding.DrainDeferredUnbind();

        // Held through world load for the same reason the preloads are — the presence entity
        // exists to make the SERVER generate sectors around the camera, which is the fragile
        // path. Reads as "knob off" until the world settles, so the existing teardown branch
        // below removes any marker that somehow survived a load.
        // MASTER GATE (task #40): a dead feed must not keep a presence entity generating
        // sectors around its camera — the existing teardown branch below removes it.
        var want = FeedConfig.ServerPresenceEntity && CameraFeed.ResidencySettled && Feeds.AnyLive;
        if (!want && _serverMarker == null
            && !FeedConfig.CameraTriggerEntity && _marker == null
            && !_serverFloraSurveyPending) return;

        try
        {
            if (!_pumpSceneIsServer.TryGetValue(sceneObj, out var isServer))
            {
                var tSync = Type.GetType(
                    "Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedWorldArea+SpawnSyncPoint, VRage.Core.Game");
                var sys = Prop(sceneObj, "_jobSystemsIndex") as IDictionary;
                var grp = Prop(sceneObj, "_jobGroupToIndex") as IDictionary;
                isServer = tSync != null && ((sys?.Contains(tSync) ?? false) || (grp?.Contains(tSync) ?? false));
                _pumpSceneIsServer[sceneObj] = isServer;
                RttLog.Line($"SIM PUMP SEAT: scene #{sceneObj.GetHashCode():x8} ticks through this seat — " +
                            (isServer ? "the SPAWNING scene; server presence is drivable."
                                      : "a client scene; the client marker is drivable."));
            }
            var scene2 = sceneObj;

            // Pending de-stage edges fire FIRST, one tick after their marker's birth —
            // this is the archetype transition the trigger index listens for.
            if (!isServer && _pendingDestage != null) { var m = _pendingDestage; _pendingDestage = null; Destage(m); }
            if (isServer && _pendingDestageServer != null) { var m = _pendingDestageServer; _pendingDestageServer = null; Destage(m); }

            // The CLIENT scene's seat drives the client marker — the same "born on the
            // pump" requirement the census exposed, applied to both scenes symmetrically.
            // ONLY the panel's own client scene: several client scenes tick through this
            // seat, and a marker in the wrong one is a marker no trigger will ever see.
            if (!isServer)
            {
                if (PanelScene != null && !ReferenceEquals(scene2, PanelScene)) return;
                DriveClientMarker(scene2);
                return;
            }
            if (_serverFloraSurveyPending) { _serverFloraSurveyPending = false; DumpServerFlora(scene2); }
            if (_serverDisarmed) return;
            if (_serverRetryAtMs != 0 && Environment.TickCount64 < _serverRetryAtMs) return;   // backing off after a fault

            // Save hold: same rule as the client drive, on this scene's own seat. The
            // SERVER scene is the one the world save is actually collected from, so this
            // branch is the one that matters most.
            if (SaveHoldActive())
            {
                if (_serverMarker != null) DestroyServerMarker(scene2, "a save is being collected");
                return;
            }

            if (!want)
            {
                // Teardown, on the same seat that created it.
                DestroyServerMarker(scene2, "knob off");
                return;
            }

            var pos = CameraFeed.PresenceCentre;
            if (pos.LengthSquared() <= 1.0) return;                 // no feed target yet
            pos = DriftAround(pos);

            if (_serverMarker == null)
            {
                _tWt ??= Type.GetType("Keen.VRage.Core.WorldTransform, VRage.Core");
                _tBbd ??= Type.GetType("Keen.VRage.Core.Game.Data.BoundingBoxData, VRage.Core.Game");
                var tDyn = Type.GetType("Keen.VRage.Physics.Data.DynamicTag, VRage.Physics");
                var add = scene2.GetType().GetMethods(Any).FirstOrDefault(
                    m => m.Name == "AddEntity" && m.IsGenericMethodDefinition
                      && m.GetGenericArguments().Length == 3 && m.GetParameters().Length == 3);
                if (_tWt == null || _tBbd == null || tDyn == null || add == null)
                {
                    _serverDisarmed = true;
                    RttLog.Line($"SERVER PRESENCE: shape missing (WorldTransform={(_tWt == null ? "X" : "ok")} " +
                                $"BoundingBoxData={(_tBbd == null ? "X" : "ok")} DynamicTag={(tDyn == null ? "X" : "ok")} " +
                                $"AddEntity<3>={(add == null ? "X" : "ok")}) — server presence inactive.");
                    return;
                }

                var bbd = Activator.CreateInstance(_tBbd);
                _tBbd.GetField("BoundingBox", Any)?.SetValue(
                    bbd, new BoundingBox(new Vector3(-MarkerHalfExtent), new Vector3(MarkerHalfExtent)));
                _serverMarker = add.MakeGenericMethod(tDyn, _tWt, _tBbd).Invoke(
                    scene2, new[] { Activator.CreateInstance(tDyn), Activator.CreateInstance(_tWt, new object[] { pos }), bbd });
                _serverMarkerPos = pos;
                _serverMarkerMoves = 0;
                _serverMarkerBornExtent = FeedConfig.CameraTriggerExtent;

                // Born staged, de-staged next seat tick — the transition the index listens for.
                Stage(_serverMarker);
                _pendingDestageServer = _serverMarker;

                RttLog.Line($"SERVER PRESENCE: created DynamicTag+WorldTransform+BoundingBoxData at " +
                            $"{pos.X:F0},{pos.Y:F0},{pos.Z:F0} IN THE SERVER SCENE, from its own pump, born " +
                            "STAGED (de-stage follows one tick later). This is the archetype every censused " +
                            "trigger tests. Watch the census 'inside' column, the feed, and VRAM.");
                return;
            }

            // Same rebuild-on-extent-change rule as the client marker — see DriveClientMarker.
            if (Math.Abs(_serverMarkerBornExtent - FeedConfig.CameraTriggerExtent) > 0.001)
            {
                RttLog.Line($"SERVER PRESENCE: extent changed {_serverMarkerBornExtent:F2} -> " +
                            $"{FeedConfig.CameraTriggerExtent:F2} m; rebuilding the server marker.");
                DestroyServerMarker(scene2, "cameraTriggerExtent changed");
                return;                                    // reborn on the next pump pass
            }

            if ((pos - _serverMarkerPos).LengthSquared() >= MarkerMoveEpsilon * MarkerMoveEpsilon)
            {
                _miSetWt ??= Type.GetType("Keen.VRage.Core.Game.Data.EntityTransformFunctions, VRage.Core.Game")
                    ?.GetMethods(Any).FirstOrDefault(m => m.Name == "SetWorldTransform" && m.GetParameters().Length == 2);
                var wt = Activator.CreateInstance(_tWt, new object[] { pos });
                if (_miSetWt != null) _miSetWt.Invoke(null, new[] { _serverMarker, wt });
                else
                {
                    var set = _serverMarker.GetType().GetMethods(Any).FirstOrDefault(
                        m => m.Name == "Set" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
                    set?.MakeGenericMethod(_tWt).Invoke(_serverMarker, new[] { wt });
                }
                _serverMarkerPos = pos;
                _serverMarkerMoves++;
            }

            // A clean pass clears the fault streak, so three failures means three IN A ROW —
            // an intermittent race can never accumulate its way to a permanent disarm.
            if (_serverFaults != 0) { _serverFaults = 0; _serverRetryAtMs = 0; }

            var now = Environment.TickCount64;
            if (now - _serverLogTicks > 30000)
            {
                _serverLogTicks = now;
                RttLog.Line($"SERVER PRESENCE: alive at {_serverMarkerPos.X:F0},{_serverMarkerPos.Y:F0},{_serverMarkerPos.Z:F0} " +
                            $"({_serverMarkerMoves} move(s)).");
            }
        }
        catch (Exception e)
        {
            // FAULT ONCE, RETRY LATER — not "fault once, dead for the session".
            //
            // The old rule was permanent, on the reasoning that a fault inside engine trigger
            // code will fault again. That is true of a SHAPE problem and false of a RACE, and
            // this path had a race: a concurrent-collection exception from the shared seat
            // disarmed server presence for a whole session while the client marker carried on,
            // which is exactly the "spawned foliage area is locked" report. Nothing said so
            // afterwards, because the disarmed check returns silently.
            //
            // Three consecutive failures still means stop — a genuine shape fault is not
            // logged forever — but a one-off gets another go ten seconds later.
            _serverFaults++;
            _serverRetryAtMs = Environment.TickCount64 + 10000;
            if (_serverFaults >= 3)
            {
                _serverDisarmed = true;
                RttLog.Line($"SERVER PRESENCE: DISARMED after {_serverFaults} consecutive faults — remote flora will " +
                            "no longer follow the camera this session. This is a real failure, not a quiet default.");
            }
            RttLog.Error($"server presence (fault {_serverFaults}/3, retrying in 10 s)", e);
        }
    }

    private static int _serverFaults;
    private static long _serverRetryAtMs;

    // ── THE VOXEL BODY SURVEY — where does near-mode terrain actually EXIST? ────────────
    //
    // 2026-08-02 morning: the metered arrival burst never fired because NO planet-scale
    // body ever reaches the clipmap override near the feed — everything in the stream is
    // sub-1.5 km boulders. The planet's near-mode terrain is not a body whose camera we
    // can swap; away from the player it apparently does not exist, and the feed renders
    // the smooth far representation instead (the user's spectator observation: mountains
    // "suddenly grow" crossing an atmosphere-ish threshold, flatten smoothly receding).
    //
    // This survey enumerates VoxelRenderUpdateSessionComponent._renderComponents — the
    // exact list UpdateClipmaps iterates — with each body's position, voxel size and
    // distance to the feed camera. The instantiation PATTERN (one planet body vs regional
    // patches; clustered around the player vs everywhere) names the system that creates
    // near-mode bodies, which is the system goal 10 must feed next.
    internal static void DumpVoxelBodies(Vector3D feedPos)
    {
        try
        {
            var bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
            var comp = bridge?.GetField("VoxelUpdateComponent")?.GetValue(null);
            if (comp == null)
            {
                RttLog.Line("VOXEL BODY SURVEY: VoxelUpdateComponent not captured yet (no clipmap update has " +
                            "run since boot?) — nothing to enumerate.");
                return;
            }
            var list = Prop(comp, "_renderComponents") as IEnumerable;
            if (list == null) { RttLog.Line("VOXEL BODY SURVEY: _renderComponents unreadable."); return; }

            var sb = new StringBuilder();
            sb.AppendLine("=== VOXEL BODY SURVEY ===");
            sb.AppendLine($"Feed camera: {feedPos.X:F0}, {feedPos.Y:F0}, {feedPos.Z:F0}");
            sb.AppendLine("Every VoxelRenderComponent in the clipmap update list. 'size' is the max axis of");
            sb.AppendLine("Clipmap.Size in voxels (~1 m each): planets read ~10^5, boulders ~10^2.");
            sb.AppendLine($"\n{"dFeed",12}  {"size",9}  {"position",-34}  storage/entity");
            sb.AppendLine(new string('-', 100));

            var rows = new List<(double D, string Line)>();
            int n = 0, unreadable = 0;
            foreach (var rc in list)
            {
                n++;
                try
                {
                    var clip = Prop(rc, "Clipmap");
                    var l2w = Prop(clip, "LocalToWorld");
                    if (Prop(l2w, "Position") is not Vector3D pos) { unreadable++; continue; }
                    double size = 0;
                    var szObj = Prop(clip, "Size");
                    if (szObj != null)
                    {
                        var t2 = szObj.GetType();
                        foreach (var ax in new[] { "X", "Y", "Z" })
                            if (t2.GetField(ax, Any)?.GetValue(szObj) is int v && v > size) size = v;
                    }
                    var d = (pos - feedPos).Length();
                    // A name, best effort: the storage tells rock from planet from debris.
                    var name = Prop(rc, "Storage")?.GetType().Name
                            ?? Prop(rc, "_storage")?.GetType().Name
                            ?? rc.GetType().Name;
                    var dStr = d >= 1000 ? $"{d / 1000:F1}km" : $"{d:F0}m";
                    var sStr = size >= 1000 ? $"{size / 1000:F0}k" : $"{size:F0}";
                    rows.Add((d, $"{dStr,12}  {sStr,9}  {pos.X:F0}, {pos.Y:F0}, {pos.Z:F0}".PadRight(60) + $"  {name}"));
                }
                catch { unreadable++; }
            }

            rows.Sort((a, b2) => a.D.CompareTo(b2.D));
            foreach (var r in rows.Take(120)) sb.AppendLine(r.Line);
            if (rows.Count > 120) sb.AppendLine($"... {rows.Count - 120} more, farther out.");
            sb.AppendLine($"\n{n} component(s) in the update list, {unreadable} unreadable.");

            // GRASS, PER LOD RING. Grass is not part of the flora octree at all: it hangs
            // off individual clipmap cells (VoxelCell._grassEntity, attached when the cell
            // has a grass material) and is drawn inside SceneDrawSystem.RenderGrass. So
            // "no grass in the feed" has two very different causes and this separates them:
            // cells near the camera WITH valid grass entities means the geometry exists and
            // the gap is in generation/rendering; cells with none means the cells the
            // override built never got grass attached.
            sb.AppendLine("\n--- grass entities on clipmap cells nearest the feed camera ---");
            try
            {
                var nearest = new List<(double D, double Size, object Rc)>();
                foreach (var rc in list)
                {
                    try
                    {
                        var clip0 = Prop(rc, "Clipmap");
                        var l2w0 = Prop(clip0, "LocalToWorld");
                        if (Prop(l2w0, "Position") is not Vector3D p0) continue;
                        double sx = 0, sy = 0, sz = 0;
                        var sz0 = Prop(clip0, "Size");
                        if (sz0 != null)
                        {
                            var ts = sz0.GetType();
                            if (ts.GetField("X", Any)?.GetValue(sz0) is int ix) sx = ix;
                            if (ts.GetField("Y", Any)?.GetValue(sz0) is int iy) sy = iy;
                            if (ts.GetField("Z", Any)?.GetValue(sz0) is int iz) sz = iz;
                        }
                        var c0 = p0 + new Vector3D(sx * 0.5, sy * 0.5, sz * 0.5);
                        nearest.Add(((c0 - feedPos).Length(), Math.Max(sx, Math.Max(sy, sz)), rc));
                    }
                    catch { }
                }
                nearest.Sort((a, b2) => a.D.CompareTo(b2.D));

                // The nearest bodies are BOULDERS (the first sweep returned only LOD 2-5
                // rings on 64-voxel rocks). Grass grows on the PLANET, so the biggest body
                // in range is the one that answers the question — include it explicitly.
                var sample = nearest.Take(2).ToList();
                var planet = nearest.Where(x => x.Size > 10000).OrderBy(x => x.D).FirstOrDefault();
                if (planet.Rc != null) sample.Add(planet);

                // CHECK THE INSTRUMENT BEFORE BELIEVING THE NEGATIVE. "Zero grass entities"
                // is only meaningful if this reader can see a grass entity when one exists,
                // so the PLAYER'S nearest planet goes in as the control: the engine is
                // certainly rendering grass around him if his world has any.
                var playerPos0 = PlayerRenderCameraPosition();
                if (playerPos0 != null)
                {
                    object bestRc = null; double bestD = double.MaxValue;
                    foreach (var (_, size, rc) in nearest)
                    {
                        if (size <= 10000) continue;
                        try
                        {
                            var l2w1 = Prop(Prop(rc, "Clipmap"), "LocalToWorld");
                            if (Prop(l2w1, "Position") is not Vector3D p1) continue;
                            var d1 = (p1 + new Vector3D(size * 0.5, size * 0.5, size * 0.5) - playerPos0.Value).Length();
                            if (d1 < bestD) { bestD = d1; bestRc = rc; }
                        }
                        catch { }
                    }
                    if (bestRc != null && !sample.Any(s => ReferenceEquals(s.Rc, bestRc)))
                        sample.Add((bestD, 999999, bestRc));    // marked as the player's planet below
                }

                foreach (var (d0, bodySize, rc) in sample)
                {
                    var clip = Prop(rc, "Clipmap");
                    if (Prop(clip, "Rings") is not IEnumerable rings) { sb.AppendLine("    Rings unreadable"); continue; }
                    sb.AppendLine(bodySize > 999998
                        ? $"    CONTROL — the PLAYER'S nearest planet, {d0 / 1000.0:F1} km from him:"
                        : $"    body at {d0 / 1000.0:F1} km from the feed camera " +
                          $"(size {(bodySize >= 1000 ? $"{bodySize / 1000.0:F0}k" : bodySize.ToString("F0"))} voxels" +
                          $"{(bodySize > 10000 ? " — THE PLANET" : "")}):");
                    int lod = 0;
                    string cellTypeName = null;
                    // PER-CELL, NOT JUST PER-RING. "43 of 216 LOD-0 cells have a grass entity"
                    // proves the PLANET has grass somewhere; it says nothing about the patch
                    // the camera is actually filming, and those are different claims. Grass is
                    // patchy by construction, so an aggregate can look healthy while every cell
                    // in shot has none — which reads as "grass is broken" when it is really
                    // "the camera is over bare rock". Collect (distance, hasGrass) per cell and
                    // print the nearest handful: that is the only sample the PICTURE can be
                    // judged against, and the picture is the thing making the claim.
                    var cellHits = new List<(double D, double DRoot, bool Grass, int Lod)>();
                    foreach (var ring in rings)
                    {
                        int cells = 0, withGrass = 0, fieldFound = 0;
                        try
                        {
                            // SNAPSHOT, NEVER ENUMERATE LIVE. These dictionaries belong to
                            // the render thread and are rebuilt as the clipmap streams. A
                            // live foreach over them crashed a world load on 2026-08-02
                            // (voxelBodySurvey was left = 1 in the config and edge-triggers
                            // re-fire on a fresh boot — the same trap loadArea sprang on
                            // 2026-08-01). Copying the values first bounds the exposure to
                            // one array copy, and a torn copy just reports fewer cells.
                            if (Prop(ring, "_cells") is IDictionary cellMap)
                            {
                                object[] vals;
                                try { vals = new object[cellMap.Count]; cellMap.Values.CopyTo(vals, 0); }
                                catch { lod++; continue; }
                                foreach (var cell in vals)
                                {
                                    if (cell == null) continue;
                                    cells++;
                                    // VoxelClipmapRing._cells holds CellData, a POOLED
                                    // WRAPPER; the VoxelCell that owns _grassEntity is
                                    // behind CellData.Cell (IVoxelActorCell). Reading the
                                    // wrapper is what produced the false "zero grass
                                    // entities everywhere" — the instrument was blind and
                                    // said so only once it was asked to report field
                                    // resolution separately from field value.
                                    var actor = Prop(cell, "Cell") ?? cell;
                                    var ge = Prop(actor, "_grassEntity");
                                    cellTypeName ??= actor.GetType().Name;
                                    // FIELD-FOUND vs VALID, separately. Zero-with-grass is
                                    // ambiguous between "no grass here" and "this reader
                                    // cannot see the field at all", and the obvious control
                                    // (the player's own planet) is void because he is
                                    // standing on a rocky world that may have no grass
                                    // either. Counting reads that RESOLVED settles it
                                    // without needing a grassy control.
                                    bool valid = false;
                                    if (ge != null)
                                    {
                                        fieldFound++;
                                        if (Prop(ge, "IsValid") is bool ok && ok) { withGrass++; valid = true; }
                                    }

                                    // THE CELL'S WORLD POSITION, COMPOSED THE WAY THE ENGINE
                                    // COMPOSES IT.
                                    //
                                    // _worldTransform is NOT the cell's position. It belongs to
                                    // the DistributedRootEntity the cell hangs off, so every
                                    // cell sharing a root reports one IDENTICAL distance — which
                                    // is precisely the degeneracy the guard below caught, and
                                    // why the first run claimed "nearest cell 867 m" for a
                                    // camera 15 m above the ground. Reading Position alone is
                                    // the bug, not the world.
                                    //
                                    // VoxelCell.CreateModelEntity shows the real composition, so
                                    // copy it rather than invent one — it builds the model's
                                    // transform as
                                    //     RelativeTransform((Vector3)((Vector3I)_offset - _rootOffset))
                                    // against _rootEntity. The cell therefore sits at the root's
                                    // world position plus that offset, rotated into world space
                                    // by the root's orientation.
                                    //
                                    // BOTH distances are kept. The old (root-only) number is
                                    // printed beside the new one so this reader can be JUDGED
                                    // rather than believed: if they agree the composition adds
                                    // nothing, and if the old one is degenerate while the new
                                    // one is not, that is the fix demonstrating itself.
                                    try
                                    {
                                        var wt = Prop(actor, "_worldTransform");
                                        if (wt != null && Prop(wt, "Position") is Vector3D rootPos)
                                        {
                                            var local = Vector3D.Zero;
                                            if (Prop(actor, "_offset") is Vector3D off)
                                            {
                                                double lx = Math.Truncate(off.X), ly = Math.Truncate(off.Y), lz = Math.Truncate(off.Z);
                                                if (Prop(actor, "_rootOffset") is Vector3I ro)
                                                { lx -= ro.X; ly -= ro.Y; lz -= ro.Z; }
                                                local = new Vector3D(lx, ly, lz);
                                                var q = Prop(wt, "Orientation");
                                                if (q != null) local = RotateByQuaternion(local, q);
                                            }
                                            cellHits.Add((((rootPos + local) - feedPos).Length(),
                                                          (rootPos - feedPos).Length(), valid, lod));
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                        if (cells > 0)
                            sb.AppendLine($"        LOD {lod}: {cells} cell(s) [{cellTypeName ?? "?"}], " +
                                          $"{fieldFound} resolved a _grassEntity field, {withGrass} of those valid" +
                                          (fieldFound == 0 ? "   <-- FIELD NOT FOUND: this reader is blind, not the world" : ""));
                        lod++;
                    }

                    // THE ROW THAT SETTLES IT. The feed camera orbits at orbitRadius, so the
                    // cells it is looking at are the ones within roughly that distance. If
                    // those carry grass entities and the panel shows no blades, the negative
                    // is real and downstream. If they carry none, there is simply no grass
                    // under the camera and the whole "grass is broken" line of enquiry was
                    // about the wrong patch of ground.
                    if (cellHits.Count > 0)
                    {
                        cellHits.Sort((x, y) => x.D.CompareTo(y.D));
                        var near = cellHits.Take(14).ToList();

                        // SANITY-CHECK THIS READER BEFORE PRINTING A CONCLUSION FROM IT.
                        //
                        // First run (2026-08-02) reported the nearest cell 867 m away while the
                        // camera sat 15 m above terrain rendering in full detail, with a dozen
                        // cells sharing an IDENTICAL distance. Both are impossible for genuine
                        // per-cell positions, so `_worldTransform` here is not what it was
                        // assumed to be — most likely a shared root/ring transform rather than
                        // one per cell.
                        //
                        // The failure mode that matters is that those numbers are PLAUSIBLE.
                        // A zero announces itself; "867 m" just quietly gets believed, and this
                        // project has already lost half a day to a reader whose output looked
                        // reasonable. So the two tells are checked explicitly and the verdict
                        // is WITHHELD rather than printed with a caveat nobody would read.
                        int distinct = cellHits.Select(h => Math.Round(h.D)).Distinct().Count();
                        int rootDistinct = cellHits.Select(h => Math.Round(h.DRoot)).Distinct().Count();
                        bool degenerate = distinct * 4 < cellHits.Count;      // most share a position
                        bool implausible = near[0].D > 300.0;                 // nothing near a camera on the ground

                        sb.AppendLine("        per-cell sample: " + string.Join("  ", near.Select(h =>
                                          $"{h.D:F0}m/L{h.Lod}{(h.Grass ? "=GRASS" : "=none")}")));
                        sb.AppendLine($"        ({cellHits.Count} cells positioned, {distinct} distinct distances; " +
                                      $"root-only reader gave {rootDistinct} distinct — if that is far smaller, the " +
                                      "shared-root theory was right and the composition is what fixed it)");

                        if (degenerate || implausible)
                        {
                            sb.AppendLine("        <-- READER UNRELIABLE, NO VERDICT. " +
                                (degenerate ? "Most cells share one distance, so this is a shared root/ring " +
                                              "transform, not per-cell positions. " : "") +
                                (implausible ? "Nearest cell is >300 m from a camera that is 15 m above " +
                                               "terrain rendering in full detail, which cannot be true. " : "") +
                                "Do NOT read grass presence off these rows. The per-LOD counts above come " +
                                "through a different path and are unaffected.");
                        }
                        else
                        {
                            int within = cellHits.Count(h => h.D <= 150.0);
                            int withinGrass = cellHits.Count(h => h.D <= 150.0 && h.Grass);
                            sb.AppendLine($"        WITHIN 150 m OF THE CAMERA: {within} cell(s), " +
                                          $"{withinGrass} with a valid grass entity." +
                                          (within == 0
                                              ? "  <-- no cells that close; widen before reading anything into it"
                                              : withinGrass == 0
                                                  ? "  <-- NO GRASS UNDER THE CAMERA. The picture cannot show grass " +
                                                    "here whatever the renderer does; move the anchor before calling grass broken."
                                                  : "  <-- grass geometry IS under the camera, so a blade-free picture " +
                                                    "is a real rendering negative."));
                        }
                    }
                }
            }
            catch (Exception e) { sb.AppendLine($"    grass sweep failed: {e.GetType().Name}: {e.Message}"); }

            var path = Path.Combine(RttLog.OutDir, "voxel-bodies.txt");
            File.WriteAllText(path, sb.ToString());
            RttLog.Line($"VOXEL BODY SURVEY written to {path} ({n} bodies).");
        }
        catch (Exception e) { RttLog.Error("voxel body survey", e); }
    }

    // ---- CLAIM STABILITY, PER SECTOR --------------------------------------------------
    //
    // THE QUESTION AGGREGATES CANNOT ANSWER. "41% of updates claimed" is identical whether
    // 41% of sectors are claimed EVERY time (stable, and the flashing is downstream) or
    // every sector is claimed 41% of the time (thrashing, and the flashing is right here).
    // Those have completely different fixes, and three config guesses have already been
    // spent not knowing which it is.
    //
    // So: remember each watched octree's last outcome and count TRANSITIONS. A sector that
    // flips claimed -> rejected -> claimed is being handed between the feed camera and the
    // player, and its flora is culled at 3906 km on every frame it spends on the player's
    // side — which is exactly "correctly positioned, correctly scaled, flashing".
    //
    // WHY THE RADIUS GUARD. This path rejects ~11,300 times a SECOND; a dictionary write per
    // call would cost more than the answer is worth. Only sectors within ClaimWatchRadius of
    // the feed are tracked — those are the ones actually in shot, and the only ones whose
    // flicker anyone can see. The table is capped for the same reason.
    private const double ClaimWatchRadius = 3000.0;
    private const int ClaimWatchCap = 512;
    private static readonly Dictionary<object, bool> _claimLast = new();
    private static long _claimFlips, _claimSamples;

    private static void NoteClaimStability(object octree, bool claimed)
    {
        try
        {
            _claimSamples++;
            if (_claimLast.TryGetValue(octree, out var was))
            {
                if (was != claimed) { _claimFlips++; _claimLast[octree] = claimed; }
            }
            else if (_claimLast.Count < ClaimWatchCap)
            {
                _claimLast[octree] = claimed;
            }

            // Remember the cell coords WE just wrote, so the probe can read them back from
            // inside our pass and see whether anything overwrote them. See CameraCoordsText.
            if (claimed && _coordsAtClaim.Count < ClaimWatchCap)
            {
                _fOctCameraCoords ??= octree.GetType().GetField("_cameraCoords", Any);
                if (_fOctCameraCoords?.GetValue(octree) is Vector3I c) _coordsAtClaim[octree] = c;
                NoteVisibility(octree);
                // NoteBatchVisibility(octree) REMOVED 2026-08-03 — it and the SampleSubSectors
                // walk it drove were costing ~800,000 reflection-driven samples a SECOND
                // (7.33M per 15 s window over 4000 batches, plus 4.74M over 6000 subsector
                // meshes) to watch two mechanisms the flashing hunt conclusively eliminated:
                // subsector visibility read 0 flips in 4.7M samples, and batch IsVisible was
                // cumulative-static throughout. The real cause was a RELATIVE texture-camera
                // delta, fixed elsewhere. Kept in source but no longer driven; delete the
                // bodies when nothing else wants their field-binding examples.
            }
            else if (claimed && _fOctCameraCoords != null)
            {
                if (_coordsAtClaim.ContainsKey(octree) &&
                    _fOctCameraCoords.GetValue(octree) is Vector3I c2) _coordsAtClaim[octree] = c2;
            }
        }
        catch { }
    }

    // ---- WHOSE CAMERA IS THE OCTREE ACTUALLY POINTED AT, AT DRAW TIME? -----------------
    //
    // THE BLIND SPOT THIS CLOSES. ClaimStabilityText counts flips among HOOK INVOCATIONS, so
    // "0 flips in 161,736 samples" proves the HOOKED PATH is stable — NOT that _cameraCoords
    // is stable. Any engine path that re-points an octree without going through our prefix is
    // invisible to that counter and would still read zero.
    //
    // So this reads the STATE instead of our view of it: we record the cell coords at the
    // moment we claim (which are ours by construction), then read them back from INSIDE OUR
    // PASS, which is where our draw actually consumes them. A mismatch means something
    // overwrote our camera between the claim and the draw — and an octree pointed at a player
    // 3906 km away culls that sector's flora to nothing, which is the flash.
    //
    // Drift is not a false positive here: the orbit moves ~1.26 m/s and octree cells are far
    // larger than that, whereas a player-derived value is a completely different cell.
    private static FieldInfo _fOctCameraCoords;
    private static readonly Dictionary<object, Vector3I> _coordsAtClaim = new();

    internal static string CameraCoordsText()
    {
        try
        {
            if (_fOctCameraCoords == null || _coordsAtClaim.Count == 0)
                return "octree camera: not sampled yet";

            int same = 0, moved = 0;
            foreach (var kv in _coordsAtClaim)
            {
                if (_fOctCameraCoords.GetValue(kv.Key) is not Vector3I now) continue;
                if (now.X == kv.Value.X && now.Y == kv.Value.Y && now.Z == kv.Value.Z) same++;
                else moved++;
            }
            return $"octree camera at DRAW time: {same} still ours, {moved} overwritten " +
                   $"(of {_coordsAtClaim.Count} claimed sector(s))" +
                   (moved == 0
                       ? "   <-- our camera SURVIVES to the draw, so the octree is not the flash"
                       : "   <-- OVERWRITTEN: something re-points these octrees after we claim them, " +
                         "through a path our prefix never sees. That sector's flora is culled at the " +
                         "PLAYER'S distance on those frames.");
        }
        catch (Exception e) { return "octree camera read failed: " + e.Message; }
    }

    // THE BOUNDARY FLICKER — the user's observation, turned into a measurement.
    //
    // Reported: nearby flora NEVER flashes, distant flora does, and REDUCING
    // worldFloraRadiusMult made it MORE frequent, not less. That last part kills the
    // "we are asking for flora further out than the engine will hold" theory outright — a
    // smaller ask would have helped. What it fits instead is a flicker band at the CULLING
    // THRESHOLD: shrink the radius and the boundary moves into view, so more of it is on
    // screen. Objects well inside pass under any camera, objects well outside fail under
    // any camera, and only the band between two candidate thresholds can flip.
    //
    // Two candidate cameras is exactly what we have. `_cameraCoords` is now 138/138 ours,
    // but that is the octree's SPATIAL camera and visibility does not read it:
    //     InstanceSparseOctree.IsOctreeVisible(Vector3 cameraPosition, out Single distance)
    //     InstanceSparseOctree.UpdateVisibility(Vector3 cameraRelativePosition)
    // both take the position as a PARAMETER. So visibility can still be evaluated against
    // the player on the ~87% of frames we do not hold Settings.RenderView, while the octree
    // itself is correctly pointed at the feed.
    //
    // Same technique that proved the octree race: record at claim, compare at draw. A
    // sector whose _isVisible flips between the two IS one flickering object.
    private static FieldInfo _fOctVisible, _fOctMinDist;
    private static readonly Dictionary<object, (bool Vis, float MinDist)> _visAtClaim = new();
    private static long _visFlips, _visSamples;

    internal static void NoteVisibility(object octree)
    {
        if (octree == null) return;
        try
        {
            _fOctVisible ??= octree.GetType().GetField("_isVisible", Any);
            _fOctMinDist ??= octree.GetType().GetField("_minDistanceToOctree", Any);
            if (_fOctVisible == null) return;
            bool v = _fOctVisible.GetValue(octree) is bool b && b;
            float d = _fOctMinDist?.GetValue(octree) is float f ? f : -1f;
            _visAtClaim[octree] = (v, d);
        }
        catch { }
    }

    internal static string VisibilityText()
    {
        try
        {
            if (_fOctVisible == null || _visAtClaim.Count == 0) return "octree visibility: not sampled yet";

            int flipped = 0, held = 0; float nearestFlip = float.MaxValue, farthestHeld = 0f;
            foreach (var kv in _visAtClaim)
            {
                if (_fOctVisible.GetValue(kv.Key) is not bool now) continue;
                _visSamples++;
                if (now != kv.Value.Vis)
                {
                    flipped++; _visFlips++;
                    if (kv.Value.MinDist >= 0 && kv.Value.MinDist < nearestFlip) nearestFlip = kv.Value.MinDist;
                }
                else { held++; if (kv.Value.MinDist > farthestHeld) farthestHeld = kv.Value.MinDist; }
            }

            return $"octree VISIBILITY at draw: {held} held, {flipped} FLIPPED " +
                   $"(of {_visAtClaim.Count} claimed; {_visFlips} flip(s) in {_visSamples} sample(s) cumulative)" +
                   (flipped == 0
                       ? "   <-- visibility is stable too, so the flicker is NOT the octree visibility test " +
                         "and the next suspect is per-batch culling (InstanceBatch.ComputeCullingDistance)"
                       : $"   <-- FLIPPING, nearest flip at {(nearestFlip == float.MaxValue ? -1 : nearestFlip):F0} m " +
                         $"vs farthest stable at {farthestHeld:F0} m. THIS IS THE BOUNDARY BAND: visibility is " +
                         "being evaluated against two different camera positions, so sectors between the two " +
                         "thresholds appear and vanish. Nearby flora is inside both and never flickers, which " +
                         "is exactly what the user reports.");
        }
        catch (Exception e) { return "octree visibility read failed: " + e.Message; }
    }

    // No clock of its own: ClaimStabilityText is called from the FLORA CAMERA report, so it
    // is already rate-limited and always reports the same window as the counts beside it.
    private static long _lastFlips, _lastSamples;

    internal static string ClaimStabilityText()
    {
        var flips = _claimFlips - _lastFlips;
        var samples = _claimSamples - _lastSamples;
        _lastFlips = _claimFlips; _lastSamples = _claimSamples;
        if (samples == 0) return "claim stability: no in-shot sectors sampled";
        var pct = 100.0 * flips / samples;
        return $"claim stability: {flips} flip(s) in {samples} sample(s) ({pct:0.##}%) across " +
               $"{_claimLast.Count} watched sector(s) within {ClaimWatchRadius:F0} m" +
               (flips == 0
                   ? "   <-- STABLE: sectors are NOT being handed back and forth, so the flashing is " +
                     "DOWNSTREAM of the claim (draw/LOD), not in the flora camera override."
                   : "   <-- THRASHING: sectors ARE alternating between the feed camera and the player. " +
                     "Every frame on the player's side culls their flora at 3906 km, which is the flash.");
    }

    // Rotate a vector by a Quaternion read REFLECTIVELY, component by component.
    //
    // Deliberately not Vector3D.Transform or any VRage helper: this runs against whatever
    // build of the game is installed, and a math overload that exists today can vanish in a
    // patch and take the whole survey down with a MissingMethodException. X/Y/Z/W are Single
    // fields on Keen.VRage.Library.Mathematics.Quaternion and are the stable surface.
    //
    // Standard v' = v + 2w(q x v) + 2(q x (q x v)), written out so there is no dependency on
    // a cross-product helper either. Convert.ToDouble rather than a cast: the fields are
    // Single, and an unboxing cast to double on a boxed float throws.
    private static Vector3D RotateByQuaternion(Vector3D v, object quaternion)
    {
        try
        {
            var qt = quaternion.GetType();
            object G(string n) => qt.GetField(n, Any)?.GetValue(quaternion);
            var ox = G("X"); var oy = G("Y"); var oz = G("Z"); var ow = G("W");
            if (ox == null || oy == null || oz == null || ow == null) return v;

            double qx = Convert.ToDouble(ox), qy = Convert.ToDouble(oy),
                   qz = Convert.ToDouble(oz), qw = Convert.ToDouble(ow);

            // An identity/zero rotation is the common case for a planet root; skip the work
            // and, more importantly, do not let a zero quaternion (Quaternion.Zero is a real
            // static on this type) collapse the vector to nothing.
            if (qx == 0 && qy == 0 && qz == 0) return v;

            double tx = 2.0 * (qy * v.Z - qz * v.Y);
            double ty = 2.0 * (qz * v.X - qx * v.Z);
            double tz = 2.0 * (qx * v.Y - qy * v.X);
            return new Vector3D(
                v.X + qw * tx + (qy * tz - qz * ty),
                v.Y + qw * ty + (qz * tx - qx * tz),
                v.Z + qw * tz + (qx * ty - qy * tx));
        }
        catch { return v; }
    }

    // ── THE SERVER FLORA CENSUS — spawned-but-not-replicated vs never-spawned ───────────
    //
    // 2026-08-02: terrain landed; scatters did not. The trigger census left a fork: the
    // server's Verdure flora triggers show 448 OCCUPIED sectors with an ACTIVE, EMPTY-queue
    // generator. Either sector content already exists server-side (gap = replication /
    // client rendering) or entry never queued generation (gap = the callback chain).
    // Counting the server scene's entities near the camera splits it: hundreds of flora
    // entities -> replication-side gap; none -> generation-side gap.
    //
    // The walk runs ON the sim-pump seat's server callback — the only thread where reading
    // the server scene's archetypes mid-session is legitimate. One shot per request; a
    // 30k-entity walk costs the server pump tens of milliseconds, once.
    private static volatile bool _serverFloraSurveyPending;

    internal static void RequestServerFloraSurvey() => _serverFloraSurveyPending = true;

    private static void DumpServerFlora(object scene)
    {
        try
        {
            var centre = CameraFeed.PresenceCentre;
            const double Radius = 3000.0;

            _miEnumerate ??= scene.GetType().GetMethod("EnumerateEntities", Any, null, Type.EmptyTypes, null);
            _tCtx ??= Type.GetType("Keen.VRage.DCS.Accessors.DEntityContext, VRage.DCS");
            _tEntity ??= Type.GetType("Keen.VRage.DCS.Components.Entity, VRage.DCS");
            _miFromData ??= _tEntity?.GetMethod("TryGetFromDataEntity", Any);
            var tBbd = Type.GetType("Keen.VRage.Core.Game.Data.BoundingBoxData, VRage.Core.Game");
            var miAabb = tBbd?.GetMethods(Any).FirstOrDefault(m =>
                m.Name == "GetWorldAABB" && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType.Name == "DEntityContext");
            if (_miEnumerate == null || _tCtx == null || _miFromData == null || miAabb == null)
            {
                RttLog.Line("SERVER FLORA: shape missing (enumerate/ctx/entity/GetWorldAABB) — survey skipped.");
                return;
            }
            if (_miEnumerate.Invoke(scene, null) is not IEnumerable handles)
            { RttLog.Line("SERVER FLORA: enumeration returned nothing."); return; }

            var byComponent = new Dictionary<string, int>();
            var samples = new List<string>();
            int seen = 0, near = 0, positioned = 0;
            foreach (var handle in handles)
            {
                seen++;
                try
                {
                    var ctx = Activator.CreateInstance(_tCtx, scene, handle);
                    // Distance first, via the AABB helper — most entities carry bounds, and
                    // the ones that do not are infrastructure the question is not about.
                    object aabb;
                    try { aabb = miAabb.Invoke(null, new[] { ctx }); } catch { continue; }
                    Vector3D pos;
                    if (Prop(aabb, "Center") is Vector3D c) pos = c;
                    else if (Prop(aabb, "Min") is Vector3D mn && Prop(aabb, "Max") is Vector3D mx) pos = (mn + mx) * 0.5;
                    else continue;
                    positioned++;
                    var d = (pos - centre).Length();
                    if (d > Radius) continue;
                    near++;

                    var entity = _miFromData.Invoke(null, new[] { ctx });
                    string label;
                    if (entity != null && _fComponents == null)
                        _fComponents = entity.GetType().GetField("Components", Any);
                    if (entity != null && _fComponents?.GetValue(entity) is IEnumerable comps)
                    {
                        var names = new List<string>();
                        foreach (var comp in comps) if (comp != null) names.Add(comp.GetType().Name);
                        foreach (var n2 in names)
                            byComponent[n2] = byComponent.TryGetValue(n2, out var v) ? v + 1 : 1;
                        label = string.Join("+", names.Take(4));
                    }
                    else
                    {
                        // Data-only entity (no managed wrapper): histogram the archetype id
                        // so pure-ECS content (which flora may well be) still shows up.
                        label = "(data-only) " + (Prop(ctx, "Archetype")?.ToString() ?? "?");
                        byComponent[label] = byComponent.TryGetValue(label, out var v) ? v + 1 : 1;
                    }
                    if (samples.Count < 30)
                        samples.Add($"    {d,6:F0}m  {pos.X:F0},{pos.Y:F0},{pos.Z:F0}  {Trunc(label, 90)}");
                }
                catch { }
            }

            // THE SECTOR STORAGE, generically. PlanetEnvironmentSectorStorage is the state
            // machine between "sector-entry callback fired" and "content spawned"; every
            // collection-typed field's count is dumped without knowing its name, so pending
            // vs materialized vs nothing reads straight off. All-zeros = the callbacks never
            // fire; growing pendings = the callbacks fire and the downstream stalls.
            var storageReport = new StringBuilder();
            try
            {
                if (_miEnumerate.Invoke(scene, null) is IEnumerable handles2)
                {
                    int planetsFound = 0;
                    foreach (var handle in handles2)
                    {
                        if (planetsFound >= 6) break;
                        try
                        {
                            var ctx = Activator.CreateInstance(_tCtx, scene, handle);
                            var entity = _miFromData.Invoke(null, new[] { ctx });
                            if (entity == null || _fComponents?.GetValue(entity) is not IEnumerable comps) continue;
                            object pec = null;
                            foreach (var comp in comps)
                                if (comp != null && comp.GetType().Name == "PlanetEnvironmentComponent") { pec = comp; break; }
                            if (pec == null) continue;
                            planetsFound++;
                            var storage = Prop(pec, "EnvironmentSectorStorage");
                            storageReport.AppendLine($"    planet entity {handle}: storage = {(storage == null ? "NULL" : storage.GetType().Name)}");
                            if (storage == null) continue;
                            for (var ty = storage.GetType(); ty != null && ty != typeof(object); ty = ty.BaseType)
                                foreach (var f2 in ty.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                                {
                                    object v2;
                                    try { v2 = f2.GetValue(storage); } catch { continue; }
                                    if (v2 is IDictionary dct) storageReport.AppendLine($"        {f2.Name}: {dct.Count} entries");
                                    else if (v2 is ICollection col) storageReport.AppendLine($"        {f2.Name}: {col.Count} items");
                                }
                        }
                        catch { }
                    }
                    if (planetsFound == 0) storageReport.AppendLine("    (no entity with PlanetEnvironmentComponent found in this scene)");
                }
            }
            catch (Exception e) { storageReport.AppendLine($"    storage sweep failed: {e.GetType().Name}"); }

            var sb = new StringBuilder();
            sb.AppendLine("=== SERVER FLORA CENSUS ===");
            sb.AppendLine($"Server scene walked on its own pump. Camera: {centre.X:F0}, {centre.Y:F0}, {centre.Z:F0}; radius {Radius:F0} m.");
            sb.AppendLine($"{seen} entit(ies) seen, {positioned} with world bounds, {near} within radius.");
            sb.AppendLine("\n--- planet EnvironmentSectorStorage state ---");
            sb.Append(storageReport);
            sb.AppendLine("\n--- component histogram within radius ---");
            foreach (var kv in byComponent.OrderByDescending(k => k.Value).Take(40))
                sb.AppendLine($"    {kv.Value,6}  {kv.Key}");
            sb.AppendLine("\n--- nearest samples ---");
            foreach (var s in samples) sb.AppendLine(s);

            var path = Path.Combine(RttLog.OutDir, "server-flora.txt");
            File.WriteAllText(path, sb.ToString());
            RttLog.Line($"SERVER FLORA CENSUS written to {path}: {near} entit(ies) within {Radius:F0} m " +
                        $"of the camera ({seen} in the scene). ZERO here with 448 occupied sectors means the " +
                        "generation side never ran; hundreds means the content exists and the gap is " +
                        "replication or client rendering.");
        }
        catch (Exception e) { RttLog.Error("server flora census", e); }
    }

    // TAG PROVENANCE ACROSS A HOT RELOAD.
    //
    // _tagAddedByUs lives in statics that a reload throws away, while the tag it records
    // lives in the world and does not. Without something durable, the sequence "tag a grid,
    // reload, set the knob to 0" leaves the tag on forever and the next A/B silently runs
    // against a world that is still armed — which is exactly the confound this file exists
    // to avoid. Same durable-consume discipline as loadarea-consumed.marker, for the same
    // reason: an in-memory edge is not a record.
    //
    // The key is the ECS handle, which is stable for the life of the SESSION and meaningless
    // after a restart. That is the correct lifetime: ClientTriggerTag is client-side state
    // and does not survive a restart either, so a stale file simply fails to match.
    private static readonly string TagMarkerPath = Path.Combine(RttLog.OutDir, "clutter-tagged.marker");

    private static string TagKey(object anchorEntity, object data)
    {
        var handle = Prop(data, "Entity");
        var name = Describe(anchorEntity)?.Name ?? "?";
        return $"{handle}|{name}";
    }

    private static string ReadTagMarker()
    {
        try { return File.Exists(TagMarkerPath) ? File.ReadAllText(TagMarkerPath).Trim() : null; }
        catch { return null; }
    }

    private static void WriteTagMarker(string key)
    {
        try { File.WriteAllText(TagMarkerPath, key); } catch { }
    }

    private static void ClearTagMarker()
    {
        try { if (File.Exists(TagMarkerPath)) File.Delete(TagMarkerPath); } catch { }
    }

    // ── THE CAMERA TRIGGER ENTITY ───────────────────────────────────────────────────────
    //
    // The same job as TagAnchorGridForEnvironment above, done the way the ENGINE does it.
    //
    // VoxelObserverSessionComponent.OnAddedToScene is, in full:
    //
    //     _observerEntity = Scene.AddEntity<ClientTriggerTag, WorldTransform, BoundingBoxData>(
    //         default(ClientTriggerTag),
    //         _render.GetSettings().CameraTransform,
    //         new BoundingBoxData { BoundingBox = new BoundingBox(-1f, 1f) });
    //
    // and UpdateObserver then moves that entity to wherever the viewer is, every tick. So
    // the engine's own voxel streaming observer IS an invisible 2 m marker carrying one
    // empty tag. This builds the same thing at the feed camera.
    //
    // WHY THIS BEATS TAGGING THE ANCHOR GRID, which it is meant to replace:
    //   - it mutates nothing the player owns; the grid tag edits a real base
    //   - it works at bare coordinates, where there is no entity to tag at all
    //   - it will work unchanged when the camera is a block on a moving ship: the block
    //     supplies a POSITION, and the marker follows it
    //   - teardown is RemoveEntity, not a TryRemove on someone else's grid
    //
    // SEAT ESTABLISHED FIRST, as everything since the TryLoad freeze has been. Read from the
    // IL, not assumed:
    //   AddEntity<T1,T2,T3>  -> TypeId<T>.Value, GetArchetype, AllocateEntity,
    //                           TryGetDataPointer. A profiler scope and an assert. No
    //                           Scene.FinishBefore, no ContinueOnDCS<SyncPoint>, nothing
    //                           planted in the scheduler's dependency graph.
    //   RemoveEntity         -> consults IsCommandBufferActive() and defers through the
    //                           command buffer when one is open. The engine built the
    //                           re-entrancy guard itself.
    //   SetWorldTransform    -> two EntityData.TryWriteTo stores plus parent maths.
    // That is the same shape as DEntityContext.Set<T>, which has run all session. It is the
    // OPPOSITE of ManagedWorldArea.TryLoad, whose FinishBefore<SpawnSyncPoint> cost three
    // CTDs and a sim freeze.
    //
    // Structural change is still structural: AddEntity moves storage, so it runs ONCE and
    // then only the transform is written. Off by default.
    // THE BUBBLE'S RADIUS, and it is the reason scatter stops at a hard circle.
    //
    // 1.375 m was copied from the engine's own voxel observer, on the reasoning that
    // matching its construction would match its behaviour. It does not, and the difference
    // is what the user spotted from the raised orbit camera: objects spawn inside a sharp
    // disc around the anchor and the ground beyond it is bare.
    //
    // The PlanetEnvironment triggers are SECTORED — they activate the sectors an entity's
    // BOUNDING BOX overlaps. A 1.375 m box overlaps essentially one sector, so a single tiny
    // marker buys a single tiny patch of world however long it sits there. A player is not a
    // bigger box, but a player carries other systems that widen their materialization; our
    // marker has only its extents.
    //
    // Widening is the native lever rather than a trick: same trigger, same mechanism, bigger
    // volume. Sectors are REF-COUNTED (that is what makes them multiplayer-safe), so
    // overlapping our bubble with a player's is not a conflict and cannot double-spawn.
    //
    // COSTS RESIDENT WORLD, roughly with the cube of this. Start modest and watch VRAM; the
    // feed VRAM cap is already the binding constraint.
    private static float MarkerHalfExtent => (float)FeedConfig.CameraTriggerExtent;

    // The legacy signature, kept so SweepStaleMarkers still recognises markers left by an
    // assembly load that ran with the old fixed extent. Without it, changing the knob would
    // orphan every previous marker permanently — they would stop being ours to clean up.
    private const float LegacyMarkerHalfExtent = 1.375f;
    private const double MarkerMoveEpsilon = 1.0;    // metres; below this there is nothing to do

    private static object _marker;            // boxed DEntityContext, or null
    private static Vector3D _markerPos;
    private static bool _markerDisarmed, _markerShapeLogged;

    // The extent each marker was BORN with. BoundingBoxData is written once at creation, so
    // this is what makes a live extent edit detectable at all — comparing the knob against
    // itself would always agree.
    private static double _markerBornExtent = -1.0;
    private static double _serverMarkerBornExtent = -1.0;
    private static long _markerLogTicks;
    private static int _markerMoves;
    private static Type _tTag, _tWt, _tBbd;
    private static MethodInfo _miAddEntity, _miSetWt, _miRemoveEntity;

    // WHICH client scene is the PANEL's? The seat sees several — two client scenes ticked
    // through it on 2026-08-02 — and the client marker is a single static, so it lands in
    // whichever client scene ticks first. If that is not the scene whose trigger system
    // owns the flora triggers, the marker is invisible to them no matter how correct its
    // archetype is, which is precisely what the census reported: the SERVER marker tracked
    // by its triggers, the CLIENT marker tracked by nothing at all.
    internal static object PanelScene;

    internal static void PublishPanelScene(object anyEntityInScene)
    {
        if (PanelScene == null && anyEntityInScene != null)
        {
            PanelScene = SceneOf(anyEntityInScene);
            if (PanelScene != null)
                RttLog.Line($"PANEL SCENE: #{PanelScene.GetHashCode():x8} — the client marker will only be " +
                            "created in THIS scene, the one whose trigger system owns the flora triggers.");
        }
    }

    // SEAT-DRIVEN, since the census verdict. An entity created from the tick thread exists
    // but never enters the trigger system's candidate index: the marker satisfied
    // must:ClientTriggerTag+WorldTransform+BoundingBoxData exactly, the sector volumes
    // demonstrably covered it (COVERS-CAM), and it sat inside ZERO triggers — while the
    // engine's own observer, born inside OnAddedToScene ON THE PUMP, is tracked fine. So
    // the client marker's lifecycle now runs in OnSimPump's client branch, same as the
    // server marker runs in its server branch. Called ONLY from the seat.
    // Teardown for the server marker. Extracted from the knob-off branch so the extent
    // rebuild uses the SAME path: two teardowns that drift apart is how one of them ends up
    // leaving an entity behind, and this one runs in the scene that spawns things.
    // MUST be called on the server scene's own pump — RemoveEntity consults
    // IsCommandBufferActive() and defers, which is only correct from that seat.
    private static void DestroyServerMarker(object scene, string why)
    {
        if (_serverMarker == null || scene == null) return;
        try
        {
            var handle = Prop(_serverMarker, "Entity");
            _miRemoveEntity ??= scene.GetType().GetMethods(Any).FirstOrDefault(
                m => m.Name == "RemoveEntity" && m.GetParameters().Length == 1);
            _miRemoveEntity?.Invoke(scene, new[] { handle });
            _serverMarker = null;
            _serverMarkerBornExtent = -1.0;
            RttLog.Line($"SERVER PRESENCE: marker removed ({why}). Sectors it materialized are the " +
                        "engine's to reclaim.");
        }
        catch (Exception e) { RttLog.Error("server marker teardown", e); }
    }

    // A SAVE IS BEING COLLECTED — see RttBridge.SaveHoldUntilMs. The bootstrap prefix on
    // SaveSessionComponent.SaveGame arms an 8 s hold; both marker drives despawn their
    // marker on the next pump tick and refuse to recreate until it lapses, so nothing of
    // ours can be in the entity set a save walks. The despawn lands within one sim frame
    // of the save starting (SaveGame is async and yields before collection); the marker
    // recreates itself automatically when the hold ends. Cost: a brief presence gap around
    // each save — remote flora refs may dip for a few seconds.
    // IMPLICATED IN A CRASH, 2026-08-04 — hence the knob, and hence it defaults OFF.
    //
    //   18:52:17.648  [Saving] Saving finished Success
    //   18:52:24.977  NullReferenceException
    //                   at VoxelPhysicsComponent.ReleaseChunk(Vector3I)
    //                   at SectoredTrigger.RemoveFromCell(...)
    //                   at SpatialTriggerSystemSessionComponent.RemoveEntityFromTriggers(DEntity)
    //
    // Seven seconds after the save: the 8 s hold expiring, the marker being recreated, and
    // SweepStaleMarkers destroying the previous one. DESTROYING A TRIGGER-REGISTERED MARKER
    // runs the engine's spatial-trigger removal into voxel-physics chunk release, and that
    // NREs. The despawn existed to guarantee nothing of ours reaches a save — a guarantee for
    // a risk that was never actually demonstrated (a save completed with both markers alive,
    // and VR3B is binary so the grep that "found nothing" was blind).
    //
    // Trading a proven crash for an unproven contamination is a bad trade, so this is now
    // opt-in. If save injection is ever demonstrated, the fix is a SAFE despawn — most likely
    // removing the trigger registration before destroying, or letting the marker live and
    // excluding it at serialisation — not this.
    private static FieldInfo _fSaveHold;
    private static bool SaveHoldActive()
    {
        if (!FeedConfig.MarkerDespawnOnSave) return false;
        _fSaveHold ??= Type.GetType("RttProbe.RttBridge, RttProbe")?.GetField("SaveHoldUntilMs");
        return _fSaveHold?.GetValue(null) is long until && Environment.TickCount64 < until;
    }

    private static void DriveClientMarker(object scene)
    {
        if (_markerDisarmed) return;
        if (SaveHoldActive())
        {
            if (_marker != null) DestroyCameraTriggerEntity("a save is being collected — no marker of ours may be in the set it walks");
            return;
        }
        // Same settle gate as the presence entity and the preloads: this marker's whole job is
        // to trigger server-side sector materialisation at the camera, so it must not exist
        // while the world is still initialising planets.
        if (!CameraFeed.ResidencySettled)
        {
            if (_marker != null) DestroyCameraTriggerEntity("the world is still settling after load — " +
                                                           "no marker of ours may drive sector generation yet");
            return;
        }
        if (!FeedConfig.CameraTriggerEntity)
        {
            // Turning the knob off deletes it live, rather than leaving a marker in the world
            // that nothing is steering any more.
            if (_marker != null) DestroyCameraTriggerEntity("cameraTriggerEntity was set to 0");
            return;
        }
        // MASTER GATE (task #40): same shape as the knob-off branch — no live feed, no
        // materialization bubble riding a dead camera.
        if (!Feeds.AnyLive)
        {
            if (_marker != null) DestroyCameraTriggerEntity("no feed is live — master gate");
            return;
        }

        try
        {
            var cameraPos = CameraFeed.PresenceCentre;
            if (cameraPos.LengthSquared() <= 1.0) return;          // no feed target yet
            cameraPos = DriftAround(cameraPos);

            if (_marker == null) { CreateMarker(scene, cameraPos); return; }

            // The extent is baked into BoundingBoxData at birth, so a live edit means nothing
            // until the marker is rebuilt. Doing that here rather than making the operator
            // toggle cameraTriggerEntity off and on is not a convenience: a knob that silently
            // does nothing until an unrelated second action is exactly how an A/B gets misread
            // as "widening the bubble changed nothing".
            if (Math.Abs(_markerBornExtent - FeedConfig.CameraTriggerExtent) > 0.001)
            {
                RttLog.Line($"CAMERA TRIGGER: extent changed {_markerBornExtent:F2} -> " +
                            $"{FeedConfig.CameraTriggerExtent:F2} m; rebuilding the client marker so the " +
                            "new bubble takes effect.");
                DestroyCameraTriggerEntity("cameraTriggerExtent changed");
                return;                                    // reborn on the next pass
            }

            if ((cameraPos - _markerPos).LengthSquared() >= MarkerMoveEpsilon * MarkerMoveEpsilon)
                MoveMarker(cameraPos);

            // TIME CADENCE ONLY, the rule this project learned at 4,200 lines/sec: a path
            // that runs every tick may log on a clock and never on "something changed".
            var now = Environment.TickCount64;
            if (now - _markerLogTicks > 30000)
            {
                _markerLogTicks = now;
                RttLog.Line($"CAMERA TRIGGER: marker alive at {_markerPos.X:F0},{_markerPos.Y:F0},{_markerPos.Z:F0} " +
                            $"({_markerMoves} move(s) so far). If sectors are materializing, trees and boulders " +
                            "arrive within a couple of minutes and BEFORE the terrain under them meshes — the " +
                            "\"floating rocks\" stage is expected, not a fault.");
            }
        }
        catch (Exception e)
        {
            // Disarm rather than throw every tick. A world mutation that faults once will
            // fault again, and the log flood is worse than the missing feature.
            _markerDisarmed = true;
            RttLog.Error("camera trigger entity", e);
        }
    }

    private static void CreateMarker(object scene, Vector3D pos)
    {
        _tTag ??= Type.GetType("Keen.VRage.Core.Game.GameSystems.GamePruning.ClientTriggerTag, VRage.Core.Game");
        _tWt  ??= Type.GetType("Keen.VRage.Core.WorldTransform, VRage.Core");
        _tBbd ??= Type.GetType("Keen.VRage.Core.Game.Data.BoundingBoxData, VRage.Core.Game");

        // AddEntity<T1,T2,T3>(T1,T2,T3). Matched on arity rather than by signature so a
        // parameter type rename does not silently select the wrong overload.
        if (scene != null && _miAddEntity == null)
            _miAddEntity = scene.GetType().GetMethods(Any).FirstOrDefault(
                m => m.Name == "AddEntity" && m.IsGenericMethodDefinition
                  && m.GetGenericArguments().Length == 3 && m.GetParameters().Length == 3);

        if (scene == null || _tTag == null || _tWt == null || _tBbd == null || _miAddEntity == null)
        {
            if (!_markerShapeLogged)
            {
                _markerShapeLogged = true;
                RttLog.Line("CAMERA TRIGGER: shape missing — " +
                            $"scene={(scene == null ? "NOT FOUND" : "ok")} " +
                            $"ClientTriggerTag={(_tTag == null ? "NOT FOUND" : "ok")} " +
                            $"WorldTransform={(_tWt == null ? "NOT FOUND" : "ok")} " +
                            $"BoundingBoxData={(_tBbd == null ? "NOT FOUND" : "ok")} " +
                            $"AddEntity<3>={(_miAddEntity == null ? "NOT FOUND" : "ok")}. " +
                            "The marker cannot be built on this game build; the feed is unaffected.");
            }
            _markerDisarmed = true;
            return;
        }

        // Before creating one, remove any WE left behind. See SweepStaleMarkers.
        SweepStaleMarkers(scene);

        var bbd = Activator.CreateInstance(_tBbd);
        _tBbd.GetField("BoundingBox", Any)?.SetValue(
            bbd, new BoundingBox(new Vector3(-MarkerHalfExtent), new Vector3(MarkerHalfExtent)));

        _marker = _miAddEntity.MakeGenericMethod(_tTag, _tWt, _tBbd).Invoke(
            scene, new[] { Activator.CreateInstance(_tTag), Activator.CreateInstance(_tWt, new object[] { pos }), bbd });
        _markerPos = pos;
        _markerMoves = 0;
        _markerBornExtent = FeedConfig.CameraTriggerExtent;   // what the box was actually built at

        // DynamicTag, decoded from the census: the client scene's voxel-sector triggers
        // (Voxel : Block / Voxel : Prediction) constrain on DynamicTag+WorldTransform+
        // BoundingBoxData — ClientTriggerTag never moves voxel DATA. Set<T> is a
        // structural archetype change, done once here at birth.
        var dynAdded = false;
        if (FeedConfig.CameraTriggerDynamicTag)
        {
            try
            {
                var tDyn = Type.GetType("Keen.VRage.Physics.Data.DynamicTag, VRage.Physics");
                var set = _marker.GetType().GetMethods(Any).FirstOrDefault(
                    m => m.Name == "Set" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
                if (tDyn != null && set != null)
                {
                    set.MakeGenericMethod(tDyn).Invoke(_marker, new[] { Activator.CreateInstance(tDyn) });
                    dynAdded = true;
                }
            }
            catch (Exception e) { RttLog.Error("marker DynamicTag", e); }
        }

        // Born staged, de-staged next seat tick — the transition the index listens for.
        Stage(_marker);
        _pendingDestage = _marker;

        RttLog.Line($"CAMERA TRIGGER: created the marker entity at {pos.X:F0},{pos.Y:F0},{pos.Z:F0} — " +
                    // The size was HARDCODED as "2.75 m box" here, and once the extent became a
                    // knob that string was a lie: it kept reporting 2.75 while the box was
                    // built at whatever cameraTriggerExtent said. A diagnostic that states a
                    // constant instead of reading the value is worse than no diagnostic —
                    // it is what made the first widened-bubble test unreadable.
                    $"ClientTriggerTag{(dynAdded ? " + DynamicTag" : "")} + WorldTransform + a " +
                    $"{MarkerHalfExtent * 2.0:F2} m box (half-extent {MarkerHalfExtent:F2}), " +
                    "born STAGED (de-stage follows one tick later). Per the census: ClientTriggerTag " +
                    "satisfies the client flora-sector triggers, DynamicTag the client voxel-sector " +
                    "triggers. Watch the census 'inside' column and VRAM.");
    }

    private static void MoveMarker(Vector3D pos)
    {
        var wt = Activator.CreateInstance(_tWt, new object[] { pos });

        // EntityTransformFunctions.SetWorldTransform(DEntityContext, ref WorldTransform) is
        // what the engine's own AlignToRenderCamera calls from OUTSIDE a job — the same seat
        // we are in. It also maintains RelativeTransform for parented entities, which a raw
        // Set<WorldTransform> does not; ours has no parent, but matching the engine costs
        // nothing and stops being a guess the day the marker gets attached to a block.
        _miSetWt ??= Type.GetType("Keen.VRage.Core.Game.Data.EntityTransformFunctions, VRage.Core.Game")
            ?.GetMethods(Any).FirstOrDefault(m => m.Name == "SetWorldTransform" && m.GetParameters().Length == 2);

        if (_miSetWt != null)
        {
            _miSetWt.Invoke(null, new[] { _marker, wt });
        }
        else
        {
            // Fallback: the raw store, which is what the engine's per-tick UpdateObserver job
            // uses. Enough to move a parentless entity.
            var set = _marker.GetType().GetMethods(Any).FirstOrDefault(
                m => m.Name == "Set" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            set?.MakeGenericMethod(_tWt).Invoke(_marker, new[] { wt });
        }

        _markerPos = pos;
        _markerMoves++;
    }

    internal static void DestroyCameraTriggerEntity(string why)
    {
        if (_marker == null) return;
        try
        {
            var scene = Prop(_marker, "Scene");
            var handle = Prop(_marker, "Entity");            // DEntity
            _miRemoveEntity ??= scene?.GetType().GetMethods(Any).FirstOrDefault(
                m => m.Name == "RemoveEntity" && m.GetParameters().Length == 1);
            _miRemoveEntity?.Invoke(scene, new[] { handle });
            RttLog.Line($"CAMERA TRIGGER: marker removed ({why}). Sectors it materialized are the " +
                        "engine's to reclaim on its own schedule — they do not vanish with it.");
        }
        catch (Exception e) { RttLog.Error("camera trigger entity remove", e); }
        finally { _marker = null; _markerMoves = 0; _markerBornExtent = -1.0; }
    }

    // STALE MARKERS FROM A PREVIOUS ASSEMBLY LOAD.
    //
    // A hot reload drops our statics but NOT the entity they pointed at, so every reload with
    // the knob on would otherwise leave one orphan marker behind, each still triggering
    // sectors at wherever the camera happened to be. That compounds, and this project already
    // has a VRAM ratchet it cannot explain — leaking world entities on top of it would make
    // that measurement worse and harder to read.
    //
    // IDENTIFICATION IS THE WHOLE RISK HERE. The engine's own voxel observer has the SAME
    // three components, so an archetype match would delete the player's terrain streaming
    // observer — a catastrophic false positive. What distinguishes ours is the box: the
    // engine's is exactly (-1, 1) and ours is (-1.375, 1.375), a value chosen for no reason
    // other than that nothing else will have it. All six components of the AABB must match,
    // every removal is logged with its position so a mistake is visible rather than silent,
    // and finding more than a couple of candidates aborts the sweep entirely on the grounds
    // that the identification must then be wrong.
    private static void SweepStaleMarkers(object scene)
    {
        try
        {
            _miEnumerate ??= scene.GetType().GetMethod("EnumerateEntities", Any, null, Type.EmptyTypes, null);
            _tCtx ??= Type.GetType("Keen.VRage.DCS.Accessors.DEntityContext, VRage.DCS");
            if (_miEnumerate == null || _tCtx == null) return;
            if (_miEnumerate.Invoke(scene, null) is not IEnumerable handles) return;

            var hasT = _tCtx.GetMethods(Any).FirstOrDefault(
                m => m.Name == "Has" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            var getT = _tCtx.GetMethods(Any).FirstOrDefault(
                m => m.Name == "Get" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            if (hasT == null || getT == null) return;

            // Closed ONCE. MakeGenericMethod per entity over 34k entities is the difference
            // between a one-shot pause and a visible hitch.
            var hasTag = hasT.MakeGenericMethod(_tTag);
            var hasBox = hasT.MakeGenericMethod(_tBbd);
            var getBox = getT.MakeGenericMethod(_tBbd);
            var fBox = _tBbd.GetField("BoundingBox", Any);
            if (fBox == null) return;

            // COLLECT, THEN REMOVE. Removing inside the walk is a structural change during
            // iteration, which is the one thing an archetype-backed enumeration cannot survive.
            var doomed = new List<object>();
            var scanned = 0;
            foreach (var handle in handles)
            {
                scanned++;
                try
                {
                    var ctx = Activator.CreateInstance(_tCtx, scene, handle);
                    if (!(bool)hasTag.Invoke(ctx, null)) continue;
                    if (!(bool)hasBox.Invoke(ctx, null)) continue;
                    if (fBox.GetValue(getBox.Invoke(ctx, null)) is not BoundingBox box) continue;
                    if (!IsMarkerBox(box)) continue;
                    doomed.Add(ctx);
                }
                catch { }
            }

            if (doomed.Count == 0)
            {
                RttLog.Line($"CAMERA TRIGGER: swept {scanned} entities, no orphan markers — clean start.");
                return;
            }
            if (doomed.Count > 4)
            {
                RttLog.Line($"CAMERA TRIGGER: sweep found {doomed.Count} candidate markers among {scanned} " +
                            "entities, which is more than this feature can ever have created. The box signature " +
                            "must be matching something it should not, so NOTHING was removed. Investigate " +
                            "before turning this knob on again.");
                return;
            }

            foreach (var ctx in doomed)
            {
                var handle = Prop(ctx, "Entity");
                _miRemoveEntity ??= scene.GetType().GetMethods(Any).FirstOrDefault(
                    m => m.Name == "RemoveEntity" && m.GetParameters().Length == 1);
                _miRemoveEntity?.Invoke(scene, new[] { handle });
                RttLog.Line($"CAMERA TRIGGER: removed an orphan marker left by a previous assembly load " +
                            $"(entity {handle}). This is expected after a hot reload with the knob on.");
            }
        }
        catch (Exception e) { RttLog.Error("camera trigger sweep", e); }
    }

    // Recognise a marker of OURS. The extent is now a knob, so the signature has to accept
    // both the current value and the legacy 1.375 — otherwise turning the knob orphans every
    // marker a previous assembly load left behind, and an orphan we no longer recognise is an
    // orphan nobody will ever delete.
    private static bool IsMarkerBox(BoundingBox b)
    {
        return IsCubeOfHalfExtent(b, MarkerHalfExtent) || IsCubeOfHalfExtent(b, LegacyMarkerHalfExtent);
    }

    private static bool IsCubeOfHalfExtent(BoundingBox b, float h)
    {
        // Scaled tolerance: 1e-4 is right for a 1.375 m box and meaninglessly tight for a
        // 500 m one, where float storage alone loses more than that.
        float eps = Math.Max(1e-4f, h * 1e-5f);
        return Math.Abs(b.Max.X - h) < eps && Math.Abs(b.Min.X + h) < eps
            && Math.Abs(b.Max.Y - h) < eps && Math.Abs(b.Min.Y + h) < eps
            && Math.Abs(b.Max.Z - h) < eps && Math.Abs(b.Min.Z + h) < eps;
    }

    // PER-BODY CLIPMAP CAMERA — the terrain fix.
    //
    // Called from the bootstrap's prefix on VoxelRenderUpdateSessionComponent.UpdateClipmap,
    // once per voxel body per frame. Returns a replacement boxed WorldTransform, or null to
    // leave the engine's choice alone.
    //
    // THE RULE, and it is designed so the player can NEVER lose:
    //     take over a body only when the feed camera is closer to it than the player is,
    //     AND the player is further away than MinPlayerDistance.
    // Both conditions, always. A body the player is anywhere near keeps the player's camera,
    // full stop. What we take over is terrain the player is provably not looking at.
    //
    // WHY THIS IS NOT THE TUG-OF-WAR. Swapping RenderSettings.CameraTransform would be: one
    // global, two writers, both directions rebuilt every round — the same shape as the LOD
    // popping bug. This is the opposite. Every clipmap still receives exactly ONE camera per
    // frame; we only choose WHICH viewer that is, per body, and the choice is stable because
    // it is a distance comparison rather than a race. Two bodies, two viewers, no contention.
    //
    // KNOWN LIMITATION, stated up front: when the player and the camera are near the SAME
    // body (a camera 20 km away on the player's own planet), both distances are ~the body
    // radius, the rule declines, and that terrain stays coarse. One clipmap cannot have two
    // centres. Same-planet remote detail needs the per-feed clipmap build; this fixes the
    // cross-body case, which is the headline "camera on another world" product.
    private static int _clipmapOverrides, _clipmapCalls;
    private static long _clipmapLogTicks;

    // DISTINCT-BODY ACCOUNTING, split so the hot path never formats a string.
    //
    // This used to be a HashSet<string> fed by an interpolated "$x,y,z(d=…km)" per OVERRIDE —
    // ~880 formatted strings a second to produce a log line that prints EIGHT of them, once
    // per 15 s. The count and the samples are different needs, so they are now different
    // structures: a long key for "how many distinct bodies" (no allocation at all) and a
    // small list of formatted samples that stops growing at the number actually printed.
    private static readonly HashSet<long> _windowBodyKeys = new();
    private static readonly List<string> _windowBodySamples = new();
    private const int WindowBodySampleCap = 8;

    // THE ARRIVAL BURST. The user's test was exact: a functioning system meshes in SECONDS,
    // because that is what happens when the PLAYER arrives anywhere — via the sync-loader's
    // loadingPhase=true sweep, not the steady-state path amortized to fractions of a
    // millisecond per frame. UpdateClipmap's third argument IS that flag, and the bootstrap
    // prefix can set it. For a bounded window after the feed camera jumps to a new site,
    // overridden bodies run in loading phase — spawn-speed meshing — then drop back to
    // steady state. Unbounded loading phase is deliberately not offered: it is the spawn
    // path, and nothing about it is budgeted for running forever.
    private static Vector3D _lastBurstOrigin;

    // METERED v2, after the un-metered version removed the device inside a minute
    // (every overridden body, 20 s of solid loadingPhase -> CreateCommittedTexture flood ->
    // DXGI_ERROR_DEVICE_REMOVED). loadingPhase is the SPAWN path, sized for a loading
    // screen; live, it must be fed in sips:
    //   - PLANET-SCALE bodies only (size > 10 km of voxels) — the terrain that matters,
    //     never the boulder swarm
    //   - 500 ms ON / 2500 ms OFF duty cycle, inside a 60 s arrival window (~10 s total ON)
    //   - hard abort while VRAM headroom is under 1.8 GB, checked every pulse
    // Off by default behind clipmapArrivalBurst; the knob exists now because the metering
    // does.
    private static long _burstWindowStart = long.MinValue;
    private static long _vramAbortLogTicks;

    private static bool ArrivalBurst(Vector3D feedPos, double bodySize)
    {
        if (!FeedConfig.ClipmapArrivalBurst) return false;
        if (bodySize <= 10000) return false;

        var now = Environment.TickCount64;
        if ((feedPos - _lastBurstOrigin).LengthSquared() > 2000.0 * 2000.0)
        {
            _lastBurstOrigin = feedPos;
            _burstWindowStart = now;
            RttLog.Line($"CLIPMAP ARRIVAL BURST (metered): camera jumped to " +
                        $"{feedPos.X:F0},{feedPos.Y:F0},{feedPos.Z:F0} — planet-scale bodies get " +
                        "loadingPhase pulses (500 ms on / 2500 ms off) for the next 60 s, VRAM permitting.");
        }
        if (now - _burstWindowStart > 60000) return false;
        if ((now - _burstWindowStart) % 3000 >= 500) return false;     // duty: 1/6 on

        var used = Perf.SampleVramMb();
        var avail = Perf.SampleVramAvailMb();
        if (used > 0 && avail > 0 && avail - used < 1800)
        {
            if (now - _vramAbortLogTicks > 15000)
            {
                _vramAbortLogTicks = now;
                RttLog.Line($"CLIPMAP ARRIVAL BURST: paused — VRAM headroom {avail - used} MB < 1800 MB. " +
                            "The un-metered version died exactly here; the pulse resumes when memory frees.");
            }
            return false;
        }
        return true;
    }

    // CACHED REFLECTION FOR THE CLIPMAP PATH — the same decision EnsureFloraReflection made,
    // finally applied here too.
    //
    // MEASURED 2026-08-02: this method runs 570,419 times per 15 s window (~38,000/sec) and
    // was doing SEVEN uncached GetField/GetProperty/GetMethod lookups per call, three of them
    // through Prop() — which additionally calls GetIndexParameters(), and that ALLOCATES a
    // ParameterInfo[] every time. So the steady state was roughly 114,000 throwaway arrays
    // and 266,000 reflection name lookups PER SECOND, on renderer job threads, for a method
    // whose actual work is six subtractions and two square roots.
    //
    // The flora path forty lines below already carried the comment "this path runs a quarter
    // of a million times a second; a GetField per call is not affordable" and a full cache to
    // match. The lesson simply never crossed over. Every handle here is resolved once and is
    // stable for the process, exactly as there.
    private static FieldInfo _cmTransformPos, _cmL2wPos, _cmSizeX, _cmSizeY, _cmSizeZ;
    private static PropertyInfo _cmClipmap, _cmLocalToWorld, _cmSize;
    private static MethodInfo _cmClone;
    private static bool _cmReflectionReady, _cmReflectionFailed;

    // Resolve the whole set in one go. All-or-nothing on purpose: a half-resolved set would
    // silently fall back to the slow path for some members and not others, which is the
    // hardest kind of performance bug to see later.
    private static bool EnsureClipmapReflection(object renderComponent, object boxedTransform)
    {
        if (_cmReflectionReady) return true;
        if (_cmReflectionFailed) return false;
        try
        {
            var tT = boxedTransform.GetType();
            _cmTransformPos = tT.GetField("Position", Any);
            _cmClone = tT.GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance);

            _cmClipmap = renderComponent?.GetType().GetProperty("Clipmap", Any);
            var clip = _cmClipmap?.GetValue(renderComponent);
            if (clip == null) { _cmReflectionFailed = true; return false; }

            var tC = clip.GetType();
            _cmLocalToWorld = tC.GetProperty("LocalToWorld", Any);
            _cmSize = tC.GetProperty("Size", Any);

            var l2w = _cmLocalToWorld?.GetValue(clip);
            if (l2w != null) _cmL2wPos = l2w.GetType().GetField("Position", Any);

            var sz = _cmSize?.GetValue(clip);
            if (sz != null)
            {
                var tS = sz.GetType();
                _cmSizeX = tS.GetField("X", Any);
                _cmSizeY = tS.GetField("Y", Any);
                _cmSizeZ = tS.GetField("Z", Any);
            }

            if (_cmTransformPos == null || _cmClipmap == null || _cmLocalToWorld == null)
            { _cmReflectionFailed = true; return false; }

            _cmReflectionReady = true;
            RttLog.Line("CLIPMAP CAMERA: reflection cached (position, clipmap, local-to-world, size, clone). " +
                        "This path runs ~38,000 times a second — before caching it did seven name lookups " +
                        "and three ParameterInfo[] allocations per call.");
            return true;
        }
        catch { _cmReflectionFailed = true; return false; }
    }

    internal static object ChooseClipmapCamera(object renderComponent, object boxedTransform)
    {
        // MASTER GATE (task #40): with no feed live, every body follows the engine's own
        // camera choice — the redirection that kept 11 bodies LODing around a DEAD feed's
        // camera (observed in the 21:16 dormant log) stops here.
        if (!Feeds.AnyLive) return null;
        if (!FeedConfig.PerBodyClipmapCamera || boxedTransform == null) return null;
        _clipmapCalls++;
        if (!EnsureClipmapReflection(renderComponent, boxedTransform)) return null;
        try
        {
            // Where the feed camera is. SubjectCentreCache is the orbit CENTRE, published by
            // the tick; the eye orbits within tens of metres of it, which is far below any
            // distance this decision turns on.
            var feedPos = CameraFeed.PresenceCentre;
            if (feedPos.LengthSquared() <= 1.0) return null;      // no feed target yet

            if (_cmTransformPos.GetValue(boxedTransform) is not Vector3D playerPos) return null;

            // The body's origin, via its clipmap's local-to-world. For a planet this is the
            // CENTRE, so both distances are measured to the same reference and the
            // comparison stays meaningful (a surface dweller sits ~radius from it).
            var clip = _cmClipmap.GetValue(renderComponent);
            var l2w = clip == null ? null : _cmLocalToWorld.GetValue(clip);
            if (l2w == null || _cmL2wPos == null) return null;
            if (_cmL2wPos.GetValue(l2w) is not Vector3D bodyPos) return null;

            // CORNER, NOT CENTRE — the bug that hid the planet from this override for the
            // project's whole life. Clipmap.LocalToWorld.Position is the voxel volume's
            // MIN-CORNER: Verdure's 262k-voxel body reads corner = centre − 131,072 per
            // axis, which put "the planet" 267 km from a camera standing on its surface —
            // outside every cap ever configured. Every sharpening ever observed came from
            // 512-voxel patches and boulders, whose corner≈centre. Distances are now taken
            // to the true centre (corner + Size/2, one voxel ≈ 1 m).
            double sx = 0, sy = 0, sz = 0;
            try
            {
                var szObj0 = _cmSize?.GetValue(clip);
                if (szObj0 != null)
                {
                    if (_cmSizeX?.GetValue(szObj0) is int ix) sx = ix;
                    if (_cmSizeY?.GetValue(szObj0) is int iy) sy = iy;
                    if (_cmSizeZ?.GetValue(szObj0) is int iz) sz = iz;
                }
            }
            catch { }
            var bodyCentre = bodyPos + new Vector3D(sx * 0.5, sy * 0.5, sz * 0.5);

            var dPlayer = (playerPos - bodyCentre).Length();
            var dFeed   = (feedPos   - bodyCentre).Length();

            // A CLEAR MARGIN, not merely "nearer". The first version used dFeed < dPlayer and
            // immediately took over asteroids where both viewers were 150 km away and the
            // difference was metres — a near-tie is noise, not intent, and overriding on it
            // churns a clipmap for no benefit. Requiring the camera to be at least twice as
            // close makes the decision stable and obviously-correct: the cross-body case
            // clears it by orders of magnitude (61 km vs 4,000 km), a tie cannot.
            if (dFeed * 2.0 >= dPlayer) return null;
            if (dPlayer < FeedConfig.ClipmapMinPlayerDistance) return null;   // player too close to risk it

            // THE RESIDENCY CONE IS DELIBERATELY *NOT* APPLIED HERE. Two reasons, and the
            // first is a bug I nearly shipped.
            //
            // A body's cone position has to be tested against its centre, and a planet's
            // centre is its CORE — Verdure's is ~131 km from a camera standing on its surface,
            // so the 300 m near-shell exemption does not protect it at all. The angle from the
            // view axis to the core is fine for a downward look (~45 deg) but exceeds 70 deg
            // the moment the camera looks up, at which point the cone would reject THE PLANET
            // and the feed would lose its terrain. The near-shell reasoning that makes this
            // safe for flora sectors (metres across, hundreds of metres away) does not
            // transfer to bodies that are tens of kilometres across.
            //
            // Second, and sufficient on its own: the 77.4% prize was measured on FLORA SECTOR
            // updates. Extending the cone to voxel bodies extrapolates past the evidence, and
            // this path is ~880 calls/sec against flora's ~19,000 — the prize is not here.
            //
            // A body-aware version (test the nearest point on the bounding sphere, not the
            // centre) is a real option later, but it needs its own measurement first.

            // AND the camera must actually be NEAR this body — where "near" is measured in
            // the body's OWN SCALE, not a fixed radius. The fixed 80 km cap admitted a
            // 242-boulder field 25 km away (the debris of the virgin-site materialization
            // test) and the frame budget split 242 ways starved the one body that mattered,
            // the planet. A body qualifies when the camera stands within ~its own size:
            // a planet (tens of km) qualifies from anywhere near it, a 64 m boulder only
            // from a few hundred metres. The floor keeps nearby scatter meshing around the
            // camera; the fixed cap survives as the outer sanity bound and the fallback
            // when the clipmap does not expose a usable size.
            // Size was already read for the centre correction above; the max axis is the
            // body's scale. For a planet the sensible "near" is ~its RADIUS (size/2) with
            // margin — a surface camera sits at radius from the centre — and the fixed cap
            // stays as the outer sanity bound for anything larger still.
            var bodySize = Math.Max(sx, Math.Max(sy, sz));
            var nearCap = bodySize > 1.0
                ? Math.Max(bodySize * 0.75, 1500.0)
                : FeedConfig.ClipmapMaxFeedDistance;
            if (dFeed > nearCap) return null;

            // Replace ONLY the position, on a copy, so the transform's orientation and any
            // other field the engine set survive untouched.
            var replacement = _cmClone?.Invoke(boxedTransform, null) ?? boxedTransform;
            _cmTransformPos.SetValue(replacement, feedPos);
            _clipmapOverrides++;

            // TIME CADENCE ONLY. The first version also logged whenever the BODY changed,
            // which with many bodies in range meant a line per body per frame — it hit 4,200
            // lines/sec and put 180 MB into rtt.log before the spike detector caught it. A
            // per-frame path may only ever log on a clock, never on "something differed".
            //
            // The window ACCUMULATES the distinct bodies overridden, because the single
            // last-body line hid the question that mattered for five minutes tonight:
            // is the PLANET among the overridden bodies, or only the small rocks?
            // Key on the body's position quantised to a kilometre. Bodies are thousands of
            // metres apart, so this cannot collide between distinct bodies, and it costs three
            // casts instead of a string format plus a hash of it.
            long bodyKey = ((long)(bodyPos.X * 0.001) * 73856093)
                         ^ ((long)(bodyPos.Y * 0.001) * 19349663)
                         ^ ((long)(bodyPos.Z * 0.001) * 83492791);
            if (_windowBodyKeys.Add(bodyKey) && _windowBodySamples.Count < WindowBodySampleCap)
                _windowBodySamples.Add($"{bodyPos.X:F0},{bodyPos.Y:F0},{bodyPos.Z:F0}(d={dFeed / 1000.0:F1}km)");

            var now = Environment.TickCount64;
            if (now - _clipmapLogTicks > 15000)
            {
                _clipmapLogTicks = now;
                RttLog.Line($"CLIPMAP CAMERA: {_windowBodyKeys.Count} distinct bod(ies) LODing around the FEED " +
                            $"this window: {string.Join("  ", _windowBodySamples)}" +
                            $"{(_windowBodyKeys.Count > WindowBodySampleCap ? " …" : "")} " +
                            $"({_clipmapOverrides} override(s) of {_clipmapCalls} body-updates; one line per 15 s.)");
                _windowBodyKeys.Clear();
                _windowBodySamples.Clear();
            }
            ApplyClipmapBudget();
            ProbeLodDataSharing(renderComponent);

            // Spawn-speed meshing for a bounded window after arrival. The pair return shape
            // needs the matching bootstrap; an older one treats the array as no replacement
            // at all, so the burst degrades to "override off" rather than half-applied —
            // which is why the pair is returned ONLY while a burst pulse is live.
            if (ArrivalBurst(feedPos, bodySize)) return new object[] { replacement, true };
            return replacement;
        }
        catch { return null; }
    }

    // IS THE MOVE GATE SHARED ACROSS BODIES? — settle the plateau hypothesis by measurement.
    //
    // IL says VoxelRenderUpdateSessionComponent._lodDistances is a 16-slot LODDataArray that
    // Init() fills indexed by LOD LEVEL, and that CheckNeedsMove stores LastPosition into the
    // slot UpdateClipmap picked. If that array is shared across bodies, my per-body camera
    // override makes the feed and the player fight over the same 16 LastPositions, the gate
    // reports "needs move" forever, and the finer rings never settle — the observed plateau.
    //
    // Rather than trust a reading of IL fragments, print the slots. If LastPosition values
    // sit near BOTH viewers at once (3,907 km apart) the array is shared and the hypothesis
    // holds; if they all cluster near one, it does not and the cap is elsewhere.
    private static long _lodProbeTicks;
    private static object _lodOwner;
    private static Vector3D? _lastLodSlot0;

    // THE CLIPMAP FRAME BUDGET — prime suspect for the mid-LOD plateau.
    //
    // VoxelRenderUpdateSessionComponent's ctor sets _updateTimeout to
    // TimeSpan.FromMilliseconds(0.5). HALF A MILLISECOND is the entire per-frame allowance
    // for ALL clipmap updates: UpdateClipmaps walks every voxel body and bails the moment
    // IsTimingOut trips. Adding a second viewer's body to that budget without widening it
    // starves the finer LOD rings, which is exactly the observed plateau — and it explains
    // why 20 minutes changed nothing (the budget resets every frame; more time buys nothing
    // if each frame gives up at the same point).
    //
    // THIS IS GLOBAL AND IT COSTS THE PLAYER. UpdateTimeout is read inside the engine's own
    // loop, not around our render, so it cannot be scoped to the feed the way shadow settings
    // are. Raising it hands more of every frame to terrain meshing for everyone. 0 = leave
    // the engine's value alone, and that stays the default.
    private static double _budgetApplied;
    private static object _budgetProp;
    private static object _budgetOriginal;   // the engine's own UpdateTimeout, saved at first apply

    private static void ApplyClipmapBudget()
    {
        // MASTER GATE (task #40): the widened budget is a GLOBAL that spends the player's
        // frame for feed terrain detail — with no feed live it must give the engine its own
        // value back, not sit at our number forever (it did, and the 21:16 dormant log paid
        // for it). Restore-on-dormant, re-apply on the next live pass.
        var want = Feeds.AnyLive ? FeedConfig.ClipmapUpdateBudgetMs : 0;
        if (want <= 0)
        {
            if (_budgetApplied > 0 && _budgetProp is System.Reflection.PropertyInfo pr && _lodOwner != null && _budgetOriginal != null)
            {
                try
                {
                    pr.SetValue(_lodOwner, _budgetOriginal);
                    RttLog.Line($"CLIPMAP BUDGET restored to the engine's own {_budgetOriginal} " +
                                (Feeds.AnyLive ? "(knob cleared)." : "(no feed live — master gate)."));
                }
                catch (Exception e) { RttLog.Error("clipmap budget restore", e); }
                _budgetApplied = 0;
            }
            return;
        }
        if (Math.Abs(want - _budgetApplied) < 0.01) return;
        try
        {
            _lodOwner ??= Type.GetType("RttProbe.RttBridge, RttProbe")
                              ?.GetField("VoxelUpdateComponent")?.GetValue(null);
            if (_lodOwner == null) return;
            var p = _lodOwner.GetType().GetProperty("UpdateTimeout", Any);
            if (p == null || !p.CanWrite) return;
            var was = p.GetValue(_lodOwner);
            _budgetOriginal ??= was;
            p.SetValue(_lodOwner, TimeSpan.FromMilliseconds(want));
            _budgetApplied = want; _budgetProp = p;
            RttLog.Line($"CLIPMAP BUDGET: UpdateTimeout {was} -> {want} ms. The engine ships 0.5 ms " +
                        "for ALL clipmap updates per frame, and UpdateClipmaps bails on IsTimingOut — " +
                        "so a second viewer's body competes for that same half-millisecond. This is " +
                        "GLOBAL: it buys terrain detail everywhere and spends the player's frame time " +
                        "to do it. Watch the player's fps, not just the feed's. Restored automatically " +
                        "while no feed is live (master gate).");
        }
        catch (Exception e) { RttLog.Error("clipmap budget", e); _budgetApplied = want; }
    }

    private static void ProbeLodDataSharing(object renderComponent)
    {
        var now = Environment.TickCount64;
        if (now - _lodProbeTicks < 8000) return;
        _lodProbeTicks = now;
        try
        {
            // The session component owning the array is the one the engine calls; reach it
            // from the component's Session rather than assuming a static.
            // renderComponent is a COMPONENT, not an entity — FindSessionComponent expects an
            // entity and returned null instantly, which is why the first probe reported
            // "unreadable". VoxelRenderComponent is a GameComponent and carries Session
            // directly; go through that to the session-components entity.
            // From the bootstrap's captured __instance. VoxelRenderUpdateSessionComponent is
            // genuinely NOT on the session-components entity — the logic walked that roster
            // and it is absent — so the patch site is the only reliable handle on it.
            _lodOwner ??= Type.GetType("RttProbe.RttBridge, RttProbe")
                              ?.GetField("VoxelUpdateComponent")?.GetValue(null);
            var arr = Prop(_lodOwner, "_lodDistances");
            if (arr == null)
            {
                RttLog.Line("LOD PROBE: _lodDistances unreadable " +
                            $"(owner={(_lodOwner == null ? "NOT FOUND on the session entity" : _lodOwner.GetType().Name)}, " +
                            $"renderComponent={renderComponent?.GetType().Name}).");
                return;
            }

            var sb = new StringBuilder("LOD PROBE: _lodDistances slots (LastPosition per slot). " +
                "Feed camera at " +
                $"{CameraFeed.PresenceCentre.X:F0},{CameraFeed.PresenceCentre.Y:F0}," +
                $"{CameraFeed.PresenceCentre.Z:F0}.");
            // InlineArray: invisible to plain reflection (its span cannot be boxed), reach
            // elements through the int indexer — the lesson the planet-up scan already paid for.
            var idx = arr.GetType().GetProperties(Any)
                .FirstOrDefault(p => p.GetIndexParameters().Length == 1 &&
                                     p.GetIndexParameters()[0].ParameterType == typeof(int));
            if (idx == null)
            {
                // NO INDEXER: LODDataArray is an [InlineArray], and a C# inline array has no
                // indexer at all — the compiler lowers element access to InlineArrayAsSpan,
                // and a Span cannot be boxed for reflection. (Same trap the planet-up scan
                // hit; there the type DID expose an int indexer, here it does not.)
                //
                // But an inline array's ONE field IS element 0. A single slot is enough for
                // the question being asked: sample slot 0's LastPosition over time. If it
                // JUMPS between positions ~3,900 km apart, the array is shared between the
                // player's bodies and the feed's and the move gate is thrashing. If it stays
                // put near one viewer, it is not shared and the plateau is elsewhere.
                var f0 = arr.GetType().GetFields(Any).FirstOrDefault(f => !f.IsStatic);
                var slot0 = f0?.GetValue(arr);
                var lp0 = Prop(slot0, "LastPosition");
                if (lp0 is Vector3D v0)
                {
                    var dFeed = (v0 - CameraFeed.PresenceCentre).Length();
                    sb.Append($"\n    slot[0] (inline array, element 0 only) LastPosition=" +
                              $"{v0.X:F0},{v0.Y:F0},{v0.Z:F0}  — {dFeed / 1000.0:F0} km from the feed camera");
                    if (_lastLodSlot0.HasValue)
                    {
                        var jump = (v0 - _lastLodSlot0.Value).Length();
                        sb.Append($"\n    moved {jump / 1000.0:F1} km since the previous sample " +
                                  (jump > 100000
                                    ? "— A JUMP OF THIS SIZE BETWEEN SAMPLES MEANS THE SLOT IS SHARED between the "
                                      + "player's bodies and the feed's, so the move gate never settles. Plateau explained."
                                    : "— stable, so this slot is NOT being fought over; the plateau is elsewhere."));
                    }
                    _lastLodSlot0 = v0;
                }
                else sb.Append($"\n    slot[0] unreadable (field={f0?.Name}, type={arr.GetType().Name})");
            }
            else
                for (int i = 0; i < 16; i++)
                {
                    object slot = null; try { slot = idx.GetValue(arr, new object[] { i }); } catch { }
                    if (slot == null) continue;
                    var lp = Prop(slot, "LastPosition");
                    var init = Prop(slot, "LastPositionInitialized");
                    if (lp is Vector3D v && Convert.ToBoolean(init ?? false))
                        sb.Append($"\n    [{i,2}] {v.X:F0},{v.Y:F0},{v.Z:F0}" +
                                  $"   (feed {(v - CameraFeed.PresenceCentre).Length() / 1000.0:F0} km away)");
                }
            sb.Append("\n    READ IT THUS: slots near the FEED and slots far from it, together, " +
                      "means the array is shared across bodies and the move gate is thrashing — " +
                      "the plateau is explained and the fix is to scope this per body. All slots " +
                      "clustered near one viewer means it is not shared and the cap is elsewhere.");
            RttLog.Line(sb.ToString());
        }
        catch (Exception e) { RttLog.Line("LOD PROBE failed: " + e.Message); }
    }

    // PRELOAD THE WORLD AROUND THE FEED CAMERA (goal 10, tier 1).
    //
    // ---- FIRE-AND-FORGET WAS A CRASH WAITING FOR A NEW LOCATION ------------------------
    //
    // CONFIRMED CTD 2026-08-04 20:48, from the game's own log:
    //
    //   [SkipWaitMonitor]: Unawaited task SpaceProbeSessionComponent.PreloadInternalAsync
    //       has thrown an KeyNotFoundException
    //   KeyNotFoundException: The given key '[X:1108, Y:-1527, Z:581]' was not present
    //   [Crash Handler]: Fatal exception ... Propagating to main
    //
    // We invoke PreloadAsync and throw the Task away. When the probe is asked for a region
    // the world has no sector entry for, it faults INSIDE that task; nobody observes it; the
    // engine's unawaited-task monitor promotes it to fatal and takes the game down.
    //
    // THIS WAS ALWAYS BROKEN AND WAS HIDDEN BY A CONSTANT. The preload used to sit on the
    // orbit ANCHOR — one long-established, always-valid location — so it never asked for a
    // region that did not exist. The moment presence started following a camera flying
    // through unvisited space, it did, and the latent fault became a crash on the first
    // unmapped sector. A bug that only fires once an input starts varying is exactly the kind
    // this project keeps finding, and "fire-and-forget" in the old comment was the tell.
    //
    // Observing the task is the whole fix: a faulted Task that someone has READ the Exception
    // of is not an unobserved exception, so the monitor has nothing to promote. We genuinely
    // do not care whether a speculative preload succeeded — that is what makes discarding the
    // result correct and NOT observing it wrong.
    private static bool _observeShapeLogged;
    private static int _preloadFaults;
    private static System.Reflection.MethodInfo _awGet, _awOnCompleted, _awGetException;

    private static void ObservePreloadTask(object vrageTask, string which)
    {
        try
        {
            if (vrageTask == null) return;

            // THE FIRST ATTEMPT AT THIS WAS WRONG, and said so rather than pretending:
            // TaskObject is a Keen.VRage.Library.Threading.PooledStateMachineWrapper, NOT a
            // System.Threading.Tasks.Task, so ContinueWith never existed and the crash hazard
            // stayed live. The right seam is VRage's own awaiter:
            //
            //   TaskAwaiter GetAwaiter()                     on Task
            //   void OnCompleted(Action continuation)        on TaskAwaiter
            //   ExceptionDispatchInfo GetExceptionResult()   on TaskAwaiter  <- the observation
            //
            // GetExceptionResult is what marks the fault seen. We deliberately do NOT call
            // GetResult(), which would RETHROW on our thread and turn a harmless miss into a
            // real exception at a worse place.
            _awGet ??= vrageTask.GetType().GetMethod("GetAwaiter", Any);
            var awaiter = _awGet?.Invoke(vrageTask, null);
            if (awaiter == null)
            {
                if (!_observeShapeLogged)
                {
                    _observeShapeLogged = true;
                    RttLog.Line($"PRELOAD: cannot observe the {which} task — no GetAwaiter on " +
                                $"{vrageTask.GetType().FullName}. A FAULTED PRELOAD CAN STILL CRASH THE GAME. " +
                                "Set preloadAroundCamera = 0 and serverPreload = 0 if CTDs appear near " +
                                "unvisited space. Shape miss, NOT a safe result.");
                }
                return;
            }

            var awType = awaiter.GetType();
            _awOnCompleted ??= awType.GetMethods(Any).FirstOrDefault(m =>
                m.Name == "OnCompleted" && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == typeof(Action));
            _awGetException ??= awType.GetMethod("GetExceptionResult", Any);

            if (_awOnCompleted == null || _awGetException == null)
            {
                if (!_observeShapeLogged)
                {
                    _observeShapeLogged = true;
                    RttLog.Line($"PRELOAD: cannot observe the {which} task — awaiter {awType.FullName} " +
                                $"has OnCompleted(Action)={(_awOnCompleted != null)} " +
                                $"GetExceptionResult={(_awGetException != null)}. A FAULTED PRELOAD CAN " +
                                "STILL CRASH THE GAME. Shape miss, NOT a safe result.");
                }
                return;
            }

            // The awaiter is a struct; box it ONCE and let the closure hold that same box, or
            // the continuation would read a copy that never saw the completion.
            var box = awaiter;
            Action onDone = () =>
            {
                try
                {
                    var edi = _awGetException.Invoke(box, null);   // <- the observation
                    if (edi == null) return;
                    if (_preloadFaults++ < 5)
                    {
                        var ex = edi.GetType().GetProperty("SourceException", Any)?.GetValue(edi) as Exception;
                        RttLog.Line($"PRELOAD FAULTED (observed, harmless): " +
                                    $"{ex?.GetType().Name ?? "unknown"} — {ex?.Message ?? "(no message)"}. " +
                                    "The probe was asked for a region the world has no data for, which is " +
                                    "expected when the camera flies somewhere nothing has ever visited. " +
                                    "Swallowed deliberately: unobserved, this reached the engine's " +
                                    "unawaited-task monitor and was promoted to a FATAL crash (CTD 20:48).");
                    }
                }
                catch { /* observing must never itself throw */ }
            };
            _awOnCompleted.Invoke(box, new object[] { onDone });
        }
        catch (Exception e) { RttLog.Error("observe preload task", e); }
    }

    // SpaceProbeSessionComponent.PreloadAsync(BoundingBoxD, Precision) fans the volume out
    // across every registered ISpaceProbePreloadable that overlaps it. The two registered
    // providers in this save are exactly the two systems a remote feed is missing —
    // VoxelStorageComponentBase (terrain data) and PlanetEnvironmentComponent (flora
    // sectors) — so ONE call asks for both.
    //
    // WHY THIS IS SAFE TO CALL FROM HERE, established BEFORE the first call rather than
    // after (the discipline four incidents bought today):
    //   * PreloadInternalAsync continues with TaskExtensions.ContinueDirectly — NOT
    //     ContinueOnDCS<SyncPoint>, and it never calls Scene.FinishBefore. That pair is
    //     precisely what made ManagedWorldArea.TryLoad deadlock the sim from this thread:
    //     FinishBefore plants a blocking task in the scene scheduler that only the sim pump
    //     can clear.
    //   * A reachability sweep from PreloadAreaAsync across Core.Game/Voxels/DCS/Simulation
    //     for FinishBefore|ContinueOnDCS returned one hit, and it was a FALSE POSITIVE: the
    //     path hops through IDisposable.Dispose(), which ilscan's virtual expansion links to
    //     every Dispose in the loaded set, landing in an unrelated encounter spawner.
    //     Reading VoxelStorageComponentBase's actual body settles it — box maths,
    //     PerformPinRequest on streamable voxel resources, CollectPreloaded callbacks, and
    //     no scheduler touch at all. The tool over-approximates BY DESIGN; a hit means
    //     "go read this", never "this happens".
    //
    // It also names an explicit VOLUME rather than competing for a shared "where is the
    // viewer" slot, so unlike the observer route it structurally cannot become the
    // single-slot tug-of-war that causes the LOD popping.
    //
    // Fire-and-forget: the returned Task is deliberately not awaited or stored. Preloading
    // is a HINT — if it never completes, the feed simply looks as it does today.
    private static long _lastPreloadTicks;
    private static int _preloadCount;
    private static bool _preloadShapeLogged;

    internal static void PreloadAroundCamera(object anyEntityInScene, Vector3D centre, double radius)
    {
        try
        {
            var now = Environment.TickCount64;
            if (now - _lastPreloadTicks < Math.Max(1000, FeedConfig.PreloadIntervalMs)) return;
            _lastPreloadTicks = now;

            var probe = FindSessionComponent(anyEntityInScene, "SpaceProbeSessionComponent");
            if (probe == null)
            {
                if (!_preloadShapeLogged)
                { _preloadShapeLogged = true; RttLog.Line("PRELOAD: SpaceProbeSessionComponent not reachable — preload disabled."); }
                return;
            }

            var tPrec = Type.GetType(
                "Keen.VRage.Core.Game.GameSystems.SpaceProbe.Precision, VRage.Core.Game");
            var mi = probe.GetType().GetMethods(Any).FirstOrDefault(m =>
                m.Name == "PreloadAsync" && m.GetParameters().Length == 2 &&
                m.GetParameters()[0].ParameterType.Name == "BoundingBoxD");
            if (tPrec == null || mi == null)
            {
                if (!_preloadShapeLogged)
                {
                    _preloadShapeLogged = true;
                    RttLog.Line($"PRELOAD: shape missing (Precision={(tPrec == null ? "NOT FOUND" : "ok")}, " +
                                $"PreloadAsync(BoundingBoxD,Precision)={(mi == null ? "NOT FOUND" : "ok")}). " +
                                "Overloads present: " + string.Join(" | ", probe.GetType().GetMethods(Any)
                                    .Where(m => m.Name == "PreloadAsync")
                                    .Select(m => string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)))));
                }
                return;
            }

            // BoundingBoxD(Vector3D min, Vector3D max) — a cube of side 2*radius on the
            // camera's subject centre, which is the anchor when one is set.
            var tBox = mi.GetParameters()[0].ParameterType;
            var half = new Vector3D(radius, radius, radius);
            var box = Activator.CreateInstance(tBox, centre - half, centre + half);

            var precName = FeedConfig.PreloadPrecision;
            object prec;
            try { prec = Enum.Parse(tPrec, precName, true); }
            catch { prec = Enum.Parse(tPrec, "Medium", true); }

            ObservePreloadTask(mi.Invoke(probe, new[] { box, prec }), "client");
            _preloadCount++;

            // First call and then every 20th: enough to prove liveness without flooding.
            if (_preloadCount == 1 || _preloadCount % 20 == 0)
                RttLog.Line($"PRELOAD #{_preloadCount}: asked the space probe for a {radius * 2:F0} m cube at " +
                            $"{centre.X:F0},{centre.Y:F0},{centre.Z:F0} at {prec} precision. This fans out to every " +
                            "registered preloadable overlapping the volume — terrain data AND flora sectors. " +
                            "Fire-and-forget: if it does nothing the feed just looks as it did.");
        }
        catch (Exception e)
        {
            // Log once and DISARM. A repeating exception from a world-mutating call is
            // exactly the shape that turns into a crash loop while nobody is watching.
            if (!_preloadShapeLogged)
            {
                _preloadShapeLogged = true;
                RttLog.Error("preload around camera (now disarmed for this session)", e);
            }
            _lastPreloadTicks = long.MaxValue / 2;
        }
    }

    // ── PER-SECTOR FLORA CAMERA — the client-visibility half of goal 10 ────────────────
    //
    // Diagnosed 2026-08-02 after serverPreload filled the server scene with flora nobody
    // could see. FloraSectorEntityComponent's two jobs both do:
    //
    //     var rootWT = rootTransforms[root.Root];
    //     var camWT  = new WorldTransform(CoreSystems.Settings.RenderView.CameraPosition);
    //     var rel    = (RelativeTransform)WorldTransform.GetRelativeTransform(camWT, rootWT);
    //     _octree.UpdateCamera(rel);          // or UpdateVisibility(rel.Position)
    //
    // ONE global camera, and the octree culls by distance from it (_maxCullingDistance,
    // _minDistanceToOctree, _isVisible). With the player 3,912 km away, every flora sector
    // near the feed is marked invisible — the content is there and the renderer is told to
    // hide it. Identical in shape to the clipmap corner bug, and it gets the identical
    // remedy: choose the viewer PER SECTOR.
    //
    // THE RULE IS THE CLIPMAP'S, deliberately — it has been correct in game for a day:
    //     claim a sector only when the feed camera is at least twice as close as the
    //     player AND the player is beyond ClipmapMinPlayerDistance.
    // A sector the player is anywhere near keeps his camera, so his flora cannot regress.
    //
    // We run as a POSTFIX: the engine's update completes first, then we re-point the
    // octrees we claim. Nothing is suppressed; a fault here leaves the engine's own result
    // in place. Instances we make visible are drawn in the player's view too, but they are
    // thousands of km outside his frustum — the same deal the clipmap override already
    // makes, and the same reason it costs nothing visible to him.
    private static int _floraClaims, _floraCalls;
    private static long _floraLogTicks;
    private static Type _tRelTransform;
    private static MethodInfo _miGetRelative, _miRelExplicit, _miOctUpdateCam, _miOctUpdateVis;
    private static bool _floraShapeLogged;

    private static double _floraNearestSeen = double.MaxValue;
    // The last rejection, stored as a code plus its two numbers rather than as prose. Written
    // ~11,300 times a second on the hot path (three field stores, no allocation); rendered to
    // text once per 15 s by FloraRejectText().
    private static int _floraRejectCode;              // 0 = none, 1 = not 2x closer, 2 = player too close
    private static double _floraRejectFeed, _floraRejectPlayer;

    private static string FloraRejectText() => _floraRejectCode switch
    {
        1 => $"not 2x closer (feed {_floraRejectFeed / 1000.0:F1} km, player {_floraRejectPlayer / 1000.0:F1} km)",
        2 => $"player too close ({_floraRejectPlayer / 1000.0:F1} km)",
        3 => $"outside the {ActiveConeDegrees():F0}deg residency cone",
        _ => "(none)",
    };

    // DENSITY, MEASURED RATHER THAN ARGUED. The user reports tree density unchanged after
    // the thrash fix, so the question is whether the feed's sectors CONTAIN fewer flora
    // instances (a generation/materialization gap, server-side) or contain the same and
    // render fewer (a LOD/culling gap, client-side). The octree knows: _numModelsInSector
    // is the instance count it was built with. Sampling the nearest sector to each viewer
    // answers it in one line — the same like-for-like discipline that reframed the
    // "boulders" control on 2026-08-01.
    private static FieldInfo _fNumModels;
    private static double _dNearFeed = double.MaxValue, _dNearPlayer = double.MaxValue;
    private static int _modelsNearFeed = -1, _modelsNearPlayer = -1;

    // ---- THE RESIDENCY-CONE STUDY: measure the prize before building the machine --------
    //
    // THE QUESTION. Every world-residency mechanism here is OMNIDIRECTIONAL — the flora claim
    // and the clipmap override are pure distance tests, preload is a cube, the viewer bubble
    // was a sphere — while the camera sees roughly a 70 degree frustum, about 11% of a
    // sphere's solid angle. If that ratio holds in practice, most of what we make resident can
    // never appear in the feed, and the VRAM wall this session hit twice is largely self-
    // inflicted.
    //
    // THIS COUNTS. IT DOES NOT CULL. Nothing below changes a single claim decision — it only
    // records what a cone WOULD have rejected, at three widths at once, so one run answers
    // both "is there a prize?" and "how wide can the cone be and still collect it?". A
    // measurement that changes behaviour cannot be trusted to measure the behaviour it
    // changed, and the cost of being wrong here is a day of building the wrong thing.
    //
    // THREE WIDTHS, chosen for what they mean rather than for round numbers:
    //   70 deg  — about the feed's own frustum. The theoretical ceiling on the saving, and
    //             unusable in practice: it leaves nothing for shadow casters or orbit motion.
    //  140 deg  — the realistic candidate. Double the frustum, so a turning orbit has a whole
    //             frustum's worth of margin before it looks into unloaded space.
    //  200 deg  — deliberately generous, to show how fast the prize decays with margin. If
    //             even this rejects most sectors, the idea is strong.
    //
    // Reading it: if the 140 deg column rejects a large majority, the feature is worth
    // building. If it rejects a small fraction, the omnidirectional loading was never the
    // problem and we drop the idea rather than spend a day on 1 fps.
    private static long _coneTotal, _cone70, _cone140, _cone200, _coneNoDir;

    private static void ConeStudy(Vector3D sectorWorld)
    {
        var look = CameraFeed.LookDirCache;
        var eye = CameraFeed.EyeCache;
        _coneTotal++;

        // No published direction yet (first frames, or a dormant feed). Counted separately
        // rather than silently treated as "in cone" — a study whose denominator quietly
        // includes unmeasurable samples is the blind instrument this project keeps meeting.
        if (look.LengthSquared() < 0.5) { _coneNoDir++; return; }

        var to = sectorWorld - eye;
        var len = to.Length();
        if (len < 1.0) return;                       // on top of the camera: in every cone

        // cos of the angle between the view direction and the sector.
        var c = (to.X * look.X + to.Y * look.Y + to.Z * look.Z) / len;

        // Half-angles: 35, 70 and 100 degrees. Outside means c < cos(halfAngle).
        if (c < 0.81915) _cone70++;                  // cos 35
        if (c < 0.34202) _cone140++;                 // cos 70
        if (c < -0.17365) _cone200++;                // cos 100
    }

    // THE CONE TEST ITSELF — the same arithmetic ConeStudy measures with, now deciding.
    //
    // Returns true when a world position should still be made resident for the feed. Every
    // uncertain case returns TRUE: no cone configured, no camera direction published yet, or
    // a position on top of the camera. A residency filter that fails CLOSED would strip the
    // world on a first frame or a dormant feed, and "the feature is off" must never look like
    // "the world is gone".
    //
    // Costs one subtract, one dot and one square root, against a claim path that already does
    // two Length() calls. It is not on the critical path for anything it does not reject.
    private static double _coneCos = 2.0;      // 2 = impossible to satisfy => recomputed on first use
    private static double _coneCosFor = double.NaN;

    // The cone angle actually in force, in degrees. 0 means "do not cull".
    //
    // DERIVED BY DEFAULT from the feed's own diagonal FOV plus a forgiveness margin, so it
    // follows the camera instead of being a number someone has to remember to update. An
    // explicit residencyConeDegrees overrides it, for A/B work only.
    //
    // Returns 0 while the FOV is still unknown (before the render thread has published a
    // projection). That is the safe direction: no cone at all rather than a guessed one.
    internal static double ActiveConeDegrees()
    {
        var forced = FeedConfig.ResidencyConeDegrees;
        if (forced > 0.0) return forced >= 360.0 ? 0.0 : forced;

        var diag = CameraFeed.FeedDiagonalFovDeg;
        if (diag <= 0.0) return 0.0;                       // FOV not published yet: keep everything

        var deg = diag + FeedConfig.ResidencyConeMarginDegrees;
        return deg >= 360.0 ? 0.0 : deg;
    }

    internal static bool InFeedCone(Vector3D worldPos)
    {
        var deg = ActiveConeDegrees();
        if (deg <= 0.0) return true;

        // Recompute the cosine only when the knob moves — this runs ~19,000 times a second.
        if (_coneCosFor != deg)
        {
            _coneCosFor = deg;
            _coneCos = Math.Cos(deg * 0.5 * Math.PI / 180.0);
        }

        var look = CameraFeed.LookDirCache;
        if (look.LengthSquared() < 0.5) return true;          // no direction yet: keep everything

        var to = worldPos - CameraFeed.EyeCache;
        var len = to.Length();
        if (len <= FeedConfig.ResidencyConeNearMetres) return true;   // the near shell, exempt

        return (to.X * look.X + to.Y * look.Y + to.Z * look.Z) / len >= _coneCos;
    }

    // How many claims the cone actually rejected, so the live effect is visible next to the
    // study that predicted it. Predicted and actual disagreeing is the interesting case.
    private static long _coneRejected;

    // Sectors we have adopted. Weak keys, so a destroyed octree is collected normally.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _ownedOctrees = new();
    private static readonly object Sentinel = new();

    // ---- MAKING A LIVE RADIUS CHANGE ACTUALLY APPLY -----------------------------------
    //
    // InstanceBatch._cullingDistance = Flora.RenderingDistanceMultiplier * lastLodDistance,
    // computed ONCE inside InstanceBatch.Initialize, which runs at batch ALLOCATION. Nothing
    // re-evaluates it. So changing worldFloraRadiusMult live leaves every batch already in
    // memory on its OLD distance while new allocations take the new one — a patchwork. As the
    // orbit moves and sectors churn, the same ground re-allocates and FLIPS between the two,
    // and instances near the boundary appear and vanish. That is the distant-flora flashing,
    // and it is why lowering the radius made it WORSE: a bigger gap between old and new.
    //
    // Rescaling by the ratio restores one consistent world immediately.
    //
    // ONE-SHOT AT CHANGE TIME, not lazily per update. A lazy pass cannot tell a stale batch
    // from one allocated after the change inside a stale octree, and would double-scale the
    // second kind. Doing it at the moment of the change means anything allocated afterwards
    // already carries the new multiplier and is never touched.
    //
    // REACHES ONLY OCTREES WE OWN. RenderingDistanceMultiplier is global, so the player's
    // sectors are left patchworked — hence the caller's warning that a fresh load is the
    // honest test. Fixing the feed's own sectors is what stops the flashing IN THE FEED.
    internal static int RescaleFloraCullingDistances(float oldMult, float newMult)
    {
        if (oldMult <= 0f || newMult <= 0f || Math.Abs(oldMult - newMult) < 1e-6f) return 0;
        float ratio = newMult / oldMult;
        int touched = 0;

        try
        {
            foreach (var kv in _ownedOctrees)
            {
                var oct = kv.Key;
                if (oct == null) continue;
                try
                {
                    var ot = oct.GetType();
                    _fOctBatches ??= ot.GetField("_instanceBatches", Any);
                    _fOctMaxCull ??= ot.GetField("_maxCullingDistance", Any);

                    if (_fOctMaxCull?.GetValue(oct) is float mx && mx > 0f)
                        _fOctMaxCull.SetValue(oct, mx * ratio);

                    if (_fOctBatches?.GetValue(oct) is System.Collections.IEnumerable batches)
                    {
                        foreach (var b in batches)
                        {
                            if (b == null) continue;
                            _fBatchCull ??= b.GetType().GetField("_cullingDistance", Any);
                            if (_fBatchCull?.GetValue(b) is float cd && cd > 0f)
                            {
                                _fBatchCull.SetValue(b, cd * ratio);
                                touched++;
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        return touched;
    }

    private static FieldInfo _fOctBatches, _fOctMaxCull, _fBatchCull;

    // ---- THE DISCRIMINATOR: DOES InstanceBatch.IsVisible ACTUALLY FLIP? ----------------
    //
    // Every instrument so far measures something UPSTREAM of what draws: _cameraCoords (ours
    // 138/138), octree _isVisible (0 flips in 214 samples), claim stability (0 flips in
    // 161,736). All clean, and the trees still flash. Three mechanisms are dead by direct
    // in-game test — rotation (frozen orbit still flashes), the residency cone (360 = zero
    // rejections, still flashes) and streaming (queue drained on the NVMe, still flashes).
    //
    // So this reads the LAST value in the chain: the per-batch IsVisible that decides whether
    // a batch is drawn at all. Two outcomes, and they point in completely different
    // directions, which is the point of measuring it:
    //
    //   FLIPS > 0  -> visibility genuinely oscillates. Something writes IsVisible even though
    //                 we suppress the engine's job for claimed sectors, and the next question
    //                 is who.
    //   FLIPS == 0 -> visibility is STABLE and the flicker is not a visibility decision at
    //                 all. It would then have to be in the draw itself — instance buffer
    //                 upload, batch culling inside our pass, or the impostor swap — and every
    //                 flora-residency theory (including the culling-distance patchwork) is
    //                 dead regardless of how well it explains the symptoms.
    //
    // Sampled on the claim path so it costs nothing extra to find the batches, throttled
    // per octree so a diagnostic cannot become the bottleneck (unthrottled it walked ~97k
    // batches a second).
    //
    // FORENSICS, NOT JUST A COUNTER. The first version proved the flips exist (2015 in a
    // 2-minute window) and nothing more. The decoded IL of InstanceBatch.UpdateVisibility
    // says the decision has exactly three inputs — the camera we pass, the batch's bounding
    // box (recomputed every pass, moves when instances mutate), and CullingDistance (which
    // the metre cap now edits live, up to a second late for a fresh batch). So each flip is
    // classified by which input plausibly moved:
    //
    //   toVisible      direction — a pop-IN; the rest are pop-OUTs
    //   gate           CullingDistance < octree._minDistanceToOctree at sample time (the
    //                  hard SetNonVisible gate at the top of the method)
    //   atClamp        CullingDistance sits exactly at wholeSceneFloraMaxMetres — the flip
    //                  involves a batch the cap has touched; a fresh batch drawing for the
    //                  <=1 s before the cadence clamps it lands here (self-inflicted)
    //   countChanged   the instance list changed since the last sample — streaming mutation
    //   boxMoved       the box centre moved >1 m since the last sample — same family
    //   QUIET          none of the above: nothing local changed and it still flipped, so the
    //                  INPUT changed — the camera or minDistance differed between calls,
    //                  i.e. two different viewers are evaluating the same batch. This class
    //                  is the smoking gun if it dominates.
    private sealed class BatchSeen
    {
        public bool Vis;
        public float Cull, CX, CY, CZ;
        public int Count;
    }

    // THE CHURN LAYER. IsVisible froze twice while the user watched flashing, and the IL
    // says why that is possible: the screen shows BUILT RENDER DATA (UpdateRenderData /
    // BuildRenderData snapshots) plus a streamed entity model — a batch can sit at
    // IsVisible=true forever while its on-screen presence churns through (a) batch-list
    // membership (rebuilds destroy+recreate batches), (b) instance lists emptying and
    // refilling (Count==0 early-returns the visibility update, so no flip is ever recorded),
    // (c) the entity handle appearing/disappearing. These per-octree aggregates watch all
    // three. All zero while the feed visibly flashes = the blink is below the component
    // (BuildRenderData / model residency) and needs a bootstrap counter instead.
    private sealed class OctreeSeen { public int Batches, Insts, Handles, Vis; }
    private sealed class SsSeen { public bool Loaded, Vis; }
    private static readonly Dictionary<object, SsSeen> _ssLast = new();
    private static long _ssLoadedUp, _ssLoadedDown, _ssVisFlips, _ssSamples;
    private static FieldInfo _fOctSubSectors, _fSsVis;
    private static MethodInfo _miSsIsLoaded;
    private static System.Reflection.PropertyInfo _pKvValue;
    private static readonly Dictionary<object, OctreeSeen> _octreeChurnLast = new();
    private static long _chBatchUp, _chBatchDown, _chInstUp, _chInstDown;
    private static long _chHandleUp, _chHandleDown, _chVisUp, _chVisDown;
    private static long _lastChBatchUp, _lastChBatchDown, _lastChInstUp, _lastChInstDown;
    private static long _lastChHandleUp, _lastChHandleDown, _lastChVisUp, _lastChVisDown;
    private static int _countReaderState;   // 0 untested, 1 ok, -1 FAILED (all counts -1)

    private static readonly Dictionary<object, BatchSeen> _batchVisLast = new();
    private static readonly Dictionary<object, long> _batchWalkAt = new();
    private static long _batchVisSamples, _batchVisFlips;
    private static long _flipToVis, _flipGate, _flipAtClamp, _flipCountChanged, _flipBoxMoved, _flipQuiet;
    private static FieldInfo _fBatchIsVisible, _fBatchBoxField, _fBoxMin, _fBoxMax;
    private static FieldInfo _fVecX, _fVecY, _fVecZ, _fOctMinDistForBatches, _fBatchInstList, _fBatchHandle;
    private static System.Reflection.PropertyInfo _pBufCount;
    private static FieldInfo _fBufCountField;

    private static void NoteBatchVisibility(object octree)
    {
        try
        {
            var now = Environment.TickCount64;
            // The BATCH walk stays cheap at 500 ms; the SUBSECTOR walk below runs at 20 ms
            // and is gated separately, because that is the tier that actually flickers.
            bool doSubsectors = true;
            if (_ssWalkAt.TryGetValue(octree, out var sat) && now - sat < 20) doSubsectors = false;
            else { if (_ssWalkAt.Count > 2000) _ssWalkAt.Clear(); _ssWalkAt[octree] = now; }

            if (_batchWalkAt.TryGetValue(octree, out var at) && now - at < 500)
            {
                if (doSubsectors) SampleSubSectors(octree);
                return;
            }
            if (_batchWalkAt.Count > 2000) _batchWalkAt.Clear();      // dead-octree entries
            _batchWalkAt[octree] = now;

            _fOctBatches ??= octree.GetType().GetField("_instanceBatches", Any);
            _fOctMinDistForBatches ??= octree.GetType().GetField("_minDistanceToOctree", Any);
            if (_fOctBatches?.GetValue(octree) is not System.Collections.IEnumerable batches) return;
            float minDist = _fOctMinDistForBatches?.GetValue(octree) is float md ? md : float.NaN;
            float clampV = (float)FeedConfig.WholeSceneFloraMaxMetres;

            int aggBatches = 0, aggInsts = 0, aggHandles = 0, aggVis = 0;

            foreach (var b in batches)
            {
                if (b == null) continue;
                var bt = b.GetType();
                _fBatchIsVisible ??= bt.GetField("<IsVisible>k__BackingField", Any) ?? bt.GetField("_isVisible", Any);
                _fBatchCull ??= bt.GetField("_cullingDistance", Any);
                _fBatchBoxField ??= bt.GetField("_boundingBox", Any);
                _fBatchInstList ??= bt.GetField("InstanceList", Any);
                if (_fBatchIsVisible?.GetValue(b) is not bool vis) continue;

                float cull = _fBatchCull?.GetValue(b) is float cd ? cd : float.NaN;

                // Box centre from Min/Max — resolved once, read boxed per sample.
                float cx = 0, cy = 0, cz = 0; bool haveBox = false;
                var box = _fBatchBoxField?.GetValue(b);
                if (box != null)
                {
                    var boxT = box.GetType();
                    _fBoxMin ??= boxT.GetField("Min", Any);
                    _fBoxMax ??= boxT.GetField("Max", Any);
                    var mn = _fBoxMin?.GetValue(box); var mx = _fBoxMax?.GetValue(box);
                    if (mn != null && mx != null)
                    {
                        var vt = mn.GetType();
                        _fVecX ??= vt.GetField("X", Any); _fVecY ??= vt.GetField("Y", Any); _fVecZ ??= vt.GetField("Z", Any);
                        if (_fVecX?.GetValue(mn) is float ax && _fVecX?.GetValue(mx) is float bx2 &&
                            _fVecY?.GetValue(mn) is float ay && _fVecY?.GetValue(mx) is float by &&
                            _fVecZ?.GetValue(mn) is float az && _fVecZ?.GetValue(mx) is float bz)
                        { cx = (ax + bx2) * 0.5f; cy = (ay + by) * 0.5f; cz = (az + bz) * 0.5f; haveBox = true; }
                    }
                }

                // The count reader PROVES itself or confesses. The first forensic pass
                // reported countChanged=0 and there was no way to tell "no mutations" from
                // "the reader silently returns -1 on every batch" — the same ambiguity that
                // made the swap guard lie. State is reported in the churn line.
                int count = -1;
                var buf = _fBatchInstList?.GetValue(b);
                if (buf != null)
                {
                    if (_pBufCount == null && _fBufCountField == null && _countReaderState >= 0)
                    {
                        var bufT = buf.GetType();
                        _pBufCount = bufT.GetProperty("Count", Any);
                        _fBufCountField = bufT.GetField("_count", Any) ?? bufT.GetField("Count", Any);
                        _countReaderState = (_pBufCount != null || _fBufCountField != null) ? 1 : -1;
                    }
                    if (_pBufCount?.GetValue(buf) is int n) count = n;
                    else if (_fBufCountField?.GetValue(buf) is int nf) count = nf;
                }

                bool hasHandle = _fBatchHandle == null
                    ? (_fBatchHandle = b.GetType().GetField("_handle", Any)) != null && _fBatchHandle.GetValue(b) != null
                    : _fBatchHandle.GetValue(b) != null;   // Nullable<T> boxes to null when empty

                aggBatches++;
                if (count > 0) aggInsts += count;
                if (hasHandle) aggHandles++;
                if (vis) aggVis++;

                _batchVisSamples++;
                if (_batchVisLast.TryGetValue(b, out var was))
                {
                    if (was.Vis != vis)
                    {
                        _batchVisFlips++;
                        if (vis) _flipToVis++;
                        bool gate = !float.IsNaN(minDist) && !float.IsNaN(cull) && cull < minDist;
                        bool clamped = clampV > 0 && !float.IsNaN(cull) && Math.Abs(cull - clampV) < 0.5f;
                        bool mutated = count >= 0 && was.Count >= 0 && count != was.Count;
                        float dx = cx - was.CX, dy = cy - was.CY, dz = cz - was.CZ;
                        bool moved = haveBox && (dx * dx + dy * dy + dz * dz) > 1.0f;
                        if (gate) _flipGate++;
                        if (clamped) _flipAtClamp++;
                        if (mutated) _flipCountChanged++;
                        if (moved) _flipBoxMoved++;
                        if (!gate && !clamped && !mutated && !moved) _flipQuiet++;
                    }
                    was.Vis = vis; was.Cull = cull; was.CX = cx; was.CY = cy; was.CZ = cz; was.Count = count;
                }
                else if (_batchVisLast.Count < 4000)
                {
                    _batchVisLast[b] = new BatchSeen { Vis = vis, Cull = cull, CX = cx, CY = cy, CZ = cz, Count = count };
                }
            }

            // THE SUB-SECTOR MESHES — THE DISTANT TIER, and the only thing in the feed the
            // user has ever seen flicker. Sampled on a 20 ms per-octree throttle, NOT the
            // 500 ms one above.
            //
            // WHY THE RATE MATTERS AND WHY THE OLD ZERO WAS WORTHLESS. The earlier version
            // shared the 500 ms throttle and reported "0 flips", which I offered as evidence
            // that visibility was stable. The user describes flicker with SUB-SECOND gaps in
            // batches: a flag that toggles and returns between two samples 500 ms apart reads
            // as zero. That was aliasing presented as proof — on the exact object class the
            // symptom is confined to. 20 ms is ~50 Hz, comfortably above Nyquist for anything
            // the eye reads as flicker.
            //
            // WHAT THE GATE ACTUALLY IS (IL of FloraSubSectorMesh.UpdateVisibility, line 1):
            //     if (Settings.Raytracing.FloraMaxDistance * 1.2f < minDistanceToOctree)
            //         { SetNonVisible(); return; }
            // A RAYTRACING distance gates the DISTANT FLORA TIER — and nothing else in the
            // scene consults it. That is the only discriminator found all session that
            // applies to this tier and to nothing else. So the margin is sampled too: if
            // minDistanceToOctree straddles that threshold, sectors flip in and out as the
            // two views' updates interleave, which is the symptom exactly.
            SampleSubSectors(octree);

            // Per-octree deltas since the last walk — the three churn channels themselves.
            if (_octreeChurnLast.TryGetValue(octree, out var seen))
            {
                int db = aggBatches - seen.Batches, di = aggInsts - seen.Insts;
                int dh = aggHandles - seen.Handles, dv = aggVis - seen.Vis;
                if (db > 0) _chBatchUp += db; else _chBatchDown -= db;
                if (di > 0) _chInstUp += di; else _chInstDown -= di;
                if (dh > 0) _chHandleUp += dh; else _chHandleDown -= dh;
                if (dv > 0) _chVisUp += dv; else _chVisDown -= dv;
                seen.Batches = aggBatches; seen.Insts = aggInsts;
                seen.Handles = aggHandles; seen.Vis = aggVis;
            }
            else if (_octreeChurnLast.Count < 1000)
            {
                _octreeChurnLast[octree] = new OctreeSeen
                    { Batches = aggBatches, Insts = aggInsts, Handles = aggHandles, Vis = aggVis };
            }
        }
        catch { }
    }

    // The bootstrap's execution-census counters, read via reflection like every bridge
    // field. Absent on an older bootstrap -> "census not on this bootstrap" rather than
    // zeros, because a zero from a missing instrument is the lie this project keeps
    // catching itself telling.
    private static FieldInfo _fCensusBuild, _fCensusUpdate, _fCensusSs, _fCensusUpload;
    private static FieldInfo _fCensusImpostor, _fCensusDistUpd;
    private static bool _censusLooked;
    private static long _lastCensusBuild, _lastCensusUpdate, _lastCensusSs, _lastCensusUpload, _censusStamp;
    private static long _lastCensusImpostor, _lastCensusDistUpd;

    private static string CensusText()
    {
        if (!_censusLooked)
        {
            _censusLooked = true;
            var bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
            _fCensusBuild  = bridge?.GetField("FloraBuildRenderDataCalls");
            _fCensusUpdate = bridge?.GetField("FloraUpdateRenderDataCalls");
            _fCensusSs     = bridge?.GetField("FloraSsUpdateCalls");
            _fCensusUpload = bridge?.GetField("FloraUploadGpuCalls");
            _fCensusImpostor = bridge?.GetField("ImpostorTagCalls");
            _fCensusDistUpd = bridge?.GetField("DistanceToCameraUpdateCalls");
        }
        if (_fCensusBuild == null) return "EXECUTION CENSUS not on this bootstrap (restart to adopt)";

        var now = Environment.TickCount64;
        double secs = _censusStamp == 0 ? 0 : (now - _censusStamp) / 1000.0;
        _censusStamp = now;

        long b = (long)_fCensusBuild.GetValue(null);
        long u = (long)(_fCensusUpdate?.GetValue(null) ?? 0L);
        long s = (long)(_fCensusSs?.GetValue(null) ?? 0L);
        long g = (long)(_fCensusUpload?.GetValue(null) ?? 0L);
        var db = b - _lastCensusBuild; _lastCensusBuild = b;
        var du = u - _lastCensusUpdate; _lastCensusUpdate = u;
        var ds = s - _lastCensusSs; _lastCensusSs = s;
        var dg = g - _lastCensusUpload; _lastCensusUpload = g;
        if (secs <= 0) return $"EXECUTION CENSUS armed (cumulative: build={b} updateRd={u} ssUpdate={s} uploadGpu={g})";

        long im = (long)(_fCensusImpostor?.GetValue(null) ?? 0L);
        long dd = (long)(_fCensusDistUpd?.GetValue(null) ?? 0L);
        var dim = im - _lastCensusImpostor; _lastCensusImpostor = im;
        var ddd = dd - _lastCensusDistUpd; _lastCensusDistUpd = dd;

        return $"EXECUTION CENSUS: BuildRenderData {db / secs:0.0}/s, batch UpdateRenderData {du / secs:0.0}/s, " +
               $"subsector Update {ds / secs:0.0}/s, UploadEntitiesOnGpu {dg / secs:0.0}/s, " +
               $"impostorTag {dim / secs:0.0}/s, distToCamera {ddd / secs:0.0}/s" +
               (_fCensusUpload == null ? " (uploadGpu NOT COUNTED — older bootstrap)" : "") +
               (dg > 0 && db == 0 && du == 0 && ds == 0
                   ? " — THE UPLOAD IS THE ONLY LIVE WRITER: per-entity GPU instance state is being " +
                     "rewritten continuously against otherwise-static flora. The blink is in WHOSE " +
                     "frustum each upload serves; next question is its view scoping."
                   : (db == 0 && du == 0 && ds == 0 && dg == 0
                       ? " — ZERO EXECUTIONS anywhere, including the upload: the GPU-side content is not " +
                         "being rewritten by this component at all."
                       : ""));
    }

    // THE DISTANT-TIER MONITOR. Runs at ~50 Hz per octree so a sub-second toggle cannot
    // alias to zero, and reads the gate's OWN terms so a flip can be attributed rather
    // than merely counted.
    private static void SampleSubSectors(object octree)
    {
        try
        {
            _fOctSubSectors ??= octree.GetType().GetField("_subSectors", Any);
            _fOctMinDist2 ??= octree.GetType().GetField("_minDistanceToOctree", Any);
            if (_fOctSubSectors?.GetValue(octree) is not System.Collections.IEnumerable subs) return;

            float minDist = _fOctMinDist2?.GetValue(octree) is float md ? md : float.NaN;
            float gate = FloraMaxDistanceGate();          // Raytracing.FloraMaxDistance * 1.2f

            foreach (var kvp in subs)
            {
                if (kvp == null) continue;
                _pKvValue ??= kvp.GetType().GetProperty("Value", Any);
                var mesh = _pKvValue?.GetValue(kvp);
                if (mesh == null) continue;
                var mt = mesh.GetType();
                _miSsIsLoaded ??= mt.GetMethod("IsLoaded", Any);
                _fSsVis ??= mt.GetField("_isVisible", Any);
                bool loaded = _miSsIsLoaded?.Invoke(mesh, null) is true;
                bool svis = _fSsVis?.GetValue(mesh) is true;
                _ssSamples++;

                if (_ssLast.TryGetValue(mesh, out var ss))
                {
                    if (ss.Loaded != loaded) { if (loaded) _ssLoadedUp++; else _ssLoadedDown++; }
                    if (ss.Vis != svis)
                    {
                        _ssVisFlips++;
                        // ATTRIBUTION AT THE MOMENT OF THE FLIP. If the octree's distance sits
                        // near the RT gate when visibility changes, the gate is the cause; if
                        // flips happen with the distance far inside the gate, something else is.
                        if (!float.IsNaN(minDist) && gate > 0f)
                        {
                            if (minDist > gate) _ssFlipBeyondGate++;
                            else if (minDist > gate * 0.8f) _ssFlipNearGate++;
                            else _ssFlipInsideGate++;

                            // WHOSE CAMERA MEASURED IT — the pause observation's prediction,
                            // made falsifiable. minDistanceToOctree is a DISTANCE, so its
                            // MAGNITUDE names the viewer without ambiguity: hundreds of metres
                            // is our orbit camera, millions is the player 3906 km away. A flip
                            // where the distance jumps between those two scales IS the two
                            // viewers alternating, which is the whole hypothesis.
                            if (minDist > 100000f) _ssFlipPlayerScale++;
                            else _ssFlipFeedScale++;
                            if (minDist > _ssFlipMaxDist) _ssFlipMaxDist = minDist;
                            if (minDist < _ssFlipMinDist) _ssFlipMinDist = minDist;
                        }
                    }
                    ss.Loaded = loaded; ss.Vis = svis;
                }
                else if (_ssLast.Count < 6000)
                {
                    _ssLast[mesh] = new SsSeen { Loaded = loaded, Vis = svis };
                }
            }
        }
        catch { }
    }

    private static FieldInfo _fRtFloraMax, _fOctMinDist2;
    private static object _rtSettingsBox;
    private static int _rtGateState;      // 0 untried, 1 ok, -1 unreadable

    private static float FloraMaxDistanceGate()
    {
        if (_rtGateState == -1) return -1f;
        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var settings = core?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var rt = settings?.GetType().GetProperty("Raytracing", Any)?.GetValue(settings)
                  ?? settings?.GetType().GetField("Raytracing", Any)?.GetValue(settings);
            if (rt == null) { _rtGateState = -1; return -1f; }
            _fRtFloraMax ??= rt.GetType().GetField("FloraMaxDistance", Any);
            if (_fRtFloraMax?.GetValue(rt) is float f) { _rtGateState = 1; _rtFloraMaxSeen = f; return f * 1.2f; }
            _rtGateState = -1; return -1f;
        }
        catch { _rtGateState = -1; return -1f; }
    }

    private static float _rtFloraMaxSeen = float.NaN;
    private static long _ssFlipBeyondGate, _ssFlipNearGate, _ssFlipInsideGate;
    private static long _ssFlipPlayerScale, _ssFlipFeedScale;
    private static float _ssFlipMaxDist = 0f, _ssFlipMinDist = float.MaxValue;
    private static long _lastSsFlips, _lastSsSamples;
    private static readonly Dictionary<object, long> _ssWalkAt = new();

    internal static string DistantTierText()
    {
        var df = _ssVisFlips - _lastSsFlips; _lastSsFlips = _ssVisFlips;
        var dsamp = _ssSamples - _lastSsSamples; _lastSsSamples = _ssSamples;
        if (dsamp == 0) return "DISTANT TIER: no subsector samples this window";
        // The LIVE value, and whether it is what we asked for. A flip count is only
        // attributable to the boundary fix when the boundary is actually installed —
        // iteration 1 read 0 flips while this said 250, i.e. the lever was NOT applied and
        // the zero proved nothing.
        var want = FeedConfig.FeedFloraMaxDistance;
        var applied = want <= 0 || Math.Abs(_rtFloraMaxSeen - want) < 0.5f;
        var gateTxt = _rtGateState == 1
            ? $"RT FloraMaxDistance={_rtFloraMaxSeen:0} (gate={_rtFloraMaxSeen * 1.2f:0} m)" +
              (applied ? "" : $" *** NOT THE REQUESTED {want:0} — lever not applied, any flip count here is UNATTRIBUTABLE ***")
            : "RT FloraMaxDistance UNREADABLE";
        var whose = (_ssFlipPlayerScale + _ssFlipFeedScale) == 0
            ? ""
            : $" WHOSE CAMERA at flip time — feed-scale {_ssFlipFeedScale}, PLAYER-scale {_ssFlipPlayerScale} " +
              $"(minDist range {_ssFlipMinDist:0}..{_ssFlipMaxDist:0} m)." +
              (_ssFlipPlayerScale > 0
                  ? " PLAYER-SCALE FLIPS PRESENT: the job measured these sectors from a camera ~3900 km away, " +
                    "so the RT gate culled them — this IS the two-viewer alternation."
                  : " All flips measured from the FEED — the alternation hypothesis is WRONG and the trigger " +
                    "is something else on this tier.");
        return $"DISTANT TIER (50 Hz): {df} visibility FLIP(s) in {dsamp} sample(s) over " +
               $"{_ssLast.Count} subsector mesh(es); flips by position — beyond gate {_ssFlipBeyondGate}, " +
               $"near gate {_ssFlipNearGate}, well inside {_ssFlipInsideGate}. {gateTxt}.{whose} " +
               (df > 0
                   ? "FLIPPING — this tier's visibility oscillates."
                   : "no flips at 50 Hz — the distant tier's visibility flag is genuinely static.");
    }

    internal static string FloraChurnText()
    {
        var bu = _chBatchUp - _lastChBatchUp; _lastChBatchUp = _chBatchUp;
        var bd = _chBatchDown - _lastChBatchDown; _lastChBatchDown = _chBatchDown;
        var iu = _chInstUp - _lastChInstUp; _lastChInstUp = _chInstUp;
        var idn = _chInstDown - _lastChInstDown; _lastChInstDown = _chInstDown;
        var hu = _chHandleUp - _lastChHandleUp; _lastChHandleUp = _chHandleUp;
        var hd = _chHandleDown - _lastChHandleDown; _lastChHandleDown = _chHandleDown;
        var vu = _chVisUp - _lastChVisUp; _lastChVisUp = _chVisUp;
        var vd = _chVisDown - _lastChVisDown; _lastChVisDown = _chVisDown;
        var reader = _countReaderState == 1 ? "count reader OK"
                   : _countReaderState == -1 ? "COUNT READER FAILED — instance numbers are lies"
                   : "count reader untested";
        bool quiet = bu + bd + iu + idn + hu + hd == 0;
        var ss = _ssSamples == 0
            ? "subsectors not seen"
            : $"SUBSECTOR MESHES: {_ssLast.Count} tracked, IsLoaded {_ssLoadedUp} up / {_ssLoadedDown} DOWN " +
              $"(cumulative), _isVisible flips {_ssVisFlips}";
        return $"CHURN/window: batches +{bu}/-{bd}, instances +{iu}/-{idn}, entityHandles +{hu}/-{hd}, " +
               $"visibleFlag +{vu}/-{vd} ({reader}). {ss}. {CensusText()}. " +
               $"{RttProbe.WholeSceneRender.CullClassifierText()}" +
               (quiet ? "" : " — component-side churn present");
    }

    internal static string BatchVisibilityText()
    {
        if (_batchVisSamples == 0) return "batch IsVisible: not sampled";
        if (_batchVisFlips == 0)
            return $"batch IsVisible: 0 flips in {_batchVisSamples} sample(s) over {_batchVisLast.Count} batch(es) — " +
                   "stable; the flicker is not a visibility decision on the sampled batches.";
        return $"batch IsVisible: {_batchVisFlips} FLIP(s) in {_batchVisSamples} sample(s) over " +
               $"{_batchVisLast.Count} batch(es) — toVisible={_flipToVis}, gate={_flipGate}, " +
               $"atClamp={_flipAtClamp}, countChanged={_flipCountChanged}, boxMoved={_flipBoxMoved}, " +
               $"QUIET={_flipQuiet}. Read the dominant class: atClamp = the metre cap's own lag " +
               "(self-inflicted, fix the clamp timing); countChanged/boxMoved = streaming mutations " +
               "re-deciding edge batches (fix = hysteresis); QUIET = the evaluation INPUT changed " +
               "between calls, i.e. two viewers are still evaluating the same batch (find the second caller).";
    }

    // ---- A HARD METRE CAP ON FLORA DRAW DISTANCE --------------------------------------
    //
    // WHY THIS IS NOT worldFloraRadiusMult. That knob is a MULTIPLIER on each model's own
    // last-LOD distance, so the resulting metres differ per model and none of them are a
    // number anyone chose. And wholeSceneLodShift is worse for this job: it biases LOD at
    // EVERY distance, so buying a cheaper horizon costs you a mushy foreground — measured
    // in game, that is exactly what shift=1 did.
    //
    // _cullingDistance is what InstanceBatch.UpdateVisibility actually tests against, so
    // clamping it is a true distance cull with no LOD term in it at all. Near flora keeps
    // its detail; far flora simply stops being drawn.
    //
    // SIZED AGAINST THE HORIZON, not by feel. From the orbit camera (~20 m up) on a 60 km
    // planet the geometric horizon is sqrt(2*60000*20) ~= 1550 m, while wholeSceneFarClip is
    // 2500 m — so a full kilometre of flora was being drawn BEYOND any visible ground. That
    // is what "distant flora loading off over the curvature" was.
    //
    // IDEMPOTENT, WHICH IS THE WHOLE DESIGN. min() applied twice is min() applied once, so
    // unlike the ratio rescale above this can run on a cadence forever and simply catch
    // batches as they are allocated. That is what makes a live cap possible at all: the
    // engine bakes _cullingDistance once, at allocation, and never revisits it.
    internal static int ClampFloraCullingDistances(float maxMetres)
    {
        if (maxMetres <= 0f) return 0;
        int clamped = 0;
        try
        {
            foreach (var kv in _ownedOctrees)
                if (kv.Key != null) clamped += ClampOctree(kv.Key, maxMetres);
        }
        catch { }
        return clamped;
    }

    private static int ClampOctree(object oct, float maxMetres)
    {
        int clamped = 0;
        try
        {
            var ot = oct.GetType();
            _fOctBatches ??= ot.GetField("_instanceBatches", Any);
            _fOctMaxCull ??= ot.GetField("_maxCullingDistance", Any);

            if (_fOctMaxCull?.GetValue(oct) is float mx && mx > maxMetres)
                _fOctMaxCull.SetValue(oct, maxMetres);

            if (_fOctBatches?.GetValue(oct) is System.Collections.IEnumerable batches)
            {
                foreach (var b in batches)
                {
                    if (b == null) continue;
                    _fBatchCull ??= b.GetType().GetField("_cullingDistance", Any);
                    if (_fBatchCull?.GetValue(b) is float cd && cd > maxMetres)
                    {
                        _fBatchCull.SetValue(b, maxMetres);
                        clamped++;
                    }
                }
            }
        }
        catch { }
        return clamped;
    }
    private static long _coneAdoptions, _coneHeldThroughRejection;

    internal static string ConeStudyText()
    {
        var t = _coneTotal;
        if (t == 0) return "no samples yet";
        string Pct(long n) => $"{100.0 * n / t:F1}%";
        var s = $"of {t} sector update(s): outside a 70deg cone {Pct(_cone70)}, " +
                $"140deg {Pct(_cone140)}, 200deg {Pct(_cone200)}" +
                (_coneNoDir > 0 ? $" [{Pct(_coneNoDir)} had no camera direction and are excluded from the case]" : "") +
                (ActiveConeDegrees() > 0
                    ? $" | LIVE at {ActiveConeDegrees():F0}deg (fov {CameraFeed.FeedDiagonalFovDeg:F0} diag + " +
                      $"{FeedConfig.ResidencyConeMarginDegrees:F0} margin): actually rejected {Pct(_coneRejected)} " +
                      $"(near shell {FeedConfig.ResidencyConeNearMetres:F0} m exempt). MEASURED 2026-08-02: live " +
                      "matches the study column for the same angle EXACTLY, so the near shell is currently exempting " +
                      "nothing — sectors are ~800 m apart, so almost none sit within 300 m of the eye AND outside " +
                      "the cone. If these two ever diverge, the shell has started doing work and is worth tuning"
                    : " | cone OFF, so nothing was rejected");
        _coneTotal = _cone70 = _cone140 = _cone200 = _coneNoDir = _coneRejected = 0;   // per-window
        // Per-window too: "held 400,000 times" only means something as a RATE. Adoptions
        // stay cumulative on purpose — that one is a population, not a rate.
        _coneHeldThroughRejection = 0;
        return s;
    }

    private static void SampleDensity(object octree, double dFeed, double dPlayer)
    {
        try
        {
            _fNumModels ??= octree.GetType().GetField("_numModelsInSector", Any);
            if (_fNumModels?.GetValue(octree) is not int n) return;
            if (dFeed < _dNearFeed) { _dNearFeed = dFeed; _modelsNearFeed = n; }
            if (dPlayer < _dNearPlayer) { _dNearPlayer = dPlayer; _modelsNearPlayer = n; }
        }
        catch { }
    }

    // CACHED REFLECTION. This path runs a quarter of a million times a second (3.8M calls
    // in 15 s, measured); a GetField per call is not affordable. Every handle below is
    // resolved once and reused. All are stable for the process.
    private static FieldInfo _fOctree, _fSphere, _fSphereCentre, _fRootDataRoot, _fWtPosition, _fRelPosition;
    private static ConstructorInfo _ctorWt;
    private static bool _floraReflectionReady;

    private static bool EnsureFloraReflection(object component, object octree, object[] args)
    {
        if (_floraReflectionReady) return true;
        try
        {
            _tWt ??= Type.GetType("Keen.VRage.Core.WorldTransform, VRage.Core");
            _tRelTransform ??= Type.GetType("Keen.VRage.Core.RelativeTransform, VRage.Core");
            if (_tWt == null || _tRelTransform == null) return false;

            _fWtPosition ??= _tWt.GetField("Position", Any);
            _fRelPosition ??= _tRelTransform.GetField("Position", Any);
            _ctorWt ??= _tWt.GetConstructor(new[] { typeof(Vector3D) });
            _miGetRelative ??= _tWt.GetMethod("GetRelativeTransform", Any);
            _miRelExplicit ??= _tRelTransform.GetMethods(Any).FirstOrDefault(
                m => m.Name == "op_Explicit" && m.GetParameters().Length == 1
                  && m.GetParameters()[0].ParameterType == _tWt);

            _fSphere ??= octree.GetType().GetField("_boundingSphere", Any);
            var sphere = _fSphere?.GetValue(octree);
            _fSphereCentre ??= sphere?.GetType().GetField("Center", Any);

            _fRootDataRoot ??= args[0]?.GetType().GetField("Root", Any);
            _miRootItem ??= args[1]?.GetType().GetMethods(Any).FirstOrDefault(
                m => m.Name == "get_Item" && m.GetParameters().Length == 1);

            var octType = octree.GetType();
            _miOctUpdateCam ??= octType.GetMethods(Any).FirstOrDefault(
                m => m.Name == "UpdateCamera" && m.GetParameters().Length == 1);
            _miOctUpdateVis ??= octType.GetMethods(Any).FirstOrDefault(
                m => m.Name == "UpdateVisibility" && m.GetParameters().Length == 1);

            _floraReflectionReady =
                _fWtPosition != null && _fRelPosition != null && _ctorWt != null &&
                _miGetRelative != null && _miRelExplicit != null && _fRootDataRoot != null &&
                _miRootItem != null && _miOctUpdateCam != null && _miOctUpdateVis != null;

            if (!_floraReflectionReady && !_floraShapeLogged)
            {
                _floraShapeLogged = true;
                RttLog.Line("FLORA CAMERA: reflection shape incomplete — override inactive, the engine's own " +
                            "player-centred update stands.");
            }
            return _floraReflectionReady;
        }
        catch { return false; }
    }

    // Returns TRUE when this sector was handled here and the engine's own update must be
    // skipped. See RttBridge.FloraCameraHook: overwriting after the fact made _cameraCoords
    // alternate between viewers every frame and the octree never settled.
    internal static bool OnFloraSectorUpdate(object component, object[] args, bool visibilityJob)
    {
        // MASTER GATE (task #40): no live feed, no flora-sector camera claims — the
        // ~6.5k/s of claim work observed while dormant stops at one field read.
        if (!Feeds.AnyLive) return false;
        if (!FeedConfig.FloraCameraOverride || component == null || args == null || args.Length < 2) return false;
        _floraCalls++;

        // HEARTBEAT OUTSIDE THE CLAIM PATH. The first armed run logged nothing at all,
        // which is ambiguous between "the postfix never fires" (no flora sector entities
        // exist client-side near the feed — the interesting answer) and "it fires and the
        // rule rejects". This line distinguishes them on a clock.
        var now0 = Environment.TickCount64;
        if (now0 - _floraLogTicks > 15000)
        {
            _floraLogTicks = now0;
            RttLog.Line($"FLORA CAMERA: {_floraCalls} update(s) seen, {_floraClaims} claimed. " +
                        $"DENSITY like-for-like — nearest sector to the FEED is {_dNearFeed:F0} m away with " +
                        $"{_modelsNearFeed} model instance(s); nearest to the PLAYER is {_dNearPlayer:F0} m away " +
                        $"with {_modelsNearPlayer}. Similar counts mean the data is there and the gap is LOD or " +
                        "culling; a much lower feed count means the sectors themselves were generated thinner. " +
                        $"Last rejection: {FloraRejectText()}. " +
                        $"CONE STUDY (counts only, culls nothing) — {ConeStudyText()}. " +
                        $"STICKY CLAIM: {_coneAdoptions} sector(s) adopted, {_coneHeldThroughRejection} update(s) " +
                        "HELD through a cone rejection because the sector was already ours. Every one of those " +
                        "was previously a frame where that sector's flora got culled at the PLAYER'S distance " +
                        "and vanished — so this number IS the popping we removed. Read it next to the octree " +
                        "camera line below: 'still ours' should now dominate 'overwritten'. " +
                        $"{ClaimStabilityText()}. {CameraCoordsText()}. {VisibilityText()}. " +
                        // BatchVisibilityText and DistantTierText dropped with their samplers.
                        // A counter whose walk no longer runs would print a frozen zero, which
                        // reads as "clean" — the single most expensive mistake of this hunt.
                        $"{FloraChurnText()}. " +
                        $"{ViewerDistance.SwapGuardText()}.");
            // Re-arm so each window reports a fresh sample rather than a session minimum.
            _dNearFeed = _dNearPlayer = double.MaxValue;
            _modelsNearFeed = _modelsNearPlayer = -1;
        }

        try
        {
            var feedPos = CameraFeed.PresenceCentre;
            if (feedPos.LengthSquared() <= 1.0) return false;

            _fOctree ??= component.GetType().GetField("_octree", Any);
            var octree = _fOctree?.GetValue(component);
            if (octree == null) return false;
            if (!EnsureFloraReflection(component, octree, args)) return false;

            // The sector's ROOT is the planet, so root position alone cannot tell sectors
            // apart. The octree's own bounding sphere is in root-relative space; its centre
            // offset from the root gives this sector's true world position.
            var root = _fRootDataRoot.GetValue(args[0]);
            if (root == null) return false;
            var rootWt = _miRootItem.Invoke(args[1], new[] { root });
            if (rootWt == null) return false;
            if (_fWtPosition.GetValue(rootWt) is not Vector3D rootPos) return false;

            var sectorWorld = rootPos;
            var centre = _fSphereCentre == null ? null : _fSphereCentre.GetValue(_fSphere.GetValue(octree));
            if (centre != null)
            {
                var ct = centre.GetType();
                double cx = 0, cy = 0, cz = 0;
                if (ct.GetField("X", Any)?.GetValue(centre) is float fx) cx = fx;
                if (ct.GetField("Y", Any)?.GetValue(centre) is float fy) cy = fy;
                if (ct.GetField("Z", Any)?.GetValue(centre) is float fz) cz = fz;
                // Rotation of a planet root is identity in practice and the offset is at
                // most a sector's extent; translation alone is accurate enough for a
                // distance comparison whose threshold is a factor of two.
                sectorWorld = rootPos + new Vector3D(cx, cy, cz);
            }

            var playerPos = PlayerRenderCameraPosition();
            if (playerPos == null) return false;

            var dPlayer = (playerPos.Value - sectorWorld).Length();
            var dFeed = (feedPos - sectorWorld).Length();
            if (dFeed < _floraNearestSeen) _floraNearestSeen = dFeed;
            // BEFORE the rejections: player-near sectors are the control half of the
            // like-for-like and they are exactly the ones the rules reject.
            SampleDensity(octree, dFeed, dPlayer);
            ConeStudy(sectorWorld);

            // REASON CODE + NUMBERS, NOT A FORMATTED STRING. These two branches reject
            // ~11,300 times a SECOND (measured: 169,488 rejections per 15 s window), and the
            // interpolated string they used to build was read by exactly one log line, once
            // per 15 s. That is ~11,300 throwaway strings a second, each with two double
            // formats, to produce four printed words. Store the raw numbers and format them
            // at the point they are actually read — see FloraRejectText().
            // THE CONE, after the study has counted this sample so the study keeps measuring
            // the FULL population rather than only what survives the cone — otherwise turning
            // the cone on would make its own prediction look like zero.
            // THE CONE IS AN ADOPTION FILTER, NOT A PER-UPDATE FILTER. This is the foliage
            // popping, and it was our own optimisation doing it.
            //
            // Returning false here does not mean "leave this sector alone" — it means the
            // ENGINE'S UpdateCameraPosition runs, and that method reads
            // Settings.RenderView.CameraPosition, a global we hold for only ~12.9% of wall
            // clock. So every cone rejection hands an already-claimed sector back to the
            // player's camera and its flora is culled at the player's distance. Measured on
            // 2026-08-02 with the feed live:
            //
            //   octree camera at DRAW time: 36 still ours, 102 OVERWRITTEN (of 138 claimed)
            //   claim stability:            0 flip(s) in 43378 samples
            //   cone:                       rejected 89.0% of sector updates
            //
            // The claim was rock stable and the octree camera still flipped on 74% of
            // sectors, because the two are not the same decision. `_cameraCoords` has
            // exactly two writers (verified by IL: the octree ctor and UpdateCamera), and
            // UpdateCamera's only caller is the method our prefix declines to suppress.
            //
            // So: the cone decides what to ADOPT. Once a sector is ours we keep answering
            // for it, and only a genuine "the player is closer" test hands it back. A sector
            // drifting across the cone edge as the camera orbits no longer flickers between
            // two viewers — which is precisely what the user sees as foliage popping in and
            // out at distance.
            bool owned = _ownedOctrees.TryGetValue(octree, out _);

            if (!InFeedCone(sectorWorld))
            {
                if (!owned) { _coneRejected++; _floraRejectCode = 3; return false; }
                // Outside the cone but ALREADY OURS. This counter is the whole point of the
                // change: every one of these used to be a sector handed back to the player's
                // camera for a frame, i.e. one flicker.
                _coneHeldThroughRejection++;
            }

            // These two ARE genuine releases: the player really is nearer, so the sector
            // should go back to being LODed for them. Drop the ownership as well as
            // declining, or we would hold a sector we have just agreed is not ours.
            if (dFeed * 2.0 >= dPlayer)
            { _ownedOctrees.Remove(octree);
              _floraRejectCode = 1; _floraRejectFeed = dFeed; _floraRejectPlayer = dPlayer;
              if (dFeed < ClaimWatchRadius) NoteClaimStability(octree, false); return false; }
            if (dPlayer < FeedConfig.ClipmapMinPlayerDistance)
            { _ownedOctrees.Remove(octree);
              _floraRejectCode = 2; _floraRejectPlayer = dPlayer;
              if (dFeed < ClaimWatchRadius) NoteClaimStability(octree, false); return false; }

            // Adopted. WEAK KEYS: octrees are created and destroyed as flora streams, and a
            // strong reference here would pin every sector the feed has ever looked at for
            // the life of the process.
            _ownedOctrees.Remove(octree);
            _ownedOctrees.Add(octree, Sentinel);
            if (!owned) _coneAdoptions++;

            // A FRESH ADOPTION CLAMPS IMMEDIATELY, not on the 1 s cadence. Without this a
            // newly streamed-in sector's batches carry their engine-baked culling distance
            // for up to a second — visible, then clamped, then culled — which is a flash WE
            // manufacture. The cadence still covers batches allocated later into an octree
            // that is already ours; this closes the big window (new octrees) for free, since
            // adoption transitions are rare and the clamp is idempotent.
            if (!owned && FeedConfig.WholeSceneFloraMaxMetres > 0)
                ClampOctree(octree, (float)FeedConfig.WholeSceneFloraMaxMetres);

            // Our camera in this sector's root frame — the same two lines the engine would
            // have run, with a different position. We then run the octree update ourselves
            // and report "handled", so the engine's player-centred call is skipped and
            // _cameraCoords stops alternating between viewers.
            var ourWt = _ctorWt.Invoke(new object[] { feedPos });
            var rel = _miRelExplicit.Invoke(null, new[] { _miGetRelative.Invoke(null, new[] { ourWt, rootWt }) });

            if (visibilityJob) _miOctUpdateVis.Invoke(octree, new[] { _fRelPosition.GetValue(rel) });
            else _miOctUpdateCam.Invoke(octree, new[] { rel });

            _floraClaims++;
            NoteClaimStability(octree, true);
            return true;
        }
        catch (Exception e)
        {
            if (!_floraShapeLogged) { _floraShapeLogged = true; RttLog.Error("flora camera (logged once)", e); }
            return false;
        }
    }

    // args[0] is the boxed RootData (DEntity Root); args[1] the ReadOnlyEntityData<WorldTransform>
    // indexer the job itself uses. Boxed by Harmony, read back the same way the job reads them.
    private static MethodInfo _miRootItem;

    private static object RootTransformOf(object[] args)
    {
        try
        {
            var root = Prop(args[0], "Root");
            if (root == null) return null;
            _miRootItem ??= args[1].GetType().GetMethods(Any).FirstOrDefault(
                m => m.Name == "get_Item" && m.GetParameters().Length == 1);
            return _miRootItem?.Invoke(args[1], new[] { root });
        }
        catch { return null; }
    }

    // The global the flora jobs read: CoreSystems.Settings.RenderView.CameraPosition.
    private static MethodInfo _miRenderView, _miCamPos;
    private static object _settingsManager;

    private static Vector3D? PlayerRenderCameraPosition()
    {
        try
        {
            if (_settingsManager == null)
            {
                var tCore = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
                _settingsManager = tCore?.GetField("Settings", Any)?.GetValue(null);
                if (_settingsManager == null) return null;
            }
            _miRenderView ??= _settingsManager.GetType().GetMethod("get_RenderView", Any);
            var view = _miRenderView?.Invoke(_settingsManager, null);
            if (view == null) return null;
            _miCamPos ??= view.GetType().GetMethod("get_CameraPosition", Any);
            return _miCamPos?.Invoke(view, null) as Vector3D?;
        }
        catch { return null; }
    }

    // SERVER-SIDE PRELOAD — the flora pipeline's suspected missing feedstock.
    //
    // The flora chain walked 2026-08-02 morning: marker tracked, sector entries firing,
    // TriggerLayer counters moving, DebugEnableUpdates true — and zero flora entities in
    // the server scene. PlanetEnvironmentServerComponent's pipeline is task-shaped
    // (GetSectorData: Task, WaitForSector, GetDependencyDataForModule): it AWAITS sector
    // data, and nothing streams that data server-side at a site no player occupies. Every
    // preload so far went through the CLIENT session's space probe; this one goes through
    // the SERVER session's — same PreloadAsync (already IL-cleared as scheduler-free:
    // ContinueDirectly, no FinishBefore), same fire-and-forget hint semantics, different
    // session's providers.
    //
    // The server session comes from the bridge's managed-area capture, resolved exactly the
    // way the census resolves it (the stashed object is the session COMPONENT; one Prop hop
    // reaches the Session, and the SpawnSyncPoint probe picks the server one).
    private static object _serverProbe;
    private static long _lastServerPreloadTicks;
    private static int _serverPreloadCount;
    private static bool _serverPreloadLogged;

    internal static void ServerPreloadAroundCamera(Vector3D centre, double radius)
    {
        try
        {
            var now = Environment.TickCount64;
            if (now - _lastServerPreloadTicks < Math.Max(1000, FeedConfig.PreloadIntervalMs)) return;
            _lastServerPreloadTicks = now;

            if (_serverProbe == null)
            {
                var bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
                var regs = bridge?.GetField("ManagedAreaRegistrations")?.GetValue(null) as IEnumerable;
                var regsLock = bridge?.GetField("ManagedAreaLock")?.GetValue(null);
                var tSync = Type.GetType(
                    "Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedWorldArea+SpawnSyncPoint, VRage.Core.Game");
                if (regs == null || regsLock == null || tSync == null) return;
                var sessions = new List<object>();
                lock (regsLock)
                {
                    foreach (var pair in regs)
                        if (pair is object[] { Length: >= 2 } p && p[1] != null)
                        {
                            var cand = p[1];
                            var real = Prop(cand, "SessionComponents") != null ? cand : Prop(cand, "Session");
                            if (real != null) sessions.Add(real);
                        }
                }
                foreach (var s in sessions)
                {
                    var scene = Prop(Prop(s, "Entity"), "Scene") ?? Prop(s, "Scene");
                    var sys = Prop(scene, "_jobSystemsIndex") as IDictionary;
                    var grp = Prop(scene, "_jobGroupToIndex") as IDictionary;
                    if (!((sys?.Contains(tSync) ?? false) || (grp?.Contains(tSync) ?? false))) continue;
                    var scEntity = Prop(s, "SessionComponents");
                    if (scEntity != null && _fComponents?.GetValue(scEntity) is IEnumerable comps)
                        foreach (var c in comps)
                            if (c != null && c.GetType().Name == "SpaceProbeSessionComponent") { _serverProbe = c; break; }
                    if (_serverProbe != null) break;
                }
                if (_serverProbe == null)
                {
                    if (!_serverPreloadLogged)
                    { _serverPreloadLogged = true; RttLog.Line("SERVER PRELOAD: no SpaceProbeSessionComponent on the spawning session — disabled."); }
                    _lastServerPreloadTicks = long.MaxValue / 2;
                    return;
                }
                RttLog.Line("SERVER PRELOAD: found the SPAWNING session's space probe — flora/terrain data " +
                            "requests now go to the side that actually generates sectors.");
            }

            var tPrec = Type.GetType("Keen.VRage.Core.Game.GameSystems.SpaceProbe.Precision, VRage.Core.Game");
            var mi = _serverProbe.GetType().GetMethods(Any).FirstOrDefault(m =>
                m.Name == "PreloadAsync" && m.GetParameters().Length == 2 &&
                m.GetParameters()[0].ParameterType.Name == "BoundingBoxD");
            if (tPrec == null || mi == null) { _lastServerPreloadTicks = long.MaxValue / 2; return; }

            var tBox = mi.GetParameters()[0].ParameterType;
            var half = new Vector3D(radius, radius, radius);
            var box = Activator.CreateInstance(tBox, centre - half, centre + half);
            object prec;
            try { prec = Enum.Parse(tPrec, FeedConfig.PreloadPrecision, true); }
            catch { prec = Enum.Parse(tPrec, "Medium", true); }

            ObservePreloadTask(mi.Invoke(_serverProbe, new[] { box, prec }), "server");
            _serverPreloadCount++;
            if (_serverPreloadCount == 1 || _serverPreloadCount % 20 == 0)
                RttLog.Line($"SERVER PRELOAD #{_serverPreloadCount}: {radius * 2:F0} m cube at " +
                            $"{centre.X:F0},{centre.Y:F0},{centre.Z:F0}, {prec} precision, via the spawning " +
                            "session's probe. If flora was starving on sector data, watch the patrol ring.");
        }
        catch (Exception e)
        {
            if (!_serverPreloadLogged)
            { _serverPreloadLogged = true; RttLog.Error("server preload (disarmed for this session)", e); }
            _lastServerPreloadTicks = long.MaxValue / 2;
        }
    }

    // THE OBSERVER REGISTRY — read-only recon for the camera-as-observer route.
    //
    // IObservers is the engine's own MULTI-viewer abstraction and the most promising route
    // to world materialization around a feed camera:
    //     Observer Add(ImmutableArray<StringId> tags, ObserverData data)
    //     ReadOnlySpan<Observer> Get(StringId tag)          <- plural BY DESIGN
    // plus helpers that ask for the CLOSEST observer to a point, which only makes sense if
    // several are expected. ObserverComponent is the supported way to register: an entity
    // carrying it calls Add on OnAddedToScene with its definition's Tags + InfluenceRadius,
    // and re-registers itself on move. ObserverComponentDefinition is just those two fields.
    //
    // WHAT THIS DUMP IS FOR: an observer is only useful if we register it under the tag the
    // consumer looks for, and tags are StringIds resolved at runtime — not readable from the
    // assemblies. So enumerate the LIVE registry: whatever the player is registered as tells
    // us exactly what to imitate.
    //
    // STRICTLY READ-ONLY. No Add, no Update. Note IObservers.CreateJobContext(Session)
    // exists, which strongly implies the registry is meant to be touched from inside DCS
    // jobs — so when the time comes to register, the safe route is attaching an
    // ObserverComponent to an entity and letting the ENGINE call Add from its own context,
    // not calling Add ourselves. Today cost four incidents to learn that distinction.
    private static void AppendObservers(StringBuilder sb, object anyEntityInScene, Vector3D player)
    {
        try
        {
            sb.AppendLine("\n--- observer registry (IObservers) ---");
            var voxObs = FindSessionComponent(anyEntityInScene, "VoxelObserverSessionComponent");
            var observers = Prop(voxObs, "_observers");
            if (observers == null)
            {
                sb.AppendLine("    IObservers not reachable" +
                              $" (VoxelObserverSessionComponent={(voxObs == null ? "not found" : "found")})");
                return;
            }
            sb.AppendLine($"    registry: {observers.GetType().FullName}");

            // The concrete registry's own fields hold the tag -> observers mapping. Dump
            // whatever collections it carries rather than guessing a member name; one
            // structural dump beats three reload-priced guesses.
            foreach (var f in observers.GetType().GetFields(Any))
            {
                object v = null; try { v = f.GetValue(observers); } catch { }
                if (v == null) { sb.AppendLine($"    {f.FieldType.Name,-34} {f.Name} = null"); continue; }
                var count = (v as System.Collections.ICollection)?.Count;
                sb.AppendLine($"    {f.FieldType.Name,-34} {f.Name}" +
                              (count.HasValue ? $"  count={count}" : $" = {v}"));

                // ENUMERATE, THEN REFLECT Key/Value. The engine's ListDictionary<K,V> is not
                // an IDictionary — its enumerator yields KeyValuePair<K,V>, and casting each
                // item to DictionaryEntry throws, which is exactly what killed the first run
                // of this dump. Reading Key/Value by reflection handles both shapes.
                if (v is System.Collections.IEnumerable pairs and not string
                    && v.GetType().Name.Contains("Dictionary", StringComparison.Ordinal))
                {
                    foreach (var item in pairs)
                    {
                        var k = Prop(item, "Key"); var val = Prop(item, "Value");
                        if (k == null && val == null) { sb.AppendLine("        " + item); continue; }
                        var entries = (val as System.Collections.ICollection)?.Count;
                        sb.AppendLine($"        tag [{k}] -> {(entries.HasValue ? entries + " observer(s)" : val?.GetType().Name)}");
                        if (val is System.Collections.IEnumerable list and not string)
                            foreach (var o in list) sb.AppendLine("            " + DescribeObserver(o, player));
                        else if (val != null)
                            sb.AppendLine("            " + DescribeObserver(val, player));
                    }
                }
                else if (v is System.Collections.IEnumerable seq and not string)
                {
                    int n = 0;
                    foreach (var o in seq)
                    {
                        if (n++ >= 20) { sb.AppendLine("        ... (truncated at 20)"); break; }
                        sb.AppendLine("        " + DescribeObserver(o, player));
                    }
                }
            }

            // The voxel side specifically: which tag it watches, and whether it is currently
            // following an observer or being force-aligned to the render camera.
            var dbg = Prop(voxObs, "_voxelDebugConfig");
            sb.AppendLine($"    voxel ObserverTag = {Prop(dbg, "ObserverTag") ?? "?"}" +
                          "   (VoxelObserverSessionComponent.UpdateObserver copies the FIRST observer " +
                          "with this tag into a single _observerEntity, and AlignToRenderCamera can " +
                          "instead force it to RenderSettings.CameraTransform — so terrain follows ONE " +
                          "point either way. Terrain likely needs its own clipmap, not just an observer.)");
        }
        catch (Exception e) { sb.AppendLine($"    observer dump FAILED ({e.GetType().Name}: {e.Message})"); }
    }

    // THE PRELOAD API — the other live route, and the only one that addresses BOTH gaps.
    //
    //     SpaceProbeSessionComponent.PreloadAsync(BoundingBoxD | OrientedBoundingBoxD | LineD,
    //                                             Precision) -> Task<...>
    //
    // It keeps a spatial tree of registered ISpaceProbePreloadable providers and fans the
    // request out to whichever overlap the volume. The two implementers are exactly the two
    // systems the feed is missing: VoxelStorageComponentBase (terrain data) and
    // PlanetEnvironmentComponent (the flora sectors). So one call over a box around the
    // camera asks for terrain AND clutter, which no other route does in a single step.
    //
    // Advantages over the observer route: it names the VOLUME explicitly rather than relying
    // on a single global "where the viewer is", so it cannot be a tug-of-war with the
    // player; and there are three Precision-keyed caches, so repeat requests over a
    // stationary orbit should be cheap.
    //
    // THREADING, given today: PreloadAsync returns a Task and the game's own admin tools and
    // debug screen call it — encouraging, but "returns a Task" is NOT proof it can be
    // *initiated* from any thread. TryLoad also returned a Task and still deadlocked the sim
    // by planting a scheduler dependency. Confirm the seat before the first call; this dump
    // is read-only reconnaissance, not a call site.
    private static void AppendSpaceProbe(StringBuilder sb, object anyEntityInScene)
    {
        try
        {
            sb.AppendLine("\n--- space probe / preload API ---");
            var probe = FindSessionComponent(anyEntityInScene, "SpaceProbeSessionComponent");
            if (probe == null) { sb.AppendLine("    SpaceProbeSessionComponent not found"); return; }

            var providers = Prop(probe, "_preloadableProviders") as System.Collections.IDictionary;
            sb.AppendLine($"    registered preloadable providers: {(providers == null ? "?" : providers.Count.ToString())}");
            if (providers != null)
            {
                // The dictionary is keyed by PreloadableToken; the PROVIDER is what we want
                // named, so look through both key and value for the ISpaceProbePreloadable.
                var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (System.Collections.DictionaryEntry kv in providers)
                {
                    string n = null;
                    foreach (var host in new[] { kv.Value, kv.Key })
                    {
                        if (host == null) continue;
                        if (host.GetType().Name.Contains("Preloadable", StringComparison.Ordinal))
                            foreach (var f2 in host.GetType().GetFields(Any))
                            {
                                object inner = null; try { inner = f2.GetValue(host); } catch { }
                                var tn = inner?.GetType().Name;
                                if (tn != null && (tn.Contains("Voxel", StringComparison.Ordinal)
                                                || tn.Contains("Planet", StringComparison.Ordinal)
                                                || tn.Contains("Environment", StringComparison.Ordinal)))
                                { n = tn; break; }
                            }
                        n ??= host.GetType().Name;
                        if (!n.Contains("Token", StringComparison.Ordinal)) break;
                    }
                    n ??= "null";
                    kinds[n] = kinds.TryGetValue(n, out var c) ? c + 1 : 1;
                }
                foreach (var k in kinds.OrderByDescending(k => k.Value))
                    sb.AppendLine($"        {k.Value,5}  {k.Key}");
                sb.AppendLine("    (VoxelStorageComponentBase = terrain data, PlanetEnvironmentComponent " +
                              "= flora sectors. Both present means one PreloadAsync over a box at the " +
                              "camera asks for terrain AND clutter in a single call.)");
            }
            foreach (var m in probe.GetType().GetMethods(Any).Where(m => m.Name == "PreloadAsync"))
                sb.AppendLine($"    PreloadAsync({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
        }
        catch (Exception e) { sb.AppendLine($"    space probe dump FAILED ({e.GetType().Name}: {e.Message})"); }
    }

    // ENVIRONMENT SECTORS — quantify the clutter gap instead of arguing about it.
    //
    // PlanetEnvironmentComponent materializes surface sectors (trees, boulders, surface ore)
    // and keeps the live state in _sectors / _pendingSectors / _entityIdsPerSector. Reading
    // those turns "the feed has no trees" from a screenshot impression into a count, and
    // shows WHERE the materialized sectors actually are — around the player, presumably,
    // and not around the camera.
    //
    // It also prints LOCAL_TAG, the static string the module uses to decide which entities
    // count as environment observers. That is the exact tag any decoy would have to carry,
    // so it is worth knowing before anyone designs one.
    //
    // STRICTLY READ-ONLY. The trigger interfaces are query-only by design (IEntityTrigger
    // exposes just a HashSetReader, ISectoredTrigger only GetSectorKey/IsValid), so there is
    // no supported way to force a sector from here and this makes no attempt to find one.
    private static void AppendEnvironmentSectors(StringBuilder sb, object anyEntityInScene, Vector3D player)
    {
        try
        {
            sb.AppendLine("\n--- planet environment sectors (trees / boulders / surface ore) ---");

            var tEnv = Type.GetType("Keen.VRage.Voxels.Components.PlanetEnvironmentComponent, VRage.Voxels");
            if (tEnv == null) { sb.AppendLine("    PlanetEnvironmentComponent type not found"); return; }
            var tagField = tEnv.GetField("LOCAL_TAG", Any);
            sb.AppendLine($"    LOCAL_TAG = \"{tagField?.GetValue(null) ?? "?"}\"   " +
                          "(the tag an entity must carry for this module to treat it as an " +
                          "environment observer — what any decoy would need)");

            // The component lives on the PLANET entity, so walk to it the same way the planet
            // survey does rather than hunting the session.
            int found = 0;
            foreach (var body in EnumeratePlanets(anyEntityInScene))
            {
                var env = ComponentOf(body.Entity, "PlanetEnvironmentComponent");
                if (env == null) continue;
                found++;

                var sectors  = Prop(env, "_sectors")        as System.Collections.ICollection;
                var pending  = Prop(env, "_pendingSectors")  as System.Collections.ICollection;
                var byId     = Prop(env, "_entitiesById")    as System.Collections.ICollection;
                sb.AppendLine($"    {body.Name}:  _sectors={sectors?.Count.ToString() ?? "?"}" +
                              $"  _pendingSectors={pending?.Count.ToString() ?? "?"}" +
                              $"  _entitiesById={byId?.Count.ToString() ?? "?"}" +
                              $"   (planet centre {(body.Position - player).Length() / 1000.0:F0} km from subject)");

                // Sector KEYS are integer grid coords; printing a few tells us whether the
                // live set clusters anywhere at all, without needing to decode the grid.
                if (Prop(env, "_sectors") is IEnumerable secs)
                {
                    int n = 0;
                    foreach (var s in secs)
                    {
                        if (n++ >= 6) break;
                        sb.AppendLine($"        sector key: {Prop(s, "Key") ?? s}");
                    }
                }
            }
            if (found == 0)
            {
                sb.AppendLine("    no PlanetEnvironmentComponent on any enumerated body — " +
                              "listing what a real planet entity DOES carry, so the next attempt is a " +
                              "lookup rather than a guess:");
                foreach (var body in EnumeratePlanets(anyEntityInScene))
                {
                    if (body.Extent < 1000) continue;          // skip boulders; want an actual planet
                    var names = new List<string>();
                    if (_fComponents?.GetValue(body.Entity) is IEnumerable cs)
                        foreach (var c in cs) names.Add(c?.GetType().Name ?? "null");
                    sb.AppendLine($"        {body.Name} (r={body.Extent:F0} m): " +
                                  string.Join(", ", names.Distinct()));

                    // THE DECOY SPECIFICATION, read from the live definition.
                    //
                    // PlanetEnvironmentComponent.OnAddedToSceneCore builds the trigger's type
                    // constraints as, for each tag type in the environment definition's
                    // ProceduralTriggerTagTypes:
                    //     MustHave(tagType)
                    //     MustNotHave<ProcedurallyGeneratedTag>()
                    //     MustNotHave<ManagedByWorldAreaTag>()
                    // So an entity trips the sector trigger exactly when it carries one of
                    // those tag types and neither exclusion. Printing the list turns "build a
                    // decoy" from a guess into a component checklist.
                    var envClient = ComponentOf(body.Entity, "PlanetEnvironmentClientComponent");
                    var def = envClient?.GetType()
                        .GetMethod("GetEnvironmentDefinition", Any)?.Invoke(envClient, null);
                    var tagTypes = Prop(def, "ProceduralTriggerTagTypes") as IEnumerable;
                    if (tagTypes == null)
                        sb.AppendLine($"        ProceduralTriggerTagTypes unreadable " +
                                      $"(client component={(envClient == null ? "absent" : "ok")}, " +
                                      $"definition={(def == null ? "null" : def.GetType().Name)})");
                    else
                    {
                        sb.AppendLine("        DECOY SPEC — an entity trips the environment sector " +
                                      "trigger iff it HAS one of these tag types and has NEITHER " +
                                      "ProcedurallyGeneratedTag NOR ManagedByWorldAreaTag:");
                        foreach (var t in tagTypes)
                        {
                            // SubclassOf<T> wraps a Type; print whichever member carries it.
                            var inner = Prop(t, "Type") ?? Prop(t, "Value") ?? t;
                            sb.AppendLine($"            {inner}");
                        }
                    }
                    break;                                      // one is enough
                }
            }
            sb.AppendLine("    READ IT THUS: a non-zero _sectors with entities means clutter IS " +
                          "materialized SOMEWHERE. The question the feed cares about is whether any " +
                          "of it is near the CAMERA, and the trigger design says it will not be — " +
                          "sectors follow tagged entities, and the camera is not one.");
        }
        catch (Exception e) { sb.AppendLine($"    environment sector dump FAILED ({e.GetType().Name}: {e.Message})"); }
    }

    private static string DescribeObserver(object o, Vector3D player)
    {
        if (o == null) return "<null>";
        var sbo = new StringBuilder(o.GetType().Name);
        foreach (var name in new[] { "Tags", "Tag", "InfluenceRadius", "Radius", "Data", "Transform", "WorldTransform" })
        {
            var v = Prop(o, name);
            if (v == null) continue;
            if (v is Vector3D p)
                sbo.Append($"  {name}={p.X:F0},{p.Y:F0},{p.Z:F0} ({(p - player).Length():F0} m from subject)");
            else if (v is System.Collections.IEnumerable en and not string)
            {
                var items = new List<string>();
                foreach (var i in en) { if (items.Count >= 8) break; items.Add(i?.ToString() ?? "null"); }
                sbo.Append($"  {name}=[{string.Join(", ", items)}]");
            }
            else sbo.Append($"  {name}={v}");
        }
        // Position often hides one level down, on the observer's data/transform struct.
        foreach (var host in new[] { Prop(o, "Data"), Prop(o, "Transform"), Prop(o, "WorldTransform") })
        {
            if (host == null) continue;
            var p2 = Prop(host, "Position") ?? Prop(host, "Translation");
            if (p2 is Vector3D pv)
            { sbo.Append($"  pos={pv.X:F0},{pv.Y:F0},{pv.Z:F0} ({(pv - player).Length():F0} m)"); break; }
        }
        return sbo.ToString();
    }

    // Any session component by type name — generalised from the managed-area hop, which
    // needed exactly this and had it inlined.
    private static object FindSessionComponent(object anyEntityInScene, string typeName)
    {
        var tBlock3 = Type.GetType(
            "Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent, Game2.Simulation");
        var grid = ComponentOf(anyEntityInScene, "CubeGridComponent")
                   ?? Prop(TryGetViaGeneric(anyEntityInScene, tBlock3), "Grid");
        var scEntity = Prop(Prop(grid, "Session"), "SessionComponents");
        if (scEntity == null || _fComponents?.GetValue(scEntity) is not IEnumerable comps) return null;
        foreach (var c in comps)
            if (c != null && c.GetType().Name == typeName) return c;
        return null;
    }

    // The session-component hop, shared by the survey section and the loader.
    private static object FindManagedWorldAreaComponent(object anyEntityInScene)
    {
        var tBlock2 = Type.GetType(
            "Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent, Game2.Simulation");
        var grid = ComponentOf(anyEntityInScene, "CubeGridComponent")
                   ?? Prop(TryGetViaGeneric(anyEntityInScene, tBlock2), "Grid");
        var session = Prop(grid, "Session");
        var scEntity = Prop(session, "SessionComponents");
        if (scEntity == null || _fComponents?.GetValue(scEntity) is not IEnumerable scComps) return null;
        object mwa = null;
        foreach (var c in scComps)
        {
            if (c == null) continue;
            var n = c.GetType().Name;
            if (n == "ManagedWorldAreaSessionComponent") return c;
            if (mwa == null && n.Contains("ManagedWorldArea", StringComparison.Ordinal)
                            && Prop(c, "_areas") != null)
                mwa = c;
        }
        return mwa;
    }

    // Planet-like bodies: entities carrying any component whose type name mentions Planet.
    // Name comes from DisplayName if any component offers one, else the entity's DebugName —
    // which for planets is usually the readable body name. Extent is the best "Radius"-ish
    // member found on any of its components (0 = unknown; the dump prints "?" rather than
    // pretending).
    private static IEnumerable<GridInfo> EnumeratePlanets(object anyEntityInScene)
    {
        var scene = SceneOf(anyEntityInScene);
        if (scene == null) yield break;
        _miEnumerate ??= scene.GetType().GetMethod("EnumerateEntities", Any, null, Type.EmptyTypes, null);
        _tCtx ??= Type.GetType("Keen.VRage.DCS.Accessors.DEntityContext, VRage.DCS");
        _tEntity ??= Type.GetType("Keen.VRage.DCS.Components.Entity, VRage.DCS");
        _miFromData ??= _tEntity?.GetMethod("TryGetFromDataEntity", Any);
        if (_miEnumerate == null || _tCtx == null || _miFromData == null) yield break;
        if (_miEnumerate.Invoke(scene, null) is not IEnumerable handles) yield break;

        foreach (var handle in handles)
        {
            GridInfo? info = null;
            try
            {
                var ctx = Activator.CreateInstance(_tCtx, scene, handle);
                var entity = _miFromData.Invoke(null, new[] { ctx });
                if (entity == null) continue;
                if (_fComponents == null)
                    _fComponents = entity.GetType().GetField("Components", Any);
                if (_fComponents?.GetValue(entity) is not IEnumerable comps) continue;

                object planetComp = null;
                string displayName = null;
                double radius = 0;
                var hasVoxel = false;
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    var tn = c.GetType().Name;
                    if (tn.Contains("Planet", StringComparison.Ordinal)) planetComp ??= c;
                    if (tn.Contains("Voxel", StringComparison.Ordinal)) hasVoxel = true;
                    displayName ??= Prop(c, "DisplayName")?.ToString();
                    if (radius <= 0)
                        foreach (var rn in new[] { "Radius", "MaxRadius", "AverageRadius", "MinRadius" })
                        {
                            var rv = Prop(c, rn);
                            if (rv != null)
                            {
                                try { radius = Convert.ToDouble(rv, CultureInfo.InvariantCulture); } catch { }
                                if (radius > 0) break;
                            }
                        }
                }
                // "Planet component or voxel body" would also catch asteroids; requiring a
                // Planet-named component keeps this to actual planetary machinery.
                if (planetComp == null || !hasVoxel) continue;

                var pos = PositionOf(planetComp, entity);
                if (pos == null) continue;

                var name = displayName;
                if (string.IsNullOrWhiteSpace(name)) name = Prop(entity, "DebugName")?.ToString();
                if (string.IsNullOrWhiteSpace(name)) name = "(unnamed body)";
                info = new GridInfo(name, pos.Value, radius, entity);
            }
            catch { }
            if (info.HasValue) yield return info.Value;
        }
    }

    // ---- the walk itself ---------------------------------------------------------------

    private static MethodInfo _miEnumerate;
    private static bool _shapeLogged;

    private static IEnumerable<GridInfo> Enumerate(object anyEntityInScene)
    {
        var scene = SceneOf(anyEntityInScene);
        if (scene == null) yield break;

        _miEnumerate ??= scene.GetType().GetMethod("EnumerateEntities", Any, null, Type.EmptyTypes, null);
        if (_miEnumerate == null)
        {
            if (!_shapeLogged)
            {
                _shapeLogged = true;
                RttLog.Line("WORLD WALK: Scene.EnumerateEntities() not found. Candidates: " +
                    string.Join(" | ", scene.GetType().GetMethods(Any)
                        .Where(m => m.Name.StartsWith("Enumerate") || m.Name.Contains("Entit"))
                        .Select(m => m.Name + "(" + m.GetParameters().Length + ")").Distinct()));
            }
            yield break;
        }

        if (_miEnumerate.Invoke(scene, null) is not IEnumerable handles) yield break;

        // ENUMERATEENTITIES YIELDS DEntity, NOT Entity.
        //
        // DEntity is a raw ECS handle (an id pair — the component dumps show "158:1"), and it
        // carries no TryGet<T>. The first version of this walk passed those handles straight
        // to TryGetComponent and got a silent 0 grids, which reads identically to "the world
        // is empty". The bridge is Entity.TryGetFromDataEntity(DEntityContext), and
        // DEntityContext has a public (Scene, DEntity) constructor.
        _tCtx ??= Type.GetType("Keen.VRage.DCS.Accessors.DEntityContext, VRage.DCS");
        _tEntity ??= Type.GetType("Keen.VRage.DCS.Components.Entity, VRage.DCS");
        _miFromData ??= _tEntity?.GetMethod("TryGetFromDataEntity", Any);
        if (_tCtx == null || _miFromData == null)
        {
            if (!_shapeLogged)
            {
                _shapeLogged = true;
                RttLog.Line($"WORLD WALK: bridge missing — DEntityContext={(_tCtx == null ? "NOT FOUND" : "ok")} " +
                            $"Entity.TryGetFromDataEntity={(_miFromData == null ? "NOT FOUND" : "ok")}. " +
                            "Without it, DEntity handles cannot be turned into Entities and the walk finds nothing.");
            }
            yield break;
        }

        // COUNT EVERY STAGE. A zero at the end is ambiguous between "no entities", "none
        // convertible" and "none are grids"; three counters make it self-diagnosing and save
        // a hot reload, which the VRAM ratchet prices at roughly a tenth of a session.
        int seen = 0, resolved = 0, grids = 0;
        _lastHadComponent = 0; _lastHadPosition = 0;

        foreach (var handle in handles)
        {
            seen++;
            GridInfo? info = null;
            // Per-entity try/catch: one malformed entity must not abort the whole inventory,
            // which is exactly what a single unguarded throw in a walk like this does.
            try
            {
                var ctx = Activator.CreateInstance(_tCtx, scene, handle);
                var entity = _miFromData.Invoke(null, new[] { ctx });
                if (entity != null) { resolved++; info = Describe(entity); }
            }
            catch { }
            if (info.HasValue) { grids++; yield return info.Value; }
        }

        RttLog.Line($"WORLD WALK: {seen} DEntity handle(s), {resolved} resolved to Entity, " +
                    $"{_lastHadComponent} carried a CubeGridComponent, {_lastHadPosition} of those " +
                    $"yielded a position, {grids} listed. The stage whose count drops to zero is the " +
                    "one that is broken.");

        // ZERO GRIDS TWICE IS AN INSTRUMENT PROBLEM, NOT A WORLD PROBLEM — there is
        // observably a base and a control seat in this save. Stop theorising about which
        // component name is right and ask the entities themselves: a census of every
        // distinct component type name across the whole scene, written once per walk that
        // finds nothing. The next name to try is then a lookup, not a guess.
        if (grids == 0 && resolved > 0)
        {
            try
            {
                var census = new Dictionary<string, int>(StringComparer.Ordinal);
                int sampled = 0;
                foreach (var handle in handles)
                {
                    if (sampled >= 3000) break;   // ample for a census; keeps the tick sane
                    object entity = null;
                    try
                    {
                        var ctx = Activator.CreateInstance(_tCtx, scene, handle);
                        entity = _miFromData.Invoke(null, new[] { ctx });
                    }
                    catch { }
                    if (entity == null) continue;
                    sampled++;
                    if (_fComponents?.GetValue(entity) is not IEnumerable comps) continue;
                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        var n = c.GetType().FullName;
                        census[n] = census.TryGetValue(n, out var k) ? k + 1 : 1;
                    }
                }
                var path = Path.Combine(RttLog.OutDir, "component-census.txt");
                File.WriteAllText(path,
                    $"Component census over {sampled} entities (walk found 0 grids):\n\n" +
                    string.Join("\n", census.OrderByDescending(kv => kv.Value)
                                            .Select(kv => $"{kv.Value,7}  {kv.Key}")));
                RttLog.Line($"WORLD WALK: census of {census.Count} distinct component types over " +
                            $"{sampled} entities -> {path}. Grep it for Grid.");
            }
            catch (Exception e) { RttLog.Error("component census", e); }
        }
    }

    private static Type _tCtx, _tEntity;
    private static MethodInfo _miFromData;

    // Split diagnostics: "had the component" and "had a position" are DIFFERENT failures,
    // and the first version of this walk conflated them — the census then showed 18
    // CubeGridComponents while the walk reported 0 grids, which is only possible when the
    // position lookup is what is failing. Never fold two failure modes into one counter.
    private static int _lastHadComponent, _lastHadPosition;

    private static GridInfo? Describe(object entity)
    {
        var grid = ComponentOf(entity, "CubeGridComponent");
        if (grid == null) return null;
        _lastHadComponent++;

        var name = Prop(grid, "DisplayName")?.ToString();
        if (string.IsNullOrWhiteSpace(name)) name = "(unnamed)";

        var pos = PositionOf(grid, entity);
        if (pos == null) return null;
        _lastHadPosition++;

        return new GridInfo(name, pos.Value, ExtentOf(grid), entity);
    }

    // Position, tried in the order most likely to be present. A grid always has a world
    // transform somewhere; which member exposes it has moved between builds, so this asks
    // rather than assuming.
    private static Vector3D? PositionOf(object grid, object entity)
    {
        // _positionComponent FIRST: the grid-survey dump shows CubeGridComponent stores its
        // WorldTransformComponent there, and the generic names below all missed it — which
        // is exactly how the first walk found 18 grids and 0 positions. The entity may also
        // carry the WorldTransformComponent directly (census: 127 of them), so both roads
        // lead to the same component type.
        foreach (var host in new[]
                 { Prop(grid, "_positionComponent"), ComponentOf(entity, "WorldTransformComponent"),
                   grid, Prop(grid, "Entity"), entity })
        {
            if (host == null) continue;
            foreach (var member in new[] { "WorldPosition", "Position", "Translation" })
            {
                var v = Prop(host, member);
                if (v is Vector3D d) return d;
            }
            // WorldTransform is typically a struct of Position + Orientation; take it apart
            // rather than expecting the host to flatten it for us.
            var wt = Prop(host, "WorldTransform") ?? Prop(host, "PositionComp") ?? Prop(host, "Transform");
            if (wt != null)
            {
                var p = Prop(wt, "Position") ?? Prop(wt, "Translation");
                if (p is Vector3D d2) return d2;
            }
        }
        return null;
    }

    private static double ExtentOf(object grid)
    {
        foreach (var member in new[] { "WorldAABB", "AABB", "WorldVolume" })
        {
            var b = Prop(grid, member);
            if (b == null) continue;
            var min = Prop(b, "Min"); var max = Prop(b, "Max");
            if (min is Vector3D a && max is Vector3D c) return (c - a).Length() * 0.5;
            var r = Prop(b, "Radius");
            if (r != null) return Convert.ToDouble(r, CultureInfo.InvariantCulture);
        }
        return 0.0;
    }

    private static object SceneOf(object entity)
    {
        if (entity == null) return null;
        var direct = Prop(entity, "Scene");
        if (direct != null) return direct;
        var grid = ComponentOf(entity, "CubeGridComponent");
        return grid == null ? null : Prop(grid, "Scene");
    }

    // BY NAME OVER THE Components ARRAY, NOT Entity.TryGet<T>.
    //
    // The first version closed TryGet<T> over CubeGridComponent and got 0 grids from 34,141
    // resolved entities — a silent all-null that reads identically to "the world has no
    // grids". TryGet's tag-keyed lookup semantics were never established for grid components
    // (the feed's own code never uses it for grids either; it reads block.Grid). Entity
    // carries a plain `ImmutableArray Components` field, and walking that asks no questions
    // about tags at all. Name-suffix matching also survives the component type moving
    // namespaces between game builds.
    private static FieldInfo _fComponents;

    private static object ComponentOf(object entity, string typeNameSuffix)
    {
        if (entity == null) return null;
        _fComponents ??= entity.GetType().GetField("Components", Any);
        if (_fComponents?.GetValue(entity) is not IEnumerable comps) return null;
        foreach (var c in comps)
            if (c != null && c.GetType().Name.EndsWith(typeNameSuffix, StringComparison.Ordinal))
                return c;
        return null;
    }

    // Entity.TryGet<T> — declared TryGet<T>(StringId tag = default), so reflection sees one
    // parameter even where C# calls it with none. Same dance as CameraFeed.WorldPositionOf.
    private static object TryGetViaGeneric(object entity, Type componentType)
    {
        if (entity == null || componentType == null) return null;
        try
        {
            var tryGet = entity.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "TryGet" && m.IsGenericMethodDefinition
                                  && m.GetParameters().Length <= 1);
            if (tryGet == null) return null;
            var closed = tryGet.MakeGenericMethod(componentType);
            var ps = closed.GetParameters();
            var args = ps.Length == 0
                ? null
                : new[] { ps[0].ParameterType.IsValueType
                            ? Activator.CreateInstance(ps[0].ParameterType) : null };
            return closed.Invoke(entity, args);
        }
        catch { return null; }
    }

    // WALKS THE HIERARCHY FOR FIELDS, deliberately. GetField on a derived type never
    // returns a BASE class's private fields — which is how `_areas` (private on
    // ManagedWorldAreaSessionComponent) read as null through its Client subclass and made
    // the correct component fail its own sanity check. Properties don't need the walk
    // (non-public inherited getters are rare here); private fields do, always.
    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        try
        {
            var p = o.GetType().GetProperty(name, Any);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o);
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, Any);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }
        catch { return null; }
    }
}
