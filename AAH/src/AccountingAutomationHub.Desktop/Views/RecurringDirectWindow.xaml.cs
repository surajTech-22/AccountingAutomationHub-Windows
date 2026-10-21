using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AccountingAutomationHub.Application.Governance;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Masters;

namespace AccountingAutomationHub.Desktop.Views;

public sealed class RecurringLedgerChoice
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Group { get; init; } = "";
    public string Display { get; init; } = "";
    public override string ToString() => Display;
}

public sealed class RecurringDirectLineEditor : INotifyPropertyChanged
{
    private long _masterId;
    private long _commonMasterId;
    private string _ledger="", _group="", _common="", _drCr="Dr", _amount="0.00";
    private bool _balance;
    public long MasterId { get=>_masterId; set=>Set(ref _masterId,value); }
    public long CommonMasterId { get=>_commonMasterId; set=>Set(ref _commonMasterId,value); }
    public string Ledger { get=>_ledger; set=>Set(ref _ledger,value); }
    public string Group { get=>_group; set=>Set(ref _group,value); }
    public string CommonLedger { get=>_common; set=>Set(ref _common,value); }
    public string DrCr { get=>_drCr; set=>Set(ref _drCr,value); }
    public string AmountText { get=>_amount; set=>Set(ref _amount,value); }
    public bool IsBalanceLine { get=>_balance; set=>Set(ref _balance,value); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field,T value,[CallerMemberName]string? name=null)
    { if(EqualityComparer<T>.Default.Equals(field,value))return;field=value;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name)); }
}

public sealed class RecurringPatternDateEditor : INotifyPropertyChanged
{
    private DateTime? _date;
    public string Label { get; init; } = "";
    public DateTime Minimum { get; init; }
    public DateTime Maximum { get; init; }
    public DateTime? Date { get=>_date; set { if(_date==value)return;_date=value;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Date))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record RecurringDirectPreviewRow(DateTime Date,string VoucherType,decimal Amount,string Details,string Closing);
public sealed record RecurringDirectAllocationPreviewRow(DateTime Date,string Source,decimal Amount,bool IsManual);

// New fields are trailing and optional so R20/R23 selections remain readable.
public sealed record RecurringDirectSelection(
    string PlanId, string AmountMode, string Amount, DateTime? Start, DateTime? End,
    string Frequency, string Narration, IReadOnlyList<DateTime> Dates,
    IReadOnlyList<RecurringDirectLine> Lines, IReadOnlyList<decimal>? FrozenAmounts, int AmountSchema = 0,
    int Times = 1, string DateMode = "Fixed", IReadOnlyList<int>? FixedPeriodOffsets = null,
    string MaximumMode = "Per Voucher", string MaximumAmount = "",
    IReadOnlyList<RecurringDirectAllocation>? ManualAllocations = null);

public partial class RecurringDirectWindow : Window
{
    private readonly RecurringDirectService _service;
    private readonly long _clientId;
    private readonly string _fy;
    private readonly string _module;
    private readonly Action _refresh;
    private readonly IReadOnlyList<DateTime> _holidays;
    private readonly DateTime _fyStart, _fyEnd;
    private string _planId=Guid.NewGuid().ToString("N");
    private bool _updating;
    private IReadOnlyList<DateTime> _dates=[];
    private IReadOnlyList<decimal>? _frozen;
    private RecurringDirectPlan? _previewPlan;
    private IReadOnlyList<RecurringDirectOccurrence> _preview=[];
    private RecurringDirectAllocationResult? _allocationResult;
    private readonly List<RecurringDirectAllocation> _manualAllocations=[];
    private int _scheduleSeed=Random.Shared.Next();
    private int _allocationSeed=Random.Shared.Next();

    public ObservableCollection<RecurringDirectLineEditor> Lines { get; }=[];
    public ObservableCollection<RecurringLedgerChoice> LedgerChoices { get; }=[];
    public ObservableCollection<RecurringDirectPreviewRow> PreviewRows { get; }=[];
    public ObservableCollection<RecurringDirectAllocationPreviewRow> AllocationRows { get; }=[];
    public ObservableCollection<string> FrequencyChoices { get; }=[];
    public ObservableCollection<int> TimesChoices { get; }=[];
    public ObservableCollection<RecurringPatternDateEditor> PatternDates { get; }=[];

    public RecurringDirectWindow(long clientId,string financialYear,string module,
        ApplicationPreferenceService preferences,Action refresh)
    {
        if(clientId<=0 || (module!="Cash" && module!="Journal"))throw new InvalidOperationException("Open a valid Client/FY first.");
        _clientId=clientId;_fy=financialYear;_module=module;_refresh=refresh;
        var first=int.Parse(financialYear.Split('-')[0],CultureInfo.InvariantCulture);
        _fyStart=new DateTime(first,4,1);_fyEnd=_fyStart.AddYears(1).AddDays(-1);
        _service=new RecurringDirectService(new SqliteBankingRepository(),new SqliteMasterRepository(),new SqliteJournalRepository(),new SqliteGovernanceRepository());
        _holidays=new ClientSchedulePreferenceService(preferences).GetManualHolidays(clientId,financialYear);
        InitializeComponent();
        DataContext=this;
        Heading.Text=$"{module} Recurring · {financialYear}";
        CommonColumn.Visibility=module=="Cash"?Visibility.Visible:Visibility.Collapsed;
        BalanceButton.Visibility=RemoveBalanceButton.Visibility=module=="Journal"?Visibility.Visible:Visibility.Collapsed;
        LineHelp.Text=module=="Cash"
            ? "Each row has Ledger, Group, Dr/Cr, Amount and Cash/Common Ledger. Existing Groups prefill. Dr creates a Payment and Cr a Receipt against Cash. Multiple lines of the same direction and Cash Ledger form one voucher."
            : "At least two lines are required. Change Dr/Cr or add lines as needed. Balance Voucher locks the balancing line's amount and direction; only its Ledger can be changed. Unmatched Journals cannot be created.";
        _updating=true;
        StartPicker.DisplayDateStart=EndPicker.DisplayDateStart=_fyStart;
        StartPicker.DisplayDateEnd=EndPicker.DisplayDateEnd=_fyEnd;
        ManualAllocationDatePicker.DisplayDateStart=_fyStart;
        ManualAllocationDatePicker.DisplayDateEnd=_fyEnd;
        var today=DateTime.Today;
        StartPicker.SelectedDate=today<_fyStart?_fyStart:today>_fyEnd?_fyStart:today;
        EndPicker.SelectedDate=_fyEnd;
        LoadMasters();
        AddLine();
        if(module=="Journal"){AddLine();Lines[1].DrCr="Cr";}
        RefreshScheduleControls("Monthly",1,"Fixed",null);
        _updating=false;
        RestoreSelection();
        UpdateTotals();UpdateMode();UpdateDatesSummary();UpdateScheduleHelp();
    }

