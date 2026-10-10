namespace Glossator.Core.Packaging;

public interface IPackageDeliveryService
{
    Task DeliverAtomicallyAsync(string sourceStagingFile, string finalDestinationPath, CancellationToken cancellationToken = default);
}
