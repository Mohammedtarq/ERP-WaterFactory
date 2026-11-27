using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Finance;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// ملاحظة التجربة 4 (الصناديق): صندوق البطاقات الإلكترونية يستقبل البيع الإلكتروني تلقائيًا ويُلغى بإلغاء الفاتورة،
/// و«الصندوق العام» = مجموع المفعّلة عدا البطاقات، والحذف للصندوق بلا حركات فقط، والموقوف مخفي حتى يُطلب،
/// ولكل مستخدم صندوق مفعّل واحد.
/// </summary>
[Collection("app")]
public class TrialFixesBatchETests
{
    private readonly AppFixture _f;
    public TrialFixesBatchETests(AppFixture f) => _f = f;

    [Fact]
    public async Task Cards_box_receives_electronic_sales_general_total_excludes_it_and_empty_boxes_can_be_deleted()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var fin = shell.Open<FinanceModuleViewModel>(ModuleCode.Finance);
        var boxes = fin.Boxes;
        fin.SelectedTab = boxes;
        await fin.LastActivation;
        await boxes.IdleAsync();
        Assert.True(boxes.CanSeeTotals);

        // صندوق البطاقات الإلكترونية: بلا صاحب، ولا يكون افتراضيًا
        boxes.NewBoxName = "صندوق البطاقات هـ";
        boxes.NewBoxType = boxes.BoxTypeOptions.Single(t => t.Value == CashBoxType.Cards);
        boxes.NewBoxDefault = true;
        await boxes.CreateBoxCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("لا يكون افتراضيًا"));
        dialogs.Errors.Clear();
        boxes.NewBoxDefault = false;
        await boxes.CreateBoxCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var cards = boxes.Boxes.Single(b => b.Name == "صندوق البطاقات هـ");
        Assert.True(cards.IsActive);
        var generalBefore = boxes.GeneralTotal;

        // فاتورة دفعها إلكتروني ← مبلغها كاملًا في صندوق البطاقات، والصندوق العام لا يتغير
        int invoiceId;
        decimal total;
        await using (var db = _f.NewDb())
        {
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            var sales = new SalesService(db);
            var (_, id) = await sales.CreateInvoiceAsync(new SalesInvoiceHeaderInput(_f.DirectId, _f.MainWarehouseId, DateTime.Today, InvoicePaymentMethod.Electronic), admin.Id);
            Assert.True((await sales.AddLineAsync(id!.Value, new SalesInvoiceLineInput(_f.WaterItemId, _f.CartonLevelId, 1), admin.Id)).Success);
            var (posted, summary) = await sales.PostInvoiceAsync(id.Value, admin.Id);
            Assert.True(posted.Success, posted.ErrorMessage);
            (invoiceId, total) = (id.Value, summary!.TotalAmount);
            var tx = await db.CashBoxTransactions.Include(t => t.CashBox).SingleAsync(t => t.ReferenceTable == "SalesInvoices" && t.ReferenceId == invoiceId);
            Assert.Equal(("صندوق البطاقات هـ", total, CashBoxTxType.SalesReceipt), (tx.CashBox.Name, tx.Amount, tx.TxType));
        }
        await boxes.RefreshCommand.ExecuteAsync();
        Assert.Equal(total, boxes.Boxes.Single(b => b.Name == "صندوق البطاقات هـ").Balance);
        Assert.Equal(generalBefore, boxes.GeneralTotal);                      // البطاقات خارج الصندوق العام
        Assert.True(boxes.CardsTotal >= total);
        await using (var db = _f.NewDb())
        {
            var activeNonCards = await db.CashBoxTransactions.Where(t => !t.IsVoided && t.CashBox.IsActive && t.CashBox.BoxType != CashBoxType.Cards).SumAsync(t => t.Amount);
            Assert.Equal(activeNonCards, boxes.GeneralTotal);

            // إلغاء الفاتورة يلغي حركة صندوق البطاقات
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            Assert.True((await new SalesService(db).VoidInvoiceAsync(invoiceId, "اختبار البطاقات", admin.Id)).Success);
            Assert.True(await db.CashBoxTransactions.Where(t => t.ReferenceTable == "SalesInvoices" && t.ReferenceId == invoiceId).AllAsync(t => t.IsVoided));
        }

        // حذف صندوق بلا حركات؛ وما عليه حركات يُرفض حذفه (يُوقف بدلًا من ذلك)
        boxes.NewBoxName = "صندوق للحذف هـ";
        boxes.NewBoxType = boxes.BoxTypeOptions.Single(t => t.Value == CashBoxType.Bank);
        await boxes.CreateBoxCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        await boxes.DeleteBoxCommand.ExecuteAsync(boxes.Boxes.Single(b => b.Name == "صندوق للحذف هـ"));
        Assert.Empty(dialogs.Errors);
        Assert.DoesNotContain(boxes.Boxes, b => b.Name == "صندوق للحذف هـ");
        await boxes.DeleteBoxCommand.ExecuteAsync(boxes.Boxes.Single(b => b.Name == "صندوق البطاقات هـ"));
        Assert.Contains(dialogs.Errors, e => e.Contains("حركات") && e.Contains("أوقفه"));
        dialogs.Errors.Clear();

        // الموقوف مخفي حتى «إظهار الموقوفة»، ويُعاد تفعيله منها
        boxes.ShowInactive = false;
        await boxes.IdleAsync();
        await boxes.ToggleActiveCommand.ExecuteAsync(boxes.Boxes.Single(b => b.Name == "صندوق البطاقات هـ"));
        Assert.Empty(dialogs.Errors);
        Assert.DoesNotContain(boxes.Boxes, b => b.Name == "صندوق البطاقات هـ");
        boxes.ShowInactive = true;
        await boxes.IdleAsync();
        Assert.False(boxes.Boxes.Single(b => b.Name == "صندوق البطاقات هـ").IsActive);

        // لكل مستخدم صندوق مفعّل واحد
        boxes.NewBoxType = boxes.BoxTypeOptions.Single(t => t.Value == CashBoxType.User);
        boxes.NewBoxOwner = boxes.Users.Single(u => u.Username == AppFixture.ClerkUser);
        boxes.NewBoxName = "صندوق الموظف هـ 1";
        boxes.NewBoxActive = true;
        await boxes.CreateBoxCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        boxes.NewBoxType = boxes.BoxTypeOptions.Single(t => t.Value == CashBoxType.User);
        boxes.NewBoxOwner = boxes.Users.Single(u => u.Username == AppFixture.ClerkUser);
        boxes.NewBoxName = "صندوق الموظف هـ 2";
        boxes.NewBoxActive = true;
        await boxes.CreateBoxCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("صندوق مفعّل"));
        dialogs.Errors.Clear();
        // يُترك صندوق الموظف موقوفًا حتى لا يغيّر مسار نقد الاختبارات الأخرى
        await boxes.ToggleActiveCommand.ExecuteAsync(boxes.Boxes.Single(b => b.Name == "صندوق الموظف هـ 1"));
        Assert.Empty(dialogs.Errors);

        await shell.IdleAllAsync();
        Assert.Empty(_f.Unhandled);
    }
}
