using System;
using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using UnityEngine;

namespace TangibleHorror.AprilTagTracking
{
    /// <summary>
    /// Drives one GameObject from one or more AprilTag markers on the same physical prop.
    /// Every marker gets its own entry with the ID and a child transform placed where that marker
    /// physically sits. All markers visible in the same camera frame are averaged, so the prop keeps
    /// tracking while a hand covers one of them.
    ///
    /// Grab and marker tracking can be combined via <see cref="LostBehaviour.HandOverToGrab"/>: while a
    /// marker is visible it owns the transform, and when it is lost the Interaction SDK takes over.
    ///
    /// The grab interactables stay enabled the whole time, which is what makes the handover work at all:
    /// the Interaction SDK only selects on a fresh grab gesture, so an interactable that is switched off
    /// while the marker drives can never be selected by a hand that is already closed around the prop -
    /// the marker gets lost and nobody is holding it. With them enabled the hand selects immediately, and
    /// on marker loss there is already an active grab to hand over to. Writing happens in LateUpdate, after
    /// the Interaction SDK, so exactly one pose reaches the transform per frame.
    /// </summary>
    [DisallowMultipleComponent]
    public class AprilTagTarget : MonoBehaviour
    {
        public enum LostBehaviour
        {
            /// <summary>Stay where the object last was.</summary>
            FreezeInPlace,

            /// <summary>Hide all renderers until a marker is seen again.</summary>
            Hide,

            /// <summary>Keep following a transform (e.g. a hand anchor) with the offset it had when tracking was lost.</summary>
            FollowFallbackTransform,

            /// <summary>
            /// Hand the object over to the Interaction SDK when the marker is lost. Grabbing stays enabled
            /// at all times so a grab can already be running when the marker disappears; the remaining gap
            /// between the marker pose and the grab pose is decayed over Reacquire Blend Seconds.
            /// </summary>
            HandOverToGrab
        }

        /// <summary>Who currently owns the transform.</summary>
        private enum DriveState
        {
            /// <summary>Nobody is driving; the object rests where it was (or follows the fallback).</summary>
            Frozen,

            /// <summary>Marker poses drive the object.</summary>
            MarkerDriven,

            /// <summary>The Interaction SDK drives the object; we keep our hands off.</summary>
            Grabbed
        }

        public enum LocalAxis
        {
            X,
            Y,
            Z
        }

        /// <summary>One printed marker on the prop.</summary>
        [Serializable]
        public class MarkerBinding
        {
            [Tooltip("ID of the printed tagStandard41h12 marker, as written on the print sheet.")]
            public int TagId;

            [Tooltip("Child transform placed exactly where this marker sits on the prop: origin at the marker " +
                     "center, +Z INTO the surface (into the prop), +Y up along the marker's top edge, " +
                     "+X to the right when looking at the marker. " +
                     "Leave empty if the driven object's own pivot IS the marker.")]
            public Transform Anchor;

            [NonSerialized] public Vector3 LocalPosition;
            [NonSerialized] public Quaternion LocalRotation;
            [NonSerialized] public float LastSeenTime;
        }

        [Header("Markers")]
        [Tooltip("All markers glued to this prop. More markers on different sides = far fewer tracking dropouts.")]
        [SerializeField] private List<MarkerBinding> markers = new List<MarkerBinding> { new MarkerBinding() };

        [Tooltip("Re-reads the anchor offsets every frame instead of only on Awake, so an anchor can be " +
                 "dragged in play mode and the prop follows immediately - that is how a lateral offset gets " +
                 "calibrated. Costs two quaternion inverses per marker. Switch off only if something else " +
                 "animates the anchors at runtime.")]
        [SerializeField] private bool liveMarkerOffsets = true;

        [Header("Driven object")]
        [Tooltip("Transform to move. Defaults to this GameObject.")]
        [SerializeField] private Transform driven;

        [Tooltip("Keeps an attached Rigidbody kinematic while the marker drives the object, so it still " +
                 "collides with virtual objects but is not pulled by gravity. The transform itself is always " +
                 "written directly - MovePosition is not used, because Grabbable manages the kinematic flag " +
                 "itself and the two fight over it.")]
        [SerializeField] private bool driveRigidbody = true;

