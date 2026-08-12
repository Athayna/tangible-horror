using System.Collections;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    [SerializeField] private GameObject pipeCoin;
    [SerializeField] private GameObject bloodParent;
    [SerializeField] private float bloodSprayDuration = 5f;


    private CoinLock _coinLock;

    private void Start()
    {
        _coinLock = FindAnyObjectByType<CoinLock>();
        _coinLock.onGameOver.AddListener(EndGame);
    }

    private void EndGame()
    {
        // TODO: Change scene to result screen via coroutine or display results
        Debug.Log("Congratulations!");
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
