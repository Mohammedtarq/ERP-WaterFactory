using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// تصحيح المستند المخزني: «إلغاء» بسبب إلزامي يعكس الأثر ويبقى المستند ظاهرًا «ملغى»؛ يُمنع إن جعل الرصيد سالبًا
/// أو لمن لا يملك الصلاحية؛ «نسخ» يفتح مستندًا جديدًا بنفس السطور؛ والتأكيد قبل الحفظ للعمليات الحساسة فقط.
/// </summary>
[Collection("app")]
public class StockDocumentVoidTests
{
    private readonly AppFixture _f;
    public StockDocumentVoidTests(AppFixture f) => _f = f;

    private static async Task Open(ModuleViewModel module, SectionViewModel section)
    {
        module.SelectedTab = section;
        await module.IdleAsync();
        await module.LastActivation;
        await section.IdleAsync();
    }

    [Fact]
    public async Task Void_reverses_the_document_keeps_it_visible_and_copy_reopens_its_lines()
    {
        int rawWh;
        await using (var db = _f.NewDb())
        {
            var branch = await db.Warehouses.Where(w => w.Id == _f.MainWarehouseId).Select(w => w.BranchId).FirstAsync();
            var store = new Warehouse { BranchId = branch, Name = "مخزن الإلغاء", WarehouseType = WarehouseType.RawMaterial };
            db.Warehouses.Add(store);
            var cap = new Item { ItemCode = "VD-CAP-1", ItemName = "سدادة — إلغاء", SourcingMethod = SourcingMethod.Purchased };
            db.Items.Add(cap);
            await db.SaveChangesAsync();
            db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = cap.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 });
            await db.SaveChangesAsync();
            rawWh = store.Id;
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        await wh.IdleAsync();
        await wh.RefreshWorkspacesAsync();
        var raw = wh.Workspace(rawWh);
        await Open(wh, raw);

        async Task Receive(decimal qty)
        {
            raw.Operation = raw.OperationOptions.Single(o => o.Value == StockDocumentType.Receipt);
            raw.ShowAllItems = true;
            raw.LineItem = null;
            raw.LineItem = raw.ItemsLookup.Single(i => i.ItemCode == "VD-CAP-1");
            await raw.IdleAsync();
            raw.LineQuantity = qty;
            await raw.AddLineCommand.ExecuteAsync();
            await raw.SaveCommand.ExecuteAsync();
            Assert.Empty(dialogs.Errors);
        }
        decimal Balance() => raw.Balances.Where(b => b.ItemCode == "VD-CAP-1").Sum(b => b.Quantity);

        // الإدخال لا يطلب تأكيدًا (ليس حساسًا ولا كمية غير معتادة)، والحساس يطلبه
        var confirmsBefore = dialogs.Confirms.Count;
        await Receive(1_000);
        Assert.DoesNotContain(dialogs.Confirms.Skip(confirmsBefore), c => c.StartsWith("حفظ مستند"));
        await Receive(500);
        Assert.Equal(1_500m, Balance());

        // إلغاء بلا سبب يُرفض، ثم بسبب: الرصيد يرجع والمستند ظاهر «ملغى» والكشف يُظهر حركة الإلغاء
        raw.ReportFrom = raw.ReportTo = DateTime.Today;
        await raw.LoadReportsCommand.ExecuteAsync();
        var wrong = raw.Documents.First();
        Assert.True(raw.CanVoidDocuments);
        raw.VoidReason = null;
        await raw.VoidDocumentCommand.ExecuteAsync(wrong);
        Assert.Contains(dialogs.Errors, e => e.Contains("سبب الإلغاء"));
        dialogs.Errors.Clear();
        raw.VoidReason = "أُدخل بالخطأ";
        await raw.VoidDocumentCommand.ExecuteAsync(wrong);
        Assert.Empty(dialogs.Errors);
        await raw.IdleAsync();
        Assert.Equal(1_000m, Balance());
        var voided = raw.Documents.Single(d => d.Id == wrong.Id);
        Assert.True(voided.IsVoided);
        Assert.Equal("ملغى — أُدخل بالخطأ", voided.StatusText);
        raw.LedgerItem = null;
        await raw.LoadReportsCommand.ExecuteAsync();
        Assert.Contains(raw.Ledger, l => l.TypeLabel.StartsWith("إلغاء:") && l.Out == 500 && l.Reference == wrong.DocumentNumber);

        // لا يُلغى مرتين
        raw.VoidReason = "مرة ثانية";
        await raw.VoidDocumentCommand.ExecuteAsync(voided);
        Assert.Contains(dialogs.Errors, e => e.Contains("ملغى مسبقًا"));
        dialogs.Errors.Clear();

        // يُمنع إن صُرف من الكمية: استهلاك إنتاج 800 ثم إلغاء إدخال الألف يجعل الرصيد سالبًا
        var first = raw.Documents.Single(d => !d.IsVoided && d.DocumentType == StockDocumentType.Receipt);
        await using (var db = _f.NewDb())
        {
            var item = await db.Items.Where(i => i.ItemCode == "VD-CAP-1").Select(i => i.Id).FirstAsync();
            var admin = await db.Users.Select(u => u.Id).FirstAsync();
            db.StockTransactions.Add(new StockTransaction { ItemId = item, WarehouseId = rawWh, QuantityBaseUnits = -800,
                                                            TransactionType = StockTransactionType.ProductionConsume, CreatedByUserId = admin });
            await db.SaveChangesAsync();
            var blocked = await new WarehouseDocumentService(db).VoidAsync(first.Id, "تجربة", admin);
            Assert.False(blocked.Success);
            Assert.Contains("صُرف أو بيع", blocked.ErrorMessage);
        }

        // نسخ: «عملية جديدة» بنفس السطور والنوع، وملاحظة المصدر
        await raw.CopyDocumentCommand.ExecuteAsync(voided);
        Assert.Equal(0, raw.SelectedTabIndex);
        Assert.Equal(StockDocumentType.Receipt, raw.Operation.Value);
        var line = Assert.Single(raw.Lines);
        Assert.Equal((500m, "سدادة — إلغاء"), (line.QuantityInLevel, line.ItemName));
        Assert.StartsWith($"نسخة من {voided.DocumentNumber}", raw.Notes);
    }
}
