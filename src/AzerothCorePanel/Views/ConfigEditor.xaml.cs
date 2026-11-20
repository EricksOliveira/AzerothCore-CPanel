using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AzerothCorePanel.Services.Config;

namespace AzerothCorePanel.Views;

/// <summary>Table editor for worldserver.conf and authserver.conf, with the .conf.dist as defaults.</summary>
public partial class ConfigEditor : UserControl
{
    private readonly Dictionary<string, string?> _exePaths = new() { ["worldserver"] = null, ["authserver"] = null };
    private ConfDocument? _document;
    private ICollectionView? _view;
    private string _currentFile = "worldserver";
    private bool _switchingFile;

    public ConfigEditor()
    {
        InitializeComponent();
        FileCombo.SelectedIndex = 0;
    }

    /// <summary>Raised after a successful save, with the saved file path.</summary>
    public event Action<string>? Saved;

    /// <summary>Called when the user needs to be asked something; returns the answer.</summary>
    public Func<string, MessageBoxButton, MessageBoxImage, MessageBoxResult>? Ask { get; set; }

    public bool HasChanges => _document?.HasChanges == true;

    /// <summary>Points the editor at the server exes; their "configs" folder holds the .conf files.</summary>
    /// <param name="reload">Reload even with unsaved changes (the caller already asked the user).</param>
    public void SetServerPaths(string? authserverExe, string? worldserverExe, bool reload = false)
    {
        _exePaths["authserver"] = authserverExe;
        _exePaths["worldserver"] = worldserverExe;

        // Reloading would throw away edits; keep the open file when it has unsaved changes.
        if (reload || !HasChanges)
        {
            LoadCurrent();
        }
    }

    /// <summary>Asks to save unsaved changes. Returns false when the user cancels.</summary>
    public bool ConfirmDiscardOrSave()
    {
        if (!HasChanges || _document is null)
        {
            return true;
        }

        MessageBoxResult answer = AskUser(
            $"Há alterações não salvas em {Path.GetFileName(_document.Path)}. Salvar agora?",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        return answer switch
        {
            MessageBoxResult.Yes => SaveDocument(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private (string? Conf, string? Dist) PathsFor(string file)
    {
        string? exe = _exePaths[file];
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            return (null, null);
        }

        string exeDir = Path.GetDirectoryName(exe)!;
        string configs = Path.Combine(exeDir, "configs");
        string dir = Directory.Exists(configs) ? configs : exeDir;
        return (Path.Combine(dir, file + ".conf"), Path.Combine(dir, file + ".conf.dist"));
    }

    private void LoadCurrent()
    {
        _document = null;
        _view = null;
        SettingsGrid.ItemsSource = null;
        DescriptionBox.Clear();

        (string? conf, string? dist) = PathsFor(_currentFile);
        CreateFromDistButton.Visibility = Visibility.Collapsed;

        if (conf is null)
        {
            PathText.Text = string.Empty;
            InfoText.Text = "Escolha a pasta do servidor para editar as configurações.";
            UpdateButtons();
            return;
        }

        PathText.Text = conf;

        if (!File.Exists(conf))
        {
            bool hasDist = dist is not null && File.Exists(dist);
            InfoText.Text = hasDist
                ? $"{_currentFile}.conf não existe. Clique em \"Criar a partir do .dist\" para criar com os valores padrão."
                : $"{_currentFile}.conf e {_currentFile}.conf.dist não existem nessa pasta.";
            CreateFromDistButton.Visibility = hasDist ? Visibility.Visible : Visibility.Collapsed;
            UpdateButtons();
            return;
        }

        try
        {
            _document = ConfDocument.Load(conf, dist);
        }
        catch (Exception ex)
        {
            InfoText.Text = $"Não foi possível ler {conf}: {ex.Message}";
            UpdateButtons();
            return;
        }

        foreach (ConfSetting setting in _document.Settings)
        {
            setting.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ConfSetting.IsModified))
                {
                    UpdateButtons();
                }
            };
        }

        _view = CollectionViewSource.GetDefaultView(_document.Settings);
        _view.Filter = Matches;
        SettingsGrid.ItemsSource = _view;
        UpdateInfo();
        UpdateButtons();
    }

