using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>تأمينات العملاء على نظام مثبّت كاملًا: الصندوق والقيد ورصيد التأمين منفصل عن الدين، والإلغاء والحدود.</summary>
[Collection("provisioned")]
public class CustomerDepositTests
{
    private readonly ProvisionedFixture _f;
    public CustomerDepositTests(ProvisionedFixture f) => _f = f;

    [Fact]
    public async Task Deposits_hit_the_box_and_ledger_but_never_the_customer_debt()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var admin = _f.AdminLocalId;
        var customer = new Customer { Name = "مطعم الحسون — تأمين ستيكر" };
        var other = new Customer { Name = "عميل آخر" };
        db.Customers.AddRange(customer, other);
        await db.SaveChangesAsync();

        var svc = new CustomerDepositService(db);
        var cash = new CashBoxService(db);
        var boxId = await db.CashBoxes.Where(b => b.IsActive && b.IsDefault).Select(b => b.Id).FirstAsync();
        var boxBefore = await cash.GetBalanceAsync(boxId);
        var debtBefore = (await new SalesService(db).GetCustomerBalancesAsync()).FirstOrDefault(b => b.CustomerId == customer.Id)?.Balance ?? 0;

        // استلام بالدينار لغرض ستيكر خاص
        var (r1, d1) = await svc.ReceiveAsync(customer.Id, 250_000, DateTime.Today, admin, purpose: "تأمين طباعة ستيكر خاص", cashBoxId: boxId);
        Assert.True(r1.Success, r1.ErrorMessage);
        Assert.StartsWith($"DP-{DateTime.Today.Year}-", d1!.DepositNumber);
        Assert.Equal(250_000, await svc.GetBalanceAsync(customer.Id));
        Assert.Equal(boxBefore + 250_000, await cash.GetBalanceAsync(boxId));
        var boxTx = await db.CashBoxTransactions.SingleAsync(t => t.ReferenceTable == "CustomerDeposits" && t.ReferenceId == d1.Id);
        Assert.Equal(CashBoxTxType.CustomerDepositIn, boxTx.TxType);

        // القيد: مدين الصندوق / دائن تأمينات العملاء
        var lines = await db.JournalEntryLines.Where(l => l.JournalEntryId == d1.JournalEntryId).Include(l => l.Account).ToListAsync();
        Assert.Equal(250_000, lines.Single(l => l.Account.AccountCode == "1101").Debit);
        Assert.Equal(250_000, lines.Single(l => l.Account.AccountCode == "2104").Credit);

        // عملة أجنبية: يلزم المبلغ بعملته
        var (bad, _) = await svc.ReceiveAsync(customer.Id, 150_000, DateTime.Today, admin, currency: "USD");
        Assert.False(bad.Success);
        var (r2, d2) = await svc.ReceiveAsync(customer.Id, 150_000, DateTime.Today, admin, currency: "usd", currencyAmount: 100, cashBoxId: boxId);
        Assert.True(r2.Success, r2.ErrorMessage);
        Assert.Equal("USD", d2!.Currency);

        // الإرجاع لا يتجاوز الرصيد، ويخرج من الصندوق
        var (over, _) = await svc.RefundAsync(customer.Id, 400_001, DateTime.Today, admin, cashBoxId: boxId);
        Assert.False(over.Success);
        Assert.Contains("400,000", over.ErrorMessage);
        var (r3, d3) = await svc.RefundAsync(customer.Id, 100_000, DateTime.Today, admin, "إلغاء الستيكر", cashBoxId: boxId);
        Assert.True(r3.Success, r3.ErrorMessage);
        Assert.Equal(300_000, await svc.GetBalanceAsync(customer.Id));
        Assert.Equal(boxBefore + 300_000, await cash.GetBalanceAsync(boxId));

