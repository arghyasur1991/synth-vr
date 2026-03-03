// Copyright 2026 Arghya Sur / Genesis  –  Apache-2.0
//
// Estimates ambient lighting from the Quest passthrough camera feed and
// applies it to the scene's directional light + ambient settings so the
// Synth's lighting matches the physical room.

using System;
using System.Collections;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Genesis.Sentience.VR
{
    public class AmbientLightEstimator : MonoBehaviour
    {
        [Header("Sampling")]
        [Tooltip("Sample the camera feed every N frames to reduce GPU readback pressure.")]
        [Range(5, 120)]
        public int sampleInterval = 15;

        [Tooltip("Requested camera resolution (lower = cheaper). Nearest supported size is used.")]
        public Vector2Int cameraResolution = new(320, 240);

        [Tooltip("Max camera FPS (lower saves power).")]
        [Range(1, 30)]
        public int cameraMaxFps = 10;

        [Header("Light Adaptation")]
        [Tooltip("How quickly the estimated light adapts to changes (lerp speed per second).")]
        [Range(0.1f, 5f)]
        public float adaptationSpeed = 1.5f;

        [Tooltip("Minimum intensity for the ambient directional light.")]
        [Range(0f, 1f)]
        public float minIntensity = 0.15f;

        [Tooltip("Maximum intensity for the ambient directional light.")]
        [Range(0.5f, 5f)]
        public float maxIntensity = 2.5f;

        [Tooltip("Saturation multiplier for the estimated color (0 = grayscale, 1 = raw).")]
        [Range(0f, 1f)]
        public float colorSaturation = 0.4f;

        [Header("References")]
        [Tooltip("The main directional light to drive. Auto-found if null.")]
        public Light targetLight;

        [Tooltip("Also update RenderSettings.ambientLight.")]
        public bool updateAmbientLight = true;

        PassthroughCameraAccess _camera;
        bool _readbackPending;
        Color _estimatedColor = Color.white;
        float _estimatedIntensity = 1f;
        Color _currentColor = Color.white;
        float _currentIntensity = 1f;
        bool _permissionRequested;
        bool _initialized;

        const string CameraPermission = "horizonos.permission.HEADSET_CAMERA";

        void Start()
        {
            if (targetLight == null)
            {
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                {
                    if (l.type == LightType.Directional)
                    {
                        targetLight = l;
                        break;
                    }
                }
            }

            if (targetLight == null)
            {
                Debug.LogWarning("[AmbientLight] No directional light found — creating one.");
                var go = new GameObject("AmbientDirectionalLight");
                targetLight = go.AddComponent<Light>();
                targetLight.type = LightType.Directional;
                targetLight.shadows = LightShadows.Soft;
                targetLight.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }

            _currentColor = targetLight.color;
            _currentIntensity = targetLight.intensity;

            StartCoroutine(InitCamera());
        }

        IEnumerator InitCamera()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(CameraPermission))
            {
                _permissionRequested = true;
                Permission.RequestUserPermission(CameraPermission);

                float timeout = Time.time + 30f;
                while (!Permission.HasUserAuthorizedPermission(CameraPermission))
                {
                    if (Time.time > timeout)
                    {
                        Debug.LogError("[AmbientLight] Camera permission not granted within 30s — disabling.");
                        enabled = false;
                        yield break;
                    }
                    yield return null;
                }
                Debug.Log("[AmbientLight] Camera permission granted.");
            }
#endif

            if (!PassthroughCameraAccess.IsSupported)
            {
                Debug.LogWarning("[AmbientLight] PassthroughCameraAccess not supported on this device — disabling.");
                enabled = false;
                yield break;
            }

            _camera = gameObject.AddComponent<PassthroughCameraAccess>();
            _camera.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
            _camera.RequestedResolution = cameraResolution;
            _camera.MaxFramerate = cameraMaxFps;

            yield return null;

            _initialized = true;
            Debug.Log($"[AmbientLight] Initialized: res={_camera.CurrentResolution}, fps={cameraMaxFps}");
        }

        void Update()
        {
            if (!_initialized || _camera == null || !_camera.IsPlaying)
                return;

            float dt = Time.deltaTime;
            _currentColor = Color.Lerp(_currentColor, _estimatedColor, adaptationSpeed * dt);
            _currentIntensity = Mathf.Lerp(_currentIntensity, _estimatedIntensity, adaptationSpeed * dt);

            if (targetLight != null)
            {
                targetLight.color = _currentColor;
                targetLight.intensity = _currentIntensity;
            }

            if (updateAmbientLight)
            {
                var ambient = _currentColor * (_currentIntensity * 0.3f);
                RenderSettings.ambientLight = ambient;
            }

            if (_readbackPending) return;
            if (Time.frameCount % sampleInterval != 0) return;

            var tex = _camera.GetTexture();
            if (tex == null) return;

            _readbackPending = true;
            AsyncGPUReadback.Request(tex, 0, TextureFormat.RGBA32, OnReadbackComplete);
        }

        void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            _readbackPending = false;

            if (request.hasError || !request.done)
                return;

            var data = request.GetData<Color32>();
            if (data.Length == 0) return;

            ComputeAmbient(data);
        }

        void ComputeAmbient(NativeArray<Color32> pixels)
        {
            int count = pixels.Length;
            int step = Mathf.Max(1, count / 512);

            float rSum = 0, gSum = 0, bSum = 0;
            int samples = 0;

            for (int i = 0; i < count; i += step)
            {
                var c = pixels[i];
                rSum += c.r;
                gSum += c.g;
                bSum += c.b;
                samples++;
            }

            if (samples == 0) return;

            float inv = 1f / (samples * 255f);
            float r = rSum * inv;
            float g = gSum * inv;
            float b = bSum * inv;

            float luminance = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            _estimatedIntensity = Mathf.Clamp(luminance * maxIntensity, minIntensity, maxIntensity);

            float gray = luminance;
            float sr = Mathf.Lerp(gray, r, colorSaturation);
            float sg = Mathf.Lerp(gray, g, colorSaturation);
            float sb = Mathf.Lerp(gray, b, colorSaturation);

            float maxC = Mathf.Max(sr, Mathf.Max(sg, sb));
            if (maxC > 0.001f)
            {
                sr /= maxC;
                sg /= maxC;
                sb /= maxC;
            }

            _estimatedColor = new Color(sr, sg, sb, 1f);
        }

        void OnDisable()
        {
            if (_camera != null)
            {
                Destroy(_camera);
                _camera = null;
            }
            _initialized = false;
        }
    }
}
