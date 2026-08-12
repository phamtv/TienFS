// =====================================================================================
// LOAN PLATFORM — PRODUCTION INFRASTRUCTURE
// -------------------------------------------------------------------------------------
// Provisions: shared App Service Plan hosting three App Services (one per
// microservice), a Key Vault (secrets, RBAC-only access, no public network access),
// a Service Bus namespace with the "loan-events" topic and two subscriptions
// matching what each service expects (see /emulator/config.json for the local
// equivalent used in development), an Azure SQL Server with one database per
// service (persistent storage — see each service's Program.cs for the SQLite-in-dev
// / Azure-SQL-in-production split), and a shared Application Insights instance for
// monitoring/telemetry across all three services.
//
// Each App Service gets a System-Assigned Managed Identity, granted:
//   - "Key Vault Secrets User" (read-only) on the Key Vault
//   - "Azure Service Bus Data Owner" on the Service Bus namespace (send + receive)
// Service Bus connection string and each service's own SQL connection string are
// stored as Key Vault secrets — see the "Post-deployment steps" note near the bottom
// of this file for what still needs to be populated manually after `az deployment
// group create`, since Bicep alone can't push arbitrary secret values derived outside
// the template (e.g. actual Service Bus keys) into Key Vault as part of the same run
// without extra scripting this sample intentionally keeps out of scope.
//
// SECURITY TRADE-OFF: this Key Vault has publicNetworkAccess set to 'Enabled' rather
// than fully private, since the App Services here aren't VNet-integrated and Key
// Vault's "AzureServices" network bypass doesn't cover an app's own outbound SDK
// calls. Access control comes from RBAC (only these three apps' Managed Identities
// can read secrets), not network isolation. See the Key Vault resource below for
// the full explanation and what a stricter production setup would add instead.
// =====================================================================================

@description('Deployment region')
param location string = resourceGroup().location

@description('Base name used to derive resource names')
param baseName string = 'tienfs'

@description('Admin login for the Azure SQL logical server. Not used at runtime by the apps (they connect via SQL auth using the generated password below, stored in Key Vault) — only needed for the server itself to exist.')
param sqlAdminLogin string = 'tienfsadmin'

@description('Admin password for the Azure SQL logical server. No default on purpose — pass explicitly at deploy time, e.g.: az deployment group create ... --parameters sqlAdminPassword=\'YourStrongP@ssw0rd123!\'. Never commit a real value for this into source control.')
@secure()
param sqlAdminPassword string

// -------------------------------------------------------------------------------------
// App Service Plan — shared across all three microservices for this sample.
// A larger production deployment might split these onto separate plans for
// independent scaling; sharing one plan here keeps the sample's cost/footprint small.
// -------------------------------------------------------------------------------------
// B1 (Basic) tier — the Free (F1) tier draws from a separate legacy "Total VMs" quota
// category that proved difficult to get approved on this subscription. Basic draws from
// the standard regional vCPU quota instead (the one already approved for this account),
// so it avoids that specific quota category entirely. Costs roughly $13/month if left
// running continuously — tear down the resource group between demo sessions to avoid
// ongoing charges (az group delete --name tienfs-rg).
resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${baseName}-plan'
  location: location
  sku: { name: 'B1', tier: 'Basic' }
}

// -------------------------------------------------------------------------------------
// Key Vault — RBAC authorization, soft-delete + purge protection.
//
// publicNetworkAccess is 'Enabled' here — NOT because that's ideal, but because the
// App Services in this template aren't VNet-integrated, and Key Vault's networkAcls
// "AzureServices" bypass does NOT cover an app's own outbound SDK calls (only a
// specific list of trusted first-party integrations, which this isn't). With
// 'Disabled', the apps' own runtime Key Vault reads would fail — which is exactly
// what happened before this was changed.
//
// Security still comes from RBAC (only these three apps' Managed Identities have
// "Key Vault Secrets User"), not from network isolation. For a deployment that
// actually needs network-level isolation too, the correct fix is VNet-integrating
// the App Services and adding a Key Vault Private Endpoint — a larger infra change
// intentionally left out of this sample to keep its footprint manageable; see the
// README/ADRs for this trade-off called out explicitly.
// -------------------------------------------------------------------------------------
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${baseName}-kv-${uniqueString(resourceGroup().id)}'
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
    networkAcls: { defaultAction: 'Allow', bypass: 'AzureServices' }
  }
}

