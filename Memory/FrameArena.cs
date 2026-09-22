using System.Runtime.InteropServices;

namespace ArisenEngine.Core.Memory;

/// <summary>
/// A high-performance linear allocator for data that only needs to live for a single frame.
/// Ownership and reclamation belong to <see cref="MemoryManager"/>: the runtime instance is
/// created when the core package loads and reclaimed at the kernel frame boundary after every
/// subsystem has ticked. An allocation is therefore valid for the frame that produced it and must
/// not be handed to code that runs after that frame ends.
/// </summary>
public unsafe sealed class FrameArena
{
    private byte* m_Buffer;
    private readonly nuint m_Capacity;
    private nuint m_Offset;

    public FrameArena(uint capacityInMB)
    {
        m_Capacity = (nuint)capacityInMB * 1024 * 1024;
        m_Buffer = (byte*)NativeMemory.Alloc(m_Capacity);
        m_Offset = 0;
    }

    /// <summary>
    /// Bytes handed out since the last <see cref="Reset"/>. The arena is a per-frame ceiling
    /// rather than a growth buffer: an allocation that would exceed <see cref="CapacityBytes"/>
    /// fails immediately instead of extending the arena across frames.
    /// </summary>
    public nuint UsedBytes => m_Offset;

    public nuint CapacityBytes => m_Capacity;

    /// <summary>
    /// Allocates unmanaged memory from the arena.
    /// </summary>
    public Span<T> Alloc<T>(int count) where T : unmanaged
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Frame arena allocation count must be non-negative.");
        }

        nuint size = (nuint)count * (nuint)sizeof(T);

        // Alignment (assume 16-byte alignment for common data)
        nuint alignment = 16;
        nuint currentPtr = (nuint)m_Buffer + m_Offset;
        nuint alignedPtr = (currentPtr + (alignment - 1)) & ~(alignment - 1);
        nuint newOffset = alignedPtr - (nuint)m_Buffer + size;

        if (newOffset > m_Capacity)
        {
            throw new OutOfMemoryException(
                $"Frame arena exhausted: {typeof(T).Name} requested {size} bytes and only " +
                $"{m_Capacity - m_Offset} of {m_Capacity} bytes remain for this frame.");
        }

        m_Offset = newOffset;
        return new Span<T>((void*)alignedPtr, count);
    }

    /// <summary>
    /// Reclaims every allocation. <see cref="MemoryManager"/> calls it once per frame from
    /// <c>EngineKernel.OnFrameEnd</c>; callers must not reset an arena that is still owned by
    /// another consumer.
    /// </summary>
    public void Reset()
    {
        m_Offset = 0;
    }

    public void Dispose()
    {
        if (m_Buffer != null)
        {
            NativeMemory.Free(m_Buffer);
            m_Buffer = null;
        }
    }
}