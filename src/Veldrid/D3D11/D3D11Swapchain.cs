using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ColorSpaceType = Vortice.DXGI.ColorSpaceType;

namespace Veldrid.D3D11
{
    internal class D3D11Swapchain : Swapchain
    {
        public override SwapchainDisplayMode EffectiveDisplayMode => effectiveDisplayMode;

        public override PixelFormat? SurfaceColorPixelFormat => D3D11Formats.ToVdFormat(colorFormat);

        public override MonitorDisplayCapabilities? MonitorDisplay => monitorDisplay;

        public override Framebuffer Framebuffer => framebuffer;

        public PresentFlags PresentFlags
        {
            get
            {
                if (AllowTearing && canTear && !SyncToVerticalBlank)
                    return PresentFlags.AllowTearing;

                return PresentFlags.None;
            }
        }

        public IDXGISwapChain DxgiSwapChain { get; private set; }

        public int SyncInterval { get; private set; }

        public override bool IsDisposed => disposed;

        public override string Name
        {
            get
            {
                unsafe
                {
                    byte* pname = stackalloc byte[1024];
                    int size = 1024 - 1;
                    DxgiSwapChain.GetPrivateData(CommonGuid.DebugObjectName, ref size, new IntPtr(pname));
                    pname[size] = 0;
                    return Marshal.PtrToStringAnsi(new IntPtr(pname));
                }
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                    DxgiSwapChain.SetPrivateData(CommonGuid.DebugObjectName, 0, IntPtr.Zero);
                else
                {
                    IntPtr namePtr = Marshal.StringToHGlobalAnsi(value);
                    DxgiSwapChain.SetPrivateData(CommonGuid.DebugObjectName, value.Length, namePtr);
                    Marshal.FreeHGlobal(namePtr);
                }
            }
        }

        public override bool SyncToVerticalBlank
        {
            get => vsync;
            set
            {
                vsync = value;
                SyncInterval = D3D11Util.GetSyncInterval(value);
            }
        }

        public bool AllowTearing
        {
            get => allowTearing;
            set
            {
                if (allowTearing == value)
                    return;

                allowTearing = value;

                if (!canTear)
                    return;

                recreateSwapchain();
            }
        }

        private readonly D3D11GraphicsDevice gd;
        private readonly SwapchainDescription description;
        private readonly PixelFormat? depthFormat;

        private readonly object referencedCLsLock = new object();

        private readonly bool canTear;
        private readonly bool canCreateFrameLatencyWaitableObject;
        private Format colorFormat;
        private bool vsync;
        private D3D11Framebuffer framebuffer;
        private D3D11Texture depthTexture;
        private float pixelScale = 1f;
        private SwapChainFlags flags;
        private bool disposed;
        private FrameLatencyWaitHandle frameLatencyWaitHandle;
        private readonly HashSet<D3D11CommandList> referencedCLs = new HashSet<D3D11CommandList>();

        private bool allowTearing;

        private uint width;
        private uint height;

        private ID3D11Texture2D backBufferTexture;
        private SwapchainDisplayMode effectiveDisplayMode;
        private bool useHighBitDepthColor;
        private MonitorDisplayCapabilities? monitorDisplay;
        private float lastMaxMasteringNits = float.NaN;
        private float lastMinMasteringNits = float.NaN;
        private float lastMaxContentLightNits = float.NaN;
        private float lastMaxFrameAverageNits = float.NaN;

