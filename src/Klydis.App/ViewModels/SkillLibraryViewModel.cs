using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klydis.Core.Skills;

namespace Klydis.App.ViewModels;

public partial class SkillDisplayItem : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _category = string.Empty;

    [ObservableProperty]
    private string _tagsText = string.Empty;

    [ObservableProperty]
    private SkillComplexity _complexity;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _source = "Awesome-LLM-Skills";

    [ObservableProperty]
    private string _filePath = string.Empty;

    public Skill OriginalSkill { get; set; } = new();
}

public partial class SkillLibraryViewModel : ObservableObject
{
    private readonly SkillLibraryManager _libraryManager;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _selectedCategory = "All";

    [ObservableProperty]
    private SkillDisplayItem? _selectedSkill;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isNewSkill;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    // Edit fields
    [ObservableProperty]
    private string _editId = string.Empty;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private string _editDescription = string.Empty;

    [ObservableProperty]
    private string _editCategory = "Custom";

    [ObservableProperty]
    private string _editTags = string.Empty;

    [ObservableProperty]
    private string _editInstruction = string.Empty;

    [ObservableProperty]
    private SkillComplexity _editComplexity = SkillComplexity.Moderate;

    [ObservableProperty]
    private string _editAuthor = "User";

    [ObservableProperty]
    private string _editVersion = "1.0.0";

