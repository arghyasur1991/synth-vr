using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Mujoco;

namespace Genesis.Sentience.VR.Editor
{
    public class VRSceneSetupWizard : EditorWindow
    {
        // ── Refresh ─────────────────────────────────────────────────────────────

        double _lastRefresh;
        const double REFRESH_SEC = 0.8;
        Vector2 _scroll;

        // ── Cached scene state ──────────────────────────────────────────────────

        OVRCameraRig _cameraRig;
        OVRSkeleton _leftSkel, _rightSkel;
        OVRPassthroughLayer _passthrough;
        GameObject _interactionComprehensive;

        Camera[] _strayMainCameras;
        GameObject[] _activeGroundPlanes;
        GameObject _haptics;
        List<GameObject> _activeControllerVisuals = new();

        MjScene _mjScene;
        MjGlobalSettings _mjGlobal;
        QuestPerformanceManager _perfMgr;

        SceneMeshManager _sceneMesh;
        PlayerHandBodies _handBodies;

        Light _dirLight;

        UniversalRenderPipelineAsset _urpPipeline;
        ScriptableRendererData _urpRenderer;

        Object _oculusConfig;
        int _cfgHandTracking, _cfgPassthrough, _cfgScene, _cfgAnchor;

        // ── Style cache ─────────────────────────────────────────────────────────

        static readonly Color COL_OK      = new(0.25f, 0.82f, 0.35f);
        static readonly Color COL_WARN    = new(0.95f, 0.78f, 0.15f);
        static readonly Color COL_MISS    = new(0.92f, 0.28f, 0.25f);
        static readonly Color COL_INFO    = new(0.45f, 0.72f, 0.95f);
        static readonly Color COL_SECTION = new(0.18f, 0.18f, 0.22f);

        // ── Open ────────────────────────────────────────────────────────────────

        [MenuItem("Synth/Setup/VR Scene Wizard")]
        static void Open()
        {
            var w = GetWindow<VRSceneSetupWizard>("VR Scene Setup");
            w.minSize = new Vector2(440, 600);
        }

        void OnEnable()  => Refresh();
        void OnFocus()   => Refresh();

        void Update()
        {
            if (EditorApplication.timeSinceStartup - _lastRefresh > REFRESH_SEC)
            {
                Refresh();
                Repaint();
            }
        }

        // =====================================================================
        //  REFRESH
        // =====================================================================

        void Refresh()
        {
            _lastRefresh = EditorApplication.timeSinceStartup;

            // Building Blocks
            _cameraRig = FindAny<OVRCameraRig>();
            var skeletons = Object.FindObjectsByType<OVRSkeleton>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            _leftSkel  = SkeletonByHand(skeletons, true);
            _rightSkel = SkeletonByHand(skeletons, false);
            _passthrough = FindAny<OVRPassthroughLayer>();

            _interactionComprehensive = null;
            foreach (var root in SceneRoots())
            {
                var hit = DeepFind(root.transform,
                    t => t.name.Contains("OVRInteractionComprehensive"));
                if (hit != null) { _interactionComprehensive = hit.gameObject; break; }
            }

            // Conflicting objects
            _strayMainCameras = Object.FindObjectsByType<Camera>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(c => c.CompareTag("MainCamera") && IsStray(c.transform))
                .Where(c => c.gameObject.activeSelf)
                .ToArray();

            _activeGroundPlanes = SceneRoots()
                .Where(g => g.activeSelf &&
                    (NameMatch(g, "Ground") || NameMatch(g, "Plane")))
                .ToArray();

            _haptics = FindActive("Haptics");

            _activeControllerVisuals.Clear();
            if (_interactionComprehensive != null)
            {
                foreach (Transform child in _interactionComprehensive.transform)
                    if (child.gameObject.activeSelf &&
                        child.name.ToLowerInvariant().Contains("controller"))
                        _activeControllerVisuals.Add(child.gameObject);
            }

            // MuJoCo
            _mjScene  = FindAny<MjScene>();
            _mjGlobal = FindAny<MjGlobalSettings>();
            _perfMgr  = FindAny<QuestPerformanceManager>();

            // Synth-VR
            _sceneMesh  = FindAny<SceneMeshManager>();
            _handBodies = FindAny<PlayerHandBodies>();

            // Lighting
            _dirLight = Object.FindObjectsByType<Light>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(l => l.type == LightType.Directional);

            // URP
            _urpPipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(
                "Assets/Settings/URP-Pipeline.asset");
            _urpRenderer = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(
                "Assets/Settings/URP-Renderer.asset");

            // Oculus config
            RefreshOculusConfig();
        }

