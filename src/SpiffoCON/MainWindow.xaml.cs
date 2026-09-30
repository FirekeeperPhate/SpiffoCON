using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SpiffoCON.Services;
using SpiffoCON.ViewModels;

namespace SpiffoCON;

public partial class MainWindow : Window
{
    readonly ServerBook _book = ProfileStore.Load();
    MainViewModel _vm = null!;
    bool _closing;
    bool _switching;
    bool _closeAfterSwitch;
    bool _readyToClose;
    TrayNotifier? _tray;

    public MainWindow()
    {
        InitializeComponent();
        SetUpLogScrolling();
        Attach(new MainViewModel(_book));
        Loaded += (_, _) =>
        {
            (string.IsNullOrEmpty(_vm.Host) ? (UIElement)HostBox : ConsoleInputBox).Focus();
            if (ProfileStore.LoadProblem is { } problem)
                MessageBox.Show(this, problem, "SpiffoCON", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
    }

    /// <summary>
    /// Every server gets its own view model, so nothing loaded for one server (catalog, logs,
    /// accounts, bridge...) carries over to the next.
    /// </summary>
    async void OnSwitchRequested(object? sender, ServerProfile target)
    {
        if (_switching || _closing)
            return;
        _switching = true;
        try
        {
            // let the server list finish its own selection change first
            await Dispatcher.Yield(DispatcherPriority.Background);
            await _vm.ShutdownAsync();
            _book.Selected = target.Id;
            Attach(new MainViewModel(_book));
            _vm.SaveProfile();
        }
        finally
        {
            _switching = false;
            if (_closeAfterSwitch)
                _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.Background);
        }
    }

    /// <summary>The tray icon appears with the first notification and goes with the window.</summary>
    void ShowNotification(AppNotification n)
    {
        if (_closing || (n.OnlyWhenInactive && IsActive && WindowState != WindowState.Minimized))
            return;
        _tray ??= new TrayNotifier(() =>
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
        });
        try
        {
            _tray.Show(n.Title, n.Text);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // a notification that can't be shown is not worth an error box
        }
    }

