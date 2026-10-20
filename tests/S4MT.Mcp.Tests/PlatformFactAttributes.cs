using Xunit;

namespace S4MT.Mcp.Tests;

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string skipReason = "This test launches a CLI in a Windows job object.")
    {
        if (!OperatingSystem.IsWindows()) Skip = skipReason;
    }
}

internal sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "This test launches a CLI in a Windows job object.";
    }
}

internal sealed class UnsupportedPlatformFactAttribute : FactAttribute
{
    public UnsupportedPlatformFactAttribute()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            Skip = "This test verifies the unsupported-platform boundary outside Windows and Linux.";
    }
}
