// ============================================================
// touchline-ai — Azure infrastructure
// ============================================================
// The single source of truth for this project's Azure resources (ADR-012).
// Resources are added at the point they are first provisioned, not in
// anticipation. Everything lives in rg-touchline-ai (ADR-011).
//
// Deploy, preview and teardown run through infra/deploy.ps1, which supplies
// principalId and ownerEmail at run time from the signed-in user, so neither
// is ever stored in the repo.
//
// Resources are ordered by dependency.
// ============================================================

targetScope = 'resourceGroup'

// ============================================================
// Parameters
// ============================================================

@description('Region for every resource. Sweden Central gets new Foundry models early (ADR-011).')
param location string = resourceGroup().location

@description('Short environment label stamped on every resource tag.')
@allowed(['dev', 'prod'])
param environment string = 'dev'

@description('Object ID of the signed-in developer, who gets data-plane roles for local development. Empty when a service principal deploys from CI.')
param principalId string = ''

@description('Where budget alerts are sent. Supplied at deploy time, never committed.')
param ownerEmail string

@description('Monthly budget in the billing currency (GBP). ADR-011.')
param monthlyBudget int = 30

@description('First day of the budget period. deploy.ps1 passes the existing budget\'s start date, so this default only applies on the first deploy.')
param budgetStartDate string = utcNow('yyyy-MM-01')

@description('Match Centre image. deploy.ps1 passes the image already running, so a redeploy never rolls back to the placeholder.')
param matchCentreImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Subject prefix of the GitHub OIDC tokens allowed to deploy (ADR-013). The repo uses immutable subjects, which carry the owner and repository IDs, so a deleted and recreated repo with the same name gets a different prefix. Read it with: gh api repos/<owner>/<repo>/actions/oidc/customization/sub')
param githubSubjectPrefix string = 'repo:CuddleYourCat@37294565/touchline-ai@1422432373'

@description('Simulator job image. deploy.ps1 passes the image already configured, so a redeploy never rolls back to the placeholder.')
param simulatorImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Ingress traffic rules. deploy.ps1 passes the live rules, so a redeploy keeps revision labels such as "judging" (ADR-009).')
param matchCentreTraffic array = [
  {
    latestRevision: true
    weight: 100
  }
]

// ============================================================
// Variables
// ============================================================

var tags = {
  project: 'touchline-ai'
  environment: environment
}

// Globally unique names get a suffix that is stable for this resource group.
var suffix = uniqueString(resourceGroup().id)
var keyVaultName = 'kv-tl-${suffix}'
var registryName = 'crtouchline${suffix}'
var foundryName = 'ais-touchline-${suffix}'
var eventHubsName = 'evhns-touchline-${suffix}'
var serviceBusName = 'sbns-touchline-${suffix}'
var cosmosName = 'cosmos-touchline-${suffix}'
var processorStorageName = 'sttouchline${suffix}' // 24 characters: the storage account maximum
var processorAppName = 'func-touchline-${suffix}'

// Must match the names in the code and the AppHost (ADR-023).
var matchEventsHubName = 'match-events'
var keyMomentsTopicName = 'key-moments'
var cosmosDatabaseName = 'touchline'
var deploymentContainerName = 'deployment-package'

var placeholderImage = 'mcr.microsoft.com/k8se/quickstart:latest'
var isPlaceholder = matchCentreImage == placeholderImage

// The placeholder listens on 80. .NET container images listen on 8080.
var matchCentrePort = isPlaceholder ? 80 : 8080

// Built-in role definition IDs.
var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  cognitiveServicesOpenAiUser: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  keyVaultSecretsOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
  containerRegistryTasksContributor: 'fb382eab-e894-4461-af04-94435c366c3f'
  containerAppsContributor: '358470bc-b998-42bd-ab17-a7e34c199c0f'
  reader: 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
  eventHubsDataSender: '2b629674-e913-4c01-ae53-ef4638d8f975'
  eventHubsDataReceiver: 'a638d3c7-ab3a-418d-83e6-5f17a39d4fde'
  serviceBusDataSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  websiteContributor: 'de139f84-1756-47ae-9be6-808fbbe84772'
  containerAppsJobsContributor: '4e3d2b60-56ae-4dc6-a233-09c8e5a82e68'
}

