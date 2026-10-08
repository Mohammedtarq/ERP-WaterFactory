using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Shell;

public record KpiTile(string Title, string Value, string Glyph, string Color, string Hint);
/// <summary>صنف عند حد التنبيه في مخزن بعينه — الرصيد والحد بعبوة الصنف.</summary>
public record LowStockRow(string ItemCode, string ItemName, string Warehouse, string Balance, string MinLevel);

public class DashboardViewModel : SessionViewModel
{
    public DashboardViewModel(AppSession session, IDialogService dialogs)
        : base(session, dialogs, ModuleCode.Dashboard)
    {
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        Background(LoadAsync());
    }

    public string Title => "لوحة المعلومات";
    public string Glyph => Icons.Dashboard;
    public string Color => ModuleColors.Dashboard;
    public string Greeting => $"أهلًا {Session.FullName} — {Session.ProjectName}";

    public ObservableCollection<KpiTile> Tiles { get; } = new();
    public ObservableCollection<LowStockRow> LowStock { get; } = new();
    public ObservableCollection<SalesInvoiceListRow> RecentInvoices { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }

    private long _loadedVersion = -1;

    public Task RefreshIfChangedAsync() => _loadedVersion == Session.DataVersion ? Task.CompletedTask : LoadAsync();

    private Task? _loading;

    public Task LoadAsync()
    {
        if (_loading is { IsCompleted: false }) return _loading;
        return _loading = LoadCoreAsync();
    }

    private async Task LoadCoreAsync()
    {
        _loadedVersion = Session.DataVersion;
        IsBusy = true;
        try
        {
            await using var db = Session.NewDb();
            var today = DateTime.Today;
            var sales = new SalesService(db);

            var todayInvoices = await db.SalesInvoices
                .Where(i => i.Status == DocumentStatus.Posted && i.InvoiceDate == today && !i.IsFreeSale && !i.IsOpeningBalance)
                .Select(i => new { i.TotalAmount, i.AmountPaidNow }).ToListAsync();
            var receivables = (await sales.GetCustomerBalancesAsync()).Where(b => b.Balance > 0).Sum(b => b.Balance);
            var low = await new StockAlertService(db).LowAsync();
            var drafts = await db.SalesInvoices.CountAsync(i => i.Status == DocumentStatus.Draft);

            Tiles.Clear();
            Tiles.Add(new KpiTile("مبيعات اليوم", $"{todayInvoices.Sum(i => i.TotalAmount):N0} د.ع", Icons.Sales, ModuleColors.Sales,
                                  $"{todayInvoices.Count} فاتورة مرحّلة"));
            Tiles.Add(new KpiTile("المقبوض اليوم", $"{todayInvoices.Sum(i => i.AmountPaidNow):N0} د.ع", Icons.Voucher, ModuleColors.Finance,
                                  "نقدي وإلكتروني عند البيع"));
            Tiles.Add(new KpiTile("ذمم العملاء", $"{receivables:N0} د.ع", Icons.People, ModuleColors.Suppliers,
                                  "إجمالي المستحق على العملاء"));
            Tiles.Add(new KpiTile("تنبيهات المخزون", low.Count.ToString(), Icons.Alert, "#EF4444",
                                  "أصناف عند حد التنبيه أو أقل"));
            Tiles.Add(new KpiTile("فواتير مسودة", drafts.ToString(), Icons.Invoice, ModuleColors.Dashboard,
                                  "بانتظار الترحيل"));

            // تذكير النسخ الاحتياطي لمن يدير النظام فقط
            if (Session.Permissions.CanView(ModuleCode.SystemSettings))
            {
                var dbName = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(Session.ConnectionString).InitialCatalog;
                DateTime? last = null;
                try { last = (await new BackupService(Session.ConnectionString).GetHistoryAsync(new[] { dbName }, 1)).FirstOrDefault()?.FinishedAt; }
                catch (Microsoft.Data.SqlClient.SqlException) { }
                var overdue = last is null || (DateTime.Now - last.Value).TotalDays >= 1;
                Tiles.Add(new KpiTile("آخر نسخة احتياطية", last?.ToString("yyyy/MM/dd") ?? "لا توجد", Icons.Backup, overdue ? "#EF4444" : "#0EA5E9",
                                      overdue ? "خذ نسخة من إعدادات النظام ← النسخ الاحتياطي" : "النسخ محدّث"));
            }

            LowStock.Clear();
            foreach (var x in low.Take(10))
                LowStock.Add(new LowStockRow(x.ItemCode, x.ItemName, x.WarehouseName, x.BalanceText, x.MinText));

            RecentInvoices.Clear();
            foreach (var r in (await sales.GetInvoiceListAsync()).Take(10)) RecentInvoices.Add(r);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
