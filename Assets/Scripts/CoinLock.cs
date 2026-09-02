using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Counts the coins inserted into this lock. Only the slots below this object are counted, so a
/// second lock somewhere else in the scene cannot raise the target.
/// </summary>
public class CoinLock : MonoBehaviour
{
    /// <summary>Invoked once, as soon as every usable slot of this lock is filled.</summary>
    public UnityEvent onGameOver;

    private int _coins;
    private int _slotCount;
    private bool _complete;

    private void Start()
    {
        foreach (CoinLockSlot slot in GetComponentsInChildren<CoinLockSlot>(true))
        {
            // A switched off slot can never report a coin, so it must not raise the target either.
            if (!slot.gameObject.activeInHierarchy) continue;

            _slotCount++;
            slot.onCoinInserted.AddListener(AddCoin);
        }

        if (_slotCount == 0)
        {
            Debug.LogWarning("CoinLock: no usable CoinLockSlot below this object.", this);
        }
    }

    private void AddCoin()
    {
        _coins++;

        if (_complete || _coins < _slotCount) return;

        _complete = true;
        onGameOver.Invoke();
    }
}
