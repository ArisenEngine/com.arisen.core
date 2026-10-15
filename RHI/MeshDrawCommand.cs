using Arisen.Native.RHI;
using System.Numerics;

namespace ArisenEngine.Core.RHI;

/// <summary>
/// A purely blittable, unmanaged structure for a single draw call.
/// This structure is shared between the simulation (ECS) and rendering packages.
/// </summary>
public struct MeshDrawCommand
{
    public Matrix4x4 LocalToWorld;
    public RHIBufferHandle VertexBuffer;
    public RHIBufferHandle IndexBuffer;
    public uint FirstIndex;
    public uint IndexCount;
    public int VertexOffset;
    public EIndexType IndexType;
    public uint MaterialID;

    /// <summary>First palette matrix this draw reads; 0 when <see cref="SkinJointCount"/> is 0.</summary>
    public uint SkinPaletteOffset;

    /// <summary>Joint matrices in the draw's skin palette block; 0 keeps the draw on the static path.</summary>
    public uint SkinJointCount;

    public bool IsSkinned => SkinJointCount > 0;
}

/// <summary>
/// Asset-facing mesh render item extracted from ECS before render setup resolves GPU resources.
/// </summary>
public struct StaticMeshRenderItem
{
    public Matrix4x4 LocalToWorld;
    public Guid MeshGuid;
    public Guid MaterialGuid;
    public int FirstSubmeshIndex;
    public int SubmeshCount;
    public Vector3 BoundsCenter;
    public Vector3 BoundsExtents;
    public byte Visible;

    /// <summary>First local joint rotation of the entity's pose; ignored when <see cref="SkinJointCount"/> is 0.</summary>
    public int SkinJointOffset;

    /// <summary>
    /// Local joint rotations the entity supplies for a skinned mesh. Zero keeps the cooked rest
    /// pose, which still renders through the skinned pipeline.
    /// </summary>
    public int SkinJointCount;

    public bool IsVisible => Visible != 0;
    public bool IsValid => MeshGuid != Guid.Empty && IsVisible;
}
