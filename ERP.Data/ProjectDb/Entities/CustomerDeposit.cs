namespace ERP.Data.ProjectDb.Entities;

/// <summary>استلام تأمين (يدخل الصندوق)، إرجاعه (يخرج من الصندوق)، أو رصيد افتتاحي منقول من نظام سابق (بلا صندوق).</summary>
public enum CustomerDepositKind { Receipt, Refund, Opening }

/// <summary>
/// تأمين يودعه العميل كأمانة — مثل تأمين طباعة ستيكر خاص باسمه. منفصل تمامًا عن دين العميل:
/// لا يدخل في الفواتير ولا توزيع الدفعات، وله رصيد مستقل = الاستلامات والافتتاحي − الإرجاعات.
/// </summary>
public class CustomerDeposit
{
    public int Id { get; set; }
    public string DepositNumber { get; set; } = "";
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public DateTime DepositDate { get; set; } = DateTime.Today;
    public CustomerDepositKind Kind { get; set; }
    /// <summary>بالدينار دائمًا (موجب)؛ الاتجاه يحدده النوع.</summary>
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "IQD";
    public decimal? CurrencyAmount { get; set; }
    public string? Purpose { get; set; }
    public int? CustomRecipeId { get; set; }
    public CustomRecipe? CustomRecipe { get; set; }
    public string? Notes { get; set; }
    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public bool IsVoided { get; set; }
    public string? VoidReason { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>الأثر على رصيد التأمين: الإرجاع ينقصه.</summary>
    public decimal SignedAmount => Kind == CustomerDepositKind.Refund ? -Amount : Amount;
}
