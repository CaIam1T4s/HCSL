namespace HCSL.Application.Abstractions;

/// <summary>Open a verified local directory in the desktop file manager; never execute its contents.</summary>
public interface ILocalDirectoryOpener
{
    Task RevealAsync(string directory, CancellationToken cancellationToken = default) =>
        OpenAsync(Path.GetDirectoryName(directory) ?? directory, cancellationToken);

    Task OpenAsync(string directory, CancellationToken cancellationToken = default);
}
