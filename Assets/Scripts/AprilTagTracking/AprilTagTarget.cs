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
    /// Grab and marker tracking are combined: while a marker is reliably visible it owns the transform,
    /// and when it is lost the prop stays exactly where it was last seen unless a hand is holding it, in
    /// which case the Interaction SDK owns it. It never moves on its own.
    ///
    /// "Reliably" is the whole point of the acquisition gate. A detection is only allowed to take the prop
    /// over as part of a streak: several detections in a row, close together in time and landing close to
    /// each other in space, all from within arm's reach. A marker that flashes up for a frame from across
    /// the room - which happens constantly while just looking around - produces one scattered detection and
    /// is ignored, so the prop stays where the real one is instead of teleporting.
    ///
    /// The prop is kinematic whenever we or the marker drive it, and becomes a dynamic body in exactly one
    /// case: a real grab that was released. Losing the marker never drops it. That keeps the collider
    /// where the participant expects it and avoids the kinematic flip-flopping that made PhysX drop
    /// pending trigger events.
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

        [Header("Acquisition gate")]
        [Tooltip("Consecutive AGREEING detections needed before the marker takes the prop over. This is " +
                 "the main defence against the prop teleporting away: a marker that is picked up for a " +
                 "fraction of a second while looking around the room produces one or two scattered " +
                 "detections, never a run of consistent ones.")]
        [Range(1, 10)]
        [SerializeField] private int requiredDetectionsToReacquire = 3;

        [Tooltip("Fastest the prop is assumed to be moved by hand, in m/s. Two consecutive detections " +
                 "count as agreeing when they are no further apart than this speed allows in the time " +
                 "between them - so the tolerance grows automatically when a detection is dropped and the " +
                 "gap gets longer, instead of a fixed distance that is either too tight after a dropout or " +
                 "too loose between two fast detections. Measured: at a 70 ms gap real movement produced " +
                 "0.1 to 2.4 cm, at 140 to 420 ms it produced 8+ cm.")]
        [SerializeField] private float maximumPropSpeed = 1.2f;

        [Tooltip("Detections further from the camera than this are discarded outright, in centimeters. A " +
                 "prop the participant is holding is within arm's reach, so a detection from further away " +
                 "is either a different prop or a misread, and applying it is what makes the prop jump " +
                 "somewhere else while merely looking around. The upper end is set by how well the marker " +
                 "still decodes, which was measured rather than calculated: at 62 to 68 cm the decision " +
                 "margin was 203 to 262 with every single detection pass hitting, at 77 to 83 cm it fell " +
                 "to 81 to 156 with whole runs of passes missing. So 80 covers a fully extended arm and " +
                 "still cuts off the range where detections are sparse and weak. Scale it with the " +
                 "printed marker - the range grows linearly with its edge length. 0 switches it off.")]
        [SerializeField] private float maximumDetectionDistanceCentimeters = 80f;

        [Tooltip("Seconds to fade out the gap between the two systems, in BOTH directions - marker taking " +
                 "over from the hand and the hand taking over from the marker. 0 switches hard.")]
        [SerializeField] private float reacquireBlendSeconds = 0.25f;

        [Tooltip("Off (recommended): the marker owns the prop whenever it is visible, even while a hand " +
                 "holds it, and the hand only takes over when the marker is lost - the physical marker is " +
                 "the more accurate source. On: touching the prop hands it to the hand until release.")]
        [SerializeField] private bool grabTakesPriority;

        [Tooltip("Off (recommended for the study): releasing a grab leaves the prop hanging exactly where " +
                 "it was let go. On: it falls. The Interaction SDK cannot reliably tell a deliberate " +
                 "release from the fingers behind the prop dropping out of tracking - measured unselects " +
                 "of 431 ms and 1222 ms were both tracking losses, not decisions - so with this on the " +
                 "prop regularly ends up on the floor while the real one is still in the hand. A prop " +
                 "hanging in the air is a bounded error, one on the floor is not.")]
        [SerializeField] private bool dropOnRelease;

        [Tooltip("Only used when Drop On Release is on: shortest grab, in seconds, that counts as the " +
                 "The Interaction SDK regularly selects and unselects again within a frame or two when the " +
                 "fingers behind the prop drop out of tracking - reading that as a deliberate release is " +
                 "what made the prop fall out of a hand that was still holding it. Anything shorter is " +
                 "treated like a plain marker loss instead.")]
        [SerializeField] private float minGrabSecondsForRelease = 0.25f;

        [Tooltip("Logs one line per detection pass with margin, distance, how far the prop WOULD jump and " +
                 "how much consecutive detections agree. This is the measurement the acquisition gate " +
                 "gets chosen from - switch it on, run the tests, then off again.")]
        [SerializeField] private bool logAcquisitions;

        [Tooltip("Logs every Select, Unselect and Cancel with the drive state it happened in. The way to see " +
                 "whether the Interaction SDK ever registered the grab, or silently cancelled it again.")]
        [SerializeField] private bool logGrabEvents;

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

        /// <summary>
        /// Pose difference from which a handover is worth blending. Below it the switch is invisible
        /// anyway, and blending would only add lag.
        /// </summary>
        private const float NoticeableGapMeters = 0.03f;

        /// <summary>Upper bound for the agreement tolerance, however long the gap between detections.</summary>
        private const float MaximumAgreementMeters = 0.25f;

        private readonly OneEuroFilterVector3 _positionFilter = new OneEuroFilterVector3();

        private Pose _measuredPose;
        private Pose _appliedPose;
        // Stored relative to driven.parent, not in world space. The prop hangs under LocationRoot, and
        // WorldLockAnchor reparents LocationRoot onto the spatial anchor - a world space rest pose would
        // stay behind while the whole room moves. Relative to the parent it travels with the room.
        private Pose _frozenLocal;
        private Quaternion _filteredRotation = Quaternion.identity;

        private bool _hasMeasurement;
        private float _lastObservationTime = float.NegativeInfinity;
        private float _poseAgeSeconds;

        // Measurement only, for choosing the acquisition gate. Nothing reads these to make decisions.
        private float _lastMargin;
        private float _lastCameraDistance;
        private Vector3 _previousPassPosition;
        private bool _hasPreviousPass;
        private float _lastPassTime = float.NegativeInfinity;

        private int _accumulatedPass = int.MinValue;
        private int _accumulatedCount;
        private Vector3 _accumulatedPosition;
        private Quaternion _accumulatedRotation = Quaternion.identity;

        private Rigidbody _rigidbody;
        private bool _useRigidbody;
        // Fingertips first, wrist last: the tips are what actually touch the prop, so the offset to them is
        // short and stays valid. The wrist is only the fallback for a prop resting against the palm.
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

        private float _grabStartTime = float.NegativeInfinity;

        // Set only when a real grab was released, and the one case in which the prop is allowed to be a
        // dynamic body. Cleared as soon as the marker or a hand takes it back.
        private bool _thrownByRelease;
        private bool _originalIsKinematic;
        private IPointableElement _pointable;

        private void Awake()
        {
            if (driven == null)
            {
                driven = transform;
            }

            _rigidbody = driven.GetComponent<Rigidbody>();
            _useRigidbody = driveRigidbody && _rigidbody != null;

            if (_useRigidbody)
            {
                _originalIsKinematic = _rigidbody.isKinematic;

                // ApplyKinematic keeps this in step from here on; it only writes on an actual change, so
                // the starting value has to be right.
                _rigidbody.interpolation = _rigidbody.isKinematic
                    ? RigidbodyInterpolation.None
                    : RigidbodyInterpolation.Interpolate;
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
            _frozenLocal = ToLocal(_appliedPose);
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
                    if (_selectCount == 0)
                    {
                        _grabStartTime = Time.time;
                    }

                    _selectCount++;
                    break;
                case PointerEventType.Unselect:
                case PointerEventType.Cancel:
                    _selectCount = Mathf.Max(0, _selectCount - 1);
                    break;
                default:
                    return;
            }

            if (logGrabEvents)
            {
                Debug.Log($"AprilTagTarget {name}: {pointerEvent.Type} in state {_state}, " +
                          $"select count now {_selectCount}, grab lasted " +
                          $"{(Time.time - _grabStartTime) * 1000f:F0} ms.", this);
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
        internal void OnMarkerObserved(int tagId, Pose markerWorldPose, int detectionPass,
            float poseAgeSeconds, float decisionMargin, float cameraDistance)
        {
            MarkerBinding marker = FindMarker(tagId);
            if (marker == null)
            {
                return;
            }

            _lastMargin = decisionMargin;
            _lastCameraDistance = cameraDistance;

            // Rejected before it can contribute anything: no accumulation, no freshness, no streak. A
            // marker that is metres away does not belong to a prop anyone is holding, so applying its
            // pose can only ever move the prop away from where the real one is.
            if (maximumDetectionDistanceCentimeters > 0f
                && cameraDistance > maximumDetectionDistanceCentimeters * 0.01f)
            {
                if (logAcquisitions)
                {
                    Debug.Log("ACQ " + name + "  pass " + detectionPass + "  REJECTED too far  camDist " +
                              (cameraDistance * 100f).ToString("F0") + "cm  margin " +
                              decisionMargin.ToString("F0"), this);
                }

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

            // Once per detection pass, no matter how many of this prop's markers were in it.
            if (detectionPass != _lastCountedPass)
            {
                _lastCountedPass = detectionPass;

                float gapSeconds = _hasPreviousPass ? Time.time - _lastPassTime : float.PositiveInfinity;
                float spread = _hasPreviousPass
                    ? Vector3.Distance(position, _previousPassPosition)
                    : float.PositiveInfinity;

                // A streak is a run of detections that follow each other closely in TIME and land no
                // further apart in SPACE than the prop could physically have travelled in between. Either
                // test alone is not enough: two detections seconds apart say nothing about each other, and
                // two that disagree by half a metre cannot both be right.
                //
                // AgreementLimit grows with the gap, so a dropped detection widens the tolerance instead
                // of breaking the streak. The 2 cm floor covers pose noise while the prop lies still, and
                // the ceiling stops the growth from swallowing the test altogether: at a long gap the
                // limit would otherwise reach a metre, and then two unrelated detections far apart would
                // pass as a streak.
                float agreementLimit = Mathf.Min(
                    0.02f + Mathf.Max(0f, maximumPropSpeed) * gapSeconds,
                    MaximumAgreementMeters);
                bool continuesStreak = gapSeconds <= lostAfterSeconds && spread <= agreementLimit;

                _pendingDetections = continuesStreak ? _pendingDetections + 1 : 1;

                LogAcquisition(detectionPass, position, spread, gapSeconds, agreementLimit);

                _previousPassPosition = position;
                _hasPreviousPass = true;
                _lastPassTime = Time.time;
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

            // Grabbed: the Interaction SDK owns the transform. Frozen right after a real release: the
            // physics engine owns it, because the participant let go on purpose and a released prop has to
            // fall like any object - otherwise the real one cannot be found again without looking down.
            // Both cases are the same job here: read, do not write, and stay ready to take it back.
            if (_state == DriveState.Grabbed || _thrownByRelease)
            {
                FollowExternalPose(deltaTime);
                return;
            }

            Pose goal = _state == DriveState.MarkerDriven ? _measuredPose : FromLocal(_frozenLocal);

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

            // Asserted EVERY frame, not just on a state change. Grabbable sets isKinematic back to false
            // and applies a throw velocity whenever it unselects, and an unselect can happen in the
            // middle of a state - the prop then stayed dynamic while we kept writing its transform, and
            // gravity fighting our writes is exactly the wild up and down glitching.
            ApplyKinematic(true);

            _appliedPose = new Pose(position, rotation);
            WritePose(position, rotation);
        }

        /// <summary>
        /// Moves the prop so that its COLLIDER moves with it.
        ///
        /// This project has Auto Sync Transforms switched off in the physics settings, and with that
        /// setting writing transform.position on an object that owns a Rigidbody moves the graphics only -
        /// the collider stays behind in the physics world at the pose it last had. The prop then visibly
        /// sits in the pipe or in a lock slot while PhysX still has it somewhere else, so no
        /// OnTriggerEnter and no OnTriggerExit ever arrive. That was the whole "colliders do not fire"
        /// problem.
        ///
        /// Rigidbody.position writes the actor pose directly and keeps the transform in step, so it is the
        /// correct way in. It teleports rather than sweeps, which is fine at render rate: we move the prop
        /// a few millimeters per frame, far less than any collider is thick.
        /// </summary>
        private void WritePose(Vector3 position, Quaternion rotation)
        {
            if (_useRigidbody && _rigidbody.isKinematic)
            {
                _rigidbody.position = position;
                _rigidbody.rotation = rotation;
                return;
            }

            driven.SetPositionAndRotation(position, rotation);
        }

        /// <summary>
        /// Someone else owns the pose - the Interaction SDK or the physics engine. Both have already written
        /// it this frame, so all that is left is to fade out whatever gap a handover left and to keep the
        /// filters in sync for the way back to marker driving.
        /// </summary>
        private void FollowExternalPose(float deltaTime)
        {
            Pose wanted = new Pose(driven.position, driven.rotation);

            if (reacquireBlendSeconds > 0f && _blendTimer < reacquireBlendSeconds)
            {
                _blendTimer += deltaTime;
                float remaining = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_blendTimer / reacquireBlendSeconds));

                wanted = new Pose(
                    wanted.position + _handoverPositionGap * remaining,
                    wanted.rotation * Quaternion.Slerp(Quaternion.identity, _handoverRotationGap, remaining));
            }

            // The Interaction SDK writes the transform, not the Rigidbody, and Grabbable keeps the body
            // kinematic while it is selected - so without this the collider of a grabbed prop stays where
            // the grab started and a coin carried into a lock slot triggers nothing. Only while kinematic:
            // once the body is dynamic, the simulation owns the pose and writing it would fight gravity.
            if (_useRigidbody && _rigidbody.isKinematic)
            {
                WritePose(wanted.position, wanted.rotation);
            }

            // Keeps the marker side ready to take over from exactly here instead of snapping.
            _appliedPose = wanted;
            _positionFilter.Reset(wanted.position);
            _filteredRotation = wanted.rotation;

            // Keeps the rest pose current, so a marker loss during a grab freezes the prop where the hand
            // left it instead of where the marker last drove it.
            _frozenLocal = ToLocal(wanted);
        }

        private DriveState ResolveState(bool markerFresh, bool grabbed)
        {
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
            DriveState previous = _state;
            _state = next;

            switch (next)
            {
                case DriveState.MarkerDriven:
                    _thrownByRelease = false;
                    _blendStartPose = _appliedPose;

                    // The blend only exists to hide a JUMP. Detection drops out for half a second every
                    // now and then, so the prop leaves and re-enters this state constantly - and if every
                    // re-entry restarted the blend, the prop would spend its whole life inside one and
                    // therefore permanently lag behind the marker. Measured: a re-acquisition after a
                    // short dropout has the prop within 0.3 to 3 cm of the measurement, which needs no
                    // hiding at all; the one that does is the first acquisition, which was 75 cm out.
                    float gap = Vector3.Distance(_appliedPose.position, _measuredPose.position);
                    _blendTimer = gap > NoticeableGapMeters ? 0f : reacquireBlendSeconds;

                    _positionFilter.Reset(_appliedPose.position);
                    _filteredRotation = _appliedPose.rotation;
                    ApplyKinematic(true);
                    break;

                case DriveState.Frozen:
                    // Starts the gate from zero, so taking the prop back always needs a full fresh streak.
                    _pendingDetections = 0;
                    _hasPreviousPass = false;
                    LastObservedMarkerCount = 0;

                    // Where the prop ends up depends on WHY it got here, and the two cases want opposite
                    // things.
                    //
                    // Marker lost: the real prop is most likely still in a hand that the Interaction SDK
                    // did not recognise as a grab - fingers behind a coin are regularly tracked as too far
                    // away. So the prop stays pinned exactly where it was last seen. That is a bounded
                    // error and it keeps the collider where the participant expects it, while dropping it
                    // from an unknown pose is an unbounded one.
                    //
                    // Grab released: the participant let go on purpose, so the prop has to fall. It is
                    // handed to physics with the velocity Grabbable already threw it with, which is Meta's
                    // own tested throw - we simply stop writing the transform.
                    //
                    // A grab that lasted only a frame or two is Interaction SDK noise, not a decision.
                    // Reading such a grab as a release is what dropped the prop out of a hand that was
                    // still holding it.
                    float grabSeconds = Time.time - _grabStartTime;
                    _thrownByRelease = dropOnRelease
                                       && previous == DriveState.Grabbed
                                       && grabSeconds >= Mathf.Max(0f, minGrabSecondsForRelease);

                    if (logGrabEvents && previous == DriveState.Grabbed)
                    {
                        Debug.Log($"AprilTagTarget {name}: grab lasted {grabSeconds * 1000f:F0} ms, " +
                                  (_thrownByRelease
                                      ? "handing the prop to physics."
                                      : "too short to be a release - pinning the prop in place."), this);
                    }

                    if (!_thrownByRelease)
                    {
                        _frozenLocal = ToLocal(_appliedPose);
                        ApplyKinematic(true);
                    }

                    // Physics continues exactly where we left off, so there is no gap to fade.
                    _handoverPositionGap = Vector3.zero;
                    _handoverRotationGap = Quaternion.identity;
                    _blendTimer = 0f;
                    break;

                case DriveState.Grabbed:
                    _thrownByRelease = false;
                    _pendingDetections = 0;
                    _hasPreviousPass = false;
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

                    // Same reasoning as above: only blend when there is a visible gap to hide.
                    _blendTimer = _handoverPositionGap.magnitude > NoticeableGapMeters
                        ? 0f
                        : reacquireBlendSeconds;

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

            bool wanted = kinematic || _originalIsKinematic;
            if (_rigidbody.isKinematic == wanted)
            {
                return;
            }

            if (wanted)
            {
                // Drop whatever Grabbable threw at it, otherwise the velocity is still there the next
                // time the prop becomes dynamic.
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }

            _rigidbody.isKinematic = wanted;

            // Interpolation smooths a body between physics steps, which is what a simulated prop wants.
            // A prop WE write once per rendered frame is already smooth, and interpolating it would show
            // a pose up to one physics step old - visible as the prop lagging behind the hand.
            _rigidbody.interpolation = wanted
                ? RigidbodyInterpolation.None
                : RigidbodyInterpolation.Interpolate;
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

        /// <summary>
        /// Converts a world pose into the parent space, so it survives the parent being moved or
        /// reparented - which is exactly what loading a spatial anchor does to the whole room.
        /// </summary>
        private Pose ToLocal(Pose world)
        {
            Transform parent = driven != null ? driven.parent : null;
            if (parent == null)
            {
                return world;
            }

            return new Pose(
                parent.InverseTransformPoint(world.position),
                Quaternion.Inverse(parent.rotation) * world.rotation);
        }

        private Pose FromLocal(Pose local)
        {
            Transform parent = driven != null ? driven.parent : null;
            if (parent == null)
            {
                return local;
            }

            return new Pose(
                parent.TransformPoint(local.position),
                parent.rotation * local.rotation);
        }

        /// <summary>
        /// One line per detection pass, so the acquisition gate can be chosen from numbers instead of
        /// guessed. What matters:
        ///
        ///   margin   - decision margin of the tag. Good detections measured 100 to 190, so a weak one
        ///              stands out and can be filtered by Minimum Decision Margin on the tracker.
        ///   camDist  - how far the tag was from the camera. A brief detection from across the room is
        ///              exactly the case that teleports the prop somewhere wrong.
        ///   jump     - how far the prop WOULD move if this detection were applied. The single most
        ///              important number: a trustworthy detection barely moves it.
        ///   spread   - distance to the previous pass, and next to it the distance the prop could have
        ///              travelled in that time. spread above allowed breaks the streak.
        ///   gap      - milliseconds since the previous pass, to tell a burst from a lone hit.
        /// </summary>
        private void LogAcquisition(int detectionPass, Vector3 position, float spread, float gapSeconds,
            float agreementLimit)
        {
            if (!logAcquisitions)
            {
                return;
            }

            float jump = Vector3.Distance(position, _appliedPose.position);

            Debug.Log("ACQ " + name +
                      "  pass " + detectionPass +
                      "  state " + _state +
                      "  margin " + _lastMargin.ToString("F0") +
                      "  camDist " + (_lastCameraDistance * 100f).ToString("F0") + "cm" +
                      "  jump " + (jump * 100f).ToString("F1") + "cm" +
                      "  spread " + Describe(spread * 100f, "cm") +
                      "  allowed " + Describe(agreementLimit * 100f, "cm") +
                      "  gap " + Describe(gapSeconds * 1000f, "ms") +
                      "  markers " + _accumulatedCount +
                      "  pending " + _pendingDetections + "/" + requiredDetectionsToReacquire, this);
        }

        private static string Describe(float value, string unit)
        {
            return float.IsInfinity(value) ? "-" : value.ToString("F1") + unit;
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

    }
}
