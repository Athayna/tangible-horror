using Meta.WitAi;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;

public class CoinLockSlot : MonoBehaviour
{
    private GameObject _doubloon;
    public UnityEvent onCoinInserted;

    private void Start()
    {
        _doubloon = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/ThirdPartyAssets/Doubloon/doubloon.obj");
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Doubloon")) return;

        other.gameObject.DestroySafely();
        Instantiate(_doubloon, transform.position, transform.rotation);
        onCoinInserted.Invoke();
        Destroy(gameObject);
    }
}
