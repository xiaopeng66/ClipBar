using System.IO;
using System.Text.Json;
using ClipBar.Core.Models;
using ClipBar.Core.Security;

namespace ClipBar.Core.Extensions;

public sealed class SnipDoManifestException(string message) : Exception(message);

/// <summary>Reads a SnipDo extension.json manifest into the Core model.</summary>
public static class SnipDoManifestParser
{
    public static SnipDoExtension Parse(string json, string rootPath)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new SnipDoManifestException("manifest is not valid JSON: " + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new SnipDoManifestException("manifest root must be a JSON object");
            }

            var name = GetString(root, "name");
            var identifier = GetString(root, "identifier");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new SnipDoManifestException("manifest is missing \"name\"");
            }

            if (string.IsNullOrWhiteSpace(identifier))
            {
                // Some community packages omit it; derive a stable id from the name. The name is
                // untrusted text, so strip anything that would make the derived id look like a
                // path (slashes, dots-only names) before it reaches the extension store.
                identifier = "imported." + DeriveIdentifierSuffix(name);
            }
            else
            {
                identifier = identifier.Trim();
            }

            var actions = new List<SnipDoAction>();
            if (TryGet(root, "actions", out var actionsElement) && actionsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in actionsElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var title = GetString(element, "title");
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    actions.Add(ParseAction(element, title, rootPath));
                }
            }

            if (actions.Count == 0)
            {
                throw new SnipDoManifestException("manifest has no usable actions");
            }

            var icon = GetString(root, "icon");
            var iconColored = GetString(root, "icon_colored");
            return new SnipDoExtension
            {
                Name = name.Trim(),
                Identifier = identifier.Trim(),
                Icon = icon,
                IconColored = iconColored,
                Monochrome = GetBool(root, "monochrome"),
                IconPath = ResolveFile(rootPath, iconColored) ?? ResolveFile(rootPath, icon),
                RequiredAppVersion = GetString(root, "requiredAppVersion"),
                RequiredOSVersion = GetString(root, "requiredOSVersion"),
                ExtensionTranslations = GetStringMap(root, "extensionTranslations"),
                Actions = actions,
                RootPath = rootPath,
            };
        }
    }

    private static SnipDoAction ParseAction(JsonElement element, string title, string rootPath)
    {
        return new SnipDoAction
        {
            ScriptRoot = rootPath,
            Title = title.Trim(),
            Icon = GetString(element, "icon"),
            IconColored = GetString(element, "icon_colored"),
            Monochrome = GetBool(element, "monochrome"),
            ActionTranslations = GetStringMap(element, "actionTranslations"),
            Url = GetString(element, "url"),
            PowershellFile = GetString(element, "powershellFile"),
            PythonFile = GetString(element, "pythonFile"),
            KeyCombo = GetString(element, "keyCombo"),
            After = GetString(element, "after")?.Trim().ToUpperInvariant(),
            LongRunning = GetBool(element, "longRunning"),
            Regex = GetString(element, "regex"),
            Requirements = GetStringList(element, "requirements"),
            NoShowApps = GetStringList(element, "noShowApps"),
            ShowOnlyApps = GetStringList(element, "showOnlyApps"),
            Options = ParseOptions(element),
        };
    }

    private static List<SnipDoAppOption> ParseOptions(JsonElement action)
    {
        var result = new List<SnipDoAppOption>();
        if (!TryGet(action, "options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var identifier = GetString(option, "identifier");
            if (string.IsNullOrWhiteSpace(identifier))
            {
                continue;
            }

            result.Add(new SnipDoAppOption
            {
                Identifier = identifier.Trim(),
                Values = GetStringMap(option, "values"),
                PowershellFile = GetString(option, "powershellFile"),
            });
        }

        return result;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static bool GetBool(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }

    private static List<string> GetStringList(JsonElement element, string name)
    {
        var result = new List<string>();
        if (!TryGet(element, name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(text.Trim());
                }
            }
        }

        return result;
    }

    private static Dictionary<string, string> GetStringMap(JsonElement element, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryGet(element, name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var text = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result[property.Name] = text;
                }
            }
        }

        return result;
    }

    private static string? ResolveFile(string rootPath, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        // Manifests are third-party data: a reference may be absolute or climb out of the
        // package with "..". Resolve it inside the package or ignore it.
        var candidate = SafePath.TryResolveInside(rootPath, relative, "图标路径");
        return candidate is not null && File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Builds the suffix of a derived identifier from a display name, keeping only characters
    /// that are safe in a folder name. Falls back to a random token when nothing survives.
    /// </summary>
    private static string DeriveIdentifierSuffix(string name)
    {
        var lowered = name.Trim().ToLowerInvariant();
        var builder = new System.Text.StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
            {
                builder.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-', '.', '_');
        return slug.Length == 0 ? Guid.NewGuid().ToString("N")[..8] : slug;
    }
}