        [Header("Smoothing")]
        [Tooltip("One Euro filter cutoff at standstill (Hz). Lower = smoother but laggier.")]
        [SerializeField] private float positionMinCutoff = 1.5f;

        [Tooltip("How much the filter opens up with speed. Higher = follows fast motion better, jitters more.")]
        [SerializeField] private float positionBeta = 0.05f;

        [Tooltip("Rotation smoothing speed. Higher = snappier.")]
        [SerializeField] private float rotationLerpSpeed = 12f;

        [Tooltip("Extrapolate along the measured velocity by the pose age the tracker reports (worker delay " +
                 "plus camera latency). Adapts on its own when detection gets slower, unlike a fixed value.")]
        [SerializeField] private bool compensateMeasuredLatency = true;

        [Tooltip("Extra extrapolation on top of the measured age, in seconds. Usually stays 0.")]
        [SerializeField] private float latencyCompensation;

        [Tooltip("Upper bound for the total extrapolation. Protects against overshooting when a detection " +
                 "arrives late.")]
        [SerializeField] private float maximumLatencyCompensation = 0.12f;

        [Header("Tracking loss")]
        [Tooltip("Seconds without any detection before the object counts as lost.")]
        [SerializeField] private float lostAfterSeconds = 0.35f;

        [SerializeField] private LostBehaviour whenLost = LostBehaviour.FreezeInPlace;

        [Tooltip("Only used by FollowFallbackTransform - e.g. a hand anchor of the camera rig.")]
        [SerializeField] private Transform fallbackFollow;

        [Header("Grab handover (When Lost = HandOverToGrab)")]
        [Tooltip("Consecutive detections needed before the marker takes the object back. Prevents the " +
                 "ownership from thrashing when the marker flickers.")]
        [Range(1, 10)]
        [SerializeField] private int requiredDetectionsToReacquire = 2;

        [Tooltip("Seconds to fade out the gap between the two systems, in BOTH directions - marker taking " +
                 "over from the hand and the hand taking over from the marker. 0 switches hard.")]
        [SerializeField] private float reacquireBlendSeconds = 0.25f;

        [Tooltip("Off (recommended): the marker owns the prop whenever it is visible, even while a hand " +
                 "holds it, and the hand only takes over when the marker is lost - the physical marker is " +
                 "the more accurate source. On: touching the prop hands it to the hand until release.")]
        [SerializeField] private bool grabTakesPriority;

        [Tooltip("Grab interactables of the prop. Kept ENABLED at all times - listed here only so a prefab " +
                 "that was saved with them switched off gets repaired on start. Auto-found if left empty.")]
        [SerializeField] private List<MonoBehaviour> grabInteractables = new List<MonoBehaviour>();

        [Tooltip("Grabbable whose select events tell us when the object is being held. Auto-found if empty.")]
        [SerializeField] private Grabbable grabbable;

        [Header("Anchor generator")]
        [Tooltip("Local axis of the driven object pointing out of the FRONT face. For a coin lying flat " +
                 "that is usually Y.")]
        [SerializeField] private LocalAxis generatorFaceAxis = LocalAxis.Y;

        [Tooltip("Local axis the patch arrows point along - the marker's up direction. Must differ from the " +
                 "face axis. Both patches of one prop point their arrow at the same physical edge.")]
        [SerializeField] private LocalAxis generatorUpAxis = LocalAxis.Z;

        [Tooltip("Takes the prop's center and half thickness from the combined mesh bounds instead of the " +
                 "pivot. Needed whenever the pivot is not in the geometric center - e.g. a mesh child whose " +
                 "pivot sits at the bottom. With this on, Face Offset Millimeters is ignored.")]
        [SerializeField] private bool generatorUseMeshBounds = true;

        [Tooltip("Only used when Use Mesh Bounds is off: distance from the pivot to each face in " +
                 "millimeters, i.e. half the prop thickness.")]
        [SerializeField] private float generatorFaceOffsetMillimeters = 1.5f;

        [Tooltip("Markers per face: 1 for the one-marker patch, 4 for the 2x2 patch.")]
        [Range(1, 4)]
        [SerializeField] private int generatorMarkersPerFace = 1;

        [Tooltip("Center to center distance of the 2x2 grid in millimeters. The coin sheet prints 19.4 mm " +
                 "for a 40 mm patch.")]
        [SerializeField] private float generatorSpacingMillimeters = 19.4f;

