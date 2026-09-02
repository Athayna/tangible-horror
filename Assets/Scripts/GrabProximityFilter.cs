using System.Collections.Generic;
using Oculus.Interaction;
using UnityEngine;

/// <summary>
/// Bounds how far away a hand may be and still grab this object.
///
/// The Interaction SDK has no distance limit of its own: HandGrabInteractor.ComputeCandidate() walks every
/// registered HandGrabInteractable, scores each one and takes the best - and GetPoseScore() never rejects
/// anything for being too far. So the nearest grabbable in the whole scene is always a hover candidate, and
/// a pinch anywhere selects it. That is why a coin can be grabbed from several centimeters beside it.
///
/// InteractableRegistry only lists interactables whose CanBeSelectedBy() passes, and that consults the
/// interactable's Interactor Filters. Failing the filter therefore removes this object from the candidate
/// list entirely, rather than letting it win the scoring from far away.
///
/// Setup: drop this on the object that carries the colliders, then add it to Interactor Filters on the
/// HandGrabInteractable (and on GrabInteractable if controllers should be limited too).
/// </summary>
public class GrabProximityFilter : MonoBehaviour, IGameObjectFilter
{
    [SerializeField, Tooltip("Colliders the distance is measured against. Auto-filled from this object and " +
                             "its children if left empty.")]
    private List<Collider> colliders = new List<Collider>();

    [SerializeField, Min(0f), Tooltip("Maximum distance in CENTIMETERS between the interactor and the " +
                                      "nearest collider surface. Measured from the interactor's own origin, " +
                                      "which sits at the hand, not at the fingertips - so tune it by feel " +
                                      "rather than by ruler. Start at 4 and lower it until grabbing beside " +
                                      "the object stops working.")]
    private float maxDistanceCentimeters = 4f;

    [SerializeField, Tooltip("Logs every rejected and accepted grab attempt with its distance, so the " +
                             "threshold can be found in one session.")]
    private bool logDistances;

    private void Reset()
    {
        colliders.Clear();
        colliders.AddRange(GetComponentsInChildren<Collider>(true));
    }

    private void Awake()
    {
        if (colliders.Count == 0)
        {
            colliders.AddRange(GetComponentsInChildren<Collider>(true));
        }
    }

    public bool Filter(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return true;
        }

        float limit = maxDistanceCentimeters * 0.01f;
        Vector3 from = gameObject.transform.position;
        float nearest = float.PositiveInfinity;

        foreach (Collider collider in colliders)
        {
            // Triggers are the grab volumes of the Interaction SDK itself, not the physical shape, and a
            // disabled collider is not there at all.
            if (collider == null || !collider.enabled || collider.isTrigger)
            {
                continue;
            }

            // ClosestPoint needs a convex shape. A non convex MeshCollider silently falls back to its
            // bounds, which would make the limit far too generous - a dynamic Rigidbody needs Convex
            // anyway, so this only bites on static setups.
            nearest = Mathf.Min(nearest, Vector3.Distance(from, collider.ClosestPoint(from)));
        }

        if (float.IsPositiveInfinity(nearest))
        {
            return true;
        }

        bool allowed = nearest <= limit;

        if (logDistances)
        {
            Debug.Log($"GrabProximityFilter {name}: {(allowed ? "allowed" : "rejected")} " +
                      $"{gameObject.name} at {nearest * 100f:F1} cm (limit {maxDistanceCentimeters:F1} cm)", this);
        }

        return allowed;
    }
}
