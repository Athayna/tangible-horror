using UnityEngine;

public class SetRealWorldSize : MonoBehaviour
{
    public enum ScaleMode
    {
        Uniform,
        PerAxis
    }

    [Tooltip("Zielgroesse in cm pro lokaler Achse. Achsen mit Wert <= 0 werden ignoriert.")]
    [SerializeField] private Vector3 targetSizeCm = new Vector3(25f, 0f, 0f);

    [Tooltip("Uniform: ein Faktor fuer alle Achsen (kleinster Faktor der gesetzten Achsen, das Objekt passt also in die Zielmasse). " +
             "PerAxis: jede gesetzte Achse bekommt ihren eigenen Faktor, nicht gesetzte Achsen den Uniform-Faktor.")]
    [SerializeField] private ScaleMode scaleMode = ScaleMode.Uniform;

    [Tooltip("Optional: Objekte, deren gemeinsame Ausdehnung gemessen wird. Pro Objekt zaehlen die eigenen Collider, " +
             "sonst die Collider in den Children, sonst die Meshes. Leer = erster MeshFilter in den Children (wie bisher).")]
    [SerializeField] private GameObject[] sizeSources;

    // --- Legacy (alte Felder, werden einmalig nach targetSizeCm uebernommen) ---
    public enum WidthAxis
    {
        X,
        Z
    }

    [SerializeField, HideInInspector] private float targetWidthCm;
    [SerializeField, HideInInspector] private WidthAxis originalWidthAxis;

    void Awake()
    {
        MigrateLegacyFields();
    }

    void Start()
    {
        Apply();
    }

    void Reset()
    {
        // Neu hinzugefuegte Komponenten haben keine Legacy-Daten.
        targetWidthCm = 0f;
    }

    void OnValidate()
    {
        MigrateLegacyFields();
    }

    [ContextMenu("Apply Real World Size")]
    public void Apply()
    {
        SetSize(targetSizeCm);
    }

    public void SetSize(float uniformSizeCm)
    {
        SetSize(new Vector3(uniformSizeCm, uniformSizeCm, uniformSizeCm));
    }

    public void SetSize(Vector3 sizeCm)
    {
        if (!TryGetOriginalSize(out Vector3 originalSize))
        {
            Debug.LogWarning("No collider or mesh found to measure.", this);
            return;
        }

        Vector3 targetMeters = sizeCm / 100f;
        Vector3 factors = transform.localScale;
        float uniformFactor = 0f;
        bool anyAxis = false;

        for (int axis = 0; axis < 3; axis++)
        {
            if (targetMeters[axis] <= 0f)
            {
                continue;
            }

            if (originalSize[axis] <= 1e-6f)
            {
                Debug.LogWarning($"Measured size on axis {(char)('X' + axis)} is zero, can't scale that axis.", this);
                continue;
            }

            float factor = targetMeters[axis] / originalSize[axis];
            factors[axis] = factor;
            uniformFactor = anyAxis ? Mathf.Min(uniformFactor, factor) : factor;
            anyAxis = true;
        }

        if (!anyAxis)
        {
            Debug.LogWarning("No target size set on any axis, nothing to scale.", this);
            return;
        }

        if (scaleMode == ScaleMode.Uniform)
        {
            transform.localScale = Vector3.one * uniformFactor;
            return;
        }

        for (int axis = 0; axis < 3; axis++)
        {
            if (targetMeters[axis] <= 0f || originalSize[axis] <= 1e-6f)
            {
                factors[axis] = uniformFactor;
            }
        }

        transform.localScale = factors;
    }

    /// <summary>
    /// Ausdehnung der Quellen im lokalen Raum dieses Transforms, unabhaengig von der aktuellen Skalierung.
    /// </summary>
    private bool TryGetOriginalSize(out Vector3 size)
    {
        Bounds bounds = default;
        bool hasBounds = false;

        if (sizeSources != null)
        {
            foreach (GameObject source in sizeSources)
            {
                if (source == null)
                {
                    continue;
                }

                if (TryGetSourceBounds(source, out Bounds sourceBounds))
                {
                    Encapsulate(ref bounds, ref hasBounds, sourceBounds);
                }
                else
                {
                    Debug.LogWarning($"'{source.name}' has no measurable collider or mesh, skipping.", source);
                }
            }
        }

        if (!hasBounds)
        {
            // Nichts (brauchbares) zugewiesen -> wie bisher der erste MeshFilter in den Children.
            MeshFilter mf = GetComponentInChildren<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                Encapsulate(ref bounds, ref hasBounds, ToLocalSpace(mf.sharedMesh.bounds, mf.transform));
            }
        }

