// Copyright 2026 Arghya Sur / Genesis  –  Apache-2.0
//
// Creates per-bone MuJoCo mocap bodies for the player's VR hands using the
// actual hand mesh from OVRMesh, split per skeleton bone — same approach as
// SynthPhysicalBody uses for the Synth humanoid.
//
// CONTACT TUNING:
//   Hand geoms have high friction, stiff contact response, contact margin,
//   torsional friction (condim=4), and priority=1 so hand contact parameters
//   always win when paired with Synth defaults.
//
// GRAB:
//   Per-bone offset-locked grab.  When grip is pressed, each hand bone locks
//   its current offset to the nearest Synth body.  Forces + torques maintain
//   those offsets as the hand moves, creating a tight wrapping grip.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Mujoco;

namespace Genesis.Sentience.VR
{
    public class PlayerHandBodies : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        public enum HandDetail { Minimal, Full }

        [Header("Hand Detail")]
        [Tooltip("Minimal = palm + 5 distal tips.  Full = all skinnable bones.")]
        public HandDetail detail = HandDetail.Minimal;

        [Header("Hand Tracking")]
        public OVRSkeleton leftSkeleton;
        public OVRSkeleton rightSkeleton;

        [Header("Controller Anchors (fallback)")]
        public Transform leftHandAnchor;
        public Transform rightHandAnchor;

        [Header("Mesh Splitting")]
        public float weightThreshold = 0.3f;
        public int minVerticesPerBone = 10;

        [Header("Geom Properties")]
        public float geomDensity = 100f;

        [Header("Solver")]
        [Tooltip("MuJoCo physics timestep (s). Lower = more accurate but more substeps per FixedUpdate.")]
        public float solverTimestep = 0.005f;
        [Tooltip("MuJoCo solver iterations per step. Higher = more accurate contacts but slower.")]
        public int solverIterations = 20;

        [Header("Contact Tuning")]
        [Tooltip("Contact detection margin (m). Contacts generated before actual touch.")]
        public float contactMargin = 0.003f;
        [Tooltip("Sliding friction coefficient for hand geoms")]
        public float slidingFriction = 3.0f;

        [Header("Grab (Per-Bone Offset-Locked)")]
        [Tooltip("Stiffness for grab PD controller (N/m)")]
        public float grabStiffness = 15000f;
        [Tooltip("Damping for grab PD controller (Ns/m)")]
        public float grabDamping = 800f;
        [Tooltip("Maximum grab force per bone-body pair (N)")]
        public float maxGrabForce = 600f;
        [Tooltip("Per-bone distance to initiate grab lock (m)")]
        public float boneGrabReach = 0.06f;
        [Tooltip("Rotational stiffness for grab torque (Nm/rad)")]
        public float grabRotStiffness = 50f;

        [Header("Debug")]
        public bool showGizmos = true;
        [Tooltip("Show per-bone mesh on hand bodies (like MuJoCo geom viz in scene view)")]
        public bool showHandViz = false;
        [Range(0.1f, 1f)] public float vizAlphaLeft  = 0.55f;
        [Range(0.1f, 1f)] public float vizAlphaRight = 0.55f;

        static readonly HashSet<int> _minimalBoneIndices = new()
        {
            0, 5, 8, 11, 14, 18,
        };

        // ── Runtime state ─────────────────────────────────────────────────────

        struct HandRuntime
        {
            public MjMocapBody[] mocapBodies;
            public int[] boneIndices;
            public int[] mocapMjIndices;
            public Vector3[] bindLocalPos;
            public Quaternion[] bindLocalRot;
            public MeshRenderer[] vizRenderers;
            public bool initialized;
        }

        HandRuntime _left, _right;

        // Per-bone grab state: each hand bone can independently lock onto a Synth body
        struct BoneGrabTarget
        {
            public int synthIdx;        // index into _synthBodies, -1 = none
            public Vector3 localOffset; // grab point in body local space
        }

        struct HandGrabState
        {
            public Vector3 posUnity, prevPosUnity, velocityUnity;
            public bool grabbing, wasGrabbing;
            public BoneGrabTarget[] boneTargets; // one per hand bone
        }

        HandGrabState _leftGrab, _rightGrab;

        struct SynthBodyInfo
        {
            public MjBody body;
            public int mjId;
            public Vector3 prevPos;
            public Vector3 cachedPos;
            public Quaternion cachedRot;
        }
        SynthBodyInfo[] _synthBodies;

        // Per-hand cached bone transforms — read once per FixedUpdate, used in ctrl callback
        Vector3[] _leftBonePosCache, _rightBonePosCache;
        Quaternion[] _leftBoneRotCache, _rightBoneRotCache;

        bool _mjReady;
        bool _subscribed;
        bool _handsCreated;
        bool _wasHandTracking;
        Coroutine _initCoroutine;
        GameObject _leftRoot, _rightRoot;

        // Last known good positions per hand (for hold-on-invalid)
        Vector3[] _leftLastPos;
        Quaternion[] _leftLastRot;
        Vector3[] _rightLastPos;
        Quaternion[] _rightLastRot;

        Material _vizMatLeft, _vizMatRight;

        // ── Start ─────────────────────────────────────────────────────────────

        void Start()
        {
            EnsureSolverSettings();
            AutoDiscoverSkeletons();
            Subscribe();
            TryStartHandInit();
        }

