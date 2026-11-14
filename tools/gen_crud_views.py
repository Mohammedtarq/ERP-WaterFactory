#!/usr/bin/env python3
"""
مولّد شاشات CRUD بنمط موحّد: جدول + بحث + أزرار صف (ElementName=Root) + نموذج جانبي.
الاستخدام: python3 tools/gen_crud_views.py   (يعيد توليد كل شاشات CRUD المعرّفة أسفل الملف)
الشاشات المتخصصة (الفاتورة، القيود، الحضور، الرواتب...) مكتوبة يدويًا ولا يمسّها هذا المولّد.
"""
import os
from xml.sax.saxutils import escape as esc

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'ERP.Desktop', 'Views')
VMNS = {
    'Warehouse': 'ERP.Presentation.ViewModels.Warehouse',
    'Finance': 'ERP.Presentation.ViewModels.Finance',
    'Suppliers': 'ERP.Presentation.ViewModels.Suppliers',
    'Sales': 'ERP.Presentation.ViewModels.Sales',
    'Settings': 'ERP.Presentation.ViewModels.Settings',
    'HR': 'ERP.Presentation.ViewModels.HR',
    'Reps': 'ERP.Presentation.ViewModels.Reps',
    'Production': 'ERP.Presentation.ViewModels.Production',
}

def a(s): return esc(s, {'"': '&quot;'})

def col(c):
    kind = c[0]
    if kind == 'text':
        _, header, binding, width = c[:4]
        fmt = c[4] if len(c) > 4 else None
        b = binding if not fmt else f'{binding}, StringFormat={fmt}'
        return f'<DataGridTextColumn Header="{a(header)}" Binding="{{Binding {b}}}" Width="{width}"/>'
    if kind == 'enum':
        _, header, binding, width = c
        return f'<DataGridTextColumn Header="{a(header)}" Binding="{{Binding {binding}, Converter={{StaticResource EnumArabicConverter}}}}" Width="{width}"/>'
    if kind == 'check':
        _, header, binding, width = c
        return f'<DataGridCheckBoxColumn Header="{a(header)}" Binding="{{Binding {binding}, Mode=OneWay}}" Width="{width}"/>'
    raise ValueError(kind)

def field(f):
    kind, label = f[0], f[1]
    lab = f'<TextBlock Text="{a(label)}" Style="{{StaticResource FieldLabel}}"/>'
    if kind == 'text':
        path = f[2]; ltr = len(f) > 3 and f[3] == 'ltr'
        extra = ' FlowDirection="LeftToRight"' if ltr else ''
        return lab + f'\n<TextBox Text="{{Binding Editor.{path}}}"{extra}/>'
    if kind == 'textvm':  # خاصية على الـ ViewModel نفسه لا على Editor
        return lab + f'\n<TextBox Text="{{Binding {f[2]}, UpdateSourceTrigger=PropertyChanged}}" FlowDirection="LeftToRight"/>'
    if kind == 'num':
        return lab + f'\n<TextBox Text="{{Binding Editor.{f[2]}, TargetNullValue=\'\'}}" FlowDirection="LeftToRight" HorizontalContentAlignment="Right"/>'
    if kind == 'check':
        return f'<CheckBox Content="{a(label)}" IsChecked="{{Binding Editor.{f[2]}}}"/>'
    if kind == 'date':
        return lab + f'\n<DatePicker SelectedDate="{{Binding Editor.{f[2]}}}"/>'
    if kind == 'options':  # قائمة Option<T> (Value/Label)
        _, _, path, source = f
        return lab + (f'\n<ComboBox ItemsSource="{{Binding {source}}}" DisplayMemberPath="Label" SelectedValuePath="Value" '
                      f'SelectedValue="{{Binding Editor.{path}}}"/>')
    if kind == 'lookup':  # قائمة كيانات، القيمة = Id
        _, _, path, source, display = f[:5]
        nullable = len(f) > 5 and f[5] == 'nullable'
        combo = (f'<ComboBox ItemsSource="{{Binding {source}}}" DisplayMemberPath="{display}" SelectedValuePath="Id" '
                 f'SelectedValue="{{Binding Editor.{path}}}"/>')
        if nullable:
            combo = (f'<DockPanel>\n    <Button DockPanel.Dock="Right" Content="&#xE711;" ToolTip="بدون" Style="{{StaticResource RowIconButton}}" Tag="#64748B"\n'
                     f'            Click="ClearCombo" Margin="4,0,0,0"/>\n    {combo}\n</DockPanel>')
        return lab + '\n' + combo
    if kind == 'plain':  # قائمة قيم بسيطة (مثل العملات)
        _, _, path, source = f
        return lab + f'\n<ComboBox ItemsSource="{{Binding {source}}}" SelectedItem="{{Binding Editor.{path}}}"/>'
    if kind == 'raw':
        return f[2]
    raise ValueError(kind)

def indent(text, n):
    pad = ' ' * n
    return '\n'.join(pad + line if line.strip() else line for line in text.split('\n'))

