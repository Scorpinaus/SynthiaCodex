using System.Text.Json.Nodes;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.Infrastructure.Codex.Codecs;

internal sealed class SkillsCodexCodec
{
    public CodexRpcCall EncodeList(CodexSkillListRequest request)
    {
        if (request.Cwds.Count == 0)
        {
            throw new ArgumentException("At least one working directory is required.", nameof(request));
        }

        var cwds = request.Cwds
            .Select(path =>
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new ArgumentException("Skill working directories cannot be empty.", nameof(request));
                }

                return Path.GetFullPath(path);
            })
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();

        return new CodexRpcCall(
            "skills/list",
            new JsonObject
            {
                ["cwds"] = new JsonArray(cwds.Select(path => JsonValue.Create(path)).ToArray()),
                ["forceReload"] = request.ForceReload
            });
    }

    public CodexSkillListResult DecodeList(JsonNode? response)
    {
        var contexts = new List<CodexSkillContextResult>();
        if (response?["data"] is not JsonArray data)
        {
            return new CodexSkillListResult(contexts);
        }

        foreach (var entry in data.OfType<JsonObject>())
        {
            var cwd = ReadString(entry, "cwd") ?? string.Empty;
            var errors = ParseSkillErrors(entry["errors"] as JsonArray);
            var skills = new List<CodexSkillMetadata>();
            if (entry["skills"] is JsonArray skillValues)
            {
                foreach (var skillValue in skillValues.OfType<JsonObject>())
                {
                    var parsed = ParseSkill(skillValue);
                    if (parsed is not null)
                    {
                        skills.Add(parsed);
                        continue;
                    }

                    errors.Add(new CodexSkillLoadError(
                        ReadString(skillValue, "path") ?? cwd,
                        "Codex returned incomplete skill metadata."));
                }
            }

            contexts.Add(new CodexSkillContextResult(cwd, skills, errors));
        }

        return new CodexSkillListResult(contexts);
    }

    public CodexRpcCall EncodeConfigWrite(CodexSkillConfigWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path) || !Path.IsPathRooted(request.Path))
        {
            throw new ArgumentException("An absolute SKILL.md path is required.", nameof(request));
        }

        return new CodexRpcCall(
            "skills/config/write",
            new JsonObject
            {
                ["path"] = Path.GetFullPath(request.Path),
                ["enabled"] = request.Enabled
            });
    }

    public CodexSkillConfigWriteResult DecodeConfigWrite(JsonNode? response)
    {
        var effectiveEnabled = ReadBool(response as JsonObject, "effectiveEnabled")
            ?? throw new CodexAppServerProtocolException(
                "skills/config/write response did not include result.effectiveEnabled.");
        return new CodexSkillConfigWriteResult(effectiveEnabled);
    }

    private static List<CodexSkillLoadError> ParseSkillErrors(JsonArray? values)
    {
        var errors = new List<CodexSkillLoadError>();
        if (values is null)
        {
            return errors;
        }

        foreach (var value in values.OfType<JsonObject>())
        {
            var message = ReadString(value, "message");
            if (!string.IsNullOrWhiteSpace(message))
            {
                errors.Add(new CodexSkillLoadError(
                    ReadString(value, "path") ?? string.Empty,
                    message));
            }
        }

        return errors;
    }

    private static CodexSkillMetadata? ParseSkill(JsonObject value)
    {
        var name = ReadString(value, "name");
        var description = ReadString(value, "description");
        var path = ReadString(value, "path");
        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(description) ||
            string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        CodexSkillInterface? skillInterface = null;
        if (value["interface"] is JsonObject interfaceValue)
        {
            skillInterface = new CodexSkillInterface(
                ReadString(interfaceValue, "displayName"),
                ReadString(interfaceValue, "shortDescription"),
                ReadString(interfaceValue, "brandColor"),
                ReadString(interfaceValue, "defaultPrompt"),
                ReadString(interfaceValue, "iconSmall"),
                ReadString(interfaceValue, "iconLarge"));
        }

        CodexSkillDependencies? dependencies = null;
        if (value["dependencies"] is JsonObject dependencyValue &&
            dependencyValue["tools"] is JsonArray toolValues)
        {
            var tools = new List<CodexSkillToolDependency>();
            foreach (var toolValue in toolValues.OfType<JsonObject>())
            {
                var type = ReadString(toolValue, "type");
                var dependencyValueText = ReadString(toolValue, "value");
                if (!string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(dependencyValueText))
                {
                    tools.Add(new CodexSkillToolDependency(
                        type,
                        dependencyValueText,
                        ReadString(toolValue, "description"),
                        ReadString(toolValue, "command"),
                        ReadString(toolValue, "transport"),
                        ReadString(toolValue, "url")));
                }
            }

            dependencies = new CodexSkillDependencies(tools);
        }

        return new CodexSkillMetadata(
            name,
            description,
            Path.GetFullPath(path),
            ParseSkillScope(ReadString(value, "scope")),
            ReadBool(value, "enabled") ?? true,
            ReadString(value, "shortDescription"),
            skillInterface,
            dependencies);
    }

    private static CodexSkillScope ParseSkillScope(string? value) => value?.ToLowerInvariant() switch
    {
        "user" => CodexSkillScope.User,
        "repo" => CodexSkillScope.Repository,
        "system" => CodexSkillScope.System,
        "admin" => CodexSkillScope.Admin,
        _ => CodexSkillScope.Unknown
    };

    private static string? ReadString(JsonObject? source, string propertyName) =>
        source?[propertyName] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static bool? ReadBool(JsonObject? source, string propertyName) =>
        source?[propertyName] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : null;
}
