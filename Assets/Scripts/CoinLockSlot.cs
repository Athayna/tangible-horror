using Meta.WitAi;
using UnityEngine;
using UnityEngine.Events;

public class CoinLockSlot : MonoBehaviour
{
    [SerializeField] private GameObject doubloonMesh;
    public UnityEvent onCoinInserted;

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Doubloon")) return;

        other.GetComponent<Collider>().enabled = false;
        other.gameObject.DestroySafely();
        GameObject doubloon = Instantiate(doubloonMesh, transform.position, transform.rotation);
        doubloon.transform.localRotation = Quaternion.Euler(0, 0, 90);
        doubloon.transform.localScale = new Vector3(0.02f, 0.02f, 0.02f);
        onCoinInserted.Invoke();
        Destroy(this);
    }
}
