using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using ClipBar.App.Platform;
using ClipBar.Core.Extensions;
using ClipBar.Core.Models;
using ClipBar.Core.Security;
using WindowsInput;

namespace ClipBar.App.Services;

public sealed record ActionResult(bool Success, string? Output, string? Error);

/// <summary>Executes a SnipDo extension action against the current clip text.</summary>
public sealed class ExtensionActionRunner
{
    private readonly InputSimulator _input = new();
    private readonly Func<IntPtr> _targetWindow;

    public ExtensionActionRunner(Func<IntPtr> targetWindow)
    {
        _targetWindow = targetWindow;
    }

    /// <summary>Runs the action and applies its "after" behaviour.</summary>
    public async Task<ActionResult> RunAsync(
        SnipDoAction action,
        string text,
        IReadOnlyList<string>? filePaths,
        IReadOnlyDictionary<string, string>? appOptions)
    {
        var context = new SnipDoTextContext(text, filePaths ?? [], appOptions);
        try
        {
            var result = action.Kind switch
            {
                SnipDoActionKind.Url => OpenUrl(action, context),
                SnipDoActionKind.PowerShell => await RunPowerShellAsync(action, context).ConfigureAwait(true),
                SnipDoActionKind.Python => await RunPythonAsync(action, context).ConfigureAwait(true),
                SnipDoActionKind.KeyCombo => SendKeys(action, context),
                _ => new ActionResult(false, null, "这个动作没有可执行的类型。"),
            };

            if (result.Success && !string.IsNullOrEmpty(result.Output))
            {
                ApplyAfter(action.After, result.Output);
            }

            return result;
        }
        catch (Exception ex)
        {
            AppLog.Error("Run extension action " + action.Title, ex);
            return new ActionResult(false, null, ex.Message);
        }
    }

    private static ActionResult OpenUrl(SnipDoAction action, SnipDoTextContext context)
    {
        var raw = SnipDoText.Resolve(action.Url ?? string.Empty, context.Text, context.AppOptions);

        // The URL is built from clipboard text by a third-party package, so the scheme decides
        // what actually launches: "file:" runs a local program, "ms-settings:" opens system
        // pages, "javascript:" executes in the browser. Only web and mail schemes pass.
        var url = SafeUrl.Normalize(raw);
        if (url is null)
        {
            return new ActionResult(false, null, "这个扩展想打开不支持的链接类型：" + Truncate(raw, 120));
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return new ActionResult(false, null, "无法打开链接：" + ex.Message);
        }

        return new ActionResult(true, null, null);
    }

    /// <summary>
    /// Kills a script that runs past this. Extensions are third-party code; one that hangs
    /// waiting for input must not pin the app forever.
    /// </summary>
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(30);

    private static async Task<ActionResult> RunPowerShellAsync(SnipDoAction action, SnipDoTextContext context)
    {
        var script = ResolveScript(action.PowershellFile, action, "PowerShell");
        if (script is null)
        {
            return new ActionResult(false, null, "找不到扩展脚本：" + action.PowershellFile);
        }

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        AddContextArguments(startInfo, context);

        return await RunProcessAsync(startInfo, "PowerShell").ConfigureAwait(true);
    }

    private static async Task<ActionResult> RunPythonAsync(SnipDoAction action, SnipDoTextContext context)
    {
        var script = ResolveScript(action.PythonFile, action, "Python");
        if (script is null)
        {
            return new ActionResult(false, null, "找不到扩展脚本：" + action.PythonFile);
        }

        // SnipDo used IronPython; fall back to any python on PATH.
        var startInfo = new ProcessStartInfo("python")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-I");   // isolated: ignore PYTHONPATH/cwd imports planted beside the script
        startInfo.ArgumentList.Add(script);
        AddContextArguments(startInfo, context);

        return await RunProcessAsync(startInfo, "Python").ConfigureAwait(true);
    }

    /// <summary>
    /// The argument set both script hosts receive. Python previously got none of these, so any
    /// pythonFile action silently saw no clip text at all.
    /// </summary>
    private static void AddContextArguments(ProcessStartInfo startInfo, SnipDoTextContext context)
    {
        startInfo.ArgumentList.Add("-PLAIN_TEXT");
        startInfo.ArgumentList.Add(context.Text);
        startInfo.ArgumentList.Add("-URLENCODED_TEXT");
        startInfo.ArgumentList.Add(SnipDoText.EncodeUrl(context.Text));
        startInfo.ArgumentList.Add("-HTMLENCODED_TEXT");
        startInfo.ArgumentList.Add(SnipDoText.EncodeHtml(context.Text));
        foreach (var url in context.Urls)
        {
            startInfo.ArgumentList.Add("-URLS");
            startInfo.ArgumentList.Add(url);
        }

        foreach (var option in context.AppOptions)
        {
            startInfo.ArgumentList.Add("-APPOPTION_" + option.Key);
            startInfo.ArgumentList.Add(option.Value);
        }
    }

