using UnityEngine;

public class SetRealWorldSize : MonoBehaviour
{
    [SerializeField] private float targetWidthCm = 25f;

    void Start()
    {
        SetWidth(targetWidthCm);
    }

    public void SetWidth(float widthCm)
    {
        float targetWidthMeters = widthCm / 100f;

        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogWarning("No mesh found to measure.");
            return;
        }

        // Local-space bounds ignore current scale/rotation
        float originalWidth = mf.sharedMesh.bounds.size.x;

        if (originalWidth == 0)
        {
            Debug.LogWarning("Mesh has zero width, can't scale.");
            return;
        }

        float scaleFactor = targetWidthMeters / originalWidth;
        transform.localScale = new Vector3(scaleFactor, scaleFactor, scaleFactor);
    }
}