        void AutoDiscoverSkeletons()
        {
            bool needLeft  = leftSkeleton == null;
            bool needRight = rightSkeleton == null;
            if (!needLeft && !needRight) return;

            var allSkels = FindObjectsByType<OVRSkeleton>(FindObjectsSortMode.None);
            foreach (var skel in allSkels)
            {
                if (!skel.gameObject.activeInHierarchy) continue;
                var t = skel.GetSkeletonType();
                if (needLeft && (t == OVRSkeleton.SkeletonType.HandLeft || t == OVRSkeleton.SkeletonType.XRHandLeft))
                {
                    leftSkeleton = skel;
                    needLeft = false;
                    Debug.Log($"[PlayerHandBodies] Auto-discovered left skeleton on '{skel.gameObject.name}'");
                }
                else if (needRight && (t == OVRSkeleton.SkeletonType.HandRight || t == OVRSkeleton.SkeletonType.XRHandRight))
                {
                    rightSkeleton = skel;
                    needRight = false;
                    Debug.Log($"[PlayerHandBodies] Auto-discovered right skeleton on '{skel.gameObject.name}'");
                }
            }

            if (needLeft)  Debug.LogWarning("[PlayerHandBodies] No active left OVRSkeleton found in scene.");
            if (needRight) Debug.LogWarning("[PlayerHandBodies] No active right OVRSkeleton found in scene.");
        }

        void OnDisable() => Unsubscribe();
        void OnDestroy()
        {
            Unsubscribe();
            if (_vizMatLeft)  Destroy(_vizMatLeft);
            if (_vizMatRight) Destroy(_vizMatRight);
            DestroyHandBodies(ref _left, ref _leftRoot);
            DestroyHandBodies(ref _right, ref _rightRoot);
        }

        static unsafe bool IsMjSceneDataReady()
        {
            return MjScene.InstanceExists && MjScene.Instance.Data != null;
        }

        void Subscribe()
        {
            if (_subscribed || !MjScene.InstanceExists) return;
            MjScene.Instance.postInitEvent += OnMjPostInit;
            MjScene.Instance.ctrlCallback  += OnCtrlCallback;
            _subscribed = true;
        }

        void Unsubscribe()
        {
            if (!_subscribed || !MjScene.InstanceExists) return;
            MjScene.Instance.postInitEvent -= OnMjPostInit;
            MjScene.Instance.ctrlCallback  -= OnCtrlCallback;
            _subscribed = false;
        }

        // ── Solver quality ────────────────────────────────────────────────────

        void EnsureSolverSettings()
        {
            var settings = FindAnyObjectByType<MjGlobalSettings>();
            if (settings == null)
            {
                var target = MjScene.InstanceExists ? MjScene.Instance.gameObject : gameObject;
                settings = target.AddComponent<MjGlobalSettings>();
            }
            Time.fixedDeltaTime = solverTimestep;
            settings.GlobalOptions.Iterations      = solverIterations;
            settings.GlobalOptions.NoSlipIterations = 0;
            Debug.Log($"[PlayerHandBodies] Solver: fixedDeltaTime={solverTimestep}, iterations={solverIterations}, noslip=0");
        }

        // ── Init control ─────────────────────────────────────────────────────

        void TryStartHandInit()
        {
            if (_initCoroutine != null) return;
            _initCoroutine = StartCoroutine(WaitAndCreateHandBodies());
        }

        void DestroyHandBodies(ref HandRuntime hand, ref GameObject root)
        {
            if (root != null)
            {
                Destroy(root);
                root = null;
            }
            hand = default;
        }

        void ReinitializeHands()
        {
            Debug.Log("[PlayerHandBodies] Reinitializing hand bodies for tracking mode change.");

            if (_initCoroutine != null)
            {
                StopCoroutine(_initCoroutine);
                _initCoroutine = null;
            }

            _mjReady = false;
            _handsCreated = false;
            _cachedLeft = default;
            _cachedRight = default;
            ClearBoneTargets(ref _leftGrab);
            ClearBoneTargets(ref _rightGrab);
            _leftGrab.boneTargets = null;
            _rightGrab.boneTargets = null;

            DestroyHandBodies(ref _left, ref _leftRoot);
            DestroyHandBodies(ref _right, ref _rightRoot);

            _leftLastPos = null; _leftLastRot = null;
            _rightLastPos = null; _rightLastRot = null;

            AutoDiscoverSkeletons();
            TryStartHandInit();
        }

        static bool IsHandTrackingActive()
        {
            var active = OVRInput.GetActiveController();
            return (active & OVRInput.Controller.Hands) != 0 ||
                   (active & OVRInput.Controller.LHand) != 0 ||
                   (active & OVRInput.Controller.RHand) != 0;
        }

        // ── Coroutine: wait for mesh data, split, create bodies ──────────────

