using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Raises <see cref="onExit"/> when a doubloon leaves this object's trigger. Sits on the pipe, where it
/// starts the blood spray.
///
/// Kept the class name so the existing scene and prefab references stay intact, even though it no longer
/// manages collisions: the pipe's collider is a trigger, so the collision callbacks could never fire.
/// </summary>
public class CollisionManager : MonoBehaviour
{
    private const string DoubloonTag = "Doubloon";

    [SerializeField] private UnityEvent onExit;

    private void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag(DoubloonTag))
        {
            return;
        }

        onExit?.Invoke();
    }
}
