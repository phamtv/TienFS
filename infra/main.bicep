// =====================================================================================
// LOAN PLATFORM — PRODUCTION INFRASTRUCTURE
// -------------------------------------------------------------------------------------
// Provisions: shared App Service Plan hosting three App Services (one per
// microservice), a Key Vault (secrets, RBAC-only access, no public network access),
// and a Service Bus namespace with the "loan-events" topic and two subscriptions
// matching what each service expects (see /emulator/config.json for the local
// equivalent used in development).
//
// Each App Service gets a System-Assigned Managed Identity, granted:
//   - "Key Vault Secrets User" (read-only) on the Key Vault
//   - "Azure Service Bus Data Owner" on the Service Bus namespace (send + receive)
// No connection strings or keys are stored anywhere in this template or in app
// settings — everything resolves at runtime via Managed Identity.
// =====================================================================================

@description('Deployment region')
param location string = resourceGroup().location

@description('Base name used to derive resource names')
param baseName string = 'tienfs'

// -------------------------------------------------------------------------------------
// App Service Plan — shared across all three microservices for this sample.
// A larger production deployment might split these onto separate plans for
// independent scaling; sharing one plan here keeps the sample's cost/footprint small.
// -------------------------------------------------------------------------------------
resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${baseName}-plan'
  location: location
  sku: { name: 'P1v3', tier: 'PremiumV3' }
}

// -------------------------------------------------------------------------------------
// Key Vault — same security posture as the earlier security-sample project:
// RBAC authorization, no public network access, soft-delete + purge protection.
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
    publicNetworkAccess: 'Disabled'
    networkAcls: { defaultAction: 'Deny', bypass: 'AzureServices' }
  }
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
    correlationFilter: { subject: 'LoanApproved' }
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
    correlationFilter: { subject: 'LoanFunded' }
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

output originationUrl string = 'https://${originationApp.properties.defaultHostName}'
output fundingUrl string = 'https://${fundingApp.properties.defaultHostName}'
output servicingUrl string = 'https://${servicingApp.properties.defaultHostName}'
output keyVaultUri string = keyVault.properties.vaultUri
output serviceBusNamespace string = serviceBusNamespace.name