def gen(module, cls, vm, columns, fields, top_extra='', side_extra='', needs_clear=False, selected=None, bottom_extra=''):
    ns = f'ERP.Desktop.Views.{module}'
    cols = '\n'.join(col(c) for c in columns)
    sel = f' SelectedItem="{{Binding {selected}}}"' if selected else ''
    flds = '\n'.join(field(f) for f in fields)
    xaml = f'''<UserControl x:Class="{ns}.{cls}"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:vm="clr-namespace:{VMNS[module]};assembly=ERP.Presentation"
             mc:Ignorable="d" d:DataContext="{{d:DesignInstance vm:{vm}}}"
             x:Name="Root">
    <!-- مُولَّد بنمط CRUD الموحّد: الجدول يمينًا، ونموذج الإضافة/التعديل يسارًا عند الفتح -->
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>

        <Border Style="{{StaticResource Card}}">
            <DockPanel>
                <DockPanel DockPanel.Dock="Top" Margin="0,0,0,10">
                    <StackPanel DockPanel.Dock="Right" Orientation="Horizontal">
                        <Button Style="{{StaticResource PrimaryButton}}" Command="{{Binding NewCommand}}" IsEnabled="{{Binding CanAdd}}">
                            <StackPanel Orientation="Horizontal">
                                <TextBlock Text="&#xE710;" Style="{{StaticResource Icon}}" Margin="0,0,6,0"/>
                                <TextBlock Text="إضافة"/>
                            </StackPanel>
                        </Button>
                        <Button Content="&#xE72C;" ToolTip="تحديث" Style="{{StaticResource RowIconButton}}" Tag="#0F766E" Command="{{Binding RefreshCommand}}"/>
                    </StackPanel>
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Text="&#xE721;" Style="{{StaticResource Icon}}" Foreground="{{StaticResource MutedTextBrush}}" Margin="0,0,8,0"/>
                        <TextBox Text="{{Binding SearchText, UpdateSourceTrigger=PropertyChanged}}" Width="260" ToolTip="بحث"/>
                    </StackPanel>
                </DockPanel>
{indent(top_extra, 16)}
                <TextBlock DockPanel.Dock="Bottom" Text="{{Binding StatusMessage}}" Style="{{StaticResource StatusText}}"/>
{indent(bottom_extra, 16)}
                <DataGrid ItemsSource="{{Binding Items}}"{sel}>
                    <DataGrid.Columns>
{indent(cols, 24)}
                        <DataGridTemplateColumn Header="" Width="84">
                            <DataGridTemplateColumn.CellTemplate>
                                <DataTemplate>
                                    <StackPanel Orientation="Horizontal" HorizontalAlignment="Center">
                                        <Button Content="&#xE70F;" ToolTip="تعديل" Style="{{StaticResource RowIconButton}}" Tag="#0EA5E9"
                                                Command="{{Binding DataContext.EditCommand, ElementName=Root}}" CommandParameter="{{Binding}}"/>
                                        <Button Content="&#xE74D;" ToolTip="حذف" Style="{{StaticResource RowIconButton}}" Tag="#DC2626"
                                                Command="{{Binding DataContext.DeleteCommand, ElementName=Root}}" CommandParameter="{{Binding}}"/>
                                    </StackPanel>
                                </DataTemplate>
                            </DataGridTemplateColumn.CellTemplate>
                        </DataGridTemplateColumn>
                    </DataGrid.Columns>
                </DataGrid>
            </DockPanel>
        </Border>

        <!-- نموذج الإضافة/التعديل -->
        <Border Grid.Column="1" Style="{{StaticResource Card}}" Width="340" Margin="12,0,0,0"
                Visibility="{{Binding IsEditing, Converter={{StaticResource BoolToVisibility}}}}">
            <DockPanel>
                <TextBlock DockPanel.Dock="Top" Text="{{Binding EditorTitle}}" Style="{{StaticResource SectionTitle}}"/>
                <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,14,0,0">
                    <Button Style="{{StaticResource SuccessButton}}" Command="{{Binding SaveCommand}}">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock Text="&#xE74E;" Style="{{StaticResource Icon}}" Margin="0,0,6,0"/>
                            <TextBlock Text="حفظ"/>
                        </StackPanel>
                    </Button>
                    <Button Content="إلغاء" Style="{{StaticResource SecondaryButton}}" Command="{{Binding CancelCommand}}"/>
                </StackPanel>
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel>
{indent(flds, 24)}
{indent(side_extra, 24)}
                    </StackPanel>
                </ScrollViewer>
            </DockPanel>
        </Border>
    </Grid>
</UserControl>
'''
    os.makedirs(f'{ROOT}/{module}', exist_ok=True)
    with open(f'{ROOT}/{module}/{cls}.xaml', 'w', encoding='utf-8') as fh:
        fh.write(xaml)
    clear = '''

    // زر "بدون" بجانب القوائم الاختيارية: يفرّغ القيمة (null) في الحقل المرتبط
    private void ClearCombo(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Parent: DockPanel panel })
            foreach (var combo in panel.Children.OfType<ComboBox>()) combo.SelectedValue = null;
    }''' if needs_clear or "'nullable'" in repr(fields) else ''
    with open(f'{ROOT}/{module}/{cls}.xaml.cs', 'w', encoding='utf-8') as fh:
        fh.write(f'''using System.Windows.Controls;

namespace {ns};

public partial class {cls} : UserControl
{{
    public {cls}()
    {{
        InitializeComponent();
    }}{clear}
}}
''')

