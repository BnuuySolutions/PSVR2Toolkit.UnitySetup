using UnityEngine;
using System.Collections.Generic;
using System; // For Math.Abs

/// <summary>
/// C# port of distortion.cpp.
/// This static class generates distortion-correction meshes for the PSVR2 cameras.
/// </summary>
public static class PSVR2Distortion
{
    /// <summary>
    /// The radius of the passthrough "bubble" in meters.
    /// </summary>
    public const float BUBBLE_RADIUS = 4.0f;

    /// <summary>
    /// Creates a simple, spherical bubble mesh. Used when distortion is disabled.
    /// This is a C# port of create_default_mesh, modified for 3D.
    /// </summary>
    /// <param name="maxFovAngle">The horizontal and vertical Field of View of the bubble, in degrees.</param>
    /// <param name="meshDensityX">Number of horizontal subdivisions</param>
    /// <param name="meshDensityY">Number of vertical subdivisions</param>
    /// <returns>A spherical segment mesh.</returns>
    public static Mesh CreateDefaultMesh(float maxFovAngle = 65.0f, int meshDensityX = 256, int meshDensityY = 256)
    {
        Mesh mesh = new Mesh
        {
            name = "PSVR2_DefaultBubble"
        };

        List<Vector3> vertices = new List<Vector3>();
        List<Vector2> uvs = new List<Vector2>();
        List<int> triangles = new List<int>();

        // This scale controls the default FOV of the bubble when distortion is off.
        float maxAngleRad = maxFovAngle * Mathf.Deg2Rad;

        // Generate vertices and UVs
        for (int j = 0; j <= meshDensityY; ++j)
        {
            for (int i = 0; i <= meshDensityX; ++i)
            {
                float uOut = (float)i / meshDensityX; // 0 to 1
                float vOut = (float)j / meshDensityY; // 0 to 1

                // Map grid (0,0)->(1,1) to angles (-maxAngle, -maxAngle)->(maxAngle, maxAngle)
                // Y is flipped to match original clip space (0,0 is top-left)
                float theta = (uOut * 2.0f - 1.0f) * maxAngleRad;
                float phi = ((1.0f - vOut) * 2.0f - 1.0f) * maxAngleRad;

                // Convert spherical angles to a normalized ray direction
                // This creates an evenly-spaced angular grid
                float x = Mathf.Sin(theta) * Mathf.Cos(phi);
                float y = Mathf.Sin(phi);
                float z = Mathf.Cos(theta) * Mathf.Cos(phi);
                Vector3 rayDir = new Vector3(x, y, z).normalized;

                // Place vertex on the sphere
                vertices.Add(rayDir * BUBBLE_RADIUS);

                // UVs map directly to the grid (0,0 top-left, 1,1 bottom-right)
                uvs.Add(new Vector2(uOut, vOut));
            }
        }

        // Generate triangles
        for (int j = 0; j < meshDensityY; ++j)
        {
            for (int i = 0; i < meshDensityX; ++i)
            {
                int rowWidth = meshDensityX + 1;
                int topLeft = j * rowWidth + i;
                int topRight = topLeft + 1;
                int bottomLeft = (j + 1) * rowWidth + i;
                int bottomRight = bottomLeft + 1;

                // First triangle
                triangles.Add(topLeft);
                triangles.Add(topRight);
                triangles.Add(bottomLeft);

                // Second triangle
                triangles.Add(bottomLeft);
                triangles.Add(topRight);
                triangles.Add(bottomRight);
            }
        }

        // Create and assign to mesh
        mesh.vertices = vertices.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();

        return mesh;
    }