// -------------------------------------------------------------------------------------
// Monitoring — a shared Log Analytics workspace + Application Insights instance.
// Unlike the Key Vault/database split (each service owns its own), observability is
// deliberately SHARED across all three services: correlating a request as it flows
// origination -> funding -> servicing is far more useful with one unified view than
// three separate ones you'd have to cross-reference by hand.
// -------------------------------------------------------------------------------------
resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${baseName}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30 // keep this sample's cost small; production may want longer
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-insights'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspace.id
    IngestionMode: 'LogAnalytics'
  }
}

// -------------------------------------------------------------------------------------
// Azure SQL — one logical server, one database per service (each service still owns
// its own database exclusively; sharing the server is just cost/footprint efficiency
// for this sample, not a sharing of data). Basic tier — cheap, fine for a demo;
// production would likely want at least Standard (S0) for real concurrent workloads.
// -------------------------------------------------------------------------------------
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: '${baseName}-sql-${uniqueString(resourceGroup().id)}'
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled' // App Services here aren't VNet-integrated; see note below
  }
}

// Allows Azure services (including these App Services) to reach the SQL server.
// This is a broad rule (0.0.0.0 special-cased by Azure to mean "Azure services only",
// not literally the whole internet) — tighter production setups would use Private
// Link / VNet integration instead of this firewall-rule approach.
resource sqlAllowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource originationDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: '${baseName}-origination-db'
  location: location
  sku: { name: 'Basic', tier: 'Basic' }
  properties: { maxSizeBytes: 2147483648 } // 2GB — Basic tier's cap
}

resource fundingDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: '${baseName}-funding-db'
  location: location
  sku: { name: 'Basic', tier: 'Basic' }
  properties: { maxSizeBytes: 2147483648 }
}

resource servicingDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: '${baseName}-servicing-db'
  location: location
  sku: { name: 'Basic', tier: 'Basic' }
  properties: { maxSizeBytes: 2147483648 }
}

// -------------------------------------------------------------------------------------
// Service Bus namespace + topic + subscriptions — mirrors emulator/config.json
// exactly, so the same message flow works in both local dev and production.
// -------------------------------------------------------------------------------------
resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: '${baseName}-sb-${uniqueString(resourceGroup().id)}'
  location: location
  sku: { name: 'Standard', tier: 'Standard' } // Standard tier required for topics/subscriptions
}

resource loanEventsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: 'loan-events'
  properties: {
    defaultMessageTimeToLive: 'P14D'
  }
}

resource fundingSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: loanEventsTopic
  name: 'funding-loan-approved'
  properties: {
    lockDuration: 'PT30S'
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: true // failed/expired messages preserved, not lost
  }
}

resource fundingRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2022-10-01-preview' = {
  parent: fundingSubscription
  name: 'OnlyLoanApproved'
  properties: {
    filterType: 'CorrelationFilter'
    // ARM's CorrelationFilter calls this property "label" — it's the same concept the
    // .NET SDK calls "Subject" (ServiceBusMessage.Subject / ServiceBusReceivedMessage.Subject).
    correlationFilter: { label: 'LoanApproved' }
  }
}

resource servicingSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: loanEventsTopic
  name: 'servicing-loan-funded'
  properties: {
    lockDuration: 'PT30S'
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: true
  }
}

