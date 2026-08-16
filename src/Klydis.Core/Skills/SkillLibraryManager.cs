using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Skills;

public class SkillLibraryManager
{
    private readonly ILogger<SkillLibraryManager>? _logger;
    private readonly string _skillsBasePath;
    private readonly string _awesomeSkillsPath;
    private readonly string _customSkillsPath;
    private readonly string _userStateFilePath;

    private readonly List<Skill> _skills = new();
    private readonly object _lock = new();

    public event Action? SkillsChanged;

    public SkillLibraryManager(ILogger<SkillLibraryManager>? logger = null, string? baseDirectory = null)
    {
        _logger = logger;
        string rootDir = baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory;

        if (baseDirectory != null)
        {
            // Explicit base (tests/fixtures): keep the historical behavior — find a project
            // root above it, else use the directory itself.
            string projectRoot = FindProjectRoot(rootDir) ?? rootDir;
            _skillsBasePath = Path.Combine(projectRoot, "assets", "skills");
        }
        else
        {
            // Resolve dev-tree vs installed layout. A dev tree is a repo checkout, which
            // always has KlydisBeta.sln somewhere above the app base — the sln is the ONLY
            // reliable marker. A "populated assets/skills" heuristic would mistake an
            // installed build for a project root (the installer ships assets/skills/custom
            // next to the exe) and then try to write skill_states.json into Program Files,
            // which throws UnauthorizedAccessException for standard users.
            string? devRoot = FindDevRoot(rootDir);
            if (devRoot != null)
            {
                _skillsBasePath = Path.Combine(devRoot, "assets", "skills");
            }
            else
            {
                // Installed build: bundled skills are read-only next to the exe, so the
                // working copy (skill_states.json, imported skills) lives in the user profile
                // — the same %USERPROFILE%\.klydis home NativeEngineManager uses. Seeded once
                // from the bundled assets on first run.
                _skillsBasePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".klydis", "skills");
                SeedFromBundledSkills();
            }
        }

        _awesomeSkillsPath = Path.Combine(_skillsBasePath, "awesome-llm-skills");
        _customSkillsPath = Path.Combine(_skillsBasePath, "custom");
        _userStateFilePath = Path.Combine(_skillsBasePath, "skill_states.json");

