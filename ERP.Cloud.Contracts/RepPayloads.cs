namespace ERP.Cloud.Contracts;

// ============================ محتوى حركات الهاتف (JSON داخل PhoneRequest.Payload) ============================
// مصدر واحد للهاتف والمعمل: RepAppService يقرأها، وتطبيق المندوب يكتبها.

/// <summary>الزبون: رقمه في النظام، أو رقم طلب «زبون جديد» أُرسل من الهاتف نفسه.</summary>
public record CustomerRef(int? CustomerId = null, Guid? NewCustomerClientId = null);

public record SaleLinePayload(int ItemId, int PackagingLevelId, decimal Quantity, int? CustomRecipeId = null);

/// <summary>بيع نقدي أو آجل أو مجاني (السعر من النظام دائمًا — لا خصم من الهاتف).</summary>
public record SalePayload(CustomerRef Customer, List<SaleLinePayload> Lines, string? FreeReason = null);

public record CollectionPayload(CustomerRef Customer, decimal Amount);

/// <summary>
/// زبون جديد من الهاتف بحقول شاشة «العملاء» نفسها: النوع Direct أو Agent أو SubCustomer (الفرعي يُربط بوكيل)،
/// وحد الدين اختياري. يُسند للمندوب، ويظهر «للاطلاع» في طلبات المندوبين.
/// </summary>
public record NewCustomerPayload(string Name, string? Phone = null, string? Address = null, string? Province = null,
                                 string? Type = null, int? ParentAgentId = null, decimal? CreditLimit = null);

public record ExpensePayload(decimal Amount, string Category, string? Notes = null, int? VehicleId = null);

public record ReturnLinePayload(int ItemId, int PackagingLevelId, decimal Quantity, decimal Damaged = 0);

/// <summary>مرتجع زبون: الافتراضي خصم قيمته من دينه، والرد النقدي باختيار المندوب (Cash = true).</summary>
public record ReturnPayload(CustomerRef Customer, List<ReturnLinePayload> Lines, string Reason, bool Cash = false);

/// <summary>طلب المندوب ربط زبائن من النظام به: لا يتغير شيء حتى توافق الإدارة من «طلبات المندوبين».</summary>
public record CustomerLinkPayload(List<int> CustomerIds);
