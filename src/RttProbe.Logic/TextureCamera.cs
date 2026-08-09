using System;
using System.Reflection;
using Keen.VRage.Library.Mathematics;

namespace RttProbe;

// THE FEED CAMERA'S VOTE IN TEXTURE MIP SELECTION.
//
// THE SYMPTOM THIS EXISTS FOR (user, 2026-08-02): the feed looks flat and almost textureless
// up close — bark, rock faces, ground — while distant trees shimmer. That is the signature of
// LOW-RESOLUTION MIPS BEING RESIDENT, not of geometry LOD, which is why every LOD knob we
// turned changed nothing at all.
//
// THE TWO DISTANCE PATHS, and the reason this is a SECOND hook rather than a setting:
//
//   RenderUtilities.CalculateDistanceToCamera
//       -> StreamingTag: which streaming BUCKET an entity is in. Also the impostor swap,
//          shadow tracking and the raytracing near/far tags.
//       -> ViewerDistance.cs patches this one.
//
//   ManagedTexturePrioritizerComponent.OnCollectStandardsRoot
//       reads Settings.Streaming.EnableCollectingMaterialDistances     (measured: True)
//       reads Settings.RenderView.CameraPosition                       (always the PLAYER)
//       -> CollectStandards(ref cameraPositionRS, ...)
//       -> StandardMaterialJobContext.{StandardMaterialCollector, StandardTextureCollector},
//          both ClosestDistanceCollector — WHICH MIP IS RESIDENT.
//       -> NOTHING patched this until now.
//
// Bucket membership and mip choice are different questions, and viewerDistance only ever
// answered the first. That is very likely why it "produced no callable visual difference"
// when it first landed and got switched off as dead weight.
//
// WHY A HOOK AND NOT ScopeSetValues. ApplyPriorities is invoked from
// ManagedTexturePrioritizerComponent_GeneratedContainer::ApplyPriorities_InvocationStub —
// a DCS job stub running on the SCENE'S schedule, not inside our nested Draw. Per-pass
// settings scoping is therefore inert here, exactly as it was for the flora spawn radius.
//
// SAFE BY CONSTRUCTION. CollectStandards uses the camera position for a DISTANCE AND NOTHING
// ELSE, then feeds a CLOSEST-distance collector. The bootstrap prefix measures both cameras
// with the engine's own metric and substitutes ours ONLY on a strict improvement, so a
// texture can only ever be asked for at HIGHER resolution. Nothing the player needs can be
// demoted. See RttBridge.TextureCameraActive for the full argument.
internal static class TextureCamera
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Instance | BindingFlags.Static;

    private static Type _bridge;
    private static FieldInfo _fActive, _fDX, _fDY, _fDZ, _fOverrides, _fCalls, _fNearest;
    private static FieldInfo _fDecisions, _fAlternations, _fBaseMinX, _fBaseMaxX, _fEnterRatio;
    private static FieldInfo _fCycle, _fDropouts, _fContinuous;
    private static FieldInfo _fTierCalls, _fTierUp, _fTierDown, _fTierRev;
    private static FieldInfo _fDistStep, _fLatchHeld, _fLatchMoved, _fMinDist;

    // ---- THE SATURATION RADIUS ---------------------------------------------------------
    //
    // ApplyStandardMaterials computes, per standard texture:
    //
    //     texelRatio = (GetPixelsPerSurfaceMeterBase() / distance) / Streaming.DefaultTexelDensity
    //     priority   = MathF.Min(texelRatio, 2.0f)
    //
    // so priority SATURATES at 2.0 for every texture closer than P/(2D), and all of them then
    // tie in the one global priority-ordered streaming pool. Closer than that buys nothing:
    // the mip is already at full resolution from P/D inward.
    //
    // THIS NUMBER CAN FALSIFY THE WHOLE THEORY, which is why it is logged before anything is
    // changed. If P/2D is a few metres, almost nothing saturates and the tie-collision story
    // is wrong. If it is hundreds of metres, our orbit camera is pushing the feed's entire
    // foliage set into a single tied band.
    private static double _floorLogged = double.NaN;
    private static string _unbindableSaid;

    // A FAILED LOOKUP MUST BE LOUD. The first version returned -1 on any miss and printed
    // nothing, so a wrong type name looked identical to "there is nothing to measure" — and
    // the missing line was read as a quiet success for a whole session. Say which member
    // could not be bound, once, and keep saying it if the reason changes.
    private static double Unbindable(string what)
    {
        if (_unbindableSaid != what)
        {
            _unbindableSaid = what;
            RttLog.Line($"TEXTURE PRIORITY SATURATION: UNBINDABLE — could not reach {what}. The floor is NOT " +
                        "computed and feedTextureCameraMinDistMult does nothing this session. This is a BROKEN " +
                        "INSTRUMENT, not a clean reading.");
        }
        return -1;
    }

    private static double SaturationFloor()
    {
        try
        {
            var t = Type.GetType("Keen.VRage.Render12.SceneSystem.Components.ManagedTexturePrioritizerComponent, VRage.Render12");
            var mi = t?.GetMethod("GetPixelsPerSurfaceMeterBase", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (mi == null) return Unbindable("ManagedTexturePrioritizerComponent.GetPixelsPerSurfaceMeterBase");
            var p = Convert.ToDouble(mi.Invoke(null, null));

            // Core.CoreSystems, NOT Core.Systems.CoreSystems — SettingsManager lives in
            // Core.Systems, CoreSystems does not, and the first version of this method got
            // that backwards. It then returned -1 and printed NOTHING, so the absent line read
            // as "no measurement to make" rather than "the lookup failed". Hence Unbindable().
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var settings = core?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (settings == null) return Unbindable("CoreSystems.Settings");
            var streaming = settings.GetType().GetProperty("Streaming", Any)?.GetValue(settings);
            if (streaming == null) return Unbindable("SettingsManager.Streaming");
            var dField = streaming.GetType().GetField("DefaultTexelDensity", Any);
            if (dField == null) return Unbindable("StreamingSettings.DefaultTexelDensity");
            var d = Convert.ToDouble(dField.GetValue(streaming));
            if (d <= 0 || p <= 0) return Unbindable($"nonsense values P={p} D={d}");

            var floor = p / (2.0 * d);
            if (double.IsNaN(_floorLogged) || Math.Abs(floor - _floorLogged) > 0.5)
            {
                _floorLogged = floor;
                RttLog.Line($"TEXTURE PRIORITY SATURATION: pixelsPerSurfaceMeterBase={p:F1}, DefaultTexelDensity={d:F0}. " +
                            $"Priority saturates at 2.0 for anything presented closer than {floor:F1} m, and full resolution " +
                            $"is already reached at {p / d:F1} m. Presenting closer than {floor:F1} m therefore CANNOT improve " +
                            "the image — it only collapses our textures into one tied priority band, where which of them " +
                            "survives a full pool is decided by tie-breaking and can change every cycle. That is the flashing.");
            }
            return floor;
        }
        catch { return -1; }
    }
    private static bool _looked, _warned;
    private static bool _active;

    private static void Look()
    {
        if (_looked) return;
        _looked = true;
        _bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
        // PREFER THE ARMED SEAM. On the new bootstrap, "TextureCameraArmed" is copied to
        // Active only at PrepareStandardMaterials — the cycle boundary — because flipping
        // Active MID-CYCLE raced the mip prioritiser's per-cycle helper state and crashed
        // 2 of 3 activations (IndexOutOfRange in CollectStandardMaterials, 173 ms after
        // FEED GATE: ACTIVE). On an older bootstrap the Armed field does not exist and we
        // fall back to writing Active directly — old behaviour, old risk, said out loud.
        _fActive = _bridge?.GetField("TextureCameraArmed");
        if (_fActive == null)
        {
            _fActive = _bridge?.GetField("TextureCameraActive");
            if (_fActive != null)
                RttLog.Line("TEXTURE CAMERA: this bootstrap has no cycle-aligned engage (TextureCameraArmed) — " +
                            "falling back to flipping Active directly, which can race the prioritiser's " +
                            "collection cycle AT ACTIVATION (2 of 3 activations crashed on 2026-08-04). " +
                            "RESTART to adopt the new bootstrap before re-enabling feedTextureCamera.");
        }
        _fDX = _bridge?.GetField("TextureCameraDX");
        _fDY = _bridge?.GetField("TextureCameraDY");
        _fDZ = _bridge?.GetField("TextureCameraDZ");
        _fOverrides = _bridge?.GetField("TextureCameraOverrides");
        // Absent on the bootstrap currently loaded — it was added after it. Report() says so
        // rather than printing an uninterpretable "0 overrides".
        _fCalls = _bridge?.GetField("TextureCameraCalls");
        _fNearest = _bridge?.GetField("TextureCameraNearestSeen");
        // Added 2026-08-03. Absent on any older bootstrap, and StabilityText says so rather
        // than printing a zero that would read as "perfectly stable".
        _fDecisions = _bridge?.GetField("TextureCameraDecisions");
        _fAlternations = _bridge?.GetField("TextureCameraAlternations");
        _fBaseMinX = _bridge?.GetField("TextureCameraBaseMinX");
        _fBaseMaxX = _bridge?.GetField("TextureCameraBaseMaxX");
        _fEnterRatio = _bridge?.GetField("TextureCameraEnterRatio");
        _fCycle = _bridge?.GetField("TextureCameraCycle");
        _fDropouts = _bridge?.GetField("TextureCameraDropouts");
        _fContinuous = _bridge?.GetField("TextureCameraContinuous");
        _fTierCalls = _bridge?.GetField("TierCalls");
        _fTierUp = _bridge?.GetField("TierUp");
        _fTierDown = _bridge?.GetField("TierDown");
        _fTierRev = _bridge?.GetField("TierReversals");
        _fDistStep = _bridge?.GetField("TextureCameraDistanceStep");
        _fLatchHeld = _bridge?.GetField("LatchHeld");
        _fLatchMoved = _bridge?.GetField("LatchMoved");
        // Absent until the bootstrap carrying the per-entity floor is adopted (restart). The
        // publish below is then a no-op, which is why the logic-side BACKOFF exists as well:
        // it approximates the same thing through the delta, and that hot reloads.
        _fMinDist = _bridge?.GetField("TextureCameraMinDist");
        _fRotHits = _bridge?.GetField("RotHits");
        _fRotMisses = _bridge?.GetField("RotMisses");
        _fMaxDist = _bridge?.GetField("TextureCameraMaxDist");
        _fCeilApplied = _bridge?.GetField("CeilingApplied");
        const BindingFlags NonPub = BindingFlags.NonPublic | BindingFlags.Static;
        _fSlotObj = _bridge?.GetField("TierSlotObj", NonPub);
        _fSlotRev = _bridge?.GetField("TierSlotRev", NonPub);
        _fMinCalls = _bridge?.GetField("MinCalls");
        _fMinSwings = _bridge?.GetField("MinSwings");
        _fMinSteady = _bridge?.GetField("MinSteady");
        _fTexEyeX = _bridge?.GetField("TexEyeX");
        _fTexEyeY = _bridge?.GetField("TexEyeY");
        _fTexEyeZ = _bridge?.GetField("TexEyeZ");
        _fTexEyeValid = _bridge?.GetField("TexEyeValid");

        _fTexEyeSnapshot = _bridge?.GetField("TexEye");
        var snapType = _bridge?.GetNestedType("EyeSnapshot");
        _ctorEyeSnapshot = snapType?.GetConstructor(new[] { typeof(double), typeof(double), typeof(double) });
        if (_fTexEyeSnapshot == null || _ctorEyeSnapshot == null)
            RttLog.Line("TEXTURE CAMERA: this bootstrap has no atomic EyeSnapshot — the eye is published as " +
                        "three separate doubles, which engine job threads can read HALF-UPDATED. At manual-flight " +
                        "speeds a torn read is hundreds of km wrong and has crashed the game twice " +
                        "(IndexOutOfRange in the mip prioritiser). RESTART to adopt the new bootstrap, or keep " +
                        "feedTextureCamera = 0.");
    }

    private static FieldInfo _fTexEyeX, _fTexEyeY, _fTexEyeZ, _fTexEyeValid;

    // RttBridge.TexEye (EyeSnapshot) — absent on an older bootstrap, in which case the loose
    // triple below is all we can publish and the torn-read hazard remains until a restart.
    // Resolved once, alongside the other bridge fields.
    private static FieldInfo _fTexEyeSnapshot;
    private static System.Reflection.ConstructorInfo _ctorEyeSnapshot;

    private static FieldInfo _fMinCalls, _fMinSwings, _fMinSteady;
    private static long _lastMinCalls, _lastMinSwings, _lastMinSteady;

    // ---- THE PER-MATERIAL FIGHT -------------------------------------------------------
    //
    // A SWING is one material's winning distance changing by a factor of 2 or more between
    // collection cycles — enough to cross a mip boundary, i.e. a load or an unload. That is
    // the two cameras trading a material back and forth, which is the thing every per-ENTITY
    // counter tonight was structurally unable to see.
    private static string MinFightText()
    {
        if (_fMinCalls == null) return " FIGHT: unavailable — bootstrap predates the collector hook.";
        try
        {
            var c = (long)_fMinCalls.GetValue(null);
            var sw = (long)_fMinSwings.GetValue(null);
            var st = (long)_fMinSteady.GetValue(null);
            var dc = c - _lastMinCalls; _lastMinCalls = c;
            var dsw = sw - _lastMinSwings; _lastMinSwings = sw;
            var dst = st - _lastMinSteady; _lastMinSteady = st;

            if (c == 0) return " FIGHT: the collector hook NEVER FIRED — this is no measurement, not a clean one.";
            var judged = dsw + dst;
            if (judged == 0) return $" FIGHT: {dc} collector call(s), no cross-cycle comparisons yet.";
            var pct = 100.0 * dsw / judged;
            return $" FIGHT: {dsw} of {judged} material(s) changed winning distance by >=2x between cycles ({pct:F1}%), " +
                   $"over {dc} collector call(s). " +
                   (pct < 1.0
                     ? "Materials keep their winner — the two cameras are NOT trading, so this is not the mechanism either."
                     : "*** MATERIALS ARE TRADING WINNERS between the player's nearest copy and ours. A 2x distance swing " +
                       "is a mip boundary crossed, i.e. a load/unload — this is the pop, at the granularity that matters. ***");
        }
        catch { return " FIGHT: read failed."; }
    }

    private static FieldInfo _fMaxDist, _fCeilApplied, _fSlotObj, _fSlotRev;

    // ---- NAME THE WORST OFFENDERS -----------------------------------------------------
    //
    // Eight mechanisms have been argued from aggregate counts and every one died. The counts
    // never said WHAT was moving. If these turn out not to be foliage materials at all, every
    // fix aimed at them has been aimed at the wrong population — which would explain a lot.
    private static string TopChurners()
    {
        if (_fSlotObj == null || _fSlotRev == null) return "";
        try
        {
            var objs = (WeakReference[])_fSlotObj.GetValue(null);
            var revs = (int[])_fSlotRev.GetValue(null);
            if (objs == null || revs == null) return "";

            var top = new List<(int rev, string name)>();
            for (int i = 0; i < revs.Length; i++)
            {
                if (revs[i] <= 0) continue;
                var target = objs[i]?.Target;
                if (target == null) continue;
                top.Add((revs[i], NameOf(target)));
            }
            if (top.Count == 0) return " OFFENDERS: none recorded yet.";
            top.Sort((a, b) => b.rev.CompareTo(a.rev));

            var sb = new System.Text.StringBuilder(" WORST OFFENDERS (cumulative reversals): ");
            for (int i = 0; i < Math.Min(8, top.Count); i++)
                sb.Append(i == 0 ? "" : ", ").Append(top[i].name).Append(" x").Append(top[i].rev);
            sb.Append($"  [{top.Count} distinct texture(s) reversing]");
            return sb.ToString();
        }
        catch { return " OFFENDERS: read failed."; }
    }

    // The engine's texture types carry their source path under one of several member names
    // depending on version; fall back to ToString() rather than reporting nothing.
    private static string NameOf(object o)
    {
        try
        {
            var t = o.GetType();
            foreach (var n in new[] { "DebugName", "Name", "FilePath", "Path", "FileName" })
            {
                var p = t.GetProperty(n, Any)?.GetValue(o) ?? t.GetField(n, Any)?.GetValue(o);
                if (p is string s && s.Length > 0) return Short(s);
            }
            return Short(o.ToString());
        }
        catch { return "?"; }
    }

    private static string Short(string s)
    {
        if (string.IsNullOrEmpty(s)) return "?";
        var cut = s.LastIndexOfAny(new[] { '\\', '/' });
        return cut >= 0 && cut < s.Length - 1 ? s.Substring(cut + 1) : s;
    }

    // Whether the orientation bracket is actually firing. A miss means CollectStandardsPrefix
    // skipped the substitution rather than applying a mis-rotated delta, so misses read as
    // "feature off for that entity", never as "feature wrong".
    private static FieldInfo _fRotHits, _fRotMisses;
    private static long _lastRotHits, _lastRotMisses;

    private static string RotText()
    {
        if (_fRotHits == null) return " ROTATION: unavailable — bootstrap predates the fix, delta is UNROTATED (the bug).";
        try
        {
            var h = (long)_fRotHits.GetValue(null);
            var m = (long)_fRotMisses.GetValue(null);
            var dh = h - _lastRotHits; _lastRotHits = h;
            var dm = m - _lastRotMisses; _lastRotMisses = m;
            if (dh == 0 && dm == 0) return " ROTATION: idle this window.";
            return dm == 0
                ? $" ROTATION: {dh} root(s) rotated, 0 skipped — every substitution used this entity's own frame."
                : $" ROTATION: {dh} rotated, {dm} SKIPPED (no bracket) — skipped entities got no substitution at all.";
        }
        catch { return " ROTATION: read failed."; }
    }

    // Publish the camera-to-camera offset, in WORLD space.
    //
    // The prefix adds this to the render-space camera position it was handed. That is valid
    // because render space is world space minus an origin — a pure translation — so the
    // vector BETWEEN two points is identical in both spaces. It means we never have to know
    // what the render origin is, which is the part that would otherwise rot across patches.
    //
    // A LEASE, NOT A LATCH, exactly like ViewerDistance.Publish: this is called from the
    // camera pass, so a dormant gate, a torn-down feed or a hot reload lets it lapse instead
    // of leaving the player's textures being chosen from a camera that no longer exists.
    internal static void Publish(Vector3D feedEye, Vector3D orbitCentre)
    {
        Look();
        if (_fActive == null)
        {
            if (!_warned)
            {
                _warned = true;
                RttLog.Line("FEED TEXTURE CAMERA: this bootstrap has no TextureCamera fields — RESTART THE " +
                            "GAME to adopt it. The CollectStandards prefix lives in the bootstrap too, so " +
                            "until the restart feedTextureCamera has NO EFFECT however it is set.");
            }
            return;
        }

        // THE LOAD-TIME GATE (2026-08-08, the promised cycle-safety for boot-with-1).
        // Both prioritizer-assert CTDs in this family fired inside the post-load fragile
        // window. The texture camera's vote is worthless during a loading screen anyway,
        // so it now serves the same settle the residency systems serve: stand down until
        // the world has been up — and un-stalled — for residencySettleMs. Mid-play engage
        // is untouched (the settle is long past), and the lease semantics mean this needs
        // no extra teardown path.
        if (!CameraFeed.ResidencySettled)
        {
            Stand_Down();
            return;
        }

        var player = PlayerCameraWorld();
        if (player == null)
        {
            // No player camera means no common frame of reference, so the delta would be
            // meaningless. Stand the hook down rather than publish a guess.
            Stand_Down();
            return;
        }

        // THE SATURATION FLOOR, two ways.
        //
        // EXACT (bootstrap, needs a restart): publish the floor and let the prefix clamp each
        // entity's presented distance individually. No-op on a bootstrap without the field.
        //
        // APPROXIMATE (here, hot reloadable): pull the virtual texture-camera back along the
        // centre->eye ray. Everything near the orbit centre is then presented that much
        // farther, which lifts the near set out of the priority clamp without touching the
        // bootstrap. It is a GLOBAL offset standing in for a PER-ENTITY clamp, so it is
        // coarser — entities close to the eye and entities close to the centre are not moved
        // by the same proportion — but it is testable now rather than after a world load.
        var floor = SaturationFloor();
        var eye = feedEye;
        var backoff = FeedConfig.FeedTextureCameraBackoff;
        // BOTH SIGNS (2026-08-08). The old `> 0` guard silently no-opped NEGATIVE values —
        // including the user's "known-good" -500, which had therefore never applied at all.
        // Positive = pull the virtual eye BACK along centre->eye (coarser/cheaper distant
        // tiers); negative = push it TOWARD the scene (sharper distant foliage textures),
        // clamped so it can never cross within 10 m of the centre and flip the ray.
        if (Math.Abs(backoff) > 0.01)
        {
            var away = feedEye - orbitCentre;
            var len = away.Length();
            if (len > 0.001)
            {
                var applied = Math.Max(backoff, -(len - 10.0));
                eye = feedEye + (away / len) * applied;
            }
        }

        var d = eye - player.Value;
        try
        {
            if (_fMinDist != null && floor > 0)
                _fMinDist.SetValue(null, (float)(floor * FeedConfig.FeedTextureCameraMinDistMult));
            _fMaxDist?.SetValue(null, (float)FeedConfig.FeedTextureCameraMaxDist);
            // Pushed every publish rather than on config change: this is the only place that
            // reliably runs while the hook is live, and a knob that silently fails to reach
            // the bootstrap is worse than no knob. Absent on an older bootstrap — skip, do
            // not throw, because the rest of the publish still works there.
            _fEnterRatio?.SetValue(null, (float)FeedConfig.FeedTextureCameraEnterRatio);
            _fDistStep?.SetValue(null, (float)FeedConfig.FeedTextureCameraDistanceStep);

            // THE WORLD EYE — what the prefix now actually uses. The delta below is kept only
            // for the log line and for an older bootstrap; the substitution itself is absolute
            // now, so it cannot depend on which camera the collector was handed. See
            // RttBridge.TexEyeValid.
            // SNAPSHOT FIRST — one atomic reference write the job threads can read whole.
            // Three separate doubles can be read half-updated, and at manual-flight speeds a
            // torn eye is hundreds of kilometres wrong; that produced an out-of-range mip index
            // and took the game down (see RttBridge.EyeSnapshot for both stack traces).
            if (_ctorEyeSnapshot != null)
            {
                try { _fTexEyeSnapshot?.SetValue(null, _ctorEyeSnapshot.Invoke(new object[] { eye.X, eye.Y, eye.Z })); }
                catch { }
            }

            // The loose fields stay for an OLDER BOOTSTRAP across a hot reload, which reads
            // them when the snapshot field does not exist. Written after the snapshot so a
            // new bootstrap never prefers the stale triple.
            if (_fTexEyeX != null)
            {
                _fTexEyeX.SetValue(null, eye.X);
                _fTexEyeY.SetValue(null, eye.Y);
                _fTexEyeZ.SetValue(null, eye.Z);
                _fTexEyeValid.SetValue(null, true);      // valid LAST, coordinates first
            }

            _fDX.SetValue(null, (float)d.X);
            _fDY.SetValue(null, (float)d.Y);
            _fDZ.SetValue(null, (float)d.Z);
            _fActive.SetValue(null, true);              // set LAST: the deltas are valid first
            if (!_active)
            {
                _active = true;
                RttLog.Line($"FEED TEXTURE CAMERA: ARMED. Offset {d.Length() / 1000.0:F1} km from the player. " +
                            "CollectStandards now measures every root entity against the NEARER of the two " +
                            "cameras, so content near the feed can demand higher mips. This is the path " +
                            "viewerDistance does not reach — that one sets the streaming bucket, this one " +
                            "sets which mip is resident.");
            }
        }
        catch { }
    }

    // Ordering matters on the way down as much as up: clear the flag FIRST so the prefix
    // stops reading the deltas before they go stale, then let them be.
    internal static void Stand_Down()
    {
        Look();
        if (_fActive == null || !_active) return;
        try
        {
            _fActive.SetValue(null, false);
            // Clear the world eye too, and in this order: the prefix checks TexEyeValid before
            // using the coordinates, so dropping it here means a torn-down feed can never leave
            // a stale position behind for the next entity walk to measure against.
            _fTexEyeValid?.SetValue(null, false);
            _active = false;
            RttLog.Line("FEED TEXTURE CAMERA: stood down — texture priority is the player's alone again.");
        }
        catch { }
    }

    private static Vector3D? PlayerCameraWorld()
    {
        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var settings = core?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var rv = settings?.GetType().GetProperty("RenderView", Any)?.GetValue(settings);
            if (rv?.GetType().GetProperty("CameraPosition", Any)?.GetValue(rv) is Vector3D p) return p;
        }
        catch { }
        return null;
    }

    // ---- THE REPORT ------------------------------------------------------------------
    //
    // Reports the OVERRIDE COUNT, because "armed" and "actually winning the distance test"
    // are different claims and this project has confused them before.
    //
    //   overrides climbing   the substitution is happening. If the feed still looks flat the
    //                        cause is downstream — budget, density, or the mip never being
    //                        requested — and NOT this mechanism.
    //   overrides zero       the feed camera never wins. Either the delta is wrong, or the
    //                        feed genuinely is farther from everything than the player is.
    //                        That is a different bug and must not be read as "no effect".
    private static long _reportTicks, _lastOverrides, _lastCalls;

    internal static void Report()
    {
        var now = Environment.TickCount64;
        if (now - _reportTicks < 15000) return;
        _reportTicks = now;

        Look();
        if (_fOverrides == null) return;
        try
        {
            var over = (long)_fOverrides.GetValue(null);
            var d = over - _lastOverrides;
            _lastOverrides = over;

            long calls = -1; float nearest = float.NaN;
            if (_fCalls != null) calls = (long)_fCalls.GetValue(null);
            if (_fNearest != null) nearest = (float)_fNearest.GetValue(null);
            var dCalls = calls - _lastCalls;
            _lastCalls = calls;

            string verdict;
            if (calls < 0)
                verdict = "call counter absent — RESTART to adopt the newer bootstrap; 0 overrides is " +
                          "UNINTERPRETABLE without it.";
            else if (calls == 0)
                verdict = "THE PREFIX NEVER RAN. Either the patch is not applied or the prioritizer is not " +
                          "collecting at all — this is NOT a distance-test failure.";
            else if (over == 0)
                verdict = $"the prefix RAN {dCalls} time(s) this window and our camera NEVER WON. Nearest " +
                          $"PLAYER distance ever offered = {(float.IsNaN(nearest) || nearest == float.MaxValue ? "n/a" : nearest.ToString("F0") + " m")}. " +
                          "If that is small, the collection set only covers the PLAYER'S neighbourhood and our " +
                          "entities are never offered — a BUCKET MEMBERSHIP problem (viewerDistance/StreamingTag), " +
                          "not a mip-choice one, and no camera substitution can help until they are in the set.";
            else
                verdict = $"{dCalls} call(s) this window; those entities had their texture mips chosen from the " +
                          "FEED camera because it was genuinely nearer by the engine's own metric.";

            RttLog.Line($"FEED TEXTURE CAMERA: {d} override(s) this window ({over} cumulative, {calls} call(s) cumulative). "
                        // STABILITY/alternation, the base span and the dropout counter are gone
                        // with the scaffolding that fed them. A counter whose writer no longer
                        // runs prints a frozen zero, and a frozen zero reads as "clean" — the
                        // single most expensive mistake of this hunt. ROTATION stays: it is the
                        // one that proves the FIX is live.
                        + verdict + RotText());
        }
        catch { }
    }

    // ---- SPATIAL OR TEMPORAL, AND WHOSE BASE ------------------------------------------
    //
    // THE ONE READING THAT SEPARATES A STABLE OVERRIDE FROM A FLASHING ONE. A 30% override
    // rate is harmless if it is the SAME 30% every pass — those entities simply sit nearer
    // the feed. It is the bug if the membership churns, because then an entity's demanded
    // mip alternates, the streamer loads and drops the same texture forever, and a texture
    // that is not resident is not drawn. Every previous theory was argued from the aggregate
    // count, which cannot tell these apart.
    //
    // Read alternations PER DECISION, not raw: raw scales with render rate and says nothing.
    private static long _lastDecisions, _lastAlternations;

    private static string StabilityText()
    {
        if (_fDecisions == null)
            return "STABILITY: unavailable — this bootstrap predates the alternation counter; RESTART to adopt it.";
        try
        {
            var dec = (long)_fDecisions.GetValue(null);
            var alt = (long)_fAlternations.GetValue(null);
            var dDec = dec - _lastDecisions; _lastDecisions = dec;
            var dAlt = alt - _lastAlternations; _lastAlternations = alt;

            var pct = dDec > 0 ? 100.0 * dAlt / dDec : 0.0;
            string verdict;
            if (dDec == 0)
                verdict = "no repeat sightings this window — cannot judge (entities are not being re-offered).";
            else if (pct < 0.5)
                verdict = "SPATIAL: entities keep the same decision pass to pass, so the override is NOT the " +
                          "flicker and the cause is downstream of mip choice.";
            else
                verdict = "*** TEMPORAL: the same entities are changing decision between passes. Their demanded " +
                          "mip oscillates, which is exactly a texture loading and dropping — THIS IS THE FLICKER " +
                          "MECHANISM. Fix = hysteresis: once an entity is ours, keep it ours. ***";

            // Two clusters here means the collector is sometimes handed OUR camera as the base,
            // which TextureCamera's own header says never happens. See the prefix.
            string bases = "";
            if (_fBaseMinX != null && _fBaseMaxX != null)
            {
                var lo = (float)_fBaseMinX.GetValue(null);
                var hi = (float)_fBaseMaxX.GetValue(null);
                if (lo <= hi)
                {
                    var dx = _fDX != null ? Math.Abs((float)_fDX.GetValue(null)) : 0f;
                    var span = hi - lo;
                    bases = $" BASE SPAN on X: {span:F0} m (our delta X = {dx:F0} m)"
                          + (dx > 1f && span > dx * 0.5f
                             ? " — COMPARABLE TO THE DELTA, so the collector is being handed two different cameras "
                               + "and the prefix is adding a player-to-feed offset to a base that may already BE the feed."
                             : " — one base cluster, so the handed camera is consistent.");
                }
            }

            // Print the ratio the BOOTSTRAP actually holds, not the config value: a knob that
            // failed to reach the prefix would otherwise read as a fix that did not work.
            var live = _fEnterRatio != null ? $" enterRatio(live)={(float)_fEnterRatio.GetValue(null):0.###}" : " enterRatio=UNAVAILABLE";
            return $"STABILITY: {dAlt} alternation(s) in {dDec} repeat decision(s) ({pct:F1}%).{live}{RotText()} {verdict}{bases} {DropoutText()}";
        }
        catch { return "STABILITY: read failed."; }
    }

    // ---- THE COLLECTION-SET DROPOUT RATE ----------------------------------------------
    //
    // WHAT THIS CATCHES THAT NOTHING ELSE DID. The collector is reset every cycle, so a
    // material's demanded mip is rebuilt only from the entities OFFERED that cycle. An entity
    // that is not offered does not "keep" its previous vote — it loses it, the material falls
    // back to the player's far distance, and an alpha-tested foliage texture that unloads is
    // not blurry, it is gone. Every earlier instrument compared entities that WERE offered,
    // so this failure was invisible to all of them.
    //
    // CYCLES/S IS THE SANITY CHECK, and it must be read first: if it is zero the tick never
    // fired and the dropout number is meaningless rather than good.
    private static long _lastCycle, _lastDropouts, _lastContinuous;

    private static string DropoutText()
    {
        if (_fDropouts == null || _fCycle == null)
            return "DROPOUT: unavailable — this bootstrap predates the counter.";
        try
        {
            var cyc = (long)_fCycle.GetValue(null);
            var drop = (long)_fDropouts.GetValue(null);
            var cont = (long)_fContinuous.GetValue(null);
            var dCyc = cyc - _lastCycle; _lastCycle = cyc;
            var dDrop = drop - _lastDropouts; _lastDropouts = drop;
            var dCont = cont - _lastContinuous; _lastContinuous = cont;

            if (dCyc == 0)
                return "DROPOUT: NO COLLECTION CYCLES TICKED this window — the cycle hook never fired, so the " +
                       "dropout count below is meaningless, NOT clean. Fix the hook before reading it.";

            var seen = dDrop + dCont;
            var pct = seen > 0 ? 100.0 * dDrop / seen : 0.0;
            var verdict = seen == 0
                ? "no repeat sightings to judge."
                : pct < 0.5
                    ? "entities stay in the collection set — the set is NOT churning, so the lost-vote theory is WRONG too."
                    : "*** ENTITIES ARE LEAVING AND RE-ENTERING THE COLLECTION SET. While out, their material has no " +
                      "near-distance vote from us and streams down. This is a mip that collapses and recovers, which " +
                      "on alpha-tested foliage reads as gone-and-back. ***";
            return $"DROPOUT: {dDrop} of {seen} repeat sighting(s) followed a missed cycle ({pct:F1}%), " +
                   $"over {dCyc} collection cycle(s) ({dCyc / 15.0:F1}/s). {verdict}";
        }
        catch { return "DROPOUT: read failed."; }
    }

    // ---- TIER CHURN: WHAT STREAMING ACTUALLY DID -------------------------------------
    //
    // REPORTED SEPARATELY AND UNCONDITIONALLY, unlike everything else in this file, because
    // feedTextureCamera=0 is the CONTROL arm — the one where the foliage is absent instead of
    // flashing — and Report() above never runs there. A measurement that only exists in one
    // arm cannot compare the two.
    //
    // REVERSALS ARE THE SIGNAL. Tier traffic on its own is normal and healthy: the world
    // streams in, things settle. The same texture going up, then down, then up is not
    // settling — it is a resident mip collapsing and recovering, and on alpha-tested foliage
    // that is gone-and-back rather than sharp-and-soft.
    private static long _tierReportTicks, _lastTierCalls, _lastTierUp, _lastTierDown, _lastTierRev;
    private static long _lastHeld, _lastMoved;

    internal static void ReportTierChurn()
    {
        var now = Environment.TickCount64;
        if (now - _tierReportTicks < 15000) return;
        _tierReportTicks = now;

        Look();
        if (_fTierCalls == null)
        {
            RttLog.Line("TEXTURE TIER CHURN: unavailable — this bootstrap predates the counter. RESTART to adopt it.");
            return;
        }
        try
        {
            var calls = (long)_fTierCalls.GetValue(null);
            var up = (long)_fTierUp.GetValue(null);
            var down = (long)_fTierDown.GetValue(null);
            var rev = (long)_fTierRev.GetValue(null);
            var dCalls = calls - _lastTierCalls; _lastTierCalls = calls;
            var dUp = up - _lastTierUp; _lastTierUp = up;
            var dDown = down - _lastTierDown; _lastTierDown = down;
            var dRev = rev - _lastTierRev; _lastTierRev = rev;

            string verdict;
            if (calls == 0)
                verdict = "THE HOOK NEVER FIRED — this is NOT 'streaming is stable', it is no measurement at all. " +
                          "Check the patch log for RequestUpdateTier before reading anything into these zeros.";
            else if (dCalls == 0)
                verdict = "no tier traffic at all this window — streaming is idle, so any flashing seen during it " +
                          "is NOT resident-mip churn and the cause is downstream in the draw.";
            else if (dRev == 0)
                verdict = "traffic but NO reversals — textures are settling in one direction, which is normal " +
                          "streaming. Resident-mip oscillation is EXONERATED as the flashing mechanism.";
            else
                verdict = $"*** {dRev} REVERSAL(S): textures are going up, then down, then up. That is a resident mip " +
                          "collapsing and recovering — on alpha-tested foliage, gone-and-back. This is the flashing, " +
                          "measured where it physically happens. ***";


            RttLog.Line($"TEXTURE TIER CHURN: {dCalls} tier request(s) this window ({dUp} up, {dDown} down, " +
                        $"{dRev} reversal(s)); {calls} call(s) cumulative. feedTextureCamera={(FeedConfig.FeedTextureCamera ? "ON" : "OFF — CONTROL ARM")}." +
                        // LATCH text dropped with the latch. FIGHT dropped with the MinValue
                        // hook, which never fired anyway — the method is two lines and the JIT
                        // inlines it, so Harmony never intercepted a single call.
                        " " + verdict + TopChurners());
        }
        catch { }
    }
}
