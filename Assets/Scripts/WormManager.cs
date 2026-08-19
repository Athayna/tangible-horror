using UnityEngine;

public class WormManager : MonoBehaviour
{
    [ContextMenu("Randomize Yaw")]
    void RandomizeYaw()
    {
        foreach (Transform child in transform)
            child.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
    }
}