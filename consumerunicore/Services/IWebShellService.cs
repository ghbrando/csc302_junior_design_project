using Renci.SshNet;

namespace consumerunicore.Services;

/// <summary>
/// Connection details returned to the WebShell component so it can open an SSH PTY session.
/// </summary>
public sealed record WebShellConnectionInfo(
    string VmId,
    string VmName,
    string Host,
    int Port,
    string Username,
    AuthenticationMethod[] AuthMethods
);

public interface IWebShellService
{
    /// <summary>
    /// Resolves SSH connection info for a VM that belongs to the given user.
    /// Returns (null, errorMessage) when the VM is unavailable or the user is not authorized.
    /// </summary>
    Task<(WebShellConnectionInfo? Info, string? Error)> GetConnectionInfoAsync(string vmId, string userUid);
}
