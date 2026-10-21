using System.Globalization;
using System.Security;
using System.Text;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>
/// Final Tally XML serialization authority shared by Bank and the other accounting modules.
/// The envelope and line hierarchy intentionally follow the proven Excel VBA modXMLWriter.
/// Business modules remain responsible for producing normalized voucher/master data.
/// </summary>
internal static class TallyExcelParityXmlWriter
{
    internal static void BeginAllMastersImport(StringBuilder builder, string companyName)
    {
        builder.AppendLine("<ENVELOPE>");
        builder.AppendLine("  <HEADER>");
        builder.AppendLine("    <TALLYREQUEST>Import Data</TALLYREQUEST>");
        builder.AppendLine("  </HEADER>");
        builder.AppendLine("  <BODY>");
        builder.AppendLine("    <IMPORTDATA>");
        builder.AppendLine("      <REQUESTDESC>");
        builder.AppendLine("        <REPORTNAME>All Masters</REPORTNAME>");
        builder.AppendLine("        <STATICVARIABLES>");
        builder.Append("          <SVCURRENTCOMPANY>").Append(Xml(companyName)).AppendLine("</SVCURRENTCOMPANY>");
        builder.AppendLine("          <IMPORTDUPS>@@DUPIGNORE</IMPORTDUPS>");
        builder.AppendLine("        </STATICVARIABLES>");
        builder.AppendLine("      </REQUESTDESC>");
        builder.AppendLine("      <REQUESTDATA>");
    }


    /// <summary>
    /// Tally.ERP 9 documented Data-import envelope. Bank voucher import uses this
    /// strict envelope so masters and vouchers are never mixed under one report ID.
    /// </summary>
    internal static void BeginErp9DataImport(StringBuilder builder, string companyName, string importId)
    {
        builder.AppendLine("<ENVELOPE>");
        builder.AppendLine("  <HEADER>");
        builder.AppendLine("    <VERSION>1</VERSION>");
        builder.AppendLine("    <TALLYREQUEST>Import</TALLYREQUEST>");
        builder.AppendLine("    <TYPE>Data</TYPE>");
        builder.Append("    <ID>").Append(Xml(importId)).AppendLine("</ID>");
        builder.AppendLine("  </HEADER>");
        builder.AppendLine("  <BODY>");
        builder.AppendLine("    <DESC>");
        builder.AppendLine("      <STATICVARIABLES>");
        builder.Append("        <SVCURRENTCOMPANY>").Append(Xml(companyName)).AppendLine("</SVCURRENTCOMPANY>");
        builder.AppendLine("      </STATICVARIABLES>");
        builder.AppendLine("    </DESC>");
        builder.AppendLine("    <DATA>");
    }

    internal static void EndErp9DataImport(StringBuilder builder)
    {
        builder.AppendLine("    </DATA>");
        builder.AppendLine("  </BODY>");
        builder.AppendLine("</ENVELOPE>");
    }

    /// <summary>
    /// Minimal ERP 9-safe accounting Ledger master. Only the documented identity
    /// and parent are sent in the blocking preflight request; optional tax/currency
    /// fields cannot prevent dependent vouchers from posting.
    /// </summary>
    internal static void AppendErp9MinimalLedgerMaster(
        StringBuilder builder,
        string name,
        string parent,
        string action = "Create")
    {
        name = (name ?? string.Empty).Trim();
        parent = (parent ?? string.Empty).Trim();
        action = string.IsNullOrWhiteSpace(action) ? "Create" : action.Trim();
        if (name.Length == 0 || parent.Length == 0) return;

        builder.AppendLine("      <TALLYMESSAGE>");
        builder.Append("        <LEDGER NAME=\"").Append(Xml(name)).Append("\" Action=\"")
            .Append(Xml(action)).AppendLine("\">");
        AppendTag(builder, "NAME", name, 10);
        AppendTag(builder, "PARENT", parent, 10);
        builder.AppendLine("        </LEDGER>");
        builder.AppendLine("      </TALLYMESSAGE>");
    }

    /// <summary>
    /// Minimal accounting voucher shape documented by Tally for voucher-mode
    /// Payment/Receipt/Contra import. This is Bank-specific so the richer Excel
    /// writer used by Purchase/Sales/Journal is left untouched.
    /// </summary>
    internal static void AppendErp9BankVoucher(
        StringBuilder builder,
        string voucherType,
        DateTime date,
        string reference,
        string narration,
        IReadOnlyCollection<TallyErp9BankLedgerLine> lines)
    {
        builder.AppendLine("      <TALLYMESSAGE>");
        builder.AppendLine("        <VOUCHER>");
        AppendTag(builder, "DATE", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 10);
        AppendTag(builder, "NARRATION", narration, 10);
        AppendTag(builder, "VOUCHERTYPENAME", voucherType, 10);
        // REFERENCE is a native Voucher method and is retained only when the Bank
        // statement provides one; it is also used by the live duplicate verifier.
        AppendTag(builder, "REFERENCE", reference, 10);

        // Keep the ERP 9 Bank voucher deliberately minimal. These are the exact
        // accounting-entry tags used by Tally's ERP 9 Payment-voucher XML case study,
        // with optional REFERENCE retained as a documented Voucher method.
        // Prime-only/richer optional flags are intentionally not emitted here.
        foreach (var line in lines)
        {
            var signed = line.IsDebit ? -Math.Abs(line.Amount) : Math.Abs(line.Amount);
            builder.AppendLine("          <ALLLEDGERENTRIES.LIST>");
            AppendTag(builder, "LEDGERNAME", line.LedgerName, 12);
            AppendTag(builder, "ISDEEMEDPOSITIVE", line.IsDebit ? "Yes" : "No", 12);
            AppendTag(builder, "AMOUNT", signed.ToString("0.00", CultureInfo.InvariantCulture), 12);
            builder.AppendLine("          </ALLLEDGERENTRIES.LIST>");
        }

        builder.AppendLine("        </VOUCHER>");
        builder.AppendLine("      </TALLYMESSAGE>");
    }

