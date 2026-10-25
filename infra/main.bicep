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

@description('Region for the Azure AI Search service only. Sweden Central had no Search capacity on 24 Oct, so Search lives in UK South (ADR-058). Defaults to UK South, so a direct deploy of this template avoids Sweden Central too.')
param searchLocation string = 'uksouth'

@description('Short environment label stamped on every resource tag.')
@allowed(['dev', 'prod'])
param environment string = 'dev'

@description('Object ID of the signed-in developer, who gets data-plane roles for local development. Empty when a service principal deploys from CI.')
param principalId string = ''

@description('Where budget alerts are sent. Supplied at deploy time, never committed.')
param ownerEmail string

@description('Monthly budget in the billing currency (GBP). ADR-011, raised to £60 by ADR-026 for Azure AI Search Basic.')
param monthlyBudget int = 60

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

@description('Create the Azure AI Search service (ADR-026, ADR-041). Off by default: Basic bills about 2 pounds a day while it exists. Flip it on for the retrieval work, and tear it down by the ADR-026 date.')
param deploySearch bool = false

// Global Standard capacity is a rate limit, in thousands of tokens per minute, not a reservation:
// tokens are billed as used. A higher limit raises the most a runaway loop could spend in a
// minute, so the defaults are the smallest that carry one live match. They're raised only for demo
// days, with a redeploy, then lowered again (ADR-051).
@description('Deploy the squad\'s chat models, their content filter and the Squad__* settings. Off by default, so a model quota failure can\'t hold up the rest of the template. deploy.ps1 keeps it on once the models exist (ADR-051).')
param deploySquadModels bool = false

@description('Point the Match Centre at Content Safety for the team builder\'s name guard (ADR-049). Off by default: the guard fails closed, so with it on and the blocklist not yet loaded every team create returns 503. Load the blocklist (infra/load-name-blocklist.ps1) first, then turn it on.')
param deployNameGuard bool = false

@description('Let fans start matches on the pipeline (ADR-050): gives the Match Centre the simulator job\'s resource ID. Off until the limits slice (one live, five a day) is deployed, because without it every POST /api/matches starts a billed job execution; a kill switch afterwards. The job role and the match-requests container are deployed either way.')
param deployFanMatches bool = false

@description('Tokens-per-minute limit, in thousands, for the small chat model (Commentator, Localiser).')
@minValue(1)
param smallModelCapacity int = 50

@description('Tokens-per-minute limit, in thousands, for the large chat model (Analyst, Producer).')
@minValue(1)
param largeModelCapacity int = 30

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
var matchFinishedTopicName = 'match-finished'
var cosmosDatabaseName = 'touchline'
var deploymentContainerName = 'deployment-package'

// The two chat models (ADR-007). The deployment name is the model name, and the apps read it from
// Squad__Deployments__Small and Squad__Deployments__Large, so changing a model is a redeploy.
// Versions are the Global Standard ones listed for Sweden Central on 24 Oct.
var smallModel = {
  name: 'gpt-5.4-mini'
  version: '2026-03-17'
}
var largeModel = {
  name: 'gpt-5.5'
  version: '2026-04-24'
}

// The Azure OpenAI v1 API host for the Foundry account. The squad's clients add /openai/v1/.
var foundryOpenAiEndpoint = 'https://${foundryName}.openai.azure.com/'

// The squad's model settings for both hosts (ADR-030), keyless: the identity named by
// AZURE_CLIENT_ID signs in. Without them the hosts run on template lines and stand-in answers.
var squadSettings = deploySquadModels
  ? [
      { name: 'Squad__FoundryEndpoint', value: foundryOpenAiEndpoint }
      { name: 'Squad__Deployments__Small', value: smallModel.name }
      { name: 'Squad__Deployments__Large', value: largeModel.name }
    ]
  : []

// The Match Centre's name guard (ADR-049): Content Safety on the Foundry account, keyless. The Content
// Safety API lives on the cognitiveservices.azure.com host, not the openai.azure.com one above.
var contentSafetyBlocklistName = 'touchline-real-names' // must match infra/load-name-blocklist.ps1
var nameGuardSettings = deployNameGuard
  ? [
      { name: 'MatchCentre__ContentSafety__Endpoint', value: 'https://${foundryName}.cognitiveservices.azure.com/' }
      { name: 'MatchCentre__ContentSafety__BlocklistName', value: contentSafetyBlocklistName }
    ]
  : []

