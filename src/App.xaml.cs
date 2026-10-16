using System.IO;
using System.Windows;

namespace BalancePet.UsageAnalytics;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        Resources["WindowCornerRadius"] = Environment.OSVersion.Version.Build >= 22000 ? new CornerRadius(10) : new CornerRadius(4);
        var dataDirectory = ReadArgument(e.Args, "--data-dir")
            ?? UsageEventStore.GetDefaultDirectory();
        var capture = ReadCapture(e.Args);
        MainWindow window;
        try
        {
            window = new MainWindow(dataDirectory, capture);
        }
        catch (System.Windows.Markup.XamlParseException error)
        {
            // A capture run is the only place this is useful, and it is very useful
            // there: the line number of a XAML that will not load is the difference
            // between a minute and an afternoon. It rethrows for every other launch,
            // because a shipped window that cannot draw itself is a crash.
            if (capture is null) throw;
            Console.Error.WriteLine($"xaml: {error.Message}");
            Console.Error.WriteLine($"xaml: line {error.LineNumber} position {error.LinePosition}  uri {error.BaseUri}");
            Console.Error.WriteLine(error.InnerException?.ToString() ?? "xaml: no inner exception");
            Shutdown(2);
            return;
        }
        MainWindow = window;
        window.Show();
    }

    private static string? ReadArgument(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1])) return null;
            return Path.GetFullPath(args[index + 1]);
        }
        return null;
    }

    /// <summary>
    /// The development capture flags, or an empty request. A capture is how a
    /// visual change in this repository gets verified rather than declared: the
    /// window is asked for one named frame, writes it to a PNG and exits, so a
    /// screenshot can say which millisecond of which animation it is.
    ///
    /// <code>
    ///   --capture path.png      write the window to a PNG and exit
    ///   --page usage            show the ledger instead of the dashboard
    ///   --expand N              open the Nth task row
    ///   --at MS                 hold the opening at this millisecond
    ///   --account ID            scope the window to one account (the ruler routes
    ///                           on account type, so both branches need a run)
    ///   --reading-motion MS     hold the reading motion at this millisecond, which
    ///                           is the gesture a watched file changing plays
    ///   --select-account ID     click that account in the account list
    ///   --select-provider NAME  click that client's filter chip
    ///   --expand-quota          click the subscription quota card's fold open
    ///   --quota-at MS           hold the fold at this millisecond
    ///   --archive-limit-step N  click the archive ceiling control N steps up (or
    ///                           down, when N is negative), which is the gesture that
    ///                           writes the plugin's own archive file
    ///   --width N --height N    open the window at that size, for the resize report
    ///   --tip N                 open the Nth task row's 模型 tooltip before the frame
    ///   --probe                 run the timing probe instead of taking a frame
    ///   --probe-file path.txt   duplicate the probe's lines into a file
    /// </code>
    ///
    /// Nothing here changes what is read: the directory still comes from
    /// <c>--data-dir</c> and nothing else.
    /// </summary>
    private static CaptureRequest? ReadCapture(string[] args)
    {
        var path = ReadArgument(args, "--capture");
        var probe = HasFlag(args, "--probe");
        if (path is null && !probe) return null;
        // ReadTextArgument, not ReadArgument: ReadArgument resolves its value as a
        // path, which is right for a directory and wrong for a word. "usage" went
        // through Path.GetFullPath and arrived as
        // C:\...\BalancePet-Ext-Feature-UsageAnalytics\usage, so the comparison
        // below never matched and every --page ran the dashboard. The same trap
        // applies to an account id and a client name.
        var page = string.Equals(ReadTextArgument(args, "--page"), "usage", StringComparison.OrdinalIgnoreCase)
            ? CapturePage.Ledger
            : CapturePage.Dashboard;
        return new CaptureRequest(
            path,
            page,
            ReadNumber(args, "--expand") is { } expand ? (int)expand : null,
            ReadNumber(args, "--at"),
            ReadTextArgument(args, "--account"),
            ReadNumber(args, "--reading-motion"),
            ReadTextArgument(args, "--select-account"),
            ReadTextArgument(args, "--select-provider"),
            HasFlag(args, "--expand-quota"),
            ReadNumber(args, "--quota-at"),
            probe,
            ReadArgument(args, "--probe-file"),
            ReadNumber(args, "--passes") is { } passes ? Math.Max(1, (int)passes) : 3,
            ReadNumber(args, "--archive-limit-step") is { } step ? (int)step : 0,
            ReadNumber(args, "--width"),
            ReadNumber(args, "--height"),
            ReadNumber(args, "--tip") is { } tip ? (int)tip : null,
            ReadNumber(args, "--tip-detail") is { } tipDetail ? (int)tipDetail : null);
    }

    /// <summary>True when a flag is present, for the switches that carry no value.</summary>
    private static bool HasFlag(string[] args, string name)
        => args.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>An argument's value as written, for values that are not paths.</summary>
    private static string? ReadTextArgument(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1])) return null;
            return args[index + 1].Trim();
        }
        return null;
    }

    private static double? ReadNumber(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Length) return null;
            return double.TryParse(args[index + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        return null;
    }
}
