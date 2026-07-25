using UnityEngine;

public class WormManager : MonoBehaviour
{
    void Start()
    {
        //random pos and rot for worms (children)
        foreach (Transform child in transform)
        {
            float randomRotY = Random.Range(0f, 360f);
            child.localRotation = Quaternion.Euler(0f, randomRotY, 0f);
            
            float randomX = Random.Range(-0.1f, 0.1f);
            float randomZ = Random.Range(-0.1f, 0.1f);
            child.localPosition = new Vector3(randomX, 0f, randomZ);
        }
    }
}