// Cosmos DB's data-plane roles are its own, not Azure RBAC (ADR-024).
var cosmosDataContributorRoleId = '00000000-0000-0000-0000-000000000002'

// ============================================================
// Resource group tags
// ============================================================
// The group's own tags come from the same `tags` variable as everything else,
// so deploy.ps1 doesn't define them a second time.

resource resourceGroupTags 'Microsoft.Resources/tags@2021-04-01' = {
  name: 'default'
  properties: {
    tags: tags
  }
}

// ============================================================
// Monitoring: Log Analytics + Application Insights (D4)
// ============================================================

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-touchline-ai'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: {
      dailyQuotaGb: json('0.1') // hard cap on ingestion, to protect the £30 budget
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-touchline-ai'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// ============================================================
// Identity: one user-assigned identity for the container apps (D4)
// ============================================================
// User-assigned rather than system-assigned, so roles can be granted before
// the container app exists. The first ACR pull depends on that.

resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-touchline-ai'
  location: location
  tags: tags
}

// One identity per pipeline workload, each granted only its own data roles (ADR-024).
resource processorIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-touchline-processor'
  location: location
  tags: tags
}

resource simulatorIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-touchline-simulator'
  location: location
  tags: tags
}

// ============================================================
// CI identity: GitHub Actions signs in with OIDC, no stored secret (ADR-013)
// ============================================================

resource ciIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-touchline-ci'
  location: location
  tags: tags
}

// Trusts GitHub's OIDC tokens for this repository's main branch only, so a
// workflow run from any other branch or fork can't obtain an Azure token.
// The subject must match the token exactly, IDs included.
resource ciGithubCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: ciIdentity
  name: 'github-main'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: '${githubSubjectPrefix}:ref:refs/heads/main'
    audiences: ['api://AzureADTokenExchange']
  }
}

// ============================================================
// Key Vault (D4)
// ============================================================

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true // always on; can't be disabled
    enablePurgeProtection: environment == 'prod' ? true : null // null = off, so teardown can purge the name
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

// ============================================================
// Container Registry (D1)
// ============================================================

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  tags: tags
  sku: { name: 'Basic' } // the only always-on cost: about £4 a month
  properties: {
    adminUserEnabled: false // pulls use the managed identity, never admin credentials
  }
}

// ============================================================
// Microsoft Foundry (D2)
// ============================================================

resource foundry 'Microsoft.CognitiveServices/accounts@2026-03-01' = {
  name: foundryName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: foundryName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true // keyless: Entra ID tokens only, no API keys to leak
    allowProjectManagement: true // lets ADR-006 add a Foundry project later
  }
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2026-03-01' = {
  parent: foundry
  name: 'text-embedding-3-small'
  sku: {
    name: 'GlobalStandard'
    capacity: 120 // thousands of tokens per minute
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-3-small'
      version: '1'
    }
  }
}

// The chat model deployment arrives with ADR-007.

// ============================================================
// Container Apps (D1)
// ============================================================

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-touchline-ai'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

resource matchCentre 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-matchcentre'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${appIdentity.id}': {}
    }
  }
  dependsOn: [acrPullForApps] // the identity must be able to pull before the app is created
  properties: {
    environmentId: containerAppsEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Multiple' // lets ADR-009 pin a "judging" revision label
      ingress: {
        external: true
        targetPort: matchCentrePort
        transport: 'auto'
        allowInsecure: false // ingress redirects HTTP to HTTPS, so the app never calls UseHttpsRedirection
        traffic: matchCentreTraffic
      }
      secrets: [
        {
          // A secret rather than a plain env value, so Reader access to the app doesn't reveal it.
          name: 'appinsights-connection-string'
          value: appInsights.properties.ConnectionString
        }
      ]
      registries: [
        {
          server: registry.properties.loginServer
          identity: appIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'matchcentre'
          image: matchCentreImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', secretRef: 'appinsights-connection-string' }
            { name: 'AZURE_CLIENT_ID', value: appIdentity.properties.clientId }
          ]
          // The placeholder image has no /alive or /health, so probes only apply to the real image.
          // The startup probe holds off liveness while a cold start from zero replicas
          // finishes (up to about 55 seconds), so a slow start isn't killed mid-boot.
          probes: isPlaceholder ? [] : [
            {
              type: 'Startup'
              httpGet: { path: '/alive', port: matchCentrePort }
              initialDelaySeconds: 5
              periodSeconds: 5
              failureThreshold: 10
            }
            {
              type: 'Liveness'
              httpGet: { path: '/alive', port: matchCentrePort }
              periodSeconds: 10
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health', port: matchCentrePort }
              periodSeconds: 10
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0 // scale to zero between demos
        maxReplicas: 2
      }
    }
  }
}

