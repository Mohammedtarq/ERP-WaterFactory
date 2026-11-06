using System.Text;
using Microsoft.Data.SqlClient;

namespace ERP.Data.Import;

/// <summary>
/// يقرأ قاعدة نظام الرحمة القديم (قراءة فقط — لا يكتب فيها شيئًا) ويبني خطة النقل:
/// البيانات الأساسية + الأرصدة الافتتاحية فقط؛ السجل التاريخي يبقى في القاعدة القديمة.
/// </summary>
public static class RahmaLegacyReader
{
    /// <summary>قواعد البيانات على نفس السيرفر التي تحمل جداول نظام الرحمة (يكفي صلاحية قراءة).</summary>
    public static async Task<List<RahmaDatabaseCandidate>> FindCandidatesAsync(string serverConnectionString)
    {
        var b = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(b.ConnectionString);
        await conn.OpenAsync();
        var names = new List<string>();
        await using (var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) names.Add(r.GetString(0));

        var result = new List<RahmaDatabaseCandidate>();
        foreach (var name in names)
        {
            var q = Quote(name);
            await using var probe = new SqlCommand(
                $"SELECT CASE WHEN OBJECT_ID(N'{q}.dbo.Fwater', 'U') IS NOT NULL AND OBJECT_ID(N'{q}.dbo.entajdetels', 'U') IS NOT NULL " +
                $"AND OBJECT_ID(N'{q}.dbo.Customer', 'U') IS NOT NULL THEN 1 ELSE 0 END", conn);
            // قاعدة قد تُحذف أو تُقفل أو تُستعاد بين القائمة والفحص: تُتخطى بدل إسقاط البحث كله
            try { if ((int)(await probe.ExecuteScalarAsync())! != 1) continue; }
            catch (SqlException) { continue; }
            DateTime? last = null;
            try
            {
                await using var lastCmd = new SqlCommand($"SELECT MAX(CAST([date] AS DATETIME2)) FROM {q}.dbo.Fwater", conn);
                last = await lastCmd.ExecuteScalarAsync() is DateTime d ? d : null;
            }
            catch (SqlException) { }
            result.Add(new RahmaDatabaseCandidate(name, last));
        }
        return result;
    }

    public static async Task<RahmaImportPlan> AnalyzeAsync(string legacyConnectionString)
    {
        var csb = new SqlConnectionStringBuilder(legacyConnectionString);
        await using var conn = new SqlConnection(legacyConnectionString);
        await conn.OpenAsync();

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand("SELECT name FROM sys.tables", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) tables.Add(r.GetString(0));
        foreach (var required in new[] { "Products", "Customer", "Supplier", "RawDetelsProudectTable", "entajdetels", "KulafManefactuerTable" })
            if (!tables.Contains(required))
                throw new InvalidOperationException($"القاعدة \"{csb.InitialCatalog}\" ليست قاعدة نظام الرحمة: الجدول {required} غير موجود");

        async Task<List<object?[]>> Rows(string sql, params string[] needTables)
        {
            var list = new List<object?[]>();
            if (needTables.Any(t => !tables.Contains(t))) return list;
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var row = new object?[r.FieldCount];
                for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                list.Add(row);
            }
            return list;
        }

        string? company = (await Rows("SELECT TOP 1 name FROM Information ORDER BY inf_id", "Information")).Select(r => Str(r[0])).FirstOrDefault();
        DateTime? last = null;
        foreach (var (t, col) in new[] { ("Fwater", "date"), ("CustomerPay", "date"), ("EntajDays", "datetime"), ("RawDetelsProudectTable", "date") })
            foreach (var r in await Rows($"SELECT MAX(CAST([{col}] AS DATETIME2)) FROM {t}", t))
                if (r[0] is DateTime d && (last is null || d > last)) last = d;

