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
    public uint IndexCount;
    public EIndexType IndexType;
}
