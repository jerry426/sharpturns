using SharpTurns.Core.InstanceManagement;
using Xunit;

namespace SharpTurns.Tests;

public sealed class WindowsVisibleFrameTests
{
    [Fact]
    public void ToVisibleFrameBounds_SubtractsInvisibleBorders()
    {
        var windowBounds = new WindowFrameBounds(100, 50, 380, 900);
        var borders = new WindowFrameBorders(8, 0, 8, 8);

        var result = WindowsVisibleFrame.ToVisibleFrameBounds(windowBounds, borders);

        Assert.Equal(new WindowFrameBounds(108, 50, 364, 892), result);
    }

    [Fact]
    public void FromVisibleFrameBounds_AddsInvisibleBorders()
    {
        var visibleBounds = new WindowFrameBounds(460, 40, 1200, 1000);
        var borders = new WindowFrameBorders(8, 0, 8, 8);

        var result = WindowsVisibleFrame.FromVisibleFrameBounds(visibleBounds, borders);

        Assert.Equal(new WindowFrameBounds(452, 40, 1216, 1008), result);
    }

    [Fact]
    public void ToAndFromVisibleFrameBounds_AreInverses()
    {
        var windowBounds = new WindowFrameBounds(17, -23, 900, 700);
        var borders = new WindowFrameBorders(7, 1, 7, 7);

        var visible = WindowsVisibleFrame.ToVisibleFrameBounds(windowBounds, borders);
        var roundTripped = WindowsVisibleFrame.FromVisibleFrameBounds(visible, borders);

        Assert.Equal(windowBounds, roundTripped);
    }

    [Fact]
    public void HandleBasedConversions_AreIdentityWithoutAValidHandle()
    {
        var bounds = new WindowFrameBounds(10, 20, 300, 400);

        Assert.Equal(bounds, WindowsVisibleFrame.ToVisibleFrameBounds(IntPtr.Zero, bounds));
        Assert.Equal(bounds, WindowsVisibleFrame.FromVisibleFrameBounds(IntPtr.Zero, bounds));
        Assert.False(WindowsVisibleFrame.TryGetInvisibleBorders(IntPtr.Zero, out var borders));
        Assert.Equal(WindowFrameBorders.Empty, borders);
    }
}
