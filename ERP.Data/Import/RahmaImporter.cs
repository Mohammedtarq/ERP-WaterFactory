using System.Text;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Import;

/// <summary>
/// ينفّذ خطة النقل من نظام الرحمة في معاملة واحدة على المشروع المفتوح، ثم يطابق ما سُجّل فعلًا مع أرقام النظام القديم.
/// <para>تجربة (commit = false): ينفّذ كل شيء ويطابق ثم يتراجع — لا يُحفظ شيء. يستخدمه المستخدم ليرى تقرير المطابقة قبل الحفظ.</para>
/// <para>تنفيذ (commit = true): نفس الخطوات، ويُحفظ فقط إن تطابقت كل الأرقام. لا يُنفَّذ مرتين على نفس المشروع.</para>
/// </summary>
public class RahmaImporter
{
    public const string SourceSystem = "ALRAHMA";
    public const string CustomerOpeningRule = "CustomerOpeningBalance";          // مدين العملاء / دائن رأس المال
    public const string CustomerCreditOpeningRule = "CustomerOpeningCredit";     // مدين رأس المال / دائن العملاء
    public const string SupplierOpeningRule = "SupplierOpeningBalance";          // مدين رأس المال / دائن الموردين
    public const string SupplierAdvanceOpeningRule = "SupplierOpeningAdvance";   // مدين دفعات مقدمة / دائن رأس المال

    private readonly ProjectDbContext _db;
    public RahmaImporter(ProjectDbContext db) => _db = db;

    public Task<bool> AlreadyImportedAsync() => _db.LegacyImports.AnyAsync(i => i.SourceSystem == SourceSystem);

    public async Task<RahmaImportResult> ExecuteAsync(RahmaImportPlan plan, int userId, bool commit)
    {
        if (!await new CashBoxService(_db).IsAdminAsync(userId))
            return Fail("النقل من النظام السابق للأدمن فقط");
        if (await AlreadyImportedAsync())
        {
            var done = await _db.LegacyImports.AsNoTracking().FirstAsync(i => i.SourceSystem == SourceSystem);
            return Fail($"نُقلت بيانات نظام الرحمة إلى هذا المشروع مسبقًا ({done.ImportedAt.ToLocalTime():yyyy/MM/dd HH:mm} من {done.SourceDatabase}) — لا يُنقل مرتين");
        }
        if (plan.CutoverDate.Date > DateTime.Today) return Fail("تاريخ الانتقال لا يمكن أن يكون في المستقبل");
        var codes = plan.Products.Select(p => p.NewCode).Concat(plan.RawMaterials.Select(m => m.NewCode)).ToList();
        if (codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != codes.Count) return Fail("أكواد مكررة في خطة النقل");
        var taken = await _db.Items.Where(i => codes.Contains(i.ItemCode)).Select(i => i.ItemCode).ToListAsync();
        if (taken.Count > 0) return Fail($"أكواد أصناف موجودة مسبقًا في المشروع: {string.Join("، ", taken.Take(5))}");
        if (plan.CashBoxes.Count(b => b.MergeIntoDefault) > 1) return Fail("اختر صندوقًا قديمًا واحدًا فقط ليصبح الصندوق الرئيسي");
        var bad = plan.Employees.FirstOrDefault(e => e.LoanBalance > 0 && (e.LoanInstallment <= 0 || e.LoanInstallment > e.LoanBalance));
        if (bad is not null) return Fail($"قسط سلفة الموظف \"{bad.Name}\" يجب أن يكون أكبر من صفر ولا يتجاوز رصيدها");
        if (plan.RawMaterials.Any(m => m.Quantity < 0) || plan.FinishedStock.Any(f => f.Packs < 0) || plan.CashBoxes.Any(b => b.CountedAmount < 0))
            return Fail("الكميات والمبالغ المنقولة لا تكون سالبة");

        var rawWh = await _db.Warehouses.FirstOrDefaultAsync(w => w.WarehouseType == WarehouseType.RawMaterial && w.IsActive);
        var fgWh = await _db.Warehouses.FirstOrDefaultAsync(w => w.WarehouseType == WarehouseType.FinishedGoods && w.IsActive);
        if (rawWh is null || fgWh is null) return Fail("يلزم مخزن مواد أولية ومخزن منتج تام فعّالان في المشروع");

        var date = plan.CutoverDate.Date;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                var run = new Run(_db, plan, userId, date, rawWh, fgWh);
                var error = await run.ImportAsync();
                if (error is not null) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return Fail(error); }

