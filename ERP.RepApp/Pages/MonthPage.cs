using ERP.Cloud.Contracts;
using ERP.RepApp.Core;

namespace ERP.RepApp.Pages;

/// <summary>
/// «شهري»: حافز الشهر بالمبلغ لكل صنف ولكل أسبوع، وأربع خانات (حضور، غياب، تأخر، إجازة) وتقويم الشهر بألوان الأيام، وسجل التأخر والغياب.
/// تأتي من نسخة العمل (آخر مزامنة)؛ بلا راتب ولا خصم ولا رصيد إجازات.
/// </summary>
public class MonthPage : ContentPage
{
    private static readonly string[] MonthNames = { "", "كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول" };
    private static readonly string[] DayNames = { "س", "ح", "ن", "ث", "ر", "خ", "ج" };   // يبدأ الأسبوع السبت

    public MonthPage()
    {
        var work = AppServices.Instance.Work;
        var month = work.Month();
        var body = new VerticalStackLayout { Spacing = 10 };
        if (month is null)
        {
            body.Children.Add(Ui.Card(Ui.Text(work.Snapshot is null ? "لم تصل نسخة العمل بعد — زامن أولًا" : "ستظهر بيانات الشهر بعد المزامنة القادمة مع المعمل", 16, Ui.Muted)));
        }
        else
        {
            body.Children.Add(IncentiveCard(month));
            body.Children.Add(Stats(work));
            body.Children.Add(Calendar(month));
            var rows = month.Days.Where(d => d.Status is "L" or "A").OrderByDescending(d => d.Date).ToList();
            if (rows.Count > 0) body.Children.Add(Ui.Card(Records(rows)));
            body.Children.Add(Ui.Text($"آخر نسخة من المعمل: {work.Snapshot!.GeneratedAtUtc.ToLocalTime():yyyy/MM/dd HH:mm}", 12, Ui.Muted));
        }
        var title = month is null ? "شهري" : $"شهري — {MonthNames[month.MonthStart.Month]}";
        Ui.SetupMain(this, Ui.Header(title, null, null, () => Navigation.PushAsync(new SettingsPage())), body, "month", work.UnreadCount());
    }

