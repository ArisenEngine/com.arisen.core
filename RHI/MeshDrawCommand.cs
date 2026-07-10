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

    public bool IsVisible => Visible != 0;
    public bool IsValid => MeshGuid != Guid.Empty && IsVisible;
}
