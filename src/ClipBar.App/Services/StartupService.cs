using System.Diagnostics;
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
    public static string BuildCommand(string executable, string? arguments)
        => string.IsNullOrWhiteSpace(arguments)
            ? $"\"{executable}\""
            : $"\"{executable}\" {arguments.Trim()}";
}