    /// <summary>
    /// C# port of the get_distorted_point function from distortion.cpp.
    /// Calculates the distorted texture coordinate for a given normalized camera point.
    /// </summary>
    /// <param name="x">Normalized x-coordinate</param>
    /// <param name="y">Normalized y-coordinate</param>
    /// <param name="params">CameraParameters struct containing distortion coefficients</param>
    /// <returns>A Vector2 representing the distorted UV coordinate</returns>
    private static Vector2 GetDistortedPoint(double x, double y, CameraParameters parameters)
    {
        // This is a direct port of the math from distortion.cpp
        double[] p = parameters.coeffs;

        double xSq = x * x;
        double ySq = y * y;
        double rSq = xSq + ySq;
        double twoXY = 2.0 * x * y;
        double rSqP4 = rSq * rSq * rSq * rSq;
        double rSqP5 = rSqP4 * rSq;

        double numPoly = 1.0 +
            (((p[4] * rSq + p[1]) * rSq + p[0]) * rSq) +
            (p[16] * rSqP5 * rSq) +
            (p[15] * rSqP5) +
            (p[14] * rSqP4);

        double denPoly = 1.0 +
            (((p[7] * rSq + p[6]) * rSq + p[5]) * rSq) +
            (p[19] * rSqP5 * rSq) +
            (p[18] * rSqP5) +
            (p[17] * rSqP4);

        double radialScale = (Math.Abs(denPoly) > 1e-9) ? (numPoly / denPoly) : 1.0;

        double distortedXTerm = (((p[9] * rSq + p[8]) * rSq)) +
                                 ((xSq + xSq + rSq) * p[3]) +
                                 (radialScale * x) +
                             (twoXY * p[2]);

        double distortedYTerm = (((p[11] * rSq + p[10]) * rSq)) +
                                 (twoXY * p[3]) +
                                 ((ySq + ySq + rSq) * p[2]) +
                                 (radialScale * y);

        // Replicate the DirectXMath matrix operations using Unity's math types.
        // C++ XMMatrixRotationAxis takes radians. Unity Quaternion.Euler/AngleAxis takes degrees.
        Quaternion rx = Quaternion.AngleAxis((float)p[12] * Mathf.Rad2Deg, Vector3.right);
        Quaternion ry = Quaternion.AngleAxis((float)p[13] * Mathf.Rad2Deg, Vector3.up);

        // Unity matrix multiplication order is reverse of DirectX
        // R = XMMatrixMultiply(Rx, Ry) becomes R = Ry * Rx
        Matrix4x4 r = Matrix4x4.Rotate(ry) * Matrix4x4.Rotate(rx);

        Vector3 pIn = new Vector3((float)distortedXTerm, (float)distortedYTerm, 1.0f);

        // C++ XMVector3Transform(pIn, R) is equivalent to Unity's MultiplyPoint3x4
        Vector3 pOut = r.MultiplyPoint3x4(pIn);

        float w = pOut.z;
        if (Math.Abs(w) < 1e-9)
        {
            return new Vector2(-10.0f, -10.0f); // Return out-of-bounds
        }

        return new Vector2(pOut.x / w, pOut.y / w);
    }

    /// <summary>
    /// Numerically solves for the *ideal* normalized point that would result in the
    /// given *distorted* normalized point. This is the inverse of GetDistortedPoint.
    /// </summary>
    /// <param name="targetXNorm">The target distorted x-coordinate (normalized)</param>
    /// <param name="targetYNorm">The target distorted y-coordinate (normalized)</param>
    /// <param name="parameters">Camera distortion coefficients</param>
    /// <param name="iterations">Number of solver iterations</param>
    /// <returns>The *ideal* normalized (x, y) point</returns>
    private static Vector2 GetIdealPoint(double targetXNorm, double targetYNorm, CameraParameters parameters, int iterations = 1024)
    {
        // Start by guessing the ideal point is the same as the distorted point.
        // This is a good approximation for the center of the image.
        double guessX = targetXNorm;
        double guessY = targetYNorm;
        double step = 0.5; // Step size for gradient descent

        for (int i = 0; i < iterations; i++)
        {
            // See what distorted point our current ideal guess produces
            Vector2 currentDistorted = GetDistortedPoint(guessX, guessY, parameters);

            // Calculate the error (how far we are from our target)
            double errX = targetXNorm - currentDistorted.x;
            double errY = targetYNorm - currentDistorted.y;

            // Stop if we are close enough
            if (Math.Abs(errX) < 1e-6 && Math.Abs(errY) < 1e-6)
            {
                break;
            }

            // Adjust our guess in the direction of the error.
            // This is a simple gradient descent.
            guessX += errX * step;
            guessY += errY * step;
        }
        return new Vector2((float)guessX, (float)guessY);
    }