var placeholderImage = 'mcr.microsoft.com/k8se/quickstart:latest'
var isPlaceholder = matchCentreImage == placeholderImage

// The placeholder listens on 80. .NET container images listen on 8080.
var matchCentrePort = isPlaceholder ? 80 : 8080

// Built-in role definition IDs.
var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  cognitiveServicesOpenAiUser: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
  // Calls the account's Content Safety API (ADR-049). The OpenAI User role does not cover it.
  cognitiveServicesUser: 'a97b65f3-24c7-4388-baec-2e87135dc908'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  keyVaultSecretsOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
  containerRegistryTasksContributor: 'fb382eab-e894-4461-af04-94435c366c3f'
  containerAppsContributor: '358470bc-b998-42bd-ab17-a7e34c199c0f'
  reader: 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
  eventHubsDataSender: '2b629674-e913-4c01-ae53-ef4638d8f975'
  eventHubsDataReceiver: 'a638d3c7-ab3a-418d-83e6-5f17a39d4fde'
  serviceBusDataSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  serviceBusDataReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  websiteContributor: 'de139f84-1756-47ae-9be6-808fbbe84772'
  containerAppsJobsContributor: '4e3d2b60-56ae-4dc6-a233-09c8e5a82e68'
  searchIndexDataContributor: '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
  searchIndexDataReader: '1407120a-92aa-4202-b7e9-c0e197c71c8f'
  searchServiceContributor: '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
}

// Azure AI Search (ADR-026, ADR-040, ADR-041).
var searchName = 'srch-touchline-${suffix}'
var searchIndexName = 'possessions'
var searchSubscriptionName = 'search-index'
var searchEndpoint = 'https://${searchName}.search.windows.net'

// Cosmos DB's data-plane roles are its own, not Azure RBAC (ADR-024).
var cosmosDataContributorRoleId = '00000000-0000-0000-0000-000000000002'
var cosmosDataReaderRoleId = '00000000-0000-0000-0000-000000000001'

// The one container the simulator may read: the season's bans (ADR-025).
var playerSeasonsContainerName = 'player-seasons'

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

// The chat models' content filter (ADR-044): the default harm categories at their default
// thresholds, plus Prompt Shields on the prompt. Jailbreak catches a fan trying to talk the
// Analyst out of its instructions; Indirect Attack catches instructions hidden in tool results
// and other data the agents are handed. A blocked call throws, and every agent already falls
// back to its template line or stand-in answer.
resource squadContentFilter 'Microsoft.CognitiveServices/accounts/raiPolicies@2026-03-01' = if (deploySquadModels) {
  parent: foundry
  name: 'touchline-squad'
  dependsOn: [embeddingDeployment] // one change at a time on the account, or it answers 409
  properties: {
    basePolicyName: 'Microsoft.DefaultV2'
    mode: 'Default'
    contentFilters: [
      { name: 'Hate', blocking: true, enabled: true, severityThreshold: 'Medium', source: 'Prompt' }
      { name: 'Hate', blocking: true, enabled: true, severityThreshold: 'Medium', source: 'Completion' }
      { name: 'Sexual', blocking: true, enabled: true, severityThreshold: 'Medium', source: 'Prompt' }
      { name: 'Sexual', blocking: true, enabled: true, severityThreshold: 'Medium', source: 'Completion' }
      { name: 'Violence', blocking: true, enabled: true, severityThreshold: 'Medium', source: 'Prompt' }
      { name: 'Violence', blocking: true, enabled: true, severityThreshold: 'Medium', source: 'Completion' }
      { name: 'Selfharm', blocking: true, enabled: true, severityThreshold: 'Medium', source: 'Prompt' }
      { name: 'Selfharm', blocking: true, enabled: true, severityThreshold: 'Medium', source: 'Completion' }
      // Prompt Shields.
      { name: 'Jailbreak', blocking: true, enabled: true, source: 'Prompt' }
      { name: 'Indirect Attack', blocking: true, enabled: true, source: 'Prompt' }
      { name: 'Protected Material Text', blocking: true, enabled: true, source: 'Completion' }
      { name: 'Protected Material Code', blocking: false, enabled: true, source: 'Completion' }
    ]
  }
}

