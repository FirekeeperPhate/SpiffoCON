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

    public MainWindow()
    {
        InitializeComponent();
        Attach(new MainViewModel(_book));
        Loaded += (_, _) => (string.IsNullOrEmpty(_vm.Host) ? (UIElement)ConnectButton : ConsoleInputBox).Focus();
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
        }
    }

    void Attach(MainViewModel vm)
    {
        _vm = vm;
        DataContext = _vm;
        _vm.SwitchRequested += OnSwitchRequested;
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
        _vm.Logs.LinesAdded += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        }, DispatcherPriority.Background);

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

    void ScrollConsoleToEnd()
    {
        if (_vm.ConsoleLines.Count > 0)
            ConsoleList.ScrollIntoView(_vm.ConsoleLines[^1]);
    }

    void RconPasswordBox_PasswordChanged(object sender, RoutedEventArgs e) => _vm.RconPassword = RconPasswordBox.Password;

    void SftpPasswordBox_PasswordChanged(object sender, RoutedEventArgs e) => _vm.SftpPassword = SftpPasswordBox.Password;

    void TestSftp_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = FilesTab;

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
            Clipboard.SetText(text);
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
        if (_closing)
            return;
        // let the RCON connection close cleanly, then close for real
        e.Cancel = true;
        _closing = true;
        await _vm.ShutdownAsync();
        // ShutdownAsync may finish synchronously, i.e. still inside this Closing event, where
        // WPF refuses Close(); run it after the event instead
        await Dispatcher.BeginInvoke(Close, DispatcherPriority.Background);
    }
}
