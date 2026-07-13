namespace SyncCoordinator.Infrastructure.Connectors;

public enum RelationalProvider
{
    SqlServer = 0,
    MySql = 1
}

public sealed class RelationalConnectorOptions
{
    public List<RelationalSystemOptions> Systems { get; set; } = [];
}

public sealed class RelationalSystemOptions
{
    public required string SystemCode { get; set; }
    public RelationalProvider Provider { get; set; }
    public required string ConnectionStringName { get; set; }
    public bool Enabled { get; set; }
}