        [Tooltip("ID of the first marker of the FRONT face. The back face IDs follow directly, in the same " +
                 "order as the print sheet labels them.")]
        [SerializeField] private int generatorFirstTagId;

        /// <summary>Raised when tracking is gained (true) or lost (false).</summary>
        public event Action<bool> TrackingChanged;

        public bool IsTracked { get; private set; }

        /// <summary>How many of this prop's markers were visible in the most recent detection.</summary>
        public int LastObservedMarkerCount { get; private set; }

        public int MarkerCount => markers != null ? markers.Count : 0;

        public float SecondsSinceLastObservation =>
            _hasMeasurement ? Time.time - _lastObservationTime : float.PositiveInfinity;

        private readonly OneEuroFilterVector3 _positionFilter = new OneEuroFilterVector3();

        private Pose _measuredPose;
        private Pose _appliedPose;
        private Pose _frozenPose;
        private Quaternion _filteredRotation = Quaternion.identity;

        private bool _hasMeasurement;
        private float _lastObservationTime = float.NegativeInfinity;
        private float _poseAgeSeconds;

        private int _accumulatedPass = int.MinValue;
        private int _accumulatedCount;
        private Vector3 _accumulatedPosition;
        private Quaternion _accumulatedRotation = Quaternion.identity;

        private Rigidbody _rigidbody;
        private bool _useRigidbody;
        private Renderer[] _renderers;
        private bool _renderersVisible = true;

        private Vector3 _fallbackLocalPosition;
        private Quaternion _fallbackLocalRotation = Quaternion.identity;

        private DriveState _state = DriveState.Frozen;
        private bool _stateInitialized;
        private int _selectCount;
        private int _pendingDetections;
        private int _lastCountedPass = int.MinValue;
        private float _blendTimer;
        private Pose _blendStartPose;

        // Difference between where the marker had the prop and where the Interaction SDK wants it at the
        // moment of the handover. Decayed to zero over reacquireBlendSeconds instead of blending between
        // two absolute poses, so the prop follows the hand one to one from the very first frame.
        private Vector3 _handoverPositionGap;
        private Quaternion _handoverRotationGap = Quaternion.identity;
        private bool _originalIsKinematic;
        private IPointableElement _pointable;

        private void Awake()
        {
            if (driven == null)
            {
                driven = transform;
            }

            _renderers = driven.GetComponentsInChildren<Renderer>(true);
            _rigidbody = driven.GetComponent<Rigidbody>();
            _useRigidbody = driveRigidbody && _rigidbody != null;

            if (_useRigidbody)
            {
                _originalIsKinematic = _rigidbody.isKinematic;
                _rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            }

            if (grabbable == null)
            {
                grabbable = driven.GetComponentInChildren<Grabbable>(true);
            }

            if (grabInteractables.Count == 0)
            {
                grabInteractables.AddRange(driven.GetComponentsInChildren<HandGrabInteractable>(true));
                grabInteractables.AddRange(driven.GetComponentsInChildren<GrabInteractable>(true));
                grabInteractables.AddRange(driven.GetComponentsInChildren<DistanceHandGrabInteractable>(true));
                grabInteractables.AddRange(driven.GetComponentsInChildren<DistanceGrabInteractable>(true));
            }

            ForceGrabInteractablesEnabled();

            BakeMarkerOffsets();
            _appliedPose = new Pose(driven.position, driven.rotation);
            _frozenPose = _appliedPose;
            _filteredRotation = driven.rotation;
        }

        private void OnEnable()
        {
            AprilTagTracker.Register(this);

            _pointable = grabbable;
            if (_pointable != null)
            {
                _pointable.WhenPointerEventRaised += OnPointerEvent;
            }
        }

        private void OnDisable()
        {
            AprilTagTracker.Unregister(this);

            if (_pointable != null)
            {
                _pointable.WhenPointerEventRaised -= OnPointerEvent;
                _pointable = null;
            }

            _selectCount = 0;
        }

        // Select and Unselect tell us whether a hand currently holds the object.
        private void OnPointerEvent(PointerEvent pointerEvent)
        {
            switch (pointerEvent.Type)
            {
                case PointerEventType.Select:
                    _selectCount++;
                    break;
                case PointerEventType.Unselect:
                case PointerEventType.Cancel:
                    _selectCount = Mathf.Max(0, _selectCount - 1);
                    break;
            }
        }