# ============================ المخازن ============================
gen('Warehouse', 'ItemsSectionView', 'ItemsSectionViewModel',
    [('text', 'الرمز', 'ItemCode', 90), ('text', 'اسم الصنف', 'ItemName', '*'), ('text', 'الباركود', 'BarCode', 130),
     ('text', 'الوحدة', 'BaseUnitName', 70), ('enum', 'المصدر', 'SourcingMethod', 100),
     ('text', 'سعر البيع', 'SalePrice', 90, 'N0'), ('text', 'حد التنبيه', 'MinStockAlertLevel', 80, 'N0'), ('check', 'فعّال', 'IsActive', 50)],
    [('text', 'رمز الصنف', 'ItemCode', 'ltr'), ('text', 'اسم الصنف', 'ItemName'), ('text', 'الباركود', 'BarCode', 'ltr'),
     ('text', 'الوحدة الأساسية', 'BaseUnitName'), ('options', 'طريقة التوفير', 'SourcingMethod', 'SourcingOptions'),
     ('num', 'سعر البيع للقطعة (د.ع)', 'SalePrice'), ('num', 'حد التنبيه الأدنى (قطعة)', 'MinStockAlertLevel'),
     ('check', 'فعّال', 'IsActive')],
    side_extra='<TextBlock Text="الصنف الجديد يحصل تلقائيًا على وحدة بيع بالقطعة؛ أضف الشرنك والكارتون من هيكلية التعبئة." Style="{StaticResource Muted}" FontSize="11" Margin="0,10,0,0"/>')

gen('Warehouse', 'WarehousesSectionView', 'WarehousesSectionViewModel',
    [('text', 'المخزن', 'Name', '*'), ('text', 'الفرع', 'Branch.Name', 150), ('enum', 'النوع', 'WarehouseType', 120),
     ('check', 'قابل للبيع', 'IsSellableStock', 80), ('text', 'المندوب', 'OwnerEmployee.FullName', 140), ('check', 'فعّال', 'IsActive', 50)],
    [('text', 'اسم المخزن', 'Name'), ('lookup', 'الفرع', 'BranchId', 'Branches', 'Name'),
     ('options', 'نوع المخزن', 'WarehouseType', 'TypeOptions'), ('check', 'مخزون قابل للبيع', 'IsSellableStock'),
     ('lookup', 'المندوب صاحب الكاش فان', 'OwnerEmployeeId', 'Employees', 'FullName', 'nullable'), ('check', 'فعّال', 'IsActive')],
    side_extra='<TextBlock Text="مخازن التالف والفحص والمرتجعات والمواد الأولية والطريق تُجعل غير قابلة للبيع تلقائيًا." Style="{StaticResource Muted}" FontSize="11" Margin="0,10,0,0"/>')

gen('Warehouse', 'PackagingSectionView', 'PackagingSectionViewModel',
    [('text', 'الصنف', 'Item.ItemName', '*'), ('text', 'المستوى', 'LevelName', 110), ('text', 'يحتوي', 'ContainsQuantity', 70, 'N0'),
     ('text', 'من المستوى', 'ParentLevel.LevelName', 110), ('text', 'عدد القطع', 'EquivalentBaseUnits', 80, 'N0'), ('check', 'وحدة بيع', 'IsSellableUnit', 70)],
    [('lookup', 'الصنف', 'ItemId', 'ItemsLookup', 'ItemName'), ('text', 'اسم المستوى (قطعة/شرنك/كارتون)', 'LevelName'),
     ('lookup', 'يتكوّن من المستوى', 'ParentLevelId', 'ParentOptions', 'LevelName', 'nullable'),
     ('num', 'عدد الوحدات الأصغر داخله', 'ContainsQuantity'), ('check', 'يُباع بهذه الوحدة', 'IsSellableUnit')],
    top_extra='''<StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,10">
    <TextBlock Text="عرض صنف:" VerticalAlignment="Center" Margin="0,0,8,0"/>
    <ComboBox ItemsSource="{Binding ItemsLookup}" DisplayMemberPath="ItemName" SelectedItem="{Binding SelectedItem}" Width="260"/>
</StackPanel>''',
    side_extra='<TextBlock Text="عدد القطع يُحسب تلقائيًا = قطع المستوى الأصغر × العدد. مثال: شرنك 6 قطع، كارتون = 4 شرنك = 24 قطعة." Style="{StaticResource Muted}" FontSize="11" Margin="0,10,0,0"/>')

gen('Warehouse', 'LocationsSectionView', 'LocationsSectionViewModel',
    [('text', 'المخزن', 'Warehouse.Name', 160), ('text', 'الموقع', 'LocationName', '*'), ('enum', 'المستوى', 'LevelType', 100),
     ('text', 'يتبع', 'ParentLocation.LocationName', 150)],
    [('lookup', 'المخزن', 'WarehouseId', 'Warehouses', 'Name'), ('text', 'اسم الموقع', 'LocationName'),
     ('options', 'المستوى', 'LevelType', 'LevelOptions'),
     ('lookup', 'يتبع الموقع', 'ParentLocationId', 'Items', 'LocationName', 'nullable')])

