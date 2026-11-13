using System.Drawing;
using System.Drawing.Imaging;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Box = Vortice.Mathematics.Box;

namespace Autocaster.Windows;

internal sealed class RathuumDesktopDuplication(
    Rectangle desktop, ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication duplication) : IDisposable
{
    private ID3D11Texture2D? _staging;
    private Size _stagingSize;
    private long _lastPresent;

    internal bool Contains(Rectangle client) => desktop.Contains(client);

    internal static RathuumDesktopDuplication? Open(Rectangle client)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            using (adapter)
            {
                for (uint j = 0; adapter.EnumOutputs(j, out var output).Success; j++)
                {
                    using (output)
                    {
                        var description = output.Description;
                        var bounds = description.DesktopCoordinates;
                        var desktop = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                        if (!description.AttachedToDesktop || !desktop.Contains(client) || description.Rotation != ModeRotation.Identity)
                            continue;
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                            [FeatureLevel.Level_11_0], out var device, out _, out var context).CheckError();
                        try
                        {
                            return new(desktop, device!, context!, output1.DuplicateOutput(device!));
                        }
                        catch
                        {
                            context?.Dispose();
                            device?.Dispose();
                            throw;
                        }
                    }
                }
            }
        }
        return null;
    }

    internal bool TryRead(Graphics atlas, Rectangle feed, Rectangle score, out long presented)
    {
        presented = 0;
        Result result = duplication.AcquireNextFrame(0, out var info, out var resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            return false;
        result.CheckError();
        try
        {
            using (resource)
            {
                if (info.LastPresentTime <= _lastPresent || info.ProtectedContentMaskedOut)
                    return false;
                _lastPresent = info.LastPresentTime;
                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                EnsureStaging(new(Math.Max(feed.Width, score.Width), feed.Height + score.Height));
                CopyRegion(texture, feed, 0);
                CopyRegion(texture, score, feed.Height);
                var mapped = context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    using var regions = new Bitmap(_stagingSize.Width, _stagingSize.Height,
                        (int)mapped.RowPitch, PixelFormat.Format32bppRgb, mapped.DataPointer);
                    var feedArea = RathuumHudReader.FeedArea;
                    atlas.DrawImage(regions, new Rectangle(Point.Empty, feedArea.Size),
                        new Rectangle(Point.Empty, feed.Size), GraphicsUnit.Pixel);
                    atlas.DrawImage(regions, new Rectangle(0, feedArea.Height, RathuumHudReader.ScoreWidth, RathuumHudReader.ScoreHeight),
                        new Rectangle(0, feed.Height, score.Width, score.Height), GraphicsUnit.Pixel);
                }
                finally
                {
                    context.Unmap(_staging!, 0);
                }
                presented = info.LastPresentTime;
                return true;
            }
        }
        finally
        {
            duplication.ReleaseFrame();
        }
    }

    private void EnsureStaging(Size size)
    {
        if (_staging is not null && _stagingSize == size)
            return;
        var staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)size.Width, Height = (uint)size.Height, ArraySize = 1, MipLevels = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read
        });
        _staging?.Dispose();
        _staging = staging;
        _stagingSize = size;
    }

    private void CopyRegion(ID3D11Texture2D source, Rectangle region, int y) =>
        context.CopySubresourceRegion(_staging!, 0, 0, (uint)y, 0, source, 0,
            new Box(region.Left - desktop.Left, region.Top - desktop.Top, 0,
                region.Right - desktop.Left, region.Bottom - desktop.Top, 1));

    public void Dispose()
    {
        _staging?.Dispose();
        duplication.Dispose();
        context.Dispose();
        device.Dispose();
    }
}
