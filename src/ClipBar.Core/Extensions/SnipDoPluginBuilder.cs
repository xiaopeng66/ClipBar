namespace ClipBar.Core.Extensions;

/// <summary>
/// Generates the SnipDo-side files for the ClipBar integration. Kept free of file I/O
/// (and of WPF) so the generated manifests can be round-tripped through
/// <see cref="SnipDoManifestParser"/> in tests.
/// </summary>
public static class SnipDoPluginBuilder
{
    public const string ExtensionIdentifier = "clipbar.panel";

    public const string PasteActionIdentifier = "clipbar";

    /// <summary>
    /// SnipDo loads paste actions in folder-name order, so "0ClipBar" sorts ahead of the
    /// built-in "0PasteMain" and our action is offered first.
    /// </summary>
    public const string PasteActionFolderName = "0ClipBar";

    /// <summary>Name of the script that summons the panel, referenced by both manifests.</summary>
    public const string LauncherFileName = "main.ps1";

    /// <summary>The extension manifest, parsed by SnipDo for a normal (non-clipboard) action.</summary>
    public static string ExtensionManifest() => """
        {
          "name": "ClipBar",
          "identifier": "clipbar.panel",
          "icon": "logo.svg",
          "icon_colored": "logo_color.svg",
          "monochrome": false,
          "requiredAppVersion": "3.0.0",
          "extensionTranslations": {
            "en": "ClipBar",
            "zh": "ClipBar 剪贴板"
          },
          "actions": [
            {
              "title": "ClipBar History",
              "icon": "logo.svg",
              "icon_colored": "logo_color.svg",
              "powershellFile": "main.ps1",
              "actionTranslations": {
                "en": "Clipboard history",
                "de": "Zwischenablageverlauf",
                "es": "Historial del portapapeles",
                "it": "Cronologia appunti",
                "fr": "Historique du presse-papiers",
                "zh": "剪贴板历史",
                "pt": "Histórico da área de transferência"
              }
            }
          ]
        }
        """;

    /// <summary>
    /// The paste-action manifest. This is the file type that fills SnipDo's clipboard bar, so
    /// it is what actually replaces SnipDo's own 10-item clipboard.
    /// </summary>
    public static string PasteActionManifest() => """
        {
          "identifier_pasteaction": "clipbar",
          "icon": "logo.svg",
          "icon_colored": "logo_color.svg",
          "powershellFile": "main.ps1",
          "actionTranslations": {
            "en": "Clipboard",
            "de": "Zwischenablage",
            "es": "Portapapeles",
            "it": "Appunti",
            "fr": "Presse-papiers",
            "zh": "剪贴板",
            "pt": "Área de transferência"
          }
        }
        """;

    /// <summary>
    /// The action script: wake the running instance through its named event, otherwise start
    /// ClipBar with <c>--show</code>. ASCII-only on purpose, because Windows PowerShell 5.1
    /// reads BOM-less scripts as the ANSI codepage.
    /// </summary>
    public static string LauncherScript(string executablePath)
    {
        var quoted = executablePath.Replace("'", "''", StringComparison.Ordinal);
        return """
            # ClipBar launcher for SnipDo.
            # Summons the running panel through its named event, or starts ClipBar with --show.
            $ErrorActionPreference = 'Stop'
            $eventName = 'Local\ClipBar.Show'
            $exe = 'EXE_PATH'

            try {
                $handle = [System.Threading.EventWaitHandle]::OpenExisting($eventName)
                $handle.Set() | Out-Null
                $handle.Dispose()
                exit 0
            }
            catch {
                # No running instance yet; fall through and start one.
            }

            if (-not (Test-Path -LiteralPath $exe)) {
                exit 1
            }

            Start-Process -FilePath $exe -ArgumentList '--show' -WorkingDirectory (Split-Path -Parent $exe)
            exit 0
            """.Replace("EXE_PATH", quoted, StringComparison.Ordinal);
    }

    /// <summary>Single-colour glyph: a rounded square with three history lines.</summary>
    public static string MonochromeIcon() => """
        <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24">
          <path fill="#000000" d="M6 2h12a4 4 0 0 1 4 4v12a4 4 0 0 1-4 4H6a4 4 0 0 1-4-4V6a4 4 0 0 1 4-4Zm0 2a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2H6Z"/>
          <path fill="#000000" d="M7 7.4h10v1.7H7zM7 11.1h10v1.7H7zM7 14.8h6.2v1.7H7z"/>
        </svg>
        """;

    /// <summary>Coloured variant: the same mark on a filled accent square.</summary>
    public static string ColorIcon() => """
        <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24">
          <rect x="1" y="1" width="22" height="22" rx="5.5" fill="#0F6CBD"/>
          <path fill="#FFFFFF" d="M6.2 7.3h11.6v1.9H6.2zM6.2 11.15h11.6v1.9H6.2zM6.2 15h7.2v1.9H6.2z"/>
        </svg>
        """;
}
