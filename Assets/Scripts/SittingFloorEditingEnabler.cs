using UnityEngine;

/// <summary>
/// Enables floor-height editing during Sitting Play Area setup, matching Standing's Floor step.
/// Also ensures a default play rectangle exists so grip/stick adjustments have something to move.
/// </summary>
public class SittingFloorEditingEnabler : MonoBehaviour
{
    [SerializeField] private GameObject floorHeightCalibrator;
    [SerializeField] private ChaperoneMesh chaperoneMesh;
    [SerializeField] private string sittingPanelName = "Sitting Play Area Panel";
    [SerializeField] private string floorPanelName = "Play Area Floor Panel";

    private GameObject _sittingPanel;
    private GameObject _floorPanel;
    private bool _ensuredDefaultForSitting;

    private void Start()
    {
        CachePanels();
    }

    private void LateUpdate()
    {
        if (_sittingPanel == null || _floorPanel == null)
        {
            CachePanels();
        }

        bool sittingActive = _sittingPanel != null && _sittingPanel.activeInHierarchy;
        bool floorActive = _floorPanel != null && _floorPanel.activeInHierarchy;
        bool shouldEditFloor = sittingActive || floorActive;

        if (sittingActive)
        {
            if (!_ensuredDefaultForSitting && chaperoneMesh != null)
            {
                chaperoneMesh.EnsureDefaultPlayArea();
                _ensuredDefaultForSitting = true;
            }
        }
        else
        {
            _ensuredDefaultForSitting = false;
        }

        if (floorHeightCalibrator != null && floorHeightCalibrator.activeSelf != shouldEditFloor)
        {
            floorHeightCalibrator.SetActive(shouldEditFloor);
        }
    }

    private void CachePanels()
    {
        foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (!t.gameObject.scene.IsValid())
            {
                continue;
            }

            if (_sittingPanel == null && t.name == sittingPanelName)
            {
                _sittingPanel = t.gameObject;
            }
            else if (_floorPanel == null && t.name == floorPanelName)
            {
                _floorPanel = t.gameObject;
            }

            if (_sittingPanel != null && _floorPanel != null)
            {
                break;
            }
        }
    }
}
