using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Npgsql;
using SyncCoordinator.Core;
using SyncCoordinator.Infrastructure;

namespace SyncCoordinator.E2ETests;

public sealed partial class PortalToCrmTests
{
    [E2EFact]
    public async Task DemoRoutesSynchronizeBidirectionally()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var cancellationToken = timeout.Token;
        var keyRingPath = CreateKeyRingDirectory();

        try
        {
            await using var builder = await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.SyncCoordinator_AppHost>(
                    ["RunMode=E2E", $"E2E:KeyRingPath={keyRingPath}"],
                    cancellationToken);
            await using var app = await builder.BuildAsync(cancellationToken);

            await app.StartAsync(cancellationToken);
            await Task.WhenAll(
                app.ResourceNotifications.WaitForResourceHealthyAsync(
                    "coordinator-web",
                    WaitBehavior.StopOnResourceUnavailable,
                    cancellationToken),
                app.ResourceNotifications.WaitForResourceHealthyAsync(
                    "coordinator-worker",
                    WaitBehavior.StopOnResourceUnavailable,
                    cancellationToken));
            using var webClient = app.CreateHttpClient("coordinator-web", "http");
            await WaitForWebReadyAsync(webClient, cancellationToken);
            await BrowserAuthenticationSmoke.VerifyAsync(
                app.GetEndpoint("coordinator-web", "http"),
                cancellationToken);

            var coordinatorConnection = RequireConnectionString(
                await app.GetConnectionStringAsync("coordinator-db", cancellationToken),
                "coordinator-db");
            var portalConnection = AddMySqlConnectorOptions(RequireConnectionString(
                await app.GetConnectionStringAsync("demo-customer-portal-db", cancellationToken),
                "demo-customer-portal-db"));
            var crmConnection = RequireConnectionString(
                await app.GetConnectionStringAsync("demo-crm-db", cancellationToken),
                "demo-crm-db");
            var fieldConnection = PreparePostgreSqlConnectionString(RequireConnectionString(
                await app.GetConnectionStringAsync("demo-field-service-db", cancellationToken),
                "demo-field-service-db"));

            await PrepareDemoRoutesAsync(
                coordinatorConnection,
                keyRingPath,
                ["Customer Portal - CRM", "CRM - Field Service"],
                cancellationToken);
            await using var webhookReceiver = await WebhookCaptureServer.StartAsync(cancellationToken);
            var webhookRegistration = await RegisterWebhookEndpointAsync(
                coordinatorConnection,
                keyRingPath,
                webhookReceiver.Endpoint,
                "Aspire E2E receiver",
                [WebhookEventTypes.SyncUpserted],
                cancellationToken);

            var caseNumber = $"E2E-{Guid.NewGuid():N}";
            var expectedCustomer = "E2E Customer";
            var expectedSubject = "Aspire E2E synchronization";
            await InsertPortalCaseAsync(
                portalConnection,
                caseNumber,
                expectedCustomer,
                expectedSubject,
                cancellationToken);

            var actual = await WaitForCrmCaseAsync(
                crmConnection,
                caseNumber,
                TimeSpan.FromSeconds(90),
                cancellationToken);

            Assert.Equal(expectedCustomer, actual.ContactName);
            Assert.Equal(expectedSubject, actual.CaseTitle);
            Assert.Equal("New", actual.WorkflowState);
            Assert.Equal("PORTAL", actual.SourceCode);

            var webhook = await webhookReceiver.WaitForAsync(
                request => IsMatchingSyncWebhook(request, caseNumber, "PORTAL", "CRM"),
                TimeSpan.FromSeconds(90),
                cancellationToken);
            var webhookEventId = ValidateSyncWebhook(
                webhook,
                webhookRegistration.Secret,
                caseNumber,
                "PORTAL",
                "CRM");
            await WaitForWebhookDeliveryAsync(
                coordinatorConnection,
                webhookEventId,
                expectedAttemptCount: 1,
                TimeSpan.FromSeconds(90),
                cancellationToken);

            const string expectedStatus = "InProgress";
            const string expectedResponse = "Updated by the CRM E2E step";
            var crmUpdateMessageId = await UpdateCrmCaseAsync(
                crmConnection,
                caseNumber,
                expectedStatus,
                expectedResponse,
                cancellationToken);

            var portalResult = await WaitForPortalCaseAsync(
                portalConnection,
                caseNumber,
                expectedStatus,
                expectedResponse,
                TimeSpan.FromSeconds(90),
                cancellationToken);

            Assert.Equal(expectedStatus, portalResult.Status);
            Assert.Equal(expectedResponse, portalResult.ResponseMessage);
            await WaitForInboxCompletionAsync(
                coordinatorConnection,
                crmUpdateMessageId,
                "PORTAL",
                TimeSpan.FromSeconds(90),
                cancellationToken);

            var eligibilityWorkOrderNumber = $"E2E-ELIGIBILITY-{Guid.NewGuid():N}";
            const string eligibilityProblem = "Verify related-table eligibility changes";
            var unassignedMessageId = await InsertUnassignedCrmWorkOrderAsync(
                crmConnection,
                eligibilityWorkOrderNumber,
                caseNumber,
                eligibilityProblem,
                cancellationToken);
            await WaitForInboxCompletionAsync(
                coordinatorConnection,
                unassignedMessageId,
                "FIELD",
                TimeSpan.FromSeconds(90),
                cancellationToken);
            Assert.Null(await ReadFieldWorkOrderAsync(
                fieldConnection,
                eligibilityWorkOrderNumber,
                cancellationToken));

            var emptyAssignmentMessageId = await InsertCrmWorkOrderAssignmentAsync(
                crmConnection,
                eligibilityWorkOrderNumber,
                staffNumber: null,
                cancellationToken);
            await WaitForInboxCompletionAsync(
                coordinatorConnection,
                emptyAssignmentMessageId,
                "FIELD",
                TimeSpan.FromSeconds(90),
                cancellationToken);
            Assert.Null(await ReadFieldWorkOrderAsync(
                fieldConnection,
                eligibilityWorkOrderNumber,
                cancellationToken));

            var eligibleMessageId = await UpdateCrmWorkOrderAssignmentAsync(
                crmConnection,
                eligibilityWorkOrderNumber,
                "E2E-STAFF-ELIGIBLE",
                cancellationToken);
            var eligibleFieldWorkOrder = await WaitForFieldWorkOrderAsync(
                fieldConnection,
                eligibilityWorkOrderNumber,
                TimeSpan.FromSeconds(90),
                cancellationToken);
            Assert.Equal(expectedCustomer, eligibleFieldWorkOrder.CustomerDisplayName);
            Assert.Equal(eligibilityProblem, eligibleFieldWorkOrder.ProblemSummary);
            await WaitForInboxCompletionAsync(
                coordinatorConnection,
                eligibleMessageId,
                "FIELD",
                TimeSpan.FromSeconds(90),
                cancellationToken);

            const string projectedCustomer = "E2E Related Projection Customer";
            var projectionChangeMessageId = await UpdateCrmCaseContactNameAsync(
                crmConnection,
                caseNumber,
                eligibilityWorkOrderNumber,
                projectedCustomer,
                cancellationToken);
            var projectedFieldWorkOrder = await WaitForFieldWorkOrderCustomerAsync(
                fieldConnection,
                eligibilityWorkOrderNumber,
                projectedCustomer,
                TimeSpan.FromSeconds(90),
                cancellationToken);
            Assert.Equal(eligibilityProblem, projectedFieldWorkOrder.ProblemSummary);
            await WaitForInboxCompletionAsync(
                coordinatorConnection,
                projectionChangeMessageId,
                "FIELD",
                TimeSpan.FromSeconds(90),
                cancellationToken);

            var eligibilityRemovalMessageId = await UpdateCrmWorkOrderAssignmentAsync(
                crmConnection,
                eligibilityWorkOrderNumber,
                staffNumber: null,
                cancellationToken);
            await WaitForInboxCompletionAsync(
                coordinatorConnection,
                eligibilityRemovalMessageId,
                "FIELD",
                TimeSpan.FromSeconds(90),
                cancellationToken);
            await WaitForFieldWorkOrderDeletionAsync(
                fieldConnection,
                eligibilityWorkOrderNumber,
                TimeSpan.FromSeconds(90),
                cancellationToken);
            Assert.NotNull(await ReadCrmWorkOrderAsync(
                crmConnection,
                eligibilityWorkOrderNumber,
                cancellationToken));

            var workOrderNumber = $"E2E-WO-{Guid.NewGuid():N}";
            const string expectedWorkOrderCustomer = "E2E Field Customer";
            const string expectedProblemSummary = "Verify mapped status codes";
            var crmWorkOrderInsertMessageId = await InsertCrmWorkOrderAsync(
                crmConnection,
                workOrderNumber,
                caseNumber,
                expectedWorkOrderCustomer,
                expectedProblemSummary,
                cancellationToken);

            var fieldWorkOrder = await WaitForFieldWorkOrderAsync(
                fieldConnection,
                workOrderNumber,
                TimeSpan.FromSeconds(90),
                cancellationToken);

            Assert.Equal(expectedWorkOrderCustomer, fieldWorkOrder.CustomerDisplayName);
            Assert.Equal(expectedProblemSummary, fieldWorkOrder.ProblemSummary);
            Assert.Equal("in_progress", fieldWorkOrder.Status);
            Assert.Equal("CRM", fieldWorkOrder.SourceCode);
            var crmToFieldInbox = await WaitForInboxCompletionAsync(
                coordinatorConnection,
                crmWorkOrderInsertMessageId,
                "FIELD",
                TimeSpan.FromSeconds(90),
                cancellationToken);
            Assert.Equal(1, crmToFieldInbox.AttemptCount);
            Assert.Null(crmToFieldInbox.LastError);

            const string expectedTechnician = "E2E Technician";
            const string expectedWorkResult = "Completed in the Field Service E2E step";
            var fieldUpdateMessageId = await CompleteFieldWorkOrderAsync(
                fieldConnection,
                workOrderNumber,
                expectedTechnician,
                expectedWorkResult,
                cancellationToken);

            var crmWorkOrder = await WaitForCompletedCrmWorkOrderAsync(
                crmConnection,
                workOrderNumber,
                expectedTechnician,
                expectedWorkResult,
                TimeSpan.FromSeconds(90),
                cancellationToken);

            Assert.Equal("Completed", crmWorkOrder.Status);
            Assert.Equal(expectedTechnician, crmWorkOrder.TechnicianName);
            Assert.Equal(expectedWorkResult, crmWorkOrder.WorkResult);
            await WaitForInboxCompletionAsync(
                coordinatorConnection,
                fieldUpdateMessageId,
                "CRM",
                TimeSpan.FromSeconds(90),
                cancellationToken);

            var fieldDeleteMessageId = await DeleteFieldWorkOrderAsync(
                fieldConnection,
                workOrderNumber,
                cancellationToken);
            await WaitForInboxCompletionAsync(
                coordinatorConnection,
                fieldDeleteMessageId,
                "CRM",
                TimeSpan.FromSeconds(90),
                cancellationToken);
            await WaitForCrmWorkOrderDeletionAsync(
                crmConnection,
                workOrderNumber,
                TimeSpan.FromSeconds(90),
                cancellationToken);

            var recoveryWorkOrderNumber = $"E2E-RECOVERY-{Guid.NewGuid():N}";
            const string recoveryCustomer = "E2E Recovery Customer";
            const string recoveryInitialProblem = "Before destination connection failure";
            const string recoveryProblem = "Recover after destination connection failure";
            var recoverySeedMessageId = await InsertCrmWorkOrderAsync(
                crmConnection,
                recoveryWorkOrderNumber,
                caseNumber,
                recoveryCustomer,
                recoveryInitialProblem,
                cancellationToken);
            await WaitForInboxCompletionAsync(
                coordinatorConnection,
                recoverySeedMessageId,
                "FIELD",
                TimeSpan.FromSeconds(90),
                cancellationToken);
            var recoverySeed = await WaitForFieldWorkOrderAsync(
                fieldConnection,
                recoveryWorkOrderNumber,
                TimeSpan.FromSeconds(90),
                cancellationToken);
            Assert.Equal(recoveryInitialProblem, recoverySeed.ProblemSummary);

            await WaitForSourceQueuesQuiescentAsync(
                coordinatorConnection,
                portalConnection,
                crmConnection,
                fieldConnection,
                TimeSpan.FromSeconds(30),
                cancellationToken);

            var isolatedCaseNumber = $"E2E-ISOLATION-{Guid.NewGuid():N}";
            const string isolatedCustomer = "E2E Isolated Source Customer";
            const string isolatedSubject = "Continue after another source fails";
            var outageStartedAt = TimeProvider.System.GetUtcNow();
            var originalFieldConnection = await MakeSystemDatabaseUnavailableAsync(
                coordinatorConnection,
                keyRingPath,
                "FIELD",
                cancellationToken);
            Guid recoveryMessageId;
            var failedAttemptCount = 0;
            Exception? fieldOutageFailure = null;
            try
            {
                var pollingFailure = await WaitForOperationalEventAsync(
                    coordinatorConnection,
                    OperationalEventCodes.SynchronizationPollingFailed,
                    "FIELD",
                    outageStartedAt,
                    TimeSpan.FromSeconds(30),
                    cancellationToken);
                Assert.Contains("Npgsql", pollingFailure, StringComparison.OrdinalIgnoreCase);

                recoveryMessageId = await UpdateCrmWorkOrderProblemAsync(
                    crmConnection,
                    recoveryWorkOrderNumber,
                    recoveryProblem,
                    cancellationToken);
                var isolatedMessageId = await InsertPortalCaseAsync(
                    portalConnection,
                    isolatedCaseNumber,
                    isolatedCustomer,
                    isolatedSubject,
                    cancellationToken);
                var failedInbox = await WaitForInboxStateAsync(
                    coordinatorConnection,
                    recoveryMessageId,
                    "FIELD",
                    "Failed",
                    TimeSpan.FromSeconds(30),
                    cancellationToken);
                Assert.True(failedInbox.AttemptCount >= 1);
                failedAttemptCount = failedInbox.AttemptCount;
                Assert.NotNull(failedInbox.LastError);
                Assert.Contains("Npgsql", failedInbox.LastError, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(recoveryInitialProblem, (await ReadFieldWorkOrderAsync(
                    fieldConnection,
                    recoveryWorkOrderNumber,
                    cancellationToken))?.ProblemSummary);

                await WaitForInboxCompletionAsync(
                    coordinatorConnection,
                    isolatedMessageId,
                    "CRM",
                    TimeSpan.FromSeconds(30),
                    cancellationToken);
                var isolatedCrmCase = await WaitForCrmCaseAsync(
                    crmConnection,
                    isolatedCaseNumber,
                    TimeSpan.FromSeconds(30),
                    cancellationToken);
                Assert.Equal(isolatedCustomer, isolatedCrmCase.ContactName);
                Assert.Equal(isolatedSubject, isolatedCrmCase.CaseTitle);
            }
            catch (Exception exception)
            {
                fieldOutageFailure = exception;
                throw;
            }
            finally
            {
                await RunCleanupAsync(
                    fieldOutageFailure,
                    "Restore FIELD database connection",
                    async () =>
                    {
                        using var restoreTimeout =
                            new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await SaveSystemDatabaseConnectionAsync(
                            coordinatorConnection,
                            keyRingPath,
                            originalFieldConnection,
                            restoreTimeout.Token);
                    });
            }

            var recoveredInbox = await WaitForInboxCompletionAsync(
                coordinatorConnection,
                recoveryMessageId,
                "FIELD",
                TimeSpan.FromSeconds(90),
                cancellationToken);
            Assert.True(recoveredInbox.AttemptCount > failedAttemptCount);
            Assert.Null(recoveredInbox.LastError);
            var recoveredWorkOrder = await WaitForFieldWorkOrderProblemAsync(
                fieldConnection,
                recoveryWorkOrderNumber,
                recoveryProblem,
                TimeSpan.FromSeconds(90),
                cancellationToken);
            Assert.Equal(recoveryCustomer, recoveredWorkOrder.CustomerDisplayName);
            Assert.Equal(recoveryProblem, recoveredWorkOrder.ProblemSummary);
            Assert.Equal("in_progress", recoveredWorkOrder.Status);
            var recoveryRouteId = await ReadRouteIdAsync(
                coordinatorConnection,
                "CRM - Field Service",
                cancellationToken);
            var recoveryDeliveryMessageId = DeliveryMessageId.Create(
                recoveryMessageId,
                recoveryRouteId,
                "FIELD");
            Assert.Equal(1, await CountFieldAppliedMessageAsync(
                fieldConnection,
                recoveryDeliveryMessageId,
                cancellationToken));

            var restartCaseNumber = $"E2E-RESTART-{Guid.NewGuid():N}";
            const string restartCustomer = "E2E Restart Customer";
            const string restartSubject = "Process queued change after Worker restart";
            var workerStopped = false;
            Guid restartMessageId;
            long restartQueueId;
            long checkpointWhileStopped;
            Exception? stoppedWorkerScenarioFailure = null;
            try
            {
                await StopWorkerAsync(
                    app,
                    cancellationToken,
                    () => workerStopped = true);
                checkpointWhileStopped = await ReadCheckpointAsync(
                    coordinatorConnection,
                    "PORTAL",
                    cancellationToken);
                restartMessageId = await InsertPortalCaseAsync(
                    portalConnection,
                    restartCaseNumber,
                    restartCustomer,
                    restartSubject,
                    cancellationToken);
                restartQueueId = await ReadPortalQueueIdAsync(
                    portalConnection,
                    restartMessageId,
                    cancellationToken);

                Assert.True(restartQueueId > checkpointWhileStopped);
                Assert.Null(await ReadInboxStatusAsync(
                    coordinatorConnection,
                    restartMessageId,
                    "CRM",
                    cancellationToken));
                Assert.Null(await ReadCrmCaseAsync(
                    crmConnection,
                    restartCaseNumber,
                    cancellationToken));
                Assert.Equal(
                    checkpointWhileStopped,
                    await ReadCheckpointAsync(
                        coordinatorConnection,
                        "PORTAL",
                        cancellationToken));
            }
            catch (Exception exception)
            {
                stoppedWorkerScenarioFailure = exception;
                throw;
            }
            finally
            {
                if (workerStopped)
                {
                    await RunCleanupAsync(
                        stoppedWorkerScenarioFailure,
                        "Restart Worker after stopped-worker scenario",
                        async () =>
                        {
                            using var restartTimeout =
                                new CancellationTokenSource(TimeSpan.FromSeconds(60));
                            await StartWorkerAsync(app, restartTimeout.Token);
                        });
                }
            }

            var restartInbox = await WaitForInboxCompletionAsync(
                coordinatorConnection,
                restartMessageId,
                "CRM",
                TimeSpan.FromSeconds(90),
                cancellationToken);
            Assert.Equal(1, restartInbox.AttemptCount);
            var restartedCrmCase = await WaitForCrmCaseAsync(
                crmConnection,
                restartCaseNumber,
                TimeSpan.FromSeconds(90),
                cancellationToken);
            Assert.Equal(restartCustomer, restartedCrmCase.ContactName);
            Assert.Equal(restartSubject, restartedCrmCase.CaseTitle);
            await WaitForCheckpointAsync(
                coordinatorConnection,
                "PORTAL",
                restartQueueId,
                TimeSpan.FromSeconds(30),
                cancellationToken);
            var restartRouteId = await ReadRouteIdAsync(
                coordinatorConnection,
                "Customer Portal - CRM",
                cancellationToken);
            Assert.Equal(1, await CountCrmAppliedMessageAsync(
                crmConnection,
                DeliveryMessageId.Create(restartMessageId, restartRouteId, "CRM"),
                cancellationToken));

            const string leaseRecoverySubject = "Recover processing Inbox after Worker interruption";
            var midProcessingWorkerStopped = false;
            var checkpointBeforeLeaseRecovery = await ReadCheckpointAsync(
                coordinatorConnection,
                "PORTAL",
                cancellationToken);
            Guid leaseRecoveryMessageId;
            long leaseRecoveryQueueId;
            InboxStatus processingInbox;
            Exception? leaseRecoveryScenarioFailure = null;
            try
            {
                await using (var blockerConnection = new SqlConnection(crmConnection))
                {
                    await blockerConnection.OpenAsync(cancellationToken);
                    await using var blockerTransaction =
                        (SqlTransaction)await blockerConnection.BeginTransactionAsync(cancellationToken);
                    Exception? leaseTransactionFailure = null;
                    try
                    {
                        await AcquireCrmCaseWriteLockAsync(
                            blockerConnection,
                            blockerTransaction,
                            restartCaseNumber,
                            cancellationToken);
                        leaseRecoveryMessageId = await UpdatePortalCaseSubjectWithoutTimestampAsync(
                            portalConnection,
                            restartCaseNumber,
                            leaseRecoverySubject,
                            cancellationToken);
                        leaseRecoveryQueueId = await ReadPortalQueueIdAsync(
                            portalConnection,
                            leaseRecoveryMessageId,
                            cancellationToken);
                        Assert.True(leaseRecoveryQueueId > checkpointBeforeLeaseRecovery);
                        processingInbox = await WaitForInboxStateAsync(
                            coordinatorConnection,
                            leaseRecoveryMessageId,
                            "CRM",
                            "Processing",
                            TimeSpan.FromSeconds(30),
                            cancellationToken);
                        Assert.Equal(1, processingInbox.AttemptCount);
                        Assert.NotNull(processingInbox.LockedUntilUtc);
                        Assert.True(processingInbox.LockedUntilUtc > TimeProvider.System.GetUtcNow());

                        await StopWorkerAsync(
                            app,
                            cancellationToken,
                            () => midProcessingWorkerStopped = true);
                    }
                    catch (Exception exception)
                    {
                        leaseTransactionFailure = exception;
                        throw;
                    }
                    finally
                    {
                        await RunCleanupAsync(
                            leaseTransactionFailure,
                            "Rollback CRM lease-recovery blocker transaction",
                            async () =>
                            {
                                using var releaseTimeout =
                                    new CancellationTokenSource(TimeSpan.FromSeconds(10));
                                await blockerTransaction.RollbackAsync(releaseTimeout.Token);
                            });
                    }
                }

                var strandedInbox = await ReadInboxStatusAsync(
                    coordinatorConnection,
                    leaseRecoveryMessageId,
                    "CRM",
                    cancellationToken);
                Assert.NotNull(strandedInbox);
                Assert.Equal("Processing", strandedInbox.State);
                Assert.Equal(1, strandedInbox.AttemptCount);
                Assert.NotNull(strandedInbox.LockedUntilUtc);
                Assert.Equal(processingInbox.LockedUntilUtc, strandedInbox.LockedUntilUtc);
                Assert.True(strandedInbox.LockedUntilUtc > TimeProvider.System.GetUtcNow());
                Assert.Equal(
                    checkpointBeforeLeaseRecovery,
                    await ReadCheckpointAsync(
                        coordinatorConnection,
                        "PORTAL",
                        cancellationToken));
                var blockedCrmCase = await ReadCrmCaseAsync(
                    crmConnection,
                    restartCaseNumber,
                    cancellationToken);
                Assert.NotNull(blockedCrmCase);
                Assert.Equal(restartSubject, blockedCrmCase.CaseTitle);
                Assert.Equal(0, await CountCrmAppliedMessageAsync(
                    crmConnection,
                    DeliveryMessageId.Create(
                        leaseRecoveryMessageId,
                        restartRouteId,
                        "CRM"),
                    cancellationToken));
                await ExpireInboxLeaseAsync(
                    coordinatorConnection,
                    leaseRecoveryMessageId,
                    restartRouteId,
                    "CRM",
                    cancellationToken);
            }
            catch (Exception exception)
            {
                leaseRecoveryScenarioFailure = exception;
                throw;
            }
            finally
            {
                if (midProcessingWorkerStopped)
                {
                    await RunCleanupAsync(
                        leaseRecoveryScenarioFailure,
                        "Restart Worker after lease-recovery scenario",
                        async () =>
                        {
                            using var restartTimeout =
                                new CancellationTokenSource(TimeSpan.FromSeconds(60));
                            await StartWorkerAsync(app, restartTimeout.Token);
                        });
                }
            }

            var leaseRecoveredInbox = await WaitForInboxCompletionAsync(
                coordinatorConnection,
                leaseRecoveryMessageId,
                "CRM",
                TimeSpan.FromSeconds(90),
                cancellationToken);
            Assert.Equal(2, leaseRecoveredInbox.AttemptCount);
            Assert.Null(leaseRecoveredInbox.LastError);
            await WaitForCheckpointAsync(
                coordinatorConnection,
                "PORTAL",
                leaseRecoveryQueueId,
                TimeSpan.FromSeconds(30),
                cancellationToken);
            var leaseRecoveredCase = await ReadCrmCaseAsync(
                crmConnection,
                restartCaseNumber,
                cancellationToken);
            Assert.NotNull(leaseRecoveredCase);
            Assert.Equal(restartCustomer, leaseRecoveredCase.ContactName);
            Assert.Equal(leaseRecoverySubject, leaseRecoveredCase.CaseTitle);
            Assert.Equal(1, await CountCrmAppliedMessageAsync(
                crmConnection,
                DeliveryMessageId.Create(
                    leaseRecoveryMessageId,
                    restartRouteId,
                    "CRM"),
                cancellationToken));

            const string crmConflictSubject = "CRM-side concurrent subject";
            const string portalConflictSubject = "Portal-side concurrent subject";
            await ApplyIgnoredCrmCaseUpdateAsync(
                crmConnection,
                caseNumber,
                crmConflictSubject,
                cancellationToken);
            var portalConflictMessageId = await UpdatePortalCaseSubjectAsync(
                portalConnection,
                caseNumber,
                portalConflictSubject,
                cancellationToken);

            await WaitForInboxStateAsync(
                coordinatorConnection,
                portalConflictMessageId,
                "CRM",
                "Held",
                TimeSpan.FromSeconds(90),
                cancellationToken);
            var conflictId = await ReadConflictIdAsync(
                coordinatorConnection,
                portalConflictMessageId,
                cancellationToken);
            await ResolveConflictWithIncomingValuesAsync(
                coordinatorConnection,
                keyRingPath,
                conflictId,
                cancellationToken);
            await WaitForConflictResolutionAsync(
                coordinatorConnection,
                conflictId,
                TimeSpan.FromSeconds(90),
                cancellationToken);
            var resolvedInbox = await WaitForInboxCompletionAsync(
                coordinatorConnection,
                portalConflictMessageId,
                "CRM",
                TimeSpan.FromSeconds(30),
                cancellationToken);
            Assert.Null(resolvedInbox.LastError);
            Assert.Null(resolvedInbox.LockedUntilUtc);

            var resolvedCase = await WaitForCrmCaseAsync(
                crmConnection,
                caseNumber,
                TimeSpan.FromSeconds(90),
                cancellationToken);
            Assert.Equal(portalConflictSubject, resolvedCase.CaseTitle);

            await using var retryReceiver = await WebhookCaptureServer.StartAsync(
                cancellationToken,
                failuresBeforeSuccess: 1);
            var retryRegistration = await RegisterWebhookEndpointAsync(
                coordinatorConnection,
                keyRingPath,
                retryReceiver.Endpoint,
                "Aspire E2E retry receiver",
                [WebhookEventTypes.Test],
                cancellationToken);
            await QueueWebhookTestAsync(
                coordinatorConnection,
                keyRingPath,
                retryRegistration.EndpointId,
                cancellationToken);

            var firstAttempt = await retryReceiver.WaitForAsync(
                request => IsWebhookEvent(request, WebhookEventTypes.Test),
                TimeSpan.FromSeconds(90),
                cancellationToken);
            var secondAttempt = await retryReceiver.WaitForAsync(
                request => IsWebhookEvent(request, WebhookEventTypes.Test),
                TimeSpan.FromSeconds(90),
                cancellationToken);
            var retryEventId = ValidateTestWebhook(firstAttempt, retryRegistration.Secret);
            Assert.Equal(retryEventId, ValidateTestWebhook(secondAttempt, retryRegistration.Secret));
            Assert.Equal(firstAttempt.Body, secondAttempt.Body);
            Assert.NotEqual(firstAttempt.Timestamp, secondAttempt.Timestamp);
            Assert.NotEqual(firstAttempt.Signature, secondAttempt.Signature);
            Assert.True(
                ParseWebhookTimestamp(secondAttempt.Timestamp) -
                ParseWebhookTimestamp(firstAttempt.Timestamp) >= 60,
                "The webhook retry was delivered less than 60 seconds after the first attempt.");
            await WaitForWebhookDeliveryAsync(
                coordinatorConnection,
                retryEventId,
                expectedAttemptCount: 2,
                TimeSpan.FromSeconds(90),
                cancellationToken);

        }
        finally
        {
            TryDeleteKeyRingDirectory(keyRingPath);
        }
    }