// Model deployments on one account can't change at the same time, so each waits for the one
// before it.
resource smallModelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2026-03-01' = if (deploySquadModels) {
  parent: foundry
  name: smallModel.name
  dependsOn: [embeddingDeployment]
  sku: {
    name: 'GlobalStandard'
    capacity: smallModelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: smallModel.name
      version: smallModel.version
    }
    raiPolicyName: squadContentFilter.name
    versionUpgradeOption: 'OnceCurrentVersionExpired' // no silent model change mid-judging
  }
}

resource largeModelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2026-03-01' = if (deploySquadModels) {
  parent: foundry
  name: largeModel.name
  dependsOn: [smallModelDeployment]
  sku: {
    name: 'GlobalStandard'
    capacity: largeModelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: largeModel.name
      version: largeModel.version
    }
    raiPolicyName: squadContentFilter.name
    versionUpgradeOption: 'OnceCurrentVersionExpired'
  }
}

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
  // The identity must be able to pull before the app is created, and the models it's pointed at must exist.
  dependsOn: [acrPullForApps, largeModelDeployment]
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
            // Keyless and read-only, for the live read model (ADR-038), the same way as the simulator.
            { name: 'Aspire__Microsoft__Azure__Cosmos__AccountEndpoint', value: cosmos.properties.documentEndpoint }
            ...(deploySearch ? [{ name: 'Search__Endpoint', value: searchEndpoint }] : []) // ADR-041
            ...squadSettings // the Analyst's models (ADR-030), only when deploySquadModels is on
            ...nameGuardSettings // the team builder's name guard (ADR-049), only when deployNameGuard is on
            // The job a fan match starts (ADR-050), only while deployFanMatches is on; without it the
            // launcher stays unconnected and answers 503.
            ...(deployFanMatches ? [{ name: 'MatchCentre__FanMatches__JobResourceId', value: simulatorJob.id }] : [])
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

// Read by CommentaryFunction (ADR-030), with the receiver role below.
resource commentarySubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: keyMomentsTopic
  name: 'commentary'
  properties: {
    // Each delivery can make up to five model calls, so a failing moment gets three (ADR-052).
    maxDeliveryCount: 3
    // An expired moment is a day old, and commentary on it is no use to anyone, so it expires
    // rather than piling up in the dead-letter queue.
    deadLetteringOnMessageExpiration: false
  }
}

// Nothing reads this yet. An unread moment simply expires after the topic's day, rather than
// piling up in the dead-letter queue as stale "failures".
resource highlightsSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: keyMomentsTopic
  name: 'highlights'
  properties: {
    maxDeliveryCount: 10
    deadLetteringOnMessageExpiration: false
  }
}

// A match reaching full time (ADR-030, amended 23 Oct). The processor publishes one message per
// match, with MessageId {matchId}:finished, so a republish after a redelivered batch is dropped.
resource matchFinishedTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: serviceBus
  name: matchFinishedTopicName
  properties: {
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    defaultMessageTimeToLive: 'P1D'
  }
}

resource recapsSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: matchFinishedTopic
  name: 'recaps'
  properties: {
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: true
  }
}

// The search indexer function (ADR-039) reads its own subscription, like the recaps function.
// Created even when Search isn't deployed, so the trigger always has a subscription to bind to.
// Unread messages expire after the topic's day without piling up in the dead-letter queue.
resource searchIndexSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: matchFinishedTopic
  name: searchSubscriptionName
  properties: {
    maxDeliveryCount: 3 // model-calling consumers retry at most 3 times (ADR-052)
    deadLetteringOnMessageExpiration: false
  }
}

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

