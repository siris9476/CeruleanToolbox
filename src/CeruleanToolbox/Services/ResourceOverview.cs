namespace CeruleanToolbox.Services;

public sealed record InfoSection(string Title, IReadOnlyList<KeyValuePair<string, string>> Items);

public sealed record InfoTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Portal-style overview of one Azure resource: key/value sections, tables and tags.</summary>
public sealed record ResourceOverview(
    string Status,
    IReadOnlyList<InfoSection> Sections,
    IReadOnlyList<InfoTable> Tables,
    IReadOnlyList<KeyValuePair<string, string>> Tags);