    public ObservableCollection<SkillDisplayItem> FilteredSkills { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();
    public ObservableCollection<SkillComplexity> AvailableComplexities { get; } = new();

    private List<Skill> _allSkills = new();

    public SkillLibraryViewModel(SkillLibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
        _libraryManager.SkillsChanged += OnSkillsChanged;

        AvailableComplexities.Add(SkillComplexity.Simple);
        AvailableComplexities.Add(SkillComplexity.Moderate);
        AvailableComplexities.Add(SkillComplexity.Complex);
        AvailableComplexities.Add(SkillComplexity.Specialized);

        _ = RefreshSkillsAsync();
    }

    private void OnSkillsChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(async () => await RefreshSkillsAsync());
        }
        else
        {
            _ = RefreshSkillsAsync();
        }
    }

    public async Task RefreshSkillsAsync()
    {
        var skills = await Task.Run(() => _libraryManager.GetAllSkills());

        Action updateUi = () =>
        {
            _allSkills = skills;
            UpdateCategoryList();
            FilterSkills();
            StatusMessage = $"Loaded {_allSkills.Count} skills ({_allSkills.Count(s => s.IsEnabled)} active)";
        };

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(updateUi);
        }
        else
        {
            updateUi();
        }
    }

    private void UpdateCategoryList()
    {
        string currentSel = SelectedCategory;
        Categories.Clear();
        Categories.Add("All");

        var cats = _allSkills.Select(s => s.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c);
        foreach (var cat in cats)
        {
            Categories.Add(cat);
        }

        if (Categories.Contains(currentSel))
            SelectedCategory = currentSel;
        else
            SelectedCategory = "All";
    }

    partial void OnSearchQueryChanged(string value) => FilterSkills();
    partial void OnSelectedCategoryChanged(string value) => FilterSkills();

    private void FilterSkills()
    {
        FilteredSkills.Clear();

        var query = SearchQuery.Trim().ToLowerInvariant();

        var matches = _allSkills.Where(s =>
        {
            bool catMatch = SelectedCategory == "All" || string.Equals(s.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase);
            if (!catMatch) return false;

            if (string.IsNullOrWhiteSpace(query)) return true;

            bool nameMatch = s.Name.ToLowerInvariant().Contains(query);
            bool descMatch = s.Description.ToLowerInvariant().Contains(query);
            bool idMatch = s.Id.ToLowerInvariant().Contains(query);
            bool tagMatch = s.Tags.Any(t => t.ToLowerInvariant().Contains(query));

            return nameMatch || descMatch || idMatch || tagMatch;
        }).OrderBy(s => s.Name);

        foreach (var s in matches)
        {
            FilteredSkills.Add(new SkillDisplayItem
            {
                Id = s.Id,
                Name = s.Name,
                Description = s.Description,
                Category = s.Category,
                TagsText = string.Join(", ", s.Tags),
                Complexity = s.Complexity,
                IsEnabled = s.IsEnabled,
                Source = s.Source,
                FilePath = s.FilePath,
                OriginalSkill = s
            });
        }

        if (SelectedSkill == null || !FilteredSkills.Any(s => s.Id == SelectedSkill.Id))
        {
            SelectedSkill = FilteredSkills.FirstOrDefault();
        }
    }

    partial void OnSelectedSkillChanged(SkillDisplayItem? value)
    {
        if (value != null)
        {
            LoadSkillToEditor(value.OriginalSkill);
            IsEditing = true;
            IsNewSkill = false;
        }
        else
        {
            IsEditing = false;
        }
    }

    private void LoadSkillToEditor(Skill s)
    {
        EditId = s.Id;
        EditName = s.Name;
        EditDescription = s.Description;
        EditCategory = s.Category;
        EditTags = string.Join(", ", s.Tags);
        EditInstruction = s.PromptInstruction;
        EditComplexity = s.Complexity;
        EditAuthor = s.Author;
        EditVersion = s.Version;
    }

    [RelayCommand]
    private void NewSkill()
    {
        SelectedSkill = null;
        EditId = $"custom-skill-{DateTime.Now:MMddHHmm}";
        EditName = "New Custom Skill";
        EditDescription = "Description of what this skill enables the AI agent to do.";
        EditCategory = "Custom";
        EditTags = "custom, workflow";
        EditInstruction = "# New Skill Directive\n\nProvide detailed guidelines, instructions, or rules for this skill here.";
        EditComplexity = SkillComplexity.Moderate;
        EditAuthor = "User";
        EditVersion = "1.0.0";

        IsEditing = true;
        IsNewSkill = true;
        StatusMessage = "Creating new custom skill";
    }

    [RelayCommand]
    private async Task SaveSkillAsync()
    {
        if (string.IsNullOrWhiteSpace(EditName) || string.IsNullOrWhiteSpace(EditInstruction))
        {
            StatusMessage = "Error: Skill Name and Instructions are required.";
            return;
        }

        var tagsList = EditTags.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(t => t.Trim())
                               .Where(t => t.Length > 0)
                               .ToList();

        var skillToSave = new Skill
        {
            Id = string.IsNullOrWhiteSpace(EditId) ? Guid.NewGuid().ToString("N") : EditId.Trim().ToLowerInvariant().Replace(" ", "-"),
            Name = EditName.Trim(),
            Description = EditDescription.Trim(),
            Category = string.IsNullOrWhiteSpace(EditCategory) ? "Custom" : EditCategory.Trim(),
            Tags = tagsList,
            PromptInstruction = EditInstruction,
            Complexity = EditComplexity,
            Author = EditAuthor,
            Version = EditVersion,
            IsEnabled = SelectedSkill?.IsEnabled ?? true,
            FilePath = SelectedSkill?.FilePath ?? string.Empty
        };

        await _libraryManager.SaveSkillAsync(skillToSave);
        StatusMessage = $"Saved skill '{skillToSave.Name}'";
    }

    [RelayCommand]
    private async Task DeleteSkillAsync()
    {
        if (SelectedSkill == null) return;

        if (SelectedSkill.Source != "Custom")
        {
            StatusMessage = "Cannot delete built-in repository skill. Toggle off to disable instead.";
            return;
        }

        string idToDelete = SelectedSkill.Id;
        string name = SelectedSkill.Name;

        await _libraryManager.DeleteCustomSkillAsync(idToDelete);
        StatusMessage = $"Deleted custom skill '{name}'";
    }

    [RelayCommand]
    private void ToggleSkill(SkillDisplayItem? item)
    {
        if (item == null) return;
        bool newState = !item.IsEnabled;
        item.IsEnabled = newState;
        _libraryManager.ToggleSkillState(item.Id, newState);
        StatusMessage = $"{item.Name} {(newState ? "enabled" : "disabled")}";
    }

    [RelayCommand]
    private async Task ReloadLibraryAsync()
    {
        StatusMessage = "Reloading skill library...";
        await _libraryManager.InitializeAsync();
    }
}
