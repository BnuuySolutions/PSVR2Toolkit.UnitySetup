using UnityEngine;
using Valve.VR;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// A simple VR pointer that can edit a ChaperoneMesh by adding or removing vertices.
/// This component should be placed on a controller object that has its transform updated every frame.
/// </summary>
public class ChaperoneEditorPointer : MonoBehaviour
{
    [Tooltip("The ChaperoneMesh instance to be edited.")]
    public ChaperoneMesh chaperoneMesh;

    [Tooltip("The RoomCenter object. Used to determine the floor plane for point placement.")]
    public Transform roomCenter;

    [Tooltip("A visual representation of the brush's hit point and area.")]
    public Transform hitVisual;

    [Tooltip("A visual representation of the pointer line.")]
    public Transform pointerVisual;

    [Tooltip("The radius of the editing brush in meters.")]
    public float brushRadius = 0.1f;

    [Tooltip("The single action used to trigger the mesh modification.")]
    public SteamVR_Action_Boolean modifyAction;

    [Tooltip("The action to activate snapping to the nearest point.")]
    public SteamVR_Action_Boolean snapAction;

    [Tooltip("The action to delete the currently snapped point.")]
    public SteamVR_Action_Boolean deletePointAction;

    [Tooltip("The action to enable floor height adjustment mode.")]
    public SteamVR_Action_Boolean floorAdjustAction;

    [Tooltip("The action to move the floor height up or down.")]
    public SteamVR_Action_Vector2 floorMoveAction;

    [Tooltip("The action to move the entire play area.")]
    public SteamVR_Action_Boolean movePlayAreaAction;

    [Tooltip("The haptic feedback action.")]
    public SteamVR_Action_Vibration hapticAction;

    [Tooltip("SteamVR Input source to use")]
    public SteamVR_Input_Sources inputSource = SteamVR_Input_Sources.Any;

    [Header("Movement Smoothing")]
    [Tooltip("Smoothing factor for the pointer's movement. Higher values mean less smoothing.")]
    [Range(1f, 30f)]
    public float smoothingFactor = 15f;

    [Tooltip("Maximum speed of the pointer in meters per second.")]
    public float maxVelocity = 10f;

    public float maxAngle = 75f;

    public Shader controllerShader;


    // Determine if we are adding or subtracting based on whether the hit point is inside the current mesh
    private bool isInside = false;
    private List<Vector3> brushPoints = new List<Vector3>();

    private Vector3 _smoothedHitPoint;
    private Vector3 _previousSmoothedHitPoint;

    private static bool isAnyPointerDrawing = false;
    private bool amIDrawing = false;
    private bool isSnapping = false;
    private int snappedVertexIndex = -1;

    private bool isMovingPlayArea = false;
    private Vector3 lastControllerPos;
    private Quaternion lastControllerRot;
    private bool hasLoaded = false;
    private GameObject modelHolder;

    private List<MeshCollider> controllerColliders = new List<MeshCollider>();

    private Task drawingTask = null;

    void Start()
    {
        // Generate the round brush points
        brushPoints = new List<Vector3>();
        for (int i = 0; i < 12; i++)
        {
            float angle = i * (360f / 8f) * Mathf.Deg2Rad;
            brushPoints.Add(new Vector3(
                brushRadius * Mathf.Cos(angle),
                0,
                brushRadius * Mathf.Sin(angle)
            ));
        }

        // Initialize smoothed position
        _smoothedHitPoint = transform.position;
        _previousSmoothedHitPoint = transform.position;

        // Add child object
        modelHolder = new GameObject("ControllerModel");
        modelHolder.transform.SetParent(transform);

        // Add SteamVR render model
        SteamVR_RenderModel renderModel = modelHolder.AddComponent<SteamVR_RenderModel>();
        renderModel.SetInputSource(inputSource);

        // Find device index belonging to our input source
        SteamVR_Behaviour_Pose pose = GetComponent<SteamVR_Behaviour_Pose>();
        if (pose != null)
        {
            pose.inputSource = inputSource;
            renderModel.SetDeviceIndex(pose.GetDeviceIndex());
        }

        // Set shader to URP lit
        renderModel.shader = controllerShader;
    }

    private void OnModelLoaded(SteamVR_RenderModel model)
    {
        // Clear existing colliders from previous models
        foreach (var collider in controllerColliders)
        {
            if (collider != null) Destroy(collider);
        }
        controllerColliders.Clear();

        foreach (var mf in model.GetComponentsInChildren<MeshFilter>())
        {
            controllerColliders.Add(mf.gameObject.AddComponent<MeshCollider>());
        }
    }

