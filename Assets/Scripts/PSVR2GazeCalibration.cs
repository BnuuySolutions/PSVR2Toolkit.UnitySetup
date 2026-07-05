using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using PSVR2Toolkit;

/// <summary>
/// Drives the PSVR2 hardware eye-tracking calibration sequence.
/// </summary>
public class PSVR2GazeCalibration : MonoBehaviour
{
    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("Eye Selection")]
    public hmd2_gaze_enabled_eye_t enabledEye = hmd2_gaze_enabled_eye_t.HMD2_GAZE_ENABLED_EYE_BOTH;

    [Header("Calibration Points")]
    public Vector3[] calibrationPoints = new Vector3[]
    {
        // Dark Stage
        new Vector3(   0.0f,    0.0f, 2000.0f),
        new Vector3(-400.0f,  400.0f, 2000.0f),
        new Vector3( 400.0f, -400.0f, 2000.0f),
        new Vector3( 400.0f,  400.0f, 2000.0f),
        new Vector3(-400.0f, -400.0f, 2000.0f),
        new Vector3(   0.0f,    0.0f, 300.0f),
        new Vector3(-100.0f,    0.0f, 300.0f),
        new Vector3( 100.0f,    0.0f, 300.0f),
        // Light Stage
        new Vector3(   0.0f,     0.0f, 2000.0f),
        new Vector3(-1000.0f,    0.0f, 2000.0f),
        new Vector3( 1000.0f,    0.0f, 2000.0f),
        new Vector3(   0.0f,  1000.0f, 2000.0f),
        new Vector3(   0.0f, -1000.0f, 2000.0f)
    };

    public int switchToLightIndex = 8;

    [Header("Polling & Timing")]
    public float pointSettleDelay = 1.0f;

    [Range(0.5f, 10.0f)]
    public float switchToLightDelay = 2.0f;

    [Range(0.01f, 0.5f)]
    public float pointPollInterval = 0.05f;

    [Range(1f, 30f)]
    public float pointTimeout = 10f;

    [Range(1f, 30f)]
    public float computeTimeout = 10f;

    public UnityEvent OnCalibrationStarted;
    public UnityEvent<Vector3> OnShowPoint;
    public UnityEvent<int> OnPointCollected;
    public UnityEvent OnDarkStageComplete;
    public UnityEvent OnCalibrationSucceeded;
    public UnityEvent OnCalibrationFailed;
    public UnityEvent OnCalibrationStopped;

    public bool IsCalibrating { get; private set; }
    public int CurrentPointIndex { get; private set; } = -1;

    private Coroutine _sessionCoroutine;

    private void Awake()
    {
        try
        {
            int initResult = PSVR2ToolkitCAPI.Init();
            if (initResult < 0)
            {
                Debug.LogError($"[PSVR2GazeCalibration] CAPI Init failed with code {initResult}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PSVR2GazeCalibration] Failed to load or initialize CAPI: {ex}");
        }
    }

    private void OnDisable()
    {
        if (IsCalibrating)
            AbortCalibration();
    }

    public void BeginCalibration()
    {
        if (IsCalibrating)
        {
            Debug.LogWarning("[PSVR2GazeCalibration] BeginCalibration called while already calibrating.");
            return;
        }

        if (calibrationPoints == null || calibrationPoints.Length == 0)
        {
            Debug.LogError("[PSVR2GazeCalibration] calibrationPoints array is empty. Calibration aborted.");
            return;
        }

        _sessionCoroutine = StartCoroutine(CalibrationSessionCoroutine());
    }

    public void AbortCalibration()
    {
        if (!IsCalibrating) return;

        if (_sessionCoroutine != null)
        {
            StopCoroutine(_sessionCoroutine);
            _sessionCoroutine = null;
        }

        FinishCalibrationSequence(false);
    }

    private IEnumerator CalibrationSessionCoroutine()
    {
        bool startOk = false;
        try
        {
            PSVR2ToolkitCAPI.SendGazeSetCommand(new GazeCalibrationCommand {
                reportMode = GazeCalibrationReportMode.SetEnabledEye,
                payload = new GazeCalibrationPacket { eyeEnabled = enabledEye }
            });

            var startResp = PSVR2ToolkitCAPI.SendGazeSetCommand(new GazeCalibrationCommand {
                reportMode = GazeCalibrationReportMode.StartCalibration,
                payload = new GazeCalibrationPacket { result = GazeCalibrationResult.Success }
            });

            Debug.Log($"[PSVR2GazeCalibration] StartCalibration → status={startResp.status}");
            startOk = true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PSVR2GazeCalibration] StartCalibration failed: {ex}");
        }

        if (!startOk)
        {
            OnCalibrationFailed?.Invoke();
            OnCalibrationStopped?.Invoke();
            yield break;
        }

        IsCalibrating = true;
        CurrentPointIndex = -1;
        OnCalibrationStarted?.Invoke();

        bool overallSuccess = true;

        for (int i = 0; i < calibrationPoints.Length; i++)
        {
            CurrentPointIndex = i;
            Vector3 pt = calibrationPoints[i];
            OnShowPoint?.Invoke(new Vector3(-pt.x/1000.0f, pt.y/1000.0f, pt.z/1000.0f));

            // Give time for user to look at the dot
            yield return new WaitForSeconds(pointSettleDelay);

            try
            {
                PSVR2ToolkitCAPI.SendGazeSetCommand(new GazeCalibrationCommand {
                    reportMode = GazeCalibrationReportMode.CollectCalibrationPoint,
                    payload = new GazeCalibrationPacket { 
                        x = pt.x, 
                        y = pt.y, 
                        z = pt.z, 
                        result = GazeCalibrationResult.Success 
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PSVR2GazeCalibration] CollectCalibrationPoint failed: {ex}");
                overallSuccess = false;
                break;
            }

            bool pointOk = false;
            float elapsed = 0f;
            while (elapsed < pointTimeout)
            {
                yield return new WaitForSeconds(pointPollInterval);
                elapsed += pointPollInterval;

                GazeCalibrationResult pollResult = GazeCalibrationResult.Waiting;
                try
                {
                    var pollResp = PSVR2ToolkitCAPI.SendGazeGetCommand(new GazeCalibrationCommand {
                        reportMode = GazeCalibrationReportMode.CollectCalibrationPoint,
                        payload = new GazeCalibrationPacket { result = GazeCalibrationResult.Success }
                    });
                    
                    pollResult = pollResp.payload.result;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[PSVR2GazeCalibration] Collect poll failed: {ex}");
                }

                if (pollResult == GazeCalibrationResult.Success)
                {
                    pointOk = true;
                    break;
                }
            }

            if (!pointOk)
            {
                Debug.LogWarning($"[PSVR2GazeCalibration] Point {i} timed out after {pointTimeout}s.");
                overallSuccess = false;
                break;
            }

            OnPointCollected?.Invoke(i);

            bool isDarkStageComplete = i == switchToLightIndex - 1;
            bool isLightStageComplete = i == calibrationPoints.Length - 1;

            if (isDarkStageComplete || isLightStageComplete)
            {
                try
                {
                    PSVR2ToolkitCAPI.SendGazeSetCommand(new GazeCalibrationCommand {
                        reportMode = GazeCalibrationReportMode.ComputeAndApplyCalibration,
                        payload = new GazeCalibrationPacket { result = GazeCalibrationResult.Success }
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[PSVR2GazeCalibration] ComputeAndApply send failed: {ex}");
                    overallSuccess = false;
                    break;
                }

                bool computeDone = false;
                bool computeSucceeded = false;
                elapsed = 0f;

                while (elapsed < computeTimeout)
                {
                    yield return new WaitForSeconds(pointPollInterval);
                    elapsed += pointPollInterval;

                    GazeCalibrationStatus status = GazeCalibrationStatus.EyetrackingInactive;
                    try
                    {
                        var pollResp = PSVR2ToolkitCAPI.SendGazeGetCommand(new GazeCalibrationCommand {
                            reportMode = GazeCalibrationReportMode.ComputeAndApplyCalibration,
                            payload = new GazeCalibrationPacket { result = GazeCalibrationResult.Success }
                        });
                        status = pollResp.status;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[PSVR2GazeCalibration] Compute poll failed: {ex}");
                    }

                    if (status == GazeCalibrationStatus.ComputeSucceeded)
                    {
                        computeDone = true;
                        computeSucceeded = true;
                        break;
                    }
                    else if (status == GazeCalibrationStatus.ComputeFailed)
                    {
                        computeDone = true;
                        computeSucceeded = false;
                        break;
                    }
                }

                if (!computeDone)
                {
                    Debug.LogWarning($"[PSVR2GazeCalibration] ComputeAndApply timed out.");
                    overallSuccess = false;
                    break;
                }

                if (!computeSucceeded)
                {
                    Debug.LogWarning($"[PSVR2GazeCalibration] Firmware reported ComputeFailed at point {i}.");
                    overallSuccess = false;
                    break;
                }

                if (isDarkStageComplete)
                {
                    Debug.Log($"[PSVR2GazeCalibration] Dark Stage Complete.");
                    OnDarkStageComplete?.Invoke();

                    // Show next point and wait for the background to switch to light
                    Vector3 ptNext = calibrationPoints[i+1];
                    OnShowPoint?.Invoke(new Vector3(-ptNext.x/1000.0f, ptNext.y/1000.0f, ptNext.z/1000.0f));
                    yield return new WaitForSeconds(switchToLightDelay);
                }
            }
        }

        FinishCalibrationSequence(overallSuccess);
        _sessionCoroutine = null;
    }

    private void FinishCalibrationSequence(bool success)
    {
        try
        {
            // Hide the last point
            OnShowPoint?.Invoke(Vector3.zero);

            PSVR2ToolkitCAPI.SendGazeSetCommand(new GazeCalibrationCommand {
                reportMode = GazeCalibrationReportMode.StopCalibration,
                payload = new GazeCalibrationPacket { result = GazeCalibrationResult.Success }
            });

            PSVR2ToolkitCAPI.SendGazeSetCommand(new GazeCalibrationCommand {
                reportMode = GazeCalibrationReportMode.SetEnabledEye,
                payload = new GazeCalibrationPacket { eyeEnabled = hmd2_gaze_enabled_eye_t.HMD2_GAZE_ENABLED_EYE_BOTH }
            });

            PSVR2ToolkitCAPI.SendGazeSetCommand(new GazeCalibrationCommand {
                reportMode = GazeCalibrationReportMode.RetrieveCalibrationData,
                payload = new GazeCalibrationPacket { result = GazeCalibrationResult.Success }
            });

            Debug.Log("[PSVR2GazeCalibration] Calibration sequence ended and data retrieved.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PSVR2GazeCalibration] FinishCalibrationSequence failed: {ex}");
        }
        finally
        {
            IsCalibrating = false;
            CurrentPointIndex = -1;

            if (success)
                OnCalibrationSucceeded?.Invoke();
            else
                OnCalibrationFailed?.Invoke();

            OnCalibrationStopped?.Invoke();
        }
    }
}