    /// <summary>
    /// Runs a child process to completion. Both streams are drained concurrently: reading stdout
    /// to the end before touching stderr deadlocks as soon as the script fills the stderr pipe.
    /// </summary>
    private static async Task<ActionResult> RunProcessAsync(ProcessStartInfo startInfo, string displayName)
    {
        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return new ActionResult(false, null, "找不到 " + displayName + "，无法运行该扩展。");
        }

        if (process is null)
        {
            return new ActionResult(false, null, "无法启动 " + displayName + "。");
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(ScriptTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Already gone.
                }

                return new ActionResult(
                    false,
                    null,
                    $"{displayName} 脚本超过 {ScriptTimeout.TotalSeconds:0} 秒未结束，已中止。");
            }

            var output = (await stdoutTask.ConfigureAwait(true)).Trim();
            var error = (await stderrTask.ConfigureAwait(true)).Trim();
            if (process.ExitCode != 0 || error.Length > 0)
            {
                return new ActionResult(
                    false,
                    output,
                    error.Length > 0 ? error : "脚本以退出码 " + process.ExitCode + " 结束。");
            }

            return new ActionResult(true, output, null);
        }
    }

    private ActionResult SendKeys(SnipDoAction action, SnipDoTextContext context)
    {
        var combo = SnipDoText.Resolve(action.KeyCombo ?? string.Empty, context.Text, context.AppOptions);
        if (!SendKeysParser.TryParse(combo, out var keys))
        {
            return new ActionResult(false, null, "无法解析按键组合：" + combo);
        }

        var target = _targetWindow();
        if (target != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(target);
            Thread.Sleep(60);
        }

        _input.Keyboard.ModifiedKeyStroke(keys.Modifiers, keys.Key);
        return new ActionResult(true, null, null);
    }

    private void ApplyAfter(string? after, string output)
    {
        switch (after)
        {
            case "COPY_RESULT":
                if (!SetClipboardText(output))
                {
                    AppLog.Write("Extension COPY_RESULT: clipboard stayed locked, result not copied");
                }
                break;
            case "PASTE_RESULT":
                // G-08: when the write fails, Ctrl+V would paste whatever the clipboard held
                // *before* — the F-39 hazard class on the extension path. Skip the keystroke.
                if (SetClipboardText(output))
                {
                    PasteIntoPreviousWindow();
                }
                else
                {
                    AppLog.Write("Extension PASTE_RESULT: clipboard stayed locked, keystroke withheld");
                }
                break;
            case "SHOW_RESULT":
            case "SHOW_SUCCESS":
                RunSta(() => FluentMessageWindow.Notify(
                    null,
                    output,
                    Wpf.Ui.Controls.SymbolRegular.Info24,
                    "扩展结果"));
                break;
        }
    }

    private static bool SetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                // OLE clipboard calls require an STA thread; the UI thread already is one,
                // but the runner may also be awaited from a background continuation.
                RunSta(() => Clipboard.SetText(text));
                return true;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ExternalException)
            {
                Thread.Sleep(40);
            }
        }

        return false;
    }

    /// <summary>Runs an OLE/Win32 UI call on an STA thread, hopping threads only when needed.</summary>
    private static void RunSta(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            action();
            return;
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw failure;
        }
    }

    private void PasteIntoPreviousWindow()
    {
        var target = _targetWindow();
        if (target == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetForegroundWindow(target);
        Thread.Sleep(90);
        _input.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_V);
    }

    /// <summary>
    /// Resolves a manifest's script field to a file inside the extension package. A manifest is
    /// downloaded data: without the containment check a package could name
    /// <c>..\..\..\Windows\System32\calc.exe</c> or an absolute path and get it executed.
    /// </summary>
    private static string? ResolveScript(string? relative, SnipDoAction action, string what)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        var root = action.ScriptRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            // No package root known: only accept a plain relative file next to the store, never
            // an absolute path supplied by the manifest.
            if (Path.IsPathRooted(relative))
            {
                AppLog.Write($"{what} 脚本路径被拒绝（没有扩展根目录，且使用了绝对路径）：{relative}");
                return null;
            }

            var local = SafePath.TryResolveInside(
                Path.GetDirectoryName(Path.GetFullPath(relative)) ?? string.Empty,
                Path.GetFileName(relative),
                what + " 脚本");
            return local is not null && File.Exists(local) ? local : null;
        }

        var candidate = SafePath.TryResolveInside(root, relative, what + " 脚本");
        if (candidate is null)
        {
            AppLog.Write($"{what} 脚本路径被拒绝（越出扩展目录）：{relative}");
            return null;
        }

        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Shortens untrusted text for an error message.</summary>
    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max] + "…";
    }
}