        /// <summary>
        /// Captures where each marker sits relative to the driven object. Runs on Awake; call it again
        /// from the context menu after moving an anchor in the editor.
        /// </summary>
        [ContextMenu("Bake Marker Offsets From Anchors")]
        public void BakeMarkerOffsets()
        {
            Transform reference = driven != null ? driven : transform;
            if (markers == null)
            {
                return;
            }

            foreach (MarkerBinding marker in markers)
            {
                if (marker == null)
                {
                    continue;
                }

                if (marker.Anchor == null)
                {
                    marker.LocalPosition = Vector3.zero;
                    marker.LocalRotation = Quaternion.identity;
                    continue;
                }

                // Offset expressed in the object's own orientation, so it survives runtime scaling.
                marker.LocalPosition = Quaternion.Inverse(reference.rotation) * (marker.Anchor.position - reference.position);
                marker.LocalRotation = Quaternion.Inverse(reference.rotation) * marker.Anchor.rotation;
            }
        }

        /// <summary>
        /// Called by <see cref="AprilTagTracker"/> for every detected marker. Markers this prop does not
        /// use are ignored. Markers sharing a pass number come from the same camera frame and are averaged.
        /// </summary>
        internal void OnMarkerObserved(int tagId, Pose markerWorldPose, int detectionPass, float poseAgeSeconds)
        {
            MarkerBinding marker = FindMarker(tagId);
            if (marker == null)
            {
                return;
            }

            _poseAgeSeconds = poseAgeSeconds;

            Quaternion rotation = markerWorldPose.rotation * Quaternion.Inverse(marker.LocalRotation);
            Vector3 position = markerWorldPose.position - rotation * marker.LocalPosition;

            if (detectionPass != _accumulatedPass)
            {
                _accumulatedPass = detectionPass;
                _accumulatedCount = 1;
                _accumulatedPosition = position;
                _accumulatedRotation = rotation;
            }
            else
            {
                _accumulatedCount++;
                _accumulatedPosition += position;

                // Quaternions have two representations per orientation - align them before averaging.
                if (Quaternion.Dot(_accumulatedRotation, rotation) < 0f)
                {
                    rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
                }

                _accumulatedRotation = Quaternion.Slerp(_accumulatedRotation, rotation, 1f / _accumulatedCount);
            }

            if (detectionPass != _lastCountedPass)
            {
                _lastCountedPass = detectionPass;
                if (_state != DriveState.MarkerDriven)
                {
                    _pendingDetections++;
                }
            }

            marker.LastSeenTime = Time.time;
            LastObservedMarkerCount = _accumulatedCount;
            _measuredPose = new Pose(_accumulatedPosition / _accumulatedCount, _accumulatedRotation.normalized);
            _lastObservationTime = Time.time;
            _hasMeasurement = true;
        }

        /// <summary>Seconds since this specific marker was last seen, or +infinity if never.</summary>
        public float SecondsSinceMarkerSeen(int tagId)
        {
            MarkerBinding marker = FindMarker(tagId);
            if (marker == null || marker.LastSeenTime <= 0f)
            {
                return float.PositiveInfinity;
            }

            return Time.time - marker.LastSeenTime;
        }

        private MarkerBinding FindMarker(int tagId)
        {
            if (markers == null)
            {
                return null;
            }

            for (int i = 0; i < markers.Count; i++)
            {
                if (markers[i] != null && markers[i].TagId == tagId)
                {
                    return markers[i];
                }
            }

            return null;
        }

        // Runs before AprilTagTracker, which sits at execution order 100 and dispatches the detections from
        // its own Update - so a freshly dragged anchor is already baked when this frame's poses arrive.
        private void Update()
        {
            if (liveMarkerOffsets)
            {
                BakeMarkerOffsets();
            }
        }

