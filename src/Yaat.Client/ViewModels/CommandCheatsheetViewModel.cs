using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

public partial class CommandCheatsheetRowVm : ObservableObject
{
    public required string Verb { get; init; }
    public required string Aliases { get; init; }
    public required string Description { get; init; }
    public required bool IsGlobal { get; init; }
    public required string Examples { get; init; }
    public required string SearchText { get; init; }

    [ObservableProperty]
    private bool _isVisible = true;
}

public partial class CommandCheatsheetSectionVm : ObservableObject
{
    public required string Category { get; init; }
    public required IReadOnlyList<CommandCheatsheetRowVm> AllRows { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public ObservableCollection<CommandCheatsheetRowVm> VisibleRows { get; } = [];

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isExpanded = true;

    public void ApplyFilter(string filter)
    {
        VisibleRows.Clear();
        var hasFilter = !string.IsNullOrWhiteSpace(filter);
        var needle = filter.Trim().ToLowerInvariant();
        var categoryMatches = hasFilter && Category.ToLowerInvariant().Contains(needle);

        var matchedRows = 0;
        foreach (var row in AllRows)
        {
            var match = !hasFilter || categoryMatches || row.SearchText.Contains(needle);
            row.IsVisible = match;
            if (match)
            {
                VisibleRows.Add(row);
                matchedRows++;
            }
        }

        var matchedNotes = 0;
        if (hasFilter && !categoryMatches)
        {
            foreach (var note in Notes)
            {
                if (note.ToLowerInvariant().Contains(needle))
                {
                    matchedNotes++;
                }
            }
        }

        IsVisible = !hasFilter || categoryMatches || matchedRows > 0 || matchedNotes > 0;
        if (hasFilter && IsVisible)
        {
            IsExpanded = true;
        }
    }
}

public partial class CommandCheatsheetViewModel : ObservableObject
{
    public IReadOnlyList<CommandCheatsheetSectionVm> Sections { get; }
    public IReadOnlyList<string> Intro { get; }

    [ObservableProperty]
    private string _filterText = "";

    public CommandCheatsheetViewModel(CommandCheatsheetData data)
    {
        Intro = data.Intro;
        Sections = [.. data.Categories.Select(BuildSection)];
    }

    partial void OnFilterTextChanged(string value)
    {
        foreach (var section in Sections)
        {
            section.ApplyFilter(value);
        }
    }

    private static CommandCheatsheetSectionVm BuildSection(CommandCheatsheetSection section)
    {
        var rows = section
            .Rows.Select(r => new CommandCheatsheetRowVm
            {
                Verb = r.Verb,
                Aliases = string.Join(" ", r.Aliases),
                Description = r.Description,
                IsGlobal = r.Global,
                Examples = string.Join("  ", r.Examples),
                SearchText = BuildSearchText(r),
            })
            .ToList();

        var vm = new CommandCheatsheetSectionVm
        {
            Category = section.Name,
            AllRows = rows,
            Notes = section.Notes,
        };
        vm.ApplyFilter("");
        return vm;
    }

    private static string BuildSearchText(CommandCheatsheetRow row)
    {
        var aliases = string.Join(" ", row.Aliases);
        var examples = string.Join(" ", row.Examples);
        return $"{row.Verb} {aliases} {row.Description} {examples}".ToLowerInvariant();
    }
}