        public D3D11Swapchain(D3D11GraphicsDevice gd, ref SwapchainDescription description)
        {
            this.gd = gd;
            this.description = description;
            depthFormat = description.DepthFormat;
            SyncToVerticalBlank = description.SyncToVerticalBlank;

            effectiveDisplayMode = description.DisplayMode;
            resolveColorFormat();

            // previously we had an extension method ("GetParentOrNull") which makes this read a lot better,
            // but it resulted in AOT compilation crashes on iOS, therefore we can only do this inline.
            using (var dxgiFactory5 = this.gd.Adapter.GetParent(out IDXGIFactory5 f).Success ? f : null)
                canTear = dxgiFactory5?.PresentAllowTearing == true;

            // previously we had an extension method ("GetParentOrNull") which makes this read a lot better,
            // but it resulted in AOT compilation crashes on iOS, therefore we can only do this inline.
            using (var dxgiFactory3 = this.gd.Adapter.GetParent(out IDXGIFactory3 f).Success ? f : null)
                canCreateFrameLatencyWaitableObject = dxgiFactory3 != null;

            width = description.Width;
            height = description.Height;

            tryCreateSwapchain();
        }

        private void resolveColorFormat()
        {
            useHighBitDepthColor = effectiveDisplayMode is SwapchainDisplayMode.Native10BitSdr or SwapchainDisplayMode.Hdr10;

            if (useHighBitDepthColor)
            {
                colorFormat = Format.R10G10B10A2_UNorm;
                return;
            }

            colorFormat = description.ColorSrgb
                ? Format.B8G8R8A8_UNorm_SRgb
                : Format.B8G8R8A8_UNorm;
        }

        private void tryCreateSwapchain()
        {
            try
            {
                recreateSwapchain();
            }
            catch (Exception) when (useHighBitDepthColor)
            {
                effectiveDisplayMode = SwapchainDisplayMode.Standard8Bit;
                useHighBitDepthColor = false;
                resolveColorFormat();
                recreateSwapchain();
            }
        }

        #region Disposal

        public override void Dispose()
        {
            if (!disposed)
            {
                depthTexture?.Dispose();
                framebuffer.Dispose();
                DxgiSwapChain.Dispose();

                disposed = true;
            }
        }

        #endregion

        public override void Resize(uint width, uint height)
        {
            this.width = width;
            this.height = height;

            lock (referencedCLsLock)
            {
                foreach (var cl in referencedCLs) cl.Reset();

                referencedCLs.Clear();
            }

            bool resizeBuffers = false;

            if (framebuffer != null)
            {
                resizeBuffers = true;
                depthTexture?.Dispose();

                backBufferTexture.Dispose();
                framebuffer.Dispose();
            }

            uint actualWidth = (uint)(width * pixelScale);
            uint actualHeight = (uint)(height * pixelScale);
            if (resizeBuffers) DxgiSwapChain.ResizeBuffers(2, (int)actualWidth, (int)actualHeight, colorFormat, flags).CheckError();

            // Get the backbuffer from the swapchain
            backBufferTexture = DxgiSwapChain.GetBuffer<ID3D11Texture2D>(0);

            if (depthFormat != null)
            {
                var depthDesc = new TextureDescription(
                    actualWidth, actualHeight, 1, 1, 1,
                    depthFormat.Value,
                    TextureUsage.DepthStencil,
                    TextureType.Texture2D);
                depthTexture = new D3D11Texture(gd.Device, ref depthDesc);
            }

            var backBufferVdTexture = new D3D11Texture(
                backBufferTexture,
                TextureType.Texture2D,
                D3D11Formats.ToVdFormat(colorFormat));

            var desc = new FramebufferDescription(depthTexture, backBufferVdTexture);
            framebuffer = new D3D11Framebuffer(gd.Device, ref desc)
            {
                Swapchain = this
            };
        }

        public void WaitForNextFrameReady()
        {
            frameLatencyWaitHandle?.WaitOne(1000);
        }

        public void AddCommandListReference(D3D11CommandList cl)
        {
            lock (referencedCLsLock) referencedCLs.Add(cl);
        }

        public void RemoveCommandListReference(D3D11CommandList cl)
        {
            lock (referencedCLsLock) referencedCLs.Remove(cl);
        }

