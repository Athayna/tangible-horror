using UnityEngine;

public class EyesManager : MonoBehaviour
{
    void Start()
    {
        foreach (Transform child in transform)
        {
            Rigidbody rb = child.GetComponent<Rigidbody>();
            rb.linearDamping = 3;
        }
    }

    [ContextMenu("Randomize Rotation")]
    void RandomizeRotation()
    {
        foreach (Transform child in transform)
            child.localRotation = Quaternion.Euler(Random.Range(0f, 360f), Random.Range(0f, 360f), Random.Range(0f, 360f));
    }
}