    private static View IncentiveCard(SnapshotMonth m)
    {
        var stack = new VerticalStackLayout { Spacing = 6 };
        stack.Children.Add(Ui.Text("الحافز هذا الشهر", 13, Ui.Muted));
        foreach (var l in m.Incentive)
        {
            var g = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                Children =
                {
                    new VerticalStackLayout { Spacing = 0, Children = { Ui.Text(l.Label, 15, Ui.Ink), Ui.Text($"{Formats.Qty(l.Quantity)} × {Formats.Money(l.Rate)}", 12, Ui.Muted) } },
                    Put(new Label { Text = Formats.Money(l.Amount), FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Ui.Ink, VerticalOptions = LayoutOptions.Center }, 1)
                }
            };
            stack.Children.Add(g);
        }
        if (m.Incentive.Count == 0) stack.Children.Add(Ui.Text("لا حافز مسجل بعد هذا الشهر", 14, Ui.Muted));
        stack.Children.Add(new BoxView { HeightRequest = 1.5, Color = Ui.Line });
        stack.Children.Add(new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            Children = { Ui.Text("المجموع", 14, Ui.Muted), Put(new Label { Text = Formats.Money(m.IncentiveTotal), FontSize = 26, FontAttributes = FontAttributes.Bold, TextColor = Ui.Navy }, 1) }
        });
        // حافز كل أسبوع (يبدأ السبت): أعمدة بحسب أكبر مبلغ
        if (m.Weeks.Count > 0)
        {
            var max = Math.Max(1m, m.Weeks.Max(w => Math.Abs(w.Amount)));
            var bars = new Grid { ColumnSpacing = 8, HeightRequest = 118, Margin = new Thickness(0, 8, 0, 0) };
            for (var i = 0; i < m.Weeks.Count; i++)
            {
                bars.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                var w = m.Weeks[i];
                var h = Math.Max(4, 70 * (double)(Math.Abs(w.Amount) / max));
                var col = new VerticalStackLayout
                {
                    VerticalOptions = LayoutOptions.End, Spacing = 3,
                    Children =
                    {
                        new Label { Text = Formats.Qty(w.Amount), FontSize = 11.5, FontAttributes = FontAttributes.Bold, TextColor = Ui.Ink, HorizontalTextAlignment = TextAlignment.Center },
                        new Border { HeightRequest = h, BackgroundColor = Ui.Brand, StrokeThickness = 0, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 } },
                        new Label { Text = $"أسبوع {i + 1}", FontSize = 11, TextColor = Ui.Muted, HorizontalTextAlignment = TextAlignment.Center }
                    }
                };
                Grid.SetColumn(col, i);
                bars.Children.Add(col);
            }
            stack.Children.Add(Ui.Text("الحافز لكل أسبوع (يبدأ السبت)", 12, Ui.Muted));
            stack.Children.Add(bars);
        }
        return Ui.Card(stack);
    }

    private static View Stats(RepWork work)
    {
        var (present, absent, late, leave, lateMinutes) = work.AttendanceCounts();
        var grid = new Grid { ColumnSpacing = 8 };
        var boxes = new (string Text, int Value, Color Color)[] { ("حضور", present, Ui.Green), ("غياب", absent, Ui.Red), ("تأخر", late, Ui.Amber), ("إجازة", leave, Ui.SyncColor) };
        for (var i = 0; i < boxes.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var (text, value, color) = boxes[i];
            var box = new Border
            {
                BackgroundColor = color, StrokeThickness = 0, Padding = new Thickness(4, 10),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
                Content = new VerticalStackLayout
                {
                    Spacing = 0,
                    Children =
                    {
                        new Label { Text = value.ToString(), FontSize = 24, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.Center },
                        new Label { Text = text + (i == 2 && lateMinutes > 0 ? $" ({lateMinutes}د)" : ""), FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.Center }
                    }
                }
            };
            Grid.SetColumn(box, i);
            grid.Children.Add(box);
        }
        return grid;
    }

    private static View Calendar(SnapshotMonth m)
    {
        var first = m.MonthStart;
        var offset = ((int)first.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7;
        var days = DateTime.DaysInMonth(first.Year, first.Month);
        var grid = new Grid { ColumnSpacing = 5, RowSpacing = 5 };
        for (var c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var c = 0; c < 7; c++)
        {
            var h = new Label { Text = DayNames[c], FontSize = 11, FontAttributes = FontAttributes.Bold, TextColor = Ui.Muted, HorizontalTextAlignment = TextAlignment.Center };
            Grid.SetColumn(h, c);
            grid.Children.Add(h);
        }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var status = m.Days.ToDictionary(d => d.Date.Day, d => d.Status);
        for (var day = 1; day <= days; day++)
        {
            var cell = offset + day - 1;
            var row = cell / 7 + 1;
            while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            status.TryGetValue(day, out var st);
            var (back, fore) = st switch
            {
                "P" => (Ui.Green.WithAlpha(0.2f), Ui.Green), "L" => (Ui.Amber, Colors.White), "A" => (Ui.Red, Colors.White),
                "V" => (Ui.SyncColor, Colors.White), _ => (Ui.Chip, Ui.Muted)
            };
            var box = new Border
            {
                BackgroundColor = back, StrokeThickness = 0, HeightRequest = 38,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 9 },
                Content = new Label { Text = day.ToString(), FontSize = 13, FontAttributes = FontAttributes.Bold, TextColor = fore, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center }
            };
            Grid.SetRow(box, row);
            Grid.SetColumn(box, cell % 7);
            grid.Children.Add(box);
        }
        var legend = new HorizontalStackLayout
        {
            Spacing = 12,
            Children = { Dot(Ui.Green, "حضور"), Dot(Ui.Red, "غياب"), Dot(Ui.Amber, "تأخر"), Dot(Ui.SyncColor, "إجازة") }
        };
        return Ui.Card(new VerticalStackLayout { Spacing = 8, Children = { Ui.Text($"الحضور — {MonthNames[first.Month]}", 13, Ui.Muted), grid, legend } });
    }

    private static View Dot(Color color, string text) => new HorizontalStackLayout
    {
        Spacing = 4,
        Children = { new BoxView { WidthRequest = 10, HeightRequest = 10, Color = color, CornerRadius = 3, VerticalOptions = LayoutOptions.Center }, Ui.Text(text, 11.5, Ui.Muted) }
    };

    private static View Records(List<SnapshotDay> rows)
    {
        var stack = new VerticalStackLayout { Spacing = 8 };
        stack.Children.Add(Ui.Text("سجل التأخر والغياب", 14, Ui.Ink));
        foreach (var d in rows)
        {
            var absent = d.Status == "A";
            var detail = absent ? "غياب" : $"تأخر — وصل {d.CheckIn ?? "؟"}" + (d.LateMinutes > 0 ? $" ({d.LateMinutes} دقيقة)" : "");
            stack.Children.Add(new Grid
            {
                ColumnSpacing = 10,
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                Children =
                {
                    Ui.Pill(absent ? "غياب" : "تأخر", absent ? Ui.Red : Ui.Amber, Colors.White),
                    Put(Ui.Text($"{d.Date:yyyy/MM/dd} — {detail}", 14, Ui.Ink), 1)
                }
            });
        }
        return stack;
    }

    private static View Put(View v, int col) { Grid.SetColumn(v, col); return v; }
}
