using WinterDesktop.Core.Geometry;
using Xunit;

namespace WinterDesktop.Core.Tests;

public sealed class GeometryTests
{
    [Fact]
    public void RectI_Contains_UsesHalfOpenSemantics()
    {
        var r = RectI.FromSize(10, 20, 100, 50);
        Assert.True(r.Contains(10, 20));
        Assert.True(r.Contains(109, 69));
        Assert.False(r.Contains(110, 20));
        Assert.False(r.Contains(10, 70));
    }

    [Fact]
    public void RectI_AdjacentMonitors_DoNotOverlap()
    {
        var left = RectI.FromSize(0, 0, 1920, 1080);
        var right = RectI.FromSize(1920, 0, 2560, 1440);

        Assert.False(left.IntersectsWith(right));
        Assert.True(left.Intersect(right).IsEmpty);
        Assert.Equal(4480, left.Union(right).Width);
    }

    [Fact]
    public void RectI_NegativeCoordinates_SupportedForLeftMonitor()
    {
        var r = RectI.FromSize(-1920, -200, 1920, 1080);
        Assert.True(r.Contains(-1, 0));
        Assert.Equal(-1920, r.Left);
        Assert.Equal(1920, r.Width);
    }

    [Fact]
    public void RectI_Intersect_ReturnsOverlap()
    {
        var a = RectI.FromSize(0, 0, 100, 100);
        var b = RectI.FromSize(50, 50, 100, 100);
        var i = a.Intersect(b);

        Assert.Equal(50, i.Left);
        Assert.Equal(50, i.Width);
        Assert.Equal(50, i.Height);
    }

    [Fact]
    public void RectF_Lerp_InterpolatesAllEdges()
    {
        var a = RectF.FromSize(0, 0, 100, 100);
        var b = RectF.FromSize(100, 100, 100, 100);
        var mid = RectF.Lerp(a, b, 0.5f);

        Assert.Equal(50, mid.Left, 3);
        Assert.Equal(100, mid.Width, 3);
    }

    [Fact]
    public void GeometryMath_Damp_IsFrameRateIndependent()
    {
        float a = 0;
        for (int i = 0; i < 60; i++) a = GeometryMath.Damp(a, 1f, 4f, 1f / 60f);

        float b = 0;
        for (int i = 0; i < 120; i++) b = GeometryMath.Damp(b, 1f, 4f, 1f / 120f);

        Assert.Equal(a, b, 2);
    }

    [Fact]
    public void GeometryMath_SmoothStep_ClampsOutsideRange()
    {
        Assert.Equal(0f, GeometryMath.SmoothStep(0, 1, -5));
        Assert.Equal(1f, GeometryMath.SmoothStep(0, 1, 5));
        Assert.Equal(0.5f, GeometryMath.SmoothStep(0, 1, 0.5f), 3);
    }
}
