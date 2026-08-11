# TienFS — Microservices Reference Architecture

A three-service, event-driven reference implementation of a loan origination -
funding - servicing pipeline, built to demonstrate microservices communicating
through Azure Service Bus rather than direct synchronous calls.

## Architecture

```
   POST /api/applications/{id}/approve
              |
              v
   +----------------------+        LoanApprovedEvent        +----------------------+
   |  LoanOrigination.Api | ------(topic: loan-events)-----> |   LoanFunding.Api    |
   |  (own database)      |       subject="LoanApproved"     |   (own database)     |
   +----------------------+                                  +-----------+----------+
                                                                          |
                                                            LoanFundedEvent
                                                      (topic: loan-events)
                                                       subject="LoanFunded"
                                                                          v
                                                              +----------------------+
                                                              |  LoanServicing.Api   |
                                                              |  (own database)      |
                                                              +----------------------+
```

Each service:
- **Owns its own database** — no service ever queries another service's data directly.
- **Communicates only through events** on the shared `loan-events` Service Bus topic — Origination has no idea Funding exists; it just publishes an event.
- **Pulls secrets from Azure Key Vault** at runtime via Managed Identity — no secrets in config or source control.
- **Runs on its own Azure App Service** in production, each with its own identity and least-privilege RBAC grants.

## Why this design

- **Independent scaling and deployment** — Funding can be redeployed or scaled without touching Origination or Servicing.
- **Loose coupling** — if Servicing were down entirely, Origination and Funding keep working; Servicing just catches up on queued messages once it's back.
- **Idempotent event handling** — both subscribers check whether they've already processed a given `LoanApplicationId` before acting, since Service Bus guarantees *at-least-once* delivery, not exactly-once. Duplicate delivery is treated as a normal case, not an edge case.
- **Dead-lettering, not silent failure** — if event processing throws, the message is deliberately left uncompleted so Service Bus redelivers it, eventually dead-lettering it after `MaxDeliveryCount` (5) failed attempts — the failure is preserved and inspectable, never silently dropped.

## Running it locally

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) and [Docker](https://www.docker.com/).

### 1. Start the local Service Bus emulator
```bash
cd emulator
docker compose up -d
```
This starts Microsoft's official Azure Service Bus emulator, pre-configured (via `config.json`) with the `loan-events` topic and both subscriptions — the exact same topology as production, just running on your machine.

### 2. Run all three services
Each service needs its own terminal (they're independent processes, exactly as they would be in production):
```bash
cd src/LoanOrigination.Api && dotnet run
cd src/LoanFunding.Api && dotnet run
cd src/LoanServicing.Api && dotnet run
```
Each will print its own Swagger URL on startup (different ports).

### 3. Walk the full flow through Swagger
1. **Origination Swagger** -> `POST /api/applications` -> create an application.
2. **Origination Swagger** -> `POST /api/applications/{id}/approve` -> approves it and publishes `LoanApprovedEvent`.
3. **Funding Swagger** -> `GET /api/funding` -> within a second or two, a new funding record appears — created entirely by the background subscriber, with no direct call from Origination.
4. **Servicing Swagger** -> `GET /api/accounts` -> a servicing account appears, opened in reaction to `LoanFundedEvent` from Funding.

Watch each service's console output — you'll see the subscriber logs firing as messages arrive, which is the clearest way to *see* the event-driven flow happening in real time.

### Running without the emulator
Any service can still be run and tested on its own without the emulator — if no `ServiceBus:ConnectionString` is configured, each service falls back to a `NullEventBus` that logs what it *would* have published instead of failing. Useful for quickly testing the Origination API in isolation, but the cross-service flow obviously requires the emulator (or real Service Bus) running.

## Deploying to Azure

```bash
az deployment group create \
  --resource-group <your-resource-group> \
  --template-file infra/main.bicep
```
This provisions the App Service Plan, all three App Services (each with a Managed Identity), Key Vault, and the Service Bus namespace/topic/subscriptions — matching the local emulator topology exactly. After deployment, publish each service's code to its corresponding App Service via your CI/CD pipeline of choice.

## Design notes / things a real system would add

- **Interest rate isn't carried on `LoanFundedEvent`** — Servicing currently opens accounts with `InterestRate = 0` as a placeholder. A real system would either include it in the event contract or have Servicing query a shared reference source — worth discussing as a live design trade-off (event payload size vs. completeness) rather than treating it as an oversight.
- **No compensating transactions / sagas** — if Funding successfully disburses funds but then fails to publish `LoanFundedEvent` (a rare but real failure mode), this sample doesn't include a saga/outbox pattern to guarantee consistency. A production system handling real money would need one.
- **No API authentication** — omitted here to keep the sample's core pattern (events between microservices) easy to read; a real deployment would add OAuth2/JWT the same way as the earlier security-sample project.

## A note on how this was built

This code was written by an AI assistant and has **not been compiled or run**
(no .NET SDK was available in the environment it was written in). The
architecture and patterns are sound, but treat it as a strong first draft:
run `dotnet build` on the full solution, work through any compile errors that
surface, and test the emulator flow end-to-end yourself before relying on it
for anything beyond a working reference — the same approach that worked well
for the earlier security-sample project.
