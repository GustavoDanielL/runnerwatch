using System.Runtime.CompilerServices;
using RunnerWatch.Core;


namespace RunnerWatch.Tests;

static class TestSetup
{
    /// <summary>Never let tests write into the developer's real log folder.</summary>
    [ModuleInitializer]
    internal static void RedirectLog() =>
        Log.UseDirectory(Path.Combine(Path.GetTempPath(), "runnerwatch-tests-" + Environment.ProcessId));
}
