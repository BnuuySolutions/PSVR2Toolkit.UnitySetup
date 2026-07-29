using UnityEngine;

public class UIPosition : MonoBehaviour
{
    public enum Mode
    {
        WorldFloat,
        HeadLocked
    }

    [Header("Settings")]
    public Transform head;
    public Mode mode = Mode.WorldFloat;
    public float distance = 2.0f;

    [Header("Smoothing Speeds")]
    [Tooltip("Smoothing speed when floating in the world (lower is smoother/slower).")]
    public float worldFloatSpeed = 2.0f;

    [Tooltip("Smoothing speed when locked in front of the head (higher is tighter lock with small smoothing).")]
    public float headLockedSpeed = 25.0f;

    [Header("Head Locked Options")]
    [Tooltip("If true, head locked mode will also follow head pitch (looking up/down).")]
    public bool lockPitch = true;

    void Update()
    {
        if (head == null) return;

        if (mode == Mode.HeadLocked)
        {
            Vector3 forward = lockPitch ? head.forward : Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (forward == Vector3.zero) forward = transform.forward;

            Vector3 targetPosition = head.position + forward * distance;
            Vector3 dirToHead = head.position - targetPosition;

            Quaternion targetRotation;
            if (lockPitch && dirToHead != Vector3.zero)
            {
                targetRotation = Quaternion.LookRotation(dirToHead, head.up);
            }
            else
            {
                Vector3 lookDir = head.position - transform.position;
                lookDir.y = 0;
                targetRotation = lookDir != Vector3.zero ? Quaternion.LookRotation(lookDir) : transform.rotation;
            }

            transform.position = Vector3.Lerp(transform.position, targetPosition, Time.deltaTime * headLockedSpeed);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * headLockedSpeed);
        }
        else // WorldFloat
        {
            Vector3 forwardNoY = head.forward;
            forwardNoY.y = 0;
            forwardNoY.Normalize();
            if (forwardNoY == Vector3.zero) forwardNoY = head.forward;

            Vector3 targetPosition = head.position + forwardNoY * distance;
            transform.position = Vector3.Lerp(transform.position, targetPosition, Time.deltaTime * worldFloatSpeed);
            transform.LookAt(head.position);
            transform.rotation = Quaternion.Euler(0, transform.rotation.eulerAngles.y, 0);
        }
    }

    public void SetHeadLockedMode()
    {
        mode = Mode.HeadLocked;
    }

    public void SetWorldFloatMode()
    {
        mode = Mode.WorldFloat;
    }
}
