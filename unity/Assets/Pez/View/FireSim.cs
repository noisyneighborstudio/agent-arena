using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace Pez.View
{
    /// <summary>
    /// One burning building's fire as a GPU fluid simulation (PezFireSim.compute) on the grid FireVoxels builds from its
    /// model, drawn by raymarching (PezFireVolume.shader), with GPU embers and sparks riding the flow (PezFireSpark.shader).
    /// After Ignitement's breakdown on the Unity blog, adapted to a 3D grid local to each building:
    ///  - the fire comes off the building's own surface and goes round its real shape (the solid cells come from the mesh);
    ///  - it licks out of the openings (dark recesses, doors, vents, glass) and curls up the wall above them;
    ///  - its light reaches the scene through one world-space light map that the model and ground shaders sample once at
    ///    their position pushed out along their normal (Ignitement's trick), and its smoke shadows the ground under it;
    ///  - embers stream off it and now and then a pocket of gas pops, flaring and throwing a burst of sparks;
    ///  - uncontrolled it makes thick black soot; under repair the flame and heat are knocked down into white steam.
    /// What leaves the top of the grid is read back (AsyncGPUReadback, a 4x4 summary) and carried on up as particle smoke,
    /// so the column rises as high as the old particle plume without a tall grid.
    /// A building's fire is drawn on its audience's layers only (fog: see <see cref="Fx.Audience"/>), and its light is
    /// masked out of views that can't see it (<see cref="SetPovLayer"/>).
    /// </summary>
    public class FireSim
    {
        // ---------------------------------------------------------------- shared

        static ComputeShader cs;
        static Material volumeMat, sparkMat;
        static Mesh cube;
        static bool loaded, ok;
        static Texture3D noise;
        /// <summary>Profiling switches, "-firedebug novol,nosim,nolight,nosparks,noread,noplume" (off in normal runs).</summary>
        static bool dbgNoVol, dbgNoSim, dbgNoLight, dbgNoSparks, dbgNoRead, dbgNoPlume;
        static int kClear, kAdvect, kCurl, kConfine, kDiv, kJacobi, kProject, kReduce, kSplat, kClearMap, kOutflow, kSpawn, kStep;
        static readonly List<FireSim> live = new List<FireSim>();
        /// <summary>The most fires simulated at once; more burning buildings fall back to the particle fire (BuildingFire).</summary>
        public const int MaxLive = 8;
        const int Sparks = 384, JacobiIters = 14;
        /// <summary>The simulation steps at a fixed 30 Hz, each fire on alternate frames from its neighbour (load spread).</summary>
        const float StepDt = 1f / 30f;

        /// <summary>Can this machine run it (compute shaders, 3D render textures), and did the shaders load?</summary>
        public static bool Supported
        {
            get
            {
                if (loaded) return ok;
                loaded = true;
                if (!SystemInfo.supportsComputeShaders || !SystemInfo.supports3DRenderTextures || !SystemInfo.supportsAsyncGPUReadback) return ok = false;
                cs = Resources.Load<ComputeShader>("PezShaders/PezFireSim");
                var vs = Resources.Load<Shader>("PezShaders/PezFireVolume");
                var ss = Resources.Load<Shader>("PezShaders/PezFireSpark");
                if (cs == null || vs == null || ss == null || !vs.isSupported || !ss.isSupported) { Debug.LogWarning("FireSim: shaders missing or unsupported; particle fire only"); return ok = false; }
                volumeMat = new Material(vs);
                sparkMat = new Material(ss);
                kClear = cs.FindKernel("Clear"); kAdvect = cs.FindKernel("Advect"); kCurl = cs.FindKernel("Curl"); kConfine = cs.FindKernel("Confine");
                kDiv = cs.FindKernel("Divergence"); kJacobi = cs.FindKernel("Jacobi"); kProject = cs.FindKernel("Project");
                kReduce = cs.FindKernel("Reduce"); kSplat = cs.FindKernel("Splat"); kClearMap = cs.FindKernel("ClearMap"); kOutflow = cs.FindKernel("Outflow");
                kSpawn = cs.FindKernel("SparkSpawn"); kStep = cs.FindKernel("SparkStep");
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube = go.GetComponent<MeshFilter>().sharedMesh;
                Object.Destroy(go);
                noise = NoiseTexture();
                var args = System.Environment.GetCommandLineArgs();
                int di = System.Array.IndexOf(args, "-firedebug");
                if (di >= 0 && di + 1 < args.Length)
                {
                    var d = args[di + 1];
                    dbgNoVol = d.Contains("novol"); dbgNoSim = d.Contains("nosim"); dbgNoLight = d.Contains("nolight"); dbgNoSparks = d.Contains("nosparks"); dbgNoRead = d.Contains("noread"); dbgNoPlume = d.Contains("noplume");
                }
                var host = new GameObject("FireSims");
                Object.DontDestroyOnLoad(host);
                host.AddComponent<Host>();
                return ok = true;
            }
        }

        /// <summary>
        /// A tiling 32^3 noise (three independent smooth value noises in rgb) that the raymarch bends its lookups by: one
        /// texture fetch per step instead of hashing three noises per step.
        /// </summary>
        static Texture3D NoiseTexture()
        {
            const int N = 32, Cell = 8;
            var rnd = new System.Random(4242);
            var lattice = new float[3, Cell, Cell, Cell];
            for (int c = 0; c < 3; c++) for (int x = 0; x < Cell; x++) for (int y = 0; y < Cell; y++) for (int z = 0; z < Cell; z++) lattice[c, x, y, z] = (float)rnd.NextDouble();
            var px = new Color32[N * N * N];
            for (int z = 0; z < N; z++)
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float fx = x * Cell / (float)N, fy = y * Cell / (float)N, fz = z * Cell / (float)N;
                        int x0 = (int)fx, y0 = (int)fy, z0 = (int)fz;
                        float tx = fx - x0, ty = fy - y0, tz = fz - z0;
                        tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty); tz = tz * tz * (3 - 2 * tz);
                        var v = new byte[3];
                        for (int c = 0; c < 3; c++)
                        {
                            float L(int a, int b, int d) => lattice[c, (x0 + a) % Cell, (y0 + b) % Cell, (z0 + d) % Cell];
                            float n = Mathf.Lerp(Mathf.Lerp(Mathf.Lerp(L(0, 0, 0), L(1, 0, 0), tx), Mathf.Lerp(L(0, 1, 0), L(1, 1, 0), tx), ty),
                                                 Mathf.Lerp(Mathf.Lerp(L(0, 0, 1), L(1, 0, 1), tx), Mathf.Lerp(L(0, 1, 1), L(1, 1, 1), tx), ty), tz);
                            v[c] = (byte)(n * 255f);
                        }
                        px[x + N * (y + N * z)] = new Color32(v[0], v[1], v[2], 255);
                    }
            var t = new Texture3D(N, N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, name = "fire_noise" };
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        /// <summary>A fire for this building's model, or null (unsupported, the budget's spent, or no usable meshes).</summary>
        public static FireSim Create(GameObject model, int seed)
        {
            if (!Supported || live.Count >= MaxLive || model == null) return null;
            // The meshes are read here; the voxelizing (tens of ms for a big building) runs on a worker thread, and the
            // fire starts drawing the frame after it's done.
            var src = FireVoxels.Gather(model);
            if (src == null) return null;
            var f = new FireSim(seed) { building = Task.Run(() => FireVoxels.Build(src, seed)) };
            live.Add(f);
            return f;
        }

        // ---------------------------------------------------------------- one fire

        public FireVoxels Vox { get; private set; }
        readonly int seed;
        Task<FireVoxels> building;
        float lastTick;
        RenderTexture velA, velB, scalA, scalB, pA, pB, div, curl;
        Texture3D sdfTex, emitTex;
        ComputeBuffer sparks, outflow, lights;
        readonly MaterialPropertyBlock volProps = new MaterialPropertyBlock(), sparkProps = new MaterialPropertyBlock();
        readonly Vector4[] outflowData = new Vector4[16];
        readonly float[] plumeAcc = new float[16];
        bool released, readPending, hasOutflow;
        float outflowAt, emberAcc, popAt, idle, stepAcc;
        int sparkHead;
        List<int> layers;                       // the render layers it's drawn on (null: all), copied each frame
        readonly List<int> layerBuf = new List<int>();
        int tickFrame = -1;
        float fireK, smokeK, extK;
        Vector3 extPos;
        public Rect LightRect { get; private set; }

        FireSim(int seed) { this.seed = seed; lastTick = Time.time; }

        /// <summary>Has the grid been built and the GPU state made? False while the worker thread is still voxelizing.</summary>
        bool Ready()
        {
            if (Vox != null) return true;
            if (released || building == null || !building.IsCompleted) return false;
            if (building.IsFaulted || building.Result == null)
            {
                Debug.LogWarning("FireSim: couldn't build the fire grid: " + building.Exception?.GetBaseException().Message);
                Release();
                return false;
            }
            Init(building.Result);
            building = null;
            return true;
        }

        void Init(FireVoxels vox)
        {
            Vox = vox;
            velA = Field(RenderTextureFormat.ARGBHalf); velB = Field(RenderTextureFormat.ARGBHalf);
            scalA = Field(RenderTextureFormat.ARGBHalf); scalB = Field(RenderTextureFormat.ARGBHalf);
            pA = Field(RenderTextureFormat.RHalf); pB = Field(RenderTextureFormat.RHalf);
            div = Field(RenderTextureFormat.RHalf); curl = Field(RenderTextureFormat.ARGBHalf);
            var d = vox.Dim;
            sdfTex = new Texture3D(d.x, d.y, d.z, TextureFormat.RFloat, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "fire_sdf" };
            sdfTex.SetPixelData(vox.Sdf, 0);
            sdfTex.Apply(false, true);
            emitTex = new Texture3D(d.x, d.y, d.z, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, name = "fire_emit" };
            emitTex.SetPixels(vox.Emit);
            emitTex.Apply(false, true);
            sparks = new ComputeBuffer(Sparks, 48);
            sparks.SetData(new float[Sparks * 12]); // all dead
            outflow = new ComputeBuffer(16, 16);
            lights = new ComputeBuffer(96, 16);
            Common(0f);
            Bind(kClear, ("_VelOut", velA), ("_ScalOut", scalA), ("_PressOut", pA)); Dispatch3(kClear);
            Bind(kClear, ("_VelOut", velB), ("_ScalOut", scalB), ("_PressOut", pB)); Dispatch3(kClear);
            popAt = Time.time + Random.Range(2f, 5f);
            stepAcc = (seed & 1) * StepDt * 0.5f;
            // Where the fire's light and smoke shadow can reach: its footprint plus a glow radius, stretched away from
            // the sun by the shadow of the plume.
            var size = vox.Size;
            float reach = 2.2f + 0.3f * Mathf.Max(size.x, size.z);
            var min = new Vector2(vox.Origin.x - reach, vox.Origin.z - reach);
            var max = new Vector2(vox.Origin.x + size.x + reach, vox.Origin.z + size.z + reach);
            var sun = SunDir();
            var shadow = new Vector2(-sun.x, -sun.z) * (size.y / Mathf.Max(0.35f, sun.y));
            min = Vector2.Min(min, min + shadow); max = Vector2.Max(max, max + shadow);
            LightRect = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        RenderTexture Field(RenderTextureFormat fmt)
        {
            var rt = new RenderTexture(Vox.Dim.x, Vox.Dim.y, 0, fmt)
            {
                dimension = TextureDimension.Tex3D, volumeDepth = Vox.Dim.z, enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, useMipMap = false, name = "fire_field",
            };
            rt.Create();
            return rt;
        }

        static Vector3 SunDir()
        {
            var sun = RenderSettings.sun;
            return sun != null ? -sun.transform.forward : new Vector3(-0.4f, 0.8f, -0.3f).normalized;
        }

        /// <summary>
        /// One frame of this building's fire. `smoke` and `fire` are the damage stages (0..1: smouldering, then flames that
        /// spread from patch to patch until the whole roof and every opening burns); `ext` is how hard the fire is being
        /// fought right now (0..1) and `extAt` where. `audience` is the render layers to draw on (null: every camera).
        /// </summary>
        public void Tick(float smoke, float fire, float ext, Vector3 extAt, List<int> audience)
        {
            lastTick = Time.time;
            if (!Ready()) return;
            smokeK = smoke; fireK = fire; extK = ext; extPos = extAt;
            if (audience == null) layers = null;
            else { layerBuf.Clear(); layerBuf.AddRange(audience); layers = layerBuf; }
            tickFrame = Time.frameCount;
            idle = smoke <= 0f && fire <= 0f ? idle + Time.deltaTime : 0f;
            float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
            if (dt <= 0f) { Draw(); return; }
            stepAcc += dt;
            if (stepAcc >= StepDt) { stepAcc = Mathf.Min(stepAcc - StepDt, StepDt); if (!dbgNoSim) Step(StepDt); }
            if (!dbgNoSparks) SparksAndPops(dt);
            if (!dbgNoPlume) Plume(dt);
            Draw();
        }

        /// <summary>The fire is out and its smoke has cleared: the caller can release it.</summary>
        public bool Spent => idle > 7f;

        /// <summary>Released (by its owner, for going unshown too long, or because its grid couldn't be built).</summary>
        public bool Dead => released;

        void Common(float dt)
        {
            var d = Vox.Dim;
            cs.SetInts("_Dim", d.x, d.y, d.z);
            cs.SetVector("_DimF", new Vector4(d.x, d.y, d.z, 0));
            cs.SetFloat("_H", Vox.H);
            cs.SetFloat("_Dt", dt);
            cs.SetFloat("_SimTime", Time.time);
            cs.SetFloat("_Seed", (seed % 97) * 1.37f);
            cs.SetVector("_Origin", Vox.Origin);
            cs.SetVector("_Wind", FxSystems.Wind);
            cs.SetFloat("_Fire", fireK);
            cs.SetFloat("_Smoulder", smokeK);
            cs.SetFloat("_Coverage", fireK > 0f ? 0.14f + 0.86f * Mathf.Pow(fireK, 0.75f) : 0f);
            cs.SetFloat("_Ext", extK);
            cs.SetVector("_ExtPos", extPos);
            cs.SetFloat("_ExtRadius", 0.7f + 0.35f * Mathf.Max(Vox.Body.size.x, Vox.Body.size.z));
            cs.SetFloat("_RoofY", Vox.RoofY);
            cs.SetFloat("_Buoy", 4.0f);
            cs.SetFloat("_Vort", 5.5f);
            cs.SetFloat("_PopK", 0f);
        }

        void Bind(int k, params (string name, Texture tex)[] texs)
        {
            foreach (var (n, t) in texs) cs.SetTexture(k, n, t);
        }

        void Dispatch3(int k) => cs.Dispatch(k, Vox.Dim.x / 4, Vox.Dim.y / 4, Vox.Dim.z / 4);

        void Step(float dt)
        {
            Common(dt);
            if (pendingPop.w > 0f) { cs.SetVector("_PopPos", pendingPop); cs.SetFloat("_PopK", pendingPop.w); pendingPop.w = 0f; }
            Bind(kAdvect, ("_Vel", velA), ("_Scal", scalA), ("_Sdf", sdfTex), ("_Emit", emitTex), ("_VelOut", velB), ("_ScalOut", scalB));
            Dispatch3(kAdvect);
            (velA, velB) = (velB, velA); (scalA, scalB) = (scalB, scalA);
            cs.SetFloat("_PopK", 0f);

            Bind(kCurl, ("_Vel", velA), ("_CurlOut", curl));
            Dispatch3(kCurl);
            Bind(kConfine, ("_Vel", velA), ("_Curl", curl), ("_Scal", scalA), ("_Sdf", sdfTex), ("_VelOut", velB));
            Dispatch3(kConfine);
            (velA, velB) = (velB, velA);

            Bind(kDiv, ("_Vel", velA), ("_Sdf", sdfTex), ("_DivOut", div));
            Dispatch3(kDiv);
            for (int i = 0; i < JacobiIters; i++)
            {
                Bind(kJacobi, ("_Press", pA), ("_Div", div), ("_Sdf", sdfTex), ("_PressOut", pB));
                Dispatch3(kJacobi);
                (pA, pB) = (pB, pA);
            }
            Bind(kProject, ("_Vel", velA), ("_Press", pA), ("_Sdf", sdfTex), ("_VelOut", velB));
            Dispatch3(kProject);
            (velA, velB) = (velB, velA);
        }

        // ---------------------------------------------------------------- sparks, pops

        Vector4 pendingPop;
        (Vector3 at, Vector3 nrm, int n) pendingBurst;

        void Spawn(int count, Vector3 at, float radius, Vector3 vel, float speed, float couple, float gravity, float size, float life)
        {
            if (count <= 0) return;
            count = Mathf.Min(count, Sparks);
            cs.SetInt("_SparkCount", Sparks);
            cs.SetInt("_SpawnHead", sparkHead);
            cs.SetInt("_SpawnCount", count);
            cs.SetVector("_SpawnPos", at);
            cs.SetVector("_SpawnVel", vel);
            cs.SetFloat("_SpawnRadius", radius);
            cs.SetFloat("_SpawnSpeed", speed);
            cs.SetFloat("_SpawnCouple", couple);
            cs.SetFloat("_SpawnGravity", gravity);
            cs.SetFloat("_SpawnSize", size);
            cs.SetFloat("_SpawnLife", life);
            cs.SetBuffer(kSpawn, "_Sparks", sparks);
            cs.Dispatch(kSpawn, (count + 63) / 64, 1, 1);
            sparkHead = (sparkHead + count) % Sparks;
        }

        /// <summary>A spot the fire is burning at now: an opening once it's licking out of them, else a roof spot.</summary>
        Vector3 BurningSpot(out Vector3 normal)
        {
            var o = Vox.Openings;
            if (o.Count > 0 && fireK > 0.25f && Random.value < 0.55f)
            {
                var w = o[Random.Range(0, o.Count)];
                normal = w.normal;
                return w.pos + w.normal * 0.08f;
            }
            normal = Vector3.up;
            return Vox.RoofSpots[Random.Range(0, Vox.RoofSpots.Count)];
        }

        void SparksAndPops(float dt)
        {
            float sup = 1f - 0.85f * extK;
            if (pendingBurst.n > 0) { Spawn(pendingBurst.n, pendingBurst.at, 0.2f, Vector3.up * 1.6f + pendingBurst.nrm * 0.8f, 2f, 0.9f, 5f, 0.04f, 1.5f); pendingBurst.n = 0; }
            // Embers: the odd one off a smoulder, a steady stream off a big fire, carried by the flow.
            emberAcc += dt * (0.6f * smokeK + 24f * fireK) * sup;
            int n = (int)emberAcc;
            if (n > 0)
            {
                emberAcc -= n;
                var at = BurningSpot(out var nrm);
                Spawn(n, at, 0.25f, Vector3.up * 1.1f + nrm * 0.5f, 0.6f, 3.2f, 1.1f, 0.035f, 1.7f);
            }
            // Pops: now and then a pocket of gas flares and throws a burst of sparks (more often the bigger the fire).
            if (fireK > 0.2f && Time.time >= popAt)
            {
                popAt = Time.time + Random.Range(2.5f, 7f) / (0.5f + fireK);
                if (extK < 0.6f)
                {
                    var at = BurningSpot(out var nrm);
                    int burst = Mathf.RoundToInt(Random.Range(20f, 45f) * (0.6f + 0.6f * fireK));
                    Spawn(burst, at, 0.15f, Vector3.up * 2.4f + nrm * 1.4f, 3.2f, 0.5f, 6.5f, 0.05f, 1.3f);
                    pendingBurst = (at, nrm, burst / 2); // a second, lazier wave next frame, so it's a spray, not a starburst
                    pendingPop = new Vector4(at.x, at.y, at.z, 0.8f + 0.6f * fireK);
                }
            }
            cs.SetInt("_SparkCount", Sparks);
            cs.SetBuffer(kStep, "_Sparks", sparks);
            Bind(kStep, ("_Vel", velA), ("_Sdf", sdfTex));
            cs.Dispatch(kStep, (Sparks + 63) / 64, 1, 1);
        }

        // ---------------------------------------------------------------- the plume above the grid

        void Plume(float dt)
        {
            if (!readPending && !dbgNoRead && Time.time >= outflowAt)
            {
                outflowAt = Time.time + 0.2f;
                readPending = true;
                cs.SetBuffer(kOutflow, "_Outflow", outflow);
                Bind(kOutflow, ("_Vel", velA), ("_Scal", scalA));
                cs.Dispatch(kOutflow, 1, 1, 1);
                AsyncGPUReadback.Request(outflow, req =>
                {
                    readPending = false;
                    if (released || req.hasError) return;
                    req.GetData<Vector4>().CopyTo(outflowData);
                    hasOutflow = true;
                });
            }
            if (!hasOutflow) return;
            var size = Vox.Size;
            float y = Vox.Origin.y + size.y * 0.76f;
            var prev = Fx.Audience;
            Fx.Audience = layers;
            for (int i = 0; i < 16; i++)
            {
                var o = outflowData[i];
                float flux = o.x + o.y;
                plumeAcc[i] += dt * flux * 26f;
                if (plumeAcc[i] < 1f) continue;
                plumeAcc[i] = Mathf.Min(plumeAcc[i] - 1f, 2f);
                float pale = o.y / Mathf.Max(1e-4f, flux);
                var at = new Vector3(Vox.Origin.x + ((i & 3) + Random.value) * size.x / 4f, y + Random.Range(-0.1f, 0.1f), Vox.Origin.z + ((i >> 2) + Random.value) * size.z / 4f);
                var vel = new Vector3(o.z, Random.Range(0.7f, 1.1f), o.w);
                Fx.Plume(at, vel, Random.Range(0.4f, 0.6f) * Mathf.Sqrt(Mathf.Max(size.x, size.z) * 0.6f), pale, Mathf.Clamp01(flux * 5f));
            }
            Fx.Audience = prev;
        }

        // ---------------------------------------------------------------- drawing

        void Draw()
        {
            var size = Vox.Size;
            volProps.SetTexture("_Scal", scalA);
            volProps.SetTexture("_Sdf", sdfTex);
            volProps.SetTexture("_FireNoise", noise);
            volProps.SetVector("_Origin", Vox.Origin);
            volProps.SetVector("_Size", size);
            volProps.SetVector("_DimF", new Vector4(Vox.Dim.x, Vox.Dim.y, Vox.Dim.z, 0));
            volProps.SetFloat("_FireTime", Time.time);
            sparkProps.SetBuffer("_Sparks", sparks);
            var m = Matrix4x4.TRS(Vox.Center, Quaternion.identity, size);
            var bounds = new Bounds(Vox.Center, size + Vector3.one * 6f);
            if (layers == null) DrawOn(0, m, bounds);
            else foreach (var l in layers) DrawOn(l, m, bounds);
        }

        void DrawOn(int layer, Matrix4x4 m, Bounds bounds)
        {
            if (!dbgNoVol) Graphics.DrawMesh(cube, m, volumeMat, layer, null, 0, volProps, ShadowCastingMode.Off, false, null, LightProbeUsage.Off);
            Graphics.DrawProcedural(sparkMat, bounds, MeshTopology.Triangles, Sparks * 6, 1, null, sparkProps, ShadowCastingMode.Off, false, layer);
        }

        public void Release()
        {
            if (released) return;
            released = true;
            live.Remove(this);
            if (Vox == null) return; // still voxelizing, or failed: no GPU state yet
            foreach (var rt in new[] { velA, velB, scalA, scalB, pA, pB, div, curl }) if (rt != null) rt.Release();
            Object.Destroy(sdfTex); Object.Destroy(emitTex);
            sparks.Release(); outflow.Release(); lights.Release();
        }

        // ---------------------------------------------------------------- the light map (every fire, once a frame)

        static RenderTexture mapA, mapB;
        static Vector2 mapMin = new Vector2(-16f, -16f), mapSize = new Vector2(160f, 160f);
        const float MapTexel = 0.5f;
        static readonly List<RectInt> dirty = new List<RectInt>();
        static readonly Vector4[] rects = new Vector4[MaxLive];
        static readonly float[] vis = new float[MaxLive];
        static int mainLayer = -1;
        static readonly int MapId = Shader.PropertyToID("_PezFireMap"), MapStId = Shader.PropertyToID("_PezFireMapST"),
            OnId = Shader.PropertyToID("_PezFireOn"), RectsId = Shader.PropertyToID("_PezFireRects"), VisId = Shader.PropertyToID("_PezFireVis");

        /// <summary>The world area the fire light map covers (the map, in tiles): WorldView sets it when a game starts.</summary>
        public static void SetMapArea(Vector2 min, Vector2 size, int mainViewLayer)
        {
            mainLayer = mainViewLayer;
            if (mapA != null && (min != mapMin || size != mapSize)) { mapA.Release(); mapB.Release(); mapA = mapB = null; dirty.Clear(); }
            mapMin = min; mapSize = size;
        }

        static RenderTexture MapRT()
        {
            var rt = new RenderTexture(Mathf.CeilToInt(mapSize.x / MapTexel), Mathf.CeilToInt(mapSize.y / MapTexel), 0, RenderTextureFormat.ARGBHalf)
            { enableRandomWrite = true, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "fire_light_map" };
            rt.Create();
            Graphics.SetRenderTarget(rt); GL.Clear(false, true, Color.clear); Graphics.SetRenderTarget(null);
            return rt;
        }

        RectInt TexelRect()
        {
            var r = LightRect;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((r.xMin - mapMin.x) / MapTexel), 0, mapA.width), y0 = Mathf.Clamp(Mathf.FloorToInt((r.yMin - mapMin.y) / MapTexel), 0, mapA.height);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((r.xMax - mapMin.x) / MapTexel), 0, mapA.width), y1 = Mathf.Clamp(Mathf.CeilToInt((r.yMax - mapMin.y) / MapTexel), 0, mapA.height);
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        static void Compose()
        {
            // A fire whose building has gone unshown for a while gives its place in the budget back.
            for (int i = live.Count - 1; i >= 0; i--) if (Time.time - live[i].lastTick > 5f) live[i].Release();
            if (live.Count == 0 && dirty.Count == 0 || dbgNoLight) return;
            if (mapA == null) { mapA = MapRT(); mapB = MapRT(); }
            cs.SetTexture(kClearMap, "_MapOut", mapA);
            foreach (var r in dirty) ClearRect(r);
            dirty.Clear();
            var sun = SunDir();
            cs.SetVector("_SunDir", sun);
            cs.SetVector("_MapOrigin", mapMin);
            cs.SetFloat("_MapTexel", MapTexel);
            cs.SetFloat("_LightGain", 3f);
            for (int i = 0; i < MaxLive; i++) rects[i] = new Vector4(1e6f, 1e6f, -1e6f, -1e6f);
            for (int i = 0; i < live.Count; i++)
            {
                var f = live[i];
                if (f.Vox == null || f.tickFrame != Time.frameCount) continue; // not built yet, or not shown this frame
                var r = f.TexelRect();
                if (r.width <= 0 || r.height <= 0) continue;
                f.Common(0f);
                cs.SetBuffer(kReduce, "_LightsOut", f.lights);
                cs.SetTexture(kReduce, "_Scal", f.scalA);
                cs.Dispatch(kReduce, 1, 1, 1);
                cs.SetBuffer(kSplat, "_Lights", f.lights);
                cs.SetTexture(kSplat, "_Scal", f.scalA);
                cs.SetTexture(kSplat, "_Map", mapA);
                cs.SetTexture(kSplat, "_MapOut", mapB);
                cs.SetInts("_RectMin", r.x, r.y);
                cs.SetInts("_RectSize", r.width, r.height);
                cs.Dispatch(kSplat, (r.width + 7) / 8, (r.height + 7) / 8, 1);
                Graphics.CopyTexture(mapB, 0, 0, r.x, r.y, r.width, r.height, mapA, 0, 0, r.x, r.y);
                dirty.Add(r);
                var lr = f.LightRect;
                rects[i] = new Vector4(lr.xMin, lr.yMin, lr.xMax, lr.yMax);
            }
            Shader.SetGlobalTexture(MapId, mapA);
            Shader.SetGlobalVector(MapStId, new Vector4(1f / mapSize.x, 1f / mapSize.y, -mapMin.x / mapSize.x, -mapMin.y / mapSize.y));
            Shader.SetGlobalFloat(OnId, live.Count > 0 ? 1f : 0f);
            Shader.SetGlobalVectorArray(RectsId, rects);
            SetPovLayer(mainLayer);
        }

        static void ClearRect(RectInt r)
        {
            cs.SetInts("_RectMin", r.x, r.y);
            cs.SetInts("_RectSize", r.width, r.height);
            cs.Dispatch(kClearMap, (r.width + 7) / 8, (r.height + 7) / 8, 1);
        }

        /// <summary>
        /// Before a camera renders: which fires' light it may show (its layer is in the fire's audience). A stream render
        /// for a player who can't see a burning building gets no glow from it on the ground either.
        /// </summary>
        public static void SetPovLayer(int layer)
        {
            if (!ok) return;
            for (int i = 0; i < MaxLive; i++)
            {
                var ls = i < live.Count ? live[i].layers : null;
                vis[i] = layer < 0 || ls == null || ls.Contains(layer) ? 1f : 0f;
            }
            Shader.SetGlobalFloatArray(VisId, vis);
        }

        /// <summary>Runs after every Update (so after this frame's fires have stepped): builds the light map.</summary>
        class Host : MonoBehaviour
        {
            void LateUpdate() => Compose();
            void OnDestroy() { if (mapA != null) { mapA.Release(); mapB.Release(); } }
        }
    }
}