// The same containers as the AppHost's emulator: the match store (ADR-023), the season
// projection (ADR-025), and the change feed's leases, which the Functions trigger requires to be
// partitioned by /id. The trigger can't create it: data roles can't create containers.
var cosmosContainerDefinitions = [
  { name: 'events', partitionKey: '/matchId' }
  { name: 'matches', partitionKey: '/matchId' }
  { name: 'player-matches', partitionKey: '/playerId' }
  { name: playerSeasonsContainerName, partitionKey: '/season' }
  { name: 'leases', partitionKey: '/id' }
  // The agents' commentary, which the Match Centre follows on the change feed (ADR-029).
  { name: 'commentary', partitionKey: '/matchId' }
  // Each finished match's recaps, written once by the processor at full time (ADR-030, amended 23 Oct).
  { name: 'recaps', partitionKey: '/matchId' }
  // Fan-built teams (ADR-048). defaultTtl -1 turns time to live on without expiring anything: a delete
  // sets a per-document ttl, so the change feed sees the flag before Cosmos DB removes the document.
  { name: 'teams', partitionKey: '/id', defaultTtl: -1 }
  // Fan match requests (ADR-050): written by the Match Centre, played by the simulator job. defaultTtl -1:
  // requests never expire (their snapshots are the roster of record), but the limits' day counters and
  // Idempotency-Key claims beside them set a per-document ttl and clear themselves.
  { name: 'match-requests', partitionKey: '/id', defaultTtl: -1 }
]

resource cosmosContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [
  for container in cosmosContainerDefinitions: {
    parent: cosmosDatabase
    name: container.name
    properties: {
      resource: {
        id: container.name
        #disable-next-line BCP187 // The 2024-11-15 type definition omits defaultTtl; the service accepts it.
        defaultTtl: container.?defaultTtl
        partitionKey: {
          paths: [container.partitionKey]
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
  // The squad's models, when deployed, must exist before the settings point at them.
  dependsOn: [storageOwnerForProcessor, deploymentContainer, largeModelDeployment]
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
        // The change feed trigger's "cosmos" connection (ProjectSeason.ConnectionName, ADR-025).
        { name: 'cosmos__accountEndpoint', value: cosmos.properties.documentEndpoint }
        { name: 'cosmos__credential', value: 'managedidentity' }
        { name: 'cosmos__clientId', value: processorIdentity.properties.clientId }
        // The commentary trigger's connection (CommentaryFunction.ConnectionName, ADR-030).
        { name: 'servicebus__fullyQualifiedNamespace', value: serviceBusHost }
        { name: 'servicebus__credential', value: 'managedidentity' }
        { name: 'servicebus__clientId', value: processorIdentity.properties.clientId }
        // The recap trigger's own connection (RecapFunction.ConnectionName, ADR-030 amended 23 Oct).
        { name: 'servicebusrecaps__fullyQualifiedNamespace', value: serviceBusHost }
        { name: 'servicebusrecaps__credential', value: 'managedidentity' }
        { name: 'servicebusrecaps__clientId', value: processorIdentity.properties.clientId }
        // The search indexer trigger's own connection (PossessionIndexFunction.ConnectionName, ADR-039).
        { name: 'servicebussearch__fullyQualifiedNamespace', value: serviceBusHost }
        { name: 'servicebussearch__credential', value: 'managedidentity' }
        { name: 'servicebussearch__clientId', value: processorIdentity.properties.clientId }
        // Aspire's clients, keyless: DefaultAzureCredential picks the identity named by AZURE_CLIENT_ID.
        { name: 'Aspire__Microsoft__Azure__Cosmos__AccountEndpoint', value: cosmos.properties.documentEndpoint }
        { name: 'Aspire__Azure__Messaging__ServiceBus__FullyQualifiedNamespace', value: serviceBusHost }
        { name: 'AZURE_CLIENT_ID', value: processorIdentity.properties.clientId }
        ...(deploySearch ? [{ name: 'Search__Endpoint', value: searchEndpoint }] : []) // ADR-041
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        ...squadSettings // the squad's models (ADR-030), only when deploySquadModels is on
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
            // Read-only, for the season's bans before a fixture (ADR-025).
            { name: 'Aspire__Microsoft__Azure__Cosmos__AccountEndpoint', value: cosmos.properties.documentEndpoint }
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
// Azure AI Search (ADR-026, ADR-039, ADR-040, ADR-041)
// ============================================================
// Basic, one replica, one partition: about 2 pounds a day while it exists, so it's behind
// deploySearch. Keyless: with local auth off there are no admin or query keys at all, so every
// caller needs a data role. The free semantic plan caps semantic queries at 1,000 a month;
// standard billing needs sign-off. The index itself is created from
// infra/search/possessions-index.json by deploy.ps1 (-Action create-index).

resource search 'Microsoft.Search/searchServices@2025-05-01' = if (deploySearch) {
  name: searchName
  location: searchLocation // ADR-058
  tags: tags
  sku: { name: 'basic' }
  properties: {
    replicaCount: 1
    partitionCount: 1
    disableLocalAuth: true
    semanticSearch: 'free'
    publicNetworkAccess: 'enabled'
  }
}

// Processor identity: write documents (merge or upload). It never creates the index.
resource searchContributorForProcessor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deploySearch) {
  name: guid(search.id, processorIdentity.id, roles.searchIndexDataContributor)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.searchIndexDataContributor)
    principalId: processorIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Match Centre identity: query only.
resource searchReaderForMatchCentre 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deploySearch) {
  name: guid(search.id, appIdentity.id, roles.searchIndexDataReader)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.searchIndexDataReader)
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Developer: query from a local run, and create the index (a management operation, so it also
// needs Search Service Contributor). Skipped when CI deploys.
resource searchContributorForDeveloper 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deploySearch && !empty(principalId)) {
  name: guid(search.id, principalId, roles.searchIndexDataContributor)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.searchIndexDataContributor)
    principalId: principalId
    principalType: 'User'
  }
}

resource searchServiceContributorForDeveloper 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deploySearch && !empty(principalId)) {
  name: guid(search.id, principalId, roles.searchServiceContributor)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.searchServiceContributor)
    principalId: principalId
    principalType: 'User'
  }
}

