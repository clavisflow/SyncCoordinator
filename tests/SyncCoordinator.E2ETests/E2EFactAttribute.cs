namespace SyncCoordinator.E2ETests;

public sealed class E2EFactAttribute : FactAttribute
{
    private const string EnabledVariable = "SYNC_COORDINATOR_RUN_E2E";

    public E2EFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnabledVariable),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = $"Set {EnabledVariable}=true to run Docker-based E2E tests.";
        }
    }
}