        void RefreshOculusConfig()
        {
            _cfgHandTracking = _cfgPassthrough = _cfgScene = _cfgAnchor = -1;
            _oculusConfig = AssetDatabase.LoadAssetAtPath<Object>(
                "Assets/Oculus/OculusProjectConfig.asset");
            if (_oculusConfig == null) return;

            var so = new SerializedObject(_oculusConfig);
            _cfgHandTracking = ReadInt(so, "handTrackingSupport");
            _cfgPassthrough  = ReadInt(so, "insightPassthroughEnabled");
            _cfgScene        = ReadInt(so, "sceneSupport");
            _cfgAnchor       = ReadInt(so, "anchorSupport");
        }

        // =====================================================================
        //  GUI
        // =====================================================================

        void OnGUI()
        {
            DrawHeader();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            GUILayout.Space(4);

            DrawBuildingBlocks();
            DrawConflicts();
            DrawMujoco();
            DrawSynthVR();
            DrawLighting();
            DrawURP();
            DrawPermissions();

            GUILayout.Space(12);
            DrawMasterButton();
            GUILayout.Space(8);

            EditorGUILayout.EndScrollView();
        }

        // ── Header ──────────────────────────────────────────────────────────────

        void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("VR Scene Setup Wizard", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
                Refresh();
            EditorGUILayout.EndHorizontal();
        }

        // ── Section 1: Building Blocks ──────────────────────────────────────────

        void DrawBuildingBlocks()
        {
            BeginSection("META BUILDING BLOCKS");

            StatusRow("Camera Rig (OVRCameraRig)", _cameraRig != null);
            StatusRow("Hand Tracking Left",        _leftSkel  != null);
            StatusRow("Hand Tracking Right",       _rightSkel != null);
            StatusRow("Passthrough",               _passthrough != null);
            StatusRow("OVRInteractionComprehensive",
                _interactionComprehensive != null, optional: true);

            bool anyMissing = _cameraRig == null || _leftSkel == null ||
                              _rightSkel == null || _passthrough == null;
            if (anyMissing)
            {
                EditorGUILayout.HelpBox(
                    "Add missing blocks via  Meta > Tools > Building Blocks  panel.\n" +
                    "Required: Camera Rig, Hand Tracking (x2), Passthrough.",
                    MessageType.Info);
            }

            EndSection();
        }

        // ── Section 2: Conflicting Objects ──────────────────────────────────────

        void DrawConflicts()
        {
            BeginSection("CONFLICTING OBJECTS");

            bool camerasOk = _strayMainCameras.Length == 0;
            bool groundOk  = _activeGroundPlanes.Length == 0;
            bool hapticsOk = _haptics == null;
            bool ctrlOk    = _activeControllerVisuals.Count == 0;

            StatusRow("Stray MainCamera disabled", camerasOk);
            StatusRow("Ground/Plane disabled",     groundOk);
            StatusRow("Haptics disabled",          hapticsOk);
            StatusRow("Controller visuals disabled", ctrlOk);

            bool needsFix = !camerasOk || !groundOk || !hapticsOk || !ctrlOk;
            if (needsFix)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Fix Conflicts", GUILayout.Width(130)))
                    FixConflicts();
                EditorGUILayout.EndHorizontal();
            }

            EndSection();
        }

