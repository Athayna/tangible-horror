using UnityEngine;

public class GameManager : MonoBehaviour
{
    [SerializeField] private GameObject pipeCoin;
    [SerializeField] private GameObject bloodParent;
    
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
