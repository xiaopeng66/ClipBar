using ClipBar.Core.Models;

namespace ClipBar.App.Services;

internal sealed record SettingsSaveResult(bool Success, string Message, Exception? Exception = null);

/// <summary>Coordinates registration, live application, side effects, and settings persistence.</summary>
internal sealed class SettingsSaveCoordinator
{
    public SettingsSaveResult Save(
        AppSettings candidate,
        Func<string, (bool Registered, string Error)> registerHotkey,
        Action<AppSettings> makeLive,
        Action<AppSettings> applyTheme,
        Func<AppSettings, bool> applyStartupEntry,
        Action<AppSettings> updateHotkey,
        Action<AppSettings> updateImagePreview,
        Action<AppSettings> applyFloatingSettings,
        Func<bool> persistSettings)
    {
        candidate.Normalize();

        var (registered, hotkeyError) = registerHotkey(candidate.Hotkey);
        if (!registered)
        {
            return new SettingsSaveResult(false, hotkeyError);
        }

        makeLive(candidate);

        try
        {
            applyTheme(candidate);
            if (!applyStartupEntry(candidate))
            {
                return new SettingsSaveResult(
                    false,
                    "无法更新 Windows 启动项；部分设置可能已在本次运行中生效，但未写入设置文件，重启后可能恢复为修改前的值。");
            }

            updateHotkey(candidate);
            updateImagePreview(candidate);
            applyFloatingSettings(candidate);
        }
        catch (Exception ex)
        {
            return new SettingsSaveResult(
                false,
                "部分设置可能已在本次运行中生效；应用过程中出错，设置未写入文件：" + ex.Message + "（重启后可能恢复为修改前的值。）",
                ex);
        }

        if (!persistSettings())
        {
            return new SettingsSaveResult(false, "设置已应用，但无法写入设置文件，重启后会丢失。");
        }

        return new SettingsSaveResult(true, string.Empty);
    }
}