# ============================ المالية ============================
gen('Finance', 'ChartOfAccountsSectionView', 'ChartOfAccountsSectionViewModel',
    [('text', 'الرمز', 'AccountCode', 90), ('text', 'اسم الحساب', 'AccountName', '*'), ('enum', 'النوع', 'AccountType', 110),
     ('text', 'الحساب الأب', 'ParentAccount.AccountName', 180), ('check', 'فعّال', 'IsActive', 50)],
    [('text', 'رمز الحساب', 'AccountCode', 'ltr'), ('text', 'اسم الحساب', 'AccountName'),
     ('options', 'نوع الحساب', 'AccountType', 'TypeOptions'),
     ('lookup', 'الحساب الأب', 'ParentAccountId', 'ParentOptions', 'AccountName', 'nullable'), ('check', 'فعّال', 'IsActive')])

gen('Finance', 'MappingRulesSectionView', 'MappingRulesSectionViewModel',
    [('text', 'نوع العملية', 'TransactionType', 200), ('text', 'الحساب المدين', 'DebitAccount.AccountName', '*'),
     ('text', 'الحساب الدائن', 'CreditAccount.AccountName', '*')],
    [('raw', '', '''<TextBlock Text="نوع العملية" Style="{StaticResource FieldLabel}"/>
<ComboBox ItemsSource="{Binding KnownTypes}" DisplayMemberPath="Label" SelectedValuePath="Value"
          IsEditable="True" Text="{Binding Editor.TransactionType}" FlowDirection="LeftToRight"/>'''),
     ('lookup', 'الحساب المدين', 'DebitAccountId', 'Accounts', 'AccountName'),
     ('lookup', 'الحساب الدائن', 'CreditAccountId', 'Accounts', 'AccountName')],
    top_extra='''<Border DockPanel.Dock="Top" Background="#FFFBEB" CornerRadius="6" Padding="10,8" Margin="0,0,0,10">
    <TextBlock Text="{Binding MissingText}" Foreground="{StaticResource WarningBrush}" TextWrapping="Wrap"/>
</Border>''',
    side_extra='<TextBlock Text="العقل المالي: كل سند أو فاتورة من هذا النوع تُنشئ قيدها تلقائيًا بهذين الحسابين، دون حاجة لخبرة محاسبية من الموظف." Style="{StaticResource Muted}" FontSize="11" Margin="0,10,0,0"/>')

# ============================ الموردون ============================
gen('Suppliers', 'SuppliersSectionView', 'SuppliersSectionViewModel',
    [('text', 'المورد', 'Name', '*'), ('text', 'الهاتف', 'Phone', 130), ('text', 'العنوان', 'Address', 200),
     ('enum', 'شروط الدفع', 'DefaultPaymentTerms', 140), ('check', 'فعّال', 'IsActive', 50)],
    [('text', 'اسم المورد', 'Name'), ('text', 'الهاتف', 'Phone', 'ltr'), ('text', 'العنوان', 'Address'),
     ('options', 'شروط الدفع الافتراضية', 'DefaultPaymentTerms', 'TermsOptions'), ('check', 'فعّال', 'IsActive')])

# ============================ المبيعات ============================
gen('Sales', 'CustomersSectionView', 'CustomersSectionViewModel',
    [('text', 'العميل', 'Name', '*'), ('enum', 'النوع', 'CustomerType', 100), ('text', 'الوكيل', 'ParentAgent.Name', 150),
     ('text', 'المحافظة', 'Province', 100), ('text', 'الهاتف', 'Phone', 120), ('check', 'فعّال', 'IsActive', 50)],
    [('text', 'اسم العميل', 'Name'), ('options', 'نوع العميل', 'CustomerType', 'TypeOptions'),
     ('lookup', 'الوكيل (للعميل الفرعي فقط)', 'ParentAgentId', 'Agents', 'Name', 'nullable'),
     ('text', 'المحافظة', 'Province'), ('text', 'الهاتف', 'Phone', 'ltr'), ('text', 'العنوان', 'Address'), ('check', 'فعّال', 'IsActive')],
    side_extra='<TextBlock Text="الوكيل: سعر خاص لكل صنف. العميل الفرعي: يرث سعر وكيله، ومديونيته مستقلة عنه. المباشر: السعر العادي." Style="{StaticResource Muted}" FontSize="11" Margin="0,10,0,0"/>')

gen('Sales', 'AgentPricesSectionView', 'AgentPricesSectionViewModel',
    [('text', 'الوكيل', 'Customer.Name', '*'), ('text', 'الصنف', 'Item.ItemName', '*'),
     ('text', 'سعر الوكيل للقطعة', 'AgentPrice', 130, 'N0'), ('text', 'السعر العادي', 'Item.SalePrice', 110, 'N0')],
    [('lookup', 'الوكيل', 'CustomerId', 'Agents', 'Name'), ('lookup', 'الصنف', 'ItemId', 'ItemsLookup', 'ItemName'),
     ('num', 'سعر الوكيل للقطعة (د.ع)', 'AgentPrice')],
    side_extra='<TextBlock Text="سعر الكارتون/الشرنك يُحسب تلقائيًا في الفاتورة = سعر القطعة × عدد القطع." Style="{StaticResource Muted}" FontSize="11" Margin="0,10,0,0"/>')