        void FixConflicts()
        {
            foreach (var cam in _strayMainCameras)
            {
                Undo.RecordObject(cam.gameObject, "Disable stray MainCamera");
                cam.gameObject.SetActive(false);
            }
            foreach (var gp in _activeGroundPlanes)
            {
                Undo.RecordObject(gp, "Disable Ground/Plane");
                gp.SetActive(false);
            }
            if (_haptics != null)
            {
                Undo.RecordObject(_haptics, "Disable Haptics");
                _haptics.SetActive(false);
            }
            foreach (var cv in _activeControllerVisuals)
            {
                Undo.RecordObject(cv, "Disable controller visual");
                cv.SetActive(false);
            }
            MarkDirty();
            Refresh();
        }

        // ── Section 3: MuJoCo ──────────────────────────────────────────────────

        void DrawMujoco()
        {
            BeginSection("MUJOCO SCENE");

            StatusRow("MjScene",                _mjScene  != null);
            StatusRow("Global Settings",        _mjGlobal != null);
            StatusRow("QuestPerformanceManager", _perfMgr != null);

            bool needsFix = _mjScene == null || _mjGlobal == null || _perfMgr == null;
            if (needsFix)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Add MuJoCo", GUILayout.Width(130)))
                    FixMujoco();
                EditorGUILayout.EndHorizontal();
            }

