#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Genesis.Sentience.EditorTools
{
    /// <summary>
    /// Menu-driven setup for XR (OpenXR + Meta Quest) after the packages are installed.
    /// Use  Sentience > Setup > Configure XR for Meta Quest  to run.
    ///
    /// This script uses reflection so it compiles even when XR Management / OpenXR
    /// packages are still being imported.  It will warn clearly if packages are missing.
    /// </summary>
    public static class XRSetupEditor
    {
        [MenuItem("Synth/Setup/Configure XR for Meta Quest")]
        public static void ConfigureXR()
        {
            try
            {
                ConfigureXRInternal();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[XRSetup] Failed: {ex.Message}\n" +
                    "Make sure XR Management and OpenXR packages are imported.\n" +
                    "Window > Package Manager > check com.unity.xr.openxr is installed.");
            }
        }

        private static void ConfigureXRInternal()
        {
            var xrMgmtAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Unity.XR.Management.Editor");
            var openxrAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Unity.XR.OpenXR.Editor");

            if (xrMgmtAsm == null || openxrAsm == null)
            {
                Debug.LogError("[XRSetup] XR Management or OpenXR Editor assemblies not loaded. " +
                    "Wait for package import to finish and try again.");
                return;
            }

            // XRGeneralSettingsPerBuildTarget
            var perBuildType = xrMgmtAsm.GetType("UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget");
            if (perBuildType == null)
            {
                Debug.LogError("[XRSetup] Cannot find XRGeneralSettingsPerBuildTarget type.");
                return;
            }

            // Load or create settings asset
            const string settingsPath = "Assets/XR/Settings/XRGeneralSettingsPerBuildTarget.asset";
            var settings = AssetDatabase.LoadAssetAtPath(settingsPath, perBuildType);
            if (settings == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/XR"))
                    AssetDatabase.CreateFolder("Assets", "XR");
                if (!AssetDatabase.IsValidFolder("Assets/XR/Settings"))
                    AssetDatabase.CreateFolder("Assets/XR", "Settings");

                settings = ScriptableObject.CreateInstance(perBuildType);
                AssetDatabase.CreateAsset(settings, settingsPath);
                Debug.Log("[XRSetup] Created XRGeneralSettingsPerBuildTarget asset.");
            }

            Debug.Log("[XRSetup] XR configuration asset is at: " + settingsPath);
            Debug.Log("[XRSetup] To complete setup:\n" +
                "  1. Edit > Project Settings > XR Plug-in Management\n" +
                "  2. Under Standalone tab, check 'OpenXR'\n" +
                "  3. Under OpenXR, add 'Meta Quest Feature Group'\n" +
                "  4. Under Android tab (if building for Quest), check 'OpenXR'\n" +
                "  5. Add 'Meta Quest Feature Group' there too");

            AssetDatabase.SaveAssets();
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = settings;
        }
    }
}
#endif