gen('Sales', 'LoadingSettingsSectionView', 'LoadingSettingsSectionViewModel',
    [('text', 'تاريخ السريان', 'EffectiveDate', 140, 'yyyy/MM/dd'), ('text', 'السعر للقطعة (د.ع)', 'RatePerPiece', '*', 'N2')],
    [('date', 'تاريخ السريان', 'EffectiveDate'), ('num', 'سعر مستلزمات التحميل للقطعة (د.ع)', 'RatePerPiece')],
    top_extra='''<Border DockPanel.Dock="Top" Background="#ECFDF5" CornerRadius="6" Padding="10,8" Margin="0,0,0,10">
    <TextBlock Text="{Binding CurrentRateText}" Foreground="{StaticResource SuccessBrush}" FontWeight="SemiBold"/>
</Border>''')

# ============================ الإعدادات ============================
gen('Settings', 'UsersSectionView', 'UsersSectionViewModel',
    [('text', 'اسم المستخدم', 'Username', '*'), ('text', 'الدور', 'Role.Name', 150), ('text', 'الموظف', 'Employee.FullName', 170),
     ('check', 'فعّال', 'IsActive', 50), ('text', 'تاريخ الإنشاء', 'CreatedAt', 120, 'yyyy/MM/dd')],
    [('text', 'اسم المستخدم', 'Username', 'ltr'), ('textvm', 'كلمة مرور جديدة (اتركها فارغة للإبقاء على الحالية)', 'NewPassword'),
     ('lookup', 'الدور', 'RoleId', 'Roles', 'Name'), ('lookup', 'الموظف المرتبط', 'EmployeeId', 'Employees', 'FullName', 'nullable'),
     ('check', 'فعّال', 'IsActive')],
    side_extra='<TextBlock Text="هذا المستخدم محلي داخل المشروع (صلاحياته). حساب الدخول الموحّد يُربط به من قاعدة التحكم." Style="{StaticResource Muted}" FontSize="11" Margin="0,10,0,0"/>')

gen('Settings', 'BranchesSectionView', 'BranchesSectionViewModel',
    [('text', 'الفرع', 'Name', '*'), ('text', 'العنوان', 'Address', 260), ('check', 'فعّال', 'IsActive', 50)],
    [('text', 'اسم الفرع', 'Name'), ('text', 'العنوان', 'Address'), ('check', 'فعّال', 'IsActive')])

# ============================ الموارد البشرية ============================
gen('HR', 'EmployeesSectionView', 'EmployeesSectionViewModel',
    [('text', 'الموظف', 'FullName', '*'), ('text', 'الوظيفة', 'JobTitle', 130), ('text', 'القسم', 'Department.Name', 110),
     ('text', 'الشفت', 'Shift.Name', 100), ('text', 'الراتب', 'BaseSalary', 100, 'N0'), ('enum', 'العملة', 'SalaryCurrency', 60),
     ('check', 'مندوب', 'IsSalesRep', 55), ('check', 'مدير مبيعات', 'IsSalesManager', 75), ('check', 'فعّال', 'IsActive', 50)],
    [('text', 'الاسم الكامل', 'FullName'), ('text', 'المسمى الوظيفي', 'JobTitle'), ('text', 'الهاتف', 'Phone', 'ltr'),
     ('lookup', 'القسم', 'DepartmentId', 'Departments', 'Name', 'nullable'),
     ('lookup', 'الشفت', 'ShiftId', 'Shifts', 'Name', 'nullable'),
     ('lookup', 'الفرع', 'BranchId', 'Branches', 'Name', 'nullable'), ('plain', 'عملة الراتب', 'SalaryCurrency', 'Currencies'),
     ('num', 'الراتب الأساسي', 'BaseSalary'), ('date', 'تاريخ التعيين', 'HireDate'),
     ('check', 'مندوب مبيعات (كاش فان)', 'IsSalesRep'), ('check', 'مدير مبيعات', 'IsSalesManager'), ('check', 'فعّال', 'IsActive')],
    side_extra='<TextBlock Text="الراتب الأساسي هنا هو راتب التعيين؛ الزيادات تُسجَّل من الترقيات والعلاوات وتدخل الرواتب تلقائيًا." Style="{StaticResource Muted}" FontSize="11" Margin="0,10,0,0"/>')

gen('HR', 'PromotionsSectionView', 'PromotionsSectionViewModel',
    [('text', 'الموظف', 'Employee.FullName', '*'), ('enum', 'الحركة', 'MovementType', 110), ('text', 'المسمى الجديد', 'NewJobTitle', 140),
     ('text', 'المبلغ', 'Amount', 100, 'N0'), ('enum', 'التطبيق', 'ApplicationType', 140),
     ('text', 'تاريخ السريان', 'EffectiveDate', 110, 'yyyy/MM/dd'), ('text', 'ملاحظات', 'Notes', 160)],
    [('lookup', 'الموظف', 'EmployeeId', 'Employees', 'FullName'), ('options', 'نوع الحركة', 'MovementType', 'MovementOptions'),
     ('text', 'المسمى الوظيفي الجديد (للترقية)', 'NewJobTitle'), ('num', 'المبلغ (بعملة راتب الموظف)', 'Amount'),
     ('options', 'طريقة التطبيق', 'ApplicationType', 'ApplicationOptions'), ('date', 'تاريخ السريان', 'EffectiveDate'),
     ('text', 'ملاحظات', 'Notes')],
    side_extra='<TextBlock Text="إضافة دائمة: تُضاف للراتب الأساسي من شهر السريان فصاعدًا. لمرة واحدة: تُصرف كبدل في شهر السريان فقط." Style="{StaticResource Muted}" FontSize="11" Margin="0,10,0,0"/>')

