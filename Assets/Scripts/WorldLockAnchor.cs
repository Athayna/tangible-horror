using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Script containing methods to create, load and delete world lock anchors with MRUK
/// </summary>
public class WorldLockAnchor : MonoBehaviour
{
    [SerializeField] private Transform referenceObject;

    [SerializeField] private Transform table;

    private const string AnchorUuidKey = "WorldLockAnchorUuid";
    private const string ScaleXKey = "WorldLockAnchorScaleX";
    private const string ScaleYKey = "WorldLockAnchorScaleY";
    private const string ScaleZKey = "WorldLockAnchorScaleZ";
    private OVRSpatialAnchor currentAnchor;

    /// <summary>
    /// Spawns a spatial anchor at the reference object's pose and saves it
    /// </summary>
    public async void CreateAnchor()
    {
        if (referenceObject == null)
        {
            Debug.LogError("[WorldLockAnchor] No reference object assigned.");
            return;
        }

        // Clean up any existing anchor first
        if (currentAnchor != null)
            await DeleteAnchorInternal();

        GameObject anchorGo = new GameObject("WorldLockAnchor");
        anchorGo.transform.SetPositionAndRotation(
            referenceObject.position, referenceObject.rotation);

        currentAnchor = anchorGo.AddComponent<OVRSpatialAnchor>();

        // Wait for the anchor to be created and localized
        while (!currentAnchor.Created)
            await Task.Yield();

        var saveResult = await currentAnchor.SaveAnchorAsync();
        if (saveResult.Success)
        {
            PlayerPrefs.SetString(AnchorUuidKey, currentAnchor.Uuid.ToString());

            Vector3 scale = table.localScale;
            PlayerPrefs.SetFloat(ScaleXKey, scale.x);
            PlayerPrefs.SetFloat(ScaleYKey, scale.y);
            PlayerPrefs.SetFloat(ScaleZKey, scale.z);

            PlayerPrefs.Save();
            Debug.Log($"[WorldLockAnchor] Created and saved anchor {currentAnchor.Uuid} with scale {scale}");
        }
        else
        {
            Debug.LogError($"[WorldLockAnchor] Save failed: {saveResult.Status}");
        }
    }

    /// <summary>
    /// Retrieves the saved anchor and snaps the reference object onto it
    /// </summary>
    public async void LoadAnchor()
    {
        if (!PlayerPrefs.HasKey(AnchorUuidKey))
        {
            Debug.LogWarning("[WorldLockAnchor] No saved anchor UUID found.");
            return;
        }

        if (!System.Guid.TryParse(PlayerPrefs.GetString(AnchorUuidKey), out var uuid))
        {
            Debug.LogError("[WorldLockAnchor] Stored UUID is invalid.");
            return;
        }

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

        if (unbound.TryGetPose(out Pose pose) && referenceObject != null)
        {
            referenceObject.SetPositionAndRotation(pose.position, pose.rotation);

            if (PlayerPrefs.HasKey(ScaleXKey))
            {
                Vector3 scale = new Vector3(
                    PlayerPrefs.GetFloat(ScaleXKey),
                    PlayerPrefs.GetFloat(ScaleYKey),
                    PlayerPrefs.GetFloat(ScaleZKey));
                table.localScale = scale;
                Debug.Log($"[WorldLockAnchor] Restored scale {scale}");
            }

            Debug.Log($"[WorldLockAnchor] Loaded and aligned to anchor {uuid}");
        }
    }

    /// <summary>
    /// Erases the anchor from storage and destroys the runtime object
    /// </summary>
    public async void DeleteAnchor()
    {
        await DeleteAnchorInternal();
    }

    private async Task DeleteAnchorInternal()
    {
        if (currentAnchor != null)
        {
            var eraseResult = await currentAnchor.EraseAnchorAsync();
            if (eraseResult.Success)
                Debug.Log($"[WorldLockAnchor] Erased anchor {currentAnchor.Uuid}");
            else
                Debug.LogError($"[WorldLockAnchor] Erase failed: {eraseResult.Status}");

            Destroy(currentAnchor.gameObject);
            currentAnchor = null;
        }

        if (PlayerPrefs.HasKey(AnchorUuidKey))
        {
            PlayerPrefs.DeleteKey(AnchorUuidKey);
            PlayerPrefs.DeleteKey(ScaleXKey);
            PlayerPrefs.DeleteKey(ScaleYKey);
            PlayerPrefs.DeleteKey(ScaleZKey);
            PlayerPrefs.Save();
        }
    }
}