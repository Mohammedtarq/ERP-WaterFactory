/* ============================================================
   الضمان الاجتماعي والتكافل الاجتماعي: مبلغ ثابت يُستقطع شهريًا من راتب الموظف
   (بعملة راتبه) عند تفعيله في بطاقته، ويظهر في سطر الرواتب وفي قيد الاستحقاق
   كمستحقات للجهة (2105 / 2106). قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF COL_LENGTH('Employees', 'HasSocialSecurity') IS NULL
    ALTER TABLE Employees ADD HasSocialSecurity BIT NOT NULL CONSTRAINT DF_Employees_HasSocialSecurity DEFAULT 0,
                              SocialSecurityAmount DECIMAL(18,2) NULL,
                              HasSocialSolidarity BIT NOT NULL CONSTRAINT DF_Employees_HasSocialSolidarity DEFAULT 0,
                              SocialSolidarityAmount DECIMAL(18,2) NULL;
GO

IF COL_LENGTH('PayrollLines', 'SocialSecurityDeduction') IS NULL
    ALTER TABLE PayrollLines ADD SocialSecurityDeduction DECIMAL(18,2) NOT NULL CONSTRAINT DF_PayrollLines_SocialSecurity DEFAULT 0,
                                 SocialSolidarityDeduction DECIMAL(18,2) NOT NULL CONSTRAINT DF_PayrollLines_SocialSolidarity DEFAULT 0;
GO
