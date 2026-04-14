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
        string message = $"VM \"{vmName}\" is now running.";
        _logger.LogInformation("Preparing VM-started notification for VM {VmId} ({VmName}).", vmId, vmName);
        await SendNativeNotificationAsync(title, message);
    }

    public async Task SendVmStoppedNotificationAsync(string vmName, string vmId)
    {
        string title = "UniCore – VM Stopped";
        string message = $"VM \"{vmName}\" has stopped.";
        _logger.LogInformation("Preparing VM-completed notification for VM {VmId} ({VmName}).", vmId, vmName);
        await SendNativeNotificationAsync(title, message);
    }

    private async Task SendNativeNotificationAsync(string title, string body)
    {
        try
        {
            _logger.LogInformation("Dispatching native notification on OS: {OS}", RuntimeInformation.OSDescription);
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
        var iconPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "icons", "unicore-notification-icon.png");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "notify-send",
            ArgumentList = { "--app-name=UniCore", "--urgency=normal", $"--icon={iconPath}", title, body },
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(psi);
        if (process != null)
            await process.WaitForExitAsync();
    }

    private async Task SendMacNotificationAsync(string title, string body)
    {
        // Prefer terminal-notifier when available because it tends to behave more
        // consistently from long-running developer processes like dotnet watch.
        _logger.LogInformation("Attempting macOS notification via terminal-notifier.");
        if (await TryTerminalNotifierAsync(title, body))
            return;

        // Fall back to osascript, which is available on all macOS installations.
        _logger.LogInformation("Falling back to macOS notification via osascript.");
        var script = $"display notification \"{EscapeAppleScriptString(body)}\" with title \"{EscapeAppleScriptString(title)}\"";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "osascript",
            ArgumentList = { "-e", script },
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null)
        {
            _logger.LogWarning("macOS notification failed to start osascript.");
            return;
        }

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            _logger.LogWarning("osascript exited with code {ExitCode} while sending a macOS notification.", process.ExitCode);
        else
            _logger.LogInformation("macOS notification sent via osascript.");
    }

    private async Task<bool> TryTerminalNotifierAsync(string title, string body)
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
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
                return false;

            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                _logger.LogInformation("macOS notification sent via terminal-notifier.");
                return true;
            }

            var stderr = await process.StandardError.ReadToEndAsync();
            _logger.LogWarning(
                "terminal-notifier exited with code {ExitCode}. Stderr: {Stderr}",
                process.ExitCode,
                string.IsNullOrWhiteSpace(stderr) ? "(empty)" : stderr.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "terminal-notifier is unavailable or failed to start.");
        }

        return false;
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