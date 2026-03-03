// Copyright 2026 Arghya Sur / Genesis  –  Apache-2.0
//
// Smooth VR locomotion for the Meta Quest OVRCameraRig (player rig, not the Synth).
//
// SETUP:
//   1. Add this component to the "[BuildingBlock] Camera Rig" GameObject.
//   2. Assign centerEyeAnchor (OVRCameraRig.centerEyeAnchor).
//   3. In Quest headset Settings > Movement Settings > disable Comfort Mode
//      to remove the OS-level vignette on physical head rotation.
//      (Unity cannot override this OS setting; we add no vignette ourselves.)
//
// CONTROLS:
//   Left  thumbstick X/Y  → strafe / walk   (head-relative horizontal plane)
//   Right thumbstick X    → smooth yaw rotation  (no snap, no vignette)
//   Right thumbstick Y    → (unused – reserved for vertical/climb later)

using UnityEngine;

namespace Genesis.Sentience.VR
{
    public class PlayerLocomotion : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("OVRCameraRig.centerEyeAnchor — used for head-relative direction")]
        public Transform centerEyeAnchor;

        [Header("Movement")]
        [Tooltip("Walk/strafe speed in m/s")]
        public float moveSpeed = 2.5f;

        [Header("Rotation")]
        [Tooltip("Smooth yaw rotation speed in degrees/second")]
        public float rotationSpeed = 80f;

        [Header("Comfort (Unity-side only)")]
        [Tooltip("Dead-zone for thumbsticks — prevents drift")]
        [Range(0f, 0.3f)]
        public float deadZone = 0.1f;

        void Update()
        {
            HandleMovement();
            HandleRotation();
        }

        void HandleMovement()
        {
            var stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);
            if (stick.magnitude < deadZone) return;

            // Project head facing onto the horizontal plane
            Vector3 forward = centerEyeAnchor
                ? centerEyeAnchor.forward
                : Camera.main ? Camera.main.transform.forward : transform.forward;

            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) return;
            forward.Normalize();

            Vector3 right = new Vector3(forward.z, 0f, -forward.x);

            Vector3 move = (forward * stick.y + right * stick.x) * moveSpeed * Time.deltaTime;
            transform.position += move;
        }

        void HandleRotation()
        {
            var stick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
            float x = stick.x;
            if (Mathf.Abs(x) < deadZone) return;

            float yaw = x * rotationSpeed * Time.deltaTime;
            transform.Rotate(0f, yaw, 0f, Space.World);
        }
    }
}