    internal static void EndAllMastersImport(StringBuilder builder)
    {
        builder.AppendLine("      </REQUESTDATA>");
        builder.AppendLine("    </IMPORTDATA>");
        builder.AppendLine("  </BODY>");
        builder.AppendLine("</ENVELOPE>");
    }


    internal static void BeginVoucher(
        StringBuilder builder,
        string voucherType,
        string view,
        DateTime date,
        DateTime? referenceDate = null,
        string voucherNumber = "",
        string reference = "",
        string partyLedger = "")
    {
        builder.AppendLine("  <TALLYMESSAGE>");
        builder.Append("    <VOUCHER VCHTYPE=\"").Append(Xml(voucherType)).Append("\" ACTION=\"Create\" OBJVIEW=\"")
            .Append(Xml(view)).AppendLine("\">");
        AppendTag(builder, "DATE", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 6);
        if (referenceDate is DateTime refDate)
            AppendTag(builder, "REFERENCEDATE", refDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 6);
        AppendTag(builder, "VOUCHERTYPENAME", voucherType, 6);
        AppendTag(builder, "VOUCHERNUMBER", voucherNumber, 6);
        AppendTag(builder, "REFERENCE", reference, 6);
        if (!string.IsNullOrWhiteSpace(partyLedger))
        {
            AppendTag(builder, "PARTYNAME", partyLedger, 6);
            AppendTag(builder, "PARTYLEDGERNAME", partyLedger, 6);
            AppendTag(builder, "BASICBASEPARTYNAME", partyLedger, 6);
        }
    }

    internal static void AppendVoucherModeAndNarration(
        StringBuilder builder,
        string view,
        string entryMode,
        bool isInvoice,
        bool affectsStock,
        bool hasCostCentre,
        string narration,
        bool? eWayBillExcluded = null,
        bool eWayBillApplicableOverride = false)
    {
        AppendTag(builder, "PERSISTEDVIEW", view, 6);
        AppendTag(builder, "VCHENTRYMODE", entryMode, 6);
        AppendTag(builder, "ISINVOICE", isInvoice ? "Yes" : "No", 6);
        if (isInvoice) AppendTag(builder, "OBJVIEW", view, 6);
        AppendTag(builder, "ISVATDUTYPAID", "Yes", 6);
        AppendTag(builder, "ISCOSTCENTRE", hasCostCentre ? "Yes" : "No", 6);
        AppendTag(builder, "ISOPTIONAL", "No", 6);
        AppendTag(builder, "AFFECTSSTOCK", affectsStock ? "Yes" : "No", 6);
        if (eWayBillExcluded.HasValue)
            AppendTag(builder, "ISEWAYBILLEXCLUDED", eWayBillExcluded.Value ? "Yes" : "No", 6);
        if (eWayBillApplicableOverride) AppendTag(builder, "OVRDNEWAYBILLAPPLICABILITY", "Yes", 6);
        AppendTag(builder, "NARRATION", narration, 6);
    }

    internal static void EndVoucher(StringBuilder builder)
    {
        builder.AppendLine("    </VOUCHER>");
        builder.AppendLine("  </TALLYMESSAGE>");
    }

    internal static void AppendLedgerLine(
        StringBuilder builder,
        string ledgerName,
        decimal amount,
        bool isDebit,
        bool isParty,
        bool invoiceLedgerList = false,
        string billReference = "",
        bool reverseChargePurchaseLine = false,
        string reverseChargeStoredNature = "")
    {
        var signed = isDebit ? -Math.Abs(amount) : Math.Abs(amount);
        var listTag = invoiceLedgerList ? "LEDGERENTRIES.LIST" : "ALLLEDGERENTRIES.LIST";
        builder.Append("      <").Append(listTag).AppendLine(">");
        AppendTag(builder, "LEDGERNAME", ledgerName, 8);
        if (reverseChargePurchaseLine)
        {
            AppendTag(builder, "GSTOVRDNSTOREDNATURE", reverseChargeStoredNature, 8);
            AppendTag(builder, "GSTOVRDNTAXABILITY", "Taxable", 8);
            AppendTag(builder, "GSTOVRDNISREVCHARGEAPPL", "Yes", 8);
        }
        AppendTag(builder, "ISDEEMEDPOSITIVE", isDebit ? "Yes" : "No", 8);
        if (invoiceLedgerList) AppendTag(builder, "LEDGERFROMITEM", "No", 8);
        AppendTag(builder, "ISPARTYLEDGER", isParty ? "Yes" : "No", 8);
        AppendTag(builder, "REMOVEZEROENTRIES", "No", 8);
        AppendTag(builder, "ISLASTDEEMEDPOSITIVE", isDebit ? "Yes" : "No", 8);
        AppendTag(builder, "AMOUNT", signed.ToString("0.00", CultureInfo.InvariantCulture), 8);

        // Excel modXMLWriter: Sundry Debtor party lines in Sales/Credit Note invoice mode
        // carry a New Ref bill allocation. Cash parties are excluded by the caller.
        if (invoiceLedgerList && isParty && !string.IsNullOrWhiteSpace(billReference))
        {
            builder.AppendLine("        <BILLALLOCATIONS.LIST>");
            AppendTag(builder, "NAME", billReference, 10);
            AppendTag(builder, "BILLTYPE", "New Ref", 10);
            AppendTag(builder, "AMOUNT", signed.ToString("0.00", CultureInfo.InvariantCulture), 10);
            builder.AppendLine("        </BILLALLOCATIONS.LIST>");
        }
        builder.Append("      </").Append(listTag).AppendLine(">");
    }

