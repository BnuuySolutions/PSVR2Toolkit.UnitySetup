using UnityEngine;
using UnityEngine.Events;
using System.Collections;

public class TriggerAfterDelay : MonoBehaviour
{
    [SerializeField] private float delay;
    [SerializeField] private UnityEvent onTrigger;

    public void Trigger()
    {
        StartCoroutine(ExecuteTriggerAfterDelay(delay, onTrigger));
    }

    private IEnumerator ExecuteTriggerAfterDelay(float delay, UnityEvent onTrigger)
    {
        yield return new WaitForSeconds(delay);
        onTrigger?.Invoke();
    }
}