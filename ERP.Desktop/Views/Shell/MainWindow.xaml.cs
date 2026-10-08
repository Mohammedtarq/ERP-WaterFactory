using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Desktop.Views.Shell;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    public MainWindow()
    {
        InitializeComponent();
        // الخروج التلقائي بعد مدة خمول: أي ضغطة مفتاح أو حركة فأرة تعدّ نشاطًا
        InputManager.Current.PreProcessInput += OnInput;
        var ticks = 0;
        _idleTimer.Tick += (_, _) =>
        {
            if (DataContext is not MainShellViewModel vm) return;
            if (vm.CheckIdle(DateTime.UtcNow)) return;
            if (++ticks % 2 == 0) vm.RefreshIndicators();   // الجرس ومؤشر السحابة كل دقيقة
        };
        _idleTimer.Start();
        Closed += (_, _) =>
        {
            _idleTimer.Stop();
            InputManager.Current.PreProcessInput -= OnInput;
        };
    }

    private void OnInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is KeyboardEventArgs or MouseButtonEventArgs or MouseWheelEventArgs or MouseEventArgs
            && DataContext is MainShellViewModel vm)
            vm.ReportActivity();
    }
}
