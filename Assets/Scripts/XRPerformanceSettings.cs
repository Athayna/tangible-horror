using UnityEngine;

/// <summary>
/// Inspector access to the two Quest performance settings that Meta only exposes as static C#
/// properties, with no UI anywhere: fixed foveated rendering and the display refresh rate.
/// Put this on any GameObject in the scene, set the values, done.
///
/// Foveated rendering renders the periphery at lower resolution. On Quest that is typically the
/// largest free GPU saving there is, at very low visible cost, and it is off by default.
/// </summary>
public class XRPerformanceSettings : MonoBehaviour
{
    [Header("Foveated rendering")]
    [Tooltip("Applies the level below on start. Off by default in the runtime, so this has to be set " +
             "explicitly - there is no project setting for it.")]
    [SerializeField] private bool applyFoveatedRendering = true;

    [Tooltip("Higher levels save more GPU and blur the periphery more. High is a good starting point " +
             "when GPU bound.")]
    [SerializeField] private OVRManager.FoveatedRenderingLevel foveatedRenderingLevel = OVRManager.FoveatedRenderingLevel.High;

    [Tooltip("Lets the system lower the level again when there is GPU headroom. Recommended.")]
    [SerializeField] private bool dynamicFoveatedRendering = true;

    [Header("Display refresh rate")]
    [Tooltip("Only enable this if you actually want to change the rate. A lower rate means more GPU " +
             "budget per frame: 72 Hz gives 13.9 ms, 90 Hz only 11.1 ms.")]
    [SerializeField] private bool applyDisplayFrequency;

    [SerializeField] private float displayFrequency = 72f;

    [Header("Debug")]
    [Tooltip("Logs what was actually applied, so the result can be verified without writing code.")]
    [SerializeField] private bool logResult = true;

    private void Start()
    {
        Apply();
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        // The runtime can reset foveation after standby, so reapply when the app comes back.
        if (hasFocus)
        {
            Apply();
        }
    }

    /// <summary>Applies the configured settings. Safe to call at any time.</summary>
    public void Apply()
    {
        if (applyFoveatedRendering)
        {
            if (OVRManager.fixedFoveatedRenderingSupported)
            {
                OVRManager.foveatedRenderingLevel = foveatedRenderingLevel;
                OVRManager.useDynamicFoveatedRendering = dynamicFoveatedRendering;

                if (logResult)
                {
                    Debug.Log($"[XRPerformanceSettings] Foveated rendering: requested {foveatedRenderingLevel}, " +
                              $"active {OVRManager.foveatedRenderingLevel}, dynamic {OVRManager.useDynamicFoveatedRendering}.", this);
                }
            }
            else if (logResult)
            {
                Debug.LogWarning("[XRPerformanceSettings] Fixed foveated rendering is not supported here. " +
                                 "On device it needs the Meta XR Foveation OpenXR feature enabled.", this);
            }
        }

        if (applyDisplayFrequency && OVRManager.display != null)
        {
            OVRManager.display.displayFrequency = displayFrequency;

            if (logResult)
            {
                Debug.Log($"[XRPerformanceSettings] Display frequency: requested {displayFrequency} Hz, " +
                          $"active {OVRManager.display.displayFrequency} Hz.", this);
            }
        }
    }
}