        // LateUpdate, not Update: the Interaction SDK writes the transform from its own Update, so running
        // afterwards means our pose is the one that survives while the marker owns the prop, and that the
        // transform we read in the Grabbed state is really the pose the Interaction SDK wants this frame.
        private void LateUpdate()
        {
            float deltaTime = Mathf.Max(Time.deltaTime, 1e-4f);
            bool markerFresh = _hasMeasurement && Time.time - _lastObservationTime <= lostAfterSeconds;
            bool grabbed = _selectCount > 0;

            DriveState desired = ResolveState(markerFresh, grabbed);
            if (!_stateInitialized || desired != _state)
            {
                bool isTransition = _stateInitialized;
                _stateInitialized = true;
                EnterState(desired, isTransition);
            }

            if (_state == DriveState.Grabbed)
            {
                ApplyGrabbedPose(deltaTime);
                return;
            }

            Pose goal = _state == DriveState.MarkerDriven ? _measuredPose : ResolveFrozenGoal();

            _positionFilter.Filter(goal.position, deltaTime, positionMinCutoff, positionBeta);
            _filteredRotation = Quaternion.Slerp(_filteredRotation, goal.rotation,
                1f - Mathf.Exp(-Mathf.Max(0.01f, rotationLerpSpeed) * deltaTime));

            float compensation = Mathf.Clamp(
                (compensateMeasuredLatency ? _poseAgeSeconds : 0f) + latencyCompensation,
                0f, Mathf.Max(0f, maximumLatencyCompensation));

            Vector3 position = _positionFilter.Value + _positionFilter.Velocity * compensation;
            Quaternion rotation = _filteredRotation;

            // Ease in after taking the object back, so the gap between the grabbed pose and the marker
            // pose does not show up as a jump.
            if (_state == DriveState.MarkerDriven && reacquireBlendSeconds > 0f && _blendTimer < reacquireBlendSeconds)
            {
                _blendTimer += deltaTime;
                float blend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_blendTimer / reacquireBlendSeconds));
                position = Vector3.Lerp(_blendStartPose.position, position, blend);
                rotation = Quaternion.Slerp(_blendStartPose.rotation, rotation, blend);
            }

            _appliedPose = new Pose(position, rotation);
            driven.SetPositionAndRotation(position, rotation);

