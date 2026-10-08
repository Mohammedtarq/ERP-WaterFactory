namespace ERP.Data.ProjectDb.Entities;

public class QualityTest
{
    public int Id { get; set; }
    public string TestName { get; set; } = string.Empty;

    public int? ApplicableItemId { get; set; }   // NULL = ينطبق على كل المنتجات
    public Item? ApplicableItem { get; set; }

    public decimal? StandardMin { get; set; }
    public decimal? StandardMax { get; set; }
    public string? StandardText { get; set; }    // لاختبارات نعم/لا أو وصفية
}

public enum QCOverallResult { Passed, Rejected }
public enum QCLineResult { Pass, Fail }

public class QCBatchResult
{
    public int Id { get; set; }

    public int ProductionOrderId { get; set; }
    public ProductionOrder ProductionOrder { get; set; } = null!;

    public int BatchId { get; set; }
    public ItemBatch Batch { get; set; } = null!;

    public int TestedByUserId { get; set; }
    public User TestedByUser { get; set; } = null!;

    public DateTime TestDate { get; set; } = DateTime.UtcNow;

    /// <summary>القاعدة الافتراضية المتفق عليها: فشل اختبار واحد فقط يكفي لرفض الدفعة كاملة.</summary>
    public QCOverallResult OverallResult { get; set; }

    public ICollection<QCTestResultLine> ResultLines { get; set; } = new List<QCTestResultLine>();
}

public class QCTestResultLine
{
    public int Id { get; set; }

    public int QCBatchResultId { get; set; }
    public QCBatchResult QCBatchResult { get; set; } = null!;

    public int QualityTestId { get; set; }
    public QualityTest QualityTest { get; set; } = null!;

    public string MeasuredValue { get; set; } = string.Empty;
    public QCLineResult Result { get; set; }
}
