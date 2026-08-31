using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// One coin slot of a <see cref="CoinLock"/>. Swallows the first doubloon that reaches it and puts a
/// static mesh in its place.
/// </summary>
public class CoinLockSlot : MonoBehaviour
{
    [SerializeField] private GameObject doubloonMesh;

    [SerializeField, Tooltip("World scale of the mesh that replaces the swallowed coin.")]
    private float insertedMeshScale = 0.02f;

    [SerializeField, Tooltip("Logs every doubloon that reaches this slot and why it was or was not taken.")]
    private bool logInsertions = true;

    public UnityEvent onCoinInserted;

    private bool _isFilled;

    public bool IsFilled => _isFilled;

    private void OnTriggerEnter(Collider other)
    {
        // Checked first so hands and other colliders do not show up in the log at all.
        if (!other.CompareTag("Doubloon"))
        {
            return;
        }

        if (_isFilled)
        {
            Log($"rejected {other.name}: slot is already filled");
            return;
        }

        // Destroy() only runs at the end of the frame, so a coin that overlaps two slots at once
        // would be swallowed twice - filling two slots and counting two coins from one coin. The
        // slot that gets there first switches the collider off, which is what this rejects.
        if (!other.enabled)
        {
            Log($"rejected {other.name}: another slot already claimed this coin");
            return;
        }

        _isFilled = true;

        // The tag sits on the coin root, but go through the body so a child collider works too.
        GameObject coin = other.attachedRigidbody
            ? other.attachedRigidbody.gameObject
            : other.gameObject;

        Log($"swallowing {coin.name} (collider {other.name}, kinematic " +
            $"{(other.attachedRigidbody ? other.attachedRigidbody.isKinematic.ToString() : "no body")})");

        other.enabled = false;
        Destroy(coin);

        SpawnMesh();
        onCoinInserted.Invoke();

        // Keeps the component alive - Destroy(this) would leave a null entry in CoinLock.slots -
        // but stops it from receiving any further trigger callbacks.
        enabled = false;
    }

    private void SpawnMesh()
    {
        if (!doubloonMesh)
        {
            Debug.LogError($"CoinLockSlot {name}: no Doubloon Mesh assigned, the slot stays empty.", this);
            return;
        }

        GameObject mesh = Instantiate(doubloonMesh);
        mesh.transform.SetPositionAndRotation(transform.position, Quaternion.Euler(0f, 0f, 90f));
        mesh.transform.localScale = Vector3.one * insertedMeshScale;

        // Parented afterwards and keeping the world pose: the mesh looks exactly as before, but now
        // follows the lock when the table is moved or scaled from the authoring menu.
        mesh.transform.SetParent(transform, true);

        Log($"spawned {mesh.name} at {mesh.transform.position}, active {mesh.activeInHierarchy}, " +
            $"renderers {mesh.GetComponentsInChildren<Renderer>(true).Length}");
    }

    private void Log(string message)
    {
        if (logInsertions)
        {
            Debug.Log($"CoinLockSlot {name}: {message}", this);
        }
    }
}
