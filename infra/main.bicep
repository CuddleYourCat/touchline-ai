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
}

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