    internal static void AppendMinimalLedgerMaster(StringBuilder builder, string name, string parent, string action = "Create")
    {
        name = (name ?? string.Empty).Trim();
        parent = (parent ?? string.Empty).Trim();
        if (name.Length == 0 || parent.Length == 0) return;
        action = string.IsNullOrWhiteSpace(action) ? "Create" : action.Trim();
        builder.AppendLine("  <TALLYMESSAGE xmlns:UDF=\"TallyUDF\">");
        builder.Append("    <LEDGER NAME=\"").Append(Xml(name)).Append("\" RESERVEDNAME=\"\" ACTION=\"")
            .Append(Xml(action)).AppendLine("\">");
        AppendTag(builder, "NAME", name, 6);
        AppendTag(builder, "PARENT", parent, 6);
        builder.AppendLine("    </LEDGER>");
        builder.AppendLine("  </TALLYMESSAGE>");
    }

    /// <summary>
    /// Exact Bank Ledger Create payload captured from the user's working Excel/VBA
    /// Combined_Import XML. The workbook deliberately emits the full Tally Bank
    /// Ledger master shape before dependent vouchers. Keep this creation payload
    /// stable; optional bank metadata may be altered separately after vouchers.
    /// </summary>
    internal static void AppendWorkbookBankLedgerCreateMaster(StringBuilder builder, string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return;
        var block = """
        <TALLYMESSAGE xmlns:UDF="TallyUDF">
        <LEDGER NAME="__AAH_BANK_LEDGER_NAME__" RESERVEDNAME="" ACTION="Create">
        <OLDAUDITENTRYIDS.LIST TYPE="Number">      </OLDAUDITENTRYIDS.LIST>
        <PARENT>Bank Accounts</PARENT>
        <TAXTYPE>Others</TAXTYPE>
        <TAXCLASSIFICATIONNAME>&#4; Not Applicable</TAXCLASSIFICATIONNAME>
        <GSTTYPE>&#4; Not Applicable</GSTTYPE>
        <APPROPRIATEFOR>&#4; Not Applicable</APPROPRIATEFOR>
        <SERVICECATEGORY>&#4; Not Applicable</SERVICECATEGORY>
        <EXCISELEDGERCLASSIFICATION>&#4; Not Applicable</EXCISELEDGERCLASSIFICATION>
        <EXCISEDUTYTYPE>&#4; Not Applicable</EXCISEDUTYTYPE>
        <EXCISENATUREOFPURCHASE>&#4; Not Applicable</EXCISENATUREOFPURCHASE>
        <LEDGERFBTCATEGORY>&#4; Not Applicable</LEDGERFBTCATEGORY>
        <ISBANKINGLEDGER>Yes</ISBANKINGLEDGER>
        <BANKNAME></BANKNAME>
        <BANKBRANCHNAME></BANKBRANCHNAME>
        <ACCOUNTNUMBER></ACCOUNTNUMBER>
        <IFSCODE></IFSCODE>
        <ACCOUNTTYPE></ACCOUNTTYPE>
        <ODLIMIT></ODLIMIT>
        <DRAWINGPOWER></DRAWINGPOWER>
        <EMAIL></EMAIL>
        <WEBSITE></WEBSITE>
        <INCOMETAXNUMBER></INCOMETAXNUMBER>
        <PINCODE></PINCODE>
        <LEDGERPHONE></LEDGERPHONE>
        <LEDGERMOBILE></LEDGERMOBILE>
        <STATENAME></STATENAME>
        <ISBILLWISEON>No</ISBILLWISEON>
        <ISCOSTCENTRESON>No</ISCOSTCENTRESON>
        <ISINTERESTON>No</ISINTERESTON>
        <ALLOWINMOBILE>No</ALLOWINMOBILE>
        <ISCOSTTRACKINGON>No</ISCOSTTRACKINGON>
        <ISBENEFICIARYCODEON>No</ISBENEFICIARYCODEON>
        <ISEXPORTONVCHCREATE>No</ISEXPORTONVCHCREATE>
        <PLASINCOMEEXPENSE>No</PLASINCOMEEXPENSE>
        <ISUPDATINGTARGETID>No</ISUPDATINGTARGETID>
        <ISDELETED>No</ISDELETED>
        <ISSECURITYONWHENENTERED>No</ISSECURITYONWHENENTERED>
        <ASORIGINAL>Yes</ASORIGINAL>
        <ISCONDENSED>No</ISCONDENSED>
        <AFFECTSSTOCK>No</AFFECTSSTOCK>
        <ISRATEINCLUSIVEVAT>No</ISRATEINCLUSIVEVAT>
        <FORPAYROLL>No</FORPAYROLL>
        <ISABCENABLED>No</ISABCENABLED>
        <ISCREDITDAYSCHKON>No</ISCREDITDAYSCHKON>
        <INTERESTONBILLWISE>No</INTERESTONBILLWISE>
        <OVERRIDEINTEREST>No</OVERRIDEINTEREST>
        <OVERRIDEADVINTEREST>No</OVERRIDEADVINTEREST>
        <USEFORVAT>No</USEFORVAT>
        <IGNORETDSEXEMPT>No</IGNORETDSEXEMPT>
        <ISTCSAPPLICABLE>No</ISTCSAPPLICABLE>
        <ISTDSAPPLICABLE>No</ISTDSAPPLICABLE>
        <ISFBTAPPLICABLE>No</ISFBTAPPLICABLE>
        <ISGSTAPPLICABLE>No</ISGSTAPPLICABLE>
        <ISEXCISEAPPLICABLE>No</ISEXCISEAPPLICABLE>
        <ISTDSEXPENSE>No</ISTDSEXPENSE>
        <ISEDLIAPPLICABLE>No</ISEDLIAPPLICABLE>
        <ISRELATEDPARTY>No</ISRELATEDPARTY>
        <USEFORESIELIGIBILITY>No</USEFORESIELIGIBILITY>
        <ISINTERESTINCLLASTDAY>No</ISINTERESTINCLLASTDAY>
        <APPROPRIATETAXVALUE>No</APPROPRIATETAXVALUE>
        <ISBEHAVEASDUTY>No</ISBEHAVEASDUTY>
        <INTERESTINCLDAYOFADDITION>No</INTERESTINCLDAYOFADDITION>
        <INTERESTINCLDAYOFDEDUCTION>No</INTERESTINCLDAYOFDEDUCTION>
        <ISOTHTERRITORYASSESSEE>No</ISOTHTERRITORYASSESSEE>
        <IGNOREMISMATCHWITHWARNING>No</IGNOREMISMATCHWITHWARNING>
        <USEASNOTIONALBANK>No</USEASNOTIONALBANK>
        <BEHAVEASPAYMENTGATEWAY>No</BEHAVEASPAYMENTGATEWAY>
        <OVERRIDECREDITLIMIT>No</OVERRIDECREDITLIMIT>
        <ISAGAINSTFORMC>No</ISAGAINSTFORMC>
        <ISCHEQUEPRINTINGENABLED>Yes</ISCHEQUEPRINTINGENABLED>
        <ISPAYUPLOAD>No</ISPAYUPLOAD>
        <ISPAYBATCHONLYSAL>No</ISPAYBATCHONLYSAL>
        <ISBNFCODESUPPORTED>No</ISBNFCODESUPPORTED>
        <ALLOWEXPORTWITHERRORS>No</ALLOWEXPORTWITHERRORS>
        <CONSIDERPURCHASEFOREXPORT>No</CONSIDERPURCHASEFOREXPORT>
        <ISTRANSPORTER>No</ISTRANSPORTER>
        <ISECASHLEDGER>No</ISECASHLEDGER>
        <USEFORNOTIONALITC>No</USEFORNOTIONALITC>
        <ISECOMMOPERATOR>No</ISECOMMOPERATOR>
        <OVERRIDEBASEDONREALIZATION>No</OVERRIDEBASEDONREALIZATION>
        <ISECDIFFINSDATE>No</ISECDIFFINSDATE>
        <SHOWINPAYSLIP>No</SHOWINPAYSLIP>
        <USEFORGRATUITY>No</USEFORGRATUITY>
        <ISTDSPROJECTED>No</ISTDSPROJECTED>
        <ISSALARYMULFILE>No</ISSALARYMULFILE>
        <FORSERVICETAX>No</FORSERVICETAX>
        <ISINPUTCREDIT>No</ISINPUTCREDIT>
        <ISEXEMPTED>No</ISEXEMPTED>
        <ISABATEMENTAPPLICABLE>No</ISABATEMENTAPPLICABLE>
        <ISSTXPARTY>No</ISSTXPARTY>
        <ISSTXNONREALIZEDTYPE>No</ISSTXNONREALIZEDTYPE>
        <USEFORKKC>No</USEFORKKC>
        <USEFORSBC>No</USEFORSBC>
        <ISUSEDFORCVD>No</ISUSEDFORCVD>
        <LEDBELONGSTONONTAXABLE>No</LEDBELONGSTONONTAXABLE>
        <ISEXCISEMERCHANTEXPORTER>No</ISEXCISEMERCHANTEXPORTER>
        <ISPARTYEXEMPTED>No</ISPARTYEXEMPTED>
        <ISSEZPARTY>No</ISSEZPARTY>
        <TDSDEDUCTEEISSPECIALRATE>No</TDSDEDUCTEEISSPECIALRATE>
        <ISECHEQUESUPPORTED>No</ISECHEQUESUPPORTED>
        <ISEDDSUPPORTED>No</ISEDDSUPPORTED>
        <HASECHEQUEDELIVERYMODE>No</HASECHEQUEDELIVERYMODE>
        <HASECHEQUEDELIVERYTO>No</HASECHEQUEDELIVERYTO>
        <HASECHEQUEPRINTLOCATION>No</HASECHEQUEPRINTLOCATION>
        <HASECHEQUEPAYABLELOCATION>No</HASECHEQUEPAYABLELOCATION>
        <HASECHEQUEBANKLOCATION>No</HASECHEQUEBANKLOCATION>
        <HASEDDDELIVERYMODE>No</HASEDDDELIVERYMODE>
        <HASEDDDELIVERYTO>No</HASEDDDELIVERYTO>
        <HASEDDPRINTLOCATION>No</HASEDDPRINTLOCATION>
        <HASEDDPAYABLELOCATION>No</HASEDDPAYABLELOCATION>
        <HASEDDBANKLOCATION>No</HASEDDBANKLOCATION>
        <ISEBANKINGENABLED>No</ISEBANKINGENABLED>
        <ISEXPORTFILEENCRYPTED>No</ISEXPORTFILEENCRYPTED>
        <ISBATCHENABLED>No</ISBATCHENABLED>
        <ISPRODUCTCODEBASED>No</ISPRODUCTCODEBASED>
        <HASEDDCITY>No</HASEDDCITY>
        <HASECHEQUECITY>No</HASECHEQUECITY>
        <ISFILENAMEFORMATSUPPORTED>No</ISFILENAMEFORMATSUPPORTED>
        <HASCLIENTCODE>No</HASCLIENTCODE>
        <PAYINSISBATCHAPPLICABLE>No</PAYINSISBATCHAPPLICABLE>
        <PAYINSISFILENUMAPP>No</PAYINSISFILENUMAPP>
        <ISSALARYTRANSGROUPEDFORBRS>No</ISSALARYTRANSGROUPEDFORBRS>
        <ISEBANKINGSUPPORTED>No</ISEBANKINGSUPPORTED>
        <ISSCBUAE>No</ISSCBUAE>
        <ISBANKSTATUSAPP>No</ISBANKSTATUSAPP>
        <ISSALARYGROUPED>No</ISSALARYGROUPED>
        <USEFORPURCHASETAX>No</USEFORPURCHASETAX>
        <BANKISRECONCILEPERFECTMATCHES>No</BANKISRECONCILEPERFECTMATCHES>
        <ISPYMTADVONLINE>No</ISPYMTADVONLINE>
        <ISPYMTADVCCENABLED>No</ISPYMTADVCCENABLED>
        <ISINCLUDEPYMTADVBILLWISE>No</ISINCLUDEPYMTADVBILLWISE>
        <UAEISPARTYPEPPOLREGISTERED>No</UAEISPARTYPEPPOLREGISTERED>
        <AUDITED>No</AUDITED>
        <SORTPOSITION> 1000</SORTPOSITION>
        <SERVICETAXDETAILS.LIST>      </SERVICETAXDETAILS.LIST>
        <LBTREGNDETAILS.LIST>      </LBTREGNDETAILS.LIST>
        <VATDETAILS.LIST>      </VATDETAILS.LIST>
        <SALESTAXCESSDETAILS.LIST>      </SALESTAXCESSDETAILS.LIST>
        <GSTDETAILS.LIST>      </GSTDETAILS.LIST>
        <HSNDETAILS.LIST>      </HSNDETAILS.LIST>
        <MSMEREGISTRATIONDETAILS.LIST>      </MSMEREGISTRATIONDETAILS.LIST>
        <LANGUAGENAME.LIST>
        <NAME.LIST TYPE="String">
        <NAME>__AAH_BANK_LEDGER_NAME__</NAME>
        </NAME.LIST>
        <LANGUAGEID> 1033</LANGUAGEID>
        </LANGUAGENAME.LIST>
        <XBRLDETAIL.LIST>      </XBRLDETAIL.LIST>
        <AUDITDETAILS.LIST>      </AUDITDETAILS.LIST>
        <SCHVIDETAILS.LIST>      </SCHVIDETAILS.LIST>
        <EXCISETARIFFDETAILS.LIST>      </EXCISETARIFFDETAILS.LIST>
        <TCSCATEGORYDETAILS.LIST>      </TCSCATEGORYDETAILS.LIST>
        <TDSCATEGORYDETAILS.LIST>      </TDSCATEGORYDETAILS.LIST>
        <SLABPERIOD.LIST>      </SLABPERIOD.LIST>
        <GRATUITYPERIOD.LIST>      </GRATUITYPERIOD.LIST>
        <ADDITIONALCOMPUTATIONS.LIST>      </ADDITIONALCOMPUTATIONS.LIST>
        <EXCISEJURISDICTIONDETAILS.LIST>      </EXCISEJURISDICTIONDETAILS.LIST>
        <EXCLUDEDTAXATIONS.LIST>      </EXCLUDEDTAXATIONS.LIST>
        <BANKALLOCATIONS.LIST>      </BANKALLOCATIONS.LIST>
        <PAYMENTDETAILS.LIST>      </PAYMENTDETAILS.LIST>
        <BANKEXPORTFORMATS.LIST>      </BANKEXPORTFORMATS.LIST>
        <TRANSFERMODELIMITDETAILS.LIST>      </TRANSFERMODELIMITDETAILS.LIST>
        <BILLALLOCATIONS.LIST>      </BILLALLOCATIONS.LIST>
        <INTERESTCOLLECTION.LIST>      </INTERESTCOLLECTION.LIST>
        <LEDGERCLOSINGVALUES.LIST>      </LEDGERCLOSINGVALUES.LIST>
        <LEDGERAUDITCLASS.LIST>      </LEDGERAUDITCLASS.LIST>
        <OLDAUDITENTRIES.LIST>      </OLDAUDITENTRIES.LIST>
        <TDSEXEMPTIONRULES.LIST>      </TDSEXEMPTIONRULES.LIST>
        <DEDUCTINSAMEVCHRULES.LIST>      </DEDUCTINSAMEVCHRULES.LIST>
        <LOWERDEDUCTION.LIST>      </LOWERDEDUCTION.LIST>
        <STXABATEMENTDETAILS.LIST>      </STXABATEMENTDETAILS.LIST>
        <LEDMULTIADDRESSLIST.LIST>      </LEDMULTIADDRESSLIST.LIST>
        <STXTAXDETAILS.LIST>      </STXTAXDETAILS.LIST>
        <CHEQUERANGE.LIST>      </CHEQUERANGE.LIST>
        <DEFAULTVCHCHEQUEDETAILS.LIST>      </DEFAULTVCHCHEQUEDETAILS.LIST>
        <ACCOUNTAUDITENTRIES.LIST>      </ACCOUNTAUDITENTRIES.LIST>
        <AUDITENTRIES.LIST>      </AUDITENTRIES.LIST>
        <BRSIMPORTEDINFO.LIST>      </BRSIMPORTEDINFO.LIST>
        <AUTOBRSCONFIGS.LIST>      </AUTOBRSCONFIGS.LIST>
        <BANKURENTRIES.LIST>      </BANKURENTRIES.LIST>
        <DEFAULTCHEQUEDETAILS.LIST>      </DEFAULTCHEQUEDETAILS.LIST>
        <DEFAULTOPENINGCHEQUEDETAILS.LIST>      </DEFAULTOPENINGCHEQUEDETAILS.LIST>
        <CANCELLEDPAYALLOCATIONS.LIST>      </CANCELLEDPAYALLOCATIONS.LIST>
        <ECHEQUEPRINTLOCATION.LIST>      </ECHEQUEPRINTLOCATION.LIST>
        <ECHEQUEPAYABLELOCATION.LIST>      </ECHEQUEPAYABLELOCATION.LIST>
        <EDDPRINTLOCATION.LIST>      </EDDPRINTLOCATION.LIST>
        <EDDPAYABLELOCATION.LIST>      </EDDPAYABLELOCATION.LIST>
        <AVAILABLETRANSACTIONTYPES.LIST>      </AVAILABLETRANSACTIONTYPES.LIST>
        <LEDPAYINSCONFIGS.LIST>      </LEDPAYINSCONFIGS.LIST>
        <TYPECODEDETAILS.LIST>      </TYPECODEDETAILS.LIST>
        <FIELDVALIDATIONDETAILS.LIST>      </FIELDVALIDATIONDETAILS.LIST>
        <INPUTCRALLOCS.LIST>      </INPUTCRALLOCS.LIST>
        <TCSMETHODOFCALCULATION.LIST>      </TCSMETHODOFCALCULATION.LIST>
        <LEDGSTREGDETAILS.LIST>      </LEDGSTREGDETAILS.LIST>
        <LEDMAILINGDETAILS.LIST>      </LEDMAILINGDETAILS.LIST>
        <GSTRECONPREFIXSUFFIXDETAILS.LIST>      </GSTRECONPREFIXSUFFIXDETAILS.LIST>
        <CONTACTDETAILS.LIST>      </CONTACTDETAILS.LIST>
        <UAEEINVOICEDETAILS.LIST>      </UAEEINVOICEDETAILS.LIST>
        <GSTCLASSFNIGSTRATES.LIST>      </GSTCLASSFNIGSTRATES.LIST>
        <EXTARIFFDUTYHEADDETAILS.LIST>      </EXTARIFFDUTYHEADDETAILS.LIST>
        <TEMPGSTITEMSLABRATES.LIST>      </TEMPGSTITEMSLABRATES.LIST>
        <LEDGSTADDRESS.LIST>      </LEDGSTADDRESS.LIST>
        <VOUCHERTYPEPRODUCTCODES.LIST>      </VOUCHERTYPEPRODUCTCODES.LIST>
        <LEDADDRESS.LIST>      </LEDADDRESS.LIST>
        <DEFMULTIPLETOPHONENO.LIST>      </DEFMULTIPLETOPHONENO.LIST>
        </LEDGER>
        </TALLYMESSAGE>
        """;
        builder.AppendLine(block.Replace(
            "__AAH_BANK_LEDGER_NAME__",
            Xml(name),
            StringComparison.Ordinal));
    }

