/* ============================================================
   الصناديق (ملاحظة التجربة 4)
   - نوع جديد «Cards» = صندوق البطاقات الإلكترونية: يستقبل تلقائيًا مبلغ كل فاتورة دفعها إلكتروني
     (حركة مرجعها الفاتورة، فتُلغى بإلغائها).
   - «الصندوق العام» في الشاشة = مجموع الصناديق المفعّلة عدا صندوق البطاقات (عرض فقط، لا حركات عليه).
   - حذف صندوق بلا أي حركة، وإيقاف/إعادة تفعيل ما عليه حركات (من الشاشة، للأدمن).
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CashBoxes_BoxType' AND definition LIKE N'%Cards%')
BEGIN
    IF OBJECT_ID('CK_CashBoxes_BoxType', 'C') IS NOT NULL ALTER TABLE CashBoxes DROP CONSTRAINT CK_CashBoxes_BoxType;
    DECLARE @oldBox SYSNAME = (SELECT TOP 1 name FROM sys.check_constraints
                               WHERE parent_object_id = OBJECT_ID('CashBoxes') AND definition LIKE N'%BoxType%');
    IF @oldBox IS NOT NULL EXEC (N'ALTER TABLE CashBoxes DROP CONSTRAINT [' + @oldBox + N']');
    ALTER TABLE CashBoxes ADD CONSTRAINT CK_CashBoxes_BoxType CHECK (BoxType IN (N'Main', N'User', N'Bank', N'Home', N'Cards'));
END;
GO
