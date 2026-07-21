using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Valve.VR;

/// <summary>
/// Calibrates the floor height of the Chaperone mesh based on the VR controller's position.
/// </summary>
public class FloorHeightCalibrator : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The ChaperoneMesh instance to calibrate.")]
    public ChaperoneMesh chaperoneMesh;

    [Tooltip("The RoomCenter object. Its Y position will be aligned with the floor height.")]
    public Transform roomCenter;

    [Tooltip("The VR controller transforms to use for height calibration. If null, this object's transform will be used.")]
    public List<Transform> controllerTransforms;

    [Header("SteamVR Input Configuration")]
    [Tooltip("The boolean action (e.g., Trigger) used to commit the floor calibration.")]
    public SteamVR_Action_Boolean calibrateAction;

    [Tooltip("The action to move the floor height up or down.")]
    public SteamVR_Action_Vector2 floorMoveAction;

    [Tooltip("SteamVR Input source to listen to.")]
    public SteamVR_Input_Sources inputSource = SteamVR_Input_Sources.Any;

    [Header("Events")]
    [Tooltip("Invoked after the floor has been successfully calibrated.")]
    public UnityEvent OnFloorCalibrated;



    private void OnEnable()
    {
        if (calibrateAction != null)
        {
            calibrateAction.actionSet.Activate(inputSource);
        }
        if (floorMoveAction != null)
        {
            floorMoveAction.actionSet.Activate(inputSource);
        }
    }

    private void OnDisable()
    {
        if (calibrateAction != null)
        {
            calibrateAction.actionSet.Deactivate(inputSource);
        }
        if (floorMoveAction != null)
        {
            floorMoveAction.actionSet.Deactivate(inputSource);
        }
    }

    private void Update()
    {
        if (calibrateAction != null)
        {
            // Continuously calibrate while held to give real-time visual feedback
            if (calibrateAction.GetState(inputSource))
            {
                CalibrateFloor(false);
            }

            // Commit and trigger transition event on release
            if (calibrateAction.GetStateUp(inputSource))
            {
                CalibrateFloor(true);
            }
        }

        // Handle manual adjustment via thumbstick/touchpad
        if (floorMoveAction != null)
        {
            Vector2 moveInput = floorMoveAction.GetAxis(inputSource);
            if (Mathf.Abs(moveInput.y) > 0.1f)
            {
                float moveAmount = moveInput.y * Time.deltaTime * 0.1f;
                float newFloorHeight = chaperoneMesh.GetFloorHeight() + moveAmount;
                chaperoneMesh.AdjustFloorHeight(newFloorHeight);
                if (roomCenter != null)
                {
                    Vector3 newCenter = roomCenter.position;
                    newCenter.y = newFloorHeight;
                    roomCenter.position = newCenter;
                }
            }
        }
    }

    /// <summary>
    /// Performs the floor calibration calculation and updates ChaperoneMesh and RoomCenter,
    /// always firing the completion event.
    /// </summary>
    public void CalibrateFloor()
    {
        CalibrateFloor(true);
    }

    /// <summary>
    /// Performs the floor calibration calculation and updates ChaperoneMesh and RoomCenter,
    /// optionally firing the completion event.
    /// </summary>
    public void CalibrateFloor(bool fireEvent)
    {
        if (chaperoneMesh == null)
        {
            Debug.LogError("[FloorHeightCalibrator] ChaperoneMesh is not assigned.", this);
            return;
        }

        float floorY = float.MaxValue;

        foreach (var controllerTransform in controllerTransforms)
        {
            if (controllerTransform == null) continue;

            // Try to find the VRControllerModelLoader component first
            VRControllerModelLoader loader = controllerTransform.GetComponent<VRControllerModelLoader>();
            if (loader != null)
            {
                List<MeshCollider> colliders = loader.GetColliders();
                foreach (var col in colliders)
                {
                    if (col == null || !col.enabled || !col.gameObject.activeInHierarchy) continue;

                    Vector3 queryPoint = col.transform.position + Vector3.down * 100f;
                    Vector3 bottomPoint = col.ClosestPoint(queryPoint);

                    if (bottomPoint.y < floorY)
                    {
                        floorY = bottomPoint.y;
                    }
                }
            }
            else
            {
                // Fallback: search for standard colliders attached directly
                Collider[] colliders = controllerTransform.GetComponentsInChildren<Collider>();
                foreach (var col in colliders)
                {
                    if (col == null || !col.enabled || !col.gameObject.activeInHierarchy) continue;

                    Vector3 queryPoint = col.transform.position + Vector3.down * 100f;
                    Vector3 bottomPoint = col.ClosestPoint(queryPoint);

                    if (bottomPoint.y < floorY)
                    {
                        floorY = bottomPoint.y;
                    }
                }
            }
        }

        // Fallback: if no active colliders are present, use the lowest controller transform position directly
        if (floorY == float.MaxValue)
        {
            foreach (var controllerTransform in controllerTransforms)
            {
                if (controllerTransform != null && controllerTransform.position.y < floorY)
                {
                    floorY = controllerTransform.position.y;
                }
            }
        }

        // This shouldn't happen, but just in case:
        if (floorY == float.MaxValue)
        {
            floorY = 0f;
        }

        // Update chaperone floor height
        chaperoneMesh.AdjustFloorHeight(floorY);

        // Update RoomCenter Y position to match the floor plane
        if (roomCenter != null)
        {
            Vector3 newCenter = roomCenter.position;
            newCenter.y = floorY;
            roomCenter.position = newCenter;
        }

        if (fireEvent)
        {
            Debug.Log($"[FloorHeightCalibrator] Floor calibration committed at Y = {floorY}m");
            OnFloorCalibrated?.Invoke();
        }
    }
}
