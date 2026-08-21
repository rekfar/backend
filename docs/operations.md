# Operations

Creating the Azure resources the API runs on, wiring up deployment, and what to do when a
deploy or the running app misbehaves.

The database is not covered here — it belongs to the
[database repository](https://github.com/rekfar/database), and its own
[docs/operations.md](https://github.com/rekfar/database/blob/main/docs/operations.md) is the
runbook for creating it, backing it up and restoring it. This document assumes it exists.

## What runs where

| | |
| --- | --- |
| Resource group | `rekfar-api`, Sweden Central |
| Registry | `rekfarapi` (ACR Basic, admin user disabled) |
| Identity | `rekfar-api`, a user-assigned managed identity |
| Environment | `rekfar-api-env`, Container Apps consumption, **no log destination** |
| App | `rekfar-api`, external ingress on 8080, scale-to-zero, `maxReplicas: 1` |
| Database | `Rekfar` on `rekfar.database.windows.net` — resource group `rekfar`, owned elsewhere |

All of it is described by [`infra/main.bicep`](../infra/main.bicep), which is applied on
every push to `main`. Nothing should be changed in the portal: the next deploy reconciles it
away.

**On the separate resource group.** The database repo puts everything in one group, `rekfar`.
This is in its own, because the template is applied *from CI* and that means the deploy
principal holds `Contributor` at resource-group scope. That scope must not contain the
production SQL server. The database stays in `rekfar` and is reached across groups over
Entra, which needs no ARM rights at all.

## Creating it — one-time bootstrap

The order matters, and CI cannot do the first run: it pushes to a registry that this template
is what creates.

### 1. The resource group

```bash
az group create --name rekfar-api --location swedencentral
```

Sweden Central to sit with the database. Norway East would otherwise be the obvious choice,
but it refuses SQL provisioning on this subscription — see the database repo's operations
document for that trap and its consequences.

### 2. The deployment identity

An Entra **app registration** for deployment (`rekfar-backend-deploy`) and a service
principal for it:

```bash
APP_ID="$(az ad app create --display-name rekfar-backend-deploy --query appId -o tsv)"
az ad sp create --id "$APP_ID"
```

Then a **federated credential**: GitHub → organisation `rekfar`, repository `backend`, entity
type *Environment*, environment `production`. That is why the deploy job declares
`environment: production` — the credential will not issue a token without it.

**The subject is not the one the portal suggests.** This organisation has GitHub's OIDC
*immutable unique IDs* enabled, so the token GitHub presents embeds numeric organisation and
repository IDs rather than names. Register **both** forms, so turning the setting off later
does not break the deploy:

```bash
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "github-production-immutable",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:rekfar@317530418/backend@1335787442:environment:production",
  "audiences": ["api://AzureADTokenExchange"]
}'
```

```bash
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "github-production-named",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:rekfar/backend:environment:production",
  "audiences": ["api://AzureADTokenExchange"]
}'
```

`317530418` is the `rekfar` organisation and `1335787442` is this repository. A credential
registered with only the plain-name form fails with `AADSTS700213: No matching federated
identity record found`, naming the subject it actually received — read that error, it tells
you exactly what to register.

### 3. Contributor on the resource group

```bash
az role assignment create --assignee "$APP_ID" --role Contributor \
    --scope "/subscriptions/$(az account show --query id -o tsv)/resourceGroups/rekfar-api"
```

This is what `az deployment group create` needs, and it is all the deploy principal ever
gets at resource-group scope. The registry-scoped grant comes after step 4, because the
registry does not exist yet.

### 4. The first deployment, by hand

```bash
az deployment group create \
    --resource-group rekfar-api \
    --template-file infra/main.bicep \
    --parameters imageTag=bootstrap
```

`imageTag=bootstrap` is a documented escape hatch in the template: it substitutes Microsoft's
quickstart image, because on this one run the registry it would otherwise pull from has only
just been created and is empty. Every later deployment passes a commit SHA.

The registry name must be globally unique. If `rekfarapi` is taken, pass a different
`registryName` here **and** change the default in `infra/main.bicep` — do not leave CI
deploying a different default from the one that exists.

Note the outputs: `identityName`, `identityClientId` and `apiFqdn`.

### 5. The two registry grants

Both need the registry to exist, which is why they come after the first deployment rather
than with the Contributor grant in step 3.

**`AcrPull` for the container app's identity.** The template deliberately does not declare
this: creating a role assignment needs `Microsoft.Authorization/roleAssignments/write`, and
the `Contributor` role explicitly excludes it, so a template containing one could not be
deployed by CI without also granting it User Access Administrator over the group. Granting it
once by hand is the smaller privilege, and it never changes afterwards.

```bash
REGISTRY_ID="$(az acr show --name rekfarapi --query id -o tsv)"
IDENTITY_PRINCIPAL_ID="$(az identity show --name rekfar-api --resource-group rekfar-api --query principalId -o tsv)"
az role assignment create --assignee-object-id "$IDENTITY_PRINCIPAL_ID" \
    --assignee-principal-type ServicePrincipal --role AcrPull --scope "$REGISTRY_ID"
```

**`AcrPush` for the deploy principal.** Separate from `Contributor` and not implied by it —
pushing an image is a data-plane action, and the registry's admin account is disabled, so
Contributor alone cannot push.

```bash
az role assignment create --assignee "$APP_ID" --role AcrPush --scope "$REGISTRY_ID"
```

Until the `AcrPull` grant exists, the container app cannot pull the real image and stays on
the placeholder. It is the first thing to check when a revision fails to start with an image
pull error.

### 6. The database user

Connected as the Entra admin — `sqlcmd` can reuse an `az login` session with
`--authentication-method ActiveDirectoryDefault`, which avoids handling a password:

```sql
CREATE USER [rekfar-api] FROM EXTERNAL PROVIDER;
GRANT SELECT ON SCHEMA::[ref] TO [rekfar-api];
```

The user name is the managed identity's name — `CREATE USER ... FROM EXTERNAL PROVIDER`
resolves it by display name, which is why the identity is user-assigned and named
deliberately.

**`SELECT` on `ref`, and nothing else.** The Catalogue module reads reference data and writes
none of it. It has no rights on `app`, `auth` or `ingest` at all, following the separate
least-privileged identity the ingestion job uses
([ADR-0015](https://github.com/rekfar/docs/blob/main/adr/0015-ingestion-lives-in-the-database-repository.md)) —
an API serving an anonymous map query has no business being able to read a diary note. These
grants widen when the Auth and Trip modules arrive, and each widening is a decision worth
making explicitly.

The server's existing `AllowAllWindowsAzureIps` firewall rule already covers the container
app's egress. Confirm it rather than assume it; nothing new should be needed.

### 7. Repository secrets and variables

Secrets — *Settings → Secrets and variables → Actions*:

| Secret | Value |
| --- | --- |
| `AZURE_CLIENT_ID` | The `rekfar-backend-deploy` app registration's client ID |
| `AZURE_TENANT_ID` | Same tenant as the database repository |
| `AZURE_SUBSCRIPTION_ID` | Same subscription as the database repository |

Variables:

| Variable | Value |
| --- | --- |
| `AZURE_RESOURCE_GROUP` | `rekfar-api` |
| `AZURE_CONTAINER_REGISTRY` | `rekfarapi` |

Tenant and subscription are the same values the database repository uses, but secrets are
per-repository and have to be set again here.

## Deploying

`.github/workflows/ci.yml` deploys on every push to `main` whose `build` **and**
`integration` jobs pass. The job logs in with the federated credential, builds the image,
pushes it tagged with the commit SHA, applies the Bicep template with that tag, and then
polls `/health` until it answers.

Deployment is by immutable SHA tag, never `latest`. `latest` is pushed as well, for a human
running `docker pull`, but nothing deploys from it: a moving tag makes a revision
irreproducible and is not reliably seen as a new image.

The deploy job has its own concurrency group that **queues** rather than cancels. The
workflow-level group cancels in-progress runs, which is right for tests and would otherwise
abort a deploy halfway through an ARM operation.

### Rolling back

Deploy an older commit's tag:

```bash
az deployment group create --resource-group rekfar-api \
    --template-file infra/main.bicep --parameters imageTag=<older-sha>
```

Or shift traffic to the previous revision, which is faster and needs no build:

```bash
az containerapp revision list -n rekfar-api -g rekfar-api -o table
az containerapp ingress traffic set -n rekfar-api -g rekfar-api --revision-weight <older-revision>=100
```

Revert the commit afterwards either way — the next push to `main` redeploys whatever `main`
says.

## Logs

The environment has **no log destination**, so there is no queryable history. Live stream
only:

```bash
az containerapp logs show --name rekfar-api --resource-group rekfar-api --follow
```

```bash
az containerapp logs show --name rekfar-api --resource-group rekfar-api --type system
```

This is a deliberate trade for keeping metered ingestion off the bill, and the cost is real:
an intermittent fault has to be reproduced while somebody is watching. If that becomes the
thing standing between a bug and its fix, attach a Log Analytics workspace — it is one
property on the managed environment — and revisit T10 in
[ADR-0010](https://github.com/rekfar/docs/blob/main/adr/0010-tech-stack-dotnet-azure-sql.md),
which still has no error tracker chosen.

## Cold starts

Two of them stack on the first request after an idle period:

1. **The container**, because `minReplicas` is 0 (scale-to-zero, per ADR-0010).
2. **The database**, because the free offer is serverless and auto-pauses; a resume can take
   tens of seconds.

The application is built for the second: `CatalogueModule` sets `EnableRetryOnFailure` and a
60-second command timeout, and `/health` deliberately never touches the database, so probes
do not flap while it resumes.

**Measure both and record the numbers.** ADR-0010 carries an open follow-up to test the
resume time with a realistic spatial query before the Phase 1 exit review, and it is also the
input [rekfar/webapp#11](https://github.com/rekfar/webapp/issues/11) needs — a Netlify proxy
in front of the API imposes its own upstream timeout, and a resume that outlasts it turns a
slow first request into a failed one.

Mitigations, in order of preference: raise the database's auto-pause delay; raise
`minReplicas` to 1 (which costs, and forfeits scale-to-zero); add a warm-up ping.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| `AADSTS700213` in `azure/login` | The federated credential's subject. Read the error — it names the subject GitHub sent. See step 2. |
| `az acr login` succeeds, `docker push` gets 403 | `AcrPush` is missing. `Contributor` does not imply it. |
| Revision unhealthy, container exits immediately | Almost always configuration. The app fails fast by design: no `Cors:AllowedOrigins` and no connection string are both startup exceptions naming what is missing. Read the system logs. |
| `Login failed for user '<token-identified principal>'` | The database user in step 6 was never created, or was created under a different name than the identity's. |
| Revision stuck on the placeholder image, or an image pull failure | The identity's `AcrPull` grant from step 5 is missing. |
| Every caller shares one rate-limit partition | `Hosting__TrustForwardedHeaders` is not `true`. Behind the ingress the app otherwise sees the proxy's address for everyone. |
| CORS errors in the browser, `curl` fine | `corsAllowedOrigin` does not match the site's origin exactly — scheme and host, no trailing slash. |
| A deploy changed nothing | Deploying the same `imageTag` twice is a no-op by design. Check the SHA in the job log. |

### Checking for drift

The template is the source of truth, so ask what a deploy *would* change before it does:

```bash
az deployment group what-if --resource-group rekfar-api \
    --template-file infra/main.bicep --parameters imageTag=<current-sha>
```

Against a group in the state `main` deployed, this reports no changes. Anything else is a
manual change somebody made in the portal, and belongs back in the template.