    private void OnEnable()
    {
        // Activate the action set when this component is enabled.
        // This assumes both actions are in the same action set.
        if (modifyAction != null)
            modifyAction.actionSet.Activate(inputSource);
        if (snapAction != null)
            snapAction.actionSet.Activate(inputSource);
        if (deletePointAction != null)
            deletePointAction.actionSet.Activate(inputSource);
        if (floorAdjustAction != null)
            floorAdjustAction.actionSet.Activate(inputSource);
        if (floorMoveAction != null)
            floorMoveAction.actionSet.Activate(inputSource);
        if (movePlayAreaAction != null)
            movePlayAreaAction.actionSet.Activate(inputSource);
    }

    private void OnDisable()
    {
        if (modifyAction != null)
            modifyAction.actionSet.Deactivate(inputSource);
        if (snapAction != null)
            snapAction.actionSet.Deactivate(inputSource);
        if (deletePointAction != null)
            deletePointAction.actionSet.Deactivate(inputSource);
        if (floorAdjustAction != null)
            floorAdjustAction.actionSet.Deactivate(inputSource);
        if (floorMoveAction != null)
            floorMoveAction.actionSet.Deactivate(inputSource);
        if (movePlayAreaAction != null)
            movePlayAreaAction.actionSet.Deactivate(inputSource);
    }

