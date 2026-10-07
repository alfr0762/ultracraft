// UltraBridge: runs the real ULTRAKILL as the "V1 layer" of Minecraft.
//
// - Minecraft (Fabric mod "ultracraft") connects over TCP on 127.0.0.1:27110.
// - Minecraft sends: its window size, keyboard/mouse input, the solid blocks around the player
//   (as boxes), the mobs around the player, and damage the player takes.
// - ULTRAKILL plays V1 for real (its own movement, guns, arm, HUD, style, blood) in an emptied
//   Sandbox scene whose only geometry is colliders built from Minecraft's blocks; mobs are invisible
//   proxy enemies with real EnemyIdentifiers so weapons, coins and auto-aim treat them as enemies.
// - We send back: V1's camera pose (in Minecraft coordinates), damage dealt to each mob, explosions,
//   and every rendered frame (V1's view with an alpha mask) through a shared memory-mapped file.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.Rendering;
using ULTRAKILL.Enemy;
using ULTRAKILL.Portal;

namespace UltraBridge
{
    [BepInPlugin("dev.ultracraft.ultrabridge", "UltraBridge", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Application.runInBackground = true;
            new Harmony("dev.ultracraft.ultrabridge").PatchAll(typeof(Plugin).Assembly);
            MpFxPatches.Apply();
            MobKnock.Apply();
            var go = new GameObject("UltraBridge");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Bridge>();
            Log.LogInfo("UltraBridge loaded");
        }
    }

    /// <summary>Marks an EnemyIdentifier as a stand-in for a Minecraft mob, and is its entry in ULTRAKILL's
    /// TargetTracker (what coins, Vision and auto-aim search), the way Enemy.Start registers real enemies.</summary>
    public class McProxy : MonoBehaviour, ITarget
    {
        public int id;
        public string type;
        public EnemyIdentifier eid;
        public GameObject head;
        public BoxCollider body;
        public Rigidbody rb;
        public float hp, maxHp;
        public Vector3 velocity;
        public float lastSeen;
        // where the mob is on Minecraft's last tick, and when Minecraft last said where it draws it (EPOS)
        public Vector3 tickPos;
        public float drawnAt;
        // a hostile Minecraft mob (zombie, skeleton, creeper...): ULTRAKILL's enemies go for it
        public bool hostile;
        // mid-body point enemies aim at (EnemyTarget.position uses the override centre)
        public Transform center;
        // a Minecraft melee hit waits this long for a parry before it lands
        public float parryUntil;
        public int pendingHurt;
        // the knockback that comes with that hit (ULTRAKILL units per second), dropped if it's parried
        public Vector3 pendingKnock;
        public float pendingKnockUp = -1f;
        public bool hasPendingKnock;
        public float parriedAt = -10f;
        // when V1's fist last landed on it: a punch just before its swing still parries
        public float punchedAt = -10f;
        public readonly CancellationTokenSource life = new CancellationTokenSource();
        // another player who is V1 ("v1"): which way they face (Minecraft's yaw), and V1's body standing for them
        public float yaw;
        // on fire in Minecraft: ULTRAKILL's own fire on it (Minecraft's flames aren't drawn over V1's view)
        public GameObject fire;
        public Transform v1Body;
        public Animator v1Anim;
        // how that player's own ULTRAKILL has their V1 (RV1): on the ground, sliding, looking up or down, the gun out
        public bool remoteKnown, remoteGrounded, remoteSliding;
        public float pitch;
        public string weaponName;
        public GameObject gun;
        public Transform hand;
        // how ULTRAKILL turns that gun in V1's hands (its prefab's own turn under the camera)
        public Quaternion gunTurn = Quaternion.identity;
        // where that gun sits from the eye, as ULTRAKILL lays it out in first person (scaled to the body)
        public Vector3 gunFromEye;
        // the gun's middle in its own space, and the right arm's bones that hold it out
        public Vector3 gunCenter;
        public float gunLength;
        public Transform upperArm, foreArm;
        Vector3 cachedPos, cachedHead;

        public int Id => GetInstanceID();
        public TargetType Type => TargetType.ENEMY;
        // spelled out: net472 can't lean on the interface's default implementations
        bool ITarget.isPlayer => false;
        bool ITarget.isEnemy => true;
        public EnemyIdentifier EID => eid;
        public GameObject GameObject => gameObject;
        public Rigidbody Rigidbody => rb;
        public Transform Transform => transform;
        public Vector3 Position => cachedPos;
        public Vector3 HeadPosition => cachedHead;

        public void UpdateCachedTransformData()
        {
            cachedPos = body != null ? body.bounds.center : transform.position;
            cachedHead = head != null ? head.transform.position : cachedPos;
        }

        public void SetData(ref TargetData data)
        {
            data.position = cachedPos;
            data.realPosition = cachedPos;
            data.headPosition = cachedHead;
            data.realHeadPosition = cachedHead;
            data.rotation = transform.rotation;
            data.velocity = velocity;
        }

        void OnDestroy()
        {
            life.Cancel();
        }
    }

    /// <summary>A Minecraft projectile (arrow, fireball, trident, shulker bullet...) flying at V1. On ULTRAKILL's
    /// projectile layer with a ParryReceiver, so the Feedbacker parries it with the real parry; ULTRAKILL also decides
    /// when it hits V1 (dodge i-frames included), and Minecraft then applies the hit.</summary>
    public class McProjectile : MonoBehaviour
    {
        public int id;
        public string type;
        public Vector3 lastPos, velocity;
        public float lastTime, radius;
        /// <summary>It reached V1 and hangs there for a moment (Minecraft holds it still): a punch now parries it.</summary>
        public bool held;
        public float heldUntil;
        Vector3 prevPos;

        /// <summary>Minecraft moves projectiles 20 times a second; carry them along in between so the parry
        /// window and the hit test see where they really are.</summary>
        public Vector3 Advance()
        {
            prevPos = transform.position;
            if (held) return prevPos;
            float dt = Mathf.Min(Time.time - lastTime, 0.1f);
            transform.position = lastPos + velocity * dt;
            return prevPos;
        }
    }

    internal static class Net
    {
        public static readonly ConcurrentQueue<string> Inbox = new ConcurrentQueue<string>();
        static TcpListener listener;
        static StreamWriter writer;
        static readonly object wlock = new object();
        public static volatile bool Connected;
        /// <summary>Input lines (IN) received so far.</summary>
        public static int InputCount;

