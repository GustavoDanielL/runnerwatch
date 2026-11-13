using Avalonia;
using RunnerWatch.Core;

namespace RunnerWatch;

public sealed record Options(bool Demo, string? RecordDir, int RecordSeconds, string? Language)
{
    public static Options Parse(string[] args)
    {
        bool demo = false;
        string? record = null, lang = null;
        int seconds = 45;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--demo": demo = true; break;
                case "--record" when i + 1 < args.Length: record = args[++i]; demo = true; break;
                case "--record-seconds" when i + 1 < args.Length && int.TryParse(args[i + 1], out var s): seconds = s; i++; break;
                case "--lang" when i + 1 < args.Length: lang = args[++i]; break;
            }
        }
        return new Options(demo, record, seconds, lang);
    }
}

public static class Program
{
    public const string Usage = """
        RunnerWatch — GitHub Actions in a tiny always-on-top widget.

          --demo                 fake data, no token or network (great for trying it out / UI work)
          --lang <code>          force a language (en, pt-BR, …); default follows the OS
          --record <dir>         demo + save PNG frames of the widget to <dir> (README GIF)
          --record-seconds <n>   how long to record (default 45)
          --version              print the version
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h")) { Console.WriteLine(Usage); return 0; }
        if (args.Contains("--version")) { Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3)); return 0; }

        App.Options = Options.Parse(args);
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal("Startup failed", ex);
            Log.Flush();
            throw;
        }
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new MacOSPlatformOptions { ShowInDock = false })
        .LogToTrace();
}