// ============================================================
// Messaging: Event Hubs for the stream, Service Bus for key moments (ADR-004, D3)
// ============================================================
// Both are keyless (ADR-024): disableLocalAuth removes the SAS keys, so every
// client signs in with Entra ID and needs a data role on the entity it uses.

resource eventHubs 'Microsoft.EventHub/namespaces@2024-01-01' = {
  name: eventHubsName
  location: location
  tags: tags
  sku: {
    name: 'Basic' // about £9 a month for one throughput unit (ADR-016)
    tier: 'Basic'
    capacity: 1
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
  }
}

resource matchEventsHub 'Microsoft.EventHub/namespaces/eventhubs@2024-01-01' = {
  parent: eventHubs
  name: matchEventsHubName
  properties: {
    partitionCount: 2 // two matches processed in parallel; fixed for the hub's life on Basic
    messageRetentionInDays: 1 // the Basic maximum
  }
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: serviceBusName
  location: location
  tags: tags
  sku: {
    name: 'Standard' // topics need Standard: Basic has queues only. About £8 a month base (ADR-024)
    tier: 'Standard'
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
  }
}

resource keyMomentsTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: serviceBus
  name: keyMomentsTopicName
  properties: {
    // A republished moment carries the same MessageId, {matchId}:{sequence}, so it's dropped.
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    defaultMessageTimeToLive: 'P1D'
  }
}

resource keyMomentSubscriptions 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = [
  for subscriptionName in ['commentary', 'highlights']: {
    parent: keyMomentsTopic
    name: subscriptionName
    properties: {
      maxDeliveryCount: 10
      // Nothing reads these subscriptions until Stage 3's agents. Until then an unread moment
      // simply expires after the topic's day, rather than piling up in the dead-letter queue
      // as stale "failures". Stage 3 turns this on with its consumers.
      deadLetteringOnMessageExpiration: false
    }
  }
]

// The trigger, the producer and the Service Bus client all want the bare host name.
var eventHubsHost = split(split(eventHubs.properties.serviceBusEndpoint, '//')[1], ':')[0]
var serviceBusHost = split(split(serviceBus.properties.serviceBusEndpoint, '//')[1], ':')[0]

// ============================================================
// Cosmos DB for NoSQL, serverless (D2)
// ============================================================
// Billed per request unit and per GB, with no hourly charge. Keyless, so the
// data roles below (Cosmos DB's own, not Azure RBAC) are the only way in.

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: cosmosName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    capabilities: [{ name: 'EnableServerless' }]
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: cosmos
  name: cosmosDatabaseName
  properties: {
    resource: { id: cosmosDatabaseName }
  }
}

// The same containers as the AppHost's emulator (ADR-023).
resource cosmosContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [
  for containerName in ['events', 'matches']: {
    parent: cosmosDatabase
    name: containerName
    properties: {
      resource: {
        id: containerName
        partitionKey: {
          paths: ['/matchId']
          kind: 'Hash'
          version: 2
        }
      }
    }
  }
]

// ============================================================
// Event Processor: Functions on Flex Consumption (ADR-015, D3)
// ============================================================
// Its own storage account holds the deployment package, the host's leases and
// the Event Hubs checkpoints. Shared keys are off, so only the processor
// identity can reach it.

resource processorStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: processorStorageName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource processorBlobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: processorStorage
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: processorBlobs
  name: deploymentContainerName
}

resource processorPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'asp-touchline-processor'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true // Linux, the only option on Flex Consumption
  }
}

