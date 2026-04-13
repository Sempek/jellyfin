using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Server.ServerSetupApp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Migrations.Stages;

internal class CodeMigration(Type migrationType, JellyfinMigrationAttribute metadata, JellyfinMigrationBackupAttribute? migrationBackupAttribute)
{
    public Type MigrationType { get; } = migrationType;

    public JellyfinMigrationAttribute Metadata { get; } = metadata;

    public JellyfinMigrationBackupAttribute? BackupRequirements { get; set; } = migrationBackupAttribute;

    public string BuildCodeMigrationId()
    {
        return Metadata.Order.ToString("yyyyMMddHHmmsss", CultureInfo.InvariantCulture) + "_" + Metadata.Name!;
    }

    private IServiceCollection MigrationServices(IServiceProvider serviceProvider, IStartupLogger logger)
    {
        var childServiceCollection = new ServiceCollection()
            .AddSingleton(serviceProvider)
            .AddSingleton(logger)
            .AddSingleton(typeof(IStartupLogger<>), typeof(NestedStartupLogger<>))
            .AddSingleton<StartupLogTopic>(logger.Topic!);

        foreach (ServiceDescriptor service in serviceProvider.GetRequiredService<IServiceCollection>())
        {
            if (service.Lifetime == ServiceLifetime.Singleton && !service.ServiceType.IsGenericTypeDefinition)
            {
                // Register via instance (not factory) so the child provider treats the object as
                // externally-owned and does NOT dispose it when the child ServiceProvider is disposed.
                // Using a factory delegate here causes the child to take ownership and dispose the
                // parent's shared singletons (e.g. ILoggerFactory), breaking subsequent migration steps.
                var instance = serviceProvider.GetService(service.ServiceType);
                if (instance is not null)
                {
                    childServiceCollection.AddSingleton(service.ServiceType, instance);
                }

                continue;
            }

            childServiceCollection.Add(service);
        }

        return childServiceCollection;
    }

    public async Task Perform(IServiceProvider? serviceProvider, IStartupLogger logger, CancellationToken cancellationToken)
    {
#pragma warning disable CS0618 // Type or member is obsolete
        if (typeof(IMigrationRoutine).IsAssignableFrom(MigrationType))
        {
            if (serviceProvider is null)
            {
                ((IMigrationRoutine)Activator.CreateInstance(MigrationType)!).Perform();
            }
            else
            {
                using var migrationServices = MigrationServices(serviceProvider, logger).BuildServiceProvider();
                ((IMigrationRoutine)ActivatorUtilities.CreateInstance(migrationServices, MigrationType)).Perform();
#pragma warning restore CS0618 // Type or member is obsolete
            }
        }
        else if (typeof(IAsyncMigrationRoutine).IsAssignableFrom(MigrationType))
        {
            if (serviceProvider is null)
            {
                await ((IAsyncMigrationRoutine)Activator.CreateInstance(MigrationType)!).PerformAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var migrationServices = MigrationServices(serviceProvider, logger).BuildServiceProvider();
                await ((IAsyncMigrationRoutine)ActivatorUtilities.CreateInstance(migrationServices, MigrationType)).PerformAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            throw new InvalidOperationException($"The type {MigrationType} does not implement either IMigrationRoutine or IAsyncMigrationRoutine and is not a valid migration type");
        }
    }

    private class NestedStartupLogger<TCategory> : StartupLogger<TCategory>
    {
        public NestedStartupLogger(ILogger logger, StartupLogTopic topic) : base(logger, topic)
        {
        }
    }
}
