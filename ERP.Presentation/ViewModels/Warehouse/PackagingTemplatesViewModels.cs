using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Warehouse;

/// <summary>سطر قالب قيد التحرير: الدور، المادة الافتراضية، والنسبة "عدد لكل عدد".</summary>
public class TemplateLineDraft : ObservableObject
{
    private string _role = "";
    private Item? _defaultItem;
    private decimal _componentQuantity = 1;
    private decimal _perUnits = 1;
    public string Role { get => _role; set => SetProperty(ref _role, value); }
    public Item? DefaultItem { get => _defaultItem; set => SetProperty(ref _defaultItem, value); }
    public decimal ComponentQuantity { get => _componentQuantity; set => SetProperty(ref _componentQuantity, value); }
    public decimal PerUnits { get => _perUnits; set => SetProperty(ref _perUnits, value); }
}

// ============================ قوالب التعبئة ============================
public class PackagingTemplatesSectionViewModel : SectionViewModel
{
    private PackagingTemplate? _selected;
    private int? _editingId;
    private string _name = "";
    private string _description = "";
    private bool _isEditing;

    public PackagingTemplatesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "قوالب التعبئة", Icons.Layers, "#0EA5E9",
               "قوالب مثل 330×40 كارتون و330×20 شرنك: أدوار المكونات ونسبها — تملأ قائمة مواد الصنف بنقرة")
    {
        NewCommand = new RelayCommand(() => { if (Require(CanAdd, "إضافة قوالب")) StartEdit(null); });
        EditCommand = new RelayCommand(p => { if (p is PackagingTemplate t && Require(CanEdit, "تعديل القوالب")) StartEdit(t); });
        AddLineCommand = new RelayCommand(() => Lines.Add(new TemplateLineDraft()));
        RemoveLineCommand = new RelayCommand(p => { if (p is TemplateLineDraft l) Lines.Remove(l); });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => IsEditing = false);
    }

    protected override bool ReloadOnActivate => true;
    protected override bool HasPendingInput => IsEditing;

    protected override void ResetInput()
    {
        IsEditing = false;
        Lines.Clear();
        Name = "";
        TemplateDescription = "";
    }

    public ObservableCollection<PackagingTemplate> Templates { get; } = new();
    public ObservableCollection<Item> RawItems { get; } = new();
    public ObservableCollection<TemplateLineDraft> Lines { get; } = new();
    public PackagingTemplate? Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public bool IsEditing { get => _isEditing; private set => SetProperty(ref _isEditing, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string TemplateDescription { get => _description; set => SetProperty(ref _description, value); }
    public RelayCommand NewCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand AddLineCommand { get; }
    public RelayCommand RemoveLineCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        Templates.Clear();
        foreach (var t in await new PackagingTemplateService(db).GetAllAsync()) Templates.Add(t);
        if (RawItems.Count == 0)
            foreach (var i in await db.Items.AsNoTracking().Where(i => i.IsActive && i.SourcingMethod != SourcingMethod.Manufactured).OrderBy(i => i.ItemName).ToListAsync())
                RawItems.Add(i);
    }

    private void StartEdit(PackagingTemplate? t)
    {
        _editingId = t?.Id;
        Name = t?.Name ?? "";
        TemplateDescription = t?.Description ?? "";
        Lines.Clear();
        foreach (var l in t?.Lines.OrderBy(l => l.Id) ?? Enumerable.Empty<PackagingTemplateLine>())
            Lines.Add(new TemplateLineDraft { Role = l.ComponentRole, DefaultItem = RawItems.FirstOrDefault(i => i.Id == l.DefaultItemId),
                                              ComponentQuantity = l.ComponentQuantity, PerUnits = l.PerUnits });
        if (Lines.Count == 0) Lines.Add(new TemplateLineDraft());
        IsEditing = true;
    }

    /// <summary>للاختبارات وشاشة التحرير: تعبئة سطر بالدور والمادة والنسبة.</summary>
    public void SetLine(int index, string role, Item? item, decimal quantity, decimal perUnits)
    {
        while (Lines.Count <= index) Lines.Add(new TemplateLineDraft());
        var l = Lines[index];
        l.Role = role; l.DefaultItem = item; l.ComponentQuantity = quantity; l.PerUnits = perUnits;
    }

    private async Task SaveAsync()
    {
        if (!Require(_editingId is null ? CanAdd : CanEdit, "حفظ القالب")) return;
        await using var db = Session.NewDb();
        var lines = Lines.Where(l => !string.IsNullOrWhiteSpace(l.Role))
                         .Select(l => new TemplateLineInput(l.Role, l.DefaultItem?.Id, l.ComponentQuantity, l.PerUnits)).ToList();
        if (await RunOperationAsync(async () => (await new PackagingTemplateService(db).SaveTemplateAsync(_editingId, Name, TemplateDescription, lines)).result,
                                    $"تم حفظ القالب {Name.Trim()}"))
        {
            IsEditing = false;
            await LoadAsync();
        }
    }
}

/// <summary>اختيار المادة لدور من أدوار القالب عند تطبيقه على صنف.</summary>
public class TemplateRoleChoice : ObservableObject
{
    private Item? _item;
    public string Role { get; init; } = "";
    public string RatioText { get; init; } = "";
    // ليس "Item": WPF يعامل خاصية بهذا الاسم كمفهرس عند كتابة null من القائمة المنسدلة فيرمي NullReferenceException
    public Item? ChosenItem { get => _item; set => SetProperty(ref _item, value); }
}
