using System.IO;
using System.Windows;
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

    public TrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();

        var openItem = new Forms.ToolStripMenuItem("Abrir painel", null, (_, _) => OpenRequested?.Invoke());
        openItem.Font = new Drawing.Font(openItem.Font, Drawing.FontStyle.Bold);
        _startItem = new Forms.ToolStripMenuItem("Start", null, (_, _) => StartRequested?.Invoke());
        _stopItem = new Forms.ToolStripMenuItem("Stop", null, (_, _) => StopRequested?.Invoke());
        _restartItem = new Forms.ToolStripMenuItem("Restart", null, (_, _) => RestartRequested?.Invoke());
        var exitItem = new Forms.ToolStripMenuItem("Sair", null, (_, _) => ExitRequested?.Invoke());

        menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            openItem,
            new Forms.ToolStripSeparator(),
            _startItem,
            _stopItem,
            _restartItem,
            new Forms.ToolStripSeparator(),
            exitItem,
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

    public void ShowNotification(string title, string message, bool isError) =>
        _icon.ShowBalloonTip(5000, title, message, isError ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }

    private static Drawing.Icon LoadIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/app.ico");
        using Stream stream = Application.GetResourceStream(uri)!.Stream;

        // Picks the frame that matches the tray size for the current DPI (16, 20, 24 or 32 px).
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}
