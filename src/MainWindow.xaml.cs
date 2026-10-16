using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BalancePet.UsageAnalytics;

public partial class MainWindow : Window
{
    /// <summary>
    /// How many tasks the ledger shows. The list is virtualized but a row that
    /// opens is only as good as the item it lives in, so the ledger is bounded the
    /// way the design's own frame is: the last N tasks, with the true total stated
    /// beside them. The filter still counts every event.
    /// </summary>
    private const int RecentLedgerLimit = 64;

    /// <summary>
    /// The chrome transition: 250 ms on <c>PowerEase(EaseInOut, Power=2)</c>, the
    /// design's hand for every change that is not a layout change. The client
    /// chips, the account rows and the quota card's fold all move on it, so a
    /// click anywhere in the window reads as the same gesture.
    /// </summary>
    private static readonly TimeSpan ChromeTransition = TimeSpan.FromMilliseconds(250);

    private readonly UsageEventStore _store;

    /// <summary>
    /// The plugin's own record of the balances it has seen, which is where a
    /// pay-as-you-go account's last recharge is read from; see
    /// <see cref="BalanceWatchStore"/> for why the host's documents cannot answer it.
    /// </summary>
    private readonly BalanceWatchStore _balances;

    /// <summary>
    /// The plugin's own ceiling on how many archived records the archive keeps. It
    /// lives in this plugin's file rather than in the host's settings; see
    /// <see cref="ArchiveLimitStore"/> for why, and for what the ceiling bounds.
    /// </summary>
    private readonly ArchiveLimitStore _archive;

    /// <summary>
    /// Debounces the file watchers. Updates come from watching the data files and
    /// from the manual refresh button, never from a timer: the host writes a file
    /// for every new record, so polling was pure overhead.
    /// </summary>
    private readonly DispatcherTimer _fileRefreshTimer;
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _balanceWatcher;
    private FileSystemWatcher? _serverUsageWatcher;
    private bool _fileRefreshPending;
    /// <summary>
    /// Set when a watched file changes: a new reading has arrived. This plugin is
    /// file-driven rather than polling, so that event <i>is</i> the arrival, and the
    /// balance and today's cost roll in on it; see <see cref="PlayReadingMotion"/>.
    /// </summary>
    private bool _readingArrived;
    /// <summary>When the readings last rolled in, for the twelve-second debounce.</summary>
    private DateTime _lastReadingMotionUtc = DateTime.MinValue;
    /// <summary>The board driving the reading motion, so a capture can stop it.</summary>
    private MotionBoard? _readingBoard;
    private Button? _activeNav;
    private UsageReport? _lastReport;
    private BalanceUsageSnapshot _lastBalanceUsage = BalanceUsageSnapshot.Read("");
    private ServerUsageSummarySnapshot _lastServerUsage = ServerUsageSummarySnapshot.Read("");
    private IReadOnlyList<UsageEvent> _lastEvents = Array.Empty<UsageEvent>();
    private TrendRange _trendRange = TrendRange.Last7Days;
    private EventRow? _selectedEvent;
    private IReadOnlyList<TrendPoint> _trendPoints = Array.Empty<TrendPoint>();
    private double _trendPlotLeft;
    private double _trendPlotRight;
    private Border? _trendTooltip;
    private Line? _trendHoverLine;
    /// <summary>Selected client, empty for every client.</summary>
    private string _providerFilter = "";
    /// <summary>
    /// Selected account, or one of the two group ids. There is no empty scope any
    /// more: the combined view is gone (see <see cref="UpdateAccountSelector"/>), and
    /// a window whose scope is empty is a window that cannot say what it is showing.
    /// It is empty only between the field's initializer and the first refresh.
    /// </summary>
    private string _accountFilter = "";
    /// <summary>
    /// How many columns the four dashboard readouts are currently laid out in: 4 or
    /// 2. Kept because the reflow moves elements rather than rebuilding them, so it
    /// has to know which arrangement is on screen — see
    /// <see cref="ReflowMetricStrip"/>.
    /// </summary>
    private int _metricColumns = 4;
    /// <summary>
    /// Signatures of what the two selectors currently show, so a periodic
    /// refresh rebuilds them only when their options really changed. Rebuilding
    /// unconditionally would fight the user's selection every 60 seconds.
    /// </summary>
    private string _providerFilterSignature = "";
    private string _accountSelectorSignature = "";
    /// <summary>
    /// Which tasks the ledger is currently showing, as one string: the client filter
    /// and the ids of the events in the list. A refresh that leaves it unchanged
    /// leaves the list alone, because assigning <c>ItemsSource</c> is what costs —
    /// see <see cref="Refresh"/>. Same idea as the two signatures above, and for the
    /// same reason: rebuilding what did not change is the thing that fights the user.
    /// </summary>
    private string _ledgerSignature = "";
    private bool _suppressFilterRefresh;
    /// <summary>
    /// Set while the ceiling control is being written to, so selecting the stored
    /// value does not read back as the user choosing it — the same guard the account
    /// selector uses, for the same reason: a control that writes state must not be
    /// told its own write was a click.
    /// </summary>
    private bool _suppressArchiveLimit;
    private IReadOnlyDictionary<string, AccountDirectory.AccountInfo> _accounts =
        new Dictionary<string, AccountDirectory.AccountInfo>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The record census of the last refresh: one count per group, and the sum they
    /// make. Every surface that states a count — the account column's rows, the scope
    /// badge, the group notice, the census line — reads it from here, so four places
    /// cannot come to count four different logs.
    /// </summary>
    private UsageTally? _tally;

    /// <summary>
    /// The account list's rows, in the order they are stacked, so a refresh writes
    /// into them instead of replacing them. Keyed by account id, empty for the
    /// combined view; the order is the panel's own, because a dictionary has none.
    /// </summary>
    private readonly Dictionary<string, AccountListItem> _accountRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _accountRowOrder = new();

    /// <summary>The row colours, read out of the window's resources once.</summary>
    private AccountListPalette? _accountPalette;

    /// <summary>
    /// The scopes whose quota fold is open, by account id (empty for the combined
    /// view). State rather than a property of the fold, because the fold is rebuilt
    /// on every refresh and what the user opened has to outlive that.
    /// </summary>
    private readonly HashSet<string> _expandedQuotaScopes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The board driving the quota fold, so a capture can hold a frame of it.</summary>
    private MotionBoard? _quotaBoard;
    /// <summary>The fold's control, so a capture can click what the user clicks.</summary>
    private Button? _quotaToggle;
    /// <summary>The fold and its caption, so a capture can say what state it photographed.</summary>
    private Grid? _quotaFold;
    private TextBlock? _quotaCaption;
    /// <summary>
    /// The primary quota bar's own texts, so a capture can compare the reading the card
    /// states with the reading the account row states — off the live elements. Null when
    /// the card is not stating a bar at all.
    /// </summary>
    private QuotaBarTexts? _quotaBar;
    /// <summary>
    /// The last fraction each quota bar was given, by scope and window type, so a
    /// rebuild can tell a reading that moved from one that was merely re-read.
    /// </summary>
    private readonly Dictionary<string, double> _quotaFillApplied = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The two fills the account list draws for itself: the row the window is
    /// scoped to, and the row under the pointer. The sidebar sits a step below the
    /// panels, so its two tones sit below PanelHover/Raised — which is why they are
    /// not the panel tokens. Named here rather than repeated at each of the four
    /// places that paint them.
    /// </summary>
    private static readonly Color SelectedRowFill = Color.FromRgb(0x26, 0x26, 0x26);
    private static readonly Color HoveredRowFill = Color.FromRgb(0x22, 0x22, 0x22);



    /// <summary>
    /// The capture request this process was started with, when it was started to
    /// take a screenshot. Empty in every other run: a capture is a development
    /// entry point, and the window has no other way to be told which frame to
    /// hold. See <see cref="CaptureRequest"/> and REDESIGN-NOTES.md.
    /// </summary>
    private readonly CaptureRequest _capture;
    private bool _captureApplied;
    private LedgerRowView? _openRow;
    /// <summary>The tooltip a capture prepared, so the shot can put its pixels on the
    /// window's. It is never opened and never parented — see
    /// <see cref="ShowCaptureBubble"/> — so no popup window ever appears.</summary>
    private ToolTip? _captureTip;
    /// <summary>Where that bubble's top-left corner goes, in this window's coordinates.</summary>
    private Point _bubbleOrigin;
    /// <summary>The board driving whichever row is opening, so a capture can stop it.</summary>
    private MotionBoard? _expandBoard;

    /// <summary>
    /// Set while a timing probe is running: the stopwatch the segmented table in
    /// REDESIGN-NOTES is read off. A probe is a development run like a capture —
    /// it drives the window's own click handlers rather than setting state — and
    /// it is the only thing that reads these six fields.
    /// </summary>
    private Stopwatch? _probeWatch;
    private double _probeReadMs;
    private double _probeReportMs;
    private double _probePanelsMs;
    private double _probeRefreshMs;
    private double _probeRowsMs;
    /// <summary>Rows rebuilt by <see cref="GenerateRows"/> in the current probe.</summary>
    private int _probeRebuiltRows;
    /// <summary>Sub-requests those rows were handed, read at the same moment.</summary>
    private int _probeRebuiltDetails;
    /// <summary>
    /// Where the probe's own lines are duplicated, when the run was given a file.
    /// This process is a WinExe: it is started from a shell that owns no console of
    /// its own, so a redirected standard output is not something the two shells this
    /// repository is worked from agree on. Writing the table to a file the caller
    /// named is the one route that behaves the same either way.
    /// </summary>
    private StreamWriter? _probeLog;

    /// <summary>One probe line, to the console and to the run's file when it has one.</summary>
    private void ProbeLine(string text)
    {
        Console.WriteLine(text);
        _probeLog?.WriteLine(text);
    }

