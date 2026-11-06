using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ERP.Presentation.Services;

/// <summary>
/// يرفع "رقم نسخة البيانات" في الجلسة بعد أي أمر يكتب في قاعدة البيانات (حفظ، ترحيل إجراء مخزَّن، حذف).
/// الشاشات تقارن هذا الرقم عند فتحها: إن تغيّر منذ آخر تحميل تُحدَّث تلقائيًا، وإلا لا يُعاد أي استعلام.
/// </summary>
public sealed class DataChangeInterceptor : DbCommandInterceptor
{
    private static readonly Regex Writes = new(@"\b(INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly AppSession _session;
    public DataChangeInterceptor(AppSession session) => _session = session;

    private void Check(DbCommand command)
    {
        if (Writes.IsMatch(command.CommandText)) _session.MarkDataChanged();
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    { Check(command); return result; }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken ct = default)
    { Check(command); return ValueTask.FromResult(result); }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    { Check(command); return result; }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
    { Check(command); return ValueTask.FromResult(result); }
}
