using System.Collections.Generic;
using UnityEngine;
using Valve.VR;

/// <summary>
/// Dynamically loads the SteamVR controller render model, applies custom shader parameters,
/// and automatically generates convex MeshColliders on load.
/// </summary>
public class VRControllerModelLoader : MonoBehaviour
{
    [Tooltip("SteamVR Input source to listen to.")]
    public SteamVR_Input_Sources inputSource = SteamVR_Input_Sources.Any;

    [Tooltip("The shader to assign to the loaded controller mesh (e.g. URP Lit).")]
    public Shader controllerShader;

    private readonly List<MeshCollider> m_Colliders = new List<MeshCollider>();
    private bool m_HasLoaded = false;
    private GameObject m_ModelHolder;

    /// <summary>
    /// Gets the list of dynamically generated MeshColliders on the controller model.
    /// </summary>
    public List<MeshCollider> GetColliders()
    {
        return m_Colliders;
    }

    private void Start()
    {
        m_ModelHolder = new GameObject("ControllerModel");
        m_ModelHolder.transform.SetParent(transform, false);

        SteamVR_RenderModel renderModel = m_ModelHolder.AddComponent<SteamVR_RenderModel>();
        renderModel.SetInputSource(inputSource);

        SteamVR_Behaviour_Pose pose = GetComponent<SteamVR_Behaviour_Pose>();
        if (pose != null)
        {
            pose.inputSource = inputSource;
            renderModel.SetDeviceIndex(pose.GetDeviceIndex());
        }

        renderModel.shader = controllerShader;
    }

    private void Update()
    {
        if (m_ModelHolder != null && m_ModelHolder.transform.childCount != 0 && !m_HasLoaded)
        {
            m_HasLoaded = true;
            OnModelLoaded(m_ModelHolder.GetComponent<SteamVR_RenderModel>());
        }
    }

    private void OnModelLoaded(SteamVR_RenderModel model)
    {
        // Clear existing colliders
        foreach (var col in m_Colliders)
        {
            if (col != null) Destroy(col);
        }
        m_Colliders.Clear();

        // Extract and add MeshColliders for each mesh filter
        foreach (MeshFilter mf in model.GetComponentsInChildren<MeshFilter>())
        {
            MeshCollider col = mf.gameObject.AddComponent<MeshCollider>();
            col.convex = true;
            m_Colliders.Add(col);
        }
        
        Debug.Log($"[VRControllerModelLoader] Loaded model for {inputSource} and generated {m_Colliders.Count} MeshColliders.", this);
    }
}
