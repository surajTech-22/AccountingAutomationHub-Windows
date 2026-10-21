using System.IO;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using AccountingAutomationHub.Infrastructure.Tally;

namespace AccountingAutomationHub.Desktop.Services;

public sealed record TallyLiveCompanyLocation(string CompanyName, string DataRoot, string CompanyNumber);

/// <summary>
/// Optional read-only current-company physical location collection. R6 verified
/// the Collection/Object LOCALFORMULA format on TallyPrime (the old Report/Form
/// request returned an empty XML element). This is metadata, not sync authority.
/// </summary>
public sealed class TallyLiveCompanyLocationReader
{
    public async Task<TallyLiveCompanyLocation?> ReadLiveCompanyLocationAsync(
        string companyName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyName)) return null;
        var expectedName = companyName.Trim();
        var escaped = System.Security.SecurityElement.Escape(expectedName);
        // R6: this exact collection idiom successfully returned TRAIL, F:\\, 100999.
        // Use one request to keep the five-second Tally authority cycle responsive.
        var request = $"""
        <ENVELOPE>
          <HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Collection</TYPE><ID>AAHFix11LiveLocation</ID></HEADER>
          <BODY><DESC>
            <STATICVARIABLES>
              <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
              <SVCURRENTCOMPANY>{escaped}</SVCURRENTCOMPANY>
            </STATICVARIABLES>
            <TDL><TDLMESSAGE>
              <OBJECT NAME="AAHFix11LiveLocationObject">
                <LOCALFORMULA>CurrentCompany:##SVCurrentCompany</LOCALFORMULA>
                <LOCALFORMULA>CurrentPath:##SVCurrentPath</LOCALFORMULA>
                <LOCALFORMULA>DirectNumber:$CompanyNumber:Company:##SVCurrentCompany</LOCALFORMULA>
              </OBJECT>
              <COLLECTION NAME="AAHFix11LiveLocation"><OBJECTS>AAHFix11LiveLocationObject</OBJECTS></COLLECTION>
            </TDLMESSAGE></TDL>
          </DESC></BODY>
        </ENVELOPE>
        """;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        using var lease = await TallyRequestGate.AcquireAsync(deadline.Token);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var content = new StringContent(request, Encoding.UTF8, "text/xml");
        using var reply = await client.PostAsync("http://127.0.0.1:9000/", content, deadline.Token);
        reply.EnsureSuccessStatusCode();
        var xml = await reply.Content.ReadAsStringAsync(deadline.Token);
        var document = XDocument.Parse(xml.TrimStart('\uFEFF'));
        var node = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName.Equals("AAHFIX11LIVELOCATIONOBJECT", StringComparison.OrdinalIgnoreCase));
        if (node is null)
            throw new InvalidDataException("Live Tally location collection object missing from the XML response; existing path retained.");

        string Field(string name) => (node.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty).Trim();
        var actualName = Field("CURRENTCOMPANY");
        var dataRoot = Field("CURRENTPATH");
        var number = Field("DIRECTNUMBER");
        if (!actualName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Live Tally location returned another company; existing path retained.");
        if (dataRoot.Length == 0)
            throw new InvalidDataException("Live Tally location has no SVCurrentPath; existing path retained.");
        if (number.Length == 0 || number.Length > 64 || !number.All(char.IsDigit))
            throw new InvalidDataException("Live Tally location has no usable numeric CompanyNumber; existing path retained.");
        return new TallyLiveCompanyLocation(actualName, dataRoot, number);
    }
}