gen('HR', 'ShiftsSectionView', 'ShiftsSectionViewModel',
    [('text', 'الشفت', 'Name', '*'), ('text', 'الدخول', 'CheckInTime', 90), ('text', 'سماح الدخول (د)', 'CheckInGraceMinutes', 110),
     ('text', 'الخروج', 'CheckOutTime', 90), ('text', 'سماح الخروج (د)', 'CheckOutGraceMinutes', 110), ('text', 'الإضافي بعد', 'OvertimeStartsAfter', 100)],
    [('text', 'اسم الشفت', 'Name'), ('text', 'وقت الدخول (08:00)', 'CheckInTime', 'ltr'), ('num', 'سماح الدخول بالدقائق', 'CheckInGraceMinutes'),
     ('text', 'وقت الخروج (16:00)', 'CheckOutTime', 'ltr'), ('num', 'سماح الخروج بالدقائق', 'CheckOutGraceMinutes'),
     ('num', 'يبدأ الإضافي بعد (اختياري)', 'OvertimeStartsAfter')],
    side_extra='<TextBlock Text="التأخير يُحسب من بداية الشفت إذا تجاوز الدخول فترة السماح. مثال: شفت 08:00 وسماح 10 د ← الدخول 08:11 = تأخير 11 دقيقة." Style="{StaticResource Muted}" FontSize="11" Margin="0,10,0,0"/>')

gen('HR', 'DepartmentsSectionView', 'DepartmentsSectionViewModel',
    [('text', 'القسم', 'Name', '*')], [('text', 'اسم القسم', 'Name')])

gen('HR', 'IncentiveSettingsSectionView', 'IncentiveSettingsSectionViewModel',
    [('text', 'من (نقاط)', 'MinScore', 110, 'N2'), ('text', 'إلى (نقاط)', 'MaxScore', 110, 'N2'), ('text', 'مبلغ الحافز (د.ع)', 'Amount', '*', 'N0')],
    [('num', 'من (نقاط)', 'MinScore'), ('num', 'إلى (نقاط)', 'MaxScore'), ('num', 'مبلغ الحافز (د.ع)', 'Amount')],
    top_extra='''<Border DockPanel.Dock="Top" Background="#F8FAFC" CornerRadius="8" Padding="12" Margin="0,0,0,10">
    <StackPanel>
        <TextBlock Text="أوزان العوامل (المجموع 100)" FontWeight="SemiBold" Margin="0,0,0,6"/>
        <WrapPanel>
            <TextBlock Text="الانضباط:" VerticalAlignment="Center" Margin="0,0,6,0"/>
            <TextBox Text="{Binding AttendanceWeight, UpdateSourceTrigger=PropertyChanged}" Width="60" Margin="0,0,16,0" FlowDirection="LeftToRight"/>
            <TextBlock Text="الأداء:" VerticalAlignment="Center" Margin="0,0,6,0"/>
            <TextBox Text="{Binding PerformanceWeight, UpdateSourceTrigger=PropertyChanged}" Width="60" Margin="0,0,16,0" FlowDirection="LeftToRight"/>
            <TextBlock Text="المهارات:" VerticalAlignment="Center" Margin="0,0,6,0"/>
            <TextBox Text="{Binding SkillsWeight, UpdateSourceTrigger=PropertyChanged}" Width="60" Margin="0,0,16,0" FlowDirection="LeftToRight"/>
            <Button Content="حفظ الأوزان" Style="{StaticResource SuccessButton}" Command="{Binding SaveWeightsCommand}"/>
            <TextBlock Text="{Binding WeightsTotalText}" VerticalAlignment="Center" Style="{StaticResource Muted}"/>
        </WrapPanel>
        <TextBlock Text="مقياس التحويل: كل شريحة نقاط تقابل مبلغًا ثابتًا (الجدول أدناه)." Style="{StaticResource Muted}" FontSize="11" Margin="0,8,0,0"/>
    </StackPanel>
</Border>''')

gen('HR', 'RepIncentiveRatesSectionView', 'RepIncentiveRatesSectionViewModel',
    [('text', 'الصنف', 'Item.ItemName', '*'), ('text', 'حافز القطعة (د.ع)', 'IncentiveRatePerUnit', 150, 'N2')],
    [('lookup', 'الصنف', 'ItemId', 'ItemsLookup', 'ItemName'), ('num', 'حافز المندوب لكل قطعة مباعة (د.ع)', 'IncentiveRatePerUnit')],
    side_extra='<TextBlock Text="حافز المندوب الشهري = Σ الكمية المباعة بالقطعة من فواتيره المرحّلة (غير المجانية) × حافز الصنف." Style="{StaticResource Muted}" FontSize="11" Margin="0,10,0,0"/>')