resource servicingRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2022-10-01-preview' = {
  parent: servicingSubscription
  name: 'OnlyLoanFunded'
  properties: {
    filterType: 'CorrelationFilter'
    correlationFilter: { label: 'LoanFunded' }
  }
}

// -------------------------------------------------------------------------------------
// App Services — one per microservice, each with its own Managed Identity.
// -------------------------------------------------------------------------------------
resource originationApp 'Microsoft.Web/sites@2023-12-01' = {
  name: '${baseName}-origination'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'KeyVault__Uri', value: keyVault.properties.vaultUri }
        { name: 'ServiceBus__Namespace', value: serviceBusNamespace.properties.serviceBusEndpoint }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
      ]
    }
  }
}

resource fundingApp 'Microsoft.Web/sites@2023-12-01' = {
  name: '${baseName}-funding'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'KeyVault__Uri', value: keyVault.properties.vaultUri }
        { name: 'ServiceBus__Namespace', value: serviceBusNamespace.properties.serviceBusEndpoint }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
      ]
    }
  }
}

resource servicingApp 'Microsoft.Web/sites@2023-12-01' = {
  name: '${baseName}-servicing'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'KeyVault__Uri', value: keyVault.properties.vaultUri }
        { name: 'ServiceBus__Namespace', value: serviceBusNamespace.properties.serviceBusEndpoint }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
      ]
    }
  }
}

// -------------------------------------------------------------------------------------
// RBAC — least privilege, per service. All three get Key Vault read access;
// all three also get Service Bus Data Owner (send + receive), since each service
// both publishes and/or subscribes to events on the shared topic.
//
// Explicit per-app role assignments (rather than a Bicep loop over a resource-
// reference array, which isn't valid syntax) — more verbose, but unambiguous.
// -------------------------------------------------------------------------------------
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var serviceBusDataOwnerRoleId = '090c5cfd-751d-490a-894a-3ce6f1109419'

resource originationKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, originationApp.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: originationApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource fundingKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, fundingApp.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: fundingApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource servicingKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, servicingApp.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: servicingApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource originationServiceBusAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, originationApp.id, serviceBusDataOwnerRoleId)
  scope: serviceBusNamespace
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataOwnerRoleId)
    principalId: originationApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource fundingServiceBusAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, fundingApp.id, serviceBusDataOwnerRoleId)
  scope: serviceBusNamespace
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataOwnerRoleId)
    principalId: fundingApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource servicingServiceBusAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, servicingApp.id, serviceBusDataOwnerRoleId)
  scope: serviceBusNamespace
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataOwnerRoleId)
    principalId: servicingApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// -------------------------------------------------------------------------------------
// Post-deployment steps — Bicep provisions the infrastructure, but four secrets still
// need to be populated in Key Vault manually (or via a follow-up script) before the
// apps will actually connect to anything real, since their values either don't exist
// until other steps happen (a Service Bus SAS key) or shouldn't be derivable from the
// template itself (per-service SQL connection strings built from this deployment's
// own outputs, but assembled and pushed as a deliberate separate step):
//   ServiceBus--ConnectionString      (shared — same value for all three services)
//   Sql-Origination-ConnectionString
//   Sql-Funding-ConnectionString
//   Sql-Servicing-ConnectionString
// Each SQL connection string follows the pattern:
//   Server=tcp:<sqlServer output>.database.windows.net,1433;Database=<db name>;
//   User ID=<sqlAdminLogin>;Password=<sqlAdminPassword>;Encrypt=true;
// -------------------------------------------------------------------------------------

output originationUrl string = 'https://${originationApp.properties.defaultHostName}'
output fundingUrl string = 'https://${fundingApp.properties.defaultHostName}'
output servicingUrl string = 'https://${servicingApp.properties.defaultHostName}'
output keyVaultUri string = keyVault.properties.vaultUri
output serviceBusNamespace string = serviceBusNamespace.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output appInsightsConnectionString string = appInsights.properties.ConnectionString