        IEnumerator WaitAndCreateHandBodies()
        {
            OVRMesh leftMesh = null, rightMesh = null;
            OVRMeshRenderer leftMR = null, rightMR = null;

            if (leftSkeleton)
            {
                leftMesh = leftSkeleton.GetComponent<OVRMesh>();
                leftMR   = leftSkeleton.GetComponent<OVRMeshRenderer>();
            }
            if (rightSkeleton)
            {
                rightMesh = rightSkeleton.GetComponent<OVRMesh>();
                rightMR   = rightSkeleton.GetComponent<OVRMeshRenderer>();
            }

            Debug.Log($"[PlayerHandBodies] Init: L(skel={leftSkeleton != null} mesh={leftMesh != null}) " +
                      $"R(skel={rightSkeleton != null} mesh={rightMesh != null})");

            bool leftReady = false, rightReady = false;
            float timeout = 20f, elapsed = 0f;
            float retryInterval = 0.5f, lastRetry = 0f;

            while ((!leftReady || !rightReady) && elapsed < timeout)
            {
                // Force-retry OVRMesh initialization on device (OVRMesh.Update retry is editor-only)
                if (elapsed - lastRetry >= retryInterval)
                {
                    if (!leftReady && leftMesh != null)  ForceRetryOVRMesh(leftMesh, leftMR);
                    if (!rightReady && rightMesh != null) ForceRetryOVRMesh(rightMesh, rightMR);
                    lastRetry = elapsed;
                }

                leftReady  = IsHandReady(leftSkeleton,  leftMesh,  leftMR);
                rightReady = IsHandReady(rightSkeleton, rightMesh, rightMR);

                if (elapsed > 0f && Mathf.FloorToInt(elapsed) != Mathf.FloorToInt(elapsed - Time.deltaTime))
                    Debug.Log($"[PlayerHandBodies] Waiting... L={leftReady} R={rightReady} ({elapsed:F0}s)");

                elapsed += Time.deltaTime;
                yield return null;
            }

            _initCoroutine = null;

            if (!leftReady && !rightReady)
            {
                Debug.LogWarning("[PlayerHandBodies] Timed out waiting for hand mesh data. " +
                                 "Will retry when hand tracking becomes active.");
                SceneMeshManager.NotifyNoHandBodies();
                yield break;
            }

            if (!leftReady)
                Debug.LogWarning("[PlayerHandBodies] Left hand timed out — only right hand created.");
            if (!rightReady)
                Debug.LogWarning("[PlayerHandBodies] Right hand timed out — only left hand created.");

            CreateHandIfReady(leftReady,  leftMesh,  leftSkeleton,  "PlayerLeftHand",  false, ref _left,  ref _leftRoot,  ref _leftGrab,  ref _leftLastPos,  ref _leftLastRot);
            CreateHandIfReady(rightReady, rightMesh, rightSkeleton, "PlayerRightHand", true,  ref _right, ref _rightRoot, ref _rightGrab, ref _rightLastPos, ref _rightLastRot);

            _handsCreated = _left.initialized || _right.initialized;

            int lc = _left.initialized  ? _left.mocapBodies.Length  : 0;
            int rc = _right.initialized ? _right.mocapBodies.Length : 0;
            Debug.Log($"[PlayerHandBodies] Created mesh bodies ({detail}). L={lc} R={rc}");

            if (IsMjSceneDataReady())
            {
                if (!_subscribed) Subscribe();
                MjScene.Instance.SceneRecreationAtLateUpdateRequested = true;
            }

            // If one hand is still missing, keep retrying in background
            if (!_left.initialized || !_right.initialized)
                StartCoroutine(RetryMissingHand(leftMesh, leftMR, rightMesh, rightMR));
        }

        void CreateHandIfReady(bool ready, OVRMesh mesh, OVRSkeleton skel, string name, bool isRight,
                               ref HandRuntime hand, ref GameObject root, ref HandGrabState grab,
                               ref Vector3[] lastPos, ref Quaternion[] lastRot)
        {
            if (!ready || hand.initialized) return;
            CreateHandFromMesh(mesh, skel, name, isRight, ref hand, ref root);
            if (!hand.initialized) return;
            grab.boneTargets = new BoneGrabTarget[hand.mocapBodies.Length];
            lastPos = new Vector3[hand.mocapBodies.Length];
            lastRot = new Quaternion[hand.mocapBodies.Length];
            for (int i = 0; i < lastRot.Length; i++) lastRot[i] = Quaternion.identity;
            ClearBoneTargets(ref grab);

            // Self-apply occluder if MR occlusion mode is active
            var occluderMat = SceneMeshManager.GetHandOccluderIfActive();
            if (occluderMat != null)
            {
                _occluderActive = true;
                _occluderMat = occluderMat;
                if (isRight)
                {
                    _cachedRight = default;
                    PopulateRendererCache(skel, ref _cachedRight);
                    EnforceOccluderCached(ref _cachedRight);
                }
                else
                {
                    _cachedLeft = default;
                    PopulateRendererCache(skel, ref _cachedLeft);
                    EnforceOccluderCached(ref _cachedLeft);
                }
                DisableMjGeomRenderers(ref hand);
            }
        }

        IEnumerator RetryMissingHand(OVRMesh leftMesh, OVRMeshRenderer leftMR,
                                     OVRMesh rightMesh, OVRMeshRenderer rightMR)
        {
            float maxRetry = 30f, elapsed = 0f;
            while (elapsed < maxRetry)
            {
                yield return new WaitForSeconds(1f);
                elapsed += 1f;

                bool needLeft  = !_left.initialized  && leftSkeleton != null;
                bool needRight = !_right.initialized && rightSkeleton != null;
                if (!needLeft && !needRight) yield break;

                if (needLeft && leftMesh != null)   ForceRetryOVRMesh(leftMesh, leftMR);
                if (needRight && rightMesh != null)  ForceRetryOVRMesh(rightMesh, rightMR);

                bool lReady = needLeft  && IsHandReady(leftSkeleton,  leftMesh,  leftMR);
                bool rReady = needRight && IsHandReady(rightSkeleton, rightMesh, rightMR);

                if (lReady)
                {
                    CreateHandIfReady(true, leftMesh, leftSkeleton, "PlayerLeftHand", false,
                                      ref _left, ref _leftRoot, ref _leftGrab, ref _leftLastPos, ref _leftLastRot);
                    if (_left.initialized)
                    {
                        _handsCreated = true;
                        Debug.Log("[PlayerHandBodies] Late-init: left hand created.");
                        if (MjScene.InstanceExists)
                            MjScene.Instance.SceneRecreationAtLateUpdateRequested = true;
                    }
                }
                if (rReady)
                {
                    CreateHandIfReady(true, rightMesh, rightSkeleton, "PlayerRightHand", true,
                                      ref _right, ref _rightRoot, ref _rightGrab, ref _rightLastPos, ref _rightLastRot);
                    if (_right.initialized)
                    {
                        _handsCreated = true;
                        Debug.Log("[PlayerHandBodies] Late-init: right hand created.");
                        if (MjScene.InstanceExists)
                            MjScene.Instance.SceneRecreationAtLateUpdateRequested = true;
                    }
                }
            }

            if (!_left.initialized)
                Debug.LogWarning("[PlayerHandBodies] Left hand never initialized after extended retry.");
            if (!_right.initialized)
                Debug.LogWarning("[PlayerHandBodies] Right hand never initialized after extended retry.");
        }

