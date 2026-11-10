using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace ClipBar.App.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipBar";

    /// <summary>The Run value written by the pre-rename build; removed when ClipBar rewrites
    /// the entry (rename round, docs/RENAME-PLAN.md).</summary>
    private const string LegacyValueName = "Clipboard11";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool SetEnabled(bool enabled, string? arguments = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var executable = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executable))
                {
                    return false;
                }

                key.SetValue(ValueName, BuildCommand(executable, arguments));
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Set startup", ex);
            return false;
        }
    }

    /// <summary>
    /// Builds the Run-entry command line. A custom data directory must survive the reboot:
    /// the entry used to name the bare executable, so a <c>--data-dir</c> profile silently
    /// started against the default store after a reboot (U-01).
    /// </summary>
    public static string BuildDataDirectoryArguments(string directory)
        => "--data-dir " + QuoteWindowsArgument(directory);

    private static string QuoteWindowsArgument(string value)
    {
        var result = new StringBuilder(value.Length + 2);
        result.Append('"');

        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            result.Append(character);
            backslashes = 0;
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    public static string BuildCommand(string executable, string? arguments)
        => string.IsNullOrWhiteSpace(arguments)
            ? $"\"{executable}\""
            : $"\"{executable}\" {arguments.Trim()}";
}
