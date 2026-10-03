namespace SharpTurns.Core.InstanceManagement;

/// <summary>
/// Outer window-frame bounds expressed in Avalonia's platform screen coordinate
/// space (the units used by PixelPoint and PixelRect). Frame sizes must be
/// converted with DesktopScaling rather than RenderScaling so these coordinates
/// remain consistent across processes and displays.
/// </summary>
public sealed record WindowFrameBounds(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);

    public bool IsValid => Width > 0 && Height > 0;
}

public sealed record LinkedWindowPair(
    WindowFrameBounds Manager,
    WindowFrameBounds App);

/// <summary>
/// Pure geometry rules for the linked manager/SharpTurns pair: the SharpTurns window's left edge sits on the
/// manager's right edge, and both share one top and height.
/// </summary>
public static class LinkedWindowLayout
{
    public static LinkedWindowPair CreateInitial(
        WindowFrameBounds manager,
        WindowFrameBounds app,
        WindowFrameBounds workingArea,
        int gap,
        int minimumAppWidth)
    {
        Validate(manager, nameof(manager));
        Validate(app, nameof(app));
        Validate(workingArea, nameof(workingArea));
        ArgumentOutOfRangeException.ThrowIfNegative(gap);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumAppWidth);

        var height = Math.Min(manager.Height, workingArea.Height);
        var appWidth = Math.Max(minimumAppWidth, app.Width);
        var maximumAppWidth = workingArea.Width - manager.Width - gap;
        if (maximumAppWidth >= minimumAppWidth)
        {
            appWidth = Math.Min(appWidth, maximumAppWidth);
        }

        var pairWidth = manager.Width + gap + appWidth;
        var maximumManagerX = workingArea.Right - pairWidth;
        var managerX = maximumManagerX >= workingArea.X
            ? Math.Clamp(manager.X, workingArea.X, maximumManagerX)
            : workingArea.X;
        var managerY = Math.Clamp(
            manager.Y,
            workingArea.Y,
            Math.Max(workingArea.Y, workingArea.Bottom - height));
        var fittedManager = new WindowFrameBounds(managerX, managerY, manager.Width, height);

        return new LinkedWindowPair(
            fittedManager,
            AnchorApp(fittedManager, app with { Width = appWidth }, gap));
    }

    public static WindowFrameBounds AnchorApp(
        WindowFrameBounds manager,
        WindowFrameBounds app,
        int gap)
    {
        Validate(manager, nameof(manager));
        Validate(app, nameof(app));
        ArgumentOutOfRangeException.ThrowIfNegative(gap);

        return app with
        {
            X = checked(manager.Right + gap),
            Y = manager.Y,
            Height = manager.Height,
        };
    }

    public static WindowFrameBounds AnchorManager(
        WindowFrameBounds manager,
        WindowFrameBounds app,
        int gap)
    {
        Validate(manager, nameof(manager));
        Validate(app, nameof(app));
        ArgumentOutOfRangeException.ThrowIfNegative(gap);

        return manager with
        {
            X = checked(app.X - gap - manager.Width),
            Y = app.Y,
            Height = app.Height,
        };
    }

    public static LinkedWindowPair FitVertically(
        WindowFrameBounds manager,
        WindowFrameBounds app,
        WindowFrameBounds workingArea,
        int gap)
    {
        Validate(manager, nameof(manager));
        Validate(app, nameof(app));
        Validate(workingArea, nameof(workingArea));
        ArgumentOutOfRangeException.ThrowIfNegative(gap);

        var height = Math.Min(manager.Height, workingArea.Height);
        var managerY = Math.Clamp(
            manager.Y,
            workingArea.Y,
            Math.Max(workingArea.Y, workingArea.Bottom - height));
        var fittedManager = manager with { Y = managerY, Height = height };

        return new LinkedWindowPair(
            fittedManager,
            AnchorApp(fittedManager, app, gap));
    }

    private static void Validate(WindowFrameBounds bounds, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(bounds, parameterName);
        if (!bounds.IsValid)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Window bounds must have positive width and height.");
        }
    }
}
