namespace unicoreprovider.Services;

public class SnapshotService : ISnapshotService
{
    private readonly IDockerService _dockerService;

    public SnapshotService(IDockerService dockerService)
    {
        _dockerService = dockerService ?? throw new ArgumentNullException(nameof(dockerService));
    }

    public async Task PullSnapshotAsync(string? imageTag, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imageTag))
            return;

        await _dockerService.PullImageAsync(imageTag, ct);
    }
}
