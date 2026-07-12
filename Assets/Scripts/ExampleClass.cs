using UnityEngine;
using System.Collections;

public class ExampleClass : MonoBehaviour
{
    public Rigidbody rb;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.linearDamping = 10;
    }

    void OpenParachute()
    {
        rb.linearDamping = 10;
    }

    void Update()
    {
        if (Input.GetButton("Space"))
            OpenParachute();
    }
}
