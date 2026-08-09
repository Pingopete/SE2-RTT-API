using System.Reflection;
using Keen.VRage.Library.Mathematics;   // Vector3D, for the planet-radial orbit up

namespace RttProbe;

// Drive the engine's WHOLE renderer a second time, from our camera, into our target.
//
// WHY THIS ROUTE. Everything before it has been an attempt to make the environment
// probe's renderer look like the main one. That renderer is not the main one turned
// down — it is a different, cheaper pipeline. Its terrain shader
// (triplanargipixel.hlsl, 52 lines) samples no textures at all, so "asteroids have no
// texture" was never a tuning problem, and the deferred route that would have fixed it
// means reassembling the main pipeline pass by pass from the middle.
//
// This attacks the top instead:
//
//     pub Void SceneDrawSystem.Draw(ResizableRWRenderTargetTexture finalLDRBuffer)
//
// Public. Takes its destination as a parameter, and derives the render resolution from
// that buffer:
//
//     IL_0043  finalLDRBuffer.get_Resolution()
//     IL_0048  ExecuteScenePreparationAndRender(Vector2I)
//
// So the output and the size are already parameterised. Everything else it needs comes
// from CoreSystems statics — and those are public FIELDS, not readonly properties. A
// second render is a matter of swapping globals around a second call, not of finding a
// second renderer. There isn't one: a sweep of all 67 shipped assemblies found no
// portal, planar reflection, mirror, minimap, split-screen or secondary-view system.
// See docs/second-view-hunt.md.
//
// WHY THE HOOK IS ON Draw ITSELF. Draw has ZERO managed callers — it is invoked from
// engine glue. That makes it the only site where a second whole-scene render can be
// driven without re-entering a frame from inside itself, which is why the probe hook
// could never host this: it already sits inside Draw.
//
// POSTFIX, so the engine's frame is complete and its temporal state settled before we
// touch anything.
//
// WHAT KILLED IT LAST TIME. Tried early in the project and abandoned on two exceptions:
//
//     KeyNotFoundException: The given key 'R11G11B10_Float' was not present in the dictionary
//     InvalidOperationException: Nullable object must have a value
//
// R11G11B10_Float is ScreenBuffers.HDR_FORMAT = 26, and Draw borrows its LBuffer as
// BindableTexturePool.Borrow("LBuffer", 26, ScreenBuffers.MaxPreUpscaleResolution, ...)
// — format and size from the GLOBAL, against a smaller target of ours. Both exceptions
// fit that mismatch. Neither is structural, and both point at the same fix: own a
// ScreenBuffers rather than patching around the engine's.
//
// STAGED, like every other risky thing in this project. Stage 1 observes and constructs
// only; nothing is swapped and no second render runs until the pieces are proven
// individually. Doing it all at once is how the deferred route produced failures nobody
// could attribute.
internal static class WholeSceneRender
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // RE-ENTRANCY. Our own Draw call fires this same postfix. Without this the first
    // frame recurses until the stack dies.
    //
    // [ThreadStatic] because Draw runs on the render thread and nothing else should be
    // able to clear another thread's guard.
    [ThreadStatic] private static bool _inOurRender;

    // For the bootstrap's log-only probes (CopyJob / InitializeBuffers): lets a patched
    // engine method say whether it fired inside our nested Draw. ThreadStatic read is
    // correct — the probes fire on the render thread, same as the guard.
    public static bool InOurRender => _inOurRender;

    // Last observed FinalLDR "Resolution|MaxResolution", for the resolution tripwire.
    private static string _lastLdrRes;

    // ---- the FinalLDR resize -----------------------------------------------------
    //
    // The ScreenBuffers CTOR sizes FinalLDRTexture from the player's swapchain, and our
    // InitializeBuffers(512) re-initialises only the pre-upscale chain — so our "512"
    // render has been upscaling 512 -> 3840x2160 into a display-resolution texture every
    // frame since the route first worked, and the panel blit then scaled it back down.
    // Caught by the blit identity log (src=3840, 2160 on a 512 build).
    //
    // Resize is the engine's own designed per-frame path for these textures (HighlightJob
    // and friends resize borrowed targets mid-frame routinely), so this creates nothing.
    // It fixes the wasted 4K upscale and the engine's deferred
    // "Source and destination should have the same resolution" assert. NOTE the limit:
    // Resize changes the CURRENT resolution, not MaxResolution — the pool key — so if the
    // ghost is pool-key aliasing this alone will not clear it. The tripwire above and the
    // bootstrap probes decide that question.
    //
    // Called from the probe hook (render thread, live DirectCommandList — which IS a
    // CopyCommandList by inheritance). One attempt per rebuild.
    // PER-FEED (phase C1a): one resize attempt per feed per rebuild.
    private static bool _ldrResized
    { get => Feeds.Cur.LdrResized; set => Feeds.Cur.LdrResized = value; }

    // OWN THE BUFFER INSTEAD OF FIGHTING OVER IT (2026-08-01).
    //
    // The resize below is superseded. Measured A/B over ~5.5 min each at ~42 fps:
    //
    //                    resize ON      resize OFF
    //     VRAM drift     +56 MB/min     -11 MB/min
    //     >50ms frames   1.22/window    0.89/window
    //     worst hitch    432 ms         228 ms
    //     phantom ghost  absent         IMMEDIATE, over the whole main world
    //
    // So the resize was buying ghost-freedom at the price of a 56 MB/min leak — and that leak
    // is what drove the VRAM ratchet into streaming eviction, i.e. the object popping.
    //
    // WHY EITHER SETTING WAS WRONG. Our ScreenBuffers' FinalLDRTexture is sized from the
    // PLAYER'S SWAPCHAIN — the one buffer our InitializeBuffers(feed size) does not cover — so
    // it shares a pool bucket with the player's same-sized targets. Without the resize we
    // upscale the feed across the full 3840x2160 of a texture the player's pass then presents:
    // the ghost. With it we write only the 1024 corner, which narrows the blast radius without
    // ending the sharing — and because SceneDrawSystem.Draw resizes whatever buffer it is
    // handed to the render resolution, the player's Draw sizes it back and ours sizes it down,
    // forever, reallocating each time. That realloc traffic IS the leak.
    //
    // The fix is ownership: borrow a resizable target at OUR size under a per-feed name, so it
    // lands in its own pool bucket and can never alias the player's, and install it once. With
    // the buffer already at render resolution, Draw has nothing to resize.
    //
    // Allocated ONCE PER FEED and reused across gate cycles — a per-cycle borrow would just be
    // a slower version of the churn this replaces.
    private static object _ownFinalLdr
    { get => Feeds.Cur.OwnFinalLdr; set => Feeds.Cur.OwnFinalLdr = value; }
    private static object _engineFinalLdr
    { get => Feeds.Cur.EngineFinalLdr; set => Feeds.Cur.EngineFinalLdr = value; }

    public static void EnsureOwnFinalLdr(object commandList)
    {
        if (_ldrResized || _ourScreenBuffers == null) return;
        _ldrResized = true;
        try
        {
            var sbType = _ourScreenBuffers.GetType();
            var prop = sbType.GetProperty("FinalLDRTexture", Any);
            var current = prop?.GetValue(_ourScreenBuffers);
            if (prop == null || current == null || !prop.CanWrite)
            {
                RttLog.Line("Own FinalLDR: FinalLDRTexture is not settable — falling back to the RESIZE " +
                            "path, which works but leaks ~56 MB/min. See EnsureFinalLdrSize.");
                EnsureFinalLdrSize(commandList);
                return;
            }

            int w = FeedConfig.WholeSceneWidth, h = FeedConfig.WholeSceneHeight;

            if (_ownFinalLdr == null)
            {
                // Format from the buffer we are replacing, never guessed: the panel blit and
                // the handover both key off it, and a mismatched format is a device removal
                // rather than a wrong colour.
                // THE BACKING FIELD, because the property is UNREACHABLE BY NAME.
                //
                // ResizableRWRenderTargetTexture exposes Format only as EXPLICIT interface
                // implementations — ITexture2DView.Format, IRenderTargetView.Format,
                // IRWTexture2DView.Format — whose reflected names are interface-qualified, so
                // GetProperty("Format") returns null however many times you try it. The
                // member dump is what showed this; guessing would not have.
                //
                // _resourceFormat is the resource's own format, and the LDR ring already
                // passes one format for both the resource and the view slots.
                var fmt = MemberValue(current, "_resourceFormat")
                       ?? MemberValue(current, "_rtvFormat")
                       ?? MemberValue(current, "Format");
                var res = MakeVector2I(w, h);
                if (fmt == null || res == null || CameraRender.BorrowResizableRt == null)
                {
                    // SAY WHAT IS ACTUALLY THERE. "format=?" on its own cost a deploy: it names
                    // the symptom and not one fact that would fix it.
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"Own FinalLDR: cannot allocate (format={(fmt == null ? "?" : "ok")}, ")
                      .Append($"res={(res == null ? "?" : "ok")}, borrow=")
                      .Append($"{(CameraRender.BorrowResizableRt == null ? "MISSING" : "ok")}) — ")
                      .Append("falling back to the resize path. Members on ")
                      .Append(current.GetType().Name).Append(':');
                    foreach (var p in current.GetType().GetProperties(Any))
                        sb.Append("\n    prop  ").Append(p.PropertyType.Name).Append(' ').Append(p.Name);
                    foreach (var f in current.GetType().GetFields(Any))
                        sb.Append("\n    field ").Append(f.FieldType.Name).Append(' ').Append(f.Name);
                    RttLog.Line(sb.ToString());
                    EnsureFinalLdrSize(commandList);
                    return;
                }

                // PER-FEED NAME. "RttProbe" vs "RttProbe{Id}" already cost us a collision once:
                // two feeds asking the pool under one name get handed each other's target.
                string name = $"RttFinalLdr{Feeds.Cur.Id}";

                // ARGUMENTS BUILT BY PARAMETER TYPE, NOT BY REMEMBERED ORDER.
                //
                // The resizable borrow does NOT share the non-resizable one's argument order —
                // copying the LDR ring's (name, fmt, fmt, res, mips, null, 128) threw
                // "Object of type 'Vortice.DXGI.Format' cannot be converted to
                // 'Vector2I'". Matching each slot by its declared type is order-independent,
                // so it survives both this signature and any future reshuffle, and the
                // signature is logged once so a wrong fill is readable rather than a throw.
                // BY PARAMETER NAME, and the VIEW formats are not the RESOURCE format.
                //
                //   String debugName | Format srvFormat | Vector2I maxResolution
                //   Format uavFormat | Int32 mipMaps | Color clearColor | Int32 lifetime
                //
                // Filling both Format slots from _resourceFormat gave R8G8B8A8_TYPELESS in the
                // SRV and UAV slots, which D3D rejects outright (E_INVALIDARG) — a typeless
                // format has no defined interpretation, so it cannot back a view. That is
                // precisely why the texture carries _resourceFormat, _srvFormat and _uavFormat
                // as three separate fields. Copy each to its own slot.
                //
                // Note there is no separate "resolution": the texture is created at
                // maxResolution, which IS the pool key — so asking for the feed size here is
                // what puts us in our own bucket, away from the player's swapchain-sized
                // targets. That is the whole point of the change.
                var srvFmt = MemberValue(current, "_srvFormat") ?? fmt;
                var uavFmt = MemberValue(current, "_uavFormat") ?? fmt;

                // CREATE IT, DO NOT BORROW IT. This is task #34 and it is also the last
                // per-frame assertion we cause.
                //
                // The borrow below asks BindableTexturePoolManager for a texture with
                // lifetime 128 and then keeps it for the whole gate cycle — the old comment
                // on RestoreEngineFinalLdr even says so, "a pool borrow that outlives the
                // gate cycle deliberately". But a borrow the pool has lent and not had back
                // sits in Pool._allocated, and Pool.OnFrameEndDisposal asserts
                //     Some of the borrowed textures has not been returned; '_allocated.Count == 0'
                // EVERY FRAME for as long as we hold it. One session's deferred summary
                // counted 9872 of them. And since the crash reporter promotes the FIRST
                // assertion of a session into a fatal exception at exit, a permanent borrow
                // is also a permanent exit-to-menu CTD.
                //
                // BindableTextureManager.CreateRWResizableRenderTargetTexture returns the
                // same ResizableRWRenderTargetTexture type, allocated directly and owned by
                // us. It never enters the pool's ledger, so there is nothing to assert about
                // — and we keep every property the borrow was chosen for, because the size we
                // ask for is still the feed size and it still cannot alias the player's
                // swapchain-sized targets.
                //
                // The borrow stays as the fallback: on a build where this method is missing,
                // a feed that asserts is better than no feed at all.
                _ownFinalLdrOwned = false;
                var texMgr = _coreType?.GetField("BindableTextures", BindingFlags.Public | BindingFlags.Static)
                                      ?.GetValue(null);
                var miCreate = texMgr?.GetType().GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "CreateRWResizableRenderTargetTexture");
                if (miCreate != null)
                {
                    try
                    {
                        var cps = miCreate.GetParameters();
                        var cargs = new object[cps.Length];
                        for (int i = 0; i < cps.Length; i++)
                            cargs[i] = cps[i].Name switch
                            {
                                "debugName"  => name,
                                "srvFormat"  => srvFmt,
                                "resolution" => res,
                                "uavFormat"  => uavFmt,
                                "mipMaps"    => 1,
                                _            => cps[i].HasDefaultValue ? cps[i].DefaultValue : null,
                            };
                        _ownFinalLdr = miCreate.Invoke(texMgr, cargs);
                    }
                    catch (Exception e) { RttLog.Error("create own FinalLDR (falling back to the pool borrow)", e); }
                }

                if (_ownFinalLdr != null)
                {
                    _ownFinalLdrOwned = true;
                    RttLog.Line($"Own FinalLDR: CREATED \"{name}\" at {w}x{h} via " +
                                "BindableTextures.CreateRWResizableRenderTargetTexture — ours outright, not a " +
                                "pool borrow. It never enters Pool._allocated, so the per-frame " +
                                "\"borrowed textures has not been returned\" assertion stops, and with it the " +
                                "exit-to-menu crash that assertion was being promoted into.");
                }
                else
                {

                var ps = CameraRender.BorrowResizableRt.GetParameters();
                var args = new object[ps.Length];
                var sig = new System.Text.StringBuilder();
                for (int i = 0; i < ps.Length; i++)
                {
                    var pt = Nullable.GetUnderlyingType(ps[i].ParameterType) ?? ps[i].ParameterType;
                    switch (ps[i].Name)
                    {
                        case "debugName":     args[i] = name;   break;
                        case "srvFormat":     args[i] = srvFmt; break;
                        case "uavFormat":     args[i] = uavFmt; break;
                        case "maxResolution": args[i] = res;    break;
                        case "mipMaps":       args[i] = 1;      break;
                        case "lifetime":      args[i] = 128;    break;
                        default:
                            // Unknown slot: fall back to type, then to the declared default.
                            if (pt == typeof(string)) args[i] = name;
                            else if (pt == res.GetType()) args[i] = res;
                            else args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                            break;
                    }
                    sig.Append("\n    ").Append(pt.Name).Append(' ').Append(ps[i].Name)
                       .Append(" <- ").Append(args[i] ?? "null");
                }
                RttLog.Line($"Own FinalLDR: BorrowResizableRWRenderTargetTexture signature —{sig}");
                _ownFinalLdr = CameraRender.BorrowResizableRt.Invoke(CameraRender.TexPool, args);
                if (_ownFinalLdr == null)
                {
                    RttLog.Line("Own FinalLDR: the borrow returned null — falling back to the resize path.");
                    EnsureFinalLdrSize(commandList);
                    return;
                }
                RttLog.Line($"Own FinalLDR: BORROWED \"{name}\" at {w}x{h} in the feed's own pool bucket " +
                            "(the create path was unavailable on this build). It cannot alias the player's " +
                            "swapchain-sized targets, so the ghost has no route — but the pool will assert " +
                            "\"borrowed textures has not been returned\" once per frame for as long as we " +
                            "hold it, and that assertion becomes the exit-to-menu crash.");
                }
            }

            // THE POOL HANDS BACK A WRAPPER, NOT THE TEXTURE.
            //
            // BorrowResizableRWRenderTargetTexture returns Borrowed<ResizableRWRenderTargetTexture>
            // — a lifetime handle. Assigning that straight to FinalLDRTexture throws, because
            // the property wants the texture. Unwrap by finding the member whose type the
            // property will actually accept, rather than guessing a name like .Resource or
            // .Value: the wrapper is generic and the accessor has been renamed before.
            object install = _ownFinalLdr;
            if (!prop.PropertyType.IsInstanceOfType(install))
            {
                object inner = null;
                foreach (var p in install.GetType().GetProperties(Any))
                    if (p.GetIndexParameters().Length == 0 && prop.PropertyType.IsAssignableFrom(p.PropertyType))
                    { try { inner = p.GetValue(install); } catch { } if (inner != null) break; }
                if (inner == null)
                    foreach (var f in install.GetType().GetFields(Any))
                        if (prop.PropertyType.IsAssignableFrom(f.FieldType))
                        { try { inner = f.GetValue(install); } catch { } if (inner != null) break; }

                if (inner == null)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"Own FinalLDR: the pool returned {install.GetType().Name} and nothing on it ")
                      .Append($"is assignable to {prop.PropertyType.Name} — falling back to the resize path. ")
                      .Append("Members:");
                    foreach (var p in install.GetType().GetProperties(Any))
                        sb.Append("\n    prop  ").Append(p.PropertyType.Name).Append(' ').Append(p.Name);
                    foreach (var f in install.GetType().GetFields(Any))
                        sb.Append("\n    field ").Append(f.FieldType.Name).Append(' ').Append(f.Name);
                    RttLog.Line(sb.ToString());
                    EnsureFinalLdrSize(commandList);
                    return;
                }
                install = inner;
            }

            _engineFinalLdr ??= current;
            prop.SetValue(_ourScreenBuffers, install);
            var wasRes = current.GetType().GetProperty("Resolution", Any)?.GetValue(current);
            RttLog.Line($"Own FinalLDR: installed on our ScreenBuffers (was {wasRes}, " +
                        $"now {w}x{h}). Watch the FinalLDR tripwire: it should now be SILENT.");
        }
        catch (Exception e)
        {
            RttLog.Error("own FinalLDR", e);
            EnsureFinalLdrSize(commandList);
        }
    }

    // Property OR field, in that order. Engine types are inconsistent about which, and
    // asking for only one is the reflection mistake this project keeps repeating.
    private static object MemberValue(object o, string name)
    {
        if (o == null) return null;
        try
        {
            var t = o.GetType();
            var p = t.GetProperty(name, Any);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o);
            return t.GetField(name, Any)?.GetValue(o);
        }
        catch { return null; }
    }

    // Put the engine's buffer back so ScreenBuffers.Dispose frees what IT allocated, then
    // release ours.
    //
    // The second half is new and is the other half of the create-instead-of-borrow change.
    // While ours was a pool borrow there was nothing to do here — the pool nominally owned
    // it (which is precisely why it asserted every frame). Now that we allocate it, nobody
    // else will ever free it, so a gate cycle that forgot this would leak a full feed-sized
    // RW render target per cycle. Ordering matters: the engine's texture goes back on the
    // ScreenBuffers FIRST, so nothing is pointing at ours when it is disposed.
    private static bool _ownFinalLdrOwned;
    private static long _lastFloraClamp, _floraClampedTotal;
    private static bool _floraClampLogged;
    private static long _lastEpisodeCheck, _lastFullRefresh, _lastSsUpdateW;
    private static bool _episodeLooked, _ssWatchLooked;
    private static FieldInfo _fFullRefresh, _fSsUpdateW;

    private static void RestoreEngineFinalLdr()
    {
        try
        {
            if (_ourScreenBuffers != null && _engineFinalLdr != null)
            {
                _ourScreenBuffers.GetType().GetProperty("FinalLDRTexture", Any)
                    ?.SetValue(_ourScreenBuffers, _engineFinalLdr);
                _engineFinalLdr = null;
            }

            if (_ownFinalLdrOwned && _ownFinalLdr is IDisposable d)
            {
                d.Dispose();
                RttLog.Line("Own FinalLDR: disposed — we allocated it, so nobody else was ever going to.");
            }
            _ownFinalLdr = null;
            _ownFinalLdrOwned = false;
        }
        catch (Exception e) { RttLog.Error("restore engine FinalLDR", e); }
    }

    public static void EnsureFinalLdrSize(object commandList)
    {
        // SUPERSEDED by EnsureOwnFinalLdr — kept as the fallback for when the FinalLDRTexture
        // setter or the pool borrow is unavailable, because a feed that resizes and leaks is
        // still better than a feed that ghosts over the player's whole world.
        if (!FeedConfig.WholeSceneLdrResize) return;
        if (_ourScreenBuffers == null || commandList == null) return;
        try
        {
            var ldr = _ourScreenBuffers.GetType().GetProperty("FinalLDRTexture", Any)?.GetValue(_ourScreenBuffers);
            if (ldr == null) return;

            var resProp = ldr.GetType().GetProperty("Resolution", Any);
            var cur = resProp?.GetValue(ldr);
            int w = FeedConfig.WholeSceneWidth, h = FeedConfig.WholeSceneHeight;
            if (cur != null && cur.ToString().Contains($"X:{w}") && cur.ToString().Contains($"Y:{h}"))
                return;     // already right — nothing to log, nothing to do

            var v2i = cur?.GetType();                 // Vector2I, taken from the live value
            var target = v2i == null ? null : Activator.CreateInstance(v2i);
            v2i?.GetField("X")?.SetValue(target, w);
            v2i?.GetField("Y")?.SetValue(target, h);

            var resize = ldr.GetType().GetMethod("Resize", Any);
            if (target == null || resize == null)
            {
                RttLog.Line("FinalLDR resize: Resize/Vector2I unreachable — the 4K upscale stays.");
                return;
            }

            resize.Invoke(ldr, new[] { commandList, target });
            // The prose used to hardcode "512->512", written when 512 was the only
            // resolution — misleading log text is how the ghost hunt lost a day, so it
            // prints the live values now.
            RttLog.Line($"FinalLDR resized: {cur} -> {w}x{h}. Our render now runs {w}x{h}->{w}x{h} " +
                        "(no upscale) instead of ->3840x2160, and the panel blit scales from that. " +
                        "MaxResolution (the pool key) is unchanged by design — watch the tripwire " +
                        "for Draw resizing it back.");
        }
        catch (Exception e) { RttLog.Error("FinalLDR resize", e); }
    }

    private static string LdrRes(object ldr)
    {
        try
        {
            var t = ldr.GetType();
            var res = t.GetProperty("Resolution", Any)?.GetValue(ldr);
            var max = t.GetProperty("MaxResolution", Any)?.GetValue(ldr);
            return $"{res}|max {max}";
        }
        catch { return "?"; }
    }

    // ---- THE GRASS PROBE -------------------------------------------------------------
    //
    // WHY A PROBE RATHER THAN MORE IL. The grass chain has now been read end to end and
    // every link looks correct on paper:
    //
    //   RenderMainView -> RenderGBuffer -> RenderGrass, and stage 11 is not on our skip list
    //   the gate is  firstPass && !Is3DMapEnabled && Grass.Enabled
    //                && Grass.DrawDistance > 0 && Grass.Density > 0
    //   DrawDistance defaults to 1000 m, so our 22 m orbit is nowhere near a distance cull
    //   every resource RenderGrass touches comes from CoreSystems.DrawContexts and
    //     CoreSystems.ScreenBuffers -- the two globals we already swap for our pass
    //   the generator's camera is JitteredCameraSettings, which CameraCbSwap points at ours
    //
    // Reading correct code and seeing no grass means one of those reads is wrong about the
    // LIVE values, and IL cannot say which. So: print them, from inside our own pass, once
    // every 15 s.
    //
    // The three questions it can distinguish, which is the point of printing all of them
    // together rather than the first one that looks suspicious:
    //
    //   Grass.Enabled false / Density 0    the gate never opens; nothing downstream matters
    //   EntityProxies count 0              the gate opens onto an EMPTY culled set, so grass
    //                                      generates for no entity -- a culling problem, not
    //                                      a grass problem
    //   both fine                          the gate opens, proxies exist, and the failure is
    //                                      in generation or in the draw -- GrassBufferContext
    //                                      sizing is then the next suspect
    private static long _grassProbeTicks;
    private static bool _grassProbeShapeLogged;

    // ---- THE RAYTRACING RESIDENCY PROBE ------------------------------------------------
    //
    // THE QUESTION: stage 0 (ExecuteAccelerationStructuresBuilding) is skipped for our pass
    // and always has been, because RayTracingSceneManager.CreateTLAS is CAMERA-DEPENDENT AND
    // WORLD-SPACE SHARED — building it from our camera corrupted the player's, and there is
    // only one. But stage 17 (RaytraceGIJob.DoWork, the trace itself) is NOT skipped, and
    // wholeSceneDisableRaytracing is 0. So we trace OUR camera's rays against a structure
    // built around the PLAYER.
    //
    // At ~100 m — every early test, in space — that was approximately right and nobody could
    // have noticed. At remote-feed range it means the rays hit nothing: we pay for GI and
    // reflections of a world that is not in the acceleration structure.
    //
    // BEFORE OWNING A SECOND RayTracingSceneManager (a big change: park it in the bootstrap
    // so it survives hot reloads, install for our render, restore on unwind — the
    // EnvironmentProbeManager pattern), PROVE THE PREMISE. That is what this reads.
    //
    // WHAT IS READABLE AND WHAT IS NOT, stated up front so a blind column is never mistaken
    // for a zero: the Buffer<T> fields are NATIVE (IntPtr _data + _count), so Count is
    // readable but ELEMENTS ARE NOT — no positions can come from them. _rootEntityToIndex is
    // a managed Dictionary, so its KEYS are enumerable and are the only route to "is any RT
    // root entity actually near our camera".
    private static long _rtProbeTicks;
    private static bool _rtProbeShapeLogged;

    private static void RaytracingProbe()
    {
        var now = Environment.TickCount64;
        if (now - _rtProbeTicks < 15000) return;
        _rtProbeTicks = now;

        try
        {
            _coreType ??= Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var rt = _coreType?.GetField("RayTracingScene", BindingFlags.Public | BindingFlags.Static)
                              ?.GetValue(null);
            if (rt == null) { RttLog.Line("RT PROBE: CoreSystems.RayTracingScene is NULL."); return; }

            var t = rt.GetType();
            object F(string n) => t.GetField(n, Any)?.GetValue(rt);
            object P(string n) => t.GetProperty(n, Any)?.GetValue(rt);

            // Native Buffer<T>: Count only. A null here means the FIELD did not resolve,
            // which is a blind reader, not an empty buffer — say which.
            string Cnt(string n)
            {
                var b = F(n);
                if (b == null) return "?";
                var c = b.GetType().GetProperty("Count", Any)?.GetValue(b);
                return c?.ToString() ?? "unreadable";
            }

            // THE ROW THAT DECIDES IT. If the TLAS holds instances but the nearest RT root
            // entity is hundreds of km away, the structure is centred on the player and our
            // rays trace empty space — which is the whole premise of owning our own.
            string nearest = "root positions unreadable (reader blind, not the scene)";
            try
            {
                if (F("_rootEntityToIndex") is System.Collections.IDictionary map)
                {
                    object[] keys;
                    try { keys = new object[map.Count]; map.Keys.CopyTo(keys, 0); }
                    catch { keys = null; }          // torn snapshot: report fewer, never guess

                    if (keys != null)
                    {
                        var eye = CameraFeed.EyeCache;
                        double best = double.MaxValue; int positioned = 0;
                        foreach (var k in keys)
                        {
                            if (k == null) continue;

                            // SAY WHAT THE KEY IS BEFORE REPORTING THAT IT CANNOT BE READ.
                            //
                            // "NONE positionable — reader blind" has been printed for days
                            // without ever naming the thing it failed to read, so nobody could
                            // act on it. TryWorldPosition guesses a fixed list of member names;
                            // when the key is not that shape it returns null and the blindness
                            // is indistinguishable from an empty TLAS. Dumping the runtime type
                            // and its members once turns "blind" into an accessor somebody can
                            // actually write — the same move that made the grass probe pay off.
                            if (!_rtKeyShapeLogged)
                            {
                                _rtKeyShapeLogged = true;
                                var kt = k.GetType();
                                RttLog.Line($"RT PROBE key shape: _rootEntityToIndex key type = {kt.FullName} | " +
                                            $"fields = [{string.Join(", ", kt.GetFields(Any).Select(f => f.FieldType.Name + " " + f.Name).Take(16))}] | " +
                                            $"props = [{string.Join(", ", kt.GetProperties(Any).Select(p => p.PropertyType.Name + " " + p.Name).Take(16))}]. " +
                                            "This is the shape TryWorldPosition has to match; until it does, a zero " +
                                            "positionable count says nothing about the TLAS.");
                            }

                            var pos = TryWorldPosition(k);
                            if (pos == null) continue;
                            positioned++;
                            var d = (pos.Value - eye).Length();
                            if (d < best) best = d;
                        }
                        nearest = positioned == 0
                            ? $"{keys.Length} root(s), NONE positionable — reader: {RtPositionReaderHow}. " +
                              "If that names a member, the reader WORKS and the roots genuinely have no usable " +
                              "position; if it says unavailable, this is blindness and says nothing about the TLAS"
                            : $"{keys.Length} root(s), {positioned} positionable, NEAREST TO OUR CAMERA = " +
                              (best > 1000.0 ? $"{best / 1000.0:F1} km" : $"{best:F0} m") +
                              (best > 10000.0
                                  ? "   <-- TLAS IS NOT AROUND OUR CAMERA: our rays trace empty space"
                                  : "   <-- RT geometry IS near our camera");
                    }
                }
            }
            catch { }

            if (!_rtProbeShapeLogged)
            {
                _rtProbeShapeLogged = true;
                RttLog.Line("RT PROBE shape: RayTracingSceneManager fields = " +
                            string.Join(", ", t.GetFields(Any).Select(f => f.Name).Take(24)));
            }

            RttLog.Line($"RT PROBE (inside our pass): HasScene={P("HasScene")} " +
                        $"RTInstanceCount={P("RTInstanceCount")} instances={F("_currentInstancesCount")} " +
                        $"geometries={F("_currentGeometriesCount")} | roots={Cnt("_rootEntities")} " +
                        $"instancedModels={Cnt("_instancedModelEntities")} flora={Cnt("_floraSubSectorEntity")} " +
                        $"pointLights={Cnt("_pointLightEntities")} spotLights={Cnt("_spotLightEntities")} " +
                        $"| {nearest}");
        }
        catch (Exception e) { RttLog.Line("RT PROBE failed: " + e.Message); }
    }

    // Best-effort world position off an unknown handle/struct. Tries the shapes this engine
    // actually uses, in order, and returns null rather than a fabricated origin — a wrong
    // position here would read as "RT geometry is right next to us" and kill a correct
    // diagnosis, which is exactly how the clipmap cell reader misled us for a day.
    private static bool _rtKeyShapeLogged;

    // RESOLVE THE ACCESSOR ONCE, THEN APPLY IT TO EVERY KEY.
    //
    // The old version guessed a fixed list of member names per call, and when the key was not
    // one of those shapes it returned null — reported as "NONE positionable", which reads
    // identically to "the TLAS is empty near our camera". Two completely different findings
    // behind one output, which is the blind-reader trap this project keeps paying for.
    //
    // This resolves a strategy from the key's TYPE the first time and caches it, so the search
    // can afford to be thorough (named members, then ANY Vector3D member, then any MatrixD's
    // translation) without doing that work 1112 times a window. `_rtPosResolved` distinguishes
    // "not looked up yet" from "looked up and genuinely unavailable", so a cached failure is
    // still reported as blindness rather than as a zero.
    private static bool _rtPosResolved;
    private static Func<object, Vector3D?> _rtPosGet;
    private static string _rtPosHow = "not resolved";

    internal static string RtPositionReaderHow => _rtPosHow;

    private static void ResolveRtPositionReader(Type t)
    {
        _rtPosResolved = true;

        foreach (var n in new[] { "Position", "WorldPosition", "Translation" })
        {
            var p = t.GetProperty(n, Any); var f = p == null ? t.GetField(n, Any) : null;
            if (p?.PropertyType == typeof(Vector3D)) { _rtPosGet = o => p.GetValue(o) as Vector3D?; _rtPosHow = $"property {n}"; return; }
            if (f?.FieldType == typeof(Vector3D)) { _rtPosGet = o => f.GetValue(o) as Vector3D?; _rtPosHow = $"field {n}"; return; }
        }

        // Any Vector3D member at all, whatever it is called.
        foreach (var f in t.GetFields(Any))
            if (f.FieldType == typeof(Vector3D)) { _rtPosGet = o => f.GetValue(o) as Vector3D?; _rtPosHow = $"field {f.Name} (first Vector3D)"; return; }
        foreach (var p in t.GetProperties(Any))
            if (p.PropertyType == typeof(Vector3D)) { _rtPosGet = o => p.GetValue(o) as Vector3D?; _rtPosHow = $"property {p.Name} (first Vector3D)"; return; }

        // Any MatrixD member — take its translation row.
        foreach (var f in t.GetFields(Any))
            if (f.FieldType == typeof(MatrixD))
            {
                _rtPosGet = o => { var m = f.GetValue(o); return m is MatrixD md ? md.Translation : (Vector3D?)null; };
                _rtPosHow = $"field {f.Name}.Translation (MatrixD)"; return;
            }
        foreach (var p in t.GetProperties(Any))
            if (p.PropertyType == typeof(MatrixD))
            {
                _rtPosGet = o => { var m = p.GetValue(o); return m is MatrixD md ? md.Translation : (Vector3D?)null; };
                _rtPosHow = $"property {p.Name}.Translation (MatrixD)"; return;
            }

        _rtPosGet = null;
        _rtPosHow = "NO Vector3D or MatrixD member on the key type — genuinely unavailable, not a zero";
    }

    private static Vector3D? TryWorldPosition(object o)
    {
        try
        {
            if (!_rtPosResolved) ResolveRtPositionReader(o.GetType());
            return _rtPosGet?.Invoke(o);
        }
        catch { return null; }
    }

    // ---- THE TEXTURE STREAMING PROBE ---------------------------------------------------
    //
    // THE QUESTION, in the user's words: "what texture resolution setting is in the feed for
    // objects?" The answer is that there is no feed-specific one — the feed inherits the
    // game's global StreamingSettings, and an object's effective resolution is DERIVED:
    // f(texel density target, distance to camera, screen resolution).
    //
    // The distance term is the one that betrays us. Confirmed in IL:
    //   ManagedTexturePrioritizerComponent.OnCollectStandardsRoot
    //       reads Settings.Streaming.EnableCollectingMaterialDistances   <- GATE
    //       reads Settings.RenderView.CameraPosition                     <- THE CAMERA
    //       -> CollectStandards(ref cameraPositionRS, ...)
    // and StandardMaterialJobContext holds two ClosestDistanceCollector fields, so mips are
    // picked by CLOSEST DISTANCE TO A CAMERA — a nearest-viewer model by design, which is
    // why our viewerDistance postfix (min() only, never raises) composes with it correctly.
    //
    // READ THE GATE FIRST. If EnableCollectingMaterialDistances is FALSE, the camera position
    // never enters at all, priority is density-only, and the entire "tiers come from the
    // player's position" theory is dead however good it looks. That single boolean decides
    // whether any of this work is worth doing.
    //
    // SkipMipLevels is the other one that would end the discussion: it is a flat, global
    // "drop N mip levels" applied to everything. A non-zero value there means the feed looks
    // soft because the whole GAME is running reduced textures, not because of us.
    private static long _streamProbeTicks;

    // WHERE THE FEED'S AMBIENT ACTUALLY COMES FROM — measured, not reasoned.
    //
    // Reported 2026-08-05: with the probe cubes correctly captured at our camera (reflections
    // confirmed fixed), ambient in the feed STILL matches the player's local surroundings, and
    // moving the player's sun still changes it. Two inferences of mine have already been wrong
    // here, so this reports the state rather than arguing from call order.
    //
    // THE ONE THAT DECIDES IT is the CloseIBL fallback. get_CloseIBL() is:
    //
    //     if (!_lastSettings.Enable || !_lastSettings.ApplyEnvProbe)
    //         return CommonResources.SkyboxIBL;      <- a GLOBAL, the player's ambient too
    //     return _closeFinalTexture;                 <- our own capture
    //
    // _lastSettings is PER-MANAGER, and ours begins as default(EnvironmentProbeSettings) with
    // every bool false. If it is never populated, our manager hands AmbientLightJob the same
    // global skybox cube the player's frame uses — which is exactly "both renders using the
    // same ambience". Reference equality against CommonResources.SkyboxIBL settles it; nothing
    // else here can, because the fallback is silent by construction.
    //
    // Reports reader resolution SEPARATELY from value throughout: a missing field says "this
    // reader is blind", never "the value is false". That distinction is why the grass probe
    // eventually paid off and why its earlier verdict-shaped output cost two sessions.
    private static long _ambientProbeTicks;
    private static bool _ambientProbeShapeLogged;

    private static void AmbientSourceProbe()
    {
        var now = Environment.TickCount64;
        if (now - _ambientProbeTicks < 15000) return;
        _ambientProbeTicks = now;

        try
        {
            _coreType ??= Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var live = _coreType?.GetField("EnvironmentProbeManager", BindingFlags.Public | BindingFlags.Static)
                                ?.GetValue(null);
            if (live == null) { RttLog.Line("AMBIENT PROBE: CoreSystems.EnvironmentProbeManager not found — reader blind."); return; }

            // Is the manager installed for our pass actually OURS? Everything below is about
            // the player's frame instead if this says NO.
            string whose = _ourProbes == null
                ? "we have no manager of our own (wholeSceneOwnProbes off or never armed)"
                : (ReferenceEquals(live, _ourProbes) ? "OURS" : "THE ENGINE'S — our swap is not in effect here");

            var t = live.GetType();
            object LS(string name)
            {
                try
                {
                    var f = t.GetField("_lastSettings", Any);
                    if (f == null) return "NO _lastSettings FIELD (blind)";
                    var box = f.GetValue(live);
                    if (box == null) return "null";
                    var inner = box.GetType().GetField(name, Any) ?? box.GetType().GetField("<" + name + ">k__BackingField", Any);
                    return inner == null ? $"NO {name} FIELD (blind)" : inner.GetValue(box);
                }
                catch { return "threw"; }
            }

            // THE DECIDING COMPARISON. Ask the manager for the cube it will hand the ambient
            // job, then ask CommonResources for the global skybox, and compare REFERENCES.
            string cubeVerdict;
            try
            {
                var close = t.GetProperty("CloseIBL", Any)?.GetValue(live);
                var far = t.GetProperty("FarIBL", Any)?.GetValue(live);
                var common = _coreType?.GetField("CommonResources", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var sky = common?.GetType().GetProperty("SkyboxIBL", Any)?.GetValue(common);

                if (close == null && far == null) cubeVerdict = "CloseIBL/FarIBL both null — reader blind or probes disposed";
                else if (sky == null) cubeVerdict = "CommonResources.SkyboxIBL unreadable — CANNOT compare, no verdict";
                else
                {
                    bool cSky = ReferenceEquals(close, sky), fSky = ReferenceEquals(far, sky);
                    cubeVerdict = $"CloseIBL={(cSky ? "THE GLOBAL SKYBOX (fallback)" : "our own capture")}, " +
                                  $"FarIBL={(fSky ? "THE GLOBAL SKYBOX (fallback)" : "our own capture")}" +
                                  (cSky || fSky
                                      ? "   <-- THE FALLBACK IS LIVE: the feed's ambient is the SAME cube the player's " +
                                        "ambient samples, which is why both look identical. Enable/ApplyEnvProbe below " +
                                        "say why."
                                      : "   <-- both cubes are ours, so the IBL half of ambient is NOT the contamination");
                }
            }
            catch (Exception e) { cubeVerdict = "threw: " + e.GetType().Name; }

            object lastAmb;
            try { lastAmb = t.GetProperty("LastLocalLightAmbient", Any)?.GetValue(live) ?? "NO PROPERTY (blind)"; }
            catch { lastAmb = "threw"; }

            if (!_ambientProbeShapeLogged)
            {
                _ambientProbeShapeLogged = true;
                RttLog.Line("AMBIENT PROBE shape: EnvironmentProbeManager fields = " +
                            string.Join(", ", t.GetFields(Any).Select(f => f.Name).Take(24)));
            }

            RttLog.Line($"AMBIENT PROBE (inside our pass): manager={whose} | {cubeVerdict} | " +
                        $"_lastSettings.Enable={LS("Enable")} ApplyEnvProbe={LS("ApplyEnvProbe")} " +
                        $"ApplyLocalLightAmbient={LS("ApplyLocalLightAmbient")} EnableFullUpdate={LS("EnableFullUpdate")} " +
                        $"| LastLocalLightAmbient={lastAmb} (a single scalar computed at " +
                        "RenderView.CameraPosition inside PrepareProbes — ours only if InstallProbes ran after InstallCamera).");
        }
        catch (Exception e) { RttLog.Error("ambient source probe", e); }
    }

    private static void StreamingProbe()
    {
        var now = Environment.TickCount64;
        if (now - _streamProbeTicks < 15000) return;
        _streamProbeTicks = now;

        try
        {
            var field = ResolveSettingsField("StreamingSettings", out var settings);
            if (settings == null || field == null)
            {
                RttLog.Line("STREAMING PROBE: no StreamingSettings on SettingsManager — reader blind.");
                return;
            }

            var s = field.GetValue(settings);
            if (s == null) { RttLog.Line("STREAMING PROBE: StreamingSettings value null."); return; }
            var st = s.GetType();
            object F(string n) => st.GetField(n, Any)?.GetValue(s);

            var gate = F("EnableCollectingMaterialDistances");
            var skip = F("SkipMipLevels");

            RttLog.Line($"STREAMING PROBE (inside our pass): EnableCollectingMaterialDistances={gate} " +
                        $"SkipMipLevels={skip} DefaultTexelDensity={F("DefaultTexelDensity")} " +
                        $"ArmorTexelDensity={F("ArmorTexelDensity")} " +
                        $"TargetDensityAndNonPinnedRatio={F("TargetDensityAndNonPinnedRatio")} " +
                        $"MinTextureStreamingBytes={F("MinTextureStreamingBytes")} " +
                        $"TargetUnusedVRAMMult={F("TargetUnusedVRAMMult")} EnableCaching={F("EnableCaching")} " +
                        $"| playerSwapchainY={ScatterControl.PlayerSwapchainHeight()} feedY={FeedConfig.WholeSceneHeight}" +
                        (gate is bool g && !g
                            ? "   <-- GATE IS OFF: material distances are NOT collected, so camera position " +
                              "never enters texture priority. The player-position theory is DEAD and " +
                              "viewerDistance cannot help textures."
                            : "") +
                        (skip is byte b && b > 0
                            ? $"   <-- SkipMipLevels={b}: the WHOLE GAME is dropping {b} mip level(s). The feed " +
                              "looks soft because everything does; fix this before blaming the feed."
                            : ""));
        }
        catch (Exception e) { RttLog.Line("STREAMING PROBE failed: " + e.Message); }
    }

    // ---- THE STREAMING BUDGET PROBE — the unified theory's instrument -----------------
    //
    // THE HYPOTHESIS (user, 2026-08-02, and it unifies two bugs this project has never
    // explained): the PLAYER'S main-world LOD cycling AND the phantom mild frame hitching in
    // both views, present since the mod's beginning, are ONE effect of a GLOBAL STREAMING
    // BUDGET OSCILLATING — not of anything camera-specific.
    //
    // WHY A BUDGET AND NOT A RACE. The user's decisive observation is that the player's
    // objects and the feed's flora degrade AT THE SAME INSTANT. A race hits them
    // independently; only a single shared decision hits everything at once. And we have now
    // MEASURED that the distance/LOD-tag path never even overlaps our draw (0 of ~2.3M calls
    // inside our swap window), so the race explanation is dead on that path anyway.
    //
    // THE MECHANISM, from the engine's own types:
    //     WindowHisteresisFilter.ComputeMinimum(Int64 newRawBudget)
    //         fields: _windowFilter, _histeresisRatio, _lastReturnedBudget
    // It returns a MINIMUM OVER A WINDOW. So a transient dip in available streaming memory —
    // which a second full scene render at a remote location makes far more likely — is not a
    // momentary downgrade. It is LATCHED for the whole window: textures and LODs drop
    // globally, then recover when the window rolls off. That is a cycle whose period is the
    // filter's window length, applied to one budget that BOTH views read.
    //
    // AND IT EXPLAINS THE HITCHING TOO. Releasing the latch means re-streaming and
    // re-uploading everything that was dropped — a real burst of copy/upload work landing in
    // one or two frames. Same event, two symptoms. It also explains why every previous
    // explanation failed: it is not GC (task #18 exonerated that by measurement), it does not
    // track our render rate (measured today), and it does not track our draw cost.
    //
    // WHAT TO READ. StreamingStatManager publishes both sides of the filter:
    //     AvailableRaw       <- the filter's INPUT
    //     AvailableFiltered  <- its OUTPUT
    // If Filtered sits BELOW Raw for stretches and then snaps back, the latch is real and
    // visible. That single comparison is the whole test.
    private static long _streamProbe2Ticks;
    private static object _streamStats;
    private static readonly Dictionary<string, PropertyInfo> _statReaderProp = new();
    private static float _rawMin = float.MaxValue, _rawMax, _filtMin = float.MaxValue, _filtMax;
    private static bool _streamSurveyDone;
    private static int _latchedSamples, _totalSamples;

    // ---- THE GRASS PROBE: which input is empty? ---------------------------------------
    //
    // ESTABLISHED: RenderGrass RUNS in our pass (census: 3541 calls ours vs 5872 the
    // player's), yet the picture has no blades. So the DRAW is not the problem — generation
    // produced nothing, or produced something that never reached the screen. Those need
    // completely different fixes, and no amount of staring at the image separates them.
    //
    // RenderGrass sources every input from DrawContextManager — and we own OURS:
    //     MainViewCulling.EntityProxies   the culled set grass scatters onto
    //     MainOutputGeometryBuffers       the terrain geometry it scatters over
    //     GrassBufferContext              where generated blades land
    // EntityProxyContext has no CPU-side count (it lives in a GPU counter buffer), but
    // GrassBufferContext publishes STAT KEYS for exactly what we need:
    //
    //     grassInstancesRendered == 0  -> generation produced NOTHING. The inputs are empty,
    //                                    and the fix is upstream: our culling context or our
    //                                    output geometry buffers are not being populated.
    //     grassInstancesRendered  > 0  -> blades WERE generated and the loss is downstream in
    //                                    the draw or the material.
    //
    // One number, two completely different investigations. Reported once every 15 s.
    private static object _grassCtx;
    private static long _grassProbeMs;

    internal static void GrassProbe()
    {
        var now = Clock.Ms;
        if (now - _grassProbeMs < 15000) return;
        _grassProbeMs = now;
        try
        {
            if (_ourDrawContexts == null)
            {
                RttLog.Line("GRASS PROBE: we have no DrawContextManager of our own this pass — " +
                            "cannot read the grass counters. NOT a zero-blade result.");
                return;
            }
            _grassCtx ??= _ourDrawContexts.GetType().GetProperty("GrassBufferContext", Any)?.GetValue(_ourDrawContexts);
            if (_grassCtx == null)
            {
                RttLog.Line("GRASS PROBE: DrawContextManager exposes no GrassBufferContext — SHAPE MISS, not a zero.");
                return;
            }

            var rendered = ReadInstanceStat(_grassCtx, "_grassInstancesRenderedCountStatKey");
            var max = ReadInstanceStat(_grassCtx, "_maxGrassInstancesCountStatKey");
            if (rendered == null)
            {
                RttLog.Line("GRASS PROBE: the instance-count stat could not be read — BROKEN INSTRUMENT, not zero blades.");
                return;
            }

            RttLog.Line($"GRASS PROBE: instances rendered={rendered:F0}, max={(max?.ToString("F0") ?? "n/a")} in OUR grass buffer. " +
                        (rendered > 0.5f
                            ? "GENERATION WORKS — blades exist, so the loss is DOWNSTREAM: the draw, the material, or the " +
                              "target they land in. Stop looking at culling and geometry inputs."
                            : "GENERATION PRODUCED NOTHING. This does NOT by itself name our contexts — read the gate " +
                              "line below, which is what decides whether the generator ran at all.")
                        + "\n" + GpuSceneGrassGate(rendered > 0.5f));
        }
        catch (Exception e) { RttLog.Error("grass probe", e); }
    }

    // ---- THE TWO GATES THAT SIT BEFORE OUR CONTEXTS EVER MATTER ------------------------
    //
    // Read straight off the IL of GrassGenerationCommandsCreationJob.DoWork, whose first
    // instructions are:
    //
    //     n = GPUScene.GrassEntityData.MaxUsedIndex + 1
    //     if (GPUScene.GrassMaterialsBuffer == null || n == 0) return;     <-- both gates
    //     ...
    //     Dispatch(_createGenCommandsPSO, ceil(n / 64.0), 1, 1)
    //
    // TWO CONSEQUENCES, and both contradict what this probe used to conclude.
    //
    // FIRST, THE DISPATCH IS SIZED BY THE GLOBAL GRASS-ENTITY COUNT, NOT BY OUR CULLED SET.
    // EntityProxyOutputBuffer and CounterBuffer are bound as SRVs the SHADER reads for its
    // per-entity visibility test; they do not decide whether the shader runs. So "generation
    // produced nothing" never implied "our EntityProxies is empty" — that was one candidate
    // stated as a conclusion, and it sent this investigation at culling for two sessions.
    //
    // SECOND, BOTH GATES READ CoreSystems.GPUScene, WHICH IS A GLOBAL WE DO NOT SWAP. It is
    // the same object in the player's pass and in ours. If it is empty then grass is
    // impossible for EITHER camera, no per-pass state can change it, and every knob aimed at
    // this so far (HiZ, per-pass NoHiZ, draw distance, density, the stage-1 split) was tuning
    // a stage the code never reaches — which is exactly why none of them moved the number.
    //
    // MaxUsedIndex == -1 means NO GRASS ENTITY IS REGISTERED IN THE GPU SCENE AT ALL. That is
    // the prediction if grass models arrive solely through the streaming path and nothing has
    // streamed grass for a site with no player near it — and it is consistent with the one
    // control we have: the player stands in bare sandstone desert, with no grass anywhere.
    //
    // Every failure to read says so in words. A blind reader printing 0 here would fabricate
    // the very answer being tested, which is the specific mistake this project has already
    // made once with the grass census.
    // `producing` = the instance counter is non-zero this window. The verdict has to depend on
    // it: this method used to end with "...so a zero result IS a genuine per-pass failure"
    // UNCONDITIONALLY, which kept asserting a failure after grass started working (measured
    // 796 instances). A probe that states a conclusion the number contradicts is the exact
    // fault that sent this investigation at culling for two sessions — see the note above.
    private static string GpuSceneGrassGate(bool producing)
    {
        try
        {
            var core = _coreType ?? Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var gpuScene = core?.GetField("GPUScene", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (gpuScene == null)
                return "  GPU-SCENE GATE UNREADABLE: no CoreSystems.GPUScene (blind, NOT zero).";

            var gt = gpuScene.GetType();
            var grassData = gt.GetProperty("GrassEntityData", Any)?.GetValue(gpuScene);
            var matsProp = gt.GetProperty("GrassMaterialsBuffer", Any);
            object mats = matsProp?.GetValue(gpuScene);

            if (grassData == null)
                return "  GPU-SCENE GATE UNREADABLE: GPUScene exposes no GrassEntityData (blind, NOT zero).";
            if (grassData.GetType().GetProperty("MaxUsedIndex", Any)?.GetValue(grassData) is not int mid)
                return "  GPU-SCENE GATE UNREADABLE: GrassEntityData exposes no MaxUsedIndex (blind, NOT zero).";

            int n = mid + 1;
            string matsText = matsProp == null ? "GrassMaterialsBuffer NOT A MEMBER (blind)"
                            : mats == null ? "GrassMaterialsBuffer=NULL"
                                             : "GrassMaterialsBuffer=present";

            return (n == 0 || (matsProp != null && mats == null))
                ? $"  GPU-SCENE GATE SHUT: MaxUsedIndex={mid} (grass entities={n}), {matsText}. DoWork RETURNS " +
                   "BEFORE ITS DISPATCH, so nothing our pass owns is ever consulted. This is a SHARED global: " +
                   "grass is equally impossible for the PLAYER's camera right now. The fix belongs in grass-entity " +
                   "registration / model streaming, NOT in our draw contexts, and no per-pass knob can reach it."
                : $"  GPU-scene gate OPEN: MaxUsedIndex={mid} (grass entities={n}, dispatch={(n + 63) / 64} group(s)), " +
                  $"{matsText}. " +
                  (producing
                    ? "The generator runs AND is producing instances, so this gate is not the constraint. " +
                      "Grass entity COUNT is the number to watch from here: it tracks how much world is " +
                      "resident around the feed camera."
                    : "The generator DOES run, so a zero result IS a genuine per-pass failure: the shader's " +
                      "visibility test against OUR EntityProxyOutputBuffer/CounterBuffer rejected every entity.");
        }
        catch (Exception e)
        {
            return "  GPU-SCENE GATE UNREADABLE: " + e.GetType().Name + " (blind, NOT zero).";
        }
    }

    private static float? ReadInstanceStat(object owner, string keyField)
    {
        try
        {
            var key = owner.GetType().GetField(keyField, Any)?.GetValue(owner);
            var reader = key?.GetType().GetProperty("Reader", Any)?.GetValue(key);
            if (reader?.GetType().GetProperty("Value", Any)?.GetValue(reader) is float v) return v;
        }
        catch { }
        return null;
    }

    private static float? ReadStat(string keyField)
    {
        try
        {
            if (_streamStats == null)
            {
                _coreType ??= Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
                _streamStats = _coreType?.GetField("StreamingStats", BindingFlags.Public | BindingFlags.Static)
                                        ?.GetValue(null);
                if (_streamStats == null) return null;
            }
            var kf = _streamStats.GetType().GetField(keyField, Any);
            var key = kf?.GetValue(_streamStats);
            if (key == null) return null;

            if (!_statReaderProp.TryGetValue(keyField, out var rp))
            {
                rp = key.GetType().GetProperty("Reader", Any);
                _statReaderProp[keyField] = rp;
            }
            var reader = rp?.GetValue(key);
            if (reader?.GetType().GetProperty("Value", Any)?.GetValue(reader) is float v) return v;
        }
        catch { }
        return null;
    }

    // Sampled on EVERY render (41/s), not on the 15 s report clock: a latch that lasts a few
    // hundred milliseconds is invisible to a 15 s sample, and the whole question is whether
    // there IS a sub-second oscillation.
    internal static void SampleStreamingBudget()
    {
        var raw = ReadStat("AvailableRaw");
        var filt = ReadStat("AvailableFiltered");
        if (raw == null || filt == null) return;

        _totalSamples++;
        if (raw.Value < _rawMin) _rawMin = raw.Value;
        if (raw.Value > _rawMax) _rawMax = raw.Value;
        if (filt.Value < _filtMin) _filtMin = filt.Value;
        if (filt.Value > _filtMax) _filtMax = filt.Value;
        // "Latched" = the filter is holding a value materially below what it is being fed.
        if (raw.Value > 0 && filt.Value < raw.Value * 0.95f) _latchedSamples++;
    }

    private static void StreamingBudgetProbe()
    {
        var now = Environment.TickCount64;
        if (now - _streamProbe2Ticks < 15000) return;
        _streamProbe2Ticks = now;

        if (_totalSamples == 0)
        {
            if (ReadStat("AvailableRaw") == null)
                RttLog.Line("STREAMING BUDGET: CoreSystems.StreamingStats unreadable — probe blind.");
            return;
        }

        // SURVEY BEFORE FOCUS. AvailableRaw/Filtered turned out to be ~14 GB with a 2% swing —
        // i.e. no pressure at all — which says the hysteresis latch is not firing but says
        // NOTHING about the other twenty-odd counters. Dumping every StatKey once, with its
        // magnitude, is how we find which quantity is actually under pressure instead of
        // picking another pair by name and guessing again.
        if (!_streamSurveyDone)
        {
            _streamSurveyDone = true;
            try
            {
                var sb = new System.Text.StringBuilder("STREAMING STAT SURVEY (one-shot, MB where plausible): ");
                foreach (var f in _streamStats.GetType().GetFields(Any))
                {
                    if (f.FieldType.Name != "StatKey") continue;
                    var v = ReadStat(f.Name);
                    if (v == null) continue;
                    sb.Append($"{f.Name}=")
                      .Append(Math.Abs(v.Value) > 1048576.0
                                ? $"{v.Value / 1048576.0:F0}MB "
                                : $"{v.Value:F0} ");
                }
                RttLog.Line(sb.ToString());
            }
            catch (Exception e) { RttLog.Line("STREAMING STAT SURVEY failed: " + e.Message); }
        }

        double pct = 100.0 * _latchedSamples / _totalSamples;
        double rawSwing = _rawMax - _rawMin, filtSwing = _filtMax - _filtMin;

        // THE ACCEPTANCE TEST FOR THE RATCHET FIX, printed every window so it can be watched
        // across reloads rather than reconstructed afterwards. KnownNonStreaming is the
        // quantity that grew ~1.1 GB per hot reload; RealAvailableStreaming going negative is
        // what puts the streaming pool into thrash. If parking works, the first stays flat
        // across reloads and the second stays positive.
        // ---- THE SHARED POOL: CHURN OR DOUBLE RESIDENCY? -----------------------------
        //
        // THE USER'S QUESTION (2026-08-02), and it is a good one: we build our OWN
        // ScreenBuffers and DrawContextManager, so what happens to the engine resources that
        // are SHARED and keyed on resolution? They see the size flip between the player's
        // 3840x2160 and our 1024x1024, 41 TIMES A SECOND.
        //
        // We already have one confirmed casualty: CloudJob's CloudAccumulateLightAlpha was
        // disposed and recreated at our resolution 20x/sec and REMOVED THE DEVICE (skip 26).
        // The audit that found it cleared HighlightJob / TerrainBlendingJob /
        // AtmosphereAdditiveJob because they only call Resize(commandList, res) on a BORROWED
        // POOL TEXTURE — "the designed per-frame path". That reasoning is sound for a window
        // resize. It is not obviously sound at 41 flips per second between two very
        // different sizes, and those three jobs all RUN in our pass.
        //
        // TWO OUTCOMES, DIFFERENT BUGS, DIFFERENT FIXES — which is why this measures rather
        // than assumes:
        //   borrow count OSCILLATES + disposal traffic -> real churn. Cost is TIME
        //       (allocate/free), which is the frame hitching.
        //   borrow count STABLE                        -> the pool is caching BOTH sizes.
        //       Cost is SPACE: a permanent second set of full-size targets sitting in
        //       KnownNonStreaming, squeezing the streaming pool -> eviction cycling.
        try
        {
            _coreType ??= Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var pool = _coreType?.GetField("GPUResourcePool", BindingFlags.Public | BindingFlags.Static)
                                ?.GetValue(null);
            if (pool != null)
            {
                var pt = pool.GetType();
                object G(string n) => pt.GetField(n, Any)?.GetValue(pool);
                var gpuBorrowed = G("_gpuBorrowedObjects");
                var cpuBorrowed = G("_cpuBorrowedObjects");
                int disposalQueued = -1;
                if (G("ParallelD3DResourceDisposalQueue") is System.Collections.ICollection q)
                    disposalQueued = q.Count;

                RttLog.Line($"GPU POOL: gpuBorrowed={gpuBorrowed} cpuBorrowed={cpuBorrowed} " +
                            $"d3dDisposalQueue={(disposalQueued < 0 ? "unreadable" : disposalQueued.ToString())}. " +
                            "Watch gpuBorrowed ACROSS windows: a steady value means the pool caches both the " +
                            "player's 4K set and our 1024 set (a permanent memory cost, squeezing streaming); " +
                            "a value that swings, with disposal-queue traffic, means real allocate/free churn " +
                            "at 41 Hz (a time cost — the hitching).");
            }
        }
        catch { }

        var nonStream = ReadStat("KnownNonStreaming");
        var realAvail = ReadStat("RealAvailableStreaming");
        var missing = ReadStat("Missing");
        if (nonStream != null && realAvail != null)
            RttLog.Line($"STREAMING HEALTH: KnownNonStreaming {nonStream.Value / 1048576.0:F0} MB, " +
                        $"RealAvailableStreaming {realAvail.Value / 1048576.0:F0} MB, " +
                        $"Missing {(missing ?? 0) / 1048576.0:F0} MB. " +
                        (realAvail.Value < 0
                            ? "NEGATIVE — the streaming pool is clamped to its floor and THRASHING: evict, " +
                              "re-need, re-fetch. That is the LOD cycling and the frame hitching, both views, " +
                              "one pool. Baseline on a fresh session was +6505 MB."
                            : "positive — the pool has real room. Watch this across hot reloads: it fell " +
                              "~1.1 GB per reload before the bootstrap park."));

        // THE FETCH QUEUE — the one instrument that can join all three symptoms.
        //
        // The user's report is that the residual foliage popping happens IN SYNC with the
        // main world's object LOD cycling, and separately that world loads block the engine's
        // EndOfLoadingFence. Those are three symptoms of one thing if the shared texture
        // fetch pipeline is not keeping up, and everything measured so far points that way:
        // Missing sits at 1.1-1.7 GB for minutes while RealAvailableStreaming reports 3.5-6.7
        // GB free, so the pool has room and the FETCH is what is not happening.
        //
        // Where the fetch actually parks, from the IL: DirectStorage.WaitFence awaits
        // Task.SetLifetime(CoreSystems.RenderLifetime, ...) BEFORE it creates its D3D fence,
        // and DirectStorage owns a `ContinuationQueue _moveToRenderThread` — so texture load
        // continuations are resumed ON THE RENDER THREAD. That is the same thread our nested
        // Draw occupies for ~13% of wall clock and adds ~8 ms of GPU work to every frame.
        //
        // A queue depth that is persistently non-zero means continuations are waiting for a
        // render thread that is busy being us. A depth of zero means the render thread is
        // keeping up and the stall is upstream (the disk — and D: is the machine's slowest,
        // shared with SE2's own streaming), which sends the next move somewhere completely
        // different. Either answer is worth more than another guess.
        try
        {
            var core = _coreType ??= Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var ds = core?.GetField("DirectStorage", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var q = ds?.GetType().GetField("_moveToRenderThread", Any)?.GetValue(ds);
            var cnt = q?.GetType().GetProperty("Count", Any)?.GetValue(q);
            if (cnt is int depth)
            {
                if (depth > _dsQueueMax) _dsQueueMax = depth;
                RttLog.Line($"FETCH QUEUE: DirectStorage._moveToRenderThread has {depth} continuation(s) " +
                            $"waiting (peak {_dsQueueMax} this session). " +
                            (depth > 0
                                ? "NON-ZERO: texture loads are parked waiting to resume ON THE RENDER THREAD — " +
                                  "the thread our nested Draw occupies. That would make our second render the " +
                                  "cause of Missing never draining, and therefore of the LOD cycling in BOTH " +
                                  "views and the loading-fence blocks. Test it by dropping wholeSceneIntervalMs " +
                                  "and watching whether this drains."
                                : "ZERO: the render thread is keeping up with the continuations, so the stall is " +
                                  "UPSTREAM of it — the disk, not us. D: is a DRAM-less SATA SSD shared with " +
                                  "SE2's own DirectStorage reads."));
            }
            else if (!_dsShapeLogged)
            {
                _dsShapeLogged = true;
                RttLog.Line($"FETCH QUEUE: shape not found — DirectStorage={(ds == null ? "NULL" : "ok")}, " +
                            $"_moveToRenderThread={(q == null ? "NOT FOUND" : "ok")}. No reading available.");
            }
        }
        catch (Exception e) { RttLog.Error("fetch queue probe", e); }

        RttLog.Line($"STREAMING BUDGET: over {_totalSamples} sample(s) — " +
                    $"AvailableRaw {_rawMin / 1048576.0:F0}..{_rawMax / 1048576.0:F0} MB (swing {rawSwing / 1048576.0:F0}), " +
                    $"AvailableFiltered {_filtMin / 1048576.0:F0}..{_filtMax / 1048576.0:F0} MB (swing {filtSwing / 1048576.0:F0}); " +
                    $"filter LATCHED below its input on {pct:F1}% of samples. " +
                    (pct > 5.0
                        ? "LATCHING CONFIRMED: WindowHisteresisFilter.ComputeMinimum is holding a low-water " +
                          "budget, which downgrades textures/LOD GLOBALLY for both views and then re-streams " +
                          "on release — the LOD cycling AND the hitch, one cause."
                        : filtSwing > 64 * 1048576.0
                            ? "The filtered budget SWINGS but is not latching by this threshold — the " +
                              "oscillation is real, so tighten the test rather than dismissing it."
                            : "No latching and little swing: the budget is steady, and this theory does NOT " +
                              "explain the cycling. Look elsewhere."));

        _rawMin = float.MaxValue; _rawMax = 0; _filtMin = float.MaxValue; _filtMax = 0;
        _latchedSamples = 0; _totalSamples = 0;
    }

    private static void GrassProbe(object sceneDrawSystem)
    {
        var now = Environment.TickCount64;
        if (now - _grassProbeTicks < 15000) return;
        _grassProbeTicks = now;

        try
        {
            var core = _coreType ?? Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            if (core == null) { RttLog.Line("GRASS PROBE: CoreSystems not resolvable."); return; }

            var settings = core.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var grass = settings?.GetType().GetProperty("Grass", Any)?.GetValue(settings);
            string gs = "Grass settings UNREADABLE";
            if (grass != null)
            {
                var gt = grass.GetType();
                object F(string n) => gt.GetField(n, Any)?.GetValue(grass);
                gs = $"Enabled={F("Enabled")} DrawDistance={F("DrawDistance")} Density={F("Density")} " +
                     $"MaxInclination={F("MaxInclination")} AngleCull={F("AngleCullingThreshold")}";
            }

            // Is3DMapEnabled is an INSTANCE property on the shared SceneDrawSystem, so it is
            // the player's map state, not ours — and it hard-gates grass for both passes.
            object map = null;
            try { map = sceneDrawSystem?.GetType().GetProperty("Is3DMapEnabled", Any)?.GetValue(sceneDrawSystem); }
            catch { }

            var dc = core.GetField("DrawContexts", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            string ctx = "DrawContexts NULL";
            if (dc != null)
            {
                var dct = dc.GetType();
                var gbc = dct.GetProperty("GrassBufferContext", Any)?.GetValue(dc);
                var cull = dct.GetProperty("MainViewCulling", Any)?.GetValue(dc);
                var proxies = cull?.GetType().GetProperty("EntityProxies", Any)?.GetValue(cull);

                // Count is not a guaranteed member name across versions, so try the usual
                // suspects and SAY which one answered rather than silently reporting 0 —
                // a blind reader printing zero is the exact failure this project hit
                // yesterday with the grass census.
                string pc = "no count member found (reader is blind, not the set)";
                if (proxies != null)
                {
                    var pt = proxies.GetType();
                    foreach (var n in new[] { "Count", "ProxyCount", "Length", "Size", "_count" })
                    {
                        var v = pt.GetProperty(n, Any)?.GetValue(proxies) ?? pt.GetField(n, Any)?.GetValue(proxies);
                        if (v != null) { pc = $"{n}={v}"; break; }
                    }

                    // THE SET THE WHOLE GRASS QUESTION TURNS ON, finally readable.
                    //
                    // EntityProxyContext has NO CPU-side count — the visible-entity tally lives
                    // in CounterBuffer, on the GPU, which is exactly why every "Count"-style
                    // probe above came back blind and why this link stayed unmeasured while the
                    // bootstrap comment named it as the cause of "no grass appears AT ALL".
                    //
                    // _outputRanges IS on the CPU: a BufferRange[] of {Start, Count}. It is the
                    // allocation the culling job writes its survivors into, so a total of 0 means
                    // this context has no room for proxies at all and RenderGrass would be
                    // generating from nothing. Non-zero does NOT prove the set was FILLED this
                    // frame — it is capacity, not occupancy — so it is reported as ranges, not
                    // as a visible count, and the census below is what settles occupancy.
                    try
                    {
                        if (pt.GetField("_outputRanges", Any)?.GetValue(proxies) is Array ranges)
                        {
                            int total = 0, nonEmpty = 0;
                            for (int i = 0; i < ranges.Length; i++)
                            {
                                var r = ranges.GetValue(i);
                                if (r == null) continue;
                                var cf = r.GetType().GetField("Count", Any);
                                if (cf?.GetValue(r) is int c) { total += c; if (c > 0) nonEmpty++; }
                            }
                            pc += $" outputRanges[{ranges.Length} range(s), {nonEmpty} non-empty, {total} slot(s) total]" +
                                  (total == 0 ? "  <-- ZERO CAPACITY: nothing can be generated from this set" : "");
                        }
                    }
                    catch { }
                    if (!_grassProbeShapeLogged)
                    {
                        _grassProbeShapeLogged = true;
                        RttLog.Line($"GRASS PROBE shape: EntityProxies is {pt.FullName}; members = " +
                                    string.Join(", ", pt.GetProperties(Any).Select(p => p.Name).Take(20)));
                    }
                }
                // INSIDE the GrassBufferContext, not merely that one exists. RenderGrass calls
                // Borrow() on it before anything else and the generator writes its commands and
                // instances into these buffers. We construct the DrawContextManager ourselves,
                // so if this context is never SIZED the whole pass runs, costs nothing
                // measurable, and produces no grass — which fits every observation, including
                // the flat ourDraw when the HiZ path was switched.
                //
                // A capacity of 0 or a null instances buffer is the answer. Reporting the type
                // name alone (what this used to do) could never have distinguished "present"
                // from "present and empty".
                string gbcDetail = "NULL";
                if (gbc != null)
                {
                    var gt2 = gbc.GetType();
                    object G(string n) => gt2.GetField(n, Any)?.GetValue(gbc)
                                       ?? gt2.GetProperty(n, Any)?.GetValue(gbc);
                    var inst = G("GrassInstancesBuffer");
                    var cmds = G("TriplanarGenerationCommandsSingle");
                    gbcDetail = $"borrowed={G("_isBorrowed")} " +
                                $"genCmdCapSingle={G("_foliageInstanceGenCommandsBufferCapacitySingle")} " +
                                $"genCmdCapMulti={G("_foliageInstanceGenCommandsBufferCapacityMulti")} " +
                                $"instancesBuffer={(inst == null ? "NULL" : inst.GetType().Name)} " +
                                $"genCmdsSingle={(cmds == null ? "NULL" : cmds.GetType().Name)}";
                }
                ctx = $"GrassBufferContext[{gbcDetail}] " +
                      $"MainViewCulling={(cull == null ? "NULL" : "present")} EntityProxies[{pc}]";
            }

            // Flora read BACK from the engine, not echoed from our config. The scatter knobs
            // are set through reflection into a boxed struct, where a silent bind failure and
            // a real change look identical from the config side — only a read-back tells them
            // apart. Printed next to grass because they are one control surface with one
            // question behind it: how much is out there, and how far out.
            // Parallax read back the same way, and for the same reason as flora: this is the
            // "close-up ground is flat" candidate, and the FIRST thing worth knowing is
            // whether the engine already has it ON. If it does, parallax is not the missing
            // piece and the flatness is somewhere else — a negative that is worth more here
            // than a knob turned hopefully.
            var parallax = settings?.GetType().GetProperty("Parallax", Any)?.GetValue(settings);
            string ps = "Parallax UNREADABLE";
            if (parallax != null)
            {
                var pt2 = parallax.GetType();
                object P(string n) => pt2.GetField(n, Any)?.GetValue(parallax);
                ps = $"Enabled={P("Enabled")} Fadeout={P("FadeoutDistance")} " +
                     $"SelfShadow={P("EnableSelfShadow")} ShadowMax={P("ShadowMaxLength")} " +
                     $"MaxSteps={P("MaxStepCount")}";
            }

            RttLog.Line($"GRASS PROBE (inside our pass): {gs} | Is3DMapEnabled={map} | {ctx} " +
                        $"| Flora[{ScatterControl.Describe()}] | Parallax[{ps}]");
        }
        catch (Exception e) { RttLog.Line("GRASS PROBE failed: " + e.Message); }
    }

    // PER-FEED (phase C1a): 0 untried, 1 observed, -1 unavailable. This is the route's
    // health, and per-feed is the graceful-cut contract (goal 7): one feed faulting
    // must mark ITSELF unavailable and leave the others rendering.
    private static int _state
    { get => Feeds.Cur.RouteState; set => Feeds.Cur.RouteState = value; }

    private static long _lastLogMs;
    private static int _hookCount;
    private static bool _describedTarget;

    // Our own screen buffers. NOT the engine's with textures swapped inside it, which is
    // what CameraRender does today — a whole second instance. ScreenBuffers has a public
    // parameterless constructor, and it owns depth, the GBuffer array, the final LDR
    // texture and the pre-upscale resolution, so owning one separates most of the
    // per-view state in a single move.
    // PER-FEED (phase C1a). Rule 25 governs every object below: our teardown may
    // dispose only what THIS instance allocated.
    private static object _ourScreenBuffers
    { get => Feeds.Cur.OurScreenBuffers; set => Feeds.Cur.OurScreenBuffers = value; }
    private static bool _sbBuilt
    { get => Feeds.Cur.SbBuilt; set => Feeds.Cur.SbBuilt = value; }

    // Our own DrawContextManager — the OTHER global family, and the one the stage
    // bisect pointed at by elimination.
    //
    // With every skippable stage suppressed the player's indirect-lighting flashing
    // persisted, so the cause sits in what remains: ScenePreparation, RenderMainView
    // and ExecuteLighting. All of those cull, range and read through
    // CoreSystems.DrawContexts — visibility lists, occlusion contexts, geometry
    // buffers, the shared GPU counters ScenePreparation ReadCurrent/ClearCurrent's
    // every frame, and LODTransitions. We own a second ScreenBuffers, but this entire
    // family was still shared, and the experimental branch already recorded its exact
    // signature: a second cull writing the engine's visibility lists made the player's
    // ship lights go bright and flicker.
    //
    // This was on the critical path anyway: culling from the ORBIT camera (stage 3b)
    // into the engine's contexts would corrupt the player's view far worse than the
    // current same-camera perturbation does. Fixing the flashing and unblocking the
    // camera swap are the same edit.
    //
    // DrawContextManager..ctor() is public, parameterless, and calls
    // CreateInitialContexts itself — so construction is one Activator call, exactly
    // like ScreenBuffers.
    private static object _ourDrawContexts
    { get => Feeds.Cur.OurDrawContexts; set => Feeds.Cur.OurDrawContexts = value; }

    // The fresh objects our manager's ctor made, kept so the dispose swap puts each
    // side's own back before anything is released.
    private static object _ourFreshShadowResources
    { get => Feeds.Cur.OurFreshShadowResources; set => Feeds.Cur.OurFreshShadowResources = value; }
    private static object _ourFreshFlares
    { get => Feeds.Cur.OurFreshFlares; set => Feeds.Cur.OurFreshFlares = value; }

    private static bool _dcBuilt
    { get => Feeds.Cur.DcBuilt; set => Feeds.Cur.DcBuilt = value; }
    private static object _panelSourceTex
    { get => Feeds.Cur.PanelSourceTex; set => Feeds.Cur.PanelSourceTex = value; }

    private static bool _cbSwapLogged;
    private static int _cbSwapErrs;

    // Our render's finished image, for CameraRender's blit to use as its source.
    //
    // Null whenever the whole-scene image should NOT own the panel: flag off, route
    // errored, buffers not built, or no render completed yet — the blit then falls back
    // to the probe image automatically, which makes wholeSceneToPanel a safe live A/B
    // switch between the two pipelines.
    public static object PanelSource
    {
        get
        {
            // WholeSceneEnabled is checked here, not just at render time. Without it,
            // turning the route off left this returning our last FinalLDRTexture — which
            // kept the probe strip engaged, so the probe pipeline stayed switched off and
            // the panel froze on a stale frame instead of falling back. The claim that
            // the strip is "self-disabling" was only true for a route that had errored,
            // not for one deliberately switched off, which is exactly the case a bisect
            // needs. A frozen picture costs a test round-trip to diagnose.
            if (!FeedConfig.WholeSceneEnabled || !FeedConfig.WholeSceneToPanel
                || _state != 1 || _ourScreenBuffers == null || _renderCount == 0)
                return null;
            try
            {
                _panelSourceTex ??= _ourScreenBuffers.GetType()
                    .GetProperty("FinalLDRTexture", Any)?.GetValue(_ourScreenBuffers);
                return _panelSourceTex;
            }
            catch { return null; }
        }
    }

    // Set when Reset() is asked to run while our render is on the stack. Drained in
    // RunSecondRender's finally, once the swaps have been unwound.
    private static bool _resetPending;
    private static bool _deferLogged;

    public static void Reset()
    {
        // ==================================================================
        // NEVER TEAR DOWN WHILE OUR RENDER IS ON THE STACK.
        // ==================================================================
        //
        // FeedConfig.Poll() runs from the camera pass, which is INSIDE the player's Draw
        // and therefore inside our nested render. A config change that alters the rebuild
        // signature calls Reset() from there — and Reset() nulls the very statics the
        // in-flight render's `finally` blocks need to unwind their swaps.
        //
        // CONFIRMED, and it cost two device removals on 2026-07-30 (flipping
        // wholeSceneOwnProbes live). The mod's own log caught the real cause the second
        // time, after the first diagnosis — "disposing GPU textures mid-frame" — turned out
        // to be a plausible story about the wrong line:
        //
        //     ERROR restore probe manager: NullReferenceException at RunSecondRender:928
        //     ERROR ... at WholeSceneRender.RestoreScoped() line 1118
        //
        // Reset() nulled _probeField, so the finally's `_probeField.SetValue(null, saved)`
        // threw — and the ENGINE'S EnvironmentProbeManager was never put back. OUR manager
        // stayed installed in CoreSystems, its textures were then released, and the
        // player's next culling pass bound null: DRED EventStack [CullingProxies,
        // MainViewCulling[FirstPass]], PageFaultVA 0x0, zero existing and zero freed.
        // RestoreScoped() failed identically on _settingsObj, which would have left the
        // player's settings scoped to OUR values as well.
        //
        // This was never a probe bug. It is EVERY finally block in the render, and it has
        // been latent for as long as Poll() has been called from inside the render — the
        // probe flip is simply the first change big enough to make it fatal rather than
        // cosmetic. Deferring is the correct fix precisely because it protects all of them
        // at once, rather than hardening one restore path and leaving the rest.
        if (_inOurRender)
        {
            _resetPending = true;
            if (!_deferLogged)
            {
                _deferLogged = true;
                RttLog.Line("Whole-scene: Reset requested from INSIDE our render — deferred to the " +
                            "end of this render. Tearing down here would null the statics the " +
                            "in-flight finally blocks use to restore the engine's ScreenBuffers, " +
                            "DrawContextManager, probe manager and scoped settings, leaving OUR " +
                            "objects installed in the player's frame.");
            }
            return;
        }

        // Do NOT dispose the engine's. Ours is disposable and holds real GPU memory, so
        // dropping it on a hot reload would leak — the pool asserts about exactly that
        // at shutdown, which is what turned every quit into a crash report earlier in
        // this project.
        _panelSourceTex = null;

        // EVERY FAILURE HERE IS LOGGED, and the VRAM is measured across the whole reset.
        //
        // These catches used to be bare `catch { }`. That is how a leak stays invisible:
        // our DrawContextManager owns a full cascade set (hundreds of MB at the player's
        // shadow settings) plus visibility, occlusion and geometry buffers ranged to the
        // whole scene, and the logic assembly reloads on every build. A Dispose that
        // throws once per reload silently costs a cascade set each time, and the only
        // symptom is the frame rate falling apart twenty minutes later when the residency
        // set goes over budget. Ask the question at the moment it can be answered.
        // ONLY when there is something to release.
        //
        // Reading VRAM touches a static field on CoreSystems, and touching a static field
        // FORCES that type's static constructor. Reset() is called from
        // LogicEntry.Install(), which runs on plugin load — five seconds before
        // Render12EngineComponent.Init loads the engine's configurations. CoreSystems's
        // cctor reads DeterministicRuntimeConfiguration, so forcing it that early threw
        // ConfigurationNotFoundException, .NET marked the type permanently failed, and
        // every later touch got TypeInitializationException. Crash on game load, and the
        // stack trace named the engine rather than us.
        //
        // Nothing has been built at Install() time, so gating on that is both the fix and
        // the honest condition: a teardown with nothing to tear down should not be
        // reaching into the engine at all.
        bool haveResources = _ourScreenBuffers != null || _ourDrawContexts != null;
        long vramBefore = haveResources ? Perf.SampleVramMb() : 0;

        // PUT THE ENGINE'S OWN FinalLDR BACK BEFORE DISPOSING (2026-08-01).
        //
        // We swap in a feed-sized buffer of our own (see EnsureOwnFinalLdr). ScreenBuffers
        // created the original and its Dispose is what should release it — so if we left ours
        // installed, Dispose would free OUR pooled borrow and leak the engine's
        // swapchain-sized texture, once per gate cycle. Same reasoning as the shadow
        // resources restored a few lines below.
        RestoreEngineFinalLdr();

        if (_ourScreenBuffers is IDisposable d)
        {
            try { d.Dispose(); }
            catch (Exception e) { RttLog.Error("whole-scene Reset: dispose our ScreenBuffers LEAKED", e); }
        }
        _ourScreenBuffers = null;
        _sbBuilt = false;
        if (_ourDrawContexts != null)
        {
            // Put OUR fresh shadow resources back before disposing, so Dispose releases
            // what we created rather than the engine's live object.
            try
            {
                if (_ourFreshShadowResources != null)
                    _ourDrawContexts.GetType().GetProperty("DirectionalLightShadowResources", Any)
                        ?.SetValue(_ourDrawContexts, _ourFreshShadowResources);
            }
            catch (Exception e) { RttLog.Error("whole-scene Reset: restore our shadow resources", e); }

            // Same dispose-safety for the flares context: our manager's Dispose would
            // otherwise dispose the ENGINE'S live one.
            try
            {
                if (_ourFreshFlares != null)
                    _ourDrawContexts.GetType().GetProperty("LensFlares", Any)
                        ?.SetValue(_ourDrawContexts, _ourFreshFlares);
            }
            catch (Exception e) { RttLog.Error("whole-scene Reset: restore our flares context", e); }

            // And the FIELD-level version of the same hazard, which the context-level
            // restore above cannot see: with wholeSceneOwnFlares the mirrored DEFINITION
            // members inside _ourFreshFlares are the ENGINE'S objects, and FlaresContext.
            // Dispose reaches all of them — it disposes _flaresBuffer (null-checked, but the
            // mirror made it non-null), iterates _flaresByGuid calling
            // _flareDefinitionsAllocator.Free per entry, and walks _texturePinsByGuid.
            // Two CTDs on 2026-07-29 came from exactly this: the teardown freed the
            // player's flare buffer and the engine's next flare pass dereferenced it.
            // Restoring the ctor originals (captured at first mirror) makes Dispose release
            // precisely what our context allocated and nothing else.
            ScrubMirroredFlareRefs();

            if (_ourDrawContexts is IDisposable dc)
            {
                try { dc.Dispose(); }
                catch (Exception e) { RttLog.Error("whole-scene Reset: dispose our DrawContextManager LEAKED " +
                                                   "(cascade set + scene-sized culling buffers)", e); }
            }
            else RttLog.Line("Whole-scene Reset: our DrawContextManager is NOT IDisposable — " +
                             "everything it owns leaks on every reload.");
        }

        long vramAfter = haveResources ? Perf.SampleVramMb() : 0;
        if (vramBefore > 0 && vramAfter > 0)
            RttLog.Line($"Whole-scene Reset: VRAM {vramBefore} MB -> {vramAfter} MB " +
                        $"({vramAfter - vramBefore:+#;-#;0} MB). Freeing should show a NEGATIVE delta; " +
                        "a flat or positive one across repeated reloads is the leak.");
        _ourDrawContexts = null;
        _ourFreshShadowResources = null;
        _ourFreshFlares = null;
        // Same class of latch as everything else cleared here: a surviving _engineFlares
        // would point at a disposed context after a rebuild, and a surviving
        // _flareMirrorLogged would swallow the log line that proves the mirror took.
        _engineFlares = null; _flareMirrorLogged = false; _flaresReady = false;
        _flareOriginals = null;   // belt-and-braces: scrub self-clears, but a stale capture
                                  // applied to a NEW context would write another context's
                                  // objects into it, which is this same bug wearing a hat

        // OUR PROBE MANAGER — KEPT. NOT DISPOSED, NOT QUEUED, NOT HANDED ANYWHERE.
        //
        // THREE device removals bought this one sentence, on 2026-07-30, all from flipping
        // wholeSceneOwnProbes on a live feed, and all with the same DRED breadcrumb:
        // EventStack [CullingProxies, MainViewCulling[FirstPass], ScenePreparation +
        // Render], PageFaultVA 0x0, zero existing AND zero freed allocations — a NULL BIND
        // in the PLAYER'S culling pass. Steady-state own-probes had already soaked an hour
        // clean before any of them, so the feature was never the problem; the teardown was.
        //
        //   Attempt 1 — dispose here. Reset() runs from FeedConfig.Poll on the RENDER
        //     THREAD, inside the player's frame. Freeing GPU textures mid-frame is the
        //     fault family this project has recorded more than any other.
        //   Attempt 2 — defer it to the LCD tick, off the render thread. Same crash. It
        //     did fix a real and SEPARATE bug (the NRE that left our manager installed
        //     after Reset nulled the finally blocks' statics — that fix is kept, see the
        //     deferred-Reset guard at the top of this method), which is why attempt 3
        //     arrived with a clean mod log and nothing to blame.
        //   Attempt 3 — same crash again, and the conclusion: OFF THE RENDER THREAD IS NOT
        //     THE SAME AS OUTSIDE A FRAME. The LCD tick runs while the render thread is
        //     rendering. There is no safe moment to free these while the renderer is live.
        //
        // So it is simply kept, and keeping it costs nothing structural: the manager is
        // independent of the ScreenBuffers and DrawContextManager this Reset rebuilds, its
        // textures are sized by ProbeSettings rather than by our resolution, and
        // constructing it was always free. Turning the feature off just stops InstallProbes
        // swapping it in. WholeSceneOwnProbes is out of the rebuild signature too, so
        // flipping it no longer reaches this path at all.
        //
        // ==================================================================
        // ...AND "KEPT" WAS A LIE ACROSS HOT RELOADS. CTD 2026-07-30 18:46.
        // ==================================================================
        //
        // This comment used to end: "eight cube textures stay resident until the game
        // restarts. That is VRAM, not correctness." BOTH HALVES WERE WRONG, and the game
        // died proving it:
        //
        //     Assertion Failure: Out of the descriptor heap
        //       at DescriptorHeapPool.BorrowRTV()
        //       at RenderTargetCubeTexture.FaceMips.Initialize()
        //       at EnvironmentProbeManager.RecreateProbes()
        //       at WholeSceneRender.InstallProbes()
        //     [Watchdog]: application froze, RenderThreadFreeze. Capturing dump.
        //
        // WRONG #1 — it is not VRAM, it is RTV DESCRIPTORS. Those live in a small fixed
        // pool and exhaust long before memory does. VRAM sat flat at 12.2 GB for the whole
        // session while this accumulated, which is precisely why every instrument we had
        // looked healthy right up to the crash.
        //
        // WRONG #2 — it is not "until the game restarts", it is ONCE PER HOT RELOAD. The
        // logic assembly is COLLECTIBLE. _ourProbes lives in it (on FeedInstance since
        // C1a, which changes nothing here — that static is replaced either way), so every
        // reload starts null, the ??= below builds a FRESH manager, and the previous one is
        // unreachable from any code that could free it. Not disposing it was a deliberate
        // choice; losing the reference to it was not. Four reloads in one session was
        // enough to run the pool dry.
        //
        // The reasoning error is worth naming: "we must not dispose this" was established
        // by three device removals and is still correct. "Therefore it costs nothing to
        // keep" did not follow, and was never tested — it is an assumption that rode in on
        // the back of a well-evidenced conclusion. A fix's PROVEN part does not confer
        // confidence on the sentence next to it.
        //
        // THE FIX: park the manager in the BOOTSTRAP assembly, which is not collectible and
        // survives logic reloads, so the reference outlives the code that made it and
        // "kept" finally means kept — one manager per process, created once, never orphaned.
        // Disposal stays forbidden; this is about not LOSING it, not about freeing it.
        //
        // If the memory ever genuinely needs reclaiming, the only defensible place remains a
        // QUIESCED renderer — gate shutdown with the feed already dormant — not "some other
        // thread".
        _probeState = 0; _probeLogged = false;
        _dcBuilt = false;
        // The failure budget resets with the thing it was counting. A gate cycle IS the
        // documented retry for a feed that gave up, so leaving this at 3 would make that
        // retry a no-op.
        _dcFailures = 0;
        _dcField = null;
        _cascFld = _charCascFld = null;
        _ownShadowsLogged = _cascadeSettingsLogged = false;
        _planetEnvGroup = null;
        _fPeFrustum = _fPeSetupsData = _fPeSetupsCbs = _fPeFirst = _fPeFirstData = null;
        _fPeSpheres = _fPeSpheresData = null;
        _miPeFillSetups = _miPeFillSlim = _miPeSetMatrix = _miPeCreateCb = null;
        _pPeModifiersCtx = null;
        _peBufMgr = null;
        _peSaved = null;
        _peCbsAreOurs = false;
        // CLEARED, NOT DISPOSED. Reset can arrive on a thread that is not the render
        // thread, and the engine asserts on exactly that ('_renderThread == null ||
        // _renderThread == _mainThread || _renderThread != Thread.CurrentThread' — it fired
        // 4 times last session). Dropping at most two renders' worth of buffers is bounded
        // and rare; a cross-thread free is neither.
        _cbStaged.Clear();
        _cbPrev.Clear();
        _planetEnvState = 0;
        _planetEnvLogged = _planetEnvCountsLogged = _peEmptyLogged = false;
        // Rearm() cleared these and Reset() did not, which is backwards: Reset is the
        // heavier path, taken precisely when the configuration changed.
        _scopeWarned.Clear();
        _skippedLogged.Clear();
        _state = 0;
        _hookCount = 0;
        _pumpErrLogs = 0;
        _lastLogMs = 0;
        _describedTarget = false;
        _inOurRender = false;
        _miDraw = null;
        _coreType = null;
        _sbField = _rvField = null;
        _settingsObj = null;
        _lastRenderMs = 0;
        _lastLdrRes = null;
        _ldrResized = false;
        _earlyRan = _earlyOwnsThisFrame = false;
        _renderCount = 0;

        // Arm the settle window. Reset is exactly the event that forces the shared probe
        // manager to reprocess, so this is where the countdown belongs — it then covers a
        // config save, a hot reload and a gate restart alike, not just the one case that
        // happened to crash.
        _settleFrames = SettleFrames;

        // DO NOT force a panel rebind from here. Tried 2026-07-29 and it CRASHED THE GAME:
        // the panel showed its test pattern (a fresh target, rebound before any feed frame
        // had landed) and then the process died.
        //
        // Why it is unsafe: this Reset runs from FeedConfig.Poll on the RENDER THREAD, and
        // clearing PanelBinding._bound makes the next panel tick call
        // SetNewScreenMaterialHandle — which is ReleaseScreenMaterialHandle plus
        // CreateRuntimeLcdMaterial, i.e. destroying and building a runtime material from
        // inside the frame. That is the same family as every other "create engine resources
        // mid-frame" fault this project has recorded (Rule 11).
        //
        // The freeze it was meant to fix is real but NOT diagnosed — the causal link to
        // this Reset was inferred, never proven, and the crash suggests the inference was
        // wrong. A gate cycle clears it; that is the workaround until it is understood.
        // Suspects to test properly: whether BlitProbe.FeedTarget actually changes across a
        // WholeSceneRender.Reset (it should not — the panel binds to that, not to our
        // ScreenBuffers), and whether CameraRender's cached _feedTexture/_resolvedPanelId
        // go stale independently.
    }

    // Clear the one-strike disable after a config change, WITHOUT throwing away the
    // second ScreenBuffers.
    //
    // RunSecondRender latches _state = -1 on any exception, which is right — a
    // whole-frame render that faults should not retry five times a second while the log
    // is being read. But it made every experiment cost a rebuild, because only a
    // resolution change called Reset(). Re-arming on any whole-scene config edit keeps
    // the safety and returns the fast iteration loop.
    //
    // Deliberately NOT Reset(): that disposes the ScreenBuffers, and rebuilding a full
    // set of render targets to retry a flag change is pure waste.
    public static void Rearm()
    {
        if (_state == -1)
        {
            _state = _describedTarget ? 1 : 0;
            RttLog.Line("Whole-scene: re-armed after a config change (the route had disabled itself " +
                        "on an error). Buffers kept.");
        }
        _skippedLogged.Clear();
        _scopeWarned.Clear();
    }

    // Should a Draw sub-stage be skipped right now?
    //
    // TRUE only while OUR render is running. The engine's own frame must always get
    // every stage — this suppresses work in the second render, not in the game.
    //
    // Settings flags could not reach all of these. ExecuteAccelerationStructuresBuilding
    // is called unconditionally at the top of Draw and checks only
    // EnableGPUParallelization, so clearing RaytracingSettings.Enabled never stopped it:
    // we rebuilt the raytracing acceleration structures on every second render, and
    // RayTracingSceneManager.CreateTLAS is camera-dependent and world-space shared. That
    // is the leak that survived three rounds of settings scoping.
    //
    //   0 ExecuteAccelerationStructuresBuilding    raytracing scene / TLAS
    //   1 ExecuteRaytracingPrepareAndSceneFinalize raytracing prepare
    //   2 RenderEnvironmentProbe                   shared probe atlas (ambient + reflections)
    //   3 RenderShadows                            shadow cascades
    //   4 ComputeExposure                          auto-exposure history
    //   5 UpdateSurfels                            water surfels
    // The culling-view classifier — consulted from the bootstrap's DoWork prefixes on the
    // culling jobs, on WHATEVER thread the job system runs them. Argument-derived.
    //
    // v2.1: RenderViewSlim carries NO resolution (the v2 bind confessed and died on that —
    // its fields are ViewD/InvViewD/Projection/CullingFarPlane, verified by IL). The view
    // is fingerprinted from what it does carry, three terms that are jointly unique to the
    // feed among every view this session composes:
    //   PERSPECTIVE   Projection.M34 != 0      excludes the ORTHO shadow cascades
    //   SQUARE        M11 == M22 (within 1e-3) excludes the player's 16:9 main view
    //                                          (measured: ours 1.0913/1.0913, player
    //                                          0.6139/1.0913)
    //   FAR == ours   CullingFarPlane ~= wholeSceneFarClip (2500) excludes square-
    //                                          perspective LOCAL-LIGHT shadow views, whose
    //                                          far planes are light radii
    // And v2.1 carries TELEMETRY — calls and fired-true counts — because "the lever is
    // engaged" must be a number after v2 shipped inert and nearly buried the theory.
    private static FieldInfo _fRvsProjection, _fRvsFarPlane, _fMatM11, _fMatM22, _fMatM34;
    private static int _cullClassifierState;    // 0 untried, 1 ok, -1 unbindable
    private static long _cullClassCalls, _cullClassOurs;

    public static bool CullingViewIsOurs(object boxedViewSlim)
    {
        if (!FeedConfig.WholeSceneNoOcclusion || boxedViewSlim == null) return false;
        try
        {
            if (_cullClassifierState == -1) return false;
            _cullClassCalls++;
            var vt = boxedViewSlim.GetType();
            if (_cullClassifierState == 0)
            {
                _fRvsProjection = vt.GetField("Projection", Any);
                _fRvsFarPlane = vt.GetField("CullingFarPlane", Any);
                var mt = _fRvsProjection?.FieldType;
                _fMatM11 = mt?.GetField("M11", Any);
                _fMatM22 = mt?.GetField("M22", Any);
                _fMatM34 = mt?.GetField("M34", Any);
                bool ok = _fRvsProjection != null && _fRvsFarPlane != null
                       && _fMatM11 != null && _fMatM22 != null && _fMatM34 != null;
                _cullClassifierState = ok ? 1 : -1;
                RttLog.Line(ok
                    ? "Culling-view classifier v2.1 BOUND: Projection(M11/M22/M34) + CullingFarPlane " +
                      "readable — fingerprint is perspective AND square AND far==wholeSceneFarClip."
                    : "Culling-view classifier v2.1 UNBINDABLE — wholeSceneNoOcclusion will have NO " +
                      "EFFECT (and this zero is the reason). Fields missing on RenderViewSlim/Matrix.");
                if (!ok) return false;
            }

            var proj = _fRvsProjection.GetValue(boxedViewSlim);
            if (proj == null) return false;
            if (_fMatM11.GetValue(proj) is not float m11 ||
                _fMatM22.GetValue(proj) is not float m22 ||
                _fMatM34.GetValue(proj) is not float m34) return false;
            if (_fRvsFarPlane.GetValue(boxedViewSlim) is not float far) return false;

            bool ours = Math.Abs(m34) > 1e-6f                                   // perspective
                     && Math.Abs(m11 - m22) < 1e-3f                             // square
                     && Math.Abs(far - (float)FeedConfig.WholeSceneFarClip) < 1.0f;
            if (ours) _cullClassOurs++;
            return ours;
        }
        catch { return false; }
    }

    private static long _lastCullCalls, _lastCullOurs;

    internal static string CullClassifierText()
    {
        var dc = _cullClassCalls - _lastCullCalls; _lastCullCalls = _cullClassCalls;
        var doo = _cullClassOurs - _lastCullOurs; _lastCullOurs = _cullClassOurs;
        if (_cullClassifierState == -1) return "noOcclusion classifier UNBINDABLE (lever inert)";
        if (_cullClassifierState == 0) return "noOcclusion classifier not yet consulted";
        return $"noOcclusion classifier: {dc} call(s), {doo} classified OURS this window" +
               (doo == 0 ? " — ZERO: the lever is armed but never firing; the fingerprint is wrong or " +
                           "the feed is not composing" : "");
    }

    public static bool ShouldSkipStage(int id)
    {
        if (!_inOurRender) return false;

        // Stage 3 and wholeSceneOwnShadows are not independent settings. Owning the
        // resources means our DirectionalLightShadowResources holds OUR cascade depth
        // table — and if RenderShadows never runs, nothing is ever drawn into it. That
        // combination is the exact state that NRE'd the shadow-mask draw the first time
        // a fresh manager was installed, which is why the engine's object got shared in
        // the first place. Refuse the skip rather than let a stale skip list produce it.
        var stages = FeedConfig.WholeSceneSkipStages;

        if (id == 3 && FeedConfig.WholeSceneOwnShadows > 0)
        {
            if (Array.IndexOf(stages, 3) >= 0 && _skippedLogged.Add(-3))
                RttLog.Line("Whole-scene: stage 3 (RenderShadows) is in the skip list but " +
                            "wholeSceneOwnShadows is on — running it anyway. Owning the cascades " +
                            "means rendering into them; drop 3 from wholeSceneSkipStages.");
            return false;
        }

        // Stage 2 and wholeSceneOwnProbes — THE PAIR THAT COST 2026-08-05, encoded so it
        // cannot recur. We owned an EnvironmentProbeManager from goal 4.4 and left stage 2
        // (RenderEnvironmentProbe) suppressed, so our probe atlas was never rendered into.
        // Nothing complained: get_CloseIBL returns _closeFinalTexture whenever Enable and
        // ApplyEnvProbe are set, WITHOUT checking that a single face was ever drawn. The feed
        // sampled an unrendered cube for reflections AND ambient while every log line said the
        // manager was installed. Suppression whose justification expired when ownership landed
        // is now its own recognised failure mode — the reason 2 was skipped was a SHARED atlas,
        // and it stopped being shared the moment we owned one.
        if (id == 2 && FeedConfig.WholeSceneOwnProbes)
        {
            if (Array.IndexOf(stages, 2) >= 0 && _skippedLogged.Add(-2))
                RttLog.Line("Whole-scene: stage 2 (RenderEnvironmentProbe) is in the skip list but " +
                            "wholeSceneOwnProbes is on — running it anyway. Owning the probe atlas " +
                            "means rendering into it, and an unrendered atlas fails SILENTLY " +
                            "(CloseIBL never checks); drop 2 from wholeSceneSkipStages.");
            return false;
        }

        // Stage 0 and wholeSceneOwnRayTracingScene, same pairing for the same reason. Stage 0
        // is ExecuteAccelerationStructuresBuilding; it is in the default skip list because
        // RayTracingSceneManager.CreateTLAS is camera-dependent and world-space SHARED, so
        // building it from our camera corrupted the player's. With our own manager installed
        // there is no shared structure to protect, and leaving 0 skipped would hand the feed a
        // TLAS that is allocated and never built — the stage-2 failure again, one subsystem
        // over, and just as silent.
        // STAGE 30 IS GATED ON A KNOB, NOT MERELY ON OWNING THE CACHE. The first version
        // force-ran it whenever the cache was installed, reasoning that a grid we allocate
        // and never populate is the silent stage-2 failure a third time. TRUE — and with an
        // EMPTY TLAS, populating is worse than emptiness: every trace MISSES and a miss
        // shades as SKY, so the cells our camera writes inside a cave hold open-sky
        // irradiance with nothing to occlude it. Confirmed 2026-08-07: blinding ambient in a
        // cave that flipped with small view changes (patchy sparse-grid sampling), while
        // outdoors the same error is invisible because sky ambient outdoors is roughly
        // right.
        //
        // An INSTALLED-but-EMPTY cache is the correct interim state: isolation holds in both
        // directions, the GI term contributes nothing, and ambient falls back to the probe
        // cubes captured at the feed camera — the cheap position-correct ambient the user
        // actually chose. Populate again only when the TLAS has real content.
        if (id == 30 && FeedConfig.WholeSceneOwnIRCache && _irCacheInstalled
                     && FeedConfig.WholeSceneIrCachePopulate)
        {
            if (Array.IndexOf(stages, 30) >= 0 && _skippedLogged.Add(-130))
                RttLog.Line("Whole-scene: stage 30 (RaytracingPrepare -> IRCacheTraceJob) is in the skip " +
                            "list but wholeSceneOwnIRCache is on AND our cache is installed — running it " +
                            "anyway, so the irradiance grid is populated at the FEED camera. The player's " +
                            "grid is untouched because it is a different manager.");
            return false;
        }

        if (id == 0 && FeedConfig.WholeSceneOwnRayTracingScene && _rtSceneInstalled)
        {
            // BUILD-ONCE (perf sprint 2026-08-07). The stage table priced this force-run at
            // 0.39 ms CPU per render — 16 ms/s at every-frame cadence — rebuilding an EMPTY
            // structure whose content cannot change (wholeSceneIrCachePopulate=0 means
            // nothing ever enters it). The coupling exists so an owned TLAS can never be
            // owned-but-NEVER-built (the silent stage-2 failure pattern); built ONCE
            // satisfies that literally. Re-arms whenever the scene is (re)installed —
            // Install/Restore flip _rtSceneInstalled, and the flag resets there — so a
            // rebuild or park cycle gets a fresh build. When the TLAS gains real content
            // someday, gate this skip on wholeSceneIrCachePopulate too: a populated scene
            // needs per-frame refits again.
            if (Feeds.Cur.OwnTlasBuilt)
                return true;    // built already; skip the empty rebuild
            Feeds.Cur.OwnTlasBuilt = true;
            if (Array.IndexOf(stages, 0) >= 0 && _skippedLogged.Add(-100))
                RttLog.Line("Whole-scene: stage 0 (acceleration structures) is in the skip list but " +
                            "wholeSceneOwnRayTracingScene is on AND our scene is installed — running " +
                            "it ONCE, so our TLAS is built (empty) around the FEED camera, then skipped: " +
                            "an empty structure never changes, and rebuilding it cost 0.39 ms per render.");
            return false;
        }

        // Stage 21 and wholeSceneOwnFlares are the same kind of pair. Owning the
        // FlaresContext is pointless if RenderFlares never runs, and the ONLY reason 21 is
        // in the default skip list is that we used to install the ENGINE'S context — where
        // running the pass would have advanced the player's occlusion readback twice a
        // frame. With our own context installed that hazard is gone, so refuse the skip
        // rather than let a stale skip list silently produce "own flares, no flares".
        // Stage 2 and wholeSceneOwnProbes, same pair as 3/ownShadows and 21/ownFlares. Owning
        // a probe manager and filling its queue is pointless if RenderEnvironmentProbe never
        // runs — the queue would just be discarded, and we would pay PrepareProbes and the
        // eight cube textures for nothing. Gated on _probeState, not on the config flag,
        // because that is the lesson stage 21 taught: intent is not readiness.
        if (id == 2 && FeedConfig.WholeSceneOwnProbes && _probeState > 0)
        {
            if (Array.IndexOf(stages, 2) >= 0 && _skippedLogged.Add(-2))
                RttLog.Line("Whole-scene: stage 2 (RenderEnvironmentProbe) is in the skip list but " +
                            "wholeSceneOwnProbes is on and our probe manager is installed — running it " +
                            "anyway. The skip existed because our queue was always empty and the atlas " +
                            "was the player's; both are now ours. Drop 2 from wholeSceneSkipStages.");
            return false;
        }

        // _flaresReady, NOT the config flag. See the comment on _flaresReady: force-running
        // this on intent alone dereferenced a null _flaresBuffer and took the game down.
        if (id == 21 && FeedConfig.WholeSceneOwnFlares && _flaresReady)
        {
            if (Array.IndexOf(stages, 21) >= 0 && _skippedLogged.Add(-21))
                RttLog.Line("Whole-scene: stage 21 (RenderFlares) is in the skip list but " +
                            "wholeSceneOwnFlares is on and our FlaresContext has a definition " +
                            "buffer — running it anyway. The skip existed only because we shared " +
                            "the engine's context; we now own ours, so the readback we advance is " +
                            "our own. Drop 21 from wholeSceneSkipStages.");
            return false;
        }

        // Own-flares requested but our context is not ready. Say so ONCE and fall through to
        // the skip list, which keeps 21 skipped. Silence here is what turned a missing buffer
        // into a crash.
        if (id == 21 && FeedConfig.WholeSceneOwnFlares && !_flaresReady && _skippedLogged.Add(-121))
            RttLog.Line("Whole-scene: wholeSceneOwnFlares is on but our FlaresContext has no " +
                        "_flaresBuffer yet — stage 21 stays SKIPPED this render rather than " +
                        "dereferencing null in GetFlareConstants. Normal for the first renders " +
                        "after a reload or rebuild; if it never clears, the definition mirror is " +
                        "failing and the feed will simply have no flares.");

        for (int i = 0; i < stages.Length; i++)
            if (stages[i] == id)
            {
                if (_skippedLogged.Add(id))
                    RttLog.Line($"Whole-scene: skipping stage {id} ({StageName(id)}) during our render.");
                return true;
            }
        return false;
    }

    private static readonly HashSet<int> _skippedLogged = new();

    private static string StageName(int id) => id switch
    {
        0 => "ExecuteAccelerationStructuresBuilding",
        1 => "ExecuteRaytracingPrepareAndSceneFinalize",
        2 => "RenderEnvironmentProbe",
        3 => "RenderShadows",
        4 => "ComputeExposure",
        5 => "UpdateSurfels",
        6 => "PrepareClusters",
        7 => "ProcessParticles",
        8 => "RenderDecals",
        9 => "ExecuteHBAO",
        10 => "ExecuteLighting",
        11 => "RenderMainView",
        12 => "ComputeDirectionalLighting",
        13 => "ComputeLocalLights",
        14 => "ComputeCloudShadows",
        15 => "UpdateAtmosphere",
        16 => "DrawUI",
        17 => "RaytraceGIJob.DoWork (the ray trace itself; ambient still runs)",
        18 => "ComputeGI (ray trace AND ambient)",
        19 => "UpsamplingJob.PrepareResources (stops us re-preparing FSR at 512 and wiping the player's TAA history)",
        20 => "force IsFSREnabledAndAllowed false for our render (no state change)",
        21 => "RenderFlares (we share the engine's FlaresContext; never advance its readback)",
        25 => "ConstantExposure -> read-only (returns the existing exposure view; stops stamping " +
              "a constant into the player's adaptation history)",
        22 => "CloudShadowJob.DoWork — stop writing the SHARED CloudShadowmap from our camera",
        23 => "CloudWeatherMapJob.DoWork — stop writing the SHARED weather map tables",
        24 => "AtmosphereLUTJob.DoWork — stop writing the SHARED per-planet atmosphere LUTs",
        26 => "CloudJob.DoWork — stop disposing/recreating the SHARED CloudAccumulateLightAlpha " +
              "temporal resource at our half-resolution (confirmed device removal; costs the feed " +
              "volumetric clouds only, not planet atmospheres)",
        27 => "EnvironmentProbeManager.PrepareProbes — stop advancing the SHARED probe state " +
              "machine a second time per frame (confirmed device removal at 30fps; costs nothing, " +
              "stage 2 already discards the queue)",
        28 => "LocalLightsManager.FlushUpdates — stop draining the SHARED local-light shadow " +
              "update queue in our render (the feed uses the player's local-light shadows)",
        31 => "force IsOcclusionCullingAllowed false for our render — single-phase culling, no " +
              "HiZ/two-phase path. The two-phase classifier keys on GPUModelEntity.LastVisibleFrame, " +
              "a per-entity SCENE-WIDE stamp both views write, so an entity visible only to the feed " +
              "oscillates between first-pass and occlusion-tested as the views' counters interleave — " +
              "measured as the distant-flora flashing that survived seven static state layers and an " +
              "execution census of zero. Per-call override, no shared state touched; costs feed overdraw " +
              "at 1024x1024 only",
        29 => "ScreenSpaceReflections.DoWork — THE PHANTOM BLEED. Stop writing our scene's " +
              "radiance into the SHARED SSR temporal history (AverageRadianceHistory / " +
              "VarianceHistory / SampleCountHistory live on SceneDrawSystem._screenSpaceReflectionsJob, " +
              "which we do not swap), so the player's reflective surfaces stop showing the feed",
        _ => "unknown",
    };

    // Fires after the engine has finished the player's frame.
    //
    // THE RENDER-THREAD PUMP (phase C1b). This hook and the prefix below are
    // SCHEDULER-driven: nothing in the engine's call names a feed, so we pick one and scope
    // every piece of per-feed state the frame touches to it. The scope must wrap the WHOLE
    // body, not just the render — ShouldSkipStage is called from deep inside the nested
    // Draw with no arguments identifying a feed, so it can only read the ambient, and the
    // ambient has to still be set when it does.
    // THE RENDER THREAD'S IDENTITY, learned rather than looked up.
    //
    // This hook is a postfix on SceneDrawSystem.Draw, so whatever thread reaches it IS the
    // render thread — that is not an assumption, it is the definition of where we are.
    // Anything that needs to ask "am I on the render thread?" compares against this.
    //
    // The alternative was reflecting RenderThreadManager._renderThread, and that field is
    // per-INSTANCE: finding the live manager is another hunt that can quietly come back
    // null, and a null there would answer "no, you are not on the render thread" — the
    // wrong answer, in the direction that does the unsafe thing. See PanelBinding.Unbind.
    internal static int RenderThreadId;

    private static FieldInfo _reloadReqField, _reloadQuiField;
    private static bool _handoverDone;

    // Returns true when this frame belongs to the handover and must do nothing else.
    //
    // Reached through the bridge by reflection like every other bootstrap field: an older
    // bootstrap without these fields simply never requests, and this returns false forever —
    // which is the pre-handover behaviour, not a crash. A build of the logic that assumed
    // the fields exist would refuse to run against the bootstrap it shipped with.
    private static bool ReloadHandover()
    {
        try
        {
            if (_reloadReqField == null)
            {
                var t = Type.GetType("RttProbe.RttBridge, RttProbe");
                _reloadReqField = t?.GetField("ReloadRequested", BindingFlags.Public | BindingFlags.Static);
                _reloadQuiField = t?.GetField("ReloadQuiesced", BindingFlags.Public | BindingFlags.Static);
                if (_reloadReqField == null)
                {
                    RttLog.Global("Hot-reload handover NOT available on this bootstrap — restart the game to " +
                                  "adopt it. Until then every reload strands a ScreenBuffers and a " +
                                  "DrawContextManager, and about six of those exhaust VRAM.");
                    _reloadQuiField = null;
                    return false;
                }
            }

            if (_reloadReqField.GetValue(null) is not true) { _handoverDone = false; return false; }
            if (_handoverDone) return true;                 // already released; just stop rendering
            _handoverDone = true;

            long before = Perf.SampleVramMb();
            RttLog.Global("=== HOT-RELOAD HANDOVER: the bootstrap is about to unload this assembly. " +
                          "Shutting every feed down from the render thread so its GPU resources are " +
                          "DISPOSED rather than abandoned. ===");

            Feeds.ForEachSlot(() => { try { FeedGate.Shutdown(); } catch (Exception e) { RttLog.Error("handover shutdown", e); } });

            long after = Perf.SampleVramMb();
            RttLog.Global($"=== HOT-RELOAD HANDOVER complete: VRAM {before} MB -> {after} MB " +
                          $"({after - before:+#;-#;0} MB). A NEGATIVE delta is the point; a flat one means " +
                          "something we own is still not being disposed. ===");

            _reloadQuiField?.SetValue(null, true);
            return true;
        }
        catch (Exception e)
        {
            // Never let the handover itself stop the render permanently — confirm and move
            // on, because a bootstrap blocked for 3 s every reload is worse than a leak.
            RttLog.Error("reload handover", e);
            try { _reloadQuiField?.SetValue(null, true); } catch { }
            return true;
        }
    }

    public static void OnWholeScene(object sceneDrawSystem, object finalLdrBuffer)
    {
        if (_inOurRender) return;               // our own nested Draw — do nothing

        RenderThreadId = Environment.CurrentManagedThreadId;

        // THE SCATTER RADIUS HAS TO LAND BEFORE THE FIRST FLORA BATCH IS ALLOCATED.
        //
        // InstanceBatch._cullingDistance is baked ONCE, at allocation, from
        // Flora.RenderingDistanceMultiplier. The only other call to Apply() sits inside the
        // render bracket, which cannot run until the feed has a panel and starts rendering —
        // measured at ~40 s after world load. Everything the world builds in those 40 s bakes
        // at the ENGINE default (10) and everything after bakes at ours (25), so the session
        // runs a permanent 2.5x patchwork and the boundary instances flip as sectors churn.
        // That is the distant-flora flashing, and it meant there was no such thing as a clean
        // load to test against — the "fresh load" control was never controlling anything.
        //
        // Here it runs from the FIRST FRAME, on the render thread, throughout loading. Apply()
        // early-returns once the value is unchanged, so the steady-state cost is one double
        // compare per frame. The in-bracket call stays: it must run before ScopeSharedState so
        // the global survives our pass unwind (see the note there).
        try { ScatterControl.Apply(); } catch { }

        // THE EPISODE WATCHER. DistanceThresholdContainer.FullRefresh re-buckets EVERY
        // entity's distance thresholds in one sweep — the mechanism whose episodic firing
        // would blip the main world's object LODs and blink the feed's impostor-tier
        // foliage TOGETHER, which is exactly the pairing the user keeps observing. It is
        // expected to be RARE, so a per-window rate would hide the timing; this logs a
        // loud, TIMESTAMPED line the moment the counter moves, so "I just saw an episode"
        // can be matched against the log to the second.
        if (Clock.Ms - _lastEpisodeCheck >= 250)
        {
            _lastEpisodeCheck = Clock.Ms;
            try
            {
                if (!_episodeLooked)
                {
                    _episodeLooked = true;
                    _fFullRefresh = Type.GetType("RttProbe.RttBridge, RttProbe")?.GetField("DistanceFullRefreshCalls");
                }
                if (_fFullRefresh?.GetValue(null) is long fr && fr != _lastFullRefresh)
                {
                    var d = fr - _lastFullRefresh;
                    _lastFullRefresh = fr;
                    RttLog.Line($"DISTANCE FULL REFRESH fired (+{d}, cumulative {fr}) — every entity's " +
                                "distance thresholds re-bucketed in one sweep. If the feed's distant " +
                                "foliage blinked and the main world's LODs blipped around this timestamp, " +
                                "this is the episode driver.");
                }

                // THE CONFIRMED CORRELATE (user's "now" = 19:06:38; ssUpdate's first-ever
                // non-zero window = the same window). Each firing is a sub-sector mesh
                // REBUILD — absent mid-rebuild, i.e. one blink of one patch of distant
                // foliage. Timestamped per event so every future blink the user calls out
                // can be matched to the second, and so the TRIGGER (whatever dirtied the
                // mesh) can be hunted in the surrounding lines.
                if (_fSsUpdateW == null && !_ssWatchLooked)
                {
                    _ssWatchLooked = true;
                    _fSsUpdateW = Type.GetType("RttProbe.RttBridge, RttProbe")?.GetField("FloraSsUpdateCalls");
                }
                if (_fSsUpdateW?.GetValue(null) is long ss && ss != _lastSsUpdateW)
                {
                    var d2 = ss - _lastSsUpdateW;
                    _lastSsUpdateW = ss;
                    RttLog.Line($"SUBSECTOR MESH REBUILD fired (+{d2}, cumulative {ss}) — a merged " +
                                "distant-flora mesh (and its RT BLAS) is rebuilding RIGHT NOW; the patch " +
                                "it covers is absent until the rebuild lands. This is the blink.");
                }
            }
            catch { }
        }

        // THE FLORA DISTANCE CAP, on a cadence rather than per frame.
        //
        // Cadenced because the walk is over every owned octree's batch list and there is no
        // event to hang it on: the engine bakes _cullingDistance at allocation and never
        // revisits it, so the only way to catch new batches is to look again. One second is
        // far below the rate at which a 1.26 m/s orbit brings new sectors into range, and the
        // clamp is idempotent so a late pass costs nothing but the walk.
        if (FeedConfig.WholeSceneFloraMaxMetres > 0 && Clock.Ms - _lastFloraClamp >= 1000)
        {
            _lastFloraClamp = Clock.Ms;
            try
            {
                int n = WorldGrids.ClampFloraCullingDistances((float)FeedConfig.WholeSceneFloraMaxMetres);
                _floraClampedTotal += n;
                // Level-triggered on the FIRST pass only: after that the count settles to the
                // trickle of newly allocated batches, and a per-second line would be noise.
                if (n > 0 && !_floraClampLogged)
                {
                    _floraClampLogged = true;
                    RttLog.Line($"FLORA DISTANCE CAP: clamped {n} batch(es) to {FeedConfig.WholeSceneFloraMaxMetres:0} m " +
                                "on the first pass. This is a pure distance cull — LOD selection is untouched, so " +
                                "foreground plants keep the detail wholeSceneLodShift would have taken from them.");
                }
            }
            catch { }
        }

        // THE HOT-RELOAD HANDOVER, performed here because this is the render thread and the
        // render thread is the only place the gate may release GPU resources. The bootstrap
        // has asked us to let go and is waiting up to 3 s; see RttBridge.ReloadRequested.
        //
        // Before this existed a reload simply abandoned our ScreenBuffers and
        // DrawContextManager — FeedGate.Shutdown disposes both correctly, it just never ran —
        // and six reloads in one session took VRAM to 17.5 GB against a 14.8 GiB budget and
        // removed the device. Ahead of every other consideration in this method, because a
        // reload that arrives mid-frame must not be answered by starting another render.
        if (ReloadHandover()) return;

        // OUTSIDE the render-slot scope, and deliberately so. The render thread is the only
        // place the gate may RELEASE anything (disposing from the LCD tick raced the frame
        // recorder and page-faulted), but WHICH feed gets released must not depend on which
        // feed holds this frame's render slot — the slot stops advancing the moment feeds go
        // dormant, which is exactly when the countdowns need to run. See FeedGate.PumpAll:
        // scheduling teardown on the render slot orphaned a whole feed's resources per gate
        // cycle and cost a device removal.
        // EVERY feed's gate, then every feed's countdowns — both outside the render-slot
        // scope, and in that order so a feed that goes dormant this frame arms its teardown
        // before the pump that will run it.
        //
        // The poll used to live inside the scope below, where only the slot holder was ever
        // asked. See FeedGate.PollAll: that meant a feed whose panel had just been destroyed
        // was the one feed nothing could reach, since the slot only goes to feeds that are
        // still eligible.
        //
        // GUARDED, unlike the render below it, which has its own catch inside the scope.
        // This is a Harmony postfix on the engine's Draw: an exception escaping here does not
        // land in our code, it lands in the engine's frame. The lifecycle pump is also the
        // one thing that must keep running when something else has gone wrong — it is what
        // releases resources and what would let a stuck feed go dormant — so it fails loudly
        // and carries on rather than taking the frame with it.
        try
        {
            // THE CONFIG POLL RIDES THIS HOOK, which fires every engine frame no matter what
            // is switched on or which feed is live.
            //
            // Until 2026-08-01 its only caller was the camera pass — which is gated on
            // FeedGate.Active AND on which of the two scene hooks the pass rides, and that
            // choice is ITSELF a config value. So the poll that loads the configuration could
            // only run once the configuration was already loaded and a feed already live.
            //
            // WholeSceneEnabled, WholeSceneBuildBuffers and WholeSceneCamera are auto-property
            // bools with no initialiser — false until first read. A hot reload gives the new
            // assembly fresh statics, so the whole route boots OFF and stays off if anything
            // keeps that one caller from running. Observed live: "ourScreenBuffers=not built,
            // secondRenders=0, camera=player's" for three minutes against a config file that
            // said render=1, camera=1.
            //
            // Same shape as the gate poll fixed this morning, one layer down: state that can
            // only change while the thing it controls is already working. Poll throttles
            // itself, so calling it from the one hook that always fires costs a file stat
            // every couple of seconds and removes the bootstrap dependency entirely.
            //
            // Scoped to Primary for its log lines: the configuration is process-wide, and this
            // runs outside any feed scope.
            // WIDEN THE BRACKET. The camera-swap window peaked at 83 ms while the engine
            // reported 369-721 ms render-thread maxima in the same minutes, so most of the
            // hitch happens in this postfix but OUTSIDE the swap. These three calls are what
            // is left, they run EVERY frame on the render thread, and none of them was ever
            // timed: Poll stats (and sometimes reparses) a 58 KB config file, PollAll walks
            // every gate, PumpAll runs teardown countdowns and can dispose GPU resources.
            long pt0 = System.Diagnostics.Stopwatch.GetTimestamp();
            using (Feeds.Enter(Feeds.Primary)) FeedConfig.Poll();
            long pt1 = System.Diagnostics.Stopwatch.GetTimestamp();
            FeedGate.PollAll();
            long pt2 = System.Diagnostics.Stopwatch.GetTimestamp();
            FeedGate.PumpAll();
            long pt3 = System.Diagnostics.Stopwatch.GetTimestamp();

            double f = System.Diagnostics.Stopwatch.Frequency / 1000.0;
            double totalMs = (pt3 - pt0) / f;
            if (totalMs >= SwapOutlierMs)
                RttLog.Line($"!!! PUMP OUTLIER: the per-frame lifecycle pump took {totalMs:F1} ms " +
                            $"(FeedConfig.Poll {(pt1 - pt0) / f:F1}, FeedGate.PollAll {(pt2 - pt1) / f:F1}, " +
                            $"FeedGate.PumpAll {(pt3 - pt2) / f:F1}). This runs on the RENDER THREAD every " +
                            "frame and is outside the camera-swap bracket, which is why the swap outliers " +
                            "never accounted for the 400-700 ms stalls the engine reports.");
        }
        catch (Exception e)
        {
            if (_pumpErrLogs++ < 5)
                RttLog.Global("!!! Feed lifecycle pump threw — gates, teardown countdowns and the " +
                              "rotation watchdog may have been skipped for this frame. The mod keeps " +
                              "running; if this repeats, a feed will eventually fail to release. " + e);
        }

        // Allocation attribution for the GC-spike hunt (see Perf.NoteRenderAlloc). This
        // wrap covers EVERYTHING our mod does per frame on the render thread except the
        // UI-stage handover — the nested Draw, the camera pass, the copy, the gate, the
        // config poll. Same-thread deltas only; GetAllocatedBytesForCurrentThread is
        // per-thread by contract.
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            using (Feeds.Enter(Feeds.NextForRender()))
                OnWholeSceneScoped(sceneDrawSystem, finalLdrBuffer);
        }
        finally { Perf.NoteRenderAlloc(GC.GetAllocatedBytesForCurrentThread() - alloc0); }
    }

    private static void OnWholeSceneScoped(object sceneDrawSystem, object finalLdrBuffer)
    {
        // The gate was polled for EVERY feed a moment ago, in the unscoped part of this
        // hook (FeedGate.PollAll). Nothing is polled here any more: doing it under the
        // render-slot scope is what made a feed's liveness depend on that feed winning the
        // slot it can only win while it is alive.
        if (!FeedGate.Active) return;

        if (_state == -1) return;

        try
        {
            _hookCount++;

            // STAGE 1: observe. The engine's own final target tells us exactly what a
            // second one has to match — format and resolution are the two things the
            // earlier attempt got wrong.
            //
            // THE STATE TRANSITION AND THE LOG LATCH ARE SEPARATE, and conflating them cost
            // the whole first two-feed evening.
            //
            // This used to be one block: `if (!_describedTarget) { _describedTarget = true;
            // _state = 1; ...log... }`. _describedTarget is process-global — correctly, it
            // describes the ENGINE'S final target, which is the same for everyone — but
            // _state is PER-FEED. So feed 0 ran first, claimed the latch, and set ITS OWN
            // _state to 1. Feed 1 never entered the block, its _state stayed 0 forever, and
            // PanelSource requires _state == 1 — so feed 1's source view was permanently
            // null, its copy failed with wholeSceneSrv=False, it never parked a frame, and
            // its panel was black. Everything else about feed 1 was healthy: own target, own
            // 1024x1024 buffers, own LDR ring, rendering and settling normally.
            //
            // This is EXACTLY the hazard the C1a inventory called out — "a log latch that
            // also gates behaviour" — written down, and then walked into anyway, because the
            // gating was one line inside something that reads as pure diagnostics.
            //
            // The rule this earns: when splitting state into per-feed and global, the
            // question is not "is this field per-feed" but "is every ASSIGNMENT to it
            // reachable by every feed".
            if (finalLdrBuffer != null) _state = 1;

            if (!_describedTarget && finalLdrBuffer != null)
            {
                _describedTarget = true;
                RttLog.Line($"Whole-scene hook: LIVE. SceneDrawSystem.Draw postfix fired with " +
                            $"{Describe(finalLdrBuffer)}. This is the top of the pipeline — the only " +
                            "site where a second whole-scene render can be driven without re-entering " +
                            "a frame from inside itself.");
                LogScreenBuffers();
            }

            if (FeedConfig.WholeSceneBuildBuffers) EnsureScreenBuffers();

            // STAGE 3: the actual second render.
            //
            // RATE GATED. Draw is a whole frame; at 53 fps an ungated second render would
            // roughly halve the game's frame rate before we have learned anything from it.
            // The gate also means a fault costs one attempt per interval rather than one
            // per frame while we work out what happened.

            // Which end of Draw owns the render was decided by the prefix at the top of
            // THIS frame (see OnWholeSceneEarly). Read the recorded decision rather than
            // the config: FeedConfig.Poll runs from the camera pass, which fires INSIDE
            // the player's Draw — i.e. between our prefix and our postfix — so re-reading
            // the flag here could see it flip mid-frame and render twice in one frame.
            bool oursRan = _earlyRan;
            _earlyRan = false;
            if (!_earlyOwnsThisFrame) oursRan = TryRender(sceneDrawSystem);

            Perf.NoteFrame(oursRan);

            long now = Clock.Ms;
            if (now - _lastLogMs >= 5000)
            {
                _lastLogMs = now;
                RttLog.Line($"Whole-scene hook: {_hookCount} frame(s), " +
                            $"ourScreenBuffers={(_ourScreenBuffers == null ? "not built" : "BUILT")}, " +
                            $"secondRenders={_renderCount}, camera={(FeedConfig.WholeSceneCamera ? "OURS" : "player's")}, " +
                            $"submit={(_earlyOwnsThisFrame ? "START-of-frame" : "end-of-frame")}.");
            }
        }
        catch (Exception e) { _state = -1; RttLog.Error("whole-scene hook", e); }
    }

    // Set by the prefix each frame; read by the postfix. Not ThreadStatic — both hooks
    // fire on the render thread, and the prefix always precedes the postfix within a frame.
    private static bool _earlyRan, _earlyOwnsThisFrame;

    // Log budget for the lifecycle-pump guard above. Process-global: it describes our own
    // per-frame bookkeeping, which sweeps every feed, not any one feed's state.
    private static int _pumpErrLogs;

    // START-OF-FRAME SUBMISSION. The targeted fix for the session drift.
    //
    // Measured 2026-07-28: our render's true GPU work is only ~3 ms, but an ours-frame
    // costs ~30 ms because the GPU sits IDLE waiting — and that idle grows with engine
    // session age (10 ms of bubbles at ~50 min, none when dormant) and survives a full
    // teardown of everything we own. Only a process restart resets it, so the reservoir is
    // engine-side; our render is just the thing that pays for it, because it sits between
    // the player's recorded work and the present copy.
    //
    // Recording our render HERE instead puts our commands ahead of the player's, so the
    // GPU executes them while the CPU is still recording the player's frame — time the GPU
    // spent idle anyway. It does not shrink the bubbles; it moves them somewhere they cost
    // nothing. Today's ~10-13 ms of gaps fit inside the player's ~15 ms record window.
    //
    // The feed image is one frame older than it would otherwise be. At 30 fps that is
    // ~33 ms of extra latency on a slowly orbiting camera — irrelevant.
    public static void OnWholeSceneEarly(object sceneDrawSystem, object finalLdrBuffer)
    {
        if (_inOurRender) return;               // our own nested Draw
        using (Feeds.Enter(Feeds.NextForRender()))
            OnWholeSceneEarlyScoped(sceneDrawSystem);
    }

    // Scoped to the SAME feed the postfix will pick. NextForRender is a pure function of the
    // rotation origin and eligibility, and the origin moves only when a render completes, so
    // the prefix and postfix of one engine frame normally agree on whose frame it is. That
    // is what lets _earlyRan / _earlyOwnsThisFrame stay plain per-frame statics rather than
    // becoming per-feed state.
    //
    // "Normally", because eligibility CAN move between the two (phase F1): the postfix polls
    // every gate before it scopes, and a panel tick on the LCD thread can revive a feed at
    // any moment. The one-render-per-frame invariant survives that intact, which is the part
    // that matters — whoever renders does so under a gate check of their own, and the
    // postfix will not render at all when the prefix owns the frame. The cost of a
    // disagreement is that one feed misses one turn, which is what a rotation is for.
    private static void OnWholeSceneEarlyScoped(object sceneDrawSystem)
    {
        // Cleared HERE, at frame start, not just after the postfix reads it. If the postfix
        // bails early — gate went dormant mid-frame, _state faulted — a stale true would
        // survive into the next frame and mis-bucket a Perf sample. That histogram is the
        // instrument this whole change is judged by, so it does not get to lie.
        _earlyRan = false;

        // Recorded unconditionally, before any early-out, so the postfix always has a
        // coherent answer for this frame even when we decline to render.
        _earlyOwnsThisFrame = FeedConfig.WholeSceneSubmitEarly;
        if (!_earlyOwnsThisFrame) return;       // the postfix owns it

        // The gate is polled and the buffers are built by the POSTFIX. On the very first
        // frames that leaves nothing to render from, so we simply decline and pick it up
        // next frame — one frame of startup latency, no special case.
        if (!FeedGate.Active || _state == -1 || _ourScreenBuffers == null) return;

        try { _earlyRan = TryRender(sceneDrawSystem); }
        catch (Exception e) { _state = -1; RttLog.Error("whole-scene early hook", e); }
    }

    // The settle countdown, the rate gate and the render itself. Called from EXACTLY ONE
    // of the two hooks per engine frame — whichever owns this frame — because both the
    // countdown and the rate stamp are per-frame state that must not tick twice.
    private static bool TryRender(object sceneDrawSystem)
    {
        // SETTLE AFTER A REBUILD, and this one cost a device removal to find.
            //
            // Reset() disposes and re-creates our ScreenBuffers and DrawContextManager, and
            // creating a DrawContextManager trips the engine's context-reset path — which
            // sets EnvironmentProbeManager._forceReprocess (OnResetContext is one of its two
            // writers). The engine's next frame therefore force-reprocesses EVERY probe: the
            // DRED dump from the crash showed a long queue of EnvProbe_Blending passes still
            // outstanding, and the fault was a null bind (PageFaultVA 0x0, zero existing and
            // zero freed allocations) inside that batch, while the probe cube textures were
            // being recreated.
            //
            // Rendering a second whole scene inside that window is what faulted. The trigger
            // was raising wholeSceneIntervalMs to 33 on a LIVE feed: at 100 ms there were
            // ~3 engine frames of slack and we never landed in the window, at 33 ms we landed
            // in it on the very first render — 2.1 s after the config save, at
            // secondRenders=1. Proven not to be a rate problem by booting straight into 33 ms
            // with no mid-session Reset: stable indefinitely at ~27 renders/sec.
            //
            // So: after any (re)build, let the engine have a few frames to itself. Frames,
            // not milliseconds — the thing being waited for is engine frames completing, and
            // during a mass probe reprocess those frames are long.
            // Counted down by TickSettle from the per-frame pump, NOT here (phase F1).
            // Ticking it on the render slot meant a settling feed only settled while it was
            // winning turns, so with two feeds a 30-frame window took 60 engine frames — the
            // countdown is specified in ENGINE frames, because engine frames are what the
            // probe reprocess drains on.
            //
            // ASKED OF EVERY FEED, not just this one. The thing being waited for is the
            // SHARED EnvironmentProbeManager reprocessing every probe after a rebuild, and
            // "rendering into that batch is a device removal" does not become safe because
            // it is a different feed doing the rendering. Until now this was accidental —
            // a settling feed happened to hold the render slot, so nobody else got one —
            // and the accident goes away as soon as the rotation is allowed to move.
            if (_settleFrames > 0 || Feeds.AnySettling()) return false;

        // No hard floor any more (was Math.Max(33, ...)). The 30fps cap was a safety rail
        // from the era when a fault cost a CTD per attempt; the route is stable now and the
        // cost model is a straight trade, so the slider is the user's. Multi-feed budgeting
        // (see docs/roadmap.md) will sit on top of this same gate later.
        if (!FeedConfig.WholeSceneEnabled || _ourScreenBuffers == null) return false;
        if (Clock.Ms - _lastRenderMs < FeedConfig.WholeSceneIntervalMs) return false;

        // RENDER-ON-DEMAND (task #25): decline the turn unless a copy consumed the last
        // frame (the handover raises RenderWanted per landed copy), with a heartbeat so
        // warm-up and copier stalls cannot starve the pipeline. Renders then track the
        // panel's true refresh instead of running once per engine frame and discarding
        // the undelivered surplus. Pair with wholeSceneSubmitEarly=1 — sparse renders in
        // the END-of-frame position stall on the engine's full GPU queue (submit 2.5 ->
        // 17.8 ms, measured), which eats the entire saving.
        if (FeedConfig.WholeSceneRenderOnDemand)
        {
            var f = Feeds.Cur;
            bool heartbeatDue = FeedConfig.WholeSceneOnDemandHeartbeatMs > 0 &&
                                Clock.Ms - _lastRenderMs >= FeedConfig.WholeSceneOnDemandHeartbeatMs;
            if (!f.RenderWanted && !heartbeatDue) return false;
            f.RenderWanted = false;
        }

        _lastRenderMs = Clock.Ms;
        RunSecondRender(sceneDrawSystem);

        // THE SLOT ADVANCES HERE, and only here: after a render actually happened. Every
        // early return above this line — dormant gate, settling after a rebuild, rate gate,
        // route disabled — leaves the rotation where it is, so a feed that declines its turn
        // keeps it rather than forfeiting it to the next feed forever. With one feed this is
        // a no-op; with N it is the difference between fair rotation and starvation.
        Feeds.AdvanceSlot();
        return true;
    }

    // STAGE 3: swap the globals, run a whole second frame, put them back.
    //
    // The entire route is this method. Everything else is scaffolding.
    //
    // WHAT IS SWAPPED, and deliberately how little. Stage 3a moves ONLY
    // CoreSystems.ScreenBuffers, so the second render is the PLAYER'S viewpoint at our
    // resolution. That isolates the one question worth asking first — can Draw run a
    // second time at all with substituted globals — with no camera to confound it. If
    // this works the picture is the player's view at 512x512, which is wrong but
    // verifiable, and the camera becomes one more flip (wholeSceneCamera) rather than
    // part of an unattributable failure. Three things moving at once is what made the
    // deferred route's failures impossible to read.
    //
    // THE SAVE THUMBNAIL WAS BEING TAKEN OFF OUR FEED. This is the one defect in this
    // file that reached outside the process and wrote a wrong file to disk.
    //
    // SceneDrawSystem.Draw calls ScreenshotsManager.TakeRequestedScreenshots(copySource)
    // as part of its own body — so OUR nested Draw services the engine's pending screenshot
    // requests, with OUR feed buffer as the copy source. From the game log, timestamp-matched
    // to "[Saving] Starting save":
    //
    //     [Saving] Starting save
    //     Assertion Failure: 'screenshot.DownsampleResolution == null ||
    //       (DownsampleResolution?.X <= copySource.Resolution.X && ...)'
    //         at ScreenshotsManager.TakeRequestedScreenshots
    //         at RttProbe.WholeSceneRender.RunSecondRender
    //     Assertion Failure: Destination texture must be smaller or equal in size to source
    //         at CopyJob.DoWork  <- the thumbnail copy itself
    //     [Saving] screenshotTask Start / Done      <- and the save went on regardless
    //
    // The request is queued by the SERVER thread the moment a save begins, so it can land
    // after the engine's own Draw has already passed its screenshot point in that frame.
    // Ours is then the next Draw to run, it takes the request, and the copy fails because
    // the save wants a thumbnail bigger than a 1024x1024 feed. The save still reports
    // Success — with a thumb.jpg that came from the wrong place or not at all.
    //
    // Skipping our render for that frame is the whole fix: the request stays queued and the
    // engine's own Draw services it next frame, from the player's full-size buffer, which is
    // what it was always asking for. One dropped feed frame per save is not observable.
    // NOT a swap-the-list-and-restore, deliberately — mutating the engine's request queue
    // around a call that can throw is how you lose a screenshot request permanently.
    private static int _screenshotSkips, _dsQueueMax;
    private static bool _dsShapeLogged;
    private static FieldInfo _shotsMgrField, _shotsListField;
    private static bool _shotsShapeLogged;

    // ===================== THE PER-FRAME POOL LEAK =====================
    //
    // The game's deferred-assertion summary, printed at exit, gave the count that turned
    // this from a curiosity into the main line of enquiry. One session, ~4458 second-renders:
    //
    //     Some of the borrowed textures has not been returned; '_allocated.Count == 0'
    //       (BindableTexturePoolManager.cs:526)              triggered 9872 time(s)
    //     'CoreSystems.BindableBuffers.AliveConstantBufferCount == 0'
    //       (BindableBufferManager.cs:201)                   triggered 4936 time(s)
    //
    // 4936 tracks our render count; 9872 is exactly twice it. Both fire from
    // IRender_Present -> CoreSystems.OnFrameEndDisposal, AFTER our postfix has returned. So
    // every nested Draw ends the frame with TWO pools holding an unreturned texture and one
    // constant buffer still alive — not occasionally, every single frame, all session.
    //
    // WHY IT IS WORTH INSTRUMENTING RATHER THAN GUESSING. Reading Draw's IL, the borrows look
    // balanced: BorrowResizableRWRenderTargetTexture (the LBuffer, keyed on
    // ScreenBuffers.MaxPreUpscaleResolution — OURS during our pass) and a second borrow that
    // comes back out of ExecuteForwardAndPostProcess for the screenshot copy, both Returned
    // before Draw exits. Balanced in source and unbalanced in fact means the imbalance comes
    // from state WE swap between the Borrow and the Return, and only the pool itself can say
    // which resource and which key.
    //
    // THE DISCRIMINATOR is sampling either side of our Draw. Our postfix runs after the
    // engine's Draw has completed, so every pool should read zero going in. Zero before and
    // non-zero after is ours and nobody else's; non-zero before means we inherited it.
    private static readonly string[] PoolFields =
    {
        "_renderTargetTextures", "_rwRenderTargetTextures", "_resizableRenderTargetTextures",
        "_resizableRenderTargetTextureArrays", "_resizableRWRenderTargetTextures",
        "_resizableDepthStencilTextures", "_depthStencilTextures",
    };

    private static FieldInfo[] _poolInfos;
    private static FieldInfo _poolMgrField, _bufMgrField, _aliveCbField;
    private static readonly FieldInfo[] _allocFields = new FieldInfo[PoolFields.Length];
    private static readonly int[] _poolBefore = new int[PoolFields.Length];
    private static readonly int[] _poolAfter  = new int[PoolFields.Length];
    private static readonly long[] _poolLeaked = new long[PoolFields.Length];
    private static readonly int[] _poolPeakBefore = new int[PoolFields.Length];
    private static readonly int[] _poolPeakAfter = new int[PoolFields.Length];
    private static readonly long[] _poolNonZeroAfter = new long[PoolFields.Length];
    private static int _cbBefore, _poolSamples, _cbPeakBefore, _cbPeakAfter;
    private static long _cbLeaked;
    private static bool _poolShapeLogged, _poolDetailDumped;

    // Fills `into` with each pool's _allocated.Count and returns AliveConstantBufferCount.
    // Never throws: an instrument that can break the render is not an instrument.
    private static int SamplePools(int[] into)
    {
        try
        {
            _coreType ??= Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            _poolMgrField ??= _coreType?.GetField("BindableTexturePool", BindingFlags.Public | BindingFlags.Static);
            _bufMgrField  ??= _coreType?.GetField("BindableBuffers", BindingFlags.Public | BindingFlags.Static);

            var mgr = _poolMgrField?.GetValue(null);
            if (mgr != null)
            {
                if (_poolInfos == null)
                {
                    var t = mgr.GetType();
                    _poolInfos = new FieldInfo[PoolFields.Length];
                    for (int i = 0; i < PoolFields.Length; i++) _poolInfos[i] = t.GetField(PoolFields[i], Any);

                    if (!_poolShapeLogged)
                    {
                        _poolShapeLogged = true;
                        var missing = new List<string>();
                        for (int i = 0; i < PoolFields.Length; i++)
                            if (_poolInfos[i] == null) missing.Add(PoolFields[i]);
                        if (missing.Count > 0)
                            RttLog.Line("POOL CENSUS: these pool fields were not found on this game build — " +
                                        string.Join(", ", missing) + ". The census is partial; a zero for a " +
                                        "missing pool means UNREAD, not empty.");
                    }
                }

                // ONE FieldInfo PER POOL, NOT ONE SHARED. The seven pools are seven DIFFERENT
                // closed generics of Pool`2, and a FieldInfo obtained from one of them throws
                // ArgumentException when used against another — straight into the catch, which
                // returned 0. So this census read pool[0] correctly and silently reported ZERO
                // for the other six, which is exactly how it came to announce "every texture
                // pool balanced" while the engine was asserting that they were not.
                for (int i = 0; i < _poolInfos.Length; i++)
                {
                    into[i] = 0;
                    var pool = _poolInfos[i]?.GetValue(mgr);
                    if (pool == null) continue;
                    _allocFields[i] ??= pool.GetType().GetField("_allocated", Any);
                    try
                    {
                        if (_allocFields[i]?.GetValue(pool) is System.Collections.ICollection c) into[i] = c.Count;
                    }
                    catch { into[i] = -1; }   // -1 = UNREADABLE, never confuse it with empty
                }
            }

            var bufs = _bufMgrField?.GetValue(null);
            if (bufs == null) return 0;
            _aliveCbField ??= bufs.GetType().GetField("AliveConstantBufferCount", Any);
            return _aliveCbField?.GetValue(bufs) is int n ? n : 0;
        }
        catch { return 0; }
    }

    // One-shot: name what is actually stuck in a pool, the first time we see it. Key is the
    // resolution the texture was sized from, which is the whole point — it says whose
    // ScreenBuffers was installed at the moment of the borrow.
    private static void DumpLeakedRecords()
    {
        if (_poolDetailDumped) return;
        _poolDetailDumped = true;
        try
        {
            var mgr = _poolMgrField?.GetValue(null);
            if (mgr == null || _poolInfos == null) return;

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _poolInfos.Length; i++)
            {
                var pool = _poolInfos[i]?.GetValue(mgr);
                if (pool == null) continue;
                System.Collections.IEnumerable list;
                try { list = _allocFields[i]?.GetValue(pool) as System.Collections.IEnumerable; }
                catch { continue; }
                if (list == null) continue;

                foreach (var rec in list)
                {
                    if (rec == null) continue;
                    var rt = rec.GetType();
                    var key = rt.GetField("Key", Any)?.GetValue(rec);
                    var bytes = rt.GetField("ByteSize", Any)?.GetValue(rec);
                    var life = rt.GetField("Lifetime", Any)?.GetValue(rec);
                    var tex = rt.GetField("Texture", Any)?.GetValue(rec);
                    var name = tex == null ? "?"
                        : (tex.GetType().GetProperty("DebugName", Any)?.GetValue(tex)
                           ?? tex.GetType().GetField("DebugName", Any)?.GetValue(tex))?.ToString() ?? tex.GetType().Name;
                    sb.Append($"\n    {PoolFields[i]}: \"{name}\" key={key} bytes={bytes} lifetime={life}");
                }
            }

            RttLog.Line("POOL LEAK — what our nested Draw left borrowed at the moment it returned:" +
                        (sb.Length == 0 ? " (nothing; the leak is not visible at this sampling point)" : sb.ToString()) +
                        "\n  The KEY is the resolution the texture was sized from, so it names whose " +
                        "ScreenBuffers was installed when the borrow happened. This dumps once per load.");
        }
        catch (Exception e) { RttLog.Error("pool leak dump", e); }
    }

    private static bool ScreenshotPending()
    {
        try
        {
            _coreType ??= Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            _shotsMgrField ??= _coreType?.GetField("ScreenshotsManager",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var mgr = _shotsMgrField?.GetValue(null);
            if (mgr == null) return false;

            _shotsListField ??= mgr.GetType().GetField("_requestedScreenshots", Any);
            if (_shotsListField == null)
            {
                if (!_shotsShapeLogged)
                {
                    _shotsShapeLogged = true;
                    RttLog.Line("Whole-scene: ScreenshotsManager._requestedScreenshots not found on this game " +
                                "build — the save-thumbnail guard is INACTIVE, so a save taken while the feed " +
                                "renders can still capture the thumbnail off our buffer.");
                }
                return false;
            }

            return _shotsListField.GetValue(mgr) is System.Collections.ICollection c && c.Count > 0;
        }
        catch { return false; }   // never let the guard itself break the render
    }

    // RESTORE ORDERING. The restore is in a finally and runs even if Draw throws
    // mid-frame, because leaving the engine pointed at a 512x512 ScreenBuffers would
    // render the player's next frame into our buffers — visually catastrophic and not
    // obviously attributable to us. The re-entrancy guard is cleared in the same finally
    // for the same reason: a stuck guard silently disables the route forever.
    //
    // ONE STRIKE. Any exception disables the route for the session. A whole-frame render
    // that faults is not something to retry 53 times a second while reading the log.
    private static void RunSecondRender(object sceneDrawSystem)
    {
        if (sceneDrawSystem == null) return;

        // NEVER RUN OUR DRAW WHILE THE ENGINE HAS A SCREENSHOT PENDING. See ScreenshotPending.
        if (ScreenshotPending()) { _screenshotSkips++; return; }

        try
        {
            _miDraw ??= sceneDrawSystem.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "Draw" && m.GetParameters().Length == 1);
            _coreType ??= Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            _sbField ??= _coreType?.GetField("ScreenBuffers", BindingFlags.Public | BindingFlags.Static);

            if (_miDraw == null || _sbField == null)
            {
                _state = -1;
                RttLog.Line($"Whole-scene: Draw={(_miDraw == null ? "NOT FOUND" : "ok")} " +
                            $"CoreSystems.ScreenBuffers={(_sbField == null ? "NOT FOUND" : "ok")} — route disabled.");
                return;
            }

            // Our own final target, out of our own ScreenBuffers. Draw takes its render
            // resolution from whatever it is handed, so this is what makes the second
            // render 512x512 rather than 4K.
            var ourLdr = _ourScreenBuffers.GetType()
                .GetProperty("FinalLDRTexture", Any)?.GetValue(_ourScreenBuffers);
            if (ourLdr == null)
            {
                _state = -1;
                RttLog.Line("Whole-scene: our ScreenBuffers has no FinalLDRTexture — route disabled.");
                return;
            }

            var savedSb = _sbField.GetValue(null);
            object savedCam = null, savedDc = null, savedProbes = null, savedEyeJob = null, savedRtScene = null,
                   savedIrCache = null;
            object[] savedCb = null;
            // Hoisted out of the install block: the finally has to free it, and it is
            // orphaned whether or not the swap actually took.
            object ourCameraCb = null;
            bool camSwapped = false, ownShadows = false, planetEnvSwapped = false;

            _inOurRender = true;
            try
            {
                _sbField.SetValue(null, _ourScreenBuffers);

                // Swap the context family too, when we have one. Everything our render
                // culls and ranges then lands in contexts the player's frame never
                // reads — which is both the flashing fix and the prerequisite for
                // culling from a different camera at all.
                if (FeedConfig.WholeSceneOwnDrawContexts)
                {
                    EnsureDrawContexts();
                    if (_ourDrawContexts != null)
                    {
                        _dcField ??= _coreType?.GetField("DrawContexts",
                            BindingFlags.Public | BindingFlags.Static);
                        if (_dcField != null)
                        {
                            savedDc = _dcField.GetValue(null);
                            _dcField.SetValue(null, _ourDrawContexts);
                        }
                    }
                }

                // Re-share the flare DEFINITIONS every render (goal 4.3). Cheap — four
                // reference assignments — and it is what keeps a replaced _flaresBuffer from
                // leaving the feed permanently stale. No-op unless wholeSceneOwnFlares is on.
                if (FeedConfig.WholeSceneOwnFlares) MirrorFlareDefinitions();

                // OUR OWN AUTO-EXPOSURE HISTORY, installed before ScopeSharedState so the
                // scope below can see that we own it and leave EyeAdaptation alone.
                savedEyeJob = InstallEyeAdaptation(sceneDrawSystem);

                // BEFORE ScopeSharedState, and deliberately so. This writes the GLOBAL flora
                // radius; ScopeSharedState then saves whatever is current as the value to
                // restore at the end of our pass. In this order the global change survives
                // the pass unwind. Reversed, the unwind would quietly undo it every frame —
                // a knob that binds, logs success, and still does nothing.
                ScatterControl.Apply();

                ScopeSharedState();
                if (FeedConfig.WholeSceneCamera) camSwapped = InstallCamera(out savedCam);

                // ENVIRONMENT PROBES — AFTER InstallCamera, AND THAT ORDER IS THE WHOLE FIX.
                //
                // This used to sit above, before ScopeSharedState and InstallCamera, with a
                // comment saying only "AFTER the DrawContextManager swap, because it writes our
                // context's EnvProbesToUpdate". That dependency is real and is still satisfied
                // here — the DC swap happens earlier than both. What the old position missed is
                // that PrepareProbes does not merely QUEUE work, it BAKES THE CAPTURE POSITION
                // into every request at queue time:
                //
                //     EnvironmentProbeManager/Render..ctor
                //       ldsfld   CoreSystems::Settings
                //       callvirt SettingsManager::get_RenderView()
                //       call     RenderView::get_CameraPosition()          <- read RIGHT HERE
                //       call     CubeTextureUtils::GenerateCubeMapViewAtPosition(pos, face)
                //       stfld    Render::View                              <- baked in
                //
                // Stage 2 (RenderEnvironmentProbe) later just drains the queue and renders each
                // request through the View it was built with. So running PrepareProbes before
                // InstallCamera stamped the PLAYER'S position into all six faces, and the feed's
                // environment cube was a capture of wherever the player stands. Reported
                // 2026-08-05 as ship windows in orbit reflecting the player's dusty desert.
                //
                // That cube is the source for BOTH symptoms, which is why they always moved
                // together: EnvironmentProbeManager.CloseIBL is the SRV the SSSR Intersection
                // dispatch binds for every off-screen ray, and IBL is also the ambient bounce
                // term. One wrong position, two wrong-looking features.
                //
                // THE TRAP, recorded because it cost a restart: "the capture executes in stage 2,
                // which runs after InstallCamera" is TRUE and irrelevant. Where the work RUNS and
                // where its inputs were RESOLVED are different questions, and only the second one
                // decides correctness. Checking that stage 2 ran late was not enough; the ctor one
                // call further down is where the position actually came from.
                savedProbes = InstallProbes();

                // OUR TLAS, for the same reason and with the same ordering constraint: a
                // CreateTLAS resolves its camera from the global RenderView, so this belongs
                // inside the swap. Stage 0 is force-run while it is installed.
                savedRtScene = InstallRayTracingScene();

                // OUR IRRADIANCE GRID, and the ordering is the same constraint a third time:
                // stage 30's trace job resolves its sampling position from the global
                // RenderView, so it must be installed inside the swap. This is the fix for
                // contamination pointing AT THE PLAYER — see InstallIRCache.
                savedIrCache = InstallIRCache();

                // The camera CONSTANT BUFFER, not just the view. The matrix check proved
                // the installed view was rebuilt perfectly — square projection, orbiting
                // At0 pair — and the panel still rendered with the player's aspect and a
                // head-tracked sky. Shaders never read the view: they read the per-frame
                // camera CB, which the engine builds from the PLAYER'S view before Draw
                // runs, and our nested Draw inherits it. Culling and camera-relative
                // positioning read the installed view directly — which is exactly why
                // geometry orbited while the sky did not. Both halves have to be ours.
                //
                // The probe pass has done this precise swap every 33ms for weeks
                // (CameraCbSwap: restore in the same frame bracket, never null, never
                // the same buffer in both fields).
                if (camSwapped && FeedConfig.WholeSceneCameraRebuild >= 2)
                {
                    var cb = CameraRender.WholeSceneCameraCb();
                    if (cb != null)
                    {
                        ourCameraCb = cb;
                        savedCb = CameraCbSwap.Install(cb);
                        if (!_cbSwapLogged)
                        {
                            _cbSwapLogged = true;
                            RttLog.Line("Whole-scene camera CB: swapped in for our render — shaders now " +
                                        "read the orbit camera's projection, sky rotation and " +
                                        $"{FeedConfig.WholeSceneWidth}x{FeedConfig.WholeSceneHeight} " +
                                        "Screen.Resolution instead of inheriting the player's frame CB.");
                        }
                    }
                    else if (_cbSwapErrs++ < 2)
                    {
                        RttLog.Line("Whole-scene camera CB: build failed — feed keeps the player's " +
                                    "projection/sky until it succeeds.");
                    }
                }

                // Last, because it reads BOTH globals we just installed: FlushUpdates
                // fits the cascade frusta to CoreSystems.Settings.RenderView (ours), and
                // the resource rebuild walks CoreSystems.DrawContexts (ours).
                ownShadows = BeginOwnShadows();

                // After the camera install: it reads the INSTALLED view, which must be
                // ours by now for the rebuild to mean anything.
                if (camSwapped) planetEnvSwapped = RebuildPlanetEnv();

                // The exposure bleed is handled by the stage-25 Harmony override
                // (ConstantExposure becomes read-only for our render), not by owning a
                // second EyeAdaptationJob. Two attempts at owning one removed the device:
                // constructing the job ran InitializeAsync's PSO compile against the live
                // recorder, and creating 1x1 targets put resources outside the engine's
                // AutoResourceState tracking. Both are recorded in docs/whole-scene-status.md.

                // THE SETTLE WINDOW CAN OPEN DURING THIS FRAME'S SETUP — re-check it here.
                //
                // EnsureDrawContexts runs ~80 lines above, and it runs THERE on purpose: the
                // context family must size itself against OUR ScreenBuffers, which are only
                // swapped into CoreSystems inside this method. But constructing that manager
                // is precisely what trips the engine's context-reset path and forces the
                // shared EnvironmentProbeManager to reprocess every probe — so TryRender's
                // settle check ran BEFORE the hazard it guards against had been created.
                //
                // On 2026-08-01 11:30:24 that gap was the whole crash: DrawContextManager
                // built at .712, Draw called at .714, DXGI_ERROR_DEVICE_REMOVED. Arming the
                // window inside EnsureDrawContexts is necessary but cannot help by itself,
                // because by then this frame's decision to render had already been taken.
                //
                // A non-zero window at this point therefore means exactly one thing: a build
                // happened during setup. Unwind and let it settle. The finally restores every
                // swap, same as any other exit.
                if (_settleFrames > 0)
                {
                    RttLog.Line($"Whole-scene: contexts were (re)built during this frame's setup, so the " +
                                $"first render is deferred by {_settleFrames} engine frames instead of " +
                                "drawing into the probe reprocess that build just triggered. This is the " +
                                "device-removal window, and it is now closed at the point the hazard is " +
                                "created rather than at a proxy for it.");
                    return;
                }

                if (_renderCount == 0)
                    RttLog.Line($"=== WHOLE-SCENE RENDER: calling SceneDrawSystem.Draw a second time, " +
                                $"into our own {FeedConfig.WholeSceneWidth}x{FeedConfig.WholeSceneHeight} " +
                                $"ScreenBuffers. Camera is {(camSwapped ? "OURS" : "the player's")}. ===");

                // RESOLUTION TRIPWIRE. The blit identity log caught our FinalLDRTexture at
                // 3840x2160 — the PLAYER'S resolution — after being built at 512. If that
                // resize happens across ONE nested Draw, our own Draw's upscale tail is
                // doing it from player display state; if it happens elsewhere, an engine
                // path is touching our instance. Either way this names the frame it flips.
                // MaxResolution is logged too because it is the POOL KEY: the moment ours
                // says 3840x2160 our textures share borrow keys with the player's pool,
                // and the aliasing that was "excluded" is excluded no longer.
                string before = LdrRes(ourLdr);

                // Sampled here, every render, because a latch lasting a few hundred ms is
                // invisible to the 15 s report clock and the whole question is sub-second.
                SampleStreamingBudget();

                // Either side of the Draw and nowhere else — see the POOL LEAK block above.
                // The engine's own Draw has already finished by the time our postfix runs, so
                // a non-zero reading HERE would mean we inherited an imbalance rather than
                // caused one. That distinction is the entire value of the instrument.
                _cbBefore = SamplePools(_poolBefore);

                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                // Split the swap window into SETUP / DRAW / TEARDOWN. Only the Draw genuinely
                // requires our camera to be the installed global; anything either side of it
                // that does not is exposure we are paying for nothing, and this says how much
                // of the 3.28 ms mean hold is actually reclaimable before we start moving code.
                _lastSetupTicks = t0 - _swapOpenedTicks;
                _setupTicks += _lastSetupTicks;

                // IBL-ONLY AMBIENT FOR OUR PASS (2026-08-07, the cave floodlight). With our
                // TLAS empty by design, stage 17's RTGI trace MISSES on every ray and a miss
                // shades as SKY — so the gi buffers carry open-sky irradiance, which outdoors
                // is roughly right and underground is a blinding, angle-flipping blast. The
                // cache was proven innocent first (grid empty, blast persisted).
                //
                // ComputeGI's own IL shows the engine has a first-class no-RT branch: the gi
                // dispatch is gated on Settings.IsRaytracingSupportedAndEnabled and the else
                // path still runs AmbientLightJob exactly as every RT-off player's game does.
                // So the fix is to have OUR pass take that branch: a ThreadStatic bridge flag,
                // read by a bootstrap postfix on the gate property, answering false only on
                // THIS thread while the nested Draw executes. No settings struct is touched,
                // no snapshot, no PSO defines — the hazards that made scoping RaytracingSettings
                // unsafe are all absent, and the player's frame reads the true value even when
                // its scene jobs overlap our window (they are other threads, and the flag is
                // ThreadStatic precisely so they never see the lie).
                //
                // Reached reflectively so an older bootstrap degrades to today's behaviour
                // (sky ambient) with one log line, per the frozen-bootstrap rule.
                bool rtOffSet = SetNestedRtOff(true);
                try { _miDraw.Invoke(sceneDrawSystem, new[] { ourLdr }); }
                finally { if (rtOffSet) SetNestedRtOff(false); }
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                _lastDrawTicks = t1 - t0;
                _drawTicks += _lastDrawTicks;
                _drawEndTicks = t1;

                int cbAfter = SamplePools(_poolAfter);
                _poolSamples++;

                // DELTAS ALONE WERE A DEAD INSTRUMENT, and it reported CLEAN while the engine
                // was asserting. Our postfix runs AFTER the engine's own Draw, so if the
                // engine legitimately holds N borrows in flight at that moment, before reads
                // N, after reads N, the delta is zero and the census announces "balanced" —
                // about a frame that ends with N textures unreturned. The engine's assert is
                // on the ABSOLUTE count at frame end (`_allocated.Count == 0`), so that is
                // what has to be tracked. Deltas are kept because they still answer the
                // separate question of whether WE unbalanced anything; the absolutes answer
                // whether the frame is clean, which is the one the assert asks.
                bool anyDelta = false;
                for (int i = 0; i < _poolAfter.Length; i++)
                {
                    int d = _poolAfter[i] - _poolBefore[i];
                    if (d != 0) { _poolLeaked[i] += d; anyDelta = true; }

                    if (_poolBefore[i] > _poolPeakBefore[i]) _poolPeakBefore[i] = _poolBefore[i];
                    if (_poolAfter[i]  > _poolPeakAfter[i])  _poolPeakAfter[i]  = _poolAfter[i];
                    if (_poolAfter[i] != 0) _poolNonZeroAfter[i]++;
                }
                _cbLeaked += cbAfter - _cbBefore;
                if (_cbBefore > _cbPeakBefore) _cbPeakBefore = _cbBefore;
                if (cbAfter  > _cbPeakAfter)   _cbPeakAfter  = cbAfter;

                // Dump on the first frame that ends our pass with anything outstanding —
                // that is the state the engine will assert on a few microseconds later.
                bool outstanding = cbAfter != 0;
                for (int i = 0; i < _poolAfter.Length && !outstanding; i++) outstanding = _poolAfter[i] != 0;
                if (outstanding || anyDelta) DumpLeakedRecords();
                Perf.NoteOurDraw((t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                _renderCount++;

                // GRASS PROBE — read the gate FROM INSIDE OUR PASS, while our contexts are
                // still installed. Placed here rather than anywhere cheaper because every
                // value it reads is a GLOBAL that means something different depending on
                // whose render is running: after the finally below, CoreSystems.DrawContexts
                // is the player's again and the answer would be about their frame, not ours.
                if (FeedConfig.GrassProbe) GrassProbe(sceneDrawSystem);

                // Rides the same gate: all three answer "is this subsystem working for OUR
                // camera or the player's", all are read-only and rate-limited to 15 s.
                if (FeedConfig.GrassProbe) RaytracingProbe();
                if (FeedConfig.GrassProbe) StreamingProbe();
                if (FeedConfig.GrassProbe) StreamingBudgetProbe();
                if (FeedConfig.GrassProbe) AmbientSourceProbe();

                string after = LdrRes(ourLdr);
                if (before != after || (_lastLdrRes != null && _lastLdrRes != before))
                    RttLog.Line($"!!! FinalLDR resolution moved: lastRender='{_lastLdrRes ?? "n/a"}' " +
                                $"beforeDraw='{before}' afterDraw='{after}' (render #{_renderCount}). " +
                                (before != after
                                    ? "OUR nested Draw resized it — the upscale tail is using player display state."
                                    : "It moved BETWEEN our renders — an engine-side path touched our instance."));
                _lastLdrRes = after;

                // The image now sits in our FinalLDRTexture. Delivery to the panel is
                // NOT done here — parking it directly was tried and CTD'd:
                // CopyCommandList.Replay threw E_INVALIDARG, because the raw
                // CopyTextureSubresource path chokes on a resizable engine-internal
                // texture where the probe ring's plain pool textures copy fine. Instead
                // CameraRender's proven blit takes PanelSource as its CopyJob source and
                // the ring/parking machinery stays untouched — the exact pattern its
                // tonemap scratch target already uses in production.

                if (_renderCount == 1)
                    RttLog.Line("=== WHOLE-SCENE RENDER SURVIVED THE FIRST CALL. The engine's entire " +
                                "renderer just ran a second time this frame, into buffers we own. ===");
            }
            finally
            {
                // Unconditional, and in reverse install order: the camera CB first (it
                // must go back inside this same frame bracket — OnEndDraw disposes
                // whatever is in the field), then camera, scoped settings groups, and
                // both global families the engine's next frame renders through.
                if (ownShadows) EndOwnShadows();
                if (savedCb != null)
                {
                    try
                    {
                        CameraCbSwap.Restore(savedCb);
                        // Only now is ours out of the field and nobody's to free but us.
                        // A restore that THREW leaves ours installed, and OnEndDraw will
                        // dispose it — staging it there would be a double-free.
                        StageCb(ourCameraCb);
                    }
                    catch (Exception e) { RttLog.Error("whole-scene CB restore", e); }
                }
                else
                {
                    // Built but never installed (Install skips outside an OnBeginDraw
                    // bracket), so it was orphaned the moment we made it.
                    StageCb(ourCameraCb);
                }
                if (camSwapped) RestoreCamera(savedCam);
                // After RestoreCamera and before anything else: the rebuild reads the
                // installed view, and this run regenerates the planet sort, the
                // weather-modifier culling and all the setup CBs from the PLAYER'S view.
                if (planetEnvSwapped) RestorePlanetEnv();
                RestoreScoped();
                // Before the DrawContextManager goes back, so the ordering mirrors install.
                // The FieldInfo is re-read rather than trusted: a deferred Reset cannot null
                // it any more, but this restore is the one whose failure leaves OUR probe
                // manager installed in the player's frame, so it carries its own guard.
                // OUR TLAS goes back FIRST — installed last, restored first, so the unwind is
                // the exact mirror of the install. Leaving ours installed would have the
                // player's own frame trace an acceleration structure built around the feed
                // camera, which is the same class of damage as leaving our exposure history in
                // place, and it clears _rtSceneInstalled so stage 0 stops being force-run the
                // instant we are no longer the ones rendering.
                // OUR IRRADIANCE GRID goes back before even the TLAS — installed last,
                // restored first. Leaving ours in place would have the player's frame sample
                // a grid populated at the feed camera, which IS the contamination this whole
                // feature exists to remove; a failed restore here would recreate the bug in a
                // harder-to-see form, so it is the very first thing the unwind does.
                RestoreIRCache(savedIrCache);

                RestoreRayTracingScene(savedRtScene);

                if (savedProbes != null)
                {
                    try
                    {
                        var pf = _probeField ?? Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12")
                            ?.GetField("EnvironmentProbeManager", BindingFlags.Public | BindingFlags.Static);
                        pf?.SetValue(null, savedProbes);
                    }
                    catch (Exception e) { RttLog.Error("restore probe manager", e); }
                }
                // The eye-adaptation job goes back BEFORE the draw contexts, mirroring install
                // in reverse. Its failure mode is the worst one available here — our histogram
                // left installed in the player's frame would adapt the PLAYER's exposure to
                // OUR camera — so it restores unconditionally and swallows nothing silently.
                RestoreEyeAdaptation(sceneDrawSystem, savedEyeJob);

                if (savedDc != null) _dcField.SetValue(null, savedDc);
                _sbField.SetValue(null, savedSb);
                _inOurRender = false;

                // Every swap is unwound, so everything staged above is now unreachable from
                // any engine field. Frees the PREVIOUS render's batch and rotates this one
                // in behind it — see the reclaim block for why one render of extra life.
                ReclaimStagedCbs();

                // Now that every swap is unwound and _inOurRender is clear, a Reset that
                // arrived mid-render can safely run.
                if (_resetPending)
                {
                    _resetPending = false;
                    RttLog.Line("Whole-scene: running the deferred Reset now that the render has unwound.");

                    // ACROSS EVERY FEED, not just the one that was rendering (phase C3
                    // prerequisite). A deferred Reset comes from a config signature change,
                    // and quality is GLOBAL by design — see docs/phase2-design.md: it is the
                    // user's VRAM throttle, not a per-feed property. So a resolution change
                    // has to rebuild ALL of them, or the feeds that did not happen to hold
                    // the render slot when Poll() fired would keep ScreenBuffers at the old
                    // size indefinitely, and nothing would ever tell us. At Count == 1 this
                    // is the single Reset it always was.
                    //
                    // ForEachSlot: Reset RELEASES, so it must reach slots that have just
                    // dropped out of Count — a signature change is exactly how feedCount
                    // shrinks, and the feed being retired is the one still holding a
                    // ScreenBuffers nothing will ask for again.
                    Feeds.ForEachSlot(Reset);
                }
            }
        }
        catch (Exception e)
        {
            // NOT EVERY FAULT DESERVES THE SESSION LATCH. This catch used to be one strike
            // and out, which turned feed 1 into a zombie twice on 2026-08-06: its FIRST
            // render NRE'd in ExecuteAccelerationStructuresBuilding — stage 0, the TLAS
            // build our own-RT-scene feature force-runs — and the route died for the
            // session while the gate stayed ACTIVE and the panel stayed black
            // (request=25/s, park#0 forever).
            //
            // A stage-0 throw is the FEATURE failing, not the route: a feed without a local
            // TLAS renders exactly as every feed did before the feature existed. So disarm
            // own-RT-scene FOR THIS FEED (the state is per-feed precisely so this cannot
            // touch the other feeds) and keep rendering. Anything else gets a bounded
            // retry — engine state is restored by the unwind either way, proven by the
            // player's view surviving both of tonight's faults — and only repeated failure
            // latches the route, because "the feed silently never came back" is the least
            // debuggable failure this project produces.
            bool stage0 = e.StackTrace?.Contains("AccelerationStructures") == true;
            if (stage0 && _rtSceneState >= 0)
            {
                _rtSceneState = -1;
                RttLog.Error($"whole-scene render — stage 0 (TLAS build) threw, so OWN RT SCENE is DISARMED " +
                             $"for feed {Feeds.Cur.Id}; the ROUTE CONTINUES without it", e);
                return;
            }
            if (++Feeds.Cur.WholeSceneFaults < 3)
            {
                RttLog.Error($"whole-scene render (fault {Feeds.Cur.WholeSceneFaults}/3 for feed " +
                             $"{Feeds.Cur.Id} — retrying on a later slot)", e);
                return;
            }
            _state = -1;
            RttLog.Error("whole-scene render (route DISABLED for this session — third fault)", e);
        }
    }

    // Scope a settings group off for the duration of our render, and remember how to
    // put it back.
    //
    // GENERALISED ON PURPOSE. The first of these was raytracing, written bespoke; the
    // second (eye adaptation) arrived within minutes, exactly as predicted. Every
    // SettingsManager group is a STRUCT in a private backing field, so they all take the
    // same treatment: box it twice, clear the flags on one, restore the other afterwards.
    // Writing a new method per group would be five copies of this by morning.
    //
    // The saved boxes are stacked and unwound in reverse, so a group added later cannot
    // disturb the restore order of one added earlier.
    private static readonly List<(FieldInfo Field, object Saved)> _scoped = new();

    // THE SETTINGS-GROUP LOOKUP, resolved once per group name instead of once per scope per
    // render.
    //
    // ScopeOff and ScopeSetValues are called about TEN TIMES PER RENDER, at ~40 renders a
    // second, and each call used to do: an assembly-qualified Type.GetType, a GetField for
    // "Settings", then settings.GetType().GetFields(Any) — which ALLOCATES an array of every
    // field on SettingsManager — and a LINQ FirstOrDefault over it with a capturing closure.
    // That is roughly 400 throwaway arrays and 400 closures a second, on the RENDER THREAD,
    // to answer a question whose answer is fixed for the process.
    //
    // Nothing here can change at runtime: CoreSystems.Settings is a static, and the field
    // that holds a given settings STRUCT TYPE on SettingsManager is fixed by the assembly.
    // So both are cached — the settings object once, and the group field per type name.
    private static readonly Dictionary<string, FieldInfo> _settingsFieldCache = new();

    private static FieldInfo ResolveSettingsField(string settingsTypeName, out object settings)
    {
        settings = null;
        if (_settingsObj == null)
        {
            _coreType ??= Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            _settingsObj = _coreType?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)
                                    ?.GetValue(null);
            if (_settingsObj == null) return null;
        }
        settings = _settingsObj;

        // A null ENTRY is cached too, deliberately: a group this build does not have must not
        // re-scan every field of SettingsManager forty times a second forever just to fail
        // again. TryGetValue distinguishes "cached miss" from "not looked up yet".
        if (_settingsFieldCache.TryGetValue(settingsTypeName, out var cached)) return cached;

        FieldInfo found = null;
        foreach (var f in settings.GetType().GetFields(Any))
            if (f.FieldType.Name == settingsTypeName) { found = f; break; }
        _settingsFieldCache[settingsTypeName] = found;
        return found;
    }

    private static void ScopeOff(string settingsTypeName, string label, params string[] flags)
    {
        try
        {
            var field = ResolveSettingsField(settingsTypeName, out var settings);
            if (settings == null) return;
            if (field == null)
            {
                if (_scopeWarned.Add(settingsTypeName))
                    RttLog.Line($"Whole-scene: SettingsManager has no {settingsTypeName} field — " +
                                $"{label} stays live during our render and will keep leaking into the " +
                                "player's frame.");
                return;
            }

            var saved = field.GetValue(settings);
            var ours = field.GetValue(settings);        // struct field: a second, independent box

            int set = 0;
            foreach (var n in flags)
                if (ClearBool(ours, n)) set++;
            if (set == 0)
            {
                if (_scopeWarned.Add(settingsTypeName))
                    RttLog.Line($"Whole-scene: no matching flags on {settingsTypeName} for {label}.");
                return;
            }

            field.SetValue(settings, ours);
            _scoped.Add((field, saved));

            // Key on the LABEL as well as the type. Keying on the type alone meant two
            // different scopes of the same settings group shared one "already logged"
            // entry — so switching wholeSceneDisableRaytracing from mode 1 to mode 2
            // silently reused mode 1's key and printed nothing, while the config bisect
            // it was there to document was the entire point of the exercise. A log that
            // cannot distinguish the thing under test is worse than no log.
            if (_scopeWarned.Add(settingsTypeName + "|" + label + ":ok"))
                RttLog.Line($"Whole-scene: {label} disabled for our render ({set}/{flags.Length} flags " +
                            $"cleared on {settingsTypeName}).");
        }
        catch (Exception e) { RttLog.Error($"whole-scene scope off {settingsTypeName}", e); }
    }

    // Clear a bool on a boxed struct, following a dotted path through nested structs.
    //
    // Needed because the interesting flags are not all at the top level:
    // EnvironmentSettings.ProbeSettings.Enable is two deep. Nested STRUCTS do not
    // behave like nested objects — GetValue on a struct field returns a COPY, so
    // mutating it changes nothing unless the copy is written back at every level on
    // the way out. That is what the recursion is for, and getting it wrong would look
    // exactly like "the flag had no effect".
    // Resolve a settings member by name, falling back to the AUTO-PROPERTY BACKING FIELD.
    //
    // Needed because these settings structs mix plain fields with auto-properties, and the
    // two are indistinguishable at the call site: RaytracingSettings.EnableIRCache is a
    // field, SSSRSettings.EnableTemporalAccumulation is a property whose only storage is
    // `<EnableTemporalAccumulation>k__BackingField`. Without this fallback the property ones
    // silently resolve to null and ScopeOff reports "no matching flags" — a miss that reads
    // like "this build does not have that setting" rather than "we looked up the wrong name".
    //
    // Writing the backing field directly is correct here for the same reason ClearBool works
    // at all: these are plain data structs, and the property is a trivial accessor over that
    // one field. Passing the mangled name at the call site would work too, but it would put
    // an unreadable string in the one place a reader goes to see WHICH setting is scoped.
    private static FieldInfo ResolveMember(Type t, string name)
        => t.GetField(name, Any) ?? t.GetField("<" + name + ">k__BackingField", Any);

    private static bool ClearBool(object box, string path)
    {
        try
        {
            int dot = path.IndexOf('.');
            if (dot < 0)
            {
                var f = ResolveMember(box.GetType(), path);
                if (f == null || f.FieldType != typeof(bool)) return false;
                f.SetValue(box, false);
                return true;
            }

            var outer = ResolveMember(box.GetType(), path.Substring(0, dot));
            if (outer == null) return false;

            var inner = outer.GetValue(box);            // a COPY when it is a struct
            if (inner == null) return false;
            if (!ClearBool(inner, path.Substring(dot + 1))) return false;

            outer.SetValue(box, inner);                 // write the mutated copy back
            return true;
        }
        catch { return false; }
    }

    // Set one field on a boxed settings struct, following a dotted path into nested
    // structs. Same shape as ClearBool: a struct field read through reflection is a COPY,
    // so every level has to be written back on the way out or the mutation is discarded.
    private static bool SetPath(object box, string path, object value)
    {
        if (box == null) return false;
        int dot = path.IndexOf('.');
        if (dot < 0)
        {
            var f = ResolveMember(box.GetType(), path);
            if (f == null) return false;
            object v = value;
            if (f.FieldType.IsEnum && value is int iv) v = Enum.ToObject(f.FieldType, iv);
            else if (f.FieldType == typeof(float)) v = System.Convert.ToSingle(value);
            else if (f.FieldType == typeof(int)) v = System.Convert.ToInt32(value);
            else if (!f.FieldType.IsInstanceOfType(value)) return false;
            f.SetValue(box, v);
            return true;
        }

        var outer = ResolveMember(box.GetType(), path.Substring(0, dot));
        if (outer == null) return false;
        var inner = outer.GetValue(box);
        if (inner == null || !SetPath(inner, path.Substring(dot + 1), value)) return false;
        outer.SetValue(box, inner);
        return true;
    }

    // Same box-copy-restore as ScopeOff, but SETS values — enums, floats, bools — so a
    // settings group can be retuned for our render rather than only having flags
    // cleared. Chained boxes compose: a group already scoped this pass is re-boxed from
    // its current (already-modified) value, and the reverse unwind restores the true
    // original last.
    private static void ScopeSetValues(string settingsTypeName, string label,
        params (string Field, object Value)[] sets)
    {
        try
        {
            var field = ResolveSettingsField(settingsTypeName, out var settings);
            if (settings == null) return;
            if (field == null)
            {
                if (_scopeWarned.Add(settingsTypeName + label))
                    RttLog.Line($"Whole-scene: SettingsManager has no {settingsTypeName} field — {label} unavailable.");
                return;
            }

            var saved = field.GetValue(settings);
            var ours = field.GetValue(settings);        // struct field: independent box

            int applied = 0;
            foreach (var (name, value) in sets)
            {
                try { if (SetPath(ours, name, value)) applied++; }
                catch { }
            }
            if (applied == 0)
            {
                if (_scopeWarned.Add(settingsTypeName + label))
                    RttLog.Line($"Whole-scene: no matching fields on {settingsTypeName} for {label}.");
                return;
            }

            field.SetValue(settings, ours);
            _scoped.Add((field, saved));

            if (_scopeWarned.Add(settingsTypeName + label + ":ok"))
                RttLog.Line($"Whole-scene: {label} set for our render ({applied}/{sets.Length} fields on {settingsTypeName}).");
        }
        catch (Exception e) { RttLog.Error($"whole-scene scope set {settingsTypeName}", e); }
    }

    private static void RestoreScoped()
    {
        for (int i = _scoped.Count - 1; i >= 0; i--)
        {
            try { _scoped[i].Field.SetValue(_settingsObj, _scoped[i].Saved); }
            catch (Exception e) { RttLog.Error("whole-scene restore scoped setting", e); }
        }
        _scoped.Clear();
    }

    private static readonly HashSet<string> _scopeWarned = new();

    // Everything our render must not advance on the player's behalf.
    //
    // Each entry here was a visible defect in the PLAYER'S view, not ours — which is the
    // signature of this whole class of problem. Owning a ScreenBuffers isolates image
    // state; it does nothing for state that integrates in world space or across frames.
    private static void ScopeSharedState()
    {
        // RAYTRACING / GI. Draw builds acceleration structures and steps ReSTIR and the
        // IR cache, all of which integrate over frames against the WORLD. Running the
        // pipeline twice per frame advanced them twice: the player's GI went patchy and
        // shifting. Not corruption — over-integration.
        // MODE 1 clears Enabled as well, which stops RaytraceGIJob running at all — and
        // ComputeGI borrows its DiffuseGIBuffer WITHOUT a clear value:
        //
        //     BorrowResizableRWRenderTargetTexture("DiffuseGIBuffer", 26, res,
        //                                          null, 1, /* clear */ null, 0)
        //
        // Safe for the engine, because RaytraceGIJob normally overwrites every pixel.
        // With it skipped, AmbientLightJob reads a RECYCLED, UNCLEARED pool texture whose
        // contents change every frame — which is the ambient flashing on shadowed sides,
        // where the ambient term dominates.
        //
        // MODE 2 keeps Enabled TRUE so the GI job still writes those buffers, and clears
        // only the accumulators that integrate in world space across frames — which were
        // the actual cause of the player's-view GI going patchy. Best of both, in theory:
        // stable feed ambient, player's history still frozen.
        // An explicit flag list beats the presets, which only ever reached six of the
        // twenty booleans on RaytracingSettings. Mode 1 (clearing Enabled) turned out to
        // cause the BRIGHT flashing all by itself — RaytraceGIJob keys a
        // LazyJobSnapshotHandler<RTGISettings, RTGISnapshot> off these settings and builds
        // shader defines from them, so toggling the wrong one forces a pipeline rebuild
        // ten times a second. Mode 2 removed that, but left the subtle per-light flicker,
        // which points at flags no preset touches: LocalLightsInIRCache,
        // LocalLightsInRTXGI, and the EnableReSTIR master that still lets candidates be
        // written into the shared reservoirs.
        if (FeedConfig.WholeSceneRtFlags.Length > 0)
            ScopeOff("RaytracingSettings",
                     "raytracing flags [" + string.Join(",", FeedConfig.WholeSceneRtFlags) + "]",
                     FeedConfig.WholeSceneRtFlags);
        else if (FeedConfig.WholeSceneDisableRaytracing == 1)
            ScopeOff("RaytracingSettings", "raytracing (full, incl. Enabled)",
                     "Enabled", "EnableTemporalReSTIR", "EnableSpatialReSTIR",
                     "EnableTemporalFilter", "EnableIRCache", "EnableIRCacheScrolling");
        else if (FeedConfig.WholeSceneDisableRaytracing == 2)
            ScopeOff("RaytracingSettings", "raytracing accumulators only (GI buffers still written)",
                     "EnableTemporalReSTIR", "EnableSpatialReSTIR",
                     "EnableTemporalFilter", "EnableIRCache", "EnableIRCacheScrolling");

        // SCREEN-SPACE REFLECTIONS — localise them to the feed camera.
        //
        // Reported by the user 2026-08-04: "screen spaced reflections are also clearly taking
        // from the main world render and not the feed's perspective". That is the same
        // mechanism already documented as stage 29 (THE PHANTOM BLEED) seen from the other
        // side — the bleed was noticed first as OUR scene appearing on the PLAYER'S reflective
        // surfaces, and it is one shared history, so it was always contaminating both.
        //
        // READ THE IL BEFORE ASSUMING THIS IS THE SAME TRAP AS RaytracingSettings. It is not,
        // and that distinction is the whole reason this is a one-flag fix rather than a
        // second RayTracingSceneManager:
        //
        //   RaytracingSettings  -> RaytraceGIJob keys a LazyJobSnapshotHandler off it and
        //                          BuildTraceShaderDefines turns fields into shader defines,
        //                          so flipping one per render is an async PSO compile at our
        //                          cadence. That is the bright flashing, and it took the
        //                          device twice.
        //   SSSRSettings        -> ScreenSpaceReflections.DoWork reads CoreSystems.Settings
        //                          .SSSR DIRECTLY and consumes the fields inline. No
        //                          snapshot, no defines, no PSO rebuild. Its PSOs are built
        //                          once in InitializeAsync.
        //
        // So SSSRSettings is safe to scope per-pass, and RaytracingSettings still is not.
        //
        // WHY THIS ONE FLAG IS ENOUGH. EnableTemporalAccumulation gates the ENTIRE denoiser
        // block in DoWork (the branch at IL_047a jumps the whole thing), and that block is
        // where all three contaminations live:
        //
        //   1. it READS the shared RadianceHistory / VarianceHistory / SampleCountHistory /
        //      AverageRadianceHistory — the player's accumulated radiance, which is what makes
        //      our reflections show the main world;
        //   2. it WRITES our radiance back into them, which is the original phantom bleed;
        //   3. its last act is `stfld _previousViewProjection` — our camera's view-projection
        //      stamped onto the job, so the PLAYER'S next frame reprojects its reflection
        //      history through OUR camera. That one is a defect in the player's view that
        //      nobody had attributed yet.
        //
        // Clearing it for our pass alone removes all three at once, and the reflections are
        // still APPLIED: the false path falls through to _applyReflectionsJob using the raw
        // intersection result. So the feed keeps screen-space reflections from its own
        // perspective — undenoised and noisy rather than temporally accumulated.
        //
        // That trade is deliberate and was authorised: "I'd be happy with a comparatively low
        // resolution and noise RT based ambient lighting and reflection solution to save on
        // cost." It also COSTS LESS than the status quo, because skipping the denoiser skips
        // its dispatches — this is the rare fix that is cheaper as well as more correct.
        //
        // NOT A REBUILD-SIGNATURE KNOB: this is a scoped setting evaluated per render, not a
        // resource allocation, so it applies live from the config file with no park cycle.
        if (FeedConfig.WholeSceneSsrLocal)
            ScopeOff("SSSRSettings",
                     "SSR temporal accumulation (localises reflections to the feed camera; " +
                     "costs denoising, so they are noisy)",
                     "EnableTemporalAccumulation");

        // EYE ADAPTATION. ComputeExposure drives EyeAdaptationJob, which ping-pongs a
        // shared auto-exposure history — this project already recorded that running it a
        // second time per frame is unsafe. Our 512x512 view of the same scene has a
        // different average luminance, so the player's adaptation oscillates between the
        // two exposures: lighting flickering at exactly our render cadence.
        //
        // Exposure itself is left ON so our image is still exposed; only the TEMPORAL
        // adaptation is cut, which for a fixed-purpose camera feed is arguably correct
        // anyway.
        // NOT WHEN WE OWN THE HISTORY. Scoping EyeAdaptation off exists solely to stop our
        // render advancing the SHARED adaptation state — with our own job installed there is
        // no shared state to protect, and clearing the flag would make our own DynamicExposure
        // never run, so the feature would silently be a no-op with no error to explain it.
        // Same coupling, and the same reasoning, as own-probes vs ProbeSettings.Enable: the
        // two settings are coupled, so the coupling is enforced here rather than left to the
        // config file where the pair could be set inconsistently.
        if (FeedConfig.WholeSceneDisableEyeAdaptation && !FeedConfig.WholeSceneOwnEyeAdaptation)
            ScopeOff("PostProcessSettings", "eye adaptation", "EyeAdaptation");

        // AND WHEN WE DO OWN IT, TURN IT ON — not merely stop turning it off.
        //
        // The first version only removed the ScopeOff above and assumed that was enough. It
        // is not: ComputeExposure chooses between EyeAdaptationJob.DynamicExposure and
        // .ConstantExposure on this flag, and the PLAYER's setting is whatever the player's
        // graphics options say. If it is false globally then our own job is installed,
        // verified initialised, and never called — the feature would be perfectly plumbed and
        // completely inert, which is the hardest kind of failure to see because every log line
        // says success.
        //
        // Scoped, so the player's own frame keeps their setting untouched.
        if (FeedConfig.WholeSceneOwnEyeAdaptation)
            ScopeSetValues("PostProcessSettings",
                "feed eye adaptation ON (we own the history, so DynamicExposure is safe to run)",
                ("EyeAdaptation", true));

        // HZBO — HIERARCHICAL-Z OCCLUSION CULLING, for our pass only.
        //
        // THE SYMPTOM THIS EXISTS TO TEST, reported by the user 2026-08-02: shadows of trees
        // and foliage visible in the feed with NO OBJECT PRESENT. That is not a missing
        // object — something has to cast a shadow. It means the entity is in the shadow
        // pass's set and the MAIN VIEW is dropping it, and occlusion culling is the one
        // mechanism that can disagree between those two passes.
        //
        // HZBO tests objects against a depth pyramid built from the CURRENT depth buffer. If
        // that pyramid does not correspond to our camera, geometry is rejected as "behind
        // something" and vanishes from the main view while its shadow, drawn in the cascade
        // pass which does not consult main-view HZBO, survives. Exactly the reported picture.
        //
        // AND IT WOULD EXPLAIN THE GRASS TOO, which is why this is worth a knob rather than a
        // guess. RenderGBuffer passes hzboMainViewEnabled straight into RenderGrass as its
        // enableHiZ argument, and GrassRendering ships explicit NoHiZ PSO variants — so a bad
        // pyramid does not thin grass, it removes all of it. One mechanism, both symptoms.
        //
        // COSTS DRAW CALLS in the feed and nothing else: no occlusion culling means more
        // geometry submitted. Our CPU submit sits around 2.8 ms, so there is room. Scoped, so
        // the player's own frame keeps its occlusion culling untouched.
        //
        // Only MainViewEnabled is cleared, not Enabled: the cascade half (CascadesEnabled)
        // is a different consumer and there is no reason to disturb it while testing this.
        if (FeedConfig.WholeSceneNoHzbo)
            ScopeOff("HZBOSettings", "HZBO main-view occlusion culling", "MainViewEnabled");

        // ENVIRONMENT PROBES. Reported as "reflections or ambient lighting from light
        // sources, but not the lights themselves" — which is indirect lighting exactly,
        // and that is what probes supply.
        //
        // The engine updates probe faces round-robin across frames into a SHARED atlas,
        // driven by DrawContextManager.EnvProbesToUpdate. Our second Draw calls
        // RenderEnvironmentProbe as well, so we both advance that queue at double rate
        // AND write probe faces using our settings — with raytracing already scoped off.
        // The player's frame then samples that atlas for ambient and reflections, which
        // is why the symptom is indirect light and not direct.
        //
        // ProbeSettings.Enable is two levels deep (EnvironmentSettings.ProbeSettings),
        // hence the dotted path. ApplyEnvProbe is deliberately NOT cleared: we want our
        // render to keep USING the probes for ambient, just not to update them. If the
        // feed loses its ambient term anyway, Enable gates both and this needs splitting.
        // NOT when we own the probes. Scoping Enable off exists solely to stop our render
        // updating the SHARED atlas; with our own manager installed there is no shared atlas
        // to protect, and leaving it off would make our own PrepareProbes do nothing — the
        // feature would silently be a no-op with no error to explain it. The two settings are
        // coupled, so the coupling is enforced here rather than left to the config file.
        if (FeedConfig.WholeSceneDisableProbeUpdates && !FeedConfig.WholeSceneOwnProbes)
            ScopeOff("EnvironmentSettings", "environment probe updates", "ProbeSettings.Enable");

        // TEMPORAL AA / UPSCALING. FSR consumes motion vectors, and ours are garbage:
        // the second view's frames are 200ms apart and its previous-frame camera state
        // was the PLAYER'S — ghost trails and edge smear on everything that moves.
        // AAMode: None=0, FXAA=1 (spatial-only, needs no motion vectors — the right AA
        // for this feed), FSR=2 (engine default). ScalingMode NativeAA=4 removes the
        // upscale entirely; sharpening off because it amplifies 512px artefacts.
        // AA MODE — and the reason our render must NOT go through FSR.
        //
        // CORRECTION. This comment used to say DRS was not switchable per-render because
        // AAMode "selects between UpscaleTargetFSR and ApplyNonFSRUpscalingAndAA" at the
        // caller, making them non-interchangeable producer/consumer branches. That model
        // was WRONG. ExecutePostPasses calls BOTH, unconditionally, in sequence:
        //
        //     PatchHoles -> ComputeExposure -> UpscaleTargetFSR -> ApplyBloom
        //         -> ApplyToneMapping -> ApplyNonFSRUpscalingAndAA -> DrawUI
        //
        // Each self-gates internally. And UpscaleTargetFSR's own head is:
        //
        //     bool work = finalLDRBuffer.Resolution != ScreenBuffers.PreUpscaleResolution
        //                 || Settings.IsFSREnabledAndAllowed;
        //     tempLDRBuffer = default; tempHDRBuffer = default;
        //     if (!work) { toneMappingOutput = finalLDRBuffer; toneMappingInput = lBuffer; return; }
        //
        // — an early-out that DOES set both out-params bloom and tonemap consume. Our
        // final target and our ScreenBuffers are both 512x512, so the resolution term is
        // false for us and this early-out is reachable the moment FSR is off.
        //
        // WHY IT MATTERS. IsFSREnabledAndAllowed is just `DRS.AAMode == 2 && debugViewOk`,
        // read off the PLAYER's settings, so today our nested render takes the FSR path:
        // it borrows a TempHDRBuffer at SwapChain.Resolution (the player's 4K, not ours)
        // and dispatches the SHARED FSR3 upscaler. That upscaler is one instance —
        // SceneDrawSystem._upsamplingJob holds a single FSR3_1, whose context, history,
        // FSR3ReactiveMask and FSR3TransparencyCompositionMask are global. Two cameras,
        // one temporal accumulator. The transparency-composition mask is exactly the
        // mechanism by which opaque geometry gets composited as partly see-through, which
        // is the reported "ship is semi-transparent and the skybox shows through it".
        //
        // AAMode: 0 none, 1 FXAA (spatial-only), 2 FSR (engine default). Anything but 2
        // takes us off the shared upscaler entirely.
        //
        // Three earlier CTDs sat behind this knob and are NOT explained away by the
        // above — but two were bundled with other changes, and the third was tested
        // against this wrong model. Worth one clean attempt now that the structure is
        // understood; if it faults again, the next suspect is ScreenBuffers.Update, which
        // also reads IsFSREnabledAndAllowed and has never run on OUR instance.
        if (FeedConfig.WholeSceneAAMode >= 0)
            ScopeSetValues("DRSSettings", $"feed AA mode {FeedConfig.WholeSceneAAMode} (off the shared FSR upscaler)",
                ("AAMode", FeedConfig.WholeSceneAAMode));

        if (FeedConfig.WholeSceneNativeScaling)
            ScopeSetValues("DRSSettings", "feed native scaling (UNSAFE — see comment)",
                ("ScalingMode", 4), ("EnableSharpening", false));

        // FEED EXPOSURE, in EV stops. Adaptation is scoped off (shared history), so the
        // feed runs ConstantExposure.hlsl, whose whole output is
        // exp2(log2(keyValue/ConstantLuminance) + LuminanceExposure) — and with
        // ConstantLuminance == 1 the first term is zero. So this field is a pure signed
        // EV offset on unity: +1 doubles, -1 halves. See FeedConfig.WholeSceneExposure.
        //
        // Gate is `!= 0`, not `> 0`: 0 is the engine's own value AND the neutral EV, so it
        // means "leave alone" for free, while negative values stay reachable. The label
        // carries the value so a re-tune re-logs (the once-per-label guard is by string).
        //
        // AUTO APERTURE takes precedence when it is on and has a sun to work from; the fixed
        // value below is the fallback and the manual override. The label is deliberately
        // CONSTANT here — the once-per-label guard is by string, and an auto value moves every
        // frame, so a value-carrying label would log at render rate. AutoExposureEv does its
        // own rate-limited reporting instead.
        double ev = FeedConfig.WholeSceneExposure;
        if (FeedConfig.FeedAutoExposure && TryAutoExposureEv(out double autoEv)) ev = autoEv;

        // DO NOT FORCE A STOP WHEN WE OWN THE ADAPTATION. This is stated as an ISOLATION TEST
        // rather than a fix, because I do not yet know whether DynamicExposure even reads
        // LuminanceExposure — the comment above establishes it for the CONSTANT path only.
        //
        // What is known: with our own EyeAdaptationJob installed, verified initialised and
        // EyeAdaptation scoped on, the feed still looked identical — as washed out as it did
        // on a fixed stop. Two readings fit that, and they need different fixes:
        //   the fixed -2 is layered ON TOP of the adapted value and pins it, or
        //   the dynamic path ignores this field and the washed-out image IS the adapted result
        //   (which would point at the histogram source, not at this).
        // Forcing nothing here separates them: if the image changes, the override was the
        // problem; if it does not, the adaptation itself is and this line was never involved.
        //
        // The fixed stop remains the fallback whenever we are NOT running our own adaptation,
        // which is the pre-existing behaviour and the safe default.
        // THE DARKENING OFFSET, and it is deliberately a SEPARATE knob from the fixed stop.
        //
        // wholeSceneExposure is the fixed-stop FALLBACK for when we are not adapting; layering
        // a second meaning onto it would make "the feed is dark" ambiguous between "adaptation
        // settled there" and "someone pinned it". wholeSceneExposureOffset instead biases the
        // ADAPTED result: negative = darker, and adaptation keeps working underneath.
        //
        // IT ALSO SETTLES THE OPEN QUESTION IN THE COMMENT ABOVE. Nobody has established
        // whether LuminanceExposure layers on top of the adapted value or is ignored by the
        // dynamic path. With adaptation ON and a non-zero offset: if the feed darkens it
        // layers, and this is a real exposure bias; if nothing changes it is ignored and the
        // adaptation result is the whole story. Either answer is worth having, so the log says
        // which one to look for rather than leaving it to be re-derived a third time.
        var evOffset = FeedConfig.WholeSceneExposureOffset;
        if (FeedConfig.WholeSceneOwnEyeAdaptation && evOffset != 0)
        {
            ScopeSetValues("PostProcessSettings", "feed exposure DARKENING OFFSET (adaptation stays live)",
                ("LuminanceExposure", (float)evOffset));
            if (_scopeWarned.Add("evOffsetArmed"))
                RttLog.Line($"Whole-scene: exposure offset {evOffset:0.###} EV applied on top of the feed's own " +
                            "adaptation. IF THE IMAGE DOES NOT CHANGE, LuminanceExposure is ignored by the dynamic " +
                            "path and the adapted result is the whole story — that is a finding, not a failed knob.");
        }

        if (ev != 0 && !FeedConfig.WholeSceneOwnEyeAdaptation)
            ScopeSetValues("PostProcessSettings", "feed exposure (auto aperture)",
                ("LuminanceExposure", (float)ev));
        else if (ev != 0 && evOffset == 0 && _scopeWarned.Add("evYieldsToAdaptation"))
            RttLog.Line($"Whole-scene: NOT forcing LuminanceExposure {ev:0.###} — wholeSceneOwnEyeAdaptation is on, " +
                        "so the feed's own DynamicExposure decides the stop. If the feed looks unchanged from the " +
                        "fixed-stop version, the override was never what pinned it and the histogram source is the " +
                        "next suspect.");

        // BLOOM. Candidate for the phantom bleed, and the only remaining shared object in
        // the composite tail.
        //
        // BloomJob holds _tmpBloomCascadeDown / _tmpBloomCascadeUp — arrays of BORROWED
        // textures retained across calls — plus _tmpMaxCascadeResolutions, a cached
        // per-cascade resolution. The job is SceneDrawSystem._bloomJob, the singleton we do
        // not swap, so our 512x512 render and the player's 4K frame drive the same cascade
        // set with different resolutions. That is the CloudJob shape, and bloom output is
        // additive, blurry and full-screen — which is what the ghost looks like.
        //
        // Scoped rather than skipped, deliberately. ApplyBloom's signature is
        // (..., out Borrowed bloom): skipping BloomJob.DoWork would leave that out-param
        // unset, the same NRE that makes stage 4 unskippable. With this flag false the
        // engine takes its OWN disabled path, which borrows a 1x1 black bloom — designed,
        // and safe.
        //
        // Rule 11 says settings scopes leak. Checked first: PostProcessSettings.Bloom is a
        // plain field read inside ApplyBloom and feeds no shader define (the only BLOOM
        // strings in the assembly are shader FILE PATHS), so it cannot trigger the async
        // PSO rebuild that made the RaytracingSettings scopes dangerous.
        //
        // Cost to the feed while on: no bloom in the feed.
        if (FeedConfig.WholeSceneNoBloom)
            ScopeSetValues("PostProcessSettings",
                "feed bloom OFF (shared BloomJob retains its cascade borrows across renders)",
                ("Bloom", false));

        // FLARE INTENSITY, feed only. GetFlareConstants reads FlaresIntensity straight into
        // FlaresConstantData.IntensityMultiplier, so this reaches the flare pass and nothing
        // else. Only meaningful while wholeSceneOwnFlares is on — with flares off the pass
        // never runs and the scope is a no-op, which is harmless and not worth a gate on.
        // See FeedConfig.WholeSceneFlareIntensity for why this and not emissivity.
        if (FeedConfig.WholeSceneFlareIntensity >= 0)
            ScopeSetValues("LightSettings",
                $"feed flare intensity {FeedConfig.WholeSceneFlareIntensity:0.###} " +
                "(fixed feed exposure cannot pull a blown flare back, and the panel multiplies by emissivity)",
                ("FlaresIntensity", (float)FeedConfig.WholeSceneFlareIntensity));

        ScopeScatter();
    }

    // ---- THE SCATTER CONTROL SURFACE, per-pass half -----------------------------------
    //
    // Flora LOD, general object LOD and grass, given to the FEED alone. Every value here is
    // read by a consumer that runs inside our nested Draw, which is what makes per-feed
    // control possible at all — see FeedConfig's scatter section for the consumer-by-consumer
    // evidence and for why the SPAWN RADIUS is not in this list.
    //
    // WHY THIS IS SAFE WHERE THE RAYTRACING SCOPES WERE NOT. Scoping RaytracingSettings was
    // dangerous because RaytraceGIJob builds SHADER DEFINES from those flags, so toggling
    // them ten times a second forced async pipeline rebuilds. LODSettings and GrassSettings
    // instead implement Convert(IGPUDataConvertor) with a GPUImprintSize — they are uploaded
    // as CONSTANT BUFFER data, which is per-frame by design and carries no PSO cost.
    //
    // Everything defaults to "do not scope". A knob that is off must cost nothing and must
    // not appear in the log, or the log stops being readable.
    private static void ScopeScatter()
    {
        // FLORA, our render only. Two independent knobs on the same settings group, so they
        // are gathered into one scope — ScopeSetValues boxes the struct once per call, and
        // two calls would box, modify and restore it twice for no reason.
        var flora = new List<(string, object)>();
        if (FeedConfig.WholeSceneFloraLodMult > 0)
            flora.Add(("LODDistanceMultiplier", (float)FeedConfig.WholeSceneFloraLodMult));
        if (flora.Count > 0)
            ScopeSetValues("FloraSettings",
                $"feed flora LOD distance x{FeedConfig.WholeSceneFloraLodMult:0.###} " +
                "(LOWER = MORE FLORA — it scales the measured distance LOD selection reads, " +
                "not the LOD thresholds; measured 2.4 -> ~4 trees, 0.25 -> more than baseline)",
                flora.ToArray());

        // LOD, our render only. MainView is the PassLODSettings the main culling job reads,
        // so the dotted paths are the engine's own per-pass structure rather than a reach.
        var lod = new List<(string, object)>();
        if (FeedConfig.WholeSceneLodShift != -999)
            lod.Add(("MainView.LODShift", FeedConfig.WholeSceneLodShift));
        if (FeedConfig.WholeSceneFloraMinLod != -999)
            lod.Add(("MainView.FloraMinLOD", FeedConfig.WholeSceneFloraMinLod));
        if (FeedConfig.WholeSceneObjectDistanceMult > 0)
            lod.Add(("ObjectDistanceMult", (float)FeedConfig.WholeSceneObjectDistanceMult));
        if (FeedConfig.WholeSceneSmallObjectMult != -999)
            lod.Add(("SmallObjectVisibleMult", FeedConfig.WholeSceneSmallObjectMult));
        if (lod.Count > 0)
            ScopeSetValues("LODSettings",
                "feed LOD [" +
                (FeedConfig.WholeSceneLodShift != -999 ? $"shift={FeedConfig.WholeSceneLodShift} " : "") +
                (FeedConfig.WholeSceneFloraMinLod != -999 ? $"floraMinLod={FeedConfig.WholeSceneFloraMinLod} " : "") +
                (FeedConfig.WholeSceneObjectDistanceMult > 0 ? $"objDistX{FeedConfig.WholeSceneObjectDistanceMult:0.###} " : "") +
                (FeedConfig.WholeSceneSmallObjectMult != -999 ? $"smallObjX{FeedConfig.WholeSceneSmallObjectMult} " : "") +
                "]", lod.ToArray());

        // GRASS, our render only. Engine defaults measured in-game are DrawDistance 1000 and
        // Density 3. DrawDistance is clamped by the static MAX_GRASS_RENDERING_DISTANCE, so
        // an over-large value is ignored rather than harmful.
        var grass = new List<(string, object)>();
        if (FeedConfig.WholeSceneGrassDrawDistance > 0)
            grass.Add(("DrawDistance", (float)FeedConfig.WholeSceneGrassDrawDistance));
        if (FeedConfig.WholeSceneGrassDensity > 0)
            grass.Add(("Density", (float)FeedConfig.WholeSceneGrassDensity));
        if (grass.Count > 0)
            ScopeSetValues("GrassSettings",
                "feed grass [" +
                (FeedConfig.WholeSceneGrassDrawDistance > 0 ? $"drawDist={FeedConfig.WholeSceneGrassDrawDistance:0.#} " : "") +
                (FeedConfig.WholeSceneGrassDensity > 0 ? $"density={FeedConfig.WholeSceneGrassDensity:0.###} " : "") +
                "]", grass.ToArray());

        // PARALLAX OCCLUSION MAPPING, our render only. The "close-up ground is flat" knob,
        // and unlike everything above it this one is a MATERIAL SHADER effect — it fakes
        // surface relief by ray-marching a height map per pixel. Geometry LOD could never
        // have produced the reported symptom, which is why that line went nowhere.
        //
        // UNPROVEN UNTIL THE PROBE REPORTS: whether the consumer reads this per-pass or
        // bakes it into a shader define. If it is a define then toggling it per frame is the
        // RaytracingSettings mistake again — see the header of this method. That is why the
        // default is "do not scope" and why the probe prints the live struct.
        var par = new List<(string, object)>();
        if (FeedConfig.WholeSceneParallax >= 0)
            par.Add(("Enabled", FeedConfig.WholeSceneParallax != 0));
        if (FeedConfig.WholeSceneParallaxFadeout > 0)
            par.Add(("FadeoutDistance", (float)FeedConfig.WholeSceneParallaxFadeout));
        if (FeedConfig.WholeSceneParallaxSelfShadow >= 0)
            par.Add(("EnableSelfShadow", FeedConfig.WholeSceneParallaxSelfShadow != 0));
        if (FeedConfig.WholeSceneParallaxShadowLength > 0)
            par.Add(("ShadowMaxLength", (float)FeedConfig.WholeSceneParallaxShadowLength));
        if (FeedConfig.WholeSceneParallaxSteps != -999)
            par.Add(("MaxStepCount", FeedConfig.WholeSceneParallaxSteps));
        if (par.Count > 0)
            ScopeSetValues("ParallaxSettings",
                "feed parallax [" +
                (FeedConfig.WholeSceneParallax >= 0 ? $"on={FeedConfig.WholeSceneParallax != 0} " : "") +
                (FeedConfig.WholeSceneParallaxFadeout > 0 ? $"fade={FeedConfig.WholeSceneParallaxFadeout:0.#} " : "") +
                (FeedConfig.WholeSceneParallaxSelfShadow >= 0 ? $"selfShadow={FeedConfig.WholeSceneParallaxSelfShadow != 0} " : "") +
                (FeedConfig.WholeSceneParallaxShadowLength > 0 ? $"shadowLen={FeedConfig.WholeSceneParallaxShadowLength:0.###} " : "") +
                (FeedConfig.WholeSceneParallaxSteps != -999 ? $"steps={FeedConfig.WholeSceneParallaxSteps} " : "") +
                "]", par.ToArray());
    }


    // ---- OWN SUN-SHADOW CASCADES ------------------------------------------------------
    //
    // The last big borrowed-lighting item. Until now the feed sampled the ENGINE'S cascade
    // set, which is fitted around the PLAYER: shadows soften and vanish with camera
    // distance, and once the orbit leaves the cascade volume entirely the shadow lookup
    // returns "occluded" for everything — the reported "whole ship goes dark at some points
    // in the orbit".
    //
    // The engine's own setup is DrawContextManager.OnBeginDraw, and it turns out to be
    // almost entirely per-context rather than global:
    //
    //   CascadeShadowsContext.FlushUpdates()
    //       reads  CoreSystems.Settings.RenderView   <- OURS while installed
    //       reads  Settings.Shadow.DirectionalLight, Settings.Light.Sun
    //       mutates only its OWN _cascades / _cascadePriorities / _lastCameraPosition
    //       calls  Cascade.UpdateViewSetupFull(mainView, lightDir)  -> refits every frustum
    //   DirectionalLightShadowResources.OnBeginDraw()
    //       reads  CoreSystems.DrawContexts.CascadeShadows/.CharacterShadows  <- OURS
    //       builds the depth-map Texture2DTable + the setup constant buffer
    //
    // So a second, independent cascade set needs no new machinery at all — just these two
    // calls made while our view and our contexts are installed, and stage 3 allowed to run.
    //
    // What we deliberately do NOT call is DrawContextManager.OnBeginDraw() itself, even
    // though that is the engine's entry point. It also does
    // CoreSystems.LocalLights.FlushUpdates() and EnvironmentProbeManager.PrepareProbes(),
    // both of which drain queues on GLOBAL managers that the player's frame owns. Draining
    // them a second time per frame is precisely the double-stepping class of bug this
    // project has already paid for twice (probe atlas, raytracing accumulators).
    //
    // Leaving LocalLightsToUpdate / ShadowMasksToUpdate unset on our manager is safe:
    // Buffer<T> is a STRUCT (IntPtr _data, int _count, int _capacity), so the unassigned
    // field is a zero-count buffer and RenderLocalLightShadows iterates it zero times. The
    // feed gets no local-light shadows, which is a fidelity gap, not a fault.
    private static bool BeginOwnShadows()
    {
        if (FeedConfig.WholeSceneOwnShadows <= 0) return false;
        if (_ourDrawContexts == null || _ourFreshShadowResources == null) return false;

        try
        {
            var dcType = _ourDrawContexts.GetType();

            // CASCADE COST. Our cascade set is sized from the PLAYER'S graphics settings —
            // CascadesCount cascades at CascadeShadowResolution squared, each a full depth
            // texture, allocated the moment our CascadeShadowsContext was constructed. At
            // 4096 x 8 that is half a gigabyte of VRAM and eight full geometry passes per
            // second render, to shade a 512x512 panel.
            //
            // Scoped, not global: our context only ever flushes during our render, so it
            // resizes itself to these values on the first flush and the engine's own set
            // keeps the player's settings. The two contexts are independent — that is the
            // whole point of owning them.
            if (FeedConfig.WholeSceneCascadeResolution > 0 || FeedConfig.WholeSceneCascadeCount > 0
                || FeedConfig.WholeSceneCharacterShadowResolution > 0)
            {
                var sets = new System.Collections.Generic.List<(string, object)>();
                if (FeedConfig.WholeSceneCascadeResolution > 0)
                    sets.Add(("DirectionalLight.CascadeShadowResolution", FeedConfig.WholeSceneCascadeResolution));
                if (FeedConfig.WholeSceneCascadeCount > 0)
                    sets.Add(("DirectionalLight.CascadesCount", FeedConfig.WholeSceneCascadeCount));

                // CHARACTER SHADOWS — the third sizing field, and we were scoping only two.
                //
                // Found by the resource report, not by reading: CharacterShadows was 32 MiB
                // of a 444 MiB feed, two 2048x2048 depth sets (first- and third-person), for
                // a camera orbiting a ship at 100 m where the player's character is not in
                // shot at all.
                //
                // Same mechanism as the cascades above, confirmed in IL rather than assumed:
                //
                //     CharacterShadowsContext..ctor      -> CheckShadowSettingChanged()
                //     CharacterShadowsContext.FlushUpdates -> CheckShadowSettingChanged()
                //     CheckShadowSettingChanged reads
                //         CoreSystems.Settings.Shadow.DirectionalLight.CharacterShadowResolution
                //         and calls ResizeCascades(int) when it differs from the current size.
                //
                // Because FlushUpdates re-checks every render, scoping is enough — no need to
                // touch construction. OUR context flushes only inside OUR render and sees our
                // value; the engine's flushes in the player's frame and sees theirs. Each
                // resizes once and then stays put, so there is no per-frame thrash.
                if (FeedConfig.WholeSceneCharacterShadowResolution > 0)
                    sets.Add(("DirectionalLight.CharacterShadowResolution",
                              FeedConfig.WholeSceneCharacterShadowResolution));

                LogCascadeSettings();
                ScopeSetValues("ShadowSettings",
                    $"feed cascades {FeedConfig.WholeSceneCascadeResolution}px x {FeedConfig.WholeSceneCascadeCount}" +
                    (FeedConfig.WholeSceneCharacterShadowResolution > 0
                        ? $", character shadows {FeedConfig.WholeSceneCharacterShadowResolution}px" : ""),
                    sets.ToArray());
            }

            // Mode 2: make every cascade re-render every time we do. The engine's policy
            // (CascadesUpdateCount per draw, priority-sorted) assumes a 60fps continuous
            // camera; ours moves in 100ms steps, so a round-robin can leave a far cascade
            // several orbit positions stale.
            if (FeedConfig.WholeSceneOwnShadows >= 2)
            {
                var casc = dcType.GetProperty("CascadeShadows", Any)?.GetValue(_ourDrawContexts);
                casc?.GetType().GetField("_forceUpdateAll", Any)?.SetValue(casc, true);
            }

            _cascFld ??= dcType.GetField("CascadesToUpdate", Any);
            _charCascFld ??= dcType.GetField("CharacterCascadesToUpdate", Any);

            // Order matters: OnBeginDraw reads the cascades' DepthTextures, and
            // CharacterShadowsContext allocates its pair lazily inside FlushUpdates.
            FlushInto(dcType, "CascadeShadows", _cascFld);
            FlushInto(dcType, "CharacterShadows", _charCascFld);

            _ourFreshShadowResources.GetType().GetMethod("OnBeginDraw", Any)
                ?.Invoke(_ourFreshShadowResources, null);

            if (!_ownShadowsLogged)
            {
                _ownShadowsLogged = true;
                RttLog.Line($"Whole-scene: OWN SHADOW CASCADES active (mode {FeedConfig.WholeSceneOwnShadows}) — " +
                            "our CascadeShadowsContext refitted every cascade frustum around the ORBIT " +
                            "camera and our DirectionalLightShadowResources rebuilt its depth table from " +
                            "them. Stage 3 renders into our textures; the engine's cascade set is " +
                            "untouched. Local-light shadow requests are empty by design.");
            }
            return true;
        }
        catch (Exception e) { RttLog.Error("whole-scene begin own shadows", e); return false; }
    }

    // Take the per-context update list and store it on our manager, where RenderShadows
    // reads it from. FlushUpdates allocates a Buffer<T> that OnEndDraw would normally
    // dispose; we dispose it ourselves in EndOwnShadows.
    private static void FlushInto(Type dcType, string contextProp, FieldInfo target)
    {
        if (target == null) return;
        var ctx = dcType.GetProperty(contextProp, Any)?.GetValue(_ourDrawContexts);
        var buf = ctx?.GetType().GetMethod("FlushUpdates", Any)?.Invoke(ctx, null);
        if (buf != null) target.SetValue(_ourDrawContexts, buf);
    }

    // Must run BEFORE the DrawContexts global is put back: OnBeginDraw read it, and the
    // symmetric teardown should see the same world.
    private static void EndOwnShadows()
    {
        try
        {
            _ourFreshShadowResources?.GetType().GetMethod("OnEndDraw", Any)
                ?.Invoke(_ourFreshShadowResources, null);

            // Buffer<T> is a struct, so GetValue boxes a COPY — but _data is an IntPtr and
            // the copy points at the same native allocation, so Dispose on the box frees
            // the real thing. Then reset the field to default (a zero-count buffer) so a
            // failed render never leaves a dangling pointer for the next one to iterate.
            DisposeBuffer(_cascFld);
            DisposeBuffer(_charCascFld);
        }
        catch (Exception e) { RttLog.Error("whole-scene end own shadows", e); }
    }

    private static void DisposeBuffer(FieldInfo f)
    {
        if (f == null || _ourDrawContexts == null) return;
        try
        {
            if (f.GetValue(_ourDrawContexts) is IDisposable d) d.Dispose();
            f.SetValue(_ourDrawContexts, Activator.CreateInstance(f.FieldType));
        }
        catch (Exception e) { RttLog.Error($"whole-scene dispose {f.Name}", e); }
    }

    private static FieldInfo _cascFld, _charCascFld;
    private static bool _ownShadowsLogged, _cascadeSettingsLogged;

    // Print what the player's shadow settings actually are, and what our set costs at
    // them, so the size of the knob is a measured number rather than an assumption.
    private static void LogCascadeSettings()
    {
        if (_cascadeSettingsLogged) return;
        _cascadeSettingsLogged = true;
        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var settings = core?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var shadow = settings?.GetType().GetProperty("Shadow", Any)?.GetValue(settings);
            var dl = shadow?.GetType().GetField("DirectionalLight", Any)?.GetValue(shadow);
            if (dl == null) { RttLog.Line("Whole-scene: could not read ShadowSettings.DirectionalLight."); return; }

            object res = dl.GetType().GetField("CascadeShadowResolution", Any)?.GetValue(dl);
            object cnt = dl.GetType().GetField("CascadesCount", Any)?.GetValue(dl);
            object upd = dl.GetType().GetField("CascadesUpdateCount", Any)?.GetValue(dl);
            object always = dl.GetType().GetField("CascadesAlwaysUpdated", Any)?.GetValue(dl);

            double mb = 0;
            if (res != null && cnt != null)
                mb = System.Convert.ToDouble(res) * System.Convert.ToDouble(res)
                     * System.Convert.ToDouble(cnt) * 4.0 / 1048576.0;

            RttLog.Line($"Whole-scene cascades: player's settings are {res}px x {cnt} cascades " +
                        $"(update {upd}/draw, {always} always) = ~{mb:F0} MB of depth textures for OUR " +
                        "set alone, plus that many geometry passes per second render — to shade a " +
                        $"{FeedConfig.WholeSceneWidth}x{FeedConfig.WholeSceneHeight} panel.");
        }
        catch (Exception e) { RttLog.Error("log cascade settings", e); }
    }

    // ---- PLANET ENVIRONMENT REBUILD ---------------------------------------------------
    //
    // The reported symptom was precise: the feed's planet ATMOSPHERE detaches from the
    // planet and moves with the PLAYER'S aim. PlanetEnvironmentGroup.OnBeginDraw builds
    // PlanetSpheres / PlanetEnvSetupFirst / AllPlanetEnvSetups from
    // SettingsManager.RenderView — the player's camera — once per frame, before our
    // nested Draw, which then inherits them. Twenty-six jobs read these, including
    // AtmosphereAdditiveJob (the atmosphere itself), VolumeRendering, LocalFog, clouds
    // and the GBuffer/deferred-texturing passes. Same bug class as the camera constant
    // buffer: per-frame data built from the player's view, silently adopted by a nested
    // render.
    //
    // The fix leans on a property confirmed from the IL: OnBeginDraw has NO drains and
    // no cross-frame accumulation. It sorts the global planet list by camera distance,
    // fills the LUT/weather tables, culls weather modifiers, and creates TRANSIENT
    // constant buffers (the allocator CameraCbSwap has used safely for weeks). Every
    // side effect is fully regenerated by running it again — so the swap is symmetric:
    // run it under OUR view before our Draw, run it again under the PLAYER'S view after.
    // The second run does not merely restore the buffers, it restores the planet sort
    // order and the weather-modifier culling too. Nothing is created that the transient
    // allocator does not reclaim, and nothing needs field surgery.
    // TWO FAILED SHAPES, then this one. Attempt 1 invoked the whole OnBeginDraw and
    // OOR'd immediately: its output lists are append-only (the engine clears them once
    // per frame upstream), so a mid-frame re-run doubled the CB list against a replaced
    // data list. Attempt 2 cleared the three lists first — and the alignment FIX WAS
    // CONFIRMED VISUALLY — but died ~2.5 minutes later in the descriptor heap:
    //
    //     ArgumentException: An item with the same key has already been added.
    //         Key: DescriptorHeapPool+Token
    //       at DescriptorHeapPool.CreateSRV
    //       at TextureCubeTable.GetD3DGpuDescriptorHandle()
    //       at CloudShadowJob.JobSnapshot.Draw          <- the PLAYER's frame
    //
    // Clearing and refilling _atmosphereLUTTables/_weatherMapTables makes the engine
    // re-create descriptor TABLES twenty times a second — Rule 11 in disguise: we did
    // not create GPU resources, we made the ENGINE create them on our schedule, and the
    // descriptor pool's tokens eventually collided.
    //
    // So this rebuild is NARROW. The atmosphere's position lives in the SETUP CBs
    // (camera-relative planet transforms); the LUT/weather tables are camera-independent
    // texture registries. We re-run only:
    //     FillPlanetEnvironmentSetups (static; clears + refills the data list, culled by
    //                                  the given frustum)
    //     the CB-list rebuild          (transient CBs — the proven allocator)
    //     FillPlanetEnvironmentSlimSetup (writes the spheres data + CB itself)
    // and never touch SortEntities or the table fills. Skipping the sort also PRESERVES
    // the player's planet order, which is what keeps setups[i] pointing at the right
    // AtmosphereLUTTables[i].
    // v4, after v3 page-faulted at VA 0x0. Two defects found offline, no game harmed:
    //
    //   * THE EMPTY CASE. When the orbit camera's frustum culls ALL planets (it points
    //     at the ship for most of the orbit), v3 wrote null into _planetEnvSetupFirst —
    //     and a consumer reading a Nullable via GetValueOrDefault binds a DEFAULT
    //     TransientConstantBuffer, i.e. GPU address zero. PageFaultVA 0x0, surfacing
    //     frames later because the GPU executes behind the recorder. v4 never writes the
    //     swap at all when our frustum yields no planets — no planets in view means the
    //     misalignment is invisible anyway.
    //
    //   * OVERLOAD AMBIGUITY. CreateTransientConstantBuffer has TWO 2-param generic
    //     overloads — (String, in TData) and (String, ReadOnlySpan<TData>) — and v3
    //     picked whichever enumerated first. v4 selects the byref (in TData) one
    //     explicitly.
    //
    //   * RESTORE IS NOW VERBATIM. v3 re-ran the fill under the player's view, which
    //     mutated the weather-modifier fade state a second time and built a second batch
    //     of CBs. v4 snapshots the six outputs (two lists, four fields) before touching
    //     anything and puts the SAME values back — same frame, so the saved transient
    //     CBs are still valid, and late readers see bit-identical state.
    private static object _planetEnvGroup;
    private static FieldInfo _fPeFrustum, _fPeSetupsData, _fPeSetupsCbs, _fPeFirst, _fPeFirstData;
    private static FieldInfo _fPeSpheres, _fPeSpheresData;

    // ---- TRUE LOCAL UP, FROM THE PLANET ITSELF (2026-08-01) ----------------------
    //
    // The orbit needs the surface normal, and every cheaper source was wrong. The subject's
    // own block rotation looked right — a real ~16 degree quaternion, not identity — but a
    // base is only gravity-aligned if it was built that way, and this one was not: the orbit
    // ring came out flat with respect to that up and still visibly tilted against the ground.
    //
    // The planet's centre is the only source that cannot be wrong. Up is radial, by
    // definition, and the engine is already handing us the planet spheres for the atmosphere
    // rebuild — so this costs one list walk and no new engine surface.
    //
    // Deliberately shape-agnostic: the sphere element's members are found by TYPE rather than
    // by name (a Vector3D for the centre, a big number for the radius), because guessing field
    // names blind is exactly what has cost time today. Radius must be kilometre-scale, which
    // rejects anything that is not a planet.
    // EACH OUTCOME GETS ITS OWN LATCH. One shared flag meant the first failure silenced
    // every later message for the session — and that is precisely what happened: the
    // spheres list is empty for the first rebuilds, "list is empty" latched, and the shape
    // dump that would have named the right field could never print. A run-once log that
    // guards a DIFFERENT run-once log is a bug factory.
    private static bool _planetUpFailLogged, _planetUpShapeLogged, _planetUpOkLogged, _planetUpRelLogged;
    private static bool _planetUpNoSubjectLogged;
    private static int _planetUpCountdown;

    // The group fields that could carry a planet centre. _allPlanetSpheresData was the
    // first guess and is empty in flight; the setups list is the one actually populated
    // ("Planet env counts: setups=1" alongside an empty spheres list). All are scanned and
    // the best match wins, so this no longer depends on picking the right name up front.
    private static readonly string[] PlanetUpSources =
    {
        "_allPlanetSpheresData", "_allPlanetSpheres",
        "_allPlanetEnvSetupsData", "_planetEnvSetupFirstData",
    };

    internal static Vector3D? PlanetUpAt(Vector3D pos)
    {
        try
        {
            if (_planetEnvGroup == null)
            {
                if (!_planetUpFailLogged)
                {
                    _planetUpFailLogged = true;
                    RttLog.Line("Orbit up: PlanetEnvironmentGroup is null — falling back to the subject's block " +
                                "rotation, which for a WALL-MOUNTED panel is the screen's facing, not the ground normal.");
                }
                return null;
            }

            var gt = _planetEnvGroup.GetType();
            if (!_planetUpShapeLogged) DumpPlanetSources(gt, pos);

            // EVERY PLANET VECTOR ON THIS SIDE IS VIEW-SPACE. The field is named ViewPosition
            // and the dump proved it: the same planet reads 31887,41155,32358 from one orbit
            // angle and -60389,-9132,-6324 from another. Nothing here can be compared against
            // a world position until it is rotated back, which is what the rejection guard
            // was catching. The basis comes from the view we just installed, so these
            // vectors are relative to OUR orbit camera, not the player's.
            var basis = InstalledViewBasis();
            if (basis == null)
            {
                if (!_planetUpFailLogged)
                {
                    _planetUpFailLogged = true;
                    RttLog.Line("Orbit up: the installed view matrix is unreadable, so view-space planet " +
                                "positions cannot be rotated back to world. Falling back.");
                }
                return null;
            }

            var best = new SphereHit();
            foreach (var name in PlanetUpSources)
            {
                object v = null;
                try { v = gt.GetField(name, Any)?.GetValue(_planetEnvGroup); } catch { }
                if (v == null) continue;

                var items = v as System.Collections.IList;
                int n = items?.Count ?? 1;
                for (int i = 0; i < n; i++)
                    ScanForSphere(items != null ? items[i] : v, pos, name, 3, best, basis);
            }

            Vector3D bestCentre = best.Centre;
            double bestRadius = best.Radius, bestErr = best.Err;
            string bestFrom = best.From;

            if (bestRadius <= 0)
            {
                if (!_planetUpFailLogged)
                {
                    _planetUpFailLogged = true;
                    RttLog.Line("Orbit up: no planet-sized sphere in any candidate source yet. Retrying on a slow " +
                                "cadence — the lists fill once the render's frustum actually contains the planet.");
                }
                return null;
            }

            // THE CAMERA-RELATIVE TRAP. Render constant buffers routinely store the planet
            // centre relative to the camera under a floating origin. Such a value is a
            // perfectly well-formed vector carrying a planet-sized radius, and using it
            // would produce a confidently wrong up rather than an obvious failure. A subject
            // standing on the surface must be ~radius from a WORLD-SPACE centre; anything
            // else is rejected instead of trusted.
            if (bestErr > 0.05)
            {
                if (!_planetUpRelLogged)
                {
                    _planetUpRelLogged = true;
                    RttLog.Line($"Orbit up: REJECTED the candidate from {bestFrom} — centre " +
                                $"{bestCentre.X:F0},{bestCentre.Y:F0},{bestCentre.Z:F0} radius {bestRadius:F0} m puts the " +
                                $"subject {(pos - bestCentre).Length():F0} m out, an error of {bestErr * 100:F0}% of the " +
                                "radius. That is almost certainly a camera-relative centre (floating origin), so the orbit " +
                                "falls back rather than turning around a confidently wrong axis. The SOURCE DUMP above " +
                                "names the fields actually present.");
                }
                return null;
            }

            var up = pos - bestCentre;
            double len = up.Length();
            if (len < 1.0) return null;
            up = new Vector3D(up.X / len, up.Y / len, up.Z / len);

            if (!_planetUpOkLogged)
            {
                _planetUpOkLogged = true;
                RttLog.Line($"Orbit up: PLANET RADIAL from {bestFrom} — centre " +
                            $"{bestCentre.X:F0},{bestCentre.Y:F0},{bestCentre.Z:F0} radius {bestRadius:F0} m, subject " +
                            $"{len:F0} m from centre ({(len - bestRadius):F0} m above the sphere, {bestErr * 100:F2}% " +
                            $"error). up = {up.X:F3},{up.Y:F3},{up.Z:F3}. This is the surface normal by definition and " +
                            "replaces the subject's block rotation, which is only the surface normal if the grid " +
                            "happened to be built gravity-aligned.");
            }
            return up;
        }
        catch { return null; }
    }

    // Print what is ACTUALLY in each candidate source, one level into nested structs.
    // Three guesses at field shapes have been spent today; this costs one message and
    // ends the guessing. Latches only once something was genuinely there to look at, so
    // an early empty call cannot silence it.
    private static void DumpPlanetSources(Type gt, Vector3D pos)
    {
        var sb = new System.Text.StringBuilder();
        var viaTarget = CameraFeed.Current;
        sb.Append("Orbit up: PLANET SOURCE DUMP — subject at ")
          .Append($"{pos.X:F0},{pos.Y:F0},{pos.Z:F0}")
          .Append(" (published by the tick). For an SE2 planet the centre we want is tens of km from that.")
          .Append("\n  cross-check: Feeds.Cur.Target read from THIS thread = ")
          .Append(viaTarget == null
                  ? "<null>"
                  : $"{viaTarget.Centre.X:F0},{viaTarget.Centre.Y:F0},{viaTarget.Centre.Z:F0}")
          .Append(". If those two disagree, the per-feed Target is not visible from the render thread ")
          .Append("and every render-side reader of it is suspect.");
        bool sawAny = false;

        foreach (var name in PlanetUpSources)
        {
            object v = null;
            try { v = gt.GetField(name, Any)?.GetValue(_planetEnvGroup); } catch { }
            if (v == null) { sb.Append("\n  ").Append(name).Append(" = <null or absent>"); continue; }

            var items = v as System.Collections.IList;
            int n = items?.Count ?? 1;
            sb.Append("\n  ").Append(name).Append("  count=").Append(n);
            if (n == 0) continue;

            object e = items != null ? items[0] : v;
            if (e == null) { sb.Append("  [0] = null"); continue; }
            sawAny = true;
            sb.Append("  element=").Append(e.GetType().Name);
            AppendMembers(sb, e, "      ", 2);
        }

        if (!sawAny) return;                  // nothing populated yet — try again next rebuild
        _planetUpShapeLogged = true;
        RttLog.Line(sb.ToString());
    }

    private static void AppendMembers(System.Text.StringBuilder sb, object o, string indent, int depth)
    {
        var t = o.GetType();

        // THE PLANET DATA LIVES BEHIND AN InlineArray, AND ONLY THE INDEXER REACHES IT.
        // Its StorageSpan is a Span<T>, which cannot be boxed, so reflection returns nothing
        // for it — the first dump printed "StorageSpan = " and looked empty when the array
        // actually held four planets. The int indexer boxes each element and works fine.
        var idx = Indexer(t);
        if (idx != null)
        {
            int n = Math.Min(InlineLength(o, t), 8);
            for (int i = 0; i < n; i++)
            {
                object e = null; try { e = idx.GetValue(o, new object[] { i }); } catch { break; }
                if (e == null) continue;
                sb.Append('\n').Append(indent).Append('[').Append(i).Append("] ").Append(e.GetType().Name);
                if (depth > 0) AppendMembers(sb, e, indent + "  ", depth - 1);
            }
        }

        foreach (var f in t.GetFields(Any))
        {
            object v = null; try { v = f.GetValue(o); } catch { }
            sb.Append('\n').Append(indent).Append(f.FieldType.Name).Append(' ').Append(f.Name).Append(" = ").Append(v);
            if (depth > 0 && v != null && Nested(f.FieldType)) AppendMembers(sb, v, indent + "  ", depth - 1);
        }
        foreach (var p in t.GetProperties(Any))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            object v = null; try { v = p.GetValue(o); } catch { }
            sb.Append('\n').Append(indent).Append(p.PropertyType.Name).Append(' ').Append(p.Name).Append(" = ").Append(v);
        }
    }

    private static System.Reflection.PropertyInfo Indexer(Type t)
    {
        foreach (var p in t.GetProperties(Any))
        {
            var ps = p.GetIndexParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(int)) return p;
        }
        return null;
    }

    private static int InlineLength(object o, Type t)
    {
        try
        {
            var lp = t.GetProperty("Length", Any);
            if (lp != null) return Convert.ToInt32(lp.GetValue(o));
        }
        catch { }
        return 0;
    }

    // The best world-space planet candidate found so far.
    private sealed class SphereHit
    {
        public Vector3D Centre;
        public double Radius;
        public double Err = double.MaxValue;
        public string From;
    }

    // Walk an unknown object graph looking for anything shaped like a planet: a vector
    // centre next to a kilometre-scale radius. Descends through nested structs AND through
    // InlineArray indexers, because the spheres sit inside one.
    //
    // The centre found is VIEW-space and is rotated to world before being scored, so the
    // error is a genuine "is the subject standing on this sphere" test. That test is also
    // what picks the right planet out of the four in the setup: the one we are on scores
    // ~0.003, the next nearest ~2.
    private static void ScanForSphere(object o, Vector3D pos, string path, int depth, SphereHit best, ViewBasis basis)
    {
        if (o == null || depth < 0) return;

        if (SphereOf(o, out var viewCentre, out double r) && r >= 1000.0)
        {
            var c = basis.ToWorld(viewCentre);
            double err = Math.Abs((pos - c).Length() - r) / r;      // on the surface => ~0
            if (err < best.Err) { best.Err = err; best.Centre = c; best.Radius = r; best.From = path; }
        }
        if (depth == 0) return;

        var t = o.GetType();
        var idx = Indexer(t);
        if (idx != null)
        {
            int n = Math.Min(InlineLength(o, t), 16);
            for (int i = 0; i < n; i++)
            {
                object e = null; try { e = idx.GetValue(o, new object[] { i }); } catch { break; }
                ScanForSphere(e, pos, $"{path}[{i}]", depth - 1, best, basis);
            }
        }
        foreach (var f in t.GetFields(Any))
        {
            if (!Nested(f.FieldType)) continue;
            object v = null; try { v = f.GetValue(o); } catch { }
            ScanForSphere(v, pos, path + "." + f.Name, depth - 1, best, basis);
        }
    }

    // THE VIEW BASIS, FOR UNDOING VIEW SPACE.
    //
    // VRage uses the row-vector convention: viewPos = (worldPos - cam) * Rt, where Rt is the
    // view matrix's upper 3x3 and is the transpose of the camera's world rotation. Both are
    // orthonormal, so undoing it needs no inversion — just a dot against each ROW:
    //
    //     worldOffset_j = viewPos . row_j        cam_j = -(translation . row_j)
    //
    // Read as doubles through reflection so this works whether the engine hands back a
    // MatrixD or a float Matrix.
    private sealed class ViewBasis
    {
        public double[] R = new double[9];      // rows 1..3 of the view matrix
        public Vector3D Cam;

        public Vector3D ToWorld(Vector3D v) => new Vector3D(
            v.X * R[0] + v.Y * R[1] + v.Z * R[2] + Cam.X,
            v.X * R[3] + v.Y * R[4] + v.Z * R[5] + Cam.Y,
            v.X * R[6] + v.Y * R[7] + v.Z * R[8] + Cam.Z);
    }

    // ---- THE AUTO APERTURE ---------------------------------------------------------
    //
    // Returns the EV the feed should be exposed at, smoothed. False means "no sun to work
    // from" — the caller then keeps the fixed wholeSceneExposure, so a failure here is a
    // return to the previous behaviour rather than a black or blown panel.
    private static double _apertureEv;          // the smoothed stop, in EV
    private static bool _apertureStarted;
    private static long _apertureLastMs, _apertureLogMs;
    private static double _apertureLoggedEv = double.NaN;

    // Day factor from the sun's elevation: 0 = night, 1 = day, smoothstepped across the
    // twilight band (feedExposureDawnDot..feedExposureDayDot). The one sun signal, shared
    // by auto-aperture and the ambient floor's night dimming (#60). False = no planet up
    // or no sun found (deep space) — callers keep their day behaviour.
    //
    // RATE-LIMITED to 1/s: TrySunFromPlanetEnv re-scans the planet-env setup reflectively
    // on every call ("the sun moves"), which is fine at aperture cadence but not at
    // per-render cadence — and the sun moves over minutes, not frames.
    private static long _dayFactorMs;
    private static double _dayFactorVal = 1;
    private static bool _dayFactorOk;

    private static bool TryDayFactor(out double t)
    {
        var now = Clock.Ms;
        if (now - _dayFactorMs >= 1000)
        {
            _dayFactorMs = now;
            _dayFactorOk = false;
            var up = CameraFeed.PlanetUpCache;
            if (up.LengthSquared() >= 0.5 && TrySunDirection(out var sun))
            {
                double dot = (sun.X * up.X + sun.Y * up.Y + sun.Z * up.Z) * FeedConfig.FeedSunSign;
                double lo = FeedConfig.FeedExposureDawnDot, hi = FeedConfig.FeedExposureDayDot;
                double v = hi - lo < 1e-6 ? (dot >= hi ? 1 : 0) : (dot - lo) / (hi - lo);
                v = v < 0 ? 0 : v > 1 ? 1 : v;
                _dayFactorVal = v * v * (3 - 2 * v);
                _dayFactorOk = true;
            }
        }
        t = _dayFactorVal;
        return _dayFactorOk;
    }

    private static bool TryAutoExposureEv(out double ev)
    {
        ev = 0;
        // In space there is no meaningful sun elevation and no up, so auto aperture
        // simply does not engage. Same gate as the floor dimming — one signal, TryDayFactor.
        if (!TryDayFactor(out double t)) return false;
        double target = FeedConfig.FeedExposureNight
                      + (FeedConfig.FeedExposureDay - FeedConfig.FeedExposureNight) * t;

        // Exponential approach on a real clock, so the adaptation rate is in seconds and does
        // not change with frame rate or with how often this feed happens to hold the slot.
        long now = Clock.Ms;
        if (!_apertureStarted) { _apertureStarted = true; _apertureEv = target; _apertureLastMs = now; }
        double dt = Math.Max(0, now - _apertureLastMs) / 1000.0;
        _apertureLastMs = now;
        double tau = Math.Max(0.01, FeedConfig.FeedExposureAdaptSeconds);
        _apertureEv += (target - _apertureEv) * (1.0 - Math.Exp(-dt / tau));

        // Rate-limited, and only when it has actually moved: this runs every render.
        if (now - _apertureLogMs > 5000 && Math.Abs(_apertureEv - _apertureLoggedEv) > 0.05)
        {
            _apertureLogMs = now; _apertureLoggedEv = _apertureEv;
            RttLog.Line($"Auto aperture: dayFactor={t:F3} -> target {target:+0.00;-0.00} EV, " +
                        $"now at {_apertureEv:+0.00;-0.00} EV (night {FeedConfig.FeedExposureNight:+0.##;-0.##}, " +
                        $"day {FeedConfig.FeedExposureDay:+0.##;-0.##}, tau {tau:F1}s). If this reads bright at " +
                        "midnight the sun vector points the other way — flip feedSunSign.");
        }

        ev = _apertureEv;
        return true;
    }

    // The sun, as a unit vector pointing TOWARD it (subject to FeedSunSign).
    //
    // Found by TYPE and NAME on the engine's light settings, because guessing a field name
    // blind has cost this project a day already. Cached once resolved. The one-shot dump on
    // failure is the same move that cracked the planet spheres: print what is actually there
    // rather than guess again.
    private static int _sunState;               // 0 untried, 1 ok, -1 unavailable

    private static bool TrySunDirection(out Vector3D dir)
    {
        dir = default;
        if (_sunState == -1) return false;
        try
        {
            if (_sunState == 0)
            {
                // SEARCH EVERY SETTINGS GROUP, not just LightSettings.
                //
                // LightSettings turned out to hold exactly one Vector3 —
                // DirectionalLightUpVector = {0,1,0}, a constant world up, not the sun. The
                // first version took it anyway ("contains light"), which would have driven the
                // aperture off a vector that never moves. So: sweep all groups, score by name,
                // reject the basis axes, and PRINT EVERY CANDIDATE WITH ITS SCORE — a wrong
                // pick then shows up as one readable line instead of a plausible-looking
                // exposure curve tracking the wrong thing.
                var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
                var settings = core?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (settings == null) { _sunState = -1; return false; }

                // PROPERTIES TOO, AND NESTED. The first sweep enumerated GetFields only and
                // concluded "the sun is not in the settings" — but the assembly carries a
                // DirectionalLightSettings type with get_Direction, i.e. the direction is a
                // PROPERTY. A field-only scan cannot see it, and the negative result was an
                // artefact of the instrument rather than a fact about the engine.
                //
                // So this is now the same recursive scored scan used for the planet data:
                // fields AND properties, a few levels deep, every candidate printed with its
                // score.
                var cands = new System.Text.StringBuilder();
                var sbest = new SunHit();
                foreach (var gf in settings.GetType().GetFields(Any))
                {
                    object group = null;
                    try { group = gf.GetValue(settings); } catch { }
                    if (group == null || group.GetType().IsPrimitive) continue;
                    ScanForSun(group, gf.FieldType.Name, 3, sbest, cands);
                }
                RttLog.Line("Auto aperture: direction-shaped vectors across ALL settings groups " +
                            "(fields AND properties, nested) —" + cands +
                            "\n    chosen: " + (sbest.Path ?? "<none>"));

                // DIAGNOSTIC ONLY for now — see the note on TryAutoExposureEv. Even a correct
                // sun would not fix the real problem, so this logs what it found and does not
                // wire it up.
                if (sbest.Path != null)
                    RttLog.Line($"Auto aperture: a direction-shaped vector exists at {sbest.Path}, " +
                                "but sun-driven exposure is parked — see the auto-exposure note.");

                // THE SETTINGS-GROUP ROUTE DOES NOT EXIST, and the scan above now only
                // documents that. Confirmed exhaustively: the only Vector3s on any settings
                // group are camera positions, a skybox euler, a constant world-up and
                // hologram offsets. The sun lives with the thing that needs it —
                // PlanetEnvironmentData.Atmosphere (AtmosphereConstants), because scattering
                // cannot be computed without a light direction.
                //
                // There used to be a second branch here that read the sun from a settings
                // field via _sunField/_sunSettings. Those were NEVER ASSIGNED by any code
                // path, so that branch, the success log beneath it and the re-read at the
                // bottom of this method were all unreachable — the compiler had been saying
                // so (CS0649) for as long as they existed. Removed rather than left as
                // plausible-looking dead scaffolding.
                if (TrySunFromPlanetEnv(out var pdir))
                {
                    _sunState = 1;
                    dir = pdir;
                    return true;
                }
                RttLog.Line("Auto aperture: no sun direction in the settings groups OR the planet-env " +
                            "setup data. Auto aperture stays OFF and the feed keeps its fixed " +
                            "wholeSceneExposure.");
                _sunState = -1;
                return false;
            }

            // Re-read every call: the sun moves. Reaching here means _sunState == 1, and the
            // planet-env route is now the ONLY way to reach that.
            return TrySunFromPlanetEnv(out dir);
        }
        catch { _sunState = -1; return false; }
    }

    // ---- the sun, out of the planet's atmosphere constants -------------------------
    private static string _sunPath;             // where it was found, for the log
    private static bool _sunPathLogged;

    // Recursive, scored search for a light direction under the planet-env setup data.
    // Returns a WORLD-space unit vector.
    private static bool TrySunFromPlanetEnv(out Vector3D dir)
    {
        dir = default;
        if (_planetEnvGroup == null) return false;
        try
        {
            object root = null;
            try
            {
                root = _planetEnvGroup.GetType().GetField("_allPlanetEnvSetupsData", Any)?.GetValue(_planetEnvGroup);
                if (root is System.Collections.IList l) root = l.Count > 0 ? l[0] : null;
            }
            catch { }
            if (root == null) return false;

            var best = new SunHit();
            var log = _sunPathLogged ? null : new System.Text.StringBuilder();
            ScanForSun(root, "setup", 5, best, log);

            if (log != null)
            {
                _sunPathLogged = true;
                RttLog.Line("Auto aperture: direction-shaped vectors under the planet-env setup —" + log +
                            "\n    chosen: " + (best.Path ?? "<none>"));
            }
            if (best.Path == null) return false;

            var v = best.Vec;
            double len = v.Length();
            if (len < 1e-6) return false;
            v = new Vector3D(v.X / len, v.Y / len, v.Z / len);

            // VIEW SPACE, IF THE NAME SAYS SO. Everything else on this side was view-space
            // (ViewPosition, ViewPlanetPosition), so assume the same for anything named
            // "view" and rotate it back. Rotation ONLY — a direction has no origin, so the
            // camera translation must not be added.
            if (best.Path.IndexOf("view", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var b = InstalledViewBasis();
                if (b != null)
                    v = new Vector3D(
                        v.X * b.R[0] + v.Y * b.R[1] + v.Z * b.R[2],
                        v.X * b.R[3] + v.Y * b.R[4] + v.Z * b.R[5],
                        v.X * b.R[6] + v.Y * b.R[7] + v.Z * b.R[8]);
            }
            dir = v;
            _sunPath = best.Path;
            return true;
        }
        catch { return false; }
    }

    private sealed class SunHit { public Vector3D Vec; public int Score; public string Path; }

    private static void ScanForSun(object o, string path, int depth, SunHit best, System.Text.StringBuilder log)
    {
        if (o == null || depth < 0) return;
        var t = o.GetType();

        foreach (var f in t.GetFields(Any))
        {
            object v = null; try { v = f.GetValue(o); } catch { }
            if (v == null) continue;
            string p = path + "." + f.Name;

            if (f.FieldType.Name.StartsWith("Vector3") && AsVec3(v, out var vec))
            {
                string n = f.Name.ToLowerInvariant();
                bool axis = n.Contains("up") || n.Contains("right") || n.Contains("tangent")
                         || n.Contains("position") || n.Contains("colour") || n.Contains("color");
                int score = 0;
                if (!axis)
                {
                    if (n.Contains("sun")) score += 3;
                    if (n.Contains("direction") || n.Contains("dir")) score += 2;
                    if (n.Contains("light")) score += 1;
                    // A direction is unit length; a radiance or a scatter coefficient is not.
                    double len = vec.Length();
                    if (score > 0 && Math.Abs(len - 1.0) < 0.05) score += 2;
                }
                log?.Append("\n    ").Append(p).Append("  score=").Append(score)
                    .Append(axis ? "  (rejected)" : "").Append("  value=").Append(v);
                if (score > best.Score) { best.Score = score; best.Vec = vec; best.Path = p; }
                continue;
            }

            if (depth > 0 && Nested(f.FieldType)) ScanForSun(v, p, depth - 1, best, log);
        }

        // InlineArray members hide behind the indexer — same as the planet spheres.
        var idx = Indexer(t);
        if (idx != null && depth > 0)
        {
            int n = Math.Min(InlineLength(o, t), 4);
            for (int i = 0; i < n; i++)
            {
                object e = null; try { e = idx.GetValue(o, new object[] { i }); } catch { break; }
                ScanForSun(e, $"{path}[{i}]", depth - 1, best, log);
            }
        }
    }

    private static ViewBasis InstalledViewBasis()
    {
        try
        {
            var settings = _coreType?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var rv = settings?.GetType().GetProperty("RenderView", Any)?.GetValue(settings);
            var view = rv?.GetType().GetProperty("ViewD", Any)?.GetValue(rv);
            if (view == null) return null;

            var b = new ViewBasis();
            string[] rows = { "M11", "M12", "M13", "M21", "M22", "M23", "M31", "M32", "M33" };
            for (int i = 0; i < 9; i++)
                if (!Comp(view, view.GetType(), rows[i], out b.R[i])) return null;

            if (!Comp(view, view.GetType(), "M41", out double t1)
                || !Comp(view, view.GetType(), "M42", out double t2)
                || !Comp(view, view.GetType(), "M43", out double t3)) return null;

            b.Cam = new Vector3D(
                -(t1 * b.R[0] + t2 * b.R[1] + t3 * b.R[2]),
                -(t1 * b.R[3] + t2 * b.R[4] + t3 * b.R[5]),
                -(t1 * b.R[6] + t2 * b.R[7] + t3 * b.R[8]));
            return b;
        }
        catch { return null; }
    }

    private static bool Nested(Type t) =>
        t.IsValueType && !t.IsPrimitive && !t.IsEnum && t != typeof(decimal)
        && !t.Name.StartsWith("Vector") && !t.Name.StartsWith("Matrix") && !t.Name.StartsWith("Quaternion");

    // A centre and a radius out of an unknown struct, found by TYPE rather than by name —
    // guessing field names blind is what cost the day. Float vectors count: render-side
    // data is as likely to be float3 as double3.
    private static bool SphereOf(object s, out Vector3D centre, out double radius)
    {
        centre = default; radius = 0;
        bool haveCentre = false;
        var t = s.GetType();

        foreach (var f in t.GetFields(Any))
        {
            object v = null; try { v = f.GetValue(s); } catch { }
            Consider(v, ref centre, ref haveCentre, ref radius);
        }
        foreach (var p in t.GetProperties(Any))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            object v = null; try { v = p.GetValue(s); } catch { }
            Consider(v, ref centre, ref haveCentre, ref radius);
        }
        return haveCentre && radius > 0;
    }

    // haveCentre rather than "centre == default": a centre legitimately at the origin
    // would otherwise be overwritten by the next vector member that came along.
    private static void Consider(object v, ref Vector3D centre, ref bool haveCentre, ref double radius)
    {
        if (v == null) return;
        if (!haveCentre && AsVec3(v, out var vec)) { centre = vec; haveCentre = true; return; }
        if (radius <= 0 && (v is double || v is float))
        {
            double d = Convert.ToDouble(v);
            if (d > 1000.0) radius = d;
        }
    }

    private static bool AsVec3(object v, out Vector3D r)
    {
        r = default;
        if (v is Vector3D vd) { r = vd; return true; }
        var t = v.GetType();
        if (!t.IsValueType || !t.Name.StartsWith("Vector3")) return false;
        if (!Comp(v, t, "X", out double x) || !Comp(v, t, "Y", out double y) || !Comp(v, t, "Z", out double z))
            return false;
        r = new Vector3D(x, y, z);
        return true;
    }

    private static bool Comp(object o, Type t, string name, out double d)
    {
        d = 0;
        object v = null;
        try { v = t.GetField(name, Any)?.GetValue(o) ?? t.GetProperty(name, Any)?.GetValue(o); } catch { }
        if (!(v is double || v is float)) return false;
        d = Convert.ToDouble(v);
        return true;
    }
    private static MethodInfo _miPeFillSetups, _miPeFillSlim, _miPeSetMatrix, _miPeCreateCb;
    private static System.Reflection.PropertyInfo _pPeModifiersCtx;
    private static object _peBufMgr;
    private static int _planetEnvState;      // 0 untried, 1 ok, -1 unavailable
    private static bool _planetEnvLogged, _peEmptyLogged;

    // Snapshot of the player's planet-env outputs, restored verbatim in the finally.
    private sealed class PeSaved
    {
        public object[] Cbs, Data;
        public object First, FirstData, Spheres, SpheresData;
    }
    private static PeSaved _peSaved;

    private static object[] SnapshotList(System.Collections.IList l)
    {
        var a = new object[l.Count];
        l.CopyTo(a, 0);
        return a;
    }

    private static void RefillList(System.Collections.IList l, object[] items)
    {
        l.Clear();
        foreach (var it in items) l.Add(it);
    }

    // AtmosphereAdditiveJob's loop indexes AtmosphereLUTTables[i] with the SETUPS index
    // (read from its IL), so the tables must never be shorter than the setups list. The
    // narrow rebuild leaves the tables at the full planet count and can only ever CULL
    // setups below it, so the invariant holds structurally — this guard is a tripwire in
    // case that reasoning is wrong, not a crutch it leans on.
    private static bool _planetEnvCountsLogged;

    private static void GuardPlanetEnvInvariant(string when)
    {
        try
        {
            var cbs = _fPeSetupsCbs.GetValue(_planetEnvGroup) as System.Collections.IList;
            var luts = _planetEnvGroup.GetType().GetField("_atmosphereLUTTables", Any)
                ?.GetValue(_planetEnvGroup) as System.Collections.IList;
            if (cbs == null || luts == null) return;

            if (!_planetEnvCountsLogged)
            {
                _planetEnvCountsLogged = true;
                RttLog.Line($"Planet env counts ({when}): setups={cbs.Count} lutTables={luts.Count}.");
            }
            if (luts.Count != 0 && luts.Count < cbs.Count)
            {
                RttLog.Line($"Planet env INVARIANT BROKEN ({when}): {cbs.Count} setups but only " +
                            $"{luts.Count} LUT tables — trimming setups to match rather than let " +
                            "AtmosphereAdditiveJob index out of range.");
                while (cbs.Count > luts.Count) cbs.RemoveAt(cbs.Count - 1);
            }
        }
        catch (Exception e) { RttLog.Error("planet env invariant guard", e); }
    }

    private static bool RebuildPlanetEnv()
    {
        if (!FeedConfig.WholeScenePlanetEnv || _planetEnvState == -1) return false;
        try
        {
            if (_planetEnvState == 0)
            {
                _planetEnvState = -1;
                var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
                var common = core?.GetFields(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(f => f.FieldType.Name.Contains("CommonResourcesManager"))?.GetValue(null);
                _planetEnvGroup = common?.GetType().GetFields(Any)
                    .FirstOrDefault(f => f.FieldType.Name == "PlanetEnvironmentGroup")?.GetValue(common);
                if (_planetEnvGroup == null)
                {
                    RttLog.Line("Whole-scene: PlanetEnvironmentGroup unreachable — the feed's planet " +
                                "atmosphere stays positioned by the player's aim.");
                    return false;
                }
                var gt = _planetEnvGroup.GetType();
                _fPeFrustum = gt.GetField("_cameraFrustum", Any);
                _fPeSetupsData = gt.GetField("_allPlanetEnvSetupsData", Any);
                _fPeSetupsCbs = gt.GetField("_allPlanetEnvironmentSetups", Any);
                _fPeFirst = gt.GetField("_planetEnvSetupFirst", Any);
                _fPeFirstData = gt.GetField("_planetEnvSetupFirstData", Any);
                _fPeSpheres = gt.GetField("_allPlanetSpheres", Any);
                _fPeSpheresData = gt.GetField("_allPlanetSpheresData", Any);
                _miPeFillSetups = gt.GetMethod("FillPlanetEnvironmentSetups", Any);
                _miPeFillSlim = gt.GetMethod("FillPlanetEnvironmentSlimSetup", Any);
                _pPeModifiersCtx = gt.GetProperty("MainViewModifiersContext", Any);
                _miPeSetMatrix = _fPeFrustum?.FieldType.GetMethod("SetMatrix", Any);

                _peBufMgr = _coreType?.GetField("BindableBuffers", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                var setupType = _fPeSetupsData?.FieldType.GetGenericArguments().FirstOrDefault();
                // TWO 2-param generic overloads exist: (String, in TData) and
                // (String, ReadOnlySpan<TData>). The in-parameter one is byref; select it
                // explicitly rather than by enumeration order.
                var miCreate = _peBufMgr?.GetType().GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "CreateTransientConstantBuffer" && m.IsGenericMethod
                                      && m.GetParameters().Length == 2
                                      && m.GetParameters()[1].ParameterType.IsByRef);
                if (setupType != null && miCreate != null)
                    _miPeCreateCb = miCreate.MakeGenericMethod(setupType);

                if (_fPeFrustum == null || _fPeSetupsData == null || _fPeSetupsCbs == null
                    || _fPeFirst == null || _fPeFirstData == null || _miPeFillSetups == null
                    || _fPeSpheres == null || _fPeSpheresData == null
                    || _miPeFillSlim == null || _pPeModifiersCtx == null || _miPeSetMatrix == null
                    || _miPeCreateCb == null)
                {
                    RttLog.Line("Whole-scene: planet env narrow-rebuild members not all found — " +
                                "disabled. The atmosphere stays positioned by the player's aim.");
                    return false;
                }
                _planetEnvState = 1;
            }

            if (!RebuildFromInstalledView("our view")) return false;

            // THE ORBIT'S UP, COMPUTED WHERE THE DATA IS LIVE.
            //
            // PlanetUpAt needs _planetEnvGroup, which is resolved lazily by THIS method on
            // the render thread. Discovery runs on the LCD tick and kept finding it null,
            // so the orbit silently fell back to the subject's block rotation — which for a
            // wall-mounted panel is the screen's facing, not the ground normal. Same shape
            // as the config-poll bootstrap: state read from a thread that cannot see it.
            //
            // Cached for discovery to pick up instead of asking across the thread boundary.
            //
            // DELIBERATELY NOT ONE-SHOT. This used to live inside the run-once logging block
            // below, so the single early call — made while the planet lists were still empty —
            // was the only attempt ever made, and a moving subject never updated its axis.
            // Retried on a slow cadence instead: up changes only as the subject travels, and
            // the scan walks a list with one element in it.
            if (--_planetUpCountdown <= 0)
            {
                // The tick publishes the very position the orbit looks at. Reading
                // Feeds.Cur.Target across the thread boundary returned 0,0,0 here while the
                // tick was logging a real one, so this takes the value by the same route
                // PlanetUpCache travels back on.
                // PresenceCentre, not SubjectCentreCache: planet-radial up is a function of
                // WHERE YOU ARE on the sphere, so computing it at the orbit anchor gives the
                // wrong horizon for a camera that has flown away from it — and under manual
                // flight that separation reached 277 km (a different planet entirely). The
                // zero-check below still works: PresenceCentre falls back to the anchor when
                // we are not flying, and both are zero before the tick has published.
                var subject = CameraFeed.PresenceCentre;
                if (subject.LengthSquared() <= 1.0)
                {
                    // A SKIP MUST NOT CONSUME THE ATTEMPT.
                    //
                    // The render thread can reach here before the LCD tick has published a
                    // subject, and the FIRST successful rebuild is exactly when that race is
                    // most likely. Counting it as an attempt cost the entire 15:12 session:
                    // the retry is gated behind 60 MORE successful rebuilds, and a success
                    // needs a planet in our frustum (see the early return above), which at
                    // night is rare. The orbit ran on the subject-transform fallback all
                    // session and said nothing, because the skip was silent.
                    //
                    // Leaving the countdown at 0 retries on the very next rebuild. Once
                    // PlanetUpCache is set it persists, so a single success is enough.
                    _planetUpCountdown = 0;
                    if (!_planetUpNoSubjectLogged)
                    {
                        _planetUpNoSubjectLogged = true;
                        RttLog.Line("Orbit up: no subject published yet (SubjectCentreCache is zero) — the " +
                                    "planet scan is DEFERRED, not counted as an attempt, and retries on the " +
                                    "next planet-env rebuild. If this is the last word on the subject, the " +
                                    "LCD tick never published and the orbit is on its fallback up.");
                    }
                }
                else
                {
                    _planetUpCountdown = 60;
                    var radial = PlanetUpAt(subject);
                    if (radial.HasValue) CameraFeed.PlanetUpCache = radial.Value;
                }
            }

            if (!_planetEnvLogged)
            {
                _planetEnvLogged = true;
                RttLog.Line("=== PLANET ENV REBUILT (narrow) for our render: the setup CBs and planet " +
                            "spheres now come from the ORBIT camera — atmosphere on the planet, not on " +
                            "the player's aim. SortEntities and the LUT/weather TABLE fills are NOT " +
                            "re-run (descriptor churn there killed attempt 2), so the player's planet " +
                            "order and descriptor tables are untouched. Rebuilt from the player's view " +
                            "after our Draw. ===");
            }
            return true;
        }
        catch (Exception e) { _planetEnvState = -1; RttLog.Error("whole-scene planet env rebuild", e); return false; }
    }

    // Rebuild the setup data + CBs + spheres from the INSTALLED (our) view, after
    // snapshotting the player's outputs for a verbatim restore. Returns false — with the
    // player's state fully intact — when our frustum sees no planets.
    private static bool RebuildFromInstalledView(string label)
    {
        var settings = _coreType?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var rv = settings?.GetType().GetProperty("RenderView", Any)?.GetValue(settings);
        if (rv == null) return false;

        var viewD = rv.GetType().GetProperty("ViewD", Any)?.GetValue(rv);
        var proj = rv.GetType().GetProperty("JitteredProjection", Any)?.GetValue(rv);
        var viewProjD = proj?.GetType().GetProperty("ViewProjectionD", Any)?.GetValue(proj);
        if (viewD == null || viewProjD == null) return false;

        var data = (System.Collections.IList)_fPeSetupsData.GetValue(_planetEnvGroup);
        var cbs = (System.Collections.IList)_fPeSetupsCbs.GetValue(_planetEnvGroup);

        // Snapshot BEFORE any mutation. The restore puts these exact values back — same
        // frame, so the transient CBs inside are still live.
        _peSaved = new PeSaved
        {
            Cbs = SnapshotList(cbs),
            Data = SnapshotList(data),
            First = _fPeFirst.GetValue(_planetEnvGroup),
            FirstData = _fPeFirstData.GetValue(_planetEnvGroup),
            Spheres = _fPeSpheres.GetValue(_planetEnvGroup),
            SpheresData = _fPeSpheresData.GetValue(_planetEnvGroup),
        };

        // The frustum is group-internal scratch, re-set by the engine every OnBeginDraw.
        var frustum = _fPeFrustum.GetValue(_planetEnvGroup);
        _miPeSetMatrix.Invoke(frustum, new[] { viewProjD });
        if (_fPeFrustum.FieldType.IsValueType) _fPeFrustum.SetValue(_planetEnvGroup, frustum);

        // Static: (ref List setups, in MatrixD, in CullingFrustumD, ctx). Clears and
        // refills the data list itself; write the ref slot back in case it reallocated.
        var fillArgs = new[]
        {
            data, viewD, frustum,
            _pPeModifiersCtx.GetValue(_planetEnvGroup),
        };
        _miPeFillSetups.Invoke(null, fillArgs);
        _fPeSetupsData.SetValue(_planetEnvGroup, fillArgs[0]);
        data = (System.Collections.IList)fillArgs[0];

        // THE EMPTY CASE — the page fault in v3. No planets in our frustum means no
        // planet is visible in the feed, so the swap buys nothing: put the player's data
        // back and leave every field untouched. NEVER write a null First — a consumer
        // reading it via GetValueOrDefault binds a constant buffer at GPU address zero.
        if (data.Count == 0)
        {
            RefillList(data, _peSaved.Data);
            _peSaved = null;
            // No creates happened above, so nothing here is ours to free.
            _peCbsAreOurs = false;
            if (!_peEmptyLogged)
            {
                _peEmptyLogged = true;
                RttLog.Line("Planet env: orbit frustum sees no planets this render — swap skipped, " +
                            "player state untouched. (Normal for the part of the orbit facing away.)");
            }
            return false;
        }

        cbs.Clear();
        foreach (var item in data)
            cbs.Add(_miPeCreateCb.Invoke(_peBufMgr, new[] { "Planet Environment Setups", item }));

        _fPeFirstData.SetValue(_planetEnvGroup, data[0]);
        _fPeFirst.SetValue(_planetEnvGroup,
            _miPeCreateCb.Invoke(_peBufMgr, new[] { "planetEnvironmentSetup0", data[0] }));

        // Writes _allPlanetSpheresData AND the _allPlanetSpheres CB itself.
        _miPeFillSlim.Invoke(_planetEnvGroup, new[] { viewD });

        // Every CB in these fields is now one we minted. Consumed by RestorePlanetEnv,
        // which is the only place that can know they have been orphaned.
        _peCbsAreOurs = true;

        GuardPlanetEnvInvariant(label);
        return true;
    }

    // ---- TRANSIENT CB RECLAIM ---------------------------------------------------------
    //
    // Every nested render mints N+3 transient constant buffers — one camera
    // ("rttCameraSettings"), one per planet setup, one setup0, and the spheres buffer
    // FillPlanetEnvironmentSlimSetup writes — and then puts the engine's originals back
    // over the top of them. The engine only ever disposes what is SITTING IN THE FIELD when
    // OnEndDraw runs, so everything we displace is orphaned the instant we restore.
    //
    // Measured, three consecutive censuses, one planet under the orbit camera:
    //     AliveConstantBufferCount  26381 -> 28865 -> 31417
    //     our renders                6627 ->  7248 ->  7886
    //     = 4.00 per render, twice, to two decimals. N+3 with N=1.
    //
    // It never comes down, and BindableBufferManager asserts on it EVERY FRAME (8187 times
    // in a six-minute session), which FirstAssertionException then promotes into the
    // exit CTD. This is task #48's constant-buffer half.
    //
    // DISPOSED ONE RENDER LATE, DELIBERATELY. The engine's own policy is frame-scoped: it
    // frees transients at OnEndDraw, after the recorder has finished with them. Our pass
    // ends mid-frame, so freeing inline would hand the GPU a buffer it has not necessarily
    // read yet — a use-after-free that surfaces as a device removal, and we have already
    // paid for three of those. Draining on the NEXT render gives every buffer a strictly
    // LONGER life than the engine gives its own. That is the conservative side of the only
    // mistake that matters here.
    //
    // STAGED, NOT GUESSED. A buffer is only ever staged once we have proof we displaced it:
    // _peCbsAreOurs is set after the create loop completes and consumed by the put-back, so
    // a rebuild that bailed early leaves the engine's buffers in the fields and we free
    // nothing. Identity comparison would not work here — these are boxed structs, and two
    // boxes of the same buffer are never reference-equal.
    private static readonly List<object> _cbStaged = new();     // orphaned by THIS render
    private static readonly List<object> _cbPrev   = new();     // orphaned by the previous one
    private static MethodInfo _miCbDispose;
    private static bool _peCbsAreOurs;
    private static long _cbReclaimed, _cbReclaimFailed;

    private static void StageCb(object cb)
    {
        if (cb != null) _cbStaged.Add(cb);
    }

    // Free the previous render's orphans, then rotate this render's in behind them.
    // Never throws: a reclaim that can break the render is worse than the leak.
    private static void ReclaimStagedCbs()
    {
        for (int i = 0; i < _cbPrev.Count; i++)
        {
            var cb = _cbPrev[i];
            try
            {
                // Nullable<TransientConstantBuffer> reads back as a boxed
                // TransientConstantBuffer, so Dispose comes off the struct type itself.
                // The engine disposes a copy too (get_Value(); Dispose(); initobj), so the
                // handle plainly lives inside the struct — this mirrors its own teardown.
                _miCbDispose ??= cb.GetType().GetMethod("Dispose", Type.EmptyTypes);
                if (_miCbDispose == null) { _cbReclaimFailed++; continue; }
                _miCbDispose.Invoke(cb, null);
                _cbReclaimed++;
            }
            catch { _cbReclaimFailed++; }
        }

        _cbPrev.Clear();
        _cbPrev.AddRange(_cbStaged);
        _cbStaged.Clear();
    }

    // THE LAST FIVE. The one-render delay above fixed the LEAK (31417 alive and climbing ->
    // flat 18) but NOT the assertion, because 'AliveConstantBufferCount == 0' tests against
    // ZERO and ~5 of ours are alive at every frame end by design. A bounded leak still
    // asserts every frame, and FirstAssertionException still promotes that into the exit CTD.
    //
    // So the bootstrap calls this from a prefix on Render12EngineComponent.IRender_Present —
    // which is the method that calls BindableTexturePoolManager.OnFrameEndDisposal, the exact
    // frame in the FirstAssertionException stack. Running immediately before the engine's own
    // disposal point is safe by construction: it is where the engine frees ITS transients, so
    // the recorder is provably finished with the frame. That is the one place the inline free
    // we could not do mid-pass becomes correct.
    //
    // Drains BOTH lists — after this there is nothing of ours outstanding for the assert to
    // find. Idempotent, so a frame with no render of ours costs two empty loops.
    internal static void DrainAllStagedCbs()
    {
        if (_cbPrev.Count == 0 && _cbStaged.Count == 0) return;
        ReclaimStagedCbs();   // frees the previous batch, rotates this one in
        ReclaimStagedCbs();   // frees the batch just rotated
    }

    // Verbatim put-back of the snapshot — no second fill, no second weather-culling
    // mutation, no new CBs. Runs in the finally regardless of camera-restore order
    // because it touches only the group's own outputs.
    private static void RestorePlanetEnv()
    {
        try
        {
            if (_planetEnvState != 1 || _peSaved == null) return;
            var s = _peSaved;
            _peSaved = null;

            // Read OURS out before the put-back buries them. Only when the create loop
            // actually finished — otherwise these fields still hold the engine's own and
            // freeing them would be a double-free at OnEndDraw.
            object[] mineCbs = null;
            object mineFirst = null, mineSpheres = null;
            if (_peCbsAreOurs)
            {
                _peCbsAreOurs = false;
                mineCbs     = SnapshotList((System.Collections.IList)_fPeSetupsCbs.GetValue(_planetEnvGroup));
                mineFirst   = _fPeFirst.GetValue(_planetEnvGroup);
                mineSpheres = _fPeSpheres.GetValue(_planetEnvGroup);
            }

            RefillList((System.Collections.IList)_fPeSetupsCbs.GetValue(_planetEnvGroup), s.Cbs);
            RefillList((System.Collections.IList)_fPeSetupsData.GetValue(_planetEnvGroup), s.Data);
            _fPeFirst.SetValue(_planetEnvGroup, s.First);
            _fPeFirstData.SetValue(_planetEnvGroup, s.FirstData);
            _fPeSpheres.SetValue(_planetEnvGroup, s.Spheres);
            _fPeSpheresData.SetValue(_planetEnvGroup, s.SpheresData);

            // Staged only after the put-back has succeeded: until this line the engine
            // still owns them through the fields.
            if (mineCbs != null)
            {
                foreach (var c in mineCbs) StageCb(c);
                StageCb(mineFirst);
                StageCb(mineSpheres);
            }
        }
        catch (Exception e) { RttLog.Error("whole-scene planet env restore", e); }
    }

    // The camera half, stage 3b. Writes SettingsManager._renderView, which is the same
    // field CameraRender.InstallOurCamera uses — a proven mechanism, not a new one.
    private static bool InstallCamera(out object saved)
    {
        saved = null;
        try
        {
            var ours = CameraRender.WholeSceneRenderView();
            if (ours == null) return false;

            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var settings = core?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            _rvField ??= settings?.GetType().GetFields(Any)
                .FirstOrDefault(f => f.Name == "_renderView");
            if (settings == null || _rvField == null) return false;

            _settingsObj = settings;
            saved = _rvField.GetValue(settings);

            // Capture the PLAYER'S camera BEFORE we overwrite the global — this is the only
            // moment it is still readable. ViewerDistance.Nearest hands it back to any engine
            // job that asks for a distance from a thread that is not our render thread, which
            // is what stops our camera poisoning the player's per-entity distance cache.
            try
            {
                var rvPos = saved?.GetType().GetProperty("CameraPosition", Any)?.GetValue(saved);
                if (rvPos is Vector3D pc) ViewerDistance.SwapOpened(pc);
            }
            catch { }

            _rvField.SetValue(settings, ours);
            _swapOpenedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            return true;
        }
        catch (Exception e) { RttLog.Error("whole-scene camera install", e); return false; }
    }

    private static void RestoreCamera(object saved)
    {
        try { if (_rvField != null && _settingsObj != null) _rvField.SetValue(_settingsObj, saved); }
        catch (Exception e) { RttLog.Error("whole-scene camera restore", e); }
        // Close the guard only AFTER the global is genuinely the player's again. Closing it
        // first would leave a window where jobs read our camera with the guard disarmed —
        // small, but it is the exact bug we are fixing.
        ViewerDistance.SwapClosed();
        NoteSwapClosed();
    }

    // ---- THE SWAP WINDOW — how long is the PLAYER'S world looking through OUR camera? ---
    //
    // THE BUG THIS EXISTS TO PRICE (user, 2026-08-02): the player's main-world object LODs
    // cycle down to coarse levels and back, at random, and it has done so since the earliest
    // testing whenever the camera render is running. The user then spotted the thing that
    // makes it tractable — the feed's own residual flora dropout happens IN SYNC with it. One
    // bug, one shared cause.
    //
    // THE MECHANISM UNDER TEST. InstallCamera writes SettingsManager._renderView, a
    // PROCESS-WIDE GLOBAL. The engine's LOD, streaming and culling jobs run asynchronously on
    // job threads. Any that samples RenderView.CameraPosition while our swap is installed
    // computes the PLAYER'S object LODs from OUR camera — 3906 km away — and collapses them
    // to the coarsest level until the next update after we restore.
    //
    // WHAT THIS MEASURES, and why it is the right first number: the DUTY CYCLE. If our swap
    // is installed for x% of wall-clock time, then roughly x% of async samples land inside it
    // and the cycling rate should scale with x. That converts a vague "intermittent" into a
    // prediction, and the render-rate A/B then either confirms or kills it.
    //
    // IT ALSO SETTLES AN OPEN CONTRADICTION. This task previously recorded "the feed causes
    // it, but NOT via render rate". If the duty cycle turns out to be near 100% — because we
    // render EVERY frame (wholeSceneIntervalMs = 0) and hold the swap across the whole nested
    // Draw — then lowering the render rate would barely move it, and that old null result is
    // explained rather than contradictory.
    //
    // Stopwatch ticks, not TickCount64: the window is single-digit milliseconds and
    // TickCount64's resolution (~15 ms) would report most of them as zero.
    private static long _swapOpenedTicks;
    private static long _swapTotalTicks, _swapCount, _swapMaxTicks;
    private static long _swapWindowStartTicks, _swapReportTicks;

    // The SETUP / DRAW / TEARDOWN split of that window. Only DRAW genuinely needs our camera
    // to be the installed global; setup and teardown are exposure we may be able to reclaim
    // by moving work outside the bracket. This says how much is worth moving BEFORE any code
    // is moved — the alternative is guessing, which is how tonight went wrong twice.
    private static long _setupTicks, _drawTicks, _teardownTicks, _drawEndTicks;

    // Per-render copies, so an outlier can be split without the 15 s aggregate polluting it.
    private static long _lastSetupTicks, _lastDrawTicks;
    private static int _swapOutliers;

    // 50 ms: two frames at the 58 fps mod-off baseline. Below that it is jitter and the
    // averages already describe it; above it the user sees a hitch.
    private const double SwapOutlierMs = 50.0;

    private static void NoteSwapClosed()
    {
        if (_swapOpenedTicks == 0) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var held = now - _swapOpenedTicks;
        _swapOpenedTicks = 0;
        if (held < 0) return;

        _swapTotalTicks += held;
        _swapCount++;
        if (held > _swapMaxTicks) _swapMaxTicks = held;
        long teardownThis = _drawEndTicks != 0 ? now - _drawEndTicks : 0;
        if (_drawEndTicks != 0) { _teardownTicks += teardownThis; _drawEndTicks = 0; }
        if (_swapWindowStartTicks == 0) _swapWindowStartTicks = now;

        // OUTLIER ATTRIBUTION — the hitch, named rather than averaged.
        //
        // The 15 s report says mean 3.70 ms and max 257.07 ms in the same breath, and an
        // average that contains a 257 ms frame is not describing anything that happens. The
        // engine agrees: with the feed running, MAX frame per minute is 378-730 ms against a
        // 24-34 ms mod-off baseline, while our own submit cost (`ourDraw`) stays at 3.1 ms
        // mean / 4.6 ms max. So the hitch is NOT the draw we measure, and averaging will
        // never find it.
        //
        // Split THIS window rather than the 15 s aggregate: setup and teardown are ~0.2 ms
        // combined at the mean, so if a 250 ms window is mostly setup we know it is a build
        // or a lock, and if it is mostly draw we know it is the engine inside our nested
        // call. One line per outlier, so a quiet session says nothing at all.
        double heldMs = 1000.0 * held / System.Diagnostics.Stopwatch.Frequency;
        if (heldMs >= SwapOutlierMs)
        {
            _swapOutliers++;
            double freq1 = System.Diagnostics.Stopwatch.Frequency;
            double setupMs = 1000.0 * _lastSetupTicks / freq1;
            double drawMs  = 1000.0 * _lastDrawTicks  / freq1;
            double downMs  = 1000.0 * teardownThis    / freq1;
            // Name the ACTUAL largest phase. The first version of this line compared setup
            // against draw and said "DRAW dominates" for everything else — which promptly
            // reported "DRAW dominates" for an 82.9 ms window whose draw was 2.9 ms and
            // whose teardown was 79.9 ms. A diagnostic that names the wrong phase is worse
            // than none: it sends the next hour of work at the wrong code.
            string worst = setupMs >= drawMs && setupMs >= downMs
                ? "SETUP dominates — a resource build, a reflection miss or a lock, not the render"
                : downMs >= drawMs
                    ? "TEARDOWN dominates — the unwind, not the render: restoring the scoped settings, " +
                      "contexts and camera is what blocked"
                    : "DRAW dominates — the engine stalled inside our nested Draw; compare against " +
                      "ourDraw(cpu submit) in the PERF line, which measures the same call";

            RttLog.Line($"!!! SWAP OUTLIER #{_swapOutliers}: our camera was installed for {heldMs:F1} ms " +
                        $"(setup {setupMs:F1}, draw {drawMs:F1}, teardown {downMs:F1}). " +
                        $"Feed render #{_renderCount}. {worst}.");
        }

        var nowMs = Environment.TickCount64;
        if (nowMs - _swapReportTicks < 15000) return;
        _swapReportTicks = nowMs;

        double freq = System.Diagnostics.Stopwatch.Frequency;
        double elapsed = (now - _swapWindowStartTicks) / freq;
        double heldSec = _swapTotalTicks / freq;
        double duty = elapsed > 0 ? 100.0 * heldSec / elapsed : 0.0;

        RttLog.Line($"CAMERA SWAP WINDOW: our RenderView was installed {_swapCount} time(s) over " +
                    $"{elapsed:F1}s, holding it {heldSec:F2}s = {duty:F1}% OF WALL CLOCK " +
                    $"(mean {1000.0 * heldSec / Math.Max(1, _swapCount):F2} ms, max " +
                    $"{1000.0 * _swapMaxTicks / freq:F2} ms). " +
                    (duty > 60.0
                        ? "HIGH: any async job sampling Settings.RenderView is MORE LIKELY THAN NOT to see " +
                          "OUR camera, so the player's LOD would collapse constantly and render RATE would " +
                          "barely change it — which explains the old 'not via render rate' null result."
                        : duty > 10.0
                            ? "MODERATE: a real race window. Cycling frequency should scale with this, so " +
                              "the render-rate A/B is now a sharp test."
                            : "LOW: too small to explain frequent cycling on its own — if the player's LODs " +
                              "still collapse often, the cause is a PERSISTENT WRITE we leave behind, not a " +
                              "sampling race."));

        // Rides this report rather than logging per skip: saves are minutes apart, so a
        // per-event line would read as silence and a cumulative count reads as a rate.
        // Non-zero is the GOOD state — it means a save asked for its thumbnail and we
        // stood aside instead of handing it a 1024x1024 feed frame.
        if (_screenshotSkips > 0)
            RttLog.Line($"SCREENSHOT GUARD: {_screenshotSkips} feed frame(s) skipped so far because the engine " +
                        "had a screenshot request pending — save thumbnails and F12 captures come from the " +
                        "player's buffer, not ours.");

        // LOG COST. Emitted before the census so it is adjacent to the other per-window
        // numbers. See RttLog.Emit for why this class became a suspect: the mod's only
        // global lock, held across a synchronous file write, taken from both the render
        // thread and the sim-pump thread. maxWait in the hundreds of ms would place the
        // hitch here; single-digit maxWait rules it out and the search moves on.
        {
            var (n, writeMs, maxWriteMs, queued, dropped) = RttLog.TakeStats();
            if (n > 0 || queued > 0 || dropped > 0)
                RttLog.Line($"LOG COST: {n} line(s) written by the BACKGROUND writer — {writeMs:F1} ms total / " +
                            $"{maxWriteMs:F1} ms worst single write, {queued} still queued, {dropped} dropped. " +
                            (maxWriteMs > 50.0
                                ? "The disk still stalls (SE2 streams textures off the same drive), but it now " +
                                  "stalls a background thread instead of the render thread."
                                : "Disk healthy this window.") +
                            (dropped > 0
                                ? " !!! DROPPED LINES: the writer could not keep up and the queue hit its cap. " +
                                  "The log has GAPS — do not read a quiet stretch as nothing happening."
                                : ""));
        }

        // POOL CENSUS. The acceptance test for task #48 is that this line reads all zeroes:
        // the game promotes the first assertion of a session into a fatal exception at exit
        // (see FirstAssertionException), so a per-frame assert is a per-session crash.
        if (_poolSamples > 0)
        {
            var deltas = new System.Text.StringBuilder();
            for (int i = 0; i < _poolLeaked.Length; i++)
                if (_poolLeaked[i] != 0) deltas.Append($" {PoolFields[i]}={_poolLeaked[i]:+#;-#;0}");

            // The absolutes are the ones the engine asserts on. Reported per pool that was
            // ever non-zero after our pass, with how OFTEN, because "peaked at 2 once" and
            // "2 on every single render" are different bugs.
            var abs = new System.Text.StringBuilder();
            for (int i = 0; i < _poolPeakAfter.Length; i++)
                if (_poolPeakAfter[i] != 0 || _poolPeakBefore[i] != 0)
                    abs.Append($"\n    {PoolFields[i]}: peak {_poolPeakBefore[i]} before / {_poolPeakAfter[i]} after our Draw, " +
                               $"non-zero after on {_poolNonZeroAfter[i]}/{_poolSamples} render(s)");

            // THE ACCEPTANCE TEST FOR THE CB RECLAIM. AliveConstantBufferCount climbed a
            // dead-linear 4.00 per render before this existed; the reclaim should hold the
            // ABSOLUTE flat instead. Reported as a rate so one window grades it: reclaimed
            // per render should sit at N+3, and the peak should stop rising window over
            // window. A non-zero failed count means Dispose could not be reached at all and
            // the leak is still running.
            RttLog.Line($"CB RECLAIM: {_cbReclaimed} transient constant buffer(s) freed, " +
                        $"{_cbReclaimFailed} could not be freed, " +
                        $"{_cbStaged.Count + _cbPrev.Count} in flight. " +
                        (_cbReclaimFailed > 0
                            ? "!!! Dispose was unreachable — the per-frame leak is STILL RUNNING."
                            : "Compare the ABSOLUTE peak below ACROSS windows: flat = fixed, " +
                              "still climbing ~4/render = the reclaim is not reaching the real owner."));

            RttLog.Line($"POOL CENSUS over {_poolSamples} render(s). " +
                        $"NET across our Draw:{(deltas.Length == 0 ? " every texture pool balanced," : deltas.ToString() + ",")}" +
                        $" constant buffers {_cbLeaked:+#;-#;0}" +
                        (deltas.Length == 0 && _cbLeaked == 0
                            ? " — so WE are not the one unbalancing them."
                            : " — WE are unbalancing them.") +
                        $"\n  ABSOLUTE outstanding (this is what OnFrameEndDisposal asserts on): " +
                        $"constant buffers peak {_cbPeakBefore} before / {_cbPeakAfter} after." +
                        (abs.Length == 0
                            ? " Every texture pool read ZERO on both sides of every render — if the engine is still " +
                              "asserting, the borrow happens AFTER our postfix returns and this sampling point cannot see it."
                            : abs.ToString()));
        }

        // THE HEADROOM LINE. setup+teardown is what could in principle move outside the
        // bracket; draw is irreducible without changing the architecture.
        double setupSec = _setupTicks / freq, drawSec = _drawTicks / freq, downSec = _teardownTicks / freq;
        double reclaimable = elapsed > 0 ? 100.0 * (setupSec + downSec) / elapsed : 0.0;
        RttLog.Line($"CAMERA SWAP SPLIT: setup {1000.0 * setupSec / Math.Max(1, _swapCount):F2} ms, " +
                    $"draw {1000.0 * drawSec / Math.Max(1, _swapCount):F2} ms, " +
                    $"teardown {1000.0 * downSec / Math.Max(1, _swapCount):F2} ms per render. " +
                    $"Setup+teardown = {reclaimable:F1} percentage points of the {duty:F1}% duty cycle — " +
                    (reclaimable > duty * 0.25
                        ? "WORTH MOVING: a meaningful share of our exposure is not the draw at all."
                        : "NOT WORTH MOVING: the draw dominates, so narrowing the bracket buys little and " +
                          "the fix has to be hooking the consumers (option 2) or the outliers instead."));

        _swapTotalTicks = 0; _swapCount = 0; _swapMaxTicks = 0; _swapWindowStartTicks = now;
        _setupTicks = 0; _drawTicks = 0; _teardownTicks = 0;

        StageTimingReport();
    }

    // ---- PER-STAGE TIMING TABLE (task #63) ------------------------------------------------
    // Reads RttBridge.StageTicks/StageRuns (bootstrap accumulators, this-pass-only) and
    // prints the DELTA since the previous report, sorted by cost. Reflective per the frozen-
    // bootstrap rule: an old bootstrap has no arrays and this says so once, then stays quiet.
    private static FieldInfo _fiStageTicks, _fiStageRuns;
    private static bool _stageTimingMissing, _stageTimingLogged;
    private static long[] _stagePrevTicks;
    private static int[] _stagePrevRuns;

    private static readonly string[] StageNames =
    {
        "0 TLASBuild", "1 RtPrep+SceneFinalize", "2 EnvProbe", "3 Shadows", "4 Exposure",
        "5 Surfels", "6 LightClusters", "7 Particles", "8 Decals", "9 HBAO",
        "10 Lighting", "11 MainView", "12 DirLight", "13 LocalLights", "14 CloudShadowMap",
        "15 Atmosphere", "16 DrawUI", "17 RaytraceGI", "18 ComputeGI", "19 FSRPrep",
        "20 (fsr gate)", "21 Flares", "22 CloudShadowJob", "23 CloudWeather", "24 AtmoLUT",
        "25 (exposure ro)", "26 CloudJob", "27 ProbeMgr", "28 LocalLightMgr", "29 SSR",
        "30 RtPrepare", "31 (occl)", "32", "33"
    };

    private static void StageTimingReport()
    {
        if (_stageTimingMissing) return;
        try
        {
            if (_fiStageTicks == null)
            {
                var bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
                _fiStageTicks = bridge?.GetField("StageTicks", BindingFlags.Public | BindingFlags.Static);
                _fiStageRuns  = bridge?.GetField("StageRuns",  BindingFlags.Public | BindingFlags.Static);
                if (_fiStageTicks == null || _fiStageRuns == null)
                {
                    _stageTimingMissing = true;
                    RttLog.Line("STAGE TIMING: bootstrap predates the accumulators — restart to adopt. No table.");
                    return;
                }
            }

            var ticks = (long[])_fiStageTicks.GetValue(null);
            var runs  = (int[])_fiStageRuns.GetValue(null);
            if (ticks == null || runs == null) return;

            _stagePrevTicks ??= new long[ticks.Length];
            _stagePrevRuns  ??= new int[runs.Length];

            double tickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            var rows = new List<(double ms, string line)>();
            double totalMs = 0;
            for (int i = 0; i < ticks.Length && i < StageNames.Length; i++)
            {
                long dt = ticks[i] - _stagePrevTicks[i];
                int dr = runs[i] - _stagePrevRuns[i];
                _stagePrevTicks[i] = ticks[i];
                _stagePrevRuns[i] = runs[i];
                if (dr <= 0) continue;
                double ms = dt * tickMs;
                totalMs += ms;
                rows.Add((ms, $"{StageNames[i]}: {ms / dr:F2}ms x{dr}"));
            }
            if (rows.Count == 0)
            {
                if (!_stageTimingLogged)
                { _stageTimingLogged = true; RttLog.Line("STAGE TIMING: armed, no stage ran in our pass this window yet."); }
                return;
            }
            rows.Sort((a, b) => b.ms.CompareTo(a.ms));
            var sb = new System.Text.StringBuilder();
            int shown = 0;
            foreach (var r in rows) { if (shown++ == 10) break; sb.Append(shown == 1 ? "" : "  |  ").Append(r.line); }
            RttLog.Line($"STAGE TIMING (our pass, window total {totalMs:F0}ms CPU-wall): {sb}" +
                        (rows.Count > 10 ? $"  (+{rows.Count - 10} smaller)" : ""));
        }
        catch (Exception e)
        {
            _stageTimingMissing = true;
            RttLog.Line("STAGE TIMING: reporter failed and is disarmed — " + e.Message);
        }
    }

    private static MethodInfo _miDraw;
    private static Type _coreType;
    private static FieldInfo _sbField, _rvField;
    private static object _settingsObj;

    // ---- THE HOT-RELOAD PARK: GENERALISED ---------------------------------------------
    //
    // THE LEAK, priced from the engine's own counters on 2026-08-02:
    //     fresh session (0 reloads):  KnownNonStreaming 6024 MB, RealAvailableStreaming +6505 MB
    //     after SIX reloads:          KnownNonStreaming 12781 MB, RealAvailableStreaming -1240 MB
    // ~1.1 GB of NON-EVICTABLE memory stranded per hot reload. Once RealAvailableStreaming
    // goes negative the streaming pool is clamped to a floor and thrashes, which is the LOD
    // cycling AND the frame hitching — one global pool, so both views at once.
    //
    // WHY: every one of our GPU-resource owners hangs off Feeds.Cur, which lives in the
    // COLLECTIBLE logic assembly. A reload discards the registry and the objects become
    // unreachable while still holding depth buffers, the GBuffer array, the LDR texture,
    // visibility lists and occlusion contexts. Nothing can dispose what nothing can reach.
    //
    // The probe manager already had a bespoke version of this fix; these two helpers are the
    // same idea written once so the remaining owners get it too. Failing to resolve the park
    // is NOT fatal — we simply construct as before and leak as before, which is strictly no
    // worse than the old behaviour and lets an older bootstrap still run.
    private static object[] ParkArray(string name)
    {
        try
        {
            return Type.GetType("RttProbe.RttBridge, RttProbe")
                ?.GetField(name, BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as object[];
        }
        catch { return null; }
    }

    // Type-checked on purpose: a slot parked by an older build can hold an object of a type
    // this build cannot use, and adopting it blind would be a crash rather than a leak.
    private static object AdoptParked(string parkName, Type expected)
    {
        try
        {
            var slots = ParkArray(parkName);
            var i = Feeds.Cur.Id;
            if (slots == null || i < 0 || i >= slots.Length) return null;
            var parked = slots[i];
            if (parked == null) return null;
            if (expected != null && !expected.IsInstanceOfType(parked)) return null;

            if (_parkAdoptLogged.Add(parkName + i))
                RttLog.Line($"HOT-RELOAD PARK: adopted the parked {parkName}[{i}] instead of building a new one. " +
                            "Its GPU resources survive this reload rather than being stranded beyond the reach " +
                            "of anything that could free them (~1.1 GB per reload before this).");
            return parked;
        }
        catch { return null; }
    }

    private static void ParkIt(string parkName, object value)
    {
        try
        {
            var slots = ParkArray(parkName);
            var i = Feeds.Cur.Id;
            if (slots == null || i < 0 || i >= slots.Length) return;
            slots[i] = value;                 // every construction writes the park, so the
        }                                     // NEXT reload adopts instead of building
        catch { }
    }

    private static readonly HashSet<string> _parkAdoptLogged = new();

    // PER-FEED (phase C1a): this feed's rate stamp. The phase E slot scheduler replaces
    // the comparison against WholeSceneIntervalMs with "is it my turn", but the stamp
    // itself stays exactly here, one per feed.
    private static long _lastRenderMs
    { get => Feeds.Cur.LastRenderMs; set => Feeds.Cur.LastRenderMs = value; }

    // Engine frames to yield after a (re)build before the first second render. 30 is ~0.5 s
    // at 60 fps and comfortably longer than the probe reprocess measured in the crash dump,
    // while being invisible at a config save.
    private const int SettleFrames = 30;

    // PER-FEED (phase C1a). Per-feed settling is also what phase E3 needs: a global
    // quality change with N feeds live rebuilds them STAGGERED, one settle window each,
    // rather than dropping every feed into the same probe-reprocess window at once.
    private static int _settleFrames
    { get => Feeds.Cur.SettleFrames; set => Feeds.Cur.SettleFrames = value; }
    private static int _renderCount
    { get => Feeds.Cur.RenderCount; set => Feeds.Cur.RenderCount = value; }

    // ONE ENGINE FRAME OF SETTLING, for the feed currently scoped. Called from
    // FeedGate.PumpOne, which visits every slot every frame — so the window is measured in
    // engine frames, which is what it was always specified in, rather than in "frames this
    // feed happened to win" (phase F1).
    internal static void TickSettle()
    {
        if (_settleFrames <= 0) return;

        // ONLY WHILE THIS FEED IS RUNNING. A dormant feed is not settling — it is stopped,
        // and it has not yet rebuilt the thing the window exists to wait out.
        //
        // This line is the fix for a CTD I caused (2026-08-01 11:30:24, DXGI_ERROR_DEVICE_
        // REMOVED). Moving the countdown out of TryRender and into the per-frame pump was
        // right for its stated purpose — the window is specified in ENGINE frames and used
        // to advance only while its feed was winning render slots — but TryRender also only
        // ran while the gate was ACTIVE, and dropping that condition was not part of the
        // intended change. The consequence: a feed's window drained during dormancy (log:
        // "[feed 1] settled after the rebuild" 0.55 s after that feed's own teardown, and
        // "[feed 2]/[feed 3]" settling although those slots have never existed), so on the
        // way back it built a ScreenBuffers, built a DrawContextManager and called Draw
        // 2 ms later, straight into the probe reprocess.
        //
        // The lesson generalises past this bug: when you move a countdown to a better clock,
        // move its GUARD with it. The old site's early-returns are part of the specification,
        // not scaffolding around it.
        if (!FeedGate.Active) return;

        if (--_settleFrames > 0) return;
        RttLog.Line($"Whole-scene: settled after the rebuild ({SettleFrames} engine frames); " +
                    "second renders resume. This window exists because a rebuild forces the " +
                    "shared EnvironmentProbeManager to reprocess every probe, and rendering " +
                    "into that batch is a device removal.");
    }

    // Construct the second DrawContextManager. One attempt per load, hot-reloadable,
    // falls back to the shared one (current behaviour) on any failure rather than
    // disabling the route — a shared-context render is degraded, not broken.
    private static void EnsureDrawContexts()
    {
        if (_dcBuilt || _ourDrawContexts != null) return;

        // THE LATCH MEANS "SUCCEEDED", NOT "ATTEMPTED". It used to be set right here, before
        // a single line of construction ran — so the `if (t == null) return` below, and any
        // exception inside the try, left _dcBuilt = true with _ourDrawContexts = null, for
        // the rest of the session, never retried.
        //
        // That never crashed anything, which is exactly why it survived: a null context is
        // survivable by design (our render falls back to the engine's contexts — degraded,
        // not broken). But it means a transient build failure silently downgrades the feed
        // permanently, and the ONLY evidence would be the absence of a log line. This is the
        // same shape as the CopyToFeed view-lookup latch that made feed 1 render 291 frames
        // into a black panel, and it was found the same way: looking for what did NOT get
        // logged.
        //
        // Now: the success latch is set on success, and failure gets its own budget so a
        // genuinely broken build stops retrying — and SAYS SO, once, instead of going quiet.
        try
        {
            var t = Type.GetType("Keen.VRage.Render12.Core.Systems.DrawContextManager, VRage.Render12");
            if (t == null) { RttLog.Line("Whole-scene: DrawContextManager type not found."); NoteDcFailure(); return; }

            // DO NOT PARK THE DrawContextManager. BACKED OUT 2026-08-02 AFTER IT CRASHED.
            //
            // Parking this one looked identical to parking ScreenBuffers, and it is not. A
            // DrawContextManager owns a CascadeShadowsContext, which holds POOLED GPU TOKENS.
            // Adopting it across a hot reload carries tokens borrowed by the PREVIOUS logic
            // assembly into a new one, and the first thing our own-shadows path does is:
            //     BeginOwnShadows -> FlushInto -> CascadeShadowsContext.FlushUpdates()
            //         -> ResizeCascades(count, size) -> Token.Dispose()
            // The pool then asserts
            //     '_lifetime == Lifetime.Used' evaluated to false   (GPUResourcePool.cs:168)
            // and the follow-on disposes an already-dead depth stencil
            //     D3DCommittedResourceWrap.GPUDispose(), 'IsValid' evaluated to false
            // which takes the render thread down. The assertion appears in exactly ONE log in
            // this project's history: the first session after the park was added.
            //
            // WHY THAT MATTERED MORE THAN THE MEMORY: the user could no longer exit to the
            // main menu without a CTD, and exiting triggers a WORLD SAVE — a save interrupted
            // by a render-thread crash is how "this save is corrupt" happens. Never trade a
            // crash-during-save for a memory saving.
            //
            // ScreenBuffers stays parked: it owns plain textures, not pool tokens, and it
            // carried most of the win anyway (the InitializeBuffers skip is what took the
            // ratchet from +1637 MB to +36 MB per reload).
            //
            // If this is ever revisited, the missing piece is releasing the cascade tokens
            // BEFORE the assembly unloads, or rebuilding CascadeShadowsContext on adoption —
            // not adopting the manager wholesale.
            _ourDrawContexts = Activator.CreateInstance(t);

            // Share the ENGINE'S DirectionalLightShadowResources into our manager.
            //
            // ComputeDirectionalLighting pulls this off DrawContexts (IL_0068) and hands
            // it to the shadow-mask draw. Our fresh manager's copy has never had cascades
            // rendered into it — our pending-work queues are never filled by the game and
            // we skip RenderShadows anyway — so the mask draw died on an empty Nullable
            // the first time it ran against our contexts.
            //
            // Sharing is safe here where sharing the CONTEXTS was not: the mask draw only
            // READS the resources (cascade depth maps + setup constant buffer). The feed
            // gets the player's real sun-shadow cascades — approximately correct near the
            // player, degrading with distance since cascades are player-centred. Honest
            // limitation, revisit if remote shadows matter.
            //
            // DISPOSE SAFETY: our manager's Dispose would dispose whatever this property
            // holds — which would be the ENGINE'S live object. Reset() puts the fresh one
            // back first, so each side disposes only what it created.
            //
            // ...UNLESS wholeSceneOwnShadows is on, in which case we keep our own and
            // BeginOwnShadows fills it each render. That is the upgrade path out of the
            // limitation above: cascades fitted around OUR camera instead of the player's.
            var resProp = t.GetProperty("DirectionalLightShadowResources", Any);
            var engineDc = _coreType?.GetField("DrawContexts", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            bool ownShadows = FeedConfig.WholeSceneOwnShadows > 0;
            if (resProp != null)
            {
                _ourFreshShadowResources = resProp.GetValue(_ourDrawContexts);
                if (!ownShadows && engineDc != null)
                    resProp.SetValue(_ourDrawContexts, resProp.GetValue(engineDc));
            }

            // SHARE THE ENGINE'S FLARES CONTEXT, and skip the flare pass (stage 21).
            //
            // Flare registration goes through the GLOBAL, not through whoever owns the
            // context: PointLightEntityComponent.Init / SetParameters / OnRemovedFromScene,
            // the spot and particle equivalents, and SceneManager.UpdateFlareDefinitions
            // all read CoreSystems.DrawContexts.LensFlares. Our nested Draw swaps that
            // global ten times a second, so a light created, retuned or removed inside one
            // of those windows talks to OUR context — and a SetParameters that lands on
            // the wrong one leaves the engine's copy holding stale parameters. A flare
            // stuck where the light no longer is.
            //
            // That is the best candidate for "the planet's atmosphere appears, completely
            // unattached to the planet". Sharing removes the window: whichever manager is
            // installed, registration reaches the same context.
            //
            // Sharing WITHOUT skipping stage 21 would be worse than the disease, because
            // RenderFlares calls ProcessFinishedFrame and PrepareReadback — the flare
            // occlusion readback, which integrates across frames. We read the
            // definitions; we never advance the state.
            //
            // Our own context is kept for the dispose swap. It was always empty: created
            // by CreateInitialContexts and never given a single definition, because
            // registration goes through the global and the global belongs to the engine
            // whenever a light is actually created. So the feed loses nothing it had.
            // With wholeSceneOwnFlares ON we take the other route: keep OUR context and
            // share only its DEFINITION members, so the feed gets real flares while the
            // player's occlusion readback stays ours-proof. See FeedConfig.WholeSceneOwnFlares
            // for the full offline verification and the one bounded risk.
            var flareProp = t.GetProperty("LensFlares", Any);
            string flareState = "NOT shared — LensFlares unreachable, flare registration during our " +
                                "render window still lands in our empty context";
            if (flareProp != null && engineDc != null)
            {
                _ourFreshFlares = flareProp.GetValue(_ourDrawContexts);
                var engineFlares = flareProp.GetValue(engineDc);

                if (FeedConfig.WholeSceneOwnFlares)
                {
                    // Ours stays installed. Remember the engine's so MirrorFlareDefinitions
                    // can re-read the definition members before every render.
                    _engineFlares = engineFlares;
                    int copied = MirrorFlareDefinitions();
                    flareState = _ourFreshFlares == null
                        ? "OWN FLARES REQUESTED but our context is null — falling back to none"
                        : $"OURS, with {copied}/{FlareDefFields.Length} definition members shared from the " +
                          "engine (stage 21 now RUNS: the feed renders flares against our own " +
                          "occlusion buffers, so the player's readback is never advanced by us)";
                }
                else
                {
                    flareProp.SetValue(_ourDrawContexts, engineFlares);
                    _engineFlares = null;
                    flareState = ReferenceEquals(flareProp.GetValue(_ourDrawContexts), engineFlares)
                        ? "SHARED from the engine (registration cannot land in the wrong context; " +
                          "stage 21 keeps us from advancing its occlusion readback)"
                        : "SHARE FAILED — the property did not take the engine's context";
                }
            }

            RttLog.Line("Whole-scene: SECOND DrawContextManager built — its ctor runs " +
                        "CreateInitialContexts, so this is the full context family (visibility " +
                        "lists, occlusion, geometry buffers, shared counters, LOD transitions) " +
                        "owned by us. The player's contexts are no longer written by our render. " +
                        "DirectionalLightShadowResources " +
                        (ownShadows
                            ? $"OURS — own cascades, mode {FeedConfig.WholeSceneOwnShadows}, fitted around the orbit camera."
                            : engineDc != null
                                ? "SHARED from the engine (read-only in the mask draw)."
                                : "NOT shared — engine manager unreachable.") +
                        " LensFlares " + flareState + ".");

            // ONLY here, with the manager actually constructed and configured.
            _dcBuilt = true;
            _dcFailures = 0;

            // ARM THE SETTLE WINDOW AT ITS ACTUAL CAUSE, not at a proxy for it.
            //
            // Constructing this manager is what trips the engine's context-reset path and
            // sets EnvironmentProbeManager._forceReprocess — so THIS line is the hazard, and
            // the window that protects against it belongs here. Reset() also arms it, which
            // covered the config-change case and was mistaken for coverage of every case.
            //
            // It was not. A feed's FIRST activation never ran Reset under its own scope
            // (LogicEntry resets feed 0 only), so it built a DrawContextManager and rendered
            // into the reprocess two milliseconds later with no window at all. That is a
            // pre-existing hole; it became reachable on every gate cycle when the settle
            // countdown started draining while feeds were dormant, and it device-removed the
            // game on 2026-08-01 11:30:24 — Draw called 2 ms after this line, exactly the
            // 2026-07-29 fault. Arming here cannot miss: no build, no window needed; a build,
            // and the window is armed by the build itself.
            _settleFrames = SettleFrames;
        }
        catch (Exception e)
        {
            RttLog.Error("build second DrawContextManager", e);
            NoteDcFailure();
        }
    }

    // Give up after a few consecutive failures so a genuinely broken build is not retried
    // every frame forever, and say so ONCE when that happens. Silence is what made the
    // original latch invisible; a feed running on the engine's shared contexts is a real
    // degradation and the log should name it rather than leave it to be inferred.
    // PER-FEED, like _dcBuilt itself. A shared counter would let feed 0's three failures
    // latch feed 1 out of ever building its own contexts — which is precisely the bug shape
    // fixed twice already tonight (the copy budget, and the gate's startup flag).
    private static int _dcFailures
    { get => Feeds.Cur.DcFailures; set => Feeds.Cur.DcFailures = value; }

    private static void NoteDcFailure()
    {
        _ourDrawContexts = null;                 // never leave a half-built manager installed
        if (++_dcFailures < 3) return;
        _dcBuilt = true;                         // stop retrying
        RttLog.Line("!!! Whole-scene: our DrawContextManager failed to build 3 times running. Giving up " +
                    "for this feed — it will render against the ENGINE'S shared contexts, which is degraded " +
                    "(the player's visibility lists and counters get written by our render too). A gate cycle " +
                    "retries. This line exists because the previous behaviour was to fail silently and forever.");
    }

    // ---- OWN FLARES CONTEXT, SHARED DEFINITIONS (goal 4.3) ----------------------------
    //
    // The four members that carry WHAT the flares are, as opposed to the per-frame state of
    // drawing them. Verified with EngineQuery to be written only by FlaresContext..ctor and
    // UpdateFlaresBuffer, neither of which is in the render path — so pointing ours at the
    // engine's cannot be mutated by our own render.
    private static readonly string[] FlareDefFields =
    {
        "_flaresByGuid",               // Dictionary<Guid, FlareHandle> — the registry
        "_texturePinsByGuid",          // Dictionary<Guid, (ManagedTexturePin, int)>
        "_flaresBuffer",               // IManagedROBuffer — the GPU definition buffer (RO)
        "_flareDefinitionsAllocator",  // SimpleIndexAllocator — index -> buffer slot
    };

    // PER-FEED (phase C1a): the engine's context, BORROWED. Per-feed because each
    // instance's mirror is paired with its own OurFreshFlares and its own originals —
    // and mixing those pairs across feeds is precisely the Rule-25 mistake that
    // disposed the engine's flare buffer twice.
    private static object _engineFlares
    { get => Feeds.Cur.EngineFlares; set => Feeds.Cur.EngineFlares = value; }

    private static bool _flareMirrorLogged;

    // THE INVARIANT THAT WAS MISSING, and it cost a CTD on 2026-07-29.
    //
    // Stage 21 was force-run on FeedConfig.WholeSceneOwnFlares alone, while
    // MirrorFlareDefinitions returns 0 SILENTLY whenever either context is null. A hot
    // reload nulls both statics in Reset(), the next render force-ran RenderFlares before
    // the DrawContextManager rebuild had re-established them, and FlaresContext.
    // GetFlareConstants dereferenced a null _flaresBuffer:
    //
    //     NullReferenceException at FlaresContext.GetFlareConstants()
    //       at FlaresOcclusionJob.DoWork -> RenderFlares_Patch1 -> Draw_Patch1
    //
    // "Own the context" and "run the pass" are one decision, not two, and the config flag
    // only expresses the INTENT. This flag expresses the FACT: our context really does have
    // a flare definition buffer right now. Stage 21 consults this, so a mirror that fails
    // for any reason degrades to the old behaviour — no flares in the feed — instead of
    // taking the process down.
    private static bool _flaresReady
    { get => Feeds.Cur.FlaresReady; set => Feeds.Cur.FlaresReady = value; }

    // Re-read the definition members from the engine's context into ours. Called before
    // EVERY render, not once at build time, deliberately: UpdateFlaresBuffer REPLACES
    // _flaresBuffer on whichever context is installed, so a one-shot copy could go stale
    // and stay stale. Re-reading makes the worst case "one frame behind", not "wrong
    // forever". Returns how many members were successfully shared.
    // The ctor-original values of the four mirrored fields, captured before the FIRST
    // overwrite. These are what FlaresContext.Dispose is entitled to see at teardown:
    // _flaresBuffer null (the ctor never writes it; Dispose null-checks it), the empty
    // dictionaries and the allocator the ctor built. ScrubMirroredFlareRefs writes them
    // back before our DrawContextManager is disposed. Nulling instead would NRE inside
    // Dispose — it iterates _flaresByGuid unguarded (verified in IL).
    private static object[] _flareOriginals
    { get => Feeds.Cur.FlareOriginals; set => Feeds.Cur.FlareOriginals = value; }

    private static void ScrubMirroredFlareRefs()
    {
        _flaresReady = false;

        // THE HOLE THIS GUARD CLOSES. The line below used to be a plain
        //     if (_ourFreshFlares == null || _flareOriginals == null) return;
        // and the second half of that condition is a silent no-scrub: a context that still
        // holds the ENGINE'S _flaresBuffer goes on to be disposed, which frees the PLAYER'S
        // flare buffer, and the player's very next frame dies exactly here:
        //     NullReferenceException at FlaresContext.GetFlareConstants()
        //       at FlaresOcclusionJob.DoWork -> RenderFlares_Patch1 -> Draw_Patch1
        // — the same signature as the 2026-07-29 CTD, and the one that killed the session
        // at 19:51 on 2026-08-02 while exiting to the main menu.
        //
        // The pair is captured together and dropped together WITHIN one assembly load, so
        // originals-null-while-context-live should be impossible. It stopped being
        // impossible the moment the DrawContextManager was parked across hot reloads (a
        // context from the previous load, originals from a registry that no longer exists).
        // That park is backed out, but "should be impossible" is what the last four CTDs
        // had in common, so this checks rather than assumes: if our buffer is REFERENCE-
        // EQUAL to the engine's, unhook it before anything can dispose it. Only
        // _flaresBuffer is nulled — the ctor never writes it and Dispose null-checks it,
        // whereas the dictionaries and the allocator are walked unguarded and must stay
        // non-null.
        if (_ourFreshFlares != null && _flareOriginals == null)
        {
            try
            {
                var fb = _ourFreshFlares.GetType().GetField("_flaresBuffer", Any);
                var ours = fb?.GetValue(_ourFreshFlares);
                var theirs = _engineFlares != null
                    ? _engineFlares.GetType().GetField("_flaresBuffer", Any)?.GetValue(_engineFlares)
                    : null;
                if (ours != null && ReferenceEquals(ours, theirs))
                {
                    fb.SetValue(_ourFreshFlares, null);
                    RttLog.Line("!!! Whole-scene flares: our FlaresContext was about to be torn down still " +
                                "holding the ENGINE'S _flaresBuffer, with no captured originals to restore. " +
                                "Unhooked it. Disposing it in that state frees the player's flare buffer and " +
                                "NREs their next frame in FlaresContext.GetFlareConstants — the exit-to-menu " +
                                "CTD. If this line appears, find out how the context outlived its originals.");
                }
            }
            catch (Exception e) { RttLog.Error("flare buffer alias check", e); }
        }

        if (_ourFreshFlares == null || _flareOriginals == null) { _flareOriginals = null; return; }
        try
        {
            var ft = _ourFreshFlares.GetType();
            for (int i = 0; i < FlareDefFields.Length; i++)
            {
                var f = ft.GetField(FlareDefFields[i], Any);
                f?.SetValue(_ourFreshFlares, _flareOriginals[i]);
            }
            RttLog.Line("Whole-scene flares: mirrored ENGINE references scrubbed from our context " +
                        "(ctor originals restored) before its dispose — the teardown can no longer " +
                        "free the player's flare buffer or drain its definition allocator.");
        }
        catch (Exception e) { RttLog.Error("scrub mirrored flare refs", e); }
        finally { _flareOriginals = null; }
    }

    private static int MirrorFlareDefinitions()
    {
        _flaresReady = false;
        if (_engineFlares == null || _ourFreshFlares == null) return 0;
        int copied = 0;
        var missing = new List<string>();
        try
        {
            var ft = _ourFreshFlares.GetType();

            // Capture the ctor originals ONCE, before anything is overwritten. Not per
            // render — after the first mirror these fields hold the engine's objects, and
            // capturing those as "originals" would defeat the entire scrub.
            if (_flareOriginals == null)
            {
                _flareOriginals = new object[FlareDefFields.Length];
                for (int i = 0; i < FlareDefFields.Length; i++)
                    _flareOriginals[i] = ft.GetField(FlareDefFields[i], Any)?.GetValue(_ourFreshFlares);
            }

            foreach (var name in FlareDefFields)
            {
                var f = ft.GetField(name, Any);
                if (f == null) { missing.Add(name); continue; }
                f.SetValue(_ourFreshFlares, f.GetValue(_engineFlares));
                copied++;
            }

            // The one field the flare pass CANNOT tolerate as null: GetFlareConstants does
            // `_flaresBuffer.GPUBufferId` unguarded. Note it is legitimately null on the
            // ENGINE'S context too until the first AddFlare/UpdateFlare, so this is a real
            // runtime state and not only a mirror failure — which is exactly why the check
            // belongs here, on every render, rather than once at build time.
            var bufField = ft.GetField("_flaresBuffer", Any);
            _flaresReady = bufField != null && bufField.GetValue(_ourFreshFlares) != null;
        }
        catch (Exception e)
        {
            _flaresReady = false;
            if (!_flareMirrorLogged) { _flareMirrorLogged = true; RttLog.Error("mirror flare definitions", e); }
            return copied;
        }

        // Name the missing member rather than failing silently — a renamed private field is
        // the likeliest way this breaks on a game update, and "no flares in the feed" with
        // no explanation is the worst possible symptom.
        if (missing.Count > 0 && !_flareMirrorLogged)
        {
            _flareMirrorLogged = true;
            RttLog.Line("Whole-scene flares: could not find FlaresContext member(s) [" +
                        string.Join(", ", missing) + "] — the feed's flare definitions will be " +
                        "incomplete. Field names likely changed; re-check with tools/EngineQuery.");
        }
        return copied;
    }

    // ---- OWN THE RAYTRACING SCENE (the TLAS) -------------------------------------------
    //
    // THE LAST PLAYER-ANCHORED INPUT TO THE FEED'S AMBIENT, by elimination rather than by
    // guess. Measured 2026-08-05: the probe cubes are ours (AMBIENT PROBE reports CloseIBL and
    // FarIBL as our own captures, not the CommonResources.SkyboxIBL fallback), and RTGIContext
    // with its ReSTIR reservoirs was never shared — it hangs off our own DrawContextManager.
    // That leaves AmbientLightJob's other two inputs, giBufferDiffuse and giBufferSpecular,
    // which come from RaytraceGIJob tracing against CoreSystems.RayTracingScene — a TLAS built
    // by stage 0, which we skip, so it is selected for the PLAYER's position.
    //
    // WHY OWNERSHIP RATHER THAN SUPPRESSION, one more time: the feed CONSUMES this. Skipping
    // stage 0 is what we already do and it is precisely why the feed's rays trace someone
    // else's world. Only a second manager fixes what is consumed.
    //
    // THE ORDERING RULE, WRITTEN IN RATHER THAN REDISCOVERED. Install AFTER InstallCamera. The
    // probe manager taught this the expensive way: PrepareProbes BAKES RenderView.CameraPosition
    // into each request at queue time, so running it before the camera swap stamped the player's
    // position into all six cube faces and the feed reflected the player's planet. Anything that
    // resolves a position from the global RenderView has to run inside the swap, and a TLAS
    // build is exactly that kind of thing. Where work RUNS is not where its inputs are RESOLVED.
    //
    // COUPLED TO STAGE 0 in ShouldSkipStage, so an owned-but-never-built TLAS is impossible.
    // PER FEED — a shared static here meant one feed's failure poisoned the feature for
    // every feed (feed 1's stage-0 NRE would have read as "unavailable" on feed 0 too).
    private static int _rtSceneState               // 0 untried, 1 armed, -1 unavailable
    { get => Feeds.Cur.RtSceneState; set => Feeds.Cur.RtSceneState = value; }
    private static FieldInfo _rtSceneField;        // CoreSystems.RayTracingScene
    private static bool _rtSceneInstalled;         // true only between install and restore
    private static bool _rtSceneLogged;

    private static object _ourRtScene
    { get => Feeds.Cur.OurRtScene; set => Feeds.Cur.OurRtScene = value; }

    private static object[] ParkedRtSceneSlot()
    {
        try
        {
            var bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
            return bridge?.GetField("ParkedRayTracingScenes")?.GetValue(null) as object[];
        }
        catch { return null; }
    }

    private static object InstallRayTracingScene()
    {
        _rtSceneInstalled = false;
        if (_rtSceneState < 0 || !FeedConfig.WholeSceneOwnRayTracingScene) return null;
        try
        {
            if (_rtSceneState == 0)
            {
                _coreType ??= Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
                _rtSceneField = _coreType?.GetField("RayTracingScene", BindingFlags.Public | BindingFlags.Static);
                if (_rtSceneField == null)
                {
                    _rtSceneState = -1;
                    RttLog.Line("Own RT scene: CoreSystems.RayTracingScene not found — feature unavailable, " +
                                "the feed keeps tracing the player's TLAS.");
                    return null;
                }

                // THE PARK IS A HARD REQUIREMENT. RayTracingSceneManager owns _tlasBuffers,
                // InstanceDataBuffer, GeometryDataBuffer, ResultBuffer and eight entity pools.
                // Without the park every hot reload strands that set unreachable — the VRAM
                // ratchet (~1.1 GB/reload measured) and the descriptor exhaustion that CTD'd
                // own-probes. Refusing to arm is the correct failure, not a fallback.
                var park = ParkedRtSceneSlot();
                if (park == null)
                {
                    _rtSceneState = -1;
                    RttLog.Line("Own RT scene: this bootstrap has NO ParkedRayTracingScenes array — RESTART " +
                                "THE GAME to adopt the new bootstrap. Refusing to arm rather than leak a TLAS " +
                                "and its buffers once per hot reload.");
                    return null;
                }

                int slot = Feeds.Cur.Id;
                if (slot < 0 || slot >= park.Length) { _rtSceneState = -1; return null; }

                if (_ourRtScene == null)
                {
                    // Type-check the adoption: the park is untyped object[] because the
                    // bootstrap must not touch engine types, so a slot filled by an older or
                    // incompatible build would otherwise be cast blindly at first use.
                    var parked = park[slot];
                    if (parked != null && _rtSceneField.FieldType.IsInstanceOfType(parked))
                    {
                        _ourRtScene = parked;
                        // A parked manager was already built by the session that parked it,
                        // but a hot reload cannot prove that — one rebuild is cheap, a
                        // never-built TLAS is the silent stage-2 failure. Build once again.
                        Feeds.Cur.OwnTlasBuilt = false;
                        RttLog.Line($"Own RT scene: ADOPTED the parked RayTracingSceneManager for feed {slot} — " +
                                    "its TLAS buffers and entity pools survive the hot reload instead of leaking.");
                    }
                }

                if (_ourRtScene == null)
                {
                    // Parameterless and non-public, same as EnvironmentProbeManager. The GPU
                    // buffers are built lazily by the first CreateTLAS, which happens inside
                    // stage 0 — i.e. inside our settle window, on the render thread.
                    try { _ourRtScene = Activator.CreateInstance(_rtSceneField.FieldType, nonPublic: true); Feeds.Cur.OwnTlasBuilt = false; }
                    catch (Exception e)
                    {
                        _rtSceneState = -1;
                        RttLog.Error("own RT scene construct", e);
                        RttLog.Line("Own RT scene: could not construct a RayTracingSceneManager — feature unavailable.");
                        return null;
                    }
                    if (_ourRtScene == null) { _rtSceneState = -1; return null; }
                    park[slot] = _ourRtScene;          // park BEFORE first use
                    RttLog.Line($"Own RT scene: built a RayTracingSceneManager for feed {slot} and parked it. " +
                                "Its TLAS is built by stage 0 inside our pass, around the FEED camera.");
                }

                _rtSceneState = 1;
            }

            var saved = _rtSceneField.GetValue(null);
            if (ReferenceEquals(saved, _ourRtScene))
            {
                // Already ours — do not return a "saved" value that would restore ours as the
                // player's. This is the re-entrancy guard the flares swap needed too.
                _rtSceneInstalled = true;
                return null;
            }

            _rtSceneField.SetValue(null, _ourRtScene);
            _rtSceneInstalled = true;

            if (!_rtSceneLogged)
            {
                _rtSceneLogged = true;
                RttLog.Line("Own RT scene: OUR RayTracingSceneManager installed for our render. Stage 0 is " +
                            "force-run while it is installed (see ShouldSkipStage), so the feed's rays trace " +
                            "an acceleration structure built around the FEED camera instead of the player's. " +
                            "Installed AFTER InstallCamera on purpose — a TLAS build resolves its position " +
                            "from the global RenderView, and getting that order wrong is what made the probe " +
                            "cubes capture at the player.");
            }
            return saved;
        }
        catch (Exception e)
        {
            RttLog.Error("own RT scene install", e);
            _rtSceneInstalled = false;
            return null;
        }
    }

    private static void RestoreRayTracingScene(object saved)
    {
        _rtSceneInstalled = false;
        if (saved == null || _rtSceneField == null) return;
        try { _rtSceneField.SetValue(null, saved); }
        catch (Exception e) { RttLog.Error("own RT scene restore", e); }
    }

    // ---- OUR OWN IRRADIANCE CACHE (2026-08-06) -----------------------------------------
    //
    // THE BUG THIS FIXES POINTS AT THE PLAYER, NOT THE FEED. CoreSystems.IRCacheResources is
    // a WORLD-SPACE hash grid keyed by cell. Stage 30 (RaytracingPrepare -> IRCacheTraceJob)
    // populates it from whatever camera the pass is running, and we un-skipped stage 30 so
    // the FEED's ambient would finally be computed at the feed camera. But we never owned the
    // cache, so our entries went into the SHARED grid and the player's frame read them back.
    //
    // The reason it hid for two days: cells 4000 km apart never collide, so with the feed in
    // orbit the contamination was invisible. Fly the camera near the player and the two views
    // share cells. Confirmed 2026-08-06 with a clean A/B — identical player position, aim and
    // time of day, moving ONLY the feed camera's aim visibly relit the player's cockpit.
    //
    // Our own config note predicted this outcome word for word when stage 30 was un-skipped
    // ("EXPECTED: feed ambient becomes correct AND the player's world ambient goes wrong").
    // The prediction was recorded and then not acted on, which is the actual lesson.
    //
    // SAME ORDERING CONSTRAINT AS THE PROBES, and it is the whole reason this is a separate
    // install rather than a settings scope: the trace job resolves its sampling position from
    // the global RenderView, so this must be installed INSIDE the camera swap. Where the work
    // RUNS and where its inputs were RESOLVED are different questions — the trap that cost a
    // restart on the probe side.
    private static int _irCacheState                 // 0 untried, 1 armed, -1 unavailable
    { get => Feeds.Cur.IrCacheState; set => Feeds.Cur.IrCacheState = value; }
    private static FieldInfo _irCacheField;          // CoreSystems.IRCacheResources
    private static bool _irCacheInstalled;           // true only between install and restore

    private static object _ourIrCache
    { get => Feeds.Cur.OurIrCache; set => Feeds.Cur.OurIrCache = value; }

    private static object[] ParkedIrCacheSlot()
    {
        try
        {
            var bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
            return bridge?.GetField("ParkedIRCaches")?.GetValue(null) as object[];
        }
        catch { return null; }
    }

    private static object InstallIRCache()
    {
        _irCacheInstalled = false;
        if (_irCacheState < 0 || !FeedConfig.WholeSceneOwnIRCache) return null;
        try
        {
            if (_irCacheState == 0)
            {
                _coreType ??= Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
                _irCacheField = _coreType?.GetField("IRCacheResources", BindingFlags.Public | BindingFlags.Static);
                if (_irCacheField == null)
                {
                    _irCacheState = -1;
                    RttLog.Line("Own IR cache: CoreSystems.IRCacheResources not found — feature unavailable. " +
                                "The feed keeps writing the SHARED irradiance grid, which contaminates the " +
                                "PLAYER's ambient whenever the two cameras occupy the same cells.");
                    return null;
                }

                // THE PARK IS A HARD REQUIREMENT, same as the RT scene: this manager owns ~14
                // RWBuffers plus a lazily built container, and a logic-owned instance strands
                // the lot on every hot reload. Refusing to arm is the correct failure.
                var park = ParkedIrCacheSlot();
                if (park == null)
                {
                    _irCacheState = -1;
                    RttLog.Line("Own IR cache: this bootstrap has NO ParkedIRCaches array — RESTART THE GAME " +
                                "to adopt the new bootstrap. Refusing to arm rather than leak the cache's " +
                                "buffer set once per hot reload. Until then the player's ambient stays " +
                                "contaminated by the feed whenever the cameras share grid cells.");
                    return null;
                }

                int slot = Feeds.Cur.Id;
                if (slot < 0 || slot >= park.Length) { _irCacheState = -1; return null; }

                if (_ourIrCache == null)
                {
                    var parked = park[slot];
                    if (parked != null && _irCacheField.FieldType.IsInstanceOfType(parked))
                    {
                        _ourIrCache = parked;
                        RttLog.Line($"Own IR cache: ADOPTED the parked IRCacheResourcesManager for feed {slot} — " +
                                    "its buffer set survives the hot reload instead of leaking.");
                    }
                }

                if (_ourIrCache == null)
                {
                    // Parameterless and non-public, same as EnvironmentProbeManager and the RT
                    // scene. The GPU buffers arrive later via CreateAndInitializeContainer,
                    // which stage 30 reaches — i.e. inside our pass, on the render thread,
                    // within the settle window.
                    try { _ourIrCache = Activator.CreateInstance(_irCacheField.FieldType, nonPublic: true); }
                    catch (Exception e)
                    {
                        _irCacheState = -1;
                        RttLog.Error("own IR cache construct", e);
                        RttLog.Line("Own IR cache: could not construct an IRCacheResourcesManager — feature " +
                                    "unavailable, and the player's ambient stays contaminated.");
                        return null;
                    }
                    if (_ourIrCache == null) { _irCacheState = -1; return null; }
                    park[slot] = _ourIrCache;          // park BEFORE first use
                    RttLog.Line($"Own IR cache: built an IRCacheResourcesManager for feed {slot} and parked it. " +
                                "Stage 30 now populates OUR grid at the feed camera; the player's grid is " +
                                "untouched, which is the fix for feed-camera aim relighting the player's world.");
                }

                _irCacheState = 1;
            }

            // NEVER INSTALL AN UNINITIALISED CONTAINER. This is the crash of 2026-08-06
            // 21:46 — "Assertion Failure: '_container' was null" inside IRCachePrepareJob
            // .DoWork, on world load, from our own RunSecondRender.
            //
            // The probe manager and the TLAS both build their GPU state lazily from inside
            // our pass, so owning them needed nothing extra; I assumed this one was the same
            // shape. It is not. CreateAndInitializeContainer is called ONE LEVEL ABOVE us —
            // Render12EngineComponent.DrawInternal, the caller of SceneDrawSystem.Draw — so a
            // manager we swap in during a nested Draw is never offered it, and stage 30
            // asserts on the null container instead of skipping.
            //
            // The bootstrap prefix on IRCachePrepareJob.DoWork initialises ours on demand
            // (that job receives the ComputeCommandList we otherwise have no route to). This
            // guard is the belt to that braces: with an older bootstrap, or if the prefix
            // ever fails to apply, we decline to install rather than assert on the render
            // thread. Failing inert costs the fix; failing installed costs the session.
            if (!ContainerReady(_ourIrCache))
            {
                if (!_irCacheWaitLogged)
                {
                    _irCacheWaitLogged = true;
                    RttLog.Line("Own IR cache: our container is not initialised yet — NOT installing this " +
                                "pass. CreateAndInitializeContainer runs in Render12EngineComponent.DrawInternal, " +
                                "one level above our nested Draw, so ours is initialised by the bootstrap prefix " +
                                "on IRCachePrepareJob.DoWork instead. If this line repeats forever the prefix is " +
                                "missing — RESTART to adopt the new bootstrap, or set wholeSceneOwnIRCache = 0.");
                }
                return null;
            }

            var saved = _irCacheField.GetValue(null);
            if (ReferenceEquals(saved, _ourIrCache))
            {
                // Already ours — returning it as "saved" would restore ours as the engine's on
                // unwind, which is the re-entrancy trap the RT scene install documents.
                _irCacheInstalled = true;
                return null;
            }

            _irCacheField.SetValue(null, _ourIrCache);
            _irCacheInstalled = true;

            if (!_irCacheLogged)
            {
                _irCacheLogged = true;
                RttLog.Line("Own IR cache: INSTALLED for our pass. Stage 30's IRCacheTraceJob writes entries " +
                            "keyed by world cell — ours now land in our own grid, so a feed near the player " +
                            "can no longer relight the player's world. Restored on unwind before anything else.");
            }
            return saved;
        }
        catch (Exception e)
        {
            _irCacheState = -1;
            RttLog.Error("own IR cache install", e);
            return null;
        }
    }

    private static void RestoreIRCache(object saved)
    {
        _irCacheInstalled = false;
        if (saved == null || _irCacheField == null) return;
        try { _irCacheField.SetValue(null, saved); }
        catch (Exception e) { RttLog.Error("own IR cache restore", e); }
    }

    private static bool _irCacheLogged, _irCacheWaitLogged;

    // The ThreadStatic RT-off bracket around our nested Draw — see the call site. Field
    // resolved once; a bootstrap without it (pre-2026-08-07) leaves the feature inert and
    // says so once, rather than throwing on a hot path.
    private static FieldInfo _fNestedRtOff, _fAmbientFloor, _fFloorState;
    private static PropertyInfo _piLastAmbient;
    private static bool _nestedRtOffTried, _nestedRtOffMissingLogged, _floorStateLogged, _llaModeLogged, _nightDimLogged;
    private static bool SetNestedRtOff(bool value)
    {
        if (!FeedConfig.WholeSceneIblOnlyAmbient) return false;
        if (!_nestedRtOffTried)
        {
            _nestedRtOffTried = true;
            var bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
            _fNestedRtOff = bridge?.GetField("NestedRenderRtOff", BindingFlags.Public | BindingFlags.Static);
            _fAmbientFloor = bridge?.GetField("FeedAmbientFloor", BindingFlags.Public | BindingFlags.Static);
        }
        // Pushed on the opening bracket of every pass, so feedAmbientFloor edits apply within
        // a second while the game runs — the tuning loop for a knob whose right value is
        // found by looking at a panel, not by arithmetic.
        if (value && _fAmbientFloor != null)
        {
            try
            {
                // THE FLOOR IS THE ENGINE'S OWN AMBIENT SCALAR, NOT A CONSTANT (2026-08-07,
                // user: "the ambient brightness remains exactly the same at night", and they
                // asked for a shipped system rather than a hand-rolled one). OUR
                // EnvironmentProbeManager — the engine's stock probe system, running at the
                // FEED camera — computes LastLocalLightAmbient inside PrepareProbes every
                // pass: the engine's own day/night ambient intensity at our position. The
                // fully-stock APPLICATION path (RT-off ambient arithmetic) is closed by the
                // snapshot law, so the division of labour is: the ENGINE computes the value,
                // we only route it into the buffer the RT-on shader reads. One pass stale,
                // which is 33 ms of dusk.
                //
                // feedAmbientFloor becomes a TRIM on that scalar rather than an absolute:
                // effective = LastLocalLightAmbient x (feedAmbientFloor / 0.05), normalised
                // so the value tuned by eye in daylight (0.07 on 2026-08-07) keeps meaning
                // roughly the same brightness if daytime LLA is ~0.05. Falls back to the
                // plain constant when the scalar is unreadable — a worse look, never a break.
                float floor = (float)FeedConfig.FeedAmbientFloor;
                var mgr = Feeds.Cur.OurProbes;
                if (mgr != null)
                {
                    _piLastAmbient ??= mgr.GetType().GetProperty("LastLocalLightAmbient",
                                           BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    // STRICTLY POSITIVE: LastLocalLightAmbient turned out to be the ambient
                    // from LOCAL LIGHTS, not the sun — zero in open wilderness, where the
                    // old >= 0 check accepted it and multiplied the floor to nothing
                    // ("much too dark in the day", 2026-08-07). A zero scalar carries no
                    // information; the constant floor is the honest fallback. Day/night
                    // dimming needs a real sun-elevation signal — a separate task.
                    if (_piLastAmbient?.GetValue(mgr) is float lla && !float.IsNaN(lla) && lla > 0f)
                    {
                        floor = lla * (floor / 0.05f);
                        if (!_llaModeLogged)
                        {
                            _llaModeLogged = true;
                            RttLog.Line($"AMBIENT FLOOR: driven by the engine's LastLocalLightAmbient at the " +
                                        $"feed camera ({lla:F4} this pass) x trim — day/night now follows the " +
                                        "engine's own curve. feedAmbientFloor is the TRIM (daylight brightness); " +
                                        "night dims with the sun by construction.");
                        }
                    }
                }

                // #60 NIGHT DIMMING: scale the floor by the sun's elevation — the same
                // signal auto-aperture uses (planet-env light direction · planet-radial up,
                // twilight-smoothstepped). In open wilderness LLA is 0 and the branch above
                // leaves the CONSTANT floor, which is why night read as bright as day. A
                // multiplicative factor keeps both paths monotone; the floor value pushed
                // is per-pass BUFFER CONTENT, so the snapshot law is untouched.
                if (FeedConfig.FeedAmbientFloorNightMult >= 0 && TryDayFactor(out double dayT))
                {
                    floor *= (float)(FeedConfig.FeedAmbientFloorNightMult
                                     + (1.0 - FeedConfig.FeedAmbientFloorNightMult) * dayT);
                    if (!_nightDimLogged)
                    {
                        _nightDimLogged = true;
                        RttLog.Line($"AMBIENT FLOOR: night dimming armed — dayFactor={dayT:F3}, floor " +
                                    $"scaled to {floor:F4} (night keeps {FeedConfig.FeedAmbientFloorNightMult:P0} " +
                                    "of the day floor; feedAmbientFloorNightMult tunes it, -1 disarms).");
                    }
                }
                _fAmbientFloor.SetValue(null, floor);
            }
            catch { }
        }

        // The floor-colour verdict, reported FROM THE LOGIC SIDE: render-thread bootstrap
        // logs proved unreliable this session, so the prefix writes a tri-state field and
        // this loop — which demonstrably logs — announces it once. -1 here is the "black at
        // every knob value" state that burned the afternoon.
        if (value && !_floorStateLogged)
        {
            try
            {
                _fFloorState ??= Type.GetType("RttProbe.RttBridge, RttProbe")
                                     ?.GetField("FloorColorState", BindingFlags.Public | BindingFlags.Static);
                if (_fFloorState?.GetValue(null) is int fs && fs != 0)
                {
                    _floorStateLogged = true;
                    RttLog.Line(fs > 0
                        ? "AMBIENT FLOOR COLOUR: BUILT — the diffuse GI clear carries feedAmbientFloor; the " +
                          "knob is live and tunable."
                        : "AMBIENT FLOOR COLOUR: NO USABLE CONSTRUCTOR on the clear-value type — the floor " +
                          "is BLACK regardless of the knob. The colour type needs a different construction.");
                }
            }
            catch { }
        }
        if (_fNestedRtOff == null)
        {
            if (!_nestedRtOffMissingLogged)
            {
                _nestedRtOffMissingLogged = true;
                RttLog.Line("IBL-only ambient: this bootstrap has no NestedRenderRtOff flag — RESTART THE " +
                            "GAME to adopt it. Until then the feed's ambient keeps the sky-miss GI term " +
                            "(the cave floodlight).");
            }
            return false;
        }
        try
        {
            _fNestedRtOff.SetValue(null, value);

            // READ-BACK VERIFY, once. The ThreadStatic episode: 4,100 renders with this
            // SetValue "succeeding" while the bootstrap's readers never saw true, because
            // reflection writes to a [ThreadStatic] field do not reliably land in the
            // reader's slot. A verified write is one property read on the first pass; an
            // unverified one was an hour of dead instrumentation.
            if (value && !_bracketVerified)
            {
                _bracketVerified = true;
                bool seen = _fNestedRtOff.GetValue(null) is bool b && b;
                RttLog.Line(seen
                    ? "IBL-only ambient: pass bracket VERIFIED — the bootstrap's job prefixes will see it."
                    : "IBL-only ambient: pass bracket WRITE DID NOT READ BACK — the prefixes will never " +
                      "fire and the feed keeps whatever ambient the real trace produces. Field shape " +
                      "mismatch between logic and bootstrap; restart with matching builds.");
            }
            return true;
        }
        catch { return false; }
    }
    private static bool _bracketVerified;

    // ContainerIsInitialized is the manager's own answer to "is _container non-null", which
    // is exactly what IRCachePrepareJob asserts on. Read reflectively and defaulted to FALSE
    // on any failure: a reader that cannot see the flag must not be read as permission.
    private static PropertyInfo _piContainerReady;
    private static bool ContainerReady(object mgr)
    {
        if (mgr == null) return false;
        try
        {
            _piContainerReady ??= mgr.GetType().GetProperty("ContainerIsInitialized",
                                      BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return _piContainerReady?.GetValue(mgr) is bool b && b;
        }
        catch { return false; }
    }

    // ---- OWN ENVIRONMENT PROBES (goal 4.4) ---------------------------------------------
    //
    // The atlas is eight cube textures held per-MANAGER, and the manager is the global
    // CoreSystems.EnvironmentProbeManager. So probes cannot be rendered from the orbit camera
    // without either corrupting the player's atlas or owning a manager. This owns one.
    //
    // Construction is genuinely free: EnvironmentProbeManager..ctor() is parameterless and
    // calls only Object..ctor(). The textures appear later, in RecreateProbes, reached from
    // PrepareProbes — which is also what trips _forceReprocess, so the first PrepareProbes
    // must land inside the 30-frame settle window. It does: our first render after a rebuild
    // is gated by _settleFrames.
    //
    // Filling the queue ourselves is the other half. DrawContexts.EnvProbesToUpdate is
    // written only by DrawContextManager.OnBeginDraw, which our nested Draw never reaches, so
    // our queue is permanently empty unless we assign it. PrepareProbes() returns
    // Buffer<Request> and EnvProbesToUpdate is a field of that type, so this is one
    // assignment — and calling it on OUR instance is not Rule 8, which is about globals.
    // PER-FEED (phase C1a): probes are centred on the CAMERA, which is the whole reason
    // goal 4.4 exists — the player's atlas is right for where the player stands, not
    // where this feed looks. Two feeds at two places need two atlases, so this is
    // per-feed by definition rather than by convenience. NOT disposed on a config
    // change; three device removals settled that (see Reset).
    private static object _ourProbes
    { get => Feeds.Cur.OurProbes; set => Feeds.Cur.OurProbes = value; }

    // THE DEFERRED-DISPOSE QUEUE IS GONE — deleted 2026-07-30 during the C1 static
    // inventory, and worth recording rather than silently dropping.
    //
    // It was attempt 2 of three: Reset() queued the retired probe manager here and an
    // LCD-tick drain (DisposePendingProbes) freed its cube textures from the game thread.
    // Attempt 3 crashed identically, the manager became KEPT rather than retired, and
    // nothing has written this slot since — so the drain ran every tick, found null every
    // time, and did nothing.
    //
    // Dead code that describes a live safety mechanism is worse than no code: it says the
    // textures ARE being reclaimed off the render thread, which is the opposite of the
    // rule that actually holds (see Reset). Rule 26 — a mechanism is only real if it has
    // been observed firing — applies to teardown paths as much as to fixes.

    private static FieldInfo _probeField, _envProbesToUpdateField;
    private static MethodInfo _miPrepareProbes;

    // The bootstrap's parked probe managers, by reflection — the logic assembly must not
    // reference the bootstrap at compile time (that would pin the collectible context; see
    // LogicEntry, which reaches RttBridge the same way). Null on an older bootstrap, in
    // which case parking silently degrades to the old per-load behaviour: one leaked set
    // per reload, exactly as before, rather than a crash. The one-shot log names which
    // world we are in, because a silently absent park is how the leak would come back
    // without anyone noticing.
    private static object[] _parkedSlots;
    private static bool _parkResolved;

    private static object[] ParkedProbeSlot()
    {
        if (!_parkResolved)
        {
            _parkResolved = true;
            _parkedSlots = Type.GetType("RttProbe.RttBridge, RttProbe")
                ?.GetField("ParkedProbeManagers", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as object[];
            RttLog.Global(_parkedSlots != null
                ? "Own probes: bootstrap park found — probe managers survive hot reloads from here on."
                : "Own probes: NO bootstrap park (bootstrap predates it). Managers leak one RTV set per " +
                  "hot reload until the game is restarted onto the new bootstrap — keep wholeSceneOwnProbes " +
                  "off until then.");
        }
        return _parkedSlots;
    }
    private static int _probeState         // 0 = untried, 1 = ready, -1 = unavailable
    { get => Feeds.Cur.ProbeState; set => Feeds.Cur.ProbeState = value; }
    private static bool _probeLogged
    { get => Feeds.Cur.ProbeLogged; set => Feeds.Cur.ProbeLogged = value; }

    // Install our manager and fill our queue. Returns the manager that was there before, or
    // null if nothing was swapped — the caller restores it in the finally either way.
    //
    // Fails SOFT and permanently on the first problem: a half-installed probe manager is
    // worse than none, and this runs on the render thread with the player's frame in flight.
    // ---- PER-FEED EYE ADAPTATION -------------------------------------------------------
    //
    // Swap SceneDrawSystem._eyeAdaptationJob for OUR instance around our render, so
    // ComputeExposure -> DynamicExposure integrates our own histogram and history instead of
    // the player's. Returns the saved instance for the unwind, or null if nothing was swapped.
    //
    // THE ASYNC CONSTRUCTION IS THE ONLY AWKWARD PART, and it is why this is not simply the
    // probe pattern copied. EyeAdaptationJob's ctor takes a List of initialization tasks and
    // its real setup happens in InitializeAsync — PSOs, root signatures, the histogram buffer.
    // A job used before that completes has null PSOs, and dispatching against those is a
    // device removal, not an exception. So construction KICKS OFF and this returns null until
    // the task reports completion; the feed keeps its fixed stop for the second or two that
    // takes. Blocking the render thread on the task instead would be a frame hitch at best
    // and a deadlock at worst, since the init tasks may want the render thread themselves.
    private static int _eyeState;                 // 0 untried, 1 armed, -1 unavailable
    private static object _ourEyeJob;             // OUR job — a HISTORY CONTAINER, never dispatched
    private static FieldInfo _eyeJobField;        // SceneDrawSystem._eyeAdaptationJob
    private static FieldInfo _fAutoExposures;     // EyeAdaptationJob._autoExposures
    private static FieldInfo _fAutoExpInit;       // EyeAdaptationJob._areAutoExposuresInitialized
    private static object _ourAutoExposures;      // our ping-pong history pair
    private static bool _ourAutoExpInit;          // our copy of the first-use flag

    // Returns the ENGINE's history to put back, boxed as a small pair, or null if nothing was
    // swapped. We swap STATE onto the engine's job rather than swapping the job itself — see
    // the header above for why owning a job cannot work.
    private sealed class SavedEyeState { public object AutoExposures; public bool Init; }

    private static object InstallEyeAdaptation(object sceneDrawSystem)
    {
        if (_eyeState < 0 || !FeedConfig.WholeSceneOwnEyeAdaptation || sceneDrawSystem == null) return null;
        try
        {
            if (_eyeState == 0)
            {
                _eyeJobField = sceneDrawSystem.GetType().GetField("_eyeAdaptationJob", Any);
                if (_eyeJobField == null)
                {
                    _eyeState = -1;
                    RttLog.Line("Own eye adaptation: SceneDrawSystem._eyeAdaptationJob not found — " +
                                "feature unavailable, the feed keeps its fixed stop.");
                    return null;
                }

                // THE PARK IS A HARD REQUIREMENT, not a nicety. Without it every hot reload
                // builds a fresh job and orphans the previous one's render target views, and
                // RTV descriptors come from a small fixed pool — own-probes died exactly that
                // way with VRAM flat. Refusing to arm is the correct failure.
                var bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
                var park = bridge?.GetField("ParkedEyeAdaptation")?.GetValue(null) as object[];
                if (park == null)
                {
                    _eyeState = -1;
                    RttLog.Line("Own eye adaptation: this bootstrap has NO ParkedEyeAdaptation array — RESTART " +
                                "THE GAME to adopt the new bootstrap. Refusing to arm: without the park each hot " +
                                "reload would orphan a job and leak its RTV descriptors, which is the crash " +
                                "own-probes already paid for (Out of the descriptor heap at BorrowRTV).");
                    return null;
                }

                int slot = Feeds.Cur.Id;
                if (slot < 0 || slot >= park.Length) { _eyeState = -1; return null; }

                // TYPE-CHECK THE ADOPTION. The park is untyped object[] because the bootstrap
                // must not touch engine types, so a slot filled by an older or incompatible
                // build would otherwise be trusted and cast blindly at first use.
                var parked = park[slot];
                _ourEyeJob = _eyeJobField.FieldType.IsInstanceOfType(parked) ? parked : null;
                if (_ourEyeJob != null)
                {
                    // ADOPTION MUST VERIFY, NOT ASSUME — and this nearly reintroduced the exact
                    // crash the park exists to prevent.
                    //
                    // The first version set _eyeReady = true here on the reasoning that a
                    // parked job "has already been initialized". That is false for the case
                    // that actually happened: a job whose InitializeAsync FAULTED is still
                    // parked (deliberately, so a reload does not build a second one beside
                    // it), and adopting it would mark it ready, install it, and dispatch
                    // against null PSOs — a device removal, not an exception.
                    //
                    // EyeAdaptationJob carries the engine's own readiness flag, so ask it
                    // rather than inferring from "the slot was non-empty".
                    // THE OLD READINESS GUARD IS GONE, and deliberately so — its premise no
                    // longer holds. It refused to adopt a container whose
                    // _areAutoExposuresInitialized was false, because approach 1 DISPATCHED
                    // that job and dispatching an uninitialised one is a device removal.
                    //
                    // We no longer dispatch it. It is a bag of render targets: the only thing
                    // read out of it is _autoExposures, which the CONSTRUCTOR builds (verified
                    // in IL — the ctor is its only writer). A false flag here is not merely
                    // tolerable, it is CORRECT: it says this history has never had its
                    // first-use clear, which is exactly what we want DynamicExposure to do the
                    // first time it integrates into it.
                    //
                    // The non-null check on _autoExposures below is what replaces this guard,
                    // and it is the check that actually matches what we now use.
                    RttLog.Line($"Own eye adaptation: adopted the parked history container for feed {slot} — " +
                                "no new render targets built, which is the point of the park.");
                }
                else
                {
                    var jobType = _eyeJobField.FieldType;
                    var ctor = jobType.GetConstructors(Any).FirstOrDefault(c => c.GetParameters().Length == 1);
                    if (ctor == null)
                    {
                        _eyeState = -1;
                        RttLog.Line("Own eye adaptation: no single-argument EyeAdaptationJob ctor — unavailable.");
                        return null;
                    }

                    // The ctor wants a List<Task> to append its initialization work to. Build
                    // the exact generic list its parameter names, so we do not guess at Task.
                    var listType = ctor.GetParameters()[0].ParameterType;
                    var tasks = Activator.CreateInstance(listType);
                    _ourEyeJob = ctor.Invoke(new[] { tasks });
                    park[slot] = _ourEyeJob;              // park BEFORE any await can fail

                    // INITIALIZEASYNC IS DELIBERATELY NEVER CALLED. It is tied to the engine's
                    // render-init cancellation token, which is already signalled by the time we
                    // run, so it can only ever return a CANCELLED task (measured 2026-08-02:
                    // completed, not successful, no exception). We do not need it: the CTOR is
                    // the only writer of _autoExposures, so the history textures exist the
                    // moment the object does. What InitializeAsync builds is PSOs and root
                    // signatures — and this job is never dispatched, so it never needs them.
                    RttLog.Line($"Own eye adaptation: built a history container for feed {slot} and parked it. " +
                                "InitializeAsync is NOT called and is not needed — this object is never " +
                                "dispatched, it only holds the ping-pong textures its constructor made.");
                }
                // The two state fields we substitute. Resolved once from the job's own type.
                var jt = _eyeJobField.FieldType;
                _fAutoExposures = jt.GetField("_autoExposures", Any);
                _fAutoExpInit = jt.GetField("_areAutoExposuresInitialized", Any);
                if (_fAutoExposures == null || _fAutoExpInit == null)
                {
                    _eyeState = -1;
                    RttLog.Line("Own eye adaptation: EyeAdaptationJob has no _autoExposures / " +
                                "_areAutoExposuresInitialized — the state-swap route is unavailable on this build.");
                    return null;
                }

                // OUR history, taken from the container's constructor output. If this is null
                // the ctor did not build the pair and there is nothing to swap in — refuse
                // rather than install a null array under a shader.
                _ourAutoExposures = _fAutoExposures.GetValue(_ourEyeJob);
                if (_ourAutoExposures == null)
                {
                    _eyeState = -1;
                    RttLog.Line("Own eye adaptation: the container's _autoExposures is NULL — its constructor did " +
                                "not build the ping-pong pair, so there is no history to swap in. Disarmed.");
                    return null;
                }
                _ourAutoExpInit = false;   // our history has never been cleared; let DynamicExposure do it

                _eyeState = 1;
            }

            if (_ourEyeJob == null || _ourAutoExposures == null) return null;

            // THE SWAP: state, not the job.
            //
            // The engine's job keeps doing the dispatching — it has the PSOs and root
            // signatures that InitializeAsync builds, and that we can never obtain for a job
            // constructed mid-session. All we substitute is WHOSE HISTORY it integrates into.
            //
            // _areAutoExposuresInitialized travels WITH the history, because it is a property
            // of that buffer pair rather than of the job: DynamicExposure uses it to decide
            // whether this history needs its first-use clear. Leaving the engine's flag in
            // place while swapping our (never-cleared) textures under it would have the shader
            // integrate against uninitialised memory — a plausible-looking wrong exposure that
            // would have been very hard to attribute.
            //
            // _histogram is deliberately NOT swapped. It is per-frame scratch, refilled from
            // the source image on every call, not accumulated state — so sharing it costs
            // nothing and avoids owning a second buffer.
            var engineJob = _eyeJobField.GetValue(sceneDrawSystem);
            if (engineJob == null || ReferenceEquals(engineJob, _ourEyeJob)) return null;

            var saved = new SavedEyeState
            {
                AutoExposures = _fAutoExposures.GetValue(engineJob),
                Init          = (bool)(_fAutoExpInit.GetValue(engineJob) ?? false),
            };

            _fAutoExposures.SetValue(engineJob, _ourAutoExposures);
            _fAutoExpInit.SetValue(engineJob, _ourAutoExpInit);

            if (_scopeWarned.Add("eyeStateSwapped"))
                RttLog.Line("Own eye adaptation: SWAPPED the history onto the engine's job for our pass. " +
                            "The engine job dispatches (it owns the PSOs); the ping-pong history it integrates " +
                            "into is OURS, so the feed adapts to what its own camera sees and the player's " +
                            "adaptation is untouched. _histogram stays shared — it is per-frame scratch.");
            return saved;
        }
        catch (Exception e) { RttLog.Error("install eye adaptation", e); _eyeState = -1; return null; }
    }

    // Put the ENGINE's history back, and keep OURS for the next pass.
    //
    // Capturing our side on the way out is what makes this an adaptation rather than a
    // one-frame measurement: DynamicExposure ping-pongs the pair and sets the first-use flag,
    // so the values sitting on the job at the end of our render ARE our updated history. Read
    // them back before restoring, or every frame would start from scratch and the exposure
    // would never converge.
    private static void RestoreEyeAdaptation(object sceneDrawSystem, object saved)
    {
        if (saved is not SavedEyeState s || _fAutoExposures == null || sceneDrawSystem == null) return;
        try
        {
            var engineJob = _eyeJobField?.GetValue(sceneDrawSystem);
            if (engineJob == null) return;

            _ourAutoExposures = _fAutoExposures.GetValue(engineJob);
            _ourAutoExpInit = (bool)(_fAutoExpInit?.GetValue(engineJob) ?? false);

            _fAutoExposures.SetValue(engineJob, s.AutoExposures);
            _fAutoExpInit.SetValue(engineJob, s.Init);
        }
        catch (Exception e) { RttLog.Error("restore eye adaptation", e); }
    }

    private static object InstallProbes()
    {
        if (_probeState < 0 || !FeedConfig.WholeSceneOwnProbes) return null;
        try
        {
            if (_probeState == 0)
            {
                var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
                _probeField = core?.GetField("EnvironmentProbeManager", BindingFlags.Public | BindingFlags.Static);
                if (_probeField == null)
                {
                    _probeState = -1;
                    RttLog.Line("Own probes: CoreSystems.EnvironmentProbeManager not found — feature unavailable.");
                    return null;
                }

                var mgrType = _probeField.FieldType;
                _miPrepareProbes = mgrType.GetMethod("PrepareProbes", Any);
                if (_miPrepareProbes == null)
                {
                    _probeState = -1;
                    RttLog.Line("Own probes: EnvironmentProbeManager.PrepareProbes not found — feature unavailable.");
                    return null;
                }

                _envProbesToUpdateField = _ourDrawContexts?.GetType().GetField("EnvProbesToUpdate", Any);
                if (_envProbesToUpdateField == null)
                {
                    _probeState = -1;
                    RttLog.Line("Own probes: DrawContextManager.EnvProbesToUpdate not found — cannot hand our " +
                                "own queue to RenderEnvironmentProbe, so stage 2 would iterate nothing.");
                    return null;
                }

                // Parameterless ctor that allocates nothing. Deliberately created HERE rather
                // than in the DrawContextManager build: it costs nothing, so there is no
                // reason to widen that function's failure surface for it.
                //
                // REUSED across resets when one already exists. Reset() deliberately keeps
                // the manager (it cannot be disposed safely while the renderer is live), so
                // constructing unconditionally here would orphan the previous one — and its
                // eight cube textures with it — on every config change. That is Rule 10's
                // leak, arriving through the door opened to avoid a device removal.
                //
                // ...AND REUSED ACROSS HOT RELOADS, which the field alone cannot provide.
                // _ourProbes lives on FeedInstance, in the COLLECTIBLE logic assembly, so
                // "kept" was only true within one load: every reload started null, built a
                // fresh manager, and orphaned the previous one's 8 cube textures x 6 faces
                // of RTV descriptors — a small FIXED pool, exhausted after four reloads
                // (the 2026-07-30 18:46 "Out of the descriptor heap" CTD, the reason
                // wholeSceneOwnProbes has been disarmed since).
                //
                // The bootstrap's RttBridge.ParkedProbeManagers[] is the fix: loaded once,
                // never unloaded, holds one slot per feed. Adoption order — parked slot
                // first, this load's field second, construct only if both are empty — and
                // every construction writes the park, so the NEXT reload adopts instead of
                // building. The park is untyped object[] (the bootstrap must not touch
                // engine types); the type check on adoption guards against a slot parked by
                // an older, incompatible build.
                // Adoption only fills a NULL field — i.e. the first install after a hot
                // reload. Within one load the field survives gate cycles on FeedInstance,
                // and it is the same reference as the park (every construction writes it),
                // so adopting again would only produce a misleading once-per-cycle log.
                var park = ParkedProbeSlot();
                if (_ourProbes == null && park != null && Feeds.Cur.Id < park.Length)
                {
                    var parked = park[Feeds.Cur.Id];
                    if (parked != null && mgrType.IsInstanceOfType(parked))
                    {
                        _ourProbes = parked;
                        RttLog.Line($"Own probes: ADOPTED the parked EnvironmentProbeManager from the bootstrap " +
                                    $"(feed {Feeds.Cur.Id}) — its cube textures and RTV descriptors survive the " +
                                    "hot reload instead of leaking one set per reload.");
                    }
                }
                _ourProbes ??= Activator.CreateInstance(mgrType, nonPublic: true);
                if (_ourProbes == null)
                {
                    _probeState = -1;
                    RttLog.Line("Own probes: could not construct an EnvironmentProbeManager — feature unavailable.");
                    return null;
                }
                if (park != null && Feeds.Cur.Id < park.Length) park[Feeds.Cur.Id] = _ourProbes;
                _probeState = 1;
            }

            var saved = _probeField.GetValue(null);
            _probeField.SetValue(null, _ourProbes);

            // PrepareProbes advances OUR state machine and, on the first call, runs
            // RecreateProbes — the eight cube textures. Inside the settle window by
            // construction, because TryRender will not call us until it expires.
            var queue = _miPrepareProbes.Invoke(_ourProbes, null);
            _envProbesToUpdateField.SetValue(_ourDrawContexts, queue);

            if (!_probeLogged)
            {
                _probeLogged = true;
                RttLog.Line("Own probes: OUR EnvironmentProbeManager installed for our render and our " +
                            "EnvProbesToUpdate filled from its own PrepareProbes(). The feed's reflections " +
                            "and ambient bounce now come from probes rendered at the ORBIT camera instead " +
                            "of the player's atlas. CloseIBL/FarIBL fall back to CommonResources.SkyboxIBL " +
                            "until the first faces land, so early frames lose local bounce rather than " +
                            "binding null. Stage 2 (RenderEnvironmentProbe) must be OUT of " +
                            "wholeSceneSkipStages for any of this to reach the screen.");
            }
            return saved;
        }
        catch (Exception e)
        {
            _probeState = -1;
            RttLog.Error("install own probes (feature DISABLED for this session)", e);
            return null;
        }
    }

    private static FieldInfo _dcField;

    // STAGE 2: construct a second ScreenBuffers, and do nothing with it.
    //
    // Deliberately construct-only first. The last two times a context was introduced
    // mid-pipeline the failure was either silent or landed on the player rather than on
    // us; proving construction in isolation costs one launch and removes a whole class
    // of ambiguity from the next step.
    private static void EnsureScreenBuffers()
    {
        if (_sbBuilt || _ourScreenBuffers != null) return;
        _sbBuilt = true;                        // one attempt per load, success or not
        try
        {
            var sbType = Type.GetType("Keen.VRage.Render12.Core.Systems.ScreenBuffers, VRage.Render12");
            if (sbType == null)
            {
                RttLog.Line("Whole-scene: ScreenBuffers type not found.");
                return;
            }

            var ctor = sbType.GetConstructor(Type.EmptyTypes);
            if (ctor == null)
            {
                RttLog.Line("Whole-scene: ScreenBuffers has no parameterless constructor after all — " +
                            "the second-instance plan needs rethinking.");
                return;
            }

            // ADOPT BEFORE CONSTRUCTING — the ratchet fix. A ScreenBuffers owns depth, the
            // GBuffer array and the final LDR texture, all NON-EVICTABLE, and it hangs off
            // Feeds.Cur inside the collectible logic assembly. Every hot reload built a fresh
            // one and stranded the last beyond the reach of anything that could dispose it.
            // Measured cost of that (with DrawContexts): ~1.1 GB of KnownNonStreaming per
            // reload, which is what drove RealAvailableStreaming negative and set the
            // streaming pool thrashing.
            var adopted = AdoptParked("ParkedScreenBuffers", sbType);
            var sb = adopted ?? ctor.Invoke(null);

            // InitializeBuffers(in Vector2I maxResolution) is internal and is what the
            // engine's own instance is set up with. Update(cl, maxRes, preUpscaleRes) is
            // the public alternative but wants a command list we do not have here, so
            // the internal one is the right call at construction time.
            //
            // DO NOT RE-INITIALISE AN ADOPTED INSTANCE THAT IS ALREADY THE RIGHT SIZE.
            //
            // The first version of this fix called InitializeBuffers unconditionally, on the
            // assumption that sizing is idempotent. IT IS NOT. Read from the IL:
            //     CreateResizableDepthStencil(...)        <- allocates a NEW depth stencil
            //     newarr ResizableRWRenderTargetTexture   <- allocates a NEW GBuffer array
            //     set_GBuffer(...)
            // with no Dispose of what was there. So re-initialising an adopted instance
            // strands its ENTIRE previous buffer set — depth, GBuffer, LDR — and parking the
            // container achieves nothing. Measured: KnownNonStreaming still grew +1637 MB
            // across one reload WITH both parks confirmed adopted, which is what exposed it.
            //
            // READ THE ENGINE'S OWN GUARD FIELD, NOT PreUpscaleResolution.
            //
            // The first version of this check compared PreUpscaleResolution against the feed
            // size, and it did not work: the game log asserted
            //     '_usedMaxResolution == Vector2.Zero' evaluated to false
            //       at ScreenBuffers.InitializeBuffers
            //       at RttProbe.WholeSceneRender.EnsureScreenBuffers()
            // ONCE PER SESSION, in every session on 2026-08-02, i.e. the skip never fired.
            //
            // The reason is in this file already, at the RESOLUTION TRIPWIRE in
            // RunSecondRender: PreUpscaleResolution is REWRITTEN by ScreenBuffers.Update()
            // during a draw, and ours had been observed carrying the PLAYER'S resolution.
            // So it is not a record of what the instance was built at — it is per-frame
            // state, and comparing against it reports "wrong size" on an instance that is
            // perfectly sized, every time.
            //
            // _usedMaxResolution is the field InitializeBuffers itself asserts on and the
            // one it writes; nothing in the draw path touches it. Non-zero means "this
            // instance has already been initialised", which is exactly the question.
            var res = MakeVector2I(FeedConfig.WholeSceneWidth, FeedConfig.WholeSceneHeight);
            var init = sbType.GetMethod("InitializeBuffers", Any);
            string how;

            // Vector2I is a struct with int X/Y fields; boxed here, read by field.
            static bool NonZeroXY(object v)
            {
                if (v == null) return false;
                var t = v.GetType();
                var x = t.GetField("X", Any)?.GetValue(v);
                var y = t.GetField("Y", Any)?.GetValue(v);
                return (x is int ix && ix != 0) || (y is int iy && iy != 0);
            }

            // CHECK THE INSTANCE WE ARE ABOUT TO INITIALISE, WHATEVER ITS PROVENANCE.
            //
            // The first version of this guard only read _usedMaxResolution when the instance
            // came from the park, on the reasoning that a ctor.Invoke result must be virgin.
            // The game log says otherwise. From the initprobe, one gate cycle, ONE instance:
            //     [20:22:37.822] InitializeBuffers(3840, 2160) instance=#03855cc0
            //     [20:22:38.005] InitializeBuffers(1024, 1024) instance=#03855cc0   <- ours
            //     [20:22:38.008] Assertion Failure: '_usedMaxResolution == Vector2.Zero'
            // Same object, initialised at the player's resolution and then again at ours,
            // 183 ms apart. Whatever route hands us that object, "we constructed it so it is
            // fresh" is not a fact we get to assume — so the check is now unconditional.
            bool initialised = false, sizeMatches = false;
            try
            {
                var used = sbType.GetField("_usedMaxResolution", Any)?.GetValue(sb);
                initialised = NonZeroXY(used);
                if (used != null)
                {
                    var ut = used.GetType();
                    var ux = ut.GetField("X", Any)?.GetValue(used);
                    var uy = ut.GetField("Y", Any)?.GetValue(used);
                    sizeMatches = ux is int mx && uy is int my
                                  && mx == FeedConfig.WholeSceneWidth
                                  && my == FeedConfig.WholeSceneHeight;
                }
            }
            catch { }

            // IDENTITY GUARD. The one outcome that must never happen is re-initialising the
            // ENGINE'S ScreenBuffers at the feed's resolution — that is the player's depth
            // stencil, GBuffer and final LDR target, resized to 1024x1024 underneath them.
            // Reference equality against the live CoreSystems.ScreenBuffers is the whole
            // test, and it costs one field read. If it ever fires, the route disables itself
            // rather than continuing: a feed that does not render is a bad day, and a player
            // whose framebuffer we resized is a broken game.
            var liveSb = _coreType?.GetField("ScreenBuffers", BindingFlags.Public | BindingFlags.Static)
                                  ?.GetValue(null);
            if (liveSb != null && ReferenceEquals(liveSb, sb))
            {
                _state = -1;
                _ourScreenBuffers = null;
                RttLog.Line("!!! Whole-scene: the ScreenBuffers we were about to initialise at " +
                            $"{FeedConfig.WholeSceneWidth}x{FeedConfig.WholeSceneHeight} IS the engine's own " +
                            $"instance (#{sb.GetHashCode():x8}) — adopted={(adopted != null ? "from the park" : "no, freshly constructed")}. " +
                            "Initialising it would resize the PLAYER'S depth stencil, GBuffer and final LDR " +
                            "target to the feed's resolution. Route DISABLED for this session. Check what " +
                            "wrote the engine's instance into RttBridge.ParkedScreenBuffers.");
                return;
            }

            if (initialised && sizeMatches)
            {
                how = $"already initialised at {FeedConfig.WholeSceneWidth}x{FeedConfig.WholeSceneHeight} " +
                      $"(read from _usedMaxResolution on instance #{sb.GetHashCode():x8}, " +
                      $"{(adopted != null ? "adopted from the park" : "freshly constructed")}) — " +
                      "InitializeBuffers SKIPPED, so its existing buffers are reused rather than reallocated " +
                      "and stranded";
            }
            else if (init != null && res != null)
            {
                // A GENUINE resize, or a first build.
                //
                // DisposeBuffers() exists — the earlier note here claiming "the engine gives
                // us no disposal path" was wrong, it is right there on the type next to
                // Dispose(). Calling it first is what turns a resize from "strand the whole
                // previous depth+GBuffer+LDR set" into an actual resize. It is only correct
                // on an instance that HAS buffers, hence the `initialised` guard: on a fresh
                // construction there is nothing to dispose and calling it would be a
                // null-walk through fields the ctor never filled.
                if (initialised)
                {
                    try
                    {
                        sbType.GetMethod("DisposeBuffers", Any)?.Invoke(sb, null);
                        RttLog.Line("Whole-scene: adopted ScreenBuffers was initialised at a DIFFERENT size — " +
                                    "DisposeBuffers() called before re-initialising, so the previous depth " +
                                    "stencil, GBuffer array and LDR texture are released instead of stranded.");
                    }
                    catch (Exception e) { RttLog.Error("ScreenBuffers.DisposeBuffers before resize", e); }
                }

                init.Invoke(sb, new[] { res });
                how = $"InitializeBuffers({FeedConfig.WholeSceneWidth}x{FeedConfig.WholeSceneHeight})" +
                      (adopted != null
                          ? $" on the ADOPTED instance (was {(initialised ? "a different size — old set disposed first" : "never initialised")})"
                          : "");
            }
            else
            {
                how = $"constructed only (InitializeBuffers={(init == null ? "NOT FOUND" : "ok")}, " +
                      $"Vector2I={(res == null ? "NOT BUILT" : "ok")})";
            }

            _ourScreenBuffers = sb;
            ParkIt("ParkedScreenBuffers", sb);

            // Arm the settle window here too. The DrawContextManager build is the known
            // hazard and arms it as well, but this call site is the one that also covers
            // wholeSceneOwnDrawContexts = 0, where EnsureDrawContexts never runs at all —
            // and a fresh allocation of a whole ScreenBuffers set is not something to follow
            // with a nested Draw in the same frame on the strength of "the other build is
            // the one that matters". Cheap insurance on the path that has removed the device
            // three times. TryRender sees it on this same frame: this runs above it.
            _settleFrames = SettleFrames;

            RttLog.Line($"Whole-scene: SECOND ScreenBuffers built — {how}. " +
                        $"{DescribeScreenBuffers(sb)} Nothing is wired to it; the engine still owns " +
                        $"CoreSystems.ScreenBuffers. Settling {SettleFrames} engine frames before the " +
                        "first render.");
        }
        catch (Exception e)
        {
            RttLog.Error("build second ScreenBuffers", e);
        }
    }

    private static void LogScreenBuffers()
    {
        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var sb = core?.GetField("ScreenBuffers", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            RttLog.Line($"Whole-scene: engine ScreenBuffers — {DescribeScreenBuffers(sb)} " +
                        "Draw sizes its LBuffer from MaxPreUpscaleResolution, which is why a smaller " +
                        "target alone was never going to work.");
        }
        catch (Exception e) { RttLog.Error("describe engine ScreenBuffers", e); }
    }

    private static string DescribeScreenBuffers(object sb)
    {
        if (sb == null) return "ScreenBuffers=null.";
        try
        {
            var t = sb.GetType();
            string maxPre = t.GetProperty("MaxPreUpscaleResolution", Any)?.GetValue(sb)?.ToString() ?? "?";
            string pre = t.GetProperty("PreUpscaleResolution", Any)?.GetValue(sb)?.ToString() ?? "?";
            var gbuf = t.GetProperty("GBuffer", Any)?.GetValue(sb) as Array;
            var depth = t.GetProperty("DepthStencilBuffer", Any)?.GetValue(sb);
            var ldr = t.GetProperty("FinalLDRTexture", Any)?.GetValue(sb);
            return $"maxPreUpscale={maxPre} preUpscale={pre} gbuffer={(gbuf == null ? "null" : gbuf.Length + " targets")} " +
                   $"depth={(depth == null ? "null" : "ok")} finalLDR={(ldr == null ? "null" : "ok")}.";
        }
        catch { return "ScreenBuffers unreadable."; }
    }

    private static string Describe(object tex)
    {
        try
        {
            var t = tex.GetType();
            string res = t.GetProperty("Resolution", Any)?.GetValue(tex)?.ToString() ?? "?";
            return $"{t.Name} resolution={res}";
        }
        catch { return tex?.GetType().Name ?? "null"; }
    }

    // Vector2I is a value type in Keen.VRage.Library.Mathematics; built by reflection so
    // the logic assembly keeps no compile-time reference to the engine.
    private static object MakeVector2I(int x, int y)
    {
        try
        {
            var t = Type.GetType("Keen.VRage.Library.Mathematics.Vector2I, VRage.Library")
                    ?? FindTypeAnywhere("Vector2I");
            if (t == null) return null;
            var ctor = t.GetConstructor(new[] { typeof(int), typeof(int) });
            return ctor?.Invoke(new object[] { x, y });
        }
        catch { return null; }
    }

    private static Type FindTypeAnywhere(string name)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = a.GetTypes().FirstOrDefault(x => x.Name == name);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }
}