namespace Pez.View
{
    /// <summary>
    /// What a fire does to its building's own surfaces: the openings (dark recesses, doors, vents, glass:
    /// FireVoxels.IsOpening) glow with the fire inside, and the rest chars darker as it burns, so the flames have
    /// something dark to read against and the building reads as burning, not lit. Team colour and the power glows are
    /// left alone (identity and state stay readable). Recovers as the fire is put out.
    /// </summary>
    public class FireShade
    {
        readonly Renderer[] openings, body;
        public float Burn { get; private set; } = -1f;
        public float Char { get; private set; } = 1f;

        public FireShade(GameObject model)
        {
            var o = new List<Renderer>(); var b = new List<Renderer>();
            foreach (var r in model.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer || r.sharedMaterial == null) continue;
                var mat = r.sharedMaterial.name;
                if (FireVoxels.IsOpening(mat, r.gameObject.name)) o.Add(r);
                else if (!mat.StartsWith("M_Team") && !mat.StartsWith("M_E_")) b.Add(r);
            }
            openings = o.ToArray(); body = b.ToArray();
        }

        /// <summary>`burn` lights the openings (0..1); `dim` is the char (1 clean, about 0.5 badly burnt).</summary>
        public void Set(float burn, float dim)
        {
            if (Mathf.Abs(burn - Burn) >= 0.01f) { PezShade.SetBurn(openings, burn); Burn = burn; }
            if (Mathf.Abs(dim - Char) >= 0.01f) { PezShade.SetDim(body, dim); Char = dim; }
        }

        /// <summary>For one render only (a fogged view of a remembered building): clean and unlit, or back to the live state.</summary>
        public void Show(bool live)
        {
            PezShade.SetBurn(openings, live ? Burn : 0f);
            PezShade.SetDim(body, live ? Char : 1f);
        }
    }
}
