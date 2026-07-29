using UnityEngine;
using PSVR2Toolkit;
using TMPro;

public class PositionGuideController : MonoBehaviour
{
    [Header("Eye Visual References")]
    [SerializeField] private Transform leftEyeVisual;
    [SerializeField] private Transform rightEyeVisual;

    [Header("Lens Visual References")]
    [SerializeField] private Transform leftLensVisual;
    [SerializeField] private Transform rightLensVisual;

    [Header("Layout & Scale Settings")]
    [SerializeField] private float mmToLocalUnits = 10.0f;
    [SerializeField] private float lerpSpeed = 12.0f;

    [Header("Blink Animation Settings")]
    [Tooltip("Target vertical scale (Y) when the eye is closed/blinking.")]
    [SerializeField] private float closedEyeScaleY = 0.05f;
    [Tooltip("Multiplier for how fast the eye closes/opens during a blink.")]
    [SerializeField] private float blinkSpeedMultiplier = 2.0f;

    [SerializeField] private TextMeshProUGUI ipdText;

    private hmd2_gaze_status_t gazeStatus;
    
    private Vector3 leftGazeOriginMM;
    private Vector3 rightGazeOriginMM;
    private Vector3 leftLensOriginMM;
    private Vector3 rightLensOriginMM;
    private bool isEyeTrackingValid;

    private bool isLeftBlinking;
    private bool isRightBlinking;

    private Vector3 targetLeftEyePos;
    private Vector3 targetRightEyePos;
    private Vector3 targetLeftLensPos;
    private Vector3 targetRightLensPos;
    private Vector3 targetLensOffset;
    private Quaternion targetLensRotation = Quaternion.identity;

    private void Start()
    {
        try
        {
            PSVR2ToolkitCAPI.Init();
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[PositionGuideController] Failed to initialize PSVR2 Toolkit CAPI: {ex.Message}");
        }
    }

    private void Update()
    {
        PollPSVR2GazeData();
        CalculateGuideLayout();
        ApplyVisualTransforms();
    }

    private void PollPSVR2GazeData()
    {
        try
        {
            if (PSVR2ToolkitCAPI.GetGazeStatus(ref gazeStatus, 0))
            {
                var leftEye = gazeStatus.wearable.left;
                var rightEye = gazeStatus.wearable.right;

                bool leftValid = leftEye.is_gaze_origin_valid == hmd2_gaze_bool_t.HMD2_GAZE_BOOL_TRUE;
                bool rightValid = rightEye.is_gaze_origin_valid == hmd2_gaze_bool_t.HMD2_GAZE_BOOL_TRUE;

                isEyeTrackingValid = leftValid && rightValid;

                if (isEyeTrackingValid)
                {
                    leftGazeOriginMM = new Vector3(
                        leftEye.gaze_origin_mm.x,
                        leftEye.gaze_origin_mm.y,
                        0.0f
                    );

                    rightGazeOriginMM = new Vector3(
                        rightEye.gaze_origin_mm.x,
                        rightEye.gaze_origin_mm.y,
                        0.0f
                    );

                    leftLensOriginMM = new Vector3(
                        gazeStatus.lens_config.left.x,
                        gazeStatus.lens_config.left.y,
                        gazeStatus.lens_config.left.z
                    );

                    rightLensOriginMM = new Vector3(
                        gazeStatus.lens_config.right.x,
                        gazeStatus.lens_config.right.y,
                        gazeStatus.lens_config.right.z
                    );
                }

                isLeftBlinking = (leftEye.is_blink_valid == hmd2_gaze_bool_t.HMD2_GAZE_BOOL_TRUE) &&
                                     (leftEye.blink == hmd2_gaze_bool_t.HMD2_GAZE_BOOL_TRUE);

                isRightBlinking = (rightEye.is_blink_valid == hmd2_gaze_bool_t.HMD2_GAZE_BOOL_TRUE) &&
                                    (rightEye.blink == hmd2_gaze_bool_t.HMD2_GAZE_BOOL_TRUE);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"CAPI Gaze Query Failed: {ex.Message}");
            isEyeTrackingValid = false;
            isLeftBlinking = false;
            isRightBlinking = false;
        }
    }