gen('HR', 'ManagerTiersSectionView', 'ManagerTiersSectionViewModel',
    [('text', 'مدير المبيعات', 'Employee.FullName', '*'), ('text', 'من (قطعة)', 'FromQuantity', 110, 'N0'),
     ('text', 'إلى (قطعة)', 'ToQuantity', 110, 'N0'), ('text', 'المعدل لكل قطعة', 'RatePerUnit', 130, 'N2')],
    [('lookup', 'مدير المبيعات', 'EmployeeId', 'Managers', 'FullName'), ('num', 'من كمية (قطعة)', 'FromQuantity'),
     ('num', 'إلى كمية (فارغ = بلا حد أعلى)', 'ToQuantity'), ('num', 'المعدل لكل قطعة (د.ع)', 'RatePerUnit')],
    side_extra='<TextBlock Text="الحافز = Σ (الكمية الواقعة في كل شريحة × معدلها) × أيام الدوام الفعلية للمدير في الشهر. الكمية = كل المبيعات المرحّلة غير المجانية للشركة." Style="{StaticResource Muted}" FontSize="11" Margin="0,10,0,0"/>')

# ============================ المالية: أسعار الصرف ============================
gen('Finance', 'ExchangeRatesSectionView', 'ExchangeRatesSectionViewModel',
    [('text', 'تاريخ السريان', 'EffectiveDate', 120, 'yyyy/MM/dd'), ('text', 'العملة', 'CurrencyCode', 80),
     ('text', 'السعر بالدينار', 'RateToIQD', '*', 'N2'), ('text', 'أدخله', 'EnteredByUser.Username', 130)],
    [('date', 'تاريخ السريان', 'EffectiveDate'), ('text', 'رمز العملة', 'CurrencyCode', 'ltr'), ('num', 'السعر: 1 من العملة = ? دينار', 'RateToIQD')],
    side_extra='<TextBlock Text="الرواتب تستخدم آخر سعر ساري حتى نهاية الشهر لتحويل رواتب الدولار في قيد الرواتب، ولتحويل الحوافز (المحسوبة بالدينار) لموظفي الدولار." Style="{StaticResource Muted}" FontSize="11" Margin="0,10,0,0"/>')

# ============================ المندوبون ============================
gen('Reps', 'TerritoriesSectionView', 'TerritoriesSectionViewModel',
    [('text', 'المندوب', 'Employee.FullName', '*'), ('text', 'المنطقة', 'TerritoryName', '*')],
    [('lookup', 'المندوب', 'EmployeeId', 'Reps', 'FullName'), ('text', 'اسم المنطقة', 'TerritoryName')])

gen('Reps', 'CustomerAssignmentsSectionView', 'CustomerAssignmentsSectionViewModel',
    [('text', 'المندوب', 'Employee.FullName', '*'), ('text', 'العميل', 'Customer.Name', '*'), ('text', 'تاريخ التخصيص', 'AssignedAt', 130, 'yyyy/MM/dd')],
    [('lookup', 'المندوب', 'EmployeeId', 'Reps', 'FullName'), ('lookup', 'العميل', 'CustomerId', 'Customers', 'Name')],
    side_extra='<TextBlock Text="العميل يمكن أن يُخصص لأكثر من مندوب. الوكيل بلا مندوب ببساطة لا يُخصص لأحد." Style="{StaticResource Muted}" FontSize="11" Margin="0,10,0,0"/>')

gen('Reps', 'FleetSectionView', 'FleetSectionViewModel',
    [('text', 'السيارة', 'VehicleName', '*'), ('text', 'رقم اللوحة', 'PlateNumber', 110), ('text', 'السائق', 'AssignedEmployee.FullName', 150),
     ('text', 'انتهاء إجازة السوق', 'DrivingLicenseExpiry', 130, 'yyyy/MM/dd'), ('text', 'انتهاء السنوية', 'VehicleRegistrationExpiry', 120, 'yyyy/MM/dd'),
     ('check', 'فعّالة', 'IsActive', 55)],
    [('text', 'اسم/نوع السيارة', 'VehicleName'), ('text', 'رقم اللوحة', 'PlateNumber'),
     ('lookup', 'السائق/المندوب', 'AssignedEmployeeId', 'Employees', 'FullName', 'nullable'),
     ('date', 'تاريخ انتهاء إجازة السوق', 'DrivingLicenseExpiry'), ('date', 'تاريخ انتهاء السنوية', 'VehicleRegistrationExpiry'),
     ('check', 'فعّالة', 'IsActive')],
    top_extra='''<Border DockPanel.Dock="Top" CornerRadius="6" Padding="10,8" Margin="0,0,0,10" Background="#FFFBEB"
        Visibility="{Binding HasAlerts, Converter={StaticResource BoolToVisibility}}">
    <TextBlock Text="{Binding AlertsText}" Foreground="#B45309" TextWrapping="Wrap" FontWeight="SemiBold"/>
</Border>
<TextBlock DockPanel.Dock="Top" Text="{Binding AlertsText}" Foreground="{StaticResource SuccessBrush}" Margin="0,0,0,10"
           Visibility="{Binding HasAlerts, Converter={StaticResource InverseBoolToVisibility}}"/>''')

