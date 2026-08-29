using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Serialization;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Script containing methods to create, load and delete the world lock anchor the LocationRoot is aligned to
/// </summary>
public class WorldLockAnchor : MonoBehaviour
{
    [Tooltip("Root of all content that gets anchored in the real world")]
    [FormerlySerializedAs("referenceObject")]
    [SerializeField] private Transform locationRoot;

    [Tooltip("Layout below the LocationRoot, it is saved and restored together with the anchor")]
    [SerializeField] private AuthoringManager authoring;

    [Tooltip("Brings the anchor and the layout back on scene load")]
    [SerializeField] private bool loadOnStart = true;

    private const string AnchorUuidKey = "WorldLockAnchorUuid";
    private OVRSpatialAnchor currentAnchor;

    private static Guid StoredUuid =>
        Guid.TryParse(PlayerPrefs.GetString(AnchorUuidKey, string.Empty), out Guid uuid) ? uuid : Guid.Empty;

    private void Awake()
    {
        if (authoring == null)
            authoring = GetComponent<AuthoringManager>();
    }

    private void Start()
    {
        if (loadOnStart && StoredUuid != Guid.Empty)
            LoadAnchor();
    }

    /// <summary>
    /// Spawns a spatial anchor at the LocationRoot's pose and saves it together with the current layout,
    /// an already stored anchor is erased first so creating a new one simply overwrites it
    /// </summary>
    public async void CreateAnchor()
    {
        if (locationRoot == null)
        {
            Debug.LogError("[WorldLockAnchor] No LocationRoot assigned.");
            return;
        }

        await EraseStoredAnchor();

        GameObject anchorGo = new GameObject("WorldLockAnchor");
        anchorGo.transform.SetPositionAndRotation(locationRoot.position, locationRoot.rotation);
        currentAnchor = anchorGo.AddComponent<OVRSpatialAnchor>();

        if (!await currentAnchor.WhenCreatedAsync())
        {
            Debug.LogError("[WorldLockAnchor] Anchor creation failed.");
            DestroyCurrentAnchor();
            return;
        }

        var saveResult = await currentAnchor.SaveAnchorAsync();
        if (!saveResult.Success)
        {
            Debug.LogError($"[WorldLockAnchor] Save failed: {saveResult.Status}");
            return;
        }

        PlayerPrefs.SetString(AnchorUuidKey, currentAnchor.Uuid.ToString());
        PlayerPrefs.Save();

        if (authoring != null)
            authoring.SaveLayout();

        SetWorldLock(true);
        Debug.Log($"[WorldLockAnchor] Created and saved anchor {currentAnchor.Uuid}");
    }

    /// <summary>
    /// Retrieves the saved anchor, snaps the LocationRoot onto it, restores the layout
    /// and turns the world lock on once the anchor is localized
    /// </summary>
    public async void LoadAnchor()
    {
        if (locationRoot == null)
        {
            Debug.LogError("[WorldLockAnchor] No LocationRoot assigned.");
            return;
        }

        Guid uuid = StoredUuid;
        if (uuid == Guid.Empty)
        {
            Debug.LogWarning("[WorldLockAnchor] No saved anchor UUID found.");
            return;
        }

        // drop a previously loaded instance, the stored anchor itself stays untouched
        DestroyCurrentAnchor();

        var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();
        var loadResult = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(
            new[] { uuid }, unboundAnchors);

        if (!loadResult.Success || unboundAnchors.Count == 0)
        {
            Debug.LogError($"[WorldLockAnchor] Load failed: {loadResult.Status}");
            return;
        }

        var unbound = unboundAnchors[0];
        var localizeResult = await unbound.LocalizeAsync();
        if (!localizeResult)
        {
            Debug.LogError("[WorldLockAnchor] Localization failed.");
            return;
        }

        GameObject anchorGo = new GameObject("WorldLockAnchor");
        currentAnchor = anchorGo.AddComponent<OVRSpatialAnchor>();
        unbound.BindTo(currentAnchor);

        if (!unbound.TryGetPose(out Pose pose))
        {
            Debug.LogError("[WorldLockAnchor] Anchor has no pose.");
            DestroyCurrentAnchor();
            return;
        }

        locationRoot.SetPositionAndRotation(pose.position, pose.rotation);

        if (authoring != null)
            authoring.RestoreLayout();

        SetWorldLock(true);
        Debug.Log($"[WorldLockAnchor] Loaded and aligned to anchor {uuid}");
    }

    /// <summary>
    /// Erases the anchor and the stored layout, the content itself stays where it is
    /// </summary>
    public async void DeleteAnchor()
    {
        await EraseStoredAnchor();

        if (authoring != null)
            authoring.ClearLayout();
    }

    // Erases by UUID so an anchor that was never loaded in this session is removed as well
    private async Task EraseStoredAnchor()
    {
        Guid uuid = currentAnchor != null ? currentAnchor.Uuid : StoredUuid;
        if (uuid != Guid.Empty)
        {
            var eraseResult = await OVRSpatialAnchor.EraseAnchorsAsync(
                Array.Empty<OVRSpatialAnchor>(), new[] { uuid });

            if (eraseResult.Success)
                Debug.Log($"[WorldLockAnchor] Erased anchor {uuid}");
            else
                Debug.LogError($"[WorldLockAnchor] Erase failed: {eraseResult.Status}");
        }

        DestroyCurrentAnchor();

        PlayerPrefs.DeleteKey(AnchorUuidKey);
        PlayerPrefs.Save();
    }

    private void DestroyCurrentAnchor()
    {
        SetWorldLock(false);

        if (currentAnchor == null)
            return;

        Destroy(currentAnchor.gameObject);
        currentAnchor = null;
    }

    // MRUK compensates the tracking drift against our own anchor, that only works once it is localized
    private void SetWorldLock(bool value)
    {
        if (MRUK.Instance == null)
        {
            if (value)
                Debug.LogWarning("[WorldLockAnchor] No MRUK instance, world lock stays off.");

            return;
        }

        MRUK.Instance.SetCustomWorldLockAnchor(value ? currentAnchor : null, Pose.identity);
        MRUK.Instance.EnableWorldLock = value;
    }
}
