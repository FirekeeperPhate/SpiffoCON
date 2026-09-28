using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Core.Commands;

namespace SpiffoCON.ViewModels;

/// <summary>One server option being edited: the server's value and the value in the editor.</summary>
public sealed partial class OptionItem : ObservableObject
{
    public OptionItem(ServerOptionInfo info, string page, string serverValue)
    {
        Info = info;
        Page = page;
        ServerValue = serverValue;
        editText = ServerOptionCatalog.ToEditorText(info, serverValue);
    }

    public ServerOptionInfo Info { get; }
    public string Page { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(IsDefault))]
    private string serverValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(BoolValue), nameof(EnumIndex), nameof(Error))]
    private string editText;

    public string Name => Info.Name;
    public string? Description => Info.Description;

    public bool BoolValue
    {
        get => EditText.Equals("true", StringComparison.OrdinalIgnoreCase);
        set => EditText = value ? "true" : "false";
    }

    /// <summary>0-based for the ComboBox; the server counts from 1.</summary>
    public int EnumIndex
    {
        get => int.TryParse(EditText, out var i) ? i - 1 : -1;
        set => EditText = (value + 1).ToString();
    }

    public ValidationResult Validation => ServerOptionCatalog.Validate(Info, EditText);

    public string? Error => Validation.Error;

    /// <summary>Compares normalized values: the server writes doubles as "40.0", the editor sends "40".</summary>
    public bool IsChanged => !Validation.Ok || Validation.Value != NormalizedServerValue;

    string NormalizedServerValue => Normalize(ServerValue);

    /// <summary>Options without a meaningful default count as default: no reset button for them.</summary>
    public bool IsDefault => Info.NoDefault || NormalizedServerValue == Normalize(Info.Default);

    public string? Warning => Info.Warning;

    string Normalize(string stored)
    {
        var v = ServerOptionCatalog.Validate(Info, ServerOptionCatalog.ToEditorText(Info, stored));
        return v.Ok ? v.Value : stored;
    }

    public string Hint
    {
        get
        {
            var parts = new List<string>();
            if (Info.NoDefault)
                return "No default: unique to each server";
            var def = Info.Type switch
            {
                ServerOptionType.Enum when int.TryParse(Info.Default, out var i) && i >= 1 && i <= Info.Values.Count => Info.Values[i - 1],
                ServerOptionType.Text or ServerOptionType.String when Info.Default.Length > 40 => Info.Default[..40] + "…",
                ServerOptionType.Text or ServerOptionType.String when Info.Default.Length == 0 => "(empty)",
                _ => Info.Default,
            };
            parts.Add("Default: " + def);
            if (Info.Min is not null && Info.Max is not null && Info.Type != ServerOptionType.Enum)
                parts.Add($"range {Info.Min} – {Info.Max}");
            return string.Join(" · ", parts);
        }
    }

    public void Revert() => EditText = ServerOptionCatalog.ToEditorText(Info, ServerValue);
}

public sealed partial class OptionPage : ObservableObject
{
    public OptionPage(string title) => Title = title;

    public string Title { get; }

    [ObservableProperty] private int count;
    [ObservableProperty] private int changed;
}

/// <summary>
/// Server options: read with showoptions, changed with changeoption (the server writes the .ini
/// at once). Values are validated first, and each reply is checked, because the server silently
/// keeps the old value when it doesn't like the new one.
/// </summary>
public sealed partial class OptionsViewModel : ObservableObject
{
    public const string AllPages = "All options";