    void Attach(MainViewModel vm)
    {
        _vm = vm;
        DataContext = _vm;
        _vm.SwitchRequested += OnSwitchRequested;
        _vm.NotificationRaised += (_, n) => ShowNotification(n);
        _vm.Confirm = question =>
            MessageBox.Show(this, question, "SpiffoCON", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        _vm.Sandbox.PickFile = () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open SandboxVars.lua",
                Filter = "Sandbox settings (*_SandboxVars.lua)|*_SandboxVars.lua|Lua files (*.lua)|*.lua|All files|*.*",
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        };

        _vm.Logs.PickFolder = () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the server's Logs folder" };
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        };
        _vm.Catalog.PickGameFolder = () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your Project Zomboid folder (with media\\texturepacks)" };
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        };
        _vm.Accounts.PickFolder = () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the server's Zomboid folder (the one with db and Saves)" };
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        };
        _vm.Bridge.PickFolder = () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the server's Zomboid\\Lua folder" };
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        };
        _vm.Map.PickFolder = () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your Project Zomboid (or dedicated server) folder" };
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        };
        _vm.Map.CenterRequested += (x, y) => MapView.CenterOn(x, y, minScale: 0.5);
        _vm.Map.FitRequested += () => MapView.Fit();
        _vm.Map.PropertyChanged += OnMapChanged;
        _vm.TabRequested += header =>
        {
            if (Tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Header as string == header) is { } tab)
                Tabs.SelectedItem = tab;
        };
        if (Tabs.SelectedItem == MapTab)
            _vm.Map.Activate();

        RconPasswordBox.Password = _vm.RconPassword;
        SftpPasswordBox.Password = _vm.SftpPassword;

        // keep the newest console line in view; deferred because this handler can run before the
        // ListBox has processed the same change, and scrolling then would measure a stale item list
        _vm.ConsoleLines.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is { Count: > 0 })
                Dispatcher.BeginInvoke(ScrollConsoleToEnd, DispatcherPriority.Background);
        };
    }

    /// <summary>Whether the log list shows its last line: new lines keep it there, unless the user scrolled up to read.</summary>
    bool _logAtEnd = true;

    /// <summary>
    /// The Logs tab list stays on its last line: when new lines come in while it is there, and every time
    /// the tab is shown again (a hidden list can't scroll, so it would come back where it was left).
    /// </summary>
    void SetUpLogScrolling()
    {
        LogList.Loaded += (_, _) =>
        {
            _logAtEnd = true;
            Dispatcher.BeginInvoke(ScrollLogToEnd, DispatcherPriority.Loaded);
        };
        LogList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not ScrollViewer viewer)
                return;
            if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
                // the user scrolled: follow the end only from the end
                _logAtEnd = viewer.VerticalOffset >= viewer.ScrollableHeight - 1;
            else if (_logAtEnd)
                viewer.ScrollToEnd();
        }));
    }

    void ScrollLogToEnd()
    {
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
        if (FindScrollViewer(LogList) is { } viewer)
            viewer.ScrollToEnd();
    }

    static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if ((child as ScrollViewer ?? FindScrollViewer(child)) is { } found)
                return found;
        }
        return null;
    }

    void ScrollConsoleToEnd()
    {
        if (_vm.ConsoleLines.Count > 0)
            ConsoleList.ScrollIntoView(_vm.ConsoleLines[^1]);
    }

    /// <summary>Enter in the connection fields connects (the button is not the window's default).</summary>
    void ConnectionFields_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.CanEditConnection && e.OriginalSource is TextBox or PasswordBox)
        {
            _vm.ConnectCommand.Execute(null);
            e.Handled = true;
        }
    }

    void RconPasswordBox_PasswordChanged(object sender, RoutedEventArgs e) => _vm.RconPassword = RconPasswordBox.Password;

    void SftpPasswordBox_PasswordChanged(object sender, RoutedEventArgs e) => _vm.SftpPassword = SftpPasswordBox.Password;

    void TestSftp_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = FilesTab;

    // double-click in the online sidebar: that player in the Players tab
    void OnlineSidebar_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OnlineSidebar.SelectedItem is SidebarPlayer player)
            _vm.PlayerActions.ShowInPlayersTab(player.Username);
    }

    // with the Map tab open, a click on a player (also the one already selected) shows and follows them there
    void OnlineSidebar_Click(object sender, MouseButtonEventArgs e)
    {
        if (Tabs.SelectedItem != MapTab || e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(OnlineSidebar, source) is not ListBoxItem { DataContext: SidebarPlayer player })
            return;
        _vm.Map.SelectedPlayer = player.Username;
        _vm.Map.Follow = true;
    }

    // a player picked on the map is the one selected in the sidebar too
    void OnMapChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapViewModel.SelectedPlayer) && sender is MapViewModel map)
            OnlineSidebar.SelectedItem = _vm.SidebarPlayers.FirstOrDefault(p => p.Username.Equals(map.SelectedPlayer, StringComparison.OrdinalIgnoreCase));
    }

    // right-click on a player's marker: the player menu instead of the map's own
    void MapView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (MapView.PlayerNameAt(Mouse.GetPosition(MapView)) is { } player)
        {
            e.Handled = true;
            Controls.PlayerMenu.Open(_vm, player, MapView);
        }
    }

    // the map is read the first time its tab is shown (lists and combo boxes inside raise this event too)
    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == Tabs && Tabs.SelectedItem == MapTab)
            _vm.Map.Activate();
    }

    void ConsoleInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _vm.SendConsoleCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Down:
                _vm.BrowseHistory(e.Key == Key.Up ? -1 : 1);
                ConsoleInputBox.CaretIndex = ConsoleInputBox.Text.Length;
                e.Handled = true;
                break;
        }
    }

    void LogMessage_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.Logs.SendMessageCommand.Execute(null);
            e.Handled = true;
        }
    }

    void ConsoleList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopyConsole_Click(sender, e);
            e.Handled = true;
        }
    }

    void CopyConsole_Click(object sender, RoutedEventArgs e)
    {
        var lines = ConsoleList.SelectedItems.Count > 0
            ? ConsoleList.SelectedItems.Cast<ConsoleLine>().OrderBy(l => _vm.ConsoleLines.IndexOf(l))
            : _vm.ConsoleLines.AsEnumerable();
        var text = string.Join(Environment.NewLine, lines.Select(l => $"{l.TimeText} {l.Text}"));
        if (text.Length > 0)
            SafeClipboard.SetText(text);
    }

    /// <summary>Inserts the color tag at the cursor; the color applies from there on.</summary>
    void ColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ColorPreset preset })
            return;
        var tag = preset.Color.ToTag();
        int caret = MessageEditor.CaretIndex;
        MessageEditor.SelectedText = tag;
        MessageEditor.CaretIndex = caret + tag.Length;
        MessageEditor.Focus();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_readyToClose)
            return;
        // from here on the window closes only once the shutdown below is done
        e.Cancel = true;
        if (_closing)
            return;
        if (_switching)
        {
            // a server switch is building the next view model: close when it is done
            _closeAfterSwitch = true;
            return;
        }
        if (!_vm.ConfirmLeaving("Close SpiffoCON"))
            return;
        // let the RCON connection close cleanly, then close for real
        _closing = true;
        // checked again: the countdown may have ended while the question was open
        if (_vm.Maintenance.IsCountingDown)
            await _vm.Maintenance.CancelRestartCommand.ExecuteAsync(null);
        await _vm.ShutdownAsync();
        _tray?.Dispose();
        _tray = null;
        // ShutdownAsync may finish synchronously, i.e. still inside this Closing event, where
        // WPF refuses Close(); run it after the event instead
        _readyToClose = true;
        await Dispatcher.BeginInvoke(Close, DispatcherPriority.Background);
    }
}
