using System.Collections;
using UnityEngine;

[RequireComponent(typeof(WinScreen))]
public class GameManager : MonoBehaviour
{
    [Header("Win condition")]
    [SerializeField, Tooltip("Lock that is watched. Left empty: the first active one in the scene.")]
    private CoinLock coinLock;

    [SerializeField, Min(1), Tooltip("Coins that have to sit in the lock before the game ends.")]
    private int requiredCoins = 4;

    [Header("Pipe")]
    [SerializeField] private GameObject pipeCoin;
    [SerializeField] private GameObject bloodParent;
    [SerializeField] private float bloodSprayDuration = 5f;

    private bool _ended;

    /// <summary>Coins currently in the lock, 0 while no lock is known.</summary>
    public int InsertedCoins => coinLock ? coinLock.InsertedCoins : 0;

    public int RequiredCoins => requiredCoins;

    public bool HasEnded => _ended;

    #region Win condition

    private void Start()
    {
        if (!coinLock)
        {
            coinLock = FindAnyObjectByType<CoinLock>();
        }

        if (!coinLock)
        {
            Debug.LogError("GameManager: no CoinLock in the scene, the game can never be won.", this);
            return;
        }

        if (requiredCoins > coinLock.SlotCount)
        {
            Debug.LogWarning($"GameManager: {requiredCoins} coins required but the lock only offers " +
                             $"{coinLock.SlotCount} usable slots - the game can never be won.", this);
        }

        coinLock.onCoinInserted.AddListener(CheckCoinCount);
    }

    private void OnDestroy()
    {
        if (coinLock)
        {
            coinLock.onCoinInserted.RemoveListener(CheckCoinCount);
        }
    }

    /// <summary>
    /// Runs once per inserted coin and ends the game as soon as <see cref="requiredCoins"/> of them
    /// are in the lock. Parameterless on purpose, so it can also be wired to a slot's
    /// onCoinInserted straight from the inspector.
    /// </summary>
    public void CheckCoinCount()
    {
        if (_ended || !coinLock)
        {
            return;
        }

        Debug.Log($"GameManager: {coinLock.InsertedCoins} of {requiredCoins} coins inserted.");

        if (coinLock.InsertedCoins < requiredCoins)
        {
            return;
        }

        EndGame();
    }

    /// <summary>Shows the win screen. Runs at most once, however often it is called.</summary>
    public void EndGame()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        Debug.Log("Congratulations!");
        GetComponent<WinScreen>().Show();
    }

    #endregion

    #region Pipe

    public void StartBloodSpray()
    {
        StartCoroutine(BloodSprayRoutine());
    }

    private IEnumerator BloodSprayRoutine()
    {
        foreach (ParticleSystem p in bloodParent.GetComponentsInChildren<ParticleSystem>())
        {
            p.Play();
        }

        yield return new WaitForSeconds(bloodSprayDuration);

        foreach (ParticleSystem p in bloodParent.GetComponentsInChildren<ParticleSystem>())
        {
            p.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }
    }

    #endregion
}
