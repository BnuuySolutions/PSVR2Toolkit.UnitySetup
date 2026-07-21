using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// A custom PointerEventData subclass that stores the 3D ray of a VR controller.
/// </summary>
public class VRPointerEventData : PointerEventData
{
    public Ray worldRay;

    public VRPointerEventData(EventSystem eventSystem) : base(eventSystem)
    {
    }
}

/// <summary>
/// A custom EventSystem InputModule that routes VR controller pointer rays
/// to standard Unity uGUI elements using 3D raycasting, completely bypassing the need for event cameras.
/// </summary>
public class VRInputModule : PointerInputModule
{
    private static readonly List<UIPointer> s_Pointers = new List<UIPointer>();

    private readonly Dictionary<int, VRPointerEventData> m_VRPointerData = new Dictionary<int, VRPointerEventData>();

    /// <summary>
    /// Registers an active UIPointer with the input module.
    /// </summary>
    public static void RegisterPointer(UIPointer pointer)
    {
        if (pointer != null && !s_Pointers.Contains(pointer))
        {
            s_Pointers.Add(pointer);
        }
    }

    /// <summary>
    /// Unregisters a UIPointer from the input module.
    /// </summary>
    public static void UnregisterPointer(UIPointer pointer)
    {
        if (pointer != null)
        {
            s_Pointers.Remove(pointer);
        }
    }

    public override void Process()
    {
        // Loop backwards in case a pointer is disabled/removed during processing
        for (int i = s_Pointers.Count - 1; i >= 0; i--)
        {
            UIPointer pointer = s_Pointers[i];
            if (pointer == null || !pointer.gameObject.activeInHierarchy || !pointer.enabled)
                continue;

            ProcessPointer(pointer);
        }
    }

    private void ProcessPointer(UIPointer pointer)
    {
        VRPointerEventData eventData = GetVRPointerData(pointer.pointerId);
        eventData.Reset();

        eventData.worldRay = new Ray(pointer.transform.position, pointer.transform.forward);
        eventData.position = new Vector2(Screen.width / 2.0f, Screen.height / 2.0f);

        eventSystem.RaycastAll(eventData, m_RaycastResultCache);
        eventData.pointerCurrentRaycast = FindFirstRaycast(m_RaycastResultCache);
        m_RaycastResultCache.Clear();

        ProcessMove(eventData);

        bool pressDown = pointer.GetClickDown();
        bool pressUp = pointer.GetClickUp();
        bool pressed = pointer.GetClickHeld();

        ProcessClickAndDrag(eventData, pressDown, pressUp, pressed);

        pointer.UpdateVisual(eventData.pointerCurrentRaycast);
    }

    private void ProcessClickAndDrag(VRPointerEventData eventData, bool pressDown, bool pressUp, bool pressed)
    {
        GameObject target = eventData.pointerCurrentRaycast.gameObject;

        // --- Press Down ---
        if (pressDown)
        {
            eventData.pressPosition = eventData.position;
            eventData.pointerPressRaycast = eventData.pointerCurrentRaycast;
            eventData.pointerPress = ExecuteEvents.ExecuteHierarchy(target, eventData, ExecuteEvents.pointerDownHandler);
            
            if (eventData.pointerPress == null)
            {
                eventData.pointerPress = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            }

            eventData.rawPointerPress = target;
            eventData.eligibleForClick = true;
            eventData.clickTime = Time.unscaledTime;

            // Handle potential drag start
            eventData.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(target);
            if (eventData.pointerDrag != null)
            {
                ExecuteEvents.Execute(eventData.pointerDrag, eventData, ExecuteEvents.initializePotentialDrag);
            }
        }

        // --- Drag ---
        if (pressed && eventData.pointerDrag != null)
        {
            float threshold = eventSystem.pixelDragThreshold;
            bool shouldStartDrag = !eventData.dragging && 
                (eventData.useDragThreshold ? (eventData.pressPosition - eventData.position).sqrMagnitude >= threshold * threshold : true);

            if (shouldStartDrag)
            {
                ExecuteEvents.Execute(eventData.pointerDrag, eventData, ExecuteEvents.beginDragHandler);
                eventData.dragging = true;
            }

            if (eventData.dragging)
            {
                ExecuteEvents.Execute(eventData.pointerDrag, eventData, ExecuteEvents.dragHandler);
            }
        }

        // --- Release / Click ---
        if (pressUp)
        {
            ExecuteEvents.Execute(eventData.pointerPress, eventData, ExecuteEvents.pointerUpHandler);

            GameObject clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            if (eventData.pointerPress == clickHandler && eventData.eligibleForClick)
            {
                ExecuteEvents.Execute(eventData.pointerPress, eventData, ExecuteEvents.pointerClickHandler);
            }

            if (eventData.dragging && eventData.pointerDrag != null)
            {
                ExecuteEvents.Execute(eventData.pointerDrag, eventData, ExecuteEvents.endDragHandler);
                GameObject dropHandler = ExecuteEvents.GetEventHandler<IDropHandler>(target);
                if (dropHandler != null)
                {
                    ExecuteEvents.Execute(dropHandler, eventData, ExecuteEvents.dropHandler);
                }
            }

            eventData.pointerPress = null;
            eventData.rawPointerPress = null;
            eventData.eligibleForClick = false;
            eventData.pointerDrag = null;
            eventData.dragging = false;
        }
    }

    private VRPointerEventData GetVRPointerData(int id)
    {
        VRPointerEventData data;
        if (!m_VRPointerData.TryGetValue(id, out data))
        {
            data = new VRPointerEventData(eventSystem);
            m_VRPointerData[id] = data;
        }
        return data;
    }
}
