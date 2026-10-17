namespace ERP.Presentation.Mvvm;

public static class ArabicText
{
    /// <summary>توحيد الكتابة العربية للبحث: أ/إ/آ ← ا، ة ← ه، ى ← ي، وحذف التشكيل والتطويل — «اسامة» تجد «أسامة».</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var chars = new List<char>(s.Length);
        foreach (var c in s.Trim())
        {
            if (c is >= 'ً' and <= 'ْ' or 'ـ') continue;
            chars.Add(c switch { 'أ' or 'إ' or 'آ' or 'ٱ' => 'ا', 'ة' => 'ه', 'ى' => 'ي', _ => c });
        }
        return new string(chars.ToArray());
    }
}