        private void recreateSwapchain()
        {
            lastMaxMasteringNits = float.NaN;
            lastMinMasteringNits = float.NaN;
            lastMaxContentLightNits = float.NaN;
            lastMaxFrameAverageNits = float.NaN;
            DxgiSwapChain?.Release();
            DxgiSwapChain?.Dispose();
            DxgiSwapChain = null;

            framebuffer?.Dispose();
            framebuffer = null;

            depthTexture?.Dispose();
            depthTexture = null;

            frameLatencyWaitHandle?.Dispose();
            frameLatencyWaitHandle = null;

            // FlipDiscard is only supported on DXGI 1.4+
            bool canUseFlipDiscard;

            // previously we had an extension method ("GetParentOrNull") which makes this read a lot better,
            // but it resulted in AOT compilation crashes on iOS, therefore we can only do this inline.
            using (var dxgiFactory4 = gd.Adapter.GetParent(out IDXGIFactory4 f).Success ? f : null)
                canUseFlipDiscard = dxgiFactory4 != null;

            var swapEffect = canUseFlipDiscard ? SwapEffect.FlipDiscard : SwapEffect.Discard;

            flags = SwapChainFlags.None;

            if (AllowTearing && canTear)
                flags |= SwapChainFlags.AllowTearing;
            else if (canCreateFrameLatencyWaitableObject && canUseFlipDiscard)
                flags |= SwapChainFlags.FrameLatencyWaitableObject;

            if (description.Source is Win32SwapchainSource win32Source)
            {
                // Standard stays on the original CreateSwapChain path. Flip-model ForHwnd is only for 10-bit/HDR.
                if (!useHighBitDepthColor)
                {
                    var dxgiScDesc = new SwapChainDescription
                    {
                        BufferCount = 2,
                        Windowed = true,
                        BufferDescription = new ModeDescription((int)width, (int)height, colorFormat),
                        OutputWindow = win32Source.Hwnd,
                        SampleDescription = new SampleDescription(1, 0),
                        SwapEffect = swapEffect,
                        BufferUsage = Usage.RenderTargetOutput,
                        Flags = flags
                    };

                    using (var dxgiFactory = gd.Adapter.GetParent<IDXGIFactory>())
                    {
                        DxgiSwapChain = dxgiFactory.CreateSwapChain(gd.Device, dxgiScDesc);
                        dxgiFactory.MakeWindowAssociation(win32Source.Hwnd, WindowAssociationFlags.IgnoreAltEnter);
                    }
                }
                else
                {
                    int bufferWidth = Math.Max(1, (int)width);
                    int bufferHeight = Math.Max(1, (int)height);

                    using (var dxgiFactory2 = gd.Adapter.GetParent(out IDXGIFactory2 factory2).Success ? factory2 : null)
                    {
                        if (dxgiFactory2 == null)
                            throw new VeldridException("10-bit/HDR swapchain requires DXGI 1.2.");

                        var swapChainDescription = new SwapChainDescription1
                        {
                            AlphaMode = AlphaMode.Ignore,
                            BufferCount = 2,
                            Format = colorFormat,
                            Width = bufferWidth,
                            Height = bufferHeight,
                            SampleDescription = new SampleDescription(1, 0),
                            SwapEffect = swapEffect,
                            BufferUsage = Usage.RenderTargetOutput,
                            Scaling = Scaling.Stretch,
                            Stereo = false,
                            Flags = flags,
                        };

                        DxgiSwapChain = dxgiFactory2.CreateSwapChainForHwnd(gd.Device, win32Source.Hwnd, swapChainDescription);
                        dxgiFactory2.MakeWindowAssociation(win32Source.Hwnd, WindowAssociationFlags.IgnoreAltEnter);
                    }
                }
            }
            else if (description.Source is UwpSwapchainSource uwpSource)
            {
                pixelScale = uwpSource.LogicalDpi / 96.0f;

                // Properties of the swap chain
                var swapChainDescription = new SwapChainDescription1
                {
                    AlphaMode = AlphaMode.Ignore,
                    BufferCount = 2,
                    Format = colorFormat,
                    Height = (int)(height * pixelScale),
                    Width = (int)(width * pixelScale),
                    SampleDescription = new SampleDescription(1, 0),
                    SwapEffect = SwapEffect.FlipSequential,
                    BufferUsage = Usage.RenderTargetOutput,
                    Flags = flags
                };

                // Get the Vortice.DXGI factory automatically created when initializing the Direct3D device.
                using (var dxgiFactory = gd.Adapter.GetParent<IDXGIFactory2>())
                {
                    // Create the swap chain and get the highest version available.
                    using (var swapChain1 = dxgiFactory.CreateSwapChainForComposition(gd.Device, swapChainDescription)) DxgiSwapChain = swapChain1.QueryInterface<IDXGISwapChain2>();
                }

#pragma warning disable CA1416
                var co = new ComObject(uwpSource.SwapChainPanelNative);
#pragma warning restore CA1416

                var swapchainPanelNative = co.QueryInterfaceOrNull<ISwapChainPanelNative>();

                if (swapchainPanelNative != null)
                    swapchainPanelNative.SetSwapChain(DxgiSwapChain);
                else
                {
                    var bgPanelNative = co.QueryInterfaceOrNull<ISwapChainBackgroundPanelNative>();
                    if (bgPanelNative != null) bgPanelNative.SetSwapChain(DxgiSwapChain);
                }
            }

            if ((flags & SwapChainFlags.FrameLatencyWaitableObject) > 0)
            {
                using (var swapChain2 = DxgiSwapChain?.QueryInterfaceOrNull<IDXGISwapChain2>())
                {
                    if (swapChain2 != null)
                    {
                        swapChain2.MaximumFrameLatency = 1;
                        frameLatencyWaitHandle = new FrameLatencyWaitHandle(swapChain2.FrameLatencyWaitableObject);
                    }
                }
            }

            applyDxgiColorSpace();
            updateMonitorDisplay();
            applyHdr10MetadataFromMonitor();

            Resize(width, height);
        }