        // OVRMesh.Update() retry is #if UNITY_EDITOR only. On device, force-retry via reflection.
        static MethodInfo _ovrMeshInitMethod;
        static FieldInfo  _ovrMeshTypeField;

        static void ForceRetryOVRMesh(OVRMesh mesh, OVRMeshRenderer mr)
        {
            if (mesh == null || mesh.IsInitialized) return;

            if (_ovrMeshInitMethod == null)
                _ovrMeshInitMethod = typeof(OVRMesh).GetMethod("Initialize",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            if (_ovrMeshTypeField == null)
                _ovrMeshTypeField = typeof(OVRMesh).GetField("_meshType",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            if (_ovrMeshInitMethod != null && _ovrMeshTypeField != null)
            {
                var meshType = _ovrMeshTypeField.GetValue(mesh);
                _ovrMeshInitMethod.Invoke(mesh, new[] { meshType });
            }

            // Once OVRMesh is initialized, force OVRMeshRenderer to rebind (creates bindposes)
            if (mesh.IsInitialized && mr != null && !mr.IsInitialized)
                mr.ForceRebind();
        }

        static bool IsHandReady(OVRSkeleton skel, OVRMesh mesh, OVRMeshRenderer mr)
        {
            if (skel == null) return false;
            if (!skel.IsInitialized || skel.Bones == null || skel.Bones.Count == 0) return false;
            if (!skel.IsDataValid || !skel.IsDataHighConfidence) return false;
            if (mesh == null || !mesh.IsInitialized || mesh.Mesh == null) return false;
            if (mesh.Mesh.bindposes != null && mesh.Mesh.bindposes.Length > 0) return true;
            if (mr != null && mr.IsInitialized) return true;
            return false;
        }

        // ── Mesh splitting → MjMocapBody + MjGeom(Mesh) per bone ─────────────

        void CreateHandFromMesh(OVRMesh ovrMesh, OVRSkeleton skeleton,
                                string handName, bool isRight, ref HandRuntime hand,
                                ref GameObject rootOut)
        {
            if (hand.initialized) return;

            var mesh = ovrMesh.Mesh;
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            var boneWeights = mesh.boneWeights;
            var bindPoses = mesh.bindposes;
            int numSkinBones = Mathf.Min(skeleton.Bones.Count, bindPoses.Length);

            var perBoneVerts = new List<int>[numSkinBones];
            for (int i = 0; i < numSkinBones; i++) perBoneVerts[i] = new List<int>();
            for (int v = 0; v < boneWeights.Length; v++)
            {
                int bi = boneWeights[v].boneIndex0;
                if (bi < numSkinBones && boneWeights[v].weight0 > weightThreshold)
                    perBoneVerts[bi].Add(v);
            }

            var perBoneTris = new List<int>[numSkinBones];
            for (int i = 0; i < numSkinBones; i++) perBoneTris[i] = new List<int>();
            for (int t = 0; t < triangles.Length / 3; t++)
            {
                int v0 = triangles[t * 3], v1 = triangles[t * 3 + 1], v2 = triangles[t * 3 + 2];
                int b0 = boneWeights[v0].boneIndex0, b1 = boneWeights[v1].boneIndex0, b2 = boneWeights[v2].boneIndex0;
                if (b0 != b1 || b0 != b2 || b0 >= numSkinBones) continue;
                var bv = perBoneVerts[b0];
                int i0 = bv.IndexOf(v0), i1 = bv.IndexOf(v1), i2 = bv.IndexOf(v2);
                if (i0 >= 0 && i1 >= 0 && i2 >= 0)
                { perBoneTris[b0].Add(i0); perBoneTris[b0].Add(i1); perBoneTris[b0].Add(i2); }
            }

            var activeBones = new List<int>();
            for (int b = 0; b < numSkinBones; b++)
            {
                if (perBoneVerts[b].Count < minVerticesPerBone || perBoneTris[b].Count < 3) continue;
                if (detail == HandDetail.Minimal && !_minimalBoneIndices.Contains(b)) continue;
                activeBones.Add(b);
            }

            var root = new GameObject(handName);
            root.transform.SetParent(transform, false);
            rootOut = root;

            hand.mocapBodies   = new MjMocapBody[activeBones.Count];
            hand.boneIndices   = new int[activeBones.Count];
            hand.mocapMjIndices = new int[activeBones.Count];
            hand.bindLocalPos  = new Vector3[activeBones.Count];
            hand.bindLocalRot  = new Quaternion[activeBones.Count];
            hand.vizRenderers  = new MeshRenderer[activeBones.Count];

            var vizMat = isRight ? GetOrCreateVizMat(ref _vizMatRight, new Color(1f, 0.85f, 0.1f), vizAlphaRight)
                                : GetOrCreateVizMat(ref _vizMatLeft,  new Color(0f, 0.75f, 1f),   vizAlphaLeft);

            var wristBone = skeleton.Bones[0].Transform;
            Vector3 wristPos = wristBone.position;
            Quaternion wristRotInv = Quaternion.Inverse(wristBone.rotation);

            for (int i = 0; i < activeBones.Count; i++)
            {
                int boneIdx = activeBones[i];
                hand.boneIndices[i] = boneIdx;
                hand.mocapMjIndices[i] = -1;

                var boneT = skeleton.Bones[boneIdx].Transform;
                hand.bindLocalPos[i] = wristRotInv * (boneT.position - wristPos);
                hand.bindLocalRot[i] = wristRotInv * boneT.rotation;

                var bvList = perBoneVerts[boneIdx];
                var boneLocalVerts = new Vector3[bvList.Count];
                Matrix4x4 bp = bindPoses[boneIdx];
                for (int j = 0; j < bvList.Count; j++)
                    boneLocalVerts[j] = bp.MultiplyPoint3x4(vertices[bvList[j]]);

                var boneMesh = new Mesh { vertices = boneLocalVerts, triangles = perBoneTris[boneIdx].ToArray() };
                boneMesh.RecalculateBounds();
                boneMesh.RecalculateNormals();

                string boneName = $"{handName}_Bone{boneIdx}";

                var bodyGO = new GameObject(boneName);
                bodyGO.transform.SetParent(root.transform, false);
                bodyGO.transform.SetPositionAndRotation(boneT.position, boneT.rotation);
                var mocapBody = bodyGO.AddComponent<MjMocapBody>();

                var geomGO = new GameObject($"{boneName}_Geom");
                geomGO.transform.SetParent(bodyGO.transform, false);
                geomGO.transform.localPosition = Vector3.zero;
                geomGO.transform.localRotation = Quaternion.identity;

                var geom = geomGO.AddComponent<MjGeom>();
                geom.ShapeType = MjShapeComponent.ShapeTypes.Mesh;
                geom.Mesh.Mesh = boneMesh;
                geom.Density = geomDensity;
                geom.Mass = 0;

                // ── Contact parameter tuning ──
                geom.Settings.Priority = 1;
                geom.Settings.Filtering.Contype = 4;
                geom.Settings.Filtering.Conaffinity = 1;
                geom.Settings.Solver.ConDim = 4;
                geom.Settings.Solver.Margin = contactMargin;
                geom.Settings.Solver.SolRef.TimeConst = 0.005f;
                geom.Settings.Solver.SolRef.DampRatio = 1.2f;
                geom.Settings.Solver.SolImp.DMin = 0.95f;
                geom.Settings.Solver.SolImp.DMax = 0.99f;
                geom.Settings.Solver.SolImp.Width = 0.001f;
                geom.Settings.Friction.Sliding = slidingFriction;
                geom.Settings.Friction.Torsional = 0.1f;
                geom.Settings.Friction.Rolling = 0.01f;

                // Visualization (no MjMeshFilter — avoids MuJoCo's always-on geom rendering)
                var mf = geomGO.GetComponent<MeshFilter>();
                if (mf == null) mf = geomGO.AddComponent<MeshFilter>();
                mf.sharedMesh = boneMesh;
                var mr = geomGO.GetComponent<MeshRenderer>();
                if (mr == null) mr = geomGO.AddComponent<MeshRenderer>();
                mr.sharedMaterial = vizMat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.enabled = showHandViz;

                hand.mocapBodies[i] = mocapBody;
                hand.vizRenderers[i] = mr;
            }
            hand.initialized = true;
        }

        // ── MjScene callbacks ─────────────────────────────────────────────────

        void OnMjPostInit(object sender, EventArgs e)
        {
            if (_left.initialized)  ResolveMocapIndices(ref _left);
            if (_right.initialized) ResolveMocapIndices(ref _right);

            var allBodies = FindObjectsByType<MjBody>(FindObjectsSortMode.None);
            var list = new List<SynthBodyInfo>(allBodies.Length);
            foreach (var b in allBodies)
            {
                if (b.MujocoId < 0) continue;
                list.Add(new SynthBodyInfo { body = b, mjId = b.MujocoId, prevPos = b.transform.position });
            }
            _synthBodies = list.ToArray();
            _mjReady = true;

            if (_occluderActive)
            {
                DisableMjGeomRenderers(ref _left);
                DisableMjGeomRenderers(ref _right);
            }

            int lc = CountValid(_left.mocapMjIndices), rc = CountValid(_right.mocapMjIndices);
            Debug.Log($"[PlayerHandBodies] PostInit: L={lc} R={rc} mocap, Synth={_synthBodies.Length}");

            SceneMeshManager.NotifyHandBodiesReady();
        }

        void ResolveMocapIndices(ref HandRuntime hand)
        {
            for (int i = 0; i < hand.mocapBodies.Length; i++)
            {
                var body = hand.mocapBodies[i];
                hand.mocapMjIndices[i] = (body != null && body.MujocoId >= 0)
                    ? body.MujocoId : -1;
            }
        }

        static int CountValid(int[] a)
        {
            if (a == null) return 0;
            int c = 0; foreach (var v in a) if (v >= 0) c++; return c;
        }

        // ── Update: VR input + per-bone grab locking ──────────────────────────

        void Update()
        {
            // Detect controller <-> hand tracking transitions
            bool handTrackingNow = IsHandTrackingActive();
            if (handTrackingNow && !_wasHandTracking && !_handsCreated)
            {
                ReinitializeHands();
            }
            else if (!handTrackingNow && _wasHandTracking && _handsCreated)
            {
                // Switched from hand tracking to controllers — keep existing bodies,
                // they'll use FallbackPose with controller anchors
            }
            _wasHandTracking = handTrackingNow;

            if (!_mjReady) return;

            bool leftGrip  = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger)   || OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger);
            bool rightGrip = OVRInput.Get(OVRInput.Button.SecondaryHandTrigger) || OVRInput.Get(OVRInput.Button.SecondaryIndexTrigger);

            UpdateGrabState(ref _leftGrab,  leftHandAnchor,  leftGrip,  ref _left);
            UpdateGrabState(ref _rightGrab, rightHandAnchor, rightGrip, ref _right);
        }

