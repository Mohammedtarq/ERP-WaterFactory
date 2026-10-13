using System.Globalization;
using ERP.Cloud.Contracts;

namespace ERP.RepApp.Core;

/// <summary>نص المبالغ والعبوات بالعربية.</summary>
public static class Formats
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;
    public static string Money(decimal v) => $"{v.ToString("#,0", En)} د.ع";
    public static string Qty(decimal v) => v.ToString("#,0.##", En);

    /// <summary>القطع بالعبوة الأكبر أولًا: «12 شرنك و5 قطعة».</summary>
    public static string Packs(decimal pieces, IReadOnlyList<SnapshotLevel> levels)
    {
        if (pieces == 0) return "0";
        var parts = new List<string>();
        var left = Math.Abs(pieces);
        foreach (var l in levels.Where(l => l.Units > 0).OrderByDescending(l => l.Units))
        {
            var n = Math.Floor(left / l.Units);
            if (n <= 0) continue;
            parts.Add($"{Qty(n)} {l.Name}");
            left -= n * l.Units;
        }
        if (left > 0) parts.Add($"{Qty(left)} قطعة");
        return (pieces < 0 ? "−" : "") + string.Join(" و", parts);
    }
}

/// <summary>سطر بيع في الهاتف.</summary>
public record SaleLine(int ItemId, int LevelId, decimal Quantity);

/// <summary>سطر محسوب: الاسم والسعر والقطع.</summary>
public record PricedLine(SnapshotProduct Product, SnapshotLevel Level, decimal Quantity, decimal UnitPrice, decimal Total, decimal Pieces)
{
    public string Text => $"{Formats.Qty(Quantity)} {Level.Name} {Product.Name} × {Formats.Money(UnitPrice)} = {Formats.Money(Total)}";
}

/// <summary>خيار في قائمة البيع: صنف محمّل في السيارة بعبوة معيّنة (مثل «ماء 330 مل — كارتون 40»).</summary>
public record LoadedOption(SnapshotProduct Product, SnapshotLevel Level, decimal VanPieces)
{
    public string Text => $"{Product.Name} — {Level.Name}";
    /// <summary>كم عبوة كاملة من هذا النوع في السيارة.</summary>
    public decimal Available => Level.Units > 0 ? Math.Floor(VanPieces / Level.Units) : 0;
    public override string ToString() => $"{Text}  (في السيارة {Formats.Qty(Available)})";
}

/// <summary>مجموع ما خرج من السيارة اليوم لكل صنف وعبوة.</summary>
public record DayItemTotal(string Item, string Pack, decimal Quantity);

/// <summary>فحص الحركة قبل حفظها: خطأ يمنع الحفظ، أو تنبيه يُعرض ويُسمح بالمتابعة.</summary>
public record DraftCheck(string? Error, string? Warning, decimal Total, List<PricedLine> Lines)
{
    public bool Ok => Error is null;
}

/// <summary>
/// زبون كما يراه المندوب الآن (رصيده بعد حركات الهاتف التي لم تدخل نسخة العمل بعد).
/// NewClientId: زبون أضافه المندوب من الهاتف ولم يصل نسخة العمل بعد (رقمه سالب في الهاتف)، ويُباع له فورًا.
/// </summary>
public record LiveCustomer(SnapshotCustomer Customer, decimal Balance, Guid? NewClientId = null)
{
    public int Id => Customer.Id;
    public string Name => Customer.Name;
    public bool IsNew => NewClientId is not null;
    public CustomerRef Ref => NewClientId is Guid g ? new CustomerRef(NewCustomerClientId: g) : new CustomerRef(Id);
    public bool OverLimit => Customer.CreditLimit is decimal cap && Balance > cap;
}

/// <summary>
/// عمل المندوب اليومي فوق الحفظ المحلي: الأرصدة الحيّة (السيارة والمحفظة والزبائن)، وفحص البيع والتحصيل وتسعيرهما
/// من نسخة العمل، وحفظ الحركة في صندوق الإرسال. السعر للعرض: المعمل يسعّر بنفسه عند الترحيل (بالقاعدة نفسها).
/// </summary>
public class RepWork
{
    private readonly LocalStore _store;
    public RepWork(LocalStore store) => _store = store;

