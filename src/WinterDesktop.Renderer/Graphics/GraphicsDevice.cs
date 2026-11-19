using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinterDesktop.Core.Diagnostics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using DxgiAlphaMode = Vortice.DXGI.AlphaMode;
using Direct3DFeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace WinterDesktop.Renderer.Graphics;

/// <summary>
/// Общий графический контекст: одно D3D11-устройство, один Direct2D-контекст и один
/// DirectComposition-устройство на все мониторы. Это уменьшает потребление памяти и
/// позволяет переиспользовать ресурсы между слоями.
/// </summary>
internal sealed class GraphicsDevice : IDisposable
{
    private GraphicsDevice(
        ID3D11Device d3dDevice,
        IDXGIDevice dxgiDevice,
        IDXGIFactory2 factory,
        ID2D1Factory1 d2dFactory,
        ID2D1Device d2dDevice,
        ID2D1DeviceContext d2dContext,
        ID2D1DeviceContext3? context3,
        IDCompositionDevice composition,
        bool isWarp)
    {
        D3DDevice = d3dDevice;
        DxgiDevice = dxgiDevice;
        Factory = factory;
        D2DFactory = d2dFactory;
        D2DDevice = d2dDevice;
        D2DContext = d2dContext;
        Context3 = context3;
        Composition = composition;
        IsWarp = isWarp;

        D2DContext.AntialiasMode = Vortice.Direct2D1.AntialiasMode.PerPrimitive;
    }

    public ID3D11Device D3DDevice { get; }

    public IDXGIDevice DxgiDevice { get; }

    public IDXGIFactory2 Factory { get; }

    public ID2D1Factory1 D2DFactory { get; }

    public ID2D1Device D2DDevice { get; }

    public ID2D1DeviceContext D2DContext { get; }

    /// <summary>Sprite batch доступен на Windows 10+. При отсутствии используется мягкий fallback.</summary>
    public ID2D1DeviceContext3? Context3 { get; }

    public IDCompositionDevice Composition { get; }

    public bool IsWarp { get; }

    public static GraphicsDevice Create(IAppLog log)
    {
        try
        {
            return CreateWith(DriverType.Hardware, log);
        }
        catch (Exception ex)
        {
            log.Warn($"Аппаратное ускорение недоступно ({ex.Message}); пробую WARP.");

            try
            {
                return CreateWith(DriverType.Warp, log);
            }
            catch (Exception warpEx)
            {
                log.Error("Не удалось создать графическое устройство даже через WARP.", warpEx);
                throw;
            }
        }
    }

    private static GraphicsDevice CreateWith(DriverType driverType, IAppLog log)
    {
        var levels = new[]
        {
            Direct3DFeatureLevel.Level_11_1,
            Direct3DFeatureLevel.Level_11_0,
            Direct3DFeatureLevel.Level_10_1,
            Direct3DFeatureLevel.Level_10_0,
        };

        var d3dDevice = D3D11.D3D11CreateDevice(driverType, DeviceCreationFlags.BgraSupport, levels)
                        ?? throw new InvalidOperationException("D3D11CreateDevice вернул null.");

        var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        var adapter = dxgiDevice.GetAdapter();
        var factory = adapter.GetParent<IDXGIFactory2>();

        var d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        var d2dDevice = d2dFactory.CreateDevice(dxgiDevice);
        var d2dContext = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        ID2D1DeviceContext3? context3 = null;
        try
        {
            context3 = d2dContext.QueryInterface<ID2D1DeviceContext3>();
        }
        catch (Exception ex)
        {
            log.Warn($"SpriteBatch недоступен: {ex.Message}. Будет использован резервный рендер снежинок.");
        }

        var composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);

        log.Info($"Графическое устройство создано: {driverType}, Direct2D 1.1, SpriteBatch={(context3 is not null ? "да" : "нет")}.");

        return new GraphicsDevice(
            d3dDevice,
            dxgiDevice,
            factory,
            d2dFactory,
            d2dDevice,
            d2dContext,
            context3,
            composition,
            driverType == DriverType.Warp);

        // adapter освобождается сборщиком мусора вместе с COM-ссылками Vortice.
    }

    public void Dispose()
    {
        try
        {
            Context3?.Dispose();
            Composition.Dispose();
            D2DContext.Dispose();
            D2DDevice.Dispose();
            D2DFactory.Dispose();
            Factory.Dispose();
            DxgiDevice.Dispose();
            D3DDevice.Dispose();
        }
        catch (Exception)
        {
            // Завершение не должно падать из-за частично освобождённых COM-объектов.
        }
    }
}
