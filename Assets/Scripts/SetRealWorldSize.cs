using UnityEngine;

public class SetRealWorldSize : MonoBehaviour
{
    public enum WidthAxis
    {
        X,
        Z
    }

    [SerializeField] private float targetWidthCm = 25f;
    [SerializeField] private WidthAxis originalWidthAxis = WidthAxis.X;

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
        float originalWidth = originalWidthAxis == WidthAxis.X
            ? mf.sharedMesh.bounds.size.x
            : mf.sharedMesh.bounds.size.z;

        if (originalWidth == 0)
        {
            Debug.LogWarning("Mesh has zero width, can't scale.");
            return;
        }

        float scaleFactor = targetWidthMeters / originalWidth;
        transform.localScale = new Vector3(scaleFactor, scaleFactor, scaleFactor);
    }
}