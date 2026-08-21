using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Klydis.Core.Skills;

/// <summary>
/// Parser that extracts machine-readable skill manifests from SKILL.md frontmatter or synthesizes them from Skill models.
/// </summary>
public static class SkillManifestParser
{
    private static readonly Regex FrontmatterRegex = new(@"^---\s*[\r\n]+(.*?)\s*[\r\n]+---", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Parses a SkillManifest from a raw SKILL.md markdown text.
    /// </summary>
    public static SkillManifest ParseMarkdown(string skillId, string markdownContent, string filePath = "")
    {
        if (string.IsNullOrWhiteSpace(markdownContent))
        {
            return new SkillManifest { SkillId = skillId, Name = skillId, FullBodyPath = filePath };
        }

        var match = FrontmatterRegex.Match(markdownContent);
        string body = match.Success ? markdownContent.Substring(match.Length).Trim() : markdownContent.Trim();

        string name = skillId;
        string description = string.Empty;
        string category = "General";
        var provides = new List<string>();
        var activateWhen = new List<string>();
        var doNotActivateWhen = new List<string>();
        var entities = new List<string>();
        var keywords = new List<string>();
        var dependencies = new List<string>();
        var conflicts = new List<string>();
        var environments = new List<string> { "windows" };
        var verification = new List<string>();

        if (match.Success)
        {
            string frontmatter = match.Groups[1].Value;
            var lines = frontmatter.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string? currentListKey = null;

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("- ") && currentListKey != null)
                {
                    string item = line.Substring(2).Trim().Trim('"', '\'');
                    AddToList(currentListKey, item);
                    continue;
                }

                int colonIdx = line.IndexOf(':');
                if (colonIdx > 0)
                {
                    string key = line.Substring(0, colonIdx).Trim().ToLowerInvariant();
                    string val = line.Substring(colonIdx + 1).Trim().Trim('"', '\'');

                    if (string.IsNullOrWhiteSpace(val))
                    {
                        currentListKey = key;
                    }
                    else
                    {
                        currentListKey = null;
                        switch (key)
                        {
                            case "id":
                                skillId = val;
                                break;
                            case "name":
                                name = val;
                                break;
                            case "description":
                                description = val;
                                break;
                            case "category":
                                category = val;
                                break;
                            case "tags":
                            case "keywords":
                                keywords.AddRange(val.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
                                break;
                            case "provides":
                                provides.Add(val);
                                break;
                            case "activate_when":
                                activateWhen.Add(val);
                                break;
                            case "do_not_activate_when":
                                doNotActivateWhen.Add(val);
                                break;
                            case "entities":
                                entities.AddRange(val.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
                                break;
                        }
                    }
                }
            }
        }

        void AddToList(string key, string item)
        {
            switch (key)
            {
                case "provides":
                    provides.Add(item);
                    break;
                case "activate_when":
                    activateWhen.Add(item);
                    break;
                case "do_not_activate_when":
                    doNotActivateWhen.Add(item);
                    break;
                case "entities":
                    entities.Add(item);
                    break;
                case "keywords":
                case "tags":
                    keywords.Add(item);
                    break;
                case "dependencies":
                    dependencies.Add(item);
                    break;
                case "conflicts":
                    conflicts.Add(item);
                    break;
                case "environments":
                case "supported_environments":
                    environments.Add(item);
                    break;
                case "verification":
                    verification.Add(item);
                    break;
            }
        }

        // Infer entities and keywords if none provided
        if (keywords.Count == 0 && !string.IsNullOrWhiteSpace(category))
        {
            keywords.Add(category.ToLowerInvariant());
        }

        return new SkillManifest
        {
            SkillId = skillId,
            Name = !string.IsNullOrWhiteSpace(name) ? name : skillId,
            Description = description,
            Category = category,
            Provides = provides,
            ActivateWhen = activateWhen,
            DoNotActivateWhen = doNotActivateWhen,
            Entities = entities,
            Keywords = keywords,
            Dependencies = dependencies,
            Conflicts = conflicts,
            SupportedEnvironments = environments,
            Verification = verification,
            FullBodyPath = filePath,
            PromptInstruction = body
        };
    }

    /// <summary>
    /// Synthesizes a SkillManifest from an existing Skill model.
    /// </summary>
    public static SkillManifest FromSkill(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);

        var manifest = ParseMarkdown(skill.Id, skill.PromptInstruction, skill.FilePath);
        
        return manifest with
        {
            SkillId = skill.Id,
            Name = !string.IsNullOrWhiteSpace(skill.Name) ? skill.Name : manifest.Name,
            Description = !string.IsNullOrWhiteSpace(skill.Description) ? skill.Description : manifest.Description,
            Category = !string.IsNullOrWhiteSpace(skill.Category) ? skill.Category : manifest.Category,
            Keywords = manifest.Keywords.Count > 0 ? manifest.Keywords : skill.Tags,
            PromptInstruction = !string.IsNullOrWhiteSpace(manifest.PromptInstruction) ? manifest.PromptInstruction : skill.PromptInstruction,
            FullBodyPath = skill.FilePath
        };
    }
}
