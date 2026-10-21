using System.Security;
using System.Text;
using System.Xml.Linq;
using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Infrastructure.Tally;

internal static class TallyErp9ManualGroupImport
{
    internal static string Build(string companyName, IEnumerable<MasterRecord> groups)
    {
        var builder = new StringBuilder();
        TallyExcelParityXmlWriter.BeginAllMastersImport(builder, companyName);
        foreach (var group in groups
                     .Where(group => group.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                                     !string.IsNullOrWhiteSpace(group.Name))
                     .GroupBy(group => TallyMasterKey(group.Name), StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            var name = group.Name.Trim();
            var parent = string.IsNullOrWhiteSpace(group.Parent) ? "Primary" : group.Parent.Trim();
            builder.AppendLine("      <TALLYMESSAGE>");
            builder.Append("        <GROUP NAME=\"").Append(Xml(name)).AppendLine("\" Action=\"Create\">");
            builder.Append("          <NAME>").Append(Xml(name)).AppendLine("</NAME>");
            builder.Append("          <PARENT>").Append(Xml(parent)).AppendLine("</PARENT>");
            builder.AppendLine("        </GROUP>");
            builder.AppendLine("      </TALLYMESSAGE>");
        }
        TallyExcelParityXmlWriter.EndAllMastersImport(builder);
        return builder.ToString();
    }

    internal static void Validate(
        string xml,
        string companyName,
        IEnumerable<MasterRecord> expectedGroups)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException("ERP 9 Group XML has no ENVELOPE root.");
        if (!root.Name.LocalName.Equals("ENVELOPE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ERP 9 Group XML root must be ENVELOPE.");

        string ElementValue(XElement parent, string name) =>
            parent.Elements().FirstOrDefault(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? string.Empty;

        var header = root.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("HEADER", StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException("ERP 9 Group XML HEADER is missing.");
        if (!ElementValue(header, "TALLYREQUEST").Equals("Import Data", StringComparison.OrdinalIgnoreCase) ||
            header.Elements().Count() != 1)
            throw new InvalidOperationException("ERP 9 Group XML HEADER must use the documented Import Data / All Masters master envelope.");

        var importData = root.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("BODY", StringComparison.OrdinalIgnoreCase))?
            .Elements().FirstOrDefault(element => element.Name.LocalName.Equals("IMPORTDATA", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("ERP 9 Group XML IMPORTDATA block is missing.");
        var requestDesc = importData.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("REQUESTDESC", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("ERP 9 Group XML REQUESTDESC is missing.");
        if (!ElementValue(requestDesc, "REPORTNAME").Equals("All Masters", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ERP 9 Group XML REPORTNAME must be All Masters.");
        var requestData = importData.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("REQUESTDATA", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("ERP 9 Group XML REQUESTDATA is missing.");

        var selectedCompany = requestDesc.Descendants()
            .FirstOrDefault(element => element.Name.LocalName.Equals("SVCURRENTCOMPANY", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? string.Empty;
        if (!selectedCompany.Equals(companyName.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"ERP 9 Group XML targets '{selectedCompany}', expected '{companyName}'.");

        var actual = requestData.Descendants()
            .Where(element => element.Name.LocalName.Equals("GROUP", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var expected = expectedGroups
            .Where(group => group.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(group.Name))
            .GroupBy(group => TallyMasterKey(group.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (actual.Length != expected.Length)
            throw new InvalidOperationException($"ERP 9 Group XML contains {actual.Length} GROUP nodes; expected {expected.Length}.");

        foreach (var group in expected)
        {
            var name = group.Name.Trim();
            var parent = string.IsNullOrWhiteSpace(group.Parent) ? "Primary" : group.Parent.Trim();
            var node = actual.FirstOrDefault(element =>
                TallyMasterKey((string?)element.Attribute("NAME") ?? ElementValue(element, "NAME"))
                    .Equals(TallyMasterKey(name), StringComparison.OrdinalIgnoreCase));
            if (node is null)
                throw new InvalidOperationException($"ERP 9 Group XML is missing Group '{name}'.");
            var action = ((string?)node.Attribute("Action") ?? (string?)node.Attribute("ACTION") ?? string.Empty).Trim();
            if (!action.Equals("Create", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"ERP 9 Group '{name}' must use Action=Create.");
            if (!ElementValue(node, "NAME").Equals(name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"ERP 9 Group '{name}' NAME tag does not match its master name.");
            if (!ElementValue(node, "PARENT").Equals(parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"ERP 9 Group '{name}' parent must be '{parent}'.");
        }
    }

    private static string TallyMasterKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Trim().Normalize(NormalizationForm.FormKC);
        return new string(normalized.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }

    private static string Xml(string? value) => SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
}