            EndSection();
        }

        void FixMujoco()
        {
            if (_mjScene == null)
            {
                var go = new GameObject("MjScene");
                Undo.RegisterCreatedObjectUndo(go, "Create MjScene");
                Undo.AddComponent<MjScene>(go);
            }

            GameObject globalGO = _mjGlobal != null
                ? _mjGlobal.gameObject
                : FindByName("Global Settings");

            if (globalGO == null)
            {
                globalGO = new GameObject("Global Settings");
                Undo.RegisterCreatedObjectUndo(globalGO, "Create Global Settings");
            }

            if (globalGO.GetComponent<MjGlobalSettings>() == null)
                Undo.AddComponent<MjGlobalSettings>(globalGO);

            if (globalGO.GetComponent<QuestPerformanceManager>() == null)
                Undo.AddComponent<QuestPerformanceManager>(globalGO);

            MarkDirty();
            Refresh();
        }

        // ── Section 4: Synth-VR Components ──────────────────────────────────────

        void DrawSynthVR()
        {
            BeginSection("SYNTH-VR COMPONENTS");

            StatusRow("SceneMeshManager",  _sceneMesh  != null);
            StatusRow("PlayerHandBodies",  _handBodies != null);

            bool needsFix = _sceneMesh == null || _handBodies == null;
            if (needsFix)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Add All Missing", GUILayout.Width(130)))
                    FixSynthVR();
                EditorGUILayout.EndHorizontal();
            }

            EndSection();
        }

        void FixSynthVR()
        {
            if (_sceneMesh == null)
            {
                var go = new GameObject("SceneMesh");
                Undo.RegisterCreatedObjectUndo(go, "Create SceneMesh");
                var sm = Undo.AddComponent<SceneMeshManager>(go);
                sm.enablePassthrough = true;
                sm.createMujocoColliders = true;
            }

            if (_handBodies == null && _cameraRig != null)
            {
                var hb = Undo.AddComponent<PlayerHandBodies>(_cameraRig.gameObject);
                if (_leftSkel != null)  hb.leftSkeleton  = _leftSkel;
                if (_rightSkel != null) hb.rightSkeleton  = _rightSkel;
                if (_cameraRig.leftHandAnchor != null)
                    hb.leftHandAnchor = _cameraRig.leftHandAnchor;
                if (_cameraRig.rightHandAnchor != null)
                    hb.rightHandAnchor = _cameraRig.rightHandAnchor;
                EditorUtility.SetDirty(hb);
            }
            else if (_handBodies == null)
            {
                Debug.LogWarning("[VRSetup] Cannot add PlayerHandBodies — " +
                    "OVRCameraRig not found. Add Camera Rig Building Block first.");
            }

            MarkDirty();
            Refresh();
        }

        // ── Section 5: Lighting ─────────────────────────────────────────────────

        void DrawLighting()
        {
            BeginSection("LIGHTING");
            StatusRow("Directional Light", _dirLight != null);

            if (_dirLight == null)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Add Light", GUILayout.Width(130)))
                    FixLighting();
                EditorGUILayout.EndHorizontal();
            }

            EndSection();
        }

        void FixLighting()
        {
            var go = new GameObject("Directional Light");
            Undo.RegisterCreatedObjectUndo(go, "Create Directional Light");
            var light = Undo.AddComponent<Light>(go);
            light.type = LightType.Directional;
            light.intensity = 2f;
            light.colorTemperature = 7900f;
            light.useColorTemperature = true;
            light.shadows = LightShadows.Soft;
            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            MarkDirty();
            Refresh();
        }

        // ── Section 6: URP Settings ─────────────────────────────────────────────

        void DrawURP()
        {
            BeginSection("URP RENDER SETTINGS");

            StatusRow("URP-Pipeline.asset", _urpPipeline != null);
            StatusRow("URP-Renderer.asset", _urpRenderer != null);

            var activeRP = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (activeRP != null && activeRP == _urpPipeline)
                StatusLabel("Active in Graphics Settings", COL_OK, "\u2713");
            else if (activeRP != null)
                StatusLabel("Different pipeline active", COL_WARN, "\u26A0");
            else
                StatusLabel("No URP pipeline active", COL_MISS, "\u2717");

            GUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Apply Quest Settings", GUILayout.Width(160)))
                ApplyURPSettings();
            EditorGUILayout.EndHorizontal();

            EndSection();
        }

        void ApplyURPSettings()
        {
            const string pipelinePath = "Assets/Settings/URP-Pipeline.asset";
            const string rendererPath = "Assets/Settings/URP-Renderer.asset";

            bool pipelineExists = _urpPipeline != null;
            bool rendererExists = _urpRenderer != null;

            if (pipelineExists || rendererExists)
            {
                if (!EditorUtility.DisplayDialog("URP Settings",
                    "URP settings assets already exist at Assets/Settings/.\n\n" +
                    "Overwrite with recommended Quest settings?",
                    "Overwrite", "Cancel"))
                    return;
            }

            if (!AssetDatabase.IsValidFolder("Assets/Settings"))
                AssetDatabase.CreateFolder("Assets", "Settings");

            // Renderer
            UniversalRendererData renderer;
            if (rendererExists)
            {
                renderer = _urpRenderer as UniversalRendererData;
            }
            else
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, rendererPath);
            }

            // Pipeline
            UniversalRenderPipelineAsset pipeline;
            if (pipelineExists)
            {
                pipeline = _urpPipeline;
            }
            else
            {
                pipeline = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
                AssetDatabase.CreateAsset(pipeline, pipelinePath);
            }

            // Wire renderer into pipeline
            var pso = new SerializedObject(pipeline);
            var list = pso.FindProperty("m_RendererDataList");
            if (list != null)
            {
                list.arraySize = 1;
                list.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
            }

            // Quest-optimized values
            SetProp(pso, "m_RenderScale", 1.0f);
            SetProp(pso, "m_MSAA", 4);
            SetProp(pso, "m_SupportsHDR", false);
            SetProp(pso, "m_RequireDepthTexture", false);
            SetProp(pso, "m_RequireOpaqueTexture", false);
            SetProp(pso, "m_ShadowDistance", 50f);
            SetProp(pso, "m_ShadowCascadeCount", 4);
            SetProp(pso, "m_MainLightShadowmapResolution", 4096);
            SetProp(pso, "m_UseSRPBatcher", true);
            SetProp(pso, "m_SupportsDynamicBatching", false);
            SetProp(pso, "m_MainLightRenderingMode", 1);
            SetProp(pso, "m_MainLightShadowsSupported", true);
            SetProp(pso, "m_SoftShadowsSupported", true);
            pso.ApplyModifiedProperties();

            // Assign as active pipeline
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;

            AssetDatabase.SaveAssets();
            Debug.Log("[VRSetup] Quest-optimized URP settings applied.");
            Refresh();
        }

        // ── Section 7: Permissions ──────────────────────────────────────────────

        void DrawPermissions()
        {
            BeginSection("PERMISSIONS (Project Settings)");

            if (_oculusConfig == null)
            {
                EditorGUILayout.HelpBox(
                    "OculusProjectConfig.asset not found at Assets/Oculus/.\n" +
                    "Import the Meta XR SDK and run the project setup first.",
                    MessageType.Warning);
                EndSection();
                return;
            }

            StatusRow("Hand Tracking (handTrackingSupport = 1)", _cfgHandTracking >= 1);
            StatusRow("Passthrough (insightPassthroughEnabled = 1)", _cfgPassthrough >= 1);
            StatusRow("Scene Support (sceneSupport = 1)",         _cfgScene >= 1);
            StatusRow("Anchor Support (anchorSupport = 1)",       _cfgAnchor >= 1);

            bool needsFix = _cfgHandTracking < 1 || _cfgPassthrough < 1 ||
                            _cfgScene < 1 || _cfgAnchor < 1;
            if (needsFix)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Fix Permissions", GUILayout.Width(130)))
                    FixPermissions();
                EditorGUILayout.EndHorizontal();
            }

            GUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "OpenXR features (Hand Tracking Subsystem, Meta Quest Meshing/Planes, " +
                "Meta Quest Anchors) must be enabled manually in:\n" +
                "Project Settings > XR Plug-in Management > OpenXR",
                MessageType.Info);

            EndSection();
        }

        void FixPermissions()
        {
            if (_oculusConfig == null) return;
            var so = new SerializedObject(_oculusConfig);
            WriteInt(so, "handTrackingSupport", 1);
            WriteInt(so, "insightPassthroughEnabled", 1);
            WriteInt(so, "sceneSupport", 1);
            WriteInt(so, "anchorSupport", 1);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(_oculusConfig);
            AssetDatabase.SaveAssets();
            Debug.Log("[VRSetup] OculusProjectConfig permissions updated.");
            Refresh();
        }

        // ── Master Button ───────────────────────────────────────────────────────

        void DrawMasterButton()
        {
            var style = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 14,
                fixedHeight = 36
            };

            if (GUILayout.Button("\u2261  Setup Everything", style))
                SetupEverything();
        }

        void SetupEverything()
        {
            if (!EditorUtility.DisplayDialog("Setup Everything",
                "This will:\n" +
                "\u2022 Disable stray MainCameras, Ground, Haptics, controller visuals\n" +
                "\u2022 Create MjScene + Global Settings + QuestPerformanceManager\n" +
                "\u2022 Add SceneMeshManager + PlayerHandBodies\n" +
                "\u2022 Add Directional Light (if missing)\n" +
                "\u2022 Apply Quest URP settings (with overwrite prompt)\n" +
                "\u2022 Fix OculusProjectConfig permissions\n\n" +
                "Continue?", "Setup", "Cancel"))
                return;

            FixConflicts();
            FixMujoco();
            FixSynthVR();
            if (_dirLight == null) FixLighting();
            ApplyURPSettings();
            if (_oculusConfig != null) FixPermissions();

            Debug.Log("[VRSetup] All setup steps complete.");
        }

        // =====================================================================
        //  GUI HELPERS
        // =====================================================================

        void BeginSection(string title)
        {
            GUILayout.Space(6);
            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.Height(22));
            EditorGUI.DrawRect(rect, COL_SECTION);
            var labelRect = new Rect(rect.x + 8, rect.y + 2, rect.width - 16, rect.height);
            var prev = GUI.color;
            GUI.color = Color.white;
            GUI.Label(labelRect, title, EditorStyles.boldLabel);
            GUI.color = prev;
        }

        static void EndSection() => GUILayout.Space(2);

        void StatusRow(string label, bool ok, bool optional = false)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12);

            string icon;
            Color col;
            string detail;

            if (ok)
            {
                icon = "\u2713"; col = COL_OK; detail = "OK";
            }
            else if (optional)
            {
                icon = "\u2014"; col = COL_INFO; detail = "Optional";
            }
            else
            {
                icon = "\u2717"; col = COL_MISS; detail = "Missing";
            }

            StatusLabel(null, col, icon, inline: true);
            GUILayout.Label(label, GUILayout.ExpandWidth(true));
            var prev = GUI.color;
            GUI.color = col;
            GUILayout.Label(detail, EditorStyles.miniLabel, GUILayout.Width(60));
            GUI.color = prev;

            EditorGUILayout.EndHorizontal();
        }

        void StatusLabel(string text, Color col, string icon, bool inline = false)
        {
            var prev = GUI.color;
            GUI.color = col;
            if (inline)
            {
                GUILayout.Label(icon, EditorStyles.boldLabel, GUILayout.Width(18));
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(12);
                GUILayout.Label(icon, EditorStyles.boldLabel, GUILayout.Width(18));
                GUILayout.Label(text, EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }
            GUI.color = prev;
        }

        // =====================================================================
        //  UTILITY
        // =====================================================================

        static T FindAny<T>() where T : Object =>
            Object.FindObjectsByType<T>(FindObjectsInactive.Include,
                FindObjectsSortMode.None).FirstOrDefault();

        static OVRSkeleton SkeletonByHand(OVRSkeleton[] all, bool left)
        {
            string token = left ? "left" : "right";
            return all.FirstOrDefault(s =>
                s.gameObject.name.ToLowerInvariant().Contains(token)) ??
                all.FirstOrDefault(s =>
                    s.transform.parent != null &&
                    s.transform.parent.name.ToLowerInvariant().Contains(token));
        }

        bool IsStray(Transform t) =>
            _cameraRig == null || !t.IsChildOf(_cameraRig.transform);

        static bool NameMatch(GameObject go, string name) =>
            go.name.Equals(name, System.StringComparison.OrdinalIgnoreCase);

        static GameObject[] SceneRoots() =>
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();

        static Transform DeepFind(Transform root, System.Func<Transform, bool> pred)
        {
            if (pred(root)) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var hit = DeepFind(root.GetChild(i), pred);
                if (hit != null) return hit;
            }
            return null;
        }

        GameObject FindActive(string nameFragment)
        {
            foreach (var root in SceneRoots())
            {
                var t = DeepFind(root.transform, tr =>
                    tr.gameObject.activeSelf &&
                    tr.name.Contains(nameFragment));
                if (t != null) return t.gameObject;
            }
            return null;
        }

        static GameObject FindByName(string exact)
        {
            foreach (var root in SceneRoots())
            {
                var t = DeepFind(root.transform,
                    tr => tr.name.Equals(exact, System.StringComparison.Ordinal));
                if (t != null) return t.gameObject;
            }
            return null;
        }

        static void MarkDirty() =>
            EditorSceneManager.MarkSceneDirty(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        static int ReadInt(SerializedObject so, string prop)
        {
            var p = so.FindProperty(prop);
            return p != null ? p.intValue : -1;
        }

        static void WriteInt(SerializedObject so, string prop, int val)
        {
            var p = so.FindProperty(prop);
            if (p != null) p.intValue = val;
        }

        static void SetProp(SerializedObject so, string name, float v)
        { var p = so.FindProperty(name); if (p != null) p.floatValue = v; }
        static void SetProp(SerializedObject so, string name, int v)
        { var p = so.FindProperty(name); if (p != null) p.intValue = v; }
        static void SetProp(SerializedObject so, string name, bool v)
        { var p = so.FindProperty(name); if (p != null) p.boolValue = v; }
    }
}
