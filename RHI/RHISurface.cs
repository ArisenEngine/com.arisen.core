using Arisen.Native.RHI;

namespace ArisenEngine.Core.RHI;

public class RHISurface
{
    public IntPtr Handle { get; }

    public RHISurface(IntPtr handle)
    {
        Handle = handle;
    }

    private RHISwapChain? m_CachedSwapChain;

    public RHISwapChain GetSwapChain()
    {
        if (m_CachedSwapChain == null || !m_CachedSwapChain.Value.IsValid)
        {
            RHISurfaceAPI.RHISurface_InitSwapChain(Handle);
            var scHandle = RHISurfaceAPI.RHISurface_GetSwapChain(Handle);
            m_CachedSwapChain = new RHISwapChain(scHandle);
        }
        return m_CachedSwapChain.Value;
    }
}