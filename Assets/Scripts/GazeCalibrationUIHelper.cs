using Unity.VisualScripting;
using UnityEngine;

/// <summary>
/// Monitors Gaze Calibration events and manages the success/failure visual screens,
/// automatically navigating to the results state.
/// </summary>
public class GazeCalibrationUIHelper : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The PSVR2GazeCalibration instance to monitor.")]
    public PSVR2GazeCalibration gazeCalibration;

    [Tooltip("The navigation state machine to command.")]
    public StateMachine stateMachine;

    [Tooltip("The name of the state machine event representing the results screen.")]
    public string resultsSuccessEventName = "GazeSuccess";
    public string resultsFailureEventName = "GazeFailure";
    
    private void OnEnable()
    {
        if (gazeCalibration != null)
        {
            gazeCalibration.OnCalibrationSucceeded.AddListener(HandleCalibrationSuccess);
            gazeCalibration.OnCalibrationFailed.AddListener(HandleCalibrationFailure);
        }
    }

    private void OnDisable()
    {
        if (gazeCalibration != null)
        {
            gazeCalibration.OnCalibrationSucceeded.RemoveListener(HandleCalibrationSuccess);
            gazeCalibration.OnCalibrationFailed.RemoveListener(HandleCalibrationFailure);
        }
    }

    private void HandleCalibrationSuccess()
    {
        stateMachine.TriggerUnityEvent(resultsSuccessEventName);
    }

    private void HandleCalibrationFailure()
    {
        stateMachine.TriggerUnityEvent(resultsFailureEventName);
    }
}