    /// <summary>
    /// C# port of create_undistortion_mesh.
    /// Generates a tessellated mesh with UVs pre-distorted to correct for lens distortion.
    /// This version iterates over the distorted texture (0,0 to 1,1) and finds
    /// the corresponding ideal 3D vertex for each point.
    /// </summary>
    /// <param name="imageWidth">Width of the camera texture</param>
    /// <param name="imageHeight">Height of the camera texture</param>
    /// <param name="intrinsics">Camera intrinsics (fx, fy, cx, cy)</param>
    /// <param name="parameters">Camera distortion coefficients</param>
    /// <param name="meshDensityX">Number of horizontal subdivisions</param>
    /// <param name="meshDensityY">Number of vertical subdivisions</param>
    /// <returns>A new Mesh configured for distortion correction</returns>
    public static Mesh CreateUndistortionMesh(
        int imageWidth, int imageHeight,
        CameraIntrinsics intrinsics,
        CameraParameters parameters,
        int meshDensityX = 128,
        int meshDensityY = 128)
    {
        List<Vector3> vertices = new List<Vector3>();
        List<Vector2> uvs = new List<Vector2>();
        List<int> triangles = new List<int>();

        // Generate vertices and UVs
        for (int j = 0; j <= meshDensityY; ++j)
        {
            for (int i = 0; i <= meshDensityX; ++i)
            {
                // 1. Iterate over the distorted texture UV grid (0,0 to 1,1)
                float u = (float)i / meshDensityX; // 0 to 1
                float v = (float)j / meshDensityY; // 0 to 1

                // The mesh's UV is this grid point.
                uvs.Add(new Vector2(u, v));

                // 2. Convert this UV to a *normalized distorted coordinate*.
                // We use the ideal intrinsics to define the space, as per the original C++
                double px_distorted = u * imageWidth;
                double py_distorted = v * imageHeight;
                double xNorm_distorted = (px_distorted - intrinsics.cx) / intrinsics.fx;
                double yNorm_distorted = (py_distorted - intrinsics.cy) / intrinsics.fy;

                // 3. Solve for the *ideal normalized coordinate*
                Vector2 idealNorm = GetIdealPoint(xNorm_distorted, yNorm_distorted, parameters);

                // 4. Convert the ideal normalized coordinate to a 3D ray and project to the bubble
                Vector3 rayDir = new Vector3(idealNorm.x, idealNorm.y, 1.0f).normalized;
                Vector3 pos = rayDir * BUBBLE_RADIUS;

                // Flip Y coordinate for Unity's coordinate system vs. sensor coordinates
                // (sensor 0,0 is top-left, Unity Y+ is up)
                pos.y = -pos.y;

                vertices.Add(pos);
            }
        }

        // Generate triangles
        for (int j = 0; j < meshDensityY; ++j)
        {
            for (int i = 0; i < meshDensityX; ++i)
            {
                int rowWidth = meshDensityX + 1;
                int topLeft = j * rowWidth + i;
                int topRight = topLeft + 1;
                int bottomLeft = (j + 1) * rowWidth + i;
                int bottomRight = bottomLeft + 1;

                // First triangle
                triangles.Add(topLeft);
                triangles.Add(topRight);
                triangles.Add(bottomLeft);

                // Second triangle
                triangles.Add(bottomLeft);
                triangles.Add(topRight);
                triangles.Add(bottomRight);
            }
        }

        // Create and assign to mesh
        Mesh mesh = new Mesh
        {
            name = "PSVR2_UndistortMesh",
            vertices = vertices.ToArray(),
            uv = uvs.ToArray(),
            triangles = triangles.ToArray()
        };
        
        mesh.RecalculateNormals();

        return mesh;
    }
}