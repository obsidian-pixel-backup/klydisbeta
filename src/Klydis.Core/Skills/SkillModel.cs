using System;
using System.Collections.Generic;

namespace Klydis.Core.Skills;

public enum SkillComplexity
{
    Simple,
    Moderate,
    Complex,
    Specialized
}

public class Skill
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public List<string> Tags { get; set; } = new();
    public string PromptInstruction { get; set; } = string.Empty;
    public SkillComplexity Complexity { get; set; } = SkillComplexity.Moderate;
    public bool IsEnabled { get; set; } = true;
    public string Source { get; set; } = "Awesome-LLM-Skills";
    public string FilePath { get; set; } = string.Empty;
    public string Author { get; set; } = "Community";
    public string Version { get; set; } = "1.0.0";
    public DateTime LastModified { get; set; } = DateTime.Now;

    public Skill Clone()
    {
        return new Skill
        {
            Id = Id,
            Name = Name,
            Description = Description,
            Category = Category,
            Tags = new List<string>(Tags),
            PromptInstruction = PromptInstruction,
            Complexity = Complexity,
            IsEnabled = IsEnabled,
            Source = Source,
            FilePath = FilePath,
            Author = Author,
            Version = Version,
            LastModified = LastModified
        };
    }
}

public class SkillReasoningResult
{
    public SkillComplexity DetectedComplexity { get; set; } = SkillComplexity.Moderate;
    public List<Skill> SelectedSkills { get; set; } = new();
    public string ReasoningExplanation { get; set; } = string.Empty;
    public string FormattedPromptInjection { get; set; } = string.Empty;
}
