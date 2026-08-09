using System;
using System.Collections;
using System.Reflection;

namespace RttProbe;

// ---- THE IMPOSTOR PROBE (2026-08-08, the missing-mid-band investigation) ----------------
//
// The user's feed shows full foliage to a cutoff, a lip of cascade shadows beyond, then
// NOTHING — no impostor/LOD cards at any distance-curve shape. IL mapping:
//
//   DistanceTagManagerComponent.OnUpdateImpostorTag assigns Near/FarDistanceTag per entity
//   by comparing its cached distance (the number our viewerDistance override presents for
//   bubble entities) against the GLOBAL ImpostorSettings.SwapDistance. SHARED per-entity
//   state — never scope SwapDistance per-pass (the flashing law).
//
//   A far-tagged entity draws its baked ImpostorMesh — or NOTHING if no card exists. Bakes
//   only queue on streaming events (InstancedModelEntityComponent.Set* -> MarkDirty), and
//   ImpostorManagerComponent.Update DISCARDS the dirty queue when
//   ImpostorSettings.EnableImpostorGeneration is false.
//
// So the whole diagnosis reduces to three live values and two queue counts, which this
// probe reports every 15 s (level-triggered; cheap reflection, cached accessors).
internal static class ImpostorProbe
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;

    private static int _state;      // 0 untried, 1 armed, -1 unavailable (said why once)
    private static object _settingsObj;
    private static FieldInfo _fiGen, _fiSwitch, _fiSwap;
    private static object _manager;
    private static FieldInfo _fiRegistered, _fiDirty, _fiGenerating;
    private static long _lastMs;
    private static string _lastLine;

    internal static void Poll()
    {
        if (_state == -1) return;
        var now = Clock.Ms;
        if (now - _lastMs < 15000) return;
        _lastMs = now;
        try
        {
            if (_state == 0 && !TryResolve()) return;

            bool gen = _fiGen?.GetValue(_settingsObj) is true;
            bool sw = _fiSwitch?.GetValue(_settingsObj) is true;
            float swap = _fiSwap?.GetValue(_settingsObj) is float f ? f : float.NaN;
            int reg = -1, dirty = -1;
            bool generating = false;
            if (_manager != null)
            {
                reg = (_fiRegistered?.GetValue(_manager) as IDictionary)?.Count ?? -1;
                dirty = (_fiDirty?.GetValue(_manager) as IList)?.Count ?? -1;
                generating = _fiGenerating?.GetValue(_manager) != null;
            }

            // Only log on change — this is a state readout, not a heartbeat.
            var line = $"IMPOSTOR PROBE: EnableGeneration={gen} EnableSwitching={sw} SwapDistance={swap:F0}m " +
                       $"| manager: registered={reg} dirtyQueue={dirty} generatingNow={generating}. " +
                       (!gen ? "GENERATION OFF — dirty impostors are DISCARDED unbaked; far-tagged entities " +
                               "with no card draw NOTHING, which is the missing mid-band."
                             : !sw ? "SWITCHING OFF — entities never far-tag, impostors never engage."
                                   : float.IsNaN(swap) ? "swap distance unreadable."
                                   : $"Entities presented beyond {swap:F0}m far-tag and need a baked card to appear.");
            if (line != _lastLine) { _lastLine = line; RttLog.Line(line); }
        }
        catch (Exception e)
        {
            _state = -1;
            RttLog.Error("impostor probe", e);
        }
    }

    private static bool TryResolve()
    {
        // Settings.Impostor — same route every settings probe uses.
        var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
        var settings = core?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var impostor = settings?.GetType().GetProperty("Impostor", Any)?.GetValue(settings);
        if (impostor == null)
        {
            _state = -1;
            RttLog.Line("IMPOSTOR PROBE: Settings.Impostor unreachable — probe disarmed.");
            return false;
        }
        _settingsObj = impostor;
        var t = impostor.GetType();
        _fiGen = t.GetField("EnableImpostorGeneration", Any);
        _fiSwitch = t.GetField("EnableImpostorSwitching", Any);
        _fiSwap = t.GetField("SwapDistance", Any);

        // The manager: walk the scene-components roster the way other probes do — it is a
        // GeneratedContainer component on the render scene. Best-effort; the settings half
        // is the load-bearing readout and works without it.
        try
        {
            var sceneMgr = core.GetField("SceneManager", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var scenes = sceneMgr?.GetType().GetProperty("Scenes", Any)?.GetValue(sceneMgr) as IEnumerable;
            if (scenes != null)
            {
                foreach (var scene in scenes)
                {
                    var comps = scene?.GetType().GetProperty("Components", Any)?.GetValue(scene) as IEnumerable;
                    if (comps == null) continue;
                    foreach (var c in comps)
                    {
                        if (c?.GetType().Name != "ImpostorManagerComponent") continue;
                        _manager = c;
                        var mt = c.GetType();
                        _fiRegistered = mt.GetField("_registeredEntities", Any);
                        _fiDirty = mt.GetField("_dirtyImpostors", Any);
                        _fiGenerating = mt.GetField("_generatingImpostor", Any);
                        break;
                    }
                    if (_manager != null) break;
                }
            }
        }
        catch { /* settings-only readout is still the answer */ }

        _state = 1;
        RttLog.Line($"IMPOSTOR PROBE armed (manager {(_manager == null ? "not found — settings-only" : "found")}).");
        return true;
    }

    internal static void Reset()
    {
        _state = 0; _settingsObj = null; _manager = null; _lastLine = null; _lastMs = 0;
    }
}
