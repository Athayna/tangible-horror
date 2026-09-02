using System;
using System.Collections.Generic;
using Meta.XR;
using TMPro;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace TangibleHorror.AprilTagTracking
{
    /// <summary>
    /// Reads frames from the Meta Passthrough Camera, has them detected on a background thread by
    /// <see cref="AprilTagPipeline"/> and reports world-space marker poses to every
    /// <see cref="AprilTagTarget"/> in the scene.
    ///
    /// Self-contained test module: deleting the AprilTagTracking folder removes the whole feature,
    /// nothing in the rest of the project depends on it.
    /// </summary>
    [DefaultExecutionOrder(100)] // PassthroughCameraAccess runs at -100, so its texture is fresh here
    public class AprilTagTracker : MonoBehaviour
    {
        /// <summary>Which space <see cref="PassthroughCameraAccess.GetCameraPose"/> reports in.</summary>
        public enum CameraPoseSpace
        {
            /// <summary>Detect it on the first detection by comparing against the center eye anchor.</summary>
            Auto,

            /// <summary>Pose is relative to OVRCameraRig/TrackingSpace and needs converting.</summary>
            TrackingSpace,

            /// <summary>Pose is already in world space.</summary>
            WorldSpace
        }

        /// <summary>Where the focal length used for pose estimation comes from.</summary>
        public enum FocalSource
        {
            /// <summary>Use the focal length the camera reports.</summary>
            Intrinsics,

            /// <summary>Replace the reported focal length with Override Vertical Fov Degrees.</summary>
            OverrideVerticalFov
        }

        /// <summary>Physical size of a range of marker IDs, for mixing differently sized markers.</summary>
        [Serializable]
        public class TagSizeOverride
        {
            [Tooltip("First marker ID this size applies to (inclusive).")]
            public int FirstTagId;

            [Tooltip("Last marker ID this size applies to (inclusive). Same as First for a single marker.")]
            public int LastTagId;

            [Tooltip("Edge length of the black square for these markers, in meters.")]
            public float TagSizeMeters = 0.028f;
        }

        /// <summary>Must also be present in Assets/Plugins/Android/AndroidManifest.xml.</summary>
        public const string HeadsetCameraPermission = "horizonos.permission.HEADSET_CAMERA";

        private const string TagSizeCalibrationKey = "AprilTagTracker.TagSizeMeters";

        [Header("Camera")]
        [Tooltip("Source of the camera frames. Auto-found on this GameObject or in the scene if left empty.")]
        [SerializeField] private PassthroughCameraAccess passthroughCamera;

        [Tooltip("Which space GetCameraPose() reports in. If the camera rig does not sit at the world " +
                 "origin, guessing wrong offsets every marker by exactly the rig transform. Auto compares " +
                 "the reported pose against OVRCameraRig.centerEyeAnchor and picks the interpretation that " +
                 "actually lands on the head. Leave it on Auto.")]
        [SerializeField] private CameraPoseSpace cameraPoseSpace = CameraPoseSpace.Auto;

        [Tooltip("Leave empty: taken from OVRCameraRig.trackingSpace automatically.")]
        [SerializeField] private Transform trackingSpaceOverride;

        [Tooltip("Where the focal length comes from. Intrinsics = as reported by the camera. Override = " +
                 "replace it with the field of view below, keeping the reported principal point. Use the " +
                 "override when the reported intrinsics do not match the delivered image - the symptom is " +
                 "that markers only sit right at a tag size that differs from the measured one.")]
        [SerializeField] private FocalSource focalSource = FocalSource.Intrinsics;

        [Tooltip("Vertical field of view of the camera image, used when Focal Source is Override. Scales " +
                 "depth only - X and Y contain no focal length - which is why this and not Tag Size Meters " +
                 "is the screw to turn: the tag size is measurable with a ruler, the focal length is not. " +
                 "Too far away: raise it. Too close: lower it. Calibrated on Quest 3 at 640x480: about 71 " +
                 "degrees. Takes effect immediately while the app runs.")]
        [Range(30f, 120f)]
        [SerializeField] private float overrideVerticalFovDegrees = 71f;

        [Tooltip("Used ONLY when the camera reports no usable intrinsics at all. Same meaning as the " +
                 "override above. The status line shows which path was taken.")]
        [Range(30f, 120f)]
        [SerializeField] private float assumedVerticalFovDegrees = 73f;

        [Header("Marker")]
        [Tooltip("Physical edge length of the BLACK SQUARE in meters (7 of the 9 cells of the printed image). " +
                 "This is purely a depth scale: too large puts markers too far away, too small too close. " +
                 "Takes effect immediately while the app runs - editing it in play mode is the calibration " +
                 "workflow. Also available as IncreaseTagSize / DecreaseTagSize.")]
        [SerializeField] private float tagSizeMeters = 0.07f;

        [Tooltip("Per-ID sizes for markers that are NOT the size above. Needed as soon as differently " +
                 "sized markers are mixed - e.g. one prop with a single large marker and another with four " +
                 "small ones. The size is applied during pose estimation, where only the ID is known, so it " +
                 "cannot come from the target. Adding, editing and removing entries takes effect immediately " +
                 "while the app runs.")]
        [SerializeField] private List<TagSizeOverride> tagSizeOverrides = new List<TagSizeOverride>();

        [Tooltip("Step of IncreaseTagSize / DecreaseTagSize in millimeters.")]
        [SerializeField] private float tagSizeStepMillimeters = 1f;

        [Tooltip("Keeps a runtime calibration across restarts. Off means the value above always wins, and " +
                 "any previously stored calibration is deleted on start - so the inspector is the single " +
                 "source of truth. Only switch this on while calibrating with the buttons.")]
        [SerializeField] private bool persistTagSizeCalibration;

        [Tooltip("Label that shows the current tag size. Updates immediately on every change, so you can " +
                 "read the value while pressing the calibration buttons and copy it into the inspector later.")]
        [SerializeField] private TMP_Text tagSizeLabel;

        [Tooltip("Text around the value, {0} is millimeters, {1} is meters.")]
        [SerializeField] private string tagSizeLabelFormat = "Tag {0:F1} mm  ({1:F4} m)";

        [Header("Detection")]
        [Tooltip("Image downscale before quad detection. 1 = most accurate and slowest, 2-3 is the sweet " +
                 "spot on Quest. Higher values cost detection range and tolerance against oblique angles.")]
        [Range(1, 8)]
        [SerializeField] private int decimation = 2;

        [Tooltip("Detections per second while at least one marker is being seen. Detection itself no longer " +
                 "blocks the frame, but every detection costs one image readback plus a memcpy on the main " +
                 "thread, so this is what drives the remaining frame cost.")]
        [Range(1f, 30f)]
        [SerializeField] private float detectionsPerSecond = 15f;

        [Tooltip("Detections per second while nothing is detected. Keep it clearly lower: an empty image is " +
                 "cheap for the worker, so the rate would otherwise run into the cap and cost readbacks " +
                 "exactly while looking around. Costs a little pickup delay when a marker enters view.")]
        [Range(1f, 30f)]
        [SerializeField] private float idleDetectionsPerSecond = 6f;

        [Tooltip("Seconds after the last detection before the idle rate takes over.")]
        [SerializeField] private float idleAfterSeconds = 0.5f;

        [Tooltip("Threads AprilTag may spawn internally. Keep this at 1: then all detection work runs on the " +
                 "pipeline's own below-normal-priority thread, which yields to the main and render thread. " +
                 "Higher values spawn native threads at normal priority that compete with rendering - " +
                 "detections finish faster, but the image starts stuttering again.")]
        [Range(1, 8)]
        [SerializeField] private int detectorThreadCount = 1;

        [Tooltip("Sub-pixel edge refinement. Costs a little, clearly improves pose stability.")]
        [SerializeField] private bool refineEdges = true;

        [Tooltip("Gaussian blur before detection. 0 = off. Around 0.8 can help with noisy or slightly " +
                 "blurred images at the cost of range.")]
        [Range(0f, 2f)]
        [SerializeField] private float quadSigma;

        [Tooltip("Sharpening applied while decoding the bits. AprilTag's default is 0.25. Raising it to " +
                 "0.5-0.75 gets marginal tags decoded that would otherwise be rejected - blurred by motion, " +
                 "low contrast, or seen at a steep angle. Too high produces false detections.")]
        [Range(0f, 2f)]
        [SerializeField] private float decodeSharpening = 0.25f;

        [Tooltip("Rejects weak detections. 0 = accept everything. Raise it (20-40) if single frames jump " +
                 "to nonsense poses.")]
        [SerializeField] private float minimumDecisionMargin;

        [Tooltip("Flips the camera image before detection. If nothing is ever detected even though a tag " +
                 "clearly fills the view, toggle this once - a mirrored tag cannot be decoded.")]
        [SerializeField] private bool flipImageVertically;

        [Tooltip("Correction of the marker coordinate frame, applied to every detected pose. The frame is " +
                 "+X right, +Y up, +Z INTO the marker surface, so this normally stays zero.")]
        [SerializeField] private Vector3 markerFrameEuler;

        // Camera Position Offset / Camera Rotation Offset used to sit here. They were diagnostic knobs for a
        // marker offset whose real cause turned out to be the tag size convention (5 of 9 cells, not 7) plus
        // a 96 % print scale. With those fixed the camera pose needs no correction at all, so the fields are
        // gone rather than left at zero for someone to rediscover and mistune.

        [Tooltip("Capture latency of the passthrough camera in milliseconds - the part this script cannot " +
                 "measure. Meta documents 20-40 ms. It is added to the measured pipeline latency and handed " +
                 "to AprilTagTarget, which can extrapolate the pose by exactly that age.")]
        [Range(0f, 100f)]
        [SerializeField] private float cameraLatencyMilliseconds = 30f;

        [Header("Debug")]
        [Tooltip("Logs a detection summary. Read it with: adb logcat -s Unity")]
        [SerializeField] private bool logStatistics = true;
        [SerializeField] private float logIntervalSeconds = 2f;

        [Tooltip("Adds the raw marker axes relative to the camera axes plus the measured distance.")]
        [SerializeField] private bool logMarkerAxes = true;

        [Tooltip("Optional label that shows the same numbers inside the headset - no adb needed.")]
        [SerializeField] private TMP_Text statusLabel;

        /// <summary>Raised for every detected marker, with the marker pose in world space.</summary>
        public event Action<int, Pose> TagObserved;

        public bool HasCameraPermission { get; private set; }
        public bool IsRunning => _pipeline != null && passthroughCamera != null && passthroughCamera.IsPlaying;
        public int LastDetectionCount { get; private set; }
        public float LastDetectionMilliseconds => _pipeline != null ? _pipeline.LastDetectionMilliseconds : 0f;
        public Vector2Int CurrentResolution => new Vector2Int(_width, _height);
        public float TagSizeMeters => tagSizeMeters;

        /// <summary>
        /// Size actually used for one ID, so the debug plate is drawn at the same size the pose was solved
        /// with. Without this the plate showed the global size even for IDs that have an override.
        /// </summary>
        public float GetTagSizeMeters(int id)
        {
            float[] map = _tagSizeById;
            if (map != null && id >= 0 && id < map.Length && map[id] > 0f)
            {
                return map[id];
            }

            return tagSizeMeters;
        }

        private static readonly List<AprilTagTarget> Targets = new List<AprilTagTarget>();

        private AprilTagPipeline _pipeline;
        private readonly List<AprilTagPipeline.Observation> _observations = new List<AprilTagPipeline.Observation>();

        private NativeArray<Color32> _gpuPixels;
        private int _width;
        private int _height;
        private int _pipelineDecimation;
        private int _pipelineThreadCount;
        private bool _pipelineRefineEdges;
        private float _pipelineQuadSigma;
        private float _pipelineDecodeSharpening;

        private float[] _tagSizeById;

        // Last values that were pushed into the detector, so inspector edits can be picked up per frame.
        private float _appliedTagSize = float.NaN;
        private int _appliedOverridesHash;
        private FocalSource _appliedFocalSource;
        private float _appliedOverrideFov = float.NaN;
        private float _appliedAssumedFov = float.NaN;

        private double _focalX;
        private double _focalY;
        private double _centerX;
        private double _centerY;

        /// <summary>How far Meta's reported principal point sits from the image center, in pixels.</summary>
        private Vector2 _reportedCenterOffset;

        private bool _readbackPending;
        private float _lastRequestTime;
        private Pose _capturePose;
        private Pose _submittedPose;
        private float _submitTime;
        private float _lastSuccessfulDetectionTime = float.NegativeInfinity;
        private int _detectionPass;

        /// <summary>Age of the most recent marker pose in seconds: measured pipeline delay plus camera latency.</summary>
        public float LastPoseAgeSeconds { get; private set; }

        private float _nextLogTime;
        private int _detectionsSinceLog;

        private OVRCameraRig _cameraRig;
        private bool _cameraRigSearched;
        private CameraPoseSpace _resolvedPoseSpace = CameraPoseSpace.Auto;
        private string _focalSourceLabel = "?";

        private int _diagTagId;
        private float _diagDotForward;
        private float _diagDotUp;
        private float _diagDotRight;
        private float _diagDistance;
        private float _diagMargin;
        private bool _hasDiagnostics;

        internal static void Register(AprilTagTarget target)
        {
            if (!Targets.Contains(target))
            {
                Targets.Add(target);
            }
        }

        internal static void Unregister(AprilTagTarget target)
        {
            Targets.Remove(target);
        }

        #region Runtime calibration

        /// <summary>Enlarges the assumed tag size, which pushes markers further away.</summary>
        public void IncreaseTagSize()
        {
            SetTagSizeMeters(tagSizeMeters + Mathf.Max(0.0001f, tagSizeStepMillimeters) * 0.001f);
        }

        /// <summary>Shrinks the assumed tag size, which pulls markers closer.</summary>
        public void DecreaseTagSize()
        {
            SetTagSizeMeters(tagSizeMeters - Mathf.Max(0.0001f, tagSizeStepMillimeters) * 0.001f);
        }

        /// <summary>
        /// Sets the assumed tag size. Depth scales linearly with it, so if a marker sits 15 cm too far
        /// away at 50 cm, the value has to shrink by 15/65.
        /// </summary>
        public void SetTagSizeMeters(float meters)
        {
            tagSizeMeters = Mathf.Clamp(meters, 0.005f, 1f);

            if (persistTagSizeCalibration)
            {
                PlayerPrefs.SetFloat(TagSizeCalibrationKey, tagSizeMeters);
                PlayerPrefs.Save();
            }

            RefreshTagSizeLabel();
            Debug.Log($"[AprilTagTracker] Tag size set to {tagSizeMeters * 1000f:F1} mm.", this);
        }

        /// <summary>Drops a stored calibration and goes back to the value from the inspector.</summary>
        public void ClearTagSizeCalibration()
        {
            PlayerPrefs.DeleteKey(TagSizeCalibrationKey);
            PlayerPrefs.Save();
            Debug.Log("[AprilTagTracker] Tag size calibration cleared, inspector value applies after restart.", this);
        }

        /// <summary>
        /// Flattens the overrides into a lookup by ID, so the worker thread can read it without locking.
        /// A zero entry means "use the default size".
        /// </summary>
        public void RebuildTagSizeMap()
        {
            int highest = -1;
            foreach (TagSizeOverride entry in tagSizeOverrides)
            {
                if (entry != null)
                {
                    highest = Mathf.Max(highest, Mathf.Max(entry.FirstTagId, entry.LastTagId));
                }
            }

            if (highest < 0)
            {
                _tagSizeById = null;
                return;
            }

            var map = new float[highest + 1];
            foreach (TagSizeOverride entry in tagSizeOverrides)
            {
                if (entry == null || entry.TagSizeMeters <= 0f)
                {
                    continue;
                }

                int first = Mathf.Min(entry.FirstTagId, entry.LastTagId);
                int last = Mathf.Max(entry.FirstTagId, entry.LastTagId);

                for (int id = Mathf.Max(0, first); id <= last && id < map.Length; id++)
                {
                    map[id] = entry.TagSizeMeters;
                }
            }

            _tagSizeById = map;
        }

        /// <summary>
        /// Picks up inspector changes while the app runs. Tag size, the per ID overrides and the focal
        /// source only reached the detector when the pipeline was rebuilt, so editing them in play mode
        /// looked like it did nothing - over Link that is exactly the calibration workflow. Detection
        /// itself needs no rebuild: size, overrides and intrinsics are handed to the worker per frame.
        /// </summary>
        private void ApplyLiveTuning()
        {
            int overridesHash = ComputeOverridesHash();

            if (overridesHash != _appliedOverridesHash || !Mathf.Approximately(_appliedTagSize, tagSizeMeters))
            {
                _appliedOverridesHash = overridesHash;
                _appliedTagSize = tagSizeMeters;
                RebuildTagSizeMap();
                RefreshTagSizeLabel();
            }

            if (focalSource != _appliedFocalSource
                || !Mathf.Approximately(_appliedOverrideFov, overrideVerticalFovDegrees)
                || !Mathf.Approximately(_appliedAssumedFov, assumedVerticalFovDegrees))
            {
                _appliedFocalSource = focalSource;
                _appliedOverrideFov = overrideVerticalFovDegrees;
                _appliedAssumedFov = assumedVerticalFovDegrees;
                ComputeIntrinsics();
            }
        }

        private void MarkTuningApplied()
        {
            _appliedOverridesHash = ComputeOverridesHash();
            _appliedTagSize = tagSizeMeters;
            _appliedFocalSource = focalSource;
            _appliedOverrideFov = overrideVerticalFovDegrees;
            _appliedAssumedFov = assumedVerticalFovDegrees;
        }

        /// <summary>Change detector for the override list. A handful of entries, once per frame.</summary>
        private int ComputeOverridesHash()
        {
            int hash = 17;

            if (tagSizeOverrides == null)
            {
                return hash;
            }

            foreach (TagSizeOverride entry in tagSizeOverrides)
            {
                if (entry == null)
                {
                    hash *= 31;
                    continue;
                }

                hash = hash * 31 + entry.FirstTagId;
                hash = hash * 31 + entry.LastTagId;
                hash = hash * 31 + entry.TagSizeMeters.GetHashCode();
            }

            return hash;
        }

        private void RefreshTagSizeLabel()
        {
            if (tagSizeLabel == null)
            {
                return;
            }

            string format = string.IsNullOrEmpty(tagSizeLabelFormat) ? "{0:F1} mm" : tagSizeLabelFormat;
            tagSizeLabel.text = string.Format(format, tagSizeMeters * 1000f, tagSizeMeters);
        }

        #endregion

        private void Awake()
        {
            if (passthroughCamera == null)
            {
                passthroughCamera = GetComponent<PassthroughCameraAccess>();
            }

            if (passthroughCamera == null)
            {
                passthroughCamera = FindFirstObjectByType<PassthroughCameraAccess>();
            }

            if (passthroughCamera == null)
            {
                Debug.LogError("[AprilTagTracker] No PassthroughCameraAccess found. Add the component to this " +
                               "GameObject (or anywhere in the scene) and assign it.", this);
                enabled = false;
                return;
            }

            if (persistTagSizeCalibration)
            {
                if (PlayerPrefs.HasKey(TagSizeCalibrationKey))
                {
                    tagSizeMeters = PlayerPrefs.GetFloat(TagSizeCalibrationKey, tagSizeMeters);
                    Debug.Log($"[AprilTagTracker] Restored calibrated tag size {tagSizeMeters * 1000f:F1} mm.", this);
                }
            }
            else if (PlayerPrefs.HasKey(TagSizeCalibrationKey))
            {
                // Persistence is off, so a leftover calibration must not silently override the inspector.
                PlayerPrefs.DeleteKey(TagSizeCalibrationKey);
                PlayerPrefs.Save();
                Debug.Log($"[AprilTagTracker] Discarded stored tag size calibration, using the inspector " +
                          $"value {tagSizeMeters * 1000f:F1} mm.", this);
            }

            RefreshTagSizeLabel();
        }

        private void Start()
        {
            HasCameraPermission = HasPermission();
            if (!HasCameraPermission)
            {
                RequestPermission();
            }

            if (!PassthroughCameraAccess.IsSupported)
            {
                Debug.LogWarning("[AprilTagTracker] Passthrough Camera Access is not supported here. " +
                                 "Needs a Quest 3 / 3S on Horizon OS v74+ (in the Editor: Meta Horizon Link v85+).", this);
            }
        }

        private void OnDisable()
        {
            if (_readbackPending)
            {
                AsyncGPUReadback.WaitAllRequests();
                _readbackPending = false;
            }

            ReleasePipeline();
        }

        private void Update()
        {
            if (passthroughCamera == null)
            {
                return;
            }

            if (!HasCameraPermission)
            {
                HasCameraPermission = HasPermission();
                if (!HasCameraPermission)
                {
                    return;
                }
            }

            if (!passthroughCamera.IsPlaying || !EnsurePipeline())
            {
                return;
            }

            ApplyLiveTuning();
            CollectResults();
            LogStatistics();
            MaybeRequestFrame();
        }

        private void MaybeRequestFrame()
        {
            if (_readbackPending || _pipeline.IsBusy)
            {
                return;
            }

            bool tracking = Time.unscaledTime - _lastSuccessfulDetectionTime <= Mathf.Max(0f, idleAfterSeconds);
            float rate = Mathf.Max(1f, tracking ? detectionsPerSecond : idleDetectionsPerSecond);

            if (Time.unscaledTime - _lastRequestTime < 1f / rate)
            {
                return;
            }

            if (!passthroughCamera.IsUpdatedThisFrame)
            {
                return;
            }

            Texture texture = passthroughCamera.GetTexture();
            if (texture == null)
            {
                return;
            }

            // The pose belongs to the timestamp of this image, so head motion between capture and now
            // does not drag the marker along.
            _capturePose = passthroughCamera.GetCameraPose();
            _lastRequestTime = Time.unscaledTime;
            _readbackPending = true;
            AsyncGPUReadback.RequestIntoNativeArray(ref _gpuPixels, texture, 0, OnReadbackComplete);
        }

        private void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            _readbackPending = false;

            if (request.hasError || _pipeline == null)
            {
                return;
            }

            if (_pipeline.Submit(_gpuPixels, flipImageVertically, tagSizeMeters, _tagSizeById,
                    _focalX, _focalY, _centerX, _centerY, minimumDecisionMargin))
            {
                _submittedPose = _capturePose;
                _submitTime = Time.unscaledTime;
            }
        }

        private void CollectResults()
        {
            if (!_pipeline.TryCollect(_observations))
            {
                return;
            }

            _detectionsSinceLog++;
            _detectionPass++;
            LastDetectionCount = _observations.Count;

            // Everything the pose is behind reality: waiting for the worker plus the camera's own capture
            // latency. AprilTagTarget can extrapolate by exactly this instead of a hand tuned constant.
            LastPoseAgeSeconds = Time.unscaledTime - _submitTime + cameraLatencyMilliseconds * 0.001f;

            if (_observations.Count > 0)
            {
                _lastSuccessfulDetectionTime = Time.unscaledTime;
            }

            Pose cameraPose = ToWorldSpace(_submittedPose);
            Quaternion frameCorrection = Quaternion.Euler(markerFrameEuler);

            for (int index = 0; index < _observations.Count; index++)
            {
                AprilTagPipeline.Observation observation = _observations[index];

                Quaternion rawRotation = cameraPose.rotation * observation.RotationInCamera;
                Quaternion rotation = rawRotation * frameCorrection;
                Vector3 position = cameraPose.position + cameraPose.rotation * observation.PositionInCamera;
                var pose = new Pose(position, rotation);

                if (index == 0)
                {
                    CaptureAxisDiagnostics(observation, rawRotation, cameraPose.rotation);
                }

                TagObserved?.Invoke(observation.Id, pose);

                for (int i = Targets.Count - 1; i >= 0; i--)
                {
                    AprilTagTarget target = Targets[i];
                    if (target == null)
                    {
                        Targets.RemoveAt(i);
                    }
                    else
                    {
                        // The target ignores IDs it does not care about.
                        target.OnMarkerObserved(observation.Id, pose, _detectionPass, LastPoseAgeSeconds,
                            observation.DecisionMargin, observation.PositionInCamera.magnitude);
                    }
                }
            }
        }

        private bool EnsurePipeline()
        {
            Vector2Int resolution = passthroughCamera.CurrentResolution;
            if (resolution.x <= 0 || resolution.y <= 0)
            {
                return false;
            }

            bool unchanged = _pipeline != null
                             && resolution.x == _width
                             && resolution.y == _height
                             && decimation == _pipelineDecimation
                             && detectorThreadCount == _pipelineThreadCount
                             && refineEdges == _pipelineRefineEdges
                             && Mathf.Approximately(quadSigma, _pipelineQuadSigma)
                             && Mathf.Approximately(decodeSharpening, _pipelineDecodeSharpening);

            if (unchanged)
            {
                return true;
            }

            ReleasePipeline();

            _width = resolution.x;
            _height = resolution.y;
            _pipelineDecimation = decimation;
            _pipelineThreadCount = detectorThreadCount;
            _pipelineRefineEdges = refineEdges;
            _pipelineQuadSigma = quadSigma;
            _pipelineDecodeSharpening = decodeSharpening;

            ComputeIntrinsics();
            RebuildTagSizeMap();
            MarkTuningApplied();

            _gpuPixels = new NativeArray<Color32>(_width * _height, Allocator.Persistent);
            _pipeline = new AprilTagPipeline(_width, _height, _pipelineDecimation, _pipelineThreadCount,
                _pipelineRefineEdges, _pipelineQuadSigma, _pipelineDecodeSharpening);

            Debug.Log($"[AprilTagTracker] Pipeline ready: {_width}x{_height}, decimation {_pipelineDecimation}, " +
                      $"focal ({_focalX:F1}, {_focalY:F1}) {_focalSourceLabel}, " +
                      $"center ({_centerX:F1}, {_centerY:F1}) image center, " +
                      $"intrinsics would offset it by ({_reportedCenterOffset.x:F1}, {_reportedCenterOffset.y:F1}) px, " +
                      $"tag size {tagSizeMeters * 1000f:F1} mm.", this);
            return true;
        }

        private void ReleasePipeline()
        {
            _pipeline?.Dispose();
            _pipeline = null;

            if (_gpuPixels.IsCreated)
            {
                _gpuPixels.Dispose();
            }

            _width = 0;
            _height = 0;
        }

        /// <summary>
        /// Maps Meta's sensor intrinsics onto the image we hand AprilTag. Meta reports focal length and
        /// principal point in sensor pixels, and lower resolutions crop the sensor, so both have to be
        /// scaled by the crop. Meta counts sensor rows from the bottom, AprilTag from the top.
        /// </summary>
        private void ComputeIntrinsics()
        {
            PassthroughCameraAccess.CameraIntrinsics intrinsics = passthroughCamera.Intrinsics;
            Vector2 sensor = intrinsics.SensorResolution;

            if (sensor.x <= 0f || sensor.y <= 0f || intrinsics.FocalLength.y <= 0f)
            {
                // No usable intrinsics at all: derive everything from the assumed field of view.
                double focal = FocalFromVerticalFov(assumedVerticalFovDegrees);
                _focalX = focal;
                _focalY = focal;
                _centerX = _width * 0.5f;
                _centerY = _height * 0.5f;
                _reportedCenterOffset = Vector2.zero;
                _focalSourceLabel = "ASSUMED FOV";
                Debug.LogWarning($"[AprilTagTracker] No usable camera intrinsics (sensor {sensor}, focal " +
                                 $"{intrinsics.FocalLength}). Using the assumed FOV of " +
                                 $"{assumedVerticalFovDegrees:F1} deg -> focal {focal:F1} px. Verify this: " +
                                 "a wrong focal length skews depth and lateral placement together.", this);
                return;
            }

            // Raw, before any mapping of ours. This is the only way to tell whether a focal length that does
            // not match reality is Meta's number or our crop model. Meta's own ViewportPointToLocalRay
            // divides by FocalLength after mapping the viewport point into SENSOR pixels, so FocalLength and
            // PrincipalPoint are in sensor pixels and have to be scaled by the crop - which is what we do
            // below. Vertical FOV straight from the raw numbers: 2*atan(sensor.y / 2 / FocalLength.y).
            float rawFovY = 2f * Mathf.Atan(sensor.y * 0.5f / intrinsics.FocalLength.y) * Mathf.Rad2Deg;
            Debug.Log($"[AprilTagTracker] Raw intrinsics as reported by PassthroughCameraAccess:" +
                      $"\n  SensorResolution  {sensor}" +
                      $"\n  CurrentResolution {_width}x{_height}" +
                      $"\n  FocalLength       {intrinsics.FocalLength}  (sensor px)" +
                      $"\n  PrincipalPoint    {intrinsics.PrincipalPoint}  (sensor px)" +
                      $"\n  vertical FOV from those numbers: {rawFovY:F1} deg" +
                      $"\n  sensor aspect {sensor.x / sensor.y:F4}, image aspect {(float)_width / _height:F4}", this);

            // Same crop model as PassthroughCameraAccess.CalcSensorCropRegion
            var scale = new Vector2(_width / sensor.x, _height / sensor.y);
            scale /= Mathf.Max(scale.x, scale.y);
            float cropWidth = sensor.x * scale.x;
            float cropHeight = sensor.y * scale.y;
            float cropX = sensor.x * (1f - scale.x) * 0.5f;
            float cropY = sensor.y * (1f - scale.y) * 0.5f;

            float scaleX = _width / cropWidth;
            float scaleY = _height / cropHeight;

            _focalX = intrinsics.FocalLength.x * scaleX;
            _focalY = intrinsics.FocalLength.y * scaleY;

            // Principal point: the image center on purpose, not the one Meta reports.
            //
            // The two parameters fail very differently. From Z = f*S/s, X = (u-cx)*S/s it follows that a
            // wrong focal length can only move the pose along the view axis - X and Y contain no f at all -
            // and that is correctable afterwards with Tag Size or the FOV override. A wrong principal point
            // moves the pose sideways by (dc / f) * distance and skews the rotation through the same
            // homography, and nothing downstream can correct it. Mapping Meta's sensor principal point onto
            // the cropped image requires the exact crop rectangle and row order; getting that wrong costs
            // more than the small real off-center offset of the physical lens. keijiro's own TagDetector
            // assumes the image center as well.
            //
            // The mapping that would use the reported point, kept for the record:
            //   _centerX = (intrinsics.PrincipalPoint.x - cropX) * scaleX;
            //   _centerY = _height - (intrinsics.PrincipalPoint.y - cropY) * scaleY; // Meta counts rows bottom up
            _centerX = _width * 0.5;
            _centerY = _height * 0.5;

            // Kept visible in the log: this is the lateral error we would be taking on, in pixels. Multiply
            // by distance / focal to get metres - at 40 cm and focal 434 px one pixel is 0.92 mm.
            _reportedCenterOffset = new Vector2(
                (float)((intrinsics.PrincipalPoint.x - cropX) * scaleX - _centerX),
                (float)(_height - (intrinsics.PrincipalPoint.y - cropY) * scaleY - _centerY));

            _focalSourceLabel = "intrinsics";

            if (focalSource != FocalSource.OverrideVerticalFov)
            {
                return;
            }

            // Replace only the focal length, keeping the reported fx/fy ratio - the override is a single
            // number and must not silently change the pixel aspect.
            double overridden = FocalFromVerticalFov(overrideVerticalFovDegrees);
            double factor = overridden / _focalY;
            _focalX *= factor;
            _focalY = overridden;
            _focalSourceLabel = "override";

            Debug.Log($"[AprilTagTracker] Focal override: {overrideVerticalFovDegrees:F1} deg vertical -> " +
                      $"focal {overridden:F1} px (intrinsics reported {intrinsics.FocalLength.y * scaleY:F1} px, " +
                      $"factor {factor:F3}).", this);
        }

        private double FocalFromVerticalFov(float verticalFovDegrees)
        {
            float fov = Mathf.Clamp(verticalFovDegrees, 1f, 179f) * Mathf.Deg2Rad;
            return _height * 0.5f / Mathf.Tan(fov * 0.5f);
        }

        private void CaptureAxisDiagnostics(AprilTagPipeline.Observation observation, Quaternion rawRotation, Quaternion cameraRotation)
        {
            if (!logMarkerAxes)
            {
                return;
            }

            _diagTagId = observation.Id;
            _diagDotForward = Vector3.Dot(rawRotation * Vector3.forward, cameraRotation * Vector3.forward);
            _diagDotUp = Vector3.Dot(rawRotation * Vector3.up, cameraRotation * Vector3.up);
            _diagDotRight = Vector3.Dot(rawRotation * Vector3.right, cameraRotation * Vector3.right);
            _diagDistance = observation.PositionInCamera.magnitude;
            _diagMargin = observation.DecisionMargin;
            _hasDiagnostics = true;
        }

        /// <summary>
        /// What the status line shows for the pose space. It used to print the auto-detected value, which is
        /// only ever set by the Auto path - so setting the mode by hand made the line read "Auto" while the
        /// hand-picked mode was actually in use. Now it names the mode that is really being applied, and
        /// marks the auto-detected one so the two stay distinguishable.
        /// </summary>
        private string EffectivePoseSpaceLabel =>
            cameraPoseSpace != CameraPoseSpace.Auto
                ? cameraPoseSpace.ToString()
                : _resolvedPoseSpace == CameraPoseSpace.Auto
                    ? "Auto (undecided)"
                    : $"{_resolvedPoseSpace} (auto)";

        private Pose ToWorldSpace(Pose reportedPose)
        {
            Transform space = ResolveTrackingSpace();
            if (space == null)
            {
                return reportedPose;
            }

            CameraPoseSpace mode = cameraPoseSpace;
            if (mode == CameraPoseSpace.Auto)
            {
                if (_resolvedPoseSpace == CameraPoseSpace.Auto)
                {
                    _resolvedPoseSpace = DetectCameraPoseSpace(reportedPose, space);
                }

                mode = _resolvedPoseSpace;
            }

            if (mode == CameraPoseSpace.WorldSpace)
            {
                return reportedPose;
            }

            return new Pose(
                space.TransformPoint(reportedPose.position),
                space.rotation * reportedPose.rotation);
        }

        /// <summary>
        /// The reported pose belongs to the passthrough lens, which sits a few centimeters from the center
        /// eye. So of the two possible interpretations, the correct one lands near the head and the wrong
        /// one lands off by the whole rig transform. Measure instead of assuming.
        /// </summary>
        private CameraPoseSpace DetectCameraPoseSpace(Pose reportedPose, Transform space)
        {
            Transform eye = _cameraRig != null ? _cameraRig.centerEyeAnchor : null;
            if (eye == null)
            {
                Debug.LogWarning("[AprilTagTracker] No centerEyeAnchor to compare against, assuming the camera " +
                                 "pose is in tracking space.", this);
                return CameraPoseSpace.TrackingSpace;
            }

            Vector3 transformed = space.TransformPoint(reportedPose.position);
            float asWorld = Vector3.Distance(reportedPose.position, eye.position);
            float asTracking = Vector3.Distance(transformed, eye.position);

            CameraPoseSpace detected = asTracking <= asWorld ? CameraPoseSpace.TrackingSpace : CameraPoseSpace.WorldSpace;
            Debug.Log($"[AprilTagTracker] Camera pose space detected as {detected}. Distance to center eye: " +
                      $"{asTracking:F3} m read as tracking space, {asWorld:F3} m read as world space.", this);

            // The two distances alone cannot say WHICH space the pose is in when neither fits - both being
            // off by about a head height points at a tracking origin mismatch, for instance. The raw vectors
            // can, so they go into the log as well.
            Debug.Log($"[AprilTagTracker] Pose space raw values:" +
                      $"\n  reported camera pos {reportedPose.position:F3}  rot {reportedPose.rotation.eulerAngles:F1}" +
                      $"\n  center eye pos      {eye.position:F3}  rot {eye.rotation.eulerAngles:F1}" +
                      $"\n  space '{space.name}'  pos {space.position:F3}  rot {space.rotation.eulerAngles:F1}" +
                      $"\n  reported through space {transformed:F3}" +
                      // The lens offset is what turns the head pose into the lens pose. A wrong or zero one
                      // is a constant error in the HEAD frame, so it swings the marker when the head rotates
                      // while head translation leaves it alone - unlike a depth error, which does the
                      // opposite. Expect a few centimeters, mostly forward and sideways.
                      $"\n  lens offset from head  {passthroughCamera.Intrinsics.LensOffset.position:F3}  " +
                      $"rot {passthroughCamera.Intrinsics.LensOffset.rotation.eulerAngles:F1}" +
                      $"\n  expected: one of the two candidates lands within about 0.05 m of the center eye.", this);

            // A center eye sitting exactly on the tracking origin with no rotation is not being tracked at
            // all, so both distances measure against a dead reference and the verdict is worthless - that is
            // how this used to flip between WorldSpace and TrackingSpace from run to run. MRUK's
            // GetCameraPose returns GetHeadsetPoseAtTime plus the lens offset, which IS tracking space, so
            // that is the answer whenever the measurement cannot be trusted.
            if (Vector3.Distance(eye.position, space.position) < 0.01f
                && Quaternion.Angle(eye.rotation, space.rotation) < 1f)
            {
                Debug.LogWarning("[AprilTagTracker] centerEyeAnchor sits on the tracking origin with no " +
                                 "rotation, so it is not being tracked and the pose space cannot be measured. " +
                                 "Falling back to TrackingSpace, which is what " +
                                 "PassthroughCameraAccess.GetCameraPose reports. Set Camera Pose Space " +
                                 "explicitly to skip this guess entirely.", this);
                return CameraPoseSpace.TrackingSpace;
            }

            return detected;
        }

        private Transform ResolveTrackingSpace()
        {
            // The rig is looked up even when an override is set: it is only used as the transform to convert
            // through, while the rig is also needed as the reference for the pose space detection. Returning
            // the override early used to skip the lookup, which left the detection without a center eye to
            // measure against - it then silently assumed tracking space instead of measuring.
            EnsureCameraRig();

            if (trackingSpaceOverride != null)
            {
                return trackingSpaceOverride;
            }

            return _cameraRig != null ? _cameraRig.trackingSpace : null;
        }

        private void EnsureCameraRig()
        {
            if (_cameraRig != null || _cameraRigSearched)
            {
                return;
            }

            _cameraRigSearched = true;
            _cameraRig = FindFirstObjectByType<OVRCameraRig>();

            if (_cameraRig == null && trackingSpaceOverride == null)
            {
                Debug.LogWarning("[AprilTagTracker] No OVRCameraRig found, cannot convert camera poses " +
                                 "from tracking space to world space. Assign Tracking Space Override.", this);
            }
        }

        private void LogStatistics()
        {
            if (Time.unscaledTime < _nextLogTime || (!logStatistics && statusLabel == null))
            {
                return;
            }

            float interval = Mathf.Max(0.5f, logIntervalSeconds);
            _nextLogTime = Time.unscaledTime + interval;

            // Kept short and in three lines, so it stays readable in a small world space label.
            string summary = $"{_detectionsSinceLog / interval:F1}/s  {LastDetectionCount} tag  " +
                             $"{LastDetectionMilliseconds:F0}ms  age {LastPoseAgeSeconds * 1000f:F0}ms" +
                             $"\n{_width}x{_height}  tag {tagSizeMeters * 1000f:F1}mm  {EffectivePoseSpaceLabel}" +
                             $"\nfocal {_focalY:F0}px  {_focalSourceLabel}";
            _detectionsSinceLog = 0;

            if (_hasDiagnostics)
            {
                summary += $"\n#{_diagTagId} f{_diagDotForward:F2} u{_diagDotUp:F2} r{_diagDotRight:F2}  " +
                           $"d {_diagDistance:F3}m  m{_diagMargin:F0}";
                _hasDiagnostics = false;
            }

            if (statusLabel != null)
            {
                statusLabel.text = summary;
            }

            if (logStatistics)
            {
                Debug.Log("[AprilTagTracker] " + summary.Replace("\n", " | "));
            }
        }

        private static bool HasPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Permission.HasUserAuthorizedPermission(HeadsetCameraPermission);
#else
            return true;
#endif
        }

        private static void RequestPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            Permission.RequestUserPermission(HeadsetCameraPermission);
#endif
        }
    }
}