        Directory.CreateDirectory(_skillsBasePath);
        Directory.CreateDirectory(_customSkillsPath);
    }

    /// <summary>
    /// Walks up from <paramref name="startPath"/> looking for KlydisBeta.sln — the definitive
    /// repo-checkout marker. Used to decide dev-tree vs installed layout.
    /// </summary>
    private static string? FindDevRoot(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "KlydisBeta.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Copies the bundled skills shipped next to the exe (<c>assets\skills</c>) into the
    /// user-writable skills directory — once, on first run. If the user directory already
    /// exists with content it is left untouched so imported/customized skills are never
    /// clobbered. Never throws: a seeding failure must not break startup.
    /// </summary>
    private void SeedFromBundledSkills()
    {
        try
        {
            string bundledSkillsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "skills");
            if (!Directory.Exists(bundledSkillsPath)) return;

            // First run only: nothing (or nothing but an empty shell) in the user directory yet.
            if (Directory.Exists(_skillsBasePath) &&
                Directory.GetFileSystemEntries(_skillsBasePath).Length > 0)
            {
                return;
            }

            Directory.CreateDirectory(_skillsBasePath);
            CopyDirectoryContents(bundledSkillsPath, _skillsBasePath);
            _logger?.LogInformation("Seeded bundled skills from {Source} into {Target}.", bundledSkillsPath, _skillsBasePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to seed bundled skills into {Target}; continuing with an empty skills library.", _skillsBasePath);
        }
    }

    private static void CopyDirectoryContents(string sourceDir, string targetDir)
    {
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            string name = Path.GetFileName(subDir);
            string target = Path.Combine(targetDir, name);
            Directory.CreateDirectory(target);
            CopyDirectoryContents(subDir, target);
        }
    }

    private static string? FindProjectRoot(string currentPath)
    {
        var dir = new DirectoryInfo(currentPath);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "KlydisBeta.sln")))
            {
                return dir.FullName;
            }

            string skillsSubDir = Path.Combine(dir.FullName, "assets", "skills");
            if (Directory.Exists(skillsSubDir))
            {
                bool hasAwesome = Directory.Exists(Path.Combine(skillsSubDir, "awesome-llm-skills")) && Directory.GetFileSystemEntries(Path.Combine(skillsSubDir, "awesome-llm-skills")).Length > 0;
                bool hasNvidia = Directory.Exists(Path.Combine(skillsSubDir, "nvidia-skills")) && Directory.GetFileSystemEntries(Path.Combine(skillsSubDir, "nvidia-skills")).Length > 0;
                bool hasCustom = Directory.Exists(Path.Combine(skillsSubDir, "custom")) && Directory.GetFileSystemEntries(Path.Combine(skillsSubDir, "custom")).Length > 0;

                if (hasAwesome || hasNvidia || hasCustom)
                {
                    return dir.FullName;
                }
            }

            dir = dir.Parent;
        }
        return null;
    }

    public async Task InitializeAsync()
    {
        await Task.Run(() => ScanAndLoadSkills());
    }

    public void ScanAndLoadSkills()
    {
        lock (_lock)
        {
            _skills.Clear();

            if (Directory.Exists(_skillsBasePath))
            {
                var subDirs = Directory.GetDirectories(_skillsBasePath);
                foreach (var dir in subDirs)
                {
                    string folderName = Path.GetFileName(dir);
                    string sourceName = folderName switch
                    {
                        "awesome-llm-skills" => "Awesome-LLM-Skills",
                        "nvidia-skills" => "NVIDIA-Skills",
                        "custom" => "Custom",
                        _ => FormatTitle(folderName)
                    };
                    LoadSkillsFromDirectory(dir, sourceName);
                }
            }

            // Apply saved enabled/disabled user states if any
            ApplySavedSkillStates();
        }

        SkillsChanged?.Invoke();
    }

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "templates", "docs", "examples", "references", "components.d", "plugins.d", 
        "fern", ".github", ".agents", ".claude-plugin", ".cursor-plugin", ".git"
    };

    private static readonly HashSet<string> IgnoredFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "README.md", "CONTRIBUTING.md", "CHANGELOG.md", "CODE_OF_CONDUCT.md", 
        "SECURITY.md", "LICENSE.md", "NOTICE.md"
    };

    private void LoadSkillsFromDirectory(string rootDir, string defaultSource)
    {
        try
        {
            if (!Directory.Exists(rootDir)) return;

            var allMdFiles = Directory.GetFiles(rootDir, "*.md", SearchOption.AllDirectories);
            
            // Group files by directory
            var filesByDir = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var filePath in allMdFiles)
            {
                string fileName = Path.GetFileName(filePath);
                string relativePath = Path.GetRelativePath(rootDir, filePath);
                var pathSegments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // Ignore metadata/sub-documentation directories
                if (pathSegments.Any(s => IgnoredDirectories.Contains(s)))
                    continue;

                // Ignore repository level README/CONTRIBUTING/CHANGELOG etc.
                if (IgnoredFiles.Contains(fileName))
                    continue;

                string dirPath = Path.GetDirectoryName(filePath) ?? rootDir;
                if (!filesByDir.ContainsKey(dirPath))
                {
                    filesByDir[dirPath] = new List<string>();
                }
                filesByDir[dirPath].Add(filePath);
            }

            foreach (var kvp in filesByDir)
            {
                var filesInDir = kvp.Value;
                // Prioritize SKILL.md or skill.md if present
                string? selectedFile = filesInDir.FirstOrDefault(f => 
                    string.Equals(Path.GetFileName(f), "SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(f), "skill.md", StringComparison.OrdinalIgnoreCase)) 
                    ?? filesInDir.FirstOrDefault();

                if (selectedFile != null)
                {
                    var skill = ParseSkillFile(selectedFile, defaultSource);
                    if (skill != null)
                    {
                        // Avoid duplicate IDs
                        if (!_skills.Any(s => s.Id.Equals(skill.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            _skills.Add(skill);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load skills from directory {Directory}", rootDir);
        }
    }

    private Skill? ParseSkillFile(string filePath, string defaultSource)
    {
        try
        {
            string content = File.ReadAllText(filePath).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (string.IsNullOrWhiteSpace(content))
                return null;

            string folderName = Path.GetFileName(Path.GetDirectoryName(filePath) ?? "");
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);

            string name = string.Empty;
            string description = string.Empty;
            string category = string.Empty;
            string author = "Community";
            string version = "1.0.0";
            List<string> tags = new();

            string promptBody = content;

            // Parse YAML frontmatter if present
            if (content.StartsWith("---"))
            {
                int endFrontmatterIndex = content.IndexOf("---", 3);
                if (endFrontmatterIndex > 3)
                {
                    string frontmatter = content.Substring(3, endFrontmatterIndex - 3);
                    promptBody = content.Substring(endFrontmatterIndex + 3).Trim();

                    var lines = frontmatter.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        int colonIndex = line.IndexOf(':');
                        if (colonIndex > 0)
                        {
                            string key = line.Substring(0, colonIndex).Trim().ToLowerInvariant();
                            string val = line.Substring(colonIndex + 1).Trim().Trim('"', '\'');

                            switch (key)
                            {
                                case "name":
                                    if (!string.IsNullOrWhiteSpace(val)) name = val;
                                    break;
                                case "description":
                                    if (!string.IsNullOrWhiteSpace(val)) description = val;
                                    break;
                                case "category":
                                    if (!string.IsNullOrWhiteSpace(val)) category = val;
                                    break;
                                case "author":
                                    if (!string.IsNullOrWhiteSpace(val)) author = val;
                                    break;
                                case "version":
                                    if (!string.IsNullOrWhiteSpace(val)) version = val;
                                    break;
                            }
                        }
                    }
                }
            }

            // Calculate skillId
            string skillId;
            if (!string.IsNullOrWhiteSpace(name))
            {
                skillId = name.Trim().ToLowerInvariant().Replace(" ", "-");
            }
            else if (!string.IsNullOrEmpty(folderName) && !folderName.Equals("custom", StringComparison.OrdinalIgnoreCase) && !folderName.Equals("skills", StringComparison.OrdinalIgnoreCase))
            {
                skillId = folderName.ToLowerInvariant();
            }
            else
            {
                skillId = fileNameWithoutExt.ToLowerInvariant();
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = FormatTitle(skillId);
            }

            if (string.IsNullOrWhiteSpace(category))
            {
                category = DetermineCategory(skillId, content);
            }

            tags = ExtractTags(skillId, content);
            SkillComplexity complexity = DetermineComplexity(content);

            // Fallback description from first paragraph if empty
            if (string.IsNullOrWhiteSpace(description))
            {
                var match = Regex.Match(promptBody, @"(?:^|\n)(?:#+ .*?\n)?\s*([^\n#]+)");
                if (match.Success)
                {
                    description = match.Groups[1].Value.Trim();
                    if (description.Length > 200) description = description.Substring(0, 197) + "...";
                }
            }

            return new Skill
            {
                Id = skillId,
                Name = name,
                Description = description,
                Category = category,
                Tags = tags,
                PromptInstruction = promptBody,
                Complexity = complexity,
                IsEnabled = true,
                Source = defaultSource,
                FilePath = filePath,
                Author = author,
                Version = version,
                LastModified = File.GetLastWriteTime(filePath)
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse skill file at {FilePath}", filePath);
            return null;
        }
    }

    private static string FormatTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Skill";
        var parts = raw.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(p => char.ToUpper(p[0]) + p.Substring(1)));
    }

    private static string DetermineCategory(string skillId, string content)
    {
        string text = (skillId + " " + content).ToLowerInvariant();

        if (text.Contains("jetson") || text.Contains("holoscan") || text.Contains("robot") || text.Contains("deepstream") || text.Contains("vision") || text.Contains("i4h"))
            return "Robotics & Edge AI";
        if (text.Contains("nemo") || text.Contains("tao") || text.Contains("quant") || text.Contains("training") || text.Contains("rag") || text.Contains("rl"))
            return "AI & ML Infrastructure";
        if (text.Contains("cuda") || text.Contains("doca") || text.Contains("hpc") || text.Contains("accelerat") || text.Contains("gpu") || text.Contains("cuopt"))
            return "Accelerated Computing & HPC";
        if (text.Contains("art") || text.Contains("p5.js") || text.Contains("canvas") || text.Contains("gif") || text.Contains("ad"))
            return "Creative & Design";
        if (text.Contains("mcp") || text.Contains("code") || text.Contains("testing") || text.Contains("impl") || text.Contains("spec"))
            return "Development & Architecture";
        if (text.Contains("notion") || text.Contains("meeting") || text.Contains("comms") || text.Contains("slack"))
            return "Productivity & Collaboration";
        if (text.Contains("writer") || text.Contains("research") || text.Contains("brand") || text.Contains("article"))
            return "Writing & Research";
        if (text.Contains("analyzer") || text.Contains("detect") || text.Contains("changelog") || text.Contains("invoice"))
            return "Analysis & Workflows";

        return "General";
    }

    private static List<string> ExtractTags(string skillId, string content)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in skillId.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length > 2) tags.Add(word);
        }

        string text = content.ToLowerInvariant();
        if (text.Contains("python")) tags.Add("python");
        if (text.Contains("typescript") || text.Contains("node")) tags.Add("typescript");
        if (text.Contains("notion")) tags.Add("notion");
        if (text.Contains("mcp")) tags.Add("mcp");
        if (text.Contains("art") || text.Contains("canvas")) tags.Add("creative");
        if (text.Contains("testing")) tags.Add("testing");

        return tags.Take(6).ToList();
    }

    private static SkillComplexity DetermineComplexity(string content)
    {
        int length = content.Length;
        if (length > 10000 || content.Contains("Phase 4") || content.Contains("Architecture"))
            return SkillComplexity.Specialized;
        if (length > 4000)
            return SkillComplexity.Complex;
        if (length > 1500)
            return SkillComplexity.Moderate;

        return SkillComplexity.Simple;
    }

    public List<Skill> GetAllSkills()
    {
        lock (_lock)
        {
            return _skills.Select(s => s.Clone()).ToList();
        }
    }

    public List<Skill> GetEnabledSkills()
    {
        lock (_lock)
        {
            return _skills.Where(s => s.IsEnabled).Select(s => s.Clone()).ToList();
        }
    }

    public Skill? GetSkillById(string id)
    {
        lock (_lock)
        {
            var match = _skills.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return match?.Clone();
        }
    }

    public List<string> GetCategories()
    {
        lock (_lock)
        {
            return _skills.Select(s => s.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();
        }
    }

    public List<Skill> SearchSkills(string query, int topN = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return GetAllSkills().Take(topN).ToList();

        string lowerQuery = query.ToLowerInvariant();
        var terms = lowerQuery.Split(new[] { ' ', ',', '.', ';' }, StringSplitOptions.RemoveEmptyEntries);

        lock (_lock)
        {
            var scored = _skills.Select(skill =>
            {
                double score = 0;
                string lowerId = skill.Id.ToLowerInvariant();
                string lowerName = skill.Name.ToLowerInvariant();
                string lowerCat = skill.Category.ToLowerInvariant();
                string lowerDesc = skill.Description.ToLowerInvariant();

                if (lowerId == lowerQuery || lowerName == lowerQuery) score += 20;
                else if (lowerId.Contains(lowerQuery) || lowerName.Contains(lowerQuery)) score += 10;

                if (lowerCat.Contains(lowerQuery)) score += 5;

                foreach (var tag in skill.Tags)
                {
                    if (tag.Equals(lowerQuery, StringComparison.OrdinalIgnoreCase)) score += 8;
                    else if (tag.ToLowerInvariant().Contains(lowerQuery)) score += 4;
                }

                foreach (var term in terms)
                {
                    if (term.Length < 3) continue;
                    if (lowerId.Contains(term) || lowerName.Contains(term)) score += 3;
                    if (lowerDesc.Contains(term)) score += 1;
                }

                return (Skill: skill.Clone(), Score: score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(topN)
            .Select(x => x.Skill)
            .ToList();

            return scored;
        }
    }

    public void ToggleSkillState(string id, bool isEnabled)
    {
        lock (_lock)
        {
            var skill = _skills.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (skill != null)
            {
                skill.IsEnabled = isEnabled;
                SaveSkillStates();
            }
        }
        SkillsChanged?.Invoke();
    }

    public async Task SaveSkillAsync(Skill skill)
    {
        await Task.Run(() =>
        {
            lock (_lock)
            {
                string targetDir = _customSkillsPath;
                if (!string.IsNullOrEmpty(skill.FilePath) && File.Exists(skill.FilePath) && skill.FilePath.StartsWith(_customSkillsPath))
                {
                    targetDir = Path.GetDirectoryName(skill.FilePath)!;
                }

                string fileName = $"{skill.Id}.md";
                string filePath = Path.Combine(targetDir, fileName);

                var sb = new StringBuilder();
                sb.AppendLine("---");
                sb.AppendLine($"name: {skill.Name}");
                sb.AppendLine($"description: {skill.Description}");
                sb.AppendLine($"category: {skill.Category}");
                sb.AppendLine($"author: {skill.Author}");
                sb.AppendLine($"version: {skill.Version}");
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine(skill.PromptInstruction);

                File.WriteAllText(filePath, sb.ToString());

                skill.FilePath = filePath;
                skill.Source = "Custom";
                skill.LastModified = DateTime.Now;

                int existingIndex = _skills.FindIndex(s => s.Id.Equals(skill.Id, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    _skills[existingIndex] = skill.Clone();
                }
                else
                {
                    _skills.Add(skill.Clone());
                }

                SaveSkillStates();
            }
        });

        SkillsChanged?.Invoke();
    }

    public async Task DeleteCustomSkillAsync(string id)
    {
        await Task.Run(() =>
        {
            lock (_lock)
            {
                var skill = _skills.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (skill != null)
                {
                    if (!string.IsNullOrEmpty(skill.FilePath) && File.Exists(skill.FilePath) && skill.FilePath.StartsWith(_customSkillsPath))
                    {
                        try
                        {
                            File.Delete(skill.FilePath);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Failed to delete skill file {FilePath}", skill.FilePath);
                        }
                    }
                    _skills.Remove(skill);
                    SaveSkillStates();
                }
            }
        });

        SkillsChanged?.Invoke();
    }

    private void SaveSkillStates()
    {
        try
        {
            var states = _skills.ToDictionary(s => s.Id, s => s.IsEnabled);
            string json = JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_userStateFilePath, json);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save skill states");
        }
    }

    private void ApplySavedSkillStates()
    {
        if (!File.Exists(_userStateFilePath)) return;

        try
        {
            string json = File.ReadAllText(_userStateFilePath);
            var states = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
            if (states != null)
            {
                foreach (var skill in _skills)
                {
                    if (states.TryGetValue(skill.Id, out bool isEnabled))
                    {
                        skill.IsEnabled = isEnabled;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load saved skill states");
        }
    }
}
