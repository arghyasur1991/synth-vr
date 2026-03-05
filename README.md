# Synth VR

Mixed Reality interaction with Synth humanoids on Meta Quest. Physics-based hand tracking, room-scale environment setup, and passthrough rendering.

## Features

- **Physics Hand Tracking** — MuJoCo-bound hand bodies driven by OVR hand tracking. Push, grab, pull, and interact with the Synth using your real hands.
- **Room Integration** — MRUK-powered room setup with physics colliders for walls, floor, ceiling, and furniture anchors. The Synth interacts with your physical room layout.
- **Smooth Locomotion** — Controller-based walk and rotate with configurable speed.
- **Passthrough Rendering** — Occluder materials for room surfaces, PTRL (Passthrough Receive Light) shader for realistic lighting on the Synth.
- **Ambient Light Estimation** — Passthrough camera-based lighting estimation for harmonized rendering in mixed reality.

## Ecosystem

synth-vr is part of a three-package architecture for creating, training, and interacting with physics-simulated humanoids:

| Package | Role | |
|---------|------|-|
| [**synth-core**](https://github.com/arghyasur1991/synth-core) | Humanoid creation, MuJoCo physics, skill architecture | Required |
| [**synth-training**](https://github.com/arghyasur1991/synth-training) | On-device reinforcement learning via TorchSharp SAC | Optional |
| **synth-vr** *(this repo)* | Mixed reality interaction on Meta Quest | — |

synth-core provides the physics body and motor system. synth-vr adds Meta Quest hand tracking, MRUK room integration, and passthrough rendering so the Synth lives in your physical space. Optionally add **synth-training** to enable on-device reinforcement learning — the Synth trains live on Quest while you interact with it.

## Requirements

- Unity 6000.x or later
- [synth-core](https://github.com/arghyasur1991/synth-core) package
- MuJoCo Unity plugin (`org.mujoco`) — via [arghyasur1991/mujoco](https://github.com/arghyasur1991/mujoco) fork (`synth-patches` branch)
- Meta XR SDK packages (v85+):
  - `com.meta.xr.sdk.interaction.ovr`
  - `com.meta.xr.mrutilitykit`

### Optional

- [synth-training](https://github.com/arghyasur1991/synth-training) — Add on-device reinforcement learning so the Synth trains directly on Quest while you interact with it in your room. Without this package, synth-vr provides physics interaction only (no learning).

## Installation

Add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.genesis.synth.vr": "https://github.com/arghyasur1991/synth-vr.git",
    "com.genesis.synth": "https://github.com/arghyasur1991/synth-core.git",
    "org.mujoco": "https://github.com/arghyasur1991/mujoco.git?path=unity#synth-patches"
  }
}
```

Meta XR SDK packages are installed separately via the Unity Package Manager or Meta's setup tools.

## Scene Setup

Use the built-in wizard to configure a VR scene: **Synth > Setup > VR Scene Wizard**

### Step 1 — Add Meta Building Blocks

Open **Meta > Tools > Building Blocks** and add these to your scene:

| Block | Purpose |
|-------|---------|
| **Camera Rig** | OVRManager + OVRCameraRig with tracking space and hand/eye anchors |
| **Hand Tracking** (x2) | OVRHand + OVRSkeleton on left and right hand anchors |
| **Passthrough** | OVRPassthroughLayer for mixed reality passthrough |
| **OVRInteractionComprehensive** | Ray/grab/poke interactions *(optional)* |

### Step 2 — Run the VR Scene Wizard

The wizard validates Building Blocks, then configures everything else automatically:

- **Conflicting objects** — disables stray MainCamera, Ground/Plane, Haptics, and controller visuals
- **MuJoCo scene** — creates `MjScene`, `Global Settings` (with `MjGlobalSettings`), and `QuestPerformanceManager`
- **Synth-VR components** — adds `SceneMeshManager` (room integration) and `PlayerHandBodies` (physics hands, auto-wired to OVR skeletons)
- **Lighting** — creates a directional light if missing
- **URP settings** — applies Quest-optimized render pipeline settings (Render Scale 1.0, MSAA 4x, HDR off, Shadow Distance 50, SRP Batcher on)
- **Permissions** — fixes OculusProjectConfig for hand tracking, passthrough, scene support, and anchor support

Click **Setup Everything** to run all steps at once, or use per-section **Fix** buttons for granular control. All changes support Undo.

### Step 3 — Add Your Synth

Add a single Synth to the scene via synth-core. **Note:** only one active Synth per scene is supported.

### Required Permissions

| Feature | OculusProjectConfig | OpenXR Feature | Android Permission |
|---------|--------------------|-----------------|--------------------|
| Hand Tracking | `handTrackingSupport: 1` | Hand Tracking Subsystem | Auto-injected |
| Passthrough | `insightPassthroughEnabled: 1` | Meta Quest: Camera | `android.permission.CAMERA` (auto) |
| Room Mesh (MRUK) | `sceneSupport: 1` | Meta Quest: Meshing / Planes | `com.oculus.permission.USE_SCENE` (auto) |
| Spatial Anchors | `anchorSupport: 1` | Meta Quest: Anchors | `com.oculus.permission.USE_SCENE` (auto) |

The wizard auto-fixes OculusProjectConfig settings. OpenXR features must be enabled manually in **Project Settings > XR Plug-in Management > OpenXR**.

## Package Structure

```
synth-vr/
├── Runtime/
│   ├── Hands/         PlayerHandBodies (MuJoCo hand tracking)
│   ├── Locomotion/    PlayerLocomotion (smooth walk/rotate)
│   ├── SceneSetup/    SceneMeshManager (MRUK room integration)
│   ├── Performance/   QuestPerformanceManager (CPU/GPU levels)
│   ├── Lighting/      AmbientLightEstimator
│   ├── Shaders/       PTRLWithDepth
│   └── Resources/     InvisibleOccluder, PTRLHighlightsAndShadows materials
└── Editor/
    ├── VRSceneSetupWizard.cs   (interactive scene setup)
    └── XRSetupEditor.cs        (XR plugin configuration)
```

## Components

| Component | Purpose | Where |
|-----------|---------|-------|
| `PlayerHandBodies` | Creates MuJoCo bodies from OVR hand tracking for physics interaction | OVRCameraRig |
| `SceneMeshManager` | Sets up room geometry, furniture anchors, and physics colliders from MRUK | SceneMesh object |
| `QuestPerformanceManager` | Requests CPU/GPU performance levels from Quest OS | Global Settings |
| `PlayerLocomotion` | Smooth walk and rotate via controller thumbstick | OVRCameraRig |
| `AmbientLightEstimator` | Estimates ambient lighting from passthrough camera for realistic rendering | Runtime (auto) |

## Roadmap

- **Voice interaction** — Speech-to-intent pipeline for verbal commands and conversational interaction with the Synth
- **Haptic feedback** — Controller and hand haptics driven by MuJoCo contact forces
- **Spatial audio** — 3D audio anchored to the Synth for immersive presence
- **Better light harmonization** — Improved passthrough lighting estimation for more realistic Synth rendering in your room
- **Better occlusion** — Depth-accurate occlusion between virtual Synth and real-world objects
- **Synth sees your world** — Feed pre-scanned Gaussian splat of the room into Synth eye cameras so the agent perceives the real environment

## License

Apache-2.0 — see [LICENSE](LICENSE) for details.
