using UnityEngine;

public class GameManager : MonoBehaviour
{
    [SerializeField] private GameObject pipeCoin;
    [SerializeField] private GameObject bloodParent;

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
        foreach (ParticleSystem p in bloodParent.GetComponentsInChildren<ParticleSystem>())
        {
            p.Play();
        }
    }
    
    #endregion
}
