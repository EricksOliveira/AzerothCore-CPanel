using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AzerothCorePanel.Services.Config;

/// <summary>One "Key = Value" option of a .conf file, editable from the UI.</summary>
public sealed class ConfSetting : INotifyPropertyChanged
{
    private string _value;

    internal ConfSetting(string key, string section, string description, string value, bool quoted,
        int? lineIndex, string linePrefix, string lineSuffix, string? defaultValue)
    {
        Key = key;
        Section = section;
        Description = description;
        _value = value;
        OriginalValue = value;
        Quoted = quoted;
        LineIndex = lineIndex;
        LinePrefix = linePrefix;
        LineSuffix = lineSuffix;
        DefaultValue = defaultValue;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }

    public string Section { get; }

    /// <summary>The comment block above the option, without the leading '#'.</summary>
    public string Description { get; }

    /// <summary>Value without the surrounding quotes; quotes are added back on save when the original had them.</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
            {
                return;
            }

            _value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsModified));
            OnPropertyChanged(nameof(DiffersFromDefault));
        }
    }

    public string OriginalValue { get; private set; }

    /// <summary>Value in the .conf.dist file, if there is one.</summary>
    public string? DefaultValue { get; }

    public bool Quoted { get; }

    /// <summary>Line of the option in the .conf file; null when it only exists in the .conf.dist.</summary>
    public int? LineIndex { get; internal set; }

    /// <summary>Everything before the value on its line ("Key = "), kept as written.</summary>
    internal string LinePrefix { get; set; }

    /// <summary>Whitespace after the value on its line, kept as written.</summary>
    internal string LineSuffix { get; set; }

    public bool IsModified => _value != OriginalValue;

    public bool IsMissing => LineIndex is null;

    public bool DiffersFromDefault => DefaultValue is not null && _value != DefaultValue;

    /// <summary>The value as it is written in the file.</summary>
    internal string FormattedValue => Quoted ? $"\"{_value}\"" : _value;

    internal void AcceptChanges()
    {
        OriginalValue = _value;
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(IsMissing));
    }

    public void RevertChanges() => Value = OriginalValue;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