/// <summary>Values a SnipDo action template or script can reference.</summary>
public sealed class SnipDoTextContext(
    string text,
    IReadOnlyList<string> filePaths,
    IReadOnlyDictionary<string, string>? appOptions)
{
    public string Text { get; } = text;

    public IReadOnlyList<string> FilePaths { get; } = filePaths;

    public IReadOnlyDictionary<string, string> AppOptions { get; } = appOptions ?? new Dictionary<string, string>();

    public IReadOnlyList<string> Urls { get; } = ExtractUrls(text);

    private static List<string> ExtractUrls(string text)
    {
        var result = new List<string>();
        var match = SnipDoText.FindUrl(text);
        if (match is not null)
        {
            result.Add(match);
        }

        return result;
    }
}

/// <summary>Parses the SendKeys subset SnipDo extensions use, e.g. "^v" or "%{TAB}".</summary>
public static class SendKeysParser
{
    public static bool TryParse(string combo, out KeyStroke stroke)
    {
        stroke = default;
        if (string.IsNullOrEmpty(combo))
        {
            return false;
        }

        var modifiers = new List<VirtualKeyCode>();
        var index = 0;

        while (index < combo.Length)
        {
            switch (combo[index])
            {
                case '^':
                    modifiers.Add(VirtualKeyCode.CONTROL);
                    index++;
                    continue;
                case '+':
                    modifiers.Add(VirtualKeyCode.SHIFT);
                    index++;
                    continue;
                case '%':
                    modifiers.Add(VirtualKeyCode.MENU);
                    index++;
                    continue;
            }

            break;
        }

        var keyPart = combo[index..].Trim();
        if (keyPart.Length == 0)
        {
            return false;
        }

        VirtualKeyCode? key = null;
        if (keyPart.StartsWith('{') && keyPart.EndsWith('}'))
        {
            key = FromName(keyPart[1..^1]);
        }
        else if (keyPart.Length == 1)
        {
            key = FromCharacter(keyPart[0]);
        }

        if (key is null)
        {
            return false;
        }

        stroke = new KeyStroke(modifiers.ToArray(), key.Value);
        return true;
    }

    private static VirtualKeyCode? FromName(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper switch
        {
            "TAB" => VirtualKeyCode.TAB,
            "ENTER" or "RETURN" => VirtualKeyCode.RETURN,
            "ESC" or "ESCAPE" => VirtualKeyCode.ESCAPE,
            "BACKSPACE" or "BS" => VirtualKeyCode.BACK,
            "DEL" or "DELETE" => VirtualKeyCode.DELETE,
            "HOME" => VirtualKeyCode.HOME,
            "END" => VirtualKeyCode.END,
            "UP" => VirtualKeyCode.UP,
            "DOWN" => VirtualKeyCode.DOWN,
            "LEFT" => VirtualKeyCode.LEFT,
            "RIGHT" => VirtualKeyCode.RIGHT,
            "SPACE" => VirtualKeyCode.SPACE,
            "F1" => VirtualKeyCode.F1,
            "F2" => VirtualKeyCode.F2,
            "F3" => VirtualKeyCode.F3,
            "F4" => VirtualKeyCode.F4,
            "F5" => VirtualKeyCode.F5,
            "F6" => VirtualKeyCode.F6,
            "F7" => VirtualKeyCode.F7,
            "F8" => VirtualKeyCode.F8,
            "F9" => VirtualKeyCode.F9,
            "F10" => VirtualKeyCode.F10,
            "F11" => VirtualKeyCode.F11,
            "F12" => VirtualKeyCode.F12,
            _ => null,
        };
    }

    private static VirtualKeyCode? FromCharacter(char c)
    {
        var upper = char.ToUpperInvariant(c);
        if (upper is >= 'A' and <= 'Z')
        {
            return (VirtualKeyCode)Enum.Parse(typeof(VirtualKeyCode), "VK_" + upper);
        }

        if (upper is >= '0' and <= '9')
        {
            return (VirtualKeyCode)Enum.Parse(typeof(VirtualKeyCode), "VK_" + upper);
        }

        return null;
    }
}

public readonly record struct KeyStroke(IReadOnlyList<VirtualKeyCode> Modifiers, VirtualKeyCode Key);