    public RepSnapshot? Snapshot => _store.Snapshot;

    /// <summary>
    /// حركات الهاتف التي لم تدخل أرصدة نسخة العمل بعد: لم تصل المعمل، أو رُحّلت بعد توليدها.
    /// المتعذّرة والمرفوضة والمعلّقة لا أثر لها (المعمل لم يرحّلها).
    /// </summary>
    private IEnumerable<OutboxItem> Unreflected(RepSnapshot snap)
    {
        var posted = (snap.PostedClientIds ?? new()).ToHashSet();
        var horizon = snap.GeneratedAtUtc.AddDays(-6);
        return _store.Outbox(days: 14).Where(i =>
            i.Status is not ("Failed" or "Rejected" or "Pending")
            && !posted.Contains(i.ClientId)
            && (i.State != OutboxState.Done || i.CreatedAtUtc >= horizon));
    }

    /// <summary>رصيد السيارة بالقطعة لكل صنف.</summary>
    public Dictionary<int, decimal> VanStock()
    {
        var snap = Snapshot;
        if (snap is null) return new();
        var stock = snap.VanStock.ToDictionary(s => s.ItemId, s => s.Pieces);
        foreach (var e in Unreflected(snap).SelectMany(i => i.Effect.Stock))
            stock[e.ItemId] = stock.GetValueOrDefault(e.ItemId) + e.Pieces;
        return stock;
    }

    /// <summary>النقد مع المندوب.</summary>
    public decimal Wallet()
    {
        var snap = Snapshot;
        return snap is null ? 0 : snap.WalletBalance + Unreflected(snap).Sum(i => i.Effect.Wallet);
    }

