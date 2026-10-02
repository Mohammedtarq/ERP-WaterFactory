using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.ViewModels.Finance;
using ERP.Presentation.ViewModels.Sales;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>واجهة كل مخزن (مستندات وتقارير) والصناديق المالية — كما يستخدمها المستخدم.</summary>
[Collection("app")]
public class WarehouseCashBoxTests
{
    private readonly AppFixture _f;
    public WarehouseCashBoxTests(AppFixture f) => _f = f;

    private static async Task Open(ModuleViewModel module, SectionViewModel section)
    {
        module.SelectedTab = section;
        await module.IdleAsync();
        await section.IdleAsync();
    }

    private static decimal Balance(WarehouseWorkspaceSectionViewModel ws, string itemCode) =>
        ws.Balances.Where(b => b.ItemCode == itemCode).Sum(b => b.Quantity);

    [Fact]
    public async Task Each_warehouse_has_its_own_workspace_documents_and_printable_reports()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        await wh.IdleAsync();
        var fg = wh.Workspace(_f.MainWarehouseId);
        Assert.Same(fg, wh.Tabs[1]);                                   // تبويبات المخازن بعد "الرئيسية" مباشرة
        Assert.Contains(wh.Home.Sections, s => s == fg);

        // مخزن جديد من "تعريف المخازن" يظهر فورًا كتبويب مستقل
        var defs = wh.Section<WarehousesSectionViewModel>();
        await Open(wh, defs);
        await defs.NewCommand.ExecuteAsync();
        defs.Editor!.Name = "مخزن الأغطية والملصقات";
        defs.Editor.WarehouseType = WarehouseType.RawMaterial;
        await defs.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var raw = wh.Workspaces.Single(w => w.Title == "مخزن الأغطية والملصقات");
        Assert.Equal("مواد أولية", raw.WarehouseTypeLabel);

        await Open(wh, fg);
        var before = Balance(fg, "W500");
        var docsBefore = dialogs.Reports.Count;

        // 1) إدخال مخزني: 10 كراتين بتشغيلة جديدة ← +120 قطعة، ومستند قابل للطباعة
        fg.Operation = fg.OperationOptions.Single(o => o.Value == StockDocumentType.Receipt);
        fg.PartyName = "خط الإنتاج 1";
        fg.ShowAllItems = true;
        fg.LineItem = fg.ItemsLookup.Single(i => i.ItemCode == "W500");
        await fg.IdleAsync();
        Assert.Equal("كارتون", fg.LineLevel!.LevelName);
        fg.LineQuantity = 10;
        fg.LineNewBatch = "WS-R1";
        fg.LineExpiry = DateTime.Today.AddMonths(12);
        await fg.AddLineCommand.ExecuteAsync();
        Assert.Equal("1 سطر — 120 قطعة", fg.LinesTotalText);
        await fg.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.StartsWith("SR-", fg.LastDocument!.DocumentNumber);
        Assert.Equal(before + 120, Balance(fg, "W500"));
        var printed = dialogs.Reports[docsBefore];
        Assert.Equal("مستند إدخال مخزني", printed.Title);
        Assert.Contains(printed.Rows, r => r[3] == "10" && r[4] == "120" && r[5] == "WS-R1");
        Assert.Empty(fg.Lines);

        // 2) مناقلة كرتونين إلى المخزن الجديد بنفس التشغيلة
        fg.Operation = fg.OperationOptions.Single(o => o.Value == StockDocumentType.Transfer);
        await fg.IdleAsync();
        fg.CounterWarehouse = fg.OtherWarehouses.Single(w => w.Id == raw.WarehouseId);
        fg.LineItem = null;
        fg.LineItem = fg.ItemsLookup.Single(i => i.ItemCode == "W500");
        await fg.IdleAsync();
        fg.LineBatch = fg.BatchOptions.Single(b => b.Label.StartsWith("WS-R1"));
        fg.LineQuantity = 2;
        await fg.AddLineCommand.ExecuteAsync();
        await fg.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(before + 96, Balance(fg, "W500"));
        await Open(wh, raw);
        var rawRow = Assert.Single(raw.Balances);
        Assert.Equal(24m, rawRow.Quantity);
        Assert.Equal("WS-R1", rawRow.BatchNumber);
        Assert.Equal("2 كارتون", rawRow.Breakdown);

