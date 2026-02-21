namespace unicoreprovider.Services;

public interface IDockerService
{
    /// <summary>
    /// Returns true if a Docker daemon is reachable on any known endpoint
    /// (Windows named pipe or TCP localhost:2375).
    /// </summary>
    Task<bool> IsReachableAsync();

    /// <summary>
    /// Pulls <paramref name="image"/> if not present locally, creates a container with SSH and
    /// the FRP client configured to tunnel SSH through the GCP relay on <paramref name="relayPort"/>,
    /// starts it, and returns the Docker container ID.
    /// </summary>
    Task<string> StartContainerAsync(string name, string image, int relayPort);

    /// <summary>
    /// Returns the host port mapped to SSH (port 22) inside the container.
    /// Returns null if no SSH port mapping is found.
    /// </summary>
    Task<int?> GetContainerSshPortAsync(string containerId);

    /// <summary>Stops and removes a container by its Docker container ID.</summary>
    Task StopContainerAsync(string containerId);

    /// <summary>
    /// Returns a one-shot CPU% and RAM% snapshot for a running container.
    /// </summary>
    Task<(double CpuPercent, double RamPercent)> GetContainerStatsAsync(string containerId);
}