    /// <summary>
    /// The first grid under <paramref name="root"/> that declares exactly
    /// <paramref name="columns"/> columns. Used by the capture probes to find the
    /// sub-request panel inside an expanded row: the row itself is a Grid of the
    /// ledger's columns and the panel is a Grid of one more, so the count is what tells
    /// them apart — no name, no field, nothing that has to be kept in step by hand.
    /// </summary>
    private static Grid? FindGridWithColumns(DependencyObject root, int columns)
    {
        if (root is Grid grid && grid.ColumnDefinitions.Count == columns) return grid;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindGridWithColumns(VisualTreeHelper.GetChild(root, index), columns) is { } found) return found;
        return null;
    }

    /// <summary>
    /// Opens the run's own log file, when the caller named one with
    /// <c>--probe-file</c>.
    ///
    /// <para>
    /// A capture writes its numbers — the ledger's geometry, the census, the tooltip it
    /// opened — to the same two places a probe does, and for the same reason: this is a
    /// WinExe started from a shell that owns no console of its own, so a redirected
    /// standard output is not something every shell this repository is worked from
    /// agrees on, while a file the caller named is. The acceptance numbers in
    /// REDESIGN-NOTES 第六轮 §5 come out of that file rather than out of a PNG.
    /// </para>
    /// </summary>
    private void OpenCaptureLog()
    {
        if (_probeLog is not null || _capture.ProbeFile is not { } path) return;
        var full = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        _probeLog = new StreamWriter(full, append: false) { AutoFlush = true };
    }

    public MainWindow(string dataDirectory, CaptureRequest? capture = null)
    {
        _capture = capture ?? new CaptureRequest();
        // A capture can name the account to scope the window to, because the
        // balance ruler routes on account type and a run that cannot reach the
        // other branch cannot show it. Ordinary launches never set it.
        _accountFilter = _capture.Account ?? "";
        InitializeComponent();
        // The size before the window is placed, because the placement is computed from
        // it: a capture that moved the window and then resized it would put half of it
        // back on the desk for the mouse to hover.
        if (_capture.Width is { } captureWidth) Width = captureWidth;
        if (_capture.Height is { } captureHeight) Height = captureHeight;
        if (_capture.IsCapture)
        {
            // A capture is put off the virtual screen before it is shown, and it is not
            // activated either. Two reasons, and the second is a rule rather than a
            // preference: the pointer is wherever the developer left it and a row under it
            // draws its hover fill, so a screenshot must not depend on where the desk's
            // mouse happens to be; and a test run must never put a window in front of the
            // person using the machine. The pixels come from the visual tree — PrintWindow
            // declines for a window the compositor holds no surface for — so where the
            // window sits changes nothing except what is hovering it.
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowActivated = false;
            Left = SystemParameters.VirtualScreenLeft - Width - 400;
            Top = SystemParameters.VirtualScreenTop - Height - 400;
        }
        // Every realized list item is handed the row it draws. The row is generated
        // here rather than declared in the item template, because a lane has to be
        // able to measure the panel it is opening to and a template cannot hand that
        // over; see LedgerRowView.MeasureOpenHeight.
        EventsList.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (EventsList.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated) GenerateRows();
        };
        // The head is written by the same code that writes the rows, so the two
        // cannot be given different widths — and both are re-placed when the page is
        // given a size, because neither is re-made by a resize and the head is placed
        // where the rows are. A width that only followed the window when the rows
        // were generated is the report: 「我拉宽界面，内容布局不会跟着动」.
        // A resize needs no rebuilding: the head and the rows are both given shares of
        // whatever width they end up with (see LedgerColumnsFor), so they answer with the
        // same widths without either being remade. Remaking the head here — and the rows
        // somewhere else — is what made the two disagree while the window moved.
        HistoryScrollViewer.SizeChanged += (_, _) => ResizeLedgerRows();
        LedgerScroller.SizeChanged += (_, _) => ResizeLedgerRows();
        // The head is inside the scroller now (see MainWindow.xaml), so there is no offset
        // to mirror: the bar moves the labels and the table in one step. The hook that
        // copied the scroller's offset into a transform went with it — two copies of one
        // number is one copy too many, and the wrong copy was the one on screen.
        BuildLedgerHead();
        _store = new UsageEventStore(dataDirectory);
        _balances = BalanceWatchStore.Read(_store.DirectoryPath);
        _archive = ArchiveLimitStore.Read(_store.DirectoryPath);
        _fileRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _fileRefreshTimer.Tick += (_, _) =>
        {
            _fileRefreshTimer.Stop();
            _fileRefreshPending = false;
            Refresh();
        };
        Loaded += (_, _) =>
        {
            StartWatching();
            DrawTrendChart();
            UpdateActiveNavigation();
            // One request to the host so opening the window shows current
            // balances. This is not a poll: it happens once per open. A capture does
            // not send it: a screenshot run is not a person opening a window, and the
            // request is a message to the running host, so a test run leaves the host
            // alone — the frame is drawn from the files the data directory already has.
            if (!_capture.IsCapture) RequestCoreRefresh();
        };
        // Content is rendered here, so this is the first moment a capture can be
        // taken: everything above has been read, measured and drawn.
        ContentRendered += (_, _) => ApplyCaptureRequest();
        Closed += (_, _) => StopWatching();
        Refresh();
    }

    // ======================================================================
    //  navigation and window chrome
    // ======================================================================

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInsideButton(e.OriginalSource as DependencyObject)) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnDashboardNavClick(object sender, RoutedEventArgs e)
    {
        HideEventDetail(immediate: true);
        
        // 已经在Dashboard，无需切换
        if (ContentScrollViewer.Visibility == Visibility.Visible) return;
        
        // 停止动画
        ContentScrollViewer.BeginAnimation(UIElement.OpacityProperty, null);
        HistoryScrollViewer.BeginAnimation(UIElement.OpacityProperty, null);
        
        var easing = new PowerEase { EasingMode = EasingMode.EaseInOut, Power = 2 };
        var duration = new Duration(TimeSpan.FromMilliseconds(300));
        
        // History淡出
        var fadeOut = new DoubleAnimation(1, 0, duration) { EasingFunction = easing };
        fadeOut.Completed += (_, _) =>
        {
            HistoryScrollViewer.Visibility = Visibility.Collapsed;
            HistoryScrollViewer.Opacity = 1;
        };
        
        // Dashboard淡入
        ContentScrollViewer.Visibility = Visibility.Visible;
        ContentScrollViewer.Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };
        
        HistoryScrollViewer.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        ContentScrollViewer.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        ContentScrollViewer.ScrollToTop();
        SetActiveNav(DashboardNav);
    }

    private void OnHistoryNavClick(object sender, RoutedEventArgs e)
    {
        HideEventDetail(immediate: true);
        
        // 已经在History，无需切换
        if (HistoryScrollViewer.Visibility == Visibility.Visible) return;
        
        // 停止动画
        ContentScrollViewer.BeginAnimation(UIElement.OpacityProperty, null);
        HistoryScrollViewer.BeginAnimation(UIElement.OpacityProperty, null);
        
        var easing = new PowerEase { EasingMode = EasingMode.EaseInOut, Power = 2 };
        var duration = new Duration(TimeSpan.FromMilliseconds(300));
        
        // Dashboard淡出
        var fadeOut = new DoubleAnimation(1, 0, duration) { EasingFunction = easing };
        fadeOut.Completed += (_, _) =>
        {
            ContentScrollViewer.Visibility = Visibility.Collapsed;
            ContentScrollViewer.Opacity = 1;
        };
        
        // History淡入
        HistoryScrollViewer.Visibility = Visibility.Visible;
        HistoryScrollViewer.Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };
        
        ContentScrollViewer.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        HistoryScrollViewer.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        SetActiveNav(HistoryNav);
    }

    /// <summary>
    /// A click on a task row opens <i>that row</i>: the container grows and the
    /// task's details appear inside it. It deliberately does not navigate. The
    /// full-window view is still there — the expanded row carries the button that
    /// opens it — but the first click answers "what was in this task" in place,
    /// which is the question a ledger row is asked.
    /// </summary>
    private void OnEventCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: EventRow row }) return;
        e.Handled = true;
        if (FindRow(row) is { } view) ToggleRow(view);
    }

    private void OnEventSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventsList.SelectedItem is EventRow row && !ReferenceEquals(_selectedEvent, row))
            ShowEventDetail(row);
    }

    private LedgerRowView? FindRow(EventRow row)
    {
        foreach (var item in EventsList.Items)
        {
            if (EventsList.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem container) continue;
            if (FindRowView(container) is { } view && ReferenceEquals(view.Row, row)) return view;
        }
        return null;
    }

    /// <summary>The generated row inside one realized list item.</summary>
    private static LedgerRowView? FindRowView(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is LedgerRowView view) return view;
            if (FindRowView(child) is { } found) return found;
        }
        return null;
    }

    private void OnDetailBackClick(object sender, RoutedEventArgs e) => HideEventDetail();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && HistoryDetailView.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            HideEventDetail();
        }
        else if (e.Key == Key.Escape && _openRow is not null)
        {
            e.Handled = true;
            ToggleRow(_openRow);
        }
    }

    private void ShowEventDetail(EventRow row)
    {
        _selectedEvent = row;
        EventsList.SelectedItem = row;
        HistoryDetailView.DataContext = row;
        ContentScrollViewer.Visibility = Visibility.Collapsed;
        HistoryScrollViewer.Visibility = Visibility.Collapsed;
        HistoryDetailView.Visibility = Visibility.Visible;
        HistoryDetailView.Opacity = 0;
        HistoryDetailScale.ScaleX = 0.97;
        HistoryDetailScale.ScaleY = 0.97;
        // Three panels describe individual relay requests. A balance-derived cost
        // has none, so showing them would only produce empty placeholders that
        // look like a parsing failure.
        var perRequestPanels = row.IsBalanceDelta ? Visibility.Collapsed : Visibility.Visible;
        CostTimelinePanel.Visibility = perRequestPanels;
        TokenTrendPanel.Visibility = perRequestPanels;
        RelayDetailPanel.Visibility = perRequestPanels;
        DrawDetailCharts(row);

        var duration = new Duration(TimeSpan.FromMilliseconds(230));
        HistoryDetailView.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        HistoryDetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        HistoryDetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Dispatcher.BeginInvoke(() => HistoryDetailScrollViewer.ScrollToTop(), DispatcherPriority.Background);
    }

    private void HideEventDetail(bool immediate = false)
    {
        if (HistoryDetailView.Visibility != Visibility.Visible && !immediate) return;
        if (immediate)
        {
            HistoryDetailView.BeginAnimation(UIElement.OpacityProperty, null);
            HistoryDetailView.Visibility = Visibility.Collapsed;
            _selectedEvent = null;
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        var fade = new DoubleAnimation(1, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            HistoryDetailView.Visibility = Visibility.Collapsed;
            HistoryScrollViewer.Visibility = Visibility.Visible;
            HistoryDetailView.Opacity = 0;
            _selectedEvent = null;
            EventsList.SelectedItem = null;
        };
        HistoryDetailView.BeginAnimation(UIElement.OpacityProperty, fade);
        HistoryDetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.97, duration));
        HistoryDetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.97, duration));
    }

    private void SetActiveNav(Button active)
    {
        if (ReferenceEquals(_activeNav, active)) return;
        _activeNav = active;
        
        var easing = new PowerEase { EasingMode = EasingMode.EaseInOut, Power = 2 };
        var duration = new Duration(TimeSpan.FromMilliseconds(250));
        
        foreach (var button in new[] { DashboardNav, HistoryNav })
        {
            var isActive = button == active;
            
            // 停止现有动画
            button.BeginAnimation(Control.BackgroundProperty, null);
            button.BeginAnimation(Control.ForegroundProperty, null);
            
            // 背景色动画: Transparent ↔ #2E2E2E
            var currentBg = (button.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
            var targetBgColor = isActive ? Color.FromRgb(0x2E, 0x2E, 0x2E) : Colors.Transparent;
            var bgBrush = new SolidColorBrush(currentBg);
            button.Background = bgBrush;
            bgBrush.BeginAnimation(SolidColorBrush.ColorProperty, 
                new ColorAnimation(currentBg, targetBgColor, duration) { EasingFunction = easing });
            
            // 前景色动画: SubtleBrush ↔ TextBrush  
            var currentFg = (button.Foreground as SolidColorBrush)?.Color ?? Colors.Gray;
            var targetFgBrush = (Brush)FindResource(isActive ? "TextBrush" : "SubtleBrush");
            var targetFgColor = (targetFgBrush as SolidColorBrush)?.Color ?? Colors.Gray;
            var fgBrush = new SolidColorBrush(currentFg);
            button.Foreground = fgBrush;
            fgBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(currentFg, targetFgColor, duration) { EasingFunction = easing });
        }
    }

    private void UpdateActiveNavigation()
    {
        if (!IsLoaded) return;
        SetActiveNav(ContentScrollViewer.Visibility == Visibility.Visible ? DashboardNav : HistoryNav);
    }

    /// <summary>
    /// Watches the three files the host writes. Every new usage record, balance
    /// snapshot and server summary shows up this way, so the view stays current
    /// without polling anything.
    /// </summary>
    private void StartWatching()
    {
        try
        {
            Directory.CreateDirectory(_store.DirectoryPath);
            _watcher = new FileSystemWatcher(_store.DirectoryPath, "usage-events*.ndjson")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnDataFileChanged;
            _watcher.Created += OnDataFileChanged;
            _watcher.Renamed += OnDataFileRenamed;

            _balanceWatcher = new FileSystemWatcher(_store.DirectoryPath, BalanceUsageSnapshot.FileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _balanceWatcher.Changed += OnDataFileChanged;
            _balanceWatcher.Created += OnDataFileChanged;
            _balanceWatcher.Renamed += OnDataFileRenamed;

            _serverUsageWatcher = new FileSystemWatcher(_store.DirectoryPath, ServerUsageSummarySnapshot.FileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _serverUsageWatcher.Changed += OnDataFileChanged;
            _serverUsageWatcher.Created += OnDataFileChanged;
            _serverUsageWatcher.Renamed += OnDataFileRenamed;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void StopWatching()
    {
        _fileRefreshTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;
        _balanceWatcher?.Dispose();
        _balanceWatcher = null;
        _serverUsageWatcher?.Dispose();
        _serverUsageWatcher = null;
    }

    private void OnDataFileChanged(object sender, FileSystemEventArgs e)
    {
        // A watched file changed, so a new reading has arrived — and this is the only
        // moment the window knows one did, because it watches files instead of asking
        // on a clock. The reading motion hangs here rather than on a timer of its own.
        _readingArrived = true;
        QueueFileRefresh();
    }

    private void OnDataFileRenamed(object sender, RenamedEventArgs e)
    {
        // A host that writes through a temporary file and renames it is the same
        // arrival by another route.
        _readingArrived = true;
        QueueFileRefresh();
    }

    private void QueueFileRefresh()
    {
        if (_fileRefreshPending) return;
        _fileRefreshPending = true;
        Dispatcher.BeginInvoke(new Action(() => _fileRefreshTimer.Start()), DispatcherPriority.Background);
    }

    // ======================================================================
    //  data -> window
    // ======================================================================

    private void Refresh()
    {
        var events = _store.ReadAll();
        _lastEvents = events;
        _accounts = AccountDirectory.ReadAccounts(_store.DirectoryPath);
        // The census first, because every panel below states a count taken from it:
        // the account column's rows, the line under the ledger's title, the group
        // notice and the census line are four readings of this one pass over the log.
        _tally = UsageTally.Build(events, _accounts, _archive.Limit);
        _probeReadMs = _probeWatch?.Elapsed.TotalMilliseconds ?? 0;
        // The scope before anything is drawn from it, because every panel below states
        // something about one account: the window has no combined view any more, so a
        // scope it cannot name is a page it cannot draw. It is resolved against the
        // balance document — the only thing that says which accounts exist — and the
        // document is read again only when the resolution moved the scope, which is
        // once per change of the account set and never in a steady run.
        //
        // A group is not an account, so the document has no entry for it: for a group
        // scope the read below is deliberately the whole document, because an id the
        // document does not name would filter every entry away. Nothing on the page
        // reads a document-wide total any more — every figure is taken from the row of
        // the account it belongs to. See REDESIGN-NOTES 第六轮 §3.
        _lastBalanceUsage = BalanceUsageSnapshot.Read(
            _store.DirectoryPath, DateTimeOffset.Now, BalanceScopeOf(_accountFilter));
        var resolved = ResolveScope(_lastBalanceUsage);
        if (!string.Equals(resolved, _accountFilter, StringComparison.Ordinal))
        {
            _accountFilter = resolved;
            _lastBalanceUsage = BalanceUsageSnapshot.Read(
                _store.DirectoryPath, DateTimeOffset.Now, BalanceScopeOf(_accountFilter));
        }
        // The report is built from the scope rather than from the log: the four
        // readouts and the trend are the current account's, which is what the page
        // says they are.
        _lastReport = UsageReport.Build(ScopeTo(events, _accountFilter), DateTimeOffset.Now);
        _probeReportMs = _probeWatch?.Elapsed.TotalMilliseconds ?? 0;
        // ... and what this plugin has now seen of those balances. That record is the
        // only place a recharge can be read out of, and writing it here is what lets the
        // next look tell money arriving from money being spent; see BalanceWatchStore.
        // A refresh that finds the same balances records nothing and writes nothing.
        _balances.Observe(_lastBalanceUsage.Rows, DateTimeOffset.Now);
        _lastServerUsage = ServerUsageSummarySnapshot.Read(
            _store.DirectoryPath, DateTimeOffset.Now, AccountScopeOf(_accountFilter));

        UpdateBalanceBar(events);
        // Straight after the ruler has written both readings, because this is a
        // motion of what the ruler just decided — including the colour it decided
        // they are; see PlayReadingMotion.
        PlayReadingMotion();
        // The chip bar's counts are the ledger's counts: the client chips sit above
        // the list they filter and describe what that list holds, and the left
        // column's account now decides part of that. Counting every event here
        // instead would make a chip say 971 while the list under it held ten.
        // The ledger's scope, resolved once and handed to everything that states a
        // count of it: the chips above the list, the rows in it, the summary beside
        // them. Two calls to VisibleEvents could not disagree, but the census line
        // and the notice do state the same numbers as these three, and one resolved
        // list is what makes that checkable rather than argued.
        var scoped = VisibleEvents(events);
        // The dashboard's own scope: the account, and not the client chips. A chip
        // narrows the list the ledger shows; the dashboard's 「按提供方拆分」 is where
        // the clients are compared, so applying a chip to it would leave one bar in
        // the breakdown it exists to draw.
        var accountScoped = ScopeTo(events, _accountFilter);
        UpdateProviderFilters(scoped);
        UpdateMetricStrip(accountScoped);
        UpdateModelTable(accountScoped);
        UpdateProviderBreakdown(accountScoped);
        UpdateAccountTable(events);
        UpdateAccountSelector(_lastBalanceUsage);
        RefreshGroupRowCounts();

        var rowSource = scoped.Take(RecentLedgerLimit).ToArray();
        _probePanelsMs = _probeWatch?.Elapsed.TotalMilliseconds ?? 0;
        // The ledger is rebuilt only when the tasks it lists change. Assigning
        // ItemsSource is what makes the list throw every realized container away —
        // sixty-four of them, each with a generated row inside it — and a refresh
        // that changes none of those tasks would pay that for nothing. The scope
        // change is exactly that refresh: which account the dashboard describes does
        // not decide which tasks the ledger lists, and clicking an account in the
        // list rebuilt all of it. The signature carries the provider filter as well
        // as the task ids, so a chip that really does change the rows still rebuilds.
        var ledgerSignature = $"{_accountFilter}|{_providerFilter}|{string.Join("|", rowSource.Select(value => value.EventId))}";
        if (ledgerSignature != _ledgerSignature)
        {
            _ledgerSignature = ledgerSignature;
            var eventRows = rowSource.Select(value => new EventRow(value, _accounts, AccountCurrency(value.AccountId))).ToArray();
            EventsList.ItemsSource = eventRows;
            RebuildLedger();
            _probeRowsMs = _probeWatch?.Elapsed.TotalMilliseconds ?? 0;
            if (_selectedEvent is not null)
            {
                var refreshed = eventRows.FirstOrDefault(value => string.Equals(value.EventId, _selectedEvent.EventId, StringComparison.OrdinalIgnoreCase));
                if (refreshed is null) HideEventDetail(immediate: true);
                else
                {
                    _selectedEvent = refreshed;
                    HistoryDetailView.DataContext = refreshed;
                    DrawDetailCharts(refreshed);
                }
            }
            RecentSummary.Text = BuildRecentSummary(scoped, rowSource.Length);
        }
        UpdateLedgerScopeLabel();
        var hasUsageDetails = events.Any(value =>
            value.InputTokens.HasValue || value.OutputTokens.HasValue || value.CacheReadTokens.HasValue ||
            value.CacheWriteTokens.HasValue || value.DurationMs.HasValue || value.TimeToFirstTokenMs.HasValue ||
            !string.IsNullOrWhiteSpace(value.Model));
        StatusText.Text = events.Count == 0
            ? $"暂无用量事件 · 请确认 BalancePet 已启用 AI 任务联动 · 数据目录：{_store.DirectoryPath}"
            : hasUsageDetails
                ? $"已读取 {events.Count:N0} 条事件 · 最后刷新：{DateTime.Now:HH:mm:ss}"
                : $"已读取 {events.Count:N0} 条生命周期事件；客户端尚未上报 Token、模型和耗时。若客户端支持计数，请通过 Usage.v1 或 balancepet-usage.ps1 上报 · 最后刷新：{DateTime.Now:HH:mm:ss}";
        DrawTrendChart();
        Dispatcher.BeginInvoke(UpdateActiveNavigation, DispatcherPriority.Background);
        if (_probeWatch is { } probeWatch)
        {
            // The rest of the pass is what a person waits for: the four panels are
            // built, the tree is measured and the frame is drawn. Nothing above is
            // timed in isolation on purpose — the numbers in the notes have to add
            // up to the click the user actually made.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                probeWatch.Stop();
                _probeRefreshMs = probeWatch.Elapsed.TotalMilliseconds;
            }), DispatcherPriority.Render);
        }
    }

    // ----------------------------------------------------------------------
    //  the balance ruler
    // ----------------------------------------------------------------------

    /// <summary>
    /// One card, two readings, routed by account type.
    ///
    /// <para>
    /// "Subscription" is the union of two independent signals, the same union
    /// <see cref="EventRow.IsSubscriptionAccount"/> uses: a subscription can be
    /// declared by the user on the account, and it can be reported per session by
    /// the client on <c>UsageEvent.plan_type</c>. Either one is enough, and the
    /// pay-as-you-go reading is only taken when neither is present — so the bar can
    /// never show a percentage for a key that has no ceiling, which is the one
    /// reading this design forbids.
    /// </para>
    /// </summary>
    private void UpdateBalanceBar(IReadOnlyList<UsageEvent> events)
    {
        var subscription = SubscriptionAccounts(events);
        var scoped = _accounts.TryGetValue(_accountFilter, out var info) ? info : null;
        var rows = _lastBalanceUsage.Rows;
        // A group is not an account and has no plan, so it cannot be a subscription by
        // either signal: the declaration is per account and the client's plan_type is
        // per account too. It used to fall through to "every account in the document is
        // a subscription", which drew a plan card named after one account's plan over a
        // group of records belonging to no account at all.
        var subscriptionScoped = UsageGroups.GroupOf(_accountFilter) is null
            && (scoped is not null
                ? scoped.IsSubscription || subscription.Contains(_accountFilter)
                : rows.Count > 0 && rows.All(row => subscription.Contains(row.AccountId)));

        if (subscriptionScoped) ShowQuotaRuler(events, subscription, scoped);
        else ShowBalanceRuler(events, subscription, scoped);
    }

    /// <summary>Accounts billed by subscription, by either signal.</summary>
    private HashSet<string> SubscriptionAccounts(IReadOnlyList<UsageEvent> events)
    {
        var declared = _accounts.Where(pair => pair.Value.IsSubscription).Select(pair => pair.Key);
        var reported = events.Where(value => value.PlanType.Length > 0 && value.AccountId.Length > 0).Select(value => value.AccountId);
        return declared.Concat(reported).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Subscription: how much of the allowance is gone and when it renews. The
    /// centre is a share of a denominator, which is the only thing this variant can
    /// honestly draw — and where the host reported no ceiling it is drawn as no
    /// reading at all rather than as 0%.
    /// </summary>
    private void ShowQuotaRuler(IReadOnlyList<UsageEvent> events, HashSet<string> subscription, AccountDirectory.AccountInfo? plan)
    {
        QuotaRuler.Visibility = Visibility.Visible;
        ApiPacePanel.Visibility = Visibility.Collapsed;
        TodayUsageCaption.Text = "今日消耗";
        BalanceAmount.Text = "已用";
        ApiWindowText.Text = "";
        ApiRecentThirtyText.Text = "—";
        ApiEstimateNote.Text = "";

        var planName = PlanName(events, subscription) ?? (plan?.IsSubscription == true ? plan.Name : "");
        QuotaLabel.Text = planName.Length > 0 ? $"套餐额度 · {planName}" : "套餐额度";
        QuotaAccountType.Text = "订阅账户";

        var snapshot = _lastBalanceUsage;
        var now = DateTimeOffset.Now;

        // The card and the account list's row state one reading of this account's
        // settings, and this is the resolution both of them are drawn from — the row
        // calls the same function for itself. See QuotaReading: before it existed the
        // two were built from two readings, and an account whose settings declare only
        // quota_windows showed a percentage here and a dash in the list.
        var readings = QuotaReading.All(_accountFilter, plan, events, now);
        var primary = readings.Count > 0 ? readings[0] : null;

        // The left figure belongs to this branch too. It used to be written only by
        // the pay-as-you-go ruler, so a window that opened on a subscription showed
        // the sample's placeholder under a label that says 已用 — and after a switch
        // it showed the previous account's balance there instead. It is the cycle's
        // spend *in money*, summed from the events through the primitive the reading's
        // own numerator is summed with, over the plan's own reset day rather than a
        // window chosen here. The bars beside it state their own window's used figure,
        // which a rate window counts in tokens or messages: two measures, each named,
        // and neither of them invented for the other.
        var cycleDay = readings
                .Where(reading => reading.ResetDay is not null)
                .Select(reading => reading.ResetDay)
                .FirstOrDefault()
            ?? plan?.QuotaResetDay
            ?? 1;
        var (cycleUsed, hasCycleUsed) = UsageInCycle(events, subscription, BillingCycle.Start(now, cycleDay));
        TotalBalance.Text = hasCycleUsed ? Money(cycleUsed) : "未上报";
        TotalBalance.Foreground = (Brush)FindResource(hasCycleUsed ? "TextBrush" : "StaleTintBrush");
        // This account's own unit, off the same document row the row in the account
        // list reads: the document's common currency is "the unit every account happens
        // to share", which is a statement about the set rather than about this account,
        // and it collapses to a default the moment two accounts disagree.
        BalanceCurrency.Text = snapshot.Rows
            .FirstOrDefault(row => string.Equals(row.AccountId, _accountFilter, StringComparison.OrdinalIgnoreCase))?.Currency ?? "";

        // Multi-window quota support: the card states the window that is closest to
        // its ceiling and folds the rest away. Three bars is a card and a half of
        // height, and the two least urgent of them are not the ones that stop work —
        // but they are still one click away, because a plan really is enforced by
        // all of them at once. The readings arrive in that order, and the same
        // record states the row in the account list.
        // The unit this account's quota figures are in, off its own row of the balance
        // document rather than off the document's "every account happens to agree"
        // currency: the bars below name a ceiling and a used figure, and those are this
        // account's money.
        var quotaCurrency = snapshot.Rows
            .FirstOrDefault(row => string.Equals(row.AccountId, _accountFilter, StringComparison.OrdinalIgnoreCase))?.Currency ?? "";
        if (primary is { CeilingDeclared: true })
        {
            QuotaWindowsContainer.Children.Clear();

            // The primary (most urgent) window is always visible.
            QuotaWindowsContainer.Children.Add(BuildQuotaWindowBar(primary, quotaCurrency, isPrimary: true));
            if (readings.Count > 1) BuildQuotaDisclosure(readings, quotaCurrency);

            // Update summary hint based on most critical window
            if (primary.Percent is { } pct)
            {
                TotalUsageHint.Text = pct >= 95
                    ? $"⚠ {primary.DisplayName}已达 {pct:0.#}%，即将耗尽"
                    : pct >= 80
                        ? $"{primary.DisplayName}已用 {pct:0.#}%，接近上限"
                        : readings.Count > 1
                            ? "多个配额窗口同时生效 · 任一窗口耗尽都会限制使用"
                            : $"{primary.DisplayName} · 由余额快照记录";
            }
            else
            {
                TotalUsageHint.Text = "多个配额窗口同时生效 · 由余额快照记录";
            }
        }
        else
        {
            // A subscription whose settings name no ceiling in either shape — neither
            // the windows nor the deprecated single ceiling: no bar, and no promise of
            // one. An earlier version left this hint at its placeholder — "等待主程序
            // 累计记录" — which is addressed to whoever wrote the host rather than to
            // the person reading the window, and reads as something still loading when
            // nothing is loading. The reading beside it is the whole truth available.
            QuotaWindowsContainer.Children.Clear();
            _quotaBar = null;
            TotalUsageHint.Text = $"{QuotaReading.NoLimitText} · 这里只显示已用；BalancePet 会记录累计用量";
        }

        // 今日消耗 is this account's own day. It was snapshot.TodayUsage, which is every
        // account's usage for today added together — the same cross-account sum the
        // pay-as-you-go card had, one branch over, and the same reason it cannot be one
        // number: 「每个账户的额度单位…都不一致」.
        var todayRow = snapshot.Rows
            .FirstOrDefault(row => string.Equals(row.AccountId, _accountFilter, StringComparison.OrdinalIgnoreCase));
        var today = todayRow?.HasToday == true ? Reading.Reported(todayRow.Today) : Reading.Missing();
        TodayUsage.Text = MoneyText(today, "暂无数据");
        TodayUsageHint.Text = todayRow?.HasToday == true ? "按余额变化推算" : "尚无余额变化记录";
        MarkStale(TodayUsage, todayRow?.HasToday != true || IsStale(snapshot.UpdatedAt));
        BalanceAmount.Foreground = (Brush)FindResource("MutedBrush");
    }

    /// <summary>
    /// The fold under the primary quota bar: the remaining windows, and the one
    /// control that shows or hides them.
    ///
    /// <para>
    /// Closed is the resting state and the default, because the card exists to
    /// state the window that is about to stop work rather than to list every
    /// allowance the plan has. The state is remembered per account and survives a
    /// refresh — the ruler is rebuilt whenever the host writes a balance, and a fold
    /// that sprang open again on every write would be one the user has to keep
    /// closing.
    /// </para>
    ///
    /// <para>
    /// The readings arrive in the card's own order, so the fold holds everything
    /// after the first one — the bars it draws are the same records the open bar is
    /// drawn from, not a second look at the settings.
    /// </para>
    /// </summary>
    private void BuildQuotaDisclosure(IReadOnlyList<QuotaReading> readings, string currency)
    {
        var scope = _accountFilter;
        var expanded = _expandedQuotaScopes.Contains(scope);

        var more = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 12, 0, 0) };
        for (var index = 1; index < readings.Count; index++)
            more.Children.Add(BuildQuotaWindowBar(readings[index], currency, isPrimary: false));
        // The fold is spaced by its own 12 px above; a last bar's bottom margin would
        // be a 12 px empty band inside the fold, under the bar it belongs to.
        if (more.Children[^1] is FrameworkElement last) last.Margin = new Thickness(0);

        // ClipToBounds, because the fold is a height being animated under content
        // that is already a full height: without it the bars below would simply be
        // drawn outside the card until the height catches up.
        var fold = new Grid { ClipToBounds = true, Height = expanded ? double.NaN : 0 };
        fold.Children.Add(more);

        var glyph = new TextBlock
        {
            Text = expanded ? "▲" : "▼",
            FontSize = 10,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("StaleTintBrush"),
        };
        var caption = new TextBlock
        {
            Text = expanded ? "收起" : QuotaFoldCaption(readings.Count),
            Style = (Style)FindResource("Note"),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("StaleTintBrush"),
        };
        var toggle = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { glyph, caption },
            },
        };
        toggle.Click += (_, _) =>
        {
            // The set is the state: reading it back off the fold's Height would have
            // to tell "closed" from "not laid out yet", and the two are not the same.
            var opening = !_expandedQuotaScopes.Contains(scope);
            if (opening) _expandedQuotaScopes.Add(scope);
            else _expandedQuotaScopes.Remove(scope);
            FoldQuotaDisclosure(fold, more, glyph, caption, readings.Count, opening);
        };

        _quotaToggle = toggle;
        _quotaFold = fold;
        _quotaCaption = caption;
        QuotaWindowsContainer.Children.Add(toggle);
        QuotaWindowsContainer.Children.Add(fold);
    }

    /// <summary>What the fold's control offers: the number of windows it is holding back.</summary>
    private static string QuotaFoldCaption(int windowCount)
        => $"展开其余 {windowCount - 1} 个配额窗口";

    /// <summary>
    /// Opens or closes the quota fold on the design's chrome transition:
    /// <c>PowerEase(EaseInOut, Power=2)</c> over 250 ms, the same hand as the client
    /// chips and the account rows.
    ///
    /// <para>
    /// Driven by a <see cref="MotionBoard"/> lane rather than a
    /// <see cref="DoubleAnimation"/> on the height so that a capture can hold a
    /// named millisecond of the fold, exactly as it does for a task row — the
    /// animation's job here is to show where the two bars came from, and a still
    /// cannot show that unless the still knows which instant it is.
    /// </para>
    /// </summary>
    private void FoldQuotaDisclosure(
        Grid fold, FrameworkElement content, TextBlock glyph, TextBlock caption, int windowCount, bool opening)
    {
        var from = fold.ActualHeight;
        var to = 0d;
        if (opening)
        {
            // Measured off the content, not off the fold. The fold carries an
            // explicit Height — 0 while closed — and a framework element's
            // DesiredSize is that explicit height, so measuring the fold returns the
            // 0 it is already at: the animated height then runs from 0 to 0 and the
            // fold never opens. The first version of this control did exactly that.
            var width = fold.ActualWidth > 0 ? fold.ActualWidth : content.ActualWidth;
            content.Measure(new Size(width, double.PositiveInfinity));
            to = content.Margin.Top + content.DesiredSize.Height;
        }

        glyph.Text = opening ? "▲" : "▼";
        caption.Text = opening ? "收起" : QuotaFoldCaption(windowCount);

        if (!SystemParameters.ClientAreaAnimation)
        {
            // "Reduced motion" is the final state, not a shorter animation.
            fold.Height = to;
            _quotaBoard = null;
            return;
        }

        _quotaBoard?.Stop();
        var board = new MotionBoard();
        _quotaBoard = board;
        board.Add("quota-fold", value => fold.Height = value, from, to, 0,
            ChromeTransition.TotalMilliseconds, MotionBoard.PowerInOut());
        board.Start();
    }

    /// <summary>
    /// Builds one quota window's progress bar with its label, percentage and reset
    /// time, from the reading both this card and the account row are drawn from — the
    /// percentage, the used / ceiling pair and the countdown are that record's own, so
    /// this bar and the row beside it cannot be given two accounts of one allowance.
    /// Includes non-linear animation using PowerEase.
    /// </summary>
    private StackPanel BuildQuotaWindowBar(QuotaReading reading, string currency, bool isPrimary = false)
    {
        var now = DateTimeOffset.Now;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        // Header: window name + reset time
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text = reading.DisplayName,
            Style = (Style)FindResource("RowLabel"),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        Grid.SetColumn(nameText, 0);

        var resetText = new TextBlock
        {
            Text = reading.ResetText(now),
            Style = (Style)FindResource("Digit"),
            FontSize = 10,
            Foreground = (Brush)FindResource("StaleTintBrush"),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        Grid.SetColumn(resetText, 1);
        headerGrid.Children.Add(nameText);
        headerGrid.Children.Add(resetText);
        panel.Children.Add(headerGrid);

        // Progress bar
        var barHeight = 14.0;
        var barGrid = new Grid
        {
            ClipToBounds = true,
            Height = barHeight
        };

        var trackRect = new Rectangle
        {
            Fill = (Brush)FindResource("QuotaTrackBrush"),
            RadiusX = 7,
            RadiusY = 7
        };
        barGrid.Children.Add(trackRect);

        var fillScale = new ScaleTransform { ScaleX = 0, ScaleY = 1 };
        var fillTrack = new Grid
        {
            ClipToBounds = true,
            RenderTransform = fillScale
        };

        // The record's own share, clamped for the drawing and never recomputed here:
        // this bar's width and the percentage beside it are two drawings of one number.
        var percent = reading.Percent ?? 0;

        var fillRect = new Rectangle
        {
            Fill = percent switch
            {
                >= 95 => new SolidColorBrush(Color.FromRgb(0xE0, 0x5C, 0x5C)),
                >= 80 => (Brush)FindResource("FlowCostBrush"),
                _ => (Brush)FindResource("FlowRateBrush"),
            },
            RadiusX = 6,
            RadiusY = 6
        };

        fillTrack.Children.Add(fillRect);
        barGrid.Children.Add(fillTrack);
        panel.Children.Add(barGrid);

        // Animate bar with PowerEase (non-linear). A bar nobody has seen yet grows
        // in; a bar that is already on screen rolls only when its reading moved.
        // Rolling it again on every refresh is a bar that throbs whenever the host
        // writes a file, which is a refresh — the ruler is rebuilt each time, and
        // the value it is rebuilt with is usually the one it already had.
        var targetScale = reading.Fraction;
        var barKey = $"{_accountFilter}|{reading.WindowType}";
        var seen = _quotaFillApplied.TryGetValue(barKey, out var applied);
        _quotaFillApplied[barKey] = targetScale;
        if (seen && Math.Abs(applied - targetScale) <= 0.0005) fillScale.ScaleX = targetScale;
        else AnimateQuotaBar(fillScale, targetScale, seen ? applied : null);

        // Usage text: "4 / 5 条", "138M / 252M", or the used side and the honest note
        // when nobody reported a ceiling. The account row writes the same string from
        // the same record.
        var usageGrid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        usageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        usageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var usageText = new TextBlock
        {
            Text = reading.AmountText(currency),
            Style = (Style)FindResource("Digit"),
            FontSize = 10.5,
            Foreground = (Brush)FindResource("MutedBrush")
        };

        Grid.SetColumn(usageText, 0);

        var percentText = new TextBlock
        {
            Text = reading.PercentText,
            Style = (Style)FindResource("Digit"),
            FontSize = 11,
            Foreground = percent switch
            {
                >= 95 => new SolidColorBrush(Color.FromRgb(0xE0, 0x5C, 0x5C)),
                >= 80 => (Brush)FindResource("FlowCostBrush"),
                _ => (Brush)FindResource("FlowRateBrush"),
            },
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(percentText, 1);

        usageGrid.Children.Add(usageText);
        usageGrid.Children.Add(percentText);
        panel.Children.Add(usageGrid);

        // The bar that states the account is the bar a capture compares the account
        // row against; see RunCapture.
        if (isPrimary) _quotaBar = new QuotaBarTexts(nameText, usageText, percentText);

        return panel;
    }

    /// <summary>
    /// Animate a ScaleTransform with PowerEase for non-linear easing.
    /// Duration: 600ms, EasingMode: EaseOut (快进慢出)
    /// </summary>
    /// <param name="from">
    /// Where the roll starts, when the caller knows the value the reading is coming
    /// from. Null reads it off the transform, which is right for a bar being seen
    /// for the first time — it is at zero.
    /// </param>
    private void AnimateQuotaBar(ScaleTransform scale, double targetScaleX, double? from = null)
    {
        var animation = new DoubleAnimation
        {
            From = from ?? scale.ScaleX,
            To = targetScaleX,
            Duration = TimeSpan.FromMilliseconds(600),
            EasingFunction = new PowerEase { EasingMode = EasingMode.EaseOut, Power = 3 }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
    }

    /// <summary>
    /// Pay-as-you-go: a balance, the pace today's spend implies, and the user's own
    /// tripwire. No percentage anywhere — an API key has no ceiling, and a bar
    /// against an invented denominator is a made-up number wearing a progress
    /// indicator.
    ///
    /// <para>
    /// Every figure on the card is the scoped account's own. It used to fall back to a
    /// sum over every non-subscription account whenever no account was picked —
    /// 「余额快照 · 非订阅账户合计」 — which added CNY to USD and printed the result as a
    /// balance. The user's own words for why that cannot be a reading: 「去掉全部账户
    /// （没有意义，毕竟每个账户的额度单位，token效率都不一致）」. A group scope has no
    /// account behind it at all, so the card states that instead of a number.
    /// </para>
    /// </summary>
    private void ShowBalanceRuler(IReadOnlyList<UsageEvent> events, HashSet<string> subscription, AccountDirectory.AccountInfo? plan)
    {
        QuotaRuler.Visibility = Visibility.Collapsed;
        ApiPacePanel.Visibility = Visibility.Visible;
        TodayUsageCaption.Text = "今日费用";
        var snapshot = _lastBalanceUsage;
        var scopedRow = snapshot.Rows.FirstOrDefault(row => string.Equals(row.AccountId, _accountFilter, StringComparison.OrdinalIgnoreCase));
        var group = UsageGroups.GroupOf(_accountFilter);
        var balance = scopedRow?.HasBalance == true ? Reading.Reported(scopedRow.Balance) : Reading.Missing();
        // No fallback to the document's own common currency: a unit is a property of
        // the account the figure belongs to, and an account with no balance entry has
        // no figure for it to belong to.
        var currency = scopedRow?.Currency ?? "";
        BalanceAmount.Text = group is null && LedgerScopeLabel.Length > 0 ? $"余额 · {LedgerScopeLabel}" : "余额";

        TotalBalance.Text = MoneyText(balance, "未上报");
        TotalBalance.Foreground = balance.Value is null
            ? (Brush)FindResource("StaleTintBrush")
            : balance.Value < (plan?.WarningBelow ?? double.NegativeInfinity)
                ? (Brush)FindResource("FlowCostBrush")
                : (Brush)FindResource("TextBrush");
        BalanceCurrency.Text = currency;
        BalanceAmount.Foreground = (Brush)FindResource("MutedBrush");

        // 30 days is the window the balance document itself keeps, so the divisor is
        // a number the host published rather than one chosen here to look good. Both
        // figures come off the same row as the balance above: they were once sums over
        // every account the moment no account was selected, and the day's fallback was
        // the document's own total, which is every account's money at once.
        var recent = scopedRow?.HasRecent == true ? scopedRow.Recent : 0;
        var perDay = recent > 0 ? recent / 30d : scopedRow?.HasToday == true ? scopedRow.Today : 0;
        ApiRecentThirtyText.Text = recent > 0 ? Money(recent) : "未上报";
        ApiDaysText.Text = balance.Value is { } money && perDay > 0
            ? $"按今日速率 ≈ 还能用 {money / perDay:0.#} 天"
            : "按今日速率 ≈ 可用天数无法推算";
        ApiRateWindow.Text = perDay > 0 ? $"≈ {Money(perDay)}/天 · 近 30 天口径" : "近 30 天没有可用记录";
        ApiWindowText.Text = snapshot.UpdatedAt is { } stamp ? $"快照 {stamp.ToLocalTime():MM-dd HH:mm}" : "";
        ApiRulerLabel.Text = "余额快照";

        // The user's own tripwire. The reading itself turns FlowCost when it is
        // under the line set for this account; when there is no line, saying so is
        // more useful than an empty row, and an empty row would read as a missing
        // warning rather than an unset one.
        if (plan?.WarningBelow is { } threshold)
        {
            var fired = balance.Value is { } value && value < threshold;
            ApiRateBadge.Text = fired
                ? $"已低于你设定的预警线 {Money(threshold)} {currency}".TrimEnd()
                : $"预警线 {Money(threshold)} {currency}".TrimEnd() + " · 未触发";
            ApiRateBadge.Foreground = (Brush)FindResource(fired ? "FlowCostBrush" : "StaleTintBrush");
        }
        else
        {
            ApiRateBadge.Text = "尚未设置预警线；低于它时上面这个数字会用费用色标出来";
            ApiRateBadge.Foreground = (Brush)FindResource("StaleTintBrush");
        }
        // Monthly budget bar — only when the user declared a budget for this account
        if (plan?.MonthlyBudget is { } budget and > 0)
        {
            BudgetBarSection.Visibility = Visibility.Visible;
            var monthStart = BillingCycle.Start(DateTimeOffset.Now, 1); // calendar month
            // This account's month, and only this account's: a budget is declared on one
            // account, so there is never a reason to reach past it. The sum is taken in
            // the one currency those records are in — a month whose records arrived in
            // two of them has no total, and the bar says so instead of drawing a
            // percentage against a number that added them.
            var month = _lastEvents
                .Where(v => v.OccurredAt >= monthStart
                    && string.Equals(v.AccountId, _accountFilter, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var reported = month.Any(v => v.Cost.HasValue);
            var monthCurrency = SingleCurrency(month, currency);
            // Three states, and the bar states which one it is: this month's cost, a
            // month nobody reported a cost for, and a month whose records arrived in two
            // currencies. The last two used to draw 0% against the budget, which is a
            // measurement of nothing wearing a percentage.
            var monthSpend = reported && monthCurrency.Length > 0 ? month.Sum(v => v.Cost ?? 0) : 0;
            var budgetPct = monthCurrency.Length > 0 ? Math.Clamp(monthSpend * 100d / budget, 0, 100) : 0;
            BudgetFillScale.ScaleX = budgetPct / 100d;
            BudgetBarPercent.Text = monthCurrency.Length > 0 ? $"{budgetPct:0.#}%" : "未上报";
            BudgetBarUsedText.Text = monthCurrency.Length > 0
                ? $"本月 {Money(monthSpend)} / 预算 {Money(budget)} {monthCurrency}"
                : reported
                    ? $"本月记录不止一种币种 · 预算 {Money(budget)} {currency}".TrimEnd() + " 不与之相加"
                    : $"本月费用未上报 · 预算 {Money(budget)} {currency}".TrimEnd();
            BudgetFillRect.Fill = monthCurrency.Length == 0 ? (Brush)FindResource("QuotaTrackBrush") : budgetPct switch
            {
                >= 95 => new SolidColorBrush(Color.FromRgb(0xE0, 0x5C, 0x5C)),
                >= 80 => (Brush)FindResource("FlowCostBrush"),
                _     => (Brush)FindResource("FlowRateBrush"),
            };
            BudgetBarPercent.Foreground = monthCurrency.Length == 0 ? (Brush)FindResource("StaleTintBrush") : budgetPct switch
            {
                >= 95 => new SolidColorBrush(Color.FromRgb(0xE0, 0x5C, 0x5C)),
                >= 80 => (Brush)FindResource("FlowCostBrush"),
                _     => (Brush)FindResource("FlowRateBrush"),
            };
        }
        else
        {
            BudgetBarSection.Visibility = Visibility.Collapsed;
        }

        // The day's cost is the scoped account's own day, never the document's. It used
        // to be snapshot.TodayUsage, which is every account's usage for today added
        // together — the same sum in a different place, and the same reason it cannot
        // be one number.
        var today = scopedRow?.HasToday == true ? Reading.Reported(scopedRow.Today) : Reading.Missing();
        TodayUsage.Text = MoneyText(today, "暂无数据");
        TodayUsageHint.Text = scopedRow?.HasToday == true ? "按余额变化推算" : "尚无余额变化记录";
        MarkStale(TodayUsage, scopedRow?.HasToday != true || IsStale(snapshot.UpdatedAt));
    }

    /// <summary>
    /// The cycle's spend, summed from the events themselves so the numerator and
    /// the denominator describe the same window. The events are the only source
    /// with a timestamp; the balance document is dated by day.
    ///
    /// <para>
    /// Summed per account through <see cref="QuotaReading.CycleSpend"/>, which is the
    /// one place that sum is written — a reading for a single account is that same
    /// call, so the total here and a row's own figure cannot be two different sums of
    /// one account's events. An account in scope that never reported a cost is not a
    /// zero: the flag says whether anything was reported at all.
    /// </para>
    /// </summary>
    private (double Used, bool HasUsed) UsageInCycle(IReadOnlyList<UsageEvent> events, HashSet<string> subscription, DateTimeOffset from)
    {
        // One account, always. It used to fall back to every subscription account when
        // no account was selected, which is the same cross-account sum in another
        // place: 「已用」 over several accounts is money from several units added
        // together. The fallback is unreachable now — a scope is always one account or
        // one group, and a group never reaches this branch — and it is written as one
        // account so that it stays unreachable.
        var scope = new[] { _accountFilter };
        double used = 0;
        var hasUsed = false;
        foreach (var account in scope)
        {
            var (amount, reported) = QuotaReading.CycleSpend(events, account, from);
            used += amount;
            hasUsed |= reported;
        }
        return (used, hasUsed);
    }

    /// <summary>The reported plan for the accounts in scope, or null when nobody reported one.</summary>
    private static string? PlanName(IReadOnlyList<UsageEvent> events, HashSet<string> subscription)
        => events.Where(value => value.PlanType.Length > 0 && subscription.Contains(value.AccountId))
            .GroupBy(value => value.PlanType, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();

    // ======================================================================
    //  the reading motion: a new reading rolls in, and its tint decays
    // ======================================================================

    /// <summary>
    /// The balance and today's cost roll in when a new reading arrives: the figure
    /// rises one line's worth from below on its own
    /// <see cref="TranslateTransform"/>, and its colour comes back from FlowCost to
    /// whatever colour the ruler decided that reading is.
    ///
    /// <para>
    /// Only those two properties are ever written. The width, the height and the
    /// font size of the window's only 26 px figure are not touched, so a reading that
    /// is moving sits in the same box as a reading that has settled and the card it
    /// is in cannot reflow while it moves.
    /// </para>
    ///
    /// <para>
    /// Three things hold it back, and each of them has to. A reading that did not
    /// arrive from a file does not roll: opening the window, or pressing 刷新, writes
    /// the numbers without a gesture, because nothing arrived. Two arrivals within
    /// twelve seconds roll once, because a host writes a file per event and without
    /// that the two figures would be permanently in motion — a meter rather than a
    /// window. And a reader who has turned animations off gets no motion at all: the
    /// reading has already been written in its final colour and position by the time
    /// this runs, so doing nothing here <i>is</i> the final state. The number is never
    /// held back with the motion — the debounce is on the gesture, not on the value.
    /// </para>
    /// </summary>
    private void PlayReadingMotion()
    {
        // A capture names the millisecond it wants to photograph, so it asks for the
        // gesture rather than waiting for a host to write a file.
        var held = _capture.ReadingMotionAt;
        if (!_readingArrived && held is null) return;
        _readingArrived = false;

        if (!SystemParameters.ClientAreaAnimation)
        {
            if (held is not null) Console.Error.WriteLine("reading motion: ClientAreaAnimation is off; the capture holds the final state.");
            return;
        }
        if (held is null && DateTime.UtcNow - _lastReadingMotionUtc < ReadingMotionDebounce) return;
        _lastReadingMotionUtc = DateTime.UtcNow;

        var board = new MotionBoard();
        AddReadingLanes(board, TotalBalance, "balance");
        AddReadingLanes(board, TodayUsage, "today");
        _readingBoard?.Stop();
        _readingBoard = board;
        if (held is { } at)
        {
            // Seeked, not started: the filename and the pixels describe one instant.
            board.Seek(at);
            ProbeLine($"capture: reading motion t={at:0} ms  {board.Sample(at)}");
            return;
        }
        board.Start();
    }

    /// <summary>
    /// One figure's two lanes: where it sits and what colour it is.
    ///
    /// <para>
    /// The travel is the figure's own font size — one line's worth, so the reading
    /// arrives from exactly the line below it. The colour it decays <i>to</i> is the
    /// one the ruler chose for that reading rather than a fixed Text: a balance under
    /// the user's warning line is amber at rest and a reading nobody reported is the
    /// stale tint, so ending every roll on white would repaint a warning as an
    /// ordinary number the moment it moved.
    /// </para>
    /// </summary>
    private void AddReadingLanes(MotionBoard board, TextBlock figure, string lane)
    {
        var text = ((SolidColorBrush)FindResource("TextBrush")).Color;
        var terminal = figure.Foreground is SolidColorBrush current ? current.Color : text;
        // A brush of this figure's own: the resource brushes are frozen, and a frozen
        // brush cannot be repainted by a lane.
        var brush = new SolidColorBrush(terminal);
        figure.Foreground = brush;
        var shift = new TranslateTransform();
        figure.RenderTransform = shift;
        var cost = ((SolidColorBrush)FindResource("FlowCostBrush")).Color;
        board.Add($"{lane}-rise", value => shift.Y = value, figure.FontSize, 0, 0, ReadingRollMs, MotionBoard.CubicOut);
        board.Add($"{lane}-color", value => brush.Color = Blend(cost, terminal, value), 0, 1, 0, ReadingDecayMs, MotionBoard.CubicOut);
    }

    /// <summary>Two palette colours, mixed by one lane's progress: the FlowCost
    /// reading decaying back to the colour the ruler gave it.</summary>
    private static Color Blend(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    // ----------------------------------------------------------------------
    //  the four-column strip, the tables
    // ----------------------------------------------------------------------

    /// <summary>
    /// The four-column strip under the balance bar. The two 24-hour readings this
    /// window already published are kept, because the column is a window rather
    /// than a lifetime; everything else in it is the all-time reading, and the two
    /// are labelled so they cannot be confused.
    /// </summary>
    /// <summary>
    /// The width one readout column needs before its own label and figure stop fitting.
    /// 195 is measured rather than guessed: the widest pair in the four is
    /// 「累计输入/输出」 beside a figure like <c>12.34M / 5.6M</c>, which is about 190 px
    /// at the reading face. Below it the four become two by two; at or above it, which
    /// is what the user's own two window sizes give, they stay in one row.
    /// </summary>
    private const double MetricColumnMin = 195;

    /// <summary>The gap between two readout columns. Between, not around: the last
    /// column of a row has nothing to its right to be clear of.</summary>
    private const double MetricGutter = 22;

    private void OnMetricStripSizeChanged(object sender, SizeChangedEventArgs e) => ReflowMetricStrip(e.NewSize.Width);

    /// <summary>
    /// Four readouts across, or two by two.
    ///
    /// <para>
    /// The four were one row of four equal quarters, so at a 980 px window each was
    /// 146 px — the number the user measured: 「现在在窄窗只有 ~146px」. At 146 px
    /// 「提供方 / 模型」 is cut and 「累计输入/输出」 ellipsises to a dash, and the four
    /// figures a person compares stop being comparable. Reflow is the fix rather than a
    /// smaller font or a scrollbar: two by two keeps every label whole, keeps each label
    /// with its own figure, and reads in the same order. Above the threshold the
    /// four-across arrangement is untouched, which is why the threshold is a column
    /// width rather than a window width.
    /// </para>
    ///
    /// <para>
    /// The panels are moved rather than rebuilt, so nothing on them is re-created by a
    /// resize: their readings keep their own styles, their own tooltips and whatever
    /// motion is running on them. The arrangement is remembered, because a reflow that
    /// ran on every size change would fight the layout it just wrote.
    /// </para>
    /// </summary>
    private void ReflowMetricStrip(double available)
    {
        var columns = available >= 4 * MetricColumnMin + 3 * MetricGutter ? 4 : 2;
        if (columns == _metricColumns) return;
        _metricColumns = columns;
        var rows = 4 / columns;

        MetricStrip.ColumnDefinitions.Clear();
        MetricStrip.RowDefinitions.Clear();
        for (var index = 0; index < columns; index++)
            MetricStrip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < rows; index++)
            MetricStrip.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var panels = new[] { MetricPanelTokens, MetricPanelRate, MetricPanelCache, MetricPanelSite };
        for (var index = 0; index < panels.Length; index++)
        {
            var column = index % columns;
            var row = index / columns;
            Grid.SetColumn(panels[index], column);
            Grid.SetRow(panels[index], row);
            panels[index].Margin = new Thickness(
                0, 0,
                column == columns - 1 ? 0 : MetricGutter,
                row < rows - 1 ? 18 : 0);
        }
    }

    private void UpdateMetricStrip(IReadOnlyList<UsageEvent> events)
    {
        var report = _lastReport!;
        var day = report.TwentyFourHours;
        var all = report.AllTime;

        TwentyFourHourTokens.Text = day.HasTokenData
            ? $"{UsageFormatting.Tokens(day.InputTokens)} / {UsageFormatting.Tokens(day.OutputTokens)}"
            : "未上报";
        MarkStale(TwentyFourHourTokens, !day.HasTokenData);

        Requests.Text = $"{day.Requests:N0}";
        SuccessRate.Text = day.Requests > 0 ? $"{day.SuccessPercent:0.#}%" : "—";
        Throughput.Text = UsageFormatting.Rate(all.OutputPerSecond);
        MarkStale(Throughput, all.OutputPerSecond is null);

        FirstTokenValue.Text = all.AverageFirstTokenMs is { } first ? UsageFormatting.Milliseconds(first) : "未上报";
        MarkStale(FirstTokenValue, all.AverageFirstTokenMs is null);
        AverageDuration.Text = UsageFormatting.Milliseconds(all.AverageDurationMs);
        MarkStale(AverageDuration, all.AverageDurationMs is null);
        var providers = events.Select(value => value.Provider).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var models = events.Select(value => value.Model).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        ProviderModelCount.Text = $"{providers} / {models}";
        AllTimeTokens.Text = all.HasTokenData ? UsageFormatting.Tokens(all.TotalTokens) : "未上报";
        MarkStale(AllTimeTokens, !all.HasTokenData);
        RangeCaption.Text = AllTimeRangeCaption(events);

        // 0% and "not reported" are two different readings, and this is the one
        // place they were being printed as one: with an input figure the ratio is
        // real even when the cache served nothing, and without one there is no ratio
        // to print at all.
        var hit = all.InputTokens > 0 ? Reading.Reported(Math.Clamp(all.CacheHitPercent, 0, 100)) : Reading.Missing();
        CacheHit.Text = hit.Value is { } percent ? $"{percent:0.#}%" : "未上报";
        CacheHit.Foreground = (Brush)FindResource(hit.Value is null ? "StaleTintBrush" : "FlowRateBrush");
        CacheWriteValue.Text = all.CacheWriteTokens > 0 ? UsageFormatting.Tokens(all.CacheWriteTokens) : all.Requests > 0 ? "0" : "未上报";
        MarkStale(CacheWriteValue, all.Requests == 0);
        AllTimeTokenBreakdown.Text = all.HasTokenData
            ? $"{UsageFormatting.Tokens(all.InputTokens)} / {UsageFormatting.Tokens(all.OutputTokens)}"
            : "未上报 / 未上报";
        MarkStale(AllTimeTokenBreakdown, !all.HasTokenData);

        var server = _lastServerUsage;
        ServerUsageSummary.Text = server.HasData ? Money(server.TodayAmount) : "未上报";
        ServerUsageSummary.Foreground = (Brush)FindResource(server.HasData ? "TextBrush" : "StaleTintBrush");
        ServerUsageDate.Text = server.HasData ? server.Date : "—";
        ServerUsageSource.Text = server.HasData ? "/v1/usage" : "—";
        ServerUsageSummaryHint.Text = server.HasData ? server.Currency : "当前站点尚无今日用量";
    }

    /// <summary>The span the all-time column actually covers, so "累计" has a date on it.</summary>
    private static string AllTimeRangeCaption(IReadOnlyList<UsageEvent> events)
    {
        if (events.Count == 0) return "暂无事件";
        var first = events.Min(value => value.OccurredAt).ToLocalTime();
        var last = events.Max(value => value.OccurredAt).ToLocalTime();
        return $"{first:MM-dd} → {last:MM-dd}";
    }

    private void UpdateModelTable(IReadOnlyList<UsageEvent> events)
    {
        var values = events.Where(value => !string.IsNullOrWhiteSpace(value.Model))
            .GroupBy(value => value.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Name: group.Key, Events: group.ToArray()))
            .OrderByDescending(value => value.Events.Sum(item => (item.InputTokens ?? 0) + (item.OutputTokens ?? 0)))
            .ThenByDescending(value => value.Events.Length)
            .Take(12)
            .ToArray();

        // The share is of cost when anything reported a cost, because that is the
        // denominator a person compares models on once token counts differ between
        // them; otherwise it falls back to tokens, then to requests. The cost basis is
        // only taken when the whole scope is in one unit: a basis that added CNY to USD
        // would be a denominator no reader could check, and the token basis is the
        // honest fallback for a scope that mixes them.
        var scopeCurrency = SingleCurrency(events, ScopedAccountCurrency());
        var costBasis = scopeCurrency.Length > 0 ? values.Sum(value => value.Events.Sum(item => item.Cost ?? 0)) : 0;
        var tokenBasis = values.Sum(value => value.Events.Sum(item => (item.InputTokens ?? 0) + (item.OutputTokens ?? 0)));
        var requestBasis = values.Sum(value => value.Events.Length);
        var basis = costBasis > 0 ? costBasis : tokenBasis > 0 ? (double)tokenBasis : requestBasis;

        ModelItems.ItemsSource = values.Select((value, index) =>
        {
            var cost = value.Events.Sum(item => item.Cost ?? 0);
            var hasCost = value.Events.Any(item => item.Cost.HasValue);
            // This row's own unit, which is the unit its own cell prints: one model can
            // be reached through two accounts, and the two need not be billed alike.
            var modelCurrency = SingleCurrency(value.Events, ScopedAccountCurrency());
            var input = value.Events.Sum(item => item.InputTokens ?? 0);
            var output = value.Events.Sum(item => item.OutputTokens ?? 0);
            var hasTokens = value.Events.Any(item => item.InputTokens.HasValue || item.OutputTokens.HasValue);
            var share = costBasis > 0 ? cost : tokenBasis > 0 ? input + output : (double)value.Events.Length;
            // A share of a basis this row has no reading for is not 0%. The column
            // said 0% for every model whose cost nobody reported, beside a 费用 cell
            // that said 未上报 — a row that was never measured, drawn as a row that
            // measured zero. On a cost basis the share is unknown for exactly the
            // rows whose cost is unknown.
            var shareKnown = costBasis > 0 ? hasCost : tokenBasis > 0 ? hasTokens : true;
            return new ModelRow(
                value.Name,
                value.Events.Length,
                hasTokens ? Reading.Reported(input) : Reading.Missing(),
                hasTokens ? Reading.Reported(output) : Reading.Missing(),
                hasCost && modelCurrency.Length > 0 ? Reading.Reported(cost) : Reading.Missing(),
                modelCurrency,
                hasCost,
                new SolidColorBrush(PaletteColor(index + 1)),
                shareKnown && basis > 0 ? Math.Clamp(share * 100d / basis, 0, 100) : 0,
                shareKnown);
        }).ToArray();
        ModelEmpty.Visibility = values.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModelScopeText.Text = values.Length == 0
            ? ""
            : $"占比按{(costBasis > 0 ? "费用" : tokenBasis > 0 ? "Token" : "请求")}计 · {values.Length:N0} 个模型";
    }

    private void UpdateProviderBreakdown(IReadOnlyList<UsageEvent> events)
    {
        var providerRows = events.GroupBy(value => string.IsNullOrWhiteSpace(value.Provider) ? "Unknown" : value.Provider, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Sum(value => (value.InputTokens ?? 0) + (value.OutputTokens ?? 0)))
            .ThenByDescending(group => group.Count())
            .Take(10)
            .Select((group, index) => new ProviderRow(group.Key, group.ToArray(), events.Count, Palette(index)))
            .ToArray();
        ProviderItems.ItemsSource = providerRows;
        ProviderSummary.Text = providerRows.Length == 0 ? "暂无请求" : $"{providerRows.Length} 个提供方 · 按 Token 排序";
    }

    /// <summary>
    /// The account table. One row per account the balance document names, with the
    /// billing mode the window routes its own ruler by — so the table and the bar
    /// above it can never disagree about which kind of account this is.
    /// </summary>
    private void UpdateAccountTable(IReadOnlyList<UsageEvent> events)
    {
        var subscription = SubscriptionAccounts(events);
        var stale = IsStale(_lastBalanceUsage.UpdatedAt);
        var rows = _lastBalanceUsage.Rows.Select(row =>
        {
            var declared = _accounts.TryGetValue(row.AccountId, out var info) && info.IsSubscription;
            return new AccountRow(
                AccountDirectory.Label(_accounts, row.AccountId),
                row.HasBalance ? Reading.Reported(row.Balance) : Reading.Missing(),
                row.HasToday ? Reading.Reported(row.Today) : Reading.Missing(),
                row.HasRecent ? Reading.Reported(row.Recent) : Reading.Missing(),
                row.Currency,
                declared || subscription.Contains(row.AccountId),
                stale,
                row.IsSelected);
        }).ToArray();
        AccountRowItems.ItemsSource = rows;
        AccountEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // The table lists every account whatever the window is scoped to, and it is
        // the per-account form the window-wide total was replaced by: each row's three
        // money figures are that account's own, and so is the unit in the 币种 column
        // beside them. That is why the caption describes the table rather than naming
        // the scope — the table is not scoped, and pretending otherwise would make a
        // reader look for this account's row among four.
        AccountScopeText.Text = rows.Length == 0 ? "" : $"{rows.Length:N0} 个账户 · 每行各自币种";
    }

    /// <summary>Events the history list should show under the current filter.</summary>
    /// <remarks>
    /// Two filters, and both of them are the left column's business: the client chip
    /// bar above the list, and the account list down the side. The account list is on
    /// screen on both pages, so an account picked in it answers the question the
    /// ledger is read with — "what did <i>this</i> account do" — and clicking the
    /// combined row widens the ledger back to every account. Before this the account
    /// column was the only place the account was ever named, and the one control that
    /// picks an account had no effect on the page whose whole subject is records.
    /// </remarks>
    private IReadOnlyList<UsageEvent> VisibleEvents(IReadOnlyList<UsageEvent> events)
    {
        var scoped = ScopeTo(events, _accountFilter);
        return string.IsNullOrWhiteSpace(_providerFilter)
            ? scoped
            : scoped.Where(value => string.Equals(value.Provider, _providerFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>
    /// The records one scope holds: every account, one account, or one of the two
    /// groups that are not an account at all.
    ///
    /// <para>
    /// A group is resolved here rather than at each of the callers, because the
    /// question is the same one everywhere — which records does this scope hold — and
    /// both groups answer it from one comparison of <c>account_id</c> with the account
    /// list the census makes. The archive's ceiling is applied here as well, so the
    /// chips, the summary and the ledger cannot list three different archive sizes.
    /// The events arrive newest first, so "the records the archive keeps" is the newest
    /// N without a second sort.
    /// </para>
    /// </summary>
    private IReadOnlyList<UsageEvent> ScopeTo(IReadOnlyList<UsageEvent> events, string scope)
    {
        switch (UsageGroups.GroupOf(scope))
        {
            case UsageAttribution.Unattributed:
                return events
                    .Where(value => UsageGroups.Of(value.AccountId, _accounts) == UsageAttribution.Unattributed)
                    .ToArray();
            case UsageAttribution.Archived:
                return events
                    .Where(value => UsageGroups.Of(value.AccountId, _accounts) == UsageAttribution.Archived)
                    .Take(_archive.Limit)
                    .ToArray();
            default:
                return scope.Length == 0
                    ? events
                    : events.Where(value => string.Equals(value.AccountId, scope, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
    }

    /// <summary>
    /// What the balance document should be read as for one scope. A group is not an
    /// account: no entry in that document is about it, and the id would filter every
    /// entry away and read as an install with no balances at all. Read whole instead,
    /// which is what the dashboard showed before a group could be picked.
    /// </summary>
    private static string BalanceScopeOf(string scope) => UsageGroups.IsGroupId(scope) ? "" : scope;

    /// <summary>
    /// The account or group the window is scoped to, or empty for the moment before
    /// the first refresh has resolved one. One name for the scope, read by the line
    /// under the ledger's title, the summary beside it and the account row's own
    /// highlight, so the three cannot call one scope three things.
    /// </summary>
    private string LedgerScopeLabel => _accountFilter.Length == 0
        ? ""
        : UsageGroups.GroupOf(_accountFilter) is { } group
            ? UsageGroups.ScopeLabel(_accountFilter)
            : AccountDirectory.Label(_accounts, _accountFilter);

    /// <summary>
    /// States the ledger's account scope under the page title.
    ///
    /// <para>
    /// It used to be a badge that appeared only for the two groups, with a hint in its
    /// place when no account was chosen. There is always a scope now — that is what
    /// removing 「全部账户」 means — so the line is always there, and the hint that told
    /// the reader to pick an account in the left column is gone with the state it
    /// described. The account row in the left column is a selection; this is the
    /// reading that follows from it, and the two are written from one
    /// <see cref="_accountFilter"/>, so the page cannot claim a scope the list is not
    /// drawn in.
    /// </para>
    /// </summary>
    private void UpdateLedgerScopeLabel()
    {
        var group = UsageGroups.GroupOf(_accountFilter);
        LedgerScopeText.Text = _accountFilter.Length == 0
            ? ""
            : group is null
                ? $"当前账户 · {LedgerScopeLabel} · 仅显示该账户的记录"
                : $"当前分组 · {LedgerScopeLabel} · {RecordCount(_accountFilter):N0} 条 · 仅显示该分组的记录";
        UpdateGroupNotice(group);
        // The census is scope-independent — it counts the whole log — so it is written
        // here, where both the full refresh and the filter-only re-render pass.
        CensusLine.Text = _tally?.CensusLine(_archive.Limit) ?? "";
    }

    /// <summary>
    /// The group notice: what one of the two groups is, in words a reader can act on.
    ///
    /// <para>
    /// Collapsed outside the two group scopes, because a strip that repeats "all
    /// accounts" is one more line of chrome over the list it describes. Inside them it
    /// carries the sentence the label alone cannot: 「未归属」 states a fact, and what a
    /// person does about it is add the client's account — so that is written here, with
    /// the clients that wrote those records named as the evidence. The archive's own
    /// ceiling is set in the same strip, because the control and the reading it changes
    /// belong together: 「当前上限 N 条」 is written by the sentence beside it.
    /// </para>
    /// </summary>
    private void UpdateGroupNotice(UsageAttribution? group)
    {
        if (group is not { } kind || _tally is not { } tally)
        {
            GroupNotice.Visibility = Visibility.Collapsed;
            ArchiveLimitPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var archive = kind == UsageAttribution.Archived;
        GroupNotice.Visibility = Visibility.Visible;
        ArchiveLimitPanel.Visibility = archive ? Visibility.Visible : Visibility.Collapsed;
        GroupBadge.Background = (Brush)FindResource(archive ? "PanelBorderStrongBrush" : "FlowCostBrush");
        GroupBadgeText.Text = archive ? UsageGroups.ArchivedNote : UsageGroups.UnattributedLabel;

        if (archive)
        {
            var removed = tally.ArchivedAccounts.Count == 0
                ? "没有任何已删除账户的记录。"
                : $"记录所属的 {tally.ArchivedAccounts.Count:N0} 个账户已从 BalancePet 移除："
                    + string.Join("、", tally.ArchivedAccounts.Select(entry =>
                        $"{AccountDirectory.Label(entry.AccountId)}（{entry.Count:N0} 条）"))
                    + "。";
            GroupNoticeLine.Text = $"{removed}归档记录不计入任何账户，但仍计入事件总数。";
            GroupAdviceLine.Text = tally.ArchivedOverflow == 0
                ? $"当前上限 {_archive.Limit:N0} 条 · {tally.ArchivedTotal:N0} 条归档记录全部保留。"
                : $"当前上限 {_archive.Limit:N0} 条 · 保留最近 {tally.Archived:N0} 条，"
                    + $"超出上限的 {tally.ArchivedOverflow:N0} 条不再列出（仍计入事件总数）。";
            GroupAdviceLine.Foreground = (Brush)FindResource(tally.ArchivedOverflow == 0 ? "MutedBrush" : "FlowCostBrush");
            SyncArchiveLimitOptions();
            return;
        }

        GroupNoticeLine.Text = $"{tally.Unattributed:N0} 条记录没有归属到任何账户 —— "
            + "这些记录不计入任何账户，但仍计入事件总数。";
        var clients = UnattributedClients();
        GroupAdviceLine.Text = clients.Length == 0
            ? $"{UsageGroups.CollectAdvice}：在 BalancePet 里添加这些客户端所用的账户，之后的记录会自动归属。"
            : $"{UsageGroups.CollectAdvice}：这些记录来自 {clients}；在 BalancePet 里添加这些客户端所用的账户，新记录会自动归属。";
        GroupAdviceLine.Foreground = (Brush)FindResource("FlowCostBrush");
    }

    /// <summary>
    /// Which clients wrote the unattributed records, largest first. This is the one
    /// piece of evidence that says <i>which</i> account to go and enter, and it is read
    /// out of the log rather than from a client-to-account table, because the event log
    /// is the only thing this plugin is entitled to read and the host's binding rules
    /// are not part of it.
    /// </summary>
    private string UnattributedClients()
    {
        return string.Join("、", _lastEvents
            .Where(value => UsageGroups.Of(value.AccountId, _accounts) == UsageAttribution.Unattributed)
            .GroupBy(value => string.IsNullOrWhiteSpace(value.Provider) ? "Unknown" : value.Provider, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .Select(group => $"{group.Key} {group.Count():N0} 条"));
    }

    /// <summary>
    /// Fills the ceiling control and selects the stored value. The choices come from
    /// <see cref="ArchiveLimitStore"/> rather than from a second list here, so what the
    /// control offers and what the store accepts cannot drift apart — and a ceiling
    /// read out of a hand-edited file is offered as itself rather than silently
    /// replaced by the nearest listed step.
    /// </summary>
    private void SyncArchiveLimitOptions()
    {
        if (ArchiveLimitBox.Items.Count == 0)
        {
            foreach (var choice in ArchiveLimitStore.Choices.Append(_archive.Limit).Distinct().OrderBy(value => value))
                ArchiveLimitBox.Items.Add(new ComboBoxItem { Tag = choice, Content = $"{choice:N0} 条" });
        }
        _suppressArchiveLimit = true;
        try
        {
            foreach (ComboBoxItem item in ArchiveLimitBox.Items)
            {
                if (item.Tag is int value && value == _archive.Limit)
                {
                    ArchiveLimitBox.SelectedItem = item;
                    break;
                }
            }
        }
        finally { _suppressArchiveLimit = false; }
    }

    /// <summary>
    /// The user's new ceiling: written to the plugin's own file and then read back by
    /// a full refresh, so the number on screen is the number on disk rather than the
    /// one the click asked for.
    /// </summary>
    private void OnArchiveLimitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressArchiveLimit) return;
        if (ArchiveLimitBox.SelectedItem is not ComboBoxItem item || item.Tag is not int limit) return;
        if (!_archive.Set(limit)) return;
        Refresh();
    }

    /// <summary>
    /// What the ledger is listing, stated beside it: the scope, the client filter, how
    /// many of the scope's records are on screen and how many there are in all.
    ///
    /// <para>
    /// The total is the scope's own count from the census rather than the rows on
    /// screen, because this list is capped twice over: at
    /// <see cref="RecentLedgerLimit"/> rows like every view of the ledger, and for the
    /// archive at the ceiling the user set. A summary that read its total off the rows
    /// would state a cap as the size of a group — which is the one thing this window
    /// must not be quietly wrong about, since the census line under the list is the
    /// reader's check on exactly this number.
    /// </para>
    /// </summary>
    private string BuildRecentSummary(IReadOnlyList<UsageEvent> events, int visible)
    {
        var group = UsageGroups.GroupOf(_accountFilter);
        var scope = LedgerScopeLabel;
        var kind = group is null ? "（账户）" : "（分组）";
        if (events.Count == 0) return $"{scope}{kind} · 暂无记录";
        var shown = visible >= RecentLedgerLimit ? $"最近 {RecentLedgerLimit:N0} 条" : $"{visible:N0} 条";
        var provider = string.IsNullOrWhiteSpace(_providerFilter) ? "" : $"{_providerFilter} ";
        var ceiling = group == UsageAttribution.Archived && _tally is { ArchivedOverflow: > 0 } census
            ? $"（上限 {_archive.Limit:N0} 条 · 保留 {census.Archived:N0} 条 · 超出 {census.ArchivedOverflow:N0} 条）"
            : "";
        return $"{scope}{kind} · {provider}{shown} · 共 {RecordCount(_accountFilter):N0} 条{ceiling}";
    }

    /// <summary>
    /// Rebuilds the client chips: one for every provider that actually reported,
    /// plus an "all" chip. A filter whose provider no longer appears resets to
    /// "all" so the list can never be stuck empty with no way back.
    ///
    /// <para>
    /// Chips whose providers are all still present keep their elements and only
    /// have their count rewritten. A chip is the thing a filter change animates,
    /// so a rebuild would throw the animation away together with the element it
    /// was running on — and a chip that is destroyed and re-made every time a new
    /// event arrives cannot show that anything changed. The elements are replaced
    /// only when the set of providers itself changes, which is the one case where
    /// there is nothing to animate.
    /// </para>
    /// </summary>
    private void UpdateProviderFilters(IReadOnlyList<UsageEvent> events, bool chipsAlreadyAnimated = false)
    {
        var groups = events
            .Select(value => string.IsNullOrWhiteSpace(value.Provider) ? "Unknown" : value.Provider)
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (_providerFilter.Length > 0 && !groups.Any(group => string.Equals(group.Key, _providerFilter, StringComparison.OrdinalIgnoreCase)))
            _providerFilter = "";

        var signature = $"{_providerFilter}|{string.Join("|", groups.Select(group => $"{group.Key}:{group.Count()}"))}";
        if (signature == _providerFilterSignature) return;
        _providerFilterSignature = signature;

        var wanted = new List<(string Label, int Count, string Provider)> { ("全部", events.Count, "") };
        wanted.AddRange(groups.Select(group => (group.Key, group.Count(), group.Key)));

        var kept = ProviderFilterPanel.Children.OfType<Button>().ToArray();
        var sameShape = kept.Length == wanted.Count;
        for (var index = 0; sameShape && index < kept.Length; index++)
            sameShape = string.Equals(kept[index].Tag as string, wanted[index].Provider, StringComparison.OrdinalIgnoreCase);

        if (!sameShape)
        {
            ProviderFilterPanel.Children.Clear();
            foreach (var (label, count, provider) in wanted)
            {
                ProviderFilterPanel.Children.Add(BuildFilterChip(
                    label, count,
                    string.Equals(provider, _providerFilter, StringComparison.OrdinalIgnoreCase),
                    provider));
            }
            return;
        }

        for (var index = 0; index < kept.Length; index++)
        {
            SetFilterChipCount(kept[index], wanted[index].Count);
            // The click path has already animated every chip to its new state; a
            // second write of the same values would only cancel that animation.
            if (!chipsAlreadyAnimated)
            {
                ApplyFilterChipState(kept[index],
                    string.Equals(wanted[index].Provider, _providerFilter, StringComparison.OrdinalIgnoreCase),
                    animate: false);
            }
        }
    }

    /// <summary>The count badge inside a chip, and the chip that holds it.</summary>
    private static (Button Chip, Border Badge, TextBlock BadgeText) ChipParts(Button chip)
    {
        var content = (StackPanel)chip.Content;
        var badge = (Border)content.Children[1];
        return (chip, badge, (TextBlock)badge.Child);
    }

    private static void SetFilterChipCount(Button chip, int count)
    {
        var (_, _, badgeText) = ChipParts(chip);
        var text = count.ToString("N0");
        if (badgeText.Text != text) badgeText.Text = text;
    }

    private Button BuildFilterChip(string label, int count, bool active, string provider)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new Border
        {
            Margin = new Thickness(7, 0, 0, 0),
            Padding = new Thickness(6, 1, 6, 1),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = count.ToString("N0"),
                FontSize = 10
            }
        });

        var chip = new Button
        {
            Content = content,
            Tag = provider,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(13, 5, 13, 5),
            FontSize = 11.5,
            Cursor = Cursors.Hand,
            ToolTip = provider.Length == 0 ? "显示所有客户端的记录" : $"只显示 {provider} 的记录",
            BorderThickness = new Thickness(1)
        };
        chip.Click += OnProviderFilterClick;
        // A brush of the chip's own, always: the resource brushes are frozen and a
        // frozen brush cannot be repainted by an animation.
        ApplyFilterChipState(chip, active, animate: false);
        return chip;
    }

    /// <summary>
    /// Writes one chip's four colours: the active chip is the raised, bordered one
    /// with the client's count in the accent badge, and every other chip is the
    /// quiet panel tone. <paramref name="animate"/> rolls them over 250 ms on the
    /// design's <c>PowerEase(EaseInOut, Power=2)</c> instead of snapping.
    /// </summary>
    private void ApplyFilterChipState(Button chip, bool active, bool animate)
    {
        var (_, badge, badgeText) = ChipParts(chip);
        var duration = ChromeTransition;
        Transition(chip, Control.BackgroundProperty,
            (SolidColorBrush)FindResource(active ? "PanelHoverBrush" : "PanelBrush"), animate, duration);
        Transition(chip, Control.ForegroundProperty,
            (SolidColorBrush)FindResource(active ? "FlowRateBrush" : "MutedBrush"), animate, duration);
        Transition(chip, Control.BorderBrushProperty,
            (SolidColorBrush)FindResource(active ? "PanelBorderStrongBrush" : "PanelBorderBrush"), animate, duration);
        Transition(badge, Border.BackgroundProperty,
            (SolidColorBrush)FindResource(active ? "QuotaFillBrush" : "PanelAltBrush"), animate, duration);
        Transition(badgeText, TextBlock.ForegroundProperty,
            active ? Brushes.White : (SolidColorBrush)FindResource("MutedBrush"), animate, duration);
    }

    private void OnProviderFilterClick(object sender, RoutedEventArgs e) => RunProviderFilterClick(sender, null);

    /// <summary>The click, with an end-of-work callback for the timing probe.</summary>
    private void RunProviderFilterClick(object sender, Action? completed)
    {
        if (sender is not Button button) return;
        var provider = button.Tag as string ?? "";
        if (string.Equals(provider, _providerFilter, StringComparison.Ordinal)) return;
        _providerFilter = provider;
        // Ported from the copy of this window that was left behind at
        // extensions\BalancePet-Ext-Feature-UsageAnalytics: the chips used to snap
        // to their new state in the same frame the list changed under them.
        AnimateProviderFilterChange(completed);
    }

    /// <summary>
    /// Rolls every chip to the state the new filter implies, then re-renders the
    /// list. One transition per chip, all started together: the chip that was
    /// active goes quiet while the one that was clicked is raised, so the bar reads
    /// as one movement rather than as two unrelated repaints.
    /// </summary>
    /// <param name="completed">
    /// Called when the list has been re-rendered, or null. A timing probe passes a
    /// callback because the click's work ends above this frame's return: the rows
    /// are handed to the list here, but the list lays them out on the render pass
    /// that follows.
    /// </param>
    private void AnimateProviderFilterChange(Action? completed = null)
    {
        foreach (Button chip in ProviderFilterPanel.Children)
        {
            var provider = chip.Tag as string ?? "";
            var isActive = string.Equals(provider, _providerFilter, StringComparison.Ordinal);
            ApplyFilterChipState(chip, isActive, animate: true);
        }

        // Apply the filter after the animation starts. The chips are left in place
        // by UpdateProviderFilters, so the transition above is the one that is seen.
        ApplyHistoryFilter(animatedChips: true, completed: completed);
    }

    /// <summary>
    /// Paints one property from wherever it currently is to a named brush over the
    /// design's chrome transition. The current colour is read off the element rather
    /// than tracked here, because a transition that interrupts another one has to
    /// start from the colour on screen — reading the property is what makes the
    /// second click of a double-click continue from the first rather than jump.
    /// </summary>
    private static void Transition(
        DependencyObject target, DependencyProperty property, SolidColorBrush to, bool animate, TimeSpan duration)
    {
        var current = (target.GetValue(property) as SolidColorBrush)?.Color ?? Colors.Transparent;
        var brush = new SolidColorBrush(current);
        target.SetValue(property, brush);

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            brush.Color = to.Color;
            return;
        }

        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            From = current,
            To = to.Color,
            Duration = new Duration(duration),
            EasingFunction = new PowerEase { EasingMode = EasingMode.EaseInOut, Power = 2 }
        });
    }

    /// <summary>
    /// Re-renders only the history list after a client filter change. A full
    /// refresh would re-read every file, rebuild the report and redraw the
    /// dashboard charts, none of which the filter affects.
    /// </summary>
    /// <param name="animatedChips">
    /// True when the caller has already rolled the chips to the new filter, so the
    /// chip pass must not write those colours a second time and cancel the roll.
    /// </param>
    private void ApplyHistoryFilter(bool animatedChips = false, Action? completed = null)
    {
        // The chip counts and the list are two readings of one set of events, so the
        // same scoped set is handed to both. Counting the unfiltered log here would
        // put 971 over a list holding ten the moment an account is picked.
        var scoped = VisibleEvents(_lastEvents);
        UpdateProviderFilters(scoped, chipsAlreadyAnimated: animatedChips);
        var rows = scoped.Take(RecentLedgerLimit).Select(value => new EventRow(value, _accounts, AccountCurrency(value.AccountId))).ToArray();
        // Where the click path hands the list its rows. Everything below is the
        // layout's own work; everything above is the filter's.
        _probePanelsMs = _probeWatch?.Elapsed.TotalMilliseconds ?? 0;
        _probeRebuiltDetails = rows.Sum(row => row.Details.Count);
        EventsList.ItemsSource = rows;
        RebuildLedger();
        _probeRowsMs = _probeWatch?.Elapsed.TotalMilliseconds ?? 0;
        RecentSummary.Text = BuildRecentSummary(scoped, rows.Length);
        UpdateLedgerScopeLabel();
        if (_selectedEvent is not null
            && !rows.Any(row => string.Equals(row.EventId, _selectedEvent.EventId, StringComparison.OrdinalIgnoreCase)))
        {
            HideEventDetail(immediate: true);
        }
        completed?.Invoke();
    }

    /// <summary>
    /// Fills the account selector — which is the window's scope — from the snapshot
    /// and the host's account names.
    ///
    /// <para>
    /// There is no combined entry, and therefore no "every account" scope. The user's
    /// reason is the whole of it: 「去掉全部账户（没有意义，毕竟每个账户的额度单位，
    /// token效率都不一致）」. An account billed in CNY and an account billed in USD have
    /// no total between them, and a token count summed across two clients with
    /// different cache behaviour describes neither of them. So the list is the
    /// accounts first and the two groups under them, and one of them is always
    /// selected — a window that cannot say which account it is showing cannot be read.
    /// </para>
    ///
    /// <para>
    /// The two groups are options here as well as rows in the panel, and there is no
    /// guard here any more that resets a scope this list does not name: that decision
    /// belongs to <see cref="ResolveScope"/>, which runs before anything is drawn and
    /// resolves against the same set of ids this list is built from. Two guards would
    /// be two answers to one question, and the panel would light a row the page had
    /// already left.
    /// </para>
    /// </summary>
    private void UpdateAccountSelector(BalanceUsageSnapshot snapshot)
    {
        var options = new List<KeyValuePair<string, string>>();
        foreach (var id in AccountIds(snapshot)) options.Add(new(id, AccountDirectory.Label(_accounts, id)));
        options.Add(new(UsageGroups.UnattributedId, UsageGroups.UnattributedLabel));
        options.Add(new(UsageGroups.ArchivedId, UsageGroups.ArchivedLabel));

        var signature = string.Join("|", options.Select(option => $"{option.Key}={option.Value}"));
        if (signature == _accountSelectorSignature) return;
        _accountSelectorSignature = signature;

        _suppressFilterRefresh = true;
        try
        {
            AccountSelector.Items.Clear();
            foreach (var option in options)
            {
                var item = new ComboBoxItem { Tag = option.Key, Content = option.Value };
                AccountSelector.Items.Add(item);
                if (string.Equals(option.Key, _accountFilter, StringComparison.OrdinalIgnoreCase)) AccountSelector.SelectedItem = item;
            }
            if (AccountSelector.SelectedItem is null && AccountSelector.Items.Count > 0) AccountSelector.SelectedIndex = 0;
        }
        finally { _suppressFilterRefresh = false; }
        BuildAccountListPanel(snapshot);
    }

    private void OnAccountSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterRefresh) return;
        var selected = (AccountSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        if (string.Equals(selected, _accountFilter, StringComparison.Ordinal)) return;
        _accountFilter = selected;
        ApplyAccountSelection(animate: true);
        Refresh();
    }
    // ======================================================================
    //  account list panel — the 250 px resource list on the left
    // ======================================================================

    /// <summary>
    /// Builds the visual account list from the options already in AccountSelector.
    /// Called at the end of UpdateAccountSelector so both representations stay in sync.
    ///
    /// <para>
    /// Every row is a real account or one of the two groups. 「全部账户」 is gone: it
    /// was not an account, it was the absence of one, and every figure on the row
    /// under it was a sum across units that cannot be added — see
    /// <see cref="UpdateAccountSelector"/>. The two groups stay, because they are not
    /// aggregates of accounts either: their records belong to no account at all.
    /// </para>
    ///
    /// <para>
    /// The rows are reused while the same accounts are listed, and only their
    /// readings are written again. A rebuild replaces the element a selection
    /// transition is running on, and a row that has been destroyed and re-made
    /// cannot show that anything about it changed — so the list is re-made only
    /// when the set of accounts itself changes.
    /// </para>
    /// </summary>
    private void BuildAccountListPanel(BalanceUsageSnapshot snapshot)
    {
        var options = new List<(string Id, string Label)>();
        foreach (var id in AccountIds(snapshot))
            options.Add((id, AccountDirectory.Label(_accounts, id)));
        // The two groups that are not accounts come last and in this order, so the
        // rows above them are exactly the accounts: the list reads accounts first and
        // the two exceptions to "everything here is an account" underneath, in the
        // order a reader asks about them — whose records are these, and whose were
        // these. They are rows rather than a separate control because clicking one has
        // to scope the ledger exactly as clicking an account does, and a control that
        // did its own scoping would be the second way to pick a scope in one window.
        options.Add((UsageGroups.UnattributedId, UsageGroups.UnattributedLabel));
        options.Add((UsageGroups.ArchivedId, UsageGroups.ArchivedLabel));

        var now = DateTimeOffset.Now;
        var wanted = options
            .Select(option => DescribeAccountRow(option.Id, option.Label, snapshot, now))
            .ToArray();

        if (!_accountRowOrder.SequenceEqual(wanted.Select(row => row.Id), StringComparer.OrdinalIgnoreCase))
        {
            _accountRows.Clear();
            _accountRowOrder.Clear();
            AccountListPanel.Children.Clear();
            foreach (var data in wanted)
            {
                var row = BuildAccountRow(data);
                _accountRows[data.Id] = row;
                _accountRowOrder.Add(data.Id);
                AccountListPanel.Children.Add(row.Outer);
            }
        }
        else
        {
            foreach (var data in wanted) ApplyAccountRow(_accountRows[data.Id], data);
        }

        ApplyAccountSelection(animate: false);
    }

    /// <summary>
    /// One row's readings, gathered from the snapshot, the account's own plan, the
    /// plugin's balance record and the event log. Pure data: what a row draws, decided
    /// here rather than while the tree is being built.
    /// </summary>
    private AccountRowData DescribeAccountRow(
        string id, string label, BalanceUsageSnapshot snapshot, DateTimeOffset now)
    {
        // A group first, because it is the one row with nothing to read out of the
        // snapshot: no account of that name exists, so no balance, no day and no plan
        // do either. Its readings are the census's, and they are the whole row. It has
        // no currency either — a group is not a bill.
        if (UsageGroups.GroupOf(id) is { } group)
            return new AccountRowData(id, label, false,
                0, false, 0, false, null, null, "", RecordCount(id), group);

        // One account's own three figures, and its own unit. There is no combined row
        // left to sum: a row that added every account's day together was adding CNY to
        // USD, and no window-wide currency is invented here to make that addable.
        double today = 0, balance = 0;
        bool hasToday = false, hasBalance = false;
        var currency = "";
        AccountDirectory.AccountInfo? info = null;
        _accounts.TryGetValue(id, out info);
        var row = snapshot.Rows.FirstOrDefault(r =>
            string.Equals(r.AccountId, id, StringComparison.OrdinalIgnoreCase));
        if (row != null)
        {
            today      = row.Today;   hasToday   = row.HasToday;
            balance    = row.Balance; hasBalance = row.HasBalance;
            currency   = row.Currency;
        }

        var subscription = info is { IsSubscription: true };

        // A subscription states the share of its allowance that is gone. The reading is
        // the one the balance ruler's card is drawn from — the same settings, the same
        // events, one resolution — so the row and the card cannot state two different
        // numbers for one account: before this, the row read the deprecated
        // quota_limit and summed the event log itself while the card read
        // quota_windows, and an account whose settings declare only windows showed a
        // percentage there and a dash here. See QuotaReading.
        var quota = subscription ? QuotaReading.Resolve(id, info, _lastEvents, now) : null;

        // A pay-as-you-go account states what is left of its last recharge, measured
        // against the balance that recharge left behind — the ceiling this plugin
        // recorded for itself, because the host's ledger cannot show money arriving.
        // The combined row gets nothing here: a recharge is one account's, and adding
        // balances up across accounts of different currencies would compare them.
        var recharge = !subscription && id.Length > 0 && hasBalance && balance > 0
            ? _balances.Gauge(id, balance, currency)
            : null;

        return new AccountRowData(id, label, subscription,
            balance, hasBalance, today, hasToday, quota, recharge, currency, RecordCount(id),
            UsageAttribution.Account);
    }

    /// <summary>
    /// Every account the window can be scoped to: the ones the balance document names,
    /// in its own order, and then any the user has configured that the document has no
    /// entry for.
    ///
    /// <para>
    /// The host's own list is what says an account exists; the document is what says it
    /// has a balance. An account with records and no balance is still an account whose
    /// records have to be reachable — under the old combined row it did not matter,
    /// because everything was in one list, and now it does.
    /// </para>
    /// </summary>
    private IReadOnlyList<string> AccountIds(BalanceUsageSnapshot snapshot)
    {
        var ids = new List<string>(snapshot.Accounts);
        foreach (var id in _accounts.Keys.OrderBy(value => AccountDirectory.Label(_accounts, value), StringComparer.CurrentCulture))
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
        return ids;
    }

    /// <summary>
    /// The scope this pass runs in.
    ///
    /// <para>
    /// A scope already in the list is kept; the balance document's own
    /// <c>selected_account_id</c> is the default when it names an account that is still
    /// listed, because that is the host's choice of account rather than one invented
    /// here; and the first row is the default otherwise. It can only return an empty
    /// string when there is no account and no group at all, which the two groups make
    /// impossible — and the caller treats empty as "all accounts", which is the state
    /// this change removes from the panel rather than from the arithmetic.
    /// </para>
    /// </summary>
    private string ResolveScope(BalanceUsageSnapshot snapshot)
    {
        if (UsageGroups.IsGroupId(_accountFilter)) return _accountFilter;
        var ids = AccountIds(snapshot);
        if (_accountFilter.Length > 0 && ids.Contains(_accountFilter, StringComparer.OrdinalIgnoreCase)) return _accountFilter;
        var selected = snapshot.Rows.FirstOrDefault(row => row.IsSelected);
        if (selected is not null && ids.Contains(selected.AccountId, StringComparer.OrdinalIgnoreCase)) return selected.AccountId;
        return ids.Count > 0 ? ids[0] : UsageGroups.UnattributedId;
    }

    /// <summary>
    /// Which account the site's own reading belongs to. A group is not an account, so
    /// it names an id no entry can carry and the reading comes back empty: 「站点今日」
    /// is one account's relay figure, and a group has no relay figure to state. The
    /// alternative — passing the group's id through — would show the last account the
    /// host happened to select under a heading about a group.
    /// </summary>
    private static string AccountScopeOf(string scope)
        => scope.Length == 0 || UsageGroups.IsGroupId(scope) ? "\u0000none" : scope;

    /// <summary>
    /// How many records the log holds for one scope — an account, the combined view,
    /// or one of the two groups. Counted from the log the ledger itself is drawn from
    /// rather than from the snapshot: the snapshot counts days with a reading, and the
    /// row's question is how much of this account is in the list next to it.
    ///
    /// <para>
    /// A group's count is the census's, not the list's: the archive lists only the
    /// newest records inside its ceiling, and the group it names is every archived
    /// record there is. The two are stated side by side — 「514 条」 in the row,
    /// 「保留 200 条」 in the notice — so the ceiling is visible as a ceiling rather
    /// than mistaken for the size of the group.
    /// </para>
    /// </summary>
    private int RecordCount(string id)
    {
        if (id.Length == 0) return _lastEvents.Count;
        if (_tally is { } tally)
        {
            var grouped = tally.CountFor(id);
            if (grouped >= 0) return grouped;
        }
        var count = 0;
        foreach (var value in _lastEvents)
            if (string.Equals(value.AccountId, id, StringComparison.OrdinalIgnoreCase)) count++;
        return count;
    }

    /// <summary>
    /// Moves every row to the account the filter currently names.
    ///
    /// <para>
    /// This is what makes the highlight follow the account. The selection is not
    /// part of the set of accounts, so a panel that was rebuilt only when that set
    /// changed painted the highlight once — on the first refresh, when the filter
    /// is empty — and never moved it again: clicking an account rescoped the whole
    /// page while 「全部账户」 stayed lit. The selection is applied on its own, both
    /// when the rows are built and every time the filter moves.
    /// </para>
    /// </summary>
    private void ApplyAccountSelection(bool animate)
    {
        foreach (var id in _accountRowOrder)
        {
            var row = _accountRows[id];
            row.Selected = string.Equals(id, _accountFilter, StringComparison.OrdinalIgnoreCase);
            PaintAccountRow(row, animate);
        }
    }

    /// <summary>
    /// Writes the four things that say which row is the current account: the fill
    /// behind it, the 3 px marker on its left edge, the weight of its name and the
    /// colour of that name. Each is a roll rather than a snap when
    /// <paramref name="animate"/> is set, which is what lets the highlight be seen
    /// to move from one row to the next instead of blinking there.
    /// </summary>
    private void PaintAccountRow(AccountListItem row, bool animate)
    {
        var palette = AccountColors;
        row.Name.FontWeight = row.Selected ? FontWeights.SemiBold : FontWeights.Normal;
        Transition(row.Fill, Border.BackgroundProperty, new SolidColorBrush(
                row.Selected ? palette.Selected : row.Hovered ? palette.Hover : Colors.Transparent),
            animate, ChromeTransition);
        Transition(row.Accent, Border.BackgroundProperty,
            new SolidColorBrush(row.Selected ? palette.Accent : Colors.Transparent), animate, ChromeTransition);
        Transition(row.Name, TextBlock.ForegroundProperty,
            new SolidColorBrush(row.Selected ? palette.Text : palette.Muted), animate, ChromeTransition);
    }

    /// <summary>
    /// The colours an account row draws with: the window's own type and marker
    /// tones plus the sidebar's two row fills, resolved once. The marker is
    /// <c>RowAccentBrush</c> rather than the cost colour it shares a value with:
    /// this bar says "this is the account in scope", not "this cost is high".
    /// </summary>
    private AccountListPalette AccountColors => _accountPalette ??= new AccountListPalette(
        ((SolidColorBrush)FindResource("TextBrush")).Color,
        ((SolidColorBrush)FindResource("MutedBrush")).Color,
        ((SolidColorBrush)FindResource("RowAccentBrush")).Color,
        SelectedRowFill,
        HoveredRowFill);

    /// <summary>
    /// One row of the 250 px account list: the name and its reading on one line, a
    /// 3 px bar under them, the row's own footnote, and the two layers that draw
    /// which row is current. Built once; every later refresh writes into it.
    /// </summary>
    private AccountListItem BuildAccountRow(AccountRowData data)
    {
        var row = new AccountListItem { Id = data.Id };

        // accent bar — 3 px stripe on the left edge when selected
        row.Accent = new Border
        {
            Width = 3,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        row.Name = new TextBlock
        {
            Text = data.Label,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = data.Label,
        };

        row.Value = new TextBlock
        {
            FontSize = 10.5,
            FontFamily = (FontFamily)FindResource("DigitFontFamily"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var topRow = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        topRow.Children.Add(row.Name);
        topRow.Children.Add(row.Value);

        // bar: subscription → mint quota fill, pay-as-you-go → amber spend fraction
        var barOuter = new Grid { Height = 3, Margin = new Thickness(0, 0, 0, 5) };
        barOuter.Children.Add(new Rectangle
        {
            Fill = (Brush)FindResource("QuotaTrackBrush"),
            RadiusX = 1.5, RadiusY = 1.5,
        });
        row.BarScale = new ScaleTransform { ScaleX = 0 };
        var fillGrid = new Grid
        {
            ClipToBounds = true,
            RenderTransform = row.BarScale,
            RenderTransformOrigin = new Point(0, 0),
        };
        row.BarFill = new Rectangle { RadiusX = 1.5, RadiusY = 1.5 };
        fillGrid.Children.Add(row.BarFill);
        barOuter.Children.Add(fillGrid);

        row.Bottom = new TextBlock { FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis };

        var content = new StackPanel { Margin = new Thickness(14, 10, 10, 10) };
        content.Children.Add(topRow);
        content.Children.Add(barOuter);
        content.Children.Add(row.Bottom);

        row.Fill = new Border();
        var host = new Grid();
        host.Children.Add(row.Fill);
        host.Children.Add(content);
        host.Children.Add(row.Accent);

        row.Outer = new Border
        {
            Tag = data.Id,
            Cursor = Cursors.Hand,
            BorderBrush = (Brush)FindResource("HairGridBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = host,
        };
        row.Outer.MouseLeftButtonUp += OnAccountListItemClick;
        // Hover is a state read at the time of the event, not a colour captured
        // when the row was built: the old handlers closed over a `selected` that
        // the build had computed, so a row that became the selected one still
        // cleared its own highlight on the way out of the pointer.
        row.Outer.MouseEnter += (_, _) =>
        {
            row.Hovered = true;
            PaintAccountRow(row, animate: true);
        };
        row.Outer.MouseLeave += (_, _) =>
        {
            row.Hovered = false;
            PaintAccountRow(row, animate: true);
        };

        ApplyAccountRow(row, data);
        return row;
    }

    /// <summary>Writes one row's readings onto the elements it already has.</summary>
    private void ApplyAccountRow(AccountListItem row, AccountRowData data)
    {
        row.Name.Text = data.Label;
        row.Name.ToolTip = data.Label;

        // The two groups first, because they are the rows with no account behind them:
        // no balance, no plan, no day and no bar. The bar especially — in this list it
        // means "how much of a ceiling is gone" on a subscription row and "how much of
        // the last recharge is left" on a pay-as-you-go one, and a third meaning drawn
        // in the same 3 px on a row that is not an account is how a reader comes to
        // think the archive has a balance to state. What the row states instead is what
        // it is: how many records are in it, and the one sentence a person can act on.
        if (data.Attribution != UsageAttribution.Account)
        {
            var archived = data.Attribution == UsageAttribution.Archived;
            WriteGroupReading(row, data.RecordCount, archived);
            row.Value.Foreground = (Brush)FindResource(archived ? "MutedBrush" : "FlowCostBrush");
            row.BarFill.Fill = Brushes.Transparent;
            if (row.Built) { if (row.Fraction > 0.0005) AnimateQuotaBar(row.BarScale, 0); }
            else row.BarScale.ScaleX = 0;
            row.Fraction = 0;
            row.Built = true;
            row.Bottom.Text = archived ? UsageGroups.ArchivedNote : UsageGroups.CollectAdvice;
            row.Bottom.FontFamily = (FontFamily)FindResource("UiFontFamily");
            row.Bottom.Foreground = (Brush)FindResource(archived ? "MutedBrush" : "FlowCostBrush");
            // A hairline above the group rows sets them off from the accounts above
            // them, which is the one thing their position in the list cannot say.
            row.Outer.BorderThickness = new Thickness(0, 1, 0, 1);
            PaintAccountRow(row, animate: false);
            return;
        }
        row.Outer.BorderThickness = new Thickness(0, 0, 0, 1);

        // The row's own reading, straight off the record the card's primary bar is
        // drawn from: the percentage here and the percentage there are one number, and
        // the line below names the same window with the same used / ceiling pair. With
        // no ceiling reported the row states the used side and nothing else — never 0%,
        // because a share of a denominator nobody reported is not a reading.
        var quota = data.Quota;
        if (data.Subscription)
        {
            row.Value.Text = quota is null ? "—"
                : quota.HasLimit ? quota.PercentText
                : quota.Used > 0 ? quota.UsedText(data.Currency)
                : "—";
            row.Value.Foreground = (Brush)FindResource("FlowRateBrush");
        }
        else
        {
            // Pay-as-you-go: show remaining balance, in this account's own unit. A
            // hard-coded "$" was a claim about the account rather than a format —
            // 「国内模型都是以人民币为单位，国外模型有基本都是美元单位」 — and the unit is
            // read off the same document row the figure is. No balance means no unit
            // either, which is the dash.
            row.Value.Text = data.HasBalance ? $"{data.Balance:0.00} {data.Currency}".TrimEnd() : "—";
            row.Value.Foreground = (Brush)FindResource(data.HasBalance && !IsStale(_lastBalanceUsage.UpdatedAt)
                ? "TextBrush" : "StaleTintBrush");
        }

        // The bar under the name. A subscription states the share of its allowance
        // that is gone. A pay-as-you-go row states what is left of its last recharge —
        // a gauge, not a share of spending: the denominator is the balance that
        // recharge left behind, so the bar shortens as the money is used. It used to be
        // drawn against the busiest row's spending today, which was a proportion nobody
        // asked for and said nothing about this account's own money. It keeps the
        // neutral tone: two marks of one hue in one list is how a spend bar on one row
        // came to be read as a second selection, when the selected row wore the amber
        // stripe.
        //
        // The combined row gets no bar at all. It is the sum of every account's
        // balance, and a recharge belongs to one account; adding ceilings up across
        // accounts of different currencies would compare figures that are not the same
        // kind of number.
        double fraction;
        Brush barFill;
        if (data.Subscription)
        {
            // The record's own fraction, which is the number the card's bar is scaled
            // by. The list draws it in one mint tone rather than in the card's
            // three-step urgency palette: a full-width amber line under a row means
            // "this is the account in scope" here, and one hue with two meanings in one
            // list is how the second highlight was read as a selection.
            fraction = quota?.Fraction ?? 0;
            barFill = (Brush)FindResource("FlowRateBrush");
        }
        else
        {
            fraction = data.Recharge?.Fraction ?? 0;
            barFill = (Brush)FindResource("MutedBrush");
        }
        row.BarFill.Fill = barFill;
        // A row that has just been built states its reading; a row that is already
        // on screen rolls to a new one. Animating a first value would be a
        // transition from a reading nobody ever saw.
        if (row.Built) { if (Math.Abs(fraction - row.Fraction) > 0.0005) AnimateQuotaBar(row.BarScale, fraction); }
        else row.BarScale.ScaleX = fraction;
        row.Fraction = fraction;
        row.Built = true;

        if (data.Subscription)
        {
            // The line under the bar says which allowance this row is stating and how
            // much of it is gone — the same window name and the same used / ceiling
            // pair the card's primary bar writes, character for character, because both
            // are that one record's own strings. Where nobody reported a ceiling it says
            // so instead of naming a window nothing is known about. The countdown is the
            // card's line, so here it is the row's tooltip: two sentences' worth of
            // window in one 250 px column is an ellipsis.
            row.Bottom.Text = quota is null
                ? "订阅"
                : quota.CeilingDeclared
                    ? $"{quota.DisplayName} · {quota.AmountText(data.Currency)}"
                    : QuotaReading.NoLimitText;
            row.Bottom.ToolTip = quota is null
                ? null
                : $"{row.Bottom.Text} · {quota.ResetText(DateTimeOffset.Now)}";
            row.Bottom.FontFamily = (FontFamily)FindResource("UiFontFamily");
            row.Bottom.Foreground = (Brush)FindResource("SubtleBrush");
        }
        else
        {
            // The line under a pay-as-you-go bar states the gauge in words and in the
            // two figures it is made of. With no recharge recorded it says so instead of
            // drawing a share of a ceiling nobody kept — and with no balance at all the
            // row keeps the reading it has always had, which is that there is none. The
            // combined row is not an account and has no recharge to report, so it keeps
            // its own line: its figure is the day's spending for every account at once.
            row.Bottom.Text = data.Recharge is { } recharge
                ? $"{recharge.SentenceText} · {recharge.PairText}"
                : data.Id.Length > 0 && data.HasBalance && data.Balance > 0
                    ? RechargeGauge.NoBaselineText
                    : data.HasToday ? $"今日 {data.Today:0.00} {data.Currency}".TrimEnd() : "今日 —";
            row.Bottom.ToolTip = data.Recharge is { } recorded
                ? $"{recorded.SentenceText} · {recorded.PairText}"
                    + (recorded.BaselineAt is { } at ? $" · 充值记录于 {at.ToLocalTime():MM-dd HH:mm}" : "")
                : null;
            row.Bottom.FontFamily = data.Recharge is not null || !data.HasToday
                ? (FontFamily)FindResource("UiFontFamily")
                : (FontFamily)FindResource("DigitFontFamily");
            row.Bottom.Foreground = (Brush)FindResource(data.Recharge is not null || data.HasToday ? "SubtleBrush" : "StaleTintBrush");
        }

        // How much of this account the ledger holds, on the row that scopes the
        // ledger to it. It is the one figure here that comes from the record list
        // rather than from the balance document, and it is what makes the click's
        // result predictable before the click: a row that said only "$1.25" gave no
        // sign whether picking it would leave ten records or none. Zero is stated
        // as zero — the reading is a count of something that was measured, not a
        // reading that is missing, and those are the same word on screen only if
        // they are the same word here; see Reading.
        var records = data.RecordCount switch
        {
            0 => "无记录",
            1 => "1 条记录",
            _ => $"{data.RecordCount:N0} 条记录"
        };
        row.Bottom.Text = $"{row.Bottom.Text} · {records}";
        row.Bottom.ToolTip = row.Bottom.ToolTip is { } tip ? $"{tip} · {records}" : records;

        PaintAccountRow(row, animate: false);
    }

    /// <summary>
    /// The one reading on a group row that moves while the window is open: how many
    /// records the group holds. Written from both the build and every later refresh, so
    /// a record arriving under a live window moves the row — the account panel itself
    /// is rebuilt only when the set of accounts changes, and a count frozen at the
    /// moment the row was made is how a group comes to disagree with the census line
    /// under the list it is drawn beside.
    /// </summary>
    private void WriteGroupReading(AccountListItem row, int count, bool archived)
    {
        row.Value.Text = $"{count:N0} 条";
        row.Value.ToolTip = $"{count:N0} 条记录";
        row.Bottom.ToolTip = archived
            ? $"{UsageGroups.ArchivedNote} · {count:N0} 条记录不计入任何账户，但仍计入事件总数"
            : $"{UsageGroups.CollectAdvice} · {count:N0} 条记录没有归属到任何账户，不计入任何账户，但仍计入事件总数";
    }

    /// <summary>
    /// Rewrites the two group rows' counts. Only their text: the row's colours, its
    /// selection and the sentence beside the count are the panel's business, and
    /// repainting them here on every pass is what would cancel a selection transition
    /// the click had just started.
    /// </summary>
    private void RefreshGroupRowCounts()
    {
        if (_tally is null) return;
        if (_accountRows.TryGetValue(UsageGroups.UnattributedId, out var unattributed))
            WriteGroupReading(unattributed, RecordCount(UsageGroups.UnattributedId), archived: false);
        if (_accountRows.TryGetValue(UsageGroups.ArchivedId, out var archived))
            WriteGroupReading(archived, RecordCount(UsageGroups.ArchivedId), archived: true);
    }

    private void OnAccountListItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string id)
            SelectAccountFilter(id);
    }

    private void SelectAccountFilter(string newId) => RunSelectAccountFilter(newId, null);

    /// <param name="completed">
    /// Called after the refresh has been scheduled and the panel has been handed its
    /// selection, or null. See <see cref="AnimateProviderFilterChange"/> for why a
    /// timing probe needs an end-of-work callback rather than a return value.
    /// </param>
    private void RunSelectAccountFilter(string newId, Action? completed)
    {
        if (string.Equals(newId, _accountFilter, StringComparison.Ordinal)) return;
        _accountFilter = newId;
        // Keep AccountSelector in sync so existing code that reads it stays valid.
        _suppressFilterRefresh = true;
        try
        {
            foreach (ComboBoxItem item in AccountSelector.Items)
            {
                if (string.Equals(item.Tag as string, newId, StringComparison.OrdinalIgnoreCase))
                {
                    AccountSelector.SelectedItem = item;
                    break;
                }
            }
        }
        finally { _suppressFilterRefresh = false; }
        // The highlight moves on its own transition, and before the refresh: that
        // may re-read every file and rebuild half the window, and which account is
        // current must not appear to wait on it.
        ApplyAccountSelection(animate: true);
        Refresh();
        completed?.Invoke();
    }


    /// <summary>
    /// Re-reads the data directory on demand. This is the only refresh the user
    /// controls; the file watchers cover everything the host writes on its own.
    /// </summary>
    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RequestCoreRefresh();
        SpinRefreshIcon();
        Refresh();
    }

    /// <summary>
    /// One full turn of the toolbar icon. A single timed animation rather than a
    /// progress indicator: reading the directory takes well under a frame, so the
    /// spin exists to confirm the click landed, not to report progress.
    /// </summary>
    private void SpinRefreshIcon()
    {
        // The icon's transform is a group of [scale, rotate]; hover drives the
        // scale and this drives the rotate, so neither animation clobbers the
        // other's property.
        if (RefreshIcon.RenderTransform is not TransformGroup group) return;
        if (group.Children.Count < 2 || group.Children[1] is not RotateTransform rotation) return;

        rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromMilliseconds(560),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private static void RequestCoreRefresh()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(@"Local\BalancePet.UsageAnalytics.Refresh.v1");
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void OnOpenDataClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_store.DirectoryPath);
            Process.Start(new ProcessStartInfo("explorer.exe", _store.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    // ======================================================================
    //  the ledger: one row per task, and the row that opens
    // ======================================================================

    /// <summary>
    /// Opening a row, as four motions at once, all of them the prototype's own
    /// measured parameters (REVISION-NOTES.md):
    ///
    /// <list type="bullet">
    /// <item>0–200 ms: the container's height, <c>CubicEase EaseOut</c>. A layout
    /// property, animated on purpose.</item>
    /// <item>0–200 ms: the row's own body — background transparent to Raised,
    /// corner radius 0 to 10, and the 2 px accent bar down its left edge. Same
    /// duration as the height rather than staggered behind it, because these two
    /// are one gesture: the row is becoming a card.</item>
    /// <item>40–360 ms: the surface's outline opacity and its inset, the opacity on
    /// <c>BackEase EaseOut</c> so it passes its target by about 8% and settles.</item>
    /// <item>60 ms on, 30 ms apart, 180 ms each: the detail lines, fading in and
    /// rising 6 px. 60 rather than 120 because the height used to finish with
    /// nothing inside it, so the middle of the gesture was an empty box.</item>
    /// </list>
    /// </summary>
    private void BuildOpenLanes(MotionBoard board, LedgerRowView view)
    {
        // The design's measured overshoot: amplitude 0.25 on the framework's scale,
        // which is the 8% peak the prototype's probe read at 230 ms. Not 1.0 — at
        // that amplitude the peak lands on the start of the sweep and the curve
        // reaches 1.5, which is a bounce rather than a settle.
        var back = MotionBoard.BackOut(0.25);
        // The height lane invalidates the row's own measure, and that is not a
        // detail: a StackPanel arranges each child at the size that child asked for,
        // so writing a larger Height onto the card and nothing else makes the card
        // assert a height its slot does not have. Measured, that state looked
        // correct — the card and the row both reported 206.4 — while the frame drew
        // a 34 px row, because the slot it was drawn into was still 34 px tall.
        board.Add("height", value =>
        {
            view.Card.Height = value;
            SizeContainer(view, value);
            InvalidateChain(view);
        }, view.ClosedHeight, view.OpenHeight, 0, ExpandHeightMs, MotionBoard.CubicOut);
        board.Add("row-bg", value => view.RowBrush.Opacity = Math.Clamp(value, 0, 1), 0, 1, 0, ExpandHeightMs, MotionBoard.CubicOut);
        board.Add("row-radius", value => view.Card.CornerRadius = new CornerRadius(value), 0, RowRadiusOpen, 0, ExpandHeightMs, MotionBoard.CubicOut);
        // Width 0 -> 2 with a fixed left alignment is the one closed form WPF draws
        // at every frame in between without a negative margin. The alignment must
        // stay Left for the whole gesture: a Rectangle with an explicit Width under
        // Stretch is not drawn at the cell's left edge, it is *centred* in it, which
        // puts a 2 px bar down the middle of the row. The prototype measured exactly
        // that at x=651 of a 1120 px window.
        board.Add("row-stripe", value =>
        {
            view.Stripe.HorizontalAlignment = HorizontalAlignment.Left;
            view.Stripe.Width = Math.Max(0, value);
        }, 0, RowStripeWidth, 0, ExpandHeightMs, MotionBoard.CubicOut);
        board.Add("surface", value =>
        {
            view.Surface.Opacity = Math.Clamp(value, 0, 1);
        }, 0, 1, ExpandSurfaceAt, ExpandSurfaceMs, back);
        board.Add("padding", value => view.Surface.Padding = new Thickness(value), 0, SurfaceInset, ExpandSurfaceAt, ExpandSurfaceMs, MotionBoard.CubicOut);

        // The panel is built here too, and this is the cheap no-op that says so: the
        // lanes below write to lines a closed row does not have, and an opened row
        // has already had them built by OpenRow. See LedgerRowView.EnsureDetails.
        var lines = view.EnsureDetails();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var shift = (TranslateTransform)line.RenderTransform;
            var begin = ExpandDetailsAt + index * LineStagger(lines.Count);
            board.Add($"line{index}-opacity", value => line.Opacity = Math.Clamp(value, 0, 1), 0, 1, begin, ExpandDetailMs);
            board.Add($"line{index}-rise", value => shift.Y = value, DetailRise, 0, begin, ExpandDetailMs);
        }
    }

    /// <summary>
    /// How far apart the detail lines arrive: the design's 30 ms, tightened only
    /// when the panel has more lines than the opening has room for. The last line
    /// starts 180 ms before the surface lane settles, so a box that has finished
    /// opening is a box with its contents in it — which is the point
    /// <see cref="ExpandDetailsAt"/> was moved from 120 to 60 for.
    ///
    /// <para>
    /// Uncapped, the tenth line of the fixture's five-sub-request panel arrived at
    /// 510 ms and the middle of that gesture was a container at its full height with
    /// three of its five rows in it: the same empty band the height fix removes at
    /// rest, drawn mid-motion instead. Panels of up to five lines keep the design's
    /// own 30 ms exactly.
    /// </para>
    /// </summary>
    private static double LineStagger(int lines)
    {
        if (lines < 2) return 0;
        var lastBegin = ExpandSurfaceAt + ExpandSurfaceMs - ExpandDetailMs;
        return Math.Min(ExpandDetailStagger, Math.Max(0, lastBegin - ExpandDetailsAt) / (lines - 1));
    }

    /// <summary>
    /// Closing: the same motion backwards and a little quicker. The lines leave
    /// first and fast, then the row's own body folds back — background to
    /// transparent, corners to square, the accent bar back to nothing — and the
    /// height last, so the row folds into a line instead of blinking out. The bar
    /// closes on the same 190 ms as the height, so it is still visible as the box
    /// collapses rather than being the first thing to go.
    /// </summary>
    private void BuildCloseLanes(MotionBoard board, LedgerRowView view)
    {
        board.Add("height", value =>
        {
            view.Card.Height = value;
            SizeContainer(view, value);
            InvalidateChain(view);
        }, view.Card.Height, view.ClosedHeight, 0, CollapseHeightMs, MotionBoard.CubicOut);
        board.Add("row-bg", value => view.RowBrush.Opacity = Math.Clamp(value, 0, 1), view.RowBrush.Opacity, 0, 0, CollapseHeightMs, MotionBoard.CubicOut);
        board.Add("row-radius", value => view.Card.CornerRadius = new CornerRadius(value), view.Card.CornerRadius.TopLeft, 0, 0, CollapseHeightMs, MotionBoard.CubicOut);
        board.Add("row-stripe", value =>
        {
            view.Stripe.HorizontalAlignment = HorizontalAlignment.Left;
            view.Stripe.Width = Math.Max(0, value);
        }, view.Stripe.Width, 0, 0, CollapseHeightMs, MotionBoard.CubicOut);
        board.Add("surface", value => view.Surface.Opacity = Math.Clamp(value, 0, 1), view.Surface.Opacity, 0, 30, CollapseSurfaceMs, MotionBoard.CubicOut);
        board.Add("padding", value => view.Surface.Padding = new Thickness(value), view.Surface.Padding.Left, 0, 0, CollapseHeightMs, MotionBoard.CubicOut);

        // A row can only be closing if it was opened, so its panel is already built;
        // asking for it keeps the closing path from depending on that being true.
        var lines = view.EnsureDetails();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var shift = (TranslateTransform)line.RenderTransform;
            var begin = index * CollapseDetailStagger;
            board.Add($"line{index}-opacity", value => line.Opacity = Math.Clamp(value, 0, 1), line.Opacity, 0, begin, CollapseDetailMs);
            board.Add($"line{index}-rise", value => shift.Y = value, shift.Y, DetailRise, begin, CollapseDetailMs);
        }
    }

    /// <summary>
    /// Opens one row and closes whatever was open. One at a time is the whole
    /// point: an accordion, not a list of independently expanded cards, because
    /// the question a reader has is "what was in <i>this</i> task".
    /// </summary>
    private void ToggleRow(LedgerRowView view)
    {
        if (view.IsOpen)
        {
            CloseRow(view);
            return;
        }
        if (_openRow is { } previous) CloseRow(previous);
        OpenRow(view);
    }

    private void OpenRow(LedgerRowView view)
    {
        view.IsOpen = true;
        _openRow = view;
        var board = new MotionBoard();
        // The panel is built and the target height measured here, before a lane is
        // added: both belong to the moment this row is opened for the first time, and
        // both have to be done before the height lane is given its numbers. See
        // LedgerRowView.EnsureDetails for why they are not done when the row is built.
        view.EnsureDetails();
        BuildOpenLanes(board, view);
        _expandBoard = board;
        board.Start();
    }

    private void CloseRow(LedgerRowView view)
    {
        view.IsOpen = false;
        if (ReferenceEquals(_openRow, view)) _openRow = null;
        var board = new MotionBoard();
        BuildCloseLanes(board, view);
        _expandBoard = board;
        board.Start();
    }

    /// <summary>Forgets every row's visual state: the list is being rebuilt.</summary>
    private void ClearRows()
    {
        _openRow = null;
    }

    /// <summary>
    /// Gives every realized list item the task row that draws it. The row is built
    /// into the item template's own host element rather than replacing the
    /// presenter's content: a presenter whose content is swapped after it has been
    /// laid out does not always take a new measure pass, and a row that is never
    /// measured draws at zero height — which is what a capture photographs.
    ///
    /// The host keeps whatever it was given, so a refresh that recycles items does
    /// not stack a second row on top of the first.
    /// </summary>
    private void GenerateRows()
    {
        // The probe reads the ledger's own accounting: how many items the list holds,
        // how many of them have a container this pass, and what each host was already
        // holding. "rebuilt=0" on a click that visibly changed the list is the kind of
        // number that is a defect in the instrument rather than in the code, and the
        // only way to tell those two apart is to print what the loop was looking at.
        var visited = 0;
        var containers = 0;
        var kept = 0;
        foreach (var item in EventsList.Items)
        {
            visited++;
            if (EventsList.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem container) continue;
            if (container.DataContext is not EventRow data) continue;
            containers++;
            // A freshly generated container has not applied its own template yet, and
            // the row host lives in that template.
            container.ApplyTemplate();
            var host = FindRowHost(container);
            if (host is null) continue;
            // A row that already belongs to this task is left exactly as it is: it
            // holds whatever state the reader put it in, and every one of its lanes
            // owns a property that a stray measure would overwrite.
            if (host.Child is LedgerRowView existing && ReferenceEquals(existing.Row, data)) { kept++; continue; }
            // Anything else is a container the list has handed to a different task,
            // or one whose template has just been applied for the first time. Both
            // need a row built for the data they carry now — and the item's height
            // put back to one line, because the height an opening row writes lands on
            // the ListViewItem and a recycled item keeps it: a strip of empty ledger,
            // exactly as tall as a row nobody opened. Every refresh rebuilds every row
            // (the rows are new objects each time), so this is also what keeps an
            // opened row from surviving a refresh at a height its own task no longer
            // fills.
            host.Child = BuildLedgerRow(data);
            SizeContainer(host, HeaderHeight);
            _probeRebuiltRows++;
            _probeRebuiltDetails += data.Details.Count;
        }
        if (_probeWatch is { } watch && visited > 0)
            ProbeLine($"probe: generate items={visited} containers={containers} kept={kept}"
                + $" t={watch.Elapsed.TotalMilliseconds:0.0}ms generator={EventsList.ItemContainerGenerator.Status}");
    }

    /// <summary>
    /// The item template's outer element: the frame the row is built into. Searched
    /// by name rather than by shape, because the presenter's own content is the
    /// EventRow (a plain object) and the template's output hangs beneath it.
    /// </summary>
    private static Border? FindRowHost(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is Border { Name: "LedgerRowHost" } border) return border;
            if (FindRowHost(child) is { } found) return found;
        }
        return null;
    }


    /// <summary>
    /// The ledger's column head: the same ten columns, in the same widths, as every
    /// task row below it, from the one list that declares them. Written here rather
    /// than in the window's XAML so the head and the rows cannot be given two
    /// answers to one question.
    /// </summary>
    private void BuildLedgerHead()
    {
        var head = RowGrid(LedgerColumnsFor(LedgerCardWidth()));
        head.Height = 26;
        HeadCell(head, "时间", 0, false);
        HeadCell(head, "模型", 1, false, 12);
        HeadCell(head, "强度", 2, true);
        HeadCell(head, "步骤", 3, true);
        HeadCell(head, "工具", 4, true);
        HeadCell(head, "输入", 5, true);
        HeadCell(head, "输出", 6, true);
        HeadCell(head, "耗时", 7, true);
        HeadCell(head, "费用", 8, true);
        HeadCell(head, "状态", 9, true);
        LedgerHead.Child = head;
        AlignLedgerHead();
    }

    /// <summary>
    /// Puts the head over the columns it names.
    ///
    /// <para>
    /// The numbers live in a panel 14 px inside a card that is centred in the
    /// list's viewport; the head lives in the row above that list. Three different
    /// boxes, so the head's own idea of where "left" is sat 130 px away from the
    /// first column's — a head that describes a table it is not on top of. The
    /// offset is measured off a realized row rather than derived from the three
    /// paddings, because a scrollbar appearing or disappearing moves the viewport
    /// and nothing else, and a constant would be wrong half the time.
    /// </para>
    /// </summary>
    private void AlignLedgerHead()
    {
        if (LedgerHead.Parent is not FrameworkElement host) return;
        if (EventsList.Items.Count == 0) return;
        if (EventsList.ItemContainerGenerator.ContainerFromIndex(0) is not ListViewItem item) return;
        if (FindRowView(item) is not { } view) return;
        // The card's own left edge is where its numbers start, less the inset the
        // card keeps around them.
        var origin = view.Card.TranslatePoint(new Point(SurfaceInset, 0), host);
        LedgerHead.Width = LedgerCardWidth() - 2 * SurfaceInset;
        LedgerHead.HorizontalAlignment = HorizontalAlignment.Left;
        LedgerHead.Margin = new Thickness(origin.X, LedgerHead.Margin.Top, 0, LedgerHead.Margin.Bottom);
    }

    /// <summary>
    /// The width one ledger card — and the head over it — is drawn at.
    ///
    /// <para>
    /// The card used to be the constant <c>LedgerWidth</c>, 748 px, inside a list
    /// that was whatever width the window gave it. Two consequences, both reported
    /// in one sentence: 「我拉宽界面，内容布局不会跟着动，拉短界面，内容也不会动很死板，
    /// 同时没有横向拖动条」. Widening the window left 500 px of nothing to the right
    /// of the last column; narrowing it pushed the last columns past the edge with
    /// no bar to bring them back. The width is now the list's own: the viewport when
    /// that is wider than the columns need, so the extra width goes into 模型 and
    /// nowhere else, and <see cref="LedgerMinWidth"/> when it is narrower, so the
    /// table is complete and the bar under it is what moves.
    /// </para>
    ///
    /// <para>
    /// The max is what keeps the two from chasing each other: the content is never
    /// narrower than the minimum, so the horizontal bar appears for exactly the
    /// windows the columns do not fit in and disappears the moment they do.
    /// </para>
    /// </summary>
    private double LedgerCardWidth()
    {
        var available = LedgerViewportWidth() - LedgerListChrome();
        // One pixel short of what fits. The scroller compares its extent with its viewport
        // at device resolution, so a fraction of a pixel past it is enough to draw a
        // horizontal bar for a table that is not cut off — and that bar, once drawn,
        // changes the box the next measurement is taken from.
        return Math.Max(LedgerMinWidth, Math.Floor(available) - 1);
    }

    /// <summary>
    /// What the list keeps for itself inside that viewport: its own vertical scrollbar.
    ///
    /// <para>
    /// Size the card to the whole viewport and the list is one scrollbar wider than the
    /// box it sits in — a horizontal bar that is drawn for every window, with a thumb
    /// that claims the table is cut off when it is not. So the card is the viewport less
    /// this, and the list is given <c>card + this</c> as its own width, which makes the
    /// scroller's extent exactly its viewport whenever the columns fit.
    /// </para>
    ///
    /// <para>
    /// Reserved rather than read, which is a correction: the first version asked the list
    /// whether its vertical bar was showing. That is a live layout fact about the very
    /// box this number sizes, so the answer depended on the previous answer — the card
    /// grew and shrank between frames, the horizontal bar blinked on a table that fit,
    /// and the whole ledger jittered under the pointer while the window was dragged. A
    /// constant gutter costs an empty strip in a window whose ledger happens to fit
    /// without scrolling, which is the one case that can spare it.
    /// </para>
    /// </summary>
    private static double LedgerListChrome()
    {
        // Exactly the width of the list's own vertical bar, which is now always drawn — so
        // this is a number about a bar that is really there, not a strip held empty on the
        // chance that one appears (see the ListView in MainWindow.xaml). The two extra pixels
        // are the plugin's own scrollbar style, which is two wider than the system metric:
        // measured — at the system width alone the card missed fitting by a pixel and a half.
        return SystemParameters.VerticalScrollBarWidth + 2;
    }

    /// <summary>
    /// The width the list has for its rows: the outer scroller's viewport, which is the
    /// page's width less the card's own padding and nothing else.
    ///
    /// <para>
    /// Read from <see cref="LedgerScroller"/> rather than from the ListView's own
    /// ScrollViewer, and that is the whole of why the first attempt at this did not
    /// work: a ListView's inner ScrollViewer measures its items against the viewport it
    /// had <i>before</i> its vertical scrollbar appeared, so its extent is permanently
    /// one scrollbar wider than the viewport left over — a horizontal bar for every
    /// window, including the ones whose table fits. The scroller the list sits in has no
    /// such history: its viewport is a number about the page.
    /// </para>
    /// </summary>
    private double LedgerViewportWidth()
    {
        var viewport = LedgerScroller.ViewportWidth > 0
            ? LedgerScroller.ViewportWidth
            : LedgerScroller.ActualWidth;
        return viewport > 0 ? viewport : HistoryScrollViewer.ActualWidth;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer scroller) return scroller;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// Gives the list, every realized row and the head above them the width the page has
    /// now. A row is generated once and a resize does not re-make it, so without this the
    /// table keeps the width the window had when its rows were realized, which is what
    /// "内容布局不会跟着动" was. Only realized rows are touched: a container that does not
    /// exist is a row that will be built at the new width when it is scrolled to, and
    /// walking the item list instead would realize the whole ledger on every pixel of a
    /// drag.
    /// </summary>
    private void ResizeLedgerRows()
    {
        var viewport = LedgerViewportWidth();
        var cardWidth = LedgerCardWidth();

        // The narrow case is the only one the code has to build: a list wider than the box
        // holding it is exactly what the outer scroller's horizontal bar is for, and the
        // columns cannot fit in less than their minimum. Every other window is left to
        // stretch, because assigning a width on each measurement is what a dragged window
        // was doing sixty times a second — and each assignment re-measured every realized
        // row, which is the shimmer in the text. It also moved the box the next
        // measurement came from, so with a bar appearing the two took turns.
        var pinnedWidth = LedgerMinWidth + LedgerListChrome();
        var listWidth = viewport < pinnedWidth ? pinnedWidth : double.NaN;
        var widthUnchanged = double.IsNaN(EventsList.Width) && double.IsNaN(listWidth)
            || !double.IsNaN(EventsList.Width) && !double.IsNaN(listWidth)
            && Math.Abs(EventsList.Width - listWidth) <= 0.5;
        if (!widthUnchanged) EventsList.Width = listWidth;

        // A row keeps the width it was built with until the card has really moved. Below a
        // pixel nobody can see the difference, and the row is not re-measured for it — the
        // guess that a sub-pixel correction is free is what the twitching scrollbars were.
        if (Math.Abs(_ledgerCardWidth - cardWidth) >= 1)
        {
            _ledgerCardWidth = cardWidth;
            var innerWidth = cardWidth - 2 * SurfaceInset;
            foreach (var item in EventsList.Items)
            {
                if (EventsList.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem container) continue;
                FindRowView(container)?.Resize(cardWidth, innerWidth);
            }
            AlignLedgerHead();
        }
    }

    /// <summary>
    /// The card width every realized row was last sized to. Kept so a resize that changes
    /// nothing visible does not re-measure the ledger, and reset to zero so the first pass
    /// after the rows are built always applies.
    /// </summary>
    private double _ledgerCardWidth;

    private static ContentPresenter? FindPresenter(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ContentPresenter presenter) return presenter;
            if (FindPresenter(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// Invalidates the measure of everything between a row and the list that holds
    /// it. A row that grows has to tell the panel above it, and the panel's answer
    /// is the slot the row is arranged into: invalidating only the row leaves the
    /// slot at the old height, and the row is then drawn clipped to it — with every
    /// measured number inside the row insisting it is the new size.
    /// </summary>
    private static void InvalidateChain(DependencyObject row)
    {
        var node = row;
        while (node is not null and not ListView)
        {
            if (node is UIElement element) element.InvalidateMeasure();
            node = VisualTreeHelper.GetParent(node);
        }
    }

    /// <summary>
    /// Sizes the list item that holds a row, so the item is measured at the height
    /// the row has grown to. Invalidation alone was measured not to be enough: the
    /// panel asks its items for a size, and an item that has already answered keeps
    /// the answer it gave until it is asked again.
    /// </summary>
    private static void SizeContainer(DependencyObject row, double height)
    {
        var node = row;
        while (node is not null)
        {
            if (node is ListViewItem item)
            {
                item.Height = height;
                return;
            }
            node = VisualTreeHelper.GetParent(node);
        }
    }

    /// <summary>
    /// Re-draws the ledger after the list has been given new items: the head, then
    /// every row the list has realized. The items are set first and the panels laid
    /// out after, because a row can only be built into a container that exists.
    /// </summary>
    private void RebuildLedger()
    {
        ClearRows();
        UpdateLayout();
        GenerateRows();
        UpdateLayout();
        // After the rows exist and have been laid out, because the head is put where
        // they are rather than where the page says they should be — and the width the
        // rows were built at is the width the list had before this rebuild, so the box,
        // the rows and the head are all given the current one here.
        ResizeLedgerRows();
    }

    /// <summary>
    /// One row, as it is drawn while it is closed: the ten-column line, the frame
    /// the row's own body is drawn in, and the host its sub-request panel will be
    /// built into the first time the row opens. Nothing here costs anything per
    /// sub-request — see <see cref="BuildDetailLines"/> for the half that does and
    /// for why it is not run here.
    /// </summary>
    private LedgerRowView BuildLedgerRow(EventRow row)
    {
        var view = new LedgerRowView(row, this);

        var cardWidth = LedgerCardWidth();
        var width = cardWidth - 2 * SurfaceInset;

        // The head: 时间 模型 强度 步骤 工具 输入 输出 耗时 费用 状态. One entry per
        // field the task carries, in the order a person reads them; the widths are
        // the ones the column head above the list declares. 账户 is not one of them
        // any more: the list is scoped to one account or one group, so a cell naming
        // it would repeat the line under the title on all sixty-four rows.
        var header = RowGrid(LedgerColumnsFor(cardWidth));
        header.Height = HeaderHeight;
        header.Children.Add(Time(row.OccurredAtText, 0));
        var model = Named(Cell(row.Model, "Digit", 1), row.Model, right: false);
        model.Margin = new Thickness(12, 0, 0, 0);
        model.HorizontalAlignment = HorizontalAlignment.Left;
        // Kept so a capture can photograph this cell's tooltip; see OpenLedgerToolTip.
        view.ModelCell = model;
        header.Children.Add(model);
        header.Children.Add(Cell(row.ReasoningShort, "FigureCell", 2));
        header.Children.Add(Cell(Count(row.Steps), "FigureCell", 3));
        header.Children.Add(Cell(Count(row.ToolCalls), "FigureCell", 4));
        header.Children.Add(Cell(Token(row.InputTokens), "FigureCell", 5));
        header.Children.Add(Cell(Token(row.OutputTokens), "FigureCell", 6));
        header.Children.Add(Cell(row.DurationShort, "FigureCell", 7));
        header.Children.Add(Cell(MoneyReading(row.Cost, row.Currency), "FigureCell", 8));
        var status = Cell(row.StatusText, "Note", 9);
        status.HorizontalAlignment = HorizontalAlignment.Right;
        status.FontSize = 10.5;
        status.Foreground = row.StatusBrush;
        header.Children.Add(status);

        // The panel's own frame, empty until the row first opens: no fill (the row's
        // body is the fill), a HairGrid outline, and the inset a lane grows from 0.
        // Two layers, not three.
        var surface = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(RowRadiusOpen),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("HairGridBrush"),
            Opacity = 0,
        };

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        content.Children.Add(header);
        var details = new Grid { Margin = new Thickness(0, 6, 0, SurfaceInset) };
        var detailsHost = new Grid();
        Grid.SetRow(detailsHost, 1);
        content.Children.Add(detailsHost);
        detailsHost.Children.Add(details);

        var inner = new Border { Background = Brushes.Transparent, Child = content, Width = width };

        // A brush of this row's own: the row's own body is what the background lane
        // writes, and a frozen shared resource cannot be animated.
        var rowBrush = new SolidColorBrush(((SolidColorBrush)FindResource("RaisedBrush")).Color) { Opacity = 0 };

        // The accent bar: 2 px down the row's left edge, inside the surface so it
        // takes the surface's growing inset with it, above the content so it is
        // never the thing a number is drawn under.
        var stripe = new Rectangle
        {
            Fill = (Brush)FindResource("RowAccentBrush"),
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        // The separator stays behind the row's Raised body while that body is up and
        // shows again on the first frame of the collapse: it is the hairline the row
        // folds back onto.
        var separator = new Border
        {
            BorderBrush = (Brush)FindResource("HairGridBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        var surfaceHost = new Grid { ClipToBounds = true };
        surfaceHost.Children.Add(separator);
        surfaceHost.Children.Add(surface);
        surfaceHost.Children.Add(inner);
        surfaceHost.Children.Add(stripe);

        var hover = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(8) };
        var card = new Border
        {
            Child = surfaceHost,
            Background = rowBrush,
            CornerRadius = new CornerRadius(0),
            ClipToBounds = true,
            Height = HeaderHeight,
            Padding = new Thickness(0),
            Width = cardWidth,
        };

        var wrapper = new Grid();
        wrapper.Children.Add(hover);
        wrapper.Children.Add(card);

        view.Attach(card, inner, surface, stripe, rowBrush, details, width);
        view.DetailBuilder = () => BuildDetailLines(row, details, view);
        // The height this row opens to is measured on the first opening, in
        // LedgerRowView.EnsureDetails, together with the panel it measures. Measuring
        // it here measured an empty panel for every row in the ledger and charged the
        // whole list for a gesture nobody had made.
        card.MouseEnter += (_, _) => { if (!view.IsOpen) hover.Background = (Brush)FindResource("RaisedBrush"); };
        card.MouseLeave += (_, _) => hover.Background = Brushes.Transparent;
        return view.WithContent(wrapper);
    }

    /// <summary>
    /// One row's sub-request panel: the caption, the column names, one line per
    /// sub-request and the sum under them. Built on the first opening of the row it
    /// belongs to, because a closed row draws one line and shows none of this.
    ///
    /// <para>
    /// It used to be built for every realized row, and the finished panel was then
    /// measured for every one of them. That is invisible when a task has no
    /// sub-requests — the panel is then a caption and one sentence — and it is the
    /// entire cost of a click when the tasks do have them. One client's tasks average
    /// 5.8 sub-requests and reach 71, the ledger is 64 rows, and every sub-request is a
    /// Grid with eleven column definitions and eight elements of its own: a ledger of
    /// those tasks built two thousand elements it would never draw, and the measure
    /// that followed laid every one of them out. The panel being built is not what a
    /// reader waits for; the ledger appearing is, and a closed row is one line.
    /// </para>
    /// </summary>
    private void BuildDetailLines(EventRow row, Grid details, LedgerRowView view)
    {
        // The panel sits inside the row's own padding, 26 px narrower than
        // the head above it, which is what makes it read as content in a container
        // rather than a second table beside the first — and it carries the opening's
        // own inset at its foot, so the last line of a sub-request table sits the
        // same distance off the card's edge as the numbers do off its sides. That
        // inset is the 14 px the height lane used to reserve without drawing: a
        // reservation for padding that is real is a height that is honest, and a
        // band of nothing is what it looks like when it is not there.
        var detailColumns = DetailColumnsFor(LedgerCardWidth());
        foreach (var column in detailColumns) details.ColumnDefinitions.Add(Column(column));
        var lines = new List<FrameworkElement>();

        var caption = new TextBlock
        {
            Text = row.Details.Count > 0
                ? $"明细 · {row.Details.Count} 个子请求（{row.EventId}）"
                : $"明细 · 没有子请求（{row.EventId}）",
            Style = (Style)FindResource("RowLabel"),
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetColumnSpan(caption, detailColumns.Count);
        lines.Add(caption);

        if (row.Details.Count == 0)
        {
            // A task that never reached the model has no sub-requests. Saying so is
            // not the same as showing an empty table.
            var why = new TextBlock
            {
                Text = row.StatusText == "成功"
                    ? "这个任务在一次调用里完成，没有拆成子请求。"
                    : $"这个任务{row.StatusText}，没有产生子请求，因此没有费用——空不是 0。",
                Style = (Style)FindResource("Note"),
                Foreground = (Brush)FindResource(row.StatusText == "成功" ? "MutedBrush" : "RedBrush"),
            };
            Grid.SetColumnSpan(why, detailColumns.Count);
            lines.Add(why);
        }
        else
        {
            var head = RowGrid(detailColumns);
            HeadCell(head, "子请求", 0, false);
            HeadCell(head, "时间", 1, false);
            HeadCell(head, "模型", 2, false, 12);
            // 步骤, 工具 and 耗时 are left unlabelled: the columns are there to keep
            // the ones beside them in line with the task's, and a label over a column
            // that is empty by definition would only invite a reader to look for a
            // number that cannot exist.
            HeadCell(head, "强度", 3, true);
            HeadCell(head, "输入", 6, true);
            HeadCell(head, "输出", 7, true);
            HeadCell(head, "缓存读", 9, true);
            HeadCell(head, "费用", 10, true);
            lines.Add(head);

            // One HairGrid rule under the column names, nothing around the outside.
            // Built fresh rather than from RowSeparator, which is 1 px tall and has
            // no opacity for a line lane to write.
            lines.Add(new Border { Height = 1, Background = (Brush)FindResource("HairGridBrush"), Margin = new Thickness(0, 0, 0, 2) });

            for (var index = 0; index < row.Details.Count; index++)
            {
                var detail = row.Details[index];
                var grid = RowGrid(detailColumns);
                var ordinal = Cell($"{index + 1}.", "MutedFigureCell", 0);
                ordinal.HorizontalAlignment = HorizontalAlignment.Left;
                // The ordinal is punctuation in front of a time, not a column of its
                // own: 34 px was a width invented for it and most of that was air.
                ordinal.Margin = new Thickness(3, 0, 0, 0);
                grid.Children.Add(ordinal);
                grid.Children.Add(Time(detail.TimeText, 1));
                var name = Named(Cell(detail.Model, "Digit", 2), detail.Model, right: false);
                name.HorizontalAlignment = HorizontalAlignment.Left;
                name.Margin = new Thickness(12, 0, 0, 0);
                // Held so a capture can hover it: this is the one cell in the ledger whose
                // value can be empty, and an empty tooltip was the white rectangle in the
                // report. See MainWindow.Tip.
                view.DetailModelCells.Add(name);
                grid.Children.Add(name);
                grid.Children.Add(Cell(detail.Effort.Length > 0 ? detail.Effort : "—", "FigureCell", 3));
                grid.Children.Add(Cell(Token(detail.Input), "FigureCell", 6));
                grid.Children.Add(Cell(Token(detail.Output), "FigureCell", 7));
                grid.Children.Add(Cell(Token(detail.CacheRead), "MutedFigureCell", 9));
                grid.Children.Add(Cell(MoneyReading(detail.Cost, detail.Currency), "FigureCell", 10));
                lines.Add(grid);
            }

            // The sum sits in the panel's own cost column, one place right of the
            // task's, under a second rule, so the total is visibly the same kind of
            // number as the parts above it and the two rules bracket the rows.
            lines.Add(new Border { Height = 1, Background = (Brush)FindResource("HairGridBrush"), Margin = new Thickness(0, 3, 0, 0) });
            var total = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            var totalColumns = new List<GridColumn>(detailColumns);
            totalColumns[9] = totalColumns[9] with { Star = true };
            foreach (var column in totalColumns) total.ColumnDefinitions.Add(Column(column));
            var sumLabel = new TextBlock
            {
                Text = $"子请求合计 · {row.Details.Count} 条，与上面一行的费用同源",
                Style = (Style)FindResource("RowLabel"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumnSpan(sumLabel, 9);
            total.Children.Add(sumLabel);
            var sumValue = new TextBlock
            {
                Text = row.DetailCostSum is { } sum ? Money(sum) : "未上报",
                Style = (Style)FindResource("FigureCell"),
                Foreground = (Brush)FindResource(row.DetailCostSum is null ? "StaleTintBrush" : "FlowRateBrush"),
                // The same gutter the sub-request lines above it keep, so the total
                // is the same kind of number in the same column as the parts.
                Margin = new Thickness(0, 0, CellGutter, 0),
            };
            Grid.SetColumn(sumValue, 10);
            total.Children.Add(sumValue);
            lines.Add(total);
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            Grid.SetRow(line, index);
            Grid.SetColumnSpan(line, detailColumns.Count);
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            line.Opacity = 0;
            line.RenderTransform = new TranslateTransform(0, DetailRise);
            details.Children.Add(line);
        }
    }

    // ----------------------------------------------------------------------
    //  ledger geometry, kept in one place so the head and the rows agree
    // ----------------------------------------------------------------------

    /// <summary>
    /// The ledger's ten columns, in the order the head row declares them: 时间 模型
    /// 强度 步骤 工具 输入 输出 耗时 费用 状态. One list, so a task row, one of its
    /// sub-request lines and the head above them cannot drift apart — the whole
    /// point of an expanded row is that its numbers sit under the numbers they
    /// belong to.
    ///
    /// <para>
    /// 账户 is no longer one of them. The ledger is always scoped to one account, or
    /// to one of the two groups, so every row in it carries the same name: a column
    /// that is constant down the page costs 120 px of a ten-column table and states
    /// nothing the page has not already said. The scope is written once, under the
    /// title. See REDESIGN-NOTES 第六轮 §2.
    /// </para>
    ///
    /// <para>
    /// Every right-aligned figure keeps <see cref="CellGutter"/> px of its own
    /// column on its right. That is what makes the gap between two numbers a
    /// property of the table rather than a coincidence of two strings happening to
    /// be short: 费用 was 62 px against a 66 px reading, so the money overflowed its
    /// column by four pixels, leftwards, into 输出 — which is what
    /// "28.07K0.0855 USD" in the last round's screenshot actually was. 费用 has to
    /// hold "1234.5678 USD", the longest thing <see cref="MoneyReading"/> can print.
    /// </para>
    ///
    /// <para>
    /// The widths are a function of the card rather than constants, because every
    /// spare pixel used to go to 模型 and to nothing else: at 1918 the name column
    /// measured 950 px against names 120 px long, and the middle of every row was a
    /// band of nothing. 模型 now grows first, up to <see cref="ModelMaxWidth"/>, and
    /// what its cap leaves is shared out across the figures, so the table widens as a
    /// table: the numbers keep their right edges and the air between them grows
    /// evenly, which is what the width is actually for.
    /// </para>
    /// </summary>
    private static IReadOnlyList<GridColumn> LedgerColumnsFor(double cardWidth)
    {
        _ = cardWidth;
        // Shares, not pixels. The head and the row are two grids that are given their
        // width by different hands at different moments; a pixel table makes each of them
        // believe it owns the columns, so rebuilding one and not the other leaves the head
        // naming numbers that are no longer under it — 「上面的标题跟下面没对齐」 — and
        // rebuilding them one after the other makes the head move while the row waits —
        // 「轮到上面标题动，内容不动了」. A share is answered from the width in front of
        // the grid, so neither table needs rebuilding when the window moves.
        return
        [
            new(100, false),                         // 时间: fixed, so the hour column never moves
            new(ModelWeight, true, ModelMinWidth, ModelMaxWidth), // 模型: twice a figure
            new(1, true, 54),                        // 强度   =  46 + gutter
            new(1, true, 46),                        // 步骤   =  38 + gutter
            new(1, true, 46),                        // 工具   =  38 + gutter
            new(1, true, 66),                        // 输入   =  58 + gutter
            new(1, true, 58),                        // 输出   =  50 + gutter
            new(1, true, 76),                        // 耗时
            new(1, true, 92),                        // 费用   =  84 + gutter
            new(1, true, 76),                        // 状态 = 68 + gutter; holds the right edge
        ];
    }

    /// <summary>
    /// The ledger's ten columns in pixels, for the one table that cannot take shares: the
    /// sub-request panel carves 序号 out of 时间, so its first two boxes are not the
    /// ledger's first box and the two lists cannot be handed the same definitions.
    /// </summary>
    private static IReadOnlyList<GridColumn> LedgerPixels(double cardWidth)
    {
        var slack = Math.Max(0, cardWidth - 2 * SurfaceInset - LedgerMinWidth);
        var model = Math.Min(ModelMaxWidth, ModelMinWidth + slack);
        var shared = Math.Max(0, slack - (model - ModelMinWidth)) / FigureColumns;
        return
        [
            new(100, false),                 // 时间
            new(model, false),               // 模型
            new(54 + shared, false),         // 强度   =  46 + gutter
            new(46 + shared, false),         // 步骤   =  38 + gutter
            new(46 + shared, false),         // 工具   =  38 + gutter
            new(66 + shared, false),         // 输入   =  58 + gutter
            new(58 + shared, false),         // 输出   =  50 + gutter
            new(76 + shared, false),         // 耗时
            new(92 + shared, false),         // 费用   =  84 + gutter
            new(76 + shared, true, 76 + shared), // 状态 = 68 + gutter; holds the right edge
        ];
    }

    /// <summary>
    /// The sub-request panel's columns: the task row's own boundaries, column for
    /// column — derived from them rather than written out a second time, because two
    /// lists that agree today are two lists that disagree the first time one of them
    /// is edited.
    ///
    /// <para>
    /// 序号 is carved out of the task's 时间 column (34 + 66 = 100), and the rest line
    /// up one for one, so a sub-request's numbers sit under the task's numbers and not
    /// 42 px to the right of them, which is where they were. A sub-request has no 步骤,
    /// no 工具 and no 耗时 of its own: those columns are left in place and left empty
    /// rather than closed up, because closing them up is what pushed every column
    /// after them out of line.
    /// </para>
    /// </summary>
    private static IReadOnlyList<GridColumn> DetailColumnsFor(double cardWidth)
    {
        var ledger = LedgerPixels(cardWidth);
        var columns = new List<GridColumn>(ledger.Count + 1) { new(34, false) };
        columns.Add(new(ledger[0].Width - 34, false));
        for (var index = 1; index < ledger.Count; index++) columns.Add(ledger[index]);
        return columns;
    }

    /// <summary>
    /// The narrowest the ledger's card is ever drawn: the ten columns of
    /// <see cref="LedgerColumnsFor"/> at their own widths, with 模型 at its floor —
    /// 614 + 144 = 758. A window narrower than this gets a horizontal scrollbar
    /// rather than columns that run off the edge with no way to reach them, which
    /// is the second half of the report: "拉短界面，内容也不会动很死板，同时没有横向
    /// 拖动条". The card is never drawn narrower, and never with a band of nothing
    /// to its right: it is this width or the viewport's, whichever is larger. See
    /// <see cref="LedgerCardWidth"/>.
    /// </summary>
    private const double LedgerMinWidth = 758;

    /// <summary>What 模型 is drawn at when there is room for nothing else.</summary>
    private const double ModelMinWidth = 144;

    /// <summary>
    /// The widest 模型 is ever drawn.
    /// </summary>
    /// <remarks>
    /// A model name is the one value in the row that is a sentence rather than a
    /// reading, and the longest one seen here is twenty-four characters. Beyond that
    /// the column is air in the middle of a table, which is what it became at 1918:
    /// the name ended a third of the way across and the rest of the width was empty
    /// on every row. The name still trims with an ellipsis and still carries its
    /// whole value on its tooltip; see <see cref="Named"/>.
    /// </remarks>
    private const double ModelMaxWidth = 360;

    /// <summary>
    /// How much faster 模型 grows than a figure's column: twice the share, until it reaches
    /// <see cref="ModelMaxWidth"/>, after which the growth goes to the figures instead.
    /// </summary>
    private const double ModelWeight = 2;

    /// <summary>
    /// How many columns share what <see cref="ModelMaxWidth"/> leaves behind: the seven
    /// figures after the model plus 状态, which is text but is read as one of them.
    /// </summary>
    private const int FigureColumns = 8;

    /// <summary>
    /// The clear space every right-aligned figure keeps between itself and the
    /// column to its right. It is a margin rather than a wider column because a
    /// right-aligned reading hugs its column's right edge: a column that is merely
    /// wide enough still puts the next number against it the moment the text is
    /// wide enough to fill it.
    /// </summary>
    private const double CellGutter = 8;

    private static Grid RowGrid(IReadOnlyList<GridColumn> columns)
    {
        // Snapped, because a share of a width that is not a whole number of pixels puts
        // every glyph on a half pixel: the text is then re-rasterised whenever the window
        // moves, which reads as the whole table shimmering under the pointer.
        var grid = new Grid { UseLayoutRounding = true, SnapsToDevicePixels = true };
        foreach (var column in columns) grid.ColumnDefinitions.Add(Column(column));
        return grid;
    }

    private static ColumnDefinition Column(GridColumn column)
    {
        var definition = new ColumnDefinition
        {
            // A star column carries its weight in Width — 2 for 模型, 1 for a figure —
            // and its floor in MinWidth, so one record describes both kinds of column.
            Width = column.Star
                ? new GridLength(column.Width > 0 ? column.Width : 1, GridUnitType.Star)
                : new GridLength(column.Width),
            MinWidth = column.MinWidth,
        };
        if (!double.IsPositiveInfinity(column.MaxWidth)) definition.MaxWidth = column.MaxWidth;
        return definition;
    }

    private static TextBlock Cell(string text, string styleKey, int column)
    {
        var block = new TextBlock { Text = text, Style = (Style)Application.Current.FindResource(styleKey) };
        // The gutter costs a left-aligned cell nothing it was using, and it is the
        // whole of the distance between two figures that are both right-aligned.
        block.Margin = new Thickness(0, 0, CellGutter, 0);
        Grid.SetColumn(block, column);
        return block;
    }

    /// <summary>
    /// A cell that shows a name: never wrapped, given an ellipsis when it does not
    /// fit, and carrying the whole value on its tooltip. A name that wraps is a row
    /// twice as tall as the ones around it, which is the rhythm of a table gone; a
    /// name that is silently cut is a value the reader cannot recover. Trimming with
    /// the text aligned right keeps the beginning of the name and puts the ellipsis
    /// at the end of it — a right-aligned cell that overflows loses its <i>first</i>
    /// characters instead, which is how "DeepSeek Harness" was being drawn as
    /// "eek Harness".
    /// </summary>
    private static TextBlock Named(TextBlock block, string value, bool right)
    {
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        block.TextWrapping = TextWrapping.NoWrap;
        block.TextAlignment = right ? TextAlignment.Right : TextAlignment.Left;
        Tip(block, value);
        return block;
    }

    /// <summary>
    /// A tooltip, or none at all.
    ///
    /// <para>
    /// An empty string is not "no tooltip": WPF opens a bubble for it, and an empty
    /// bubble is a white rectangle with nothing in it. That is the fourth report —
    /// 「鼠标悬浮时，显示白色的框。里面没内容。。。」 — and this line is its root
    /// cause: <see cref="Named"/> assigned the value it was given, and the 模型 cell
    /// of a task whose client never reported a model is given "". The row it
    /// appeared over is the one the screenshot shows, its 强度/步骤 columns just
    /// right of the pointer.
    /// </para>
    ///
    /// <para>
    /// The white is the other half of the same report, and it is a style problem
    /// rather than a data one: WPF's default tooltip is the shell's pale bubble,
    /// while this window's implicit <c>TextBlock</c> style sets every TextBlock's
    /// foreground to #F2F2F2 — including the one a tooltip draws its text in. So a
    /// bubble with text in it looked empty as well. App.xaml now styles ToolTip
    /// itself; both halves are needed, and neither alone would have fixed it.
    /// </para>
    /// </summary>
    private static void Tip(FrameworkElement element, string value)
        => element.ToolTip = string.IsNullOrWhiteSpace(value) ? null : value;

    private void HeadCell(Grid grid, string text, int column, bool right, double leftMargin = 0)
    {
        var block = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("ColumnHead"),
            // A head sits over a column of right-aligned figures, so it keeps the
            // same gutter they do: a label that ends where the column ends is a
            // label that ends eight pixels right of every number under it.
            Margin = new Thickness(leftMargin, 0, right ? CellGutter : 0, 2),
        };
        if (right) block.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    /// <summary>Timestamps are monospace: a ledger is read down its first column.
    /// Built as an element rather than through <see cref="Cell"/> because the style
    /// it needs is the one every reading already uses, plus the face.</summary>
    private static TextBlock Time(string text, int column)
    {
        var block = new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.FindResource("Digit"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(block, column);
        return block;
    }

    /// <summary>A token count, or the word for one nobody reported. Never 0.</summary>
    private static string Token(Reading reading)
        => reading.Value is { } value ? UsageFormatting.Tokens((long)value) : "未上报";

    private static string Count(Reading reading)
        => reading.Value is { } value ? value.ToString("0") : "—";

    private static string MoneyReading(Reading reading, string currency)
        => reading.Value is { } value
            ? $"{value:0.####} {currency}".TrimEnd()
            : "未上报";

    // ======================================================================
    //  the trend chart
    // ======================================================================

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => DrawTrendChart();

    private void DrawTrendChart()
    {
        if (TrendChart is null || _lastReport is null || TrendChart.ActualWidth < 80 || TrendChart.ActualHeight < 80) return;
        TrendChart.Children.Clear();
        var events = _lastEvents;
        var now = DateTimeOffset.Now;
        var buckets = CreateTrendBuckets(_trendRange, events, now);
        var points = buckets.Select(bucket =>
        {
            var values = events.Where(value => value.OccurredAt.LocalDateTime >= bucket.Start && value.OccurredAt.LocalDateTime < bucket.End).ToArray();
            var input = values.Sum(value => value.InputTokens ?? 0);
            var cacheRead = values.Sum(value => value.CacheReadTokens ?? 0);
            return new TrendPoint(
                bucket.Start,
                bucket.Label,
                Math.Max(0, input - cacheRead),
                values.Sum(value => value.OutputTokens ?? 0),
                values.Sum(value => value.CacheWriteTokens ?? 0),
                cacheRead,
                input <= 0 ? 0 : Math.Clamp(cacheRead * 100d / input, 0, 100),
                values.Length);
        }).ToArray();
        _trendPoints = points;
        var hasTokens = points.Any(value => value.HasTokenData);
        TrendEmpty.Visibility = hasTokens ? Visibility.Collapsed : Visibility.Visible;
        var plotLeft = 34d;
        var plotRight = Math.Max(plotLeft + 40, TrendChart.ActualWidth - 42);
        var plotTop = 10d;
        var plotBottom = Math.Max(plotTop + 30, TrendChart.ActualHeight - 28);
        _trendPlotLeft = plotLeft;
        _trendPlotRight = plotRight;
        HideTrendTooltip();
        var maxTokens = Math.Max(1, points.Max(value => value.MaxTokenSeries));
        DrawGridLines(plotLeft, plotRight, plotTop, plotBottom, maxTokens);
        DrawDateLabels(points, plotLeft, plotRight, plotBottom);
        if (!hasTokens) return;

        DrawTokenSeries(points, value => value.InputTokens, (Brush)FindResource("BlueBrush"), new SolidColorBrush(Color.FromArgb(42, 110, 168, 255)), maxTokens, plotLeft, plotRight, plotTop, plotBottom);
        DrawTokenSeries(points, value => value.OutputTokens, (Brush)FindResource("FlowRateBrush"), new SolidColorBrush(Color.FromArgb(42, 45, 225, 194)), maxTokens, plotLeft, plotRight, plotTop, plotBottom);
        DrawTokenSeries(points, value => value.CacheCreationTokens, (Brush)FindResource("OrangeBrush"), new SolidColorBrush(Color.FromArgb(30, 244, 185, 66)), maxTokens, plotLeft, plotRight, plotTop, plotBottom);
        DrawTokenSeries(points, value => value.CacheReadTokens, new SolidColorBrush(Color.FromRgb(21, 199, 215)), new SolidColorBrush(Color.FromArgb(38, 21, 199, 215)), maxTokens, plotLeft, plotRight, plotTop, plotBottom);
        DrawRateSeries(points, (Brush)FindResource("PurpleBrush"), plotLeft, plotRight, plotTop, plotBottom);
    }

    private void OnTrendChartMouseMove(object sender, MouseEventArgs e)
    {
        if (_trendPoints.Count == 0 || _trendPlotRight <= _trendPlotLeft) return;
        var position = e.GetPosition(TrendChart);
        var ratio = Math.Clamp((position.X - _trendPlotLeft) / (_trendPlotRight - _trendPlotLeft), 0, 1);
        var index = (int)Math.Round(ratio * Math.Max(0, _trendPoints.Count - 1));
        ShowTrendTooltip(index, position.X, position.Y);
    }

    private void OnTrendChartMouseLeave(object sender, MouseEventArgs e) => HideTrendTooltip();

    private void ShowTrendTooltip(int index, double cursorX, double cursorY)
    {
        if (index < 0 || index >= _trendPoints.Count) return;
        var point = _trendPoints[index];
        _trendTooltip ??= CreateTrendTooltip();
        _trendHoverLine ??= new Line
        {
            Stroke = new SolidColorBrush(Color.FromArgb(150, 150, 170, 195)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 2 },
            IsHitTestVisible = false
        };
        if (!TrendChart.Children.Contains(_trendHoverLine))
        {
            Canvas.SetZIndex(_trendHoverLine, 8);
            TrendChart.Children.Add(_trendHoverLine);
        }

        var content = new StackPanel { MinWidth = 176 };
        content.Children.Add(new TextBlock
        {
            Text = FormatTrendTooltipDate(point),
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        });
        AddTrendTooltipLine(content, "Input", UsageFormatting.Tokens(point.InputTokens), (Brush)FindResource("BlueBrush"));
        AddTrendTooltipLine(content, "Output", UsageFormatting.Tokens(point.OutputTokens), (Brush)FindResource("FlowRateBrush"));
        AddTrendTooltipLine(content, "Cache Creation", UsageFormatting.Tokens(point.CacheCreationTokens), (Brush)FindResource("OrangeBrush"));
        AddTrendTooltipLine(content, "Cache Read", UsageFormatting.Tokens(point.CacheReadTokens), new SolidColorBrush(Color.FromRgb(21, 199, 215)));
        AddTrendTooltipLine(content, "Cache Hit Rate", $"{point.CacheHitRate:0.#}%", (Brush)FindResource("PurpleBrush"));
        AddTrendTooltipLine(content, "Requests", point.Requests.ToString("N0"), (Brush)FindResource("MutedBrush"));
        _trendTooltip.Child = content;
        if (!TrendChart.Children.Contains(_trendTooltip))
        {
            Canvas.SetZIndex(_trendTooltip, 10);
            TrendChart.Children.Add(_trendTooltip);
        }

        var x = Math.Clamp(cursorX, _trendPlotLeft, _trendPlotRight);
        _trendHoverLine.X1 = x;
        _trendHoverLine.X2 = x;
        _trendHoverLine.Y1 = 10;
        _trendHoverLine.Y2 = Math.Max(10, TrendChart.ActualHeight - 28);
        _trendHoverLine.Visibility = Visibility.Visible;
        _trendTooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tooltipWidth = _trendTooltip.DesiredSize.Width;
        var tooltipHeight = _trendTooltip.DesiredSize.Height;
        var left = x + 12;
        if (left + tooltipWidth > TrendChart.ActualWidth - 4) left = x - tooltipWidth - 12;
        var top = cursorY + 14;
        if (top + tooltipHeight > TrendChart.ActualHeight - 4) top = cursorY - tooltipHeight - 14;
        top = Math.Clamp(top, 4d, Math.Max(4d, TrendChart.ActualHeight - tooltipHeight - 4));
        Canvas.SetLeft(_trendTooltip, Math.Max(4, left));
        Canvas.SetTop(_trendTooltip, top);
        _trendTooltip.Visibility = Visibility.Visible;
    }

    private Border CreateTrendTooltip()
        => new()
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 3, 8, 15)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, 65, 84, 109)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(9, 7, 9, 7),
            IsHitTestVisible = false
        };

    private static void AddTrendTooltipLine(Panel panel, string label, string value, Brush brush)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 10 });
        var valueText = new TextBlock { Text = value, Foreground = brush, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(valueText, 1);
        row.Children.Add(valueText);
        panel.Children.Add(row);
    }

    private string FormatTrendTooltipDate(TrendPoint point)
        => _trendRange == TrendRange.Last24Hours
            ? point.Date.ToString("yyyy-MM-dd HH:00")
            : point.Date.ToString("yyyy-MM-dd");

    private void HideTrendTooltip()
    {
        if (_trendTooltip is not null) _trendTooltip.Visibility = Visibility.Collapsed;
        if (_trendHoverLine is not null) _trendHoverLine.Visibility = Visibility.Collapsed;
    }

    private static IReadOnlyList<TrendBucket> CreateTrendBuckets(TrendRange range, IReadOnlyList<UsageEvent> events, DateTimeOffset now)
    {
        var localNow = now.LocalDateTime;
        switch (range)
        {
            case TrendRange.Last24Hours:
            {
                var end = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0);
                var start = end.AddHours(-23);
                return Enumerable.Range(0, 24).Select(index =>
                {
                    var bucketStart = start.AddHours(index);
                    return new TrendBucket(bucketStart, bucketStart.AddHours(1), bucketStart.ToString("HH:mm"));
                }).ToArray();
            }
            case TrendRange.Last30Days:
            {
                var end = localNow.Date;
                var start = end.AddDays(-29);
                return Enumerable.Range(0, 30).Select(index =>
                {
                    var bucketStart = start.AddDays(index);
                    return new TrendBucket(bucketStart, bucketStart.AddDays(1), bucketStart.ToString("MM-dd"));
                }).ToArray();
            }
            case TrendRange.Last90Days:
            {
                var end = localNow.Date.AddDays(1);
                var start = end.AddDays(-91);
                return Enumerable.Range(0, 13).Select(index =>
                {
                    var bucketStart = start.AddDays(index * 7);
                    return new TrendBucket(bucketStart, bucketStart.AddDays(7), bucketStart.ToString("MM-dd"));
                }).ToArray();
            }
            case TrendRange.AllTime:
            {
                var first = events.Count == 0 ? localNow.Date.AddMonths(-11) : events.Min(value => value.OccurredAt.LocalDateTime).Date;
                var start = new DateTime(first.Year, first.Month, 1);
                var end = new DateTime(localNow.Year, localNow.Month, 1).AddMonths(1);
                var count = Math.Max(1, (end.Year - start.Year) * 12 + end.Month - start.Month);
                return Enumerable.Range(0, count).Select(index =>
                {
                    var bucketStart = start.AddMonths(index);
                    return new TrendBucket(bucketStart, bucketStart.AddMonths(1), bucketStart.ToString("yyyy-MM"));
                }).ToArray();
            }
            default:
            {
                var end = localNow.Date;
                var start = end.AddDays(-6);
                return Enumerable.Range(0, 7).Select(index =>
                {
                    var bucketStart = start.AddDays(index);
                    return new TrendBucket(bucketStart, bucketStart.AddDays(1), bucketStart.ToString("MM-dd"));
                }).ToArray();
            }
        }
    }

    private void OnTrendRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrendRangeSelector?.SelectedItem is not ComboBoxItem item
            || !Enum.TryParse(item.Tag?.ToString(), ignoreCase: true, out TrendRange range)) return;
        _trendRange = range;
        DrawTrendChart();
    }

    private void DrawGridLines(double plotLeft, double plotRight, double plotTop, double plotBottom, long maxTokens)
    {
        var gridBrush = new SolidColorBrush(Color.FromArgb(90, 70, 100, 130));
        var muted = (Brush)FindResource("MutedBrush");
        for (var index = 0; index < 3; index++)
        {
            var fraction = index / 2d;
            var y = plotBottom - (plotBottom - plotTop) * fraction;
            TrendChart.Children.Add(new Line
            {
                X1 = plotLeft,
                X2 = plotRight,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeDashArray = new DoubleCollection { 2, 3 },
                StrokeThickness = 1
            });
            AddChartLabel(UsageFormatting.Tokens((long)Math.Round(maxTokens * fraction)), 2, y - 8, muted, 10, 30);
            AddChartLabel($"{fraction * 100:0}%", plotRight + 5, y - 8, muted, 10, 38);
        }
    }

    private void DrawDateLabels(IReadOnlyList<TrendPoint> points, double plotLeft, double plotRight, double plotBottom)
    {
        if (points.Count == 0) return;
        var width = plotRight - plotLeft;
        var step = Math.Max(1, (int)Math.Ceiling(points.Count / 7d));
        var indexes = Enumerable.Range(0, points.Count).Where(index => index % step == 0).ToList();
        if (!indexes.Contains(points.Count - 1)) indexes.Add(points.Count - 1);
        foreach (var index in indexes.Distinct())
        {
            var x = plotLeft + width * index / Math.Max(1, points.Count - 1);
            AddChartLabel(points[index].Label, x - 22, plotBottom + 8, (Brush)FindResource("MutedBrush"), 10, 48);
        }
    }

    private void DrawTokenSeries(
        IReadOnlyList<TrendPoint> points,
        Func<TrendPoint, long> selector,
        Brush color,
        Brush areaBrush,
        long maxTokens,
        double plotLeft,
        double plotRight,
        double plotTop,
        double plotBottom)
    {
        var line = new Polyline
        {
            Stroke = color,
            StrokeThickness = 2.1,
            Opacity = 0.95,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        var width = plotRight - plotLeft;
        var height = plotBottom - plotTop;
        var area = new Polygon { Fill = areaBrush, StrokeThickness = 0, IsHitTestVisible = false };
        area.Points.Add(new Point(plotLeft, plotBottom));
        for (var index = 0; index < points.Count; index++)
        {
            var x = plotLeft + width * index / Math.Max(1, points.Count - 1);
            var value = Math.Max(0, selector(points[index]));
            var y = plotBottom - height * value / maxTokens;
            line.Points.Add(new Point(x, y));
            area.Points.Add(new Point(x, y));
            if (value <= 0) continue;
            var dot = new Ellipse { Width = 6, Height = 6, Fill = color, Stroke = (Brush)FindResource("PanelBrush"), StrokeThickness = 1 };
            Canvas.SetLeft(dot, x - 3);
            Canvas.SetTop(dot, y - 3);
            Canvas.SetZIndex(dot, 3);
            TrendChart.Children.Add(dot);
        }
        area.Points.Add(new Point(plotRight, plotBottom));
        Canvas.SetZIndex(area, 1);
        TrendChart.Children.Add(area);
        Canvas.SetZIndex(line, 2);
        TrendChart.Children.Add(line);
    }

    private void DrawRateSeries(IReadOnlyList<TrendPoint> points, Brush color, double plotLeft, double plotRight, double plotTop, double plotBottom)
    {
        var line = new Polyline
        {
            Stroke = color,
            StrokeThickness = 2.2,
            Opacity = 0.98,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        var width = plotRight - plotLeft;
        var height = plotBottom - plotTop;
        for (var index = 0; index < points.Count; index++)
        {
            var x = plotLeft + width * index / Math.Max(1, points.Count - 1);
            var y = plotBottom - height * Math.Clamp(points[index].CacheHitRate, 0, 100) / 100d;
            line.Points.Add(new Point(x, y));
            if (points[index].InputTokens <= 0) continue;
            var dot = new Ellipse { Width = 6, Height = 6, Fill = color, Stroke = (Brush)FindResource("PanelBrush"), StrokeThickness = 1 };
            Canvas.SetLeft(dot, x - 3);
            Canvas.SetTop(dot, y - 3);
            TrendChart.Children.Add(dot);
        }
        Canvas.SetZIndex(line, 3);
        TrendChart.Children.Add(line);
    }

    private void AddChartLabel(string text, double left, double top, Brush brush, double fontSize, double width)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = fontSize,
            Width = width,
            TextAlignment = TextAlignment.Left,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        Canvas.SetZIndex(label, 4);
        TrendChart.Children.Add(label);
    }

    private void OnDetailChartSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_selectedEvent is not null) DrawDetailCharts(_selectedEvent);
    }

    private void DrawDetailCharts(EventRow row)
    {
        DrawTokenComposition(row);
        DrawCostTimeline(row);
        DrawTokenTrend(row);
    }

    private void DrawTokenComposition(EventRow row)
    {
        TokenCompositionChart.Children.Clear();
        var values = new[]
        {
            (Name: "输入", Value: row.InputTokensValue, Brush: (Brush)FindResource("BlueBrush")),
            (Name: "缓存读取", Value: row.CacheReadTokensValue, Brush: new SolidColorBrush(Color.FromRgb(21, 199, 215))),
            (Name: "输出", Value: row.OutputTokensValue, Brush: (Brush)FindResource("FlowRateBrush")),
            (Name: "缓存写入", Value: row.CacheWriteTokensValue, Brush: (Brush)FindResource("OrangeBrush"))
        };
        var max = values.Max(value => value.Value);
        TokenChartEmpty.Visibility = max <= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (max <= 0) return;

        var width = Math.Max(180, TokenCompositionChart.ActualWidth);
        var labelWidth = 58d;
        var valueWidth = 64d;
        var barWidth = Math.Max(40, width - labelWidth - valueWidth);
        for (var index = 0; index < values.Length; index++)
        {
            var item = values[index];
            var y = index * 17d;
            AddChartText(TokenCompositionChart, item.Name, 0, y - 2, (Brush)FindResource("MutedBrush"), 10);
            var track = new Border { Width = barWidth, Height = 7, Background = (Brush)FindResource("QuotaTrackBrush"), CornerRadius = new CornerRadius(4) };
            Canvas.SetLeft(track, labelWidth); Canvas.SetTop(track, y + 1); TokenCompositionChart.Children.Add(track);
            var fill = new Border { Width = Math.Max(item.Value > 0 ? 3 : 0, barWidth * item.Value / max), Height = 7, Background = item.Brush, CornerRadius = new CornerRadius(4) };
            Canvas.SetLeft(fill, labelWidth); Canvas.SetTop(fill, y + 1); TokenCompositionChart.Children.Add(fill);
            AddChartText(TokenCompositionChart, item.Value > 0 ? UsageFormatting.Tokens(item.Value) : "—", labelWidth + barWidth + 7, y - 2, (Brush)FindResource("TextBrush"), 10);
        }
    }

    private void DrawCostTimeline(EventRow row)
    {
        CostTimelineChart.Children.Clear();
        var values = row.Details.Where(detail => detail.CostValue.HasValue).ToArray();
        CostChartEmpty.Visibility = values.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (values.Length == 0) return;

        var width = Math.Max(220, CostTimelineChart.ActualWidth);
        var height = Math.Max(54, CostTimelineChart.ActualHeight);
        var max = Math.Max(0.000001, values.Max(value => value.CostValue!.Value));
        var left = 8d;
        var bottom = height - 16;
        var plotWidth = Math.Max(80, width - 16);
        var step = plotWidth / Math.Max(1, values.Length);
        var barWidth = Math.Max(3, Math.Min(18, step * 0.58));
        var baseline = new Border { Width = plotWidth, Height = 1, Background = (Brush)FindResource("HairGridBrush") };
        Canvas.SetLeft(baseline, left); Canvas.SetTop(baseline, bottom); CostTimelineChart.Children.Add(baseline);
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            var barHeight = Math.Max(3, (height - 28) * value.CostValue!.Value / max);
            var bar = new Border
            {
                Width = barWidth,
                Height = barHeight,
                Background = (Brush)FindResource("PurpleBrush"),
                CornerRadius = new CornerRadius(3),
                ToolTip = $"{value.TimeText} · {value.CostText}"
            };
            Canvas.SetLeft(bar, left + index * step + (step - barWidth) / 2); Canvas.SetTop(bar, bottom - barHeight); CostTimelineChart.Children.Add(bar);
        }
        AddChartText(CostTimelineChart, Money(max), 8, 0, (Brush)FindResource("MutedBrush"), 10);
        AddChartText(CostTimelineChart, values.Length == 1 ? "1 次请求" : $"{values.Length} 次请求", Math.Max(8, width - 62), bottom + 4, (Brush)FindResource("MutedBrush"), 10);
    }

    private void DrawTokenTrend(EventRow row)
    {
        TokenTrendChart.Children.Clear();
        var values = row.Details.Where(detail => detail.HasTokenData).ToArray();
        TokenTrendEmpty.Visibility = values.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (values.Length == 0) return;

        var width = Math.Max(420, TokenTrendChart.ActualWidth);
        var height = Math.Max(120, TokenTrendChart.ActualHeight);
        var left = 44d;
        var right = 12d;
        var top = 14d;
        var bottom = height - 25d;
        var plotWidth = Math.Max(120, width - left - right);
        var plotHeight = Math.Max(50, bottom - top);
        var max = Math.Max(1L, values.SelectMany(value => new long?[] { value.InputValue, value.CacheValue, value.OutputValue })
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max());

        foreach (var fraction in new[] { 0d, 0.5d, 1d })
        {
            var y = bottom - plotHeight * fraction;
            var grid = new Border
            {
                Width = plotWidth,
                Height = 1,
                Background = (Brush)FindResource("HairGridBrush"),
                Opacity = fraction == 0 ? 0.9 : 0.55
            };
            Canvas.SetLeft(grid, left);
            Canvas.SetTop(grid, y);
            TokenTrendChart.Children.Add(grid);
            AddChartText(TokenTrendChart, UsageFormatting.Tokens((long)(max * fraction)), 0, y - 7, (Brush)FindResource("MutedBrush"), 10);
        }

        DrawTokenTrendSeries(values, detail => detail.InputValue, (Brush)FindResource("BlueBrush"), "输入", max, left, plotWidth, top, bottom);
        DrawTokenTrendSeries(values, detail => detail.CacheValue, new SolidColorBrush(Color.FromRgb(21, 199, 215)), "缓存读取", max, left, plotWidth, top, bottom);
        DrawTokenTrendSeries(values, detail => detail.OutputValue, (Brush)FindResource("FlowRateBrush"), "输出", max, left, plotWidth, top, bottom);

        var labelIndexes = values.Length switch
        {
            1 => new[] { 0 },
            2 => new[] { 0, 1 },
            _ => new[] { 0, values.Length / 2, values.Length - 1 }
        };
        foreach (var index in labelIndexes.Distinct())
        {
            var x = left + plotWidth * index / Math.Max(1, values.Length - 1);
            var labelLeft = Math.Clamp(x - 28, left, Math.Max(left, width - 58));
            AddChartText(TokenTrendChart, values[index].TimeText, labelLeft, bottom + 7, (Brush)FindResource("MutedBrush"), 10);
        }
    }

    private void DrawTokenTrendSeries(
        IReadOnlyList<EventRow.DetailRow> values,
        Func<EventRow.DetailRow, long?> selector,
        Brush color,
        string seriesName,
        long max,
        double left,
        double plotWidth,
        double top,
        double bottom)
    {
        var lines = new List<Polyline>();
        Polyline? line = null;
        var plotHeight = bottom - top;
        for (var index = 0; index < values.Count; index++)
        {
            var x = left + plotWidth * index / Math.Max(1, values.Count - 1);
            var value = selector(values[index]);
            if (!value.HasValue)
            {
                if (line is not null) lines.Add(line);
                line = null;
                continue;
            }
            line ??= new Polyline
            {
                Stroke = color,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                ToolTip = seriesName
            };
            var numericValue = Math.Max(0, value.Value);
            var y = bottom - plotHeight * numericValue / max;
            line.Points.Add(new Point(x, y));
            if (numericValue <= 0) continue;
            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = color,
                Stroke = (Brush)FindResource("PanelBrush"),
                StrokeThickness = 1,
                ToolTip = $"{values[index].TimeText} · {seriesName} {UsageFormatting.Tokens(numericValue)}"
            };
            Canvas.SetLeft(dot, x - 3.5);
            Canvas.SetTop(dot, y - 3.5);
            Canvas.SetZIndex(dot, 3);
            TokenTrendChart.Children.Add(dot);
        }
        if (line is not null) lines.Add(line);
        foreach (var series in lines)
        {
            Canvas.SetZIndex(series, 2);
            TokenTrendChart.Children.Add(series);
        }
    }

    private static void AddChartText(Canvas canvas, string text, double left, double top, Brush brush, double fontSize)
    {
        var label = new TextBlock { Text = text, Foreground = brush, FontSize = fontSize, IsHitTestVisible = false };
        Canvas.SetLeft(label, left); Canvas.SetTop(label, top); canvas.Children.Add(label);
    }

    private void OnContentScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
        UpdateActiveNavigation();
    }

    // ======================================================================
    //  the reading: "reported as 0" is not "not reported"
    // ======================================================================

    /// <summary>
    /// The smallest thing that can carry the difference between a counter the host
    /// reported as zero and one it never reported. Every figure in this window has
    /// to pick one, and the rule is the same everywhere: a missing reading is the
    /// word 未上报 in <c>StaleTint</c>, and it is never printed as 0.
    /// </summary>
    private readonly record struct Reading(double? Value)
    {
        public static Reading Reported(double value) => new(value);
        public static Reading Missing() => new(null);
    }

    /// <summary>"Reported as zero" and "never reported" are two different readings,
    /// and every figure in this window has to pick one.</summary>
    private static Reading Meaningful(bool reported, double value) => reported ? Reading.Reported(value) : Reading.Missing();

    /// <summary>
    /// The freshness rule, in one place. A figure is stale when the host has not
    /// rewritten the document for an hour: long enough that a quiet account is not
    /// called stale every time it is looked at, short enough that a snapshot from
    /// yesterday is. A document with no readable write time is stale, because
    /// "unknown age" is not evidence of being current.
    /// </summary>
    private static bool IsStale(DateTimeOffset? updatedAt)
        => updatedAt is null || DateTimeOffset.UtcNow - updatedAt.Value > TimeSpan.FromHours(1);

    /// <summary>
    /// Renders a reading that is missing rather than zero, by desaturating it and
    /// keeping the value: the number is still the last thing the host said, and
    /// hiding it would lose more than the tint warns about.
    /// </summary>
    private void MarkStale(TextBlock block, bool stale)
        => block.Foreground = (Brush)FindResource(stale ? "StaleTintBrush" : "TextBrush");

    /// <summary>
    /// Money, to two decimals, everywhere in this window. The implementation lives in
    /// <see cref="Amounts"/> because the quota record writes money too, and a figure a
    /// person compares across two panels has to be written by one format string.
    /// </summary>
    private static string Money(double value) => Amounts.Money(value);

    private static string MoneyText(Reading reading) => reading.Value is { } value ? Money(value) : "未上报";

    private static string MoneyText(Reading reading, string missing) => reading.Value is { } value ? Money(value) : missing;

    /// <summary>
    /// The one currency a set of records may be added up in, or "" when they are not
    /// all in the same one.
    ///
    /// <para>
    /// This is the guard the whole of the currency rule rests on. The user's own
    /// sentence is the reason: 「额度单位不好统一，因为国内模型都是以人民币为单位，国外模型
    /// 有基本都是美元单位，除非是中转站才统一了单位」 — so the rule is not "write the unit
    /// once at the top of a column", it is "a total exists only inside one unit". A set
    /// that mixes them has no total, and every caller says so instead of printing one.
    /// </para>
    ///
    /// <para>
    /// A record that reported no cost does not vote: an absent cost is not a currency.
    /// A record that named none is taken to be in its own account's unit, which is the
    /// declaration the balance document carries — 「每个账户自己的币种跟着自己的数字走」 —
    /// and when nothing declares one either, such a record votes for no unit rather than
    /// for dollars. "" therefore means one of two things, and the callers tell them apart
    /// with <c>Any(v =&gt; v.Cost.HasValue)</c>: nothing was reported at all, or what was
    /// reported is not all in one unit.
    /// </para>
    /// </summary>
    /// <param name="fallback">The unit to use for a record that named none: its own
    /// account's, or "" when nothing declares one.</param>
    private static string SingleCurrency(IEnumerable<UsageEvent> events, string fallback)
    {
        var found = "";
        foreach (var value in events)
        {
            if (value.Cost is null) continue;
            var currency = EventCurrency(value, fallback);
            if (found.Length == 0) { found = currency; continue; }
            if (!string.Equals(found, currency, StringComparison.OrdinalIgnoreCase)) return "";
        }
        return found;
    }

    /// <summary>One record's unit, or its account's when it named none.</summary>
    private static string EventCurrency(UsageEvent value, string fallback)
        => !string.IsNullOrWhiteSpace(value.Currency) ? value.Currency : fallback;

    /// <summary>
    /// The unit the account in scope is billed in, off the balance document's own row for
    /// it, or "" when the document names none. This is the account's declaration rather
    /// than a default: a figure printed in a unit the account never declared is the
    /// window's claim about the account, not the account's reading.
    /// </summary>
    private string AccountCurrency(string accountId)
        => _lastBalanceUsage.Rows
            .FirstOrDefault(row => string.Equals(row.AccountId, accountId, StringComparison.OrdinalIgnoreCase))?.Currency ?? "";

    /// <summary>The same for the scope this window is in.</summary>
    private string ScopedAccountCurrency() => AccountCurrency(_accountFilter);

    private static Color PaletteColor(int index) => (index % 5) switch
    {
        0 => Color.FromRgb(45, 225, 194),
        1 => Color.FromRgb(110, 168, 255),
        2 => Color.FromRgb(181, 134, 255),
        3 => Color.FromRgb(244, 185, 66),
        _ => Color.FromRgb(255, 112, 134)
    };

    private static Brush Palette(int index) => new SolidColorBrush(PaletteColor(index));

    /// <summary>
    /// One account row's readings, decided before anything is drawn: what the row
    /// says, with the plan resolution and the cycle arithmetic already done. The quota
    /// reading is the same record the balance ruler draws — the row holds a reference
    /// to it rather than a copy of its numbers; see <see cref="QuotaReading"/> — and the
    /// recharge gauge is this plugin's own record of the account's balance; see
    /// <see cref="RechargeGauge"/>.
    /// </summary>
    private readonly record struct AccountRowData(
        string Id, string Label, bool Subscription,
        double Balance, bool HasBalance,
        double Today, bool HasToday,
        QuotaReading? Quota, RechargeGauge? Recharge, string Currency,
        int RecordCount,
        /// <summary>
        /// Which kind of row this is. <see cref="UsageAttribution.Account"/> covers the
        /// combined row too — it is drawn from the same snapshot and states a balance —
        /// while the two groups are the rows with no account behind them at all.
        /// </summary>
        UsageAttribution Attribution);

    /// <summary>
    /// The three texts one quota bar is written with. Held for the primary bar so a
    /// capture can compare what the card states with what the account row states, off
    /// the live elements rather than out of a second calculation; see
    /// <see cref="RunCapture"/>.
    /// </summary>
    private sealed record QuotaBarTexts(TextBlock Name, TextBlock Usage, TextBlock Percent);

    /// <summary>The five colours an account row paints with, read out of the window.</summary>
    private readonly record struct AccountListPalette(
        Color Text, Color Muted, Color Accent, Color Selected, Color Hover);

    /// <summary>
    /// The live parts of one account row. Holding the elements is the point: the
    /// row outlives the refresh that moves its readings, so the selection it draws
    /// can be seen to move on the elements the user is already looking at, and
    /// <see cref="Selected"/> and <see cref="Hovered"/> are the row's state rather
    /// than a value frozen into an event handler when the row was built.
    /// </summary>
    private sealed class AccountListItem
    {
        public string Id = "";
        public Border Outer = null!;
        /// <summary>The fill layer behind the row's text.</summary>
        public Border Fill = null!;
        /// <summary>The 3 px marker on the row's left edge.</summary>
        public Border Accent = null!;
        public TextBlock Name = null!;
        public TextBlock Value = null!;
        public TextBlock Bottom = null!;
        public Rectangle BarFill = null!;
        public ScaleTransform BarScale = null!;
        /// <summary>The bar's last written fraction, so a change can be told from a repaint.</summary>
        public double Fraction;
        /// <summary>False until the row has stated its first reading.</summary>
        public bool Built;
        public bool Selected;
        public bool Hovered;
    }

    // ======================================================================
    //  the ledger's own row
    // ======================================================================

    /// <summary>One generated column, in the units a Grid actually accepts. A star
    /// column carries its weight in <c>Width</c>, the floor it may not be squeezed past
    /// in <c>MinWidth</c>, and the ceiling it may not grow past in <c>MaxWidth</c>.</summary>
    private sealed record GridColumn(double Width, bool Star, double MinWidth = 0, double MaxWidth = double.PositiveInfinity);

    /// <summary>
    /// One task row, and the state its opening animation writes to.
    ///
    /// The row is a <see cref="Grid"/> that builds its own content once, in
    /// <see cref="MainWindow.BuildLedgerRow"/>, and then changes state in place —
    /// which is the whole point: the row that opens is the row that was clicked,
    /// not a panel that appears underneath it. The lanes need named handles on the
    /// card, the surface, the bar and each detail line, so the object that owns
    /// them is the object the lanes are built against.
    /// </summary>
    private sealed class LedgerRowView : Grid
    {
        private readonly MainWindow _owner;

        public LedgerRowView(EventRow row, MainWindow owner)
        {
            Row = row;
            _owner = owner;
        }

        public EventRow Row { get; }
        public bool IsOpen { get; set; }
        public Border Card { get; private set; } = null!;
        /// <summary>The row's own content box: the card, less the inset it keeps.</summary>
        public Border Inner { get; private set; } = null!;
        public Border Surface { get; private set; } = null!;
        public Rectangle Stripe { get; private set; } = null!;
        public SolidColorBrush RowBrush { get; private set; } = null!;
        public Grid Details { get; private set; } = null!;
        /// <summary>The 模型 cell, whose tooltip is the one a capture photographs. See
        /// <see cref="MainWindow.OpenLedgerToolTip"/>.</summary>
        public TextBlock? ModelCell { get; set; }
        /// <summary>
        /// The 模型 cell of each sub-request line, in order, filled when the panel is
        /// built. A sub-request's model is the one value in the ledger that can arrive
        /// empty — a relay that logged the request without naming a model — so this is
        /// where the empty tooltip the report is about came from.
        /// </summary>
        public List<TextBlock> DetailModelCells { get; } = new();
        /// <summary>
        /// Fills <see cref="Details"/> with this row's sub-request lines. Set once,
        /// when the row is built, and run at most once: on the first opening. It is a
        /// callback rather than a list because building the panel is the expensive
        /// half of a row and a closed row draws none of it — see
        /// <see cref="MainWindow.BuildDetailLines"/>.
        /// </summary>
        public Action? DetailBuilder { get; set; }
        /// <summary>Where the built lines are kept between openings, empty until then.</summary>
        private IReadOnlyList<FrameworkElement> _lines = Array.Empty<FrameworkElement>();
        public double ClosedHeight => HeaderHeight;
        public double OpenHeight { get; private set; } = HeaderHeight;
        /// <summary>The width the panel is measured at: the row's, less its own inset.</summary>
        private double _contentWidth = LedgerMinWidth - 2 * SurfaceInset;

        public void Attach(Border card, Border inner, Border surface, Rectangle stripe, SolidColorBrush rowBrush, Grid details, double contentWidth)
        {
            Card = card;
            Inner = inner;
            Surface = surface;
            Stripe = stripe;
            RowBrush = rowBrush;
            Details = details;
            _contentWidth = contentWidth;
        }

        /// <summary>
        /// The width the list has now.
        ///
        /// <para>
        /// A row is generated once, and a resize does not re-make it: without this
        /// the columns would keep the width the window had when the row was
        /// realized, which is exactly the report — widen the window and the layout
        /// does not move. A row that is open is measured again, because its detail
        /// panel was wrapped to the width it had when it was built.
        /// </para>
        /// </summary>
        public void Resize(double cardWidth, double contentWidth)
        {
            if (Math.Abs(Card.Width - cardWidth) > 0.5) Card.Width = cardWidth;
            if (Math.Abs(_contentWidth - contentWidth) < 0.5) return;
            _contentWidth = contentWidth;
            Inner.Width = contentWidth;
            if (_lines.Count > 0) MeasureOpenHeight();
        }

        public LedgerRowView WithContent(UIElement content)
        {
            Children.Add(content);
            return this;
        }

        /// <summary>
        /// The lines the opening and closing lanes write to, and the reason they have
        /// to be asked for rather than read: a row that has never been opened has
        /// none. This is where the panel is built, where it is measured, and the only
        /// place either happens — both used to run for every realized row, on every
        /// rebuild of the ledger, for a gesture most rows never take part in.
        /// </summary>
        public IReadOnlyList<FrameworkElement> EnsureDetails()
        {
            if (_lines.Count > 0 || DetailBuilder is null) return _lines;
            DetailBuilder();
            DetailBuilder = null;
            _lines = Details.Children.OfType<FrameworkElement>().ToArray();
            MeasureOpenHeight();
            return _lines;
        }

        /// <summary>
        /// The height the row opens to: the header plus the detail panel's own
        /// measured height, and nothing else. Measured at the width the row is
        /// actually laid out in — measuring a Grid whose model column is a star
        /// against an infinite width collapses that column to nothing and
        /// under-reports the height the wrapped text will need.
        ///
        /// <para>
        /// It used to add 2 × <see cref="SurfaceInset"/> "for the padding the
        /// surface lane grows, because the panel is inside it". The panel is not
        /// inside it — the surface is a sibling of the content — and the padding was
        /// never applied to anything, so the row opened 28 px taller than what it
        /// held: the band of empty card an expanded row ended in. The panel now
        /// carries that inset itself, which is why the sum below is the panel's own
        /// measured height and nothing else.
        /// </para>
        /// </summary>
        private void MeasureOpenHeight()
        {
            // The panel is measured on its own, never the row: measuring the row
            // writes its own ActualHeight, and the lane that is about to animate that
            // height would then be starting from a value the layout had already
            // fixed at the closed one.
            Details.Measure(new Size(Math.Max(0, _contentWidth), double.PositiveInfinity));
            OpenHeight = HeaderHeight + Details.DesiredSize.Height;
        }
    }

    /// <summary>A row of the account table.</summary>
    private sealed class AccountRow
    {
        public AccountRow(string name, Reading balance, Reading today, Reading recent, string currency, bool subscription, bool stale, bool selected)
        {
            Name = selected ? $"{name} · 当前" : name;
            // The unit the three figures beside it are in. Empty — a document that named
            // no currency for this account — is stated as a dash rather than as USD: a
            // default unit is a unit nobody declared. See REDESIGN-NOTES 第六轮 §3.
            Currency = currency.Length > 0 ? currency : "—";
            BalanceText = balance.Value is { } value ? Money(value) : "未上报";
            BalanceStyle = StyleFor(balance.Value is not null && !stale);
            TodayText = today.Value is { } day ? Money(day) : "未上报";
            TodayStyle = StyleFor(today.Value is not null && !stale);
            RecentText = recent.Value is { } span ? Money(span) : "未上报";
            RecentStyle = StyleFor(recent.Value is not null && !stale);
            BillingText = subscription ? "订阅制" : "API · 按量";
            BillingBrush = subscription
                ? (Brush)Application.Current.FindResource("FlowRateBrush")
                : (Brush)Application.Current.FindResource("MutedBrush");
        }

        public string Name { get; }
        public string Currency { get; }
        public string BalanceText { get; }
        public string TodayText { get; }
        public string RecentText { get; }
        public string BillingText { get; }
        public Brush BillingBrush { get; }
        public Style BalanceStyle { get; }
        public Style TodayStyle { get; }
        public Style RecentStyle { get; }

        /// <summary>
        /// A measured figure and a figure nobody measured are the same word on
        /// screen only if they are the same word in the style: unreported is the
        /// stale tint, so a column of 未上报 reads as one thing.
        /// </summary>
        private static Style StyleFor(bool reported)
            => (Style)Application.Current.FindResource(reported ? "FigureCell" : "UnreportedCell");
    }

    /// <summary>A row of the model table. Every column is a reading, so every column
    /// can say 未上报 instead of 0.</summary>
    private sealed class ModelRow
    {
        public ModelRow(string name, int requests, Reading input, Reading output, Reading cost,
            string currency, bool costReported, Brush brush, double percent, bool shareKnown)
        {
            Name = name;
            RequestText = requests.ToString("N0");
            InputText = Token(input);
            OutputText = Token(output);
            // The unit travels with the number rather than sitting over the column:
            // 「不要把币种塞进表头统一显示」, because the next account's models are
            // billed in another one. A row whose own records are not all in one unit has
            // no total, and says that instead of adding them.
            CostText = cost.Value is { } value ? $"{value:0.###} {currency}"
                : costReported ? "多币种" : "未上报";
            InputStyle = StyleFor(input);
            OutputStyle = StyleFor(output);
            CostStyle = StyleFor(cost);
            Brush = brush;
            // Same rule in this column as in the other four: a share nobody reported
            // is 未上报, and its gauge is not drawn at all rather than drawn empty —
            // an empty bar beside a number reads as zero whatever the number says.
            ShareText = shareKnown ? $"{percent:0.#}%" : "未上报";
            ShareStyle = StyleFor(shareKnown ? Reading.Reported(percent) : Reading.Missing());
            BarVisibility = shareKnown ? Visibility.Visible : Visibility.Collapsed;
            // The share bar is drawn as a width rather than a ProgressBar so it can
            // share the row's own baseline with the number beside it.
            BarWidth = Math.Max(0.6, percent);
        }

        public string Name { get; }
        public string RequestText { get; }
        public string InputText { get; }
        public string OutputText { get; }
        public string CostText { get; }
        public string ShareText { get; }
        public double BarWidth { get; }
        public Brush Brush { get; }
        public Style InputStyle { get; }
        public Style OutputStyle { get; }
        public Style CostStyle { get; }
        public Style ShareStyle { get; }
        public Visibility BarVisibility { get; }

        private static Style StyleFor(Reading reading)
            => (Style)Application.Current.FindResource(reading.Value is null ? "UnreportedCell" : "FigureCell");
    }

    private sealed class ProviderRow
    {
        public ProviderRow(string name, IReadOnlyList<UsageEvent> events, int totalEvents, Brush brush)
        {
            Name = name; Brush = brush;
            RequestText = $"{events.Count:N0} 次请求 · 成功 {events.Count(value => value.Success == true):N0}";
            var tokens = events.Sum(value => (value.InputTokens ?? 0) + (value.OutputTokens ?? 0));
            TokenText = tokens > 0 ? $"{UsageFormatting.Tokens(tokens)} Token" : "Token 未上报";
            ShareText = totalEvents == 0 ? "—" : $"{events.Count * 100d / totalEvents:0.#}%";
        }
        public string Name { get; }
        public string RequestText { get; }
        public string TokenText { get; }
        public string ShareText { get; }
        public Brush Brush { get; }
    }

    /// <summary>
    /// One task, as the ledger row and the detail view both read it. Field for
    /// field the same <see cref="UsageEvent"/> the store produced: nothing here
    /// reads a file, and nothing here decides what the data means — the difference
    /// between a measured 0 and an absent reading is carried by
    /// <see cref="Reading"/>, and the rest is formatting.
    /// </summary>
    private sealed class EventRow
    {
        private readonly UsageEvent _event;
        /// <summary>
        /// The currency the record's own account declares, for the records that declare
        /// none.
        ///
        /// <para>
        /// A client that reports a cost usually reports no unit, and a blanket "USD" for
        /// that is the window stating the account's unit instead of the account — the
        /// mistake the user named: 「国内模型都是以人民币为单位，国外模型有基本都是美元单位」.
        /// The account's own unit comes from the balance document, which is the only
        /// thing that declares one, and an account that declares none gets a bare figure
        /// rather than a unit nobody chose.
        /// </para>
        /// </summary>
        private readonly string _accountCurrency;

        public EventRow(UsageEvent value, IReadOnlyDictionary<string, AccountDirectory.AccountInfo> accounts, string accountCurrency = "")
        {
            _event = value;
            _accountCurrency = accountCurrency;
            Details = value.Details.Select(detail => new DetailRow(detail, accountCurrency)).ToArray();
            var accountId = value.AccountId;
            // The two groups are named in the account column rather than left blank or
            // shown as a bare id. A blank cell reads as a fault in the row, and a bare
            // id reads as an account somebody named badly — neither says the one thing
            // that is true of the record, which is that its account is not in the list.
            if (string.IsNullOrWhiteSpace(accountId))
            {
                Attribution = UsageAttribution.Unattributed;
                AccountLabel = UsageGroups.UnattributedLabel;
                return;
            }
            if (accounts.TryGetValue(accountId, out var account))
            {
                Attribution = UsageAttribution.Account;
                AccountLabel = account.Name;
                IsOfficialAccount = account.IsOfficial;
                // Either the user declared the account subscription-billed, or the
                // client said so itself for this session. The two are independent:
                // the declaration covers subscriptions whose client never reports a
                // plan, and a reported plan covers accounts the user never labelled.
                IsSubscriptionAccount = account.IsSubscription || value.PlanType.Length > 0;
                return;
            }
            Attribution = UsageAttribution.Archived;
            AccountLabel = $"{UsageGroups.ArchivedLabel} · {AccountDirectory.Label(accountId)}";
        }
        /// <summary>Which group this record's account id puts it in; see <see cref="UsageGroups"/>.</summary>
        public UsageAttribution Attribution { get; }
        /// <summary>
        /// Whether the account billed for this task is a vendor's own API rather than
        /// a relay. Decides how an absent cost reads; see the cost text below.
        /// </summary>
        public bool IsOfficialAccount { get; }
        /// <summary>
        /// Whether the user declared this account subscription-billed. Takes priority
        /// over the official-account reading: a subscription has no per-request price
        /// at all, so "no cost" is the expected state rather than a missing reading.
        /// </summary>
        public bool IsSubscriptionAccount { get; }
        public string OccurredAtText => _event.OccurredAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
        public string Provider => _event.Provider;
        /// <summary>Account the task billed to, empty when the host did not say.</summary>
        /// <remarks>
        /// Initialised because the constructor returns early when the host reported no
        /// account at all, and <see cref="ProviderLine"/> and <see cref="HasAccount"/>
        /// both read this without a null check.
        /// </remarks>
        public string AccountLabel { get; } = "";
        /// <summary>Client and account together: which tool ran, on whose key.</summary>
        public string ProviderLine => AccountLabel.Length == 0 ? Provider : $"{Provider} · {AccountLabel}";
        public bool HasAccount => AccountLabel.Length > 0;
        /// <summary>
        /// A balance-derived cost is the whole task's spend, not a per-request
        /// charge, so it is labelled differently and gets a different detail view.
        /// </summary>
        public bool IsBalanceDelta => string.Equals(_event.CostSource, "balance-delta", StringComparison.Ordinal);
        public string CostSourceLabel => _event.CostSource switch
        {
            "relay-log" => "中转站逐次",
            "balance-delta" => "余额变化",
            "client" => "客户端上报",
            _ => ""
        };
        public bool HasCostSource => CostSourceLabel.Length > 0;
        public string CostCaption => IsBalanceDelta ? "任务总消耗" : "消耗额度";
        /// <summary>
        /// A balance-derived figure is a real charge but not a per-request one, so
        /// the detail view says where it came from instead of implying the
        /// precision of a matched relay record.
        /// </summary>
        public string CostNote => IsBalanceDelta
            ? "账户余额在本次任务期间的变化，非逐次计费"
            : DetailsCaption;
        public string Model => string.IsNullOrWhiteSpace(_event.Model) ? "模型未上报" : _event.Model;
        public string ReasoningText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_event.ReasoningEffort)) return $"思考强度 {_event.ReasoningEffort}";
                var values = _event.Details.Select(detail => detail.ReasoningEffort)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return values.Length switch { 1 => $"思考强度 {values[0]}", > 1 => "思考强度 多种", _ => "思考强度 未上报" };
            }
        }
        /// <summary>The effort in one column's worth of text. A dash, not a zero.</summary>
        public string ReasoningShort
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_event.ReasoningEffort)) return _event.ReasoningEffort;
                var values = _event.Details.Select(detail => detail.ReasoningEffort)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return values.Length switch { 1 => values[0], > 1 => "多种", _ => "—" };
            }
        }
        public string InputText => _event.InputTokens is null ? "输入给模型 —" : $"输入给模型 {UsageFormatting.Tokens(_event.InputTokens.Value)}";
        public string OutputText => _event.OutputTokens is null ? "模型输出 —" : $"模型输出 {UsageFormatting.Tokens(_event.OutputTokens.Value)}";
        public string CacheReadText => _event.CacheReadTokens is null ? "缓存读取 —" : $"缓存读取 {UsageFormatting.Tokens(_event.CacheReadTokens.Value)}";
        public string CacheWriteText => _event.CacheWriteTokens is null ? "缓存写入 —" : $"缓存写入 {UsageFormatting.Tokens(_event.CacheWriteTokens.Value)}";
        // "Unreported" means two different things. A relay has a billing log that
        // either matched or did not, so an absent cost is a failed lookup. An official
        // account has no such log, and its only signal is the balance drop a task
        // caused, so an absent cost usually just means the balance did not move.
        public string CostText => _event.Cost is null
            ? (IsSubscriptionAccount ? "订阅制 · 不计费" : IsOfficialAccount ? "余额未变化" : "未上报")
            : $"{_event.Cost:0.########} {(_event.Currency.Length == 0 ? "USD" : _event.Currency)}";
        public string DurationText => _event.DurationMs is null ? "耗时 —" : $"耗时 {UsageFormatting.Milliseconds(_event.DurationMs)}";
        // The ledger's 耗时 cell: the same reading as DurationText without the word in
        // front of it, because the column head already carries the word and a column of
        // "耗时 6.38 min" under a head saying 耗时 is the word said twice. An absent
        // duration is an em dash, like every other figure the event does not carry —
        // never "0", which would be a number the host never sent.
        public string DurationShort => _event.DurationMs is null ? "—" : UsageFormatting.Milliseconds(_event.DurationMs);
        public string DetailsCaption => Details.Count == 0 ? "查看中转站请求明细 · 暂无可关联记录" : $"查看本次任务的 {Details.Count} 次中转站请求";
        public string Currency => !string.IsNullOrWhiteSpace(_event.Currency) ? _event.Currency : _accountCurrency;
        public string EventId => _event.EventId;
        public long InputTokensValue => _event.InputTokens ?? _event.Details.Sum(detail => detail.InputTokens ?? 0);
        public long OutputTokensValue => _event.OutputTokens ?? _event.Details.Sum(detail => detail.OutputTokens ?? 0);
        public long CacheReadTokensValue => _event.CacheReadTokens ?? _event.Details.Sum(detail => detail.CacheReadTokens ?? 0);
        public long CacheWriteTokensValue => _event.CacheWriteTokens ?? 0;

        /// <summary>The task's own readings, as readings rather than as numbers.</summary>
        public Reading InputTokens => _event.InputTokens is { } value ? Reading.Reported(value) : Reading.Missing();
        public Reading OutputTokens => _event.OutputTokens is { } value ? Reading.Reported(value) : Reading.Missing();
        public Reading Cost => _event.Cost is { } value ? Reading.Reported(value) : Reading.Missing();
        /// <summary>Steps and tool calls are counts the task carried; a dash when it carried none.</summary>
        public Reading Steps => _event.Steps is { } value ? Reading.Reported(value) : Reading.Missing();
        public Reading ToolCalls => _event.ToolCalls is { } value ? Reading.Reported(value) : Reading.Missing();
        /// <summary>
        /// The sum of the sub-requests' costs, or null when none of them reported
        /// one. Null is not 0: a task whose details carry no price has no total,
        /// and printing 0.00 there would claim the sub-requests were free.
        /// </summary>
        public double? DetailCostSum => Details.Any(detail => detail.CostValue.HasValue)
            ? Details.Where(detail => detail.CostValue.HasValue).Sum(detail => detail.CostValue!.Value)
            : null;
        public IReadOnlyList<DetailRow> Details { get; }
        public string StatusText => _event.Success switch { true => "成功", false => "失败", _ => "未知" };
        public string StepsToolsText
            => $"步骤 {(_event.Steps is { } steps ? steps.ToString("0") : "—")} · 工具调用 {(_event.ToolCalls is { } calls ? calls.ToString("0") : "—")}";
        public Brush StatusBrush => _event.Success switch
        {
            true => new SolidColorBrush(Color.FromRgb(45, 225, 194)),
            false => new SolidColorBrush(Color.FromRgb(255, 112, 134)),
            _ => (Brush)Application.Current.FindResource("MutedBrush")
        };

        public sealed class DetailRow(UsageEventDetail value, string accountCurrency = "")
        {
            public string TimeText => value.OccurredAt.ToLocalTime().ToString("HH:mm:ss");
            public string ModelEffortText => ModelEffort();
            public string InputText => value.InputTokens is null ? "输入 —" : $"输入 {UsageFormatting.Tokens(value.InputTokens.Value)}";
            public string OutputText => value.OutputTokens is null ? "输出 —" : $"输出 {UsageFormatting.Tokens(value.OutputTokens.Value)}";
            public string CacheText => value.CacheReadTokens is null ? "缓存 —" : $"缓存 {UsageFormatting.Tokens(value.CacheReadTokens.Value)}";
            public string CostText => value.Cost is null ? "费用未上报" : $"{value.Cost:0.########} {Currency}".TrimEnd();
            public string Model => value.Model;
            public string Effort => value.ReasoningEffort;
            /// <summary>The sub-request's own unit, or its account's when it named none.</summary>
            public string Currency => !string.IsNullOrWhiteSpace(value.Currency) ? value.Currency : accountCurrency;
            public double? CostValue => value.Cost;
            public long? InputValue => value.InputTokens;
            public long? OutputValue => value.OutputTokens;
            public long? CacheValue => value.CacheReadTokens;
            public Reading Input => value.InputTokens is { } input ? Reading.Reported(input) : Reading.Missing();
            public Reading Output => value.OutputTokens is { } output ? Reading.Reported(output) : Reading.Missing();
            public Reading CacheRead => value.CacheReadTokens is { } cache ? Reading.Reported(cache) : Reading.Missing();
            public Reading Cost => value.Cost is { } cost ? Reading.Reported(cost) : Reading.Missing();
            public bool HasTokenData => value.InputTokens.HasValue || value.OutputTokens.HasValue || value.CacheReadTokens.HasValue;

            /// <summary>
            /// The model and the effort it ran at, or the one thing that is true of a
            /// sub-request that reported neither. This string is also the cell's tooltip in
            /// the detail view, and a tooltip with nothing in it is a box with nothing in
            /// it — which is half of what the user photographed. It never returns an empty
            /// string; see MainWindow.Tip.
            /// </summary>
            private string ModelEffort() => (string.IsNullOrWhiteSpace(value.Model), string.IsNullOrWhiteSpace(value.ReasoningEffort)) switch
            {
                (true, true) => "模型未上报",
                (true, false) => $"模型未上报 · {value.ReasoningEffort}",
                (false, true) => value.Model,
                _ => $"{value.Model} · {value.ReasoningEffort}",
            };
        }
    }

    // ======================================================================
    //  trend point, buckets, ranges
    // ======================================================================

    private sealed record TrendPoint(
        DateTime Date,
        string Label,
        long InputTokens,
        long OutputTokens,
        long CacheCreationTokens,
        long CacheReadTokens,
        double CacheHitRate,
        int Requests)
    {
        public long MaxTokenSeries => Math.Max(Math.Max(InputTokens, OutputTokens), Math.Max(CacheCreationTokens, CacheReadTokens));
        public bool HasTokenData => MaxTokenSeries > 0;
    }

    private sealed record TrendBucket(DateTime Start, DateTime End, string Label);

    private enum TrendRange
    {
        Last24Hours,
        Last7Days,
        Last30Days,
        Last90Days,
        AllTime
    }

    // ======================================================================
    //  the expand parameters, in one block
    // ======================================================================

    /// <summary>One line when closed, so the ledger is a table rather than a list.</summary>
    private const double HeaderHeight = 34;

    /// <summary>The inset the surface grows into while the row opens.</summary>
    private const double SurfaceInset = 14;

    private const double RowRadiusOpen = 10;
    private const double RowStripeWidth = 2;
    private const double DetailRise = 6;

    // 0-200 ms: the container's height, and the row's own body changing state with
    // it. See BuildOpenLanes for why these two share a duration.
    private const double ExpandHeightMs = 200;
    // 40-360 ms: the surface's outline and inset, on BackEase for the overshoot.
    private const double ExpandSurfaceAt = 40;
    private const double ExpandSurfaceMs = 320;
    // 60 ms on, 30 ms apart, 180 ms each: the detail lines. 60 rather than 120
    // because the height used to finish with nothing inside it.
    private const double ExpandDetailsAt = 60;
    private const double ExpandDetailMs = 180;
    private const double ExpandDetailStagger = 30;

    // Closing plays the same motion backwards and a little quicker.
    private const double CollapseHeightMs = 190;
    private const double CollapseSurfaceMs = 230;
    private const double CollapseDetailMs = 110;
    private const double CollapseDetailStagger = 22;

    // A new reading rolls in and its tint decays. Kept beside the ledger's own
    // parameters because they are one language: what moves here is a strip of type,
    // not a layout.
    private const double ReadingRollMs = 420;
    private const double ReadingDecayMs = 900;
    /// <summary>Two rolls closer together than this are one roll: the newest reading
    /// is drawn, and the gesture that says so is not repeated.</summary>
    private static readonly TimeSpan ReadingMotionDebounce = TimeSpan.FromSeconds(12);

    // ======================================================================
    //  reading one figure back, for a capture's log
    // ======================================================================

    /// <summary>How far a figure is displaced at the moment of the shot.</summary>
    private static double Rise(TextBlock figure) => figure.RenderTransform is TranslateTransform shift ? shift.Y : 0;

    /// <summary>The colour a figure is drawn in at the moment of the shot, so the
    /// frame a PNG claims to be is a frame the log also describes.</summary>
    private static string Colour(TextBlock figure)
        => figure.Foreground is SolidColorBrush brush ? brush.Color.ToString() : "—";

    // ======================================================================
    //  timing probes: the segmented table in REDESIGN-NOTES, measured
    // ======================================================================

    /// <summary>
    /// Drives the two clicks this plugin was reported to be slow on — a client
    /// chip and an account in the list — and prints one line per phase and per
    /// repeat. Nothing here reads a file or sets state directly: every repeat goes
    /// through the same handler the mouse goes through, so the numbers describe the
    /// gesture the user made rather than a shortcut around it.
    ///
    /// <para>
    /// It exists because the report was counter-intuitive — fewer rows, longer
    /// wait — and a counter-intuitive report is exactly the kind that invites a
    /// plausible fix. The cost has to be attributed to a phase before anything is
    /// changed, and afterwards the same run has to say whether it moved.
    /// </para>
    /// </summary>
    private void RunProbe()
    {
        // The same opener the capture path uses, so a run that is both cannot end up with
        // two writers on one file — the second one truncating what the first wrote.
        OpenCaptureLog();
        ProbeLine($"probe: data-dir={_store.DirectoryPath}");
        ProbeLine($"probe: events total={_lastEvents.Count}"
            + $"  all={_lastEvents.Count}"
            + $"  dsh={_lastEvents.Count(value => value.Provider == "DeepSeek Harness")}"
            + $"  codex={_lastEvents.Count(value => value.Provider == "Codex")}"
            + $"  claude={_lastEvents.Count(value => value.Provider == "Claude")}"
            + $"  realized={EventsList.Items.Count}");
        _probeWatch = new Stopwatch();
        var pass = 0;
        var wanted = _capture.ProbePasses;
        // The ledger has to be the page that is showing before a chip is clicked.
        // A collapsed panel has no size, so a virtualizing list inside one realizes
        // no containers at all — and a probe run on the dashboard would then measure
        // a click that rebuilt nothing and report it as a click that cost nothing.
        if (HistoryScrollViewer.Visibility != Visibility.Visible)
        {
            OnHistoryNavClick(this, new RoutedEventArgs());
            PumpSmall();
        }
        Next();

        void Next()
        {
            if (pass++ >= wanted)
            {
                RunAccountSwitchProbe();
                return;
            }
            ProbeProviderPass(() =>
            {
                // Back to every client between passes, so the pass that follows
                // starts from the same state this one did.
                var reset = (Button?)ProviderFilterPanel.Children
                    .OfType<Button>()
                    .FirstOrDefault(chip => (chip.Tag as string)?.Length == 0);
                if (reset is not null) DrillResolve(reset);
                Next();
            });
        }
    }

    /// <summary>One chip, one phase per line, repeated so a single warm frame cannot
    /// be read as the cost. The chip that is clicked is the one on screen: the probe
    /// looks it up by label, so a chip that is missing is reported rather than
    /// skipped.</summary>
    /// <summary>One chip's own label, which is also how the probe finds it.</summary>
    private static string ChipLabel(Button chip)
        => ((StackPanel)chip.Content).Children[0] is TextBlock label ? label.Text : "?";

    private void ProbeProviderPass(Action finished)
    {
        var chips = ProviderFilterPanel.Children.OfType<Button>().ToArray();
        ProbeLine("probe: chips=" + string.Join(", ", chips.Select(ChipLabel)));
        var wanted = new[] { "全部", "DeepSeek Harness", "Codex", "Claude" };
        var index = 0;
        Step();

        void Step()
        {
            if (index >= wanted.Length) { finished(); return; }
            var name = wanted[index++];
            var chip = chips.FirstOrDefault(candidate => string.Equals(ChipLabel(candidate), name, StringComparison.Ordinal));
            if (chip is null)
            {
                ProbeLine($"probe: no chip for {name}");
                Step();
                return;
            }
            DrillResolve(chip, Step);
        }
    }

    /// <summary>
    /// Resolves <paramref name="chip"/> to its own ledger, whichever chip that is:
    /// the "all" chip while the probe is walking the bar, and the chip that is
    /// already in the bar once the pass is being timed. Both are one call to
    /// <see cref="RunProviderFilterClick"/> — a probe that special-cased the second
    /// one would measure a path the mouse cannot take.
    /// </summary>
    private void DrillResolve(Button chip, Action? then = null)
    {
        if (!string.Equals(chip.Tag as string, _providerFilter, StringComparison.Ordinal))
        {
            PumpSmall();
            ClickChip(chip, then);
            return;
        }
        if (then is null) return;
        PumpSmall();
        then();
    }

    /// <summary>A chip click, measured from the handler's first line to the frame
    /// that follows it — the same wait the person who clicked it waits for.</summary>
    private void ClickChip(Button chip, Action? then)
    {
        var label = ChipLabel(chip);
        PumpSmall();
        _probeWatch!.Restart();
        RunProviderFilterClick(chip, () =>
        {
            var read = _probeReadMs;
            var report = _probeReportMs;
            var panels = _probePanelsMs;
            var rowsAt = _probeRowsMs;
            var refresh = _probeRefreshMs;
            var rebuilt = _probeRebuiltRows;
            var details = _probeRebuiltDetails;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                EventsList.UpdateLayout();
                var atPaint = _probeWatch.Elapsed.TotalMilliseconds;
                // Read off the chip rather than off the filter state: a chip
                // whose brush never arrived at the new colour is a chip whose
                // roll did not run, and that is the one thing a table of
                // milliseconds cannot show.
                var brush = chip.Background as SolidColorBrush;
                ProbeLine(MeasureLine("chip", label, EventsList.Items.Count, rebuilt, details,
                    read, report, panels, rowsAt, refresh, atPaint,
                    $"chipColour=#{brush?.Color.R:X2}{brush?.Color.G:X2}{brush?.Color.B:X2} chipAnimating={brush?.HasAnimatedProperties}"));
                _probeWatch.Stop();
                then?.Invoke();
            }), DispatcherPriority.Render);
        });
    }

    /// <summary>
    /// One measured click, in one line and in one shape: every number is the elapsed
    /// time at the end of a stage, read off the same stopwatch the click started, so
    /// the stages are comparable with each other and with a later run of the same
    /// probe. A table is only a table if every row is the same sentence.
    /// </summary>
    private static string MeasureLine(
        string kind, string label, int rows, int rebuilt, int details,
        double read, double report, double panels, double rowsAt,
        double refresh, double paint, string tail)
        => $"{kind}={label,-16} rows={rows,3} rebuilt={rebuilt,3} subreq={details,5}"
            + $" read={read:0.0} report={report:0.0} panels={panels:0.0}"
            + $" rows={rowsAt:0.0} refresh={refresh:0.0} paint={paint:0.0} {tail}";

    /// <summary>
    /// The account list, one account per click, the same way. The rows are the
    /// window's own; the combined row is included because it is the one click that
    /// widens the scope rather than narrowing it.
    /// </summary>
    private void RunAccountSwitchProbe()
    {
        ProbeLine($"probe: navigate dashboard={ContentScrollViewer.Visibility} ledger={HistoryScrollViewer.Visibility}");
        var ids = _accountRowOrder.ToArray();
        ProbeLine("probe: accounts=" + string.Join(", ", ids.Select(id => AccountDirectory.Label(_accounts, id))));
        // The dashboard first, then the ledger — and the ledger is what a person
        // reported the freeze on, so it is the pass that matters. Both passes click
        // the same rows through the same handler; only the page under them differs,
        // and that difference is the point: the same click costs ten times as much
        // with the ledger showing, which is what makes the ledger the suspect.
        ProbeAccountPass(ids, () =>
        {
            OnHistoryNavClick(this, new RoutedEventArgs());
            PumpSmall();
            ProbeAccountPass(ids, FinishProbe);
        });
    }

    /// <summary>Ends a probe run. Deferred to idle so the last measurement is
    /// printed before the window goes away.</summary>
    private static void FinishProbe()
        => Application.Current.Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()),
            DispatcherPriority.ApplicationIdle);

    private void ProbeAccountPass(IReadOnlyList<string> ids, Action finished)
    {
        var index = 0;
        Step();

        void Step()
        {
            if (index >= ids.Count) { finished(); return; }
            var id = ids[index++];
            var row = _accountRows[id];
            if (string.Equals(id, _accountFilter, StringComparison.Ordinal))
            {
                ProbeLine($"probe: account={AccountDirectory.Label(_accounts, id)} already current; skipped");
                Step();
                return;
            }
            PumpSmall();
            _probeWatch!.Restart();
            RunSelectAccountFilter(id, () =>
            {
                var read = _probeReadMs;
                var report = _probeReportMs;
                var panels = _probePanelsMs;
                var rowsAt = _probeRowsMs;
                var refresh = _probeRefreshMs;
                var rebuilt = _probeRebuiltRows;
                var details = _probeRebuiltDetails;
                var page = HistoryScrollViewer.Visibility == Visibility.Visible ? "usage" : "dashboard";
                ProbeLine($"probe: state page={page} account={_accountFilter}"
                    + $" provider={(_providerFilter.Length == 0 ? "(全部)" : _providerFilter)}"
                    + $" lastEvents={_lastEvents.Count} visible={VisibleEvents(_lastEvents).Count} rows={EventsList.Items.Count}");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateLayout();
                    var atPaint = _probeWatch.Elapsed.TotalMilliseconds;
                    _probeWatch.Stop();
                    ProbeLine(MeasureLine("account", AccountDirectory.Label(_accounts, id),
                        EventsList.Items.Count, rebuilt, details,
                        read, report, panels, rowsAt, refresh, atPaint, $"page={page}"));
                    Step();
                }), DispatcherPriority.Render);
            });
        }
    }

    /// <summary>Lets the two transitions a probe starts finish before the next one
    /// begins, so no repeat is measured on top of the previous one's animation. It also
    /// clears the two rebuild counters, which is what lets the measured click be the
    /// only click in them: a warm-up click builds rows too, and those must not be
    /// reported as the measured click's work.</summary>
    private void PumpSmall()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(ChromeTransition.TotalMilliseconds + 220), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        _probeRebuiltRows = 0;
        _probeRebuiltDetails = 0;
    }

    // ======================================================================
    //  capture: a window that can be photographed on a named frame
    // ======================================================================

    /// <summary>
    /// Renders the window to a PNG, then exits. Two routes, because one of them
    /// does not always work: <c>PrintWindow</c> returns the composited window, and
    /// DWM keeps no pixels for a window that lives outside the work area, so a
    /// capture that comes back one flat colour is retried straight off the visual
    /// tree. The content is identical either way; the difference is whether the
    /// system's own shadow is in the frame.
    /// </summary>
    private void ApplyCaptureRequest()
    {
        if (_captureApplied) return;
        if (!_capture.IsCapture && !_capture.Probe) return;
        _captureApplied = true;
        // The capture is taken from the rendered frame, not from the command line's
        // idea of it, so the shot is whatever the window has actually drawn.
        Dispatcher.BeginInvoke(new Action(RunCapture), DispatcherPriority.ApplicationIdle);
    }

    private void RunCapture()
    {
        OpenCaptureLog();
        if (_capture.Probe)
        {
            RunProbe();
            return;
        }
        if (_capture.Page == CapturePage.Ledger) OnHistoryNavClick(this, new RoutedEventArgs());
        UpdateLayout();
        // Realize the rows before asking for one: a container that has not been
        // generated yet has no row in it to open.
        EventsList.UpdateLayout();
        GenerateRows();
        UpdateLayout();
        if (_capture.ExpandRow is { } index) SelectLedgerRow(index);
        UpdateLayout();
        // Last, because the head is placed where the rows are and this is the last
        // moment the ledger's geometry is settled before the pixels are taken.
        AlignLedgerHead();
        UpdateLayout();

        // The hover frame, if this run is one. Opened here, after the rows are realized
        // and the head is placed, so the cell it hangs off is the cell in the frame.
        if (_capture.ToolTipRow is { } toolTipRow) OpenLedgerToolTip(toolTipRow, _capture.ToolTipDetail);

        // The three things a person does with the mouse, done the way the mouse does
        // them: an account click runs SelectAccountFilter, a filter chip and the quota
        // fold run their own Click handlers. A capture that set the state directly
        // would photograph a window no click can produce.
        if (_capture.SelectAccount is { } account)
        {
            SelectAccountFilter(account);
            UpdateLayout();
        }
        if (_capture.SelectProvider is { } provider)
        {
            ClickProviderChip(provider);
            UpdateLayout();
        }
        if (_capture.ExpandQuota)
        {
            _quotaToggle?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            UpdateLayout();
        }
        // The archive's ceiling, moved the way the user moves it: the control's own
        // selection changes, which is what runs the handler that writes the plugin's
        // file. Setting the store here would photograph a number no click produces.
        if (_capture.ArchiveLimitStep != 0)
        {
            StepArchiveLimit(_capture.ArchiveLimitStep);
            UpdateLayout();
        }
        // Those clicks start transitions on the framework's own clock, so a frame
        // taken the instant after one would photograph the state the window just
        // left. This pumps the dispatcher until they have run, which is also what
        // lets a quota bar that is being seen for the first time reach its value.
        PumpFor((int)ChromeTransition.TotalMilliseconds + 450);


        // A capture of a frame in the middle of an opening is taken on the opening's
        // own clock: the lanes are seeked to the instant the command line names, so
        // the filename and the pixels describe the same millisecond. The live board
        // is stopped first — two boards writing one property is a race, not a frame.
        if (_capture.At is { } at && _openRow is { } row)
        {
            _expandBoard?.Stop();
            var board = new MotionBoard();
            BuildOpenLanes(board, row);
            board.Seek(at);
            ProbeLine($"capture: expand t={at:0} ms  {board.Sample(at)}");
            ProbeLine($"capture: expand lanes{Environment.NewLine}{board.Range(ExpandHeightMs + ExpandSurfaceMs)}");
            ProbeLine($"capture: overshoot of the surface curve = {MotionBoard.Overshoot(0.25) * 100:0.#}%");
        }
        UpdateLayout();
        // The fold is the one gesture here that a still has to be told the instant
        // of, so -QuotaFoldAt holds it the way --at holds a task row.
        if (_quotaFold is { } quotaFold)
        {
            if (_capture.QuotaFoldAt is { } foldAt)
            {
                _quotaBoard?.Seek(foldAt);
                UpdateLayout();
            }
            ProbeLine($"capture: quota fold height={quotaFold.ActualHeight:0.##}"
                + $"  state={(_capture.QuotaFoldAt is { } held ? $"held at {held:0} ms" : "settled")}"
                + $"  control=\"{_quotaCaption?.Text}\"");
        }
        // The pay-as-you-go gauge's provenance, in one line and for the same reason: a
        // bar whose denominator is "the balance the last recharge left" is a claim about
        // a series of observations, and the run that drew it should say what the series
        // was and whether this run is what moved it. A subscription account has no such
        // gauge and prints nothing here.
        var watchedSubscription = _capture.Account is { } planOwner
            && _accounts.TryGetValue(planOwner, out var planInfo) && planInfo.IsSubscription;
        if (!watchedSubscription && _capture.Account is { } watchedAccount
            && _accountRows.TryGetValue(watchedAccount, out var watchedRow))
        {
            var observation = _lastBalanceUsage.Rows.FirstOrDefault(candidate =>
                string.Equals(candidate.AccountId, watchedAccount, StringComparison.OrdinalIgnoreCase));
            var gauge = observation is null ? null : _balances.Gauge(watchedAccount, observation.Balance, observation.Currency);
            ProbeLine($"capture: recharge account={watchedAccount}"
                + $"  balance={(observation?.HasBalance == true ? Amounts.Money(observation.Balance) : "unreported")}"
                + $"  baseline={(gauge is null ? "none" : Amounts.Money(gauge.Baseline))}"
                + $"  left={(gauge is null ? "no bar" : $"{gauge.Percent:0.#}%")}"
                + $"  rechargeSeenThisRun={_balances.LastRecharges.Contains(watchedAccount, StringComparer.OrdinalIgnoreCase)}"
                + $"  row=\"{watchedRow.Bottom.Text}\"");
        }
        ProbeLine($"capture: page={_capture.Page} rows={EventsList.Items.Count} expand={_capture.ExpandRow?.ToString() ?? "none"} t={_capture.At?.ToString() ?? "settled"}");
        // The group readings, off the live elements rather than recomputed here, for the
        // same reason the quota line below reads the row and the card: the census under
        // the list is a claim a reader should not have to check by eye across 700 px of
        // window, and the account column's two group rows are the other half of it.
        ProbeLine($"capture: scope=\"{LedgerScopeText.Text}\"  summary=\"{RecentSummary.Text}\"");
        ProbeLine($"capture: groups=\"{UsageGroups.UnattributedLabel} {GroupRowText(UsageGroups.UnattributedId)}\""
            + $" \"{UsageGroups.ArchivedLabel} {GroupRowText(UsageGroups.ArchivedId)}\""
            + $"  census=\"{CensusLine.Text}\"");
        // Every row of the rail, so the census can be checked against the list it
        // counts rather than against a second calculation: the accounts' own counts,
        // then the two groups', then the total the census states. The check is an
        // addition per the acceptance for this change; see REDESIGN-NOTES 第六轮 §2.
        Console.WriteLine("capture: rail=" + string.Join(" · ", _accountRowOrder.Select(id =>
            $"{AccountDirectory.Label(_accounts, id)} [{RowText(id)}]")));
        ProbeLine($"capture: rail-total={_accountRowOrder.Where(id => !UsageGroups.IsGroupId(id)).Sum(RecordCount)}"
            + $" + {RecordCount(UsageGroups.UnattributedId)} + {RecordCount(UsageGroups.ArchivedId)}"
            + $" = {_accountRowOrder.Where(id => !UsageGroups.IsGroupId(id)).Sum(RecordCount) + RecordCount(UsageGroups.UnattributedId) + RecordCount(UsageGroups.ArchivedId)}"
            + $"  events={_lastEvents.Count}");
        // The ledger's own geometry, read off the live elements. The resize report is
        // four numbers per window width — the content area, the table, whether a bar is
        // drawn and how much dead space is left — and nobody should have to measure
        // columns in a PNG to get them.
        var ledgerScroller = LedgerScroller;
        var innerScroller = FindScrollViewer(EventsList);
        var firstItem = EventsList.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
        ProbeLine($"capture: ledger window={Width:0}x{Height:0}"
            + $" dashboard={ContentScrollViewer.ActualWidth:0.#} content={HistoryScrollViewer.ActualWidth:0.#}"
            + $" card={LedgerCardWidth():0.#} viewport={ledgerScroller?.ViewportWidth:0.#}"
            + $" extent={ledgerScroller?.ExtentWidth:0.#}"
            + $" hbar={ledgerScroller?.ComputedHorizontalScrollBarVisibility}"
            + $" gutter={Math.Max(0, (ledgerScroller?.ViewportWidth ?? 0) - LedgerCardWidth()):0.#}"
            + $" vbar={innerScroller?.ComputedVerticalScrollBarVisibility}"
            + $" rows={EventsList.Items.Count}");
        ProbeLine($"capture: ledger-box list={EventsList.ActualWidth:0.#}"
            + $" scroller={ledgerScroller?.ActualWidth:0.#} item0={firstItem?.ActualWidth:0.#}"
            + $" vbarW={SystemParameters.VerticalScrollBarWidth:0.#}");
        // The column widths themselves, in the order the head declares them, so the
        // question the resize report asks — "where did the extra width go?" — is
        // answered by the log rather than by measuring a screenshot: 时间 模型 强度
        // 步骤 工具 输入 输出 耗时 费用 状态.
        ProbeLine($"capture: ledger-cols={string.Join('/', LedgerColumnsFor(LedgerCardWidth()).Select(column => column.Width.ToString("0.#")))}"
            + $" modelCap={ModelMaxWidth:0.#} min={LedgerMinWidth:0.#}"
            + $" slack={Math.Max(0, LedgerCardWidth() - 2 * SurfaceInset - LedgerMinWidth):0.#}");
        // What the two grids were actually laid out at, task row and sub-request panel,
        // in one list each. The acceptance for the expanded panel is that a sub-request's
        // 输入 sits under the task's 输入 — a claim about numbers, so the numbers are
        // printed here instead of being eyeballed off a PNG: row-cols[2..] must equal
        // detail-cols[3..], one column for one, and detail-cols[0..2] must add up to
        // row-cols[0].
        for (var rowIndex = 0; rowIndex < EventsList.Items.Count && rowIndex < 64; rowIndex++)
        {
            if (EventsList.Items[rowIndex] is not LedgerRowView candidate) continue;
            var columns = LedgerColumnsFor(LedgerCardWidth()).Count;
            if (FindGridWithColumns(candidate, columns + 1) is not { } panel) continue;
            var origin = candidate.TransformToAncestor(this).Transform(new Point(0, 0));
            var panelOrigin = panel.TransformToAncestor(this).Transform(new Point(0, 0));
            ProbeLine($"capture: expanded-row={rowIndex} row-cols={string.Join('/', candidate.ColumnDefinitions.Select(definition => definition.ActualWidth.ToString("0.#")))}"
                + $" x={origin.X:0.#}");
            ProbeLine($"capture: expanded-row={rowIndex} detail-cols={string.Join('/', panel.ColumnDefinitions.Select(definition => definition.ActualWidth.ToString("0.#")))}"
                + $" x={panelOrigin.X:0.#} w={panel.ActualWidth:0.#}");
            break;
        }
        ProbeLine($"capture: window at ({Left:0.#},{Top:0.#}) size={Width:0}x{Height:0}"
            + $" state={WindowState} active={IsActive} showActivated={ShowActivated}"
            + $" screen={SystemParameters.VirtualScreenLeft:0.#},{SystemParameters.VirtualScreenTop:0.#}"
            + $" {SystemParameters.VirtualScreenWidth:0.#}x{SystemParameters.VirtualScreenHeight:0.#}");
        ProbeLine($"capture: metrics columns={_metricColumns}"
            + $" strip={MetricStrip.ActualWidth:0.#}"
            + $" column={(_metricColumns > 0 ? (MetricStrip.ActualWidth - (_metricColumns - 1) * MetricGutter) / _metricColumns : 0):0.#}"
            + $" first={MetricPanelTokens.ActualWidth:0.#} site={MetricPanelSite.ActualWidth:0.#}");
        if (GroupNotice.Visibility == Visibility.Visible)
            ProbeLine($"capture: notice badge=\"{GroupBadgeText.Text}\" line=\"{GroupNoticeLine.Text}\" advice=\"{GroupAdviceLine.Text}\"");
        // The one reading, drawn twice: the account list's row and the card's primary
        // bar. Both strings are read off the elements that are in the frame rather than
        // recomputed here, so the line describes the pixels that were just taken — a
        // reader comparing "80%" in the sidebar with "80%" on the card should not have
        // to take the two agreeing on trust across 700 px of window. A pay-as-you-go
        // account has no bar to compare and prints nothing.
        if (_quotaBar is { } quotaBar && _capture.Account is { } scopedAccount
            && _accountRows.TryGetValue(scopedAccount, out var scopedRow))
        {
            var cardDetail = $"{quotaBar.Name.Text} · {quotaBar.Usage.Text}";
            ProbeLine($"capture: quota account={scopedAccount}"
                + $"  row=\"{scopedRow.Value.Text}\" detail=\"{scopedRow.Bottom.Text}\""
                + $"  card=\"{quotaBar.Percent.Text}\" detail=\"{cardDetail}\""
                + $"  same={scopedRow.Value.Text == quotaBar.Percent.Text && scopedRow.Bottom.Text == cardDetail}");
        }
        if (_capture is { ReadingMotionAt: { } readingAt })
            ProbeLine($"capture: reading motion held at t={readingAt:0} ms"
                + $"  balance Y={Rise(TotalBalance):0.##} colour={Colour(TotalBalance)}"
                + $"  today Y={Rise(TodayUsage):0.##} colour={Colour(TodayUsage)}");


        WriteWindowShot(_capture.Path!);
        Close();
        Application.Current.Shutdown();
    }


    /// <summary>
    /// One account-column row's own value text, read off the live element rather than
    /// recomputed, so a capture's census line describes the pixels it just took.
    /// </summary>
    private string GroupRowText(string id) =>
        _accountRows.TryGetValue(id, out var row) ? row.Value.Text : "（该行未建立）";

    /// <summary>The same, for any row of the rail: its figure and the line under it.</summary>
    private string RowText(string id) =>
        _accountRows.TryGetValue(id, out var row) ? $"{row.Value.Text} · {row.Bottom.Text}" : "（该行未建立）";

    /// <summary>
    /// Moves the archive's ceiling by whole steps through the control's own selection,
    /// for a capture. Clamped at the ends of the list rather than wrapped: a shot taken
    /// after four steps past the largest ceiling should show the largest ceiling, not
    /// the second smallest — a wrap would photograph a value the clicks did not ask for.
    /// </summary>
    private void StepArchiveLimit(int steps)
    {
        var index = ArchiveLimitBox.SelectedIndex;
        if (index < 0) return;
        var wanted = Math.Clamp(index + steps, 0, ArchiveLimitBox.Items.Count - 1);
        ProbeLine($"capture: archive ceiling {index} -> {wanted} of {ArchiveLimitBox.Items.Count - 1} steps"
            + $"  before={_archive.Limit}");
        if (wanted != index) ArchiveLimitBox.SelectedIndex = wanted;
        ProbeLine($"capture: archive ceiling now {_archive.Limit} 条  file={ArchiveLimitStore.FileName}");
    }

    /// <summary>Runs the client chip for one provider, exactly as a click on it would.</summary>
    private void ClickProviderChip(string provider)
    {
        var chip = ProviderFilterPanel.Children.OfType<Button>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, provider, StringComparison.OrdinalIgnoreCase));
        if (chip is null)
        {
            Console.Error.WriteLine($"capture: no client chip for \"{provider}\"");
            return;
        }
        chip.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    /// <summary>
    /// Lets the window run for a while without taking a frame from it: the chrome
    /// transitions are driven by the framework's clock, so a screenshot taken the
    /// instant after a click photographs the state the click was leaving.
    /// </summary>
    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>Opens the Nth task row, exactly as a click on it would.</summary>
    private void SelectLedgerRow(int index)
    {
        if (index < 0 || index >= EventsList.Items.Count) return;
        var item = EventsList.Items[index];
        // Scrolled into view, because a virtualized list has no container for a row
        // it is not showing and a row with no container cannot be opened. The scroll
        // happens before the shot, so the frame still shows the whole list.
        EventsList.ScrollIntoView(item);
        EventsList.UpdateLayout();
        UpdateLayout();
        GenerateRows();
        UpdateLayout();
        if (EventsList.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem container)
        {
            Console.Error.WriteLine($"capture: row {index} has no container");
            return;
        }
        if (FindRowView(container) is { } view) ToggleRow(view);
        else Console.Error.WriteLine($"capture: row {index} has no generated row");
    }

    /// <summary>
    /// Opens one task row's 模型 tooltip, the way the mouse would open it.
    ///
    /// <para>
    /// A hover cannot be synthesized — the pointer belongs to the desk, and a capture
    /// deliberately runs off-screen so nothing is hovered — so this opens the bubble
    /// through the element's own tooltip value and lets WPF place it against the cell,
    /// exactly as <c>ToolTipService</c> does on hover. What it cannot be is a hand-built
    /// copy of the bubble: the whole report is about what WPF draws for a tooltip, so a
    /// frame that drew its own box would prove nothing about it.
    /// </para>
    ///
    /// <para>
    /// It prints what it found rather than what it opened, because the fix for the
    /// report is that an empty tooltip is not set at all: a row whose model nobody
    /// reported has <c>ToolTip == null</c>, and the run says so
    /// (<c>capture: tip … set=False</c>) instead of throwing.
    /// </para>
    /// </summary>
    private void OpenLedgerToolTip(int index, int? detailIndex)
    {
        if (index < 0 || index >= EventsList.Items.Count)
        {
            Console.Error.WriteLine($"capture: no row {index} to open a tooltip on");
            return;
        }
        var item = EventsList.Items[index];
        EventsList.ScrollIntoView(item);
        EventsList.UpdateLayout();
        UpdateLayout();
        if (EventsList.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem container
            || FindRowView(container) is not { } view)
        {
            Console.Error.WriteLine($"capture: row {index} has no generated row");
            return;
        }

        // Which cell: the task's own 模型, or one of its sub-request lines'. The panel is
        // built on the row's first opening, so a detail cell only exists once the row is
        // open — the capture's --expand runs before --tip for exactly that reason.
        TextBlock? cell;
        if (detailIndex is { } detail)
        {
            view.EnsureDetails();
            UpdateLayout();
            cell = detail >= 0 && detail < view.DetailModelCells.Count ? view.DetailModelCells[detail] : null;
            if (cell is null)
            {
                Console.Error.WriteLine($"capture: row {index} has no sub-request {detail} 模型 cell");
                return;
            }
        }
        else
        {
            cell = view.ModelCell;
            if (cell is null)
            {
                Console.Error.WriteLine($"capture: row {index} has no 模型 cell");
                return;
            }
        }

        var value = cell.ToolTip;
        ProbeLine($"capture: tip row={index} detail={detailIndex?.ToString() ?? "task"}"
            + $" set={value is not null} text=\"{(value as string) ?? ""}\""
            + $" cell=\"{cell.Text}\" model=\"{view.Row.Model}\"");
        if (value is null)
        {
            // Nothing to open, which is the state a cell whose text nobody reported is
            // supposed to be in — and the fix. The frame is still taken: a hover with no
            // bubble is the answer, and it is worth a screenshot.
            PumpFor(120);
            return;
        }
        ShowCaptureBubble(cell, value);
    }

    /// <summary>
    /// Prepares the bubble a tooltip would show, without ever opening one.
    ///
    /// <para>
    /// The bubble is a real <see cref="ToolTip"/> holding the real value, so it is drawn
    /// by the same implicit style and the same template the mouse would get. What it is
    /// not is open, and it is not in the tree either — a ToolTip throws on being given a
    /// visual parent ("ToolTip 不能具有逻辑父级或可视父级"), and an open one is a popup,
    /// which is a window of its own: it would land on the desk for the duration of a test
    /// run, and it is not part of this window's pixels, so the shot would not contain it.
    /// Measured and arranged off the tree it still resolves its style and draws itself,
    /// which is all a frame needs; see <see cref="TryBubbleShot"/>.
    /// </para>
    /// </summary>
    private void ShowCaptureBubble(FrameworkElement cell, object value)
    {
        var bubble = new ToolTip { Content = value };
        bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bubble.Arrange(new Rect(new Point(0, 0), bubble.DesiredSize));
        bubble.UpdateLayout();
        _captureTip = bubble;
        // Where ToolTipService would put it for Placement.Bottom: under the cell, centred
        // on it, measured off the live cell so the number in the log is the one the frame
        // used rather than one this method assumed.
        var origin = cell.TranslatePoint(new Point(0, cell.ActualHeight), this);
        _bubbleOrigin = new Point(
            Math.Max(0, origin.X + (cell.ActualWidth - bubble.DesiredSize.Width) / 2),
            Math.Max(0, origin.Y + 6));
        ProbeLine($"capture: bubble at ({_bubbleOrigin.X:0.#},{_bubbleOrigin.Y:0.#})"
            + $" {bubble.DesiredSize.Width:0.#}x{bubble.DesiredSize.Height:0.#}");
    }

    /// <summary>The window's own pixels, by the route that works for this window.</summary>
    private void WriteWindowShot(string path)
    {
        // System.IO.Path, spelled out: this file also draws WPF Path elements.
        var full = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        // A hover frame draws its bubble onto the window's own pixels, because a popup is
        // a window of its own and must not be opened by a test run; see ShowCaptureBubble.
        ProbeLine($"capture: shot bubble={(_captureTip is null ? "none" : "composited")}");
        if (_captureTip is not null && TryBubbleShot(full)) return;
        if (TryPrintWindow(full)) return;
        Console.Error.WriteLine("PrintWindow declined; falling back to the visual tree.");
        SaveFromVisualTree(full);
    }

    /// <summary>
    /// The window with the prepared bubble drawn onto it, at the position the cell and
    /// the bubble's own measured size give.
    ///
    /// <para>
    /// Both halves are rendered from the elements themselves: the window through a
    /// <see cref="VisualBrush"/>, the bubble through its own
    /// <see cref="RenderTargetBitmap"/> after being measured and arranged off the tree.
    /// Nothing is opened, so nothing appears on the desk; nothing is drawn by hand, so
    /// what the frame shows is the control's own template and the window's own style.
    /// </para>
    /// </summary>
    private bool TryBubbleShot(string path)
    {
        if (_captureTip is not { } bubble) return false;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        var bubbleWidth = bubble.DesiredSize.Width;
        var bubbleHeight = bubble.DesiredSize.Height;
        if (width <= 0 || height <= 0 || bubbleWidth <= 0 || bubbleHeight <= 0) return false;

        var bubblePixels = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(bubbleWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(bubbleHeight * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bubblePixels.Render(bubble);

        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(this), null, new Rect(0, 0, ActualWidth, ActualHeight));
            context.DrawImage(bubblePixels, new Rect(_bubbleOrigin.X, _bubbleOrigin.Y, bubbleWidth, bubbleHeight));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        ProbeLine($"wrote {path}  {width}x{height}  window + bubble at"
            + $" ({_bubbleOrigin.X:0.#},{_bubbleOrigin.Y:0.#}) {bubbleWidth:0.#}x{bubbleHeight:0.#}");
        return true;
    }

    /// <summary>A real, composited window, captured with PrintWindow.</summary>
    private bool TryPrintWindow(string path)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return false;
        if (!GetWindowRect(handle, out var rect)) return false;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return false;

        var stride = width * 4;
        var data = Marshal.AllocHGlobal(stride * height);
        try
        {
            if (!PrintWindow(handle, data, 2 /* PW_RENDERFULLCONTENT */)) return false;
            var taken = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, data, stride * height, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(taken));
            using (var stream = File.Create(path)) encoder.Save(stream);
            if (IsBlank(taken))
            {
                File.Delete(path);
                return false;
            }
            Console.WriteLine($"wrote {path}  {width}x{height}  via PrintWindow  dpi={VisualTreeHelper.GetDpi(this).PixelsPerInchX:0}");
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    /// <summary>A screenshot that is one flat colour is a failed screenshot, not a dark window.</summary>
    private static bool IsBlank(BitmapSource source)
    {
        var first = new byte[4];
        var probe = new byte[4];
        var stepX = Math.Max(1, source.PixelWidth / 20);
        var stepY = Math.Max(1, source.PixelHeight / 14);
        source.CopyPixels(new Int32Rect(0, 0, 1, 1), first, 4, 0);
        for (var x = 0; x < source.PixelWidth; x += stepX)
        {
            for (var y = 0; y < source.PixelHeight; y += stepY)
            {
                source.CopyPixels(new Int32Rect(x, y, 1, 1), probe, 4, 0);
                if (first[0] != probe[0] || first[1] != probe[1] || first[2] != probe[2]) return false;
            }
        }
        return true;
    }

    /// <summary>The same pixels straight out of the visual tree, for when the
    /// compositor will not hand them over.</summary>
    private void SaveFromVisualTree(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine($"wrote {path}  {width}x{height}  via RenderTargetBitmap  dpi={dpi.PixelsPerInchX:0}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr handle, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out RECT rect);
}

/// <summary>Which page a capture run should show.</summary>
public enum CapturePage
{
    Dashboard,
    Ledger
}

/// <summary>
/// What a development capture run was asked for. Empty — <see cref="IsCapture"/>
/// false — for every ordinary launch, which is how the window behaves exactly as
/// it did before this file had a capture mode at all.
/// </summary>
/// <param name="Path">Where the PNG goes.</param>
/// <param name="Page">Which page to show.</param>
/// <param name="ExpandRow">Index of the task row to open, or null to open none.</param>
/// <param name="At">Millisecond of the opening to hold, or null for the settled frame.</param>
/// <param name="Account">Account to scope the window to, or null for the combined view.</param>
/// <param name="ReadingMotionAt">Millisecond of the reading motion to hold, or null when
/// this run has no reading to roll in. A capture is the only caller that sets it: an
/// ordinary run rolls when a watched file changes and never on request.</param>
/// <param name="SelectAccount">Account to click in the account list after the window is
/// up, or null to click nothing. This is the click path — <see cref="MainWindow"/>'s
/// SelectAccountFilter — rather than the scope the window starts in, because only the
/// click can show whether the list's highlight follows the account.</param>
/// <param name="SelectProvider">Client chip to click, or null. Matched against the chip's
/// own tag, so an unknown client is reported rather than silently photographing 「全部」.</param>
/// <param name="ExpandQuota">True to click the subscription quota card's fold open.</param>
/// <param name="QuotaFoldAt">Millisecond of the fold to hold, or null for the settled frame.</param>
/// <param name="Probe">True to run the timing probe instead of taking a frame. It writes
/// no PNG and exits the moment the last measurement is printed; see
/// <see cref="MainWindow.RunProbe"/> for why the segmented table needs a run of its own.</param>
/// <param name="ProbeFile">Where the probe duplicates its lines when the shell this was
/// started from cannot be counted on to have redirected them, or null for the console
/// alone. A WinExe has no console of its own, so this is the route that behaves the same
/// in every shell.</param>
/// <param name="ArchiveLimitStep">Whole steps to move the archive ceiling control by, or
/// zero to leave it alone. Negative steps move it down. A step is a click on the
/// control, not a value written into the store: the handler behind the click is what
/// writes the plugin's file, and a capture that set the field directly would photograph
/// a ceiling no click can produce.</param>
/// <param name="Width">Window width to open at, or null for the XAML's own. This is a
/// real size, not a scaled render: the layout under test is a function of the width, so
/// a capture that resized the pixels afterwards would prove nothing about it.</param>
/// <param name="Height">Window height to open at, or null for the XAML's own.</param>
/// <param name="ToolTipRow">Opens that task row's 模型 tooltip before the frame is
/// taken, or null. A hover cannot be synthesized — the pointer belongs to the desk — so
/// the bubble is opened through the same ToolTip the mouse would open, and its pixels
/// are composited onto the window's, because a tooltip lives in its own popup window and
/// PrintWindow on this one cannot see it. See MainWindow.WriteWindowShot.</param>
/// <param name="ToolTipDetail">Opens the tooltip of that sub-request line's 模型 cell
/// instead of the task's own, or null for the task's. The row has to be open already —
/// <c>--expand</c> — because the sub-request lines are built on the row's first
/// opening.</param>
public sealed record CaptureRequest(
    string? Path = null,
    CapturePage Page = CapturePage.Dashboard,
    int? ExpandRow = null,
    double? At = null,
    string? Account = null,
    double? ReadingMotionAt = null,
    string? SelectAccount = null,
    string? SelectProvider = null,
    bool ExpandQuota = false,
    double? QuotaFoldAt = null,
    bool Probe = false,
    string? ProbeFile = null,
    int ProbePasses = 3,
    int ArchiveLimitStep = 0,
    double? Width = null,
    double? Height = null,
    int? ToolTipRow = null,
    int? ToolTipDetail = null)
{
    public bool IsCapture => !string.IsNullOrWhiteSpace(Path);
}
