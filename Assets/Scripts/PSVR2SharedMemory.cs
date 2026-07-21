using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

// Replicates the structs from distortion.h and shared_memory.cpp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CameraParameters
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
    public double[] coeffs;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CameraIntrinsics
{
    public double cx, fx, cy, fy;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CameraConfig
{
    public uint camId;
    public ushort widthPx;
    public ushort heightPx;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
    public float[] pxMat;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
    public double[] coff;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
    public uint[] zeros;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CameraTransform
{
    public byte fromCamId;
    public byte toCamId;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public byte[] pad;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
    public float[] mat;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] pos;
}

public struct RelativeTransform
{
    public int FromId;
    public int ToId;
    public Matrix4x4 T;
}

public struct PoseData
{
    public Vector3 position;
    public Quaternion rotation;
    public bool isValid;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PlayArea
{
    public int version;
    public float height;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public float[] playAreaRect;
    public int pointCount;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
    public float[] points; // 256 max (x, z) points in driver space

    public UInt64 padding;
    public UInt64 padding2;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] standingCenter; // driver space
    public float yaw;
}

public static class PSVR2SharedMemory
{
    private const string FILE_MAPPING_NAME = "SHARE_VRT2_WIN";
    private const uint SHARED_MEM_SIZE = 0x2000000; // 32MB
    private const string EVENT_NAME = "SHARE_VRT2_WIN_IMAGE_EVT";
    private const string MUTEX_NAME = "SHARE_VRT2_WIN_IMAGE_MTX";
    private const string CALIB_MUTEX_NAME = "SHARE_VRT2_WIN_CALIB_MTX";
    private const int IMAGE_BUFFER_OFFSET = 0x10ba00 + 256;
    private const int PER_CAMERA_BUFFER_STRIDE = 0x200100;
    private const int CALIB_DATA_OFFSET = 0x524;
    public static readonly int BC4_DATA_SIZE = (1024 * 1016) / 2;

    private const string EVF_EVENT_NAME = "SHARE_VRT2_WIN_EVF_EVT";
    private const string EVF_MUTEX_NAME = "SHARE_VRT2_WIN_EVF_MTX";
    private const int EVF_FLAG_OFFSET = 0x110C200;

    private const string COMMON_EVENT_NAME = "SHARE_VRT2_WIN_COMMON_EVT";
    private const string COMMON_MUTEX_NAME = "SHARE_VRT2_WIN_COMMON_MTX";
    private const int COMMON_PLAY_AREA_APP_KEEPALIVE_OFFSET = 0x9DD1;

    private const string PLAYAREA_RESULT_MUTEX_NAME = "SHARE_VRT2_WIN_PLAYAREA_RESULT_MTX";
    private const int PLAYAREA_RESULT_OFFSET = 0x927C;

    private const uint INFINITE = 0xFFFFFFFF;

    private static IntPtr shm = IntPtr.Zero;
    private static IntPtr pBuf = IntPtr.Zero;
    private static IntPtr imageEvent = IntPtr.Zero;
    private static IntPtr imageMutex = IntPtr.Zero;
    private static IntPtr commonEvent = IntPtr.Zero;
    private static IntPtr commonMutex = IntPtr.Zero;
    private static uint lastImageTimestamp = 0;

    private static bool initialized = false;

    public static void Init()
    {
        if (initialized) return;

        try
        {
            shm = CrossIPC.CreateIpcSharedMemory(FILE_MAPPING_NAME, (UIntPtr)SHARED_MEM_SIZE);
            if (shm == IntPtr.Zero) throw new Exception("Could not open file mapping object. Is PSVR2 and SteamVR on?");

            pBuf = CrossIPC.IpcSharedMemory_Map(shm);
            if (pBuf == IntPtr.Zero) throw new Exception("Could not map view of file.");

            imageEvent = CrossIPC.CreateIpcEvent(EVENT_NAME, false);
            imageMutex = CrossIPC.CreateIpcMutex(MUTEX_NAME);

            commonEvent = CrossIPC.CreateIpcEvent(COMMON_EVENT_NAME, false);
            commonMutex = CrossIPC.CreateIpcMutex(COMMON_MUTEX_NAME);

            if (imageEvent == IntPtr.Zero || imageMutex == IntPtr.Zero || commonEvent == IntPtr.Zero || commonMutex == IntPtr.Zero)
            {
                throw new Exception("Failed to open sync objects.");
            }

            initialized = true;

            Debug.Log("PSVR2 Shared Memory Initialized.");
        }
        catch (Exception e)
        {
            Debug.LogError(e.Message);
            Cleanup();
            throw;
        }
    }

    public static void Cleanup()
    {
        initialized = false;

        if (imageEvent != IntPtr.Zero) CrossIPC.DestroyIpcEvent(imageEvent);
        if (imageMutex != IntPtr.Zero) CrossIPC.DestroyIpcMutex(imageMutex);
        if (commonEvent != IntPtr.Zero) CrossIPC.DestroyIpcEvent(commonEvent);
        if (commonMutex != IntPtr.Zero) CrossIPC.DestroyIpcMutex(commonMutex);
        if (shm != IntPtr.Zero)
        {
            if (pBuf != IntPtr.Zero)
            {
                CrossIPC.IpcSharedMemory_Unmap(shm);
            }
            CrossIPC.DestroyIpcSharedMemory(shm);
        }

        shm = pBuf = imageEvent = imageMutex = IntPtr.Zero;
        lastImageTimestamp = 0;
        Debug.Log("PSVR2 Shared Memory Cleaned up.");
    }

    public static bool GetLatestImageBuffer(byte[] leftCameraData, byte[] rightCameraData, out PoseData leftCameraPose)
    {
        if (!initialized) {
            leftCameraPose = new PoseData();
            return false;
        }
        
        // Perform keep-alive to make sure the PSVR2 does not force 3DOF.
        if (commonEvent != IntPtr.Zero && commonMutex != IntPtr.Zero)
        {
            try
            {
                CrossIPC.IpcMutex_Lock(commonMutex);
                try
                {
                    byte val = Marshal.ReadByte(pBuf, COMMON_PLAY_AREA_APP_KEEPALIVE_OFFSET);
                    Marshal.WriteByte(pBuf, COMMON_PLAY_AREA_APP_KEEPALIVE_OFFSET, (byte)(val + 1));
                }
                finally
                {
                    CrossIPC.IpcMutex_Unlock(commonMutex);
                }
                CrossIPC.IpcEvent_Set(commonEvent);
            }
            catch (Exception ex)
            {
                Debug.LogError("Error in common keep-alive: " + ex.Message);
            }
        }

        leftCameraPose = new PoseData { isValid = false };
        
        try
        {
            CrossIPC.IpcMutex_Lock(imageMutex);
        }
        catch (Exception ex)
        {
            Debug.LogError("Failed to lock image mutex: " + ex.Message);
            return false;
        }

        try
        {
            uint latestTimestamp = 0;
            int latestIndex = 0;
            IntPtr basePtr = pBuf;

            // Find latest image frame
            latestTimestamp = (uint)Marshal.ReadInt32(basePtr, 0x3c18);

            if (latestTimestamp <= (uint)Marshal.ReadInt32(basePtr, 0x4490)) { latestIndex = 1; latestTimestamp = (uint)Marshal.ReadInt32(basePtr, 0x4490); }
            if (latestTimestamp <= (uint)Marshal.ReadInt32(basePtr, 0x4d08)) { latestIndex = 2; latestTimestamp = (uint)Marshal.ReadInt32(basePtr, 0x4d08); }
            if (latestTimestamp <= (uint)Marshal.ReadInt32(basePtr, 0x5580)) { latestIndex = 3; latestTimestamp = (uint)Marshal.ReadInt32(basePtr, 0x5580); }
            if (latestTimestamp <= (uint)Marshal.ReadInt32(basePtr, 0x5df8)) { latestIndex = 4; latestTimestamp = (uint)Marshal.ReadInt32(basePtr, 0x5df8); }
            if (latestTimestamp <= (uint)Marshal.ReadInt32(basePtr, 0x6670)) { latestIndex = 5; latestTimestamp = (uint)Marshal.ReadInt32(basePtr, 0x6670); }
            if (latestTimestamp <= (uint)Marshal.ReadInt32(basePtr, 0x6ee8)) { latestIndex = 6; latestTimestamp = (uint)Marshal.ReadInt32(basePtr, 0x6ee8); }
            if (latestTimestamp <= (uint)Marshal.ReadInt32(basePtr, 0x7760)) { latestIndex = 7; latestTimestamp = (uint)Marshal.ReadInt32(basePtr, 0x7760); }

            if (latestTimestamp == lastImageTimestamp)
            {
                return false;
            }

            lastImageTimestamp = latestTimestamp;

            IntPtr dataPtr = new IntPtr(basePtr.ToInt64() + IMAGE_BUFFER_OFFSET + (PER_CAMERA_BUFFER_STRIDE * latestIndex));

            float[] floats = new float[64];

            IntPtr infoPtr = new IntPtr(basePtr.ToInt64() + 0x3c10 + (0x878 * latestIndex));
            Marshal.Copy(infoPtr, floats, 0, 64);

            var poseQuaternion = new Quaternion(floats[3 + 3], floats[4 + 3], floats[5 + 3], floats[6 + 3]);

            // The PSVR2 computes a dynamic rotation for camera 0 based on minimizing SLAM error.
            // We will apply the rotation here since we have access to that data.
            var offsetQuaternion = new Quaternion(floats[3 + 10], floats[4 + 10], floats[5 + 10], floats[6 + 10]);
            var rotatedPosePositionOffset = poseQuaternion * new Vector3(floats[0 + 10], floats[1 + 10], floats[2 + 10]);

            poseQuaternion = poseQuaternion * offsetQuaternion;

            leftCameraPose = new PoseData
            {
                position = new Vector3(floats[0 + 3], floats[1 + 3], floats[2 + 3]) + rotatedPosePositionOffset,
                rotation = poseQuaternion.normalized,
                isValid = true
            };

            Marshal.Copy(dataPtr, leftCameraData, 0, BC4_DATA_SIZE);
            IntPtr rightDataPtr = new IntPtr(dataPtr.ToInt64() + BC4_DATA_SIZE);
            Marshal.Copy(rightDataPtr, rightCameraData, 0, BC4_DATA_SIZE);
        }
        finally
        {
            CrossIPC.IpcMutex_Unlock(imageMutex);
        }
        return true;
    }

    public static bool GetDistortionConfig(int cameraId, out CameraParameters parameters, out CameraIntrinsics intrinsics)
    {
        parameters = new CameraParameters();
        intrinsics = new CameraIntrinsics();
        if (!initialized) {
            return false;
        }
        
        IntPtr hCalibMutex = CrossIPC.CreateIpcMutex(CALIB_MUTEX_NAME);
        if (hCalibMutex == IntPtr.Zero) return false;

        try
        {
            CrossIPC.IpcMutex_Lock(hCalibMutex);
        }
        catch (Exception ex)
        {
            Debug.LogError("Failed to lock calibration mutex: " + ex.Message);
            CrossIPC.DestroyIpcMutex(hCalibMutex);
            return false;
        }

        bool found = false;
        try
        {
            IntPtr configBasePtr = new IntPtr(pBuf.ToInt64() + CALIB_DATA_OFFSET);
            int configStructSize = Marshal.SizeOf(typeof(CameraConfig));

            for (int i = 0; i < 4; i++)
            {
                IntPtr configPtr = new IntPtr(configBasePtr.ToInt64() + (i * configStructSize));
                CameraConfig config = (CameraConfig)Marshal.PtrToStructure(configPtr, typeof(CameraConfig));

                if (config.camId == cameraId)
                {
                    parameters.coeffs = config.coff;
                    intrinsics.fx = config.pxMat[0];
                    intrinsics.fy = config.pxMat[4];
                    intrinsics.cx = config.pxMat[2];
                    intrinsics.cy = config.pxMat[5];
                    found = true;
                    break;
                }
            }
        }
        finally
        {
            try
            {
                CrossIPC.IpcMutex_Unlock(hCalibMutex);
            }
            catch {}
            CrossIPC.DestroyIpcMutex(hCalibMutex);
        }

        return found;
    }

    public static RelativeTransform ConvertToRelativeTransform(CameraTransform camT)
    {
        Matrix4x4 m = Matrix4x4.identity;
        
        m.m00 = camT.mat[0]; m.m01 = camT.mat[1]; m.m02 = camT.mat[2];
        m.m10 = camT.mat[3]; m.m11 = camT.mat[4]; m.m12 = camT.mat[5];
        m.m20 = camT.mat[6]; m.m21 = camT.mat[7]; m.m22 = camT.mat[8];
    
        m.m03 = camT.pos[0];
        m.m13 = camT.pos[1];
        m.m23 = camT.pos[2];
    
        return new RelativeTransform
        {
            FromId = camT.fromCamId,
            ToId = camT.toCamId,
            T = m
        };
    }
    

    public static RelativeTransform[] GetCameraRelativeTransforms()
    {
        if (!initialized) return null;
        
        var transforms = new RelativeTransform[3];

        IntPtr hCalibMutex = CrossIPC.CreateIpcMutex(CALIB_MUTEX_NAME);
        if (hCalibMutex == IntPtr.Zero) return null;

        try
        {
            CrossIPC.IpcMutex_Lock(hCalibMutex);
        }
        catch (Exception ex)
        {
            Debug.LogError("Failed to lock calibration mutex for relative transforms: " + ex.Message);
            CrossIPC.DestroyIpcMutex(hCalibMutex);
            return null;
        }

        try
        {
            IntPtr configBasePtr = new IntPtr(pBuf.ToInt64() + CALIB_DATA_OFFSET);
            int configStructSize = Marshal.SizeOf(typeof(CameraConfig));
            IntPtr configTransformsBasePtr = configBasePtr + (4 * configStructSize);
            for (int i = 0; i < 3; i++)
            {
                IntPtr transformPtr = new IntPtr(configTransformsBasePtr.ToInt64() + (i * Marshal.SizeOf(typeof(CameraTransform))));
                CameraTransform transform = (CameraTransform)Marshal.PtrToStructure(transformPtr, typeof(CameraTransform));

                transforms[i] = ConvertToRelativeTransform(transform);
            }
        }
        finally
        {
            try
            {
                CrossIPC.IpcMutex_Unlock(hCalibMutex);
            }
            catch {}
            CrossIPC.DestroyIpcMutex(hCalibMutex);
        }

        return transforms;
    }

    public static Dictionary<int, Matrix4x4> ComputeAbsolutePoses(
        List<RelativeTransform> relativeTransforms,
        Vector3 rootPosition, 
        Quaternion rootRotation)
    {
        int rootId = 0;

        // Create the matrix for Cam 0 using the passed-in position and rotation
        Matrix4x4 pose0 = Matrix4x4.TRS(rootPosition, rootRotation, Vector3.one);
        
        Dictionary<int, Matrix4x4> poses = new Dictionary<int, Matrix4x4>
        {
            { rootId, pose0 }
        };
        
        Queue<int> queue = new Queue<int>();
        queue.Enqueue(rootId);
        
        // BFS to resolve the Kinematic Tree into global space
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            
            foreach (var rel in relativeTransforms)
            {
                // If the transform defines 'from' relative to 'current' (to)
                if (rel.ToId == current && !poses.ContainsKey(rel.FromId))
                {
                    poses[rel.FromId] = poses[current] * rel.T;
                    queue.Enqueue(rel.FromId);
                }
                // If the transform defines 'current' (from) relative to 'to'
                // We must invert the matrix to traverse backward up the tree
                else if (rel.FromId == current && !poses.ContainsKey(rel.ToId))
                {
                    poses[rel.ToId] = poses[current] * rel.T.inverse;
                    queue.Enqueue(rel.ToId);
                }
            }
        }

        return poses;
    }

    public static bool TriggerEVFWorker(long flags)
    {
        if (!initialized) return false;

        IntPtr hEvfEvent = CrossIPC.CreateIpcEvent(EVF_EVENT_NAME, false);
        IntPtr hEvfMutex = CrossIPC.CreateIpcMutex(EVF_MUTEX_NAME);

        if (hEvfEvent == IntPtr.Zero || hEvfMutex == IntPtr.Zero)
        {
            if (hEvfEvent != IntPtr.Zero) CrossIPC.DestroyIpcEvent(hEvfEvent);
            if (hEvfMutex != IntPtr.Zero) CrossIPC.DestroyIpcMutex(hEvfMutex);
            Debug.LogError("Failed to open EVF sync objects.");
            return false;
        }

        try
        {
            CrossIPC.IpcMutex_Lock(hEvfMutex);
            try
            {
                Marshal.WriteInt64(pBuf, EVF_FLAG_OFFSET, flags);
            }
            finally
            {
                CrossIPC.IpcMutex_Unlock(hEvfMutex);
            }

            CrossIPC.IpcEvent_Set(hEvfEvent);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("Failed to trigger EVF worker: " + ex.Message);
            return false;
        }
        finally
        {
            CrossIPC.DestroyIpcEvent(hEvfEvent);
            CrossIPC.DestroyIpcMutex(hEvfMutex);
        }
    }

    public static PlayArea GetPlayArea()
    {
        PlayArea playArea = new PlayArea();

        if (!initialized) return playArea;

        IntPtr hPlayAreaMutex = CrossIPC.CreateIpcMutex(PLAYAREA_RESULT_MUTEX_NAME);
        if (hPlayAreaMutex == IntPtr.Zero)
        {
            Debug.LogError("Failed to open PlayArea mutex.");
            return playArea;
        }

        try
        {
            CrossIPC.IpcMutex_Lock(hPlayAreaMutex);
        }
        catch (Exception ex)
        {
            Debug.LogError("Failed to acquire PlayArea mutex: " + ex.Message);
            CrossIPC.DestroyIpcMutex(hPlayAreaMutex);
            return playArea;
        }

        try
        {
            IntPtr playAreaPtr = new IntPtr(pBuf.ToInt64() + PLAYAREA_RESULT_OFFSET);
            playArea = (PlayArea)Marshal.PtrToStructure(playAreaPtr, typeof(PlayArea));
        }
        finally
        {
            try
            {
                CrossIPC.IpcMutex_Unlock(hPlayAreaMutex);
            }
            catch {}
            CrossIPC.DestroyIpcMutex(hPlayAreaMutex);
        }
        return playArea;
    }

    public static void SetPlayArea(PlayArea playArea)
    {
        if (!initialized) return;
        
        IntPtr hPlayAreaMutex = CrossIPC.CreateIpcMutex(PLAYAREA_RESULT_MUTEX_NAME);
        if (hPlayAreaMutex == IntPtr.Zero)
        {
            Debug.LogError("Failed to open PlayArea mutex for writing.");
            return;
        }

        try
        {
            CrossIPC.IpcMutex_Lock(hPlayAreaMutex);
        }
        catch (Exception ex)
        {
            Debug.LogError("Failed to acquire PlayArea mutex for writing: " + ex.Message);
            CrossIPC.DestroyIpcMutex(hPlayAreaMutex);
            return;
        }

        try
        {
            IntPtr playAreaPtr = new IntPtr(pBuf.ToInt64() + PLAYAREA_RESULT_OFFSET);
            Marshal.StructureToPtr(playArea, playAreaPtr, false);
        }
        finally
        {
            try
            {
                CrossIPC.IpcMutex_Unlock(hPlayAreaMutex);
            }
            catch {}
            CrossIPC.DestroyIpcMutex(hPlayAreaMutex);
        }

        TriggerEVFWorker(0x40);
        Debug.Log("PlayArea data written to shared memory.");
    }

    public static void ClearMap()
    {
        TriggerEVFWorker(0x20);
    }
}