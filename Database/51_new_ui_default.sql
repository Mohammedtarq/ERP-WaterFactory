/* ============================================================
   «الواجهة الجديدة» تصبح الافتراضية لكل المستخدمين (لوحة مربعات تنبثق يسار القائمة، وشريط علوي).
   مرة واحدة: يُفعّلها لكل المستخدمين الحاليين ويجعل الافتراضي 1؛ ومن أراد الواجهة القديمة يعيدها من قائمته.
   قابل لإعادة التنفيذ بأمان (لا يتكرر التفعيل بعد تغيير الافتراضي).
   ============================================================ */
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_Users_UseNewUi' AND definition = '((0))')
BEGIN
    EXEC('UPDATE Users SET UseNewUi = 1');
    ALTER TABLE Users DROP CONSTRAINT DF_Users_UseNewUi;
    ALTER TABLE Users ADD CONSTRAINT DF_Users_UseNewUi DEFAULT 1 FOR UseNewUi;
END
GO