        void UpdateGrabState(ref HandGrabState grab, Transform anchor, bool gripDown, ref HandRuntime hand)
        {
            grab.prevPosUnity  = grab.posUnity;
            grab.posUnity      = anchor ? anchor.position : Vector3.zero;
            grab.velocityUnity = (grab.posUnity - grab.prevPosUnity) / Mathf.Max(Time.deltaTime, 1e-4f);

            bool justPressed  = gripDown && !grab.wasGrabbing;
            bool justReleased = !gripDown && grab.wasGrabbing;
            grab.grabbing = gripDown;
            grab.wasGrabbing = gripDown;

            if (justReleased)
                ClearBoneTargets(ref grab);

            if (justPressed && hand.initialized && _synthBodies != null)
                LockBoneTargets(ref grab, ref hand);
        }

        void LockBoneTargets(ref HandGrabState grab, ref HandRuntime hand)
        {
            if (grab.boneTargets == null) return;
            for (int b = 0; b < hand.mocapBodies.Length; b++)
            {
                grab.boneTargets[b].synthIdx = -1;
                var body = hand.mocapBodies[b];
                if (!body) continue;
                Vector3 bonePos = body.transform.position;

                float bestDist = boneGrabReach;
                int bestIdx = -1;
                for (int s = 0; s < _synthBodies.Length; s++)
                {
                    float d = Vector3.Distance(bonePos, _synthBodies[s].body.transform.position);
                    if (d < bestDist) { bestDist = d; bestIdx = s; }
                }

                if (bestIdx >= 0)
                {
                    var synthT = _synthBodies[bestIdx].body.transform;
                    grab.boneTargets[b].synthIdx = bestIdx;
                    grab.boneTargets[b].localOffset = synthT.InverseTransformPoint(bonePos);
                }
            }
        }