    private static decimal ParseAmount(string value)
    {
        if(!decimal.TryParse((value??"").Replace(",",""),NumberStyles.Number,CultureInfo.InvariantCulture,out var amount) ||
            amount<0m || decimal.Round(amount,2,MidpointRounding.AwayFromZero)!=amount)
            throw new InvalidOperationException("Enter a nonnegative amount with at most two decimal places.");
        return amount;
    }
    private static string Money(decimal value)=>value.ToString("0.00",CultureInfo.InvariantCulture);
    private static string SelectedText(ComboBox box)=>box.SelectedItem switch
    {
        ComboBoxItem item=>item.Content?.ToString()??"",
        string value=>value,
        int value=>value.ToString(CultureInfo.InvariantCulture),
        _=>box.Text??""
    };
    private bool IsLump=>AmountModeBox.SelectedIndex==1;
    private bool IsCash=>_module=="Cash";
    private int SelectedTimes=>FrequencyBox.SelectedItem is string frequency && frequency=="Daily"?1:TimesBox.SelectedItem is int value?value:1;
    private string SelectedDateMode=>FrequencyBox.SelectedItem is string frequency && frequency=="Daily"?"Fixed":SelectedText(DateModeBox)=="Random"?"Random":"Fixed";

    private void LoadMasters()
    {
        // Keep existing choice objects: clearing/replacing ItemsSource makes WPF
        // write empty SelectedValue bindings back into every accounting row.
        var known=LedgerChoices.Select(x=>x.Id).ToHashSet();
        foreach(var master in _service.GetMasters(_clientId,_fy).Where(x=>x.Type.Equals("Ledger",StringComparison.OrdinalIgnoreCase)&&!x.IsArchived).OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase))
            if(known.Add(master.PersistentId))
                LedgerChoices.Add(new RecurringLedgerChoice{Id=master.PersistentId,Name=master.Name,Group=master.Parent,Display=$"{master.Name}  •  {master.AahMasterId}"});
    }
    private RecurringLedgerChoice? Choice(long id)=>LedgerChoices.FirstOrDefault(x=>x.Id==id);
    private static bool IsCashGroup(string group)=>group.Equals("Cash-in-Hand",StringComparison.OrdinalIgnoreCase)||group.Equals("Bank Accounts",StringComparison.OrdinalIgnoreCase);
    private static string SuggestDirection(string group)
    {
        if(group.Equals("Sundry Debtors",StringComparison.OrdinalIgnoreCase))return "Cr";
        if(group.Equals("Sundry Creditors",StringComparison.OrdinalIgnoreCase))return "Dr";
        if(group.Contains("Expense",StringComparison.OrdinalIgnoreCase)||group.Equals("Purchase Accounts",StringComparison.OrdinalIgnoreCase)||
           group.Equals("Fixed Assets",StringComparison.OrdinalIgnoreCase)||group.Contains("Investment",StringComparison.OrdinalIgnoreCase)||
           group.Contains("Loans and Advances (Asset)",StringComparison.OrdinalIgnoreCase))return "Dr";
        return "Cr";
    }
    private void ApplyMaster(RecurringDirectLineEditor line,bool suggest)
    {
        var choice=Choice(line.MasterId);
        line.Ledger=choice?.Name??"";line.Group=choice?.Group??"";
        if(suggest && choice is not null && !line.IsBalanceLine)line.DrCr=SuggestDirection(choice.Group);
        var common=Choice(line.CommonMasterId);
        line.CommonLedger=common?.Name??"";
    }
    private void Ledger_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_updating||sender is not ComboBox{DataContext:RecurringDirectLineEditor line})return;
        _updating=true;ApplyMaster(line,true);_updating=false;Changed();
    }
    private void Common_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_updating||sender is not ComboBox{DataContext:RecurringDirectLineEditor line})return;
        _updating=true;ApplyMaster(line,false);_updating=false;Changed();
    }
    private RecurringDirectLineEditor AddLine()
    {
        var common=_service.GetCashSettings(_clientId,_fy).DefaultCommonLedger;
        var commonId=IsCash?LedgerChoices.FirstOrDefault(x=>x.Name.Equals(common,StringComparison.OrdinalIgnoreCase))?.Id??0:0;
        var line=new RecurringDirectLineEditor{CommonMasterId=commonId,CommonLedger=common,DrCr="Dr",AmountText="0.00"};
        line.PropertyChanged+=LineChanged;
        var index=Lines.ToList().FindIndex(x=>x.IsBalanceLine);
        if(index>=0)Lines.Insert(index,line);else Lines.Add(line);
        return line;
    }
    private void AddLine_Click(object sender,RoutedEventArgs e){var line=AddLine();Changed();LinesGrid.SelectedItem=line;LinesGrid.ScrollIntoView(line);}
    private void DeleteLine_Click(object sender,RoutedEventArgs e)
    {
        if(LinesGrid.SelectedItem is not RecurringDirectLineEditor line)return;
        if(Lines.Count<=(IsCash?1:2)){StatusText.Text="Keep at least the required number of lines.";return;}
        line.PropertyChanged-=LineChanged;Lines.Remove(line);Changed();
    }
    private void LineChanged(object? sender,PropertyChangedEventArgs e){if(!_updating)Changed();}

    private void Changed(bool preserveFrozen=false)
    {
        _previewPlan=null;_preview=[];_allocationResult=null;PreviewRows.Clear();AllocationRows.Clear();PreviewGrid.Visibility=Visibility.Collapsed;
        CreateButton.IsEnabled=false;if(!preserveFrozen)_frozen=null;UpdateTotals();SaveSelection();
    }
    private void Plan_Changed(object sender,TextChangedEventArgs e){if(!_updating&&IsLoaded)Changed();}
    private void Amount_Changed(object sender,TextChangedEventArgs e){if(!_updating&&IsLoaded)Changed();}
    private void AmountMode_Changed(object sender,SelectionChangedEventArgs e){if(!_updating&&IsLoaded){UpdateMode();Changed();}}
    private void Maximum_Changed(object sender,TextChangedEventArgs e){if(!_updating&&IsLoaded)Changed();}
    private void MaximumMode_Changed(object sender,SelectionChangedEventArgs e){if(!_updating&&IsLoaded)Changed();}

    private void UpdateMode()
    {
        if(LineAmountColumn is null)return;
        LineAmountColumn.Header=IsLump?"Total to divide":"Amount per date";
        MaximumPanel.Visibility=IsCash&&IsLump?Visibility.Visible:Visibility.Collapsed;
        if(!IsCash||!IsLump)AllocationPanel.Visibility=Visibility.Collapsed;
        AmountHelp.Text=IsLump
            ? "Enter each ledger's total below. The exact lump sum is allocated across the selected dates. Cash Lump Sum can additionally cap Per Voucher, Per Day or Per Month; Preview shows any Remaining amount for manual override."
            : "Enter each ledger's amount below. The same amount repeats on every selected date. Maximum Amount is intentionally hidden for Fixed Amount.";
        UpdateTotals();
    }

    private void DateRule_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_updating||!IsLoaded)return;
        RefreshScheduleControls(SelectedText(FrequencyBox),SelectedTimes,SelectedDateMode,CurrentPatternOffsets());
        DateSummary.Text="Rules changed — press Generate Dates to replace the stored selection.";
        Changed();UpdateScheduleHelp();
    }
    private void Frequency_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_updating||!IsLoaded)return;
        RefreshScheduleControls(SelectedText(FrequencyBox),1,"Fixed",null);
        DateSummary.Text="Frequency changed — press Generate Dates to replace the stored selection.";
        Changed();UpdateScheduleHelp();
    }
    private void Times_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_updating||!IsLoaded)return;
        RefreshPatternDates(null);
        DateSummary.Text="Times changed — press Generate Dates to replace the stored selection.";
        Changed();UpdateScheduleHelp();
    }
    private void DateMode_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_updating||!IsLoaded)return;
        RefreshPatternDates(CurrentPatternOffsets());
        DateSummary.Text="Date selection changed — press Generate Dates to replace the stored selection.";
        Changed();UpdateScheduleHelp();
    }
    private void PatternDate_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_updating||!IsLoaded)return;
        DateSummary.Text="Fixed date pattern changed — press Generate Dates to replace the stored selection.";
        Changed();
    }

    private void RefreshScheduleControls(string desiredFrequency,int desiredTimes,string desiredDateMode,IReadOnlyList<int>? desiredOffsets)
    {
        if(StartPicker.SelectedDate is not DateTime start||EndPicker.SelectedDate is not DateTime end)return;
        desiredFrequency=string.IsNullOrWhiteSpace(desiredFrequency)?"Daily":desiredFrequency;
        desiredDateMode=string.IsNullOrWhiteSpace(desiredDateMode)?"Fixed":desiredDateMode;
        var prior=_updating;_updating=true;
        try
        {
            var available=RecurringDirectPlanner.AvailableFrequencies(start,end,_holidays);
            FrequencyChoices.Clear();foreach(var value in available)FrequencyChoices.Add(value);
            var normalized=RecurringDirectPlanner.NormalizeFrequencyTimes(desiredFrequency,Math.Max(1,desiredTimes));
            var frequency=available.Contains(normalized.Frequency,StringComparer.OrdinalIgnoreCase)?normalized.Frequency:
                available.Contains("Monthly")?"Monthly":available.LastOrDefault()??"Daily";
            FrequencyBox.SelectedItem=frequency;
            TimesChoices.Clear();
            foreach(var value in RecurringDirectPlanner.TimesOptions(start,end,frequency,_holidays))TimesChoices.Add(value);
            var times=TimesChoices.Contains(normalized.Times)?normalized.Times:TimesChoices.FirstOrDefault();
            if(times<=0)times=1;
            if(frequency!="Daily")TimesBox.SelectedItem=times;
            TimesPanel.Visibility=frequency=="Daily"?Visibility.Collapsed:Visibility.Visible;
            DateModePanel.Visibility=frequency=="Daily"?Visibility.Collapsed:Visibility.Visible;
            DateModeBox.SelectedIndex=frequency!="Daily"&&desiredDateMode.Equals("Random",StringComparison.OrdinalIgnoreCase)?1:0;
            RefreshPatternDates(desiredOffsets);
            ManualAllocationDatePicker.DisplayDateStart=start.Date;
            ManualAllocationDatePicker.DisplayDateEnd=end.Date;
            if(ManualAllocationDatePicker.SelectedDate is not DateTime selected||selected<start.Date||selected>end.Date)
                ManualAllocationDatePicker.SelectedDate=start.Date;
        }
        finally{_updating=prior;}
    }

    private void RefreshPatternDates(IReadOnlyList<int>? desiredOffsets)
    {
        if(StartPicker.SelectedDate is not DateTime start||EndPicker.SelectedDate is not DateTime end)return;
        var frequency=SelectedText(FrequencyBox);
        var fixedMode=frequency!="Daily"&&SelectedDateMode=="Fixed";
        PatternDatesPanel.Visibility=fixedMode?Visibility.Visible:Visibility.Collapsed;
        PatternDates.Clear();
        if(!fixedMode)return;
        var times=Math.Max(1,SelectedTimes);
        var window=RecurringDirectPlanner.PatternWindow(start,end,frequency);
        var period=RecurringDirectPlanner.PatternPeriod(start,end,frequency);
        IReadOnlyList<DateTime> defaults;
        try{defaults=RecurringDirectPlanner.DefaultPatternDates(start,end,frequency,times,_holidays);}
        catch{defaults=[];}
        for(var index=0;index<times;index++)
        {
            DateTime date;
            if(desiredOffsets is { Count: > 0 }&&index<desiredOffsets.Count)
                date=period.Start.AddDays(Math.Max(0,desiredOffsets[index]));
            else if(index<defaults.Count)date=defaults[index];
            else date=window.Start;
            if(date<window.Start)date=window.Start;if(date>window.End)date=window.End;
            PatternDates.Add(new RecurringPatternDateEditor{Label=$"Date {index+1}",Minimum=window.Start,Maximum=window.End,Date=date});
        }
        PatternHelp.Text=$"These {times:N0} date position(s) are copied into every true {frequency} period. If an automatic fixed date is excluded, AAH chooses a valid date before or after it inside the same period.";
    }

    private IReadOnlyList<int> CurrentPatternOffsets()
    {
        if(StartPicker.SelectedDate is not DateTime start||EndPicker.SelectedDate is not DateTime end||PatternDates.Count==0)return [];
        var period=RecurringDirectPlanner.PatternPeriod(start,end,SelectedText(FrequencyBox));
        return PatternDates.Where(x=>x.Date.HasValue).Select(x=>(x.Date!.Value.Date-period.Start.Date).Days).ToArray();
    }

    private void UpdateScheduleHelp()
    {
        if(ScheduleHelp is null)return;
        var frequency=SelectedText(FrequencyBox);
        if(frequency=="Daily")
        {
            ScheduleHelp.Text="Daily uses each eligible working date between Start and End. Random is not used for Daily.";
            return;
        }
        var times=TimesChoices.Count==0?"none":string.Join(", ",TimesChoices);
        ScheduleHelp.Text=$"Available Times for {frequency}: {times}. Options are calculated from the actual Start/End range and true calendar/FY periods, while reducible counts are moved to the denser frequency. Automatic dates continue to exclude weekends, national holidays and client holidays; Show Dates/manual allocation may override them intentionally.";
    }

    private void UpdateDatesSummary()
    {
        if(DateSummary is null)return;
        DateSummary.Text=_dates.Count==0?"No dates selected":$"{_dates.Count:N0} stored date(s) · {_dates[0]:dd-MMM-yyyy} to {_dates[^1]:dd-MMM-yyyy}";
    }
    private void GenerateDates_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            if(StartPicker.SelectedDate is not DateTime start||EndPicker.SelectedDate is not DateTime end)throw new InvalidOperationException("Select Start and End dates.");
            if(end.Date<start.Date)throw new InvalidOperationException("End date cannot be before Start date.");
            var frequency=SelectedText(FrequencyBox);
            if(string.IsNullOrWhiteSpace(frequency))throw new InvalidOperationException("The selected Start/End range does not provide a valid recurring frequency.");
            var random=SelectedDateMode=="Random";
            if(random)_scheduleSeed=Random.Shared.Next();
            var dates=RecurringDirectPlanner.Dates(start,end,frequency,SelectedTimes,SelectedDateMode,CurrentPatternOffsets(),_holidays,new Random(_scheduleSeed));
            if(dates.Count==0)throw new InvalidOperationException("The selected schedule contains no eligible dates.");
            _dates=dates;Changed();UpdateDatesSummary();StatusText.Text="Dates stored. Press Show Dates to add or delete dates manually.";
        }
        catch(Exception ex){StatusText.Text=ex.Message;}
    }
    private void ShowDates_Click(object sender,RoutedEventArgs e)
    {
        if(StartPicker.SelectedDate is not DateTime start||EndPicker.SelectedDate is not DateTime end){StatusText.Text="Select Start and End dates first.";return;}
        var editor=new RecurringDateEditorWindow(_dates,start.Date,end.Date){Owner=this};
        if(editor.ShowDialog()!=true)return;
        _dates=editor.Dates.ToArray();Changed();UpdateDatesSummary();StatusText.Text="Edited dates stored. Manual dates are authoritative inside Start/End.";
    }

    private void UpdateTotals()
    {
        if(TotalsText is null||_updating)return;
        try
        {
            if(IsCash)
            {
                var total=Lines.Sum(x=>ParseAmount(x.AmountText));
                var payment=Lines.Where(x=>x.DrCr=="Dr").Sum(x=>ParseAmount(x.AmountText));
                TotalsText.Text=$"Payment lines: {payment:N2} · Receipt lines: {total-payment:N2} · Total: {total:N2}";
            }
            else
            {
                UpdateBalance();
                var dr=Lines.Where(x=>x.DrCr=="Dr").Sum(x=>ParseAmount(x.AmountText));
                var cr=Lines.Where(x=>x.DrCr=="Cr").Sum(x=>ParseAmount(x.AmountText));
                TotalsText.Text=$"Total Dr: {dr:N2} · Total Cr: {cr:N2} · Difference: {dr-cr:N2} {(dr==cr&&dr>0?"· Matched":"· Unmatched")}";
            }
        }
        catch(Exception ex){TotalsText.Text=ex.Message;}
    }
    private void UpdateBalance()
    {
        if(IsCash||_updating)return;
        var balance=Lines.FirstOrDefault(x=>x.IsBalanceLine);
        if(balance is null)return;
        _updating=true;
        try
        {
            var regular=Lines.Where(x=>!x.IsBalanceLine).ToArray();
            var dr=regular.Where(x=>x.DrCr=="Dr").Sum(x=>ParseAmount(x.AmountText));
            var cr=regular.Where(x=>x.DrCr=="Cr").Sum(x=>ParseAmount(x.AmountText));
            balance.DrCr=dr>=cr?"Cr":"Dr";balance.AmountText=Money(Math.Abs(dr-cr));
        }
        finally{_updating=false;}
    }
    private void Balance_Click(object sender,RoutedEventArgs e)
    {
        if(IsCash)return;
        var balance=Lines.FirstOrDefault(x=>x.IsBalanceLine);
        if(balance is null)
        {
            balance=LinesGrid.SelectedItem as RecurringDirectLineEditor;
            if(balance is null||balance==Lines[0])balance=Lines.Last();
            if(Lines.Count<2)return;
            balance.IsBalanceLine=true;
            if(balance!=Lines.Last()){Lines.Remove(balance);Lines.Add(balance);}
        }
        Changed();LinesGrid.SelectedItem=balance;
    }
    private void RemoveBalance_Click(object sender,RoutedEventArgs e)
    {
        var balance=Lines.FirstOrDefault(x=>x.IsBalanceLine);
        if(balance is null)return;balance.IsBalanceLine=false;Changed();
    }
    private void NewLedger_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            if(!LinesGrid.CommitEdit(DataGridEditingUnit.Cell,true)||!LinesGrid.CommitEdit(DataGridEditingUnit.Row,true))
                throw new InvalidOperationException("Complete the current accounting line before creating a ledger.");
            var targetLine=LinesGrid.SelectedItem as RecurringDirectLineEditor??Lines.Last();
            var targetCommon=IsCash&&LinesGrid.CurrentColumn==CommonColumn;
            var dialog=new Window{Title="Create Ledger",Width=500,Height=260,MinWidth=420,WindowStartupLocation=WindowStartupLocation.CenterOwner,Owner=this,Background=Background};
            var panel=new StackPanel{Margin=new Thickness(16)};
            panel.Children.Add(new TextBlock{Text="Ledger name"});
            var name=new TextBox{Margin=new Thickness(0,5,0,10)};panel.Children.Add(name);
            panel.Children.Add(new TextBlock{Text="Ledger Group"});
            var groups=new ComboBox{Margin=new Thickness(0,5,0,10),ItemsSource=_service.GetMasters(_clientId,_fy).Where(x=>x.Type=="Group").Select(x=>x.Name).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray()};panel.Children.Add(groups);
            var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
            var cancel=new Button{Content="Cancel",IsCancel=true,Padding=new Thickness(12,6,12,6),Margin=new Thickness(0,0,8,0)};
            var create=new Button{Content="Create Ledger",Padding=new Thickness(12,6,12,6),IsDefault=true};
            cancel.Click+=(_,_)=>dialog.DialogResult=false;
            create.Click+=(_,_)=>CreateLedger();
            void CreateLedger()
            {
                try
                {
                    if(groups.SelectedItem is not string group)throw new InvalidOperationException("Select a Group.");
                    var master=_service.CreateLedger(_clientId,_fy,name.Text,group);
                    dialog.Tag=master.PersistentId;dialog.DialogResult=true;
                }
                catch(Exception ex){MessageBox.Show(dialog,ex.Message,"Ledger",MessageBoxButton.OK,MessageBoxImage.Warning);}
            }
            buttons.Children.Add(cancel);buttons.Children.Add(create);panel.Children.Add(buttons);dialog.Content=panel;
            if(dialog.ShowDialog()!=true||dialog.Tag is not long id)return;
            _updating=true;
            try
            {
                LoadMasters();
                if(targetCommon)targetLine.CommonMasterId=id;else targetLine.MasterId=id;
                ApplyMaster(targetLine,false);
            }
            finally{_updating=false;}
            LinesGrid.SelectedItem=targetLine;Changed();
        }
        catch(Exception ex){StatusText.Text=ex.Message;}
    }

    private IReadOnlyList<RecurringDirectLine> CurrentLines()=>Lines.Select(x=>new RecurringDirectLine(x.Ledger,x.Group,x.DrCr,ParseAmount(x.AmountText),x.CommonLedger,x.IsBalanceLine,x.MasterId,x.CommonMasterId)).ToArray();
    private decimal CurrentBaseAmount(IReadOnlyList<RecurringDirectLine> lines)=>IsCash?lines.Sum(x=>x.Amount):lines.Where(x=>x.DrCr=="Dr").Sum(x=>x.Amount);
    private decimal? ParsedMaximumAmount()
    {
        if(!IsCash||!IsLump||string.IsNullOrWhiteSpace(MaximumAmountBox.Text))return null;
        var value=ParseAmount(MaximumAmountBox.Text);
        if(value<=0m)throw new InvalidOperationException("Maximum Amount must be greater than zero or left blank.");
        return value;
    }

    private RecurringDirectPlan MakePlan(IReadOnlyList<DateTime>? planDates=null,IReadOnlyList<decimal>? frozenAmounts=null)
    {
        var dates=planDates??_dates;
        if(dates.Count==0)throw new InvalidOperationException("Generate or select dates first.");
        var lines=CurrentLines();
        var amount=CurrentBaseAmount(lines);
        return new RecurringDirectPlan(_planId,_clientId,_fy,_module,IsLump?"Lump Sum":"Fixed Amount",amount,dates,lines,NarrationBox.Text,frozenAmounts,LineAmountsAreTotals:IsLump);
    }

    private void UpdateAllocationUi(RecurringDirectAllocationResult result)
    {
        _allocationResult=result;AllocationRows.Clear();
        foreach(var row in result.Allocations)AllocationRows.Add(new RecurringDirectAllocationPreviewRow(row.Date,row.IsManual?"Manual":"Auto",row.Amount,row.IsManual));
        TargetAmountText.Text=$"Target: {result.TargetAmount:N2}";
        AllocatedAmountText.Text=$"Allocated: {result.AllocatedAmount:N2}";
        RemainingAmountText.Text=$"Remaining: {result.RemainingAmount:N2}";
        AllocationPanel.Visibility=IsCash&&IsLump?Visibility.Visible:Visibility.Collapsed;
    }

    private void FreezeAndPreview(bool regenerate)
    {
        var basePlan=MakePlan();
        if(IsCash&&IsLump)
        {
            if(regenerate)_allocationSeed=Random.Shared.Next();
            var allocation=RecurringDirectPlanner.AllocateLumpSum(basePlan.Amount,_dates,ParsedMaximumAmount(),SelectedText(MaximumModeBox),_manualAllocations,new Random(_allocationSeed));
            UpdateAllocationUi(allocation);
            if(allocation.RemainingAmount>0m)
            {
                _previewPlan=null;_preview=[];_frozen=null;PreviewRows.Clear();PreviewGrid.Visibility=Visibility.Collapsed;CreateButton.IsEnabled=false;
                PreviewSummary.Text=$"Automatic capacity cannot allocate {allocation.RemainingAmount:N2}. Add manual Date + Amount overrides until Remaining reaches 0.00.";
                StatusText.Text="Recurring Lump Sum is not balanced yet. Manual overrides are retained and may intentionally exceed automatic maximum rules.";
                SaveSelection();return;
            }
            var allocationDates=allocation.Allocations.Select(x=>x.Date.Date).ToArray();
            _frozen=allocation.Allocations.Select(x=>x.Amount).ToArray();
            var plan=MakePlan(allocationDates,_frozen);
            var occurrences=_service.Preview(plan);
            ShowFinalPreview(plan,occurrences,$"{allocation.Allocations.Count:N0} allocation(s) · {allocation.AllocatedAmount:N2} total");
            return;
        }

        AllocationPanel.Visibility=Visibility.Collapsed;
        var planFixed=basePlan;
        if(regenerate||_frozen is null||_frozen.Count!=_dates.Count)
        {
            if(regenerate)_allocationSeed=Random.Shared.Next();
            _frozen=RecurringDirectPlanner.Distribute(planFixed.Amount,_dates.Count,IsLump,new Random(_allocationSeed));
        }
        planFixed=planFixed with{FrozenAmounts=_frozen};
        var preview=_service.Preview(planFixed);
        ShowFinalPreview(planFixed,preview,$"{preview.Count:N0} date(s) · {preview.Sum(x=>x.Amount):N2} total");
    }

    private void ShowFinalPreview(RecurringDirectPlan plan,IReadOnlyList<RecurringDirectOccurrence> occurrences,string prefix)
    {
        _previewPlan=plan;_preview=occurrences;PreviewRows.Clear();
        foreach(var row in BuildPreviewRows(occurrences))PreviewRows.Add(row);
        PreviewGrid.Visibility=Visibility.Visible;CreateButton.IsEnabled=true;
        PreviewSummary.Text=$"{prefix} · {PreviewRows.Count:N0} voucher(s). Amounts and dates are frozen until the plan changes or you request regeneration.";
        StatusText.Text="Review the complete preview before creating entries.";SaveSelection();
    }

    private void Preview_Click(object sender,RoutedEventArgs e){try{FreezeAndPreview(false);}catch(Exception ex){StatusText.Text=ex.Message;CreateButton.IsEnabled=false;}}
    private void Regenerate_Click(object sender,RoutedEventArgs e){try{FreezeAndPreview(true);}catch(Exception ex){StatusText.Text=ex.Message;CreateButton.IsEnabled=false;}}

    private void AddManualAllocation_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            if(!IsCash||!IsLump)throw new InvalidOperationException("Manual amount allocation is available for Cash Lump Sum mode.");
            if(ManualAllocationDatePicker.SelectedDate is not DateTime date)throw new InvalidOperationException("Choose a manual allocation date.");
            if(StartPicker.SelectedDate is not DateTime start||EndPicker.SelectedDate is not DateTime end||date.Date<start.Date||date.Date>end.Date)
                throw new InvalidOperationException("Manual allocation date must remain inside Start and End dates.");
            var amount=ParseAmount(ManualAllocationAmountBox.Text);
            if(amount<=0m)throw new InvalidOperationException("Enter a positive manual amount.");
            // Re-evaluate first so the user cannot accidentally exceed the true remaining balance.
            var basePlan=MakePlan();
            var current=RecurringDirectPlanner.AllocateLumpSum(basePlan.Amount,_dates,ParsedMaximumAmount(),SelectedText(MaximumModeBox),_manualAllocations,new Random(_allocationSeed));
            if(amount>current.RemainingAmount)throw new InvalidOperationException($"Manual amount cannot exceed the current Remaining {current.RemainingAmount:N2}.");
            _manualAllocations.Add(new RecurringDirectAllocation(date.Date,amount,true));
            ManualAllocationAmountBox.Clear();Changed();FreezeAndPreview(false);
        }
        catch(Exception ex){StatusText.Text=ex.Message;}
    }

    private void DeleteManualAllocation_Click(object sender,RoutedEventArgs e)
    {
        if(AllocationGrid.SelectedItem is not RecurringDirectAllocationPreviewRow row||!row.IsManual){StatusText.Text="Select a Manual allocation row first.";return;}
        var index=_manualAllocations.FindIndex(x=>x.IsManual&&x.Date.Date==row.Date.Date&&x.Amount==row.Amount);
        if(index<0)return;
        _manualAllocations.RemoveAt(index);Changed();
        try{FreezeAndPreview(false);}catch(Exception ex){StatusText.Text=ex.Message;}
    }

    private IReadOnlyList<RecurringDirectPreviewRow> BuildPreviewRows(IReadOnlyList<RecurringDirectOccurrence> occurrences)
    {
        var rows=new List<RecurringDirectPreviewRow>();
        decimal? closing=null;string? cashLedger=null;CashEntryRecord[] existingCash=[];var existingIndex=0;
        if(IsCash)
        {
            var commons=occurrences.SelectMany(x=>x.Lines).Where(x=>x.Amount>0m).Select(x=>x.CommonLedger).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var settings=_service.GetCashSettings(_clientId,_fy);
            var commonChoice=commons.Length==1?LedgerChoices.FirstOrDefault(x=>x.Name.Equals(commons[0],StringComparison.OrdinalIgnoreCase)):null;
            if(commons.Length==1&&commons[0].Equals(settings.DefaultCommonLedger,StringComparison.OrdinalIgnoreCase)&&commonChoice?.Group.Equals("Cash-in-Hand",StringComparison.OrdinalIgnoreCase)==true)
            {
                cashLedger=commons[0];closing=settings.OpeningBalance;
                existingCash=_service.GetExistingCashEntries(_clientId,_fy).Where(x=>x.CommonLedger.Equals(cashLedger,StringComparison.OrdinalIgnoreCase)&&!x.SourceRunId.Equals("R23:"+_planId,StringComparison.Ordinal)).OrderBy(x=>x.EntryDate).ThenBy(x=>x.Id).ToArray();
            }
        }
        foreach(var occurrence in occurrences)
        {
            if(!IsCash)
            {
                rows.Add(new(occurrence.Date,"Journal",occurrence.Amount,string.Join("; ",occurrence.Lines.Where(x=>x.Amount>0m).Select(x=>$"{x.Ledger} {x.DrCr} {x.Amount:N2}")),""));continue;
            }
            if(closing.HasValue)
            {
                while(existingIndex<existingCash.Length&&existingCash[existingIndex].EntryDate.Date<=occurrence.Date.Date)
                {
                    var existing=existingCash[existingIndex++];if(existing.VoucherType.Equals("Receipt",StringComparison.OrdinalIgnoreCase))closing+=existing.Amount;else if(existing.VoucherType.Equals("Payment",StringComparison.OrdinalIgnoreCase))closing-=existing.Amount;
                }
            }
            foreach(var group in occurrence.Lines.Where(x=>x.Amount>0m).GroupBy(x=>new{x.CommonMasterId,x.CommonLedger,x.DrCr}))
            {
                var amount=group.Sum(x=>x.Amount);var common=Choice(group.Key.CommonMasterId);
                var type=common is not null&&IsCashGroup(common.Group)?group.Key.DrCr=="Dr"?"Payment":"Receipt":"Journal";
                if(closing.HasValue&&cashLedger is not null&&cashLedger.Equals(group.Key.CommonLedger,StringComparison.OrdinalIgnoreCase))closing+=type=="Receipt"?amount:type=="Payment"?-amount:0m;
                rows.Add(new(occurrence.Date,type,amount,string.Join("; ",group.Select(x=>$"{x.Ledger} {x.DrCr} {x.Amount:N2}")),closing.HasValue?closing.Value.ToString("N2"):"Not available"));
            }
        }
        return rows;
    }

    private void Create_Click(object sender,RoutedEventArgs e)
    {
        if(_previewPlan is null||_preview.Count==0)return;
        try
        {
            var plan=_previewPlan;var similar=_service.GetSimilarCompleted(plan);
            var extra=similar.Count==0?"":$"\n\nWARNING: {similar.Count} previously completed generation(s) have the same dates, amounts and ledger structure. Creating a new plan will create another set of entries.";
            var distinctDates=_preview.Select(x=>x.Date.Date).Distinct().Count();
            var answer=MessageBox.Show(this,$"Create {PreviewRows.Count:N0} normal accounting voucher(s) across {distinctDates:N0} distinct date(s)?\n\nTotal: {_preview.Sum(x=>x.Amount):N2}\n\nEntries will be saved directly as Ready, not Draft. Tally export remains separate.{extra}","Confirm Recurring Entries",MessageBoxButton.YesNo,similar.Count>0?MessageBoxImage.Warning:MessageBoxImage.Question,MessageBoxResult.No);
            if(answer!=MessageBoxResult.Yes)return;
            CreateButton.IsEnabled=false;var result=_service.Create(plan);_refresh();
            _planId=Guid.NewGuid().ToString("N");_previewPlan=null;_preview=[];_frozen=null;_allocationResult=null;PreviewRows.Clear();AllocationRows.Clear();PreviewGrid.Visibility=Visibility.Collapsed;_manualAllocations.Clear();
            SaveSelection();
            MessageBox.Show(this,$"Created {result.Created:N0} new accounting line(s)/voucher(s). Existing: {result.Existing:N0}.\n\nThe selected schedule and template remain available for another run. Tally export is separate.","Recurring Entries",MessageBoxButton.OK,MessageBoxImage.Information);
            StatusText.Text="Generation completed. The next plan has a fresh identity.";
        }
        catch(Exception ex){StatusText.Text=ex.Message;MessageBox.Show(this,ex.Message,"Recurring Generation",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private void Resume_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var runs=_service.GetIncomplete(_clientId,_fy,_module);if(runs.Count==0){StatusText.Text="No incomplete recurring runs were found.";return;}
            var run=runs[0];var plan=JsonSerializer.Deserialize<RecurringDirectPlan>(run.PlanJson)??throw new InvalidOperationException("Saved plan could not be read.");
            if(MessageBox.Show(this,$"Resume saved plan {run.PlanId}?\n\n{run.Status}: {run.LastError}\n\nOnly incomplete source identities will be retried. Previously committed rows are retained.","Resume Recurring Run",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
            var result=_service.Create(plan);_refresh();MessageBox.Show(this,$"Recovery completed. New: {result.Created}, existing: {result.Existing}.","Recurring Recovery",MessageBoxButton.OK,MessageBoxImage.Information);StatusText.Text="Incomplete run recovered. Review the normal accounting module.";
        }
        catch(Exception ex){StatusText.Text=ex.Message;}
    }

    private void SaveSelection()
    {
        if(_updating||!IsLoaded)return;
        try
        {
            var selection=new RecurringDirectSelection(_planId,SelectedText(AmountModeBox),string.Empty,StartPicker.SelectedDate,EndPicker.SelectedDate,
                SelectedText(FrequencyBox),NarrationBox.Text,_dates,
                Lines.Select(x=>new RecurringDirectLine(x.Ledger,x.Group,x.DrCr,decimal.TryParse(x.AmountText,NumberStyles.Number,CultureInfo.InvariantCulture,out var a)?a:0m,x.CommonLedger,x.IsBalanceLine,x.MasterId,x.CommonMasterId)).ToArray(),
                _frozen,AmountSchema:2,Times:SelectedTimes,DateMode:SelectedDateMode,FixedPeriodOffsets:CurrentPatternOffsets(),
                MaximumMode:SelectedText(MaximumModeBox),MaximumAmount:MaximumAmountBox.Text??"",ManualAllocations:_manualAllocations.ToArray());
            _service.SaveSelection(_clientId,_fy,_module,JsonSerializer.Serialize(selection));
        }
        catch{ /* An incomplete form remains editable; only validated creation writes accounting. */ }
    }

    private void RestoreSelection()
    {
        try
        {
            var saved=_service.GetSelection(_clientId,_fy,_module);if(string.IsNullOrWhiteSpace(saved))return;
            var selection=JsonSerializer.Deserialize<RecurringDirectSelection>(saved);if(selection is null)return;
            _updating=true;
            _planId=string.IsNullOrWhiteSpace(selection.PlanId)?Guid.NewGuid().ToString("N"):selection.PlanId;
            AmountModeBox.SelectedIndex=selection.AmountMode=="Lump Sum"?1:0;
            if(selection.Start is DateTime start)StartPicker.SelectedDate=start;
            if(selection.End is DateTime end)EndPicker.SelectedDate=end;
            RefreshScheduleControls(selection.Frequency,selection.Times,selection.DateMode,selection.FixedPeriodOffsets);
            NarrationBox.Text=selection.Narration;_dates=selection.Dates??[];_frozen=selection.FrozenAmounts;
            MaximumAmountBox.Text=selection.MaximumAmount??"";
            var maximumMode=string.IsNullOrWhiteSpace(selection.MaximumMode)?"Per Voucher":selection.MaximumMode;
            for(var index=0;index<MaximumModeBox.Items.Count;index++)
                if(MaximumModeBox.Items[index] is ComboBoxItem item&&string.Equals(item.Content?.ToString(),maximumMode,StringComparison.OrdinalIgnoreCase)){MaximumModeBox.SelectedIndex=index;break;}
            _manualAllocations.Clear();if(selection.ManualAllocations is not null)_manualAllocations.AddRange(selection.ManualAllocations.Select(x=>x with{Date=x.Date.Date,IsManual=true}));
            var restoredLines=selection.Lines??[];
            if(IsLump&&selection.AmountSchema==0&&decimal.TryParse(selection.Amount,NumberStyles.Number,CultureInfo.InvariantCulture,out var oldTotal)&&oldTotal>0m)
            {
                try{restoredLines=RecurringDirectPlanner.LegacyLineTotals(restoredLines,oldTotal,!IsCash);}catch(InvalidOperationException ex){StatusText.Text=ex.Message;}
            }
            Lines.Clear();if(selection.AmountSchema==0)_frozen=null;
            foreach(var row in restoredLines)
            {
                var editor=AddLine();editor.MasterId=row.MasterId;editor.CommonMasterId=row.CommonMasterId;editor.Ledger=row.Ledger;editor.Group=row.Group;editor.CommonLedger=row.CommonLedger;editor.DrCr=row.DrCr;editor.AmountText=Money(row.Amount);editor.IsBalanceLine=row.IsBalanceLine;ApplyMaster(editor,false);
            }
            while(Lines.Count<(IsCash?1:2))AddLine();_updating=false;UpdateMode();UpdateDatesSummary();UpdateScheduleHelp();
        }
        catch{_updating=false;StatusText.Text="Saved recurring selection could not be restored. Existing accounting records are unchanged.";}
    }
}