                var rows = await run.ReconcileAsync();
                var summary = run.Summary();
                var allMatch = rows.All(r => r.Matches);
                if (commit && allMatch)
                {
                    run.Record.Summary = summary;
                    await _db.SaveChangesAsync();
                    await tx.CommitAsync();
                }
                else await tx.RollbackAsync();
                _db.ChangeTracker.Clear();
                return new RahmaImportResult
                {
                    Success = !commit || allMatch, Committed = commit && allMatch, Reconciliation = rows, Summary = summary,
                    Error = commit && !allMatch ? "لم يُحفظ شيء: أرقام المطابقة لا تتطابق — راجع البنود المعلَّمة" : null
                };
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                _db.ChangeTracker.Clear();
                return Fail("تعذّر النقل ولم يُحفظ شيء: " + (ex.InnerException?.Message ?? ex.Message));
            }
        });
    }

    private static RahmaImportResult Fail(string message) => new() { Success = false, Error = message };

    /// <summary>تنفيذ واحد: يحفظ الخرائط (قديم ← جديد) ليُطابق بعدها ما سُجّل فعلًا.</summary>
    private sealed class Run
    {
        private readonly ProjectDbContext _db;
        private readonly RahmaImportPlan _plan;
        private readonly int _user;
        private readonly DateTime _date;
        private readonly Warehouse _rawWh, _fgWh;
        private readonly Dictionary<string, Item> _raw = new();
        private readonly Dictionary<int, Item> _products = new();
        private readonly Dictionary<int, Customer> _customers = new();
        private readonly Dictionary<int, Employee> _employees = new();
        private readonly List<(RahmaCashBoxPlan plan, int boxId)> _boxes = new();
        private readonly List<int> _supplierIds = new();
        private int _recipes;
        public LegacyImport Record { get; private set; } = null!;

        public Run(ProjectDbContext db, RahmaImportPlan plan, int user, DateTime date, Warehouse rawWh, Warehouse fgWh)
        { _db = db; _plan = plan; _user = user; _date = date; _rawWh = rawWh; _fgWh = fgWh; }

        private void Map(string entity, object legacyKey, int newId) =>
            _db.LegacyImportMap.Add(new LegacyImportMapEntry { ImportId = Record.Id, EntityType = entity, LegacyKey = legacyKey.ToString()!, NewId = newId });

        public async Task<string?> ImportAsync()
        {
            Record = new LegacyImport
            {
                SourceSystem = SourceSystem, SourceServer = _plan.SourceServer, SourceDatabase = _plan.SourceDatabase,
                CutoverDate = _date, ImportedByUserId = _user
            };
            _db.LegacyImports.Add(Record);
            await _db.SaveChangesAsync();

            // ---------- الأصناف: المواد الأولية ثم المنتجات ووصفاتها ----------
            foreach (var m in _plan.RawMaterials)
            {
                var item = await NewItemAsync(m.NewCode, m.NewName, SourcingMethod.Purchased, 0, m.UnitCost, m.AlertLevel, null, 0);
                _raw[m.Key] = item;
                Map("RawMaterial", m.Key, item.Id);
                if (m.Quantity > 0) await OpeningStockAsync(item, _rawWh, $"RH-OPEN", m.Quantity, null);
            }
            foreach (var p in _plan.Products)
            {
                var item = await NewItemAsync(p.NewCode, p.NewName, SourcingMethod.Manufactured, p.UnitPrice, null, null, p.PackLevelName, p.UnitsPerPack);
                _products[p.LegacyId] = item;
                Map("Product", p.LegacyId, item.Id);
                var bom = new BillOfMaterials { FinishedItemId = item.Id, Name = "الوصفة الأساسية (من نظام الرحمة)" };
                foreach (var (key, perUnit, role) in _plan.Boms.GetValueOrDefault(p.LegacyId) ?? new())
                    if (_raw.TryGetValue(key, out var rm) && perUnit > 0)
                        bom.Lines.Add(new BOMLine { RawMaterialItemId = rm.Id, QuantityPerUnit = perUnit, ComponentRole = role });
                _db.BillOfMaterials.Add(bom);
            }
            await _db.SaveChangesAsync();

            // ---------- العملاء: بياناتهم + الدين الافتتاحي + التأمينات ----------
            foreach (var c in _plan.Customers)
            {
                var customer = new Customer { Name = Trim(c.Name, 150)!, Phone = Trim(c.Phone, 30), Address = Trim(c.Address, 300), CustomerType = CustomerType.Direct };
                _db.Customers.Add(customer);
                _customers[c.LegacyId] = customer;
            }
            await _db.SaveChangesAsync();
            foreach (var (legacyId, customer) in _customers) Map("Customer", legacyId, customer.Id);
            await _db.SaveChangesAsync();

            var obNo = 0;
            foreach (var c in _plan.Customers.Where(c => c.Balance != 0 || c.DepositBalance > 0))
            {
                var customer = _customers[c.LegacyId];
                if (c.Balance > 0)
                {
                    var number = $"OB-{_date.Year}-{++obNo:D5}";
                    var text = $"رصيد افتتاحي من نظام الرحمة — {customer.Name}";
                    var (je, err) = await LedgerHelper.PostJournalAsync(_db, CustomerOpeningRule, c.Balance, _date, JournalEntryType.AutoSales, text, _user, "SalesInvoices", null, "OB");
                    if (err is not null) return err;
                    var inv = new SalesInvoice
                    {
                        InvoiceNumber = number, CustomerId = customer.Id, WarehouseId = _fgWh.Id, InvoiceDate = _date, PaymentMethod = InvoicePaymentMethod.Credit,
                        Status = DocumentStatus.Posted, IsOpeningBalance = true, SubTotal = c.Balance, TotalAmount = c.Balance,
                        Notes = "رصيد افتتاحي (نظام الرحمة)", PostedByUserId = _user, PostedAt = DateTime.UtcNow, CreatedByUserId = _user
                    };
                    _db.SalesInvoices.Add(inv);
                    await _db.SaveChangesAsync();
                    inv.JournalEntryId = je!.Id;
                    je.SourceId = inv.Id;
                }
                else if (c.Balance < 0)
                {
                    var amount = -c.Balance;
                    var text = $"رصيد افتتاحي دائن من نظام الرحمة — {customer.Name}";
                    var (je, err) = await LedgerHelper.PostJournalAsync(_db, CustomerCreditOpeningRule, amount, _date, JournalEntryType.AutoVoucher, text, _user, "Vouchers", null, "OB");
                    if (err is not null) return err;
                    var count = await _db.Vouchers.CountAsync();
                    var v = new Voucher
                    {
                        VoucherNumber = $"RV-{count + 1:D5}", VoucherType = VoucherType.Receipt, PartyType = VoucherPartyType.Customer, PartyId = customer.Id,
                        Amount = amount, PaymentMethod = PaymentMethod.Cash, VoucherDate = _date, Notes = "رصيد افتتاحي دائن (نظام الرحمة) — بلا حركة نقد",
                        JournalEntry = je!, CreatedByUserId = _user
                    };
                    _db.Vouchers.Add(v);
                    await _db.SaveChangesAsync();
                    je!.SourceId = v.Id;
                }
                await _db.SaveChangesAsync();

                if (c.DepositBalance > 0)
                {
                    var (r, _) = await new CustomerDepositService(_db).OpeningAsync(customer.Id, c.DepositBalance, _date, _user, "رصيد تأمين منقول من نظام الرحمة");
                    if (!r.Success) return $"تأمين العميل \"{customer.Name}\": {r.ErrorMessage}";
                }
            }

            // ---------- الملصقات الخاصة ← وصفات مخصصة ----------
            var standardLabels = _plan.Boms.ToDictionary(b => b.Key, b => b.Value.Where(l => l.rawKey.StartsWith(nameof(RahmaRawKind.Label) + "|")).Select(l => l.rawKey).FirstOrDefault());
            foreach (var r in _plan.Recipes)
            {
                if (!_products.TryGetValue(r.ProductLegacyId, out var product) || !_raw.TryGetValue(r.LabelKey, out var label)) continue;
                int? customerId = r.CustomerLegacyId is int cid && _customers.TryGetValue(cid, out var cu) ? cu.Id : null;
                int? replaces = standardLabels.GetValueOrDefault(r.ProductLegacyId) is { } std && _raw.TryGetValue(std, out var stdItem) ? stdItem.Id : null;
                var recipe = new CustomRecipe { FinishedItemId = product.Id, CustomerId = customerId, Name = Trim($"{r.SpecialName} — {product.ItemName}", 150)! };
                recipe.Lines.Add(new CustomRecipeLine { ComponentItemId = label.Id, ComponentLabel = "لاصق", QuantityPerUnit = r.LabelsPerUnit, ReplacesRawMaterialItemId = replaces });
                _db.CustomRecipes.Add(recipe);
                _recipes++;
            }
            await _db.SaveChangesAsync();

            // ---------- رصيد المنتج التام: تشغيلة افتتاحية لكل (منتج، ملصق، لون) ----------
            var usedBatches = new HashSet<(int, string)>();
            foreach (var f in _plan.FinishedStock.Where(f => f.Packs > 0))
            {
                if (!_products.TryGetValue(f.ProductLegacyId, out var item)) continue;
                var units = _plan.Products.First(p => p.LegacyId == f.ProductLegacyId).UnitsPerPack;
                var full = $"RH-{f.Label}";
                var batch = full.Length > 44 ? full[..44] : full;
                for (var n = 2; !usedBatches.Add((item.Id, batch)); n++)       // اسمان طويلان يتطابقان بعد القص
                    batch = $"{(full.Length > 40 ? full[..40] : full)}-{n}";
                await OpeningStockAsync(item, _fgWh, batch, f.Packs * units, _date);
            }

            // ---------- الموردون: الذمم الافتتاحية بقيد ----------
            foreach (var s in _plan.Suppliers)
            {
                var supplier = new Supplier { Name = Trim(s.Name, 150)!, Phone = Trim(s.Phone, 30) };
                _db.Suppliers.Add(supplier);
                await _db.SaveChangesAsync();
                _supplierIds.Add(supplier.Id);
                Map("Supplier", s.LegacyId, supplier.Id);
                if (s.Balance == 0) continue;
                var rule = s.Balance > 0 ? SupplierOpeningRule : SupplierAdvanceOpeningRule;
                var text = s.Balance > 0 ? $"ذمة افتتاحية للمورد {supplier.Name} (نظام الرحمة)" : $"دفعة مقدمة افتتاحية للمورد {supplier.Name} (نظام الرحمة)";
                var (_, err) = await LedgerHelper.PostJournalAsync(_db, rule, Math.Abs(s.Balance), _date, JournalEntryType.AutoPurchase, text, _user, "Suppliers", supplier.Id, "OB");
                if (err is not null) return err;
                await _db.SaveChangesAsync();
            }

            // ---------- الموارد البشرية: الأقسام والشفتات والموظفون والسلف ----------
            var depts = new Dictionary<string, int>();
            foreach (var name in _plan.Departments)
            {
                var existing = await _db.Departments.FirstOrDefaultAsync(d => d.Name == name);
                if (existing is null) { _db.Departments.Add(existing = new Department { Name = Trim(name, 100)! }); await _db.SaveChangesAsync(); }
                depts[name] = existing.Id;
            }
            var shifts = new Dictionary<int, int>();
            foreach (var s in _plan.Shifts)
            {
                var name = string.IsNullOrWhiteSpace(s.Name) ? $"شفت {s.LegacyId}" : s.Name;
                var existing = await _db.Shifts.FirstOrDefaultAsync(x => x.Name == name);
                if (existing is null)
                {
                    _db.Shifts.Add(existing = new Shift { Name = Trim(name, 100)!, CheckInTime = s.CheckIn, CheckOutTime = s.CheckOut, CheckInGraceMinutes = s.CheckInGrace, CheckOutGraceMinutes = s.CheckOutGrace });
                    await _db.SaveChangesAsync();
                }
                shifts[s.LegacyId] = existing.Id;
            }
            foreach (var e in _plan.Employees)
            {
                var emp = new Employee
                {
                    FullName = Trim(e.Name, 150)!, Phone = Trim(e.Phone, 30), JobTitle = Trim(e.JobTitle, 100),
                    DepartmentId = e.Department is not null && depts.TryGetValue(e.Department, out var d) ? d : null,
                    ShiftId = e.ShiftLegacyId is int sid && shifts.TryGetValue(sid, out var sh) ? sh : null,
                    SalaryCurrency = e.IsUsd ? SalaryCurrency.USD : SalaryCurrency.IQD, BaseSalary = e.Salary, HireDate = e.HireDate?.Date,
                    IsSalesRep = e.IsSalesRep
                };
                _db.Employees.Add(emp);
                await _db.SaveChangesAsync();
                // المندوب يحتاج سيارته (كاش فان) حتى تُسند إليها الحمولة من مخزن المنتج التام
                if (e.IsSalesRep && !await _db.Warehouses.AnyAsync(w => w.WarehouseType == WarehouseType.RepVan && w.OwnerEmployeeId == emp.Id))
                {
                    var branchId = await _db.Warehouses.Where(w => w.WarehouseType == WarehouseType.FinishedGoods).Select(w => (int?)w.BranchId).FirstOrDefaultAsync()
                                   ?? await _db.Branches.Select(b => b.Id).FirstAsync();
                    _db.Warehouses.Add(new Warehouse { BranchId = branchId, Name = Trim($"سيارة {emp.FullName}", 150)!, WarehouseType = WarehouseType.RepVan,
                                                       OwnerEmployeeId = emp.Id, IsSellableStock = true });
                    await _db.SaveChangesAsync();
                }
                _employees[e.LegacyId] = emp;
                Map("Employee", e.LegacyId, emp.Id);
                if (e.LoanBalance > 0)
                {
                    var (r, _) = await new EmployeeDeductionService(_db).CreateAsync(new EmployeeDeductionService.CreateRequest(
                        EmployeeDeductionKind.Loan, emp.Id, e.LoanBalance, _date, _date.Month, _date.Year, e.LoanInstallment,
                        "رصيد سلفة منقول من نظام الرحمة", null, IsOpening: true), _user);
                    if (!r.Success) return $"سلفة الموظف \"{emp.FullName}\": {r.ErrorMessage}";
                }
            }

            // ---------- الصناديق: الجرد الفعلي كرصيد افتتاحي ----------
            var cash = new CashBoxService(_db);
            foreach (var b in _plan.CashBoxes)
            {
                int boxId;
                if (b.MergeIntoDefault)
                {
                    var def = await _db.CashBoxes.FirstOrDefaultAsync(x => x.IsDefault && x.IsActive)
                              ?? await _db.CashBoxes.FirstOrDefaultAsync(x => x.BoxType == CashBoxType.Main && x.IsActive);
                    if (def is null) return "لا يوجد صندوق رئيسي في المشروع";
                    boxId = def.Id;
                }
                else
                {
                    var name = string.IsNullOrWhiteSpace(b.Name) ? $"صندوق {b.LegacyId}" : b.Name;
                    if (await _db.CashBoxes.AnyAsync(x => x.Name == name)) name += " (الرحمة)";
                    var box = new CashBox { Name = Trim(name, 100)!, BoxType = CashBoxType.Main, Notes = "منقول من نظام الرحمة — اربطه بمستخدمه من شاشة الصناديق" };
                    _db.CashBoxes.Add(box);
                    await _db.SaveChangesAsync();
                    boxId = box.Id;
                }
                Map("CashBox", b.LegacyId, boxId);
                _boxes.Add((b, boxId));
                if (b.CountedAmount > 0)
                {
                    var (r, _) = await cash.OpeningAsync(boxId, b.CountedAmount, _date, $"جرد يوم الانتقال من نظام الرحمة ({b.Name})", _user);
                    if (!r.Success) return $"صندوق \"{b.Name}\": {r.ErrorMessage}";
                }
            }
            await _db.SaveChangesAsync();
            return null;
        }

        private async Task<Item> NewItemAsync(string code, string name, SourcingMethod src, decimal price, decimal? cost, decimal? alert, string? level, int per)
        {
            var item = new Item { ItemCode = code, ItemName = Trim(name, 200)!, SourcingMethod = src, SalePrice = price, CostPrice = cost, MinStockAlertLevel = alert };
            _db.Items.Add(item);
            await _db.SaveChangesAsync();
            var piece = new ItemPackagingLevel { ItemId = item.Id, LevelName = "قطعة", ContainsQuantity = 1, EquivalentBaseUnits = 1, IsSellableUnit = src != SourcingMethod.Purchased };
            _db.ItemPackagingLevels.Add(piece);
            await _db.SaveChangesAsync();
            if (level is not null && per > 1)
                _db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = item.Id, LevelName = level, ParentLevelId = piece.Id, ContainsQuantity = per, EquivalentBaseUnits = per });
            return item;
        }

        private async Task OpeningStockAsync(Item item, Warehouse wh, string batchNumber, decimal qty, DateTime? manufactured)
        {
            var batch = new ItemBatch { ItemId = item.Id, BatchNumber = batchNumber, ManufactureDate = manufactured };
            _db.ItemBatches.Add(batch);
            await _db.SaveChangesAsync();
            _db.StockTransactions.Add(new StockTransaction
            {
                ItemId = item.Id, WarehouseId = wh.Id, BatchId = batch.Id, QuantityBaseUnits = qty, TransactionType = StockTransactionType.Receipt,
                ReferenceTable = "LegacyImports", ReferenceId = Record.Id, TransactionDate = _date, CreatedByUserId = _user
            });
        }

        /// <summary>يقرأ من قاعدة المشروع (لا من الخطة) ما سُجّل فعلًا، ويقارنه بأرقام النظام القديم.</summary>
        public async Task<List<RahmaReconciliationRow>> ReconcileAsync()
        {
            var rows = new List<RahmaReconciliationRow>();
            var customerIds = _customers.Values.Select(c => c.Id).ToList();
            var balances = (await new SalesService(_db).GetCustomerBalancesAsync()).Where(b => customerIds.Contains(b.CustomerId)).ToList();
            rows.Add(new("العملاء", "عدد العملاء", _plan.Customers.Count, customerIds.Count));
            rows.Add(new("العملاء", "ديون العملاء (عليهم)", _plan.CustomersDebt, balances.Where(b => b.Balance > 0).Sum(b => b.Balance)));
            rows.Add(new("العملاء", "أرصدة دائنة (لهم)", _plan.CustomersCredit, balances.Where(b => b.Balance < 0).Sum(b => -b.Balance)));
            var deposits = await _db.CustomerDeposits.Where(d => customerIds.Contains(d.CustomerId) && !d.IsVoided).SumAsync(d => (decimal?)d.Amount) ?? 0;
            rows.Add(new("العملاء", "تأمينات الستيكر الخاص", _plan.DepositsTotal, deposits));

            var supplierEntries = await _db.JournalEntryLines.Where(l => l.JournalEntry.SourceTable == "Suppliers" && _supplierIds.Contains(l.JournalEntry.SourceId!.Value))
                .Select(l => new { l.Account.AccountCode, l.Debit, l.Credit }).ToListAsync();
            rows.Add(new("الموردون", "عدد الموردين", _plan.Suppliers.Count, _supplierIds.Count));
            rows.Add(new("الموردون", "ذمم الموردين (لهم)", _plan.SuppliersDebt, supplierEntries.Where(l => l.AccountCode == "2101").Sum(l => l.Credit - l.Debit)));
            rows.Add(new("الموردون", "دفعات مقدمة للموردين", _plan.SuppliersAdvance, supplierEntries.Where(l => l.AccountCode == "1302").Sum(l => l.Debit - l.Credit)));

            var stock = await _db.StockTransactions.Where(t => t.ReferenceTable == "LegacyImports" && t.ReferenceId == Record.Id)
                .GroupBy(t => t.WarehouseId).Select(g => new { g.Key, Qty = g.Sum(t => t.QuantityBaseUnits) }).ToListAsync();
            rows.Add(new("المخزون", "المواد الأولية (بالقطعة)", _plan.RawMaterials.Sum(m => m.Quantity), stock.Where(s => s.Key == _rawWh.Id).Sum(s => s.Qty)));
            var fgPieces = _plan.FinishedStock.Sum(f => f.Packs * _plan.Products.First(p => p.LegacyId == f.ProductLegacyId).UnitsPerPack);
            rows.Add(new("المخزون", "المنتج التام (بالقطعة)", fgPieces, stock.Where(s => s.Key == _fgWh.Id).Sum(s => s.Qty)));
            rows.Add(new("المخزون", "أصناف المواد الأولية", _plan.RawMaterials.Count, _raw.Count));
            rows.Add(new("المخزون", "الوصفات المخصصة (الملصقات الخاصة)", _plan.Recipes.Count, _recipes));

            var employeeIds = _employees.Values.Select(e => e.Id).ToList();
            rows.Add(new("الموظفون", "الموظفون الفعّالون", _plan.Employees.Count, employeeIds.Count));
            var repEmployeeIds = _employees.Values.Where(x => x.IsSalesRep).Select(x => x.Id).ToList();
            rows.Add(new("الموظفون", "المندوبون وسياراتهم", _plan.Employees.Count(x => x.IsSalesRep),
                         await _db.Warehouses.CountAsync(w => w.WarehouseType == WarehouseType.RepVan && w.OwnerEmployeeId != null && repEmployeeIds.Contains(w.OwnerEmployeeId.Value))));
            rows.Add(new("الموظفون", "أرصدة السلف", _plan.LoansTotal,
                await _db.EmployeeDeductions.Where(d => employeeIds.Contains(d.EmployeeId) && d.IsOpening && !d.IsVoided).SumAsync(d => (decimal?)d.Amount) ?? 0));

            var cash = new CashBoxService(_db);
            decimal cashNow = 0;
            foreach (var g in _boxes.GroupBy(b => b.boxId))
                cashNow += await _db.CashBoxTransactions.Where(t => t.CashBoxId == g.Key && t.TxType == CashBoxTxType.Opening && t.JournalEntry!.SourceTable == "CashBoxTransactions"
                                                                    && t.Description!.Contains("نظام الرحمة") && !t.IsVoided).SumAsync(t => (decimal?)t.Amount) ?? 0;
            rows.Add(new("النقد", "أرصدة الصناديق (الجرد الفعلي)", _plan.CashTotal, cashNow));
            return rows;
        }

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"من {_plan.SourceDatabase} على {_plan.SourceServer} — أرصدة افتتاحية بتاريخ {_date:yyyy/MM/dd}");
            sb.AppendLine($"أصناف: {_products.Count} منتج تام، {_raw.Count} مادة أولية، {_recipes} وصفة ملصق خاص");
            sb.AppendLine($"عملاء: {_customers.Count} (ديون {_plan.CustomersDebt:N0} د.ع، تأمينات {_plan.DepositsTotal:N0} د.ع)");
            sb.AppendLine($"موردون: {_supplierIds.Count} (ذمم {_plan.SuppliersDebt:N0} د.ع)");
            sb.AppendLine($"موظفون: {_employees.Count} (سلف {_plan.LoansTotal:N0} د.ع)");
            sb.Append($"صناديق: {_boxes.Count} (نقد {_plan.CashTotal:N0} د.ع)");
            return sb.ToString();
        }

        private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();
    }
}