    [E2EFact]
    public async Task MappingAndDatabaseSetupUiSupportsRelatedTableEditing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var cancellationToken = timeout.Token;
        var keyRingPath = CreateKeyRingDirectory();

        try
        {
            await using var builder = await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.SyncCoordinator_AppHost>(
                    ["RunMode=E2E", $"E2E:KeyRingPath={keyRingPath}"],
                    cancellationToken);
            await using var app = await builder.BuildAsync(cancellationToken);
            await app.StartAsync(cancellationToken);
            await Task.WhenAll(
                app.ResourceNotifications.WaitForResourceHealthyAsync(
                    "coordinator-web",
                    WaitBehavior.StopOnResourceUnavailable,
                    cancellationToken),
                app.ResourceNotifications.WaitForResourceHealthyAsync(
                    "coordinator-worker",
                    WaitBehavior.StopOnResourceUnavailable,
                    cancellationToken));

            using var webClient = app.CreateHttpClient("coordinator-web", "http");
            await WaitForWebReadyAsync(webClient, cancellationToken);
            var webEndpoint = app.GetEndpoint("coordinator-web", "http");
            await BrowserAuthenticationSmoke.VerifyAsync(webEndpoint, cancellationToken);

            var coordinatorConnection = RequireConnectionString(
                await app.GetConnectionStringAsync("coordinator-db", cancellationToken),
                "coordinator-db");
            await PrepareDemoRoutesAsync(
                coordinatorConnection,
                keyRingPath,
                ["CRM - Field Service"],
                cancellationToken);
            var routeId = await ReadRouteIdAsync(
                coordinatorConnection,
                "CRM - Field Service",
                cancellationToken);

            await BrowserAuthenticationSmoke.VerifyMappingAndDatabaseSetupAsync(
                webEndpoint,
                routeId,
                cancellationToken);
        }
        finally
        {
            TryDeleteKeyRingDirectory(keyRingPath);
        }
    }

    private static async Task WaitForWebReadyAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromMinutes(2);
        Exception? lastError = null;

        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            try
            {
                using var response = await client.GetAsync("/health", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                lastError = new HttpRequestException(
                    $"Web health endpoint returned HTTP {(int)response.StatusCode}.");
            }
            catch (HttpRequestException exception)
            {
                lastError = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            "Coordinator Web did not become ready within 120 seconds.",
            lastError);
    }

    private static async Task StopWorkerAsync(
        DistributedApplication app,
        CancellationToken cancellationToken,
        Action? onStopAccepted = null)
    {
        var result = await app.ResourceCommands.ExecuteCommandAsync(
            "coordinator-worker",
            KnownResourceCommands.StopCommand,
            cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Aspire could not stop the test Worker: " +
                $"{result.Message ?? (result.Canceled ? "canceled" : "unknown error")}");
        }
        onStopAccepted?.Invoke();

        await app.ResourceNotifications.WaitForResourceAsync(
            "coordinator-worker",
            [KnownResourceStates.Exited, KnownResourceStates.Finished],
            cancellationToken);
    }

    private const string CleanupFailuresDataKey =
        "SyncCoordinator.E2E.CleanupFailures";

    private static async Task RunCleanupAsync(
        Exception? primaryFailure,
        string operation,
        Func<Task> cleanup)
    {
        try
        {
            await cleanup();
        }
        catch (Exception cleanupFailure) when (primaryFailure is not null)
        {
            RecordCleanupFailure(primaryFailure, operation, cleanupFailure);
        }
    }

    private static void RecordCleanupFailure(
        Exception primaryFailure,
        string operation,
        Exception cleanupFailure)
    {
        var detail = $"[{operation}]{Environment.NewLine}{cleanupFailure}";
        try
        {
            primaryFailure.Data[CleanupFailuresDataKey] =
                primaryFailure.Data[CleanupFailuresDataKey] is string existing
                    ? $"{existing}{Environment.NewLine}{Environment.NewLine}{detail}"
                    : detail;
        }
        catch
        {
            // Recording cleanup diagnostics must not replace the primary failure.
        }

        try
        {
            Console.Error.WriteLine(
                "Secondary E2E cleanup failure; preserving the primary exception." +
                Environment.NewLine + detail);
        }
        catch
        {
            // Console diagnostics must not replace the primary failure.
        }
    }

    private static async Task StartWorkerAsync(
        DistributedApplication app,
        CancellationToken cancellationToken)
    {
        var result = await app.ResourceCommands.ExecuteCommandAsync(
            "coordinator-worker",
            KnownResourceCommands.StartCommand,
            cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Aspire could not start the test Worker: " +
                $"{result.Message ?? (result.Canceled ? "canceled" : "unknown error")}");
        }

        await app.ResourceNotifications.WaitForResourceHealthyAsync(
            "coordinator-worker",
            WaitBehavior.StopOnResourceUnavailable,
            cancellationToken);
    }

    private static async Task PrepareDemoRoutesAsync(
        string coordinatorConnection,
        string keyRingPath,
        IReadOnlyList<string> routeNames,
        CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:coordinator-db"] = coordinatorConnection,
                ["DataProtection:KeyRingPath"] = keyRingPath,
                ["DatabaseDeployment:AllowDirectApply"] = "true"
            })
            .Build();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSyncCoordinator(configuration)
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();

        var reads = scope.ServiceProvider.GetRequiredService<ICoordinatorReadService>();
        var routes = await reads.GetRoutesAsync(cancellationToken);
        var deployments = scope.ServiceProvider.GetRequiredService<IDatabaseDeploymentService>();

        foreach (var routeName in routeNames)
        {
            var route = routes.Single(x => x.Name == routeName);
            var plan = await deployments.GetPlanAsync(route.Id, cancellationToken);

            foreach (var target in plan.Targets)
            {
                var applied = await deployments.ApplyTargetAsync(
                    route.Id,
                    target.SystemCode,
                    target.DatabaseName,
                    cancellationToken);
                Assert.True(applied.Success);
            }

            var verified = await deployments.VerifyAsync(route.Id, cancellationToken);
            Assert.True(verified.Success);
            var enabled = await deployments.SetEnabledAsync(route.Id, true, cancellationToken);
            Assert.True(enabled.Enabled);
        }
    }

    private static async Task<DatabaseConnectionInput> MakeSystemDatabaseUnavailableAsync(
        string coordinatorConnection,
        string keyRingPath,
        string systemCode,
        CancellationToken cancellationToken)
    {
        var configuration = CreateInfrastructureConfiguration(coordinatorConnection, keyRingPath);
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSyncCoordinator(configuration)
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<ICoordinatorAdminService>();
        var system = (await admin.GetSystemsAsync(cancellationToken))
            .Single(x => string.Equals(x.Code, systemCode, StringComparison.OrdinalIgnoreCase));
        var original = await admin.GetDatabaseConnectionAsync(system.Id, cancellationToken) ??
                       throw new InvalidOperationException(
                           $"System '{systemCode}' does not have a database connection.");
        var unavailable = new DatabaseConnectionInput
        {
            SystemId = original.SystemId,
            Server = "127.0.0.1",
            Port = 1,
            Database = original.Database,
            UserName = original.UserName,
            IntegratedSecurity = original.IntegratedSecurity,
            Encrypt = original.Encrypt,
            TrustServerCertificate = original.TrustServerCertificate,
            HasStoredPassword = original.HasStoredPassword
        };
        await admin.SaveDatabaseConnectionAsync(unavailable, cancellationToken);
        return original;
    }

    private static async Task SaveSystemDatabaseConnectionAsync(
        string coordinatorConnection,
        string keyRingPath,
        DatabaseConnectionInput connection,
        CancellationToken cancellationToken)
    {
        var configuration = CreateInfrastructureConfiguration(coordinatorConnection, keyRingPath);
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSyncCoordinator(configuration)
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICoordinatorAdminService>()
            .SaveDatabaseConnectionAsync(connection, cancellationToken);
    }

    private static IConfiguration CreateInfrastructureConfiguration(
        string coordinatorConnection,
        string keyRingPath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:coordinator-db"] = coordinatorConnection,
                ["DataProtection:KeyRingPath"] = keyRingPath
            })
            .Build();

    private static async Task<WebhookRegistration> RegisterWebhookEndpointAsync(
        string coordinatorConnection,
        string keyRingPath,
        Uri endpoint,
        string name,
        IReadOnlyList<string> eventTypes,
        CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:coordinator-db"] = coordinatorConnection,
                ["DataProtection:KeyRingPath"] = keyRingPath
            })
            .Build();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSyncCoordinator(configuration)
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var webhooks = scope.ServiceProvider.GetRequiredService<IWebhookAdminService>();
        var saved = await webhooks.SaveEndpointAsync(new WebhookEndpointInput
        {
            Name = name,
            Url = endpoint.ToString(),
            Enabled = true,
            SignatureEnabled = true,
            EventTypes = eventTypes.ToList()
        }, cancellationToken);

        return new WebhookRegistration(
            saved.Id,
            saved.NewSecret ??
            throw new InvalidOperationException("The E2E webhook endpoint did not generate a signature secret."));
    }

    private static async Task QueueWebhookTestAsync(
        string coordinatorConnection,
        string keyRingPath,
        Guid endpointId,
        CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:coordinator-db"] = coordinatorConnection,
                ["DataProtection:KeyRingPath"] = keyRingPath
            })
            .Build();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSyncCoordinator(configuration)
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWebhookAdminService>()
            .QueueTestAsync(endpointId, cancellationToken);
    }

    private static bool IsMatchingSyncWebhook(
        CapturedWebhook request,
        string entityId,
        string sourceSystem,
        string destinationSystem)
    {
        using var payload = JsonDocument.Parse(request.Body);
        var root = payload.RootElement;
        return root.GetProperty("eventType").GetString() == WebhookEventTypes.SyncUpserted &&
               root.GetProperty("entityId").GetString() == entityId &&
               root.GetProperty("sourceSystem").GetString() == sourceSystem &&
               root.GetProperty("destinationSystem").GetString() == destinationSystem;
    }

    private static bool IsWebhookEvent(CapturedWebhook request, string eventType)
    {
        using var payload = JsonDocument.Parse(request.Body);
        return payload.RootElement.GetProperty("eventType").GetString() == eventType;
    }

    private static Guid ValidateSyncWebhook(
        CapturedWebhook request,
        string base64Secret,
        string entityId,
        string sourceSystem,
        string destinationSystem)
    {
        using var payload = JsonDocument.Parse(request.Body);
        var root = payload.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(WebhookEventTypes.SyncUpserted, root.GetProperty("eventType").GetString());
        Assert.Equal("Customer Portal - CRM", root.GetProperty("routeName").GetString());
        Assert.Equal(entityId, root.GetProperty("entityId").GetString());
        Assert.Equal(sourceSystem, root.GetProperty("sourceSystem").GetString());
        Assert.Equal(destinationSystem, root.GetProperty("destinationSystem").GetString());
        Assert.Equal("SupportCase", root.GetProperty("entityType").GetString());

        var eventId = root.GetProperty("eventId").GetGuid();
        Assert.Equal(eventId.ToString("D"), request.WebhookId);
        Assert.True(long.TryParse(
            request.Timestamp,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out _));

        var signedContent = Encoding.UTF8.GetBytes(request.Timestamp + "." + request.Body);
        var expectedHash = HMACSHA256.HashData(
            Convert.FromBase64String(base64Secret),
            signedContent);
        Assert.StartsWith("v1=", request.Signature, StringComparison.Ordinal);
        var providedHash = Convert.FromBase64String(request.Signature[3..]);
        Assert.True(CryptographicOperations.FixedTimeEquals(expectedHash, providedHash));
        return eventId;
    }

    private static Guid ValidateTestWebhook(
        CapturedWebhook request,
        string base64Secret)
    {
        using var payload = JsonDocument.Parse(request.Body);
        var root = payload.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(WebhookEventTypes.Test, root.GetProperty("eventType").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("routeId").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("entityId").ValueKind);

        var eventId = root.GetProperty("eventId").GetGuid();
        Assert.Equal(eventId.ToString("D"), request.WebhookId);
        var signedContent = Encoding.UTF8.GetBytes(request.Timestamp + "." + request.Body);
        var expectedHash = HMACSHA256.HashData(
            Convert.FromBase64String(base64Secret),
            signedContent);
        Assert.StartsWith("v1=", request.Signature, StringComparison.Ordinal);
        Assert.True(CryptographicOperations.FixedTimeEquals(
            expectedHash,
            Convert.FromBase64String(request.Signature[3..])));
        return eventId;
    }

    private static long ParseWebhookTimestamp(string value)
    {
        Assert.True(long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var timestamp));
        return timestamp;
    }

    private static async Task WaitForWebhookDeliveryAsync(
        string coordinatorConnection,
        Guid eventId,
        int expectedAttemptCount,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        WebhookDeliveryStatus? lastSeen = null;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            await using var connection = new SqlConnection(coordinatorConnection);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                "SELECT TOP(1) State, AttemptCount FROM WebhookDelivery WHERE EventId = @eventId;",
                connection);
            command.Parameters.AddWithValue("@eventId", eventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            lastSeen = await reader.ReadAsync(cancellationToken)
                ? new WebhookDeliveryStatus(reader.GetString(0), reader.GetInt32(1))
                : null;
            if (lastSeen is { State: "Delivered", AttemptCount: var attempts } &&
                attempts == expectedAttemptCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Webhook event '{eventId}' was not marked Delivered within " +
            $"{waitTimeout.TotalSeconds:N0} seconds. Last seen: {lastSeen}.");
    }

    private static async Task<Guid> InsertPortalCaseAsync(
        string connectionString,
        string caseNumber,
        string customerName,
        string subject,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO SupportCase
                (CaseNumber, CustomerName, Email, Phone, ProductName, SerialNumber, Subject,
                 Description, PreferredVisitDate, Status, ResponseMessage, OriginSystem, UpdatedAtUtc)
            VALUES
                (@caseNumber, @customerName, 'e2e@example.com', '090-0000-0000', 'E2E Product',
                 'E2E-SERIAL', @subject, 'Created by the Aspire E2E test', UTC_DATE(), 'New',
                 NULL, 'PORTAL', UTC_TIMESTAMP(6));
            """;

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@caseNumber", caseNumber);
        command.Parameters.AddWithValue("@customerName", customerName);
        command.Parameters.AddWithValue("@subject", subject);
        await command.ExecuteNonQueryAsync(cancellationToken);

        const string queueSql = """
            SELECT MessageId
            FROM SyncChangeQueue
            WHERE EntityType = 'SupportCase'
              AND EntityId = @caseNumber
              AND Operation = 'Upsert'
            ORDER BY QueueId DESC
            LIMIT 1;
            """;
        await using var queueCommand = new MySqlCommand(queueSql, connection);
        queueCommand.Parameters.AddWithValue("@caseNumber", caseNumber);
        var value = await queueCommand.ExecuteScalarAsync(cancellationToken) ??
                    throw new InvalidOperationException(
                        $"Portal did not enqueue the inserted case '{caseNumber}'.");
        return value is Guid guid
            ? guid
            : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    private static async Task<CrmCase> WaitForCrmCaseAsync(
        string connectionString,
        string caseNumber,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var found = await ReadCrmCaseAsync(connectionString, caseNumber, cancellationToken);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"CRM did not receive support case '{caseNumber}' within {waitTimeout.TotalSeconds:N0} seconds.");
    }

    private static async Task<CrmCase?> ReadCrmCaseAsync(
        string connectionString,
        string caseNumber,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ContactName, CaseTitle, WorkflowState, SourceCode
            FROM dbo.SupportCase
            WHERE CaseRef = @caseNumber;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@caseNumber", caseNumber);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CrmCase(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
    }

    private static async Task<Guid> UpdateCrmCaseAsync(
        string connectionString,
        string caseNumber,
        string status,
        string responseMessage,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.SupportCase
            SET WorkflowState = @status,
                AgentReply = @responseMessage,
                ModifiedAtUtc = SYSUTCDATETIME()
            WHERE CaseRef = @caseNumber;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@caseNumber", caseNumber);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@responseMessage", responseMessage);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        return await ReadLatestCrmMessageIdAsync(
            connection,
            "SupportCase",
            caseNumber,
            "Upsert",
            cancellationToken);
    }

    private static async Task<PortalCase> WaitForPortalCaseAsync(
        string connectionString,
        string caseNumber,
        string expectedStatus,
        string expectedResponse,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        PortalCase? lastSeen = null;

        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            lastSeen = await ReadPortalCaseAsync(connectionString, caseNumber, cancellationToken);
            if (lastSeen is
                {
                    Status: var status,
                    ResponseMessage: var response
                } &&
                status == expectedStatus &&
                response == expectedResponse)
            {
                return lastSeen;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Portal did not receive the CRM update for '{caseNumber}' within " +
            $"{waitTimeout.TotalSeconds:N0} seconds. Last seen: {lastSeen}.");
    }

    private static async Task<PortalCase?> ReadPortalCaseAsync(
        string connectionString,
        string caseNumber,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Status, ResponseMessage
            FROM SupportCase
            WHERE CaseNumber = @caseNumber;
            """;

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@caseNumber", caseNumber);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PortalCase(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task<Guid> InsertUnassignedCrmWorkOrderAsync(
        string connectionString,
        string workOrderNumber,
        string caseNumber,
        string problemSummary,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT dbo.WorkOrder
                (WorkOrderNumber, CaseRef, ServiceAddress, ProblemSummary, ScheduledAt,
                 TechnicianName, Status, WorkResult, CompletedAt, EstimatedMinutes, EstimatedCost,
                 RequiresParts, WorkNote, ExternalTrackingId,
                 OriginSystem, UpdatedAtUtc)
            VALUES
                (@workOrderNumber, @caseNumber, N'E2E Eligibility Address', @problemSummary,
                 DATEADD(day, 1, SYSUTCDATETIME()), NULL, N'InProgress', NULL, NULL,
                 60, 8000.0000, 0, N'E2E eligibility synchronization', NEWID(),
                 N'CRM', SYSUTCDATETIME());
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@workOrderNumber", workOrderNumber);
        command.Parameters.AddWithValue("@caseNumber", caseNumber);
        command.Parameters.AddWithValue("@problemSummary", problemSummary);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        return await ReadLatestCrmMessageIdAsync(
            connection,
            "WorkOrder",
            workOrderNumber,
            "Upsert",
            cancellationToken);
    }

    private static async Task<Guid> InsertCrmWorkOrderAssignmentAsync(
        string connectionString,
        string workOrderNumber,
        string? staffNumber,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT dbo.WorkOrderAssignment
                (WorkOrderNumber, StaffNo, AssignmentType, AssignedAtUtc, UpdatedAtUtc)
            VALUES
                (@workOrderNumber, @staffNumber, N'Primary', SYSUTCDATETIME(), SYSUTCDATETIME());
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@workOrderNumber", workOrderNumber);
        command.Parameters.AddWithValue("@staffNumber", (object?)staffNumber ?? DBNull.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        return await ReadLatestCrmMessageIdAsync(
            connection,
            "WorkOrder",
            workOrderNumber,
            "Upsert",
            cancellationToken);
    }

    private static async Task<Guid> UpdateCrmWorkOrderAssignmentAsync(
        string connectionString,
        string workOrderNumber,
        string? staffNumber,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.WorkOrderAssignment
            SET StaffNo = @staffNumber,
                UpdatedAtUtc = SYSUTCDATETIME()
            WHERE WorkOrderNumber = @workOrderNumber;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@workOrderNumber", workOrderNumber);
        command.Parameters.AddWithValue("@staffNumber", (object?)staffNumber ?? DBNull.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        return await ReadLatestCrmMessageIdAsync(
            connection,
            "WorkOrder",
            workOrderNumber,
            "Upsert",
            cancellationToken);
    }

    private static async Task<Guid> UpdateCrmWorkOrderProblemAsync(
        string connectionString,
        string workOrderNumber,
        string problemSummary,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.WorkOrder
            SET ProblemSummary = @problemSummary,
                UpdatedAtUtc = SYSUTCDATETIME()
            WHERE WorkOrderNumber = @workOrderNumber;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@workOrderNumber", workOrderNumber);
        command.Parameters.AddWithValue("@problemSummary", problemSummary);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        return await ReadLatestCrmMessageIdAsync(
            connection,
            "WorkOrder",
            workOrderNumber,
            "Upsert",
            cancellationToken);
    }

    private static async Task<Guid> UpdateCrmCaseContactNameAsync(
        string connectionString,
        string caseNumber,
        string workOrderNumber,
        string customerName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.SupportCase
            SET ContactName = @customerName,
                ModifiedAtUtc = SYSUTCDATETIME()
            WHERE CaseRef = @caseNumber;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@caseNumber", caseNumber);
        command.Parameters.AddWithValue("@customerName", customerName);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        return await ReadLatestCrmMessageIdAsync(
            connection,
            "WorkOrder",
            workOrderNumber,
            "Upsert",
            cancellationToken);
    }

    private static async Task<Guid> InsertCrmWorkOrderAsync(
        string connectionString,
        string workOrderNumber,
        string caseNumber,
        string customerName,
        string problemSummary,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            UPDATE dbo.SupportCase
            SET ContactName = @customerName, ModifiedAtUtc = SYSUTCDATETIME()
            WHERE CaseRef = @caseNumber;

            INSERT dbo.WorkOrder
                (WorkOrderNumber, CaseRef, ServiceAddress, ProblemSummary, ScheduledAt,
                 TechnicianName, Status, WorkResult, CompletedAt, EstimatedMinutes, EstimatedCost,
                 RequiresParts, WorkNote, ExternalTrackingId,
                 OriginSystem, UpdatedAtUtc)
            VALUES
                (@workOrderNumber, @caseNumber, N'E2E Address', @problemSummary,
                 DATEADD(day, 1, SYSUTCDATETIME()), NULL, N'InProgress', NULL, NULL,
                 90, 12500.0000, 0, N'E2E related-table synchronization', NEWID(),
                 N'CRM', SYSUTCDATETIME());

            INSERT dbo.WorkOrderAssignment
                (WorkOrderNumber, StaffNo, AssignmentType, AssignedAtUtc, UpdatedAtUtc)
            VALUES
                (@workOrderNumber, N'E2E-STAFF-001', N'Primary', SYSUTCDATETIME(), SYSUTCDATETIME());

            COMMIT TRANSACTION;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@workOrderNumber", workOrderNumber);
        command.Parameters.AddWithValue("@caseNumber", caseNumber);
        command.Parameters.AddWithValue("@customerName", customerName);
        command.Parameters.AddWithValue("@problemSummary", problemSummary);
        Assert.Equal(3, await command.ExecuteNonQueryAsync(cancellationToken));
        return await ReadLatestCrmMessageIdAsync(
            connection,
            "WorkOrder",
            workOrderNumber,
            "Upsert",
            cancellationToken);
    }

    private static async Task<FieldWorkOrder> WaitForFieldWorkOrderAsync(
        string connectionString,
        string workOrderNumber,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var found = await ReadFieldWorkOrderAsync(
                connectionString,
                workOrderNumber,
                cancellationToken);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Field Service did not receive work order '{workOrderNumber}' within " +
            $"{waitTimeout.TotalSeconds:N0} seconds.");
    }

    private static async Task<FieldWorkOrder> WaitForFieldWorkOrderCustomerAsync(
        string connectionString,
        string workOrderNumber,
        string expectedCustomer,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        FieldWorkOrder? lastSeen = null;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            lastSeen = await ReadFieldWorkOrderAsync(
                connectionString,
                workOrderNumber,
                cancellationToken);
            if (lastSeen?.CustomerDisplayName == expectedCustomer)
            {
                return lastSeen;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Field Service did not receive projected customer '{expectedCustomer}' for " +
            $"'{workOrderNumber}' within {waitTimeout.TotalSeconds:N0} seconds. Last seen: {lastSeen}.");
    }

    private static async Task<FieldWorkOrder> WaitForFieldWorkOrderProblemAsync(
        string connectionString,
        string workOrderNumber,
        string expectedProblem,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        FieldWorkOrder? lastSeen = null;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            lastSeen = await ReadFieldWorkOrderAsync(
                connectionString,
                workOrderNumber,
                cancellationToken);
            if (lastSeen?.ProblemSummary == expectedProblem)
            {
                return lastSeen;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Field Service did not receive problem '{expectedProblem}' for '{workOrderNumber}' within " +
            $"{waitTimeout.TotalSeconds:N0} seconds. Last seen: {lastSeen}.");
    }

    private static async Task WaitForFieldWorkOrderDeletionAsync(
        string connectionString,
        string workOrderNumber,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            if (await ReadFieldWorkOrderAsync(
                    connectionString,
                    workOrderNumber,
                    cancellationToken) is null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Field Service work order '{workOrderNumber}' was not deleted within " +
            $"{waitTimeout.TotalSeconds:N0} seconds after it became ineligible.");
    }

    private static async Task<FieldWorkOrder?> ReadFieldWorkOrderAsync(
        string connectionString,
        string workOrderNumber,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT customer_display_name, problem_summary, job_status, source_code
            FROM public.work_order
            WHERE work_order_no = @workOrderNumber;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@workOrderNumber", workOrderNumber);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FieldWorkOrder(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
    }

    private static async Task<Guid> CompleteFieldWorkOrderAsync(
        string connectionString,
        string workOrderNumber,
        string technicianName,
        string workResult,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE public.work_order
            SET technician_name = @technicianName,
                job_status = 'done',
                work_result = @workResult,
                completed_at = CURRENT_TIMESTAMP,
                modified_at = CURRENT_TIMESTAMP
            WHERE work_order_no = @workOrderNumber;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@workOrderNumber", workOrderNumber);
        command.Parameters.AddWithValue("@technicianName", technicianName);
        command.Parameters.AddWithValue("@workResult", workResult);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        return await ReadLatestFieldMessageIdAsync(
            connection,
            workOrderNumber,
            "Upsert",
            cancellationToken);
    }

    private static async Task<CrmWorkOrder> WaitForCompletedCrmWorkOrderAsync(
        string connectionString,
        string workOrderNumber,
        string expectedTechnician,
        string expectedWorkResult,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        CrmWorkOrder? lastSeen = null;

        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            lastSeen = await ReadCrmWorkOrderAsync(
                connectionString,
                workOrderNumber,
                cancellationToken);
            if (lastSeen is
                {
                    Status: "Completed",
                    TechnicianName: var technicianName,
                    WorkResult: var workResult
                } &&
                technicianName == expectedTechnician &&
                workResult == expectedWorkResult)
            {
                return lastSeen;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"CRM did not receive the completed work order '{workOrderNumber}' within " +
            $"{waitTimeout.TotalSeconds:N0} seconds. Last seen: {lastSeen}.");
    }

    private static async Task<CrmWorkOrder?> ReadCrmWorkOrderAsync(
        string connectionString,
        string workOrderNumber,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Status, TechnicianName, WorkResult
            FROM dbo.WorkOrder
            WHERE WorkOrderNumber = @workOrderNumber;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@workOrderNumber", workOrderNumber);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CrmWorkOrder(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async Task<Guid> DeleteFieldWorkOrderAsync(
        string connectionString,
        string workOrderNumber,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM public.work_order
            WHERE work_order_no = @workOrderNumber;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@workOrderNumber", workOrderNumber);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        return await ReadLatestFieldMessageIdAsync(
            connection,
            workOrderNumber,
            "Delete",
            cancellationToken);
    }

    private static async Task<Guid> ReadLatestFieldMessageIdAsync(
        NpgsqlConnection connection,
        string workOrderNumber,
        string operation,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "MessageId"
            FROM public."SyncChangeQueue"
            WHERE "EntityType" = 'WorkOrder'
              AND "EntityId" = @workOrderNumber
              AND "Operation" = @operation
            ORDER BY "QueueId" DESC
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@workOrderNumber", workOrderNumber);
        command.Parameters.AddWithValue("@operation", operation);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken) ??
            throw new InvalidOperationException(
                $"Field Service did not enqueue {operation} for '{workOrderNumber}'."));
    }

    private static async Task<InboxStatus> WaitForInboxCompletionAsync(
        string coordinatorConnection,
        Guid sourceMessageId,
        string destinationSystem,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        InboxStatus? lastSeen = null;

        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            lastSeen = await ReadInboxStatusAsync(
                coordinatorConnection,
                sourceMessageId,
                destinationSystem,
                cancellationToken);
            if (lastSeen?.State == "Completed")
            {
                return lastSeen;
            }
            if (lastSeen?.State is "Held" or "WaitingForPrevious")
            {
                throw new InvalidOperationException(
                    $"Inbox for message '{sourceMessageId}' entered terminal state " +
                    $"'{lastSeen.State}'. Error: {lastSeen.LastError ?? "(none)"}. " +
                    $"Conflict fields: {lastSeen.ConflictFieldsJson ?? "(none)"}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Inbox for message '{sourceMessageId}' did not complete within " +
            $"{waitTimeout.TotalSeconds:N0} seconds. Last seen: {lastSeen}.");
    }

    private static async Task<InboxStatus> WaitForInboxStateAsync(
        string coordinatorConnection,
        Guid sourceMessageId,
        string destinationSystem,
        string expectedState,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        InboxStatus? lastSeen = null;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            lastSeen = await ReadInboxStatusAsync(
                coordinatorConnection,
                sourceMessageId,
                destinationSystem,
                cancellationToken);
            if (lastSeen?.State == expectedState)
            {
                return lastSeen;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Inbox for message '{sourceMessageId}' did not enter '{expectedState}' within " +
            $"{waitTimeout.TotalSeconds:N0} seconds. Last seen: {lastSeen}.");
    }

    private static async Task ApplyIgnoredCrmCaseUpdateAsync(
        string connectionString,
        string caseNumber,
        string subject,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT dbo.SyncAppliedMessage(MessageId, AppliedAtUtc)
            VALUES (@messageId, SYSUTCDATETIME());
            EXEC sys.sp_set_session_context @key=N'SyncMessageId', @value=@messageId;
            UPDATE dbo.SupportCase
            SET CaseTitle = @subject,
                ModifiedAtUtc = SYSUTCDATETIME()
            WHERE CaseRef = @caseNumber;
            EXEC sys.sp_set_session_context @key=N'SyncMessageId', @value=NULL;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@messageId", Guid.NewGuid());
        command.Parameters.AddWithValue("@caseNumber", caseNumber);
        command.Parameters.AddWithValue("@subject", subject);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task AcquireCrmCaseWriteLockAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string caseNumber,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CaseRef
            FROM dbo.SupportCase WITH (XLOCK, ROWLOCK, HOLDLOCK)
            WHERE CaseRef = @caseNumber;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@caseNumber", caseNumber);
        Assert.Equal(caseNumber, await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ExpireInboxLeaseAsync(
        string coordinatorConnection,
        Guid sourceMessageId,
        Guid routeId,
        string destinationSystem,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE InboxMessage
            SET LockedUntilUtc = DATEADD(minute, -1, SYSUTCDATETIME())
            WHERE SourceMessageId = @sourceMessageId
              AND RouteId = @routeId
              AND DestinationSystem = @destinationSystem
              AND State = 'Processing'
              AND AttemptCount = 1;
            """;

        await using var connection = new SqlConnection(coordinatorConnection);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceMessageId", sourceMessageId);
        command.Parameters.AddWithValue("@routeId", routeId);
        command.Parameters.AddWithValue("@destinationSystem", destinationSystem);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }

    private static async Task<Guid> UpdatePortalCaseSubjectAsync(
        string connectionString,
        string caseNumber,
        string subject,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE SupportCase
            SET Subject = @subject,
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE CaseNumber = @caseNumber;
            """;

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@caseNumber", caseNumber);
        command.Parameters.AddWithValue("@subject", subject);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));

        const string queueSql = """
            SELECT MessageId
            FROM SyncChangeQueue
            WHERE EntityType = 'SupportCase'
              AND EntityId = @caseNumber
              AND Operation = 'Upsert'
            ORDER BY QueueId DESC
            LIMIT 1;
            """;
        await using var queueCommand = new MySqlCommand(queueSql, connection);
        queueCommand.Parameters.AddWithValue("@caseNumber", caseNumber);
        var value = await queueCommand.ExecuteScalarAsync(cancellationToken) ??
            throw new InvalidOperationException(
                $"Portal did not enqueue the conflict update for '{caseNumber}'.");
        return value is Guid guid
            ? guid
            : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    private static async Task<Guid> UpdatePortalCaseSubjectWithoutTimestampAsync(
        string connectionString,
        string caseNumber,
        string subject,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE SupportCase
            SET Subject = @subject
            WHERE CaseNumber = @caseNumber;
            """;

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@caseNumber", caseNumber);
        command.Parameters.AddWithValue("@subject", subject);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));

        const string queueSql = """
            SELECT MessageId
            FROM SyncChangeQueue
            WHERE EntityType = 'SupportCase'
              AND EntityId = @caseNumber
              AND Operation = 'Upsert'
            ORDER BY QueueId DESC
            LIMIT 1;
            """;
        await using var queueCommand = new MySqlCommand(queueSql, connection);
        queueCommand.Parameters.AddWithValue("@caseNumber", caseNumber);
        var value = await queueCommand.ExecuteScalarAsync(cancellationToken) ??
            throw new InvalidOperationException(
                $"Portal did not enqueue the lease recovery update for '{caseNumber}'.");
        return value is Guid guid
            ? guid
            : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    private static async Task<Guid> ReadLatestCrmMessageIdAsync(
        SqlConnection connection,
        string entityType,
        string entityId,
        string operation,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP(1) MessageId
            FROM dbo.SyncChangeQueue
            WHERE EntityType = @entityType
              AND EntityId = @entityId
              AND Operation = @operation
            ORDER BY QueueId DESC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@entityType", entityType);
        command.Parameters.AddWithValue("@entityId", entityId);
        command.Parameters.AddWithValue("@operation", operation);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken) ??
            throw new InvalidOperationException(
                $"CRM did not enqueue {operation} for '{entityId}'."));
    }

    private static async Task<Guid> ReadConflictIdAsync(
        string coordinatorConnection,
        Guid sourceMessageId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP(1) Id
            FROM SyncConflict
            WHERE SourceMessageId = @sourceMessageId
              AND ResolutionState = 'AwaitingDecision'
            ORDER BY DetectedAtUtc DESC;
            """;

        await using var connection = new SqlConnection(coordinatorConnection);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceMessageId", sourceMessageId);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken) ??
            throw new InvalidOperationException(
                $"No awaiting conflict was stored for message '{sourceMessageId}'."));
    }

    private static async Task<Guid> ReadRouteIdAsync(
        string coordinatorConnection,
        string routeName,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT Id FROM SyncRoute WHERE Name = @routeName;";
        await using var connection = new SqlConnection(coordinatorConnection);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@routeName", routeName);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken) ??
                      throw new InvalidOperationException(
                          $"Route '{routeName}' was not found."));
    }

    private static async Task<long> ReadCheckpointAsync(
        string coordinatorConnection,
        string systemCode,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT LastQueueId FROM QueueCheckpoint WHERE SystemCode = @systemCode;";
        await using var connection = new SqlConnection(coordinatorConnection);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@systemCode", systemCode);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? 0
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task WaitForSourceQueuesQuiescentAsync(
        string coordinatorConnection,
        string portalConnection,
        string crmConnection,
        string fieldConnection,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        (long Portal, long Crm, long Field)? previous = null;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var current = (
                Portal: await ReadPortalMaxQueueIdAsync(portalConnection, cancellationToken),
                Crm: await ReadCrmMaxQueueIdAsync(crmConnection, cancellationToken),
                Field: await ReadFieldMaxQueueIdAsync(fieldConnection, cancellationToken));
            var checkpoints = (
                Portal: await ReadCheckpointAsync(coordinatorConnection, "PORTAL", cancellationToken),
                Crm: await ReadCheckpointAsync(coordinatorConnection, "CRM", cancellationToken),
                Field: await ReadCheckpointAsync(coordinatorConnection, "FIELD", cancellationToken));

            if (previous == current &&
                checkpoints.Portal >= current.Portal &&
                checkpoints.Crm >= current.Crm &&
                checkpoints.Field >= current.Field)
            {
                return;
            }

            previous = current;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException(
            $"Source queues did not become quiescent within {waitTimeout.TotalSeconds:N0} seconds.");
    }

    private static async Task<long> ReadPortalMaxQueueIdAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(
            "SELECT COALESCE(MAX(QueueId), 0) FROM SyncChangeQueue;",
            connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadCrmMaxQueueIdAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT COALESCE(MAX(QueueId), 0) FROM dbo.SyncChangeQueue;",
            connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadFieldMaxQueueIdAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(MAX(\"QueueId\"), 0) FROM public.\"SyncChangeQueue\";",
            connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task WaitForCheckpointAsync(
        string coordinatorConnection,
        string systemCode,
        long minimumQueueId,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        long lastSeen = 0;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            lastSeen = await ReadCheckpointAsync(
                coordinatorConnection,
                systemCode,
                cancellationToken);
            if (lastSeen >= minimumQueueId)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Checkpoint for '{systemCode}' did not reach queue {minimumQueueId} within " +
            $"{waitTimeout.TotalSeconds:N0} seconds. Last seen: {lastSeen}.");
    }

    private static async Task<long> ReadPortalQueueIdAsync(
        string connectionString,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT QueueId FROM SyncChangeQueue WHERE MessageId = @messageId;";
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@messageId", messageId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken) ??
            throw new InvalidOperationException(
                $"Portal queue message '{messageId}' was not found."),
            CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountFieldAppliedMessageAsync(
        string connectionString,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM public."SyncAppliedMessage"
            WHERE "MessageId" = @messageId;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@messageId", messageId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountCrmAppliedMessageAsync(
        string connectionString,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM dbo.SyncAppliedMessage WHERE MessageId = @messageId;";
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@messageId", messageId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task ResolveConflictWithIncomingValuesAsync(
        string coordinatorConnection,
        string keyRingPath,
        Guid conflictId,
        CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:coordinator-db"] = coordinatorConnection,
                ["DataProtection:KeyRingPath"] = keyRingPath
            })
            .Build();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSyncCoordinator(configuration)
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var resolutions = scope.ServiceProvider.GetRequiredService<IConflictResolutionService>();
        var details = await resolutions.GetAsync(conflictId, cancellationToken) ??
            throw new InvalidOperationException($"Conflict '{conflictId}' was not found.");
        Assert.Equal(ConflictResolutionState.AwaitingDecision, details.ResolutionState);
        Assert.Contains(details.Fields, field => field.FieldName == "Subject");

        await resolutions.QueueAsync(
            conflictId,
            new ConflictResolutionInput
            {
                ExpectedCurrentVersionToken = details.CurrentVersionToken ?? string.Empty,
                Comment = "Resolved by Aspire E2E",
                Fields = details.Fields.Select(field => new FieldResolutionInput
                {
                    FieldName = field.FieldName,
                    Choice = ManualConflictChoice.Incoming
                }).ToList()
            },
            "e2e",
            cancellationToken);
    }

    private static async Task WaitForConflictResolutionAsync(
        string coordinatorConnection,
        Guid conflictId,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            await using var connection = new SqlConnection(coordinatorConnection);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                "SELECT ResolutionState FROM SyncConflict WHERE Id = @conflictId;",
                connection);
            command.Parameters.AddWithValue("@conflictId", conflictId);
            if (string.Equals(
                    Convert.ToString(
                        await command.ExecuteScalarAsync(cancellationToken),
                        CultureInfo.InvariantCulture),
                    "Resolved",
                    StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Conflict '{conflictId}' was not resolved within {waitTimeout.TotalSeconds:N0} seconds.");
    }

    private static async Task<InboxStatus?> ReadInboxStatusAsync(
        string coordinatorConnection,
        Guid sourceMessageId,
        string destinationSystem,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP(1)
                inbox.State,
                inbox.AttemptCount,
                inbox.LastError,
                inbox.LockedUntilUtc,
                (SELECT TOP(1) conflict.FieldsJson
                 FROM SyncConflict AS conflict
                 WHERE conflict.SourceMessageId = inbox.SourceMessageId
                   AND conflict.RouteId = inbox.RouteId
                 ORDER BY conflict.DetectedAtUtc DESC) AS ConflictFieldsJson
            FROM InboxMessage AS inbox
            WHERE inbox.SourceMessageId = @sourceMessageId
              AND inbox.DestinationSystem = @destinationSystem;
            """;

        await using var connection = new SqlConnection(coordinatorConnection);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceMessageId", sourceMessageId);
        command.Parameters.AddWithValue("@destinationSystem", destinationSystem);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InboxStatus(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static async Task<string> WaitForOperationalEventAsync(
        string coordinatorConnection,
        string code,
        string target,
        DateTimeOffset occurredAfter,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP(1) Details
            FROM OperationalEvent
            WHERE Code = @code
              AND Target = @target
              AND LastOccurredAtUtc >= @occurredAfter
            ORDER BY LastOccurredAtUtc DESC;
            """;
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;

        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            await using var connection = new SqlConnection(coordinatorConnection);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@code", code);
            command.Parameters.AddWithValue("@target", target);
            command.Parameters.AddWithValue("@occurredAfter", occurredAfter);
            var details = await command.ExecuteScalarAsync(cancellationToken);
            if (details is string value)
            {
                return value;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Operational event '{code}' for '{target}' was not recorded within " +
            $"{waitTimeout.TotalSeconds:N0} seconds.");
    }

    private static async Task WaitForCrmWorkOrderDeletionAsync(
        string connectionString,
        string workOrderNumber,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            if (await ReadCrmWorkOrderAsync(
                    connectionString,
                    workOrderNumber,
                    cancellationToken) is null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"CRM did not delete work order '{workOrderNumber}' within " +
            $"{waitTimeout.TotalSeconds:N0} seconds.");
    }

    private static string AddMySqlConnectorOptions(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            GuidFormat = MySqlGuidFormat.Char36,
            AllowUserVariables = true
        };
        return builder.ConnectionString;
    }

    private static string PreparePostgreSqlConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            SslMode = SslMode.Disable,
            GssEncryptionMode = GssEncryptionMode.Disable
        }.ConnectionString;

    private static string RequireConnectionString(string? value, string resourceName) =>
        value ?? throw new InvalidOperationException(
            $"Aspire resource '{resourceName}' did not expose a connection string.");

    private static string CreateKeyRingDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "SyncCoordinator.E2E",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteKeyRingDirectory(string path)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SyncCoordinator.E2E"));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, recursive: true);
                }
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                lastError = exception;
                if (attempt < 3)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(100 * attempt));
                }
            }
        }

        Console.Error.WriteLine(
            $"Could not remove the temporary E2E key ring '{fullPath}': {lastError?.Message}");
    }

    private sealed record CrmCase(
        string ContactName,
        string CaseTitle,
        string WorkflowState,
        string SourceCode);

    private sealed record PortalCase(
        string Status,
        string? ResponseMessage);

    private sealed record FieldWorkOrder(
        string CustomerDisplayName,
        string ProblemSummary,
        string Status,
        string SourceCode);

    private sealed record CrmWorkOrder(
        string Status,
        string? TechnicianName,
        string? WorkResult);

    private sealed record InboxStatus(
        string State,
        int AttemptCount,
        string? LastError,
        DateTimeOffset? LockedUntilUtc,
        string? ConflictFieldsJson);

    private sealed record WebhookRegistration(Guid EndpointId, string Secret);

    private sealed record WebhookDeliveryStatus(string State, int AttemptCount);
}
