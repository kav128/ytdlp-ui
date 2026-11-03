using Ytdlp.Ui.Infrastructure.Persistence.Entities;

namespace Ytdlp.Ui.Infrastructure.Media;

public sealed class ArtifactIntegrity(WorkspaceStorage workspace)
{
    public async Task<bool> IsValidAsync(DownloadArtifactRecord artifact, CancellationToken cancellationToken)
    {
        if (!artifact.IsReady || artifact.Size is null || string.IsNullOrEmpty(artifact.Sha256)) return false;

        try
        {
            var path = workspace.Resolve(artifact.DownloadId, artifact.WorkspaceGeneration, artifact.RelativePath);

            return File.Exists(path)
                && new FileInfo(path).Length == artifact.Size
                && await MediaProcessor.Sha256Async(path, cancellationToken) == artifact.Sha256;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
