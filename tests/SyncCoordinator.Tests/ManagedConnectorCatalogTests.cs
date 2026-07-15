using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SyncCoordinator.Contracts;
using SyncCoordinator.Core;
using SyncCoordinator.Infrastructure;
using SyncCoordinator.Infrastructure.Connectors;
using SyncCoordinator.Infrastructure.Persistence;

namespace SyncCoordinator.Tests;

public sealed class ManagedConnectorCatalogTests
{
    [Fact]
    public void DependencyInjectionDoesNotRegisterFixedConfigurationConnectors()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SyncCoordinator:Connectors:Systems:0:SystemCode"] = "LEGACY",
                ["SyncCoordinator:Connectors:Systems:0:Provider"] = "SqlServer",
                ["SyncCoordinator:Connectors:Systems:0:ConnectionStringName"] = "legacy-db",
                ["SyncCoordinator:Connectors:Systems:0:Enabled"] = "true",
                ["ConnectionStrings:legacy-db"] = "Server=legacy"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddSyncCoordinator(configuration);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ISyncConnector));
        var catalog = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IConnectorCatalog));
        Assert.Equal(typeof(ManagedConnectorCatalog), catalog.ImplementationType);
    }

    [Fact]
    public async Task CatalogUsesEnabledConfiguredSystemsAndCachesOneScopeSnapshot()
    {
        var source = new FakeDefinitionSource(
        [
            Definition("CRM", enabled: true, protectedConnectionString: "crm-secret"),
            Definition("DISABLED", enabled: false, protectedConnectionString: "disabled-secret"),
            Definition("MISSING", enabled: true, protectedConnectionString: null)
        ]);
        var factory = new FakeConnectorFactory();
        var catalog = new ManagedConnectorCatalog(source, factory);

        var first = await catalog.GetAllAsync(CancellationToken.None);
        source.Definitions = [Definition("FIELD", enabled: true, protectedConnectionString: "field-secret")];
        var second = await catalog.GetAllAsync(CancellationToken.None);

        Assert.Equal(["CRM"], first.Select(x => x.SystemCode));
        Assert.Same(first.Single(), second.Single());
        Assert.Equal(1, source.LoadCount);
        Assert.Equal(["CRM"], factory.CreatedSystemCodes);

        var nextScope = new ManagedConnectorCatalog(source, factory);
        var refreshed = await nextScope.GetAllAsync(CancellationToken.None);
        Assert.Equal(["FIELD"], refreshed.Select(x => x.SystemCode));
        Assert.Equal(2, source.LoadCount);
    }

    [Fact]
    public async Task CatalogGetRequiredIsCaseInsensitive()
    {
        var catalog = new ManagedConnectorCatalog(
            new FakeDefinitionSource([Definition("CRM", true, "secret")]),
            new FakeConnectorFactory());

        var connector = await catalog.GetRequiredAsync("crm", CancellationToken.None);

        Assert.Equal("CRM", connector.SystemCode);
    }

    [Fact]
    public async Task CatalogGetRequiredRejectsDisabledOrUnconfiguredSystem()
    {
        var catalog = new ManagedConnectorCatalog(
            new FakeDefinitionSource([Definition("CRM", false, "secret")]),
            new FakeConnectorFactory());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.GetRequiredAsync("CRM", CancellationToken.None));

        Assert.Contains("無効か、接続情報が未設定", exception.Message);
    }

    [Fact]
    public void FactoryDecryptsManagedConnectionAndUsesProviderAndSystemCode()
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        var protector = new ProtectedConnectionStringService(dataProtection);
        using var dbContext = CreateContext();
        var factory = new ManagedConnectorFactory(
            protector,
            new RelationalMappingProvider(dbContext));
        var protectedConnectionString = protector.Protect(
            "Server=(localdb)\\mssqllocaldb;Database=SyncTarget;Trusted_Connection=True");

        var connector = factory.Create(new ManagedConnectorDefinition(
            "CRM",
            "sqlserver",
            true,
            protectedConnectionString));

        Assert.Equal("CRM", connector.SystemCode);
    }

    [Fact]
    public void FactoryRejectsUnsupportedProvider()
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        var protector = new ProtectedConnectionStringService(dataProtection);
        using var dbContext = CreateContext();
        var factory = new ManagedConnectorFactory(
            protector,
            new RelationalMappingProvider(dbContext));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(
            new ManagedConnectorDefinition(
                "UNKNOWN",
                "Oracle",
                true,
                protector.Protect("Data Source=unknown"))));

        Assert.Contains("未対応", exception.Message);
    }

    private static ManagedConnectorDefinition Definition(
        string systemCode,
        bool enabled,
        string? protectedConnectionString) =>
        new(systemCode, "SqlServer", enabled, protectedConnectionString);

    private static CoordinatorDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoordinatorDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=Unused;Trusted_Connection=True")
            .Options;
        return new CoordinatorDbContext(options);
    }

    private sealed class FakeDefinitionSource(IReadOnlyList<ManagedConnectorDefinition> definitions)
        : IManagedConnectorDefinitionSource
    {
        public IReadOnlyList<ManagedConnectorDefinition> Definitions { get; set; } = definitions;
        public int LoadCount { get; private set; }

        public Task<IReadOnlyList<ManagedConnectorDefinition>> GetAllAsync(
            CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.FromResult(Definitions);
        }
    }

    private sealed class FakeConnectorFactory : IManagedConnectorFactory
    {
        public List<string> CreatedSystemCodes { get; } = [];

        public ISyncConnector Create(ManagedConnectorDefinition definition)
        {
            CreatedSystemCodes.Add(definition.SystemCode);
            return new FakeConnector(definition.SystemCode);
        }
    }

    private sealed class FakeConnector(string systemCode) : ISyncConnector
    {
        public string SystemCode { get; } = systemCode;

        public Task<IReadOnlyList<ChangeQueueItem>> ReadChangesAsync(
            long afterQueueId,
            int take,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChangeQueueItem>>([]);

        public Task<bool> WasAppliedMessageAsync(Guid messageId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<SyncMessage?> ReadLatestMessageAsync(
            ChangeQueueItem change,
            CancellationToken cancellationToken) =>
            Task.FromResult<SyncMessage?>(null);

        public Task<EntityPayload?> ReadCurrentAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult<EntityPayload?>(null);

        public Task<ApplyResult> ApplyAsync(
            ApplyRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ApplyResult(ApplyStatus.Applied));
    }
}