        var plan = new RahmaImportPlan { SourceServer = csb.DataSource, SourceDatabase = csb.InitialCatalog, CompanyName = company, LastActivity = last };
        if (last is DateTime l && (DateTime.Today - l.Date).TotalDays > 3)
            plan.Warnings.Add($"آخر حركة في القاعدة القديمة بتاريخ {l:yyyy/MM/dd} — تأكد أنها النسخة الأحدث قبل النقل الفعلي");

        // ---------------- المنتجات التامة ----------------
        var kulaf = (await Rows("SELECT proudectid, rawname, number, ISNULL(speshalname, N''), ISNULL(color, N'') FROM KulafManefactuerTable"))
            .Select(r => (product: Int(r[0]), raw: Clean(Str(r[1])), number: Dec(r[2]), special: Clean(Str(r[3])), color: Clean(Str(r[4])))).ToList();
        var lastPrices = (await Rows(@"SELECT prodectid, price FROM (
                SELECT prodectid, price, ROW_NUMBER() OVER (PARTITION BY prodectid ORDER BY FID DESC, deletid DESC) rn
                FROM Fwaterdetel WHERE price > 0 AND ISNULL(spesialname, N'') = N'') x WHERE rn = 1", "Fwaterdetel"))
            .ToDictionary(r => Int(r[0]), r => Dec(r[1]));
        foreach (var r in await Rows("SELECT ProductID, ProductName, UnitsPerPackage FROM Products ORDER BY ProductID"))
        {
            var id = Int(r[0]);
            var units = Math.Max(1, Int(r[2]));
            var name = Clean(Str(r[1]));
            var mine = kulaf.Where(k => k.product == id && k.special.Length == 0).ToList();
            var level = mine.Any(k => KindOf(k.raw) == RahmaRawKind.Carton) ? "كارتون" : mine.Any(k => KindOf(k.raw) == RahmaRawKind.Shrink) ? "شرنك" : "عبوة";
            var size = name.Split('*', '×', 'x', 'X')[0].Trim();
            plan.Products.Add(new RahmaProductPlan
            {
                LegacyId = id, LegacyName = name, UnitsPerPack = units, PackLevelName = level,
                NewCode = $"RH-{size}-{(level == "كارتون" ? "C" : level == "شرنك" ? "S" : "P")}{units}",
                NewName = $"ماء {Brand(company)} {size} مل — {level} {units}",
                PackPrice = lastPrices.GetValueOrDefault(id)
            });
        }

        // ---------------- المواد الأولية (بحسب النوع + الاسم الخاص/اللون) ----------------
        var alerts = (await Rows("SELECT rawprudectname, numbertanbeh FROM RawProdectTable", "RawProdectTable"))
            .GroupBy(r => KindOf(Clean(Str(r[0])))).ToDictionary(g => g.Key, g => g.Select(r => r[1] is null ? (decimal?)null : Dec(r[1])).FirstOrDefault());
        var lots = await Rows(@"WITH l AS (
                SELECT LTRIM(RTRIM(rawProudectname)) AS nm, LTRIM(RTRIM(ISNULL(spichalname, N''))) AS sp, LTRIM(RTRIM(ISNULL(color, N''))) AS co,
                       number, priceperone,
                       ROW_NUMBER() OVER (PARTITION BY LTRIM(RTRIM(rawProudectname)), LTRIM(RTRIM(ISNULL(spichalname, N''))), LTRIM(RTRIM(ISNULL(color, N'')))
                                          ORDER BY CASE WHEN priceperone > 0 THEN 0 ELSE 1 END, [date] DESC, id DESC) AS rn
                FROM RawDetelsProudectTable)
            SELECT nm, sp, co, SUM(CAST(number AS DECIMAL(18,3))),
                   -- كلفة الرصيد المنقول = المتوسط المرجّح للدفعات المتبقية بأسعارها، وإلا سعر آخر دفعة
                   ISNULL(SUM(CASE WHEN number > 0 AND priceperone > 0 THEN CAST(number AS DECIMAL(18,3)) * CAST(priceperone AS DECIMAL(18,4)) END)
                          / NULLIF(SUM(CASE WHEN number > 0 AND priceperone > 0 THEN CAST(number AS DECIMAL(18,3)) END), 0),
                          MAX(CASE WHEN rn = 1 THEN priceperone END)),
                   COUNT(*)
            FROM l GROUP BY nm, sp, co");
        var raw = new Dictionary<string, (RahmaRawKind kind, string legacyName, string descriptor, decimal remaining, decimal? cost, int lots)>();
        void AddRaw(string legacyName, string special, string color, decimal remaining, decimal? cost, int lotCount)
        {
            var kind = KindOf(legacyName);
            if (kind == RahmaRawKind.Label && IsNoLabel(special)) return;           // "بدون ليبل" ليست مادة
            var descriptor = Descriptor(kind, special, color);
            var key = RawKey(kind, kind == RahmaRawKind.Other ? legacyName : "", descriptor);
            if (raw.TryGetValue(key, out var e))
                raw[key] = (e.kind, e.legacyName, e.descriptor, e.remaining + remaining, e.cost ?? cost, e.lots + lotCount);
            else
                raw[key] = (kind, legacyName, descriptor, remaining, cost, lotCount);
        }
        foreach (var r in lots) AddRaw(Clean(Str(r[0])), Clean(Str(r[1])), Clean(Str(r[2])), Dec(r[3]), r[4] is null ? null : Math.Round(Dec(r[4]), 4), Int(r[5]));
        foreach (var k in kulaf.Where(k => k.number > 0)) AddRaw(k.raw, k.special, k.color, 0, null, 0);   // مكونات الوصفات حتى إن لم يبقَ منها رصيد

        var prefixes = new Dictionary<RahmaRawKind, (string code, string name)>
        {
            [RahmaRawKind.Preform] = ("PRE", "امبولة (بريفورم)"), [RahmaRawKind.Cap] = ("CAP", "سدادة"),
            [RahmaRawKind.Label] = ("LBL", "ليبل"), [RahmaRawKind.Shrink] = ("SHR", "نايلون شرنك"),
            [RahmaRawKind.Carton] = ("CTN", "كارتون"), [RahmaRawKind.Other] = ("OTH", "")
        };
        foreach (var g in raw.Values.GroupBy(v => v.kind).OrderBy(g => g.Key))
        {
            var n = 0;
            foreach (var v in g.OrderBy(v => v.descriptor.Length > 0).ThenBy(v => v.legacyName).ThenBy(v => v.descriptor, StringComparer.Ordinal))
            {
                var (code, baseName) = prefixes[v.kind];
                if (baseName.Length == 0) baseName = v.legacyName;
                var isGeneral = v.descriptor.Length == 0 && v.kind != RahmaRawKind.Other;
                var newCode = isGeneral ? $"RH-{code}" : $"RH-{code}-{++n:D3}";
                var newName = v.descriptor.Length == 0 ? (v.kind == RahmaRawKind.Label ? "ليبل عام (الرحمة)" : baseName) : $"{baseName} — {v.descriptor}";
                plan.RawMaterials.Add(new RahmaRawPlan
                {
                    Key = RawKey(v.kind, v.kind == RahmaRawKind.Other ? v.legacyName : "", v.descriptor), Kind = v.kind, LegacyName = v.legacyName,
                    Descriptor = v.descriptor, NewCode = newCode, NewName = newName, LegacyRemaining = v.remaining, Quantity = Math.Max(0, v.remaining),
                    UnitCost = v.cost, Lots = v.lots, AlertLevel = isGeneral ? alerts.GetValueOrDefault(v.kind) : null
                });
                if (v.remaining < 0) plan.Warnings.Add($"رصيد سالب في النظام القديم للمادة \"{newName}\" ({v.remaining:N0}) — نُقل صفرًا");
            }
        }
        foreach (var m in plan.RawMaterials.Where(m => m.UnitCost is null && m.Quantity > 0))
            plan.Warnings.Add($"لا يوجد سعر شراء للمادة \"{m.NewName}\" — أدخل الكلفة من الأصناف بعد النقل لتظهر في المطابقة");

        // ---------------- الوصفات: الأساسية لكل منتج + الملصقات الخاصة ----------------
        foreach (var p in plan.Products)
        {
            var lines = new List<(string, decimal, string)>();
            foreach (var k in kulaf.Where(k => k.product == p.LegacyId && k.special.Length == 0 && k.number > 0))
            {
                var kind = KindOf(k.raw);
                var key = RawKey(kind, kind == RahmaRawKind.Other ? k.raw : "", Descriptor(kind, "", k.color));
                lines.Add((key, Math.Round(k.number / p.UnitsPerPack, 6), RoleOf(kind, k.raw)));
            }
            plan.Boms[p.LegacyId] = lines;
        }

        var customers = (await Rows("SELECT id, customer_name, phone, address, deon FROM Customer ORDER BY id"))
            .Select(r => (id: Int(r[0]), name: Clean(Str(r[1])), phone: NullIfEmpty(Str(r[2])), address: NullIfEmpty(Str(r[3])), deon: Dec(r[4]))).ToList();
        var byNorm = customers.GroupBy(c => Normalize(c.name)).ToDictionary(g => g.Key, g => g.First());

        var specialFromProduction = (await Rows("SELECT DISTINCT prodectid, LTRIM(RTRIM(spechalname)) FROM entajdetels WHERE ISNULL(LTRIM(RTRIM(spechalname)), N'') <> N''"))
            .Select(r => (product: Int(r[0]), special: Clean(Str(r[1]))));
        var specialPairs = kulaf.Where(k => k.special.Length > 0 && KindOf(k.raw) == RahmaRawKind.Label && k.number > 0)
            .Select(k => (k.product, k.special)).Concat(specialFromProduction)
            .Where(x => !IsNoLabel(x.special)).Distinct().ToList();
        foreach (var (product, special) in specialPairs.OrderBy(x => x.product).ThenBy(x => x.special, StringComparer.Ordinal))
        {
            var p = plan.Products.FirstOrDefault(x => x.LegacyId == product);
            var labelKey = RawKey(RahmaRawKind.Label, "", special);
            if (p is null || plan.RawMaterials.All(m => m.Key != labelKey)) continue;
            var perPack = kulaf.FirstOrDefault(k => k.product == product && k.special == special && KindOf(k.raw) == RahmaRawKind.Label).number;
            if (perPack <= 0) perPack = kulaf.FirstOrDefault(k => k.product == product && k.special.Length == 0 && KindOf(k.raw) == RahmaRawKind.Label).number;
            if (perPack <= 0) continue;
            var match = MatchCustomer(special, byNorm);
            plan.Recipes.Add(new RahmaRecipePlan
            {
                ProductLegacyId = product, SpecialName = special, LabelKey = labelKey, LabelsPerUnit = Math.Round(perPack / p.UnitsPerPack, 6),
                CustomerLegacyId = match?.id, CustomerName = match?.name
            });
        }

        // ---------------- رصيد المنتج التام ----------------
        foreach (var r in await Rows(@"SELECT d.prodectid, LTRIM(RTRIM(ISNULL(d.spechalname, N''))), LTRIM(RTRIM(ISNULL(d.color, N''))),
                       SUM(CAST(ISNULL(d.entaj, 0) AS DECIMAL(18,3))), SUM(CAST(ISNULL(d.mutabaqientaj, 0) AS DECIMAL(18,3))),
                       SUM(ISNULL(s.n, 0)), SUM(ISNULL(t.n, 0)), SUM(ISNULL(m.n, 0))
                FROM entajdetels d
                LEFT JOIN (SELECT entajdetelid AS id, SUM(CAST(number AS DECIMAL(18,3))) n FROM entajselldetels GROUP BY entajdetelid) s ON s.id = d.id
                LEFT JOIN (SELECT entajdetelsid AS id, SUM(CAST(ISNULL(number, 0) AS DECIMAL(18,3))) n FROM EntajTalafDetelsTabel GROUP BY entajdetelsid) t ON t.id = d.id
                LEFT JOIN (SELECT entajdetelsid AS id, SUM(CAST(ISNULL(number, 0) AS DECIMAL(18,3))) n FROM EntajMashobDetelsTabel GROUP BY entajdetelsid) m ON m.id = d.id
                GROUP BY d.prodectid, LTRIM(RTRIM(ISNULL(d.spechalname, N''))), LTRIM(RTRIM(ISNULL(d.color, N'')))",
                "entajselldetels", "EntajTalafDetelsTabel", "EntajMashobDetelsTabel"))
        {
            var p = plan.Products.FirstOrDefault(x => x.LegacyId == Int(r[0]));
            if (p is null) continue;
            var recorded = Dec(r[4]);
            var computed = Dec(r[3]) - Dec(r[5]) - Dec(r[6]) - Dec(r[7]);
            if (recorded <= 0 && computed <= 0) continue;
            plan.FinishedStock.Add(new RahmaFinishedStockPlan
            {
                ProductLegacyId = p.LegacyId, ProductName = p.LegacyName, SpecialName = Clean(Str(r[1])), Color = Clean(Str(r[2])),
                ProducedPacks = Dec(r[3]), RecordedPacks = recorded, ComputedPacks = computed, Packs = Math.Max(0, recorded)
            });
        }
        var mismatch = plan.FinishedStock.Count(f => f.RecordedPacks != f.ComputedPacks);
        if (mismatch > 0)
            plan.Warnings.Add($"{mismatch} رصيد منتج تام يختلف فيه \"المتبقي المسجل\" عن \"المحسوب من الحركات\" — المنقول هو المسجل؛ عدّله بعد الجرد الفعلي");

        // ---------------- العملاء والتأمينات ----------------
        var depositsIn = (await Rows("SELECT cid, SUM(allamountiraq) FROM EstelamAmanat GROUP BY cid", "EstelamAmanat")).ToDictionary(r => Int(r[0]), r => Dec(r[1]));
        var depositsOut = (await Rows("SELECT cid, SUM(amount) FROM TaslemAmanat GROUP BY cid", "TaslemAmanat")).ToDictionary(r => Int(r[0]), r => Dec(r[1]));
        foreach (var c in customers)
        {
            var deposit = depositsIn.GetValueOrDefault(c.id) - depositsOut.GetValueOrDefault(c.id);
            if (deposit < 0) plan.Warnings.Add($"المُرجَع من تأمينات العميل \"{c.name}\" أكبر من المستلم ({deposit:N0}) — لم يُنقل له رصيد تأمين");
            plan.Customers.Add(new RahmaCustomerPlan
            {
                LegacyId = c.id, Name = c.name.Length > 0 ? c.name : $"عميل {c.id}", Phone = c.phone, Address = c.address,
                Balance = Math.Round(c.deon, 2), DepositBalance = Math.Max(0, Math.Round(deposit, 2))
            });
        }
        if (plan.CustomersCredit > 0)
            plan.Warnings.Add($"{plan.Customers.Count(c => c.Balance < 0)} عميل برصيد دائن (لهم عندنا) بمجموع {plan.CustomersCredit:N0} — يُنقل كسند قبض افتتاحي");

        foreach (var r in await Rows("SELECT id, name, phone, deon FROM Supplier ORDER BY id"))
            plan.Suppliers.Add(new RahmaSupplierPlan { LegacyId = Int(r[0]), Name = Clean(Str(r[1])), Phone = NullIfEmpty(Str(r[2])), Balance = Math.Round(Dec(r[3]), 2) });

        // ---------------- الموظفون ----------------
        var departments = (await Rows("SELECT DepartmentID, DepartmentName FROM Departments", "Departments")).ToDictionary(r => Int(r[0]), r => Clean(Str(r[1])));
        var jobs = (await Rows("SELECT JobTitleID, JobTitleName FROM JobTitles", "JobTitles")).ToDictionary(r => Int(r[0]), r => Clean(Str(r[1])));
        foreach (var r in await Rows("SELECT ShiftID, ShiftName, EntryTime, ExitTime, EntryPermission, ExitPermission FROM Shifts ORDER BY ShiftID", "Shifts"))
            plan.Shifts.Add(new RahmaShiftPlan
            {
                LegacyId = Int(r[0]), Name = Clean(Str(r[1])), CheckIn = r[2] is TimeSpan a ? a : new TimeSpan(8, 0, 0),
                CheckOut = r[3] is TimeSpan b ? b : new TimeSpan(16, 0, 0), CheckInGrace = Math.Max(0, Int(r[4])), CheckOutGrace = Math.Max(0, Int(r[5]))
            });
        var loans = (await Rows("SELECT EID, SUM(CAST(amount AS DECIMAL(18,2))) FROM SlafTabel WHERE amount > 0 AND EID IS NOT NULL GROUP BY EID", "SlafTabel"))
            .ToDictionary(r => Int(r[0]), r => Dec(r[1]));
        var lastInstallments = (await Rows(@"SELECT EID, isteqtaslfa FROM (
                SELECT EID, isteqtaslfa, ROW_NUMBER() OVER (PARTITION BY EID ORDER BY [date] DESC, SID DESC) rn FROM Rwateb WHERE isteqtaslfa > 0) x WHERE rn = 1", "Rwateb"))
            .ToDictionary(r => Int(r[0]), r => Dec(r[1]));
        var repIds = (await Rows("SELECT DISTINCT EID FROM Fwater WHERE EID IS NOT NULL", "Fwater")).Select(r => Int(r[0])).ToHashSet();
        foreach (var r in await Rows(@"SELECT EmployeeID, EmployeeName, PhoneNumber, SalaryCurrency, NominalSalary, ShiftID, JobTitleID, DepartmentID, DateStartWork
                                       FROM Employees WHERE isWorke = 1 ORDER BY EmployeeID", "Employees"))
        {
            var id = Int(r[0]);
            var loan = Math.Round(loans.GetValueOrDefault(id), 2);
            var inst = lastInstallments.GetValueOrDefault(id);
            plan.Employees.Add(new RahmaEmployeePlan
            {
                LegacyId = id, Name = Clean(Str(r[1])), Phone = NullIfEmpty(Str(r[2])), IsUsd = Clean(Str(r[3])).Contains("دولار") || Clean(Str(r[3])).Equals("USD", StringComparison.OrdinalIgnoreCase),
                Salary = Math.Round(Dec(r[4]), 2), ShiftLegacyId = r[5] is null ? null : Int(r[5]),
                JobTitle = r[6] is null ? null : jobs.GetValueOrDefault(Int(r[6])), Department = r[7] is null ? null : departments.GetValueOrDefault(Int(r[7])),
                HireDate = r[8] as DateTime?, LoanBalance = loan, LoanInstallment = loan <= 0 ? 0 : Math.Min(loan, inst > 0 ? Math.Round(inst, 2) : loan),
                IsSalesRep = repIds.Contains(id)
            });
        }
        foreach (var d in plan.Employees.Select(e => e.Department).Where(d => !string.IsNullOrEmpty(d)).Distinct()) plan.Departments.Add(d!);
        var inactiveLoans = loans.Keys.Except(plan.Employees.Select(e => e.LegacyId)).Count();
        if (inactiveLoans > 0) plan.Warnings.Add($"{inactiveLoans} سلفة لموظفين غير فعّالين في النظام القديم — لم تُنقل؛ راجعها يدويًا");

        // ---------------- الصناديق ----------------
        foreach (var r in await Rows("SELECT id, name, amount FROM kasat ORDER BY id", "kasat"))
        {
            var bal = Math.Round(Dec(r[2]), 2);
            plan.CashBoxes.Add(new RahmaCashBoxPlan { LegacyId = Int(r[0]), Name = Clean(Str(r[1])), LegacyBalance = bal, CountedAmount = Math.Max(0, bal) });
        }
        if (plan.CashBoxes.Count > 0) plan.CashBoxes[0].MergeIntoDefault = true;
        plan.Warnings.Add("أرصدة الصناديق المقترحة من النظام القديم — أدخل الجرد الفعلي للنقد يوم الانتقال قبل التنفيذ");
        return plan;
    }

    // ============================ أدوات النص ============================

    private static string Brand(string? company) =>
        (company ?? "").Replace("معمل مياه", "").Replace("معمل", "").Trim() is { Length: > 0 } b ? b : "الرحمة";

    public static RahmaRawKind KindOf(string name)
    {
        var n = Normalize(name);
        if (n.Contains("امبول") || n.Contains("بريفورم") || n.Contains("قالب")) return RahmaRawKind.Preform;
        if (n.Contains("سداد") || n.Contains("غطا")) return RahmaRawKind.Cap;
        if (n.Contains("ليبل") || n.Contains("لاصق") || n.Contains("ستيكر")) return RahmaRawKind.Label;
        if (n.Contains("شرنك") || n.Contains("نايلون")) return RahmaRawKind.Shrink;
        if (n.Contains("كارتون") || n.Contains("كرتون")) return RahmaRawKind.Carton;
        return RahmaRawKind.Other;
    }

    private static string RoleOf(RahmaRawKind kind, string legacyName) => kind switch
    {
        RahmaRawKind.Preform => "امبولة", RahmaRawKind.Cap => "غطاء", RahmaRawKind.Label => "لاصق",
        RahmaRawKind.Shrink => "شرنك", RahmaRawKind.Carton => "كارتون", _ => legacyName
    };

    /// <summary>الليبل يتميّز بالاسم الخاص؛ السدادة باللون (أو اسم خاص كُتب فيه اللون خطأً)؛ البقية بالاثنين.</summary>
    private static string Descriptor(RahmaRawKind kind, string special, string color) => kind switch
    {
        RahmaRawKind.Label => special,
        RahmaRawKind.Cap => color.Length > 0 ? color : special,
        _ => string.Join(" · ", new[] { special, color }.Where(x => x.Length > 0))
    };

    public static string RawKey(RahmaRawKind kind, string otherName, string descriptor) => $"{kind}|{Normalize(otherName)}|{Normalize(descriptor)}";

    private static bool IsNoLabel(string special) => Normalize(special) is "بدون ليبل" or "بلا ليبل" or "بدون";

    private static (int id, string name)? MatchCustomer(string special, Dictionary<string, (int id, string name, string? phone, string? address, decimal deon)> byNorm)
    {
        var s = Normalize(special);
        if (byNorm.TryGetValue(s, out var exact)) return (exact.id, exact.name);
        // "مطعم الحسون (جديد)" ↔ "مطعم الحسون": أحدهما يحوي الآخر، بشرط تطابق واحد فقط
        var partial = byNorm.Where(kv => kv.Key.Length >= 4 && s.Length >= 4 && (s.Contains(kv.Key) || kv.Key.Contains(s))).Take(2).ToList();
        return partial.Count == 1 ? (partial[0].Value.id, partial[0].Value.name) : null;
    }

    /// <summary>توحيد النص العربي للمقارنة: الهمزات، التاء المربوطة، الألف المقصورة، التطويل، المسافات.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim().ToLowerInvariant())
            sb.Append(ch switch { 'أ' or 'إ' or 'آ' => 'ا', 'ة' => 'ه', 'ى' => 'ي', 'ـ' => '\0', _ => ch });
        return string.Join(' ', sb.ToString().Replace("\0", "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Clean(string? s) => string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string? NullIfEmpty(string? s) => Clean(s) is { Length: > 0 } c && c != "***" ? c : null;
    private static string? Str(object? o) => o?.ToString();
    private static int Int(object? o) => o is null ? 0 : Convert.ToInt32(o);
    private static decimal Dec(object? o) => o is null ? 0 : Convert.ToDecimal(o);
    private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";
}