        private void updateMonitorDisplay()
        {
            monitorDisplay = null;

            if (DxgiSwapChain == null)
                return;

            try
            {
                using var output = DxgiSwapChain.GetContainingOutput();
                using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();

                if (output6 == null)
                    return;

                OutputDescription1 desc = output6.Description1;
                ColorSpaceType cs = desc.ColorSpace;

                bool hdrSignal = cs is ColorSpaceType.RgbFullG2084NoneP2020
                                       or ColorSpaceType.RgbFullG10NoneP709
                                       or ColorSpaceType.RgbStudioG2084NoneP2020;

                monitorDisplay = new MonitorDisplayCapabilities
                {
                    MonitorName = desc.DeviceName.TrimEnd('\0'),
                    BitsPerColor = (byte)Math.Clamp(desc.BitsPerColor, 0, 255),
                    HdrSignal = hdrSignal,
                    MaxLuminance = desc.MaxLuminance,
                    MinLuminance = desc.MinLuminance,
                    MaxFullFrameLuminance = desc.MaxFullFrameLuminance,
                };
            }
            catch
            {
                // Headless / remote desktop may not expose output metadata.
            }
        }

        private void applyDxgiColorSpace()
        {
            if (DxgiSwapChain == null)
                return;

            // Native 10-bit SDR: keep default DXGI colour space until the compositor encodes for the display.
            // Forcing G22/P709 here mis-matches the SDR sRGB-ish framebuffer and can blow contrast on some paths.
            ColorSpaceType? colorSpace = effectiveDisplayMode switch
            {
                SwapchainDisplayMode.Hdr10 => ColorSpaceType.RgbFullG2084NoneP2020,
                _ => null,
            };

            if (colorSpace == null)
                return;

            using var swapChain3 = DxgiSwapChain.QueryInterfaceOrNull<IDXGISwapChain3>();

            if (swapChain3 == null)
                return;

            try
            {
                swapChain3.SetColorSpace1(colorSpace.Value);
            }
            catch
            {
                if (useHighBitDepthColor)
                    throw new VeldridException("Failed to set DXGI color space for high bit-depth swapchain.");
            }
        }

