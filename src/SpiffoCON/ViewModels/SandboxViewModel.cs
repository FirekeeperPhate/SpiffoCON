using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Core.Commands;
using SpiffoCON.Core.Files;

namespace SpiffoCON.ViewModels;

/// <summary>
/// Sandbox options: there is no RCON command for them, so SpiffoCON edits the server's
/// &lt;name&gt;_SandboxVars.lua (over SFTP, or a local copy) and the server applies it at the next
/// start. Verified on a B42 server: edits made while it runs survive a shutdown, and the new
/// values apply to an existing world after the restart.
/// </summary>
public sealed partial class SandboxViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly List<OptionItem> _items = [];
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };
    ISandboxStore? _store;
    string? _loadedText;

    public SandboxViewModel(MainViewModel main)
    {
        _main = main;
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            View?.Refresh();
        };
        if (_main.Profile.SftpSandboxPath is { } saved)
            ServerFiles.Add(saved);
        SelectedServerFile = _main.Profile.SftpSandboxPath;
    }

    string BackupFolder => Path.Combine(
        Environment.GetEnvironmentVariable("SPIFFOCON_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpiffoCON"),
        "backups");

    /// <summary>Asks for a local SandboxVars.lua; set by the view.</summary>
    public Func<string?>? PickFile { get; set; }

    public ObservableCollection<OptionPage> Pages { get; } = [];
    public ObservableCollection<string> ServerFiles { get; } = [];

    [ObservableProperty] private string? selectedServerFile;
    [ObservableProperty] private ICollectionView? view;
    [ObservableProperty] private OptionPage? selectedPage;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private bool onlyNonDefault;
    [ObservableProperty] private string source = "No file loaded.";
    [ObservableProperty] private string statusText = "Load the server's SandboxVars.lua over SFTP, or open a copy of it.";
    [ObservableProperty] private int changedCount;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isLoaded;

    public bool HasChanges => ChangedCount > 0;

    partial void OnChangedCountChanged(int value) => OnPropertyChanged(nameof(HasChanges));
    partial void OnSelectedPageChanged(OptionPage? value) => View?.Refresh();
    partial void OnOnlyNonDefaultChanged(bool value) => View?.Refresh();

    partial void OnSearchChanged(string value)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    bool Matches(object o)
    {
        var item = (OptionItem)o;
        var s = Search.Trim();
        if (s.Length > 0)
        {
            if (!item.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                && !item.DisplayName.Contains(s, StringComparison.OrdinalIgnoreCase)
                && item.Description?.Contains(s, StringComparison.OrdinalIgnoreCase) != true)
                return false;
        }
        else if (SelectedPage is { } page && page.Title != OptionsViewModel.AllPages && item.Page != page.Title)
        {
            return false;
        }
        return !OnlyNonDefault || !item.IsDefault || item.IsChanged;
    }

    // ---- finding and loading the file ----

    [RelayCommand]
    private async Task FindOnServerAsync()
    {
        var sftp = _main.CurrentSftpSettings();
        if (sftp is null)
        {
            StatusText = "Enable SFTP on the left (user and password) to read the server's files.";
            return;
        }
        IsBusy = true;
        StatusText = "Looking for server configs over SFTP...";
        try
        {
            var probe = await _main.ProbeAsync(sftp);
            // from now on the key this probe saw is required
            sftp = _main.CurrentSftpSettings() ?? sftp;
            ServerFiles.Clear();
            foreach (var ini in probe.ServerConfigs)
                ServerFiles.Add(SandboxFiles.ForServerConfig(ini));
            SelectedServerFile = ServerFiles.FirstOrDefault(f => f == _main.Profile.SftpSandboxPath) ?? ServerFiles.FirstOrDefault();
            StatusText = ServerFiles.Count switch
            {
                0 => "No Server/*.ini found over SFTP. Open a local copy of SandboxVars.lua instead.",
                1 => "Found the server's sandbox file.",
                _ => $"Found {ServerFiles.Count} server configs: choose the one this server runs.",
            };
            if (ServerFiles.Count == 1)
                await LoadFromServerAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = "SFTP: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadFromServerAsync()
    {
        var sftp = _main.CurrentSftpSettings();
        if (sftp is null || SelectedServerFile is null)
        {
            StatusText = sftp is null ? "Enable SFTP on the left first." : "Find the file on the server first.";
            return;
        }
        if (!ConfirmDiscard())
            return;
        _main.Profile.SftpSandboxPath = SelectedServerFile;
        _main.SaveProfile();
        await LoadAsync(new SftpSandboxStore(sftp, SelectedServerFile));
    }

    [RelayCommand]
    private async Task OpenLocalFileAsync()
    {
        if (!ConfirmDiscard())
            return;
        var path = PickFile?.Invoke();
        if (path is not null)
            await LoadAsync(new LocalSandboxStore(path));
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (_store is not null && ConfirmDiscard())
            await LoadAsync(_store);
    }

    bool ConfirmDiscard() =>
        ChangedCount == 0 || _main.Confirm?.Invoke($"Discard {ChangedCount} unsaved sandbox changes?") == true;

    async Task LoadAsync(ISandboxStore store)
    {
        IsBusy = true;
        StatusText = "Reading " + store.Description + "...";
        try
        {
            var text = await store.ReadAsync();
            var file = SandboxVarsFile.Parse(text);
            _store = store;
            _loadedText = text;
            Build(file);
            Source = store.Description;
            IsLoaded = true;
            StatusText = $"{_items.Count} sandbox options read at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = "Could not read the file: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    void Build(SandboxVarsFile file)
    {
        var keepPage = SelectedPage?.Title;
        foreach (var item in _items)
            item.PropertyChanged -= OnItemChanged;
        _items.Clear();
        Pages.Clear();

        foreach (var page in ServerOptionCatalog.SandboxPages)
            foreach (var info in page.Options)
                if (file.Values.TryGetValue(info.Name, out var value))
                    _items.Add(new OptionItem(info, page.Title, value.Value));

        // mod settings and anything newer than the bundled metadata
        foreach (var value in file.Values.Values.Where(v => v.Path != "VERSION" && ServerOptionCatalog.FindSandbox(v.Path) is null).OrderBy(v => v.Path))
        {
            var type = value.Kind switch
            {
                LuaValueKind.Boolean => ServerOptionType.Boolean,
                LuaValueKind.Number => value.Value.Contains('.') ? ServerOptionType.Double : ServerOptionType.Integer,
                _ => ServerOptionType.String,
            };
            var info = new ServerOptionInfo
            {
                Name = value.Path,
                Type = type,
                NoDefault = true,
                // verified on B42: at start-up the server rewrites the file with the options it knows,
                // dropping those of mods it doesn't load
                Description = "Not in SpiffoCON's list: a mod's setting, or newer than this version. " +
                              "The server removes settings of mods it doesn't load when it starts.",
            };
            _items.Add(new OptionItem(info, "Mods & unknown", value.Value));
        }

        Pages.Add(new OptionPage(OptionsViewModel.AllPages) { Count = _items.Count });
        foreach (var group in _items.GroupBy(i => i.Page))
            Pages.Add(new OptionPage(group.Key) { Count = group.Count() });
        foreach (var item in _items)
            item.PropertyChanged += OnItemChanged;

        View = new ListCollectionView(_items) { Filter = Matches };
        SelectedPage = Pages.FirstOrDefault(p => p.Title == keepPage) ?? Pages.ElementAtOrDefault(1) ?? Pages.FirstOrDefault();
        UpdateChanged();
    }

    void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OptionItem.EditText))
            UpdateChanged();
    }

    void UpdateChanged()
    {
        ChangedCount = _items.Count(i => i.IsChanged);
        foreach (var page in Pages)
            page.Changed = _items.Count(i => i.IsChanged && (page.Title == OptionsViewModel.AllPages || i.Page == page.Title));
    }

    // ---- editing and saving ----

    [RelayCommand]
    private void Discard()
    {
        foreach (var item in _items.Where(i => i.IsChanged))
            item.Revert();
    }

    [RelayCommand]
    private void RevertItem(OptionItem? item) => item?.Revert();

    [RelayCommand]
    private void ResetToDefault(OptionItem? item)
    {
        if (item is not null && !item.Info.NoDefault)
            item.EditText = ServerOptionCatalog.ToEditorText(item.Info, item.Info.Default);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_store is null || _loadedText is null)
            return;
        var changed = _items.Where(i => i.IsChanged).ToList();
        var invalid = changed.Where(i => !i.Validation.Ok).ToList();
        if (invalid.Count > 0)
        {
            StatusText = $"Fix {invalid.Count} invalid value(s) first: " + string.Join(", ", invalid.Select(i => i.DisplayName));
            return;
        }
        if (changed.Count == 0)
            return;
        if (_main.Confirm?.Invoke($"Save {changed.Count} change(s) to {_store.Description}?\n\nA backup of the current file is kept on this PC. " +
                                  "The server applies sandbox options when it starts: restart it afterwards.") != true)
            return;

        IsBusy = true;
        try
        {
            // the server rewrites the file when it starts: apply our edits to what is there now
            var current = await _store.ReadAsync();
            var file = SandboxVarsFile.Parse(current);
            var newText = file.With(changed.ToDictionary(i => i.Name, i => i.Validation.Value), out var missing);
            var backup = SandboxFiles.Backup(BackupFolder, _store.BackupLabel, current);
            await _store.WriteAsync(newText);

            var saved = await _store.ReadAsync();
            _loadedText = saved;
            Build(SandboxVarsFile.Parse(saved));
            StatusText = $"Saved {changed.Count - missing.Count} change(s) at {DateTime.Now:HH:mm:ss}. Restart the server to apply them. Backup: {Path.GetFileName(backup)}"
                + (missing.Count > 0 ? $" Not in the file any more: {string.Join(", ", missing)}." : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = "Saving failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
