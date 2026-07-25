using UnityEngine;
using System.Collections;

public class EyeManager : MonoBehaviour
{
    public Rigidbody rb;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.linearDamping = 3;
    }
}
