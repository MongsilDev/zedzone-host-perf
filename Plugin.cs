using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Hook;
using Il2CppInterop.Runtime;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ZZPerf
{
    // 호스트 화면과 호스트 안의 캐시만 다룬다. 동기화 데이터와 세이브에 쓰이는 값은 건드리지 않는다.
    [BepInPlugin("local.zzperf", "ZZPerf", "0.4.0")]
    public class PerfPlugin : BasePlugin
    {
        internal static ManualLogSource L;
        static ConfigFile cfg;
        static ConfigEntry<bool> cEnabled, cHostOnly, cLight, cSort, cStats, cNearby;
        static ConfigEntry<int> cWaterMs, cFarInterval;
        static ConfigEntry<float> cSortMargin, cSortDelay, cSortBudgetMs, cFarDistance, cNearResync;

        static string cfgPath; static DateTime cfgStamp; static long nextCfgCheck;
        static bool active; static long nextActiveCheck; static bool cfgWarned;

        public override void Load()
        {
            L = Log; cfg = Config; cfgPath = Config.ConfigFilePath;
            cEnabled = Config.Bind("General", "Enabled", true, "Turns the whole plugin on or off. Changes take effect within 2 seconds, even mid-game.");
            cHostOnly = Config.Bind("General", "OnlyWhenHosting", true, "Runs only while you host a room.");
            cStats = Config.Bind("General", "LogStats", false, "Writes activity counts to the BepInEx log every 30 seconds.");
            cLight = Config.Bind("Light", "UseRegisteredLights", true, "Gets the light list from URP's registered lights instead of searching the whole scene.");
            cWaterMs = Config.Bind("Water", "PurgeMinIntervalMs", 250, "Minimum interval between water tile cache purges in ms. 0 keeps the original behavior.");
            cSort = Config.Bind("Sorting", "CullOffscreenChunks", true, "Disables SortingGroup on terrain objects in chunks outside the host's view.");
            cSortMargin = Config.Bind("Sorting", "MarginUnits", 48f, "Chunks within this distance of the screen edge stay enabled.");
            cSortDelay = Config.Bind("Sorting", "DelaySeconds", 1f, "A chunk is disabled after staying offscreen this long.");
            cSortBudgetMs = Config.Bind("Sorting", "BudgetMs", 1f, "Time budget in ms for disabling work, run every 0.1 seconds.");
            cFarInterval = Config.Bind("FarChunks", "UpdateInterval", 1, "Update objects in chunks far from every player once every N frames. 1 keeps the original behavior. 2 or more can change what guests see.");
            cFarDistance = Config.Bind("FarChunks", "Distance", 150f, "Chunks farther than this from every player count as far. The game default is 150. Values below 64 are not applied.");
            cNearby = Config.Bind("NearbyItems", "CacheSearch", true, "Replaces the per-frame whole-scene search for water wells and containers in the nearby items panel with a list recorded when they are created. The original search runs once when the panel opens to resync the list.");
            cNearResync = Config.Bind("NearbyItems", "ResyncSeconds", 30f, "While the panel is open, run the original search at this interval to resync the list. 0 resyncs only when the panel opens.");
            try { cfgStamp = File.GetLastWriteTimeUtc(cfgPath); } catch { }

            var h = new Harmony("local.zzperf");
            var light = new HarmonyMethod(typeof(PerfPlugin), nameof(LightPre));
            Try(() => h.Patch(AccessTools.Method(typeof(PointLightSlotTable), "Collect"), prefix: light), "PointLightSlotTable.Collect");
            Try(() => h.Patch(AccessTools.Method(typeof(PointLightShadow2DGeometry), "EnsureLightCacheFresh"), prefix: light), "EnsureLightCacheFresh");
            Try(() => h.Patch(AccessTools.Method(typeof(PointLightShadow2DGeometry), "GetLightCache"), prefix: light), "GetLightCache");
            Try(() => h.Patch(AccessTools.Method(typeof(MapController), "PurgeExpiredWaterCache"),
                prefix: new HarmonyMethod(typeof(PerfPlugin), nameof(WaterPre))), "PurgeExpiredWaterCache");
            Try(() => h.Patch(AccessTools.Method(typeof(MapController), "Update"),
                postfix: new HarmonyMethod(typeof(PerfPlugin), nameof(AfterMapUpdate))), "MapController.Update");
            // Init이 SortingGroup.enabled를 기본값으로 기록하므로 이 패치 없이는 끄지 않는다
            try { h.Patch(AccessTools.Method(typeof(TerrainObject), "Init"), prefix: new HarmonyMethod(typeof(PerfPlugin), nameof(InitPre)), postfix: new HarmonyMethod(typeof(PerfPlugin), nameof(InitPost))); }
            catch (Exception e) { sortFailed = true; nearFailed = true; L.LogWarning("patch failed: TerrainObject.Init, sorting cull and nearby cache off: " + e.Message); }
            NearInstall(h);
            Log.LogInfo("ZZPerf loaded");
        }

        static void Try(Action a, string what)
        {
            try { a(); } catch (Exception e) { L.LogWarning("patch failed: " + what + ": " + e.Message); }
        }

        static bool Active()
        {
            long now = Stopwatch.GetTimestamp();
            if (now >= nextActiveCheck)
            {
                nextActiveCheck = now + Stopwatch.Frequency / 2;
                bool a = cEnabled.Value;
                if (a && cHostOnly.Value) { try { a = ZedZone.Net.NetworkManager.isHost; } catch { a = false; } }
                active = a;
            }
            return active;
        }

        // ---- 조명 목록 ----
        static bool lightFailed; static int lightRefreshes, lightLast = -1;

        static void LightPre(int refreshFrames)
        {
            if (lightFailed || !cLight.Value || !Active()) return;
            try
            {
                int f = Time.frameCount;
                var cache = PointLightShadow2DGeometry.s_LightCache;
                int last = PointLightShadow2DGeometry.s_LastCacheFrame;
                if (cache != null && f - last < refreshFrames && last <= f) return;
                var src = Light2DManager.lights;
                int n = src.Count;
                var arr = new Il2CppReferenceArray<Light2D>(n);
                for (int i = 0; i < n; i++) arr[i] = src[i];
                PointLightShadow2DGeometry.s_LightCache = arr;
                PointLightShadow2DGeometry.s_LastCacheFrame = f;
                lightRefreshes++; lightLast = n;
            }
            catch (Exception e) { lightFailed = true; L.LogWarning("light cache off: " + e.Message); }
        }

        // ---- 물 타일 캐시 ----
        static long lastPurge; static int purgeRun, purgeSkipped;

        static bool WaterPre()
        {
            int ms = cWaterMs.Value;
            if (ms <= 0 || !Active()) return true;
            long now = Stopwatch.GetTimestamp();
            if (now - lastPurge < ms * Stopwatch.Frequency / 1000) { purgeSkipped++; return false; }
            lastPurge = now; purgeRun++;
            return true;
        }

        // ---- 근처 물품 창의 물 우물과 컨테이너 검색 ----
        // FindObjectsOfType<T>()의 참조형 공유 구현. 이 빌드에서 인자는 MethodInfo 하나뿐이다
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr FindFn(IntPtr method);
        static FindFn findHook, findOrig; static INativeDetour findDetour;
        static readonly byte[] findPrologue = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x83, 0x79, 0x38, 0x00 };
        static bool nearFailed, inPanel; static int nearWarns;
        static readonly Dictionary<IntPtr, RegBase> regByMethod = new Dictionary<IntPtr, RegBase>();
        static readonly Dictionary<IntPtr, RegBase> regByClass = new Dictionary<IntPtr, RegBase>();
        static RegBase[] regs;

        abstract class RegBase
        {
            public string name; public IntPtr cls;
            public long lastCall, nextResync;
            public int served, resyncs, missing, extra;
            public abstract void Add(IntPtr p);
            public abstract IntPtr Build();
            public abstract void Sync(IntPtr arr);
            public abstract int Count { get; }
        }

        // FindObjectsOfType와 같이 살아 있고 활성인 물체만 인스턴스 ID 순서로 내준다
        sealed class Reg<T> : RegBase where T : Component
        {
            sealed class Ent { public T c; public GameObject go; public int id; }
            readonly Func<IntPtr, T> wrap;
            readonly Dictionary<IntPtr, Ent> map = new Dictionary<IntPtr, Ent>();
            readonly List<Ent> list = new List<Ent>();
            readonly List<Ent> tmp = new List<Ent>();
            readonly HashSet<IntPtr> seen = new HashSet<IntPtr>();
            bool dirty; int pruneAt = 256;
            Il2CppReferenceArray<T> keep;

            public Reg(string n, Func<IntPtr, T> w) { name = n; wrap = w; cls = Il2CppClassPointerStore<T>.NativeClassPtr; }
            public override int Count => list.Count;

            public override void Add(IntPtr p)
            {
                if (map.ContainsKey(p)) return;
                var c = wrap(p);
                var e = new Ent { c = c, go = c.gameObject, id = c.GetInstanceID() };
                map[p] = e; list.Add(e); dirty = true;
                if (list.Count >= pruneAt) { Prune(); pruneAt = Math.Max(256, list.Count * 2); }
            }

            static bool Alive(Ent e) { return e.c.m_CachedPtr != IntPtr.Zero; }

            void Prune()
            {
                int w = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (Alive(e)) list[w++] = e; else map.Remove(e.c.Pointer);
                }
                list.RemoveRange(w, list.Count - w);
            }

            public override IntPtr Build()
            {
                if (dirty) { list.Sort((a, b) => a.id.CompareTo(b.id)); dirty = false; }
                tmp.Clear();
                bool dead = false;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (!Alive(e)) { dead = true; continue; }
                    if (e.go.activeInHierarchy) tmp.Add(e);
                }
                if (dead) Prune();
                var a = new Il2CppReferenceArray<T>(tmp.Count);
                for (int i = 0; i < tmp.Count; i++) a[i] = tmp[i].c;
                keep = a;
                return a.Pointer;
            }

            // 원래 검색 결과와 비교해 빠진 것은 넣고 차이 수를 센다
            public override void Sync(IntPtr arr)
            {
                seen.Clear();
                int miss = 0, ext = 0;
                var a = new Il2CppReferenceArray<T>(arr);
                for (int i = 0; i < a.Length; i++)
                {
                    var c = a[i];
                    if (c == null) continue;
                    seen.Add(c.Pointer);
                    if (!map.ContainsKey(c.Pointer)) { Add(c.Pointer); miss++; }
                }
                // 원래 검색이 내주지 않은 것은 빼고, 다시 나타나면 다음 맞춤 때 넣는다
                int w = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    bool alive = Alive(e);
                    if (alive && !seen.Contains(e.c.Pointer) && e.go.activeInHierarchy) { ext++; alive = false; }
                    if (alive) list[w++] = e; else map.Remove(e.c.Pointer);
                }
                list.RemoveRange(w, list.Count - w);
                missing += miss; extra += ext;
                if ((miss > 0 || ext > 0) && nearWarns < 20) { nearWarns++; L.LogWarning("nearby " + name + " resync missing=" + miss + " extra=" + ext + " reg=" + list.Count); }
            }
        }

        static void NearInstall(Harmony h)
        {
            if (nearFailed) return;
            try
            {
                regs = new RegBase[]
                {
                    new Reg<TerrainObject_WaterWell>("well", p => new TerrainObject_WaterWell(p)),
                    new Reg<TerrainObject_Enterable_Container>("container", p => new TerrainObject_Enterable_Container(p)),
                };
                IntPtr code = FindCode();
                if (code == IntPtr.Zero) throw new Exception("FindObjectsOfType code not found");
                var head = new byte[findPrologue.Length];
                Marshal.Copy(code, head, 0, head.Length);
                for (int i = 0; i < head.Length; i++)
                    if (head[i] != findPrologue[i]) throw new Exception("FindObjectsOfType prologue differs, game build changed");
                // 창 갱신 안에서 부른 검색만 대신한다
                var m = AccessTools.Method(typeof(NearbyItemInventoryPanel), "RefreshGroundItemInventoryExecute");
                if (m == null) throw new Exception("RefreshGroundItemInventoryExecute not found");
                h.Patch(m, prefix: new HarmonyMethod(typeof(PerfPlugin), nameof(PanelPre)), postfix: new HarmonyMethod(typeof(PerfPlugin), nameof(PanelPost)));
                findHook = FindDetour;
                findDetour = INativeDetour.CreateAndApply(code, findHook, out findOrig);
                L.LogInfo("nearby cache hook at GameAssembly+0x" + (code.ToInt64() - GameAssemblyBase()).ToString("x"));
            }
            catch (Exception e) { nearFailed = true; L.LogWarning("nearby cache off: " + e.Message); }
        }

        static long GameAssemblyBase()
        {
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                if (string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase)) return m.BaseAddress.ToInt64();
            return 0;
        }

        static IntPtr FindCode()
        {
            var flags = Il2CppSystem.Reflection.BindingFlags.Public | Il2CppSystem.Reflection.BindingFlags.Static;
            foreach (var m in Il2CppType.Of<UnityEngine.Object>().GetMethods(flags))
            {
                if (m.Name != "FindObjectsOfType" || !m.IsGenericMethodDefinition || m.GetParameters().Length != 0) continue;
                var ta = new Il2CppReferenceArray<Il2CppSystem.Type>(1);
                ta[0] = Il2CppType.Of<TerrainObject_WaterWell>();
                IntPtr mi = IL2CPP.il2cpp_method_get_from_reflection(m.MakeGenericMethod(ta).Pointer);
                if (mi != IntPtr.Zero) return Marshal.ReadIntPtr(mi);
            }
            return IntPtr.Zero;
        }

        // 하위 클래스도 FindObjectsOfType 결과에 들어가므로 부모 클래스 기준으로 고른다
        static RegBase RegFor(IntPtr klass)
        {
            if (regByClass.TryGetValue(klass, out var r)) return r;
            r = null;
            foreach (var g in regs)
                if (klass == g.cls || IL2CPP.il2cpp_class_is_subclass_of(klass, g.cls, false)) { r = g; break; }
            regByClass[klass] = r;
            return r;
        }

        static void PanelPre() { inPanel = true; }
        static void PanelPost() { inPanel = false; }

        static void InitPost(TerrainObject __instance)
        {
            if (nearFailed || regs == null) return;
            try
            {
                IntPtr p = __instance.Pointer;
                RegFor(IL2CPP.il2cpp_object_get_class(p))?.Add(p);
            }
            catch (Exception e) { nearFailed = true; L.LogWarning("nearby cache off: " + e.Message); }
        }

        static IntPtr FindDetour(IntPtr method)
        {
            if (!inPanel || nearFailed || !cNearby.Value || !Active()) return findOrig(method);
            try
            {
                if (!regByMethod.TryGetValue(method, out var r))
                {
                    IntPtr res = findOrig(method);
                    if (res != IntPtr.Zero)
                    {
                        // 하위 클래스를 찾는 호출은 대신하지 않는다
                        IntPtr el = IL2CPP.il2cpp_class_get_element_class(IL2CPP.il2cpp_object_get_class(res));
                        r = Array.Find(regs, g => g.cls == el);
                        regByMethod[method] = r;
                        if (r != null) { r.Sync(res); r.resyncs++; r.lastCall = Stopwatch.GetTimestamp(); r.nextResync = NextResync(r.lastCall); }
                    }
                    return res;
                }
                if (r == null) return findOrig(method);
                long now = Stopwatch.GetTimestamp();
                // 1초 넘게 불리지 않았으면 창을 새로 연 것으로 보고 원래 검색으로 맞춘다
                bool resync = now - r.lastCall > Stopwatch.Frequency || (r.nextResync > 0 && now >= r.nextResync);
                r.lastCall = now;
                if (resync)
                {
                    IntPtr res = findOrig(method);
                    if (res != IntPtr.Zero) r.Sync(res);
                    r.resyncs++; r.nextResync = NextResync(now);
                    return res;
                }
                r.served++;
                return r.Build();
            }
            catch (Exception e) { nearFailed = true; L.LogWarning("nearby cache off: " + e); return findOrig(method); }
        }

        static long NextResync(long now)
        {
            float s = cNearResync.Value;
            return s > 0 ? now + (long)(s * Stopwatch.Frequency) : 0;
        }

        static string NearStats()
        {
            if (regs == null) return " nearby=off";
            var sb = new System.Text.StringBuilder(nearFailed ? " nearby=failed" : "");
            foreach (var r in regs) sb.Append(" " + r.name + "=" + r.Count + "/" + r.served + "/" + r.resyncs + "/" + r.missing + "/" + r.extra);
            return sb.ToString();
        }

        // ---- 먼 청크 갱신 간격(게임에 들어 있는 기능의 값만 바꾼다) ----
        static bool farApplied; static int farOrigInterval; static float farOrigDistance; static long nextFar;

        static void FarTick()
        {
            long now = Stopwatch.GetTimestamp();
            if (now < nextFar) return;
            nextFar = now + Stopwatch.Frequency / 2;
            int iv = cFarInterval.Value;
            if (iv > 1 && Active())
            {
                if (!farApplied)
                {
                    farOrigInterval = GameConfigs.hostFarChunkUpdateInterval; farOrigDistance = GameConfigs.hostFarChunkUpdateDistance;
                    farApplied = true;
                }
                GameConfigs.hostFarChunkUpdateInterval = iv;
                GameConfigs.hostFarChunkUpdateDistance = Math.Max(64f, cFarDistance.Value);
            }
            else if (farApplied)
            {
                GameConfigs.hostFarChunkUpdateInterval = farOrigInterval; GameConfigs.hostFarChunkUpdateDistance = farOrigDistance;
                farApplied = false;
            }
        }

        // ---- 화면 밖 청크의 SortingGroup ----
        sealed class Culled
        {
            public readonly List<IntPtr> owners = new List<IntPtr>();
            public int seenCount = -1, next;
        }
        static readonly Dictionary<IntPtr, SortingGroup> offByOwner = new Dictionary<IntPtr, SortingGroup>();
        static int statInitFix;
        static readonly Dictionary<long, Culled> culled = new Dictionary<long, Culled>();
        static readonly Dictionary<long, float> farSince = new Dictionary<long, float>();
        static readonly HashSet<long> activeKeys = new HashSet<long>();
        static readonly List<long> scratch = new List<long>();
        static float nextScan; static int cursor; static bool cullPending, sortFailed, farFailed; static IntPtr lastMap;
        static int statDisabled, statRestored; static float nextStats;
        static double restoreMsMax, cullMsMax;

        static long Key(int x, int y) { return ((long)x << 32) ^ (uint)y; }

        static void AfterMapUpdate(MapController __instance)
        {
            // 창 갱신이 예외로 끝나 후처리가 빠져도 다음 프레임에는 풀린다
            inPanel = false;
            // 저장 중인 파일을 읽다 실패하면 스탬프를 두고 2초 뒤에 다시 읽는다
            try
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= nextCfgCheck)
                {
                    nextCfgCheck = now + 2 * Stopwatch.Frequency;
                    var st = File.GetLastWriteTimeUtc(cfgPath);
                    if (st != cfgStamp) { cfg.Reload(); cfgStamp = st; nextActiveCheck = 0; nextFar = 0; L.LogInfo("config reloaded"); }
                }
            }
            catch (Exception e) { if (!cfgWarned) { cfgWarned = true; L.LogWarning("config reload: " + e.Message); } }
            if (!farFailed)
            {
                try { FarTick(); }
                catch (Exception e) { farFailed = true; L.LogWarning("far chunk interval off: " + e.Message); }
            }
            if (!sortFailed)
            {
                try { SortTick(__instance); }
                catch (Exception e) { sortFailed = true; L.LogWarning("offscreen cull off: " + e); try { RestoreAll(); } catch { } }
            }
            if (cStats.Value && Time.unscaledTime >= nextStats)
            {
                nextStats = Time.unscaledTime + 30f;
                string game = "";
                try { game = GameStats(); } catch { }
                L.LogInfo("stats active=" + active + " lightRefresh=" + lightRefreshes + " lights=" + lightLast + " purgeRun=" + purgeRun + " purgeSkipped=" + purgeSkipped
                    + " culledChunks=" + culled.Count + " offObjects=" + offByOwner.Count + " disabled=" + statDisabled + " restored=" + statRestored
                    + " initFix=" + statInitFix + NearStats()
                    + " restoreMsMax=" + restoreMsMax.ToString("0.00") + " cullMsMax=" + cullMsMax.ToString("0.00") + game);
                restoreMsMax = 0; cullMsMax = 0;
            }
        }

        // 게임 쪽 멤버를 읽는 부분을 따로 두어, 멤버가 없어져도 AfterMapUpdate의 JIT가 실패하지 않게 한다
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static string GameStats()
        {
            return " bindersActive=" + SpriteRectBinder.s_Active.Count
                + " farInterval=" + GameConfigs.hostFarChunkUpdateInterval + " farDistance=" + GameConfigs.hostFarChunkUpdateDistance;
        }

        static void Enable(SortingGroup g)
        {
            if (g != null && !g.enabled) { g.enabled = true; statRestored++; }
        }

        // 풀에서 나온 물체가 꺼진 상태로 Init에 들어가면 꺼짐이 기본값으로 굳는다
        static void InitPre(TerrainObject __instance)
        {
            if (offByOwner.Count == 0) return;
            try
            {
                if (offByOwner.Remove(__instance.Pointer, out var o)) { Enable(o); statInitFix++; }
            }
            catch (Exception e) { L.LogWarning("init guard: " + e.Message); }
        }

        static void Restore(Culled c)
        {
            var l = c.owners;
            for (int i = 0; i < l.Count; i++)
                if (offByOwner.Remove(l[i], out var o)) Enable(o);
            l.Clear();
        }

        static void RestoreAll()
        {
            long t0 = Stopwatch.GetTimestamp();
            foreach (var c in culled.Values) Restore(c);
            foreach (var o in offByOwner.Values) Enable(o);
            culled.Clear(); farSince.Clear(); offByOwner.Clear();
            Note(ref restoreMsMax, t0);
        }

        static void Note(ref double max, long t0)
        {
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms > max) max = ms;
        }

        static void SortTick(MapController mc)
        {
            if (!cSort.Value || !Active()) { if (culled.Count > 0 || offByOwner.Count > 0) RestoreAll(); return; }
            // 방을 다시 열면 MapController가 새로 만들어지고 앞 방의 물체는 없어진다
            if (mc.Pointer != lastMap)
            {
                lastMap = mc.Pointer;
                RestoreAll();
            }
            float now = Time.unscaledTime;
            var cam = CameraControl.mainCam;
            if (cam == null) cam = Camera.main;
            var cdm = ChunkDataManager.instance;
            if (cam == null || cdm == null) return;
            Vector3 cp = cam.transform.position;
            float hh = cam.orthographicSize + cSortMargin.Value, hw = cam.orthographicSize * cam.aspect + cSortMargin.Value;
            var lo = mc.GetChunkCoordinateByPosition(new Vector2(cp.x - hw, cp.y - hh));
            var hi = mc.GetChunkCoordinateByPosition(new Vector2(cp.x + hw, cp.y + hh));
            int x0 = Math.Min(lo.x, hi.x), x1 = Math.Max(lo.x, hi.x), y0 = Math.Min(lo.y, hi.y), y1 = Math.Max(lo.y, hi.y);

            // 화면에 들어온 청크는 매 프레임 되돌린다. 끄는 일만 0.1초마다 한다
            if (culled.Count > 0)
            {
                scratch.Clear();
                foreach (var k in culled.Keys)
                {
                    int kx = (int)(k >> 32), ky = (int)k;
                    if (kx >= x0 && kx <= x1 && ky >= y0 && ky <= y1) scratch.Add(k);
                }
                if (scratch.Count > 0)
                {
                    long r0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < scratch.Count; i++) { Restore(culled[scratch[i]]); culled.Remove(scratch[i]); farSince.Remove(scratch[i]); }
                    Note(ref restoreMsMax, r0);
                }
            }
            // 예산에 걸려 남은 일이 있으면 다음 프레임에 이어서 한다
            if (now < nextScan && !cullPending) return;
            nextScan = now + 0.1f;
            cullPending = false;

            var list = mc.currentChunkCoorList;
            int n = list.Count;
            activeKeys.Clear();
            for (int i = 0; i < n; i++)
            {
                var c = list[i];
                long k = Key(c.x, c.y);
                activeKeys.Add(k);
                if (c.x >= x0 && c.x <= x1 && c.y >= y0 && c.y <= y1) farSince.Remove(k);
                else if (!farSince.ContainsKey(k)) farSince[k] = now;
            }
            // 내려간 청크의 물체는 풀로 돌아가 다른 청크에서 다시 쓰이므로 바로 되돌린다
            scratch.Clear();
            foreach (var k in culled.Keys) if (!activeKeys.Contains(k)) scratch.Add(k);
            for (int i = 0; i < scratch.Count; i++) { Restore(culled[scratch[i]]); culled.Remove(scratch[i]); }
            scratch.Clear();
            foreach (var k in farSince.Keys) if (!activeKeys.Contains(k)) scratch.Add(k);
            for (int i = 0; i < scratch.Count; i++) farSince.Remove(scratch[i]);

            if (n == 0) return;
            long budget = (long)(cSortBudgetMs.Value * Stopwatch.Frequency / 1000.0), t0 = Stopwatch.GetTimestamp();
            float delay = cSortDelay.Value;
            for (int step = 0; step < n; step++)
            {
                if (cursor >= n) cursor = 0;
                var c = list[cursor++];
                long k = Key(c.x, c.y);
                if (!farSince.TryGetValue(k, out float since) || now - since < delay) continue;
                var cd = cdm.LoadChunkDataFromCache(c);
                if (cd == null) continue;
                var objs = cd.terrainObjectListTemp;
                if (objs == null) continue;
                int count = objs.Count;
                if (!culled.TryGetValue(k, out var cu)) culled[k] = cu = new Culled();
                if (cu.seenCount != count) { cu.seenCount = count; cu.next = 0; }
                if (cu.next >= count) continue;
                int j = cu.next, j0 = j;
                for (; j < count; j++)
                {
                    // 이어서 할 때 첫 물체는 예산과 상관없이 처리해 같은 자리에서 멈추지 않게 한다
                    if (j > j0 && (j & 15) == 15 && Stopwatch.GetTimestamp() - t0 > budget) { cullPending = true; break; }
                    var to = objs[j];
                    if (to == null) continue;
                    IntPtr owner = to.Pointer;
                    if (offByOwner.ContainsKey(owner)) continue;
                    var g = to.m_sortingGroup;
                    if (g != null && g.enabled) { g.enabled = false; statDisabled++; offByOwner[owner] = g; cu.owners.Add(owner); }
                }
                cu.next = j;
                if (cullPending) { cursor--; break; }
                if (Stopwatch.GetTimestamp() - t0 > budget) { cullPending = true; break; }
            }
            Note(ref cullMsMax, t0);
        }
    }
}
