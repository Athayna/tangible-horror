using System;
using System.Collections.Generic;
using System.Globalization;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Runtime authoring of the content below the LocationRoot: moves and scales the selected
/// target, switches grab on objects on and off and stores the authored layout
/// </summary>
public class AuthoringManager : MonoBehaviour
{
    /// <summary>
    /// A piece of furniture: the group is moved as a whole, only the scale target is scaled and
    /// the props keep their spot on the furniture without changing their own size
    /// </summary>
    [Serializable]
    public class FurnitureGroup
    {
        public string label = "Furniture";

        [Tooltip("Parent that gets moved, props included (e.g. TableParent)")]
        public Transform group;

        [Tooltip("Only this transform gets scaled (e.g. Table)")]
        public Transform scaleTarget;

        [Tooltip("Objects standing on the furniture, they follow it but keep their own size")]
        public Transform[] props = Array.Empty<Transform>();
    }

    [Serializable]
    private struct LayoutEntry
    {
        public string path;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
    }

    [Serializable]
    private class Layout
    {
        public List<LayoutEntry> entries = new();
    }

    // One thing the buttons act on, everything that may only be moved keeps a null scale target
    private struct AuthoringTarget
    {
        public string label;
        public Transform move;
        public Transform scaleTarget;
        public Transform[] props;
    }

    [Header("Content")]
    [SerializeField] private Transform locationRoot;
    [SerializeField] private FurnitureGroup[] furniture = Array.Empty<FurnitureGroup>();

    [Header("Grab")]
    [Tooltip("Objects whose grab components get (de)activated, the components themselves are attached by hand")]
    [SerializeField] private GameObject[] grabbables = Array.Empty<GameObject>();
    [SerializeField] private bool grabEnabledOnStart = true;

    [Header("Step size")]
    [SerializeField] private float moveValue = 0.01f;
    [SerializeField] private float scaleValue = 0.1f;
    [SerializeField] private TMP_InputField moveValueField;
    [SerializeField] private TMP_InputField scaleValueField;

    [Header("Selection")]
    [Tooltip("Shows which target the transform and scale buttons act on")]
    [SerializeField] private TMP_Text selectionLabel;

    private const string LayoutKey = "AuthoringLayout";
    private const float MinScale = 0.05f;

    // Everything the buttons can act on, in the order they are cycled through
    private readonly List<AuthoringTarget> targets = new();
    private int selection;
    private bool grabEnabled;

    private void Start()
    {
        BuildTargets();
        BindStepField(moveValueField, moveValue, SetMoveValue);
        BindStepField(scaleValueField, scaleValue, SetScaleValue);
        SetGrabEnabled(grabEnabledOnStart);
        Select(0);
    }

    #region Selection

    public void SelectNext() => Select(selection + 1);

    public void SelectPrevious() => Select(selection - 1);

    /// <summary>
    /// Picks what the transform and scale buttons act on: the LocationRoot, a piece of
    /// furniture or one of its props
    /// </summary>
    public void Select(int index)
    {
        if (targets.Count == 0)
            return;

        selection = ((index % targets.Count) + targets.Count) % targets.Count;

        if (selectionLabel != null)
            selectionLabel.text = targets[selection].label;
    }

    // Only furniture carries a scale target, the LocationRoot and the props are move only
    private void BuildTargets()
    {
        targets.Clear();

        if (locationRoot != null)
            targets.Add(new AuthoringTarget { label = "Transform Object", move = locationRoot });

        foreach (FurnitureGroup entry in furniture)
        {
            if (entry.group != null)
                targets.Add(new AuthoringTarget
                {
                    label = entry.label,
                    move = entry.group,
                    scaleTarget = entry.scaleTarget,
                    props = entry.props
                });

            foreach (Transform prop in entry.props)
                if (prop != null)
                    targets.Add(new AuthoringTarget { label = $"{entry.label} / {prop.name}", move = prop });
        }
    }

    #endregion

    #region Transform

    public void IncreaseTransformX() => Move(Vector3.right);

    public void DecreaseTransformX() => Move(Vector3.left);

    public void IncreaseTransformY() => Move(Vector3.up);

    public void DecreaseTransformY() => Move(Vector3.down);

    public void IncreaseTransformZ() => Move(Vector3.forward);

    public void DecreaseTransformZ() => Move(Vector3.back);

    public void IncreaseScaleX() => Scale(Vector3.right);

    public void DecreaseScaleX() => Scale(Vector3.left);

    public void IncreaseScaleY() => Scale(Vector3.up);

    public void DecreaseScaleY() => Scale(Vector3.down);

    public void IncreaseScaleZ() => Scale(Vector3.forward);

    public void DecreaseScaleZ() => Scale(Vector3.back);

    // Moves along the LocationRoot's axes, so a step stays in meters no matter how the room is rotated
    private void Move(Vector3 direction)
    {
        if (targets.Count == 0 || locationRoot == null)
        {
            Debug.LogWarning("[Authoring] Nothing to move, check the LocationRoot and the furniture entries.");
            return;
        }

        targets[selection].move.position += locationRoot.rotation * direction * moveValue;
    }

