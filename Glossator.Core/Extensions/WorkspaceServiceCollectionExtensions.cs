namespace Glossator.Core.Extensions;

using Glossator.Core.Packaging;
using Glossator.Core.Workspace;
using Microsoft.Extensions.DependencyInjection;

public static class WorkspaceServiceCollectionExtensions
{
    public static IServiceCollection AddGlossatorWorkspace(this IServiceCollection services)
    {
        services.AddSingleton<IPackageDeliveryService, PackageDeliveryService>();
        services.AddSingleton<IWorkspaceManager, WorkspaceManager>();
        return services;
    }
}
