using System.Text;
using Renci.SshNet;

namespace consumerunicore.Services;

public class WebShellService : IWebShellService
{
    private readonly IFirestoreRepository<VirtualMachine> _vmRepo;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _env;

    public WebShellService(
        IFirestoreRepository<VirtualMachine> vmRepo,
        IConfiguration config,
        IHostEnvironment env)
    {
        _vmRepo = vmRepo;
        _config = config;
        _env = env;
    }

    public async Task<(WebShellConnectionInfo? Info, string? Error)> GetConnectionInfoAsync(
        string vmId, string userUid)
    {
        if (string.IsNullOrWhiteSpace(vmId))
            return (null, "VM ID is required.");

        var vm = await _vmRepo.GetByIdAsync(vmId);
        if (vm == null)
            return (null, "VM not found.");

        if (!string.Equals(vm.Status, "Running", StringComparison.OrdinalIgnoreCase))
            return (null, $"VM is not running (status: {vm.Status}).");

        if (!string.Equals(vm.Client, userUid, StringComparison.Ordinal))
            return (null, "You are not authorized to access this VM.");

        var (host, port) = ResolveTarget(vm);
        if (host == null)
            return (null, "No SSH endpoint is available for this VM. Check that SshPort or RelayPort is set.");

        var username = _config["WebShell:SshUsername"] ?? "consumer";
        var authMethods = BuildAuthMethods(username);
        if (authMethods.Length == 0)
            return (null, "No SSH authentication method is configured on the server.");

        return (new WebShellConnectionInfo(vm.VmId, vm.Name, host, port, username, authMethods), null);
    }

    private (string? host, int port) ResolveTarget(VirtualMachine vm)
    {
        // Prefer the FRP relay when available — works for both local and remote connections.
        var relayHost = _config["FrpRelay:ServerAddr"] ?? _config["WebShell:RelayHost"];
        if (!string.IsNullOrWhiteSpace(relayHost) && vm.RelayPort.HasValue)
            return (relayHost, vm.RelayPort.Value);

        // Fallback: connect directly to the provider host on the Docker-mapped SSH port.
        // Only works when the consumer is on the same machine as the provider.
        if (vm.SshPort.HasValue)
        {
            var localHost = _config["WebShell:ProviderHost"] ?? "localhost";
            return (localHost, vm.SshPort.Value);
        }

        return (null, 0);
    }

    private AuthenticationMethod[] BuildAuthMethods(string username)
    {
        var methods = new List<AuthenticationMethod>();

        var key = _config["WebShell:PrivateKey"];
        var keyPath = _config["WebShell:PrivateKeyPath"];
        var passphrase = _config["WebShell:PrivateKeyPassphrase"];

        if (!string.IsNullOrWhiteSpace(key))
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(key.Replace("\\n", "\n")));
            var keyFile = string.IsNullOrWhiteSpace(passphrase)
                ? new PrivateKeyFile(ms)
                : new PrivateKeyFile(ms, passphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(username, keyFile));
        }
        else if (!string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
        {
            var keyFile = string.IsNullOrWhiteSpace(passphrase)
                ? new PrivateKeyFile(keyPath)
                : new PrivateKeyFile(keyPath, passphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(username, keyFile));
        }

        var password = _config["WebShell:SshPassword"];
        // Fall back to the default container password if none is configured.
        if (string.IsNullOrWhiteSpace(password))
            password = "consumer123";

        if (!string.IsNullOrWhiteSpace(password))
            methods.Add(new PasswordAuthenticationMethod(username, password));

        return methods.ToArray();
    }
}