resource processorApp 'Microsoft.Web/sites@2024-04-01' = {
  name: processorAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${processorIdentity.id}': {}
    }
  }
  // The identity must reach its storage before the host first starts.
  dependsOn: [storageOwnerForProcessor, deploymentContainer]
  properties: {
    serverFarmId: processorPlan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      appSettings: [
        // Host storage, through the identity.
        { name: 'AzureWebJobsStorage__accountName', value: processorStorage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: processorIdentity.properties.clientId }
        // The trigger's "eventhubs" connection (ProcessMatchEvents.ConnectionName).
        { name: 'eventhubs__fullyQualifiedNamespace', value: eventHubsHost }
        { name: 'eventhubs__credential', value: 'managedidentity' }
        { name: 'eventhubs__clientId', value: processorIdentity.properties.clientId }
        // Aspire's clients, keyless: DefaultAzureCredential picks the identity named by AZURE_CLIENT_ID.
        { name: 'Aspire__Microsoft__Azure__Cosmos__AccountEndpoint', value: cosmos.properties.documentEndpoint }
        { name: 'Aspire__Azure__Messaging__ServiceBus__FullyQualifiedNamespace', value: serviceBusHost }
        { name: 'AZURE_CLIENT_ID', value: processorIdentity.properties.clientId }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
      ]
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${processorStorage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: processorIdentity.id
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 40 // the trigger scales to one instance per partition, so two in practice
        instanceMemoryMB: 2048
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
  }
}

// ============================================================
// Simulator: a manually started Container Apps job (ADR-024, D1)
// ============================================================
// Each execution plays the configured matches and exits. infra/start-match.ps1
// starts one with a fresh seed.

resource simulatorJob 'Microsoft.App/jobs@2024-03-01' = {
  name: 'job-simulator'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${simulatorIdentity.id}': {}
    }
  }
  dependsOn: [acrPullForSimulator] // the identity must be able to pull before the first execution
  properties: {
    environmentId: containerAppsEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      // 30 minutes: a match at the default 10x takes about 9. start-match.ps1 refuses speeds
      // below 4, which would run past it (about 100 minutes of match clock).
      replicaTimeout: 1800
      replicaRetryLimit: 0 // a retry replays the same seed, which the processor ignores as duplicates
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      secrets: [
        {
          name: 'appinsights-connection-string'
          value: appInsights.properties.ConnectionString
        }
      ]
      registries: [
        {
          server: registry.properties.loginServer
          identity: simulatorIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'simulator'
          image: simulatorImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', secretRef: 'appinsights-connection-string' }
            { name: 'AZURE_CLIENT_ID', value: simulatorIdentity.properties.clientId }
            { name: 'Aspire__Azure__Messaging__EventHubs__EventHubProducerClient__FullyQualifiedNamespace', value: eventHubsHost }
            { name: 'Aspire__Azure__Messaging__EventHubs__EventHubProducerClient__EventHubName', value: matchEventsHubName }
            { name: 'Simulator__ExitWhenDone', value: 'true' }
            // Its role name in Application Insights, the same as its Aspire resource name. Without it
            // OpenTelemetry falls back to "unknown_service:dotnet".
            { name: 'OTEL_SERVICE_NAME', value: 'simulator' }
          ]
        }
      ]
    }
  }
}

// ============================================================
// Role assignments: least privilege (D4)
// ============================================================

// App identity: pull images, call Foundry models, read secrets. Nothing more.
resource acrPullForApps 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, appIdentity.id, roles.acrPull)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource foundryUserForApps 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundry.id, appIdentity.id, roles.cognitiveServicesOpenAiUser)
  scope: foundry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesOpenAiUser)
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource secretsUserForApps 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, appIdentity.id, roles.keyVaultSecretsUser)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// CI identity: build images in the registry and update the one container app.
// It can't create resources, read other resources or grant roles (ADR-013).
resource tasksContributorForCi 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, ciIdentity.id, roles.containerRegistryTasksContributor)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.containerRegistryTasksContributor)
    principalId: ciIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource containerAppsContributorForCi 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(matchCentre.id, ciIdentity.id, roles.containerAppsContributor)
  scope: matchCentre
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.containerAppsContributor)
    principalId: ciIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// `az containerapp update` reads the app's environment before updating it, and
// Container Apps Contributor only covers its own scope (the app). Reader on the
// environment alone fills that gap without granting read across the group.
resource environmentReaderForCi 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerAppsEnvironment.id, ciIdentity.id, roles.reader)
  scope: containerAppsEnvironment
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.reader)
    principalId: ciIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Simulator identity: pull its image and send to match-events. Nothing more (ADR-024).