// Processor identity: read its own search-index subscription only.
resource searchIndexReceiverForProcessor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchIndexSubscription.id, processorIdentity.id, roles.serviceBusDataReceiver)
  scope: searchIndexSubscription
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataReceiver)
    principalId: processorIdentity.properties.principalId
    principalType: 'ServicePrincipal'
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

resource finishedSenderForProcessor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(matchFinishedTopic.id, processorIdentity.id, roles.serviceBusDataSender)
  scope: matchFinishedTopic
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataSender)
    principalId: processorIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// The commentary function reads its own subscription only.
resource commentaryReceiverForProcessor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(commentarySubscription.id, processorIdentity.id, roles.serviceBusDataReceiver)
  scope: commentarySubscription
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataReceiver)
    principalId: processorIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// The recap function reads its own subscription only.
resource recapsReceiverForProcessor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(recapsSubscription.id, processorIdentity.id, roles.serviceBusDataReceiver)
  scope: recapsSubscription
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataReceiver)
    principalId: processorIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Call the squad's models for the commentary and the recaps (ADR-030). Inference only: it can't
// list keys, change deployments or manage the account. The role can't be narrowed below the
// account, so it covers the embedding deployment too.
resource foundryUserForProcessor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundry.id, processorIdentity.id, roles.cognitiveServicesOpenAiUser)
  scope: foundry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesOpenAiUser)
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

// Simulator identity: read the season's bans before a fixture (ADR-025). Read-only, and on one
// container, so it can't see the events or the match store, let alone write to them.
resource cosmosReaderForSimulator 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmos
  name: guid(cosmos.id, simulatorIdentity.id, cosmosDataReaderRoleId)
  dependsOn: [cosmosContainers]
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataReaderRoleId}'
    principalId: simulatorIdentity.properties.principalId
    scope: '${cosmos.id}/dbs/${cosmosDatabase.name}/colls/${playerSeasonsContainerName}'
  }
}

// Match Centre identity: read the match store, the season cards, the commentary and the recaps (ADR-038).
// Read-only, with one assignment per container, so it can't see the season projection's
// working set or the leases, let alone write anything.
var matchCentreReadContainers = ['matches', 'events', playerSeasonsContainerName, 'commentary', 'recaps']

resource cosmosReaderForMatchCentre 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = [
  for container in matchCentreReadContainers: {
    parent: cosmos
    name: guid(cosmos.id, appIdentity.id, cosmosDataReaderRoleId, container)
    dependsOn: [cosmosContainers]
    properties: {
      roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataReaderRoleId}'
      principalId: appIdentity.properties.principalId
      scope: '${cosmos.id}/dbs/${cosmosDatabase.name}/colls/${container}'
    }
  }
]

