//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Resource.CosmosExceptions;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Cosmos.Tracing;
    using Microsoft.Azure.Cosmos.Tracing.TraceData;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Collections;

    internal sealed class GatewayAccountReader
    {
        private readonly ConnectionPolicy connectionPolicy;
        private readonly AuthorizationTokenProvider cosmosAuthorization;
        private readonly CosmosHttpClient httpClient;
        private readonly Uri serviceEndpoint;
        private readonly CancellationToken cancellationToken;

        // Backlog: Auth abstractions are spilling through. 4 arguments for this CTOR are result of it.
        public GatewayAccountReader(Uri serviceEndpoint,
                AuthorizationTokenProvider cosmosAuthorization,
                ConnectionPolicy connectionPolicy,
                CosmosHttpClient httpClient,
                CancellationToken cancellationToken = default)
        {
            this.httpClient = httpClient;
            this.serviceEndpoint = serviceEndpoint;
            this.cosmosAuthorization = cosmosAuthorization ?? throw new ArgumentNullException(nameof(AuthorizationTokenProvider));
            this.connectionPolicy = connectionPolicy;
            this.cancellationToken = cancellationToken;
        }
        
        private async Task<T> GetDatabaseAccountAsync<T>(Uri serviceEndpoint)
        {
            Console.WriteLine("Request Uri: " + serviceEndpoint);
            INameValueCollection headers = new RequestNameValueCollection();
            await this.cosmosAuthorization.AddAuthorizationHeaderAsync(
                headersCollection: headers,
                requestAddress: serviceEndpoint,
                verb: HttpConstants.HttpMethods.Get,
                tokenType: AuthorizationTokenType.PrimaryMasterKey);
            
            using (ITrace trace = Trace.GetRootTrace("Account Read", TraceComponent.Transport, TraceLevel.Info))
            {
                IClientSideRequestStatistics stats = new ClientSideRequestStatisticsTraceDatum(DateTime.UtcNow, trace.Summary);
                
                try
                {
                    using (HttpResponseMessage responseMessage = await this.httpClient.GetAsync(
                        uri: serviceEndpoint,
                        additionalHeaders: headers,
                        resourceType: ResourceType.DatabaseAccount,
                        timeoutPolicy: HttpTimeoutPolicyControlPlaneRead.Instance,
                        clientSideRequestStatistics: stats,
                        cancellationToken: default))
                    using (DocumentServiceResponse documentServiceResponse = await ClientExtensions.ParseResponseAsync(responseMessage))
                    {
                        return CosmosResource.FromStream<T>(documentServiceResponse);
                    }
                }
                catch (ObjectDisposedException) when (this.cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException($"Client is being disposed for {serviceEndpoint} at {DateTime.UtcNow}, cancelling further operations.");
                }
                catch (OperationCanceledException ex)
                {
                    trace.AddDatum("Client Side Request Stats", stats);
                    throw CosmosExceptionFactory.CreateRequestTimeoutException(
                                                message: ex.Data?["Message"]?.ToString() ?? ex.Message,
                                                headers: new Headers()
                                                {
                                                    ActivityId = System.Diagnostics.Trace.CorrelationManager.ActivityId.ToString()
                                                },
                                                innerException: ex,
                                                trace: trace);
                }
            }
        }

        public async Task<AccountProperties> InitializeReaderAsync()
        {
            AccountProperties databaseAccount = await GlobalEndpointManager.GetDatabaseAccountFromAnyLocationsAsync(
                defaultEndpoint: this.serviceEndpoint,
                locations: this.connectionPolicy.PreferredLocations,
                getDatabaseAccountFn: async (defaultEndpoint) => 
                {
                    AccountProperties accountProperties = await this.GetDatabaseAccountAsync<AccountProperties>(defaultEndpoint);
                    accountProperties.ClientConfiguration = await this.GetDatabaseAccountAsync<AccountClientConfiguration>(new Uri(defaultEndpoint.ToString() + Paths.ClientConfigPathSegment));
                    
                    return accountProperties;
                },
                cancellationToken: this.cancellationToken);

            return databaseAccount;
        }
    }
}