    readonly MainViewModel _main;
    readonly List<OptionItem> _items = [];
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public OptionsViewModel(MainViewModel main)
    {
        _main = main;
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            View?.Refresh();
        };
        _main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSessionActive) && !_main.IsSessionActive)
                Clear();
        };
    }

    public ObservableCollection<OptionPage> Pages { get; } = [];

    [ObservableProperty] private ICollectionView? view;
    [ObservableProperty] private OptionPage? selectedPage;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private bool onlyNonDefault;
    [ObservableProperty] private string statusText = "Connect to read the server's options.";
    [ObservableProperty] private int changedCount;
    [ObservableProperty] private bool isBusy;

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
        if (Search.Trim().Length > 0)
        {
            // searching looks across all pages
            var s = Search.Trim();
            if (!item.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                && item.Description?.Contains(s, StringComparison.OrdinalIgnoreCase) != true)
                return false;
        }
        else if (SelectedPage is { } page && page.Title != AllPages && item.Page != page.Title)
        {
            return false;
        }
        return !OnlyNonDefault || !item.IsDefault || item.IsChanged;
    }

    void Clear()
    {
        _items.Clear();
        Pages.Clear();
        View = null;
        ChangedCount = 0;
        StatusText = "Connect to read the server's options.";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!_main.IsSessionActive)
            return;
        if (ChangedCount > 0 && _main.Confirm?.Invoke($"Discard {ChangedCount} unsaved changes and reload the options from the server?") != true)
            return;
        IsBusy = true;
        try
        {
            var reply = await _main.RunAsync("showoptions", logReply: false);
            if (reply is null)
            {
                StatusText = "showoptions failed: see the console.";
                return;
            }
            Build(ServerOptions.Parse(reply));
        }
        finally
        {
            IsBusy = false;
        }
    }

    void Build(ServerOptions server)
    {
        var keepPage = SelectedPage?.Title;
        foreach (var item in _items)
            item.PropertyChanged -= OnItemChanged;
        _items.Clear();
        Pages.Clear();

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in ServerOptionCatalog.Pages)
        {
            foreach (var info in page.Options)
            {
                known.Add(info.Name);
                if (server[info.Name] is { } value)
                    _items.Add(new OptionItem(info, page.Title, value));
            }
        }
        // options this server has that the bundled metadata doesn't (newer game build)
        foreach (var name in server.Names.Where(n => !known.Contains(n)).Order())
            _items.Add(new OptionItem(ServerOptionInfo.Unknown(name), "Other", server[name]!));

        Pages.Add(new OptionPage(AllPages) { Count = _items.Count });
        foreach (var group in _items.GroupBy(i => i.Page))
            Pages.Add(new OptionPage(group.Key) { Count = group.Count() });
        foreach (var item in _items)
            item.PropertyChanged += OnItemChanged;

        View = new ListCollectionView(_items) { Filter = Matches };
        SelectedPage = Pages.FirstOrDefault(p => p.Title == keepPage) ?? Pages.ElementAtOrDefault(1) ?? Pages.FirstOrDefault();
        UpdateChanged();
        StatusText = $"{_items.Count} options read at {DateTime.Now:HH:mm:ss}.";
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
            page.Changed = _items.Count(i => i.IsChanged && (page.Title == AllPages || i.Page == page.Title));
    }

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
    private async Task ApplyAsync()
    {
        var changed = _items.Where(i => i.IsChanged).ToList();
        var invalid = changed.Where(i => !i.Validation.Ok).ToList();
        if (invalid.Count > 0)
        {
            StatusText = $"Fix {invalid.Count} invalid value(s) first: " + string.Join(", ", invalid.Select(i => i.Name));
            return;
        }
        if (changed.Count == 0 || !_main.IsSessionActive)
            return;
        foreach (var risky in changed.Where(i => i.Warning is not null))
        {
            if (_main.Confirm?.Invoke($"{risky.Name}: {risky.Warning}\n\nApply the new value {risky.Validation.Value}?") != true)
            {
                StatusText = "Nothing applied.";
                return;
            }
        }

        IsBusy = true;
        var applied = new List<string>();
        var problems = new List<string>();
        try
        {
            foreach (var item in changed)
            {
                var value = item.Validation.Value;
                var reply = await _main.RunAsync(ServerOptionCatalog.ChangeCommand(item.Name, value));
                if (reply is null)
                {
                    problems.Add($"{item.Name}: no reply");
                    continue;
                }
                var result = ServerOptionCatalog.InterpretChange(reply, value);
                switch (result.Outcome)
                {
                    case ChangeOutcome.Applied:
                        item.ServerValue = value;
                        applied.Add(item.Name);
                        break;
                    case ChangeOutcome.Rejected:
                        problems.Add($"{item.Name}: the server kept {result.ServerValue}");
                        break;
                    case ChangeOutcome.UnknownOption:
                        problems.Add($"{item.Name}: unknown to this server");
                        break;
                    default:
                        problems.Add($"{item.Name}: unexpected reply \"{reply.Trim()}\"");
                        break;
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        // re-read so the editor shows what the server really has
        var reply2 = await _main.RunAsync("showoptions", logReply: false);
        if (reply2 is not null)
            Build(ServerOptions.Parse(reply2));

        StatusText = $"{applied.Count} applied" + (problems.Count > 0 ? $", {problems.Count} not: " + string.Join("; ", problems) : ".")
            + (applied.Count > 0 ? " Saved to the server's .ini; some options (ports, mods, map) apply after a restart." : "");
    }

    [RelayCommand]
    private async Task ReloadFromFileAsync()
    {
        if (!_main.IsSessionActive || _main.Confirm?.Invoke("Reload the options from the server's .ini file (reloadoptions)? Use it after editing the file by hand.") != true)
            return;
        await _main.RunAsync("reloadoptions");
        var reply = await _main.RunAsync("showoptions", logReply: false);
        if (reply is not null)
            Build(ServerOptions.Parse(reply));
    }
}
