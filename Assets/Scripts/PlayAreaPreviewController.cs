using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using Clipper2Lib;
using Valve.VR;

/// <summary>
/// Smoothly previews the fitted play area rectangle while a SteamVR action is held.
/// Calculations run in a background thread to prevent main thread frame-rate hitching.
/// </summary>
public class PlayAreaPreviewController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The ChaperoneMesh instance containing the play area boundary.")]
    public ChaperoneMesh chaperoneMesh;

    [Tooltip("The SteamVR boolean action used to preview the play area while held.")]
    public SteamVR_Action_Boolean previewAction;

    [Tooltip("SteamVR Input source to listen to.")]
    public SteamVR_Input_Sources inputSource = SteamVR_Input_Sources.Any;

    [Header("Preview Settings")]
    [Tooltip("Smoothing speed for the rectangle preview interpolation. Higher is faster.")]
    public float lerpSpeed = 8.0f;

    [Tooltip("Grid resolution for the fitting algorithm.")]
    public int gridResolution = 100;

    [Header("Events")]
    [Tooltip("Invoked when a valid play area rectangle has been successfully fitted and previewed.")]
    public UnityEvent OnPreviewExists;

    // Thread synchronization locks
    private readonly object m_Lock = new object();
    private bool m_Running = false;
    private Task m_Task;

    // Inputs sent from Main Thread -> Background Thread
    private List<Vector3> m_PointsInput = new List<Vector3>();
    private Vector3 m_HeadPositionInput;
    private Vector3 m_HeadForwardInput;
    private float m_FloorHeightInput;
    private bool m_IsPreviewingInput = false;

    // Outputs computed by Background Thread -> Main Thread
    private float m_TargetCenter0;
    private float m_TargetCenter2;
    private float m_TargetWidth;
    private float m_TargetLength;
    private float m_TargetYaw;
    private bool m_HasTargets = false;

    // Current interpolated visual values (Main Thread)
    private float currentCenter0;
    private float currentCenter2;
    private float currentWidth;
    private float currentLength;
    private float currentYaw;
    private bool m_IsInitialized = false;
    private bool m_PreviewExistsEventFired = false;

    private void OnEnable()
    {
        m_IsInitialized = false;
        m_HasTargets = false;
        m_PreviewExistsEventFired = false;

        // Clear visual preview on enable
        if (chaperoneMesh != null)
        {
            chaperoneMesh.UpdateRectanglePreview(null);
        }

        if (previewAction != null)
        {
            previewAction.actionSet.Activate(inputSource);
        }

        m_Running = true;
        m_Task = Task.Run(BackgroundCalculationLoop);
    }

    private void OnDisable()
    {
        m_Running = false;
        m_HasTargets = false;
        m_IsInitialized = false;

        if (previewAction != null)
        {
            previewAction.actionSet.Deactivate(inputSource);
        }
    }

    private void Update()
    {
        if (chaperoneMesh == null || chaperoneMesh.head == null) return;

        // Reset preview state when the action is first pressed down
        if (previewAction != null && previewAction.GetStateDown(inputSource))
        {
            lock (m_Lock)
            {
                m_HasTargets = false;
            }
            m_IsInitialized = false;
            m_PreviewExistsEventFired = false;
        }

        // Check if the preview action is held
        bool isHeld = previewAction != null && previewAction.GetState(inputSource);
        
        // Capture inputs on Main Thread and share with background thread only if held
        lock (m_Lock)
        {
            m_IsPreviewingInput = isHeld;
            if (isHeld)
            {
                m_PointsInput = chaperoneMesh.GetWorldPoints();
                m_HeadPositionInput = chaperoneMesh.head.position;
                m_HeadForwardInput = chaperoneMesh.head.forward;
                m_FloorHeightInput = chaperoneMesh.GetFloorHeight();
            }
        }

        // Read outputs computed by background thread
        float tCenter0;
        float tCenter2;
        float tWidth;
        float tLength;
        float tYaw;
        bool hasTargets;

        lock (m_Lock)
        {
            tCenter0 = m_TargetCenter0;
            tCenter2 = m_TargetCenter2;
            tWidth = m_TargetWidth;
            tLength = m_TargetLength;
            tYaw = m_TargetYaw;
            hasTargets = m_HasTargets;
        }

        if (hasTargets)
        {
            // Fire event once when preview becomes valid
            if (!m_PreviewExistsEventFired)
            {
                m_PreviewExistsEventFired = true;
                Debug.Log("Preview Exists");
                OnPreviewExists?.Invoke();
            }

            if (!m_IsInitialized)
            {
                currentCenter0 = tCenter0;
                currentCenter2 = tCenter2;
                currentWidth = tWidth;
                currentLength = tLength;
                currentYaw = tYaw;
                m_IsInitialized = true;
            }

            currentCenter0 = Mathf.Lerp(currentCenter0, tCenter0, Time.deltaTime * lerpSpeed);
            currentCenter2 = Mathf.Lerp(currentCenter2, tCenter2, Time.deltaTime * lerpSpeed);
            currentWidth = Mathf.Lerp(currentWidth, tWidth, Time.deltaTime * lerpSpeed);
            currentLength = Mathf.Lerp(currentLength, tLength, Time.deltaTime * lerpSpeed);

            float targetYawDeg = tYaw * Mathf.Rad2Deg;
            float currentYawDeg = currentYaw * Mathf.Rad2Deg;
            currentYawDeg = Mathf.LerpAngle(currentYawDeg, targetYawDeg, Time.deltaTime * lerpSpeed);
            currentYaw = currentYawDeg * Mathf.Deg2Rad;

            float halfW = currentWidth / 2.0f;
            float halfL = currentLength / 2.0f;

            Vector2 c0 = new Vector2(-halfW, -halfL);
            Vector2 c1 = new Vector2(halfW, -halfL);
            Vector2 c2 = new Vector2(halfW, halfL);
            Vector2 c3 = new Vector2(-halfW, halfL);

            float[] centerCopy = new float[3] { currentCenter0, -m_FloorHeightInput, currentCenter2 };

            Vector3 w0 = TransformDriverToWorld(c0, centerCopy, currentYaw);
            Vector3 w1 = TransformDriverToWorld(c1, centerCopy, currentYaw);
            Vector3 w2 = TransformDriverToWorld(c2, centerCopy, currentYaw);
            Vector3 w3 = TransformDriverToWorld(c3, centerCopy, currentYaw);

            Vector3[] corners = new Vector3[] { w0, w1, w2, w3 };

            chaperoneMesh.UpdateRectanglePreview(corners);
        }
    }

    /// <summary>
    /// Returns the currently computed (and potentially interpolated) preview rectangle parameters.
    /// Returns true if a valid preview is active.
    /// </summary>
    public bool GetPreviewRectangle(out float center0, out float center2, out float width, out float length, out float yaw)
    {
        center0 = currentCenter0;
        center2 = currentCenter2;
        width = currentWidth;
        length = currentLength;
        yaw = currentYaw;
        return m_HasTargets;
    }

    private Vector3 TransformDriverToWorld(Vector2 driverPoint, float[] center, float yaw)
    {
        Vector3 basePos = new Vector3(center[0], center[1], center[2]);
        Quaternion yawRotation = Quaternion.Euler(0, yaw * Mathf.Rad2Deg, 0);
        Vector3 p = new Vector3(driverPoint.x, 0, driverPoint.y);
        Vector3 g = (yawRotation * p) - basePos;
        g.z = -g.z;
        return g;
    }

    private async Task BackgroundCalculationLoop()
    {
        while (m_Running)
        {
            try
            {
                CalculateTargetRectangle();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PlayAreaPreviewController] Error in background calculation: {ex.Message}\n{ex.StackTrace}");
            }

            await Task.Delay(100); // 10Hz update interval
        }
    }

    private void CalculateTargetRectangle()
    {
        List<Vector3> points;
        Vector3 headPos;
        Vector3 headForward;
        float floorHeight;
        bool isPreviewing;

        lock (m_Lock)
        {
            isPreviewing = m_IsPreviewingInput;
            if (!isPreviewing || m_PointsInput == null || m_PointsInput.Count < 3) return;
            points = new List<Vector3>(m_PointsInput);
            headPos = m_HeadPositionInput;
            headForward = m_HeadForwardInput;
            floorHeight = m_FloorHeightInput;
        }

        PathD polygon = new PathD(points.Count);
        foreach (var p in points)
        {
            polygon.Add(new PointD(p.x, p.z));
        }

        // Align the search angle with the head orientation on the XZ plane
        headForward.y = 0;
        headForward.Normalize();
        double angle = System.Math.Atan2(headForward.z, headForward.x);

        PathD largestRect = LargestRectangleFinder.FindLargestRectAtAngle(
            polygon, 
            angle, 
            gridResolution, 
            new PointD(headPos.x, headPos.z)
        );

        if (largestRect != null && largestRect.Count == 4)
        {
            float center0 = (float)(largestRect[0].x + largestRect[2].x) / -2.0f;
            float center2 = (float)(largestRect[0].y + largestRect[2].y) / 2.0f;
            
            float w = (float)System.Math.Sqrt(System.Math.Pow(largestRect[3].x - largestRect[0].x, 2) + System.Math.Pow(largestRect[3].y - largestRect[0].y, 2));
            float l = (float)System.Math.Sqrt(System.Math.Pow(largestRect[1].x - largestRect[0].x, 2) + System.Math.Pow(largestRect[1].y - largestRect[0].y, 2));
            
            float yaw = (float)((angle - (System.Math.PI / 2.0)) % (System.Math.PI * 2.0));

            lock (m_Lock)
            {
                m_TargetCenter0 = center0;
                m_TargetCenter2 = center2;
                m_TargetWidth = w;
                m_TargetLength = l;
                m_TargetYaw = yaw;
                m_HasTargets = true;
            }
        }
    }
}
