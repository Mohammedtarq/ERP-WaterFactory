/* نسخة مصغّرة من قاعدة نظام الرحمة القديم: نفس أسماء الجداول والأعمدة الحقيقية، ببيانات معروفة المجاميع لاختبار النقل.
   الجداول مأخوذة من قائمة أعمدة القاعدة الأصلية (ALRAHMA). */
CREATE TABLE dbo.[Customer] ([id] int NOT NULL, [customer_name] nvarchar(max) NOT NULL, [phone] nvarchar(50) NOT NULL, [add_at] date NOT NULL, [deon] float NOT NULL, [address] nvarchar(max) NULL);
CREATE TABLE dbo.[CustomerPay] ([id] int NOT NULL, [CustmerID] int NOT NULL, [paidamount] float NOT NULL, [note] nvarchar(max) NULL, [date] date NOT NULL, [FWaterID] int NULL);
CREATE TABLE dbo.[Departments] ([DepartmentID] int NOT NULL, [DepartmentName] nvarchar(255) NOT NULL);
CREATE TABLE dbo.[Employees] ([EmployeeID] int NOT NULL, [EmployeeName] nvarchar(255) NOT NULL, [PhoneNumber] nvarchar(20) NULL, [SalaryCurrency] nvarchar(10) NOT NULL, [NominalSalary] float NOT NULL, [ShiftID] int NOT NULL, [JobTitleID] int NOT NULL, [DepartmentID] int NOT NULL, [CanClockOut] bit NOT NULL, [isWorke] bit NOT NULL, [basmaid] int NOT NULL, [photo] image NULL, [daman] int NULL, [berthday] date NULL, [tahselderase] nvarchar(50) NULL, [address] nvarchar(50) NULL, [IsMarried] bit NOT NULL, [FamilyNumber] int NOT NULL, [DateStartWork] datetime NULL, [AllowanceAmount] decimal(18,2) NOT NULL, [AllowanceType] nvarchar(100) NULL);
CREATE TABLE dbo.[EntajDays] ([ENID] int NOT NULL, [datetime] date NOT NULL, [note] nvarchar(max) NULL, [wherehousename] nvarchar(max) NULL);
CREATE TABLE dbo.[entajdetels] ([id] int NOT NULL, [ENID] int NULL, [prodectid] int NULL, [entaj] int NULL, [free] int NULL, [spechalname] nvarchar(max) NULL, [mutabaqientaj] int NULL, [color] nvarchar(50) NULL);
CREATE TABLE dbo.[EntajMashobDetelsTabel] ([id] int NOT NULL, [Mashobentajid] int NOT NULL, [entajdetelsid] int NOT NULL, [number] int NULL);
CREATE TABLE dbo.[entajselldetels] ([id] int NOT NULL, [fwaterdetelid] int NOT NULL, [entajdetelid] int NOT NULL, [number] int NOT NULL);
CREATE TABLE dbo.[EntajTalafDetelsTabel] ([id] int NOT NULL, [talafentajid] int NOT NULL, [entajdetelsid] int NOT NULL, [number] int NULL);
CREATE TABLE dbo.[EstelamAmanat] ([id] int NOT NULL, [cid] int NOT NULL, [amount] float NOT NULL, [curruncy] nvarchar(50) NOT NULL, [allamountiraq] float NOT NULL, [date] date NOT NULL, [note] nvarchar(max) NULL);
CREATE TABLE dbo.[Fwater] ([FID] int NOT NULL, [date] date NOT NULL, [note] nvarchar(max) NULL, [EID] int NULL, [name] nvarchar(max) NULL, [iscompleat] bit NULL, [wherehousename] nvarchar(max) NULL);
CREATE TABLE dbo.[Fwaterdetel] ([deletid] int NOT NULL, [FID] int NOT NULL, [prodectid] int NOT NULL, [number] int NULL, [note] nvarchar(max) NULL, [spesialname] nvarchar(max) NULL, [price] int NULL);
CREATE TABLE dbo.[Information] ([inf_id] int NOT NULL, [name] nvarchar(50) NULL, [logo] image NULL, [phone] nvarchar(50) NULL, [address] nvarchar(50) NULL);
CREATE TABLE dbo.[JobTitles] ([JobTitleID] int NOT NULL, [JobTitleName] nvarchar(255) NOT NULL, [isworker] bit NOT NULL);
CREATE TABLE dbo.[kasat] ([id] int NOT NULL, [name] nvarchar(max) NOT NULL, [userid] int NULL, [amount] float NOT NULL);
CREATE TABLE dbo.[KulafManefactuerTable] ([id] int NOT NULL, [proudectid] int NOT NULL, [rawname] nvarchar(max) NOT NULL, [number] int NOT NULL, [wherehouseId] int NULL, [speshalname] nvarchar(max) NULL, [color] nvarchar(50) NULL);
CREATE TABLE dbo.[Products] ([ProductID] int NOT NULL, [ProductName] nvarchar(50) NOT NULL, [UnitsPerPackage] int NOT NULL);
CREATE TABLE dbo.[RawDetelsProudectTable] ([id] int NOT NULL, [rawProudectId] int NULL, [number] int NOT NULL, [allnumber] int NOT NULL, [date] date NOT NULL, [note] nvarchar(max) NULL, [priceperone] float NOT NULL, [usdtoiq] float NOT NULL, [rawProudectname] nvarchar(max) NOT NULL, [rawwherhousename] nvarchar(max) NOT NULL, [spichalname] nvarchar(max) NULL, [color] nvarchar(50) NULL);
CREATE TABLE dbo.[RawProdectTable] ([id] int NOT NULL, [rawprudectname] nvarchar(max) NOT NULL, [barcode] nvarchar(50) NULL, [wherehouseid] int NOT NULL, [numbertanbeh] int NULL);
CREATE TABLE dbo.[Rwateb] ([SID] int NOT NULL, [EID] int NULL, [salaryname] float NULL, [curuncy] nvarchar(50) NULL, [hwafizmandob] float NULL, [daywork] int NULL, [dayexitwork] int NULL, [salaryperday] float NULL, [salaryperhour] float NULL, [geabday] int NULL, [igazaday] int NULL, [overdaywork] int NULL, [overhourwork] int NULL, [takerhour] int NULL, [allsalary] float NULL, [date] date NULL, [isteqtadaman] float NULL, [isteqtaslfa] float NULL, [otherhwafez] float NULL, [isrteqtaeqoba] float NULL, [mashobat] float NULL, [AllowanceAmount] decimal(18,2) NOT NULL, [AllowanceType] nvarchar(100) NULL);
CREATE TABLE dbo.[Shifts] ([ShiftID] int NOT NULL, [ShiftName] nvarchar(255) NOT NULL, [EntryTime] time NOT NULL, [ExitTime] time NOT NULL, [EntryPermission] int NOT NULL, [ExitPermission] int NOT NULL);
CREATE TABLE dbo.[SlafTabel] ([SlafID] int NOT NULL, [EID] int NULL, [date] date NULL, [amount] int NULL);
CREATE TABLE dbo.[Supplier] ([id] int NOT NULL, [name] nvarchar(max) NOT NULL, [phone] nvarchar(50) NULL, [deon] float NOT NULL);
CREATE TABLE dbo.[TaslemAmanat] ([id] int NOT NULL, [cid] int NOT NULL, [date] date NOT NULL, [amount] float NOT NULL, [note] nvarchar(max) NULL);
GO
INSERT Information (inf_id, name, phone, address) VALUES (1, N'معمل مياه الرحمة', N'07704937129', N'البصرة صناعية حمدان');
INSERT Products VALUES (1, N'330*20', 20), (2, N'330*40', 40);
INSERT RawProdectTable VALUES (1, N'امبولة', NULL, 1, 500000), (2, N'سدادة', NULL, 1, 1000000), (3, N'كارتون', NULL, 1, 10000), (4, N'ليبل', NULL, 1, NULL), (5, N'شرنك', NULL, 1, NULL);
INSERT KulafManefactuerTable VALUES
 (1, 1, N'امبولة', 20, 1, N'', N''), (2, 1, N'سدادة', 20, 1, N'', N''), (3, 1, N'شرنك', 1, 1, N'', N''), (4, 1, N'ليبل', 40, 1, N'', N''),
 (6, 1, N'ليبل', 40, 1, N'زواج سعيد', N''), (90, 1, N'ليبل', 0, 1, N'بدون ليبل', N''),
 (7, 2, N'امبولة', 40, 1, N'', N''), (8, 2, N'سدادة', 40, 1, N'', N''), (9, 2, N'ليبل', 80, 1, N'', N''), (10, 2, N'كارتون', 1, 1, N'', N''),
 (16, 2, N'ليبل', 80, 1, N'مطعم الحسون', N''), (11, 2, N'ليبل', 80, 1, N'كافيه شغف', N'');
