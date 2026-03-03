# Synth VR

Mixed Reality interaction with Synth humanoids on Meta Quest. Physics-based hand tracking, room-scale environment setup, and passthrough rendering.

## Features

- **Physics Hand Tracking** — MuJoCo-bound hand bodies driven by OVR hand tracking. Push, grab, pull, and interact with the Synth using your real hands.
- **Room Integration** — MRUK-powered room setup with physics colliders for walls, floor, ceiling, and furniture anchors. The Synth interacts with your physical room layout.
- **Smooth Locomotion** — Controller-based walk and rotate with configurable speed.
- **Passthrough Rendering** — Occluder materials for room surfaces, PTRL (Passthrough Receive Light) shader for realistic lighting on the Synth.
- **Ambient Light Estimation** — Passthrough camera-based lighting estimation for harmonized rendering in mixed reality.

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

## Quick Start

1. Set up your Unity project with Meta XR SDK (Building Blocks, OVR camera rig).
2. Add a single Synth to your scene (via synth-core). **Note:** only one active Synth per scene is supported.
3. Add `PlayerHandBodies` to your OVR hand objects for physics interaction.
4. Add `PlayerLocomotion` to your camera rig for movement.
5. Add `SceneMeshManager` to your scene for room integration.
6. Build and deploy to Quest — your Synth is in your physical room.

## Package Structure

```
synth-vr/
├── Runtime/
│   ├── Hands/         PlayerHandBodies (MuJoCo hand tracking)
│   ├── Locomotion/    PlayerLocomotion (smooth walk/rotate)
│   ├── SceneSetup/    SceneMeshManager (MRUK room integration)
│   ├── Lighting/      AmbientLightEstimator
│   ├── Shaders/       PTRLWithDepth
│   └── Resources/     InvisibleOccluder, PTRLHighlightsAndShadows materials
└── Editor/
    └── XRSetupEditor.cs
```

## Components

| Component | Purpose |
|-----------|---------|
| `PlayerHandBodies` | Creates MuJoCo bodies from OVR hand tracking for physics interaction |
| `PlayerLocomotion` | Smooth walk and rotate via controller thumbstick |
| `SceneMeshManager` | Sets up room geometry, furniture anchors, and physics colliders from MRUK |
| `AmbientLightEstimator` | Estimates ambient lighting from passthrough camera for realistic rendering |

## Roadmap

- **Voice interaction** — Speech-to-intent pipeline for verbal commands and conversational interaction with the Synth
- **Haptic feedback** — Controller and hand haptics driven by MuJoCo contact forces
- **Spatial audio** — 3D audio anchored to the Synth for immersive presence
- **Better light harmonization** — Improved passthrough lighting estimation for more realistic Synth rendering in your room
- **Better occlusion** — Depth-accurate occlusion between virtual Synth and real-world objects
- **Synth sees your world** — Feed pre-scanned Gaussian splat of the room into Synth eye cameras so the agent perceives the real environment

## License

Apache-2.0 — see [LICENSE](LICENSE) for details.
