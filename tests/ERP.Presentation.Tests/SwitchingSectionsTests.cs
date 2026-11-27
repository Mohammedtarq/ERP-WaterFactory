using System.Collections.Specialized;
using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.Mvvm;
using ERP.Presentation.ViewModels.Reps;
using ERP.Presentation.ViewModels.Shell;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// التنقل بين قسمين بلا تعليقة: القوائم الكبيرة (آلاف العملاء) تُستبدل بإشعار واحد بدل إشعار لكل عنصر،
/// والقسم الذي لم يتغير شيء فيه ولا إدخال معلّق لا يُعاد تحميله عند العودة إليه.
/// </summary>
[Collection("app")]
public class SwitchingSectionsTests
{
    private readonly AppFixture _f;
    public SwitchingSectionsTests(AppFixture f) => _f = f;

    [Fact]
    public void Bulk_replace_raises_a_single_reset_notification_however_many_items()
    {
        var list = new BulkObservableCollection<int>();
        var events = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => events.Add(e.Action);

        list.ReplaceAll(Enumerable.Range(0, 20_000));
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, events);
        Assert.Equal(20_000, list.Count);

        events.Clear();
        list.ReplaceAll(new[] { 7, 8 });
        Assert.Equal(new[] { 7, 8 }, list);
        Assert.Single(events);

        // الامتداد على مجموعة عادية يعطي النتيجة نفسها
        var plain = new System.Collections.ObjectModel.ObservableCollection<int> { 1 };
        plain.ReplaceAll(new[] { 4, 5 });
        Assert.Equal(new[] { 4, 5 }, plain);
    }

    [Fact]
    public async Task Switching_between_two_sections_does_not_reload_an_unchanged_one_but_reloads_after_abandoned_input()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var reps = shell.Open<RepsModuleViewModel>(ModuleCode.Reps);
        var zones = reps.Section<TerritoriesSectionViewModel>();
        var assigned = reps.Section<CustomerAssignmentsSectionViewModel>();
        async Task Go(SectionViewModel s) { reps.SelectedTab = s; await reps.LastActivation; await s.IdleAsync(); }

        await Go(zones);
        await Go(assigned);
        Assert.IsType<BulkObservableCollection<Customer>>(assigned.Customers);

        int changes = 0;
        zones.Items.CollectionChanged += (_, _) => changes++;
        await Go(assigned);
        await Go(zones);                                   // لم يتغير شيء: تظهر فورًا بلا إعادة قراءة
        await Go(assigned);
        await Go(zones);
        Assert.Equal(0, changes);

        // نموذج مفتوح ثم ترك القسم: يعود نظيفًا ومُحمَّلًا من جديد
        await zones.NewCommand.ExecuteAsync();
        Assert.True(zones.IsEditing);
        await Go(assigned);
        await Go(zones);
        Assert.False(zones.IsEditing);
        Assert.True(changes > 0);

        Assert.Empty(dialogs.Errors);
        await shell.IdleAllAsync();
        Assert.Empty(_f.Unhandled);
    }
}
