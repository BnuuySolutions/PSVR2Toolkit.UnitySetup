using System;
using System.Runtime.InteropServices;

public static class CrossIPC
{
    private const string LIB_NAME = "libcrossipc";

    // Mutex API
    [DllImport(LIB_NAME, EntryPoint = "CreateIpcMutex", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr CreateIpcMutex(string name);

    [DllImport(LIB_NAME, EntryPoint = "DestroyIpcMutex", CallingConvention = CallingConvention.Cdecl)]
    public static extern void DestroyIpcMutex(IntPtr mutex);

    [DllImport(LIB_NAME, EntryPoint = "IpcMutex_Lock", CallingConvention = CallingConvention.Cdecl)]
    public static extern void IpcMutex_Lock(IntPtr mutex);

    [DllImport(LIB_NAME, EntryPoint = "IpcMutex_TryLock", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IpcMutex_TryLock(IntPtr mutex);

    [DllImport(LIB_NAME, EntryPoint = "IpcMutex_Unlock", CallingConvention = CallingConvention.Cdecl)]
    public static extern void IpcMutex_Unlock(IntPtr mutex);

    [DllImport(LIB_NAME, EntryPoint = "IpcMutex_GetNativeHandle", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr IpcMutex_GetNativeHandle(IntPtr mutex);

    // Event API
    [DllImport(LIB_NAME, EntryPoint = "CreateIpcEvent", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr CreateIpcEvent(string name, [MarshalAs(UnmanagedType.I1)] bool manualReset);

    [DllImport(LIB_NAME, EntryPoint = "DestroyIpcEvent", CallingConvention = CallingConvention.Cdecl)]
    public static extern void DestroyIpcEvent(IntPtr ev);

    [DllImport(LIB_NAME, EntryPoint = "IpcEvent_Set", CallingConvention = CallingConvention.Cdecl)]
    public static extern void IpcEvent_Set(IntPtr ev);

    [DllImport(LIB_NAME, EntryPoint = "IpcEvent_Wait", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IpcEvent_Wait(IntPtr ev, uint timeoutMs);

    [DllImport(LIB_NAME, EntryPoint = "IpcEvent_Reset", CallingConvention = CallingConvention.Cdecl)]
    public static extern void IpcEvent_Reset(IntPtr ev);

    [DllImport(LIB_NAME, EntryPoint = "IpcEvent_GetNativeHandle", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr IpcEvent_GetNativeHandle(IntPtr ev);

    // Shared Memory API
    [DllImport(LIB_NAME, EntryPoint = "CreateIpcSharedMemory", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr CreateIpcSharedMemory(string name, UIntPtr size);

    [DllImport(LIB_NAME, EntryPoint = "DestroyIpcSharedMemory", CallingConvention = CallingConvention.Cdecl)]
    public static extern void DestroyIpcSharedMemory(IntPtr shm);

    [DllImport(LIB_NAME, EntryPoint = "IpcSharedMemory_Map", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr IpcSharedMemory_Map(IntPtr shm);

    [DllImport(LIB_NAME, EntryPoint = "IpcSharedMemory_Unmap", CallingConvention = CallingConvention.Cdecl)]
    public static extern void IpcSharedMemory_Unmap(IntPtr shm);

    [DllImport(LIB_NAME, EntryPoint = "IpcSharedMemory_GetNativeHandle", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr IpcSharedMemory_GetNativeHandle(IntPtr shm);

    // Broadcast API
    [DllImport(LIB_NAME, EntryPoint = "CreateIpcBroadcast", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr CreateIpcBroadcast(string name);

    [DllImport(LIB_NAME, EntryPoint = "DestroyIpcBroadcast", CallingConvention = CallingConvention.Cdecl)]
    public static extern void DestroyIpcBroadcast(IntPtr broadcast);

    [DllImport(LIB_NAME, EntryPoint = "IpcBroadcast_Wait", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IpcBroadcast_Wait(IntPtr broadcast, uint timeoutMs);

    [DllImport(LIB_NAME, EntryPoint = "IpcBroadcast_NotifyAll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void IpcBroadcast_NotifyAll(IntPtr broadcast);
}
