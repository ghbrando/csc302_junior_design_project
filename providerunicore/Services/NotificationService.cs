using System.Runtime.InteropServices;

namespace providerunicore.Services;

public interface INotificationService
{
    Task SendVmStartedNotificationAsync(string vmName, string vmId);
    Task SendVmStoppedNotificationAsync(string vmName, string vmId);
}

public class NotificationService : INotificationService
{
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(ILogger<NotificationService> logger)
    {
        _logger = logger;
    }

    public async Task SendVmStartedNotificationAsync(string vmName, string vmId)
    {
        string title = "UniCore – VM Started";
        string message = $"VM \"{vmName}\" is now running.\nID: {vmId}";
        await SendNativeNotificationAsync(title, message);
    }

    public async Task SendVmStoppedNotificationAsync(string vmName, string vmId)
    {
        string title = "UniCore – VM Stopped";
        string message = $"VM \"{vmName}\" has stopped.\nID: {vmId}";
        await SendNativeNotificationAsync(title, message);
    }

    private async Task SendNativeNotificationAsync(string title, string body)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                await SendWindowsNotificationAsync(title, body);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                await SendLinuxNotificationAsync(title, body);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                await SendMacNotificationAsync(title, body);
            else
                _logger.LogWarning("Push notifications not supported on this OS.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send native notification.");
        }
    }

    private Task SendWindowsNotificationAsync(string title, string body)
    {
        var script = $@"
            [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
            $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent(
                [Windows.UI.Notifications.ToastTemplateType]::ToastText02)
            $textNodes = $template.GetElementsByTagName('text')
            $textNodes[0].InnerText = '{title.Replace("'", "''")}'
            $textNodes[1].InnerText = '{body.Replace("'", "''")}'
            $toast = [Windows.UI.Notifications.ToastNotification]::new($template)
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('UniCore').Show($toast)
        ";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        System.Diagnostics.Process.Start(psi);
        return Task.CompletedTask;
    }

    private async Task SendLinuxNotificationAsync(string title, string body)
    {
        // notify-send is part of libnotify, available on most Linux distros.
        // Install with: sudo apt install libnotify-bin (Debian/Ubuntu)
        //               sudo dnf install libnotify      (Fedora)
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "notify-send",
            ArgumentList = { "--app-name=UniCore", "--urgency=normal", title, body },
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(psi);
        if (process != null)
            await process.WaitForExitAsync();
    }

    private async Task SendMacNotificationAsync(string title, string body)
    {
        // osascript is built into every macOS installation (10.9+), no dependencies needed.
        // Note: on macOS 10.14+ the user may need to grant notification permissions
        // to the running app the first time — macOS handles this automatically.
        var script = $"display notification \"{body.Replace("\"", "\\\"")}\" with title \"{title.Replace("\"", "\\\"")}\"";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "osascript",
            ArgumentList = { "-e", script },
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process != null)
            await process.WaitForExitAsync();
    }
}