# ============================ الإنتاج ============================
gen('Production', 'QualityTestsSectionView', 'QualityTestsSectionViewModel',
    [('text', 'الاختبار', 'TestName', '*'), ('text', 'المنتج', 'ApplicableItem.ItemName', 150), ('text', 'الحد الأدنى', 'StandardMin', 100, 'N2'),
     ('text', 'الحد الأعلى', 'StandardMax', 100, 'N2'), ('text', 'النتيجة المقبولة', 'StandardText', 130)],
    [('text', 'اسم الاختبار', 'TestName'), ('lookup', 'خاص بمنتج (فارغ = كل المنتجات)', 'ApplicableItemId', 'ItemsLookup', 'ItemName', 'nullable'),
     ('num', 'الحد الأدنى (للاختبار الرقمي)', 'StandardMin'), ('num', 'الحد الأعلى (للاختبار الرقمي)', 'StandardMax'),
     ('text', 'النتيجة المقبولة (للاختبار الوصفي، مثل: سليم)', 'StandardText')],
    side_extra='<TextBlock Text="الرقمي: ناجح إذا وقعت القيمة بين الحدين. الوصفي: ناجح إذا طابقت النتيجة المقبولة. فشل اختبار واحد يرفض الدفعة كاملة." Style="{StaticResource Muted}" FontSize="11" Margin="0,10,0,0"/>')

gen('Production', 'CustomRecipesSectionView', 'CustomRecipesSectionViewModel',
    [('text', 'الوصفة', 'Name', '*'), ('text', 'المنتج', 'FinishedItem.ItemName', 150), ('text', 'العميل', 'Customer.Name', 150), ('check', 'فعّالة', 'IsActive', 55)],
    [('text', 'اسم الوصفة (مثل: وصفة مطعم الحسون)', 'Name'), ('lookup', 'المنتج', 'FinishedItemId', 'FinishedItems', 'ItemName'),
     ('lookup', 'العميل صاحب الاسم التجاري', 'CustomerId', 'Customers', 'Name'), ('check', 'فعّالة', 'IsActive')],
    selected='SelectedRecipe',
    bottom_extra='''<Border DockPanel.Dock="Bottom" Background="#F8FAFC" CornerRadius="8" Padding="12" Margin="0,10,0,0" MaxHeight="300">
    <DockPanel>
        <TextBlock DockPanel.Dock="Top" Text="{Binding SelectedRecipe.Name, StringFormat='مكوّنات الوصفة المختارة: {0}', TargetNullValue='اختر وصفة من الجدول لعرض مكوّناتها'}" FontWeight="SemiBold" Margin="0,0,0,8"/>
        <WrapPanel DockPanel.Dock="Top" Margin="0,0,0,8">
            <ComboBox ItemsSource="{Binding RawItems}" SelectedItem="{Binding NewComponent}" DisplayMemberPath="ItemName" Width="200" Margin="0,0,8,0" ToolTip="المكوّن الخاص"/>
            <TextBox Text="{Binding NewLabel, UpdateSourceTrigger=PropertyChanged}" Width="130" Margin="0,0,8,0" ToolTip="وصف المكوّن: غطاء / لاصق أمامي / لاصق خلفي"/>
            <TextBox Text="{Binding NewQuantity}" Width="60" Margin="0,0,8,0" ToolTip="الكمية لكل وحدة" FlowDirection="LeftToRight"/>
            <TextBlock Text="يستبدل:" VerticalAlignment="Center" Margin="0,0,6,0"/>
            <ComboBox ItemsSource="{Binding RawItems}" SelectedItem="{Binding NewReplaces}" DisplayMemberPath="ItemName" Width="200" Margin="0,0,8,0" ToolTip="المادة الأساسية التي يحل محلها (فارغ = إضافي)"/>
            <Button Content="إضافة مكوّن" Style="{StaticResource PrimaryButton}" Command="{Binding AddLineCommand}"/>
        </WrapPanel>
        <DataGrid ItemsSource="{Binding Lines}">
            <DataGrid.Columns>
                <DataGridTextColumn Header="المكوّن الخاص" Binding="{Binding ComponentName}" Width="*"/>
                <DataGridTextColumn Header="الوصف" Binding="{Binding ComponentLabel}" Width="120"/>
                <DataGridTextColumn Header="لكل وحدة" Binding="{Binding QuantityPerUnit, StringFormat=N2}" Width="80"/>
                <DataGridTextColumn Header="يستبدل" Binding="{Binding ReplacesName}" Width="*"/>
                <DataGridTemplateColumn Header="" Width="46">
                    <DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <Button Content="&#xE74D;" Style="{StaticResource RowIconButton}" Tag="#DC2626"
                                    Command="{Binding DataContext.DeleteLineCommand, ElementName=Root}" CommandParameter="{Binding}"/>
                        </DataTemplate>
                    </DataGridTemplateColumn.CellTemplate>
                </DataGridTemplateColumn>
            </DataGrid.Columns>
        </DataGrid>
    </DockPanel>
</Border>''')

print('generated')
