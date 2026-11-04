using System.Text.RegularExpressions;
using ClipBar.Core.Models;

namespace ClipBar.Core.Extensions;

/// <summary>Context an action is evaluated against: the text being acted on.</summary>
public sealed class ActionContext
{
    public ActionContext(
        string text,
        IReadOnlyList<string>? filePaths = null,
        string? processName = null,
        Func<string, bool>? fileExists = null)
    {
        Text = text ?? string.Empty;
        FilePaths = filePaths ?? [];
        ProcessName = processName;
        FileExists = fileExists ?? (path => System.IO.File.Exists(path));
    }

    public string Text { get; }

    public IReadOnlyList<string> FilePaths { get; }

    public string? ProcessName { get; }

    public Func<string, bool> FileExists { get; }

    /// <summary>First file path of a file clip, used for FILEPATH requirements.</summary>
    public string? SinglePath => FilePaths.Count > 0 ? FilePaths[0] : null;
}

/// <summary>Decides whether a SnipDo action should be offered for a clip.</summary>
public static class SnipDoActionMatcher
{
    public static bool IsVisible(SnipDoAction action, ActionContext context)
    {
        if (action.Kind == SnipDoActionKind.None)
        {
            return false;
        }

        if (!MatchesAppFilter(action, context.ProcessName))
        {
            return false;
        }

        if (action.Requirements.Count > 0 && !MatchesRequirements(action.Requirements, context))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(action.Regex) && !MatchesRegex(action.Regex, context.Text))
        {
            return false;
        }

        return true;
    }

    public static IReadOnlyList<SnipDoAction> VisibleActions(
        IEnumerable<SnipDoExtension> extensions,
        ActionContext context)
    {
        var result = new List<SnipDoAction>();
        foreach (var extension in extensions)
        {
            foreach (var action in extension.Actions)
            {
                if (IsVisible(action, context))
                {
                    result.Add(action);
                }
            }
        }

        return result;
    }

    private static bool MatchesAppFilter(SnipDoAction action, string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return action.ShowOnlyApps.Count == 0;
        }

        var name = Normalize(processName);

        foreach (var app in action.NoShowApps)
        {
            if (string.Equals(Normalize(app), name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (action.ShowOnlyApps.Count > 0)
        {
            return action.ShowOnlyApps.Any(
                app => string.Equals(Normalize(app), name, StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }

    private static string Normalize(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    private static bool MatchesRequirements(IEnumerable<string> requirements, ActionContext context)
    {
        foreach (var requirement in requirements)
        {
            var ok = requirement.Trim().ToUpperInvariant() switch
            {
                "HAS_URL" => SnipDoText.FindUrl(context.Text) is not null,
                "IS_URL" => SnipDoText.IsUrl(context.Text),
                "EMAIL" => SnipDoText.IsEmail(context.Text),
                "FILEPATH" => HasFilePath(context),
                _ => true,
            };

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasFilePath(ActionContext context)
    {
        if (context.FilePaths.Count > 0)
        {
            return true;
        }

        return SnipDoText.IsFilePath(context.Text, context.FileExists);
    }

    private static bool MatchesRegex(string pattern, string text)
    {
        try
        {
            return Regex.IsMatch(text ?? string.Empty, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
        }
        catch (ArgumentException)
        {
            // A malformed pattern from a community package must not hide every action.
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