    private bool Matches(object item)
    {
        if (item is not ConfSetting setting)
        {
            return false;
        }

        if (ModifiedOnlyCheck.IsChecked == true && !setting.DiffersFromDefault && !setting.IsModified)
        {
            return false;
        }

        string search = SearchBox.Text.Trim();
        return search.Length == 0
            || setting.Key.Contains(search, StringComparison.OrdinalIgnoreCase)
            || setting.Value.Contains(search, StringComparison.OrdinalIgnoreCase)
            || setting.Section.Contains(search, StringComparison.OrdinalIgnoreCase)
            || setting.Description.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateInfo()
    {
        if (_document is null)
        {
            return;
        }

        int missing = _document.Settings.Count(s => s.IsMissing);
        int differs = _document.Settings.Count(s => s.DiffersFromDefault);
        string missingText = missing > 0
            ? $" {missing} opção(ões) do .dist faltam neste arquivo (em cinza; o servidor usa o padrão)."
            : string.Empty;

        InfoText.Text = $"{_document.Settings.Count} opções, {differs} diferente(s) do padrão.{missingText} " +
            "Clique duas vezes no valor para editar. As mudanças valem depois de reiniciar o servidor.";
    }

    private void UpdateButtons()
    {
        SaveButton.IsEnabled = HasChanges;
        SaveButton.Content = HasChanges
            ? $"Salvar ({_document!.Settings.Count(s => s.IsModified)})"
            : "Salvar";
    }

    private void FileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingFile || FileCombo.SelectedItem is not ComboBoxItem { Tag: string file } || file == _currentFile && _document is not null)
        {
            return;
        }

        if (!ConfirmDiscardOrSave())
        {
            // Put the previous file back in the list without reloading.
            _switchingFile = true;
            FileCombo.SelectedIndex = _currentFile == "worldserver" ? 0 : 1;
            _switchingFile = false;
            return;
        }

        _currentFile = file;
        LoadCurrent();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        // Commit a cell that is being edited before the list is filtered again.
        SettingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _view?.Refresh();
    }

    private void SettingsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsGrid.SelectedItem is ConfSetting setting)
        {
            string header = $"{setting.Key}   [{setting.Section}]";
            if (setting.DefaultValue is not null)
            {
                header += $"   padrão: {setting.DefaultValue}";
            }

            DescriptionBox.Text = header + "\n\n" +
                (setting.Description.Length > 0 ? setting.Description : "(sem descrição no arquivo)");
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveDocument();

    private bool SaveDocument()
    {
        if (_document is null)
        {
            return true;
        }

        SettingsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        try
        {
            _document.Save();
        }
        catch (Exception ex)
        {
            AskUser($"Não foi possível salvar {_document.Path}:\n\n{ex.Message}", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        UpdateInfo();
        UpdateButtons();
        _view?.Refresh();
        Saved?.Invoke(_document.Path);
        return true;
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (HasChanges)
        {
            MessageBoxResult answer = AskUser("Descartar as alterações não salvas e recarregar o arquivo?",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK)
            {
                return;
            }
        }

        LoadCurrent();
    }

    private void CreateFromDistButton_Click(object sender, RoutedEventArgs e)
    {
        (string? conf, string? dist) = PathsFor(_currentFile);
        if (conf is null || dist is null || !File.Exists(dist) || File.Exists(conf))
        {
            return;
        }

        try
        {
            File.Copy(dist, conf);
        }
        catch (Exception ex)
        {
            AskUser($"Não foi possível criar {conf}:\n\n{ex.Message}", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadCurrent();
        Saved?.Invoke(conf);
    }

    private void OpenNotepadButton_Click(object sender, RoutedEventArgs e)
    {
        (string? conf, _) = PathsFor(_currentFile);
        if (conf is null || !File.Exists(conf))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { conf }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            AskUser($"Não foi possível abrir o Bloco de Notas:\n\n{ex.Message}", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private MessageBoxResult AskUser(string text, MessageBoxButton buttons, MessageBoxImage image) =>
        Ask?.Invoke(text, buttons, image) ?? MessageBox.Show(text, "AzerothCore CPanel", buttons, image);
}
