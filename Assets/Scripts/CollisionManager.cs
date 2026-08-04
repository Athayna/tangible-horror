using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

public class CollisionManager : MonoBehaviour
{
    [Header("Detection")]
    [SerializeField] private bool listenToTriggers = true;
    [SerializeField] private bool listenToCollisions = true;
    [SerializeField] private Collider assignedCollider;

    [Header("Events")]
    [SerializeField] private UnityEvent onEnter;
    [SerializeField] private UnityEvent onStay;
    [SerializeField] private UnityEvent onExit;

    [Header("Object toggles")]
    [SerializeField] private List<ObjectToggleAction> toggleOnEnter = new List<ObjectToggleAction>();
    [SerializeField] private List<ObjectToggleAction> toggleOnStay = new List<ObjectToggleAction>();
    [SerializeField] private List<ObjectToggleAction> toggleOnExit = new List<ObjectToggleAction>();

    private bool isInside;

    private void Reset()
    {
        assignedCollider = GetComponent<Collider>();
    }

    private void OnTriggerEnter(Collider other)
    {
        Debug.Log($"OnTriggerEnter called with {other.name}");
        if (!listenToTriggers)// || !IsRelevantCollider(other))
        {
            return;
        }

        if (!isInside)
        {
            isInside = true;
            onEnter?.Invoke();
            Debug.Log($"OnTriggerEnter: Invoked onEnter for {other.name}");
            ApplyToggles(toggleOnEnter);
        }
    }

    private void OnTriggerStay(Collider other)
    {
        if (!listenToTriggers || !IsRelevantCollider(other) || !isInside)
        {
            return;
        }

        onStay?.Invoke();
        ApplyToggles(toggleOnStay);
    }

    private void OnTriggerExit(Collider other)
    {
        if (!listenToTriggers)// || !IsRelevantCollider(other) || !isInside)
        {
            return;
        }

        isInside = false;
        onExit?.Invoke();
        ApplyToggles(toggleOnExit);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!listenToCollisions || !IsRelevantCollider(collision.collider))
        {
            return;
        }

        if (!isInside)
        {
            isInside = true;
            onEnter?.Invoke();
            ApplyToggles(toggleOnEnter);
        }
    }

    private void OnCollisionStay(Collision collision)
    {
        if (!listenToCollisions || !IsRelevantCollider(collision.collider) || !isInside)
        {
            return;
        }

        onStay?.Invoke();
        ApplyToggles(toggleOnStay);
    }

    private void OnCollisionExit(Collision collision)
    {
        if (!listenToCollisions || !IsRelevantCollider(collision.collider) || !isInside)
        {
            return;
        }

        isInside = false;
        onExit?.Invoke();
        ApplyToggles(toggleOnExit);
    }

    private bool IsRelevantCollider(Collider other)
    {
        if (assignedCollider == null)
        {
            assignedCollider = GetComponent<Collider>();
        }

        return assignedCollider != null && other == assignedCollider;
    }

    private void ApplyToggles(List<ObjectToggleAction> actions)
    {
        if (actions == null)
        {
            return;
        }

        foreach (ObjectToggleAction action in actions)
        {
            if (action == null || action.targetObject == null)
            {
                continue;
            }

            action.targetObject.SetActive(action.setActive);
        }
    }

    [System.Serializable]
    public class ObjectToggleAction
    {
        public GameObject targetObject;
        public bool setActive = true;
    }
}
