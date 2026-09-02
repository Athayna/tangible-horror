using UnityEngine;

/// <summary>
/// Raises the contact offset of this object's colliders. Unity does not expose the property in the Collider
/// inspector, only the project wide default in Physics settings - and that one deliberately sits at 0.001
/// here for the eyes and worms, so it must not be changed for a coin's sake.
///
/// Only worth adding when a flat prop jitters or sinks while resting. Tunnelling is already handled by
/// Collision Detection = Continuous Dynamic.
/// </summary>
public class ColliderContactOffset : MonoBehaviour
{
    [SerializeField, Min(0.0001f), Tooltip("Contact offset in meters. Unity's own default is 0.01, this " +
                                           "project runs 0.001. Try 0.005 for a flat prop.")]
    private float contactOffset = 0.005f;

    private void Awake()
    {
        Apply();
    }

    /// <summary>Also callable from the context menu, so the effect is visible without entering play mode.</summary>
    [ContextMenu("Apply Contact Offset")]
    private void Apply()
    {
        foreach (Collider collider in GetComponentsInChildren<Collider>(true))
        {
            collider.contactOffset = contactOffset;
        }
    }
}
