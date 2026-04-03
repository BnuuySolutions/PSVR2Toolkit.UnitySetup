using UnityEngine;
using System;
using Valve.VR;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Linq;
using System.Collections.Generic;

public class PSVR2CameraViewer : MonoBehaviour
{
    public MeshRenderer cameraRendererLeft;
    public MeshRenderer cameraRendererRight;
    public Material bc4Material;
    public Transform poseStabilizer; // Assign the parent of the passthrough meshes here
    public Transform center;
    public Vector3 correction;
    public Vector3 correctionPost;

    private Texture2D texLeft, texRight;
    private byte[] bufferLeft, bufferRight;
    private Material matLeft, matRight;
    private MeshFilter meshFilterLeft, meshFilterRight;

    private List<RelativeTransform> relativeTransforms;

    void Start()
    {
        // Ensure SteamVR is initialized
        if (SteamVR.instance != null)
        {
            Debug.Log("Setting tracking universe to Raw and Uncalibrated...");

            SteamVR.settings.trackingSpace = ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated;
        }
        else
        {
            Debug.LogError("SteamVR is not initialized. Cannot set tracking universe.");
        }

        try
        {
            PSVR2SharedMemory.Init();
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to initialize PSVR2 Shared Memory: {e.Message}");
            this.enabled = false;
            return;
        }

        int width = 1024;
        int height = 1016;
        texLeft = new Texture2D(width, height, TextureFormat.BC4, false);
        texRight = new Texture2D(width, height, TextureFormat.BC4, false);
        bufferLeft = new byte[PSVR2SharedMemory.BC4_DATA_SIZE];
        bufferRight = new byte[PSVR2SharedMemory.BC4_DATA_SIZE];

        meshFilterLeft = cameraRendererLeft.GetComponent<MeshFilter>();
        meshFilterRight = cameraRendererRight.GetComponent<MeshFilter>();

        matLeft = Instantiate(bc4Material);
        matRight = Instantiate(bc4Material);

        matLeft.SetInt("_StereoEyeIndex", 0);  // Left Eye
        matRight.SetInt("_StereoEyeIndex", 1); // Right Eye

        cameraRendererLeft.material = matLeft;
        cameraRendererRight.material = matRight;

        matLeft.SetTexture("_MainTex", texLeft);
        matRight.SetTexture("_MainTex", texRight);

        UpdateMeshes();

        if (poseStabilizer == null)
        {
            Debug.LogWarning("Pose Stabilizer not assigned. Creating one automatically.");
            GameObject stabilizer = new GameObject("Passthrough Stabilizer");
            poseStabilizer = stabilizer.transform;

            // Try to parent to XR Origin if possible, otherwise root is okay if it tracks room-scale.
            if (Camera.main != null && Camera.main.transform.parent != null)
            {
                poseStabilizer.SetParent(Camera.main.transform.parent);
            }

            poseStabilizer.localPosition = Vector3.zero;
            poseStabilizer.localRotation = Quaternion.identity;

            cameraRendererLeft.transform.SetParent(poseStabilizer, false);
            cameraRendererRight.transform.SetParent(poseStabilizer, false);
        }

        relativeTransforms = PSVR2SharedMemory.GetCameraRelativeTransforms().ToList();
    }

    void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeforeRender;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeforeRender;
    }

    void OnBeforeRender(ScriptableRenderContext context, Camera camera)
    {
        // Get latest image AND the interpolated pose for when it was taken
        if (PSVR2SharedMemory.GetLatestImageBuffer(bufferLeft, bufferRight, out PoseData historicalPose))
        {
            texLeft.LoadRawTextureData(bufferLeft);
            texRight.LoadRawTextureData(bufferRight);
            texLeft.Apply();
            texRight.Apply();

            if (historicalPose.isValid)
            {
                // Note that Camera 0 is the root. poseStabilizer will be where the HMD thinks camera 0 is.
                poseStabilizer.localPosition = historicalPose.position;
                poseStabilizer.localRotation =
                    Quaternion.Euler(correction.x, 0, 0)
                    * Quaternion.Euler(0, correction.y, 0)
                    * Quaternion.Euler(0, 0, correction.z)
                    * historicalPose.rotation
                    * Quaternion.Euler(correctionPost.x, 0, 0)
                    * Quaternion.Euler(0, correctionPost.y, 0)
                    * Quaternion.Euler(0, 0, correctionPost.z);

                // These relative transforms seem to be in another space compared to the poses we get directly from ShareManager.
                // Luckly for us, it appears the relative transforms from the config are in the same coordinate space as Unity.
                // If we put any offset on the left camera (index 0), we must start the relative from that Transform.
                var abs = PSVR2SharedMemory.ComputeAbsolutePoses(
                    relativeTransforms,
                    meshFilterLeft.transform.position,
                    meshFilterLeft.transform.rotation);

                // Right camera is index 1
                meshFilterRight.transform.position = abs[1].GetPosition();
                meshFilterRight.transform.rotation = abs[1].GetRotation();
            }

            matLeft.SetVector("_FloorPosition", center.position);
            matRight.SetVector("_FloorPosition", center.position);
            matLeft.SetVector("_FloorUp", center.up);
            matRight.SetVector("_FloorUp", center.up);
        }
    }

    void UpdateMeshes()
    {
        CameraParameters params0, params1;
        CameraIntrinsics intr0, intr1;

        bool ok0 = PSVR2SharedMemory.GetDistortionConfig(0, out params0, out intr0);
        bool ok1 = PSVR2SharedMemory.GetDistortionConfig(1, out params1, out intr1);

        if (ok0)
            meshFilterLeft.mesh = PSVR2Distortion.CreateUndistortionMesh(1024, 1016, intr0, params0);
        else
            meshFilterLeft.mesh = PSVR2Distortion.CreateDefaultMesh();

        if (ok1)
            meshFilterRight.mesh = PSVR2Distortion.CreateUndistortionMesh(1024, 1016, intr1, params1);
        else
            meshFilterRight.mesh = PSVR2Distortion.CreateDefaultMesh();
    }

    void OnApplicationQuit()
    {
        PSVR2SharedMemory.Cleanup();
    }
}