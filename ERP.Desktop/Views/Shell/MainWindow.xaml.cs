using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Desktop.Views.Shell;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    public MainWindow()
    {
        InitializeComponent();
        MouseLeftButtonUp += (_, _) => _pressed = false;   // ضغطة بلا سحب لا تترك بداية سحب معلّقة
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
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainShellViewModel old) old.AlertSoundRequested -= AlertChime.Play;
            if (e.NewValue is MainShellViewModel now) now.AlertSoundRequested += AlertChime.Play;
        };
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

    /// <summary>التلاشي السفلي يظهر ما دام في اللوحة المزيد تحت، ويختفي عند الوصول لآخرها أو إن اتسعت للكل.</summary>
    private void OnFlyScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        var more = FlyScroll.ScrollableHeight > 0 && FlyScroll.VerticalOffset < FlyScroll.ScrollableHeight - 1;
        FlyFade.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFlyoutBackdrop(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainShellViewModel vm) vm.IsFlyoutOpen = false;
    }

    // ============================ السحب والإفلات: «مفضلتي» ============================

    private const string DragFormat = "ERP.SectionKey";
    private Point _dragStart;
    private bool _pressed;
    private string? _pressedKey;
    private InsertionAdorner? _insertion;

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null) { if (d is T t) return t; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d); }
        return null;
    }

    private bool PastThreshold(Point now) =>
        Math.Abs(now.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(now.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance;

    // ---- من بطاقات اللوحة المنبثقة ----
    private void OnTileDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _pressedKey = MainShellViewModel.FavoriteKeyOf((sender as FrameworkElement)?.DataContext);
        _pressed = _pressedKey is not null;
    }

    private void OnTileMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed || !PastThreshold(e.GetPosition(this))) return;
        _pressed = false;
        if (_pressedKey is { } key && sender is FrameworkElement tile) StartDrag(tile, key, fromFavorite: false);
    }

    // ---- من بطاقات «مفضلتي» نفسها (إعادة ترتيب أو إزالة) ----
    private void OnFavDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) { _pressed = false; return; }
        _dragStart = e.GetPosition(this);
        _pressedKey = (sender as FrameworkElement)?.Tag as string;
        _pressed = _pressedKey is not null;
    }

    private void OnFavMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed || !PastThreshold(e.GetPosition(this))) return;
        _pressed = false;
        if (_pressedKey is { } key && sender is FrameworkElement card) StartDrag(card, key, fromFavorite: true);
    }

    private void OnFavUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        if (DataContext is MainShellViewModel vm && (sender as FrameworkElement)?.DataContext is QuickEntry entry) vm.OpenEntryCommand.Execute(entry);
    }

    private async void OnFavRemove(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is MainShellViewModel vm && (sender as FrameworkElement)?.DataContext is QuickEntry entry) await vm.UnpinFavoriteAsync(entry.Key);
    }

    private void StartDrag(FrameworkElement source, string key, bool fromFavorite)
    {
        if (DataContext is not MainShellViewModel vm || vm.Entries.FirstOrDefault(x => x.Key == key) is not { } entry) return;
        var color = (Brush)new System.Windows.Media.BrushConverter().ConvertFromString(entry.Color)!;
        var ghost = new DragGhost(this, entry.Title, entry.ModuleTitle, entry.Glyph, color);
        var escaped = false;
        GiveFeedbackEventHandler feedback = (_, args) =>
        {
            ghost.Move();
            args.UseDefaultCursors = false;
            Mouse.SetCursor(args.Effects == DragDropEffects.None ? Cursors.No : Cursors.Hand);
            args.Handled = true;
        };
        QueryContinueDragEventHandler cont = (_, args) => { if (args.EscapePressed) escaped = true; };
        source.GiveFeedback += feedback;
        source.QueryContinueDrag += cont;
        var original = source.Opacity;
        source.Opacity = 0.4;        // الأصل يبقى باهتًا في مكانه
        vm.IsDraggingSection = true;
        ghost.Show();
        DragDropEffects result;
        try { result = DragDrop.DoDragDrop(source, new DataObject(DragFormat, key), fromFavorite ? DragDropEffects.Move : DragDropEffects.Copy); }
        finally
        {
            ghost.Close();
            source.GiveFeedback -= feedback;
            source.QueryContinueDrag -= cont;
            source.Opacity = original;
            vm.IsDraggingSection = false;
            HideInsertion();
        }
        // سُحبت بطاقة من «مفضلتي» وأُفلتت خارجها (وليس بـ Esc): تُزال
        if (fromFavorite && result == DragDropEffects.None && !escaped) _ = vm.UnpinFavoriteAsync(key);
    }

    // ---- الإفلات على «مفضلتي» ----
    private int InsertIndexAt(DragEventArgs e, out double lineY)
    {
        var y = e.GetPosition(FavItems).Y;
        lineY = 0;
        var count = FavItems.Items.Count;
        for (var i = 0; i < count; i++)
        {
            if (FavItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            var top = c.TranslatePoint(new Point(0, 0), FavItems).Y;
            if (y < top + c.ActualHeight / 2) { lineY = top - 3; return i; }
            lineY = top + c.ActualHeight + 1;
        }
        return count;
    }

    private void OnFavDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = (e.AllowedEffects & DragDropEffects.Move) != 0 && FavItems.Items.Cast<QuickEntry>().Any(f => f.Key == (string)e.Data.GetData(DragFormat))
            ? DragDropEffects.Move : DragDropEffects.Copy;
        var index = InsertIndexAt(e, out var lineY);
        if (FavItems.Items.Count > 0)
        {
            _insertion ??= EnsureAdorner();
            _insertion?.Show(Math.Max(2, lineY));
        }
        FavZone.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFD, 0xE6, 0x8A));
        _ = index;
        e.Handled = true;
    }

    private void OnFavDragLeave(object sender, DragEventArgs e)
    {
        HideInsertion();
        FavZone.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
    }

    private async void OnFavDrop(object sender, DragEventArgs e)
    {
        var index = InsertIndexAt(e, out _);
        HideInsertion();
        e.Handled = true;
        if (DataContext is not MainShellViewModel vm || e.Data.GetData(DragFormat) is not string key) return;
        e.Effects = (e.AllowedEffects & DragDropEffects.Move) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
        if (!await vm.PinFavoriteAtAsync(key, index)) return;
        // وميض ذهبي على البطاقة بعد استقرارها
        await Dispatcher.InvokeAsync(() => Pulse(key), DispatcherPriority.ContextIdle);
    }

    private InsertionAdorner? EnsureAdorner()
    {
        if (AdornerLayer.GetAdornerLayer(FavItems) is not { } layer) return null;
        var adorner = new InsertionAdorner(FavItems);
        layer.Add(adorner);
        return adorner;
    }

    private void HideInsertion()
    {
        if (_insertion is null) return;
        _insertion.Hide();
        AdornerLayer.GetAdornerLayer(FavItems)?.Remove(_insertion);
        _insertion = null;
    }

    private void Pulse(string key)
    {
        if (DataContext is not MainShellViewModel vm) return;
        var index = vm.Favorites.ToList().FindIndex(f => f.Key == key);
        if (index < 0 || FavItems.ItemContainerGenerator.ContainerFromIndex(index) is not ContentPresenter cp) return;
        if (cp.ContentTemplate?.FindName("Card", cp) is not Border card) return;
        var brush = new SolidColorBrush(Color.FromArgb(0x99, 0xFB, 0xBF, 0x24));
        card.Background = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), TimeSpan.FromMilliseconds(1600))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }
}