        static void ClearBoneTargets(ref HandGrabState grab)
        {
            if (grab.boneTargets == null) return;
            for (int i = 0; i < grab.boneTargets.Length; i++)
                grab.boneTargets[i].synthIdx = -1;
        }

        // ── FixedUpdate: drive mocap bodies from skeleton ─────────────────────

        unsafe void FixedUpdate()
        {
            if (!_mjReady || !MjScene.InstanceExists || MjScene.Instance.Data == null) return;

            // Cache synth body transforms once per FixedUpdate (used by ctrl callback grab forces)
            if (_synthBodies != null)
            {
                for (int i = 0; i < _synthBodies.Length; i++)
                {
                    var t = _synthBodies[i].body.transform;
                    _synthBodies[i].prevPos = _synthBodies[i].cachedPos;
                    _synthBodies[i].cachedPos = t.position;
                    _synthBodies[i].cachedRot = t.rotation;
                }
            }

            // Cache hand bone transforms once per FixedUpdate (used by ctrl callback grab forces)
            CacheBoneTransforms(ref _left, ref _leftBonePosCache, ref _leftBoneRotCache);
            CacheBoneTransforms(ref _right, ref _rightBonePosCache, ref _rightBoneRotCache);

            var data = MjScene.Instance.Data;

            if (_left.initialized)
                DriveHand(ref _left, leftSkeleton, leftHandAnchor, data->mocap_pos, data->mocap_quat,
                          ref _leftLastPos, ref _leftLastRot);
            if (_right.initialized)
                DriveHand(ref _right, rightSkeleton, rightHandAnchor, data->mocap_pos, data->mocap_quat,
                          ref _rightLastPos, ref _rightLastRot);
        }

        static void CacheBoneTransforms(ref HandRuntime hand, ref Vector3[] posCache, ref Quaternion[] rotCache)
        {
            if (!hand.initialized) return;
            int n = hand.mocapBodies.Length;
            if (posCache == null || posCache.Length != n)
            {
                posCache = new Vector3[n];
                rotCache = new Quaternion[n];
            }
            for (int i = 0; i < n; i++)
            {
                var b = hand.mocapBodies[i];
                if (b != null)
                {
                    posCache[i] = b.transform.position;
                    rotCache[i] = b.transform.rotation;
                }
            }
        }

        unsafe void DriveHand(ref HandRuntime hand, OVRSkeleton skeleton, Transform anchor,
                       double* mocapPos, double* mocapQuat,
                       ref Vector3[] lastPos, ref Quaternion[] lastRot)
        {
            bool skelReady = skeleton != null && skeleton.IsInitialized && skeleton.IsDataValid
                             && skeleton.Bones != null && skeleton.Bones.Count > 0;

            bool hasAnchor = anchor != null;
            bool canDrive = skelReady || hasAnchor;

            if (!canDrive && lastPos != null)
            {
                // Hold at last known good position
                for (int i = 0; i < hand.mocapBodies.Length; i++)
                {
                    int mocapIdx = hand.mocapMjIndices[i];
                    if (mocapIdx < 0) continue;
                    MjEngineTool.SetMjVector3(MjEngineTool.MjVector3AtEntry(mocapPos, mocapIdx), lastPos[i]);
                    MjEngineTool.SetMjQuaternion(MjEngineTool.MjQuaternionAtEntry(mocapQuat, mocapIdx), lastRot[i]);
                }
                return;
            }

            for (int i = 0; i < hand.mocapBodies.Length; i++)
            {
                int mocapIdx = hand.mocapMjIndices[i];
                if (mocapIdx < 0) continue;

                Vector3 pos; Quaternion rot;
                if (skelReady && hand.boneIndices[i] < skeleton.Bones.Count)
                {
                    var boneT = skeleton.Bones[hand.boneIndices[i]].Transform;
                    if (boneT != null) { pos = boneT.position; rot = boneT.rotation; }
                    else FallbackPose(anchor, ref hand, i, out pos, out rot);
                }
                else FallbackPose(anchor, ref hand, i, out pos, out rot);

                MjEngineTool.SetMjVector3(MjEngineTool.MjVector3AtEntry(mocapPos, mocapIdx), pos);
                MjEngineTool.SetMjQuaternion(MjEngineTool.MjQuaternionAtEntry(mocapQuat, mocapIdx), rot);

                if (hand.mocapBodies[i])
                    hand.mocapBodies[i].transform.SetPositionAndRotation(pos, rot);

                // Store as last known good
                if (lastPos != null && i < lastPos.Length)
                {
                    lastPos[i] = pos;
                    lastRot[i] = rot;
                }
            }
        }