        public static void Start(int port)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        var c = listener.AcceptTcpClient();
                        c.NoDelay = true;
                        var s = c.GetStream();
                        lock (wlock) writer = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
                        Connected = true;
                        Inbox.Enqueue("CONNECTED");
                        var r = new StreamReader(s, Encoding.UTF8);
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            // a clock ping is answered at once, from here (Minecraft lines its clock up with the frames' stamps)
                            if (line.StartsWith("CLOCK "))
                            {
                                Send(line + " " + Bridge.NowMicros());
                                Flush();
                                continue;
                            }
                            if (line.StartsWith("IN ")) System.Threading.Interlocked.Increment(ref InputCount);
                            Inbox.Enqueue(line);
                        }
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("net: " + e.Message); }
                    Connected = false;
                    Inbox.Enqueue("DISCONNECTED");
                }
            }) { IsBackground = true, Name = "UltraBridge net" }.Start();
        }

        public static void Send(string line)
        {
            lock (wlock)
            {
                if (writer == null) return;
                try { writer.WriteLine(line); } catch { writer = null; }
            }
        }

        public static void Flush()
        {
            lock (wlock)
            {
                try { writer?.Flush(); } catch { writer = null; }
            }
        }
    }

    public partial class Bridge : MonoBehaviour
    {
        public static Bridge I;
        /// <summary>ULTRAKILL units per Minecraft block: V1's 3.5 u capsule stands for the 1.8-block player.</summary>
        public const float K = 3.5f / 1.8f;

        /// <summary>ULTRAKILL's frame rate cap (OPTS fps; Ultracraft's settings).</summary>
        public static int FpsCap = 120;
        /// <summary>Low-Latency Frames: each frame's readback is finished as soon as it's drawn (instead of a frame or
        /// two later in the background), so Minecraft shows it that much sooner.</summary>
        public static bool LowLatency = true;

        /// <summary>Microseconds on Windows' performance counter (Minecraft's System.nanoTime runs on the same one).</summary>
        internal static int NowMicros() => (int)(long)(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency * 1e6);
        public Vector3 origin; // Minecraft coordinates of the ULTRAKILL world origin
        bool originSet;
        bool levelPrepared;
        public bool LevelReady => levelPrepared;
        bool v1Landed;
        bool tpReceived;
        float nextLandTry;
        bool sawLevelStart;
        string lastScene = "";
        int mcW = 1280, mcH = 720;

        Keyboard kb;
        Mouse mouse;
        static readonly AccessTools.FieldRef<CameraController, bool> CamZooming = AccessTools.FieldRefAccess<CameraController, bool>("zooming");
        readonly HashSet<Key> held = new HashSet<Key>();
        Vector2 mouseDelta;
        float wheel;
        int buttons;

        // far terrain: one MeshCollider per 16x16x16 Minecraft chunk section, so shots and projectiles hit
        // walls far beyond the box colliders kept around V1
        GameObject sectionRoot;
        readonly Dictionary<string, GameObject> sections = new Dictionary<string, GameObject>();
        readonly Queue<string> sectionQueue = new Queue<string>();

        // ULTRAKILL menus (the Spawner Arm's spawn menu, alter menu) free the cursor; Minecraft then shows its own
        // cursor and sends where it is
        bool uiMode;
        Vector2 pointer;
        float nextCanvasCheck;

        GameObject blockRoot;
        readonly Dictionary<string, GameObject> blocks = new Dictionary<string, GameObject>();
        readonly Stack<GameObject> blockPool = new Stack<GameObject>();
        readonly Dictionary<int, McProxy> proxies = new Dictionary<int, McProxy>();

        // Shared frames (%TEMP%/ultracraft_frame2.bin): a 64-byte header {seq, w, h, bottomUp, slot, maskBytesPerPixel,
        // version} then Slots slots of [colour w*h*4][mask w*h*maskBpp]. The GPU readback lands directly in a slot;
        // Minecraft uploads straight from it.
        MemoryMappedFile mmf;
        MemoryMappedViewAccessor view;
        const int HeaderSize = 64;
        const int MaxPixels = 3840 * 2160;
        const int Slots = 4;
        const long SlotBytes = MaxPixels * 8L;
        const int MaxInFlight = 2;
        unsafe byte* frameBase;
        readonly NativeArray<byte>[] colorOut = new NativeArray<byte>[Slots];
        readonly NativeArray<byte>[] maskOut = new NativeArray<byte>[Slots];
        int inFlight, nextSeq, publishedSeq;
        // 4: the whole pre-post-processing target, so Minecraft can add ULTRAKILL's additive light (tracers, flashes,
        // glows) that never writes alpha; 1 would be alpha only
        int maskBpp = 4;
        int lastHp = -1;
        float nextLoadingNote;
        float forceClickUntil;
        internal static readonly bool LaunchedForMinecraft = Array.IndexOf(Environment.GetCommandLineArgs(), "-ultracraft") >= 0;

        /// <summary>-ucinstance N: a second (third...) Ultracraft on the same computer (testing multiplayer alone) uses
        /// its own port (27110 + N - 1) and its own shared files, so the two pairs of games don't cross.</summary>
        public static readonly int Instance = ReadInstance();
        public static string InstanceSuffix => Instance > 1 ? "_" + Instance : "";

        static int ReadInstance()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-ucinstance");
            return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n > 0 ? n : 1;
        }

        void Awake()
        {
            I = this;
            Net.Start(27110 + Instance - 1);
            MapFrames();
            MapDoll();
            Win32.IgnoreTimerThrottling();
            StartCoroutine(Capture());
        }

        unsafe void MapFrames()
        {
            var path = Path.Combine(Path.GetTempPath(), "ultracraft_frame2" + InstanceSuffix + ".bin");
            // never truncate: Minecraft may still have the file mapped from an earlier ULTRAKILL
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            long need = HeaderSize + Slots * SlotBytes;
            if (fs.Length < need) fs.SetLength(need);
            mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
            view = mmf.CreateViewAccessor();
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            frameBase = p;
            for (int s = 0; s < Slots; s++)
            {
                byte* slot = frameBase + HeaderSize + s * SlotBytes;
                colorOut[s] = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(slot, MaxPixels * 4, Allocator.None);
                maskOut[s] = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(slot + MaxPixels * 4L, MaxPixels * 4, Allocator.None);
            }
            ((int*)frameBase)[6] = 2;
        }

        // ------------------------------------------------------------ coordinates

        public Vector3 McToUk(Vector3 m)
        {
            var d = (m - origin) * K;
            return new Vector3(d.x, d.y, -d.z);
        }

        public Vector3 UkToMc(Vector3 u)
        {
            return new Vector3(u.x / K, u.y / K, -u.z / K) + origin;
        }

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        static string S(float f) => f.ToString("0.####", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------ main loop

        // debug FPSINFO: how long ULTRAKILL's recent frames took
        readonly float[] frameTimes = new float[240];
        int frameCount;

        void Update()
        {
            frameTimes[frameCount++ % frameTimes.Length] = Time.unscaledDeltaTime;
            // Low-Latency Frames: the last frame's readback is finished now (the GPU drew it while this frame waited its
            // turn, so this rarely waits), so Minecraft has it at once and sends its input straight back: a moment for
            // that input, so this frame already turns with it
            if (LowLatency && inFlight > 0 && Net.Connected)
            {
                int inputs = Net.InputCount;
                AsyncGPUReadback.WaitAllRequests();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (Net.InputCount == inputs && sw.ElapsedTicks < System.Diagnostics.Stopwatch.Frequency / 500) System.Threading.Thread.SpinWait(64);
            }
            while (Net.Inbox.TryDequeue(out var line))
            {
                try { Handle(line); }
                catch (Exception e) { Plugin.Log.LogWarning("handle '" + Trunc(line) + "': " + e); }
            }

            HoldPause();
            var scene = SceneHelper.CurrentScene ?? "";
            if (scene != lastScene)
            {
                lastScene = scene;
                levelPrepared = false;
                v1Landed = false;
                tpReceived = false;
                sawLevelStart = false;
                uiMode = false;
                sections.Clear();
                sectionQueue.Clear();
                sectionRoot = null;
                projectiles.Clear();
                projectileDone.Clear();
                blocks.Clear();
                blockPool.Clear();
                proxies.Clear();
                blockRoot = null;
                ukEnemies.Clear();
                v1AggroUntil.Clear();
                fluids.Clear();
                lit.Clear();
                ResetWorldExtras();
                foreach (var inst in navInstances) inst.Remove();
                navInstances.Clear();
                navData.Clear();
                navSettings.Clear();
                navBaking = false;
                navDirty = true;
                navBounds = new Bounds();
                dollRoot = null;
                dollFailed = false;
                Plugin.Log.LogInfo("scene: " + scene);
            }
            // a save that hasn't been through the tutorial gets sent there by the intro (IntroViolenceScreen); V1 is
            // needed in the Sandbox all the same (ForMinecraftSkipTutorial keeps the intro from going there at all)
            if ((Net.Connected || LaunchedForMinecraft) && (scene == "Main Menu" || scene == "Intro" || scene == "Tutorial") && Time.unscaledTime > 3f)
            {
                SceneHelper.LoadScene("uk_construct");
            }
            // Never touch MonoSingleton<T>.Instance before the level is ready: a lookup that finds nothing uses up
            // the singleton's one search, and objects whose Awake asks for it later get null forever. That is what
            // left CameraController without V1 (its LateUpdate then bails out, so the view never turns).
            // not ready yet: tell Minecraft where ULTRAKILL is, so a hang says where it is
            if (!levelPrepared && Net.Connected && Time.unscaledTime > nextLoadingNote)
            {
                nextLoadingNote = Time.unscaledTime + 2f;
                Net.Send("LOADING " + scene);
            }
            if (scene == "uk_construct" && !levelPrepared && Time.timeSinceLevelLoad > 1f && LevelStarted())
            {
                PrepareLevel();
            }
            var nm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
            if (nm != null && tpReceived && !v1Landed && Time.unscaledTime > nextLandTry)
            {
                nextLandTry = Time.unscaledTime + 0.5f;
                LandV1(nm);
            }
            BuildSections();
            InjectInput();
            if (nm != null)
            {
                UpdateProjectiles(nm);
                UpdateMelee(nm);
            }
            if (nm != null && levelPrepared && originSet)
            {
                UpdateDrive(nm);
                SendPose(nm);
            }
            gameFrames++;
            if (levelPrepared && Net.Connected)
            {
                if (nm != null && originSet)
                {
                    UpdateUkEnemies(nm);
                    UpdateNavMesh(nm);
                    UnstickEnemies();
                    KeepGroundHonest(nm);
                    ApplyEffects(nm);
                    TrackFall(nm);
                    KeepFluidsSynced();
                    ApplyLight(nm);
                    UpdateWorldExtras(nm);
                }
                KeepHands();
                KeepGunOut();
                UpdateMpSync(nm);
                KeepCheats();
                UpdateMusic();
                UpdateStyleRank();
                UpdateOil();
                UpdateArmExport();
                UpdateFreeze();
                UpdateDoll();
                KeepAudio();
                ClearStrayLocks();
                UpdateUiMode();
                if (Time.unscaledTime > nextCanvasCheck)
                {
                    nextCanvasCheck = Time.unscaledTime + 1f;
                    CaptureOverlayCanvases();
                    NoBakedOcclusion();
                }
            }
            Status();
            // started by the Ultracraft launcher (-ultracraft): stay out of sight from the first frame
            if ((Net.Connected || LaunchedForMinecraft) && Time.unscaledTime > nextPark)
            {
                nextPark = Time.unscaledTime + (Net.Connected ? 2f : 0.25f);
                if (Screen.fullScreenMode != FullScreenMode.Windowed) Screen.SetResolution(mcW, mcH, FullScreenMode.Windowed);
                else Win32.ParkWindow();
            }
            Net.Flush();
        }

        void SendPose(NewMovement nm)
        {
            var cc = MonoSingleton<CameraController>.Instance;
            if (cc == null) return;
            var cam = cc.cam.transform;
            var eye = UkToMc(cam.position);
            var feet = UkToMc(nm.transform.position + Vector3.down * 1.5f);
            var e = cam.eulerAngles;
            float yaw = e.y + 180f;
            float pitch = e.x > 180f ? e.x - 360f : e.x;
            float roll = e.z > 180f ? e.z - 360f : e.z;
            var v = nm.rb.velocity;
            Net.Send("P " + S(eye.x) + " " + S(eye.y) + " " + S(eye.z) + " " + S(yaw) + " " + S(pitch) + " " + S(-roll) + " " + S(cc.cam.fieldOfView)
                     + " " + S(feet.x) + " " + S(feet.y) + " " + S(feet.z) + " " + S(v.x / K) + " " + S(v.y / K) + " " + S(-v.z / K)
                     + " " + (nm.gc != null && nm.gc.onGround ? 1 : 0) + " " + (nm.sliding ? 1 : 0));
            if (nm.hp != lastHp)
            {
                lastHp = nm.hp;
                // HP <hp> <dead> <max>: Minecraft's hearts mirror it (so its own regeneration knows when to heal)
                Net.Send("HP " + nm.hp + " " + (nm.dead ? 1 : 0) + " " + MaxHp(nm));
            }
        }

        void Handle(string line)
        {
            int sp = line.IndexOf(' ');
            string cmd = sp < 0 ? line : line.Substring(0, sp);
            string rest = sp < 0 ? "" : line.Substring(sp + 1);
            switch (cmd)
            {
                case "CONNECTED":
                    Plugin.Log.LogInfo("Minecraft connected");
                    Screen.SetResolution(mcW, mcH, FullScreenMode.Windowed);
                    // a Minecraft that (re)connects after the level loaded still needs to hear it's ready
                    if (levelPrepared)
                    {
                        Net.Send("READY");
                        PrefsOnReady();
                    }
                    uiMode = false;
                    break;
                case "PTR":
                {
                    // PTR <x> <y>: Minecraft's cursor in window pixels (top-left origin) while an ULTRAKILL menu is open
                    var a = rest.Split(' ');
                    pointer = new Vector2(F(a[0]), Screen.height - F(a[1]));
                    break;
                }
                case "SEC":
                    sectionQueue.Enqueue(rest);
                    break;
                case "PROJS":
                    ApplyProjectiles(rest);
                    break;
                case "MELEE":
                {
                    // MELEE <mob id> <ULTRAKILL damage>
                    var a = rest.Split(' ');
                    if (levelPrepared) MeleeHit(int.Parse(a[0]), Mathf.Max(1, Mathf.RoundToInt(F(a[1]))));
                    break;
                }
                case "SECX":
                    RemoveSection(rest.Replace(' ', ','));
                    break;
                case "SECCLR":
                    ClearSections();
                    break;
                case "TESTCOIN":
                    StartCoroutine(TestCoin());
                    break;
                case "RAY":
                {
                    // debug: what V1's crosshair ray hits (enemies + environment), the way weapons cast
                    var cc = MonoSingleton<CameraController>.Instance;
                    if (cc == null) break;
                    var o = cc.GetDefaultPos();
                    var dir = cc.transform.forward;
                    var hits = Physics.RaycastAll(o, dir, 500f, LayerMaskDefaults.Get(LMD.EnemiesAndEnvironment), QueryTriggerInteraction.Collide);
                    var sb = new StringBuilder("RAY from " + o + " dir " + dir + ": ");
                    foreach (var h in hits) sb.Append(h.collider.name).Append("[L").Append(h.collider.gameObject.layer).Append(' ').Append(h.collider.tag).Append("] @").Append(h.distance.ToString("0.0")).Append("  ");
                    Plugin.Log.LogInfo(sb.ToString());
                    Net.Send(sb.ToString());
                    break;
                }
                case "DISCONNECTED":
                    Plugin.Log.LogInfo("Minecraft disconnected");
                    SetMcPaused(false);
                    ClearFluids();
                    WorldExtrasDisconnected();
                    fxSpeed = fxSlow = fxJump = fxSlowFall = fxLevitate = -1;
                    SetMcZoom(1f);
                    held.Clear();
                    buttons = 0;
                    // let go of everything, or V1 keeps running on the last keys Minecraft held
                    if (kb != null) InputSystem.QueueStateEvent(kb, new KeyboardState());
                    if (mouse != null) InputSystem.QueueStateEvent(mouse, new MouseState());
                    break;
                case "SIZE":
                {
                    var a = rest.Split(' ');
                    int w = int.Parse(a[0]), h = int.Parse(a[1]);
                    if (w > 0 && h > 0 && (w != mcW || h != mcH || Screen.width != w || Screen.height != h))
                    {
                        mcW = Math.Min(w, 3840);
                        mcH = Math.Min(h, 2160);
                        Screen.SetResolution(mcW, mcH, FullScreenMode.Windowed);
                    }
                    break;
                }
                case "IN":
                {
                    // IN <dx> <dy> <wheel> <buttons> <glfw keys...>
                    var a = rest.Split(' ');
                    mouseDelta += new Vector2(F(a[0]), -F(a[1]));
                    wheel += F(a[2]);
                    buttons = int.Parse(a[3]);
                    held.Clear();
                    for (int i = 4; i < a.Length; i++)
                    {
                        if (a[i].Length == 0) continue;
                        var k = GlfwToKey(int.Parse(a[i]));
                        if (k != Key.None) held.Add(k);
                    }
                    break;
                }
                case "ORIGIN":
                {
                    var a = rest.Split(' ');
                    var o = new Vector3(F(a[0]), F(a[1]), F(a[2]));
                    // terrain built around another origin would sit in the wrong place
                    if (originSet && o != origin)
                    {
                        ClearSections();
                        // "keep": the same world, only the origin moved along with V1 (floats stay precise near it), so
                        // its enemies and bosses move with it instead of going
                        bool keep = a.Length > 3 && a[3] == "keep";
                        if (keep) ShiftActors(new Vector3(origin.x - o.x, origin.y - o.y, -(origin.z - o.z)) * K);
                        OriginMoved(!keep);
                    }
                    origin = o;
                    originSet = true;
                    break;
                }
                case "TP":
                {
                    // TP x y z yaw pitch (feet, Minecraft coordinates)
                    var a = rest.Split(' ');
                    if (!levelPrepared) break;
                    var nm = MonoSingleton<NewMovement>.Instance;
                    if (nm == null) break;
                    var p = McToUk(new Vector3(F(a[0]), F(a[1]), F(a[2]))) + Vector3.up * 1.5f;
                    // Minecraft put V1 down somewhere (a pearl, a bed, off a horse): it's no longer carried
                    StopCarry();
                    nm.transform.position = p;
                    nm.rb.position = p;
                    // a teleport is no fall
                    fallPeak = p.y;
                    nm.rb.isKinematic = false;
                    nm.rb.velocity = Vector3.zero;
                    HoldAfterTeleport(p);
                    // the Sandbox's start trigger is gone, so switch V1 on ourselves (movement, camera, guns, fists, HUD)
                    tpReceived = true;
                    if (!v1Landed) LandV1(nm);
                    var cc = MonoSingleton<CameraController>.Instance;
                    if (cc != null && a.Length >= 5)
                    {
                        // Minecraft pitch is positive looking down, CameraController.rotationX positive looking up
                        cc.rotationY = F(a[3]) - 180f;
                        cc.rotationX = -F(a[4]);
                        cc.ApplyRotations();
                    }
                    break;
                }
                case "BLOCKS":
                    ApplyBlocks(rest);
                    break;
                case "ENTS":
                    ApplyEntities(rest);
                    break;
                case "EPOS":
                    ApplyDrawnPositions(rest);
                    break;
                case "KILL":
                {
                    if (levelPrepared && proxies.TryGetValue(int.Parse(rest), out var p) && p != null)
                    {
                        MonoSingleton<StyleHUD>.Instance?.AddPoints(50, "ultrakill.kill", null, p.eid);
                    }
                    break;
                }
                case "DIED":
                {
                    // DIED id x y z w h: a Minecraft mob died (to anything): it bursts like an ULTRAKILL enemy
                    var a = rest.Split(' ');
                    if (!levelPrepared || !originSet || a.Length < 6) break;
                    proxies.TryGetValue(int.Parse(a[0]), out var p);
                    var feet = McToUk(new Vector3(F(a[1]), F(a[2]), F(a[3])));
                    DeathGore(p, feet + Vector3.up * F(a[5]) * K * 0.5f, F(a[4]) * K, F(a[5]) * K);
                    break;
                }
                case "BLEED":
                {
                    // BLEED id damage: hurt by something other than V1 (another mob, fire, a fall...): blood all the same
                    var a = rest.Split(' ');
                    if (levelPrepared && proxies.TryGetValue(int.Parse(a[0]), out var p) && p != null)
                        SpawnGore(p.eid, F(a[1]) >= 4f ? GoreType.Body : GoreType.Small, p.center.position, false, -1);
                    break;
                }
                case "EHURT":
                {
                    // EHURT <ULTRAKILL enemy id> <Minecraft damage> <attacker: mob id, V1, V1:<teammate's id> or -1> [fire]:
                    // something in Minecraft hurt one of ULTRAKILL's enemies (a mob, a V1's Minecraft weapon, lava,
                    // fire, cactus...)
                    var a = rest.Split(' ');
                    if (!levelPrepared) break;
                    string by = a.Length > 2 ? a[2] : "-1";
                    int mobId = int.TryParse(by, out var m) ? m : -1;
                    int mate = by.StartsWith("V1:") && int.TryParse(by.Substring(3), out var mm) ? mm : 0;
                    EnemyHurtByMob(int.Parse(a[0]), F(a[1]), mobId, by == "V1" || mate != 0, a.Length > 3 ? a[3] : null, mate);
                    break;
                }
                case "FLUID":
                    ApplyFluid(rest);
                    break;
                case "HEAL":
                {
                    // HEAL <Minecraft health>: Minecraft healed the player (a full stomach's regeneration, potions,
                    // golden apples, beacons): V1 heals the same, 5 HP per half heart (hard damage still caps it)
                    var hnm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
                    if (hnm == null || hnm.dead) break;
                    healCarry += F(rest) * 5f;
                    int n = Mathf.FloorToInt(healCarry);
                    if (n >= 1)
                    {
                        healCarry -= n;
                        hnm.GetHealth(n, true);
                    }
                    break;
                }
                case "ZOOM":
                    SetMcZoom(F(rest));
                    break;
                case "EFFECTS":
                {
                    // EFFECTS speed slowness jumpBoost slowFalling levitation: amplifiers, -1 for none
                    var a = rest.Split(' ');
                    if (a.Length < 5) break;
                    fxSpeed = int.Parse(a[0]);
                    fxSlow = int.Parse(a[1]);
                    fxJump = int.Parse(a[2]);
                    fxSlowFall = int.Parse(a[3]);
                    fxLevitate = int.Parse(a[4]);
                    break;
                }
                case "LIGHT":
                    ApplyLightLevels(rest);
                    break;
                case "LIGHTINFO":
                    LightInfo();
                    break;
                case "MOVEINFO":
                {
                    // debug: V1's fall and slam state, and which fist is out
                    var mnm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
                    if (mnm == null) break;
                    var tr = Traverse.Create(mnm);
                    var fc = MonoSingleton<FistControl>.Instance;
                    Net.Send("MOVEINFO ground=" + mnm.gc.onGround + " heavyFall=" + mnm.gc.heavyFall + " falling=" + mnm.falling
                             + " fallTime=" + tr.Field("fallTime").GetValue() + " slamCooldown=" + mnm.slamCooldown + " sliding=" + mnm.sliding
                             + " vel=" + mnm.rb.velocity + " peak=" + fallPeak + " y=" + mnm.transform.position.y
                             + " fist=" + (fc != null && fc.currentPunch != null ? fc.currentPunch.type.ToString() : "none"));
                    break;
                }
                case "WATERINFO":
                {
                    // debug: is V1 in ULTRAKILL's water (touching, head under), and what Minecraft's fluids became
                    var wnm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
                    var uwc = levelPrepared ? MonoSingleton<UnderwaterController>.Instance : null;
                    var sb = new StringBuilder("WATERINFO");
                    if (wnm != null) sb.Append(" touching=").Append(wnm.touchingWaters.Count).Append(" vel=").Append(wnm.rb.velocity.y.ToString("0.0"));
                    if (uwc != null) sb.Append(" under=").Append(uwc.inWater);
                    sb.Append(" pools=").Append(FindObjectOfType<PooledWaterStore>() != null);
                    foreach (var kv in fluids) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value.boxes.Count).Append("/").Append(kv.Value.water != null ? WaterColliders(kv.Value.water)?.Length ?? -1 : -2);
                    Net.Send(sb.ToString());
                    break;
                }
                case "LIGHTSET":
                    LightSet(rest.Trim());
                    break;
                case "PAUSE":
                    // Minecraft's pause menu is open: ULTRAKILL stops too (V1, enemies, projectiles, sound)
                    SetMcPaused(rest.Trim() == "1");
                    break;
                case "HANDS":
                    // Minecraft hands (items, blocks, food): V1 puts the guns away; fists and movement stay ULTRAKILL's
                    SetHands(rest.Trim() == "1");
                    break;
                case "DOLL":
                {
                    // DOLL <yaw> <pitch> [w h]: the inventory is open and wants V1's portrait, turned like Minecraft's
                    // paper doll, w x h pixels (the size Minecraft shows it at)
                    var a = rest.Split(' ');
                    dollYaw = F(a[0]);
                    dollPitch = a.Length > 1 ? F(a[1]) : 0f;
                    if (a.Length > 3)
                    {
                        dollTargetW = Mathf.Clamp(int.Parse(a[2]), 32, MaxDollW);
                        dollTargetH = Mathf.Clamp(int.Parse(a[3]), 32, MaxDollH);
                    }
                    dollWanted = Time.unscaledTime + 0.5f;
                    break;
                }
                case "HURT":
                {
                    // HURT <ULTRAKILL damage> [void]: Minecraft hurt the player (lava, fire, cactus, drowning, blasts...);
                    // "void" is the bottom of the world, which kills outright like ULTRAKILL's pits
                    var nm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
                    var a = rest.Split(' ');
                    if (nm == null || nm.dead) break;
                    if (a.Length > 1 && a[1] == "void") nm.GetHurt(nm.hp * 10 + 999, false, 0f, ignoreInvincibility: true);
                    else nm.GetHurt(Mathf.Max(1, Mathf.RoundToInt(F(a[0]))), true);
                    break;
                }
                case "FULLHEAL":
                {
                    // FULLHEAL: V1 whole again (a duel starting or over)
                    var fnm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
                    if (fnm == null || fnm.dead) break;
                    fnm.ResetHardDamage();
                    fnm.FullHeal(true);
                    break;
                }
                case "DUEL":
                    // DUEL id | DUEL -: our shots hurt that player's V1 (a duel), or nobody's again
                    duelWith = rest.Trim() == "-" ? int.MinValue : int.Parse(rest.Trim());
                    break;
                case "RESPAWN":
                {
                    var nm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
                    if (nm != null && nm.dead) nm.Respawn();
                    break;
                }
                case "SNAP":
                    StartCoroutine(Snap());
                    break;
                case "DOLLSNAP":
                {
                    // debug: V1's inventory portrait as a PNG
                    if (dollOutRT == null) { Net.Send("DOLLSNAPPED none"); break; }
                    var prev = RenderTexture.active;
                    RenderTexture.active = dollOutRT;
                    var t = new Texture2D(dollW, dollH, TextureFormat.RGBA32, false);
                    t.ReadPixels(new Rect(0, 0, dollW, dollH), 0, 0);
                    RenderTexture.active = prev;
                    var dir = Path.Combine(Paths.BepInExRootPath, "ultrabridge_snap");
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, "doll.png"), t.EncodeToPNG());
                    Net.Send("DOLLSNAPPED " + dir.Replace('\\', '/'));
                    break;
                }
                case "GORETEST":
                    StartCoroutine(GoreTest());
                    break;

                case "FRAMESNAP":
                    FrameSnap(rest.Trim());
                    break;
                case "GROUND":
                {
                    // debug: what V1's ground checks think they stand on
                    var gnm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
                    if (gnm != null) Net.Send("GROUND " + GroundInfo(gnm) + " gravity=" + gnm.rb.useGravity + " vel=" + gnm.rb.velocity);
                    break;
                }
                case "GROUNDFIX":
                    // debug: switch KeepGroundHonest off to see ULTRAKILL's own ground check alone
                    groundFix = rest.Trim() != "0";
                    break;
                case "CAMS":
                {
                    // debug: the order ULTRAKILL's cameras draw in
                    var sb = new StringBuilder("CAMS");
                    foreach (var c in Camera.allCameras)
                    {
                        sb.Append(" | ").Append(c.name).Append(" depth ").Append(c.depth).Append(c.useOcclusionCulling ? " occl" : "").Append(" clear ").Append(c.clearFlags)
                          .Append(" target ").Append(c.targetTexture != null ? c.targetTexture.name : "screen/buffers").Append(" layers:");
                        for (int i = 0; i < 32; i++) if ((c.cullingMask & (1 << i)) != 0) sb.Append(' ').Append(i).Append(':').Append(LayerMask.LayerToName(i));
                    }
                    Net.Send(sb.ToString());
                    break;
                }
                case "BOOMTEST":
                {
                    // debug: BOOMTEST x y z (Minecraft coordinates): one of ULTRAKILL's explosions, harmless
                    var a = rest.Split(' ');
                    var drm = MonoSingleton<DefaultReferenceManager>.Instance;
                    if (drm == null || drm.explosion == null || !originSet) break;
                    var go = Instantiate(drm.explosion, McToUk(new Vector3(F(a[0]), F(a[1]), F(a[2]))), Quaternion.identity);
                    foreach (var ex in go.GetComponentsInChildren<Explosion>()) ex.harmless = true;
                    break;
                }
                case "MENUINFO":
                {
                    // debug: the Spawner Arm's menu: how many entries, how many still locked, where it's scrolled to
                    var sm = FindObjectOfType<SpawnMenu>();
                    if (sm == null) { Net.Send("MENU none"); break; }
                    var locked = Traverse.Create(sm).Field("lockedIcon").GetValue<Sprite>();
                    int total = 0, lockedN = 0;
                    foreach (var b in sm.GetComponentsInChildren<UnityEngine.UI.Button>())
                    {
                        total++;
                        foreach (var img in b.GetComponentsInChildren<UnityEngine.UI.Image>()) if (img.sprite == locked) { lockedN++; break; }
                    }
                    var sr = sm.GetComponentInChildren<UnityEngine.UI.ScrollRect>();
                    Net.Send("MENU buttons " + total + " locked " + lockedN + " scroll " + (sr != null ? sr.verticalNormalizedPosition.ToString("0.000") : "none"));
                    break;
                }
                case "SPAWN":
                {
                    // debug: SPAWN <enemy name> <distance>: one of ULTRAKILL's enemies in front of V1, as the Spawner Arm would
                    var a = rest.Split(' ');
                    SpawnEnemy(a[0], a.Length > 1 ? F(a[1]) : 10f);
                    break;
                }
                case "LOAD":
                    SceneHelper.LoadScene(rest);
                    break;
                default:
                    HandleWorld(cmd, rest);
                    break;
            }
        }

        static string Trunc(string s) => s.Length > 120 ? s.Substring(0, 120) + "..." : s;

        // ------------------------------------------------------------ level

        /// <summary>Strip the Sandbox scene down to V1: hide and disable every renderer, collider and
        /// light that isn't part of the player rig or the HUD, so only Minecraft's geometry remains.</summary>
        void PrepareLevel()
        {
            var nm = FindObjectOfType<NewMovement>();
            nm.MakeCurrent();
            var cc = nm.cc != null ? nm.cc : nm.GetComponentInChildren<CameraController>(true);
            if (cc != null)
            {
                cc.MakeCurrent();
                // CameraController looks V1 up once in Awake; if that came back empty its LateUpdate never runs
                if (cc.nm == null || cc.player == null)
                {
                    Plugin.Log.LogWarning("CameraController had no V1 (nm " + (cc.nm != null) + ", player " + (cc.player != null) + "); linking it");
                    cc.nm = nm;
                    cc.player = nm.gameObject;
                }
            }
            var player = nm.transform.root;
            var keep = new HashSet<Transform> { player };
            if (cc != null) keep.Add(cc.transform.root);
            int hidden = 0;
            // ULTRAKILL's blood and gibs wait in pools under its BloodsplatterManager: they're for the fight, not the
            // level, and stay drawn (switched off, blood still flew and stained, but was never seen)
            var bsmMgr = MonoSingleton<BloodsplatterManager>.Instance;
            var gore = bsmMgr != null ? bsmMgr.transform : null;
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (keep.Contains(root.transform))
                {
                    // V1's root is the spawn room ("FirstRoom Pit"): keep V1, strip the room's walls, floor and triggers
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true)) if (!r.transform.IsChildOf(nm.transform)) { r.enabled = false; hidden++; }
                    foreach (var c in root.GetComponentsInChildren<Collider>(true)) if (!c.transform.IsChildOf(nm.transform)) { c.enabled = false; hidden++; }
                    continue;
                }
                if (root.GetComponentInChildren<Canvas>(true) != null && root.GetComponentInChildren<Renderer>(true) == null) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (gore != null && r.transform.IsChildOf(gore)) continue;
                    r.enabled = false;
                    hidden++;
                }
                foreach (var c in root.GetComponentsInChildren<Collider>(true))
                {
                    if (gore != null && c.transform.IsChildOf(gore)) continue;
                    c.enabled = false;
                    hidden++;
                }
                foreach (var t in root.GetComponentsInChildren<Terrain>(true)) { t.enabled = false; }
            }
            RenderSettings.skybox = null;
            RenderSettings.fog = false;
            if (cc != null)
            {
                cc.cam.clearFlags = CameraClearFlags.SolidColor;
                cc.cam.backgroundColor = new Color(0, 0, 0, 0);
            }
            NoBakedOcclusion();
            SandboxLampsOff();
            blockRoot = new GameObject("McBlocks");
            sectionRoot = new GameObject("McSections");
            // hold V1 still until Minecraft has sent the ground and a start position
            nm.rb.isKinematic = true;
            levelPrepared = true;
            ApplySky();
            Net.Send("READY");
            PrefsOnReady();
            Plugin.Log.LogInfo("level prepared, hid " + hidden + " renderers/colliders");
        }

        /// <summary>The Sandbox ships baked occlusion culling for its own (now hidden) walls. Left on, it culls our
        /// Minecraft-terrain occluders (and enemies) wherever an old Sandbox wall stands in the way, so enemies show
        /// through Minecraft's blocks. Minecraft's world isn't baked, so no camera may use it.</summary>
        void NoBakedOcclusion()
        {
            foreach (var cam in Camera.allCameras) if (cam.useOcclusionCulling) cam.useOcclusionCulling = false;
        }

        /// <summary>The Sandbox has really started: loading/menu states are gone and its start sequence (V1 dropping
        /// into the pit, "pit-falling") has run. Taking over earlier gets V1 thrown back to the spawn and finds
        /// GunControl not ready.</summary>
        bool LevelStarted()
        {
            var gsm = GameStateManager.Instance;
            if (gsm == null || gsm.IsStateActive("main-menu") || gsm.IsStateActive("pause")) return false;
            if (gsm.IsStateActive("pit-falling")) sawLevelStart = true;
            if (!sawLevelStart && Time.timeSinceLevelLoad < 8f) return false;
            return FindObjectOfType<NewMovement>() != null;
        }

        /// <summary>What the pit's PlayerActivator does when V1 lands on it. The Sandbox drops V1 down a pit with
        /// the CameraController switched off; only that trigger switches it back on (and brings up the HUD).</summary>
        void LandV1(NewMovement nm)
        {
            v1Landed = true;
            GameStateManager.Instance.PopState("pit-falling");
            // what this world owns (WorldGear, Progress.cs): rebuild the loadout from it
            try
            {
                MonoSingleton<GunSetter>.Instance?.ResetWeapons();
                MonoSingleton<FistControl>.Instance?.ResetFists();
            }
            catch (Exception e) { Plugin.Log.LogWarning("loadout: " + e.Message); }
            try { nm.ActivatePlayer(); }
            catch (Exception e)
            {
                // GunControl/FistControl not ready: V1 still has to move and look; weapons come with the next TP
                Plugin.Log.LogWarning("ActivatePlayer: " + e.Message);
                nm.activated = true;
                v1Landed = false;
            }
            if (nm.cc != null)
            {
                nm.cc.activated = true;
                nm.cc.enabled = true;
            }
            var relay = FindObjectOfType<PlayerActivatorRelay>();
            if (relay != null)
            {
                try
                {
                    relay.ResetIndex();
                    relay.Activate();
                }
                catch (Exception e) { Plugin.Log.LogWarning("HUD relay: " + e.Message); }
            }
            // the Sandbox hands out the Spawner Arm (weapon slot 6) through its cheat; make sure V1 has it
            var gc = MonoSingleton<GunControl>.Instance;
            if (gc != null && gc.slot6.Count == 0 && CheatOn("ultrakill.spawner-arm"))
            {
                try
                {
                    var cm = MonoSingleton<CheatsManager>.Instance;
                    var arm = cm?.GetCheatInstance<ULTRAKILL.Cheats.SummonSandboxArm>();
                    if (arm != null && !arm.IsActive) cm.SetCheatActive(arm, true, false);
                }
                catch (Exception e) { Plugin.Log.LogWarning("spawner arm: " + e.Message); }
            }
            Plugin.Log.LogInfo("V1 landed: camera " + (nm.cc != null && nm.cc.isActiveAndEnabled ? "on" : "OFF") + ", HUD relay " + (relay != null ? "on" : "missing")
                               + ", spawner arms " + (gc != null ? gc.slot6.Count : -1)
                               + ", weapons per slot " + (gc != null ? string.Join("/", gc.slots.ConvertAll(s => s.Count.ToString()).ToArray()) : "?"));
        }

        // ------------------------------------------------------------ far terrain

        void BuildSections()
        {
            if (!levelPrepared || !originSet || sectionRoot == null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sectionQueue.Count > 0 && sw.ElapsedMilliseconds < 4) BuildSection(sectionQueue.Dequeue());
        }

        /// <summary>SEC sx sy sz face;face;... where face = d,plane,a0,b0,a1,b1[,tex] in Minecraft coordinates:
        /// d = 0..5 for +x,-x,+y,-y,+z,-z (6..11: the same for see-through blocks like glass and leaves, which collide
        /// but don't hide what's behind them, except through their texture tex's solid pixels when they have one);
        /// (a,b) are the other two axes in x,y,z order.</summary>
        static bool SameList(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        void BuildSection(string rest)
        {
            var parts = rest.Split(new[] { ' ' }, 4);
            if (parts.Length < 3) return;
            string key = parts[0] + "," + parts[1] + "," + parts[2];
            string data = parts.Length > 3 ? parts[3] : "";
            var origin = new Vector3(int.Parse(parts[0]) * 16, int.Parse(parts[1]) * 16, int.Parse(parts[2]) * 16);
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var opaqueTris = new List<int>();
            CutoutFaces cutout = null;
            foreach (var rec in data.Split(';'))
            {
                if (rec.Length == 0) continue;
                var f = rec.Split(',');
                int code = int.Parse(f[0]);
                if (code == 13)
                {
                    // a solid model's quad (a fence post, a wall, a stair's step): hides what's behind it, from either
                    // side; its collision comes as boxes
                    if (f.Length >= 13)
                    {
                        int q = verts.Count;
                        for (int k = 0; k < 4; k++) verts.Add(McToUk(origin + new Vector3(F(f[1 + k * 3]), F(f[2 + k * 3]), F(f[3 + k * 3]))));
                        opaqueTris.Add(q); opaqueTris.Add(q + 1); opaqueTris.Add(q + 2);
                        opaqueTris.Add(q); opaqueTris.Add(q + 2); opaqueTris.Add(q + 3);
                        opaqueTris.Add(q); opaqueTris.Add(q + 2); opaqueTris.Add(q + 1);
                        opaqueTris.Add(q); opaqueTris.Add(q + 3); opaqueTris.Add(q + 2);
                    }
                    continue;
                }
                if (code == 12)
                {
                    // a plant's (or torch's, door's...) quad: drawn with holes, nothing to collide with
                    if (f.Length >= 22) AddCutoutQuad(cutout ??= new CutoutFaces(), int.Parse(f[1]), origin, f);
                    continue;
                }
                bool seeThrough = code >= 6;
                int d = code % 6;
                float p = F(f[1]), a0 = F(f[2]), b0 = F(f[3]), a1 = F(f[4]), b1 = F(f[5]);
                // a 7th field: a see-through block with a cutout texture (leaves, glass), hiding what's behind its
                // solid pixels
                if (seeThrough && f.Length > 6) AddCutoutFace(cutout ??= new CutoutFaces(), int.Parse(f[6]), d, p, a0, b0, a1, b1);
                int axis = d >> 1;
                float sign = (d & 1) == 0 ? 1f : -1f;
                var u00 = McToUk(FacePoint(axis, p, a0, b0));
                var u10 = McToUk(FacePoint(axis, p, a1, b0));
                var u11 = McToUk(FacePoint(axis, p, a1, b1));
                var u01 = McToUk(FacePoint(axis, p, a0, b1));
                // outward normal in ULTRAKILL space (z is mirrored), so raycasts see the face from outside
                var n = axis == 0 ? new Vector3(sign, 0, 0) : axis == 1 ? new Vector3(0, sign, 0) : new Vector3(0, 0, -sign);
                int i = verts.Count;
                verts.Add(u00); verts.Add(u10); verts.Add(u11); verts.Add(u01);
                bool front = Vector3.Dot(Vector3.Cross(u10 - u00, u11 - u00), n) >= 0f;
                foreach (var list in seeThrough ? new[] { tris } : new[] { tris, opaqueTris })
                {
                    if (front)
                    {
                        list.Add(i); list.Add(i + 1); list.Add(i + 2);
                        list.Add(i); list.Add(i + 2); list.Add(i + 3);
                    }
                    else
                    {
                        list.Add(i); list.Add(i + 2); list.Add(i + 1);
                        list.Add(i); list.Add(i + 3); list.Add(i + 2);
                    }
                }
            }
            if (tris.Count == 0 && opaqueTris.Count == 0 && (cutout == null || cutout.verts.Count == 0))
            {
                RemoveSection(key);
                return;
            }
            // only plants (a section of open air above a meadow): nothing to collide with, only what they hide
            var mesh = tris.Count > 0 ? NewMesh(verts, tris) : null;
            // what blocks ULTRAKILL's view: the same faces minus see-through blocks
            // (the same mesh when every face is solid and there are no model quads)
            var occluder = opaqueTris.Count == 0 ? null : mesh != null && SameList(opaqueTris, tris) ? mesh : NewMesh(verts, opaqueTris);
            if (!sections.TryGetValue(key, out var go) || go == null)
            {
                go = new GameObject("sec " + key);
                go.transform.SetParent(sectionRoot.transform, false);
                go.layer = 8; // Environment
                go.tag = "Floor";
                go.AddComponent<MeshCollider>();
                go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = OccluderMaterial();
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                mr.allowOcclusionWhenDynamic = false;
                sections[key] = go;
            }
            ReleaseSectionMeshes(go);
            go.GetComponent<MeshCollider>().sharedMesh = mesh;
            // the renderer stays enabled even with nothing to occlude (see-through blocks only): ULTRAKILL's weapons
            // only count renderer-carrying colliders as static environment
            go.GetComponent<MeshFilter>().sharedMesh = occluder;
            BuildCutout(go, cutout);
            // blocks broken or placed near V1: the enemies' navmesh follows
            if (mesh != null) MarkNavDirty(mesh.bounds);
        }

        static Mesh NewMesh(List<Vector3> verts, List<int> tris)
        {
            var mesh = new Mesh();
            if (verts.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static void ReleaseSectionMeshes(GameObject go)
        {
            var mc = go.GetComponent<MeshCollider>();
            var mf = go.GetComponent<MeshFilter>();
            var a = mc != null ? mc.sharedMesh : null;
            var b = mf != null ? mf.sharedMesh : null;
            if (mc != null) mc.sharedMesh = null;
            if (mf != null) mf.sharedMesh = null;
            if (a != null) Destroy(a);
            if (b != null && b != a) Destroy(b);
        }

        Material occluderMat;

        /// <summary>Invisible stand-in for Minecraft's terrain in ULTRAKILL's view: writes depth and fully transparent
        /// black, so ULTRAKILL's effects, projectiles and enemies behind Minecraft blocks are hidden, and the mask
        /// leaves Minecraft's own blocks showing.</summary>
        Material OccluderMaterial()
        {
            if (occluderMat != null) return occluderMat;
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) Plugin.Log.LogWarning("Hidden/Internal-Colored missing: terrain won't hide ULTRAKILL effects");
            occluderMat = new Material(sh) { renderQueue = 1990, name = "Ultracraft occluder" };
            occluderMat.SetColor("_Color", new Color(0, 0, 0, 0));
            occluderMat.SetInt("_SrcBlend", (int)BlendMode.One);
            occluderMat.SetInt("_DstBlend", (int)BlendMode.Zero);
            occluderMat.SetInt("_SrcAlphaBlend", (int)BlendMode.One);
            occluderMat.SetInt("_DstAlphaBlend", (int)BlendMode.Zero);
            occluderMat.SetInt("_ZWrite", 1);
            occluderMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            occluderMat.SetInt("_Cull", (int)CullMode.Off);
            return occluderMat;
        }

        static Mesh cubeMesh;

        static Mesh CubeMesh()
        {
            if (cubeMesh != null) return cubeMesh;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            DestroyImmediate(tmp);
            return cubeMesh;
        }

        static Vector3 FacePoint(int axis, float p, float a, float b)
        {
            return axis == 0 ? new Vector3(p, a, b) : axis == 1 ? new Vector3(a, p, b) : new Vector3(a, b, p);
        }

        void RemoveSection(string key)
        {
            if (!sections.TryGetValue(key, out var go)) return;
            sections.Remove(key);
            if (go != null)
            {
                ReleaseSectionMeshes(go);
                BuildCutout(go, null);
                Destroy(go);
            }
        }

        void ClearSections()
        {
            foreach (var key in new List<string>(sections.Keys)) RemoveSection(key);
            sectionQueue.Clear();
        }

        // ------------------------------------------------------------ V1's footing

        static readonly AccessTools.FieldRef<GroundCheckGroup, List<GroundCheck>> GroundChecks = AccessTools.FieldRefAccess<GroundCheckGroup, List<GroundCheck>>("instances");
        static readonly Action<GroundCheck, Collider> GroundExit = AccessTools.MethodDelegate<Action<GroundCheck, Collider>>(AccessTools.Method(typeof(GroundCheck), "OnTriggerExit"));
        readonly Collider[] footing = new Collider[64];
        readonly List<Collider> lostFooting = new List<Collider>();

        /// <summary>ULTRAKILL's ground check only learns V1 left the ground from trigger-exit events, and Unity sends none
        /// for a collider that is switched off, re-meshed or moved: our block boxes get recycled and chunk sections
        /// re-meshed when blocks break. V1 then still "stands" on terrain that's gone, and standing still on ground turns
        /// its gravity off, so it hung in the air until it moved. Our terrain the check holds but no longer touches leaves
        /// it the way a trigger exit would.</summary>
        bool groundFix = true;

        void KeepGroundHonest(NewMovement nm)
        {
            if (!groundFix) return;
            KeepGroundHonest(nm.gc);
            KeepGroundHonest(nm.slopeCheck);
        }

        void KeepGroundHonest(GroundCheckGroup group)
        {
            var list = group != null ? GroundChecks(group) : null;
            if (list == null) return;
            foreach (var g in list)
            {
                if (g == null || !g.isActiveAndEnabled || g.capsule == null || g.cols.Count == 0) continue;
                int n = -1;
                lostFooting.Clear();
                foreach (var c in g.cols)
                {
                    if (c == null || !IsMinecraftTerrain(c.transform)) continue;
                    if (n < 0) n = Footing(g.capsule);
                    if (Array.IndexOf(footing, c, 0, n) < 0) lostFooting.Add(c);
                }
                foreach (var c in lostFooting)
                {
                    GroundExit(g, c);
                    groundFixes++;
                }
            }
        }

        int groundFixes;

        string GroundInfo(NewMovement nm)
        {
            var sb = new StringBuilder();
            foreach (var group in new[] { nm.gc, nm.slopeCheck })
            {
                var list = group != null ? GroundChecks(group) : null;
                if (list == null) continue;
                foreach (var g in list)
                {
                    if (g == null) continue;
                    sb.Append(g.name).Append(g.slopeCheck ? "(slope)" : "").Append(" on=").Append(g.onGround).Append(" touch=").Append(g.touchingGround).Append(" cols=[");
                    foreach (var c in g.cols) sb.Append(c == null ? "null" : c.name + (c.gameObject.activeInHierarchy ? "" : "(off)")).Append(' ');
                    sb.Append("] ");
                }
            }
            return sb.Append("fixes=").Append(groundFixes).ToString();
        }

        /// <summary>Our terrain colliders touching a ground check's capsule, a little bigger so contact at the edge counts.</summary>
        int Footing(CapsuleCollider cap)
        {
            var t = cap.transform;
            var s = t.lossyScale;
            Vector3 axis;
            float along, across;
            switch (cap.direction)
            {
                case 0: axis = t.right; along = Mathf.Abs(s.x); across = Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)); break;
                case 2: axis = t.forward; along = Mathf.Abs(s.z); across = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y)); break;
                default: axis = t.up; along = Mathf.Abs(s.y); across = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z)); break;
            }
            float r = cap.radius * across;
            float half = Mathf.Max(cap.height * along * 0.5f - r, 0f);
            var c = t.TransformPoint(cap.center);
            return Physics.OverlapCapsuleNonAlloc(c + axis * half, c - axis * half, r + 0.1f, footing, 1 << 8, QueryTriggerInteraction.Ignore);
        }

        public bool IsMinecraftTerrain(Transform t)
        {
            return t != null && ((blockRoot != null && t.IsChildOf(blockRoot.transform)) || (sectionRoot != null && t.IsChildOf(sectionRoot.transform)));
        }

        // ------------------------------------------------------------ Minecraft water and lava

        // ULTRAKILL's own Water over Minecraft's water and lava: V1 sinks slowly and floats, splashes, bubbles, the
        // muffled underwater sound and tint; enemies get wet, Streetcleaners drown, electricity carries through. One
        // Water per fluid, one trigger box per stretch of fluid Minecraft sends.
        static readonly AccessTools.FieldRef<Water, Collider[]> WaterColliders = AccessTools.FieldRefAccess<Water, Collider[]>("waterColliders");
        static readonly AccessTools.FieldRef<Water, int> WaterCount = AccessTools.FieldRefAccess<Water, int>("waterCount");
        static readonly AccessTools.FieldRef<Water, List<Water.WaterColData>> WaterColDatas = AccessTools.FieldRefAccess<Water, List<Water.WaterColData>>("waterColData");
        static readonly AccessTools.FieldRef<Water, Dictionary<Collider, WaterObject>> WaterTracked = AccessTools.FieldRefAccess<Water, Dictionary<Collider, WaterObject>>("tracked");
        static readonly Action<Water, Collider, bool> WaterRemove = AccessTools.MethodDelegate<Action<Water, Collider, bool>>(AccessTools.Method(typeof(Water), "RemoveFromWater"));

        class Fluid
        {
            public Water water;
            public readonly Dictionary<string, BoxCollider> boxes = new Dictionary<string, BoxCollider>();
        }

        readonly Dictionary<string, Fluid> fluids = new Dictionary<string, Fluid>();

        /// <summary>FLUID water|lava rrggbb x0,y0,z0,x1,y1,z1;... in Minecraft coordinates: every stretch of that fluid
        /// near V1, merged into boxes by Minecraft, each as high as the fluid stands.</summary>
        void ApplyFluid(string rest)
        {
            if (!levelPrepared || !originSet) return;
            var parts = rest.Split(new[] { ' ' }, 3);
            if (parts.Length < 2) return;
            var f = GetFluid(parts[0]);
            if (f == null) return;
            if (ColorUtility.TryParseHtmlString("#" + parts[1], out var clr) && f.water.clr != clr) f.water.UpdateColor(clr);
            var seen = new HashSet<string>();
            bool changed = false;
            foreach (var entry in parts.Length > 2 ? parts[2].Split(';') : new string[0])
            {
                if (entry.Length == 0) continue;
                seen.Add(entry);
                if (f.boxes.ContainsKey(entry)) continue;
                var a = entry.Split(',');
                if (a.Length < 6) continue;
                var u0 = McToUk(new Vector3(F(a[0]), F(a[1]), F(a[2])));
                var u1 = McToUk(new Vector3(F(a[3]), F(a[4]), F(a[5])));
                var box = f.water.gameObject.AddComponent<BoxCollider>();
                box.isTrigger = true;
                // the Water sits at the origin, unrotated: local is world
                box.center = (u0 + u1) * 0.5f;
                box.size = new Vector3(Mathf.Abs(u1.x - u0.x), Mathf.Abs(u1.y - u0.y), Mathf.Abs(u1.z - u0.z));
                f.boxes[entry] = box;
                changed = true;
            }
            foreach (var key in new List<string>(f.boxes.Keys))
            {
                if (seen.Contains(key)) continue;
                var box = f.boxes[key];
                f.boxes.Remove(key);
                if (box != null)
                {
                    box.enabled = false;
                    Destroy(box);
                }
                changed = true;
            }
            if (changed) SyncWater(f);
        }

        Fluid GetFluid(string kind)
        {
            if (kind != "water" && kind != "lava") return null;
            if (fluids.TryGetValue(kind, out var f) && f.water != null) return f;
            // splashes and bubbles come from ULTRAKILL's pools; the Sandbox may have none of its own
            if (FindObjectOfType<PooledWaterStore>() == null) new GameObject("Ultracraft Water Pools").AddComponent<PooledWaterStore>();
            var go = new GameObject("Minecraft " + kind);
            go.layer = 4; // Water
            go.SetActive(false);
            var w = go.AddComponent<Water>();
            // lava: an orange tint, and nothing comes out of it wet (fires stay lit); the burning is Minecraft's
            if (kind == "lava")
            {
                w.notWet = true;
                w.clr = new Color(1f, 0.35f, 0f);
            }
            go.SetActive(true);
            f = new Fluid { water = w };
            fluids[kind] = f;
            return f;
        }

        /// <summary>Water only looks its colliders up once (in Start), so it gets the current boxes from us whenever they
        /// change, with what it caches about them.</summary>
        void SyncWater(Fluid f)
        {
            var w = f.water;
            var cols = new Collider[f.boxes.Count];
            var data = WaterColDatas(w);
            data.Clear();
            int i = 0;
            foreach (var box in f.boxes.Values)
            {
                cols[i++] = box;
                var b = new Bounds(box.center, box.size);
                data.Add(new Water.WaterColData { maxHeight = b.max.y, minHeight = b.min.y, position = w.transform.position, rotation = w.transform.rotation });
            }
            WaterColliders(w) = cols;
            WaterCount(w) = cols.Length;
            // Unity sends no trigger exit for a box switched off under V1 or an enemy (the water drained or flowed
            // away): whatever no longer touches this water leaves it now
            var tracked = WaterTracked(w);
            if (tracked == null || tracked.Count == 0) return;
            foreach (var col in new List<Collider>(tracked.Keys))
                if (col == null || !w.IsCollidingWithWater(col)) WaterRemove(w, col, false);
        }

        /// <summary>Water.Start (its first frame) looks its colliders up again; put ours back.</summary>
        void KeepFluidsSynced()
        {
            foreach (var f in fluids.Values)
            {
                if (f.water == null) continue;
                var cols = WaterColliders(f.water);
                if (cols == null || cols.Length != f.boxes.Count) SyncWater(f);
            }
        }

        void ClearFluids()
        {
            foreach (var f in fluids.Values)
            {
                if (f.water == null) continue;
                foreach (var box in f.boxes.Values) if (box != null) { box.enabled = false; Destroy(box); }
                f.boxes.Clear();
                SyncWater(f);
            }
        }

        // ------------------------------------------------------------ ground slams

        // the highest V1 has been since it last stood on the ground (ULTRAKILL units): a slam's crater grows with the drop
        float fallPeak, groundedFor;
        // V1 slammed during this fall (the ground check drops its own flag the moment it touches down, before the landing)
        bool slammed;

        void TrackFall(NewMovement nm)
        {
            float y = nm.transform.position.y;
            if (nm.gc != null && nm.gc.onGround)
            {
                groundedFor += Time.deltaTime;
                if (groundedFor > 0.25f)
                {
                    fallPeak = y;
                    slammed = false;
                }
            }
            else
            {
                groundedFor = 0f;
                fallPeak = Mathf.Max(fallPeak, y);
                if (nm.gc != null && nm.gc.heavyFall) slammed = true;
            }
        }

        /// <summary>Whether the landing under way is a slam's (a ground pound), not a jump's or a fall's.</summary>
        public bool Slamming(NewMovement nm) => slammed || (nm.gc != null && nm.gc.heavyFall);

        /// <summary>How many blocks V1 fell into the slam it's landing (0 for a landing that isn't a slam).</summary>
        public float SlamDrop(NewMovement nm)
        {
            return Slamming(nm) ? Mathf.Max(0f, fallPeak - nm.transform.position.y) / K : 0f;
        }

        /// <summary>A slam landed: from high enough, Minecraft blows a crater that grows with the drop (SLAM x y z
        /// blocks fallen).</summary>
        public void ReportSlam(NewMovement nm)
        {
            float drop = SlamDrop(nm);
            fallPeak = nm.transform.position.y;
            slammed = false;
            if (levelPrepared && drop > 0f) Seismic(nm, drop);
            if (!levelPrepared || !originSet || !Net.Connected || drop < SlamCraterDrop) return;
            var m = UkToMc(nm.gc != null ? nm.gc.transform.position : nm.transform.position);
            Net.Send("SLAM " + S(m.x) + " " + S(m.y) + " " + S(m.z) + " " + S(drop));
            MonoSingleton<CameraController>.Instance?.CameraShake(Mathf.Min(drop / 40f, 4f));
        }

        public const float SlamCraterDrop = 12f;

        // ------------------------------------------------------------ Minecraft's effects on V1

        // Minecraft potion effects that move V1 (amplifier, -1 = none)
        int fxSpeed = -1, fxSlow = -1, fxJump = -1, fxSlowFall = -1, fxLevitate = -1;
        float baseWalkSpeed, baseJumpPower;
        // Minecraft's heals arrive in half hearts; the fractions add up here
        float healCarry;
        // ULTRAKILL's camera is zoomed because Minecraft's view is (a bow drawn, a spyglass)
        bool zoomedByMc;

        /// <summary>Speed and Slowness change V1's run as they change Steve's walk (+20% / -15% a level), Jump Boost its
        /// jump, Slow Falling caps its fall (a slam still slams), Levitation lifts it.</summary>
        void ApplyEffects(NewMovement nm)
        {
            if (baseWalkSpeed <= 0f)
            {
                baseWalkSpeed = nm.walkSpeed;
                baseJumpPower = nm.jumpPower;
            }
            nm.walkSpeed = baseWalkSpeed * (1f + 0.2f * (fxSpeed + 1)) * Mathf.Max(0.1f, 1f - 0.15f * (fxSlow + 1)) * (CheatOn("ultracraft.super-speed") ? 1.6f : 1f);
            nm.jumpPower = baseJumpPower * (1f + 0.24f * (fxJump + 1));
            if (nm.dead || nm.rb.isKinematic || mcPaused) return;
            var v = nm.rb.velocity;
            if (fxLevitate >= 0)
            {
                // up at 0.9 blocks a second per level
                v.y = Mathf.MoveTowards(v.y, 0.9f * (fxLevitate + 1) * K, 40f * Time.deltaTime);
                nm.rb.velocity = v;
            }
            else if (fxSlowFall >= 0 && !(nm.gc != null && nm.gc.heavyFall) && v.y < -SlowFallSpeed * K)
            {
                v.y = -SlowFallSpeed * K;
                nm.rb.velocity = v;
            }
        }

        const float SlowFallSpeed = 1.5f; // blocks a second

        /// <summary>Minecraft's view zoomed by factor m (a drawn bow, a spyglass, Speed's wider view): ULTRAKILL's camera
        /// zooms the same way, so both games keep looking through the same lens.</summary>
        void SetMcZoom(float m)
        {
            var cc = levelPrepared ? MonoSingleton<CameraController>.Instance : null;
            if (cc == null) return;
            if (Mathf.Abs(m - 1f) < 0.01f)
            {
                if (zoomedByMc) cc.StopZoom();
                zoomedByMc = false;
            }
            else
            {
                cc.Zoom(cc.defaultFov * m);
                zoomedByMc = true;
            }
        }

        static readonly AccessTools.FieldRef<NewMovement, int> NmDifficulty = AccessTools.FieldRefAccess<NewMovement, int>("difficulty");

        /// <summary>V1's full health: 200 on Harmless (or Lenient after a few restarts at one checkpoint), else 100, as
        /// NewMovement.GetHealth caps it.</summary>
        static int MaxHp(NewMovement nm)
        {
            int d = NmDifficulty(nm);
            return d == 0 || (d == 1 && nm.sameCheckpointRestarts > 2) ? 200 : 100;
        }

        // ------------------------------------------------------------ Minecraft's light on ULTRAKILL's things

        // LIGHT id,r,g,b;... from Minecraft: its lightmap colour (block light, sky light, time of day, gamma) where
        // each of ULTRAKILL's enemies stands, and "v1" for V1 (its arm and guns)
        readonly Dictionary<string, Color> lit = new Dictionary<string, Color>();
        readonly MaterialPropertyBlock litBlock = new MaterialPropertyBlock();
        static readonly List<Renderer> litRenderers = new List<Renderer>();
        // "on": each material's _Color is tinted by the light (ULTRAKILL/Master multiplies everything it draws, its
        // own glow included, by _Color; it takes ambient from the global light model, not from probes); "off": untouched
        string lightMode = "on";
        // Minecraft's full daylight is this bright in ULTRAKILL's terms
        float lightScale = 1f;
        readonly List<Light> sandboxLights = new List<Light>();

        void ApplyLightLevels(string rest)
        {
            foreach (var entry in rest.Split(';'))
            {
                var a = entry.Split(',');
                if (a.Length < 4) continue;
                lit[a[0]] = new Color(F(a[1]), F(a[2]), F(a[3]));
            }
        }

        void ApplyLight(NewMovement nm)
        {
            if (lightMode == "off" || lit.Count == 0) return;
            foreach (var kv in ukEnemies)
            {
                if (kv.Value == null || !lit.TryGetValue(kv.Key.ToString(), out var c)) continue;
                LightUp(kv.Value.gameObject, c);
            }
            if (lit.TryGetValue("v1", out var v))
            {
                var gc = MonoSingleton<GunControl>.Instance;
                if (gc != null && gc.currentWeapon != null) LightUp(gc.currentWeapon, v);
                var fc = MonoSingleton<FistControl>.Instance;
                if (fc != null) LightUp(fc.gameObject, v);
            }
            LightShops();
        }

        /// <summary>Everything drawn under go takes Minecraft's light colour c: each material keeps its own colour,
        /// times the light (per material, so ULTRAKILL's own per-renderer settings - outlines, sand, blessing - stay).</summary>
        /// <para>fill (0..1): also an ambient light of that much of c, so sides facing away from ULTRAKILL's sun aren't
        /// black (Minecraft shades a block's sides at 60-80% of its top, never darker).</para>
        void LightUp(GameObject go, Color c, float fill = -1f)
        {
            go.GetComponentsInChildren(false, litRenderers);
            c *= lightScale;
            c.a = 1f;
            var ambient = fill >= 0f ? new Vector4(c.r * fill, c.g * fill, c.b * fill, 1f) : Vector4.zero;
            foreach (var r in litRenderers)
            {
                if (r == null || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer || r is SpriteRenderer) continue;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || !mats[i].HasProperty(ColorId) || IsGlow(mats[i])) continue;
                    r.GetPropertyBlock(litBlock, i);
                    litBlock.SetColor(ColorId, mats[i].GetColor(ColorId) * c);
                    if (fill >= 0f) litBlock.SetVector(AmbientId, ambient);
                    r.SetPropertyBlock(litBlock, i);
                }
            }
        }

        /// <summary>A glow (the Cerberus's orb, charge spheres, sprites): it shines on its own, so Minecraft's light
        /// leaves it be. Darkened, a sprite's colour went black while its coverage stayed: a black box in the dark.</summary>
        static bool IsGlow(Material m)
        {
            var sh = m.shader != null ? m.shader.name : "";
            return sh.StartsWith("Sprites/") || sh.Contains("Particles") || sh.Contains("Additive");
        }

        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int AmbientId = Shader.PropertyToID("glstate_lightmodel_ambient");

        /// <summary>Minecraft's light nearest a spot: V1's, or that of the closest of ULTRAKILL's enemies (Minecraft
        /// only measures it where those are). White before Minecraft has said anything.</summary>
        public Color LightNear(Vector3 at)
        {
            Color best = Color.white;
            float bestD = float.MaxValue;
            var nm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
            if (nm != null && lit.TryGetValue("v1", out var v))
            {
                best = v;
                bestD = (nm.transform.position - at).sqrMagnitude;
            }
            foreach (var kv in ukEnemies)
            {
                if (kv.Value == null || !lit.TryGetValue(kv.Key.ToString(), out var e)) continue;
                float d = (kv.Value.transform.position - at).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = e;
                }
            }
            best *= lightScale;
            best.a = 1f;
            return best;
        }

        /// <summary>The Sandbox's lamps (point and spot lights) stand at fixed spots of ULTRAKILL's map, which mean
        /// nothing in Minecraft's world: off. Its sun stays, so things keep ULTRAKILL's shading; Minecraft's light
        /// does the rest.</summary>
        void SandboxLampsOff()
        {
            var nm = MonoSingleton<NewMovement>.Instance;
            var player = nm != null ? nm.transform.root : null;
            var cam = MonoSingleton<CameraController>.Instance;
            var camRoot = cam != null ? cam.transform.root : null;
            int n = 0;
            foreach (var l in FindObjectsOfType<Light>())
            {
                if (l.type == LightType.Directional || l.transform.root == player || l.transform.root == camRoot) continue;
                l.enabled = false;
                n++;
            }
            Plugin.Log.LogInfo("Sandbox lamps off: " + n);
        }

        /// <summary>Debug: how ULTRAKILL lights things here (lights, ambient, probes) and what its shaders take.</summary>
        void LightInfo()
        {
            var sb = new StringBuilder("LIGHTINFO ambientMode=" + RenderSettings.ambientMode + " ambient=" + RenderSettings.ambientLight + " sky=" + RenderSettings.ambientSkyColor
                + " equator=" + RenderSettings.ambientEquatorColor + " ground=" + RenderSettings.ambientGroundColor + " intensity=" + RenderSettings.ambientIntensity
                + " probes=" + (LightmapSettings.lightProbes != null ? LightmapSettings.lightProbes.count : 0) + " lightmaps=" + LightmapSettings.lightmaps.Length);
            foreach (var l in FindObjectsOfType<Light>())
                sb.Append(" | ").Append(l.name).Append(" root=").Append(l.transform.root.name).Append(' ').Append(l.type).Append(" on=").Append(l.enabled)
                  .Append(" i=").Append(l.intensity.ToString("0.##")).Append(" c=").Append(l.color).Append(" range=").Append(l.range.ToString("0"))
                  .Append(" mode=").Append(l.renderMode).Append(" mask=").Append(l.cullingMask).Append(" at=").Append(l.transform.position);
            Net.Send(sb.ToString());
            var tracker = MonoSingleton<EnemyTracker>.Instance;
            int n = 0;
            if (tracker != null)
                foreach (var eid in tracker.GetCurrentEnemies())
                {
                    if (eid == null || IsProxy(eid)) continue;
                    Net.Send("LIGHTINFO enemy " + eid.enemyType + RendererInfo(eid.gameObject));
                    if (++n >= 2) break;
                }
            var gc = MonoSingleton<GunControl>.Instance;
            if (gc != null && gc.currentWeapon != null) Net.Send("LIGHTINFO gun" + RendererInfo(gc.currentWeapon));
        }

        static string RendererInfo(GameObject go)
        {
            var sb = new StringBuilder();
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer) continue;
                sb.Append(" [").Append(r.GetType().Name).Append(' ').Append(r.name).Append(" layer=").Append(r.gameObject.layer).Append(" probes=").Append(r.lightProbeUsage);
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null) continue;
                    sb.Append(" mat=").Append(mat.name).Append(" shader=").Append(mat.shader.name).Append(" kw=").Append(string.Join(",", mat.shaderKeywords)).Append(" props=");
                    for (int i = 0; i < mat.shader.GetPropertyCount() && i < 40; i++) sb.Append(mat.shader.GetPropertyName(i)).Append(',');
                }
                sb.Append(']');
            }
            return sb.ToString();
        }

        /// <summary>Debug: LIGHTSET lights 0|1 (the Sandbox's own lights), ambient r g b (flat), mode on|off,
        /// scale s, v1 r g b / all r g b (a light colour for V1 / every enemy, as Minecraft would send).</summary>
        void LightSet(string args)
        {
            var a = args.Split(' ');
            switch (a[0])
            {
                case "lights":
                    SandboxLights(a.Length > 1 && a[1] == "1");
                    break;
                case "ambient":
                    RenderSettings.ambientMode = AmbientMode.Flat;
                    RenderSettings.ambientLight = new Color(F(a[1]), F(a[2]), F(a[3]));
                    break;
                case "mode":
                    lightMode = a[1];
                    break;
                case "scale":
                    lightScale = F(a[1]);
                    break;
                case "v1":
                    lit["v1"] = new Color(F(a[1]), F(a[2]), F(a[3]));
                    break;
                case "all":
                    foreach (var id in ukEnemies.Keys) lit[id.ToString()] = new Color(F(a[1]), F(a[2]), F(a[3]));
                    break;
                case "mpb":
                    // mpb <property> <float> | <r g b>: on every renderer of the enemies and V1's gun and arm
                    foreach (var r in TestRenderers())
                    {
                        r.GetPropertyBlock(litBlock);
                        if (a.Length >= 5) litBlock.SetVector(a[1], new Vector4(F(a[2]), F(a[3]), F(a[4]), 1f));
                        else litBlock.SetFloat(a[1], F(a[2]));
                        r.SetPropertyBlock(litBlock);
                    }
                    break;
                case "mpbclear":
                    foreach (var r in TestRenderers())
                    {
                        r.SetPropertyBlock(null);
                        for (int i = 0; i < r.sharedMaterials.Length; i++) r.SetPropertyBlock(null, i);
                    }
                    break;
                case "global":
                    if (a.Length >= 5) Shader.SetGlobalVector(a[1], new Vector4(F(a[2]), F(a[3]), F(a[4]), 1f));
                    else Shader.SetGlobalFloat(a[1], F(a[2]));
                    break;
                case "matcolor":
                    // matcolor r g b: the materials' own _Color (instanced), to see whether the shader uses it
                    foreach (var r in TestRenderers())
                        foreach (var m in r.materials)
                            if (m != null && m.HasProperty("_Color")) m.SetColor("_Color", new Color(F(a[1]), F(a[2]), F(a[3])));
                    break;
            }
            Net.Send("LIGHTSET " + args);
        }

        List<Renderer> TestRenderers()
        {
            var list = new List<Renderer>();
            var gos = new List<GameObject>();
            foreach (var eid in ukEnemies.Values) if (eid != null) gos.Add(eid.gameObject);
            var gc = MonoSingleton<GunControl>.Instance;
            if (gc != null && gc.currentWeapon != null) gos.Add(gc.currentWeapon);
            foreach (var go in gos)
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                    if (!(r is ParticleSystemRenderer) && !(r is TrailRenderer) && !(r is LineRenderer)) list.Add(r);
            return list;
        }

        /// <summary>The Sandbox's own lights (its sun, its lamps), not V1's or the effects' flashes.</summary>
        void SandboxLights(bool on)
        {
            if (sandboxLights.Count == 0)
            {
                var nm = MonoSingleton<NewMovement>.Instance;
                var player = nm != null ? nm.transform.root : null;
                var cam = MonoSingleton<CameraController>.Instance;
                var camRoot = cam != null ? cam.transform.root : null;
                foreach (var l in FindObjectsOfType<Light>())
                    if (l.transform.root != player && l.transform.root != camRoot) sandboxLights.Add(l);
            }
            foreach (var l in sandboxLights) if (l != null) l.enabled = on;
        }

        // ------------------------------------------------------------ menus

        /// <summary>An ULTRAKILL menu that frees the cursor (spawn menu, alter menu) is open: tell Minecraft to show its
        /// cursor and send pointer positions instead of mouse look.</summary>
        void UpdateUiMode()
        {
            var gsm = GameStateManager.Instance;
            bool ui = v1Landed && gsm != null && !gsm.CursorLocked;
            if (ui == uiMode) return;
            uiMode = ui;
            Net.Send("UI " + (ui ? 1 : 0));
            Plugin.Log.LogInfo("UI mode " + (ui ? "on" : "off") + " states=[" + ActiveStates() + "]");
        }

        /// <summary>Screen-space-overlay canvases are drawn straight to ULTRAKILL's (hidden) window, never into the
        /// frame we share. Hand them to the HUD camera, which draws into the same texture as V1's guns, so menus,
        /// crosshair and the classic HUD reach Minecraft with their alpha.</summary>
        void CaptureOverlayCanvases()
        {
            var pp = MonoSingleton<PostProcessV2_Handler>.Instance;
            var hud = pp != null ? pp.hudCam : null;
            if (hud == null) return;
            foreach (var c in FindObjectsOfType<Canvas>())
            {
                if (!c.isRootCanvas || c.renderMode != RenderMode.ScreenSpaceOverlay || c.name == "Debug Canvas") continue;
                c.renderMode = RenderMode.ScreenSpaceCamera;
                c.worldCamera = hud;
                c.planeDistance = hud.nearClipPlane + 0.05f;
                hud.cullingMask |= 1 << c.gameObject.layer;
                Plugin.Log.LogInfo("canvas " + c.name + " (layer " + c.gameObject.layer + ", order " + c.sortingOrder + ") now drawn by the HUD camera");
            }
        }

        // ------------------------------------------------------------ Minecraft attacks, parries

        // a mob's swing lands this long after Minecraft decides it (the parry flash shows meanwhile); kept short so
        // melee hits don't feel delayed. A punch up to EarlyParry before the swing parries too, which is where most of
        // the leniency lives (the Feedbacker's Reflex upgrade mostly widens that, and only nudges the delay)
        static float ParryWindow => 0.15f + ReflexBonus * 0.3f;
        static float EarlyParry => 0.3f + ReflexBonus;
        // a projectile that reaches V1 hangs in front of it this long before it hits, so any of Minecraft's projectiles
        // (even a fast arrow) can be parried; a Feedbacker punch up to EarlyParry before it arrives parries it too
        const float ProjectileHold = 0.2f;
        public static float lastParryPunch = -10f;
        readonly Dictionary<int, McProjectile> projectiles = new Dictionary<int, McProjectile>();
        readonly Dictionary<int, float> projectileDone = new Dictionary<int, float>();

        /// <summary>PROJS id,type,x,y,z,vx,vy,vz,size;... (Minecraft coordinates, velocity in blocks per tick).</summary>
        void ApplyProjectiles(string rest)
        {
            if (!levelPrepared || !originSet) return;
            var seen = new HashSet<int>();
            foreach (var entry in rest.Split(';'))
            {
                if (entry.Length == 0) continue;
                var a = entry.Split(',');
                int id = int.Parse(a[0]);
                if (projectileDone.TryGetValue(id, out var until) && Time.time < until) continue;
                seen.Add(id);
                var pos = McToUk(new Vector3(F(a[2]), F(a[3]), F(a[4])));
                var vel = new Vector3(F(a[5]), F(a[6]), -F(a[7])) * (20f * K);
                if (!projectiles.TryGetValue(id, out var p) || p == null)
                {
                    p = MakeProjectile(id, a[1], F(a[8]) * K);
                    projectiles[id] = p;
                    p.transform.position = pos;
                }
                p.lastPos = pos;
                p.velocity = vel;
                p.lastTime = Time.time;
            }
            var gone = new List<int>();
            // a held projectile stands still in Minecraft, so Minecraft stops listing it: it stays until it hits or is parried
            foreach (var kv in projectiles) if (!seen.Contains(kv.Key) && (kv.Value == null || !kv.Value.held)) gone.Add(kv.Key);
            foreach (var id in gone)
            {
                if (projectiles[id] != null) Destroy(projectiles[id].gameObject);
                projectiles.Remove(id);
            }
        }

        McProjectile MakeProjectile(int id, string type, float size)
        {
            var go = new GameObject("mcproj_" + type + "_" + id);
            go.layer = 14; // Projectile: what the Feedbacker's parry casts look for
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            var p = go.AddComponent<McProjectile>();
            p.id = id;
            p.type = type;
            p.radius = Mathf.Max(0.35f, size * 0.5f);
            // the parry catch area is bigger than the projectile, like ULTRAKILL's own projectiles (a punch parries what
            // this sphere holds at the moment of the punch)
            var sc = go.AddComponent<SphereCollider>();
            sc.isTrigger = true;
            sc.radius = p.radius + 1f;
            var pr = go.AddComponent<ParryReceiver>();
            pr.parryHeal = true; // a full ULTRAKILL parry: hitstop, flash, heal, style
            if (pr.onParry == null) pr.onParry = new UnityEngine.Events.UnityEvent();
            pr.onParry.AddListener(() => ProjectileParried(p));
            return p;
        }

        void ProjectileParried(McProjectile p)
        {
            if (p == null) return;
            // back where V1 aims, as ULTRAKILL sends parried projectiles
            var dir = (Punch.GetParryLookTarget() - p.transform.position).normalized;
            Net.Send("PARRY " + p.id + " " + S(dir.x) + " " + S(dir.y) + " " + S(-dir.z));
            FinishProjectile(p);
        }

        void FinishProjectile(McProjectile p)
        {
            projectileDone[p.id] = Time.time + 3f;
            projectiles.Remove(p.id);
            Destroy(p.gameObject);
        }

        /// <summary>Move projectiles between Minecraft ticks and decide hits on V1 here, with V1's real body and
        /// dodge invincibility; Minecraft applies the hit when told.</summary>
        void UpdateProjectiles(NewMovement nm)
        {
            if (projectiles.Count == 0) return;
            var col = nm.playerCollider;
            List<McProjectile> hits = null;
            List<McProjectile> landed = null;
            foreach (var p in projectiles.Values)
            {
                if (p == null) continue;
                var prev = p.Advance();
                if (p.held)
                {
                    // dodged while it hung there: it flies on past; otherwise it lands when its moment is up
                    if (nm.gameObject.layer == 15 || nm.dead)
                    {
                        p.held = false;
                        Net.Send("PRELEASE " + p.id);
                    }
                    else if (Time.time >= p.heldUntil) (landed ??= new List<McProjectile>()).Add(p);
                    continue;
                }
                if (nm.dead || col == null || !col.enabled || nm.gameObject.layer == 15) continue;
                for (int i = 1; i <= 3; i++)
                {
                    var pt = Vector3.Lerp(prev, p.transform.position, i / 3f);
                    if ((col.ClosestPoint(pt) - pt).sqrMagnitude <= p.radius * p.radius)
                    {
                        if (hits == null) hits = new List<McProjectile>();
                        hits.Add(p);
                        break;
                    }
                }
            }
            if (landed != null)
                foreach (var p in landed)
                {
                    Net.Send("PIMPACT " + p.id);
                    FinishProjectile(p);
                }
            if (hits == null) return;
            var cam = MonoSingleton<CameraController>.Instance;
            foreach (var p in hits)
            {
                // the Feedbacker went out just before it arrived: parried
                if (Time.time - lastParryPunch < EarlyParry)
                {
                    try { MonoSingleton<FistControl>.Instance.currentPunch.Parry(false, null); }
                    catch (Exception e) { Plugin.Log.LogWarning("early projectile parry: " + e.Message); }
                    lastParryPunch = -10f;
                    ProjectileParried(p);
                    continue;
                }
                // it hangs right in front of V1's eyes for a moment, flashing: punch it now
                p.held = true;
                p.heldUntil = Time.time + ProjectileHold;
                if (cam != null)
                {
                    var eye = cam.GetDefaultPos();
                    var from = p.transform.position - eye;
                    if (from.sqrMagnitude < 0.01f) from = cam.transform.forward;
                    p.transform.position = eye + from.normalized * 0.6f;
                }
                Net.Send("PHOLD " + p.id);
                var drm = MonoSingleton<DefaultReferenceManager>.Instance;
                if (drm != null && drm.parryableFlash != null) Instantiate(drm.parryableFlash, p.transform.position, Quaternion.identity);
            }
        }

        /// <summary>MELEE mob damage: a Minecraft mob's hit gets ULTRAKILL's parry flash and lands only if V1 doesn't
        /// punch that mob within the parry window.</summary>
        void MeleeHit(int mobId, int damage)
        {
            var nm = MonoSingleton<NewMovement>.Instance;
            if (nm == null || nm.dead) return;
            if (proxies.TryGetValue(mobId, out var p) && p != null)
            {
                if (p.pendingHurt == 0 && Time.time - p.punchedAt < EarlyParry)
                {
                    // V1 punched it a moment before its swing: parried, as if the fist had met the swing
                    p.punchedAt = -10f;
                    p.parriedAt = Time.time;
                    try { MonoSingleton<FistControl>.Instance.currentPunch.Parry(false, p.eid); }
                    catch (Exception e) { Plugin.Log.LogWarning("early parry: " + e.Message); }
                    ReportDamage(p, 5f, false, false, p.center != null ? p.center.position : p.transform.position, true, 0);
                    return;
                }
                if (p.pendingHurt == 0)
                {
                    p.parryUntil = Time.time + ParryWindow;
                    var drm = MonoSingleton<DefaultReferenceManager>.Instance;
                    if (drm != null && drm.parryableFlash != null && p.head != null)
                        Instantiate(drm.parryableFlash, p.head.transform.position, Quaternion.identity);
                }
                p.pendingHurt += damage;
            }
            else
            {
                nm.GetHurt(damage, true);
            }
        }

        void UpdateMelee(NewMovement nm)
        {
            foreach (var p in proxies.Values)
            {
                if (p == null || p.pendingHurt <= 0 || Time.time < p.parryUntil) continue;
                int d = p.pendingHurt;
                p.pendingHurt = 0;
                if (!nm.dead) nm.GetHurt(d, true);
                // the swing landed: so does its knockback
                if (p.hasPendingKnock && !nm.dead) Knock(nm, p.pendingKnock, p.pendingKnockUp);
                p.hasPendingKnock = false;
            }
        }

        // ------------------------------------------------------------ ULTRAKILL's enemies vs Minecraft's mobs

        // ULTRAKILL's own enemies (Spawner Arm, anything spawned) live in the Minecraft world too: Minecraft gets a
        // stand-in for each (UKE) that mobs target and hit (EHURT), and they in turn go for hostile mobs.
        readonly Dictionary<int, EnemyIdentifier> ukEnemies = new Dictionary<int, EnemyIdentifier>();
        readonly Dictionary<int, EnemyIdentifier> lastUkEnemies = new Dictionary<int, EnemyIdentifier>();
        // where each was last seen (its middle): one that dies and goes in the same moment (a drone's blast, Gabriel)
        // can't be asked any more
        readonly Dictionary<int, Vector3> ukEnemyAt = new Dictionary<int, Vector3>();
        // enemies V1 attacked stay on V1 for a while, as ULTRAKILL's own "unless attacked" rule
        readonly Dictionary<int, float> v1AggroUntil = new Dictionary<int, float>();
        float nextUkReport, nextRetarget;
        static readonly List<Collider> colBuf = new List<Collider>();

        static bool IsProxy(EnemyIdentifier eid) => eid != null && eid.GetComponent<McProxy>() != null;

        public void MarkV1Attack(EnemyIdentifier eid)
        {
            if (eid != null) v1AggroUntil[eid.GetInstanceID()] = Time.time + 8f;
        }

        bool V1Aggro(EnemyIdentifier eid) => v1AggroUntil.TryGetValue(eid.GetInstanceID(), out var t) && Time.time < t;

        /// <summary>Where an enemy's body is: its hitboxes (limbs, body, the collider it walks with).</summary>
        internal static Bounds EnemyBounds(EnemyIdentifier eid)
        {
            eid.GetComponentsInChildren(false, colBuf);
            bool any = false;
            var b = new Bounds();
            foreach (var c in colBuf)
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                int l = c.gameObject.layer;
                if (l != 10 && l != 11 && l != 12) continue;
                if (!any) { b = c.bounds; any = true; }
                else b.Encapsulate(c.bounds);
            }
            if (!any) b = new Bounds(eid.transform.position + Vector3.up * 1.75f, new Vector3(1.5f, 3.5f, 1.5f));
            return b;
        }

        void UpdateUkEnemies(NewMovement nm)
        {
            if (Time.unscaledTime < nextUkReport) return;
            nextUkReport = Time.unscaledTime + 0.1f;
            var tracker = MonoSingleton<EnemyTracker>.Instance;
            if (tracker == null) return;
            lastUkEnemies.Clear();
            foreach (var kv in ukEnemies) lastUkEnemies[kv.Key] = kv.Value;
            ukEnemies.Clear();
            var sb = new StringBuilder("UKE ");
            foreach (var eid in tracker.GetCurrentEnemies())
            {
                if (eid == null || eid.dead || !eid.gameObject.activeInHierarchy || IsProxy(eid) || IsPuppet(eid)) continue;
                int id = eid.GetInstanceID();
                ukEnemies[id] = eid;
                var b = EnemyBounds(eid);
                ukEnemyAt[id] = b.center;
                var feet = UkToMc(new Vector3(b.center.x, b.min.y, b.center.z));
                float w = Mathf.Clamp(Mathf.Max(b.size.x, b.size.z) / K, 0.3f, 12f), h = Mathf.Clamp(b.size.y / K, 0.3f, 16f);
                sb.Append(id).Append(',').Append(eid.enemyType).Append(',').Append(S(feet.x)).Append(',').Append(S(feet.y)).Append(',').Append(S(feet.z))
                  .Append(',').Append(S(w)).Append(',').Append(S(h)).Append(',').Append(S(eid.health))
                  // what the other players' ULTRAKILLs need to show it as a puppet
                  .Append(',').Append(PuppetKey(eid)).Append(',').Append(S(eid.transform.eulerAngles.y)).Append(',').Append(AnimState(eid)).Append(';');
            }
            Net.Send(sb.ToString());
            // the ones that died since: Minecraft drops their experience (a destroyed enemy's fields still read: one
            // that died and was removed at once died all the same)
            foreach (var kv in lastUkEnemies)
            {
                if (ukEnemies.ContainsKey(kv.Key)) continue;
                ukEnemyAt.TryGetValue(kv.Key, out var at);
                ukEnemyAt.Remove(kv.Key);
                if ((object)kv.Value != null && kv.Value.dead)
                {
                    ReportEnemyDeath(kv.Value, at);
                    // its puppets in the other players' games die with it
                    Net.Send("UKDIE " + kv.Key);
                }
            }
            SweepGrind();
            if (Time.unscaledTime >= nextRetarget)
            {
                nextRetarget = Time.unscaledTime + 0.5f;
                Retarget(nm);
            }
        }

        /// <summary>Each ULTRAKILL enemy goes for the nearest hostile mob while that mob is clearly nearer than V1, and
        /// back to V1 when V1 is the nearer one or attacked it (ULTRAKILL's prioritizeEnemiesUnlessAttacked).</summary>
        void Retarget(NewMovement nm)
        {
            if (ukEnemies.Count == 0) return;
            var v1 = nm.transform.position;
            foreach (var eid in ukEnemies.Values)
            {
                if (eid == null || eid.dead || eid.IgnorePlayer && !eid.AttackEnemies) continue;
                // a boss came for the V1s and nothing else (AimBosses); with ours down, the others go for teammates there too
                if (IsBossEid(eid) || AimOf(eid) > 0) continue;
                var pos = eid.transform.position;
                McProxy best = null;
                float bestD = 80f * 80f;
                foreach (var p in proxies.Values)
                {
                    if (p == null || !p.hostile || p.eid == null || p.center == null) continue;
                    float d = (p.center.position - pos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = p; }
                }
                var cur = eid.target;
                bool onMob = cur != null && cur.isEnemy && cur.isValid && IsProxy(cur.enemyIdentifier);
                float dV1 = nm.dead ? float.MaxValue : (v1 - pos).sqrMagnitude;
                if (V1Aggro(eid))
                {
                    if (onMob) TargetV1(eid);
                    continue;
                }
                if (best != null && bestD < dV1 * 0.64f)
                {
                    // already after a mob that's about as close: don't flip between two
                    if (onMob && (cur.enemyIdentifier == best.eid || (cur.position - pos).sqrMagnitude < bestD * 1.5f)) continue;
                    eid.attackEnemies = true;
                    eid.prioritizeEnemiesUnlessAttacked = true;
                    eid.target = new EnemyTarget(best.eid);
                }
                else if (onMob && (best == null || dV1 < bestD * 0.36f))
                {
                    TargetV1(eid);
                }
            }
        }

        static void TargetV1(EnemyIdentifier eid)
        {
            eid.prioritizeEnemiesUnlessAttacked = false;
            if (!eid.IgnorePlayer) eid.target = EnemyTarget.TrackPlayer();
        }

        /// <summary>Which of ULTRAKILL's enemies just hurt this mob: the one going for it, else the nearest.</summary>
        public int AttackerOf(McProxy p)
        {
            if (p == null) return 0;
            EnemyIdentifier best = null;
            float bestD = float.MaxValue;
            bool bestTargets = false;
            foreach (var eid in ukEnemies.Values)
            {
                if (eid == null || eid.dead) continue;
                bool targets = eid.target != null && eid.target.enemyIdentifier == p.eid;
                float d = (eid.transform.position - p.transform.position).sqrMagnitude;
                if ((targets && !bestTargets) || (targets == bestTargets && d < bestD))
                {
                    best = eid;
                    bestD = d;
                    bestTargets = targets;
                }
            }
            return best != null ? best.GetInstanceID() : 0;
        }

        /// <summary>A mob's hit (bite, arrow, blast) on one of ULTRAKILL's enemies: real ULTRAKILL damage (its own blood
        /// and death), and the enemy turns on that mob unless V1 has its attention.</summary>
        void EnemyHurtByMob(int ukId, float mcDamage, int mobId, bool byV1 = false, string kind = null, int mate = 0)
        {
            if (!ukEnemies.TryGetValue(ukId, out var eid) || eid == null || eid.dead) return;
            var b = EnemyBounds(eid);
            var limb = eid.GetComponentInChildren<EnemyIdentifierIdentifier>();
            // V1's own Minecraft weapon (a sword in Minecraft hands) is V1's melee; lava and fire burn
            eid.hitter = kind == "fire" ? "fire" : byV1 ? "punch" : "enemy";
            eid.DeliverDamage(limb != null ? limb.gameObject : eid.gameObject, Vector3.zero, b.center, mcDamage / 10f, false, 0f, null);
            if (byV1)
            {
                // ours, or a teammate's (it counts towards whom the enemy goes for)
                if (mate != 0) AddThreat(eid, mate, mcDamage / 10f);
                else MarkV1Attack(eid);
                return;
            }
            if (!eid.dead && !V1Aggro(eid) && proxies.TryGetValue(mobId, out var p) && p != null && p.eid != null)
            {
                eid.attackEnemies = true;
                eid.prioritizeEnemiesUnlessAttacked = true;
                eid.target = new EnemyTarget(p.eid);
            }
        }

        /// <summary>Debug: put one of ULTRAKILL's enemies on the ground in front of V1, the way the Spawner Arm does.</summary>
        void SpawnEnemy(string name, float dist)
        {
            SpawnableObject so = null;
            foreach (var db in Resources.FindObjectsOfTypeAll<SpawnableObjectsDatabase>())
            {
                if (db.enemies == null) continue;
                foreach (var e in db.enemies)
                {
                    if (e == null || e.gameObject == null) continue;
                    if (string.Equals(e.objectName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(e.enemyType.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    {
                        so = e;
                        break;
                    }
                }
                if (so != null) break;
            }
            var cc = MonoSingleton<CameraController>.Instance;
            if (so == null || cc == null)
            {
                Net.Send("SPAWNED none");
                return;
            }
            var fwd = cc.transform.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.forward;
            var p = cc.transform.position + fwd * dist;
            if (Physics.Raycast(p + Vector3.up * 20f, Vector3.down, out var hit, 80f, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore))
                p = hit.point + Vector3.up * so.spawnOffset;
            var go = Instantiate(so.gameObject, p, Quaternion.LookRotation(-fwd));
            (go.GetComponent<Sandbox.EnemySpawnableInstance>() ?? go.AddComponent<Sandbox.EnemySpawnableInstance>()).sourceObject = so;
            go.SetActive(true);
            Net.Send("SPAWNED " + so.objectName + " " + p);
        }

        // ------------------------------------------------------------ navmesh on Minecraft terrain

        // ULTRAKILL's enemies walk on a navmesh; the Sandbox's own is for its floor, which is gone. Bake one from the
        // Minecraft terrain around V1 instead, and again as V1 moves on or the terrain changes.
        readonly List<NavMeshBuildSettings> navSettings = new List<NavMeshBuildSettings>();
        readonly List<NavMeshData> navData = new List<NavMeshData>();
        readonly List<NavMeshDataInstance> navInstances = new List<NavMeshDataInstance>();
        readonly List<NavMeshBuildSource> navSources = new List<NavMeshBuildSource>();
        AsyncOperation[] navOps;
        Bounds navBounds;
        Vector3 navCenter;
        bool navDirty = true, navBaking;
        float nextNavBake, navStarted;
        // (wide enough that enemies coming from afar have ground to walk on before they reach V1)
        const float NavRadiusBlocks = 56f, NavHeightBlocks = 56f;

        void MarkNavDirty(Bounds changed)
        {
            if (!navDirty && navBounds.size != Vector3.zero && navBounds.Intersects(changed)) navDirty = true;
        }

        void UpdateNavMesh(NewMovement nm)
        {
            if (navBaking)
            {
                foreach (var op in navOps) if (op != null && !op.isDone) return;
                navBaking = false;
                Plugin.Log.LogInfo("navmesh: baked " + navSources.Count + " terrain sections for " + navSettings.Count + " agent types in " + (Time.realtimeSinceStartup - navStarted).ToString("0.00") + " s");
                SettleAgents();
            }
            var pos = nm.transform.position;
            var flat = new Vector2(pos.x - navCenter.x, pos.z - navCenter.z);
            bool moved = flat.sqrMagnitude > (12f * K) * (12f * K) || Mathf.Abs(pos.y - navCenter.y) > 10f * K;
            if ((!navDirty && !moved) || Time.unscaledTime < nextNavBake || sections.Count == 0) return;
            BakeNavMesh(pos);
        }

        void BakeNavMesh(Vector3 center)
        {
            navDirty = false;
            nextNavBake = Time.unscaledTime + 1.5f;
            navCenter = center;
            if (navSettings.Count == 0)
            {
                // the Sandbox's navmesh belongs to its (hidden) floor
                NavMesh.RemoveAllNavMeshData();
                for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
                {
                    var s = NavMesh.GetSettingsByIndex(i);
                    // a Minecraft block is ~1.94 units: let enemies step up one, as they would up stairs
                    s.agentClimb = Mathf.Max(s.agentClimb, K * 1.05f);
                    s.overrideVoxelSize = true;
                    s.voxelSize = Mathf.Clamp(s.agentRadius / 2f, 0.2f, 0.5f);
                    var d = new NavMeshData(s.agentTypeID);
                    navSettings.Add(s);
                    navData.Add(d);
                    navInstances.Add(NavMesh.AddNavMeshData(d));
                }
            }
            navBounds = new Bounds(center, new Vector3(NavRadiusBlocks * 2f * K, NavHeightBlocks * K, NavRadiusBlocks * 2f * K));
            navSources.Clear();
            foreach (var go in sections.Values)
            {
                if (go == null) continue;
                var mc = go.GetComponent<MeshCollider>();
                if (mc == null || mc.sharedMesh == null || !navBounds.Intersects(mc.bounds)) continue;
                navSources.Add(new NavMeshBuildSource { shape = NavMeshBuildSourceShape.Mesh, sourceObject = mc.sharedMesh, transform = go.transform.localToWorldMatrix, area = 0 });
            }
            navOps = new AsyncOperation[navSettings.Count];
            for (int i = 0; i < navSettings.Count; i++) navOps[i] = NavMeshBuilder.UpdateNavMeshDataAsync(navData[i], navSettings[i], navSources, navBounds);
            navBaking = true;
            navStarted = Time.realtimeSinceStartup;
        }

        /// <summary>Enemies that were standing where there was no navmesh yet get put onto the new one.</summary>
        void SettleAgents()
        {
            foreach (var eid in ukEnemies.Values)
            {
                if (eid == null || eid.dead) continue;
                var nma = eid.GetComponent<NavMeshAgent>();
                if (nma == null || !nma.enabled || nma.isOnNavMesh) continue;
                if (NavMesh.SamplePosition(eid.transform.position, out var hit, 4f, nma.areaMask)) nma.Warp(hit.position);
            }
        }

        // ------------------------------------------------------------ Minecraft hands, hitstop, audio

        bool hands;

        void SetHands(bool on)
        {
            hands = on;
            var gc = levelPrepared ? MonoSingleton<GunControl>.Instance : null;
            if (gc == null) return;
            try
            {
                if (on) gc.NoWeapon();
                // at a shop's screen the guns stay away until V1 steps back (the shop brings them out itself)
                else if (!shopTouch) gc.YesWeapon();
            }
            catch (Exception e) { Plugin.Log.LogWarning("hands: " + e.Message); }
        }

        /// <summary>While Minecraft has V1's hands, a gun that comes back out (respawn, pickups) goes away again.</summary>
        void KeepHands()
        {
            if (!hands) return;
            var gc = MonoSingleton<GunControl>.Instance;
            if (gc != null && gc.currentWeapon != null && gc.currentWeapon.activeSelf) gc.NoWeapon();
        }

        float nextGunCheck;

        /// <summary>V1's gun is out whenever it should be: rebuilding the loadout (V1's gear arriving at spawn, a
        /// purchase) left the HUD showing a gun that wasn't drawn until the weapon was swapped.</summary>
        void KeepGunOut()
        {
            if (hands || shopTouch || SteveView || uiMode || mcPaused || !v1Landed || Time.unscaledTime < nextGunCheck) return;
            nextGunCheck = Time.unscaledTime + 0.5f;
            var gc = MonoSingleton<GunControl>.Instance;
            var nm = MonoSingleton<NewMovement>.Instance;
            if (gc == null || nm == null || nm.dead || gc.slots == null) return;
            if (gc.currentWeapon != null && gc.currentWeapon.activeInHierarchy) return;
            bool any = false;
            foreach (var slot in gc.slots) if (slot != null && slot.Count > 0) any = true;
            if (!any) return;
            try
            {
                gc.YesWeapon();
                Plugin.Log.LogInfo("gun brought back out (" + (gc.currentWeapon != null ? gc.currentWeapon.name : "none") + ")");
            }
            catch (Exception e) { Plugin.Log.LogDebug("gun out: " + e.Message); }
        }

        static readonly AccessTools.FieldRef<TimeController, float> CurrentStop = AccessTools.FieldRefAccess<TimeController, float>("currentStop");
        bool frozenSent;

        /// <summary>ULTRAKILL's hitstop (parries, heavy hits) stops time; Minecraft stops with it for exactly as long.</summary>
        void UpdateFreeze()
        {
            var tc = MonoSingleton<TimeController>.Instance;
            bool frozen = tc != null && Time.timeScale == 0f && CurrentStop(tc) > 0f;
            if (frozen == frozenSent) return;
            frozenSent = frozen;
            Net.Send("FREEZE " + (frozen ? 1 : 0));
        }

        /// <summary>ULTRAKILL never pauses its listener itself; if anything left it paused or silent, V1 would be mute.</summary>
        void KeepAudio()
        {
            if (AudioListener.pause != mcPaused) AudioListener.pause = mcPaused;
        }

        bool mcPaused;

        // what ULTRAKILL's own pause switches off besides time (V1's movement, camera and guns), as it was
        bool pausedNm, pausedCam, pausedGuns, pauseSaved;

        void SetMcPaused(bool on)
        {
            if (on == mcPaused) return;
            mcPaused = on;
            AudioListener.pause = on;
            var nm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
            var cc = levelPrepared ? MonoSingleton<CameraController>.Instance : null;
            var gc = levelPrepared ? MonoSingleton<GunControl>.Instance : null;
            if (on)
            {
                // as ULTRAKILL's own pause menu does: V1 stops taking input and moving, its camera and guns stop
                if (nm != null && cc != null && gc != null)
                {
                    pausedNm = nm.enabled;
                    pausedCam = cc.activated;
                    pausedGuns = gc.activated;
                    pauseSaved = true;
                    nm.enabled = false;
                    cc.activated = false;
                    gc.activated = false;
                }
            }
            else
            {
                if (pauseSaved && nm != null && cc != null && gc != null)
                {
                    nm.enabled = pausedNm;
                    cc.activated = pausedCam;
                    gc.activated = pausedGuns;
                }
                pauseSaved = false;
                // back to ULTRAKILL's own time (its slow-motion and assist speed included)
                var tc = levelPrepared ? MonoSingleton<TimeController>.Instance : null;
                Time.timeScale = tc != null ? tc.timeScale * tc.timeScaleModifier : 1f;
            }
            HoldPause();
            Plugin.Log.LogInfo("Minecraft " + (on ? "paused" : "resumed"));
        }

        /// <summary>Time stays stopped while Minecraft is paused, whatever else in ULTRAKILL restores it (a hitstop
        /// ending, slow-motion easing back).</summary>
        void HoldPause()
        {
            if (mcPaused && Time.timeScale != 0f) Time.timeScale = 0f;
        }

        // ------------------------------------------------------------ V1's portrait for Minecraft's inventory

        // The real V1 body (the platformer V1 from 4-S) posed by its own animator in a corner of the world, filmed by
        // its own camera into a texture that Minecraft shows where Steve's paper doll would be. It is drawn at twice
        // the size Minecraft shows it (with MSAA) and averaged down, so its edges are smooth at any GUI scale.
        const int MaxDollW = 1024, MaxDollH = 1536, DollHeader = 16;
        GameObject dollRoot;
        Transform dollModel, dollHead;
        Camera dollCam;
        RenderTexture dollRT, dollOutRT;
        Bounds dollBounds;
        int dollLayer = -1;
        bool dollFailed, dollBusy;
        float dollWanted, dollYaw, dollPitch;
        int dollSeq, dollW, dollH, dollTargetW = 192, dollTargetH = 288;
        MemoryMappedFile dollMmf;
        MemoryMappedViewAccessor dollView;
        unsafe byte* dollBase;

        unsafe void MapDoll()
        {
            var path = Path.Combine(Path.GetTempPath(), "ultracraft_doll" + InstanceSuffix + ".bin");
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            long need = DollHeader + (long)MaxDollW * MaxDollH * 4L;
            if (fs.Length < need) fs.SetLength(need);
            dollMmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
            dollView = dollMmf.CreateViewAccessor();
            byte* p = null;
            dollView.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            dollBase = p;
        }

        unsafe void UpdateDoll()
        {
            bool wanted = Time.unscaledTime < dollWanted;
            if (dollCam != null) dollCam.enabled = wanted;
            if (!wanted) return;
            if (dollRoot == null && !dollFailed) BuildDoll();
            if (dollRoot == null) return;
            SizeDoll();
            // Minecraft turns its paper doll's body toward the cursor (the head turns further, in LateUpdate)
            dollModel.localRotation = Quaternion.Euler(0f, dollYaw, 0f);
            dollModel.rotation = Quaternion.AngleAxis(-dollPitch * 0.4f, dollModel.right) * dollModel.rotation;
            if (dollBusy || dollBase == null) return;
            // last frame's supersampled picture, averaged 2x2 down to the size Minecraft shows it at
            Graphics.Blit(dollRT, dollOutRT);
            dollBusy = true;
            int seq = ++dollSeq, w = dollW, h = dollH;
            var dst = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(dollBase + DollHeader, w * h * 4, Allocator.None);
            AsyncGPUReadback.RequestIntoNativeArray(ref dst, dollOutRT, 0, TextureFormat.RGBA32, r =>
            {
                dollBusy = false;
                if (r.hasError) return;
                int* hdr = (int*)dollBase;
                hdr[1] = w;
                hdr[2] = h;
                hdr[3] = 1; // version; rows bottom-up
                Thread.MemoryBarrier();
                hdr[0] = seq;
            });
        }

        /// <summary>The portrait's textures at the size Minecraft asked for, and the camera framing V1 in them.</summary>
        void SizeDoll()
        {
            int w = dollTargetW, h = dollTargetH;
            if (dollOutRT != null && w == dollW && h == dollH) return;
            if (dollRT != null)
            {
                dollCam.targetTexture = null;
                dollRT.Release();
                Destroy(dollRT);
            }
            if (dollOutRT != null)
            {
                dollOutRT.Release();
                Destroy(dollOutRT);
            }
            dollW = w;
            dollH = h;
            dollRT = new RenderTexture(w * 2, h * 2, 24, RenderTextureFormat.ARGB32) { name = "Ultracraft V1 doll", antiAliasing = 4, filterMode = FilterMode.Bilinear };
            dollRT.Create();
            dollOutRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "Ultracraft V1 doll out", filterMode = FilterMode.Bilinear };
            dollOutRT.Create();
            dollCam.targetTexture = dollRT;
            // tall enough for V1, wide enough for its wings
            dollCam.orthographicSize = Mathf.Max(dollBounds.size.y * 0.53f, dollBounds.size.x * 0.53f * h / w);
            dollBusy = false;
        }

        /// <summary>After the animator has posed the body: the head turns on toward the cursor and tilts up or down to
        /// it, as Minecraft's doll's head does (twice the body's turn).</summary>
        void PoseDollHead()
        {
            if (dollHead == null || dollModel == null || !(Time.unscaledTime < dollWanted)) return;
            dollHead.rotation = Quaternion.AngleAxis(dollYaw, dollModel.up) * Quaternion.AngleAxis(-dollPitch, dollModel.right) * dollHead.rotation;
        }

        void BuildDoll()
        {
            try
            {
                var pt = MonoSingleton<PlayerTracker>.Instance;
                var prefab = pt != null ? pt.platformerPlayerPrefab : null;
                var pm = prefab != null ? prefab.GetComponentInChildren<PlatformerMovement>(true) : null;
                if (pm == null)
                {
                    dollFailed = true;
                    Plugin.Log.LogWarning("V1 doll: no platformer V1 in this level");
                    return;
                }
                // a layer none of ULTRAKILL's cameras draw (every layer has a name, e.g. 31 is SpecialLighting), so the
                // doll is only ever seen by its own camera and nothing of ULTRAKILL's changes
                int used = 0;
                foreach (var cam in Camera.allCameras) used |= cam.cullingMask;
                for (int i = 30; i >= 8 && dollLayer < 0; i--) if ((used & (1 << i)) == 0) dollLayer = i;
                if (dollLayer < 0) dollLayer = 30;
                dollRoot = new GameObject("Ultracraft V1 doll");
                dollRoot.transform.position = new Vector3(0f, -4000f, 0f);
                // built inactive so none of the platformer's scripts wake up; only the body and its animator stay
                var holder = new GameObject("holder");
                holder.SetActive(false);
                holder.transform.SetParent(dollRoot.transform, false);
                var body = Instantiate(pm.gameObject, holder.transform);
                body.transform.localPosition = Vector3.zero;
                body.transform.localRotation = Quaternion.identity;
                foreach (var c in body.GetComponentsInChildren<MonoBehaviour>(true)) DestroyImmediate(c);
                foreach (var c in body.GetComponentsInChildren<Joint>(true)) DestroyImmediate(c);
                foreach (var c in body.GetComponentsInChildren<Collider>(true)) DestroyImmediate(c);
                foreach (var c in body.GetComponentsInChildren<Rigidbody>(true)) DestroyImmediate(c);
                foreach (var c in body.GetComponentsInChildren<NavMeshAgent>(true)) DestroyImmediate(c);
                foreach (var c in body.GetComponentsInChildren<AudioSource>(true)) DestroyImmediate(c);
                foreach (var c in body.GetComponentsInChildren<Camera>(true)) DestroyImmediate(c);
                foreach (var c in body.GetComponentsInChildren<Light>(true)) DestroyImmediate(c);
                foreach (var r in body.GetComponentsInChildren<Renderer>(true)) if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) r.enabled = false;
                foreach (var t in body.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = dollLayer;
                var anim = body.GetComponent<Animator>();
                if (anim != null)
                {
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    anim.updateMode = AnimatorUpdateMode.UnscaledTime;
                }
                dollModel = body.transform;
                // the head bone: the highest transform named for it that isn't itself a mesh
                foreach (var t in body.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name.IndexOf("head", StringComparison.OrdinalIgnoreCase) < 0 || t.GetComponent<Renderer>() != null) continue;
                    dollHead = t;
                    Plugin.Log.LogInfo("V1 doll head: " + t.name + " under " + (t.parent != null ? t.parent.name : "-"));
                    break;
                }
                // a soft fill so the side away from the key light isn't black: ULTRAKILL's shader takes its ambient
                // from the light model, which a property block can set for these renderers alone
                var fill = new MaterialPropertyBlock();
                foreach (var r in body.GetComponentsInChildren<Renderer>(true))
                {
                    r.GetPropertyBlock(fill);
                    fill.SetVector("glstate_lightmodel_ambient", new Vector4(0.36f, 0.38f, 0.46f, 1f));
                    r.SetPropertyBlock(fill);
                }
                holder.SetActive(true);
                // frame the body
                dollBounds = new Bounds(body.transform.position + Vector3.up * 1.75f, new Vector3(1f, 3.5f, 1f));
                bool any = false;
                foreach (var r in body.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled) continue;
                    if (!any) { dollBounds = r.bounds; any = true; }
                    else dollBounds.Encapsulate(r.bounds);
                }
                var camGo = new GameObject("doll camera");
                camGo.transform.SetParent(dollRoot.transform, false);
                camGo.transform.position = dollBounds.center + Vector3.forward * (dollBounds.size.y * 3f);
                camGo.transform.rotation = Quaternion.LookRotation(Vector3.back);
                dollCam = camGo.AddComponent<Camera>();
                dollCam.orthographic = true;
                dollCam.clearFlags = CameraClearFlags.SolidColor;
                dollCam.backgroundColor = new Color(0, 0, 0, 0);
                dollCam.cullingMask = 1 << dollLayer;
                dollCam.nearClipPlane = 0.05f;
                dollCam.farClipPlane = dollBounds.size.y * 8f;
                dollCam.allowHDR = false;
                dollCam.allowMSAA = true;
                dollCam.useOcclusionCulling = false;
                // key light from the upper front, a little to V1's right
                var key = new GameObject("doll key light").AddComponent<Light>();
                key.transform.SetParent(dollRoot.transform, false);
                key.transform.rotation = Quaternion.Euler(30f, 200f, 0f);
                key.type = LightType.Directional;
                key.cullingMask = 1 << dollLayer;
                key.color = new Color(1f, 0.97f, 0.92f);
                key.intensity = 1.15f;
                SizeDoll();
                Plugin.Log.LogInfo("V1 doll ready (layer " + dollLayer + ", body " + dollBounds.size + ", head " + (dollHead != null ? dollHead.name : "none")
                                   + ", animator " + (anim != null && anim.runtimeAnimatorController != null) + ")");
            }
            catch (Exception e)
            {
                dollFailed = true;
                Plugin.Log.LogWarning("V1 doll: " + e);
            }
        }

        // ------------------------------------------------------------ weapons on the world

        float lastBoomTime;
        Vector3 lastBoomPos;
        float lastBoomSize;

        /// <summary>enemy: an enemy's blast (Minecraft's "Enemies Break Blocks" setting decides about the blocks).</summary>
        public void ReportExplosion(Vector3 pos, float radius, bool big = false, bool nuke = false, bool enemy = false)
        {
            if (!levelPrepared || !originSet || !Net.Connected || radius <= 0f) return;
            // one blast is often several Explosion spheres at the same spot; keep the biggest (a nuke's spheres all
            // count as the one nuke)
            bool same = Time.time - lastBoomTime < 0.1f && Vector3.Distance(pos, lastBoomPos) < 2f;
            if (same && (lastBoomNuke || (!nuke && radius <= lastBoomSize))) return;
            lastBoomTime = Time.time;
            lastBoomPos = pos;
            lastBoomSize = radius;
            lastBoomNuke = nuke;
            var m = UkToMc(pos);
            SendBoom("BOOM " + S(m.x) + " " + S(m.y) + " " + S(m.z) + " " + S(radius / K) + (nuke ? " 2" : big ? " 1" : enemy ? " e" : ""));
        }

        readonly List<string> heldBooms = new List<string>();

        /// <summary>A blast set off during an impact frame (the nuke's freeze, a parry's) goes off in Minecraft when
        /// ULTRAKILL's time moves again and its fireball actually blooms, not on the frozen frame.</summary>
        void SendBoom(string msg)
        {
            if (Time.timeScale <= 0f) heldBooms.Add(msg);
            else Net.Send(msg);
        }

        void FlushBooms()
        {
            if (heldBooms.Count == 0 || Time.timeScale <= 0f || mcPaused) return;
            foreach (var m in heldBooms) Net.Send(m);
            heldBooms.Clear();
        }

        bool lastBoomNuke;

        object lastTunnelSource;

        /// <summary>The Electric railcannon doesn't stop at the wall: it bores a tunnel through the terrain behind.</summary>
        public void ReportTunnel(Vector3 point, Vector3 dir, float length, float radius)
        {
            if (!levelPrepared || !originSet || !Net.Connected) return;
            var m = UkToMc(point);
            dir = dir.normalized;
            Net.Send("RAIL " + S(m.x) + " " + S(m.y) + " " + S(m.z) + " " + S(dir.x) + " " + S(dir.y) + " " + S(-dir.z) + " " + S(length) + " " + S(radius));
        }

        /// <summary>A weapon struck Minecraft terrain: the block there takes damage (cracks, then breaks and drops,
        /// by its hardness). damage is in ULTRAKILL units; radius (blocks) spreads it for heavy impacts.</summary>
        /// <summary>aim: the way the shot travelled (default: dir); Minecraft clears grass, flowers and vines it passed
        /// through on the way, which ULTRAKILL can't hit (they have no collision).</summary>
        public void ReportBlockHit(Vector3 point, Vector3 dir, float damage, float radius = 0f, Vector3 aim = default, bool enemy = false)
        {
            if (!levelPrepared || !originSet || !Net.Connected || damage <= 0f) return;
            var m = UkToMc(point);
            dir = dir.normalized;
            aim = aim == Vector3.zero ? dir : aim.normalized;
            Net.Send("HIT " + S(m.x) + " " + S(m.y) + " " + S(m.z) + " " + S(dir.x) + " " + S(dir.y) + " " + S(-dir.z) + " " + S(damage) + " " + S(radius)
                     + " " + S(aim.x) + " " + S(aim.y) + " " + S(-aim.z) + (enemy ? " e" : ""));
        }

        /// <summary>From SceneHelper.CreateEnviroGibs, ULTRAKILL's "something hit the environment here": find the
        /// surface it means and, if it is Minecraft's, damage the block.</summary>
        public void ReportEnvironmentHit(Vector3 position, Vector3 direction, float distance, float damage, float radius = 0f)
        {
            if (!levelPrepared || !originSet || damage <= 0f || direction == Vector3.zero) return;
            if (Physics.Raycast(position, direction.normalized, out var h, distance + 1f, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore)
                && IsMinecraftTerrain(h.transform))
            {
                // the environment hit only knows the surface normal; the shot's own direction is where it was aimed
                var aim = HitContext.aim != Vector3.zero ? HitContext.aim : direction;
                if (HitContext.tunnel > 0f)
                {
                    // one tunnel per beam, however many times the beam reports the wall; it bores on the way it was aimed
                    if (HitContext.source != null && HitContext.source == lastTunnelSource) return;
                    lastTunnelSource = HitContext.source;
                    ReportTunnel(h.point, aim, HitContext.tunnel, Mathf.Max(radius, 1.5f));
                    return;
                }
                ReportBlockHit(h.point, direction, damage, radius, aim, IsEnemySource(HitContext.source));
            }
        }

        readonly Dictionary<Vector3Int, float> fireSent = new Dictionary<Vector3Int, float>();

        /// <summary>Burning gasoline (Firestarter) sets the Minecraft blocks it sits on alight.</summary>
        /// <summary>An enemy's attack (its shots, beams, blasts) rather than V1's: Minecraft's "Enemies Break Blocks"
        /// setting decides about the blocks it hits.</summary>
        public static bool IsEnemySource(object o)
        {
            switch (o)
            {
                case RevolverBeam b: return b.beamType == BeamType.Enemy;
                case Nail n: return n.enemy;
                case Projectile p: return !(p.friendly || p.playerBullet);
            }
            return false;
        }

        public void ReportFire(Vector3 position, bool enemy = false)
        {
            if (!levelPrepared || !originSet || !Net.Connected) return;
            var m = UkToMc(position);
            var cell = Vector3Int.FloorToInt(m);
            if (fireSent.TryGetValue(cell, out var t) && Time.time - t < 2f) return;
            fireSent[cell] = Time.time;
            if (fireSent.Count > 512) fireSent.Clear();
            Net.Send("FIRE " + S(m.x) + " " + S(m.y) + " " + S(m.z) + (enemy ? " e" : ""));
        }

        /// <summary>Debug: throw a real coin above V1 and "shoot" it the way a revolver beam does.</summary>
        IEnumerator TestCoin()
        {
            Revolver rev = null;
            foreach (var r in Resources.FindObjectsOfTypeAll<Revolver>()) if (r.coin != null && r.gameObject.scene.IsValid()) { rev = r; break; }
            var cc = MonoSingleton<CameraController>.Instance;
            if (rev == null || cc == null) { Plugin.Log.LogWarning("TESTCOIN: no revolver with coins"); yield break; }
            var coin = Instantiate(rev.coin, cc.GetDefaultPos() + Vector3.up * 2f, Quaternion.identity).GetComponent<Coin>();
            coin.sourceWeapon = rev.gameObject;
            yield return new WaitForSeconds(0.3f);
            if (coin == null) yield break;
            Plugin.Log.LogInfo("TESTCOIN: shooting coin at " + coin.transform.position + ", proxies " + proxies.Count);
            coin.DelayedReflectRevolver(coin.transform.position);
        }

        void ApplyBlocks(string rest)
        {
            if (!levelPrepared || !originSet || blockRoot == null) return;
            // x0,y0,z0,x1,y1,z1;... in Minecraft coordinates
            var seen = new HashSet<string>();
            foreach (var entry in rest.Split(';'))
            {
                if (entry.Length == 0) continue;
                seen.Add(entry);
                if (blocks.ContainsKey(entry)) continue;
                var a = entry.Split(',');
                var mn = new Vector3(F(a[0]), F(a[1]), F(a[2]));
                var mx = new Vector3(F(a[3]), F(a[4]), F(a[5]));
                var u0 = McToUk(mn);
                var u1 = McToUk(mx);
                var center = (u0 + u1) * 0.5f;
                var size = new Vector3(Mathf.Abs(u1.x - u0.x), Mathf.Abs(u1.y - u0.y), Mathf.Abs(u1.z - u0.z));
                GameObject go;
                if (blockPool.Count > 0) { go = blockPool.Pop(); go.SetActive(true); }
                else
                {
                    go = new GameObject("b");
                    go.transform.SetParent(blockRoot.transform, false);
                    go.layer = 8; // Environment
                    go.tag = "Floor";
                    go.AddComponent<BoxCollider>();
                    // an (empty) renderer makes ULTRAKILL treat it as static environment, which is what weapons check
                    // before their environment hits (gibs, our block damage)
                    go.AddComponent<MeshRenderer>();
                }
                go.transform.position = center;
                go.GetComponent<BoxCollider>().size = size;
                blocks[entry] = go;
            }
            var remove = new List<string>();
            foreach (var kv in blocks) if (!seen.Contains(kv.Key)) remove.Add(kv.Key);
            foreach (var k in remove)
            {
                var go = blocks[k];
                blocks.Remove(k);
                go.SetActive(false);
                blockPool.Push(go);
            }
        }

        /// <summary>EPOS id,x,y,z;... where Minecraft draws each mob this frame (between ticks). The stand-in sits there,
        /// so the mob hides ULTRAKILL's enemies behind it exactly, even mid-knockback.</summary>
        void ApplyDrawnPositions(string rest)
        {
            if (!levelPrepared || !originSet) return;
            foreach (var entry in rest.Split(';'))
            {
                if (entry.Length == 0) continue;
                var a = entry.Split(',');
                if (a.Length < 4 || !int.TryParse(a[0], out int id) || !proxies.TryGetValue(id, out var p) || p == null) continue;
                p.transform.position = McToUk(new Vector3(F(a[1]), F(a[2]), F(a[3])));
                p.drawnAt = Time.unscaledTime;
            }
        }

        void ApplyEntities(string rest)
        {
            if (!levelPrepared || !originSet) return;
            // id,type,x,y,z,w,h,eye,hp,maxhp,hostile,yaw,burning;...
            var seen = new HashSet<int>();
            foreach (var entry in rest.Split(';'))
            {
                if (entry.Length == 0) continue;
                var a = entry.Split(',');
                int id = int.Parse(a[0]);
                seen.Add(id);
                var feet = McToUk(new Vector3(F(a[2]), F(a[3]), F(a[4])));
                float w = F(a[5]) * K, h = F(a[6]) * K, eye = F(a[7]) * K;
                float hp = F(a[8]), maxHp = F(a[9]);
                if (proxies.TryGetValue(id, out var p) && p != null && p.type != a[1])
                {
                    // the same entity as something else now: a player who became V1 (V1's body, not a hidden box) or
                    // went back to Steve. Made anew; kept as it was, a player who joined as Steve never showed as V1.
                    ProxyFire(p, false);
                    if (p.eid != null)
                    {
                        p.eid.dead = true;
                        MonoSingleton<EnemyTracker>.Instance?.GetCurrentEnemies().Remove(p.eid);
                    }
                    Destroy(p.gameObject);
                    p = null;
                }
                if (p == null)
                {
                    p = MakeProxy(id, a[1], w, h, eye);
                    proxies[id] = p;
                }
                if (p.lastSeen > 0f && Time.time > p.lastSeen) p.velocity = (feet - p.tickPos) / (Time.time - p.lastSeen);
                p.lastSeen = Time.time;
                p.tickPos = feet;
                // Minecraft draws mobs between ticks and says where every frame (EPOS); the tick position is the
                // fallback when those stop
                if (Time.unscaledTime - p.drawnAt > 0.25f) p.transform.position = feet;
                p.hp = hp;
                p.maxHp = maxHp;
                p.hostile = a.Length > 10 && a[10] == "1";
                if (a.Length > 11) p.yaw = F(a[11]);
                if (p.eid != null) p.eid.health = hp / 10f;
                ProxyFire(p, a.Length > 12 && a[12] == "1");
            }
            var gone = new List<int>();
            // (negative ids: debug stand-ins of our own, not Minecraft's)
            foreach (var kv in proxies) if (!seen.Contains(kv.Key) && kv.Key >= 0) gone.Add(kv.Key);
            foreach (var id in gone)
            {
                var p = proxies[id];
                proxies.Remove(id);
                if (p != null)
                {
                    ProxyFire(p, false);
                    if (p.eid != null)
                    {
                        p.eid.dead = true;
                        MonoSingleton<EnemyTracker>.Instance?.GetCurrentEnemies().Remove(p.eid);
                    }
                    Destroy(p.gameObject);
                }
            }
        }

        public IEnumerable<McProxy> ProxyList() => proxies.Values;

        McProxy MakeProxy(int id, string type, float w, float h, float eye)
        {
            var root = new GameObject("mc_" + type + "_" + id);
            root.SetActive(false);
            // tagged like a real enemy: every weapon (beams, punch, nails, projectiles, harpoons) only hurts
            // colliders tagged Head/Body/Limb/EndLimb/Enemy on the enemy layers
            root.tag = "Enemy";
            var proxy = root.AddComponent<McProxy>();
            proxy.id = id;
            proxy.type = type;
            float headH = Mathf.Min(h * 0.25f, w);
            var body = new GameObject("body");
            body.transform.SetParent(root.transform, false);
            body.layer = 10;
            body.tag = "Body";
            var bc = body.AddComponent<BoxCollider>();
            bc.size = new Vector3(w, h - headH, w);
            bc.center = new Vector3(0, (h - headH) * 0.5f, 0);
            proxy.body = bc;
            var head = new GameObject("head");
            head.transform.SetParent(root.transform, false);
            head.layer = 10;
            head.tag = "Head";
            var hc = head.AddComponent<BoxCollider>();
            hc.size = new Vector3(w, headH, w);
            head.transform.localPosition = new Vector3(0, h - headH * 0.5f, 0);
            var center = new GameObject("center");
            center.transform.SetParent(root.transform, false);
            center.transform.localPosition = new Vector3(0, h * 0.5f, 0);
            proxy.center = center.transform;
            var eid = root.AddComponent<EnemyIdentifier>();
            // a type no Filth/Stray/Schism/Soldier hurt exception covers, so ULTRAKILL's enemies can hit Minecraft mobs
            eid.enemyType = EnemyType.Mannequin;
            eid.weakPoint = head;
            eid.health = 2f;
            eid.overrideCenter = center.transform;
            // what EnemyIdentifier.Awake (skipped for stand-ins) would set up: gasoline and fire need these lists
            eid.flammables = new List<Flammable>();
            eid.burners = new List<Flammable>();
            // ULTRAKILL's explosions (rockets, cores, the Knuckleblaster's blast) only hurt an enemy whose own object
            // has a collider: a switched-off one, so it counts without touching anything
            var rootCol = root.AddComponent<BoxCollider>();
            rootCol.size = new Vector3(w, h, w);
            rootCol.center = new Vector3(0, h * 0.5f, 0);
            rootCol.enabled = false;
            foreach (var go in new[] { body, head })
            {
                var eii = go.AddComponent<EnemyIdentifierIdentifier>();
                eii.eid = eid;
                // a kinematic body per limb, like real enemies: a hit reports the rigidbody's transform, and weapons
                // check that transform's layer and tag (one body on the root made every hit look like layer 0)
                var lrb = go.AddComponent<Rigidbody>();
                lrb.isKinematic = true;
                lrb.useGravity = false;
            }
            proxy.rb = body.GetComponent<Rigidbody>();
            proxy.eid = eid;
            proxy.head = head;
            // another player who is V1: V1's own body stands there (drawn by us, so no hiding box)
            if (type == "v1")
            {
                root.SetActive(true);
                AddV1Body(proxy, h);
                proxy.UpdateCachedTransformData();
                MonoSingleton<EnemyTracker>.Instance?.AddEnemy(eid);
                return proxy;
            }
            // the mob in ULTRAKILL's depth: ULTRAKILL's enemies and effects behind a Minecraft mob are hidden by it
            // (a slim box, so little of what's beside the mob gets cut away); no collider, only depth
            var occ = new GameObject("occluder");
            occ.layer = 8;
            occ.transform.SetParent(root.transform, false);
            occ.transform.localPosition = new Vector3(0, h * 0.5f, 0);
            occ.transform.localScale = new Vector3(w * 0.8f, h * 0.95f, w * 0.8f);
            occ.AddComponent<MeshFilter>().sharedMesh = CubeMesh();
            var omr = occ.AddComponent<MeshRenderer>();
            omr.sharedMaterial = OccluderMaterial();
            omr.shadowCastingMode = ShadowCastingMode.Off;
            omr.receiveShadows = false;
            omr.lightProbeUsage = LightProbeUsage.Off;
            omr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            omr.allowOcclusionWhenDynamic = false;
            root.SetActive(true);
            proxy.UpdateCachedTransformData();
            MonoSingleton<EnemyTracker>.Instance?.AddEnemy(eid);
            // coins, Vision and auto-aim look enemies up in the TargetTracker, not EnemyTracker
            var pm = MonoSingleton<PortalManagerV2>.Instance;
            if (pm != null && pm.TargetTracker != null) pm.TargetTracker.RegisterTarget(proxy, proxy.life.Token);
            return proxy;
        }

        // ------------------------------------------------------------ input

        void InjectInput()
        {
            if (!Net.Connected) return;
            if (kb == null)
            {
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                kb = InputSystem.AddDevice<Keyboard>("UltraBridgeKeyboard");
                mouse = InputSystem.AddDevice<Mouse>("UltraBridgeMouse");
            }
            var ks = new KeyboardState();
            foreach (var k in held) ks.Set(k, true);
            InputSystem.QueueStateEvent(kb, ks);
            var cc = levelPrepared ? MonoSingleton<CameraController>.Instance : null;
            var opm = levelPrepared ? MonoSingleton<OptionsManager>.Instance : null;
            var nm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
            // V1 is alive and in control: its CameraController must be running (it is what turns the view)
            if (cc != null && nm != null && v1Landed && !nm.dead && !cc.enabled) cc.enabled = true;
            // ULTRAKILL's weapon wheel (hold Q): the camera holds still and the mouse picks a weapon instead (its
            // WheelLook reads the mouse's own movement, which otherwise stays at zero here)
            var ww = levelPrepared ? MonoSingleton<WeaponWheel>.Instance : null;
            bool wheelOpen = ww != null && ww.gameObject.activeInHierarchy;
            if (cc != null && opm != null && cc.activated && mouseDelta != Vector2.zero && !wheelOpen)
            {
                // ULTRAKILL's own look math: Look = <Mouse>/delta * 0.05, then * sensitivity/10, slower while zoomed
                bool zooming = CamZooming(cc);
                float sens = opm.mouseSensitivity / 10f * 0.05f;
                float zoom = zooming && cc.defaultFov > 0f ? cc.cam.fieldOfView / cc.defaultFov : 1f;
                cc.rotationX += (cc.reverseY ? -mouseDelta.y : mouseDelta.y) * sens * zoom;
                cc.rotationY += (cc.reverseX ? -mouseDelta.x : mouseDelta.x) * sens * zoom;
                float f = Mathf.DeltaAngle(0f, cc.rotationX);
                if (Mathf.Abs(f) > 90f) cc.rotationX = 90f * Mathf.Sign(f);
                lookTotal += mouseDelta.magnitude;
                // CameraController only applies rotation while running and unlocked; never let either freeze V1's view
                if (!cc.isActiveAndEnabled || (GameStateManager.Instance != null && GameStateManager.Instance.CameraLocked))
                {
                    cc.ApplyRotations();
                }
            }
            var ms = new MouseState { position = pointer, delta = wheelOpen ? mouseDelta : Vector2.zero, scroll = new Vector2(0, wheel),
                buttons = (ushort)(buttons | (Time.unscaledTime < forceClickUntil ? 1 : 0)) };
            InputSystem.QueueStateEvent(mouse, ms);
            mouseDelta = Vector2.zero;
            wheel = 0;
        }

        internal static Key GlfwKey(int g) => GlfwToKey(g);

        static Key GlfwToKey(int g)
        {
            if (g >= 65 && g <= 90) return Key.A + (g - 65);
            if (g >= 48 && g <= 57) return g == 48 ? Key.Digit0 : Key.Digit1 + (g - 49);
            switch (g)
            {
                case 32: return Key.Space;
                case 340: return Key.LeftShift;
                case 344: return Key.RightShift;
                case 341: return Key.LeftCtrl;
                case 345: return Key.RightCtrl;
                case 342: return Key.LeftAlt;
                case 258: return Key.Tab;
                case 256: return Key.Escape;
                case 257: return Key.Enter;
                case 259: return Key.Backspace;
                case 96: return Key.Backquote;
                case 290: return Key.F1;
                case 346: return Key.RightAlt;
                case 280: return Key.CapsLock;
                case 262: return Key.RightArrow;
                case 263: return Key.LeftArrow;
                case 264: return Key.DownArrow;
                case 265: return Key.UpArrow;
                case 44: return Key.Comma;
                case 46: return Key.Period;
                case 47: return Key.Slash;
                case 59: return Key.Semicolon;
                case 39: return Key.Quote;
                case 45: return Key.Minus;
                case 61: return Key.Equals;
                case 91: return Key.LeftBracket;
                case 93: return Key.RightBracket;
                case 92: return Key.Backslash;
                default: return Key.None;
            }
        }

        // ------------------------------------------------------------ frames

        RenderTexture finalRT;
        Camera hookedCam;

        /// <summary>ULTRAKILL draws its final, post-processed image with the "Virtual Camera". Point that
        /// camera at our own texture so the frame doesn't depend on the (parked) window being visible.</summary>
        void LateUpdate()
        {
            HoldPause();
            if (!levelPrepared) return;
            UpdateSteveCamera();
            UpdateSelfBody();
            PlaceRemoteGuns();
            PoseDollHead();
            var pp = MonoSingleton<PostProcessV2_Handler>.Instance;
            var vc = pp != null ? pp.virtualCam : null;
            if (vc == null || !Net.Connected) return;
            int w = Screen.width, h = Screen.height;
            if (finalRT == null || finalRT.width != w || finalRT.height != h)
            {
                if (finalRT != null)
                {
                    finalRT.Release();
                    Destroy(finalRT);
                }
                finalRT = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                finalRT.Create();
            }
            if (vc.targetTexture != finalRT) vc.targetTexture = finalRT;
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != FpsCap) Application.targetFrameRate = FpsCap;
            hookedCam = vc;
        }

        /// <summary>Every frame (up to MaxInFlight at once) the final image and V1's mask are read back from the GPU
        /// straight into the next shared slot; no copies on our side, the game loop never waits.</summary>
        IEnumerator Capture()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                if (!Net.Connected || inFlight >= MaxInFlight || finalRT == null || hookedCam == null) continue;
                int w = finalRT.width, h = finalRT.height;
                if (w * h > MaxPixels) continue;
                var pp = MonoSingleton<PostProcessV2_Handler>.Instance;
                var mask = pp != null ? pp.mainTex : null;
                if (mask == null || mask.width != w || mask.height != h) continue;
                int seq = ++nextSeq;
                var job = new Readback { seq = seq, slot = seq % Slots, w = w, h = h, maskBpp = maskBpp, drawnAt = NowMicros() };
                // the exact view this frame was drawn from: Minecraft draws its world from the same one, so the two
                // never slide apart while the mouse moves
                var cc = MonoSingleton<CameraController>.Instance;
                if (cc != null && cc.cam != null && originSet)
                {
                    var ct = cc.cam.transform;
                    var eye = UkToMc(ct.position);
                    var e = ct.eulerAngles;
                    job.pose = new[] { eye.x, eye.y, eye.z, e.y + 180f, e.x > 180f ? e.x - 360f : e.x, -(e.z > 180f ? e.z - 360f : e.z), cc.cam.fieldOfView };
                }
                var co = colorOut[job.slot];
                var mo = maskOut[job.slot];
                inFlight++;
                AsyncGPUReadback.RequestIntoNativeArray(ref co, finalRT, 0, TextureFormat.RGBA32, r => ReadbackDone(job, r, false));
                AsyncGPUReadback.RequestIntoNativeArray(ref mo, mask, 0, job.maskBpp == 1 ? TextureFormat.Alpha8 : TextureFormat.RGBA32, r => ReadbackDone(job, r, true));
            }
        }

        class Readback
        {
            public int seq, slot, w, h, maskBpp, done;
            // when it was drawn (NowMicros), for Minecraft to tell how old a frame is when it shows it
            public int drawnAt;
            public bool failed;
            // eye x y z (Minecraft), yaw, pitch, roll, fov at the moment the frame was drawn
            public float[] pose;
        }

        /// <summary>Both halves of a frame have landed in its slot: publish it (size, slot, mask format, then seq).</summary>
        unsafe void ReadbackDone(Readback job, AsyncGPUReadbackRequest r, bool isMask)
        {
            if (r.hasError)
            {
                job.failed = true;
                if (isMask && job.maskBpp == 1 && maskBpp == 1)
                {
                    maskBpp = 4;
                    Plugin.Log.LogWarning("Alpha8 readback not supported here; sending the mask as RGBA");
                }
            }
            if (++job.done < 2) return;
            inFlight--;
            if (job.failed || job.seq <= publishedSeq) return;
            int* hdr = (int*)frameBase;
            Thread.MemoryBarrier();
            hdr[1] = job.w;
            hdr[2] = job.h;
            hdr[3] = 1; // rows bottom-up
            hdr[4] = job.slot;
            hdr[5] = job.maskBpp;
            hdr[6] = 2;
            hdr[7] = job.drawnAt;
            // ints 8..14: the frame's view as floats; 15: 1 when they're there
            float* fp = (float*)(hdr + 8);
            if (job.pose != null) for (int i = 0; i < 7; i++) fp[i] = job.pose[i];
            hdr[15] = job.pose != null ? 1 : 0;
            Thread.MemoryBarrier();
            hdr[0] = job.seq;
            publishedSeq = job.seq;
            stats_frames++;
        }

        int stats_frames;
        float lookTotal;
        int gameFrames;
        float nextStatus;
        float nextPark;

        // the Sandbox's own menus (spawn menu, alter menu) are real now: Minecraft shows a cursor for them (UI mode)
        static readonly string[] StrayLocks = { "pit-falling", "unlock-mouse-component", "cheats-menu", "teleport-menu", "workshop-map-credits" };

        /// <summary>States from the stripped Sandbox (pits, menus) that lock V1's camera; Minecraft has none of them.</summary>
        void ClearStrayLocks()
        {
            var gsm = GameStateManager.Instance;
            if (gsm == null) return;
            foreach (var k in StrayLocks) if (gsm.IsStateActive(k)) { gsm.PopState(k); Plugin.Log.LogInfo("cleared game state " + k); }
        }

        static string ActiveStates()
        {
            try
            {
                var order = Traverse.Create(GameStateManager.Instance).Field("stateOrder").GetValue<List<string>>();
                return order == null ? "?" : string.Join(",", order);
            }
            catch { return "?"; }
        }

        void Status()
        {
            if (Time.unscaledTime < nextStatus) return;
            nextStatus = Time.unscaledTime + 1f;
            var nm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
            var im = levelPrepared ? MonoSingleton<InputManager>.Instance : null;
            string move = "?";
            try { move = im != null ? im.InputSource.Move.ReadValue<Vector2>().ToString() : "no InputManager"; } catch (Exception e) { move = "err " + e.Message; }
            var cs = new System.Text.StringBuilder();
            foreach (var c in FindObjectsOfType<Canvas>()) if (c.isRootCanvas) cs.Append(c.name).Append(':').Append(c.renderMode).Append(' ');
            var cc = levelPrepared ? MonoSingleton<CameraController>.Instance : null;
            string cam = cc == null ? "none" : (cc.isActiveAndEnabled ? "on" : "OFF") + (cc.nm != null ? "" : " NO-V1") + " rot=(" + cc.rotationX.ToString("0.0") + "," + cc.rotationY.ToString("0.0") + ") euler=" + (cc.cam != null ? cc.cam.transform.eulerAngles.ToString("0.0") : "?");
            Plugin.Log.LogInfo("status: fps=" + gameFrames + " look=" + lookTotal.ToString("0") + " cam=" + cam + " camLocked=" + GameStateManager.Instance.CameraLocked + " states=[" + ActiveStates() + "]" + " conn=" + Net.Connected + " focused=" + Application.isFocused + " screen=" + Screen.width + "x" + Screen.height + " " + Screen.fullScreenMode
                + " held=[" + string.Join(",", held) + "] btn=" + buttons + " move=" + move
                + " v1=" + (nm != null ? nm.transform.position + " vel " + nm.rb.velocity + " ground " + (nm.gc != null && nm.gc.onGround) + " activated " + nm.activated : "none")
                + " blocks=" + blocks.Count + " sections=" + sections.Count + "(+" + sectionQueue.Count + " queued) ui=" + uiMode + " proxies=" + proxies.Count + " frames/s=" + stats_frames + " kb=" + (kb != null ? kb.enabled.ToString() : "none")
                + " ukEnemies=" + ukEnemies.Count + " hands=" + hands + " audio=" + AudioListener.volume.ToString("0.00") + (AudioListener.pause ? " PAUSED" : "") + " playing=" + PlayingSources()
                + " ts=" + Time.timeScale.ToString("0.###") + " mcPaused=" + mcPaused + " fixed=" + fixedSteps
                + " canvases=" + cs);
            stats_frames = 0;
            gameFrames = 0;
            lookTotal = 0;
            fixedSteps = 0;
        }

        // physics steps since the last status line (none may run while Minecraft is paused)
        int fixedSteps;

        void FixedUpdate()
        {
            fixedSteps++;
            // a physics step while paused means something gave time back: stop it again before anything moves
            if (mcPaused) HoldPause();
            else if (levelPrepared && v1Landed) StepUp(MonoSingleton<NewMovement>.Instance);
        }

        /// <summary>Debug: the frame exactly as Minecraft gets it (colour, mask), and how many "ghost" pixels it has:
        /// opaque in the mask but black in the colour, which Minecraft would draw as black.</summary>
        unsafe void FrameSnap(string tag)
        {
            int* hdr = (int*)frameBase;
            int w = hdr[1], h = hdr[2], slot = hdr[4];
            if (w <= 0 || h <= 0) { Net.Send("FRAMESNAPPED none"); return; }
            byte* col = frameBase + HeaderSize + slot * SlotBytes;
            byte* msk = col + MaxPixels * 4L;
            var c = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var m = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var cp = new Color32[w * h];
            var mp = new Color32[w * h];
            int ghosts = 0, opaque = 0;
            for (int i = 0; i < w * h; i++)
            {
                byte r = col[i * 4], g = col[i * 4 + 1], b = col[i * 4 + 2];
                byte a = msk[i * 4 + 3];
                cp[i] = new Color32(r, g, b, 255);
                bool ghost = a > 128 && r < 6 && g < 6 && b < 6;
                if (a > 128) opaque++;
                if (ghost) ghosts++;
                mp[i] = ghost ? new Color32(255, 0, 0, 255) : new Color32(a, a, a, 255);
            }
            c.SetPixels32(cp);
            m.SetPixels32(mp);
            var dir = Path.Combine(Paths.BepInExRootPath, "ultrabridge_snap");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "frame_" + tag + "_color.png"), c.EncodeToPNG());
            File.WriteAllBytes(Path.Combine(dir, "frame_" + tag + "_mask.png"), m.EncodeToPNG());
            Destroy(c);
            Destroy(m);
            Net.Send("FRAMESNAPPED " + tag + " opaque " + opaque + " ghosts " + ghosts);
        }

        /// <summary>Debug: blood right next to V1, and why it did or didn't heal.</summary>
        IEnumerator GoreTest()
        {
            var nm = MonoSingleton<NewMovement>.Instance;
            var bsm = MonoSingleton<BloodsplatterManager>.Instance;
            if (nm == null || bsm == null) { Net.Send("GORE none"); yield break; }
            var sb = new StringBuilder("GORE v1 layer " + nm.gameObject.layer + " tag " + nm.gameObject.tag + " hp " + nm.hp + " anti " + nm.antiHp.ToString("0") + " cols:");
            foreach (var c in nm.GetComponentsInChildren<Collider>()) sb.Append(' ').Append(c.name).Append("[L").Append(c.gameObject.layer).Append(' ').Append(c.tag).Append(c.isTrigger ? " trig" : "").Append(c.enabled ? "" : " off").Append(']');
            var gore = bsm.GetGore(GoreType.Body, false, false, false, null, false);
            if (gore == null) { Net.Send(sb + " | no gore"); yield break; }
            gore.transform.position = nm.transform.position + nm.transform.forward * 1f;
            gore.transform.SetParent(McGoreZone().goreZone, true);
            var bs = gore.GetComponent<Bloodsplatter>();
            bs?.GetReady();
            var sc = gore.GetComponent<SphereCollider>();
            sb.Append(" | gore L").Append(gore.layer).Append(" active ").Append(gore.activeInHierarchy).Append(" hpAmount ").Append(bs != null ? bs.hpAmount : -1)
              .Append(" col ").Append(sc != null ? sc.radius.ToString("0.00") + "x" + gore.transform.lossyScale.x.ToString("0.00") + (sc.enabled ? " on" : " off") + (sc.isTrigger ? " trig" : "") : "none")
              .Append(" ignores player layer ").Append(Physics.GetIgnoreLayerCollision(gore.layer, nm.gameObject.layer))
              .Append(" rb ").Append(gore.GetComponent<Rigidbody>() != null);
            int before = nm.hp;
            yield return new WaitForSeconds(0.6f);
            sb.Append(" | hp ").Append(before).Append(" -> ").Append(nm.hp);
            Net.Send(sb.ToString());
            Plugin.Log.LogInfo(sb.ToString());
        }

        static int PlayingSources()
        {
            int n = 0;
            foreach (var s in FindObjectsOfType<AudioSource>()) if (s.isPlaying) n++;
            return n;
        }

        IEnumerator Snap()
        {
            yield return new WaitForEndOfFrame();
            var dir = Path.Combine(Paths.BepInExRootPath, "ultrabridge_snap");
            Directory.CreateDirectory(dir);
            var shot = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(dir, "screen.png"), shot.EncodeToPNG());
            var pp = MonoSingleton<PostProcessV2_Handler>.Instance;
            if (pp != null && pp.mainTex != null)
            {
                var prev = RenderTexture.active;
                RenderTexture.active = pp.mainTex;
                var t = new Texture2D(pp.mainTex.width, pp.mainTex.height, TextureFormat.RGBA32, false);
                t.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0);
                RenderTexture.active = prev;
                var px = t.GetPixels32();
                var rgb = new Color32[px.Length];
                for (int i = 0; i < px.Length; i++) rgb[i] = new Color32(px[i].r, px[i].g, px[i].b, 255);
                var c = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false);
                c.SetPixels32(rgb);
                File.WriteAllBytes(Path.Combine(dir, "maintex_rgb.png"), c.EncodeToPNG());
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(px[i].a, px[i].a, px[i].a, 255);
                var a = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false);
                a.SetPixels32(px);
                File.WriteAllBytes(Path.Combine(dir, "maintex_alpha.png"), a.EncodeToPNG());
            }
            Net.Send("SNAPPED " + dir.Replace('\\', '/'));
            Plugin.Log.LogInfo("snap written to " + dir);
        }

        void OnDestroy()
        {
            if (view != null) view.SafeMemoryMappedViewHandle.ReleasePointer();
            view?.Dispose();
            mmf?.Dispose();
        }

        // ------------------------------------------------------------ damage out

        /// <summary>DMG mob damage head explosion parry by: by is the ULTRAKILL enemy that dealt it (0 = V1), so the mob
        /// turns on whoever hurt it.</summary>
        public void ReportDamage(McProxy p, float multiplier, bool head, bool explosion, Vector3 hitPoint, bool parry = false, int by = 0, bool fire = false)
        {
            Net.Send("DMG " + p.id + " " + S(multiplier) + " " + (head ? 1 : 0) + " " + (explosion ? 1 : 0) + " " + (parry ? 1 : 0) + " " + by + " " + (fire ? 1 : 0));
        }

        // ------------------------------------------------------------ blood

        /// <summary>A splatter of ULTRAKILL blood (sound, stains, and V1's blood healing when close, as for its own
        /// enemies). hp &lt; 0 keeps the splatter's own amount.</summary>
        public static GameObject SpawnGore(EnemyIdentifier eid, GoreType type, Vector3 at, bool fromExplosion, int hp)
        {
            try
            {
                var bsm = MonoSingleton<BloodsplatterManager>.Instance;
                var gz = I != null ? I.McGoreZone() : null;
                if (bsm == null || gz == null) return null;
                var gore = eid != null ? bsm.GetGore(type, eid, fromExplosion) : bsm.GetGore(type, false, false, false, null, fromExplosion);
                if (gore == null) return null;
                // the pool keeps splatters under an inactive store; like an enemy's own, it only comes alive (spray,
                // sound, stains, the healing trigger) once it's in a gore zone
                gore.transform.position = at;
                gore.transform.SetParent(gz.goreZone, true);
                var bs = gore.GetComponent<Bloodsplatter>();
                if (bs != null)
                {
                    if (hp >= 0) bs.hpAmount = hp;
                    bs.GetReady();
                }
                return gore;
            }
            catch (Exception e) { Plugin.Log.LogDebug("gore: " + e.Message); }
            return null;
        }

        GoreZone mcGore;

        /// <summary>Where Minecraft mobs' blood and gibs go (ULTRAKILL caps and cleans up gore per zone).</summary>
        public GoreZone McGoreZone()
        {
            if (mcGore == null && levelPrepared) mcGore = new GameObject("Ultracraft Gore Zone").AddComponent<GoreZone>();
            return mcGore;
        }

        /// <summary>A mob's death, the way a Filth's chest bursts: a blood explosion, gibs, and the big healing splatters.</summary>
        static readonly BSType[] SmallGibs = { BSType.jawChunk, BSType.brainChunk, BSType.skullChunk, BSType.eyeball };

        void DeathGore(McProxy p, Vector3 at, float w, float h)
        {
            if (p != null && p.type == "end_crystal") return;
            var bsm = MonoSingleton<BloodsplatterManager>.Instance;
            if (bsm == null) return;
            var eid = p != null ? p.eid : null;
            if (p != null && p.center != null) at = p.center.position;
            try
            {
                // gibs and bursts live in a gore zone, which shows and cleans them up like any enemy's
                var gz = McGoreZone();
                var burst = ExtraGore ? bsm.GetFromQueue(BSType.chestExplosion) : null;
                if (burst != null)
                {
                    burst.transform.position = at;
                    burst.transform.localScale = Vector3.one * Mathf.Clamp(Mathf.Max(w, h) / 3.5f, 0.6f, 3f);
                    if (gz != null) gz.SetGoreZone(burst);
                    NoOrgans(burst);
                    OnBody(burst, null);
                }
                int gibs = 0;
                for (int i = 0; i < gibs; i++)
                {
                    var gib = bsm.GetGib(SmallGibs[i % SmallGibs.Length]);
                    if (gib == null) continue;
                    gib.transform.SetPositionAndRotation(at + UnityEngine.Random.insideUnitSphere * Mathf.Max(0.3f, w * 0.4f), UnityEngine.Random.rotation);
                    if (gz != null) gz.SetGoreZone(gib);
                    gib.SetActive(bsm.goreOn);
                    var rb = gib.GetComponent<Rigidbody>();
                    if (rb != null) rb.AddForce(UnityEngine.Random.onUnitSphere * 15f + Vector3.up * 10f, ForceMode.VelocityChange);
                }
            }
            catch (Exception e) { Plugin.Log.LogDebug("gibs: " + e.Message); }
            SpawnGore(eid, GoreType.Head, at + Vector3.up * h * 0.3f, false, 10);
            SpawnGore(eid, GoreType.Body, at, false, 10);
            SpawnGore(eid, GoreType.Limb, at - Vector3.up * h * 0.25f, false, 10);
            BloodBurst(at, Mathf.Max(w, h) / K, eid);
        }

        /// <summary>Everything unlocked (all weapons, variants and arms) while ULTRAKILL runs for Minecraft; the save
        /// file is never touched.</summary>
        public static bool AllWeapons => LaunchedForMinecraft || Net.Connected;
    }

    // ---------------------------------------------------------------- Harmony: proxy enemies

    [HarmonyPatch(typeof(EnemyIdentifier), "Awake")]
    static class EidAwake
    {
        static bool Prefix(EnemyIdentifier __instance) => __instance.GetComponent<McProxy>() == null;
    }

    [HarmonyPatch(typeof(EnemyIdentifier), "Start")]
    static class EidStart
    {
        static bool Prefix(EnemyIdentifier __instance) => __instance.GetComponent<McProxy>() == null;
    }

    [HarmonyPatch(typeof(EnemyIdentifier), "Update")]
    static class EidUpdate
    {
        static bool Prefix(EnemyIdentifier __instance) => __instance.GetComponent<McProxy>() == null;
    }

    [HarmonyPatch(typeof(EnemyIdentifier), "OnEnable")]
    static class EidOnEnable
    {
        static bool Prefix(EnemyIdentifier __instance) => __instance.GetComponent<McProxy>() == null;
    }

    [HarmonyPatch(typeof(EnemyIdentifier), "OnDisable")]
    static class EidOnDisable
    {
        static bool Prefix(EnemyIdentifier __instance) => __instance.GetComponent<McProxy>() == null;
    }

    [HarmonyPatch(typeof(EnemyIdentifier), nameof(EnemyIdentifier.DeliverDamage))]
    static class EidDamage
    {
        static bool Prefix(EnemyIdentifier __instance, GameObject target, Vector3 hitPoint, float multiplier, float critMultiplier, bool fromExplosion, GameObject sourceWeapon)
        {
            var p = __instance.GetComponent<McProxy>();
            if (p == null)
            {
                // another player's enemy (its puppet here): the hit goes to the real one, in their game
                var pup = __instance.GetComponentInParent<UkPuppet>();
                if (pup != null) return Bridge.I == null || Bridge.I.PuppetHit(pup, __instance, target, hitPoint, multiplier, critMultiplier, fromExplosion);
                // V1 shot one of ULTRAKILL's own enemies: it stops chasing Minecraft mobs for a while (and, with
                // teammates about, it counts towards whom it goes for)
                if (sourceWeapon != null)
                {
                    Bridge.I?.MarkV1Attack(__instance);
                    Bridge.I?.AddThreat(__instance, 0, multiplier);
                }
                return true;
            }
            // a teammate (another player's V1): our enemies hurt them, we don't, unless we're dueling them (DUEL)
            if (p.type == "v1" && __instance.hitter != "enemy" && (Bridge.I == null || Bridge.I.duelWith != p.id)) return false;
            bool head = target != null && target == p.head;
            float dmg = multiplier;
            if (head) dmg *= critMultiplier > 0f ? 1f + critMultiplier : 2f;
            bool parry = false;
            if (__instance.hitter == "punch" && p.pendingHurt > 0 && Time.time < p.parryUntil)
            {
                // punched while its hit was landing: a parry, exactly as a Filth takes one (5 damage, and the real
                // parry with hitstop, flash, heal and style); the mob's hit never lands
                p.pendingHurt = 0;
                p.parryUntil = 0f;
                p.hasPendingKnock = false;
                dmg = 5f;
                parry = true;
                try { MonoSingleton<FistControl>.Instance.currentPunch.Parry(false, __instance); }
                catch (Exception e) { Plugin.Log.LogWarning("parry: " + e); }
            }
            else if (__instance.hitter == "punch")
            {
                p.punchedAt = Time.time;
            }
            // one of ULTRAKILL's enemies did it (its projectile, swing, beam, shockwave or blast): the mob fights back
            // against that enemy, not V1
            int by = 0;
            if (__instance.hitter == "enemy") by = Bridge.I != null ? Bridge.I.AttackerOf(p) : 0;
            // burning (gasoline or the Firestarter's flames): Minecraft fire, which fire resistance shrugs off
            bool fire = __instance.hitter == "fire";
            Bridge.Conduct(__instance, p, sourceWeapon);
            // a touch that does nothing (the JumpStart's spark through the other limbs) isn't a hit in Minecraft
            if (dmg < 0.001f && !parry) return false;
            Bridge.I?.ReportDamage(p, dmg, head, fromExplosion, hitPoint, parry, by, fire);
            // an enemy's swing, stomp or blast that would launch V1 launches the mob too
            if (__instance.hitter == "enemy") Bridge.I?.KnockMob(p);
            if (p.type == "end_crystal") return false;
            // real ULTRAKILL blood where it was hit, sized like a Filth's: splatters, stains, and V1's blood healing
            // when close (the same amounts ULTRAKILL gives: 3 per pellet or blast, 1 per nail)
            var type = head ? GoreType.Head : dmg >= 1f || fromExplosion ? GoreType.Body : GoreType.Small;
            int hp = __instance.hitter == "nail" ? 1 : __instance.hitter == "shotgun" || __instance.hitter == "shotgunzone" || fromExplosion ? 3 : -1;
            var at = target != null && target != __instance.gameObject ? target.transform.position : hitPoint;
            if (hitPoint != Vector3.zero) at = hitPoint;
            Bridge.SpawnGore(__instance, type, at, fromExplosion, hp);
            return false;
        }
    }

    /// <summary>With teammates about, an enemy going for one of them doesn't snap back to our V1 the moment we hit it
    /// (ULTRAKILL's own rule): Bridge.AimBosses weighs the hit and decides.</summary>
    [HarmonyPatch(typeof(EnemyIdentifier), nameof(EnemyIdentifier.DeliverDamage))]
    [HarmonyPriority(Priority.Last)]
    static class EidDamageKeepsAim
    {
        static void Prefix(EnemyIdentifier __instance, out EnemyTarget __state) => __state = __instance.target;

        static void Postfix(EnemyIdentifier __instance, EnemyTarget __state)
        {
            if (Bridge.I == null || __state == null || __instance.dead || Bridge.I.AimOf(__instance) <= 0) return;
            if (__instance.target == __state) return;
            __instance.target = __state;
            __instance.prioritizeEnemiesUnlessAttacked = true;
        }
    }

    /// <summary>Every ULTRAKILL explosion (rockets, cores, malicious beams, enemy blasts) also blows up Minecraft.</summary>
    [HarmonyPatch(typeof(Explosion), "Start")]
    static class ExplosionStart
    {
        static void Postfix(Explosion __instance)
        {
            if (__instance.harmless || __instance.damage <= 0) return;
            // a blast right where V1 stands (Counterblast, Seismic, Ripcord) leaves the blocks be
            if (__instance.GetComponentInParent<NoCrater>() != null) return;
            // the "mini nuke" (a rocket or core shot with a railcannon, a charged Malicious beam through a projectile)
            if (Bridge.IsNuke(__instance))
            {
                Bridge.I?.ReportExplosion(__instance.transform.position, __instance.maxSize, false, true);
                return;
            }
            // the Malicious railcannon's blast tears out twice as much
            bool rail = __instance.sourceWeapon != null && __instance.sourceWeapon.GetComponent<Railcannon>() != null;
            bool enemy = __instance.enemy && __instance.sourceWeapon == null;
            Bridge.I?.ReportExplosion(__instance.transform.position, __instance.maxSize * (rail ? 2f : 1f), rail, false, enemy);
        }
    }

    /// <summary>The cheats' default hotkeys (noclip on V, flight on B, and M N C O L P I J H) are keys Minecraft presses
    /// for other things, so in Ultracraft they don't toggle cheats; the cheats menu still can.</summary>
    [HarmonyPatch(typeof(CheatsManager), nameof(CheatsManager.HandleCheatBind))]
    static class NoCheatHotkeys
    {
        static bool Prefix(string identifier)
        {
            // every cheat hotkey sits on a key Minecraft uses (L is advancements, C, M, N, O, P, I, J, H...); the cheats
            // menu still toggles them
            return !Bridge.AllWeapons;
        }
    }

    /// <summary>The Spawner Arm's menu: every enemy and unlockable, as if all were found (nothing is saved).</summary>
    [HarmonyPatch(typeof(BestiaryData), nameof(BestiaryData.GetEnemy))]
    static class SpawnMenuEnemies
    {
        static void Postfix(ref int __result)
        {
            if (Bridge.AllWeapons && __result < 1) __result = 1;
        }
    }

    [HarmonyPatch(typeof(UnlockablesData), nameof(UnlockablesData.IsUnlocked))]
    static class SpawnMenuUnlockables
    {
        static void Postfix(ref bool __result)
        {
            if (Bridge.AllWeapons) __result = true;
        }
    }

    // ---------------------------------------------------------------- Harmony: weapons on Minecraft blocks

    /// <summary>ULTRAKILL calls SceneHelper.CreateEnviroGibs wherever something hits the environment (beams, coins,
    /// nails, sawblades, pellets, chainsaw, hammer, slam landings). Around each weapon method that can do that we
    /// note how hard the weapon hits; the gibs call then becomes block damage on Minecraft terrain.</summary>
    static class HitContext
    {
        public static float damage;
        public static float radius;
        // > 0: the hit bores a tunnel this many blocks long (Electric railcannon)
        public static float tunnel;
        public static object source;
        // which way the shot was really going (the environment-hit call only gives the surface normal)
        public static Vector3 aim;

        public struct Saved
        {
            public float damage, radius, tunnel;
            public object source;
            public Vector3 aim;
        }
    }

    [HarmonyPatch]
    static class WeaponHitContext
    {
        static readonly AccessTools.FieldRef<ShotgunHammer, float> HammerDamage = AccessTools.FieldRefAccess<ShotgunHammer, float>("damage");

        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(RevolverBeam), "ExecuteHits");
            yield return AccessTools.Method(typeof(RevolverBeam), "PiercingShotCheck");
            yield return AccessTools.Method(typeof(Coin), "ReflectRevolver");
            yield return AccessTools.Method(typeof(Coin), "Punchflection");
            yield return AccessTools.Method(typeof(Nail), "FixedUpdate");
            yield return AccessTools.Method(typeof(Nail), "OnCollisionEnter");
            yield return AccessTools.Method(typeof(Projectile), "Collided");
            yield return AccessTools.Method(typeof(Chainsaw), "FixedUpdate");
            yield return AccessTools.Method(typeof(Chainsaw), "CheckMultipleRicochets");
            yield return AccessTools.Method(typeof(Shotgun), "Update");
            yield return AccessTools.Method(typeof(ShotgunHammer), "Update");
            // a coroutine: its body runs in the generated enumerator's MoveNext
            yield return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(ShotgunHammer), "ImpactRoutine"));
            yield return AccessTools.Method(typeof(NewMovement), "LandingImpact");
        }

        /// <summary>The Piercer's charged shot (and the Slab's): "Revolver Beam Super", its ricochets included.</summary>
        static bool Charged(object o) => o is RevolverBeam b && b.beamType == BeamType.Revolver && b.name.Contains("Super");

        static object Unwrap(object o)
        {
            if (o != null && o.GetType().DeclaringType == typeof(ShotgunHammer))
                o = Traverse.Create(o).Field("<>4__this").GetValue();
            return o;
        }

        /// <summary>How hard the weapon behind a hit is upgraded (V1's own weapons only).</summary>
        static float Upgrade(object o)
        {
            string kind = null;
            switch (o)
            {
                case RevolverBeam b: if (b.beamType == BeamType.Revolver || b.beamType == BeamType.Railgun) kind = Bridge.WeaponKind(b.sourceWeapon); break;
                case Coin c: kind = "rev"; break;
                case Nail n: if (!n.enemy) kind = Bridge.WeaponKind(n.sourceWeapon) ?? "nai"; break;
                case Chainsaw cs: kind = "sho"; break;
                case ShotgunHammer _: kind = "sho"; break;
                case Shotgun _: kind = "sho"; break;
            }
            return Bridge.BlockPower(kind);
        }

        static float Damage(object o)
        {
            o = Unwrap(o);
            float d = BaseDamage(o) * Upgrade(o);
            // the charged shot digs three times as hard as a plain one
            return Charged(o) ? d * 3f : d;
        }

        static float BaseDamage(object o)
        {
            switch (o)
            {
                // enemies' shots wreck blocks too, as hard as ULTRAKILL lets them break its own breakables
                // (their damage is in V1's health, a tenth of that against things)
                case RevolverBeam b:
                    if (b.beamType == BeamType.Enemy) return Mathf.Clamp(b.damage / 10f, 0.5f, 4f);
                    return b.beamType == BeamType.Railgun ? Mathf.Max(b.damage, 10f) : Mathf.Max(b.damage, 0.5f);
                case Coin c: return Mathf.Max(c.power, 1f);
                case Nail n: return n.enemy ? Mathf.Clamp(n.damage / 10f, 0.2f, 1f) : Mathf.Max(n.damage, 0.2f) * (n.sawblade ? 1.5f : 1f);
                case Projectile p: return p.friendly || p.playerBullet ? Mathf.Max(p.damage, 0.25f) : Mathf.Clamp(p.damage / 10f, 0.5f, 4f);
                case Chainsaw cs: return Mathf.Max(cs.damage, 0.5f);
                case ShotgunHammer h: return Mathf.Max(HammerDamage(h), 2f);
                case Shotgun _: return 1f;
                // only a slam (ground pound) cracks the ground, harder from higher (from SlamCraterDrop up it blows a crater);
                // a hard landing from a jump or a fall leaves the block alone
                case NewMovement nm: return Bridge.I != null && Bridge.I.Slamming(nm) ? 2f + Mathf.Min(Bridge.I.SlamDrop(nm) * 0.5f, 8f) : 0f;
            }
            return 0f;
        }

        /// <summary>Railcannon beams blow a hole, not a dent.</summary>
        static float Radius(object o)
        {
            if (o is NewMovement nm) return Mathf.Min(Bridge.I != null ? Bridge.I.SlamDrop(nm) / 8f : 0f, 1.5f);
            // a plain revolver shot cracks the one block it hits; the charged shot also the blocks around it
            if (Charged(o)) return 1f;
            return o is RevolverBeam b && b.beamType == BeamType.Railgun ? 1.5f : 0f;
        }

        /// <summary>The Electric railcannon (the beam without a blast; Malicious explodes instead) tunnels on through.</summary>
        static float Tunnel(object o)
        {
            if (!(o is RevolverBeam b) || b.beamType != BeamType.Railgun) return 0f;
            bool malicious = b.hitParticle != null && b.hitParticle.GetComponentInChildren<Explosion>(true) != null;
            return malicious ? 0f : 48f;
        }

        /// <summary>The direction the shot travels: beams along their own forward, projectiles along their flight.</summary>
        static Vector3 Aim(object o)
        {
            switch (o)
            {
                case RevolverBeam b: return b.transform.forward;
                case Nail n:
                {
                    var rb = n.GetComponent<Rigidbody>();
                    return rb != null && rb.velocity.sqrMagnitude > 0.01f ? rb.velocity.normalized : n.transform.forward;
                }
                case Projectile p: return p.transform.forward;
            }
            return Vector3.zero;
        }

        static void Prefix(object __instance, out HitContext.Saved __state)
        {
            __state = new HitContext.Saved { damage = HitContext.damage, radius = HitContext.radius, tunnel = HitContext.tunnel, source = HitContext.source, aim = HitContext.aim };
            HitContext.damage = Damage(__instance);
            HitContext.radius = Radius(__instance);
            HitContext.tunnel = Tunnel(__instance);
            HitContext.source = __instance;
            HitContext.aim = Aim(__instance);
        }

        static void Postfix(HitContext.Saved __state)
        {
            HitContext.damage = __state.damage;
            HitContext.radius = __state.radius;
            HitContext.tunnel = __state.tunnel;
            HitContext.source = __state.source;
            HitContext.aim = __state.aim;
        }
    }

    /// <summary>When the Feedbacker last punched: a Minecraft projectile arriving just after is parried.</summary>
    [HarmonyPatch(typeof(Punch), "ActiveStart")]
    static class FeedbackerPunched
    {
        static void Prefix(Punch __instance)
        {
            if (__instance.type == FistType.Standard) Bridge.lastParryPunch = Time.time;
        }
    }

    /// <summary>A slam's landing: Minecraft gets a crater sized by how far V1 fell.</summary>
    [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.LandingImpact))]
    static class SlamLanding
    {
        static void Postfix(NewMovement __instance) => Bridge.I?.ReportSlam(__instance);
    }

    [HarmonyPatch(typeof(SceneHelper), nameof(SceneHelper.CreateEnviroGibs), new[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(int), typeof(float) })]
    static class EnvironmentHit
    {
        static void Prefix(Vector3 position, Vector3 direction, float distance)
        {
            if (HitContext.damage > 0f) Bridge.I?.ReportEnvironmentHit(position, direction, distance, HitContext.damage, HitContext.radius);
        }
    }

    /// <summary>Feedbacker and Knuckleblaster punches into a wall.</summary>
    [HarmonyPatch(typeof(Punch), "AltHit")]
    static class PunchTerrain
    {
        static readonly AccessTools.FieldRef<Punch, RaycastHit> Hit = AccessTools.FieldRefAccess<Punch, RaycastHit>("hit");
        static readonly AccessTools.FieldRef<Punch, float> Damage = AccessTools.FieldRefAccess<Punch, float>("damage");

        static void Postfix(Punch __instance, Transform target)
        {
            var b = Bridge.I;
            if (b == null || target == null || !b.IsMinecraftTerrain(target)) return;
            var h = Hit(__instance);
            var cc = MonoSingleton<CameraController>.Instance;
            var dir = cc != null ? cc.transform.forward : -h.normal;
            bool heavy = __instance.type == FistType.Heavy;
            // each arm's own Power, and the Knuckleblaster's Demolition (how wide it digs)
            float power = Bridge.BlockPower(heavy ? "arm1" : "arm0");
            b.ReportBlockHit(h.point, dir, Mathf.Max(Damage(__instance), 1f) * (heavy ? 2f : 1f) * power, heavy ? Bridge.DemolitionRadius : 0f);
        }
    }

    /// <summary>Screwdriver harpoons drilling into a wall.</summary>
    [HarmonyPatch(typeof(Harpoon), "OnTriggerEnter")]
    static class HarpoonTerrain
    {
        static readonly AccessTools.FieldRef<Harpoon, bool> Stopped = AccessTools.FieldRefAccess<Harpoon, bool>("stopped");

        static void Prefix(Harpoon __instance, out bool __state)
        {
            __state = Stopped(__instance);
        }

        static void Postfix(Harpoon __instance, Collider other, bool __state)
        {
            var b = Bridge.I;
            if (__state || !Stopped(__instance) || b == null || other == null || !b.IsMinecraftTerrain(other.transform)) return;
            // the Screwdriver drills a hole around where it bites
            b.ReportBlockHit(__instance.transform.position, __instance.transform.forward, Mathf.Max(__instance.damage, 3f) * 2f * Bridge.BlockPower("rai"), 1f);
        }
    }

    /// <summary>S.R.S. cannonballs smash into terrain.</summary>
    [HarmonyPatch(typeof(Cannonball), nameof(Cannonball.Collide))]
    static class CannonballTerrain
    {
        static readonly AccessTools.FieldRef<Cannonball, bool> Launched = AccessTools.FieldRefAccess<Cannonball, bool>("launched");

        static void Prefix(Cannonball __instance, Collider other)
        {
            var b = Bridge.I;
            if (b == null || other == null || other.isTrigger || !Launched(__instance) || !b.IsMinecraftTerrain(other.transform)) return;
            var rb = __instance.GetComponent<Rigidbody>();
            var dir = rb != null && rb.velocity != Vector3.zero ? rb.velocity : __instance.transform.forward;
            b.ReportBlockHit(__instance.transform.position, dir, Mathf.Max(__instance.damage, 4f) * Bridge.BlockPower("rock"), 1.5f);
        }
    }

    /// <summary>Firestarter gasoline catching fire sets Minecraft blocks alight.</summary>
    [HarmonyPatch(typeof(BurningVoxel), nameof(BurningVoxel.Initialize))]
    static class GasolineFire
    {
        static void Postfix(BurningVoxel __instance)
        {
            Bridge.I?.ReportFire(__instance.transform.position);
            Bridge.I?.ReportOilBurn(__instance.transform.position);
        }
    }

    [HarmonyPatch(typeof(EnemyIdentifier), nameof(EnemyIdentifier.Death), new Type[] { typeof(bool) })]
    static class EidDeath
    {
        static int logged;

        static bool Prefix(EnemyIdentifier __instance, out bool __state)
        {
            __state = __instance.dead;
            return __instance.GetComponent<McProxy>() == null;
        }

        // what killed each of ULTRAKILL's own enemies (an enemy that vanishes without a fight shows here)
        static void Postfix(EnemyIdentifier __instance, bool __state) => Log(__instance, __state, "");

        internal static void Log(EnemyIdentifier __instance, bool __state, string how)
        {
            if (__state || !__instance.dead || __instance.GetComponent<McProxy>() != null || logged >= 60) return;
            logged++;
            var nm = MonoSingleton<NewMovement>.Instance;
            float d = nm != null ? Vector3.Distance(nm.transform.position, __instance.transform.position) / Bridge.K : -1f;
            Plugin.Log.LogInfo("enemy died" + how + ": " + __instance.enemyType + " hitter=" + __instance.hitter + " " + d.ToString("0") + " blocks from V1, y "
                + (Bridge.I != null ? Bridge.I.UkToMc(__instance.transform.position).y.ToString("0") : "?"));
        }
    }

    [HarmonyPatch(typeof(EnemyIdentifier), nameof(EnemyIdentifier.InstaKill))]
    static class EidInstaKill
    {
        static void Prefix(EnemyIdentifier __instance, out bool __state) => __state = __instance.dead;
        static void Postfix(EnemyIdentifier __instance, bool __state) => EidDeath.Log(__instance, __state, " (instakill)");
    }

    // ---------------------------------------------------------------- Win32

    static class Win32
    {
        [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr process, int cls, ref PowerThrottling info, int size);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        delegate bool EnumProc(IntPtr h, IntPtr l);

        [StructLayout(LayoutKind.Sequential)]
        struct PowerThrottling { public uint Version, ControlMask, StateMask; }

        /// <summary>Windows 11 coarsens timers for processes without a visible window; opt out so
        /// the parked game keeps its frame rate.</summary>
        public static void IgnoreTimerThrottling()
        {
            try
            {
                var p = new PowerThrottling { Version = 1, ControlMask = 0x4 | 0x1, StateMask = 0 };
                SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref p, Marshal.SizeOf(p));
            }
            catch { }
        }

        /// <summary>Move ULTRAKILL's window off-screen without minimizing (minimized windows stop rendering).</summary>
        public static void ParkWindow()
        {
            uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (pid == me && IsWindowVisible(h))
                {
                    const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
                    SetWindowPos(h, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }
                return true;
            }, IntPtr.Zero);
        }
    }
}
