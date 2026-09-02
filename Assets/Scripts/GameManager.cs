using System.Collections;
using UnityEngine;

[RequireComponent(typeof(WinScreen))]
public class GameManager : MonoBehaviour
{
    [SerializeField, Tooltip("Lock that is watched. Left empty: the first active one in the scene.")]
    private CoinLock coinLock;

    [Header("Pipe")]
    [SerializeField] private GameObject bloodParent;
    [SerializeField] private float bloodSprayDuration = 5f;

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

        coinLock.onGameOver.AddListener(EndGame);
    }

    /// <summary>Shows the win screen. Wired to <see cref="CoinLock.onGameOver"/>, which fires once.</summary>
    public void EndGame()
    {
        Debug.Log("Congratulations!");
        GetComponent<WinScreen>().Show();
    }

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
