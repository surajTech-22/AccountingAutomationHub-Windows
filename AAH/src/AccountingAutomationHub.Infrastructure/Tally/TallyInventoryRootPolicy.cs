using System.Security;
using System.Text;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// ERP9 inventory root names are built-in sentinels, not user-created Masters.
/// Keeps the source's real category hierarchy and never manufactures a missing parent.
/// </summary>
public static class TallyInventoryRootPolicy
{
    public static string NormalizeName(string? value)
    {
        var text = value ?? string.Empty;
        // Tally uses character 4 as an internal "Not Applicable" prefix. It is
        // not part of a user Master name and must not be treated as a real category.
        var start = 0;
        while (start < text.Length && (char.IsWhiteSpace(text[start]) || text[start] == '\u0004'))
            start++;
        return AccountingText.CleanWhitespace(text[start..]);
    }

    public static bool IsRootCategory(string? value)
    {
        var name = NormalizeName(value);
        return name.Length == 0 ||
               name.Equals("Primary", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Not Applicable", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRootStockGroup(string? value)
    {
        var name = NormalizeName(value);
        return name.Length == 0 || name.Equals("Primary", StringComparison.OrdinalIgnoreCase);
    }

    public static string CategoryForExport(string? value) =>
        IsRootCategory(value) ? string.Empty : NormalizeName(value);

    public static void AppendItemCategory(StringBuilder builder, string? category)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var name = CategoryForExport(category);
        if (name.Length > 0)
            builder.Append("      <CATEGORY>").Append(Escape(name)).AppendLine("</CATEGORY>");
    }

    public static IReadOnlyList<StockSimpleMasterRecord> OrderRequiredCategories(
        IEnumerable<string> requestedNames,
        IReadOnlyCollection<StockSimpleMasterRecord> categories)
    {
        ArgumentNullException.ThrowIfNull(requestedNames);
        ArgumentNullException.ThrowIfNull(categories);
        var index = new Dictionary<string, StockSimpleMasterRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            var name = CategoryForExport(category.Name);
            if (name.Length == 0) continue;
            if (!index.TryAdd(name, category))
                throw new InvalidOperationException($"Duplicate Stock Category '{name}' requires review before Tally export.");
        }

        var ordered = new List<StockSimpleMasterRecord>();
        var complete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string requested)
        {
            var name = CategoryForExport(requested);
            if (name.Length == 0 || complete.Contains(name)) return;
            if (!index.TryGetValue(name, out var category) || category.IsArchived)
                throw new InvalidOperationException($"Stock Category '{name}' is not an active saved Master. Resolve it in Stock Master before Tally export.");
            if (!visiting.Add(name))
                throw new InvalidOperationException($"Stock Category hierarchy contains a cycle at '{name}'.");
            var parent = CategoryForExport(category.Parent);
            if (parent.Length > 0) Visit(parent);
            visiting.Remove(name);
            complete.Add(name);
            ordered.Add(category);
        }
        foreach (var name in requestedNames) Visit(name);
        return ordered;
    }

    public static void AppendRequiredCategories(
        StringBuilder builder,
        IEnumerable<string> requestedNames,
        IReadOnlyCollection<StockSimpleMasterRecord> categories)
    {
        ArgumentNullException.ThrowIfNull(builder);
        // Resolve and validate the entire hierarchy before appending any XML.
        foreach (var category in OrderRequiredCategories(requestedNames, categories))
            AppendSimpleInventoryMaster(builder, "STOCKCATEGORY", category.Name, category.Parent, category.Source);
    }

    public static void AppendSimpleInventoryMaster(
        StringBuilder builder, string tag, string name, string parent, string source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (tag is not ("STOCKGROUP" or "STOCKCATEGORY" or "GODOWN"))
            throw new ArgumentException("Unsupported simple inventory Master.", nameof(tag));
        var actualName = NormalizeName(name);
        if (actualName.Length == 0) throw new InvalidOperationException("Inventory Master name is blank.");
        if (tag == "STOCKCATEGORY" && IsRootCategory(actualName)) return;
        if (tag == "STOCKGROUP" && IsRootStockGroup(actualName)) return;

        var actualParent = tag == "STOCKCATEGORY"
            ? CategoryForExport(parent)
            : tag == "STOCKGROUP" && IsRootStockGroup(parent)
                ? string.Empty
                : NormalizeName(parent);

        builder.AppendLine("  <TALLYMESSAGE>");
        builder.Append("    <").Append(tag).Append(" NAME=\"").Append(Escape(actualName))
            .Append("\" ACTION=\"")
            .Append(!string.IsNullOrWhiteSpace(source) && source.Contains("Tally", StringComparison.OrdinalIgnoreCase)
                ? "Alter" : "Create").AppendLine("\">");
        builder.Append("      <NAME>").Append(Escape(actualName)).AppendLine("</NAME>");
        if (actualParent.Length > 0)
            builder.Append("      <PARENT>").Append(Escape(actualParent)).AppendLine("</PARENT>");
        builder.Append("    </").Append(tag).AppendLine(">");
        builder.AppendLine("  </TALLYMESSAGE>");
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