            SetRenderersVisible(_state == DriveState.MarkerDriven || whenLost != LostBehaviour.Hide);
        }

        /// <summary>
        /// The Interaction SDK owns the pose. It has already written it this frame, so all that is left is
        /// to fade out whatever gap the handover left and to keep the filters in sync for the way back.
        /// </summary>
        private void ApplyGrabbedPose(float deltaTime)
        {
            Pose wanted = new Pose(driven.position, driven.rotation);

            if (reacquireBlendSeconds > 0f && _blendTimer < reacquireBlendSeconds)
            {
                _blendTimer += deltaTime;
                float remaining = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_blendTimer / reacquireBlendSeconds));

                wanted = new Pose(
                    wanted.position + _handoverPositionGap * remaining,
                    wanted.rotation * Quaternion.Slerp(Quaternion.identity, _handoverRotationGap, remaining));

                driven.SetPositionAndRotation(wanted.position, wanted.rotation);
            }

            // Keeps the marker side ready to take over from exactly here instead of snapping.
            _appliedPose = wanted;
            _positionFilter.Reset(wanted.position);
            _filteredRotation = wanted.rotation;
            SetRenderersVisible(true);
        }

        private DriveState ResolveState(bool markerFresh, bool grabbed)
        {
            if (whenLost != LostBehaviour.HandOverToGrab)
            {
                return markerFresh ? DriveState.MarkerDriven : DriveState.Frozen;
            }

            if (grabbed && grabTakesPriority)
            {
                return DriveState.Grabbed;
            }

            // Once the marker owns the object it keeps it while it stays fresh. The detection counter only
            // runs while it does not own it, and that is what provides the hysteresis.
            if (_state == DriveState.MarkerDriven && markerFresh)
            {
                return DriveState.MarkerDriven;
            }

            if (markerFresh && _pendingDetections >= Mathf.Max(1, requiredDetectionsToReacquire))
            {
                return DriveState.MarkerDriven;
            }

            return grabbed ? DriveState.Grabbed : DriveState.Frozen;
        }

        private void EnterState(DriveState next, bool isTransition)
        {
            _state = next;

            switch (next)
            {
                case DriveState.MarkerDriven:
                    _blendStartPose = _appliedPose;
                    _blendTimer = 0f;
                    _positionFilter.Reset(_appliedPose.position);
                    _filteredRotation = _appliedPose.rotation;
                    ApplyKinematic(true);
                    break;

                case DriveState.Frozen:
                    _frozenPose = _appliedPose;
                    _pendingDetections = 0;
                    LastObservedMarkerCount = 0;
                    CaptureFallbackOffset();
                    ApplyKinematic(true);
                    break;

                case DriveState.Grabbed:
                    _pendingDetections = 0;
                    LastObservedMarkerCount = 0;

                    // driven currently holds the pose the Interaction SDK wrote this frame, _appliedPose is
                    // where we had the prop. Remember the difference so it can be faded out instead of
                    // popping. On the very first frame there is no previous pose to compare against.
                    if (isTransition)
                    {
                        _handoverPositionGap = _appliedPose.position - driven.position;
                        _handoverRotationGap = Quaternion.Inverse(driven.rotation) * _appliedPose.rotation;
                    }
                    else
                    {
                        _handoverPositionGap = Vector3.zero;
                        _handoverRotationGap = Quaternion.identity;
                    }

                    _blendTimer = 0f;

                    // Hand the Rigidbody back: Grabbable locks the kinematic flag itself while selected.
                    ApplyKinematic(false);
                    break;
            }

            bool tracked = next == DriveState.MarkerDriven;
            if (tracked != IsTracked)
            {
                IsTracked = tracked;
                TrackingChanged?.Invoke(tracked);
            }
        }

        private void ApplyKinematic(bool kinematic)
        {
            if (!_useRigidbody)
            {
                return;
            }

            _rigidbody.isKinematic = kinematic || _originalIsKinematic;
        }

        /// <summary>
        /// Switches every grab interactable on and leaves it on. Only needed because an earlier version
        /// disabled them while the marker was driving, which could get saved into a prefab - and because a
        /// disabled interactable is exactly what breaks the handover: the Interaction SDK selects on a fresh
        /// grab gesture only, so a hand that is already holding the prop never gets a second chance.
        /// </summary>
        private void ForceGrabInteractablesEnabled()
        {
            foreach (MonoBehaviour interactable in grabInteractables)
            {
                if (interactable != null && !interactable.enabled)
                {
                    interactable.enabled = true;
                }
            }
        }

        private Pose ResolveFrozenGoal()
        {
            if (whenLost == LostBehaviour.FollowFallbackTransform && fallbackFollow != null)
            {
                return new Pose(
                    fallbackFollow.position + fallbackFollow.rotation * _fallbackLocalPosition,
                    fallbackFollow.rotation * _fallbackLocalRotation);
            }

            return _frozenPose;
        }

        private void CaptureFallbackOffset()
        {
            if (fallbackFollow == null)
            {
                return;
            }

            _fallbackLocalPosition = Quaternion.Inverse(fallbackFollow.rotation) * (_appliedPose.position - fallbackFollow.position);
            _fallbackLocalRotation = Quaternion.Inverse(fallbackFollow.rotation) * _appliedPose.rotation;
        }

        /// <summary>
        /// Creates the marker anchors for a two sided prop from the printed patch geometry, so the offsets
        /// do not have to be placed by hand. Front face first, then the back face, IDs ascending - the same
        /// order the coin print sheet labels them in.
        ///
        /// Both faces use the same up axis, which matches the instruction on the sheet: glue both patches so
        /// their arrows point at the same physical edge of the prop.
        /// </summary>
        [ContextMenu("Generate Marker Anchors")]
        public void GenerateMarkerAnchors()
        {
            Transform reference = driven != null ? driven : transform;

            if (generatorFaceAxis == generatorUpAxis)
            {
                Debug.LogError("[AprilTagTarget] Face axis and up axis must differ.", this);
                return;
            }

            Vector3 normal = AxisToVector(generatorFaceAxis);
            Vector3 up = AxisToVector(generatorUpAxis);
            float spacing = generatorSpacingMillimeters * 0.001f;
            int perFace = Mathf.Clamp(generatorMarkersPerFace, 1, 4);

            // Where the prop's geometric center sits relative to the driven pivot, and how far each face
            // is from it. Taken from the mesh, because a pivot at the bottom would otherwise put both
            // faces on top of each other at the wrong height.
            Vector3 center = Vector3.zero;
            float faceOffset = generatorFaceOffsetMillimeters * 0.001f;

            if (generatorUseMeshBounds)
            {
                if (TryComputeLocalBounds(reference, out Bounds bounds))
                {
                    center = bounds.center;
                    faceOffset = Mathf.Abs(Vector3.Dot(bounds.extents, normal));
                    Debug.Log($"[AprilTagTarget] Mesh bounds: center {center}, half thickness along " +
                              $"{generatorFaceAxis} {faceOffset * 1000f:F2} mm.", this);
                }
                else
                {
                    Debug.LogWarning("[AprilTagTarget] No mesh found below the driven object, falling back " +
                                     "to the pivot and Face Offset Millimeters.", this);
                }
            }

            for (int i = reference.childCount - 1; i >= 0; i--)
            {
                Transform child = reference.GetChild(i);
                if (child.name.StartsWith("Marker_", StringComparison.Ordinal))
                {
                    DestroyImmediate(child.gameObject);
                }
            }

            markers.Clear();
            int tagId = generatorFirstTagId;

            for (int face = 0; face < 2; face++)
            {
                Vector3 faceNormal = face == 0 ? normal : -normal;

                // +Z of the anchor points INTO the surface, +Y along the marker's top edge.
                Quaternion rotation = Quaternion.LookRotation(-faceNormal, up);
                Vector3 faceCenter = center + faceNormal * faceOffset;

                for (int index = 0; index < perFace; index++)
                {
                    Vector2 grid = perFace > 1 ? GridOffset(index, spacing) : Vector2.zero;
                    Vector3 localPosition = faceCenter + rotation * new Vector3(grid.x, grid.y, 0f);

                    var anchor = new GameObject($"Marker_{tagId}");
                    anchor.transform.SetParent(reference, false);
                    anchor.transform.SetLocalPositionAndRotation(localPosition, rotation);

                    markers.Add(new MarkerBinding { TagId = tagId, Anchor = anchor.transform });
                    tagId++;
                }
            }

            BakeMarkerOffsets();
            Debug.Log($"[AprilTagTarget] Generated {markers.Count} marker anchors, IDs " +
                      $"{generatorFirstTagId}..{tagId - 1}.", this);

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        /// <summary>
        /// Combined bounds of every mesh below <paramref name="reference"/>, expressed in the reference's
        /// own local space. Corners are transformed individually, so child rotations and scales are covered.
        /// </summary>
        private static bool TryComputeLocalBounds(Transform reference, out Bounds bounds)
        {
            bounds = default;
            bool hasAny = false;

            foreach (MeshFilter filter in reference.GetComponentsInChildren<MeshFilter>())
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                Bounds meshBounds = mesh.bounds;

                for (int corner = 0; corner < 8; corner++)
                {
                    var sign = new Vector3(
                        (corner & 1) == 0 ? -1f : 1f,
                        (corner & 2) == 0 ? -1f : 1f,
                        (corner & 4) == 0 ? -1f : 1f);

                    Vector3 world = filter.transform.TransformPoint(meshBounds.center + Vector3.Scale(meshBounds.extents, sign));
                    Vector3 local = reference.InverseTransformPoint(world);

                    if (!hasAny)
                    {
                        bounds = new Bounds(local, Vector3.zero);
                        hasAny = true;
                    }
                    else
                    {
                        bounds.Encapsulate(local);
                    }
                }
            }

            return hasAny;
        }

        // Row major as printed: top left, top right, bottom left, bottom right.
        private static Vector2 GridOffset(int index, float spacing)
        {
            float half = spacing * 0.5f;
            float x = index % 2 == 0 ? -half : half;
            float y = index < 2 ? half : -half;
            return new Vector2(x, y);
        }

        private static Vector3 AxisToVector(LocalAxis axis)
        {
            switch (axis)
            {
                case LocalAxis.X: return Vector3.right;
                case LocalAxis.Y: return Vector3.up;
                default: return Vector3.forward;
            }
        }

        private void SetRenderersVisible(bool visible)
        {
            if (visible == _renderersVisible || _renderers == null)
            {
                return;
            }

            _renderersVisible = visible;
            foreach (Renderer renderer in _renderers)
            {
                if (renderer != null)
                {
                    renderer.enabled = visible;
                }
            }
        }
    }
}
