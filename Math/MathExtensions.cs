using System;
using System.Numerics;

namespace ArisenEngine.Core.Math;

/// <summary>
/// Shared helpers for the engine's world and camera basis.
/// </summary>
/// <remarks>
/// The world is right-handed with up = +Y and camera forward = <see cref="Forward"/> (+Z). The render
/// camera turns that basis into a view matrix with <see cref="Matrix4x4.CreateLookAt"/>, whose right axis
/// is forward x up: for an unrotated camera the image's right is world -X, not <see cref="Right"/> (+X).
/// Camera-relative code (look, strafe, sky and atmosphere ray reconstruction) must therefore use
/// <see cref="RightVector(Quaternion)"/> or <see cref="RightVector(Matrix4x4)"/>; <see cref="Right"/> is
/// only the world +X axis and must never be treated as "screen right".
/// </remarks>
public static class MathExtensions
{
    private const float GimbalLockSinPitch = 1.0e-6f;

    public static Vector3 Forward => new Vector3(0, 0, 1);
    public static Vector3 Up => new Vector3(0, 1, 0);
    public static Vector3 Right => new Vector3(1, 0, 0);

    public static Vector3 ForwardVector(this Quaternion rotation)
    {
        return Vector3.Transform(Forward, rotation);
    }

    /// <summary>
    /// Camera-relative right: the world direction that appears on the right of the rendered image.
    /// </summary>
    public static Vector3 RightVector(this Quaternion rotation)
    {
        return Vector3.Cross(Vector3.Transform(Forward, rotation), Vector3.Transform(Up, rotation));
    }

    /// <inheritdoc cref="RightVector(Quaternion)"/>
    public static Vector3 RightVector(this Matrix4x4 rotation)
    {
        return Vector3.Cross(Vector3.Transform(Forward, rotation), Vector3.Transform(Up, rotation));
    }

    /// <summary>
    /// Converts a rotation quaternion into engine euler degrees, returned as (X = pitch, Y = yaw, Z = roll).
    /// </summary>
    /// <remarks>
    /// This is the exact inverse of <see cref="Quaternion.CreateFromYawPitchRoll"/> and of
    /// <see cref="Matrix4x4.CreateFromYawPitchRoll"/>, which compose Rz(roll) * Rx(pitch) * Ry(yaw) over the
    /// +Z forward basis. Callers rebuild a rotation from these angles (the runtime look loop keeps the roll
    /// it read), so a decomposition that is not the exact inverse makes every look step accumulate roll
    /// error until the view flips upside down.
    /// </remarks>
    public static Vector3 QuaternionToEulerDegrees(this Quaternion q)
    {
        // R = Rz(roll) * Rx(pitch) * Ry(yaw) in the row-vector convention used by System.Numerics:
        // M32 = -sin(pitch), (M31, M33) = cos(pitch) * (sin(yaw), cos(yaw)),
        // (M12, M22) = cos(pitch) * (sin(roll), cos(roll)).
        Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(q);
        float sinPitch = Mathf.Clamp(-rotation.M32, -1.0f, 1.0f);
        float pitch = MathF.Asin(sinPitch);
        if (MathF.Abs(sinPitch) >= 1.0f - GimbalLockSinPitch)
        {
            // cos(pitch) == 0: yaw and roll turn the same axis, so the split is not observable.
            return new Vector3(
                pitch * Mathf.Rad2Deg,
                MathF.Atan2(-rotation.M13, rotation.M11) * Mathf.Rad2Deg,
                0.0f);
        }

        return new Vector3(
            pitch * Mathf.Rad2Deg,
            MathF.Atan2(rotation.M31, rotation.M33) * Mathf.Rad2Deg,
            MathF.Atan2(rotation.M12, rotation.M22) * Mathf.Rad2Deg);
    }

    /// <summary>
    /// Wraps an angle in degrees to (-180, 180] so long look sessions keep yaw in the range where the
    /// degree representation keeps its precision.
    /// </summary>
    public static float WrapDegrees(float degrees)
    {
        if (!float.IsFinite(degrees))
        {
            return degrees;
        }

        float wrapped = degrees % 360.0f;
        if (wrapped > 180.0f)
        {
            wrapped -= 360.0f;
        }
        else if (wrapped <= -180.0f)
        {
            wrapped += 360.0f;
        }

        return wrapped;
    }
}