// Match Centre identity: write the teams container, and nothing else (ADR-048). Fan teams are the one thing
// the Match Centre stores. Data Contributor on this one container also covers its reads and queries.
resource cosmosContributorForMatchCentreTeams 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmos
  name: guid(cosmos.id, appIdentity.id, cosmosDataContributorRoleId, 'teams')
  dependsOn: [cosmosContainers]
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
    principalId: appIdentity.properties.principalId
    scope: '${cosmos.id}/dbs/${cosmosDatabase.name}/colls/teams'
  }
}

// Fan match requests (ADR-050), one Data Contributor assignment per identity on this one container.
// The Match Centre writes requests and reads them for rosters; the simulator reads one and moves its
// status on. The processor reads them for rosters, which its Data Contributor role on the whole
// touchline database (above) already covers, so it needs no assignment of its own here.
resource cosmosContributorForMatchCentreRequests 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmos
  name: guid(cosmos.id, appIdentity.id, cosmosDataContributorRoleId, 'match-requests')
  dependsOn: [cosmosContainers]
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
    principalId: appIdentity.properties.principalId
    scope: '${cosmos.id}/dbs/${cosmosDatabase.name}/colls/match-requests'
  }
}

resource cosmosContributorForSimulatorRequests 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmos
  name: guid(cosmos.id, simulatorIdentity.id, cosmosDataContributorRoleId, 'match-requests')
  dependsOn: [cosmosContainers]
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
    principalId: simulatorIdentity.properties.principalId
    scope: '${cosmos.id}/dbs/${cosmosDatabase.name}/colls/match-requests'
  }
}

// Match Centre identity: read and start job-simulator, and nothing else (ADR-050). A custom role,
// because both built-in job roles (Operator and Contributor) grant Microsoft.App/jobs/*/action, which
// includes listSecrets. Starting a job can still reference the job's secrets from an execution
// template, so this is the narrowest grant that works, not a harmless one.
resource simulatorJobStarterRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, 'touchline-simulator-job-starter')
  properties: {
    roleName: 'Touchline simulator job starter (${resourceGroup().name})'
    description: 'Read a Container Apps job and start an execution of it. No listSecrets, stop, write or delete.'
    type: 'CustomRole'
    permissions: [
      {
        actions: ['Microsoft.App/jobs/read', 'Microsoft.App/jobs/start/action']
        notActions: []
      }
    ]
    assignableScopes: [resourceGroup().id]
  }
}

resource jobStarterForMatchCentre 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(simulatorJob.id, appIdentity.id, simulatorJobStarterRole.id)
  scope: simulatorJob
  properties: {
    roleDefinitionId: simulatorJobStarterRole.id
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Content Safety checks team-builder names (ADR-049), and needs its own role on the Foundry account.
resource contentSafetyUserForApps 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundry.id, appIdentity.id, roles.cognitiveServicesUser)
  scope: foundry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesUser)
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Developer: manage the blocklist from infra/load-name-blocklist.ps1.
resource contentSafetyUserForDeveloper 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  name: guid(foundry.id, principalId, roles.cognitiveServicesUser)
  scope: foundry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesUser)
    principalId: principalId
    principalType: 'User'
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
output squadFoundryEndpoint string = deploySquadModels ? foundryOpenAiEndpoint : ''
output smallModelDeployment string = deploySquadModels ? smallModel.name : ''
output largeModelDeployment string = deploySquadModels ? largeModel.name : ''
output keyVaultUri string = keyVault.properties.vaultUri
output appIdentityClientId string = appIdentity.properties.clientId
output ciIdentityClientId string = ciIdentity.properties.clientId
output registryName string = registry.name
output processorAppName string = processorApp.name
output simulatorJobName string = simulatorJob.name
output cosmosEndpoint string = cosmos.properties.documentEndpoint
output eventHubsNamespace string = eventHubs.name
output serviceBusNamespace string = serviceBus.name
output searchEndpoint string = deploySearch ? searchEndpoint : ''
output searchIndexName string = searchIndexName