INSERT RawDetelsProudectTable (id, rawProudectId, number, allnumber, [date], note, priceperone, usdtoiq, rawProudectname, rawwherhousename, spichalname, color) VALUES
 (1, 1, 1000, 5000, '2026-06-01', N'', 38.44, 0, N'امبولة', N'المواد الاولية', N'', N''),
 (2, 1, 500, 1000, '2025-01-01', N'', 20, 0, N'امبولة', N'المواد الاولية', N'', N''),
 (3, 2, 2000, 9000, '2026-05-18', N'', 3.8, 0, N'سدادة', N'المواد الاولية', N'', N''),
 (4, 2, 300, 1000, '2026-05-18', N'', 3.9, 0, N'سدادة', N'المواد الاولية', N'', N'ابيض'),
 (5, 2, 100, 500, '2025-07-19', N'', 3.8, 0, N'سدادة', N'المواد الاولية', N'اسود', N''),
 (6, 2, 50, 500, '2026-04-07', N'', 3.85, 0, N'سدادة', N'المواد الاولية', N'', N'اسود'),
 (7, 4, 10000, 90000, '2026-05-26', N'', 2.2, 0, N'ليبل', N'المواد الاولية', NULL, NULL),
 (8, 4, 800, 2000, '2026-01-27', N'', 2.5, 0, N'ليبل', N'المواد الاولية', N'مطعم الحسون', N''),
 (9, 4, 0, 1500, '2024-07-22', N'', 2.7, 0, N'ليبل', N'المواد الاولية', N'كافيه شغف', N''),
 (10, 4, 400, 14000, '2026-03-07', N'', 2.3, 0, N'ليبل', N'المواد الاولية', N'رمضان كريم', N''),
 (11, 5, 50, 4000, '2026-05-12', N'', 50, 0, N'شرنك', N'المواد الاولية', N'', N''),
 (12, 3, 20, 1000, '2026-05-10', N'', 335, 0, N'كارتون', N'المواد الاولية', N'', N'');
