using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// A custom 3D Canvas Raycaster that detects intersections of a VR pointer's ray
/// with uGUI elements directly in world space, eliminating the need for event cameras.
/// </summary>
[RequireComponent(typeof(Canvas))]
public class VRGraphicRaycaster : BaseRaycaster
{
    private Canvas m_Canvas;
    private Canvas canvas
    {
        get
        {
            if (m_Canvas == null)
                m_Canvas = GetComponent<Canvas>();
            return m_Canvas;
        }
    }

    // Standard override required by EventSystem; we return Camera.main as a fallback reference
    public override Camera eventCamera
    {
        get
        {
            return Camera.main;
        }
    }

    protected override void Awake()
    {
        base.Awake();
        
        // Automatically clean up standard GraphicRaycaster on this canvas to prevent double raycasts
        GraphicRaycaster standardRaycaster = GetComponent<GraphicRaycaster>();
        if (standardRaycaster != null)
        {
            if (Application.isPlaying)
            {
                Destroy(standardRaycaster);
            }
            else
            {
                DestroyImmediate(standardRaycaster);
            }
            Debug.Log($"[VRGraphicRaycaster] Replaced default GraphicRaycaster on Canvas '{name}'", this);
        }
    }

    public override void Raycast(PointerEventData eventData, List<RaycastResult> resultBag)
    {
        if (canvas == null) return;

        // Cast eventData to VRPointerEventData to access the 3D ray
        VRPointerEventData vrEventData = eventData as VRPointerEventData;
        if (vrEventData == null) return;

        Ray ray = vrEventData.worldRay;

        // Define the plane of the canvas (canvases face the Z-back direction)
        Plane canvasPlane = new Plane(-canvas.transform.forward, canvas.transform.position);

        if (canvasPlane.Raycast(ray, out float enterDistance))
        {
            // Enforce max distance check if configured
            float maxDist = 100f; // Standard default far plane limit
            if (enterDistance > maxDist) return;

            Vector3 worldHitPoint = ray.GetPoint(enterDistance);
            
            // Check if the intersection point is within the canvas boundary limits
            RectTransform canvasRect = canvas.GetComponent<RectTransform>();
            Vector3 localHitPoint = canvasRect.InverseTransformPoint(worldHitPoint);

            if (!canvasRect.rect.Contains(localHitPoint))
            {
                return;
            }

            // Get all active graphics on this canvas
            IList<Graphic> graphics = GraphicRegistry.GetGraphicsForCanvas(canvas);
            List<Graphic> hitGraphics = new List<Graphic>();

            for (int i = 0; i < graphics.Count; i++)
            {
                Graphic graphic = graphics[i];
                if (!graphic.raycastTarget || !graphic.isActiveAndEnabled) continue;

                // Project hit point into the graphic's local space
                Vector3 graphicLocalHit = graphic.rectTransform.InverseTransformPoint(worldHitPoint);
                if (graphic.rectTransform.rect.Contains(graphicLocalHit))
                {
                    // Check canvas group interactable filters and alpha masks
                    if (RaycastFilterPasses(graphic.gameObject, worldHitPoint))
                    {
                        hitGraphics.Add(graphic);
                    }
                }
            }

            if (hitGraphics.Count > 0)
            {
                // Sort by graphic depth descending (highest depth on top)
                hitGraphics.Sort((a, b) => b.depth.CompareTo(a.depth));

                Graphic topmostHit = hitGraphics[0];

                RaycastResult result = new RaycastResult
                {
                    gameObject = topmostHit.gameObject,
                    module = this,
                    distance = enterDistance,
                    worldPosition = worldHitPoint,
                    worldNormal = -canvas.transform.forward,
                    screenPosition = eventData.position,
                    index = resultBag.Count,
                    sortingLayer = canvas.sortingLayerID,
                    sortingOrder = canvas.sortingOrder
                };

                resultBag.Add(result);
            }
        }
    }

    private bool RaycastFilterPasses(GameObject go, Vector3 worldPoint)
    {
        ICanvasRaycastFilter[] filters = go.GetComponentsInParent<ICanvasRaycastFilter>();
        Vector2 screenPoint = Camera.main != null ? (Vector2)Camera.main.WorldToScreenPoint(worldPoint) : Vector2.zero;
        
        foreach (ICanvasRaycastFilter filter in filters)
        {
            if (!filter.IsRaycastLocationValid(screenPoint, Camera.main))
            {
                return false;
            }
        }
        return true;
    }
}