    public List<LiveCustomer> Customers()
    {
        var snap = Snapshot;
        if (snap is null) return new();
        var added = _store.Outbox(days: 60).Where(i => i.Kind == "NewCustomer" && i.Status is not ("Failed" or "Rejected")).ToList();
        // حركات زبون جديد رُحّل: تُنسب لرقمه في المعمل
        var posted = added.Where(i => i.ResultId is not null).ToDictionary(i => PendingId(i.ClientId), i => i.ResultId!.Value);
        var deltas = Unreflected(snap).Where(i => i.Effect.CustomerId is not null)
                                       .GroupBy(i => posted.GetValueOrDefault(i.Effect.CustomerId!.Value, i.Effect.CustomerId!.Value))
                                       .ToDictionary(g => g.Key, g => g.Sum(i => i.Effect.Balance));
        var list = snap.Customers.Select(c => new LiveCustomer(c, c.Balance + deltas.GetValueOrDefault(c.Id))).ToList();
        var known = snap.Customers.Select(c => c.Id).ToHashSet();
        foreach (var i in added.Where(i => i.ResultId is not int id || !known.Contains(id)).OrderBy(i => i.CreatedAtUtc))
        {
            var p = System.Text.Json.JsonSerializer.Deserialize<NewCustomerPayload>(i.Payload, JsonWeb)!;
            var pid = PendingId(i.ClientId);
            var c = new SnapshotCustomer(pid, p.Name, p.Phone, p.Address, p.Type ?? "Direct", p.Type == "SubCustomer" ? p.ParentAgentId : null, p.CreditLimit, 0);
            list.Add(new LiveCustomer(c, deltas.GetValueOrDefault(pid) + (i.ResultId is int r ? deltas.GetValueOrDefault(r) : 0), i.ClientId));
        }
        return list;
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonWeb = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>رقم الزبون الجديد في الهاتف قبل أن يصل المعمل: سالب وثابت من رقم الحركة.</summary>
    public static int PendingId(Guid clientId) => -(int)(BitConverter.ToUInt32(clientId.ToByteArray(), 0) & 0x3FFFFFFF) - 1;

    /// <summary>الوكلاء بين زبائن المندوب (لربط عميل فرعي جديد).</summary>
    public List<LiveCustomer> Agents() => Customers().Where(c => c.Customer.Type == "Agent" && !c.IsNew).OrderBy(c => c.Name).ToList();

    public LiveCustomer? Customer(int id) => Customers().FirstOrDefault(c => c.Id == id);

    /// <summary>مبيعات اليوم (غير المجانية) كما سجّلها الهاتف، ما لم تُرفض أو تتعذّر.</summary>
    public decimal TodaySales() => _store.Outbox(days: 2)
        .Where(i => i.Kind is "CashSale" or "CreditSale" && i.OccurredAt.Date == DateTime.Today && i.Status is not ("Failed" or "Rejected"))
        .Sum(i => i.Amount);

    /// <summary>ما يُعرض في قائمة البيع: كل عبوة لكل صنف في السيارة الآن (الأكبر أولًا).</summary>
    public List<LoadedOption> LoadedOptions()
    {
        var snap = Snapshot;
        if (snap is null) return new();
        var van = VanStock();
        return snap.Products
            .Where(p => van.GetValueOrDefault(p.ItemId) > 0)
            .SelectMany(p => p.Levels.OrderByDescending(l => l.Units).Select(l => new LoadedOption(p, l, van[p.ItemId])))
            .Where(o => o.Available > 0)
            .ToList();
    }

    /// <summary>مجموع المواد في بيع اليوم (نقدي وآجل ومجاني) ما لم يُرفض أو يتعذّر.</summary>
    public List<DayItemTotal> TodayItems() => _store.Outbox(days: 2)
        .Where(i => i.Kind is "CashSale" or "CreditSale" or "Free" && i.OccurredAt.Date == DateTime.Today && i.Status is not ("Failed" or "Rejected"))
        .SelectMany(i => i.Lines ?? new())
        .GroupBy(l => (l.Item, l.Pack))
        .Select(g => new DayItemTotal(g.Key.Item, g.Key.Pack, g.Sum(l => l.Quantity)))
        .OrderBy(t => t.Item).ThenBy(t => t.Pack).ToList();

    /// <summary>اسم المعمل في رأس التطبيق والوصل.</summary>
    public string CompanyName => Snapshot?.CompanyName ?? "مندوب المبيعات";

    public int WaitingCount() => _store.Outbox(days: 30).Count(i => i.State != OutboxState.Done);

    /// <summary>سعر القطعة للزبون: سعر وكيله الخاص إن وُجد، وإلا السعر العادي (كما في fn_Sales_BaseUnitPrice).</summary>
    public static decimal PiecePrice(RepSnapshot snap, SnapshotCustomer c, SnapshotProduct p) =>
        c.PriceAgentId is int agent && snap.AgentPrices.FirstOrDefault(a => a.AgentCustomerId == agent && a.ItemId == p.ItemId) is { } special
            ? special.PiecePrice
            : p.PiecePrice;

    // ============================ البيع ============================

    /// <param name="kind">CashSale أو CreditSale أو Free.</param>
    public DraftCheck CheckSale(string kind, int customerId, IReadOnlyList<SaleLine> lines, string? freeReason = null)
    {
        var snap = Snapshot;
        if (snap is null) return Fail("لم تصل نسخة العمل من المعمل بعد — اتصل بالشبكة وزامن");
        if (kind is not ("CashSale" or "CreditSale" or "Free")) return Fail("نوع البيع غير معروف");
        var customer = Customer(customerId);
        if (customer is null) return Fail("اختر الزبون");
        var wanted = lines.Where(l => l.Quantity != 0).ToList();
        if (wanted.Count == 0) return Fail("أضف صنفًا واحدًا على الأقل");
        if (wanted.Any(l => l.Quantity < 0)) return Fail("الكمية يجب أن تكون أكبر من صفر");
        if (kind == "Free" && string.IsNullOrWhiteSpace(freeReason)) return Fail("المجاني يحتاج سببًا");

        var priced = new List<PricedLine>();
        foreach (var l in wanted)
        {
            var product = snap.Products.FirstOrDefault(p => p.ItemId == l.ItemId);
            var level = product?.Levels.FirstOrDefault(x => x.LevelId == l.LevelId);
            if (product is null || level is null) return Fail("صنف أو عبوة غير موجودة في نسخة العمل — زامن ثم أعد المحاولة");
            var unit = kind == "Free" ? 0 : Math.Round(PiecePrice(snap, customer.Customer, product) * level.Units, 2, MidpointRounding.AwayFromZero);
            priced.Add(new PricedLine(product, level, l.Quantity, unit, Math.Round(l.Quantity * unit, 2, MidpointRounding.AwayFromZero), l.Quantity * level.Units));
        }

        // لا يُباع أكثر مما في السيارة (المعمل يرفضه أيضًا)
        var van = VanStock();
        foreach (var g in priced.GroupBy(p => p.Product.ItemId))
        {
            var need = g.Sum(p => p.Pieces);
            var have = van.GetValueOrDefault(g.Key);
            if (need > have)
            {
                var p = g.First().Product;
                return Fail($"رصيد السيارة من {p.Name} لا يكفي: المتوفر {Formats.Packs(have, p.Levels)}، والمطلوب {Formats.Packs(need, p.Levels)}");
            }
        }

        var total = priced.Sum(p => p.Total);
        string? warning = null;
        if (kind == "CreditSale" && customer.Customer.CreditLimit is decimal cap && customer.Balance + total > cap)
        {
            var text = $"الزبون يتجاوز حد دينه ({Formats.Money(cap)}): رصيده {Formats.Money(customer.Balance)} والفاتورة تضيف {Formats.Money(total)}";
            if (!snap.AllowCreditOverLimit) return new($"{text}. اقبض نقدًا أو راجع الإدارة.", null, total, priced);
            warning = text;
        }
        return new(null, warning, total, priced);
    }

    public OutboxItem SaveSale(string kind, int customerId, IReadOnlyList<SaleLine> lines, string? freeReason = null)
    {
        var check = CheckSale(kind, customerId, lines, freeReason);
        if (!check.Ok) throw new InvalidOperationException(check.Error);
        var customer = Customer(customerId)!;
        var payload = new SalePayload(customer.Ref, check.Lines.Select(l => new SaleLinePayload(l.Product.ItemId, l.Level.LevelId, l.Quantity)).ToList(),
                                      kind == "Free" ? freeReason!.Trim() : null);
        var stock = check.Lines.GroupBy(l => l.Product.ItemId).Select(g => new SnapshotStock(g.Key, -g.Sum(l => l.Pieces))).ToList();
        var effect = kind switch
        {
            "CashSale" => new Effect(stock, Wallet: check.Total),
            "CreditSale" => new Effect(stock, CustomerId: customerId, Balance: check.Total),
            _ => new Effect(stock)
        };
        var label = kind switch { "CashSale" => "بيع نقدي", "CreditSale" => "بيع آجل", _ => "مجاني" };
        var summary = $"{label} — {customer.Name}: {string.Join("، ", check.Lines.Select(l => $"{Formats.Qty(l.Quantity)} {l.Level.Name} {l.Product.Name}"))}";
        var lines2 = check.Lines.Select(l => new ReceiptLine(l.Product.Name, l.Level.Name, l.Quantity, l.UnitPrice, l.Total)).ToList();
        return _store.Enqueue(kind, payload, summary, check.Total, effect, lines: lines2, receipt: true);
    }

    // ============================ التحصيل ============================

    public DraftCheck CheckCollection(int customerId, decimal amount)
    {
        if (Snapshot is null) return Fail("لم تصل نسخة العمل من المعمل بعد — اتصل بالشبكة وزامن");
        var customer = Customer(customerId);
        if (customer is null) return Fail("اختر الزبون");
        if (amount <= 0) return Fail("مبلغ التحصيل يجب أن يكون أكبر من صفر");
        var warning = amount > customer.Balance
            ? $"المبلغ أكبر من دين الزبون ({Formats.Money(customer.Balance)}) — الزيادة تبقى رصيدًا له"
            : null;
        return new(null, warning, amount, new());
    }

    public OutboxItem SaveCollection(int customerId, decimal amount)
    {
        var check = CheckCollection(customerId, amount);
        if (!check.Ok) throw new InvalidOperationException(check.Error);
        var customer = Customer(customerId)!;
        return _store.Enqueue("Collection", new CollectionPayload(customer.Ref, amount), $"تحصيل — {customer.Name}", amount,
                              new Effect(new(), Wallet: amount, CustomerId: customerId, Balance: -amount));
    }

    // ============================ وصل البيع ============================

    /// <summary>وصل حركة بيع (null لغير البيع). الرقم: رقم المندوب ثم تسلسل الوصل في الهاتف (مثل 7-15).</summary>
    public Receipt? ReceiptFor(OutboxItem item)
    {
        if (item.ReceiptNo is not int no || item.Kind is not ("CashSale" or "CreditSale" or "Free")) return null;
        var snap = Snapshot;
        var sale = System.Text.Json.JsonSerializer.Deserialize<SalePayload>(item.Payload, JsonWeb);
        var customers = Customers();
        var customer = sale?.Customer switch
        {
            { NewCustomerClientId: Guid g } => customers.FirstOrDefault(c => c.NewClientId == g)?.Name
                                               ?? customers.FirstOrDefault(c => c.Id == _store.Find(g)?.ResultId)?.Name,
            { CustomerId: int id } => customers.FirstOrDefault(c => c.Id == id)?.Name,
            _ => null
        } ?? item.Summary.Split('—').ElementAtOrDefault(1)?.Split(':')[0].Trim() ?? "";
        var kind = item.Kind switch { "CashSale" => "بيع نقدي", "CreditSale" => "بيع آجل", _ => "مجاني" };
        return new Receipt(CompanyName, snap?.CompanyLogo, snap is null ? no.ToString() : $"{snap.RepEmployeeId}-{no}", item.OccurredAt, kind,
                           customer, snap?.RepName ?? "", item.Lines ?? new(), item.Amount);
    }

    // ============================ زبون جديد ============================

    /// <summary>فحص الزبون الجديد بقواعد شاشة «العملاء»: الاسم، والفرعي بوكيل، وحد دين غير سالب؛ وتنبيه إن تكرر الاسم.</summary>
    public DraftCheck CheckNewCustomer(NewCustomerPayload p)
    {
        if (Snapshot is null) return Fail("لم تصل نسخة العمل من المعمل بعد — اتصل بالشبكة وزامن");
        if (string.IsNullOrWhiteSpace(p.Name)) return Fail("اكتب اسم الزبون");
        if (p.Type is not (null or "Direct" or "Agent" or "SubCustomer")) return Fail("نوع الزبون غير معروف");
        if (p.Type == "SubCustomer" && (p.ParentAgentId is not int a || Agents().All(x => x.Id != a))) return Fail("العميل الفرعي يجب ربطه بوكيل");
        if (p.CreditLimit < 0) return Fail("حد الدين لا يكون سالبًا");
        var name = p.Name.Trim();
        var warning = Customers().Any(c => string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) ? "يوجد زبون بالاسم نفسه" : null;
        return new(null, warning, 0, new());
    }

    /// <returns>الزبون كما يظهر في الهاتف فورًا (يُباع له قبل وصوله المعمل).</returns>
    public LiveCustomer SaveNewCustomer(NewCustomerPayload p)
    {
        var check = CheckNewCustomer(p);
        if (!check.Ok) throw new InvalidOperationException(check.Error);
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var payload = p with
        {
            Name = p.Name.Trim(), Phone = Clean(p.Phone), Address = Clean(p.Address), Province = Clean(p.Province),
            Type = p.Type ?? "Direct", ParentAgentId = p.Type == "SubCustomer" ? p.ParentAgentId : null
        };
        var item = _store.Enqueue("NewCustomer", payload, $"زبون جديد — {payload.Name}", 0, Effect.None);
        return Customers().Single(c => c.NewClientId == item.ClientId);
    }

    private static DraftCheck Fail(string message) => new(message, null, 0, new());
}
