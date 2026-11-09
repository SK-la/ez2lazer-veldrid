namespace Veldrid
{
    /// <summary>
    ///     Preferred display encoding for the main swapchain (platform-specific; D3D11 implements 10-bit and HDR color spaces).
    /// </summary>
    public enum SwapchainDisplayMode : byte
    {
        /// <summary>8-bit BGRA swapchain (default).</summary>
        Standard8Bit = 0,

        /// <summary>10-bit per channel Rec.709 (e.g. DXGI R10G10B10A2 + full-range gamma 2.2).</summary>
        Native10BitSdr = 1,

        /// <summary>10-bit HDR10 (BT.2020 / ST2084 container; content tone mapping is the host application's responsibility).</summary>
        Hdr10 = 2,
    }
}