    internal static void AppendLedgerMaster(StringBuilder builder, TallyExcelLedgerMaster spec)
    {
        var action = string.IsNullOrWhiteSpace(spec.Action) ? "Create" : spec.Action.Trim();
        var partyLedger = IsPartyGroup(spec.Parent);
        var cashLedger = spec.Parent.Equals("Cash-in-Hand", StringComparison.OrdinalIgnoreCase) ||
                         spec.Parent.Equals("Cash-in-hand", StringComparison.OrdinalIgnoreCase);
        var suspenseLedger = spec.Parent.Equals("Suspense A/c", StringComparison.OrdinalIgnoreCase) ||
                             spec.Parent.Equals("Suspense A/C", StringComparison.OrdinalIgnoreCase);
        var gstin = NormalizeGstin(spec.Gstin);
        var state = spec.State.Trim();
        var country = spec.Country.Trim();
        var mailing = spec.MailingName.Trim();
        if (partyLedger)
        {
            if (string.IsNullOrWhiteSpace(country)) country = "India";
            if (string.IsNullOrWhiteSpace(mailing)) mailing = spec.Name;
        }

        builder.AppendLine("  <TALLYMESSAGE xmlns:UDF=\"TallyUDF\">");
        builder.Append("    <LEDGER NAME=\"").Append(Xml(spec.Name)).Append("\" RESERVEDNAME=\"\" ACTION=\"")
            .Append(Xml(action)).AppendLine("\">");
        AppendAddressList(builder, spec.Address1, spec.Address2, spec.Address3);
        AppendTag(builder, "NAME", spec.Name, 6);
        if (partyLedger || cashLedger) AppendTag(builder, "STARTINGFROM", spec.ApplicableFrom.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 6);
        AppendTag(builder, "PARENT", spec.Parent, 6);
        AppendTag(builder, "TAXTYPE", "Others", 6);
        AppendWorkbookNotApplicableTag(builder, "TAXCLASSIFICATIONNAME", 6);
        if (state.Length > 0)
        {
            AppendTag(builder, "PRIORSTATENAME", state, 6);
            AppendTag(builder, "STATENAME", state, 6);
        }
        AppendTag(builder, "COUNTRYOFRESIDENCE", cashLedger ? "India" : country, 6);
        AppendTag(builder, "MAILINGNAME", mailing, 6);
        AppendTag(builder, "PINCODE", spec.Pincode, 6);
        if (suspenseLedger) AppendWorkbookNotApplicableTag(builder, "LEDADDLALLOCTYPE", 6);
        AppendWorkbookNotApplicableTag(builder, "GSTTYPE", 6);
        AppendWorkbookNotApplicableTag(builder, "APPROPRIATEFOR", 6);
        AppendWorkbookNotApplicableTag(builder, "SERVICECATEGORY", 6);
        AppendWorkbookNotApplicableTag(builder, "EXCISELEDGERCLASSIFICATION", 6);
        AppendWorkbookNotApplicableTag(builder, "EXCISEDUTYTYPE", 6);
        AppendWorkbookNotApplicableTag(builder, "EXCISENATUREOFPURCHASE", 6);
        AppendWorkbookNotApplicableTag(builder, "LEDGERFBTCATEGORY", 6);
        if (cashLedger)
        {
            AppendTag(builder, "DEFAULTTRANSFERMODE", "RTGS", 6);
            AppendWorkbookNotApplicableTag(builder, "BANKPERFECTMATCHCONFIG", 6);
        }
        AppendTag(builder, "ISBILLWISEON", partyLedger ? "Yes" : "No", 6);
        AppendTag(builder, "ISCOSTCENTRESON", IsCostCentreGroup(spec.Parent) ? "Yes" : "No", 6);
        if (partyLedger)
        {
            AppendTag(builder, "GSTREGISTRATIONTYPE", gstin.Length == 15 ? "Regular" : "Unregistered/Consumer", 6);
            if (gstin.Length == 15)
            {
                AppendTag(builder, "VATDEALERTYPE", "Regular", 6);
                AppendTag(builder, "PARTYGSTIN", gstin, 6);
            }
        }
        if (spec.IsBank)
        {
            // Keep the rich bank metadata used by the workbook, but normalize the
            // short Hub account-type codes before sending them to Tally. Bank-ledger
            // existence is bootstrapped separately with a minimal Ledger master so a
            // bank-metadata incompatibility can never block voucher creation.
            AppendTag(builder, "ISBANKINGLEDGER", "Yes", 6);
            AppendTag(builder, "BANKNAME", spec.BankName, 6);
            AppendTag(builder, "BANKBRANCHNAME", spec.BankBranchName, 6);
            AppendTag(builder, "ACCOUNTNUMBER", spec.AccountNumber, 6);
            AppendTag(builder, "IFSCODE", spec.IfscCode, 6);
            AppendTag(builder, "ACCOUNTTYPE", NormalizeBankAccountType(spec.AccountType), 6);
            AppendTag(builder, "DEFAULTTRANSFERMODE", "RTGS", 6);
            AppendWorkbookNotApplicableTag(builder, "BANKPERFECTMATCHCONFIG", 6);
            AppendTag(builder, "ISCHEQUEPRINTINGENABLED", "Yes", 6);
            AppendTag(builder, "USEASNOTIONALBANK", "No", 6);
            AppendTag(builder, "BEHAVEASPAYMENTGATEWAY", "No", 6);
            AppendTag(builder, "ISEBANKINGENABLED", "No", 6);
            if (spec.OdLimit.HasValue) AppendTag(builder, "ODLIMIT", spec.OdLimit.Value.ToString("0.##", CultureInfo.InvariantCulture), 6);
            if (spec.DrawingPower.HasValue) AppendTag(builder, "DRAWINGPOWER", spec.DrawingPower.Value.ToString("0.##", CultureInfo.InvariantCulture), 6);
        }
        if (partyLedger)
        {
            builder.AppendLine("      <LEDGSTREGDETAILS.LIST>");
            AppendTag(builder, "APPLICABLEFROM", spec.ApplicableFrom.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 8);
            AppendTag(builder, "GSTREGISTRATIONTYPE", gstin.Length == 15 ? "Regular" : "Unregistered/Consumer", 8);
            AppendTag(builder, "ISOTHTERRITORYASSESSEE", "No", 8);
            AppendTag(builder, "CONSIDERPURCHASEFOREXPORT", "No", 8);
            AppendTag(builder, "ISTRANSPORTER", "No", 8);
            AppendTag(builder, "ISCOMMONPARTY", "No", 8);
            builder.AppendLine("      </LEDGSTREGDETAILS.LIST>");
            if (mailing.Length > 0 || country.Length > 0)
            {
                builder.AppendLine("      <LEDMAILINGDETAILS.LIST>");
                AppendTag(builder, "APPLICABLEFROM", spec.ApplicableFrom.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 8);
                AppendTag(builder, "MAILINGNAME", mailing, 8);
                AppendTag(builder, "COUNTRY", country, 8);
                builder.AppendLine("      </LEDMAILINGDETAILS.LIST>");
            }
        }
        builder.AppendLine("    </LEDGER>");
        builder.AppendLine("  </TALLYMESSAGE>");
    }

