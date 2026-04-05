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

    // Win32 Imports
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr OpenFileMappingA(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr OpenEventA(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr OpenMutexA(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll")]
    private static extern bool ReleaseMutex(IntPtr hMutex);

    [DllImport("kernel32.dll")]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint FILE_MAP_READ = 0x0004;
    private const uint FILE_MAP_WRITE = 0x0002;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint EVENT_MODIFY_STATE = 0x0002;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint INFINITE = 0xFFFFFFFF;

    private static IntPtr hMapFile = IntPtr.Zero;
    private static IntPtr pBuf = IntPtr.Zero;
    private static IntPtr hImageEvent = IntPtr.Zero;
    private static IntPtr hImageMutex = IntPtr.Zero;

    public static void Init()
    {
        try
        {
            hMapFile = OpenFileMappingA(FILE_MAP_WRITE | FILE_MAP_READ, false, FILE_MAPPING_NAME);
            if (hMapFile == IntPtr.Zero) throw new Exception("Could not open file mapping object. Is PSVR2 and SteamVR on?");

            pBuf = MapViewOfFile(hMapFile, FILE_MAP_WRITE | FILE_MAP_READ, 0, 0, (UIntPtr)SHARED_MEM_SIZE);
            if (pBuf == IntPtr.Zero) throw new Exception("Could not map view of file.");

            hImageEvent = OpenEventA(SYNCHRONIZE, false, EVENT_NAME);
            hImageMutex = OpenMutexA(SYNCHRONIZE, false, MUTEX_NAME);

            if (hImageEvent == IntPtr.Zero || hImageMutex == IntPtr.Zero)
            {
                throw new Exception("Failed to open sync objects.");
            }
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
        if (hImageEvent != IntPtr.Zero) CloseHandle(hImageEvent);
        if (hImageMutex != IntPtr.Zero) CloseHandle(hImageMutex);
        if (pBuf != IntPtr.Zero) UnmapViewOfFile(pBuf);
        if (hMapFile != IntPtr.Zero) CloseHandle(hMapFile);

        hMapFile = pBuf = hImageEvent = hImageMutex = IntPtr.Zero;
        Debug.Log("PSVR2 Shared Memory Cleaned up.");
    }

    public static bool GetLatestImageBuffer(byte[] leftCameraData, byte[] rightCameraData, out PoseData leftCameraPose)
    {
        // Perform keep-alive to make sure the PSVR2 does not force 3DOF.
        IntPtr hCommonEvent = OpenEventA(EVENT_MODIFY_STATE, false, COMMON_EVENT_NAME);
        IntPtr hCommonMutex = OpenMutexA(SYNCHRONIZE, false, COMMON_MUTEX_NAME);

        if (hCommonEvent != IntPtr.Zero && hCommonMutex != IntPtr.Zero)
        {
            if (WaitForSingleObject(hCommonMutex, INFINITE) == WAIT_OBJECT_0)
            {
                try
                {
                    byte val = Marshal.ReadByte(pBuf, COMMON_PLAY_AREA_APP_KEEPALIVE_OFFSET);
                    Marshal.WriteByte(pBuf, COMMON_PLAY_AREA_APP_KEEPALIVE_OFFSET, (byte)(val + 1));
                }
                finally
                {
                    ReleaseMutex(hCommonMutex);
                }
                SetEvent(hCommonEvent);
            }
            CloseHandle(hCommonEvent);
            CloseHandle(hCommonMutex);
        }
        

        leftCameraPose = new PoseData { isValid = false };

        if (WaitForSingleObject(hImageEvent, INFINITE) != WAIT_OBJECT_0) return false;
        if (WaitForSingleObject(hImageMutex, INFINITE) != WAIT_OBJECT_0) return false;

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
            if (latestTimestamp <= (uint)Marshal.ReadInt32(basePtr, 0x7760)) { latestIndex = 7; }

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
            ReleaseMutex(hImageMutex);
        }
        return true;
    }

    public static bool GetDistortionConfig(int cameraId, out CameraParameters parameters, out CameraIntrinsics intrinsics)
    {
        parameters = new CameraParameters();
        intrinsics = new CameraIntrinsics();
        IntPtr hCalibMutex = OpenMutexA(SYNCHRONIZE, false, CALIB_MUTEX_NAME);
        if (hCalibMutex == IntPtr.Zero) return false;

        if (WaitForSingleObject(hCalibMutex, INFINITE) != WAIT_OBJECT_0)
        {
            CloseHandle(hCalibMutex);
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
            ReleaseMutex(hCalibMutex);
            CloseHandle(hCalibMutex);
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
        var transforms = new RelativeTransform[3];

        IntPtr hCalibMutex = OpenMutexA(SYNCHRONIZE, false, CALIB_MUTEX_NAME);
        if (hCalibMutex == IntPtr.Zero) return null;

        if (WaitForSingleObject(hCalibMutex, INFINITE) != WAIT_OBJECT_0)
        {
            CloseHandle(hCalibMutex);
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
            ReleaseMutex(hCalibMutex);
            CloseHandle(hCalibMutex);
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
        if (pBuf == IntPtr.Zero) return false;

        // We need EVENT_MODIFY_STATE to call SetEvent
        IntPtr hEvfEvent = OpenEventA(EVENT_MODIFY_STATE, false, EVF_EVENT_NAME);
        IntPtr hEvfMutex = OpenMutexA(SYNCHRONIZE, false, EVF_MUTEX_NAME);

        if (hEvfEvent == IntPtr.Zero || hEvfMutex == IntPtr.Zero)
        {
            if (hEvfEvent != IntPtr.Zero) CloseHandle(hEvfEvent);
            if (hEvfMutex != IntPtr.Zero) CloseHandle(hEvfMutex);
            Debug.LogError("Failed to open EVF sync objects.");
            return false;
        }

        try
        {
            if (WaitForSingleObject(hEvfMutex, INFINITE) == WAIT_OBJECT_0)
            {
                try
                {
                    Marshal.WriteInt64(pBuf, EVF_FLAG_OFFSET, flags);
                }
                finally
                {
                    ReleaseMutex(hEvfMutex);
                }

                SetEvent(hEvfEvent);
                return true;
            }
            else
            {
                Debug.LogError("Failed to acquire EVF mutex.");
                return false;
            }
        }
        finally
        {
            // Cleanup local handles
            CloseHandle(hEvfEvent);
            CloseHandle(hEvfMutex);
        }
    }

    public static PlayArea GetPlayArea()
    {
        PlayArea playArea = new PlayArea();
        IntPtr hPlayAreaMutex = OpenMutexA(SYNCHRONIZE, false, PLAYAREA_RESULT_MUTEX_NAME);
        if (hPlayAreaMutex == IntPtr.Zero)
        {
            Debug.LogError("Failed to open PlayArea mutex.");
            return playArea;
        }

        if (WaitForSingleObject(hPlayAreaMutex, INFINITE) != WAIT_OBJECT_0)
        {
            Debug.LogError("Failed to acquire PlayArea mutex.");
            CloseHandle(hPlayAreaMutex);
            return playArea;
        }

        try
        {
            IntPtr playAreaPtr = new IntPtr(pBuf.ToInt64() + PLAYAREA_RESULT_OFFSET);
            playArea = (PlayArea)Marshal.PtrToStructure(playAreaPtr, typeof(PlayArea));
        }
        finally
        {
            ReleaseMutex(hPlayAreaMutex);
            CloseHandle(hPlayAreaMutex);
        }
        return playArea;
    }

    public static void SetPlayArea(PlayArea playArea)
    {
        IntPtr hPlayAreaMutex = OpenMutexA(SYNCHRONIZE, false, PLAYAREA_RESULT_MUTEX_NAME);
        if (hPlayAreaMutex == IntPtr.Zero)
        {
            Debug.LogError("Failed to open PlayArea mutex for writing.");
            return;
        }

        if (WaitForSingleObject(hPlayAreaMutex, INFINITE) != WAIT_OBJECT_0)
        {
            Debug.LogError("Failed to acquire PlayArea mutex for writing.");
            CloseHandle(hPlayAreaMutex);
            return;
        }

        try
        {
            IntPtr playAreaPtr = new IntPtr(pBuf.ToInt64() + PLAYAREA_RESULT_OFFSET);
            Marshal.StructureToPtr(playArea, playAreaPtr, false);
        }
        finally
        {
            ReleaseMutex(hPlayAreaMutex);
            CloseHandle(hPlayAreaMutex);
        }

        TriggerEVFWorker(0x40);
        Debug.Log("PlayArea data written to shared memory.");
    }

    public static void ClearMap()
    {
        TriggerEVFWorker(0x20);
    }
}