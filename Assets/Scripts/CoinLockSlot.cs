using UnityEngine;
using UnityEngine.Events;

public class CoinLockSlot : MonoBehaviour
{
    [SerializeField] private GameObject doubloonMesh;
    public UnityEvent onCoinInserted;
    private bool _isFilled;

    private void OnTriggerEnter(Collider other)
    {
        if (_isFilled) return;
        if (!other.CompareTag("Doubloon")) return;

        _isFilled = true;

        other.enabled = false;
        Destroy(other.gameObject);
        GameObject doubloon = Instantiate(doubloonMesh, transform.position, transform.rotation);
        doubloon.transform.localRotation = Quaternion.Euler(0, 0, 90);
        doubloon.transform.localScale = new Vector3(0.02f, 0.02f, 0.02f);
        onCoinInserted.Invoke();
        Destroy(this);
    }
}
