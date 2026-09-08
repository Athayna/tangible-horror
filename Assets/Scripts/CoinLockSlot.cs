using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// One coin slot of a <see cref="CoinLock"/>. Ingests the first doubloon that collides with it and puts a
/// static mesh in its place.
/// </summary>
public class CoinLockSlot : MonoBehaviour
{
    [SerializeField, Tooltip("The doubloon that will be shown in this slot.")]
    private GameObject doubloon;
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

        EnableDoubloon();
        onCoinInserted.Invoke();

        Debug.Log("Coin ingested.");
        enabled = false;
    }

    private void EnableDoubloon()
    {
        if (!doubloon)
        {
            Debug.LogError($"CoinLockSlot {name}: no Doubloon assigned, the slot stays empty.", this);
            return;
        }

        doubloon.SetActive(true);
        //doubloon.transform.localScale = new Vector3(0.012f, 0.012f, 0.012f);
    }
}