    private void Scale(Vector3 direction)
    {
        AuthoringTarget selected = targets.Count > 0 ? targets[selection] : default;
        if (selected.scaleTarget == null)
        {
            Debug.LogWarning("[Authoring] Only furniture can be scaled.");
            return;
        }

        Transform target = selected.scaleTarget;
        Transform[] props = selected.props ?? Array.Empty<Transform>();

        // remember the props in the furniture's local space so they keep their spot on it
        Vector3[] localProps = new Vector3[props.Length];
        for (int i = 0; i < props.Length; i++)
            if (props[i] != null)
                localProps[i] = target.InverseTransformPoint(props[i].position);

        Vector3 scale = target.localScale + direction * scaleValue;
        target.localScale = new Vector3(
            Mathf.Max(scale.x, MinScale),
            Mathf.Max(scale.y, MinScale),
            Mathf.Max(scale.z, MinScale));

        for (int i = 0; i < props.Length; i++)
            if (props[i] != null)
                props[i].position = target.TransformPoint(localProps[i]);
    }

    #endregion

    #region Step size

    /// <summary>
    /// Sets the step of the transform buttons in meters, hooked up to the input field
    /// </summary>
    public void SetMoveValue(string value) => moveValue = ParseStep(value, moveValue);

    /// <summary>
    /// Sets the step of the scale buttons, hooked up to the input field
    /// </summary>
    public void SetScaleValue(string value) => scaleValue = ParseStep(value, scaleValue);

    private static float ParseStep(string value, float fallback)
    {
        // the virtual keyboard hands out a comma on a german layout
        if (float.TryParse(value?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float step)
            && step > 0f)
            return step;

        Debug.LogWarning($"[Authoring] '{value}' is not a valid step size.");
        return fallback;
    }

    private static void BindStepField(TMP_InputField field, float value, UnityAction<string> setter)
    {
        if (field == null)
            return;

        field.text = value.ToString(CultureInfo.InvariantCulture);
        field.onEndEdit.AddListener(setter);
    }

    #endregion

    #region Grab

    public void EnableGrab() => SetGrabEnabled(true);

    public void DisableGrab() => SetGrabEnabled(false);

    public void ToggleGrab() => SetGrabEnabled(!grabEnabled);

    /// <summary>
    /// Switches the grab components on the assigned objects and their children on or off
    /// </summary>
    public void SetGrabEnabled(bool value)
    {
        grabEnabled = value;

        foreach (GameObject grabbable in grabbables)
        {
            if (grabbable == null)
                continue;

            SetComponentsEnabled<Grabbable>(grabbable, value);
            SetComponentsEnabled<GrabInteractable>(grabbable, value);
            SetComponentsEnabled<DistanceGrabInteractable>(grabbable, value);
            SetComponentsEnabled<HandGrabInteractable>(grabbable, value);
            SetComponentsEnabled<DistanceHandGrabInteractable>(grabbable, value);
        }
    }

    private static void SetComponentsEnabled<T>(GameObject target, bool value) where T : MonoBehaviour
    {
        foreach (T component in target.GetComponentsInChildren<T>(true))
            component.enabled = value;
    }

    #endregion

    #region Layout

    /// <summary>
    /// Stores pose and scale of every authored transform relative to the LocationRoot, so the
    /// layout can be restored wherever the anchor ends up
    /// </summary>
    public void SaveLayout()
    {
        if (locationRoot == null)
        {
            Debug.LogError("[Authoring] No LocationRoot assigned.");
            return;
        }

        Layout layout = new();
        foreach (Transform authored in AuthoredTransforms())
        {
            layout.entries.Add(new LayoutEntry
            {
                path = PathFrom(locationRoot, authored),
                position = authored.localPosition,
                rotation = authored.localRotation,
                scale = authored.localScale
            });
        }

        PlayerPrefs.SetString(LayoutKey, JsonUtility.ToJson(layout));
        PlayerPrefs.Save();
        Debug.Log($"[Authoring] Saved layout of {layout.entries.Count} transforms");
    }

    /// <summary>
    /// Applies a stored layout, the transforms are looked up by their path below the LocationRoot
    /// </summary>
    public void RestoreLayout()
    {
        if (locationRoot == null || !PlayerPrefs.HasKey(LayoutKey))
            return;

        Layout layout = JsonUtility.FromJson<Layout>(PlayerPrefs.GetString(LayoutKey));
        if (layout == null)
            return;

        foreach (LayoutEntry entry in layout.entries)
        {
            Transform authored = locationRoot.Find(entry.path);
            if (authored == null)
            {
                Debug.LogWarning($"[Authoring] Layout entry '{entry.path}' does not exist anymore.");
                continue;
            }

            authored.SetLocalPositionAndRotation(entry.position, entry.rotation);
            authored.localScale = entry.scale;
        }

        Debug.Log($"[Authoring] Restored layout of {layout.entries.Count} transforms");
    }

    /// <summary>
    /// Drops the stored layout, the content itself stays where it is
    /// </summary>
    public void ClearLayout()
    {
        PlayerPrefs.DeleteKey(LayoutKey);
        PlayerPrefs.Save();
    }

    private IEnumerable<Transform> AuthoredTransforms()
    {
        foreach (FurnitureGroup entry in furniture)
        {
            if (entry.group != null)
                yield return entry.group;

            if (entry.scaleTarget != null)
                yield return entry.scaleTarget;

            foreach (Transform prop in entry.props)
                if (prop != null)
                    yield return prop;
        }
    }

    // Hierarchy path below the root, unlike an instance id it survives a scene reload
    private static string PathFrom(Transform root, Transform target)
    {
        string path = target.name;
        for (Transform parent = target.parent; parent != null && parent != root; parent = parent.parent)
            path = parent.name + "/" + path;

        return path;
    }

    #endregion

    public void ReloadScene()
    {
        string currentSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        UnityEngine.SceneManagement.SceneManager.LoadScene(currentSceneName);
    }
}
