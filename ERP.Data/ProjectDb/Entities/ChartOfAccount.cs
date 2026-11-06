namespace ERP.Data.ProjectDb.Entities;

public enum AccountType { Asset, Liability, Equity, Revenue, Expense }

public class ChartOfAccount
{
    public int Id { get; set; }
    public string AccountCode { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public AccountType AccountType { get; set; }

    public int? ParentAccountId { get; set; }
    public ChartOfAccount? ParentAccount { get; set; }

    public bool IsActive { get; set; } = true;
}
