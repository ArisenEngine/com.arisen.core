namespace ArisenEngine.Core.RHI
{
    /// <summary>
    /// Queue-family sentinel values used with
    /// <see cref="RHICommandBuffer.TransitionImageLayout(RHIImageHandle, EImageLayout, EImageLayout, uint, uint)"/>
    /// for cross-API ownership transfers on shared images.
    /// </summary>
    public static class RHIQueueFamily
    {
        /// <summary>In-family transition; no ownership transfer. Mirrors VK_QUEUE_FAMILY_IGNORED.</summary>
        public const uint Ignored = 0xFFFFFFFFu;

        /// <summary>
        /// External API ownership (e.g. D3D11 accessing a shared Win32 NT handle).
        /// Mirrors VK_QUEUE_FAMILY_EXTERNAL_KHR.
        /// </summary>
        public const uint External = 0xFFFFFFFEu;
    }
}
