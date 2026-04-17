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
    private readonly IWebHostEnvironment _env;

    public NotificationService(ILogger<NotificationService> logger, IWebHostEnvironment env)
    {
        _logger = logger;
        _env = env;
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

    private static bool _appIdRegistered;

    private Task SendWindowsNotificationAsync(string title, string body)
    {
        var iconPath = Path.Combine(_env.WebRootPath, "favicon-96x96.png");

        if (!_appIdRegistered)
        {
            RegisterAppId(iconPath);
            _appIdRegistered = true;
        }

        var xmlTitle = title.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&#39;");
        var xmlBody  = body .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&#39;");

        // appLogoOverride shows the icon in the toast popup; hint-crop=circle gives it the
        // rounded look consistent with Windows 11 app notifications.
        var iconSrc = File.Exists(iconPath)
            ? "file:///" + iconPath.Replace('\\', '/')
            : string.Empty;
        var imageTag = string.IsNullOrEmpty(iconSrc)
            ? string.Empty
            : $"<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"{iconSrc}\"/>";

        var script =
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null\n" +
            "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null\n" +
            "$xml = New-Object Windows.Data.Xml.Dom.XmlDocument\n" +
            "$xml.LoadXml('<toast><visual><binding template=\"ToastGeneric\">" + imageTag + "<text>" + xmlTitle + "</text><text>" + xmlBody + "</text></binding></visual></toast>')\n" +
            "$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)\n" +
            "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('UniCore').Show($toast)\n";

        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -WindowStyle Hidden -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        System.Diagnostics.Process.Start(psi);
        return Task.CompletedTask;
    }

    private static void RegisterAppId(string iconPath)
    {
        // Windows requires the AppUserModelId to exist under HKCU\Software\Classes\AppUserModelId\<id>
        // before CreateToastNotifier(id) will deliver toasts. Register once per machine user.
        const string regPath = @"Software\Classes\AppUserModelId\UniCore";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(regPath);
            key.SetValue("DisplayName", "UniCore");
            if (File.Exists(iconPath))
                key.SetValue("IconUri", iconPath);
        }
        catch { /* non-fatal */ }
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