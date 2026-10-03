using SharpTurns.Core.InstanceManagement;
using Xunit;

namespace SharpTurns.Tests;

public sealed class LinkedWindowLayoutTests
{
    [Fact]
    public void AnchorApp_AlignsLeftTopAndBottomWhilePreservingWidth()
    {
        var manager = new WindowFrameBounds(100, 80, 360, 900);
        var app = new WindowFrameBounds(900, 200, 1200, 700);

        var result = LinkedWindowLayout.AnchorApp(manager, app, gap: 4);

        Assert.Equal(new WindowFrameBounds(464, 80, 1200, 900), result);
    }

    [Fact]
    public void AnchorManager_FollowsAppMovementAndVerticalResize()
    {
        var manager = new WindowFrameBounds(100, 80, 360, 900);
        var app = new WindowFrameBounds(700, 120, 1100, 780);

        var result = LinkedWindowLayout.AnchorManager(manager, app, gap: 4);

        Assert.Equal(new WindowFrameBounds(336, 120, 360, 780), result);
    }

    [Fact]
    public void CreateInitial_FitsPairWithinWorkingAreaAndKeepsMinimumAppWidth()
    {
        var manager = new WindowFrameBounds(1700, 40, 360, 1000);
        var app = new WindowFrameBounds(100, 100, 1600, 800);
        var workingArea = new WindowFrameBounds(0, 24, 1920, 1056);

        var result = LinkedWindowLayout.CreateInitial(
            manager,
            app,
            workingArea,
            gap: 4,
            minimumAppWidth: 820);

        Assert.Equal(new WindowFrameBounds(0, 40, 360, 1000), result.Manager);
        Assert.Equal(new WindowFrameBounds(364, 40, 1556, 1000), result.App);
        Assert.Equal(workingArea.Right, result.App.Right);
    }

    [Fact]
    public void CreateInitial_ClampsSharedVerticalIntervalToWorkingArea()
    {
        var manager = new WindowFrameBounds(50, -200, 320, 1400);
        var app = new WindowFrameBounds(400, 20, 900, 700);
        var workingArea = new WindowFrameBounds(0, 24, 1600, 900);

        var result = LinkedWindowLayout.CreateInitial(
            manager,
            app,
            workingArea,
            gap: 0,
            minimumAppWidth: 820);

        Assert.Equal(24, result.Manager.Y);
        Assert.Equal(900, result.Manager.Height);
        Assert.Equal(result.Manager.Y, result.App.Y);
        Assert.Equal(result.Manager.Bottom, result.App.Bottom);
    }

    [Fact]
    public void FitVertically_PreservesHorizontalMovementAndFitsActiveDisplay()
    {
        var manager = new WindowFrameBounds(2400, -80, 380, 1400);
        var app = new WindowFrameBounds(2780, -80, 1500, 1400);
        var workingArea = new WindowFrameBounds(1920, 24, 2560, 1300);

        var result = LinkedWindowLayout.FitVertically(manager, app, workingArea, gap: 0);

        Assert.Equal(new WindowFrameBounds(2400, 24, 380, 1300), result.Manager);
        Assert.Equal(new WindowFrameBounds(2780, 24, 1500, 1300), result.App);
    }
}