        public override void SetHdr10Metadata(float maxMasteringNits, float minMasteringNits, float maxContentLightNits, float maxFrameAverageNits)
        {
            if (effectiveDisplayMode != SwapchainDisplayMode.Hdr10 || DxgiSwapChain == null)
                return;

            if (lastMaxMasteringNits == maxMasteringNits
                && lastMinMasteringNits == minMasteringNits
                && lastMaxContentLightNits == maxContentLightNits
                && lastMaxFrameAverageNits == maxFrameAverageNits)
                return;

            using var swapChain4 = DxgiSwapChain.QueryInterfaceOrNull<IDXGISwapChain4>();

            if (swapChain4 == null)
                return;

            var meta = new DxgiHdrMetadataHdr10
            {
                // BT.2020 / D65, DXGI units of 0.00002.
                RedPrimaryX = 35400,
                RedPrimaryY = 14600,
                GreenPrimaryX = 8500,
                GreenPrimaryY = 39850,
                BluePrimaryX = 6550,
                BluePrimaryY = 2300,
                WhitePointX = 15635,
                WhitePointY = 16450,
                MaxMasteringLuminance = toMasteringNits(maxMasteringNits),
                MinMasteringLuminance = toMinMastering(minMasteringNits),
                MaxContentLightLevel = toLightLevel(maxContentLightNits),
                MaxFrameAverageLightLevel = toLightLevel(maxFrameAverageNits),
            };

            int size = Marshal.SizeOf<DxgiHdrMetadataHdr10>();
            IntPtr ptr = Marshal.AllocHGlobal(size);

            try
            {
                Marshal.StructureToPtr(meta, ptr, false);
                swapChain4.SetHDRMetaData(HdrMetadataType.Hdr10, size, ptr);
                lastMaxMasteringNits = maxMasteringNits;
                lastMinMasteringNits = minMasteringNits;
                lastMaxContentLightNits = maxContentLightNits;
                lastMaxFrameAverageNits = maxFrameAverageNits;
            }
            catch
            {
                // Remote desktop / drivers without HDR metadata.
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private void applyHdr10MetadataFromMonitor()
        {
            if (effectiveDisplayMode != SwapchainDisplayMode.Hdr10)
                return;

            float peak = monitorDisplay?.MaxLuminance ?? 0;
            float fullFrame = monitorDisplay?.MaxFullFrameLuminance ?? 0;
            float min = monitorDisplay?.MinLuminance ?? 0;
            float paper = fullFrame > 1 ? fullFrame : peak > 1 ? peak : 200f;
            SetHdr10Metadata(peak > 1 ? peak : paper, min, paper, fullFrame > 1 ? fullFrame : paper);
        }

        private static uint toMasteringNits(float nits)
            => (uint)Math.Clamp(MathF.Round(MathF.Max(nits, 1)), 1, 10000);

        private static uint toMinMastering(float nits)
            => (uint)Math.Clamp(MathF.Round(MathF.Max(nits, 0) * 10000f), 0, 10000 * 10000);

        private static ushort toLightLevel(float nits)
            => (ushort)Math.Clamp(MathF.Round(MathF.Max(nits, 1)), 1, 65535);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct DxgiHdrMetadataHdr10
        {
            public ushort RedPrimaryX;
            public ushort RedPrimaryY;
            public ushort GreenPrimaryX;
            public ushort GreenPrimaryY;
            public ushort BluePrimaryX;
            public ushort BluePrimaryY;
            public ushort WhitePointX;
            public ushort WhitePointY;
            public uint MaxMasteringLuminance;
            public uint MinMasteringLuminance;
            public ushort MaxContentLightLevel;
            public ushort MaxFrameAverageLightLevel;
        }

        private class FrameLatencyWaitHandle : WaitHandle
        {
            public FrameLatencyWaitHandle(IntPtr ptr)
            {
                SafeWaitHandle = new SafeWaitHandle(ptr, true);
            }
        }
    }
}
