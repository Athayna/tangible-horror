using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TangibleHorror.AprilTagTracking
{
    /// <summary>
    /// Spawns a small unfiltered axis cross on every detected marker. Use it to check that detection
    /// works at all, how far away it still works, and how much the raw pose jitters - the crosses are
    /// deliberately NOT smoothed, unlike <see cref="AprilTagTarget"/>.
    ///
    /// Red = marker +X, green = +Y, blue = +Z (marker normal).
    ///
    /// The flat plate has the configured tag size, which for tagStandard41h12 is 5 of the 9 cells of the
    /// printed pattern - so it is SUPPOSED to look clearly smaller than the printed marker, about 55 % of
    /// its width. Its edge should land on the black-to-white transition just inside the black ring, because
    /// that inner edge is the quad the detector actually locks onto. A plate that covers the whole printed
    /// pattern means the tag size is too large by 9/5.
    /// </summary>
    [RequireComponent(typeof(AprilTagTracker))]
    public class AprilTagDebugVisualizer : MonoBehaviour
    {
        [Tooltip("Length of each axis rod in meters.")]
        [SerializeField] private float axisLength = 0.05f;

        [Tooltip("Hide a marker's cross again after this many seconds without a detection.")]
        [SerializeField] private float hideAfterSeconds = 0.5f;

        [Tooltip("Material for the debug parts - ASSIGN ONE. Any URP material from the project works, the " +
                 "color is overridden per part anyway. GameObject.CreatePrimitive assigns the built-in " +
                 "Standard material, which has no URP variant and renders MAGENTA. The automatic fallbacks " +
                 "below only work reliably in the Editor: URP fills RenderPipelineAsset.defaultMaterial in " +
                 "the Editor but leaves it null in a build, and Shader.Find only sees shaders that the build " +
                 "actually includes. A serialized reference here is the only guaranteed path.")]
        [SerializeField] private Material debugMaterial;

        private static readonly string[] FallbackShaders =
        {
            "Universal Render Pipeline/Unlit",
            "Universal Render Pipeline/Lit",
            "Sprites/Default"
        };

        private AprilTagTracker _tracker;
        private Material _sourceMaterial;
        private readonly Dictionary<int, Visual> _visuals = new Dictionary<int, Visual>();

        private class Visual
        {
            public GameObject Root;
            public Transform Plate;
            public float LastSeenTime;
        }

        private void Awake()
        {
            _tracker = GetComponent<AprilTagTracker>();
            _sourceMaterial = ResolveSourceMaterial();
        }

        /// <summary>
        /// The primitives created at runtime get the built-in Standard material, which has no URP variant
        /// and renders magenta. Find something that actually renders instead.
        /// </summary>
        private Material ResolveSourceMaterial()
        {
            if (debugMaterial != null)
            {
                return debugMaterial;
            }

            // Filled by URP in the Editor, null in a build.
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline != null && pipeline.defaultMaterial != null)
            {
                Debug.Log("[AprilTagDebugVisualizer] Using the render pipeline default material.", this);
                return pipeline.defaultMaterial;
            }

            // Only finds shaders the build actually contains, so this can fail too.
            foreach (string shaderName in FallbackShaders)
            {
                Shader shader = Shader.Find(shaderName);
                if (shader != null)
                {
                    Debug.Log($"[AprilTagDebugVisualizer] Using fallback shader '{shaderName}'.", this);
                    return new Material(shader);
                }
            }

            Debug.LogError("[AprilTagDebugVisualizer] No usable material found, the debug parts will render " +
                           "MAGENTA. Assign 'Debug Material' in the inspector - any URP material from the " +
                           "project does the job.", this);
            return null;
        }

#if UNITY_EDITOR
        private void Reset()
        {
            AutoAssignMaterial();
        }

        private void OnValidate()
        {
            AutoAssignMaterial();
        }

        // Resolving in the Editor serializes the reference into the scene, which is what makes it
        // survive into the build.
        private void AutoAssignMaterial()
        {
            if (debugMaterial != null)
            {
                return;
            }

            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline != null && pipeline.defaultMaterial != null)
            {
                debugMaterial = pipeline.defaultMaterial;
            }
        }
