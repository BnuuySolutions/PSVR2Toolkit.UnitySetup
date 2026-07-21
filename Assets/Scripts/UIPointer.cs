using UnityEngine;
using UnityEngine.EventSystems;
using Valve.VR;

/// <summary>
/// A VR pointer component that registers with VRInputModule to interact with uGUI.
/// </summary>
public class UIPointer : MonoBehaviour
{
    [Header("Input Setup")]
    [Tooltip("The SteamVR boolean action used to trigger clicks.")]
    public SteamVR_Action_Boolean uiClick;

    [Tooltip("SteamVR Input source to listen to.")]
    public SteamVR_Input_Sources inputSource = SteamVR_Input_Sources.Any;

    [Tooltip("Unique ID for this pointer (e.g. -2 for right hand, -3 for left hand) to track hover/drag states independently.")]
    public int pointerId = -2;

    [Header("Visual References")]
    [Tooltip("Visual cursor positioned at the intersection point.")]
    public GameObject hitVisual;

    [Tooltip("Optional visual laser pointer line (e.g. Cylinder/Line) to scale with hit distance.")]
    public Transform pointerVisual;

    private void OnEnable()
    {
        VRInputModule.RegisterPointer(this);

        if (uiClick != null)
        {
            uiClick.actionSet.Activate(inputSource);
        }
    }

    private void OnDisable()
    {
        VRInputModule.UnregisterPointer(this);
    }

    public bool GetClickDown()
    {
        return uiClick != null && uiClick.GetStateDown(inputSource);
    }

    public bool GetClickUp()
    {
        return uiClick != null && uiClick.GetStateUp(inputSource);
    }

    public bool GetClickHeld()
    {
        return uiClick != null && uiClick.GetState(inputSource);
    }

    /// <summary>
    /// Updates pointer and cursor visuals using the actual hit distance returned by the EventSystem.
    /// </summary>
    public void UpdateVisual(RaycastResult raycastResult)
    {
        if (raycastResult.isValid)
        {
            float hitDistance = raycastResult.distance;

            if (hitVisual != null)
            {
                hitVisual.SetActive(true);
                // Project position forward along pointer transform
                hitVisual.transform.position = transform.position + transform.forward * hitDistance;
            }

            if (pointerVisual != null)
            {
                pointerVisual.gameObject.SetActive(true);
                // Adjust cylinder-style visual length and pivot
                pointerVisual.localPosition = new Vector3(0, 0, hitDistance / 2.0f);
                pointerVisual.localScale = new Vector3(pointerVisual.localScale.x, hitDistance / 2.0f, pointerVisual.localScale.z);
            }
        }
        else
        {
            if (hitVisual != null)
            {
                hitVisual.SetActive(false);
            }

            if (pointerVisual != null)
            {
                pointerVisual.gameObject.SetActive(true);
                // Fallback to default length of 5.0m
                pointerVisual.localPosition = new Vector3(0, 0, 5.0f / 2.0f);
                pointerVisual.localScale = new Vector3(pointerVisual.localScale.x, 5.0f / 2.0f, pointerVisual.localScale.z);
            }
        }
    }
}
