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
        _doubloon = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Doubloon.prefab");
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.gameObject.name != "Doubloon") return;

        other.gameObject.DestroySafely();
        Instantiate(_doubloon, transform.position, transform.rotation);
        onCoinInserted.Invoke();
        Destroy(gameObject);
    }
}
