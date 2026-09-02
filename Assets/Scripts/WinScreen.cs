using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// World-space popup that fades in front of the player once the study task is finished.
/// The canvas is built at runtime so no scene setup is needed: drop this on the GameManager
/// and call <see cref="Show"/>.
/// </summary>
public class WinScreen : MonoBehaviour
{
    [SerializeField, TextArea] private string message = "You escaped!\n\nThank you for participating in our study.";
    [SerializeField] private float distance = 0.5f;
    [SerializeField] private float fadeDuration = 1.5f;
    [SerializeField] private float followSpeed = 2f;
    [SerializeField] private Color panelColor = new Color(0f, 0f, 0f, 0.85f);

    private CanvasGroup _group;
    private Transform _head;

    public void Show()
    {
        if (_group) return;

        Camera cam = Camera.main;
        if (!cam)
        {
            Debug.LogWarning("WinScreen: no camera tagged MainCamera, cannot show the popup.");
            return;
        }

        _head = cam.transform;
        _group = Build();
        _group.transform.SetPositionAndRotation(TargetPosition(), TargetRotation());
        StartCoroutine(FadeIn());
    }

    private CanvasGroup Build()
    {
        GameObject root = new GameObject("WinScreen", typeof(Canvas), typeof(CanvasGroup));
        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        RectTransform rect = (RectTransform)root.transform;
        rect.sizeDelta = new Vector2(600f, 300f);
        rect.localScale = Vector3.one * 0.001f; // 0.6 m x 0.3 m

        Image panel = new GameObject("Panel", typeof(Image)).GetComponent<Image>();
        Stretch(panel.rectTransform, root.transform);
        panel.color = panelColor;

        Text text = new GameObject("Message", typeof(Text)).GetComponent<Text>();
        Stretch(text.rectTransform, root.transform);
        text.rectTransform.offsetMin = new Vector2(40f, 40f);
        text.rectTransform.offsetMax = new Vector2(-40f, -40f);
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 42;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.text = message;

        CanvasGroup group = root.GetComponent<CanvasGroup>();
        group.alpha = 0f;
        return group;
    }

    private static void Stretch(RectTransform rect, Transform parent)
    {
        rect.SetParent(parent, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private IEnumerator FadeIn()
    {
        for (float t = 0f; t < fadeDuration; t += Time.deltaTime)
        {
            _group.alpha = t / fadeDuration;
            yield return null;
        }

        _group.alpha = 1f;
    }

    private void LateUpdate()
    {
        if (!_group) return;

        float step = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
        _group.transform.SetPositionAndRotation(
            Vector3.Lerp(_group.transform.position, TargetPosition(), step),
            Quaternion.Slerp(_group.transform.rotation, TargetRotation(), step));
    }

    /// <summary>Eye height, <see cref="distance"/> metres ahead, ignoring how far up or down the player looks.</summary>
    private Vector3 TargetPosition() => _head.position + HeadForward() * distance;

    private Quaternion TargetRotation() => Quaternion.LookRotation(HeadForward());

    private Vector3 HeadForward()
    {
        Vector3 forward = _head.forward;
        forward.y = 0f;
        return forward.sqrMagnitude < 0.0001f ? Vector3.forward : forward.normalized;
    }
}
