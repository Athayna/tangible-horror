using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Serialization;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Script containing methods to create, load and delete the world lock anchor the LocationRoot is aligned to.
///
/// The LocationRoot is parented under the spatial anchor instead of being positioned from the anchor pose,
/// and MRUK's own world lock stays off on purpose:
/// MRUK's world lock keeps room anchors aligned by moving the OVRCameraRig/TrackingSpace transform every
/// frame. This project loads no room data at all, so there is nothing to gain from it - but it would move
/// the whole tracking space under the marker tracking, whose camera poses belong to an earlier capture
/// timestamp. Parenting corrects the content exactly where the content is, and leaves tracking space
/// identical to world space.
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

    // Where the LocationRoot hangs while it is not attached to an anchor
    private Transform originalParent;

    private static Guid StoredUuid =>
        Guid.TryParse(PlayerPrefs.GetString(AnchorUuidKey, string.Empty), out Guid uuid) ? uuid : Guid.Empty;

    /// <summary>True while the content is parented under a localized spatial anchor.</summary>
    public bool IsAnchored => currentAnchor != null && locationRoot != null && locationRoot.parent == currentAnchor.transform;

    private void Awake()
    {
        if (authoring == null)
            authoring = GetComponent<AuthoringManager>();

        if (locationRoot != null)
            originalParent = locationRoot.parent;
    }

    private void Start()
    {
        // MRUK ships with EnableWorldLock set to true, so switch it off before its first Update runs
        DisableMrukWorldLock();

        if (loadOnStart && StoredUuid != Guid.Empty)
            LoadAnchor();
    }

    /// <summary>
    /// Spawns a spatial anchor at the LocationRoot's pose, parents the content under it and saves it
    /// together with the current layout, an already stored anchor is erased first so creating a new one
    /// simply overwrites it
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

        // The anchor sits exactly at the LocationRoot's pose, so attaching does not move the content
        AttachToAnchor();

        if (authoring != null)
            authoring.SaveLayout();

        Debug.Log($"[WorldLockAnchor] Created and saved anchor {currentAnchor.Uuid}");
    }

    /// <summary>
    /// Retrieves the saved anchor, parents the LocationRoot under it and restores the layout.
    /// From then on OVRSpatialAnchor keeps the anchor on its real world spot every frame and the
    /// content follows along, drift correction included
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

        // Place the anchor object right away, otherwise the content would pop for a frame
        if (unbound.TryGetPose(out Pose pose))
            anchorGo.transform.SetPositionAndRotation(pose.position, pose.rotation);
        else
            Debug.LogWarning("[WorldLockAnchor] Anchor has no pose yet, waiting for the component to update it.");

        AttachToAnchor();

        if (authoring != null)
            authoring.RestoreLayout();

        Debug.Log($"[WorldLockAnchor] Loaded and attached to anchor {uuid}");
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

    // Hangs the content under the anchor. The anchor always sits where the content belongs, so the
    // local pose is zeroed instead of being kept.
    private void AttachToAnchor()
    {
        if (locationRoot == null || currentAnchor == null)
            return;

        // Reparent keeping the world transform first, so a scaled old parent cannot change the content
        // size, then snap the local pose onto the anchor
        locationRoot.SetParent(currentAnchor.transform, true);
        locationRoot.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
    }

    // Puts the content back where it hung before, keeping its world pose so nothing jumps
    private void DetachFromAnchor()
    {
        if (locationRoot == null || currentAnchor == null)
            return;

        if (locationRoot.parent == currentAnchor.transform)
            locationRoot.SetParent(originalParent, true);
    }

    private void DestroyCurrentAnchor()
    {
        // Has to happen first, otherwise the content would be destroyed together with the anchor
        DetachFromAnchor();

        if (currentAnchor == null)
            return;

        Destroy(currentAnchor.gameObject);
        currentAnchor = null;
    }

    // MRUK's world lock would move the OVRCameraRig/TrackingSpace every frame. That is meant for keeping
    // loaded room anchors aligned - this project loads none, and a moving tracking space adds a
    // time varying error to every AprilTag pose, because those come from an earlier capture timestamp.
    private void DisableMrukWorldLock()
    {
        if (MRUK.Instance == null)
        {
            Debug.LogWarning("[WorldLockAnchor] No MRUK instance found, cannot verify that world lock is off.");
            return;
        }

        MRUK.Instance.SetCustomWorldLockAnchor(null, Pose.identity);
        MRUK.Instance.EnableWorldLock = false;
    }
}