INSERT Customer (id, customer_name, phone, add_at, deon, address) VALUES
 (1, N'مطعم الحسون', N'07701112222', '2024-01-01', 1000000, N'البصرة - العشار'),
 (2, N'كافيه شغف', N'', '2024-01-01', 0, NULL),
 (3, N'زبون نقدي', N'', '2024-01-01', 0, NULL),
 (4, N'وكيل البصرة', N'', '2024-01-01', -50000, NULL),
 (5, N'محل الزبير', N'', '2024-01-01', 250000, NULL);
INSERT EstelamAmanat VALUES (1, 1, 800000, N'دينار', 800000, '2025-05-01', N'صندوق المعمل'), (2, 2, 500, N'دولار', 775000, '2025-05-02', N'صندوق المعمل');
INSERT TaslemAmanat VALUES (1, 2, '2025-09-04', 775000, N'ارجاع امانات');
INSERT Supplier VALUES (1, N'شركة القوالب', N'', 3718022), (2, N'مطبعة الليبل', NULL, 0), (3, N'مورد السدادات', N'', -100000);
INSERT EntajDays VALUES (1, DATEADD(DAY, -1, CAST(GETDATE() AS DATE)), NULL, N'الانتاج التام');
INSERT entajdetels (id, ENID, prodectid, entaj, free, spechalname, mutabaqientaj, color) VALUES
 (1, 1, 1, 100, 0, N'', 30, N''),
 (2, 1, 2, 50, 0, N'مطعم الحسون', 50, N'اسود'),
 (3, 1, 1, 40, 0, N'رمضان كريم', 10, N'ذهبي'),
 (4, 1, 2, 20, 0, N'', 0, N''),
 (5, 1, 1, 10, 0, N'بدون ليبل', 10, N'');
