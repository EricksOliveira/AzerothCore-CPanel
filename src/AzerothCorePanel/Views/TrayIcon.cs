using System.IO;
using System.Windows;
using AzerothCorePanel.Localization;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AzerothCorePanel.Views;

/// <summary>
/// Icon in the Windows notification area with a menu to open the panel and control the servers.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    // NotifyIcon.Text is limited to 127 characters.
    private const int MaxTooltipLength = 127;

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _startItem;
    private readonly Forms.ToolStripMenuItem _stopItem;
    private readonly Forms.ToolStripMenuItem _restartItem;
    private readonly Forms.ToolStripMenuItem _openItem;
    private readonly Forms.ToolStripMenuItem _exitItem;

    public TrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();

        _openItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => OpenRequested?.Invoke());
        _openItem.Font = new Drawing.Font(_openItem.Font, Drawing.FontStyle.Bold);
        _startItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => StartRequested?.Invoke());
        _stopItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => StopRequested?.Invoke());
        _restartItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => RestartRequested?.Invoke());
        _exitItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => ExitRequested?.Invoke());

        menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            _openItem,
            new Forms.ToolStripSeparator(),
            _startItem,
            _stopItem,
            _restartItem,
            new Forms.ToolStripSeparator(),
            _exitItem,
        });

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "AzerothCore CPanel",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                OpenRequested?.Invoke();
            }
        };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();

        UpdateTexts();
        L.Changed += UpdateTexts;
    }

    public event Action? OpenRequested;
    public event Action? StartRequested;
    public event Action? StopRequested;
    public event Action? RestartRequested;
    public event Action? ExitRequested;

    public void Update(bool canStart, bool canStop, bool canRestart, string tooltip)
    {
        _startItem.Enabled = canStart;
        _stopItem.Enabled = canStop;
        _restartItem.Enabled = canRestart;

        if (tooltip.Length > MaxTooltipLength)
        {
            tooltip = tooltip[..MaxTooltipLength];
        }

        if (_icon.Text != tooltip)
        {
            _icon.Text = tooltip;
        }
    }

    /// <summary>Menu texts in the current language.</summary>
    public void UpdateTexts()
    {
        _openItem.Text = L.T("Tray.Open");
        _startItem.Text = L.T("Tray.Start");
        _stopItem.Text = L.T("Tray.Stop");
        _restartItem.Text = L.T("Tray.Restart");
        _exitItem.Text = L.T("Tray.Exit");
    }

    /// <summary>Colors the right-click menu for the dark or light theme.</summary>
    public void SetDark(bool dark)
    {
        Forms.ContextMenuStrip? menu = _icon.ContextMenuStrip;
        if (menu is null)
        {
            return;
        }

        menu.Renderer = dark
            ? new Forms.ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false }
            : new Forms.ToolStripProfessionalRenderer();

        Drawing.Color fore = dark ? Drawing.Color.FromArgb(0xEC, 0xEC, 0xEC) : Drawing.SystemColors.ControlText;
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            item.ForeColor = fore;
        }
    }

    public void ShowNotification(string title, string message, bool isError) =>
        _icon.ShowBalloonTip(5000, title, message, isError ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        L.Changed -= UpdateTexts;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }

    /// <summary>Menu colors matching Themes/Dark.xaml.</summary>
    private sealed class DarkMenuColors : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Background = Drawing.Color.FromArgb(0x2A, 0x2A, 0x2A);
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(0x4A, 0x4A, 0x4A);
        private static readonly Drawing.Color Selected = Drawing.Color.FromArgb(0x2E, 0x4A, 0x66);

        public override Drawing.Color ToolStripDropDownBackground => Background;
        public override Drawing.Color ImageMarginGradientBegin => Background;
        public override Drawing.Color ImageMarginGradientMiddle => Background;
        public override Drawing.Color ImageMarginGradientEnd => Background;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Selected;
        public override Drawing.Color MenuItemSelected => Selected;
        public override Drawing.Color SeparatorDark => Border;
        public override Drawing.Color SeparatorLight => Border;
    }

    private static Drawing.Icon LoadIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/app.ico");
        using Stream stream = Application.GetResourceStream(uri)!.Stream;

        // Picks the frame that matches the tray size for the current DPI (16, 20, 24 or 32 px).
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}