    /// <summary>
    /// Excel/Tally does not use the literal text "Not Applicable" for these system
    /// enumerations. The working workbook emits the Tally system sentinel
    /// "&#4; Not Applicable". Escaping it through SecurityElement would turn it into
    /// &amp;#4; and make Tally look for a real Tax Classification master named
    /// "Not Applicable", which is exactly the Import Exceptions failure seen in Tally.
    /// </summary>
    internal static void AppendWorkbookNotApplicableTag(StringBuilder builder, string tag, int indent)
    {
        builder.Append(' ', indent).Append('<').Append(tag).Append(">&#4; Not Applicable</")
            .Append(tag).AppendLine(">");
    }

    internal static void AppendTag(StringBuilder builder, string tag, string? value, int indent)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        builder.Append(' ', indent).Append('<').Append(tag).Append('>')
            .Append(Xml(value.Trim())).Append("</").Append(tag).AppendLine(">");
    }

    internal static string Xml(string? value)
    {
        // XML 1.0 permits TAB/LF/CR and characters U+0020 and above. Bank
        // narrations can originate in external files, so strip only illegal C0
        // controls before escaping. Surrogate pairs remain untouched.
        var source = value ?? string.Empty;
        var sanitized = new string(source
            .Where(character =>
                character == '\t' ||
                character == '\n' ||
                character == '\r' ||
                character >= ' ')
            .ToArray());
        return SecurityElement.Escape(sanitized) ?? string.Empty;
    }

    private static void AppendAddressList(StringBuilder builder, params string[] lines)
    {
        var values = lines.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Trim()).ToArray();
        if (values.Length == 0) return;
        builder.AppendLine("      <ADDRESS.LIST>");
        foreach (var value in values) AppendTag(builder, "ADDRESS", value, 8);
        builder.AppendLine("      </ADDRESS.LIST>");
    }

    private static string NormalizeBankAccountType(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized switch
        {
            "CA" or "CURRENT" or "CURRENT ACCOUNT" => "Current Account",
            "SB" or "SA" or "SAVING" or "SAVINGS" or "SAVING ACCOUNT" or "SAVINGS ACCOUNT" => "Saving Account",
            "OD" or "OVERDRAFT" or "OVER DRAFT" or "OVERDRAFT ACCOUNT" or "OVER DRAFT ACCOUNT" => "Over Draft",
            "CC" or "OCC" or "CASH CREDIT" or "CASH CREDIT ACCOUNT" => "Cash Credit",
            _ => value?.Trim() ?? string.Empty
        };
    }

    private static string NormalizeGstin(string? value)
    {
        var normalized = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return normalized.Length == 15 ? normalized : string.Empty;
    }

    private static bool IsPartyGroup(string group) =>
        group.Trim().Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase) ||
        group.Trim().Equals("Sundry Creditors", StringComparison.OrdinalIgnoreCase);

    private static bool IsCostCentreGroup(string group) =>
        group.Contains("Sales", StringComparison.OrdinalIgnoreCase) ||
        group.Contains("Purchase", StringComparison.OrdinalIgnoreCase) ||
        group.Contains("Expense", StringComparison.OrdinalIgnoreCase) ||
        group.Contains("Income", StringComparison.OrdinalIgnoreCase);
}

internal sealed record TallyErp9BankLedgerLine(
    string LedgerName,
    decimal Amount,
    bool IsDebit);

internal sealed record TallyExcelLedgerMaster(
    string Name,
    string Parent,
    DateTime ApplicableFrom,
    string Action = "Create",
    string Gstin = "",
    string State = "",
    string Country = "",
    string Pincode = "",
    string MailingName = "",
    string Address1 = "",
    string Address2 = "",
    string Address3 = "",
    bool IsBank = false,
    string BankName = "",
    string BankBranchName = "",
    string AccountNumber = "",
    string IfscCode = "",
    string AccountType = "",
    decimal? OdLimit = null,
    decimal? DrawingPower = null);
