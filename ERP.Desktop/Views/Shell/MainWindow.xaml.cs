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

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "إغلاق البرنامج؟", "نظام معمل المياه", MessageBoxButton.YesNo, MessageBoxImage.Question,
                            MessageBoxResult.No, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) == MessageBoxResult.Yes)
            Application.Current.Shutdown();
    }

    private void OnSearchVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { SearchBox.Focus(); Keyboard.Focus(SearchBox); }));
    }

    private void OnSearchBackdrop(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainShellViewModel vm) vm.IsSearchOpen = false;
    }
}
