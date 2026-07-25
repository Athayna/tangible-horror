using UnityEngine;

public class WormManager : MonoBehaviour
{
    void Start()
    {
        foreach (Transform child in transform)
        {
            child.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        }
    }
}
