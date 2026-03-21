// Copyright 2026 Arghya Sur / Genesis  –  Apache-2.0
//
// Sets up the Quest room using MRUK (MR Utility Kit) for proper semantic
// room understanding — EffectMesh for floor/walls/ceiling surfaces,
// AnchorPrefabSpawner for furniture, and raw global mesh fed into MuJoCo
// as static collision geometry so the Synth can interact with real-world surfaces.

using System.Collections;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;

namespace Genesis.Sentience.VR
{
    public class SceneMeshManager : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("MRUK Data Source")]
        [Tooltip("Where to load scene data from")]
        public MRUK.SceneDataSource dataSource = MRUK.SceneDataSource.DeviceWithPrefabFallback;

        [Tooltip("Enable high-fidelity scene model (V2) for slanted ceilings, inner walls, etc.")]
        public bool highFidelityScene = true;

        [Tooltip("Fallback room prefabs for editor / when device has no room setup")]
        public GameObject[] fallbackRoomPrefabs;

        [Header("Room Surfaces (EffectMesh)")]
        [Tooltip("Material for floor, ceiling, wall surfaces (leave null for auto-generated)")]
        public Material roomSurfaceMaterial;

        [Tooltip("Which surfaces to render (walls handled by prefab spawner)")]
        public MRUKAnchor.SceneLabels surfaceLabels =
            MRUKAnchor.SceneLabels.FLOOR |
            MRUKAnchor.SceneLabels.CEILING;

        [Tooltip("Generate colliders on room surfaces")]
        public bool surfaceColliders = true;

        [Tooltip("Cut holes for door/window frames")]
        public MRUKAnchor.SceneLabels cutHoles =
            MRUKAnchor.SceneLabels.DOOR_FRAME |
            MRUKAnchor.SceneLabels.WINDOW_FRAME;

        [Header("Global Mesh")]
        [Tooltip("Also load the raw global triangle mesh (higher detail but rougher)")]
        public bool loadGlobalMesh = true;

        [Tooltip("Material for global mesh (leave null for auto-generated transparent)")]
        public Material globalMeshMaterial;

        [Tooltip("Color for auto-generated global mesh material")]
        public Color globalMeshColor = new(0.3f, 0.8f, 0.3f, 0.12f);

        [Header("Furniture Prefabs")]
        [Tooltip("Spawn prefabs at detected furniture anchors")]
        public bool spawnFurniturePrefabs = true;

        public List<AnchorPrefabSpawner.AnchorPrefabGroup> furniturePrefabs = new();

        [Header("Rendering")]
        [Tooltip("When off, surfaces and furniture are invisible but still provide " +
                 "occlusion and shadows (MR passthrough mode). Physics unaffected.")]
        public bool renderSurfaces = true;

        [Header("Passthrough")]
        public bool enablePassthrough = true;

        [Header("MuJoCo Physics")]
        [Tooltip("Create MuJoCo static bodies from room mesh for physics collisions")]
        public bool createMujocoColliders = true;
        public float mujocoFriction = 1.5f;

        [Tooltip("Use only the global mesh for MuJoCo (skip per-anchor boxes). Much faster.")]
        public bool globalMeshOnlyForPhysics = true;

        [Tooltip("Collision bitmask for room geoms (default 2). " +
                 "Synth/hand geoms use conaffinity=3 to collide with both contype=1 and contype=2.")]
        public int roomContype = 2;

        [Header("Events")]
        public UnityEvent<MRUKRoom> OnRoomReady;
        public UnityEvent OnSceneFailed;

        // ── Runtime refs ─────────────────────────────────────────────────────

        MRUK _mruk;
        EffectMesh _surfaceEffectMesh;
        EffectMesh _globalEffectMesh;
        AnchorPrefabSpawner _prefabSpawner;
        readonly List<GameObject> _mjBodyObjects = new();
        bool _pendingMjRecreation;
        Material _occluderMaterial;
        Material _ptrlMaterial;

        // ── Loading stage ────────────────────────────────────────────────────

        static SceneMeshManager _instance;
        static GameObject _synthGO;
        static Vector3 _synthOrigPos;
        static Quaternion _synthOrigRot;
        Vector3 _spawnBodyPos;
        Quaternion _spawnBodyRot;
        bool _roomBodiesReady;
        bool _handBodiesReady;
        bool _synthSpawned;
        float _loadingStartTime;
        const float LoadingTimeout = 25f;

        // Runs after all scene Awake() but before Start() — disable the Synth
        // so MjScene.Start().CreateScene() builds the world without her.
        // She'll be re-enabled and positioned on the floor once the room is ready.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void OnAfterSceneLoad()
        {
            // Always pause MjScene during startup to prevent FixedUpdate time
            // accumulation from causing a burst of physics steps on the first frames.
            // if (Mujoco.MjScene.InstanceExists)
            //     Mujoco.MjScene.Instance.PauseSimulation = true;

            AutoBootstrap();

            // Only hijack synth spawn if an active SceneMeshManager will manage it.
            // Otherwise let the synth stay at its scene-authored position and
            // schedule a deferred unpause with velocity zeroing.
            var manager = Object.FindAnyObjectByType<SceneMeshManager>(FindObjectsInactive.Exclude);
            if (manager == null)
            {
                Debug.Log("[SceneMesh] No active SceneMeshManager — synth stays at start location.");
                // ScheduleDeferredUnpause();
                return;
            }

            var synth = Object.FindAnyObjectByType<Genesis.Sentience.Synth.SynthEntity>(FindObjectsInactive.Exclude);
            if (synth != null)
            {
                _synthGO = synth.gameObject;
                _synthOrigPos = _synthGO.transform.position;
                _synthOrigRot = _synthGO.transform.rotation;
                _synthGO.SetActive(false);
                Debug.Log("[SceneMesh] Synth disabled — will spawn after room loads.");
            }
        }

        static void ScheduleDeferredUnpause()
        {
            var go = new GameObject("[MjDeferredUnpause]");
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<MjDeferredUnpause>();
        }

        // ── Lifecycle ────────────────────────────────────────────────────────

        void Awake()
        {
            _instance = this;
            _loadingStartTime = Time.time;
            PauseMjScene();
        }

        void Start()
        {
            SetupMRUK();

            if (enablePassthrough) EnablePassthrough();

            SetupSurfaceEffectMesh();

            if (loadGlobalMesh) SetupGlobalEffectMesh();

            SetupPrefabSpawner();

            _mruk.RegisterSceneLoadedCallback(OnSceneLoaded);

            StartCoroutine(LoadSceneDeferred());
        }

        unsafe void LateUpdate()
        {
            if (!_pendingMjRecreation)
            {
                CheckLoadingComplete();
                return;
            }
            _pendingMjRecreation = false;

            if (createMujocoColliders && Mujoco.MjScene.InstanceExists &&
                Mujoco.MjScene.Instance.Data != null)
            {
                Mujoco.MjScene.Instance.SceneRecreationAtLateUpdateRequested = true;
                Debug.Log("[SceneMesh] Requested MjScene recreation with room colliders.");
            }
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_mruk != null && _mruk.SceneLoadedEvent != null)
                _mruk.SceneLoadedEvent.RemoveListener(OnSceneLoaded);
        }

        // ── Loading stage control ────────────────────────────────────────────

        static void PauseMjScene()
        {
            if (Mujoco.MjScene.InstanceExists)
            {
                Mujoco.MjScene.Instance.PauseSimulation = true;
                Debug.Log("[SceneMesh] MjScene PAUSED for loading stage.");
            }
        }

        void CheckLoadingComplete()
        {
            if (_synthSpawned) return;

            bool timeout = Time.time - _loadingStartTime > LoadingTimeout;
            bool ready = _roomBodiesReady && _handBodiesReady;

            if (ready || timeout)
            {
                SpawnSynth();
            }
        }

        void SpawnSynth()
        {
            if (_synthSpawned) return;
            _synthSpawned = true;

            if (_synthGO != null)
            {
                PositionSynthOnFloor();

                // Snapshot the pelvis body's world pose BEFORE any recreation
                // so we can force-set the free joint qpos later.
                var freeJoint = _synthGO.GetComponentInChildren<Mujoco.MjFreeJoint>(true);
                if (freeJoint != null)
                {
                    var bodyT = freeJoint.transform.parent;
                    _spawnBodyPos = bodyT.position;
                    _spawnBodyRot = bodyT.rotation;
                    Debug.Log($"[SceneMesh] Stored spawn body pose: pos={_spawnBodyPos}");
                }

                var anim = _synthGO.GetComponent<Animator>();
                if (anim) anim.enabled = false;

                _synthGO.SetActive(true);
                Debug.Log($"[SceneMesh] Synth enabled at {_synthGO.transform.position}");

                if (!renderSurfaces)
                    EnableAmbientLightEstimation();

                if (Mujoco.MjScene.InstanceExists)
                {
                    var scene = Mujoco.MjScene.Instance;
                    scene.postInitEvent += OnSynthMjPostInit;
                    scene.RecreateScene();
                    Debug.Log("[SceneMesh] MjScene recreated with Synth (direct call).");
                }
            }
            else
            {
                UnpauseMjScene();
            }
        }

        void OnSynthMjPostInit(object sender, System.EventArgs e)
        {
            if (Mujoco.MjScene.InstanceExists)
                Mujoco.MjScene.Instance.postInitEvent -= OnSynthMjPostInit;

            Debug.Log("[SceneMesh] Synth MuJoCo bodies initialized — stabilizing before unpause.");
            StartCoroutine(StabilizeAndUnpause());
        }

        IEnumerator StabilizeAndUnpause()
        {
            yield return null;
            yield return null;

            if (!IsMjSceneReady()) yield break;

            ForceSpawnPoseAndUnpause(Mujoco.MjScene.Instance);
        }

        static unsafe bool IsMjSceneReady()
        {
            return Mujoco.MjScene.InstanceExists &&
                   Mujoco.MjScene.Instance.Data != null &&
                   Mujoco.MjScene.Instance.Model != null;
        }

        unsafe void ForceSpawnPoseAndUnpause(Mujoco.MjScene scene)
        {
            var freeJoint = _synthGO != null
                ? _synthGO.GetComponentInChildren<Mujoco.MjFreeJoint>()
                : null;
            if (freeJoint != null && freeJoint.QposAddress >= 0)
            {
                int qa = freeJoint.QposAddress;
                Vector3 mjP = Mujoco.MjEngineTool.MjVector3(_spawnBodyPos);
                scene.Data->qpos[qa]     = mjP.x;
                scene.Data->qpos[qa + 1] = mjP.y;
                scene.Data->qpos[qa + 2] = mjP.z;

                Quaternion mjQ = Mujoco.MjEngineTool.MjQuaternion(_spawnBodyRot);
                scene.Data->qpos[qa + 3] = mjQ.w;
                scene.Data->qpos[qa + 4] = mjQ.x;
                scene.Data->qpos[qa + 5] = mjQ.y;
                scene.Data->qpos[qa + 6] = mjQ.z;

                Debug.Log($"[SceneMesh] Free joint qpos forced to {_spawnBodyPos}");
            }

            int nv = (int)scene.Model->nv;
            for (int i = 0; i < nv; i++)
                scene.Data->qvel[i] = 0;

            Mujoco.MujocoLib.mj_forward(scene.Model, scene.Data);
            scene.SyncUnityToMjState();

            scene.PauseSimulation = false;
            Debug.Log($"[SceneMesh] MjScene UNPAUSED (qpos forced, qvel zeroed). " +
                $"fixedDt={Time.fixedDeltaTime:F6}, opt.timestep={scene.Model->opt.timestep:F6}, " +
                $"subSteps={scene.SubStepsPerFixedUpdate}");
        }

        static void UnpauseMjScene()
        {
            if (Mujoco.MjScene.InstanceExists)
            {
                Mujoco.MjScene.Instance.PauseSimulation = false;
                Debug.Log("[SceneMesh] MjScene UNPAUSED.");
            }
        }

        void PositionSynthOnFloor()
        {
            if (_synthGO == null) return;

            if (_mruk == null || _mruk.Rooms.Count == 0 ||
                _mruk.Rooms[0].FloorAnchors.Count == 0)
            {
                _synthGO.transform.SetPositionAndRotation(_synthOrigPos, _synthOrigRot);
                Debug.Log("[SceneMesh] No room/floor data, using Synth original position.");
                return;
            }

            var room = _mruk.GetCurrentRoom() ?? _mruk.Rooms[0];
            var floorAnchor = room.FloorAnchors[0];

            // The Synth's root transform is at the pelvis, NOT the feet.
            // _synthOrigPos.y is the pelvis height above the scene floor (y≈0).
            // Preserve that offset above the MRUK floor so the feet aren't
            // embedded in the floor mesh (which causes massive contact forces).
            float pelvisHeight = Mathf.Max(_synthOrigPos.y, 0.5f);
            float floorY = floorAnchor.transform.position.y + pelvisHeight;

            // Start from the floor anchor center — guaranteed inside the room
            var center = floorAnchor.transform.position;
            center.y = floorY;

            // Scan a grid and pick the candidate closest to room center that
            // clears walls (wallBuffer) and furniture volumes (volumeBuffer).
            const float gridStep = 0.3f;
            const float volumeBuffer = 0.5f;
            const float wallBuffer = 0.6f;   // half-shoulder-width + margin

            var bounds = room.GetRoomBounds();
            float xMin = bounds.min.x + wallBuffer;
            float xMax = bounds.max.x - wallBuffer;
            float zMin = bounds.min.z + wallBuffer;
            float zMax = bounds.max.z - wallBuffer;

            Vector3 bestPos = center;
            float bestDist = float.MaxValue;

            for (float x = xMin; x <= xMax; x += gridStep)
            {
                for (float z = zMin; z <= zMax; z += gridStep)
                {
                    var candidate = new Vector3(x, floorY, z);
                    if (!room.IsPositionInRoom(candidate, testVerticalBounds: false))
                        continue;
                    if (room.IsPositionInSceneVolume(candidate, distanceBuffer: volumeBuffer))
                        continue;

                    float dist = (candidate - center).sqrMagnitude;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestPos = candidate;
                    }
                }
            }

            if (bestDist >= float.MaxValue)
            {
                bestPos = center;
                Debug.LogWarning("[SceneMesh] No clear floor spot found, using floor center.");
            }

            // Face toward room center if not spawning at center
            var lookDir = (center - bestPos);
            lookDir.y = 0;
            var rot = lookDir.sqrMagnitude > 0.01f
                ? Quaternion.LookRotation(lookDir, Vector3.up)
                : _synthOrigRot;

            _synthGO.transform.SetPositionAndRotation(bestPos, rot);
            Debug.Log($"[SceneMesh] Synth at {bestPos} (inRoom={room.IsPositionInRoom(bestPos)}, " +
                      $"inVolume={room.IsPositionInSceneVolume(bestPos)})");
        }

        /// <summary>
        /// Called by PlayerHandBodies after its MjScene recreation completes.
        /// </summary>
        public static void NotifyHandBodiesReady()
        {
            if (_instance != null)
            {
                _instance._handBodiesReady = true;
                Debug.Log("[SceneMesh] Hand bodies ready notification received.");

                // Hand occluder is self-applied by PlayerHandBodies when each hand initializes
            }
        }

        /// <summary>
        /// Returns the occluder material if occlusion mode is active, null otherwise.
        /// Called by PlayerHandBodies to self-apply hand occlusion on init.
        /// </summary>
        public static Material GetHandOccluderIfActive()
        {
            if (_instance == null || _instance.renderSurfaces) return null;
            return _instance.GetOccluderMaterial();
        }

        /// <summary>
        /// Call if no hand bodies will be created (e.g., controllers only, no hand tracking).
        /// </summary>
        public static void NotifyNoHandBodies()
        {
            NotifyHandBodiesReady();
        }

        // ── MRUK Setup ──────────────────────────────────────────────────────

        void SetupMRUK()
        {
            _mruk = FindAnyObjectByType<MRUK>();
            if (_mruk == null)
            {
                var go = new GameObject("[MRUK]");
                go.transform.SetParent(transform, false);
                _mruk = go.AddComponent<MRUK>();
            }

            _mruk.SceneSettings ??= new MRUK.MRUKSettings();
            _mruk.SceneSettings.DataSource = dataSource;
            _mruk.SceneSettings.LoadSceneOnStartup = false;
            _mruk.SceneSettings.EnableHighFidelityScene = highFidelityScene;

            if (fallbackRoomPrefabs != null && fallbackRoomPrefabs.Length > 0)
            {
                _mruk.SceneSettings.RoomPrefabs = fallbackRoomPrefabs;
            }
            else if (_mruk.SceneSettings.RoomPrefabs == null || _mruk.SceneSettings.RoomPrefabs.Length == 0)
            {
                TryLoadDefaultRoomPrefabs();
            }

            Debug.Log($"[SceneMesh] MRUK configured: source={dataSource}, hifi={highFidelityScene}");
        }

        void TryLoadDefaultRoomPrefabs()
        {
            var prefab = Resources.Load<GameObject>("MRUK/LivingRoom00");
            if (prefab != null)
            {
                _mruk.SceneSettings.RoomPrefabs = new[] { prefab };
                Debug.Log($"[SceneMesh] Loaded room fallback prefab: {prefab.name}");
            }
            else
            {
                Debug.LogWarning("[SceneMesh] No room fallback prefab found in Resources/MRUK/. " +
                    "Copy a room prefab from Packages/com.meta.xr.mrutilitykit/Core/Rooms/Prefabs/");
            }
        }

        // ── Deferred Scene Load ───────────────────────────────────────────────
        // MRUK.Awake() fires before we can configure SceneSettings, so we
        // disable LoadSceneOnStartup and manually trigger the load after
        // EffectMesh components have registered their callbacks (next frame).

        IEnumerator LoadSceneDeferred()
        {
            yield return null;

            if (_mruk.IsInitialized)
            {
                Debug.Log("[SceneMesh] MRUK already initialized, skipping manual load.");
                yield break;
            }

            var sceneModel = highFidelityScene
                ? MRUK.SceneModel.V2FallbackV1
                : MRUK.SceneModel.V1;

            Debug.Log($"[SceneMesh] Triggering scene load: source={dataSource}, model={sceneModel}");

#if UNITY_EDITOR
            if (dataSource == MRUK.SceneDataSource.Device ||
                dataSource == MRUK.SceneDataSource.DeviceWithPrefabFallback)
            {
                if (_mruk.SceneSettings.RoomPrefabs != null && _mruk.SceneSettings.RoomPrefabs.Length > 0)
                {
                    _ = _mruk.LoadSceneFromPrefab(_mruk.SceneSettings.RoomPrefabs[0]);
                }
                else
                {
                    Debug.LogWarning("[SceneMesh] No room prefabs for editor fallback.");
                    OnSceneFailed?.Invoke();
                }
                yield break;
            }
#endif
            _ = _mruk.LoadSceneFromDevice(sceneModel: sceneModel);
        }

        // ── EffectMesh: Room Surfaces ────────────────────────────────────────

        void SetupSurfaceEffectMesh()
        {
            var go = new GameObject("RoomSurfaces_EffectMesh");
            go.transform.SetParent(transform, false);

            _surfaceEffectMesh = go.AddComponent<EffectMesh>();
            _surfaceEffectMesh.SpawnOnStart = MRUK.RoomFilter.AllRooms;
            _surfaceEffectMesh.Labels = surfaceLabels;
            _surfaceEffectMesh.Colliders = surfaceColliders;
            _surfaceEffectMesh.CutHoles = cutHoles;
            _surfaceEffectMesh.CastShadow = renderSurfaces;

            if (renderSurfaces)
            {
                _surfaceEffectMesh.MeshMaterial = roomSurfaceMaterial != null
                    ? roomSurfaceMaterial
                    : CreateSurfaceMaterial();
            }
            else
            {
                _surfaceEffectMesh.MeshMaterial = GetPTRLMaterial();
            }

            Debug.Log($"[SceneMesh] Surface EffectMesh ready: labels={surfaceLabels}, " +
                      $"render={renderSurfaces}, castShadow={renderSurfaces}");
        }

        // ── EffectMesh: Global Mesh ──────────────────────────────────────────

        void SetupGlobalEffectMesh()
        {
            var go = new GameObject("GlobalMesh_EffectMesh");
            go.transform.SetParent(transform, false);

            _globalEffectMesh = go.AddComponent<EffectMesh>();
            _globalEffectMesh.SpawnOnStart = MRUK.RoomFilter.AllRooms;
            _globalEffectMesh.Labels = MRUKAnchor.SceneLabels.GLOBAL_MESH;
            _globalEffectMesh.Colliders = !createMujocoColliders;
            _globalEffectMesh.CastShadow = renderSurfaces;

            if (renderSurfaces)
            {
                _globalEffectMesh.HideMesh = true;
                _globalEffectMesh.MeshMaterial = globalMeshMaterial != null
                    ? globalMeshMaterial
                    : CreateGlobalMeshMaterial();
            }
            else
            {
                _globalEffectMesh.HideMesh = false;
                _globalEffectMesh.MeshMaterial = GetPTRLMaterial();
            }

            Debug.Log("[SceneMesh] Global mesh EffectMesh ready.");
        }

        // ── Prefab Spawner ───────────────────────────────────────────────────

        void SetupPrefabSpawner()
        {
            if (!spawnFurniturePrefabs) return;

            if (furniturePrefabs.Count == 0) LoadDefaultFurniturePrefabs();
            if (furniturePrefabs.Count == 0) return;

            var go = new GameObject("Furniture_PrefabSpawner");
            go.transform.SetParent(transform, false);

            _prefabSpawner = go.AddComponent<AnchorPrefabSpawner>();
            _prefabSpawner.SpawnOnStart = MRUK.RoomFilter.AllRooms;
            _prefabSpawner.PrefabsToSpawn = furniturePrefabs;

            Debug.Log($"[SceneMesh] Prefab spawner ready: {furniturePrefabs.Count} group(s).");
        }

        void LoadDefaultFurniturePrefabs()
        {
            var volumePrefab = Resources.Load<GameObject>("MRUK/Volume");
            var wallPrefab = Resources.Load<GameObject>("MRUK/WALL");
            var otherPrefab = Resources.Load<GameObject>("MRUK/OTHER");

            if (volumePrefab == null)
            {
                Debug.LogWarning("[SceneMesh] MRUK/Volume prefab not found in Resources. " +
                    "Copy Volume.prefab from Packages/com.meta.xr.mrutilitykit/Core/Prefabs/ " +
                    "into Assets/Resources/MRUK/");
                return;
            }

            furniturePrefabs.Add(new AnchorPrefabSpawner.AnchorPrefabGroup
            {
                Labels = MRUKAnchor.SceneLabels.TABLE |
                         MRUKAnchor.SceneLabels.COUCH |
                         MRUKAnchor.SceneLabels.BED |
                         MRUKAnchor.SceneLabels.STORAGE |
                         MRUKAnchor.SceneLabels.SCREEN |
                         MRUKAnchor.SceneLabels.LAMP |
                         MRUKAnchor.SceneLabels.PLANT,
                Prefabs = new List<GameObject> { volumePrefab },
                Scaling = AnchorPrefabSpawner.ScalingMode.Stretch,
                Alignment = AnchorPrefabSpawner.AlignMode.Automatic,
                PrefabSelection = AnchorPrefabSpawner.SelectionMode.Random
            });

            if (wallPrefab != null)
            {
                furniturePrefabs.Add(new AnchorPrefabSpawner.AnchorPrefabGroup
                {
                    Labels = MRUKAnchor.SceneLabels.WALL_FACE |
                             MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE |
                             MRUKAnchor.SceneLabels.INNER_WALL_FACE,
                    Prefabs = new List<GameObject> { wallPrefab },
                    Scaling = AnchorPrefabSpawner.ScalingMode.Stretch,
                    Alignment = AnchorPrefabSpawner.AlignMode.Automatic,
                    PrefabSelection = AnchorPrefabSpawner.SelectionMode.Random
                });
            }

            if (otherPrefab != null)
            {
                furniturePrefabs.Add(new AnchorPrefabSpawner.AnchorPrefabGroup
                {
                    Labels = MRUKAnchor.SceneLabels.OTHER,
                    Prefabs = new List<GameObject> { otherPrefab },
                    Scaling = AnchorPrefabSpawner.ScalingMode.Stretch,
                    Alignment = AnchorPrefabSpawner.AlignMode.Automatic,
                    PrefabSelection = AnchorPrefabSpawner.SelectionMode.Random
                });
            }

            Debug.Log($"[SceneMesh] Loaded {furniturePrefabs.Count} prefab group(s) from Resources/MRUK/");
        }

        // ── Scene Loaded Callback ────────────────────────────────────────────

        void OnSceneLoaded()
        {
            Debug.Log("[SceneMesh] MRUK scene loaded.");

            if (_mruk.Rooms.Count == 0)
            {
                Debug.LogWarning("[SceneMesh] No rooms found.");
                _roomBodiesReady = true;
                OnSceneFailed?.Invoke();
                return;
            }

            foreach (var room in _mruk.Rooms)
            {
                Debug.Log($"[SceneMesh] Room: {room.Anchors.Count} anchors");

                if (createMujocoColliders)
                    CreateMujocoFromRoom(room);

                OnRoomReady?.Invoke(room);
            }

            if (!renderSurfaces)
                StartCoroutine(ApplyOccluderDeferred());

            if (createMujocoColliders && _mjBodyObjects.Count > 0)
            {
                if (Mujoco.MjScene.InstanceExists)
                    Mujoco.MjScene.Instance.postInitEvent += OnRoomMjPostInit;
                _pendingMjRecreation = true;
            }
            else
            {
                _roomBodiesReady = true;
            }
        }

        void OnRoomMjPostInit(object sender, System.EventArgs e)
        {
            _roomBodiesReady = true;
            if (Mujoco.MjScene.InstanceExists)
                Mujoco.MjScene.Instance.postInitEvent -= OnRoomMjPostInit;
            Debug.Log("[SceneMesh] Room MuJoCo bodies initialized.");
        }

        // ── MuJoCo from MRUK room ────────────────────────────────────────────

        void CreateMujocoFromRoom(MRUKRoom room)
        {
            if (!Mujoco.MjScene.InstanceExists) return;

            bool addedGlobalMesh = false;

            if (loadGlobalMesh)
            {
                foreach (var anchor in room.Anchors)
                {
                    if (!anchor.HasAnyLabel(MRUKAnchor.SceneLabels.GLOBAL_MESH))
                        continue;

                    var globalMesh = anchor.GlobalMesh;
                    if (globalMesh == null || globalMesh.vertexCount == 0)
                    {
                        Debug.LogWarning("[SceneMesh] Global mesh anchor has no mesh data.");
                        continue;
                    }

                    AttachMujocoMeshBody(anchor.gameObject, globalMesh);
                    addedGlobalMesh = true;
                    Debug.Log($"[SceneMesh] MuJoCo body from global mesh: " +
                              $"{globalMesh.vertexCount} verts, {globalMesh.triangles.Length / 3} tris");
                }
            }

            if (globalMeshOnlyForPhysics && addedGlobalMesh)
            {
                Debug.Log("[SceneMesh] Using global mesh only — skipping per-anchor MuJoCo bodies.");
                return;
            }

            int anchorCount = 0;
            foreach (var anchor in room.Anchors)
            {
                if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.GLOBAL_MESH))
                    continue;

                if (!anchor.HasAnyLabel(
                    MRUKAnchor.SceneLabels.FLOOR |
                    MRUKAnchor.SceneLabels.CEILING |
                    MRUKAnchor.SceneLabels.WALL_FACE |
                    MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE |
                    MRUKAnchor.SceneLabels.INNER_WALL_FACE |
                    MRUKAnchor.SceneLabels.TABLE |
                    MRUKAnchor.SceneLabels.COUCH |
                    MRUKAnchor.SceneLabels.BED |
                    MRUKAnchor.SceneLabels.STORAGE |
                    MRUKAnchor.SceneLabels.OTHER))
                    continue;

                if (anchor.VolumeBounds.HasValue)
                    AttachMujocoBox(anchor);
                else if (anchor.PlaneRect.HasValue)
                    AttachMujocoPlane(anchor);

                anchorCount++;
            }
            Debug.Log($"[SceneMesh] Created {anchorCount} per-anchor MuJoCo bodies (fallback).");
        }

        void AttachMujocoMeshBody(GameObject parent, Mesh mesh)
        {
            var bodyGO = new GameObject($"{parent.name}_MjBody");
            bodyGO.transform.SetParent(parent.transform, false);
            bodyGO.AddComponent<Mujoco.MjBody>();

            var geomGO = new GameObject($"{parent.name}_MjGeom");
            geomGO.transform.SetParent(bodyGO.transform, false);

            var geom = geomGO.AddComponent<Mujoco.MjGeom>();
            geom.ShapeType = Mujoco.MjShapeComponent.ShapeTypes.Mesh;
            geom.Mesh.Mesh = mesh;
            geom.Mass = 0;
            geom.Density = 0;

            ConfigureRoomGeom(geom);
            _mjBodyObjects.Add(bodyGO);
        }

        void AttachMujocoBox(MRUKAnchor anchor)
        {
            var bounds = anchor.VolumeBounds.Value;

            var bodyGO = new GameObject($"{anchor.name}_MjBody");
            bodyGO.transform.SetParent(anchor.transform, false);
            bodyGO.transform.localPosition = bounds.center;
            bodyGO.AddComponent<Mujoco.MjBody>();

            var geomGO = new GameObject($"{anchor.name}_MjGeom");
            geomGO.transform.SetParent(bodyGO.transform, false);

            var geom = geomGO.AddComponent<Mujoco.MjGeom>();
            geom.ShapeType = Mujoco.MjShapeComponent.ShapeTypes.Box;
            geom.Box.Extents = bounds.extents;
            geom.Mass = 0;
            geom.Density = 0;

            ConfigureRoomGeom(geom);
            _mjBodyObjects.Add(bodyGO);
        }

        void AttachMujocoPlane(MRUKAnchor anchor)
        {
            var rect = anchor.PlaneRect.Value;

            var bodyGO = new GameObject($"{anchor.name}_MjBody");
            bodyGO.transform.SetParent(anchor.transform, false);
            bodyGO.transform.localPosition = new Vector3(rect.center.x, rect.center.y, 0f);
            bodyGO.AddComponent<Mujoco.MjBody>();

            var geomGO = new GameObject($"{anchor.name}_MjGeom");
            geomGO.transform.SetParent(bodyGO.transform, false);

            var geom = geomGO.AddComponent<Mujoco.MjGeom>();
            geom.ShapeType = Mujoco.MjShapeComponent.ShapeTypes.Box;
            geom.Box.Extents = new Vector3(rect.width * 0.5f, rect.height * 0.5f, 0.01f);
            geom.Mass = 0;
            geom.Density = 0;

            ConfigureRoomGeom(geom);
            _mjBodyObjects.Add(bodyGO);
        }

        void ConfigureRoomGeom(Mujoco.MjGeom geom)
        {
            geom.Settings.Friction.Sliding = mujocoFriction;
            geom.Settings.Friction.Torsional = 0.05f;
            geom.Settings.Friction.Rolling = 0.01f;

            // Synth ct=1 ca=1, Hand ct=4 ca=1, Room ct=2 ca=1
            // Room↔Synth: (1&1)||(2&1)=YES  Room↔Hand: (4&1)||(2&1)=NO  Room↔Room: NO
            geom.Settings.Filtering.Contype = roomContype;
            geom.Settings.Filtering.Conaffinity = 1;

            geom.Settings.Solver.ConDim = 3;
            geom.Settings.Solver.Margin = 0.001f;
        }

        // Performance levels moved to QuestPerformanceManager (on Global Settings object)

        // ── Passthrough ──────────────────────────────────────────────────────

        static void EnablePassthrough()
        {
            var layer = FindAnyObjectByType<OVRPassthroughLayer>();
            if (layer != null)
            {
                layer.hidden = false;
                layer.textureOpacity = 1f;
            }
            if (OVRManager.instance != null)
                OVRManager.instance.isInsightPassthroughEnabled = true;
            Debug.Log("[SceneMesh] Passthrough enabled.");
        }

        // ── Materials ────────────────────────────────────────────────────────

        static Material CreateSurfaceMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var mat = new Material(shader);

            var color = new Color(0.85f, 0.87f, 0.9f, 0.35f);
            mat.SetColor("_BaseColor", color);
            mat.color = color;

            mat.SetFloat("_Surface", 1);
            mat.SetFloat("_Blend", 0);
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 1);
            mat.SetFloat("_AlphaClip", 0);
            mat.renderQueue = 3050;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

            mat.SetFloat("_Metallic", 0.05f);
            mat.SetFloat("_Smoothness", 0.6f);
            mat.SetInt("_Cull", 2);
            return mat;
        }

        Material CreateGlobalMeshMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var mat = new Material(shader);

            mat.SetColor("_BaseColor", globalMeshColor);
            mat.color = globalMeshColor;

            mat.SetFloat("_Surface", 1);
            mat.SetFloat("_Blend", 0);
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0);
            mat.SetFloat("_AlphaClip", 0);
            mat.renderQueue = 3000;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 0.1f);
            mat.SetInt("_Cull", 0);
            return mat;
        }

        Material GetOccluderMaterial()
        {
            if (_occluderMaterial != null) return _occluderMaterial;

            var loaded = Resources.Load<Material>("InvisibleOccluder");
            if (loaded != null)
            {
                _occluderMaterial = loaded;
                Debug.Log("[SceneMesh] Loaded InvisibleOccluder material from Resources.");
                return _occluderMaterial;
            }

            var shader = Shader.Find("Meta/MRUK/MixedReality/InvisibleOccluder");
            if (shader == null)
            {
                Debug.LogError("[SceneMesh] InvisibleOccluder shader NOT found — " +
                               "occlusion mode will not work on device. " +
                               "Ensure Resources/InvisibleOccluder.mat exists.");
                return CreateSurfaceMaterial();
            }

            _occluderMaterial = new Material(shader);
            _occluderMaterial.name = "SceneMesh_Occluder";
            _occluderMaterial.renderQueue = 1998;
            return _occluderMaterial;
        }

        Material GetPTRLMaterial()
        {
            if (_ptrlMaterial != null) return _ptrlMaterial;

            // PTRLWithDepth = HighlightsAndShadows + ZWrite On + queue Geometry-1.
            // Single shader handles both shadow/highlight rendering AND depth
            // occlusion — no need for a duplicate depth-only EffectMesh.
            var loaded = Resources.Load<Material>("PTRLHighlightsAndShadows");
            if (loaded != null)
            {
                _ptrlMaterial = new Material(loaded);
                Debug.Log("[SceneMesh] Loaded PTRLWithDepth material from Resources.");
                return _ptrlMaterial;
            }

            var shader = Shader.Find("Sentience/PTRLWithDepth");
            if (shader == null)
                shader = Shader.Find("Meta/MRUK/Scene/HighlightsAndShadows");
            if (shader == null)
            {
                Debug.LogError("[SceneMesh] PTRL shader NOT found — falling back to InvisibleOccluder.");
                return GetOccluderMaterial();
            }

            _ptrlMaterial = new Material(shader);
            _ptrlMaterial.name = "SceneMesh_PTRLWithDepth";
            _ptrlMaterial.renderQueue = 1999;
            _ptrlMaterial.SetFloat("_ShadowIntensity", 0.7f);
            _ptrlMaterial.SetFloat("_HighLightAttenuation", 0.5f);
            _ptrlMaterial.SetFloat("_HighlightOpacity", 0.25f);
            _ptrlMaterial.SetFloat("_EnvironmentDepthBias", 0.06f);
            Debug.Log("[SceneMesh] Created PTRLWithDepth material from shader.");
            return _ptrlMaterial;
        }

        void EnableAmbientLightEstimation()
        {
            if (GetComponent<AmbientLightEstimator>() != null) return;

            var estimator = gameObject.AddComponent<AmbientLightEstimator>();
            Debug.Log("[SceneMesh] AmbientLightEstimator enabled for MR lighting harmonization.");
        }

        // ── Auto-bootstrap (runs inside EarlyPauseMjScene AfterSceneLoad) ──

        static void AutoBootstrap()
        {
            if (Object.FindAnyObjectByType<SceneMeshManager>(FindObjectsInactive.Include) != null) return;
            var go = new GameObject("[SceneMeshManager]");
            go.AddComponent<SceneMeshManager>();
            Debug.Log("[SceneMesh] Auto-created SceneMeshManager.");
        }

        // ── Public API ───────────────────────────────────────────────────────

        public MRUK MrukInstance => _mruk;
        public bool IsLoaded => _mruk != null && _mruk.IsInitialized && _mruk.Rooms.Count > 0;
        public IReadOnlyList<MRUKRoom> Rooms => _mruk?.Rooms;

        public MRUKRoom GetCurrentRoom() => _mruk?.GetCurrentRoom();

        /// <summary>
        /// Toggle between visible surfaces and occlusion-only mode at runtime.
        /// When false, all room geometry becomes invisible but still provides
        /// depth occlusion and shadow casting — the Synth appears in your real room.
        /// </summary>
        public void SetRenderSurfaces(bool render)
        {
            renderSurfaces = render;
            ForceApplyOccluder(!render);
            Debug.Log($"[SceneMesh] Render surfaces = {render}");
        }

        IEnumerator ApplyOccluderDeferred()
        {
            yield return null;
            yield return null;
            ForceApplyOccluder(true);
        }

        void ForceApplyOccluder(bool useMRMode)
        {
            var ptrl = useMRMode ? GetPTRLMaterial() : null;
            var visible = useMRMode
                ? null
                : (roomSurfaceMaterial != null ? roomSurfaceMaterial : CreateSurfaceMaterial());

            if (_surfaceEffectMesh != null)
            {
                var mat = useMRMode ? ptrl : visible;
                _surfaceEffectMesh.MeshMaterial = mat;
                _surfaceEffectMesh.OverrideEffectMaterial(mat);
                _surfaceEffectMesh.HideMesh = false;
                _surfaceEffectMesh.CastShadow = !useMRMode;
            }

            if (_globalEffectMesh != null)
            {
                if (useMRMode)
                {
                    _globalEffectMesh.HideMesh = false;
                    _globalEffectMesh.MeshMaterial = ptrl;
                    _globalEffectMesh.OverrideEffectMaterial(ptrl);
                }
                else
                {
                    _globalEffectMesh.HideMesh = true;
                }
            }

            if (_prefabSpawner != null)
            {
                foreach (var r in _prefabSpawner.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (useMRMode)
                    {
                        r.sharedMaterial = ptrl;
                        r.shadowCastingMode = ShadowCastingMode.Off;
                    }
                    else
                    {
                        r.shadowCastingMode = ShadowCastingMode.Off;
                    }
                }
            }

            int count = 0;
            foreach (var room in _mruk.Rooms)
            {
                foreach (var r in room.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (useMRMode)
                    {
                        r.sharedMaterial = ptrl;
                        r.shadowCastingMode = ShadowCastingMode.Off;
                    }
                    count++;
                }
            }

            Debug.Log($"[SceneMesh] ForceApplyOccluder({useMRMode}): " +
                      $"ptrl={ptrl != null}, room renderers={count}");
        }

        public void SetSurfaceVisibility(bool visible)
        {
            if (_surfaceEffectMesh != null)
                _surfaceEffectMesh.HideMesh = !visible;
        }

        public void SetGlobalMeshVisibility(bool visible)
        {
            if (_globalEffectMesh != null)
                _globalEffectMesh.HideMesh = !visible;
        }
    }

    /// <summary>
    /// Waits a couple of frames for MjScene to fully initialize, zeros all
    /// velocities, then unpauses. Used when no SceneMeshManager is active
    /// so MjScene doesn't run a burst of accumulated FixedUpdates on startup.
    /// </summary>
    internal class MjDeferredUnpause : MonoBehaviour
    {
        private int _framesToWait = 100;

        void Update()
        {
            if (_framesToWait-- > 0) return;

            StabilizeAndUnpause();
            Destroy(gameObject);
        }

        private static unsafe void StabilizeAndUnpause()
        {
            if (!Mujoco.MjScene.InstanceExists) return;

            var scene = Mujoco.MjScene.Instance;
            if (scene.Model != null && scene.Data != null)
            {
                int nv = (int)scene.Model->nv;
                for (int i = 0; i < nv; i++)
                    scene.Data->qvel[i] = 0;

                Mujoco.MujocoLib.mj_forward(scene.Model, scene.Data);
                scene.SyncUnityToMjState();
            }
            scene.PauseSimulation = false;
            double ts = scene.Model != null ? scene.Model->opt.timestep : -1;
            Debug.Log($"[SceneMesh] MjScene UNPAUSED (deferred, velocities zeroed). " +
                $"fixedDt={Time.fixedDeltaTime:F6}, opt.timestep={ts:F6}, " +
                $"subSteps={scene.SubStepsPerFixedUpdate}");
        }
    }
}
