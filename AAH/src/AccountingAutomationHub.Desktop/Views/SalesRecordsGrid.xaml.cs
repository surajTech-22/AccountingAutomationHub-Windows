using System.Collections;
using System.Collections.Specialized;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Domain.Gst;

namespace AccountingAutomationHub.Desktop.Views;

public partial class SalesRecordsGrid : UserControl
{
    private INotifyCollectionChanged? _observableSource;

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(SalesRecordsGrid),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public SalesRecordsGrid()
    {
        InitializeComponent();
        SearchFilterBar.Attach("GstDate", RecordsGrid);
        Loaded += (_, _) => RebuildColumns();
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SalesRecordsGrid)d;
        if (control._observableSource is not null)
            control._observableSource.CollectionChanged -= control.OnCollectionChanged;
        control._observableSource = e.NewValue as INotifyCollectionChanged;
        if (control._observableSource is not null)
            control._observableSource.CollectionChanged += control.OnCollectionChanged;
        control.RebuildColumns();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(RebuildColumns));

    private void RebuildColumns()
    {
        if (RecordsGrid is null) return;
        var rows = ItemsSource?.Cast<object>().OfType<SalesImportRecord>().ToArray() ?? [];
        RecordsGrid.Columns.Clear();

        var rateOptions = GstRateCatalog.AllowedRates
            .Concat(rows.Select(row => decimal.Round(row.Rate, 4, MidpointRounding.AwayFromZero)))
            .Distinct()
            .OrderBy(rate => rate)
            .ToArray();

        foreach (var descriptor in ColumnDescriptors)
        {
            if (!descriptor.Core && !rows.Any(row => HasValue(row, descriptor.PropertyName)))
                continue;
            RecordsGrid.Columns.Add(CreateColumn(descriptor, rateOptions));
        }
        SearchFilterBar.RefreshFields();
    }

    private static DataGridColumn CreateColumn(ColumnDescriptor descriptor, IReadOnlyList<decimal> rateOptions)
    {
        if (descriptor.Kind == ColumnKind.GstRate)
        {
            return new DataGridComboBoxColumn
            {
                Header = descriptor.Header,
                Width = descriptor.Width,
                ItemsSource = rateOptions,
                SelectedItemBinding = new Binding(descriptor.PropertyName)
                {
                    Mode = descriptor.ReadOnly ? BindingMode.OneWay : BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                },
                IsReadOnly = descriptor.ReadOnly
            };
        }

        if (descriptor.Kind == ColumnKind.Boolean)
        {
            return new DataGridCheckBoxColumn
            {
                Header = descriptor.Header,
                Width = descriptor.Width,
                Binding = new Binding(descriptor.PropertyName)
                {
                    Mode = descriptor.ReadOnly ? BindingMode.OneWay : BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                },
                IsReadOnly = descriptor.ReadOnly
            };
        }

        var binding = new Binding(descriptor.PropertyName)
        {
            Mode = descriptor.ReadOnly ? BindingMode.OneWay : BindingMode.TwoWay,
            UpdateSourceTrigger = descriptor.ReadOnly ? UpdateSourceTrigger.Default : UpdateSourceTrigger.LostFocus
        };
        if (descriptor.Kind == ColumnKind.Date)
            binding.StringFormat = "dd-MM-yyyy";
        else if (descriptor.Kind == ColumnKind.Decimal)
            binding.StringFormat = descriptor.Format ?? "N2";

        return new DataGridTextColumn
        {
            Header = descriptor.Header,
            Width = descriptor.Width,
            Binding = binding,
            IsReadOnly = descriptor.ReadOnly
        };
    }

    private static bool HasValue(SalesImportRecord row, string propertyName)
    {
        var property = typeof(SalesImportRecord).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property is null) return false;
        var value = property.GetValue(row);
        return value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            decimal number => Math.Abs(number) > 0.000001m,
            int number => number != 0,
            long number => number != 0,
            bool flag => flag,
            DateTime date => date != default,
            _ => true
        };
    }


    private void RecordsGrid_CellEditEnding(
        object sender,
        DataGridCellEditEndingEventArgs e)
    {
        if (DataContext is not SalesImportViewModel { CanEditSales: true }) return;
        if (e.EditAction != DataGridEditAction.Commit ||
            e.Row.Item is not SalesImportRecord row)
            return;

        Dispatcher.BeginInvoke(
            () =>
            {
                RecordsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                RecordsGrid.CommitEdit(DataGridEditingUnit.Row, true);

                if (DataContext is SalesImportViewModel viewModel)
                    viewModel.QueueAutomaticSave(row);
            },
            DispatcherPriority.Background);
    }

    private enum ColumnKind { Text, Decimal, Date, Boolean, GstRate }
    private sealed record ColumnDescriptor(string Header, string PropertyName, double Width, bool Core = false, bool ReadOnly = false, ColumnKind Kind = ColumnKind.Text, string? Format = null);

    private static readonly IReadOnlyList<ColumnDescriptor> ColumnDescriptors =
    [
        new("Destination", nameof(SalesImportRecord.Destination), 105, true, true),
        new("Client GSTIN", nameof(SalesImportRecord.ClientGstin), 145, true, true),
        new("GSTIN", nameof(SalesImportRecord.CustomerGstin), 145, true),
        new("Customer", nameof(SalesImportRecord.CustomerName), 220, true),
        new("Document Type", nameof(SalesImportRecord.DocumentType), 120, true),
        new("Document No.", nameof(SalesImportRecord.DocumentNumber), 150, true),
        new("Document Date", nameof(SalesImportRecord.DocumentDate), 112, true, false, ColumnKind.Date),
        new("GST Date", nameof(SalesImportRecord.GstDate), 108, false, false, ColumnKind.Date),
        new("Document Value", nameof(SalesImportRecord.DocumentValue), 110, true, false, ColumnKind.Decimal),
        new("Rate %", nameof(SalesImportRecord.Rate), 82, true, false, ColumnKind.GstRate, "0.##"),
        new("Mixed GST", nameof(SalesImportRecord.IsEffectiveMixedGstRate), 86, false, true, ColumnKind.Boolean),
        new("Rate Source", nameof(SalesImportRecord.GstRateEvidence), 240, false, true),
        new("Taxable", nameof(SalesImportRecord.TaxableValue), 105, true, false, ColumnKind.Decimal),
        new("IGST", nameof(SalesImportRecord.Igst), 86, false, false, ColumnKind.Decimal),
        new("CGST", nameof(SalesImportRecord.Cgst), 86, false, false, ColumnKind.Decimal),
        new("SGST", nameof(SalesImportRecord.Sgst), 86, false, false, ColumnKind.Decimal),
        new("Cess", nameof(SalesImportRecord.Cess), 82, false, false, ColumnKind.Decimal),
        new("Round Off", nameof(SalesImportRecord.RoundOff), 88, false, false, ColumnKind.Decimal),
        new("Sales Ledger", nameof(SalesImportRecord.SalesLedger), 155, true),
        new("IGST Ledger", nameof(SalesImportRecord.IgstLedger), 170),
        new("CGST Ledger", nameof(SalesImportRecord.CgstLedger), 170),
        new("SGST Ledger", nameof(SalesImportRecord.SgstLedger), 170),

        new("Item Name", nameof(SalesImportRecord.ItemName), 190),
        new("HSN Code", nameof(SalesImportRecord.HsnCode), 100),
        new("Actual Quantity", nameof(SalesImportRecord.ActualQuantity), 110, false, false, ColumnKind.Decimal, "0.####"),
        new("Unit", nameof(SalesImportRecord.Unit), 80),
        new("Item Rate", nameof(SalesImportRecord.ItemRate), 100, false, false, ColumnKind.Decimal, "0.####"),
        new("Billed Quantity", nameof(SalesImportRecord.BilledQuantity), 110, false, false, ColumnKind.Decimal, "0.####"),
        new("Item Amount", nameof(SalesImportRecord.ItemAmount), 110, false, false, ColumnKind.Decimal),
        new("Item Sales Ledger", nameof(SalesImportRecord.ItemSalesLedger), 165),
        new("Accounting Amount", nameof(SalesImportRecord.AccountingAmount), 125, false, false, ColumnKind.Decimal),
        new("Godown", nameof(SalesImportRecord.Godown), 110),
        new("Batch Name", nameof(SalesImportRecord.BatchName), 120),
        new("Batch Actual Qty", nameof(SalesImportRecord.BatchActualQuantity), 115, false, false, ColumnKind.Decimal, "0.####"),
        new("Batch Billed Qty", nameof(SalesImportRecord.BatchBilledQuantity), 115, false, false, ColumnKind.Decimal, "0.####"),
        new("Batch Amount", nameof(SalesImportRecord.BatchAmount), 105, false, false, ColumnKind.Decimal),
        new("Discount %", nameof(SalesImportRecord.DiscountPercent), 90, false, false, ColumnKind.Decimal, "0.####"),
        new("Discount Amount", nameof(SalesImportRecord.DiscountAmount), 110, false, false, ColumnKind.Decimal),
        new("Order No", nameof(SalesImportRecord.OrderNumber), 110),
        new("Tracking No", nameof(SalesImportRecord.TrackingNumber), 115),
        new("Manufacturing Date", nameof(SalesImportRecord.ManufacturingDate), 125, false, false, ColumnKind.Date),
        new("Expiry Date", nameof(SalesImportRecord.ExpiryDate), 112, false, false, ColumnKind.Date),
        new("Item Description", nameof(SalesImportRecord.ItemDescription), 190),
        new("Part No / SKU", nameof(SalesImportRecord.PartNumberSku), 120),
        new("MRP", nameof(SalesImportRecord.Mrp), 90, false, false, ColumnKind.Decimal),
        new("Serial / Lot No", nameof(SalesImportRecord.SerialLotNumber), 120),
        new("Cost Centre", nameof(SalesImportRecord.CostCentre), 120),
        new("Price Level", nameof(SalesImportRecord.PriceLevel), 100),
        new("Alternate Quantity", nameof(SalesImportRecord.AlternateQuantity), 120, false, false, ColumnKind.Decimal, "0.####"),
        new("Alternate Unit", nameof(SalesImportRecord.AlternateUnit), 100),
        new("Cess Rate %", nameof(SalesImportRecord.CessRate), 90, false, false, ColumnKind.Decimal, "0.####"),
        new("Free Quantity", nameof(SalesImportRecord.FreeQuantity), 100, false, false, ColumnKind.Decimal, "0.####"),
        new("Delivery Note No", nameof(SalesImportRecord.DeliveryNoteNumber), 125),
        new("Buyer Order No", nameof(SalesImportRecord.BuyerOrderNumber), 125),
        new("Buyer Order Date", nameof(SalesImportRecord.BuyerOrderDate), 120, false, false, ColumnKind.Date),
        new("Place of Supply", nameof(SalesImportRecord.PlaceOfSupply), 130, true),
        new("Narration", nameof(SalesImportRecord.Narration), 220),
        new("Import Source", nameof(SalesImportRecord.ImportSource), 120),

        new("Party Mailing Name", nameof(SalesImportRecord.PartyMailingName), 170),
        new("Party State", nameof(SalesImportRecord.PartyState), 115),
        new("Party Country", nameof(SalesImportRecord.PartyCountry), 115),
        new("Party Pincode", nameof(SalesImportRecord.PartyPincode), 100),
        new("Party Address 1", nameof(SalesImportRecord.PartyAddressLine1), 180),
        new("Party Address 2", nameof(SalesImportRecord.PartyAddressLine2), 180),
        new("Party Address 3", nameof(SalesImportRecord.PartyAddressLine3), 180),
        new("Buyer Name", nameof(SalesImportRecord.BuyerName), 160),
        new("Buyer Address 1", nameof(SalesImportRecord.BuyerAddressLine1), 180),
        new("Buyer Address 2", nameof(SalesImportRecord.BuyerAddressLine2), 180),
        new("Buyer Address 3", nameof(SalesImportRecord.BuyerAddressLine3), 180),
        new("Consignee Name", nameof(SalesImportRecord.ConsigneeName), 160),
        new("Consignee GSTIN", nameof(SalesImportRecord.ConsigneeGstin), 145),
        new("Consignee State", nameof(SalesImportRecord.ConsigneeState), 120),
        new("Consignee Country", nameof(SalesImportRecord.ConsigneeCountry), 120),
        new("Consignee Pincode", nameof(SalesImportRecord.ConsigneePincode), 115),
        new("Consignee Address 1", nameof(SalesImportRecord.ConsigneeAddressLine1), 180),
        new("Consignee Address 2", nameof(SalesImportRecord.ConsigneeAddressLine2), 180),
        new("Consignee Address 3", nameof(SalesImportRecord.ConsigneeAddressLine3), 180),
        new("Payment Due Days", nameof(SalesImportRecord.PaymentDueDays), 105, false, false, ColumnKind.Decimal, "0"),
        new("Delivery Note Date", nameof(SalesImportRecord.DeliveryNoteDate), 120, false, false, ColumnKind.Date),
        new("Delivery Note No 2", nameof(SalesImportRecord.DeliveryNoteNumber2), 130),
        new("Delivery Note Date 2", nameof(SalesImportRecord.DeliveryNoteDate2), 125, false, false, ColumnKind.Date),
        new("Buyer Order Reference", nameof(SalesImportRecord.BuyerOrderReference), 145),
        new("Buyer Order Terms", nameof(SalesImportRecord.BuyerOrderTerms), 145),
        new("Dispatch Document No", nameof(SalesImportRecord.DispatchDocumentNumber), 145),
        new("Dispatch Through", nameof(SalesImportRecord.DispatchThrough), 120),
        new("Final Destination", nameof(SalesImportRecord.FinalDestination), 135),
        new("Destination Country", nameof(SalesImportRecord.DestinationCountry), 125),
        new("Vehicle / Vessel No", nameof(SalesImportRecord.VehicleVesselNumber), 130),
        new("LR / Bill of Lading No", nameof(SalesImportRecord.LrBillOfLadingNumber), 150),
        new("LR / Bill of Lading Date", nameof(SalesImportRecord.LrBillOfLadingDate), 145, false, false, ColumnKind.Date),
        new("Place of Receipt", nameof(SalesImportRecord.PlaceOfReceipt), 125),
        new("Port of Loading", nameof(SalesImportRecord.PortOfLoading), 120),
        new("Port of Discharge", nameof(SalesImportRecord.PortOfDischarge), 125),
        new("Shipping Bill No", nameof(SalesImportRecord.ShippingBillNumber), 125),
        new("Shipping Bill Date", nameof(SalesImportRecord.ShippingBillDate), 125, false, false, ColumnKind.Date),
        new("Port Code", nameof(SalesImportRecord.PortCode), 100),
        new("Check Post", nameof(SalesImportRecord.CheckPost), 100),

        new("Company GST Registration", nameof(SalesImportRecord.CompanyGstRegistration), 165),
        new("Company GST Registration Type", nameof(SalesImportRecord.CompanyGstRegistrationType), 180),
        new("Company GST State", nameof(SalesImportRecord.CompanyGstState), 135),
        new("Tax Unit Name", nameof(SalesImportRecord.TaxUnitName), 110),
        new("Invoice Date Time", nameof(SalesImportRecord.InvoiceDateTime), 135, false, false, ColumnKind.Date),
        new("IRN", nameof(SalesImportRecord.Irn), 250),
        new("IRN Ack No", nameof(SalesImportRecord.IrnAcknowledgementNumber), 150),
        new("IRN Ack Date", nameof(SalesImportRecord.IrnAcknowledgementDate), 120, false, false, ColumnKind.Date),
        new("Bill To Place", nameof(SalesImportRecord.BillToPlace), 115),
        new("Ship To Place", nameof(SalesImportRecord.ShipToPlace), 115),
        new("Dispatch From Name", nameof(SalesImportRecord.DispatchFromName), 150),
        new("Dispatch From Address 1", nameof(SalesImportRecord.DispatchFromAddressLine1), 180),
        new("Dispatch From Address 2", nameof(SalesImportRecord.DispatchFromAddressLine2), 180),
        new("Dispatch From Address 3", nameof(SalesImportRecord.DispatchFromAddressLine3), 180),
        new("Dispatch From State", nameof(SalesImportRecord.DispatchFromState), 130),
        new("Dispatch From Pincode", nameof(SalesImportRecord.DispatchFromPincode), 130),
        new("Dispatch From Place", nameof(SalesImportRecord.DispatchFromPlace), 130),
        new("E-Way Bill Number", nameof(SalesImportRecord.EWayBillNumber), 140),
        new("E-Way Bill Date", nameof(SalesImportRecord.EWayBillDate), 120, false, false, ColumnKind.Date),
        new("Transporter ID", nameof(SalesImportRecord.TransporterId), 120),
        new("Transporter Name", nameof(SalesImportRecord.TransporterName), 150),
        new("Transport Mode", nameof(SalesImportRecord.TransportMode), 110),
        new("Transport Document No", nameof(SalesImportRecord.TransportDocumentNumber), 145),
        new("Transport Document Date", nameof(SalesImportRecord.TransportDocumentDate), 145, false, false, ColumnKind.Date),
        new("E-Way Vehicle Number", nameof(SalesImportRecord.EWayVehicleNumber), 145),
        new("Vehicle Type", nameof(SalesImportRecord.VehicleType), 105),
        new("Type of Goods Gold", nameof(SalesImportRecord.TypeOfGoodsGold), 125),
        new("Exclude From E-Way Bill", nameof(SalesImportRecord.ExcludeFromEWayBill), 130, false, false, ColumnKind.Boolean),

        new("Section", nameof(SalesImportRecord.SourceSection), 95),
        new("Amendment", nameof(SalesImportRecord.IsAmendment), 85, false, false, ColumnKind.Boolean),
        new("Original Document", nameof(SalesImportRecord.OriginalDocumentNumber), 145),
        new("Return Period", nameof(SalesImportRecord.ReturnPeriod), 100),
        new("Source File", nameof(SalesImportRecord.SourceFile), 210, true, true)
    ];
}
