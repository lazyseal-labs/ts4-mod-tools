using S4MT.Mcp;
using Xunit;

namespace S4MT.Mcp.Tests;

public sealed class HostOptionsTests
{
    [WindowsFact("Windows dotnetPath validation enforces the executable path used by CreateProcessW.")]
    public void WindowsDotnetPathRequiresExeExtension()
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var options = new HostOptions
        {
            CliPath = Path.Combine(root, "s4mt.dll"),
            DotnetPath = Path.Combine(root, "dotnet"),
            ReadRoots = [root],
            WriteRoots = [root],
            GameRoots = []
        };
        var failure = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Contains("dotnetPath", failure.Message);
    }

    [WindowsFact("Windows dotnetPath validation enforces the executable path used by CreateProcessW.")]
    public void WindowsDotnetPathAcceptsExeExtension()
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        new HostOptions
        {
            CliPath = Path.Combine(root, "s4mt.dll"),
            DotnetPath = Path.Combine(root, "dotnet.exe"),
            ReadRoots = [root],
            WriteRoots = [root],
            GameRoots = []
        }.Validate();
    }
}
