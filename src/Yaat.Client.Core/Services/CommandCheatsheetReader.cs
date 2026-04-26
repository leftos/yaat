using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaat.Client.Services;

public sealed record CommandCheatsheetRow(
    string Verb,
    IReadOnlyList<string> Aliases,
    string Description,
    bool Global,
    IReadOnlyList<string> Examples
);

public sealed record CommandCheatsheetSection(string Id, string Name, IReadOnlyList<CommandCheatsheetRow> Rows, IReadOnlyList<string> Notes);

public sealed record CommandCheatsheetData(string Version, IReadOnlyList<string> Intro, IReadOnlyList<CommandCheatsheetSection> Categories);

public static class CommandCheatsheetReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static CommandCheatsheetData Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("JSON payload was empty.", nameof(json));
        }

        var raw = JsonSerializer.Deserialize<RawData>(json, JsonOptions) ?? throw new InvalidDataException("Cheatsheet JSON deserialized to null.");

        var categories = new List<CommandCheatsheetSection>(raw.Categories?.Count ?? 0);
        foreach (var cat in raw.Categories ?? [])
        {
            var rows = new List<CommandCheatsheetRow>(cat.Rows?.Count ?? 0);
            foreach (var row in cat.Rows ?? [])
            {
                rows.Add(
                    new CommandCheatsheetRow(
                        Verb: row.Verb ?? string.Empty,
                        Aliases: row.Aliases ?? [],
                        Description: row.Description ?? string.Empty,
                        Global: row.Global,
                        Examples: row.Examples ?? []
                    )
                );
            }
            categories.Add(
                new CommandCheatsheetSection(Id: cat.Id ?? string.Empty, Name: cat.Name ?? string.Empty, Rows: rows, Notes: cat.Notes ?? [])
            );
        }

        return new CommandCheatsheetData(Version: raw.Version ?? string.Empty, Intro: raw.Intro ?? [], Categories: categories);
    }

    private sealed class RawData
    {
        public string? Version { get; set; }
        public List<string>? Intro { get; set; }
        public List<RawSection>? Categories { get; set; }
    }

    private sealed class RawSection
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public List<RawRow>? Rows { get; set; }
        public List<string>? Notes { get; set; }
    }

    private sealed class RawRow
    {
        public string? Verb { get; set; }
        public List<string>? Aliases { get; set; }
        public string? Description { get; set; }
        public bool Global { get; set; }
        public List<string>? Examples { get; set; }
    }
}