        void FallbackPose(Transform anchor, ref HandRuntime hand, int idx,
                          out Vector3 pos, out Quaternion rot)
        {
            if (!anchor) { pos = Vector3.zero; rot = Quaternion.identity; return; }
            pos = anchor.TransformPoint(hand.bindLocalPos[idx]);
            rot = anchor.rotation * hand.bindLocalRot[idx];
        }

        // ── ctrlCallback: per-bone grab forces + torques ──────────────────────

        unsafe void OnCtrlCallback(object sender, MjStepArgs e)
        {
            if (!_mjReady || _synthBodies == null) return;
            if (_left.initialized)  WritePerBoneGrabForces(e.data, ref _leftGrab,  _leftBonePosCache, _leftBoneRotCache);
            if (_right.initialized) WritePerBoneGrabForces(e.data, ref _rightGrab, _rightBonePosCache, _rightBoneRotCache);
        }

        unsafe void WritePerBoneGrabForces(MujocoLib.mjData_* data, ref HandGrabState grab,
                                           Vector3[] bonePosCache, Quaternion[] boneRotCache)
        {
            if (!grab.grabbing || grab.boneTargets == null || bonePosCache == null) return;
            if (!MjScene.InstanceExists || MjScene.Instance.Model == null) return;

            int totalBodies = (int)MjScene.Instance.Model->nbody;
            float dt = Mathf.Max(Time.fixedDeltaTime, 1e-4f);

            for (int b = 0; b < grab.boneTargets.Length; b++)
            {
                int sIdx = grab.boneTargets[b].synthIdx;
                if (sIdx < 0 || sIdx >= _synthBodies.Length) continue;

                var bi = _synthBodies[sIdx];
                if (bi.mjId < 0 || bi.mjId >= totalBodies) continue;
                if (b >= bonePosCache.Length) continue;

                Vector3 bonePos = bonePosCache[b];
                Quaternion boneRot = boneRotCache[b];

                Vector3 bodyPos = bi.cachedPos;
                Quaternion bodyRot = bi.cachedRot;

                // Target position: where the body should be so the grab point aligns with the bone
                Vector3 grabWorldTarget = bodyPos + (bodyRot * grab.boneTargets[b].localOffset);
                Vector3 delta = grabWorldTarget - bonePos;

                // Body velocity for damping
                Vector3 bodyVel = (bodyPos - bi.prevPos) / dt;

                // PD force: pull the grabbed body so its grab point reaches the bone
                Vector3 force = -delta * grabStiffness - bodyVel * grabDamping;
                force = Vector3.ClampMagnitude(force, maxGrabForce);

                // Torque: align body orientation with bone rotation
                Quaternion rotError = boneRot * Quaternion.Inverse(bodyRot);
                rotError.ToAngleAxis(out float angleDeg, out Vector3 axis);
                if (angleDeg > 180f) angleDeg -= 360f;
                Vector3 torque = axis * (angleDeg * Mathf.Deg2Rad * grabRotStiffness);
                torque = Vector3.ClampMagnitude(torque, maxGrabForce * 0.1f);

                Vector3 mjF = MjEngineTool.MjVector3(force);
                Vector3 mjT = MjEngineTool.MjVector3(torque);
                int idx = bi.mjId * 6;
                data->xfrc_applied[idx + 0] += mjF.x;
                data->xfrc_applied[idx + 1] += mjF.y;
                data->xfrc_applied[idx + 2] += mjF.z;
                data->xfrc_applied[idx + 3] += mjT.x;
                data->xfrc_applied[idx + 4] += mjT.y;
                data->xfrc_applied[idx + 5] += mjT.z;
            }
        }

        // ── Hand Occluder (MR passthrough) ───────────────────────────────────

        bool _occluderActive;
        Material _occluderMat;

        struct CachedSkeletonRenderers
        {
            public OVRMeshRenderer ovrMR;
            public OVRSkeletonRenderer skelRenderer;
            public SkinnedMeshRenderer smr;
            public Renderer[] otherRenderers;
            public bool populated;
        }

        CachedSkeletonRenderers _cachedLeft, _cachedRight;

        void PopulateRendererCache(OVRSkeleton skeleton, ref CachedSkeletonRenderers cache)
        {
            if (skeleton == null) return;
            cache.ovrMR = skeleton.GetComponent<OVRMeshRenderer>();
            cache.skelRenderer = skeleton.GetComponent<OVRSkeletonRenderer>();
            cache.smr = skeleton.GetComponent<SkinnedMeshRenderer>();

            var all = skeleton.GetComponentsInChildren<Renderer>(true);
            var others = new System.Collections.Generic.List<Renderer>();
            foreach (var r in all)
                if (r != (Renderer)cache.smr) others.Add(r);
            cache.otherRenderers = others.ToArray();
            cache.populated = true;
        }