        // 3) تالف 5 قطع، 4) مسحوب مجاني يتطلب الجهة، 5) إخراج يفوق المتاح يُرفض
        await Open(wh, fg);
        fg.Operation = fg.OperationOptions.Single(o => o.Value == StockDocumentType.Damaged);
        fg.LineItem = null;
        fg.LineItem = fg.ItemsLookup.Single(i => i.ItemCode == "W500");
        await fg.IdleAsync();
        fg.LineLevel = fg.LevelOptions.Single(l => l.LevelName == "قطعة");
        fg.LineQuantity = 5;
        await fg.AddLineCommand.ExecuteAsync();
        await fg.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.StartsWith("SD-", fg.LastDocument!.DocumentNumber);
        var damageDoc = fg.LastDocument.DocumentNumber;

        fg.Operation = fg.OperationOptions.Single(o => o.Value == StockDocumentType.FreeIssue);
        fg.LineItem = null;
        fg.LineItem = fg.ItemsLookup.Single(i => i.ItemCode == "W500");
        await fg.IdleAsync();
        fg.LineLevel = fg.LevelOptions.Single(l => l.LevelName == "قطعة");
        fg.LineQuantity = 6;
        await fg.AddLineCommand.ExecuteAsync();
        fg.PartyName = null;
        await fg.SaveCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("الجهة المستفيدة"));
        dialogs.Errors.Clear();
        fg.PartyName = "ضيافة الإدارة";
        await fg.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);

        fg.Operation = fg.OperationOptions.Single(o => o.Value == StockDocumentType.Issue);
        fg.LineItem = null;
        fg.LineItem = fg.ItemsLookup.Single(i => i.ItemCode == "W500");
        await fg.IdleAsync();
        fg.LineLevel = fg.LevelOptions.Single(l => l.LevelName == "قطعة");
        fg.LineQuantity = Balance(fg, "W500") + 1;
        await fg.AddLineCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("أكبر من المتاح"));
        dialogs.Errors.Clear();
        var finalBalance = before + 96 - 5 - 6;
        Assert.Equal(finalBalance, Balance(fg, "W500"));

        // تقرير الرصيد والمتبقي لهذا المخزن وحده
        await fg.LoadReportsCommand.ExecuteAsync();
        var row = fg.Summary.Single(s => s.ItemCode == "W500");
        Assert.True(row.Damaged >= 5m);                            // + تسويات اختبارات أخرى في نفس الشهر
        Assert.True(row.Free >= 6m);
        Assert.Equal(finalBalance, row.Closing);
        fg.PrintSummaryCommand.Execute(null);
        Assert.Equal("تقرير حركة مخزن المنتج التام", dialogs.Reports.Last().Title);
        Assert.Contains(dialogs.Reports.Last().Totals, t => t.Label == "المتبقي آخر المدة" && t.Emphasis);

        // كشف الحركة التفصيلي للصنف: الرصيد التراكمي ينتهي بالرصيد الفعلي
        fg.LedgerItem = fg.LedgerItems.Single(i => i.ItemCode == "W500");
        await fg.IdleAsync();
        Assert.Equal(finalBalance, fg.Ledger.Last().Balance);
        Assert.Contains(fg.Ledger, l => l.TypeLabel == "مناقلة صادرة" && l.Out == 24);
        // التالف قد يتوزع على أكثر من تشغيلة (الأقرب انتهاءً أولًا) — مجموعه 5
        Assert.Equal(5m, fg.Ledger.Where(l => l.Reference == damageDoc).Sum(l => l.Out));
        Assert.All(fg.Ledger.Where(l => l.Reference == damageDoc), l => Assert.StartsWith("تالف", l.TypeLabel));
        fg.PrintLedgerCommand.Execute(null);
        Assert.Contains(dialogs.Reports.Last().Totals, t => t.Label == "الرصيد آخر المدة" && t.Value == $"{finalBalance:N0} قطعة");

        fg.PrintBalancesCommand.Execute(null);
        Assert.Equal("أرصدة مخزن المنتج التام", dialogs.Reports.Last().Title);

        // المستندات قابلة لإعادة الطباعة؛ مستند المناقلة يظهر في المخزنين
        Assert.Equal(4, fg.Documents.Count(d => d.DocumentNumber.StartsWith("S")));
        await fg.PrintDocumentCommand.ExecuteAsync(fg.Documents.Single(d => d.DocumentType == StockDocumentType.Transfer));
        Assert.Equal("مستند مناقلة إلى مخزن آخر", dialogs.Reports.Last().Title);
        Assert.Contains(dialogs.Reports.Last().HeaderFields, f => f.Label == "إلى مخزن" && f.Value == "مخزن الأغطية والملصقات");
        await raw.LoadReportsCommand.ExecuteAsync();
        Assert.Single(raw.Documents);
        Assert.Empty(_f.Unhandled);
    }

    [Fact]
    public async Task Cash_boxes_sales_receipts_transfers_and_admin_only_edits()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var fin = shell.Open<FinanceModuleViewModel>(ModuleCode.Finance);
        var boxes = fin.Boxes;
        await Open(fin, boxes);
        Assert.True(boxes.IsAdmin);
        var main = boxes.Boxes.Single(b => b.Name == "الصندوق الرئيسي");     // يُنشأ تلقائيًا عند فتح المشروع
        Assert.True(main.IsDefault);
        boxes.SelectedBox = main;
        await boxes.IdleAsync();
        var start = boxes.Closing;

        // إيداع ← إيصال بالمبلغ كتابةً
        boxes.Action = boxes.ActionOptions.Single(a => a.Value == CashBoxTxType.Deposit);
        boxes.Amount = 250_000;
        Assert.Equal("فقط مئتان وخمسون ألف دينار عراقي لا غير", boxes.AmountWords);
        boxes.Party = "المالك";
        await boxes.SubmitCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(start + 250_000, boxes.Closing);
        var receipt = dialogs.Reports.Last();
        Assert.Equal("إيصال إيداع نقدي", receipt.Title);
        Assert.Contains(receipt.Totals, t => t.Value == "فقط مئتان وخمسون ألف دينار عراقي لا غير");
        var depositId = boxes.LastTransaction!.Id;

        // سحب يفوق الرصيد يُرفض
        boxes.Action = boxes.ActionOptions.Single(a => a.Value == CashBoxTxType.Withdrawal);
        boxes.Amount = boxes.Closing + 1;
        await boxes.SubmitCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("رصيد الصندوق غير كافٍ"));
        dialogs.Errors.Clear();

        // صندوق مستخدم لسارة (موظفة المبيعات)
        boxes.NewBoxName = "صندوق سارة";
        boxes.NewBoxType = boxes.BoxTypeOptions.Single(t => t.Value == CashBoxType.User);
        boxes.NewBoxOwner = boxes.Users.Single(u => u.Username == AppFixture.ClerkUser);
        boxes.NewBoxActive = true;                                         // التفعيل اختياري عند الإنشاء
        await boxes.CreateBoxCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal("صندوق سارة", boxes.SelectedBox!.Name);

        // مناقلة 40,000 من الرئيسي إلى صندوق سارة
        boxes.SelectedBox = boxes.Boxes.Single(b => b.Name == "الصندوق الرئيسي");
        await boxes.IdleAsync();
        boxes.Action = boxes.ActionOptions.Single(a => a.Value == CashBoxTxType.TransferOut);
        boxes.TargetBox = boxes.TransferTargets.Single(t => t.Name == "صندوق سارة");
        boxes.Amount = 40_000;
        await boxes.SubmitCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(start + 210_000, boxes.Closing);
        Assert.Equal(40_000m, boxes.Boxes.Single(b => b.Name == "صندوق سارة").Balance);
        var transferRow = boxes.Rows.Last();
        Assert.Equal("مناقلة صادرة", transferRow.TypeLabel);

        // فاتورة نقدية مرحّلة تدخل الصندوق الافتراضي (المدير بلا صندوق خاص)
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var inv = sales.Invoice;
        await Open(sales, inv);
        inv.Customer = inv.Customers.Single(c => c.Id == _f.DirectId);
        inv.Warehouse = inv.Warehouses.Single(w => w.Id == _f.MainWarehouseId);
        inv.LineItem = inv.ItemsLookup.Single(i => i.Id == _f.WaterItemId);
        await inv.IdleAsync();
        inv.LineQuantity = 1;
        await inv.AddLineCommand.ExecuteAsync();
        await inv.IdleAsync();
        inv.PaymentMethod = inv.PaymentOptions.Single(o => o.Value == InvoicePaymentMethod.Cash);
        var total = inv.Total;
        await inv.PostCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var number = inv.LastPosted!.InvoiceNumber;
        shell.Open<FinanceModuleViewModel>(ModuleCode.Finance);
        await boxes.RefreshCommand.ExecuteAsync();
        var saleRow = Assert.Single(boxes.Rows, r => r.Reference == number);
        Assert.Equal("مبيعات نقدية", saleRow.TypeLabel);
        Assert.Equal(total, saleRow.In);
        Assert.False(saleRow.IsManual);

        // سارة: ترى صندوقها فقط من المبيعات، تسلّم للرئيسي، ولا تستطيع التعديل
        var (clerkShell, clerkDialogs) = await _f.LoginAsync(AppFixture.ClerkUser, AppFixture.ClerkPassword);
        var clerkSales = clerkShell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var mine = clerkSales.MyBox;
        await Open(clerkSales, mine);
        Assert.False(mine.IsAdmin);
        Assert.Equal("صندوق سارة", Assert.Single(mine.Boxes).Name);
        mine.Action = mine.ActionOptions.Single(a => a.Value == CashBoxTxType.TransferOut);
        mine.TargetBox = mine.TransferTargets.Single(t => t.Name == "الصندوق الرئيسي");
        mine.Amount = 15_000;
        await mine.SubmitCommand.ExecuteAsync();
        Assert.Empty(clerkDialogs.Errors);
        Assert.Equal(25_000m, mine.Closing);
        mine.EditCommand.Execute(mine.Rows.Last());
        Assert.Contains(clerkDialogs.Errors, e => e.Contains("للأدمن فقط"));
        Assert.False(mine.IsEditing);

        // الأدمن: الحركة الناتجة عن فاتورة لا تُعدَّل من الصندوق
        await boxes.RefreshCommand.ExecuteAsync();
        boxes.EditCommand.Execute(boxes.Rows.Single(r => r.Reference == number));
        Assert.Contains(dialogs.Errors, e => e.Contains("مستندها الأصلي"));
        dialogs.Errors.Clear();

        // تعديل المناقلة 40,000 ← 30,000 يعدّل طرفيها
        boxes.EditCommand.Execute(boxes.Rows.Single(r => r.TxNumber == transferRow.TxNumber));
        Assert.True(boxes.IsEditing);
        boxes.EditAmount = 30_000;
        await boxes.SaveEditCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(15_000m, boxes.Boxes.Single(b => b.Name == "صندوق سارة").Balance);
        Assert.True(boxes.Rows.Single(r => r.TxNumber == transferRow.TxNumber).IsModified);

        // إلغاء الإيداع: يبقى في الكشف مشطوبًا، يخرج من الرصيد، ويُعكس قيده
        var mainBefore = boxes.Closing;
        boxes.EditCommand.Execute(boxes.Rows.Single(r => r.Id == depositId));
        await boxes.VoidCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("سبب الإلغاء"));
        dialogs.Errors.Clear();
        boxes.VoidReason = "إيداع مكرر";
        await boxes.VoidCommand.ExecuteAsync();
        // جزء من الإيداع خرج بالمناقلة: الإلغاء يجعل الصندوق سالبًا فيُرفض
        Assert.Contains(dialogs.Errors, e => e.Contains("سالبًا"));
        dialogs.Errors.Clear();
        boxes.CancelEditCommand.Execute(null);
        boxes.Action = boxes.ActionOptions.Single(a => a.Value == CashBoxTxType.Deposit);
        boxes.Amount = 100_000;
        await boxes.SubmitCommand.ExecuteAsync();
        mainBefore = boxes.Closing;
        boxes.EditCommand.Execute(boxes.Rows.Single(r => r.Id == depositId));
        boxes.VoidReason = "إيداع مكرر";
        await boxes.VoidCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(mainBefore - 250_000, boxes.Closing);
        Assert.True(boxes.Rows.Single(r => r.Id == depositId).IsVoided);
        await boxes.PrintTxCommand.ExecuteAsync(boxes.Rows.Single(r => r.Id == depositId));
        Assert.StartsWith("ملغاة", dialogs.Reports.Last().Stamp);

        await using var db = _f.NewDb();
        var reversal = await db.JournalEntries.Include(j => j.Lines).SingleAsync(j => j.SourceTable == "CashBoxTransactions" && j.EntryNumber.StartsWith("CBR-"));
        Assert.True(reversal.Lines.Sum(l => l.Debit) == 250_000 && reversal.Lines.Sum(l => l.Credit) == 250_000);

        // كشف الصندوق قابل للطباعة
        boxes.PrintStatementCommand.Execute(null);
        Assert.Equal("كشف صندوق — الصندوق الرئيسي", dialogs.Reports.Last().Title);
        Assert.Empty(_f.Unhandled);
    }

    private async Task<string> PostCashInvoiceAsync(MainShellViewModel shell, RecordingDialogs dialogs)
    {
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var inv = sales.Invoice;
        await Open(sales, inv);
        inv.Customer = inv.Customers.Single(c => c.Id == _f.DirectId);
        inv.Warehouse = inv.Warehouses.Single(w => w.Id == _f.MainWarehouseId);
        inv.LineItem = inv.ItemsLookup.Single(i => i.Id == _f.WaterItemId);
        await inv.IdleAsync();
        inv.LineQuantity = 1;
        await inv.AddLineCommand.ExecuteAsync();
        await inv.IdleAsync();
        inv.PaymentMethod = inv.PaymentOptions.Single(o => o.Value == InvoicePaymentMethod.Cash);
        await inv.PostCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        return inv.LastPosted!.InvoiceNumber;
    }

    /// <summary>
    /// تفعيل صندوق المستخدم اختياري: صندوق غير مفعّل ← المبيعات النقدية تذهب للصندوق الرئيسي بلا أي رسالة؛
    /// بعد التفعيل ← تذهب لصندوقه. والصندوق الافتراضي لا يُوقف.
    /// </summary>
    [Fact]
    public async Task Inactive_user_box_sends_cash_sales_to_the_main_box_silently()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var fin = shell.Open<FinanceModuleViewModel>(ModuleCode.Finance);
        var boxes = fin.Boxes;
        await Open(fin, boxes);

        boxes.NewBoxName = "صندوق المدير";
        boxes.NewBoxType = boxes.BoxTypeOptions.Single(t => t.Value == CashBoxType.User);
        boxes.NewBoxOwner = boxes.Users.Single(u => u.Username == AppFixture.AdminUser);
        Assert.False(boxes.NewBoxActive);                                  // غير مفعّل افتراضيًا
        await boxes.CreateBoxCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var mine = boxes.Boxes.Single(b => b.Name == "صندوق المدير");
        Assert.False(mine.IsActive);
        Assert.Contains("غير مفعّل", boxes.InactiveNotice);
        Assert.False(boxes.SelectedIsActive);                              // لا حركات يدوية على صندوق موقوف

        // غير مفعّل: الفاتورة النقدية تُرحَّل بلا أي رسالة وتدخل الرئيسي
        var first = await PostCashInvoiceAsync(shell, dialogs);
        await using (var db = _f.NewDb())
        {
            var tx = await db.CashBoxTransactions.Include(t => t.CashBox).SingleAsync(t => t.ReferenceTable == "SalesInvoices" &&
                         t.ReferenceId == db.SalesInvoices.Where(i => i.InvoiceNumber == first).Select(i => i.Id).First());
            Assert.Equal("الصندوق الرئيسي", tx.CashBox.Name);
        }

        // تفعيل ← الفاتورة التالية تدخل صندوقه
        shell.Open<FinanceModuleViewModel>(ModuleCode.Finance);
        await boxes.RefreshCommand.ExecuteAsync();
        await boxes.ToggleActiveCommand.ExecuteAsync(boxes.Boxes.Single(b => b.Name == "صندوق المدير"));
        Assert.Empty(dialogs.Errors);
        Assert.True(boxes.Boxes.Single(b => b.Name == "صندوق المدير").IsActive);
        var second = await PostCashInvoiceAsync(shell, dialogs);
        shell.Open<FinanceModuleViewModel>(ModuleCode.Finance);           // العودة للوحدة تحدّث الصندوق تلقائيًا (بيانات تغيّرت)
        fin.Reactivate();                                                  // تفعيل مزدوج متزامن: تحميل واحد، بلا صفوف مكررة
        await fin.LastActivation;
        Assert.Equal(boxes.Boxes.Count, boxes.Boxes.Select(b => b.Id).Distinct().Count());
        boxes.SelectedBox = boxes.Boxes.Single(b => b.Name == "صندوق المدير");
        await boxes.IdleAsync();
        Assert.Contains(boxes.Rows, r => r.Reference == second && r.TypeLabel == "مبيعات نقدية");

        // إيقافه مجددًا يعيد التوجيه للرئيسي؛ والافتراضي لا يُوقف
        await boxes.ToggleActiveCommand.ExecuteAsync(boxes.Boxes.Single(b => b.Name == "صندوق المدير"));
        await boxes.ToggleActiveCommand.ExecuteAsync(boxes.Boxes.Single(b => b.Name == "الصندوق الرئيسي"));
        Assert.Contains(dialogs.Errors, e => e.Contains("الافتراضي") && e.Contains("لا يُوقف"));
        dialogs.Errors.Clear();
        var third = await PostCashInvoiceAsync(shell, dialogs);
        await using (var db = _f.NewDb())
        {
            var id = await db.SalesInvoices.Where(i => i.InvoiceNumber == third).Select(i => i.Id).FirstAsync();
            Assert.Equal("الصندوق الرئيسي", await db.CashBoxTransactions.Where(t => t.ReferenceTable == "SalesInvoices" && t.ReferenceId == id)
                                                                         .Select(t => t.CashBox.Name).SingleAsync());
        }
        Assert.Empty(_f.Unhandled);
    }

    /// <summary>بعد أي حفظ في شاشة، الشاشات الأخرى تُحدَّث تلقائيًا عند فتحها — ولا تُعيد الاستعلام إن لم يتغير شيء.</summary>
    [Fact]
    public async Task Screens_refresh_automatically_after_a_save_elsewhere()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var customers = sales.Section<CustomersSectionViewModel>();
        var inv = sales.Invoice;
        await Open(sales, inv);
        var before = inv.Customers.Count;

        // عميل جديد من تبويب العملاء يظهر في الفاتورة بمجرد فتحها (بلا زر تحديث)
        await Open(sales, customers);
        await customers.NewCommand.ExecuteAsync();
        customers.Editor!.Name = "عميل جديد للتحديث التلقائي";
        await customers.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        await Open(sales, inv);
        Assert.Equal(before + 1, inv.Customers.Count);
        Assert.Contains(inv.Customers, c => c.Name == "عميل جديد للتحديث التلقائي");

        // فاتورة قيد الإدخال لا تُعاد تعبئة قوائمها تحتها
        inv.Customer = inv.Customers.First(c => c.Name == "عميل جديد للتحديث التلقائي");
        var sameList = inv.Customers.ToList();
        await Open(sales, customers);
        await customers.NewCommand.ExecuteAsync();
        customers.Editor!.Name = "عميل ثانٍ";
        await customers.SaveCommand.ExecuteAsync();
        await Open(sales, inv);
        Assert.Equal(sameList, inv.Customers.ToList());
        Assert.Equal("عميل جديد للتحديث التلقائي", inv.Customer!.Name);
        inv.ResetForm();

        // لوحة الوحدة تُحدَّث عند العودة للرئيسية بعد عملية
        var dash = sales.Dashboard!;
        await dash.IdleAsync();
        var invoicesToday = dash.Tiles.Single(t => t.Title == "مبيعات اليوم").Hint;
        await PostCashInvoiceAsync(shell, dialogs);
        sales.SelectedTab = sales.Home;
        await sales.LastActivation;
        Assert.NotEqual(invoicesToday, dash.Tiles.Single(t => t.Title == "مبيعات اليوم").Hint);

        // بلا تغيير: الفتح لا يعيد التحميل
        var version = shell.Session.DataVersion;
        sales.SelectedTab = sales.Home;
        sales.SelectedTab = inv;
        await sales.LastActivation;
        Assert.Equal(version, shell.Session.DataVersion);
        Assert.Empty(_f.Unhandled);
    }
}