#endif

        private void OnEnable()
        {
            _tracker.TagObserved += OnTagObserved;
        }

        private void OnDisable()
        {
            _tracker.TagObserved -= OnTagObserved;
        }

        private void OnTagObserved(int id, Pose pose)
        {
            if (!_visuals.TryGetValue(id, out Visual visual))
            {
                visual = new Visual();
                visual.Root = CreateVisual(id, out Transform plate);
                visual.Plate = plate;
                _visuals.Add(id, visual);
            }

            visual.Root.transform.SetPositionAndRotation(pose.position, pose.rotation);
            visual.LastSeenTime = Time.time;

            // Refreshed every observation, not only on creation: the tag size can be edited while the app
            // runs, and it is per ID. A plate stuck at the size it had when the marker was first seen looks
            // like a tracking error that is not there.
            if (visual.Plate != null)
            {
                float size = Mathf.Max(0.005f, _tracker.GetTagSizeMeters(id));
                var scale = new Vector3(size, size, size * 0.02f);
                if (visual.Plate.localScale != scale)
                {
                    visual.Plate.localScale = scale;
                }
            }

            if (!visual.Root.activeSelf)
            {
                visual.Root.SetActive(true);
            }
        }

        private void Update()
        {
            foreach (KeyValuePair<int, Visual> entry in _visuals)
            {
                Visual visual = entry.Value;
                if (visual.Root.activeSelf && Time.time - visual.LastSeenTime > hideAfterSeconds)
                {
                    visual.Root.SetActive(false);
                }
            }
        }

        private void OnDestroy()
        {
            foreach (KeyValuePair<int, Visual> entry in _visuals)
            {
                if (entry.Value.Root != null)
                {
                    Destroy(entry.Value.Root);
                }
            }

            _visuals.Clear();
        }

        private GameObject CreateVisual(int id, out Transform plate)
        {
            var root = new GameObject($"AprilTagDebug_{id}");
            root.transform.SetParent(transform, false);

            AddAxis(root.transform, Vector3.right, Color.red);
            AddAxis(root.transform, Vector3.up, Color.green);
            AddAxis(root.transform, Vector3.forward, Color.blue);
            plate = AddPlate(root.transform);

            return root;
        }

        private void AddAxis(Transform parent, Vector3 direction, Color color)
        {
            float thickness = axisLength * 0.08f;
            GameObject rod = CreateCube(parent, color);
            rod.transform.localPosition = direction * (axisLength * 0.5f);
            rod.transform.localRotation = Quaternion.identity;
            rod.transform.localScale = new Vector3(
                Mathf.Abs(direction.x) > 0.5f ? axisLength : thickness,
                Mathf.Abs(direction.y) > 0.5f ? axisLength : thickness,
                Mathf.Abs(direction.z) > 0.5f ? axisLength : thickness);
        }

        private Transform AddPlate(Transform parent)
        {
            float size = Mathf.Max(0.005f, _tracker.TagSizeMeters);
            GameObject plate = CreateCube(parent, new Color(1f, 1f, 0f, 1f));
            plate.transform.localPosition = Vector3.zero;
            plate.transform.localScale = new Vector3(size, size, size * 0.02f);
            return plate.transform;
        }

        private GameObject CreateCube(Transform parent, Color color)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "DebugPart";
            cube.transform.SetParent(parent, false);

            Collider collider = cube.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            var meshRenderer = cube.GetComponent<Renderer>();

            if (_sourceMaterial != null)
            {
                meshRenderer.sharedMaterial = _sourceMaterial;
            }

            // Touching .material clones the shared material, so the color stays local to this part.
            meshRenderer.material.color = color;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;

            return cube;
        }
    }
}