        size = hasBounds ? bounds.size : Vector3.zero;
        return hasBounds;
    }

    /// <summary>
    /// Ausdehnung eines zugewiesenen Objekts: eigene Collider, sonst Collider in den Children, sonst Meshes.
    /// </summary>
    private bool TryGetSourceBounds(GameObject source, out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;

        Collider[] colliders = source.GetComponents<Collider>();
        if (colliders.Length == 0)
        {
            colliders = source.GetComponentsInChildren<Collider>();
        }

        foreach (Collider collider in colliders)
        {
            if (TryGetLocalBounds(collider, out Bounds colliderBounds))
            {
                Encapsulate(ref bounds, ref hasBounds, colliderBounds);
            }
        }

        if (hasBounds)
        {
            return true;
        }

        MeshFilter[] filters = source.GetComponents<MeshFilter>();
        if (filters.Length == 0)
        {
            filters = source.GetComponentsInChildren<MeshFilter>();
        }

        foreach (MeshFilter filter in filters)
        {
            if (filter.sharedMesh != null)
            {
                Encapsulate(ref bounds, ref hasBounds, ToLocalSpace(filter.sharedMesh.bounds, filter.transform));
            }
        }

        return hasBounds;
    }

    private static void Encapsulate(ref Bounds bounds, ref bool hasBounds, Bounds addition)
    {
        if (hasBounds)
        {
            bounds.Encapsulate(addition);
        }
        else
        {
            bounds = addition;
            hasBounds = true;
        }
    }

    private bool TryGetLocalBounds(Collider collider, out Bounds bounds)
    {
        Bounds local;
        switch (collider)
        {
            case BoxCollider box:
                local = new Bounds(box.center, box.size);
                break;
            case SphereCollider sphere:
                local = new Bounds(sphere.center, Vector3.one * (sphere.radius * 2f));
                break;
            case CapsuleCollider capsule:
                float diameter = capsule.radius * 2f;
                Vector3 capsuleSize = new Vector3(diameter, diameter, diameter);
                capsuleSize[capsule.direction] = Mathf.Max(capsule.height, diameter);
                local = new Bounds(capsule.center, capsuleSize);
                break;
            case MeshCollider mesh when mesh.sharedMesh != null:
                local = mesh.sharedMesh.bounds;
                break;
            default:
                Debug.LogWarning($"Collider type {collider.GetType().Name} can't be measured, skipping.", collider);
                bounds = default;
                return false;
        }

        bounds = ToLocalSpace(local, collider.transform);
        return true;
    }

    /// <summary>
    /// Rechnet lokale Bounds einer Quelle in den lokalen Raum dieses Transforms um.
    /// Die eigene Skalierung kuerzt sich dabei heraus, gemessen wird also die Groesse bei localScale = 1.
    /// </summary>
    private Bounds ToLocalSpace(Bounds source, Transform sourceTransform)
    {
        if (sourceTransform == transform)
        {
            return source;
        }

        Matrix4x4 matrix = transform.worldToLocalMatrix * sourceTransform.localToWorldMatrix;
        Vector3 min = source.min;
        Vector3 max = source.max;
        Bounds result = new Bounds(matrix.MultiplyPoint3x4(min), Vector3.zero);

        for (int corner = 1; corner < 8; corner++)
        {
            Vector3 point = new Vector3(
                (corner & 1) == 0 ? min.x : max.x,
                (corner & 2) == 0 ? min.y : max.y,
                (corner & 4) == 0 ? min.z : max.z);
            result.Encapsulate(matrix.MultiplyPoint3x4(point));
        }

        return result;
    }

    private void MigrateLegacyFields()
    {
        if (targetWidthCm <= 0f)
        {
            return;
        }

        targetSizeCm = Vector3.zero;
        targetSizeCm[originalWidthAxis == WidthAxis.X ? 0 : 2] = targetWidthCm;
        scaleMode = ScaleMode.Uniform;
        targetWidthCm = 0f;
    }
}