    void Update()
    {
        if (chaperoneMesh == null) return;
        if (roomCenter == null) return;

        Plane floorPlane = new Plane(Vector3.up, roomCenter != null ? roomCenter.position : Vector3.zero);

        Ray ray = new Ray(transform.position, transform.forward);
        bool didHit = floorPlane.Raycast(ray, out float hitDistance);

        // Limit how far the pointer can angle away the floor
        float angleFromNormal = Vector3.Angle(Vector3.up, -transform.forward);
        if (angleFromNormal > maxAngle)
        {
            didHit = false;
        }

        if (hitVisual != null && !isAnyPointerDrawing)
        {
            hitVisual.gameObject.SetActive(didHit);
        }

        // Adjust length of pointer visual
        if (pointerVisual != null)
        {
            float visualLength = didHit ? hitDistance : 1.0f;
            pointerVisual.localPosition = new Vector3(0, 0, visualLength / 2.0f);
            pointerVisual.localScale = new Vector3(pointerVisual.localScale.x, visualLength / 2.0f, pointerVisual.localScale.z);
        }

        // Check if new children were added (the render model)
        if (modelHolder.transform.childCount != 0 && !hasLoaded)
        {
            hasLoaded = true;
            OnModelLoaded(modelHolder.GetComponent<SteamVR_RenderModel>());
        }

        // Handle floor movement input
        Vector2 moveInput = floorMoveAction.GetAxis(inputSource);
        if (Mathf.Abs(moveInput.y) > 0.1f)
        {
            float moveAmount = moveInput.y * Time.deltaTime * 0.1f;
            chaperoneMesh.AdjustFloorHeight(chaperoneMesh.GetFloorHeight() + moveAmount);
            var newCenter = roomCenter.position;
            newCenter.y += moveAmount;
            roomCenter.position = newCenter;
        }

        if (movePlayAreaAction != null && movePlayAreaAction.GetStateDown(inputSource))
        {
            isMovingPlayArea = true;
            lastControllerPos = transform.position;
            lastControllerRot = transform.rotation;
        }
        else if (movePlayAreaAction != null && movePlayAreaAction.GetStateUp(inputSource))
        {
            isMovingPlayArea = false;
        }

        if (isMovingPlayArea)
        {
            Vector3 translation = transform.position - lastControllerPos;
            translation.y = 0; // Prevent changing floor height
            float yawDelta = transform.rotation.eulerAngles.y - lastControllerRot.eulerAngles.y;

            chaperoneMesh.TransformPoints(translation, yawDelta, lastControllerPos);

            lastControllerPos = transform.position;
            lastControllerRot = transform.rotation;
            return; // Skip other interactions
        }

        isSnapping = snapAction.GetState(inputSource);

        if (floorAdjustAction.GetState(inputSource))
        {
            // --- FLOOR ADJUST MODE ---
            if (controllerColliders.Count > 0)
            {
                float lowestY = float.MaxValue;
                foreach (var collider in controllerColliders)
                {
                    // We need the world space bounds to find the lowest point.
                    var bounds = collider.bounds;
                    if (bounds.min.y < lowestY)
                    {
                        lowestY = bounds.min.y;
                    }
                }

                if (lowestY != float.MaxValue)
                {
                    chaperoneMesh.AdjustFloorHeight(lowestY);
                    var newCenter = roomCenter.position;
                    newCenter.y = lowestY;
                    roomCenter.position = newCenter;
                }
            }
        }
        else
        {
            // --- REGULAR EDITING MODE ---
            if (!didHit) return;
            UpdateSmoothedHitPoint(ray.GetPoint(hitDistance));
            if (isSnapping)
            {
                // --- SNAPPING MODE ---
                Vector3 snappedVertexWorldPos;
                snappedVertexIndex = chaperoneMesh.FindNearestPoint(_smoothedHitPoint, out snappedVertexWorldPos);

                if (snappedVertexIndex != -1)
                {
                    if ((hitVisual.position - snappedVertexWorldPos).magnitude > 0.1f)
                    {
                        hapticAction.Execute(0f, 0.05f, 300f, 0.5f, inputSource);
                    }
                    hitVisual.position = snappedVertexWorldPos;
                    hitVisual.localScale = new Vector3(0.05f, 0.05f, 0.05f); // Visual for point selection

                    if (deletePointAction.GetStateDown(inputSource))
                    {
                        chaperoneMesh.RemovePointAt(snappedVertexIndex);
                        chaperoneMesh.Simplify();
                    }
                    else if (modifyAction.GetState(inputSource))
                    {
                        chaperoneMesh.MovePoint(snappedVertexIndex, _smoothedHitPoint);
                    }
                }
            }
            else
            {
                // --- DRAWING MODE ---
                snappedVertexIndex = -1;
                hitVisual.position = _smoothedHitPoint;
                hitVisual.localScale = new Vector3(brushRadius * 2, 0.01f, brushRadius * 2);

                if (modifyAction.GetStateDown(inputSource))
                {
                    if (!isAnyPointerDrawing)
                    {
                        isAnyPointerDrawing = true;
                        amIDrawing = true;
                        isInside = chaperoneMesh.IsPointInside(_smoothedHitPoint);
                    }
                }
                else if (modifyAction.GetStateUp(inputSource))
                {
                    if (amIDrawing)
                    {
                        isAnyPointerDrawing = false;
                        amIDrawing = false;
                        chaperoneMesh.Simplify();
                    }
                }
                else if (!isAnyPointerDrawing)
                {
                    bool isInsideNew = chaperoneMesh.IsPointInside(_smoothedHitPoint);
                    if (isInsideNew != isInside)
                    {
                        isInside = isInsideNew;
                        hapticAction.Execute(0f, 0.025f, 60f, 0.5f, inputSource);
                    }
                }

                if (amIDrawing && modifyAction.GetState(inputSource))
                {
                    float dist = Vector3.Distance(_previousSmoothedHitPoint, _smoothedHitPoint);
                    if (dist > 0.001f) // Only modify if there's movement
                    {
                        hapticAction.Execute(0f, 0.1f, 350f, 0.25f, inputSource);

                        if (drawingTask != null && !drawingTask.IsCompleted)
                        {
                            // If the previous drawing task is still running, skip this frame's drawing to avoid conflicts.
                            return;
                        }

                        List<Vector3> combinedBrushPoints = new List<Vector3>();
                        Vector3 direction = (_smoothedHitPoint - _previousSmoothedHitPoint).normalized;

                        // Generate points for the capsule shape in perimeter order.
                        int brushPointCount = brushPoints.Count;
                        if (brushPointCount == 0) return;

                        // First semicircle at the current hit point
                        for (int i = 0; i <= brushPointCount / 2; i++)
                        {
                            float angle = (float)i / (brushPointCount / 2) * Mathf.PI;
                            Vector3 pointOnCircle = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * brushRadius;
                            combinedBrushPoints.Add(_smoothedHitPoint + Quaternion.LookRotation(direction) * pointOnCircle);
                        }

                        // Second semicircle at the previous hit point
                        for (int i = 0; i <= brushPointCount / 2; i++)
                        {
                            float angle = Mathf.PI + (float)i / (brushPointCount / 2) * Mathf.PI;
                            Vector3 pointOnCircle = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * brushRadius;
                            combinedBrushPoints.Add(_previousSmoothedHitPoint + Quaternion.LookRotation(direction) * pointOnCircle);
                        }

                        chaperoneMesh.ModifyPoints(combinedBrushPoints, isInside);
                        chaperoneMesh.Simplify(true);
                    }
                }
            }

            _previousSmoothedHitPoint = _smoothedHitPoint;
        }
    }

    private void UpdateSmoothedHitPoint(Vector3 rawHitPoint)
    {
        if (!isAnyPointerDrawing)
        {
            _smoothedHitPoint = rawHitPoint;
            _previousSmoothedHitPoint = rawHitPoint;
            return;
        }

        // Apply smoothing
        _smoothedHitPoint = Vector3.Lerp(_smoothedHitPoint, rawHitPoint, smoothingFactor * Time.deltaTime);

        // Apply max velocity
        Vector3 displacement = _smoothedHitPoint - _previousSmoothedHitPoint;
        float maxDisplacement = maxVelocity * Time.deltaTime;
        if (displacement.sqrMagnitude > maxDisplacement * maxDisplacement)
        {
            _smoothedHitPoint = _previousSmoothedHitPoint + displacement.normalized * maxDisplacement;
        }
    }
}