resource acrPullForSimulator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, simulatorIdentity.id, roles.acrPull)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
    principalId: simulatorIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource hubSenderForSimulator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(matchEventsHub.id, simulatorIdentity.id, roles.eventHubsDataSender)
  scope: matchEventsHub
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.eventHubsDataSender)
    principalId: simulatorIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Processor identity: read match-events, write the touchline database, send
// to key-moments, and own its host storage. Each scoped to that one entity.
resource hubReceiverForProcessor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(matchEventsHub.id, processorIdentity.id, roles.eventHubsDataReceiver)
  scope: matchEventsHub
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.eventHubsDataReceiver)
    principalId: processorIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource topicSenderForProcessor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyMomentsTopic.id, processorIdentity.id, roles.serviceBusDataSender)
  scope: keyMomentsTopic
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataSender)
    principalId: processorIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource storageOwnerForProcessor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(processorStorage.id, processorIdentity.id, roles.storageBlobDataOwner)
  scope: processorStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataOwner)
    principalId: processorIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Cosmos DB data roles are child resources of the account, with a path for a scope.
resource cosmosContributorForProcessor 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmos
  name: guid(cosmos.id, processorIdentity.id, cosmosDataContributorRoleId)
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
    principalId: processorIdentity.properties.principalId
    scope: '${cosmos.id}/dbs/${cosmosDatabase.name}'
  }
}

// CI identity, continued: deploy the function's package and point the job at a new image.
resource websiteContributorForCi 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(processorApp.id, ciIdentity.id, roles.websiteContributor)
  scope: processorApp
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.websiteContributor)
    principalId: ciIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Container Apps Contributor doesn't cover jobs; this role does, on this job only.
resource jobsContributorForCi 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(simulatorJob.id, ciIdentity.id, roles.containerAppsJobsContributor)
  scope: simulatorJob
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.containerAppsJobsContributor)
    principalId: ciIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Developer: browse the database in Data Explorer, which needs a data role once keys are off.
resource cosmosContributorForDeveloper 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = if (!empty(principalId)) {
  parent: cosmos
  name: guid(cosmos.id, principalId, cosmosDataContributorRoleId)
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
    principalId: principalId
    scope: cosmos.id
  }
}

// Developer: call models and manage secrets from a local run. Skipped when a
// service principal deploys from CI, because there's no developer to grant.
resource foundryUserForDeveloper 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  name: guid(foundry.id, principalId, roles.cognitiveServicesOpenAiUser)
  scope: foundry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesOpenAiUser)
    principalId: principalId
    principalType: 'User'
  }
}

resource secretsOfficerForDeveloper 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  name: guid(keyVault.id, principalId, roles.keyVaultSecretsOfficer)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsOfficer)
    principalId: principalId
    principalType: 'User'
  }
}

// ============================================================
// Budget (ADR-011)
// ============================================================
// A budget alerts but doesn't stop spending. Hourly-billed resources still
// get a teardown date in the resource ledger.

resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: 'budget-touchline-ai'
  properties: {
    category: 'Cost'
    amount: monthlyBudget
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: budgetStartDate
    }
    notifications: {
      actual50: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 50
        thresholdType: 'Actual'
        contactEmails: [ownerEmail]
      }
      actual80: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: [ownerEmail]
      }
      forecast100: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: [ownerEmail]
      }
    }
  }
}

// ============================================================
// Outputs (no secrets)
// ============================================================

output matchCentreUrl string = 'https://${matchCentre.properties.configuration.ingress.fqdn}'
output registryLoginServer string = registry.properties.loginServer
output foundryEndpoint string = foundry.properties.endpoint
output keyVaultUri string = keyVault.properties.vaultUri
output appIdentityClientId string = appIdentity.properties.clientId
output ciIdentityClientId string = ciIdentity.properties.clientId
output registryName string = registry.name
output processorAppName string = processorApp.name
output simulatorJobName string = simulatorJob.name
output cosmosEndpoint string = cosmos.properties.documentEndpoint
output eventHubsNamespace string = eventHubs.name
output serviceBusNamespace string = serviceBus.name
