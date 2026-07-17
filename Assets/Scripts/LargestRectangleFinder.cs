using System;
using System.Collections.Generic;
using Clipper2Lib;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Jobs;
using Unity.Burst;

public static class LargestRectangleFinder
{
    [BurstCompile]
    public struct FindLargestRectJob : IJob
    {
        [ReadOnly] public NativeArray<float2> Polygon;
        public float AngleRadians;
        public int GridResolution;
        public float2 StandingPoint;

        public NativeArray<float2> OutputCorners;
        public NativeArray<bool> Success;

        private const int NUM_STEPS = 128;

        public void Execute()
        {
            Success[0] = false;
            if (Polygon.Length < 3) return;

            NativeList<float> tValues = new NativeList<float>(Allocator.Temp);

            // Rotate Polygon to align with axes (simulate the "angle" view)
            NativeArray<float2> rotatedPoly = new NativeArray<float2>(Polygon.Length, Allocator.Temp);
            float cos = math.cos(-AngleRadians);
            float sin = math.sin(-AngleRadians);
            for (int i = 0; i < Polygon.Length; i++)
            {
                float2 p = Polygon[i];
                rotatedPoly[i] = new float2(
                    p.x * cos - p.y * sin,
                    p.x * sin + p.y * cos
                );
            }

            // Rotate standing point to rotated space
            float2 standingPtRotated = new float2(
                StandingPoint.x * cos - StandingPoint.y * sin,
                StandingPoint.x * sin + StandingPoint.y * cos
            );

            // Get Bounding Box of rotated polygon
            float minX = rotatedPoly[0].x;
            float maxX = rotatedPoly[0].x;
            float minY = rotatedPoly[0].y;
            float maxY = rotatedPoly[0].y;
            for (int i = 1; i < rotatedPoly.Length; i++)
            {
                if (rotatedPoly[i].x < minX) minX = rotatedPoly[i].x;
                if (rotatedPoly[i].x > maxX) maxX = rotatedPoly[i].x;
                if (rotatedPoly[i].y < minY) minY = rotatedPoly[i].y;
                if (rotatedPoly[i].y > maxY) maxY = rotatedPoly[i].y;
            }

            float width = maxX - minX;
            float height = maxY - minY;

            if (width <= 0 || height <= 0)
            {
                rotatedPoly.Dispose();
                return;
            }

            float cellW = width / GridResolution;
            float cellH = height / GridResolution;

            // Find grid column and row containing the standing point
            int standingCol = (int)math.floor((standingPtRotated.x - minX) / cellW);
            int standingRow = (int)math.floor((standingPtRotated.y - minY) / cellH);
            bool standingInBounds = standingCol >= 0 && standingCol < GridResolution &&
                                     standingRow >= 0 && standingRow < GridResolution;

            standingCol = math.clamp(standingCol, 0, GridResolution - 1);
            standingRow = math.clamp(standingRow, 0, GridResolution - 1);

            // Fill Grid (0 = outside, 1 = inside)
            // Check if all 4 corners of each cell are on or inside the rotated polygon.
            int numVertices = (GridResolution + 1) * (GridResolution + 1);
            NativeArray<bool> gridVertices = new NativeArray<bool>(numVertices, Allocator.Temp);

            for (int r = 0; r <= GridResolution; r++)
            {
                float y = minY + r * cellH;
                for (int c = 0; c <= GridResolution; c++)
                {
                    float x = minX + c * cellW;
                    float2 pt = new float2(x, y);
                    gridVertices[r * (GridResolution + 1) + c] = PointInPolygon(pt, rotatedPoly);
                }
            }

            NativeArray<int> matrix = new NativeArray<int>(GridResolution * GridResolution, Allocator.Temp);
            int stride = GridResolution + 1;
            for (int r = 0; r < GridResolution; r++)
            {
                int nextRow = r + 1;
                for (int c = 0; c < GridResolution; c++)
                {
                    int nextCol = c + 1;
                    bool tl = gridVertices[r * stride + c];
                    bool tr = gridVertices[r * stride + nextCol];
                    bool bl = gridVertices[nextRow * stride + c];
                    bool br = gridVertices[nextRow * stride + nextCol];

                    if (tl && tr && bl && br)
                    {
                        matrix[r * GridResolution + c] = 1;
                    }
                    else
                    {
                        matrix[r * GridResolution + c] = 0;
                    }
                }
            }

            // Largest Rectangle in Histogram Algorithm
            float maxAreaWithConstraint = 0f;
            float bestLeftWithConstraint = 0f;
            float bestTopWithConstraint = 0f;
            float bestRightWithConstraint = 0f;
            float bestBottomWithConstraint = 0f;

            float maxAreaWithoutConstraint = 0f;
            float bestLeftWithoutConstraint = 0f;
            float bestTopWithoutConstraint = 0f;
            float bestRightWithoutConstraint = 0f;
            float bestBottomWithoutConstraint = 0f;

            NativeArray<int> heights = new NativeArray<int>(GridResolution, Allocator.Temp);
            NativeArray<int> stack = new NativeArray<int>(GridResolution + 1, Allocator.Temp);

            for (int r = 0; r < GridResolution; r++)
            {
                for (int c = 0; c < GridResolution; c++)
                {
                    if (matrix[r * GridResolution + c] == 0)
                        heights[c] = 0;
                    else
                        heights[c]++;
                }

                int stackSize = 0;
                for (int i = 0; i <= GridResolution; i++)
                {
                    int h = (i == GridResolution) ? 0 : heights[i];

                    while (stackSize > 0 && h < heights[stack[stackSize - 1]])
                    {
                        int poppedIndex = stack[--stackSize];
                        int heightVal = heights[poppedIndex];
                        int widthVal = (stackSize == 0) ? i : i - stack[stackSize - 1] - 1;

                        float realW = widthVal * cellW;
                        float realH = heightVal * cellH;
                        float realArea = realW * realH;

                        float topVal = minY + (r - heightVal + 1) * cellH;
                        float leftVal = minX + (i - widthVal) * cellW;
                        float rightVal = leftVal + realW;
                        float bottomVal = topVal + realH;

                        int c_min = i - widthVal;
                        int c_max = i - 1;
                        int r_min = r - heightVal + 1;
                        int r_max = r;

                        if (standingInBounds &&
                            c_min <= standingCol && standingCol <= c_max &&
                            r_min <= standingRow && standingRow <= r_max)
                        {
                            if (realArea > maxAreaWithConstraint)
                            {
                                maxAreaWithConstraint = realArea;
                                bestTopWithConstraint = topVal;
                                bestLeftWithConstraint = leftVal;
                                bestRightWithConstraint = rightVal;
                                bestBottomWithConstraint = bottomVal;
                            }
                        }

                        if (realArea > maxAreaWithoutConstraint)
                        {
                            maxAreaWithoutConstraint = realArea;
                            bestTopWithoutConstraint = topVal;
                            bestLeftWithoutConstraint = leftVal;
                            bestRightWithoutConstraint = rightVal;
                            bestBottomWithoutConstraint = bottomVal;
                        }
                    }
                    stack[stackSize++] = i;
                }
            }

            // Select the best rectangle and apply boundary refinement
            float maxArea = 0f;
            float bestLeft = 0f;
            float bestTop = 0f;
            float bestRight = 0f;
            float bestBottom = 0f;

            if (maxAreaWithConstraint > 0f)
            {
                maxArea = maxAreaWithConstraint;
                bestLeft = bestLeftWithConstraint;
                bestTop = bestTopWithConstraint;
                bestRight = bestRightWithConstraint;
                bestBottom = bestBottomWithConstraint;
            }
            else if (maxAreaWithoutConstraint > 0f)
            {
                maxArea = maxAreaWithoutConstraint;
                bestLeft = bestLeftWithoutConstraint;
                bestTop = bestTopWithoutConstraint;
                bestRight = bestRightWithoutConstraint;
                bestBottom = bestBottomWithoutConstraint;
            }

            if (maxArea > 0f)
            {
                float stepW = 16.0f * cellW / NUM_STEPS;
                float stepH = 16.0f * cellH / NUM_STEPS;

                for (int step = 0; step < NUM_STEPS; step++)
                {
                    if (IsRectInsidePolygon(bestLeft - stepW, bestRight, bestTop, bestBottom, rotatedPoly, tValues))
                    {
                        bestLeft -= stepW;
                    }
                    if (IsRectInsidePolygon(bestLeft, bestRight + stepW, bestTop, bestBottom, rotatedPoly, tValues))
                    {
                        bestRight += stepW;
                    }
                    if (IsRectInsidePolygon(bestLeft, bestRight, bestTop - stepH, bestBottom, rotatedPoly, tValues))
                    {
                        bestTop -= stepH;
                    }
                    if (IsRectInsidePolygon(bestLeft, bestRight, bestTop, bestBottom + stepH, rotatedPoly, tValues))
                    {
                        bestBottom += stepH;
                    }
                }

                Success[0] = true;
                float cosBack = math.cos(AngleRadians);
                float sinBack = math.sin(AngleRadians);

                // 4 Corners: TL, TR, BR, BL
                float2 tl = new float2(bestLeft, bestTop);
                float2 tr = new float2(bestRight, bestTop);
                float2 br = new float2(bestRight, bestBottom);
                float2 bl = new float2(bestLeft, bestBottom);

                OutputCorners[0] = new float2(tl.x * cosBack - tl.y * sinBack, tl.x * sinBack + tl.y * cosBack);
                OutputCorners[1] = new float2(tr.x * cosBack - tr.y * sinBack, tr.x * sinBack + tr.y * cosBack);
                OutputCorners[2] = new float2(br.x * cosBack - br.y * sinBack, br.x * sinBack + br.y * cosBack);
                OutputCorners[3] = new float2(bl.x * cosBack - bl.y * sinBack, bl.x * sinBack + bl.y * cosBack);
            }

            rotatedPoly.Dispose();
            gridVertices.Dispose();
            matrix.Dispose();
            heights.Dispose();
            stack.Dispose();
            tValues.Dispose();
        }

