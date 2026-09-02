using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// One coin slot of a <see cref="CoinLock"/>. Ingests the first doubloon that collides with it and puts a
/// static mesh in its place.
/// </summary>
public class CoinLockSlot : MonoBehaviour
{
    [SerializeField] private GameObject doubloonMesh;
    [SerializeField, Tooltip("World scale of the mesh that replaces the swallowed coin.")]
    private float insertedMeshScale = 0.02f;
    public UnityEvent onCoinInserted;
    private bool isFilled;

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Doubloon")) return;
        if (isFilled) return;
        if (!other.enabled) return;

        isFilled = true;

        // The tag sits on the coin root, but go through the body so a child collider works too.
        GameObject coin = other.attachedRigidbody ? other.attachedRigidbody.gameObject : other.gameObject;

        foreach (Collider c in coin.GetComponentsInChildren<Collider>()) c.enabled = false;
        Destroy(coin);

        SpawnMesh();
        onCoinInserted.Invoke();

        Debug.Log("Coin ingested.");
        enabled = false;
    }

    private void SpawnMesh()
    {
        if (!doubloonMesh)
        {
            Debug.LogError($"CoinLockSlot {name}: no Doubloon Mesh assigned, the slot stays empty.", this);
            return;
        }

        GameObject mesh = Instantiate(doubloonMesh, transform.position, transform.rotation);
        mesh.transform.localRotation = Quaternion.Euler(0, 0, 90);
        mesh.transform.localScale = Vector3.one * insertedMeshScale;
    }
}
