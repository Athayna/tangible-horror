using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Counts the coins inserted into this lock. Only the slots below this object are counted, so a
/// second lock somewhere else in the scene cannot raise the target - the previous global
/// FindObjectsByType lookup did exactly that as soon as a duplicate lock was enabled.
/// </summary>
public class CoinLock : MonoBehaviour
{
    [SerializeField, Tooltip("Filled from the children automatically. Only these slots are counted.")]
    private CoinLockSlot[] slots;

    /// <summary>Raised once per coin, right after <see cref="InsertedCoins"/> has gone up.</summary>
    public UnityEvent onCoinInserted;

    /// <summary>Raised once, as soon as every usable slot of this lock is filled.</summary>
    public UnityEvent onGameOver;

    private int _coins;
    private int _slotCount;
    private bool _complete;

    public int InsertedCoins => _coins;

    /// <summary>Slots that were usable when the lock started, so the highest reachable coin count.</summary>
    public int SlotCount => _slotCount;

    public bool IsComplete => _complete;

    private void Reset()
    {
        slots = GetComponentsInChildren<CoinLockSlot>(true);
    }

    private void OnValidate()
    {
        if (slots == null || slots.Length == 0)
        {
            slots = GetComponentsInChildren<CoinLockSlot>(true);
        }
    }

    private void Awake()
    {
        // Defensive: the events are new fields, older serialized data does not carry them.
        onCoinInserted ??= new UnityEvent();
        onGameOver ??= new UnityEvent();
    }

    private void Start()
    {
        if (slots == null || slots.Length == 0)
        {
            slots = GetComponentsInChildren<CoinLockSlot>(true);
        }

        _coins = 0;
        _slotCount = 0;

        foreach (CoinLockSlot slot in slots)
        {
            // A switched off slot can never report a coin, so it must not raise the target either.
            if (!slot || !slot.gameObject.activeInHierarchy)
            {
                continue;
            }

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
        onCoinInserted.Invoke();

        if (_complete || _coins < _slotCount)
        {
            return;
        }

        _complete = true;
        onGameOver.Invoke();
    }
}