        private bool PointInPolygon(float2 pt, NativeArray<float2> poly)
        {
            int nvert = poly.Length;
            bool inside = false;
            for (int i = 0, j = nvert - 1; i < nvert; j = i++)
            {
                float2 pi = poly[i];
                float2 pj = poly[j];

                // Check if point is on the segment
                float cross = (pt.y - pi.y) * (pj.x - pi.x) - (pt.x - pi.x) * (pj.y - pi.y);
                if (math.abs(cross) < 1e-5f)
                {
                    float minSegX = math.min(pi.x, pj.x);
                    float maxSegX = math.max(pi.x, pj.x);
                    float minSegY = math.min(pi.y, pj.y);
                    float maxSegY = math.max(pi.y, pj.y);
                    if (pt.x >= minSegX && pt.x <= maxSegX && pt.y >= minSegY && pt.y <= maxSegY)
                    {
                        return true;
                    }
                }

                if (((pi.y > pt.y) != (pj.y > pt.y)) &&
                    (pt.x < (pj.x - pi.x) * (pt.y - pi.y) / (pj.y - pi.y + 1e-9f) + pi.x))
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private void InsertionSort(NativeList<float> list)
        {
            int n = list.Length;
            for (int i = 1; i < n; i++)
            {
                float key = list[i];
                int j = i - 1;
                while (j >= 0 && list[j] > key)
                {
                    list[j + 1] = list[j];
                    j--;
                }
                list[j + 1] = key;
            }
        }

        private void GetIntersectionParams(float2 a, float2 b, NativeArray<float2> poly, NativeList<float> tValues)
        {
            tValues.Clear();
            tValues.Add(0f);
            tValues.Add(1f);

            float2 v = b - a;
            float v2 = math.dot(v, v);
            if (v2 < 1e-9f) return;

            int n = poly.Length;
            for (int i = 0; i < n; i++)
            {
                float2 c = poly[i];
                float2 d = poly[(i + 1) % n];

                float2 w = d - c;
                float denom = v.x * w.y - v.y * w.x;

                if (math.abs(denom) < 1e-6f)
                {
                    // Parallel or collinear
                    float cross = (c.x - a.x) * v.y - (c.y - a.y) * v.x;
                    if (math.abs(cross) < 1e-5f)
                    {
                        // Collinear
                        float tC = math.dot(c - a, v) / v2;
                        float tD = math.dot(d - a, v) / v2;

                        if (tC > 0f && tC < 1f) tValues.Add(tC);
                        if (tD > 0f && tD < 1f) tValues.Add(tD);
                    }
                }
                else
                {
                    float t = ((c.x - a.x) * w.y - (c.y - a.y) * w.x) / denom;
                    float u = ((c.x - a.x) * v.y - (c.y - a.y) * v.x) / denom;

                    if (t > 0f && t < 1f && u >= 0f && u <= 1f)
                    {
                        tValues.Add(t);
                    }
                }
            }
        }

        private bool IsSegmentInsidePolygon(float2 a, float2 b, NativeArray<float2> poly, NativeList<float> tValues)
        {
            GetIntersectionParams(a, b, poly, tValues);
            InsertionSort(tValues);

            for (int i = 0; i < tValues.Length - 1; i++)
            {
                float t1 = tValues[i];
                float t2 = tValues[i + 1];
                if (t2 - t1 < 1e-5f) continue; // Skip tiny intervals

                float2 mid = a + (t1 + t2) * 0.5f * (b - a);
                if (!PointInPolygon(mid, poly))
                {
                    return false;
                }
            }
            return true;
        }

        private bool IsRectInsidePolygon(float left, float right, float top, float bottom, NativeArray<float2> poly, NativeList<float> tValues)
        {
            // Check 4 corners
            float2 tl = new float2(left, top);
            float2 tr = new float2(right, top);
            float2 br = new float2(right, bottom);
            float2 bl = new float2(left, bottom);

            if (!PointInPolygon(tl, poly) ||
                !PointInPolygon(tr, poly) ||
                !PointInPolygon(br, poly) ||
                !PointInPolygon(bl, poly))
            {
                return false;
            }

            // Check 4 edges
            if (!IsSegmentInsidePolygon(tl, tr, poly, tValues) ||
                !IsSegmentInsidePolygon(tr, br, poly, tValues) ||
                !IsSegmentInsidePolygon(br, bl, poly, tValues) ||
                !IsSegmentInsidePolygon(bl, tl, poly, tValues))
            {
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Finds the largest inscribed rectangle aligned to a specific angle and containing the standing point.
    /// </summary>
    public static PathD FindLargestRectAtAngle(PathD polygon, double angleRadians, int gridResolution, PointD standingPoint)
    {
        if (polygon == null || polygon.Count < 3) return new PathD();

        NativeArray<float2> polyArray = new NativeArray<float2>(polygon.Count, Allocator.TempJob);
        for (int i = 0; i < polygon.Count; i++)
        {
            polyArray[i] = new float2((float)polygon[i].x, (float)polygon[i].y);
        }

        NativeArray<float2> outCorners = new NativeArray<float2>(4, Allocator.TempJob);
        NativeArray<bool> success = new NativeArray<bool>(1, Allocator.TempJob);

        var job = new FindLargestRectJob
        {
            Polygon = polyArray,
            AngleRadians = (float)angleRadians,
            GridResolution = gridResolution,
            StandingPoint = new float2((float)standingPoint.x, (float)standingPoint.y),
            OutputCorners = outCorners,
            Success = success
        };

        job.Run();

        PathD result = new PathD(4);
        if (success[0])
        {
            for (int i = 0; i < 4; i++)
            {
                result.Add(new PointD(outCorners[i].x, outCorners[i].y));
            }
        }

        polyArray.Dispose();
        outCorners.Dispose();
        success.Dispose();

        return result;
    }
}