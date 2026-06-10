using UnityEngine;

public class WormManager : MonoBehaviour
{
    void Start()
    {
        //random pos and rot for worms (children)
        foreach (Transform child in transform)
        {
            float randomRotX = Random.Range(0f, 360f);
            float randomRotY = Random.Range(0f, 360f);
            float randomRotZ = Random.Range(0f, 360f);
            child.localRotation = Quaternion.Euler(randomRotX, randomRotY, randomRotZ);
            
            float randomX = Random.Range(-0.1f, 0.1f);
            float randomZ = Random.Range(-0.1f, 0.1f);
            child.localPosition = new Vector3(randomX, 0f, randomZ);
        }
    }

    void Update()
    {
        
    }
}
