using UnityEngine;
using UnityEngine.Events;

public class CoinLock : MonoBehaviour
{
    public UnityEvent onGameOver;
    private int _coins;
    private int _winCondition;

    private void Start()
    {
        CoinLockSlot[] slots = FindObjectsByType<CoinLockSlot>(FindObjectsSortMode.None);
        _coins = 0;
        _winCondition = slots.Length;

        foreach (CoinLockSlot slot in slots)
        {
            slot.onCoinInserted.AddListener(AddCoin);
        }
    }

    private void AddCoin()
    {
        _coins++;
        CheckWinCondition();
    }

    private void CheckWinCondition()
    {
        if (_coins == _winCondition)
        {
            onGameOver.Invoke();
        }
    }
}