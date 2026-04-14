using System.Runtime.InteropServices;

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
        var title = "UniCore - VM Started";
        var message = $"VM \"{vmName}\" is now running.";
        await SendNativeNotificationAsync(title, message);
    }

    public async Task SendVmStoppedNotificationAsync(string vmName, string vmId)
    {
        var title = "UniCore - VM Stopped";
        var message = $"VM \"{vmName}\" has stopped.";
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
        if (await TryTerminalNotifierAsync(title, body))
            return;

        var script = $"display notification \"{EscapeAppleScriptString(body)}\" with title \"{EscapeAppleScriptString(title)}\"";

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

    private static async Task<bool> TryTerminalNotifierAsync(string title, string body)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "terminal-notifier",
                ArgumentList =
                {
                    "-title", title,
                    "-message", body,
                    "-appName", "UniCore"
                },
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
                return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeAppleScriptString(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", " ")
            .Replace("\n", " ");
    }
}
