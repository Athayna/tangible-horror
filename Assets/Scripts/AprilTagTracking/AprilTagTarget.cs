using System;
using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction.Input;
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
            HandOverToGrab,

            /// <summary>
            /// Keep the offset to whichever hand was closest when the marker was lost, and follow that hand.
            /// Falls back to freezing when no hand was near.
            ///
            /// Built for the case where the prop is held in an unknown grip: the Interaction SDK only selects
            /// on a recognised grab gesture, and fingers behind the prop are regularly tracked as too far
            /// away for that. The wrist pose survives finger occlusion, so following it keeps the virtual
            /// prop within a few centimeters of the real one without needing any gesture at all. A grab that
            /// IS recognised still wins, because it also tracks the fingers.
            /// </summary>
            FollowNearestHand,

            /// <summary>
            /// When the marker is lost, hand the prop to the Interaction SDK by FORCING the grab on
            /// whichever hand is touching it. From that moment on everything is Meta's own, tested grab:
            /// rotation, passing it from hand to hand, releasing, throwing.
            ///
            /// This is the clean answer to the problem the other modes only work around. Their weakness is
            /// that they SIMULATE being held - they follow a joint, so the prop never really belongs to the
            /// hand and every switch has to be papered over. HandGrabInteractor.ForceSelect makes the grab
            /// real instead, and ForceSelect also overrides the unselect condition, so a finger dropping
            /// out of tracking can no longer cancel it - which was the root cause all along.
            /// </summary>
            ForceGrabOnLoss
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

        [Tooltip("USE HandOverToGrab. Marker visible: the marker drives. Marker lost: the prop stays exactly " +
                 "where it was last seen, kinematic, no gravity, and it never moves on its own. Grabbed: the " +
                 "Interaction SDK drives it, and releasing it lets it fall.\n\n" +
                 "FollowNearestHand and ForceGrabOnLoss MOVE the prop by themselves when a hand comes near - " +
                 "they were built to keep a held prop with the hand while the marker is occluded, but they " +
                 "guess, and a wrong guess puts the prop somewhere the real one is not.")]
        [SerializeField] private LostBehaviour whenLost = LostBehaviour.FreezeInPlace;

        [Tooltip("Only used by FollowFallbackTransform - e.g. a hand anchor of the camera rig.")]
        [SerializeField] private Transform fallbackFollow;

        [Tooltip("Only used by FollowNearestHand: the tracked hands, as Interaction SDK IHand sources. Auto " +
                 "found in the scene if left empty. NOT the camera rig's hand anchors - those are driven by " +
                 "OVRManager's node queries, which return nothing in this OpenXR project, so they sit frozen " +
                 "on the tracking origin. IHand.GetRootPose is what the Interaction SDK itself uses.")]
        [SerializeField, Interface(typeof(IHand))]
        private List<UnityEngine.Object> hands = new List<UnityEngine.Object>();

        [Tooltip("Only used by FollowNearestHand: how close the nearest FINGERTIP (or the wrist) has to be " +
                 "to the prop at the moment the marker is lost to count as holding it, in centimeters. This " +
                 "is a real contact test, so it can be tight - 4 to 6 cm. No joint within range means nobody " +
                 "is holding the prop, and it freezes in place instead.")]
        [SerializeField] private float handAttachDistanceCentimeters = 6f;

        [Tooltip("Downward speed in m/s at the moment the marker is lost above which the prop counts as " +
                 "FALLING and goes straight to physics instead of following a hand. Without this a dropped " +
                 "prop would stick to the hand that just released it while the real one hits the floor.")]
        [SerializeField] private float fallDetectSpeed = 0.4f;

        [Tooltip("Only used by FollowNearestHand: re-checks every frame which hand joint is closest, so a " +
                 "prop passed from one hand to the other follows along even while the marker stays hidden. " +
                 "Costs one joint query per finger per hand - a few dozen per frame.")]
        [SerializeField] private bool followNearestHandContinuously = true;

        [Tooltip("How long the OTHER hand has to touch the prop before it takes it over, in seconds. " +
                 "Stops a hand that only brushes past from stealing the prop, and stops the two hands from " +
                 "trading it back and forth.")]
        [SerializeField] private float handSwitchDwellSeconds = 0.2f;

        [Tooltip("Only used by FollowNearestHand: after following a hand for this long without the marker " +
                 "coming back, hand the prop to physics anyway, so it cannot stay stuck to a hand that is " +
                 "not actually holding it. 0 follows indefinitely.")]
        [SerializeField] private float followHandTimeoutSeconds = 2f;

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

        [Tooltip("Upper bound in m/s for the velocity handed to the Rigidbody when the marker is lost and " +
                 "physics takes over. Keeps a prop that was moving moving, without letting a bad velocity " +
                 "estimate launch it across the room.")]
        [SerializeField] private float releaseSpeedLimit = 1.5f;

        [Tooltip("Grace period after the marker is lost before the prop is handed to physics. It stays put " +
                 "and kinematic until then, so a hand that is already holding the real prop gets time to " +
                 "register as a grab instead of watching the virtual one drop. 0 releases immediately.")]
        [SerializeField] private float releaseToPhysicsDelaySeconds = 0.6f;

        [Tooltip("Shortest grab, in seconds, that counts as the participant deliberately holding the prop. " +
                 "The Interaction SDK regularly selects and unselects again within a frame or two when the " +
                 "fingers behind the prop drop out of tracking - reading that as a deliberate release is " +
                 "what made the prop fall out of a hand that was still holding it. Anything shorter is " +
                 "treated like a plain marker loss instead.")]
        [SerializeField] private float minGrabSecondsForRelease = 0.25f;

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

        private readonly OneEuroFilterVector3 _positionFilter = new OneEuroFilterVector3();

        private Pose _measuredPose;
        private Pose _appliedPose;
        // Stored relative to driven.parent, not in world space. The prop hangs under LocationRoot, and
        // WorldLockAnchor reparents LocationRoot onto the spatial anchor - a world space rest pose would
        // stay behind while the whole room moves. Relative to the parent it travels with the room.
        private Pose _frozenLocal;
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

        /// <summary>What the prop follows while the marker is gone, chosen when tracking was lost.</summary>
        private Transform _activeFollow;
        private HandGrabInteractable _handGrabInteractable;
        private HandGrabInteractor[] _interactors;
        private HandGrabInteractor _forcedInteractor;
        private float _lastForceAttempt = float.NegativeInfinity;
        private float _lastInteractorSearch = float.NegativeInfinity;
        private bool _forceGrabWorked;
        private IHand _activeHand;
        private IHand _candidateHand;
        private HandJointId _candidateJoint = HandJointId.HandWristRoot;
        private Pose _candidatePose;
        private float _candidateSince;
        private HandJointId _activeJoint = HandJointId.HandWristRoot;

        // Fingertips first, wrist last: the tips are what actually touch the prop, so the offset to them is
        // short and stays valid. The wrist is only the fallback for a prop resting against the palm.
        private static readonly HandJointId[] AttachJoints =
        {
            HandJointId.HandThumbTip,
            HandJointId.HandIndexTip,
            HandJointId.HandMiddleTip,
            HandJointId.HandRingTip,
            HandJointId.HandPinkyTip,
            HandJointId.HandWristRoot
        };
        private readonly List<IHand> _handCache = new List<IHand>();
        private bool _handsResolved;

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
        private float _frozenSince = float.NegativeInfinity;
        private bool _releasedToPhysics;
        private bool _physicsReleaseAllowed;
        private float _physicsReleaseDelay;
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

            _handGrabInteractable = driven.GetComponentInChildren<HandGrabInteractable>(true);

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

            // The grace period runs out here rather than in EnterState, because it ends on a timer and not
            // on a state change.
            if (WaitingForPhysicsRelease && Time.time - _frozenSince >= _physicsReleaseDelay)
            {
                _releasedToPhysics = true;
                ApplyKinematic(false);
                SeedReleaseVelocity();
            }

            // Grabbed: the Interaction SDK owns the transform. Frozen under HandOverToGrab: the physics
            // engine owns it - the prop is a normal dynamic body again and falls, rolls and collides. Both
            // cases are the same job for us: read, do not write, and stay ready to take it back.
            if (_state == DriveState.Grabbed || PhysicsOwnsTransform)
            {
                FollowExternalPose(deltaTime);
                return;
            }

            if (_state == DriveState.Frozen && whenLost == LostBehaviour.FollowNearestHand
                && followNearestHandContinuously)
            {
                MaybeReattachHandJoint();
            }
            else if (_state == DriveState.Frozen && whenLost == LostBehaviour.ForceGrabOnLoss)
            {
                // Force grab first - a real Interaction SDK grab is always better than following a joint,
                // because rotation, passing and releasing are then Meta's own logic. But if it cannot be
                // handed over - no interactor found yet, or none in contact - the prop still has to stay
                // with the hand rather than hang in the air, so the joint follow runs as the fallback.
                _forceGrabWorked = false;
                TryForceGrab();

                if (!_forceGrabWorked)
                {
                    MaybeReattachHandJoint();
                }
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

            // Asserted EVERY frame, not just on a state change. Grabbable sets isKinematic back to false
            // and applies a throw velocity whenever it unselects, and an unselect can happen in the
            // middle of a state - the prop then stayed dynamic while we kept writing its transform, and
            // gravity fighting our writes is exactly the wild up and down glitching.
            ApplyKinematic(true);

            _appliedPose = new Pose(position, rotation);
            driven.SetPositionAndRotation(position, rotation);

            SetRenderersVisible(_state == DriveState.MarkerDriven || whenLost != LostBehaviour.Hide);
        }

        /// <summary>
        /// True while the prop is a plain dynamic body: no marker, no hand, HandOverToGrab selected, and the
        /// grace period expired. Until then it stays pinned and kinematic, which is what stops it dropping
        /// out of a hand that is holding the real prop but has not been registered as a grab yet.
        /// </summary>
        private bool PhysicsOwnsTransform =>
            _state == DriveState.Frozen && _releasedToPhysics;

        /// <summary>Distance a joint has to be inside to TAKE the prop.</summary>
        private float AttachLimit => Mathf.Max(0f, handAttachDistanceCentimeters) * 0.01f;

        /// <summary>Wider than the attach limit, so an attached prop is not dropped by a little noise.</summary>
        private float KeepLimit => AttachLimit * 2f;

        private bool WaitingForPhysicsRelease =>
            _state == DriveState.Frozen && _physicsReleaseAllowed && !_releasedToPhysics;

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

                driven.SetPositionAndRotation(wanted.position, wanted.rotation);
            }

            // Keeps the marker side ready to take over from exactly here instead of snapping.
            _appliedPose = wanted;
            _positionFilter.Reset(wanted.position);
            _filteredRotation = wanted.rotation;
            SetRenderersVisible(true);
        }

        /// <summary>Both modes let a recognised grab take the prop over; they differ in what Frozen means.</summary>
        private bool GrabCanTakeOver =>
            whenLost == LostBehaviour.HandOverToGrab
            || whenLost == LostBehaviour.FollowNearestHand
            || whenLost == LostBehaviour.ForceGrabOnLoss;

        private DriveState ResolveState(bool markerFresh, bool grabbed)
        {
            if (!GrabCanTakeOver)
            {
                return markerFresh ? DriveState.MarkerDriven : DriveState.Frozen;
            }

            // A grab WE forced has no hysteresis on the way back. The marker is unambiguously the better
            // source, and the grab only exists because we asked for it, so the first fresh detection takes
            // the prop back. Requiring several detections here is what left it glued to the hand for 19
            // seconds: every Frozen/Grabbed transition resets the counter, and with the unselect condition
            // overridden the marker is the only way out at all.
            if (_forcedInteractor != null && markerFresh)
            {
                return DriveState.MarkerDriven;
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
            DriveState previous = _state;
            _state = next;

            switch (next)
            {
                case DriveState.MarkerDriven:
                    // The marker is the more accurate source, so it takes the prop back off the hand.
                    ReleaseForcedGrab();
                    _blendStartPose = _appliedPose;
                    _blendTimer = 0f;
                    _positionFilter.Reset(_appliedPose.position);
                    _filteredRotation = _appliedPose.rotation;
                    ApplyKinematic(true);
                    break;

                case DriveState.Frozen:
                    _frozenPose = _appliedPose;
                    _frozenLocal = ToLocal(_appliedPose);
                    _pendingDetections = 0;
                    LastObservedMarkerCount = 0;

                    // Where the prop lands depends on WHY it got here, and the two cases want opposite
                    // things. Marker lost: the real prop is probably still in a hand, so follow that hand and
                    // never drop it - a dropped prop is the largest error there is. Grab released: the
                    // participant let go on purpose, so a purely virtual prop has to fall like any object,
                    // and it must not stick back onto the hand that just released it.
                    // A grab that lasted only a frame or two is Interaction SDK noise, not a decision.
                    // Treating it as a release is what dropped the prop out of a hand that still held it.
                    bool releasedByHand = previous == DriveState.Grabbed
                                          && Time.time - _grabStartTime >= Mathf.Max(0f, minGrabSecondsForRelease);

                    if (logGrabEvents && previous == DriveState.Grabbed && !releasedByHand)
                    {
                        Debug.Log($"AprilTagTarget {name}: grab lasted only " +
                                  $"{(Time.time - _grabStartTime) * 1000f:F0} ms, treating it as tracking " +
                                  $"noise rather than a release.", this);
                    }


                    // The marker's own last velocity separates "still in a hand" from "already falling".
                    // A dropped prop must fall in VR too, otherwise the participant cannot find the real one
                    // again without looking at the floor.
                    // 0 disables it: a marker lost during a fast downward hand move would otherwise be
                    // mistaken for a drop, and then the prop leaves the place it was last seen.
                    bool wasFalling = fallDetectSpeed > 0f
                                      && _positionFilter.Velocity.y <= -fallDetectSpeed;

                    CaptureFallbackOffset(attachToHand: !releasedByHand && !wasFalling);
                    ConfigurePhysicsRelease(releasedByHand || wasFalling);

                    _frozenSince = Time.time;
                    _releasedToPhysics = false;
                    ApplyKinematic(true);

                    // Physics later continues exactly where the marker left off, so there is no gap to fade.
                    _handoverPositionGap = Vector3.zero;
                    _handoverRotationGap = Quaternion.identity;
                    _blendTimer = 0f;
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

        /// <summary>
        /// Hands the marker's last measured velocity to the Rigidbody when physics takes over, so a prop
        /// that was moving keeps moving instead of dropping straight down. Clamped, because a marker that
        /// vanishes during a fast move can leave a wild velocity estimate behind.
        /// </summary>
        private void SeedReleaseVelocity()
        {
            if (!_useRigidbody || _rigidbody.isKinematic)
            {
                return;
            }

            _rigidbody.linearVelocity = Vector3.ClampMagnitude(_positionFilter.Velocity, releaseSpeedLimit);
            _rigidbody.angularVelocity = Vector3.zero;
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
            if (_activeHand != null && _activeHand.GetJointPose(_activeJoint, out Pose joint))
            {
                return new Pose(
                    joint.position + joint.rotation * _fallbackLocalPosition,
                    joint.rotation * _fallbackLocalRotation);
            }

            if (_activeFollow != null)
            {
                return new Pose(
                    _activeFollow.position + _activeFollow.rotation * _fallbackLocalPosition,
                    _activeFollow.rotation * _fallbackLocalRotation);
            }

            return FromLocal(_frozenLocal);
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
        /// Decides what the prop follows while the marker is gone, and remembers the offset it had at that
        /// moment. FollowNearestHand picks the closer of the hand anchors, but only if one is actually within
        /// reach - otherwise nobody is holding the prop and freezing in place is the smaller error.
        /// </summary>
        private void CaptureFallbackOffset(bool attachToHand)
        {
            _activeFollow = null;
            _activeHand = null;
            Pose reference = default;
            bool haveReference = false;

            if (whenLost == LostBehaviour.FollowFallbackTransform && fallbackFollow != null)
            {
                _activeFollow = fallbackFollow;
                reference = new Pose(fallbackFollow.position, fallbackFollow.rotation);
                haveReference = true;
            }
            else if (whenLost == LostBehaviour.FollowNearestHand && attachToHand)
            {
                haveReference = FindNearestHandJoint(out _activeHand, out _activeJoint, out reference,
                    AttachLimit, true);
            }

            if (!haveReference)
            {
                return;
            }

            _fallbackLocalPosition = Quaternion.Inverse(reference.rotation) * (_appliedPose.position - reference.position);
            _fallbackLocalRotation = Quaternion.Inverse(reference.rotation) * _appliedPose.rotation;
        }

        /// <summary>
        /// Whether and when the prop may become a dynamic body while frozen.
        /// </summary>
        private void ConfigurePhysicsRelease(bool dropImmediately)
        {
            switch (whenLost)
            {
                case LostBehaviour.HandOverToGrab:
                    // Losing the marker must NEVER drop the prop - it stays exactly where it was last
                    // seen. Physics is only for the one case the participant caused on purpose: a real
                    // grab that was released (or, if enabled, a fall the marker actually measured).
                    // Release To Physics Delay Seconds is therefore unused in this mode.
                    _physicsReleaseAllowed = dropImmediately;
                    _physicsReleaseDelay = 0f;
                    break;

                case LostBehaviour.FollowNearestHand when dropImmediately:
                    _physicsReleaseAllowed = true;
                    _physicsReleaseDelay = 0f;
                    break;

                case LostBehaviour.FollowNearestHand when _activeHand != null:
                    // Following a hand: drop only after the timeout, so a prop cannot stay stuck to a hand
                    // that is not really holding it. 0 means follow indefinitely.
                    _physicsReleaseAllowed = followHandTimeoutSeconds > 0f;
                    _physicsReleaseDelay = followHandTimeoutSeconds;
                    break;

                default:
                    // No hand nearby, or a lost behaviour that pins the prop on purpose: stay put. Freezing
                    // in place is a bounded error, dropping from an unknown pose is not.
                    _physicsReleaseAllowed = false;
                    _physicsReleaseDelay = 0f;
                    break;
            }
        }

        /// <summary>
        /// The scene carries a dozen IHand implementations for two physical hands - data sources, filters,
        /// synthetic hands and references, all chained. They report the same pose, so keeping them all just
        /// multiplies the work and makes the log unreadable. One per handedness is enough.
        /// </summary>
        private void DedupeHandsByHandedness()
        {
            var kept = new Dictionary<Handedness, IHand>();

            foreach (IHand hand in _handCache)
            {
                try
                {
                    Handedness handedness = hand.Handedness;
                    if (!kept.TryGetValue(handedness, out IHand current) ||
                        SourceRank(hand) < SourceRank(current))
                    {
                        kept[handedness] = hand;
                    }
                }
                catch (Exception)
                {
                    // A wrapper whose reference is not resolved yet. Skip it, the chain has others.
                }
            }

            if (kept.Count > 0)
            {
                _handCache.Clear();
                _handCache.AddRange(kept.Values);
            }
        }

        /// <summary>
        /// Lower is better. Both hands must come from the same KIND of source, otherwise their poses are not
        /// comparable and the nearest-hand decision is biased - the log showed exactly that, one hand read
        /// from an interactor reference and the other from a synthetic hand. The raw data source is preferred
        /// because a SyntheticHand is deliberately modified to snap onto grab poses.
        /// </summary>
        private static int SourceRank(IHand hand)
        {
            string type = hand.GetType().Name;

            if (type.Contains("DataSource"))
            {
                return 0;
            }

            if (type.Contains("Synthetic") || type.Contains("Ref"))
            {
                return 2;
            }

            return 1;
        }

        /// <summary>
        /// Re-decides every frame which joint carries the prop. That is what makes passing it from one hand
        /// to the other work while the marker stays hidden, and it also lets a hand pick up a prop that was
        /// already frozen. The offset is re-captured from where the prop currently is, so a switch cannot
        /// produce a jump - only what it is attached to changes.
        ///
        /// Two thresholds keep it steady: a joint has to come inside the attach limit to take the prop, and
        /// it keeps it until twice that distance. A rival joint has to be closer by the switch margin.
        /// </summary>
        /// <summary>
        /// Hands the prop to the Interaction SDK as a real grab. Picks the hand whose nearest fingertip is
        /// actually touching, then forces that interactor to select our interactable.
        ///
        /// allowManualRelease stays false on purpose: that makes ForceSelect override the unselect
        /// condition as well, so the grab survives the finger tracking dropouts that used to cancel it
        /// after 40 ms. We give it back ourselves - when the marker returns, or when no fingertip is in
        /// contact any more.
        /// </summary>
        private void TryForceGrab()
        {
            if (_handGrabInteractable == null)
            {
                return;
            }

            // Retrying every frame would spam ForceSelect while a hand hovers just out of reach.
            if (Time.time - _lastForceAttempt < 0.1f)
            {
                return;
            }

            _lastForceAttempt = Time.time;
            EnsureInteractors();

            if (_interactors == null || _interactors.Length == 0)
            {
                return;
            }

            HandGrabInteractor best = null;
            float bestDistance = AttachLimit;
            HandJointId bestJoint = HandJointId.HandWristRoot;

            foreach (HandGrabInteractor interactor in _interactors)
            {
                if (interactor == null || !interactor.enabled)
                {
                    continue;
                }

                IHand hand = interactor.Hand;
                if (hand == null || !hand.IsTrackedDataValid)
                {
                    continue;
                }

                foreach (HandJointId joint in AttachJoints)
                {
                    if (!hand.GetJointPose(joint, out Pose pose))
                    {
                        continue;
                    }

                    float distance = Vector3.Distance(pose.position, _appliedPose.position);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = interactor;
                        bestJoint = joint;
                    }
                }
            }

            if (best == null)
            {
                // Nobody is touching it. Give any earlier forced grab back so a released prop can fall.
                ReleaseForcedGrab();
                return;
            }

            _forceGrabWorked = true;

            if (best == _forcedInteractor)
            {
                // Already forced on this hand. ISDK may have cancelled it, so force it again - that is the
                // self healing part, and it costs one call every 100 ms at most.
                best.ForceSelect(_handGrabInteractable);
                return;
            }

            ReleaseForcedGrab();

            Log("forcing a grab on " + DescribeHand(best.Hand) + " " + bestJoint + " at " +
                (bestDistance * 100f).ToString("F1") + " cm");

            best.ForceSelect(_handGrabInteractable);
            _forcedInteractor = best;
        }

        private void ReleaseForcedGrab()
        {
            if (_forcedInteractor == null)
            {
                return;
            }

            Log("releasing the forced grab on " + DescribeHand(_forcedInteractor.Hand));
            _forcedInteractor.ForceRelease();
            _forcedInteractor = null;
        }

        /// <summary>
        /// Looks for the hand grab interactors, and does NOT cache an empty result. The Interaction SDK
        /// builds its hand rig during startup, and over Link that takes a moment - the first search ran
        /// before the interactors existed, cached zero of them and never looked again.
        /// </summary>
        private void EnsureInteractors()
        {
            if (_interactors != null && _interactors.Length > 0)
            {
                return;
            }

            if (Time.time - _lastInteractorSearch < 0.5f)
            {
                return;
            }

            _lastInteractorSearch = Time.time;

            // Include inactive: the SDK parks interactors under interactor groups that drive them
            // directly, and their GameObjects are not necessarily active in the usual sense.
            HandGrabInteractor[] found = FindObjectsByType<HandGrabInteractor>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (found.Length == 0)
            {
                return;
            }

            _interactors = found;
            Debug.Log("[AprilTagTarget] " + name + ": found " + _interactors.Length +
                      " HandGrabInteractor(s) for ForceGrabOnLoss.", this);
        }

        private void MaybeReattachHandJoint()
        {
            bool found = FindNearestHandJoint(out IHand hand, out HandJointId joint, out Pose pose,
                AttachLimit, false);

            // Nothing touching, or the hand that already carries it is still the nearest: leave it alone.
            // Switching joints WITHIN one hand is never useful - every switch re-anchors the offset, and
            // re-anchoring cancels the hand's own motion. With five fingertips all 2 to 5 cm from a coin
            // in a palm grip that happened every frame, which is why a rotating palm left the prop
            // standing still.
            if (!found || hand == _activeHand)
            {
                _candidateHand = null;
                return;
            }

            // A forced grab is pending: do not fight it by also dragging the prop along a joint.
            if (_forcedInteractor != null)
            {
                return;
            }

            // A different hand is touching. It has to keep touching for a moment before it gets the prop,
            // otherwise a hand brushing past picks it up.
            if (hand != _candidateHand)
            {
                _candidateHand = hand;
                _candidateJoint = joint;
                _candidatePose = pose;
                _candidateSince = Time.time;
                return;
            }

            _candidateJoint = joint;
            _candidatePose = pose;

            if (Time.time - _candidateSince < Mathf.Max(0f, handSwitchDwellSeconds))
            {
                return;
            }

            Log((_activeHand == null ? "picked up by " : "handed over to ") +
                DescribeHand(_candidateHand) + " " + _candidateJoint);

            _activeHand = _candidateHand;
            _activeJoint = _candidateJoint;
            _candidateHand = null;

            // Re-captured from the CURRENT pose, which is what makes the switch invisible.
            _fallbackLocalPosition = Quaternion.Inverse(_candidatePose.rotation) *
                                     (_appliedPose.position - _candidatePose.position);
            _fallbackLocalRotation = Quaternion.Inverse(_candidatePose.rotation) * _appliedPose.rotation;
        }

        private void Log(string message)
        {
            if (logGrabEvents)
            {
                Debug.Log("AprilTagTarget " + name + ": " + message, this);
            }
        }

        /// <summary>
        /// Finds the hand JOINT closest to the prop, not the wrist. The wrist sits 10 to 15 cm from a held
        /// prop, and an offset that long turns every wrist rotation into a wide arc - which is exactly the
        /// "I rotate my hand and the prop swings away from it" failure. A fingertip is 2 to 3 cm away, so the
        /// lever arm nearly vanishes and the offset stays valid far longer. It also turns the distance test
        /// into a genuine contact test, which is what makes a tight threshold safe.
        /// </summary>
        private bool FindNearestHandJoint(out IHand nearestHand, out HandJointId nearestJoint,
            out Pose nearestPose, float limit, bool logResult)
        {
            EnsureHands();

            nearestHand = null;
            nearestJoint = HandJointId.HandWristRoot;
            nearestPose = default;
            float nearestDistance = float.PositiveInfinity;
            string report = string.Empty;

            foreach (IHand hand in _handCache)
            {
                if (hand == null || !hand.IsTrackedDataValid)
                {
                    report += "  |  " + DescribeHand(hand) + ": no valid tracking";
                    continue;
                }

                HandJointId bestJoint = HandJointId.HandWristRoot;
                Pose bestPose = default;
                float bestDistance = float.PositiveInfinity;

                foreach (HandJointId joint in AttachJoints)
                {
                    if (!hand.GetJointPose(joint, out Pose pose))
                    {
                        continue;
                    }

                    float distance = Vector3.Distance(pose.position, _appliedPose.position);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestJoint = joint;
                        bestPose = pose;
                    }
                }

                if (float.IsPositiveInfinity(bestDistance))
                {
                    report += "  |  " + DescribeHand(hand) + ": no joints";
                    continue;
                }

                report += "  |  " + DescribeHand(hand) + " " + bestJoint + ": " +
                          (bestDistance * 100f).ToString("F1") + " cm";

                if (bestDistance <= limit && bestDistance < nearestDistance)
                {
                    nearestDistance = bestDistance;
                    nearestHand = hand;
                    nearestJoint = bestJoint;
                    nearestPose = bestPose;
                }
            }

            if (logGrabEvents && logResult)
            {
                string outcome = nearestHand != null
                    ? "attached to " + DescribeHand(nearestHand) + " " + nearestJoint
                    : "none, freezing in place";
                Debug.Log("AprilTagTarget " + name + ": joint search, limit " +
                          (limit * 100f).ToString("F0") + " cm, " + _handCache.Count +
                          " hand(s):" + report + "  ->  " + outcome, this);
            }

            return nearestHand != null;
        }

        private static string DescribeHand(IHand hand)
        {
            // Deliberately does not read Handedness: on a HandRef that throws until the reference it
            // wraps has initialised itself, and this only ever feeds a log line.
            if (hand == null)
            {
                return "null";
            }

            string owner = hand.GetType().Name;

            try
            {
                return $"{hand.Handedness} ({owner})";
            }
            catch (Exception)
            {
                return owner;
            }
        }

        /// <summary>
        /// Collects the IHand sources. Explicitly NOT OVRCameraRig.leftHandAnchor / rightHandAnchor: those
        /// are written by OVRCameraRig.UpdateAnchors from OVRPlugin node queries, and in this project those
        /// deliver nothing - the measured proof is that centerEyeAnchor sat exactly on the tracking origin
        /// with identity rotation while the headset was clearly tracked. Following them means following a
        /// transform that never moves, which is why the prop only ever froze in place.
        /// </summary>
        private void EnsureHands()
        {
            if (_handsResolved)
            {
                return;
            }

            _handsResolved = true;
            _handCache.Clear();

            foreach (UnityEngine.Object candidate in hands)
            {
                if (candidate is IHand assigned)
                {
                    _handCache.Add(assigned);
                }
            }

            if (_handCache.Count > 0)
            {
                return;
            }

            foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (behaviour is IHand found && !_handCache.Contains(found))
                {
                    _handCache.Add(found);
                }
            }

            DedupeHandsByHandedness();

            if (_handCache.Count == 0)
            {
                Debug.LogWarning($"[AprilTagTarget] {name}: FollowNearestHand found no IHand in the scene. " +
                                 "Assign Hands manually, otherwise the prop can only freeze in place.", this);
                return;
            }

            Debug.Log($"[AprilTagTarget] {name}: using {_handCache.Count} auto-found hand source(s): " +
                      string.Join(", ", _handCache.ConvertAll(DescribeHand)), this);
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
