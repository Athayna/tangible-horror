using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TangibleHorror.AprilTagTracking
{
    /// <summary>
    /// Runs AprilTag detection and pose estimation on a background thread.
    ///
    /// jp.keijiro.apriltag's own TagDetector cannot do that: it finishes its pose estimation through
    /// Unity's job system, which has to be completed from the main thread, so the whole detection blocks
    /// the frame. Everything below the TagDetector - Detector, ImageU8, DetectionInfo, Pose - is plain
    /// P/Invoke and public, so driving those directly moves the entire cost off the main thread. The main
    /// thread only pays for one memcpy of the camera image.
    ///
    /// The pose conversion mirrors jp.keijiro.apriltag's PoseEstimationJob exactly (Y flip on the
    /// position, x and z negated on the quaternion), so the resulting marker frame is unchanged.
    /// </summary>
    internal sealed class AprilTagPipeline : IDisposable
    {
        /// <summary>One detected marker, pose relative to the camera.</summary>
        public struct Observation
        {
            public int Id;
            public Vector3 PositionInCamera;
            public Quaternion RotationInCamera;
            public float DecisionMargin;
        }

        public int Width { get; }
        public int Height { get; }
        public float LastDetectionMilliseconds { get; private set; }
        public bool IsBusy => Interlocked.CompareExchange(ref _busy, 0, 0) != 0;

        private readonly AprilTag.Interop.Detector _detector;
        private readonly AprilTag.Interop.Family _family;
        private readonly AprilTag.Interop.ImageU8 _image;
        private readonly Color32[] _pixels;
        private readonly List<Observation> _pending = new List<Observation>();
        private readonly object _gate = new object();
        private readonly Stopwatch _stopwatch = new Stopwatch();

        // Dedicated long-lived thread instead of the thread pool: no per-detection allocation, and it can
        // run below normal priority so the main and render thread keep their cores. That is what makes the
        // difference between "detection is off the main thread" and "the image stops stuttering".
        private readonly Thread _thread;
        private readonly SemaphoreSlim _workAvailable = new SemaphoreSlim(0);

        private int _busy;
        private bool _resultsReady;
        private volatile bool _shutdown;
        private bool _disposed;
        private float _workerMilliseconds;

        // Written by the main thread before the worker starts, read by the worker only.
        private double _tagSize;
        private float[] _tagSizeById;
        private double _focalX;
        private double _focalY;
        private double _centerX;
        private double _centerY;
        private float _minimumDecisionMargin;

        public AprilTagPipeline(int width, int height, int decimation, int threadCount, bool refineEdges,
            float quadSigma, float decodeSharpening)
        {
            Width = width;
            Height = height;
            _pixels = new Color32[width * height];

            _detector = AprilTag.Interop.Detector.Create();
            _family = AprilTag.Interop.Family.CreateTagStandard41h12();
            _detector.ThreadCount = Mathf.Clamp(threadCount, 1, 8);
            _detector.QuadDecimate = Mathf.Max(1, decimation);
            _detector.QuadSigma = Mathf.Max(0f, quadSigma);
            _detector.RefineEdges = refineEdges ? 1 : 0;
            _detector.DecodeSharpening = decodeSharpening;
            _detector.AddFamily(_family);
            _image = AprilTag.Interop.ImageU8.Create(width, height);

            LogFamilyGeometry();

            _thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "AprilTagDetector",
                Priority = System.Threading.ThreadPriority.BelowNormal
            };
            _thread.Start();
        }

        /// <summary>
        /// Reads width_at_border and total_width straight out of the native apriltag_family_t, because that
        /// ratio IS the definition of the tag size: the detector locks onto a quad and the homography maps
        /// that quad to a square of width_at_border cells, so the value handed to the pose estimator has to
        /// be the physical length of exactly that many cells of the printed image.
        ///
        /// struct apriltag_family (apriltag 3, LP64):
        ///   0  uint32 ncodes        (+4 padding)
        ///   8  uint64* codes
        ///  16  int    width_at_border
        ///  20  int    total_width
        ///  24  bool   reversed_border
        /// </summary>
        private void LogFamilyGeometry()
        {
            try
            {
                IntPtr family = _family.DangerousGetHandle();
                if (family == IntPtr.Zero)
                {
                    return;
                }

                int widthAtBorder = Marshal.ReadInt32(family, 16);
                int totalWidth = Marshal.ReadInt32(family, 20);
                bool reversedBorder = Marshal.ReadByte(family, 24) != 0;

                // A layout mismatch would show up as nonsense here rather than as a silent wrong number.
                if (totalWidth < 3 || totalWidth > 32 || widthAtBorder < 3 || widthAtBorder > totalWidth)
                {
                    Debug.LogWarning($"[AprilTagPipeline] apriltag_family_t layout not as expected " +
                                     $"(width_at_border {widthAtBorder}, total_width {totalWidth}). " +
                                     "Ignoring - the tag size ratio has to be taken from the print sheet.");
                    return;
                }

                Debug.Log($"[AprilTagPipeline] tagStandard41h12 geometry from the native family: " +
                          $"width_at_border {widthAtBorder}, total_width {totalWidth}, " +
                          $"reversed_border {reversedBorder}." +
                          $"\n  Tag Size Meters = {widthAtBorder}/{totalWidth} = " +
                          $"{(float)widthAtBorder / totalWidth:F4} x printed pattern width.");
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[AprilTagPipeline] Could not read the family geometry: {exception.Message}");
            }
        }

        private void WorkerLoop()
        {
            while (true)
            {
                _workAvailable.Wait();

                if (_shutdown)
                {
                    return;
                }

                Work();
            }
        }

        /// <summary>
        /// Main thread: copies the frame and starts detection. Returns false while a detection is still
        /// running, so the caller can simply try again next frame.
        /// </summary>
        public bool Submit(NativeArray<Color32> source, bool flipVertically, double tagSize, float[] tagSizeById,
            double focalX, double focalY, double centerX, double centerY, float minimumDecisionMargin)
        {
            if (_disposed || !source.IsCreated)
            {
                return false;
            }

            int pixelCount = Width * Height;
            if (source.Length < pixelCount)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                return false;
            }

            if (flipVertically)
            {
                for (int y = 0; y < Height; y++)
                {
                    NativeArray<Color32>.Copy(source, (Height - 1 - y) * Width, _pixels, y * Width, Width);
                }
            }
            else
            {
                NativeArray<Color32>.Copy(source, 0, _pixels, 0, pixelCount);
            }

            _tagSize = tagSize;
            _tagSizeById = tagSizeById;
            _focalX = focalX;
            _focalY = focalY;
            _centerX = centerX;
            _centerY = centerY;
            _minimumDecisionMargin = minimumDecisionMargin;

            _workAvailable.Release();
            return true;
        }

        /// <summary>
        /// Main thread: picks up the results of a finished detection. Returns false if none are waiting.
        /// </summary>
        public bool TryCollect(List<Observation> into)
        {
            lock (_gate)
            {
                if (!_resultsReady)
                {
                    return false;
                }

                into.Clear();
                into.AddRange(_pending);
                _pending.Clear();
                _resultsReady = false;
                LastDetectionMilliseconds = _workerMilliseconds;
                return true;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // The worker holds the native handles, so it has to finish before they are released.
            _shutdown = true;
            _workAvailable.Release();
            _thread?.Join(2000);
            _workAvailable.Dispose();

            if (_detector != null)
            {
                _detector.RemoveFamily(_family);
                _detector.Dispose();
            }

            _family?.Dispose();
            _image?.Dispose();
        }

        private void Work()
        {
            try
            {
                _stopwatch.Restart();
                ConvertToGrayscale();

                var results = new List<Observation>();
                using (AprilTag.Interop.DetectionArray detections = _detector.Detect(_image))
                {
                    for (int i = 0; i < detections.Length; i++)
                    {
                        ref AprilTag.Interop.Detection detection = ref detections[i];

                        if (detection.DecisionMargin < _minimumDecisionMargin)
                        {
                            continue;
                        }

                        // Markers of different physical size can be mixed, so the size is per ID.
                        var info = new AprilTag.Interop.DetectionInfo(
                            ref detection, LookupTagSize(detection.ID), _focalX, _focalY, _centerX, _centerY);

                        // Deliberately not a using declaration: that would make the local read-only, and
                        // pose.t / pose.R are ref returning members.
                        var pose = new AprilTag.Interop.Pose(ref info);
                        try
                        {
                            results.Add(new Observation
                            {
                                Id = detection.ID,
                                PositionInCamera = ToUnityPosition(ref pose.t),
                                RotationInCamera = ToUnityRotation(ref pose.R),
                                DecisionMargin = detection.DecisionMargin
                            });
                        }
                        finally
                        {
                            pose.Dispose();
                        }
                    }
                }

                _stopwatch.Stop();

                lock (_gate)
                {
                    _pending.Clear();
                    _pending.AddRange(results);
                    _resultsReady = true;
                    _workerMilliseconds = (float)_stopwatch.Elapsed.TotalMilliseconds;
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private double LookupTagSize(int id)
        {
            float[] map = _tagSizeById;
            if (map != null && id >= 0 && id < map.Length && map[id] > 0f)
            {
                return map[id];
            }

            return _tagSize;
        }

        // Same row order and channel as jp.keijiro.apriltag's ImageConverter: source row 0 is the bottom
        // of the Unity texture, AprilTag counts rows from the top, and the green channel stands in for
        // luminance.
        private void ConvertToGrayscale()
        {
            Span<byte> buffer = _image.Buffer;
            int stride = _image.Stride;

            for (int y = 0; y < Height; y++)
            {
                int destination = stride * (Height - 1 - y);
                int source = y * Width;

                for (int x = 0; x < Width; x++)
                {
                    buffer[destination + x] = _pixels[source + x].g;
                }
            }
        }

        private static Vector3 ToUnityPosition(ref AprilTag.Interop.Matd3x1 translation)
        {
            return new Vector3((float)translation.e0, -(float)translation.e1, (float)translation.e2);
        }

        private static Quaternion ToUnityRotation(ref AprilTag.Interop.Matd3x3 rotation)
        {
            Quaternion q = MatrixToQuaternion(rotation);
            // Right handed to left handed, mirroring the Y axis - identical to PoseEstimationJob.
            return new Quaternion(-q.x, q.y, -q.z, q.w);
        }

        // Standard Shepperd extraction on a row/column indexed rotation matrix.
        private static Quaternion MatrixToQuaternion(AprilTag.Interop.Matd3x3 m)
        {
            float m00 = (float)m.e00, m01 = (float)m.e01, m02 = (float)m.e02;
            float m10 = (float)m.e10, m11 = (float)m.e11, m12 = (float)m.e12;
            float m20 = (float)m.e20, m21 = (float)m.e21, m22 = (float)m.e22;

            float trace = m00 + m11 + m22;

            if (trace > 0f)
            {
                float s = Mathf.Sqrt(trace + 1f) * 2f;
                return new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }

            if (m00 > m11 && m00 > m22)
            {
                float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
                return new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }

            if (m11 > m22)
            {
                float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
                return new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }

            float sz = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
            return new Quaternion((m02 + m20) / sz, (m12 + m21) / sz, 0.25f * sz, (m10 - m01) / sz);
        }
    }
}
