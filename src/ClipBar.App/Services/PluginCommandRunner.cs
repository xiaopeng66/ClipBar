using System.IO;
using ClipBar.Core.Extensions;

namespace ClipBar.App.Services;

/// <summary>
/// The headless maintenance switches (<c>--export-snipdo</c>, <c>--install-snipdo</c>) and their
/// exit code.
/// </summary>
/// <remarks>
/// Split out of <c>App.OnStartup</c> so plan §5.3 T-43 ("a failing CLI must not exit 0") is a
/// unit test with an injected exporter rather than a script that has to make the real data
/// directory read-only.
/// </remarks>
public static class PluginCommandRunner
{
    public const string ExportSwitch = "--export-snipdo";
    public const string InstallSwitch = "--install-snipdo";

    /// <summary>True when either maintenance switch is present.</summary>
    public static bool IsRequested(IEnumerable<string> args)
        => args.Any(a =>
            string.Equals(a, ExportSwitch, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, InstallSwitch, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs the requested command and returns the process exit code. <c>0</c> means the bundle
    /// was written (and, for install, copied into SnipDo's store).
    /// </summary>
    public static int Run(
        IEnumerable<string> args,
        Func<SnipDoPluginExporter> exporterFactory,
        Action<string> log,
        Action<string, Exception> logError)
    {
        var install = args.Any(a => string.Equals(a, InstallSwitch, StringComparison.OrdinalIgnoreCase));

        try
        {
            var exporter = exporterFactory();
            var files = exporter.Export();
            log("SnipDo plugin exported: " + files.PackagePath);

            if (install)
            {
                var target = exporter.InstallPasteAction();
                log("SnipDo paste action installed: " + target);
            }

            return 0;
        }
        catch (Exception ex)
        {
            logError("SnipDo plugin command", ex);

            // A scripted call has to be able to tell success from failure; the old code
            // swallowed the error and still exited 0.
            return 1;
        }
    }
}
