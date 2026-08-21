// The API's Azure footprint: a registry, an identity, a Container Apps environment and the
// app itself (ADR-0010 — Azure Container Apps, consumption, scale-to-zero).
//
// Deployed into its own resource group, separate from the `rekfar` group that holds the
// logical SQL server. That is deliberate: this template is applied from CI, so the deploy
// principal needs Contributor at resource-group scope, and that scope must not include the
// production database. See docs/operations.md.
//
// `imageTag` is the only required parameter. Everything else is defaulted here rather than
// in a .bicepparam file, so the deployed configuration is one reviewable file and CI needs
// no parameter plumbing beyond the tag it just built.

targetScope = 'resourceGroup'

@description('Immutable image tag to deploy — the commit SHA. Pass the literal "bootstrap" for the very first deployment, before any image exists in the registry.')
param imageTag string

@description('Azure region. Sweden Central to sit with the database: Norway East refuses SQL provisioning on this subscription.')
param location string = 'swedencentral'

@description('Container registry name. Globally unique, lowercase alphanumeric only.')
param registryName string = 'rekfarapi'

@description('User-assigned managed identity. Its name is also the database user name, because CREATE USER ... FROM EXTERNAL PROVIDER resolves the identity by display name.')
param identityName string = 'rekfar-api'

param environmentName string = 'rekfar-api-env'
param containerAppName string = 'rekfar-api'
param imageName string = 'rekfar-api'

@description('The logical server holding the Rekfar database. In the `rekfar` resource group, not this one.')
param sqlServerName string = 'rekfar'
param sqlDatabaseName string = 'Rekfar'

@description('The web client origin. The API refuses to start without one (Program.cs), so a wrong value here is a startup failure, not a silent misconfiguration.')
param corsAllowedOrigin string = 'https://REPLACE-ME.netlify.app'

// The built-in AcrPull role. The identity needs it to pull; nothing here uses registry
// admin credentials, which is why adminUserEnabled stays false.
var acrPullRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d'
)

// The first deployment has to create the registry that later deployments push to, so there
// is nothing to pull on that run. Microsoft's quickstart image stands in for one run only.
var bootstrapImage = 'mcr.microsoft.com/k8se/quickstart:latest'
var image = imageTag == 'bootstrap'
  ? bootstrapImage
  : '${registry.properties.loginServer}/${imageName}:${imageTag}'

// Entra-only server, so there is no password here and no Container Apps secret anywhere in
// this template. `Active Directory Managed Identity` with an explicit User Id rather than
// `Active Directory Default`: it names which identity to use and skips the credential-chain
// probing DefaultAzureCredential would do at startup.
var sqlConnectionString = join([
  // environment().suffixes.sqlServerHostname rather than a literal '.database.windows.net',
  // which is what the linter asks for and is correct anyway.
  'Server=tcp:${sqlServerName}${environment().suffixes.sqlServerHostname},1433'
  'Database=${sqlDatabaseName}'
  'Authentication=Active Directory Managed Identity'
  'User Id=${identity.properties.clientId}'
  'Encrypt=True'
  ''
], ';')

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    // The identity pulls with AcrPull and CI pushes with AcrPush, so the shared admin
    // account has no purpose and would only be a long-lived credential to leak.
    adminUserEnabled: false
  }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
}

// User-assigned rather than system-assigned: the identity must exist before the app so it
// can hold AcrPull at first pull, and before the database user can be created for it.
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, identity.id, acrPullRoleDefinitionId)
  properties: {
    roleDefinitionId: acrPullRoleDefinitionId
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// No appLogsConfiguration, which means a log destination of `none`: console logs are
// available as a live stream (`az containerapp logs show`) and are not retained anywhere.
// Chosen to keep metered ingestion out of the bill; the cost is that a problem has to be
// reproduced to be read.
resource managedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  properties: {}
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: managedEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        // Matches EXPOSE in the Dockerfile and the ASP.NET Core container default.
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: identity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: image
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              // Required. Program.cs keeps forwarded headers off by default and clears the
              // known-proxy lists when on, precisely for this ingress. Left false, every
              // caller lands in one rate-limit partition.
              name: 'Hosting__TrustForwardedHeaders'
              value: 'true'
            }
            {
              // Index binding. appsettings.json notes that configuration merges arrays by
              // index, so this overwrites element 0 rather than appending to it.
              name: 'Cors__AllowedOrigins__0'
              value: corsAllowedOrigin
            }
            {
              name: 'ConnectionStrings__Rekfar'
              value: sqlConnectionString
            }
          ]
          probes: [
            {
              // /health is liveness only and deliberately does not touch the database, so
              // these never flap while the serverless database is resuming from auto-pause.
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 3
              periodSeconds: 10
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        // Scale-to-zero, per ADR-0010. The cost is a container cold start on the first
        // request after an idle period, on top of any database resume.
        minReplicas: 0
        // One, on purpose. The rate limiter in Program.cs is an in-process fixed window, so
        // N replicas serve N x PermitLimit. Raising this means either accepting that
        // multiplication or moving to a distributed limiter — decide it, do not drift into it.
        maxReplicas: 1
        rules: [
          {
            name: 'http'
            http: {
              metadata: {
                concurrentRequests: '20'
              }
            }
          }
        ]
      }
    }
  }
  dependsOn: [
    acrPull
  ]
}

output apiFqdn string = containerApp.properties.configuration.ingress.fqdn
output registryLoginServer string = registry.properties.loginServer

@description('Create the database user with this name: CREATE USER [<value>] FROM EXTERNAL PROVIDER.')
output identityName string = identity.name
output identityClientId string = identity.properties.clientId
