using UnityEngine;
using Clipper2Lib;
using System.Linq;
using System.Collections.Generic;
using Unity.Collections;
using andywiecko.BurstTriangulator;
using Unity.Mathematics;
using System;
using System.Threading.Tasks;
using Unity.Collections.NotBurstCompatible;
using Valve.VR;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ChaperoneMesh : MonoBehaviour
{
    [Header("Mesh Settings")]
    public float uvScale = 0.1f;

    [Header("Simplification Pipeline")]
    [Tooltip("Ramer-Douglas-Peucker line simplification tolerance. Higher values create simpler lines.")]
    public float lineError = 0.04f; // 4 cm
    [Tooltip("The maximum number of vertices the final polygon should have.")]
    public int maxVertices = 256;

    [Tooltip("The maximum number of vertices the current polygon while drawing should have.")]
    public int maxDrawingVertices = 512;

    [Header("Coordinate System")]
    [Tooltip("If true, transforms the mesh into Raw/Uncalibrated space. If false, keeps it in Standing space.")]
    public bool createInRawSpace = false;

    [Header("Rectangle Fitting")]
    public int gridResolution = 100;

    [Header("Boundary Line")]
    public MeshFilter lineMeshFilter;
    public float lineThickness = 0.0075f; // 7.5 cm

    [Header("Floor Rect")]
    public MeshFilter floorRectMeshFilter;

    [Header("VR References")]

    public Transform head;
    public SteamVR_Action_Boolean headsetOnHead;

    private List<Vector3> _worldPoints = new List<Vector3>();
    private Mesh _mesh;
    private PlayArea _playArea;
    
    private struct MeshUpdateData
    {
        public Vector3[] vertices;
        public int[] triangles;
    }

    // Queue for actions to be executed on the main thread
    private readonly Queue<Action> _mainThreadActions = new Queue<Action>();

    void Start()
    {
        _mesh = new Mesh { name = "ChaperoneMesh" };
        GetComponent<MeshFilter>().mesh = _mesh;

        lineMeshFilter.mesh = new Mesh { name = "ChaperoneLineMesh" };

        if (floorRectMeshFilter != null)
        {
            floorRectMeshFilter.mesh = new Mesh { name = "ChaperoneFloorRectMesh" };
        }

        LoadPlayArea();
    }

    void LateUpdate()
    {
        lock (_mainThreadActions)
        {
            while (_mainThreadActions.Count > 0) _mainThreadActions.Dequeue().Invoke();
        }

        if (!HasPlayArea() && headsetOnHead.GetState(SteamVR_Input_Sources.Head))
        {
            CreateDefaultPlayArea();
        }
    }

    public void LoadPlayArea()
    {
        _playArea = PSVR2SharedMemory.GetPlayArea();
        if (_playArea.playAreaRect == null)
        {
            _playArea.playAreaRect = new float[2];
        }
        if (_playArea.standingCenter == null)
        {
            _playArea.standingCenter = new float[3];
        }
        if (_playArea.points == null)
        {
            _playArea.points = new float[512];
        }

        _worldPoints.Clear();
        for (int i = 0; i < _playArea.pointCount; i++)
        {
            Vector2 driverPoint = new Vector2(_playArea.points[i * 2], _playArea.points[i * 2 + 1]);
            _worldPoints.Add(TransformDriverToWorld(driverPoint));
        }
        
        RefreshMesh();
    }

    public bool HasPlayArea()
    {
        return _worldPoints.Count > 2;
    }

    public void CreateDefaultPlayArea()
    {
        _worldPoints.Clear();

        // Make a default rectangle 2m x 2m where it is 1.5m below the head
        float halfSize = 1.0f;
        float headHeightOffset = -1.5f;

        float yaw = head.rotation.eulerAngles.y;

        _worldPoints.Add(Quaternion.Euler(0, yaw, 0) * new Vector3(-halfSize, headHeightOffset, -halfSize) + head.position);
        _worldPoints.Add(Quaternion.Euler(0, yaw, 0) * new Vector3(halfSize, headHeightOffset, -halfSize) + head.position);
        _worldPoints.Add(Quaternion.Euler(0, yaw, 0) * new Vector3(halfSize, headHeightOffset, halfSize) + head.position);
        _worldPoints.Add(Quaternion.Euler(0, yaw, 0) * new Vector3(-halfSize, headHeightOffset, halfSize) + head.position);

        AdjustFloorHeight(headHeightOffset + head.position.y);

        RefreshMesh();
    }

    private Task floorTask = null;
    private Task lineTask = null;
    private Task rectTask = null;

    public void RefreshMesh()
    {
        if (!HasPlayArea())
        {
            _mesh.Clear();
            lineMeshFilter.mesh.Clear();
            if (floorRectMeshFilter != null && floorRectMeshFilter.mesh != null)
            {
                floorRectMeshFilter.mesh.Clear();
            }
            return;
        }

        if (floorTask == null || floorTask.IsCompleted)
        {
            floorTask = Task.Run(() =>
            {
                try
                {
                    GenerateFloorMesh();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error generating floor mesh: {ex.Message}\n{ex.StackTrace}");
                }
            });
        }

        if (lineTask == null || lineTask.IsCompleted)
        {
            lineTask = Task.Run(() =>
            {
                GenerateLineMesh();
            });
        }

        if (floorRectMeshFilter != null && (rectTask == null || rectTask.IsCompleted))
        {
            float rectW = (_playArea.playAreaRect != null && _playArea.playAreaRect.Length >= 2) ? _playArea.playAreaRect[0] : 0f;
            float rectL = (_playArea.playAreaRect != null && _playArea.playAreaRect.Length >= 2) ? _playArea.playAreaRect[1] : 0f;
            float yawVal = _playArea.yaw;
            float[] centerCopy = new float[3];
            if (_playArea.standingCenter != null && _playArea.standingCenter.Length >= 3)
            {
                centerCopy[0] = _playArea.standingCenter[0];
                centerCopy[1] = _playArea.standingCenter[1];
                centerCopy[2] = _playArea.standingCenter[2];
            }

            rectTask = Task.Run(() =>
            {
                try
                {
                    GenerateFloorRectMesh(rectW, rectL, yawVal, centerCopy);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error generating floor rect mesh: {ex.Message}\n{ex.StackTrace}");
                }
            });
        }
    }

    private void GenerateFloorMesh()
    {
        if (_worldPoints.Count < 3)
        {
            lock (_mainThreadActions)
            {
                _mainThreadActions.Enqueue(() =>
                {
                    _mesh.Clear();
                });
            }
            return;
        }

        List<Vector3> copyPoints = _worldPoints;

        // Use BurstTriangulator for high-performance triangulation.
        var input = new NativeArray<double2>(copyPoints.Count, Allocator.TempJob);
        for (int i = 0; i < copyPoints.Count; i++)
        {
            input[i] = new double2(copyPoints[i].x, copyPoints[i].z);
        }

        // Define the polygon edges as constraints for the triangulation.
        var constraints = new NativeArray<int>(copyPoints.Count * 2, Allocator.TempJob);
        for (int i = 0; i < copyPoints.Count; i++)
        {
            constraints[i * 2] = i;
            constraints[i * 2 + 1] = (i + 1) % copyPoints.Count;
        }

        using var triangulator = new Triangulator(Allocator.TempJob)
        {
            Input = { Positions = input, ConstraintEdges = constraints },
            Settings = { RestoreBoundary = true }
        };

        triangulator.Run();

        var meshData = new MeshUpdateData
        {
            vertices = copyPoints.ToArray(),
            triangles = triangulator.Output.Triangles.ToArrayNBC()
        };

        input.Dispose();
        constraints.Dispose();

        lock (_mainThreadActions)
        {
            _mainThreadActions.Enqueue(() =>
            {
                _mesh.Clear();
                _mesh.vertices = meshData.vertices;
                _mesh.triangles = meshData.triangles;

                _mesh.RecalculateNormals();
                _mesh.RecalculateBounds();
            });
        }
    }

    private void GenerateLineMesh()
    {
        if (_worldPoints.Count < 3)
        {
            lock (_mainThreadActions)
            {
                _mainThreadActions.Enqueue(() =>
                {
                    _mesh.Clear();
                });
            }
            return;
        }

        List<Vector3> copyPoints = _worldPoints;

        var vertices = new Vector3[copyPoints.Count * 2];
        var triangles = new int[copyPoints.Count * 6];
        float halfThickness = lineThickness / 2.0f;

        for (int i = 0; i < copyPoints.Count; i++)
        {
            int prevIndex = (i + copyPoints.Count - 1) % copyPoints.Count;
            int nextIndex = (i + 1) % copyPoints.Count;

            Vector3 p_prev = copyPoints[prevIndex];
            Vector3 p_curr = copyPoints[i];
            Vector3 p_next = copyPoints[nextIndex];

            Vector3 v_in = (p_curr - p_prev).normalized;
            Vector3 v_out = (p_next - p_curr).normalized;

            Vector3 n_in = new Vector3(-v_in.z, 0, v_in.x).normalized;
            Vector3 n_out = new Vector3(-v_out.z, 0, v_out.x).normalized;

            Vector3 bisector = (n_in + n_out).normalized;

            float angle = Vector3.Angle(n_in, n_out);
            float miterLength = halfThickness / Mathf.Cos(angle * 0.5f * Mathf.Deg2Rad);

            Vector3 miterVector = bisector * miterLength;

            // Check for very sharp angles to prevent extreme miter joints
            if (miterLength > halfThickness * 5) // Cap miter length
            {
                miterVector = bisector * (halfThickness * 5);
            }

            vertices[i * 2 + 0] = p_curr - miterVector; // Inner vertex
            vertices[i * 2 + 1] = p_curr + miterVector; // Outer vertex
        }

        for (int i = 0; i < copyPoints.Count; i++)
        {
            int next_i = (i + 1) % copyPoints.Count;

            int i0 = i * 2;
            int i1 = i * 2 + 1;
            int i2 = next_i * 2;
            int i3 = next_i * 2 + 1;

            // Triangle 1
            triangles[i * 6 + 0] = i0;
            triangles[i * 6 + 1] = i3;
            triangles[i * 6 + 2] = i1;

            // Triangle 2
            triangles[i * 6 + 3] = i0;
            triangles[i * 6 + 4] = i2;
            triangles[i * 6 + 5] = i3;
        }

        var meshData = new MeshUpdateData
        {
            vertices = vertices.ToArray(),
            triangles = triangles.ToArray()
        };

        lock (_mainThreadActions)
        {
            _mainThreadActions.Enqueue(() =>
            {
                Mesh lineMesh = lineMeshFilter.mesh;
                lineMesh.Clear();
                lineMesh.vertices = meshData.vertices;
                lineMesh.triangles = meshData.triangles;
                lineMesh.RecalculateNormals();
                lineMesh.RecalculateBounds();
            });
        }
    }

    private Vector3 TransformDriverToWorld(Vector2 driverPoint, float[] center, float yaw)
    {
        Vector3 basePos = new Vector3(center[0], center[1], center[2]);
        Quaternion yawRotation = Quaternion.Euler(0, yaw * Mathf.Rad2Deg, 0);
        Vector3 p = new Vector3(driverPoint.x, 0, driverPoint.y);
        Vector3 g = (yawRotation * p) - basePos;
        g.z = -g.z;
        return g;
    }

    private void GenerateFloorRectMesh(float rectW, float rectL, float yawVal, float[] centerCopy)
    {
        if (floorRectMeshFilter == null) return;

        if (rectW <= 0f || rectL <= 0f)
        {
            lock (_mainThreadActions)
            {
                _mainThreadActions.Enqueue(() =>
                {
                    if (floorRectMeshFilter != null && floorRectMeshFilter.mesh != null)
                    {
                        floorRectMeshFilter.mesh.Clear();
                    }
                });
            }
            return;
        }

        float halfW = rectW / 2.0f;
        float halfL = rectL / 2.0f;

        Vector2 c0 = new Vector2(-halfW, -halfL);
        Vector2 c1 = new Vector2(halfW, -halfL);
        Vector2 c2 = new Vector2(halfW, halfL);
        Vector2 c3 = new Vector2(-halfW, halfL);

        Vector3 w0 = TransformDriverToWorld(c0, centerCopy, yawVal);
        Vector3 w1 = TransformDriverToWorld(c1, centerCopy, yawVal);
        Vector3 w2 = TransformDriverToWorld(c2, centerCopy, yawVal);
        Vector3 w3 = TransformDriverToWorld(c3, centerCopy, yawVal);

        Vector3[] vertices = new Vector3[] { w0, w1, w2, w3 };
        
        int[] triangles = new int[]
        {
            // Face up
            0, 3, 2,
            0, 2, 1,
            // Face down
            0, 1, 2,
            0, 2, 3
        };

        Vector2[] uvs = new Vector2[]
        {
            new Vector2(0, 0),
            new Vector2(1, 0),
            new Vector2(1, 1),
            new Vector2(0, 1)
        };

        lock (_mainThreadActions)
        {
            _mainThreadActions.Enqueue(() =>
            {
                if (floorRectMeshFilter != null && floorRectMeshFilter.mesh != null)
                {
                    Mesh rectMesh = floorRectMeshFilter.mesh;
                    rectMesh.Clear();
                    rectMesh.vertices = vertices;
                    rectMesh.triangles = triangles;
                    rectMesh.uv = uvs;
                    rectMesh.RecalculateNormals();
                    rectMesh.RecalculateBounds();
                }
            });
        }
    }

    private Vector3 TransformDriverToWorld(Vector2 driverPoint)
    {
        // Reverse your GetTransformedPoints logic here
        Vector3 basePos = new Vector3(_playArea.standingCenter[0], _playArea.standingCenter[1], _playArea.standingCenter[2]);
        Quaternion yawRotation = Quaternion.Euler(0, _playArea.yaw * Mathf.Rad2Deg, 0);
        Vector3 p = new Vector3(driverPoint.x, 0, driverPoint.y);
        Vector3 g = (yawRotation * p) - basePos;
        g.z = -g.z;
        return g;
    }

    private Vector2 TransformWorldToDriver(Vector3 worldPoint)
    {
        worldPoint.z = -worldPoint.z;
        Vector3 basePos = new Vector3(_playArea.standingCenter[0], _playArea.standingCenter[1], _playArea.standingCenter[2]);
        Quaternion inverseYaw = Quaternion.Euler(0, -_playArea.yaw * Mathf.Rad2Deg, 0);

        // First, transform the world point relative to the base position
        Vector3 relativeWorldPoint = worldPoint + basePos;

        // Then, apply the inverse yaw rotation
        Vector3 driverSpacePoint3D = inverseYaw * relativeWorldPoint;

        // Return the X and Z components as a Vector2
        return new Vector2(driverSpacePoint3D.x, driverSpacePoint3D.z);
    }

    /// <summary>
    /// Checks if a world-space point is inside the chaperone floor mesh.
    /// </summary>
    public bool IsPointInside(Vector3 worldPoint)
    {
        List<Vector3> polyPoints = _worldPoints;

        bool inside = false;
        for (int i = 0, j = polyPoints.Count - 1; i < polyPoints.Count; j = i++)
        {
            if (((polyPoints[i].z > worldPoint.z) != (polyPoints[j].z > worldPoint.z)) &&
                (worldPoint.x < (polyPoints[j].x - polyPoints[i].x) * (worldPoint.z - polyPoints[i].z) / (polyPoints[j].z - polyPoints[i].z) + polyPoints[i].x))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>
    /// Modifies the chaperone geometry by adding or subtracting a shape.
    /// </summary>
    public void ModifyPoints(List<Vector3> brushWorldPoints, bool add)
    {
        // Convert current world points to driver space for the boolean operation
        var currentDriverPoints = _worldPoints.Select(p => TransformWorldToDriver(p)).ToList();
        var brushDriverPoints = brushWorldPoints.Select(p => TransformWorldToDriver(p)).ToList();

        // --- 1. Boolean Operation (Clipper) ---
        var subj = new Paths64();
        if (currentDriverPoints.Count > 0)
            subj.Add(Vector2ToClipperPath(currentDriverPoints));

        var clip = new Paths64();
        clip.Add(Vector2ToClipperPath(brushDriverPoints));

        var clipper = new Clipper64();
        clipper.AddSubject(subj);
        clipper.AddClip(clip);

        var solution = new Paths64();
        var clipType = add ? ClipType.Union : ClipType.Difference;

        clipper.Execute(clipType, FillRule.NonZero, solution);

        if (solution.Count > 0 && solution[0].Count > 0)
        {
            // Find the largest resulting polygon by area
            int largestPolyIndex = 0;
            double maxArea = 0.0;
            for (int i = 0; i < solution.Count; i++)
            {
                double area = ClipperPathArea(solution[i]);
                if (area > maxArea)
                {
                    maxArea = area;
                    largestPolyIndex = i;
                }
            }
            
            // Convert the result back to world points
            var resultDriverPoints = ClipperPathToVector2(solution[largestPolyIndex]);
            _worldPoints = resultDriverPoints.Select(p => TransformDriverToWorld(p)).ToList();
        }
        else
        {
            _worldPoints.Clear();
        }
    }

    public void Simplify(bool isDrawing = false)
    {
        if (_worldPoints.Count > 0)
        {
            var processedPoints = _worldPoints.Select(p => TransformWorldToDriver(p)).ToList();
            if (isDrawing)
            {
                // --- Decimation Step (Vertex Culling) ---
                if (processedPoints.Count > maxDrawingVertices) {
                    processedPoints = SimplifyByCount(processedPoints, maxDrawingVertices);
                }
            }
            else
            {
                // --- RDP Step (Line Simplification) ---
                if (processedPoints.Count > 0 && lineError > 0)
                    processedPoints = SimplifyRDP(processedPoints, lineError);

                // --- Decimation Step (Vertex Culling) ---
                if (processedPoints.Count > maxVertices)
                    processedPoints = SimplifyByCount(processedPoints, maxVertices);
            }

            // Convert back to world points
            _worldPoints = processedPoints.Select(p => TransformDriverToWorld(p)).ToList();
        }
        else
        {
            _worldPoints.Clear();
        }

        // After simplifying, update the PlayArea struct to be saved.
        var finalDriverPoints = _worldPoints.Select(p => TransformWorldToDriver(p)).ToList();

        _playArea.pointCount = Mathf.Min(finalDriverPoints.Count, 256);
        for (int i = 0; i < _playArea.pointCount; i++)
        {
            _playArea.points[i * 2] = finalDriverPoints[i].x;
            _playArea.points[i * 2 + 1] = finalDriverPoints[i].y;
        }
        // Clear remaining part of the array
        for (int i = _playArea.pointCount * 2; i < _playArea.points.Length; i++)
        {
            _playArea.points[i] = 0;
        }

        RefreshMesh();
    }

    #region Clipper Helpers

    private const float CLIPPER_SCALE = 10000.0f; // Scale for float-to-int conversion

    private Path64 Vector2ToClipperPath(List<Vector2> points)
    {
        var path = new Path64(points.Count);
        foreach (var p in points)
        {
            path.Add(new Point64(p.x * CLIPPER_SCALE, p.y * CLIPPER_SCALE));
        }
        return path;
    }

    private double ClipperPathArea(Path64 path)
    {
        if (path.Count < 3)
            return 0.0;

        double area = 0.0;
        for (int i = 0; i < path.Count; i++)
        {
            int j = (i + 1) % path.Count;
            area += ((double)path[i].X * path[j].Y) - ((double)path[j].X * path[i].Y);
        }
        return System.Math.Abs(area / 2.0);
    }

    private List<Vector2> ClipperPathToVector2(Path64 path)
    {
        var points = new List<Vector2>(path.Count);
        foreach (var p in path)
        {
            points.Add(new Vector2(p.X / CLIPPER_SCALE, p.Y / CLIPPER_SCALE));
        }
        return points;
    }

    #endregion
    
    public void AddPoint(Vector3 worldPosition)
    {
        if (_worldPoints.Count < 3)
        {
            _worldPoints.Add(worldPosition);
        }
        else
        {
            // Find the closest segment to insert the new point
            float minDistance = float.MaxValue;
            int insertIndex = 0;

            for (int i = 0; i < _worldPoints.Count; i++)
            {
                Vector3 p1 = _worldPoints[i];
                int next_i = (i + 1) % _worldPoints.Count;
                Vector3 p2 = _worldPoints[next_i];

                Vector3 segment = p2 - p1;
                float segmentLengthSqr = segment.sqrMagnitude;
                if (segmentLengthSqr == 0.0f) continue;

                float t = Mathf.Clamp01(Vector3.Dot(worldPosition - p1, segment) / segmentLengthSqr);
                Vector3 projection = p1 + t * segment;
                float distSqr = (worldPosition - projection).sqrMagnitude;

                if (distSqr < minDistance)
                {
                    minDistance = distSqr;
                    insertIndex = next_i;
                }
            }

            _worldPoints.Insert(insertIndex, worldPosition);
        }

        RefreshMesh();
    }

    public void RemovePoint(Vector3 worldPosition)
    {
        if (_worldPoints.Count == 0) return;

        List<Vector3> copyPoints = _worldPoints;
        Vector3 localPos = transform.InverseTransformPoint(worldPosition);

        int closestIndex = -1;
        float minDistanceSqr = float.MaxValue;

        for (int i = 0; i < copyPoints.Count; i++)
        {
            float distSqr = (copyPoints[i] - localPos).sqrMagnitude;
            if (distSqr < minDistanceSqr)
            {
                minDistanceSqr = distSqr;
                closestIndex = i;
            }
        }

        if (closestIndex != -1)
        {
            _worldPoints.RemoveAt(closestIndex);
            RefreshMesh();
        }
    }

    /// <summary>
    /// Finds the index of the nearest vertex to a given world position.
    /// </summary>
    /// <param name="worldPoint">The point in world space to search near.</param>
    /// <param name="snappedPoint">The world position of the found vertex.</param>
    /// <returns>The index of the nearest vertex, or -1 if no points exist.</returns>
    public int FindNearestPoint(Vector3 worldPoint, out Vector3 snappedPoint)
    {
        snappedPoint = Vector3.zero;
        if (_worldPoints.Count == 0) return -1;

        List<Vector3> copyPoints = _worldPoints;
        Vector3 localPos = transform.InverseTransformPoint(worldPoint);

        int closestIndex = -1;
        float minDistanceSqr = float.MaxValue;

        for (int i = 0; i < copyPoints.Count; i++)
        {
            float distSqr = (copyPoints[i] - localPos).sqrMagnitude;
            if (distSqr < minDistanceSqr)
            {
                minDistanceSqr = distSqr;
                closestIndex = i;
            }
        }

        if (closestIndex != -1)
        {
            snappedPoint = transform.TransformPoint(copyPoints[closestIndex]);
        }

        return closestIndex;
    }

    /// <summary>
    /// Moves a vertex at a specific index to a new world position.
    /// </summary>
    /// <param name="index">The index of the vertex to move.</param>
    /// <param name="newWorldPosition">The new position in world space.</param>
    public void MovePoint(int index, Vector3 newWorldPosition)
    {
        if (index >= 0 && index < _worldPoints.Count)
        {
            _worldPoints[index] = newWorldPosition;
            RefreshMesh();
        }
    }

    public void RemovePointAt(int index)
    {
        if (index >= 0 && index < _worldPoints.Count)
        {
            _worldPoints.RemoveAt(index);
            RefreshMesh();
        }
    }

    /// <summary>
    /// Adjusts the floor height based on a world-space Y coordinate.
    /// </summary>
    /// <param name="worldY">The new Y coordinate for the floor in world space.</param>
    public void AdjustFloorHeight(float worldY)
    {
        // The world points are relative to the standing center.
        // To adjust the floor, we need to adjust the standing center's Y.
        // The current floor is at _playArea.standingCenter[1] in driver space, which corresponds to some world Y.
        // The new floor is at worldY. We need to find the new standingCenter[1].
        // From TransformWorldToDriver, worldPoint.y is not used, but we can infer the relationship.
        // A lower worldY means a higher standingCenter[1] because of the coordinate system inversion.
        _playArea.standingCenter[1] = -worldY;
        _playArea.height = -worldY;

        // Move all world points (edit just y)
        for (int i = 0; i < _worldPoints.Count; i++)
        {
            Vector3 p = _worldPoints[i];
            _worldPoints[i] = new Vector3(p.x, worldY, p.z);
        }

        RefreshMesh();
    }

    /// <summary>
    /// Get current floor height in world space.
    /// </summary>
    /// <returns>The Y coordinate of the floor in world space.</returns>
    public float GetFloorHeight()
    {
        return -_playArea.standingCenter[1];
    }


    /// <summary>
    /// Transforms all chaperone points.
    /// </summary>
    /// <param name="translation">The translation vector.</param>
    /// <param name="yawRotation">The rotation on the Y axis in degrees.</param>
    /// <param name="pivot">The pivot point for the rotation.</param>
    public void TransformPoints(Vector3 translation, float yawRotation, Vector3 pivot)
    {
        for (int i = 0; i < _worldPoints.Count; i++)
        {
            Vector3 relativePos = _worldPoints[i] - pivot;
            relativePos = Quaternion.Euler(0, yawRotation, 0) * relativePos;
            _worldPoints[i] = pivot + relativePos + translation;
        }
        RefreshMesh();
    }

    public void SaveToSharedMemory()
    {
        // Find the largest rectangle based on head orientation
        if (head != null && _worldPoints.Count >= 3)
        {
            PathD polygon = new PathD(_worldPoints.Count);
            foreach (var p in _worldPoints)
            {
                polygon.Add(new PointD(p.x, p.z));
            }

            Vector3 headForward = head.forward;
            headForward.y = 0;
            headForward.Normalize();
            double angle = Math.Atan2(headForward.z, headForward.x);

            PathD largestRect = LargestRectangleFinder.FindLargestRectAtAngle(polygon, angle, gridResolution, new PointD(head.position.x, head.position.z));

            if (largestRect.Count == 4)
            {
                // Update _playArea with the rectangle's properties
                _playArea.standingCenter[0] = (float)(largestRect[0].x + largestRect[2].x) / -2.0f;
                _playArea.standingCenter[2] = (float)(largestRect[0].y + largestRect[2].y) / 2.0f;
                _playArea.playAreaRect[1] = (float)Math.Sqrt(Math.Pow(largestRect[1].x - largestRect[0].x, 2) + Math.Pow(largestRect[1].y - largestRect[0].y, 2));
                _playArea.playAreaRect[0] = (float)Math.Sqrt(Math.Pow(largestRect[3].x - largestRect[0].x, 2) + Math.Pow(largestRect[3].y - largestRect[0].y, 2));
                _playArea.yaw = (float)((angle - (Mathf.PI / 2.0)) % (Math.PI * 2.0));
            }
        }

        // Run simplification before saving
        Simplify();

        // Convert world points to driver points
        for (int i = 0; i < _playArea.pointCount; i++)
        {
            Vector2 driverPoint = TransformWorldToDriver(_worldPoints[i]);
            _playArea.points[i * 2] = driverPoint.x;
            _playArea.points[i * 2 + 1] = driverPoint.y;
        }

        // Clear any remaining points in the array if the new count is smaller
        for (int i = _playArea.pointCount * 2; i < _playArea.points.Length; i++)
        {
            _playArea.points[i] = 0;
        }
        
        // Save the updated play area to shared memory
        PSVR2SharedMemory.SetPlayArea(_playArea);
        Debug.Log("Chaperone data saved to shared memory.");
    }

    public void ClearMap()
    {
        PSVR2SharedMemory.ClearMap();
        Debug.Log("Map cleared.");
    }

    #region Simplification Algorithms

    /// <summary>
    /// Simplifies a polygon using the Ramer-Douglas-Peucker algorithm.
    /// </summary>
    private List<Vector2> SimplifyRDP(List<Vector2> points, float epsilon)
    {
        if (points.Count < 3) return points;

        // Find the point with the maximum distance
        float dmax = 0;
        int index = 0;
        int end = points.Count - 1;
        for (int i = 1; i < end; i++)
        {
            float d = PerpendicularDistance(points[i], points[0], points[end]);
            if (d > dmax)
            {
                index = i;
                dmax = d;
            }
        }

        // If max distance is greater than epsilon, recursively simplify
        if (dmax > epsilon)
        {
            var recResults1 = SimplifyRDP(points.GetRange(0, index + 1), epsilon);
            var recResults2 = SimplifyRDP(points.GetRange(index, points.Count - index), epsilon);

            // Build the result list
            var result = new List<Vector2>();
            result.AddRange(recResults1.GetRange(0, recResults1.Count - 1));
            result.AddRange(recResults2);
            return result;
        }
        else
        {
            return new List<Vector2> { points[0], points[end] };
        }
    }

    private float PerpendicularDistance(Vector2 pt, Vector2 lineStart, Vector2 lineEnd)
    {
        float dx = lineEnd.x - lineStart.x;
        float dy = lineEnd.y - lineStart.y;
        if (dx == 0 && dy == 0) return Vector2.Distance(pt, lineStart);
        float num = Mathf.Abs(dy * pt.x - dx * pt.y + lineEnd.x * lineStart.y - lineEnd.y * lineStart.x);
        float den = Mathf.Sqrt(dx * dx + dy * dy);
        return num / den;
    }

    /// <summary>
    /// Simplifies a polygon by iteratively removing the vertex that forms the smallest triangle with its neighbors,
    /// until the target vertex count is reached.
    /// </summary>
    private List<Vector2> SimplifyByCount(List<Vector2> points, int maxCount)
    {
        if (points.Count <= maxCount) return points;

        var simplifiedPoints = new List<Vector2>(points);

        while (simplifiedPoints.Count > maxCount)
        {
            float minArea = float.MaxValue;
            int minIndex = -1;

            for (int i = 0; i < simplifiedPoints.Count; i++)
            {
                Vector2 prev = simplifiedPoints[(i + simplifiedPoints.Count - 1) % simplifiedPoints.Count];
                Vector2 curr = simplifiedPoints[i];
                Vector2 next = simplifiedPoints[(i + 1) % simplifiedPoints.Count];
                float area = 0.5f * Mathf.Abs(prev.x * (curr.y - next.y) + curr.x * (next.y - prev.y) + next.x * (prev.y - curr.y));
                if (area < minArea)
                {
                    minArea = area;
                    minIndex = i;
                }
            }
            if (minIndex != -1) simplifiedPoints.RemoveAt(minIndex);
            else break; // Should not happen
        }
        return simplifiedPoints;
    }

    #endregion
}