    private void CalculateGuideLayout()
    {
        if (!isEyeTrackingValid) return;

        float measuredIPDMM = Vector3.Distance(leftGazeOriginMM, rightGazeOriginMM);
        float measuredLensIPDMM = Vector3.Distance(leftLensOriginMM, rightLensOriginMM);

        ipdText.text = $"Your IPD is {measuredIPDMM:F1} mm. Adjust the lenses using the IPD wheel.";

        Vector3 centerGazeOriginMM = (leftGazeOriginMM + rightGazeOriginMM) * 0.5f;

        float halfSeparation = (measuredIPDMM * 0.5f) * mmToLocalUnits;
        float halfLensSeparation = (measuredLensIPDMM * 0.5f) * mmToLocalUnits;

        targetLeftEyePos = new Vector3(-halfSeparation, 0.0f, 0.0f);
        targetRightEyePos = new Vector3(halfSeparation, 0.0f, 0.0f);

        targetLeftLensPos = new Vector3(-halfLensSeparation, 0.0f, 0.0f);
        targetRightLensPos = new Vector3(halfLensSeparation, 0.0f, 0.0f);

        targetLensOffset = new Vector3(
            -centerGazeOriginMM.x * mmToLocalUnits,
            -centerGazeOriginMM.y * mmToLocalUnits,
            0.0f
        );

        Vector3 eyeDelta = rightGazeOriginMM - leftGazeOriginMM;
        float rollAngleDegrees = Mathf.Atan2(eyeDelta.y, eyeDelta.x) * Mathf.Rad2Deg;

        targetLensRotation = Quaternion.Euler(0.0f, 0.0f, -rollAngleDegrees);
    }

    private void ApplyVisualTransforms()
    {
        float step = Time.deltaTime * lerpSpeed;
        float blinkStep = step * blinkSpeedMultiplier;

        Vector3 targetLeftScale = new Vector3(1.0f, isLeftBlinking ? closedEyeScaleY : 1.0f, 1.0f);
        Vector3 targetRightScale = new Vector3(1.0f, isRightBlinking ? closedEyeScaleY : 1.0f, 1.0f);

        if (leftEyeVisual != null)
        {
            leftEyeVisual.localPosition = Vector3.Lerp(leftEyeVisual.localPosition, targetLeftEyePos, step);
            leftEyeVisual.localScale = Vector3.Lerp(leftEyeVisual.localScale, targetLeftScale, blinkStep);
        }

        if (rightEyeVisual != null)
        {
            rightEyeVisual.localPosition = Vector3.Lerp(rightEyeVisual.localPosition, targetRightEyePos, step);
            rightEyeVisual.localScale = Vector3.Lerp(rightEyeVisual.localScale, targetRightScale, blinkStep);
        }

        if (leftLensVisual != null)
        {
            Vector3 rawLeftTarget = targetLeftLensPos + targetLensOffset;
            Vector3 rotatedLeftTarget = targetLensRotation * rawLeftTarget;

            leftLensVisual.localPosition = Vector3.Lerp(leftLensVisual.localPosition, rotatedLeftTarget, step);
            leftLensVisual.localRotation = Quaternion.Lerp(leftLensVisual.localRotation, targetLensRotation, step);
        }

        if (rightLensVisual != null)
        {
            Vector3 rawRightTarget = targetRightLensPos + targetLensOffset;
            Vector3 rotatedRightTarget = targetLensRotation * rawRightTarget;

            rightLensVisual.localPosition = Vector3.Lerp(rightLensVisual.localPosition, rotatedRightTarget, step);
            rightLensVisual.localRotation = Quaternion.Lerp(rightLensVisual.localRotation, targetLensRotation, step);
        }
    }
}