        // الرصيد الافتتاحي (نقل من نظام سابق): بلا حركة صندوق
        var (r4, d4) = await svc.OpeningAsync(customer.Id, 50_000, DateTime.Today, admin, "من نظام الرحمة");
        Assert.True(r4.Success, r4.ErrorMessage);
        Assert.False(await db.CashBoxTransactions.AnyAsync(t => t.ReferenceTable == "CustomerDeposits" && t.ReferenceId == d4!.Id));
        Assert.Equal(350_000, await svc.GetBalanceAsync(customer.Id));
        Assert.Equal(boxBefore + 300_000, await cash.GetBalanceAsync(boxId));

        // الوصفة المخصصة يجب أن تكون للعميل نفسه
        var product = await db.Items.FirstAsync(i => i.SourcingMethod == SourcingMethod.Manufactured);
        var foreignRecipe = new CustomRecipe { FinishedItemId = product.Id, CustomerId = other.Id, Name = "ستيكر عميل آخر" };
        db.CustomRecipes.Add(foreignRecipe);
        await db.SaveChangesAsync();
        var (wrongRecipe, _) = await svc.ReceiveAsync(customer.Id, 10_000, DateTime.Today, admin, customRecipeId: foreignRecipe.Id);
        Assert.False(wrongRecipe.Success);

        // دين العميل لم يتأثر إطلاقًا
        var debtAfter = (await new SalesService(db).GetCustomerBalancesAsync()).FirstOrDefault(b => b.CustomerId == customer.Id)?.Balance ?? 0;
        Assert.Equal(debtBefore, debtAfter);

        // الإلغاء: للأدمن فقط، ولا يجعل الرصيد سالبًا
        var clerkRole = await db.Roles.FirstAsync(r => r.Name == "موظف مبيعات");
        var clerk = new User { Username = "dep_clerk", PasswordHash = PasswordHasher.Hash("x"), RoleId = clerkRole.Id };
        db.Users.Add(clerk);
        await db.SaveChangesAsync();
        Assert.False((await svc.VoidAsync(d1.Id, "خطأ", clerk.Id)).Success);
        Assert.False((await svc.VoidAsync(d1.Id, " ", admin)).Success);
        var v1 = await svc.VoidAsync(d1.Id, "سند مكرر", admin);
        Assert.True(v1.Success, v1.ErrorMessage);
        Assert.Equal(100_000, await svc.GetBalanceAsync(customer.Id));
        Assert.Equal(boxBefore + 50_000, await cash.GetBalanceAsync(boxId));
        var negative = await svc.VoidAsync(d2.Id, "تجربة", admin);   // 100,000 − 150,000 < 0
        Assert.False(negative.Success);
        Assert.Contains("سالبًا", negative.ErrorMessage);
        Assert.False((await svc.VoidAsync(d1.Id, "مرة ثانية", admin)).Success);

        // السجل التراكمي والأرصدة وحساب التأمينات في الدفتر يساوي رصيد التأمين
        var history = await svc.GetHistoryAsync(customer.Id);
        Assert.Equal(4, history.Count);
        Assert.True(history[0].IsVoided);
        Assert.Equal(100_000, history[^1].Balance);
        Assert.Equal("100.00 USD", history[1].CurrencyText);
        var row = (await svc.GetBalancesAsync()).Single(b => b.CustomerId == customer.Id);
        Assert.Equal(100_000, row.Balance);
        var depositAccount = await db.ChartOfAccounts.SingleAsync(a => a.AccountCode == "2104");
        var ledgerIds = await db.CustomerDeposits.Where(d => d.CustomerId == customer.Id).Select(d => d.Id).ToListAsync();
        var ledger = await db.JournalEntryLines.Where(l => l.AccountId == depositAccount.Id && l.JournalEntry.SourceTable == "CustomerDeposits"
                                                            && ledgerIds.Contains(l.JournalEntry.SourceId!.Value))
                             .SumAsync(l => l.Credit - l.Debit);
        Assert.Equal(100_000, ledger);
    }
}
