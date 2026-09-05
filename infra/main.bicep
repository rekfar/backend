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

@description('The web client origin — scheme and host only, no trailing slash, matched exactly against the browser Origin header. The API refuses to start without one (Program.cs), so a wrong value here is a startup failure rather than a silent misconfiguration.')
param corsAllowedOrigin string = 'https://rekfar.netlify.app'

@description('Azure Communication Services resource — the sender of every sign-in code (ADR-0018).')
param communicationServiceName string = 'rekfar-comms'

@description('The Email Communication Service that owns the sending domain.')
param emailServiceName string = 'rekfar-email'

@description('Where Communication Services keeps data at rest. Norway where the offering allows it, Europe otherwise (ADR-0018, NFR-PRIV-5). Chosen at creation and NOT changeable afterwards — the same trap as the database collation.')
@allowed([
  'Norway'
  'Europe'
])
param emailDataLocation string = 'Norway'

@description('The custom domain sign-in codes are sent from. Customer-managed, so it stays Pending until its SPF, DKIM and verification DNS records exist — see docs/operations.md. Azure managed domains send from a generated azurecomm.net subdomain, which is not acceptable for mail carrying a login code.')
param senderDomain string = 'rekfar.no'

@description('The From address, which must be on senderDomain. No-reply because there is nothing to reply to: the mail carries a code and asks for nothing back.')
param senderAddress string = 'ikke-svar@rekfar.no'

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

// Email (ADR-0018). Three resources: the Email Communication Service, the domain it sends
// from, and the Communication Services resource the SDK actually talks to. All three are
// `global` — the geography that matters is dataLocation, not a region.
//
// The domain is CustomerManaged, which is the whole point: a login code from
// something.azurecomm.net reads as phishing, and this is the one message Rekfar sends where
// being trusted is the point. Creating it here does not verify it — that needs DNS records
// this template cannot write, and the domain sits Pending until they exist.
resource emailService 'Microsoft.Communication/emailServices@2023-04-01' = {
  name: emailServiceName
  location: 'global'
  properties: {
    dataLocation: emailDataLocation
  }
}

resource emailDomain 'Microsoft.Communication/emailServices/domains@2023-04-01' = {
  parent: emailService
  name: senderDomain
  location: 'global'
  properties: {
    domainManagement: 'CustomerManaged'
    // Open and click tracking rewrites links and adds a pixel. In a message whose entire
    // content is a login code, that is telemetry about nothing and a reason to distrust it.
    userEngagementTracking: 'Disabled'
  }
}

resource communicationService 'Microsoft.Communication/communicationServices@2023-04-01' = {
  name: communicationServiceName
  location: 'global'
  properties: {
    dataLocation: emailDataLocation
    linkedDomains: [
      emailDomain.id
    ]
  }
}

// The identity's AcrPull assignment is deliberately NOT declared here. Creating a role
// assignment needs Microsoft.Authorization/roleAssignments/write, which the Contributor role
// explicitly excludes — so a template containing one cannot be deployed by CI without also
// granting it User Access Administrator over this group. The grant is made once by hand
// during the bootstrap instead (docs/operations.md); it never changes afterwards, and this
// template stays deployable with Contributor alone.
//
// The identity's send role on the Communication Services resource above is left out for the
// same reason and granted the same way. Declaring it here would make every CI deploy fail
// with AuthorizationFailed until the deploy principal were also made User Access
// Administrator over this group — a far larger grant than the one being avoided.

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
            {
              // Required in Production: the API refuses to start without a sender, because
              // the alternative sender writes login codes to the log.
              name: 'Auth__Email__Endpoint'
              value: 'https://${communicationService.properties.hostName}'
            }
            {
              name: 'Auth__Email__SenderAddress'
              value: senderAddress
            }
            {
              // Named rather than discovered, exactly as the connection string names it: no
              // credential-chain probing at startup, and no key anywhere in the deployment.
              name: 'Auth__Email__ManagedIdentityClientId'
              value: identity.properties.clientId
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
}

output apiFqdn string = containerApp.properties.configuration.ingress.fqdn
output registryLoginServer string = registry.properties.loginServer

@description('Grant the container app identity a sending role on this resource: az role assignment create --assignee-object-id <identityPrincipalId> --assignee-principal-type ServicePrincipal --role Contributor --scope <this>.')
output communicationServiceId string = communicationService.id

@description('Read the domain\'s SPF, DKIM and verification records from here and put them in DNS: az communication email domain show --name <senderDomain> --email-service-name <emailServiceName> --resource-group <group>.')
output senderDomainId string = emailDomain.id

@description('Create the database user with this name: CREATE USER [<value>] FROM EXTERNAL PROVIDER.')
output identityName string = identity.name
output identityClientId string = identity.properties.clientId

@description('The object id the two by-hand role assignments are made against — AcrPull on the registry, and sending on the Communication Services resource.')
output identityPrincipalId string = identity.properties.principalId
