using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace RttProbe;

// ---- THE GPU-SIDE INSTRUMENT (task #65) -------------------------------------------------
//
// The engine ships a full GPU profiler: CopyCommandList.BeginBlock(tag)/EndBlock() insert
// timestamp query pairs (GPUProfiler.AddQuery) that a per-queue Resolver reads back with
// CPU/GPU clocks synced, raising GPUProfiler.OnWatchesReady when a frame's reports are
// ready. Report = { QueueType, Tag, Depth, Duration, BeginTimeS, EndTimeS }.
//
// THE FREE LUNCH, read from BeginBlock's IL: only DEPTH-1 blocks get queries — and our
// nested SceneDrawSystem.Draw runs the engine's own stage code, which opens its own
// blocks. With no outer block open at our position, the engine GPU-times OUR pass's
// stages under its own standard tags. Every frame with a feed render therefore resolves
// TWO time-clusters of the same tags: the player's pass and ours, separable by
// BeginTimeS. So v1 is a pure CONSUMER: subscribe, read, split by time order, aggregate.
// No command-list surgery, no depth games, nothing recorded that the engine does not
// already record.
//
// Gating readable at runtime: Cached.RenderConfiguration.DiscardGPUBlocks — if true the
// engine discards all block queries and this instrument says so once and stays quiet.
//
// SHAPE-FIRST DISCIPLINE: the first sessions dump the distinct tags per window before any
// clustering is trusted — the swap-guard rule (an instrument must confess when it is not
// installed) and the ilscan rule (never conclude from a truncated view) both apply.
internal static class GpuReportProbe
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;

    private static bool _tried, _dead, _subscribed;
    private static object _profiler;
    private static FieldInfo _fiReports;
    private static FieldInfo _fiTag, _fiDuration, _fiBegin, _fiQueue, _fiDepth;
    private static FieldInfo _fiBridgeHook;   // RttBridge.GpuWatchesReadyHook (bootstrap-owned subscription)

    // Aggregation window (written on the render thread in the handler, read by the 15s
    // report on the logic side; coarse locking is fine at one handler call per frame).
    private static readonly object _lock = new();
    private static readonly Dictionary<string, (double ms, int n)> _tagTotals = new();
    private static int _frames, _handlerErrs;
    private static long _lastDumpMs;
    private static bool _discardLogged, _shapeLogged;

    // Called from the per-frame pump (cheap once installed). Lazy, failure-tolerant,
    // and silent after a terminal failure.
    internal static void Poll()
    {
        if (_dead) return;
        if (!_subscribed) TrySubscribe();
        if (_subscribed) MaybeDump();
    }

    private static void TrySubscribe()
    {
        if (_tried && _profiler != null) return;
        if (_tried) return;    // one construction attempt per reload; a throw marks _dead
        _tried = true;
        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var fiProf = core?.GetField("GPUProfiler", Any);
            _profiler = fiProf?.GetValue(null);
            if (_profiler == null)
            {
                _dead = true;
                RttLog.Line("GPU REPORT PROBE: CoreSystems.GPUProfiler is absent/null — no GPU table this build.");
                return;
            }

            var tProf = _profiler.GetType();
            _fiReports = tProf.GetField("_tmpReports", Any);
            var tReport = tProf.GetNestedType("Report", Any);
            if (_fiReports == null || tReport == null)
            {
                _dead = true;
                RttLog.Line("GPU REPORT PROBE: report plumbing not found (_tmpReports/Report) — engine shape changed.");
                return;
            }
            _fiTag      = tReport.GetField("Tag", Any);
            _fiDuration = tReport.GetField("Duration", Any);
            _fiBegin    = tReport.GetField("BeginTimeS", Any);
            _fiQueue    = tReport.GetField("QueueType", Any);
            _fiDepth    = tReport.GetField("Depth", Any);

            // DiscardGPUBlocks: if the engine is discarding block queries, say so once —
            // silence must be diagnosable (the instrument confesses when not installed).
            try
            {
                var cached = Type.GetType("Keen.VRage.Render12.Core.Systems.Cached, VRage.Render12");
                var cfg = cached?.GetProperty("RenderConfiguration", Any)?.GetValue(null);
                var discard = cfg?.GetType().GetProperty("DiscardGPUBlocks", Any)?.GetValue(cfg);
                if (discard is true)
                {
                    _dead = true;
                    RttLog.Line("GPU REPORT PROBE: RenderConfiguration.DiscardGPUBlocks is TRUE — the engine " +
                                "discards all GPU block queries in this configuration. No GPU table possible " +
                                "without flipping that configuration flag.");
                    return;
                }
            }
            catch { /* the probe still works if the flag read fails — worst case is silence */ }

            // NOT a direct engine-event subscription: a logic delegate on an engine event
            // outlives this collectible assembly across hot reloads. The BOOTSTRAP owns
            // the one subscription (EnsureGpuProfilerSubscription) and forwards through
            // this bridge field, which every install overwrites — the standard pattern.
            _fiBridgeHook = Type.GetType("RttProbe.RttBridge, RttProbe")
                ?.GetField("GpuWatchesReadyHook", Any);
            if (_fiBridgeHook == null)
            {
                _dead = true;
                RttLog.Line("GPU REPORT PROBE: GpuWatchesReadyHook not on this bootstrap — restart the game " +
                            "to adopt it. No GPU table until then.");
                return;
            }
            _fiBridgeHook.SetValue(null, (Action)OnWatchesReady);
            _subscribed = true;
            RttLog.Line("GPU REPORT PROBE: armed via the bootstrap forwarder — first windows dump the " +
                        "raw tag shape; clustering into player-pass vs feed-pass comes once the shape is seen.");
        }
        catch (Exception e)
        {
            _dead = true;
            RttLog.Error("gpu report probe subscribe", e);
        }
    }

    // Render-thread handler: aggregate only, never log here, never throw.
    private static void OnWatchesReady()
    {
        try
        {
            if (_fiReports?.GetValue(_profiler) is not IList reports || reports.Count == 0) return;
            lock (_lock)
            {
                _frames++;
                for (int i = 0; i < reports.Count; i++)
                {
                    var r = reports[i];
                    if (r == null) continue;
                    string tag = _fiTag?.GetValue(r) as string ?? "?";
                    double ms = _fiDuration?.GetValue(r) is TimeSpan ts ? ts.TotalMilliseconds : 0;
                    _tagTotals.TryGetValue(tag, out var cur);
                    _tagTotals[tag] = (cur.ms + ms, cur.n + 1);
                }
            }
        }
        catch { if (++_handlerErrs > 50) Unsubscribe(); }
    }

    private static void MaybeDump()
    {
        var now = Clock.Ms;
        if (now - _lastDumpMs < 15000) return;
        _lastDumpMs = now;

        KeyValuePair<string, (double ms, int n)>[] rows;
        int frames;
        lock (_lock)
        {
            if (_tagTotals.Count == 0) return;
            rows = new KeyValuePair<string, (double, int)>[_tagTotals.Count];
            int i = 0;
            foreach (var kv in _tagTotals) rows[i++] = new(kv.Key, kv.Value);
            frames = _frames;
            _tagTotals.Clear();
            _frames = 0;
        }
        Array.Sort(rows, (a, b) => b.Value.ms.CompareTo(a.Value.ms));

        var sb = new System.Text.StringBuilder();
        int shown = 0;
        foreach (var kv in rows)
        {
            if (shown++ == 12) break;
            sb.Append(shown == 1 ? "" : "  |  ")
              .Append(kv.Key).Append(": ")
              .Append((kv.Value.ms / Math.Max(1, frames)).ToString("F2")).Append("ms x")
              .Append(((double)kv.Value.n / Math.Max(1, frames)).ToString("F1")).Append("/frame");
        }
        RttLog.Line($"GPU BLOCKS over {frames} frame(s), per-frame means: {sb}" +
                    (rows.Length > 12 ? $"  (+{rows.Length - 12} more tags)" : "") +
                    (_shapeLogged ? "" : "   <- SHAPE WINDOW: a tag at ~2x/frame while the feed renders " +
                                          "is one that runs in BOTH passes; the split lands next build."));
        _shapeLogged = true;
    }

    private static void Unsubscribe()
    {
        try { _fiBridgeHook?.SetValue(null, null); } catch { }
        _subscribed = false;
        _dead = true;
        RttLog.Line("GPU REPORT PROBE: disarmed (bridge hook cleared).");
    }

    // Hot-reload hygiene: clear the bridge delegate so it cannot point into a dead
    // assembly; the next install re-sets it. The bootstrap's engine subscription persists
    // harmlessly (its handler null-checks the bridge field).
    internal static void Reset()
    {
        try { _fiBridgeHook?.SetValue(null, null); } catch { }
        _subscribed = false; _tried = false; _dead = false;
    }
}