INSERT Fwater VALUES (1, DATEADD(DAY, -1, CAST(GETDATE() AS DATE)), NULL, 7, NULL, 1, N'الانتاج التام'),
                     (2, DATEADD(DAY, -1, CAST(GETDATE() AS DATE)), NULL, 11, NULL, 1, N'الانتاج التام'),
                     (3, DATEADD(DAY, -1, CAST(GETDATE() AS DATE)), NULL, NULL, N'بيع مباشر', 1, N'الانتاج التام');
INSERT Fwaterdetel VALUES (1, 1, 1, 60, NULL, N'', 2250), (2, 2, 2, 20, NULL, N'', 4250), (3, 3, 2, 25, NULL, N'مطعم الحسون', 4600), (4, 1, 1, 25, NULL, N'رمضان كريم', 2400);
INSERT entajselldetels VALUES (1, 1, 1, 60), (2, 4, 3, 25), (3, 2, 4, 20);
INSERT EntajTalafDetelsTabel VALUES (1, 1, 1, 5);
INSERT EntajMashobDetelsTabel VALUES (1, 1, 1, 5);
INSERT Departments VALUES (1, N'الإنتاج'), (2, N'المبيعات');
INSERT JobTitles VALUES (1, N'عامل خط', 1), (2, N'مندوب', 0);
INSERT Shifts VALUES (1, N'صباحي', '08:00', '16:00', 15, 10);
INSERT Employees (EmployeeID, EmployeeName, PhoneNumber, SalaryCurrency, NominalSalary, ShiftID, JobTitleID, DepartmentID, CanClockOut, isWorke, basmaid, IsMarried, FamilyNumber, DateStartWork, AllowanceAmount) VALUES
 (10, N'أحمد عامل الخط', N'07700000010', N'دينار', 600000, 1, 1, 1, 1, 1, 10, 0, 0, '2024-01-01', 0),
 (11, N'سامي المندوب', NULL, N'دولار', 500, 1, 2, 2, 1, 1, 11, 0, 0, '2025-03-01', 0),
 (12, N'موظف سابق', NULL, N'دينار', 400000, 1, 1, 1, 1, 0, 12, 0, 0, '2023-01-01', 0);
INSERT SlafTabel VALUES (1, 10, '2026-04-01', 300000), (2, 12, '2025-01-01', 100000), (3, 11, '2025-06-01', 0);
INSERT Rwateb (SID, EID, salaryname, [date], isteqtaslfa, AllowanceAmount) VALUES (1, 10, 600000, '2026-04-30', 40000, 0), (2, 10, 600000, '2026-05-31', 50000, 0);
INSERT kasat VALUES (1, N'صندوق المدير', 1, 3000000), (2, N'صندوق سيف', 2, 0), (3, N'صندوق محمد', 3, 15458350);
GO