        /// <summary>
        /// Apply an occluder material to the OVR hand mesh renderers so virtual
        /// hands are invisible but write depth -- the real hands show through
        /// passthrough while still occluding virtual objects behind them.
        /// Pass null to restore default OVR hand rendering.
        /// </summary>
        public void ApplyHandOccluder(Material occluder)
        {
            _occluderActive = occluder != null;
            _occluderMat = occluder;

            if (!_cachedLeft.populated)  PopulateRendererCache(leftSkeleton,  ref _cachedLeft);
            if (!_cachedRight.populated) PopulateRendererCache(rightSkeleton, ref _cachedRight);

            EnforceOccluderCached(ref _cachedLeft);
            EnforceOccluderCached(ref _cachedRight);
            DisableMjGeomRenderers(ref _left);
            DisableMjGeomRenderers(ref _right);

            if (_occluderActive)
            {
                DestroySkeletonRendererChildren(leftSkeleton);
                DestroySkeletonRendererChildren(rightSkeleton);
            }
        }

        void EnforceOccluderCached(ref CachedSkeletonRenderers cache)
        {
            if (!cache.populated) return;
            bool hide = _occluderActive;

            if (cache.ovrMR != null)
                cache.ovrMR.enabled = !hide;

            if (cache.skelRenderer != null)
                cache.skelRenderer.enabled = !hide;

            if (cache.smr != null && hide)
            {
                if (cache.smr.sharedMaterial != _occluderMat)
                    cache.smr.sharedMaterial = _occluderMat;
                cache.smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (!cache.smr.enabled) cache.smr.enabled = true;
            }

            if (cache.otherRenderers != null)
            {
                for (int i = 0; i < cache.otherRenderers.Length; i++)
                {
                    var r = cache.otherRenderers[i];
                    if (r != null && hide && r.enabled)
                        r.enabled = false;
                }
            }
        }

        static void DestroySkeletonRendererChildren(OVRSkeleton skeleton)
        {
            if (skeleton == null) return;
            foreach (Transform child in skeleton.transform)
            {
                if (child.name == "SkeletonRenderer")
                    Destroy(child.gameObject);
            }
        }

        static void DisableMjGeomRenderers(ref HandRuntime hand)
        {
            if (!hand.initialized || hand.mocapBodies == null) return;
            foreach (var body in hand.mocapBodies)
            {
                if (body == null) continue;
                foreach (var r in body.GetComponentsInChildren<Renderer>(true))
                    r.enabled = false;
            }
        }

        // ── Visualization ─────────────────────────────────────────────────────

        void LateUpdate()
        {
            if (_occluderActive)
            {
                if (!_cachedLeft.populated)  PopulateRendererCache(leftSkeleton,  ref _cachedLeft);
                if (!_cachedRight.populated) PopulateRendererCache(rightSkeleton, ref _cachedRight);
                EnforceOccluderCached(ref _cachedLeft);
                EnforceOccluderCached(ref _cachedRight);
            }

            bool showViz = showHandViz;// && !_occluderActive;
            ToggleVizRenderers(ref _left,  showViz);
            ToggleVizRenderers(ref _right, showViz);

            if (showViz)
            {
                UpdateVizMat(_vizMatLeft,  new Color(0f, 0.75f, 1f),   vizAlphaLeft,  _leftGrab.grabbing);
                UpdateVizMat(_vizMatRight, new Color(1f, 0.85f, 0.1f), vizAlphaRight, _rightGrab.grabbing);
            }
        }

        static void ToggleVizRenderers(ref HandRuntime hand, bool on)
        {
            if (hand.vizRenderers == null) return;
            foreach (var mr in hand.vizRenderers)
                if (mr && mr.enabled != on) mr.enabled = on;
        }

        static void UpdateVizMat(Material mat, Color baseCol, float alpha, bool grabbing)
        {
            if (mat == null) return;
            Color col = grabbing ? new Color(1f, 0.15f, 0.05f, Mathf.Min(alpha + 0.15f, 1f))
                                 : new Color(baseCol.r, baseCol.g, baseCol.b, alpha);
            mat.SetColor("_BaseColor", col);
            mat.color = col;
        }

        static Material GetOrCreateVizMat(ref Material cached, Color baseCol, float alpha)
        {
            if (cached != null) return cached;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            cached = new Material(shader);
            cached.SetFloat("_Surface", 1);
            cached.SetFloat("_Blend", 0);
            cached.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            cached.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            cached.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            cached.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            cached.SetFloat("_ZWrite", 0);
            cached.SetFloat("_AlphaClip", 0);
            cached.SetInt("_Cull", 0);
            cached.renderQueue = 3000;
            cached.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            cached.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            cached.SetFloat("_Metallic", 0.3f);
            cached.SetFloat("_Smoothness", 0.6f);
            Color col = new Color(baseCol.r, baseCol.g, baseCol.b, alpha);
            cached.SetColor("_BaseColor", col);
            cached.color = col;
            return cached;
        }

        // ── Gizmos ────────────────────────────────────────────────────────────

        void OnDrawGizmos()
        {
            if (!showGizmos || !Application.isPlaying) return;
            DrawHandGizmos(_left,  _leftGrab,  Color.cyan);
            DrawHandGizmos(_right, _rightGrab, Color.yellow);
        }

        void DrawHandGizmos(HandRuntime hand, HandGrabState grab, Color col)
        {
            if (!hand.initialized || hand.mocapBodies == null) return;
            for (int i = 0; i < hand.mocapBodies.Length; i++)
            {
                var b = hand.mocapBodies[i];
                if (!b) continue;
                bool locked = grab.boneTargets != null && i < grab.boneTargets.Length && grab.boneTargets[i].synthIdx >= 0;
                Gizmos.color = locked ? Color.red : col;
                Gizmos.DrawWireSphere(b.transform.position, 0.01f);

                if (locked && _synthBodies != null)
                {
                    int sIdx = grab.boneTargets[i].synthIdx;
                    if (sIdx < _synthBodies.Length)
                        Gizmos.DrawLine(b.transform.position, _synthBodies[sIdx].body.transform.position);
                }
            }
        }
    }
}
