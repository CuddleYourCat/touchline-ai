# DECISIONS.md — Architecture Decision Records

`touchline-ai` · .NET 10 + Azure AI · Inside the Game hackathon entry

Each entry records what was decided, what the alternatives were, why this option was chosen, and the trade-offs. Numbers are stable. A superseded decision gets a new entry rather than an edit.

**Statuses:** *Accepted* means decided. *Proposed* means options are laid out and waiting for a decision, and no code that depends on it gets written until it's accepted.

---

## ADR-001 — A seeded match simulator is the live data source

**Status:** Accepted · Kick-off

**Decision**
A .NET worker service generates a synthetic match from a seed and publishes its events in match-clock order at an adjustable replay speed.

**Alternatives considered**
- **Static JSON match files.** Simple, but nothing is "live", and the brief asks for events analysed as they happen.
- **Open real-world event data** (for example StatsBomb open data). Realistic, but the rules require synthetic data, and real data carries licence terms and real player names.
- **Have a model generate events.** Hard to make statistically believable, costly per match, and not repeatable.

**Rationale**
The hackathon provides no dataset and no feed. A seeded simulator gives a live stream for the demo, exact repeatability for tests and the video, and full control over realism. Its outputs are calibrated against public league averages: about 2.8 goals and 25 shots per match, with xG driven by shot location.

**Trade-offs**
Realism depends on our model of football. A simulator that's too random produces nonsense patterns, and one that's too scripted makes the agents' insights look canned. Calibration is real work.

---

## ADR-002 — Fictional clubs defined as team-profile data, with a roster guardrail

**Status:** Accepted · Kick-off

**Decision**
Clubs are JSON team profiles holding the name, colours, squad and tactical tendencies. The default profile is a fictional, Arsenal-flavoured club. Every generated line is checked against the active profiles' rosters.

**Alternatives considered**
- **Real club and player names.** The most personal option, but trademark and image-rights exposure, and the video rules forbid third-party trademarks.
- **Hard-coded fictional teams.** Safe, but it blocks the "choose your team" stretch goal.

**Rationale**
It meets the brief's personalisation goal without IP risk. It makes the stretch goal a matter of adding data rather than code. The guardrail controls a real hallucination risk: language models know real squads and will name real players unprompted.

**Trade-offs**
Fans connect less with invented players. The guardrail adds a post-processing step and needs a rewrite-or-reject policy for any line that fails it.

---

## ADR-003 — Cloud models through Microsoft Foundry, not local models through Ollama

**Status:** Accepted · Kick-off

**Decision**
All chat and embedding calls go to models deployed in Microsoft Foundry (Azure OpenAI). Application code depends on `IChatClient` and `IEmbeddingGenerator` from `Microsoft.Extensions.AI`, not on a provider SDK.

**Alternatives considered**
- **Ollama in Docker**, as in Project Hearth, an earlier .NET and Aspire prototype that ran Mistral locally.
- **A mix:** Ollama for development, Foundry for the demo.

**Rationale**
Project Hearth's decision log measured about 3 tokens per second on this machine's CPU, so a single extraction took 30–40 minutes. Live commentary needs a line within seconds. Foundry is also a judged "hero technology" with its own category prize. Coding against `Microsoft.Extensions.AI` keeps an Ollama provider possible as a configuration change.

**Trade-offs**
Every model call costs money, so the key-moment filter (ADR-004) and per-call token limits are mandatory, not optional. Development needs an Azure connection.

---

## ADR-004 — Event Hubs for the event stream, Service Bus for key moments

**Status:** Accepted · Kick-off

**Decision**
Every match event goes through Azure Event Hubs, partitioned by match ID. An Azure Function consumes the stream, stores each event, and publishes only **key moments** to a Service Bus topic, with subscriptions for live commentary and for highlights.

**Alternatives considered**
- **Service Bus for everything.** Simpler, but Service Bus is built for business messages, not a high-volume telemetry stream.
- **Event Hubs for everything,** with the agents reading the stream directly. Every agent would see every event, which makes cost control harder.
- **Event Grid** for key moments. A good fit for routing events, but there's no dead-letter queue to replay a failed commentary job from.

**Rationale**
Each service does what it's built for, and the split is the cost guard. About 1,500 events per match go into the stream, and perhaps 30 key moments reach a model. Choosing between Event Hubs, Service Bus and Event Grid is core AI-200 Domain 3 material.

**Trade-offs**
Two messaging services to provision, secure and monitor instead of one.

---

## ADR-005 — Aspire for local orchestration, Container Apps for hosting from day one

**Status:** Accepted · Kick-off

**Decision**
An Aspire AppHost runs the whole system locally, using emulators wherever one exists. The simulator, API and agent host are containers in Azure Container Registry, deployed to Azure Container Apps. A skeleton deployment goes up during Kick-off.

**Alternatives considered**
- **App Service.** Simpler hosting, but Container Apps fits event-driven scaling and is the bigger Domain 1 topic.
- **AKS.** Too heavy for an 11-day build.
- **Deploy at the end.** It saves early effort but risks a broken deployment on deadline day.

**Rationale**
The judges must be able to open the app, so deployment can't be left to the last day. Aspire is familiar from Project Hearth and wires dependencies and service discovery for free.

**Trade-offs**
Container Apps keeps running through the judging period, so we need a budget cap and scale-to-zero where possible.

---

## ADR-009 — Trunk-based development with squash-merged slice PRs and a frozen submission revision

**Status:** Accepted · Kick-off

*Numbered 009 because it was decided after ADR-006 to ADR-008 were proposed. It sits here with the other accepted decisions.*

**Decision**
`main` is always deployable and deploys on every merge. Work happens on short-lived branches, one per slice, merged by pull request with a squash merge once CI and a code review pass. At submission, the `v1.0-submission` tag is deployed to a Container Apps revision labelled `judging`, and that revision is what the judges use.

**Alternatives considered**
- **GitFlow with `develop` and `main`.** A familiar structure, but a second long-lived branch adds merge overhead with no payoff for a single developer on an 11-day build.
- **Committing straight to `main`.** The fastest option, but nothing stands between a broken build and the live deployment.
- **Merge commits instead of squash.** Keeps every intermediate commit, but `main` becomes harder to read as a story of the build.

**Rationale**
Small slices and same-day merges keep `main` close to the live app. Each PR records why the slice exists. Pinning the judged version to a revision label means work after submission can't break what the judges are looking at.

**Trade-offs**
Squashing loses the intermediate commit history of each branch. A revision label pins the code, but the Azure resources behind it stay shared, so a schema change after submission could still affect the judged revision.

---

## ADR-010 — Projects are added when a stage needs them, around a pure domain

**Status:** Accepted · Kick-off

**Decision**
The solution starts with only the projects Kick-off needs: `Touchline.AppHost`, `Touchline.ServiceDefaults`, `Touchline.Domain`, `Touchline.Simulator`, `Touchline.MatchCentre` and `Touchline.Tests.Unit`. Later projects arrive with the stage that first uses them:
- `Touchline.EventProcessor` (Azure Functions) in Stage 1.
- `Touchline.Agents` and `Touchline.Tests.Integration` in Stage 3.
- `Touchline.Infrastructure`, but only once two services need the same Azure adapter.

`Touchline.Domain` stays pure C#, and a unit test fails the build if it ever references an Azure or AI package.

**Alternatives considered**
- **Every planned project up front.** The whole shape is visible on day one, but several projects would sit empty for a week, and the Functions project can't run locally until Azure Functions Core Tools is installed.
- **Layered like ai-doc-intelligence** (Domain, Application, Infrastructure shared by every deployable). Familiar, but there are more layers to cross per change, and the deployables would share more code than an event-driven system needs.

**Rationale**
Each deployable owns its Azure plumbing until sharing it is clearly worth it. The pure domain holds the football logic (events, team profiles, match state, key-moment rules), which is where the skill shots happen, so it must test without any cloud dependency.

**Trade-offs**
The solution's shape changes between stages, and the first shared adapter means a refactor to move code into `Touchline.Infrastructure`.

---

## ADR-011 — Azure environment: own resource group in Sweden Central, £30 monthly budget

**Status:** Accepted · Kick-off

**Decision**
Every touchline-ai resource lives in a new resource group, `rg-touchline-ai`, in **Sweden Central**. It's on the existing pay-as-you-go subscription, alongside other projects but separate from them. A budget alert at **£30 a month** is scoped to the resource group, and the app must stay within it through judging.

**Alternatives considered**
- **A new subscription.** A clean billing boundary and its own free-tier allowances, but more setup, and the subscription-wide free tiers aren't worth it for an 11-day build.
- **Sharing the other project's resource group.** Fewer resources, but the two projects' lifecycles and costs would be entangled, and a teardown of one would risk the other.
- **East US 2.** Also gets new Foundry models early, but it's further from the UK, so the live demo would have more latency.
- **A £50 or £100 budget.** Would fit Azure AI Search Basic, at the cost of more exposure if something runs away.

**Rationale**
A resource group is the unit of lifecycle and cost reporting: one group means one teardown and one cost view. Sweden Central gets new models early, which keeps ADR-007's options open, and it's close to the UK. £30 is enough for the consumption-priced services (Container Apps scaled to zero, Event Hubs and Service Bus Basic, serverless Cosmos DB, Functions Flex Consumption) plus capped model calls.

**Trade-offs**
- **Subscription-wide limits are shared with the other project.** Azure AI Search allows one free service per subscription, and Cosmos DB one free-tier account. If those are already taken, this project uses serverless Cosmos DB, and **Azure AI Search Basic doesn't fit in £30**. That pushes ADR-008's retrieval design towards a cheaper option, or towards running Search only for the final days. This gets decided before Stage 3.
- **A budget alert warns but doesn't stop spending.** Hourly-billed resources still need teardown dates in the resource ledger.

---

## ADR-012 — Hand-written Bicep defines the infrastructure

**Status:** Accepted · Kick-off

**Decision**
Azure resources are described in hand-written Bicep under `infra/`, following the approach of the ai-doc-intelligence project, and deployed with `az deployment group create` against `rg-touchline-ai`. Locally, the Aspire AppHost keeps running the services and emulators. It doesn't provision Azure.

**Alternatives considered**
- **Aspire models it and azd deploys it.** The AppHost declares Azure resources, and `azd up` generates and deploys the Bicep. It's the fastest route with the least code, but the Bicep stays hidden unless you ask for it.
- **Aspire first, then eject with `azd infra gen`.** Speed now, with generated Bicep committed and edited later. That Bicep is machine-shaped, and two sources of truth exist until the switch is made.

**Rationale**
Every resource, SKU, role assignment and network setting is written down and reviewable, which matters on a fixed £30 budget (ADR-011). It also gives the most practice for AI-200 Domain 1 (Container Apps, revisions, ingress, ACR) and Domain 4 (managed identity, RBAC, Key Vault). Following the ai-doc-intelligence layout means one way of working across both portfolio projects.

**Trade-offs**
- **The slowest of the three options.** Every resource is written by hand, so the timeline has less slack.
- **Two descriptions of the system.** The AppHost (local) and the Bicep (Azure) have to be kept in step by hand. A setting changed in one and missed in the other only shows up in Azure.

---

## ADR-013 — CI deploys the app only, as a federated managed identity

**Status:** Accepted · Kick-off

**Decision**
Every push to `main` that passes CI builds the Match Centre image with **ACR Tasks** (`az acr build`) and moves `ca-matchcentre` to it as a new revision. Infrastructure changes still go through `infra/deploy.ps1` from a developer's terminal.

GitHub Actions signs in as **`id-touchline-ci`**, a user-assigned managed identity defined in `main.bicep`. Its federated credential trusts only GitHub's OIDC tokens for this repository's `main` branch, so no Azure secret is stored in GitHub. The identity has exactly three roles:
- **Container Registry Tasks Contributor** on the registry.
- **Container Apps Contributor** on `ca-matchcentre`.
- **Reader** on the Container Apps environment only, because `az containerapp update` reads the environment and the Contributor role doesn't reach it.

Each deploy smoke-tests the **new revision's own FQDN**, then deactivates superseded revisions, keeping any revision named in the traffic rules (such as the `judging` label).

**Alternatives considered**
- **CI also deploys the infrastructure** by running `deploy.ps1`. Bicep changes would be fully automated, but CI would need Contributor plus role-assignment rights on the resource group, which is close to owning it.
- **An Entra ID app registration with a federated credential.** The shape most tutorials show, but it lives outside the resource group and the Bicep, needs directory permissions to create, and has to be cleaned up by hand.
- **A client secret stored in GitHub.** Simple, but it's a long-lived credential that can leak and must be rotated.
- **Building with `docker build` on the runner and pushing.** Familiar, but CI would need push rights and a Docker login. ACR Tasks builds inside Azure, close to the registry, and is an AI-200 Domain 1 topic.

**Rationale**
A leaked or misused workflow can only build images and update one container app. It can't create resources, read other resources or grant permissions. The federated credential is scoped to `refs/heads/main`, so a pull request from another branch can't obtain a token. Everything is in Bicep, reviewable, and torn down with the resource group.

**Trade-offs**
- **Container Apps Contributor can read the app's secrets.** Its `Microsoft.App/containerApps/*/action` wildcard includes `listSecrets`, so CI could read the App Insights connection string. A custom role limited to `containerApps/read` and `containerApps/write` would close that gap. Accepted for now, because that secret only allows sending telemetry.
- **The branch-based subject is a choice forced by the plan.** GitHub environments (and their approval rules) aren't available for private repositories on the free plan, so the subject is the branch rather than a `production` environment. Revisit when the repository goes public.
- **Changing the port or probes still needs `deploy.ps1`.** `az containerapp update --image` changes only the image, so the first switch from the placeholder to port 8080 with probes is a one-off manual bootstrap.

---

## ADR-006 — Agent orchestration framework

**Status:** Proposed · needed before Stage 3

**Question**
What hosts and orchestrates the agent squad (Orchestrator, Analyst, Commentator, Localiser, Producer)?

**Options**
- **A. Microsoft Agent Framework, in process.** Agents run in our own .NET service and call Foundry models through `IChatClient`. Tools are plain C# methods. Full control and easy to unit test.
- **B. Foundry Agent Service, hosted.** Agents are defined in Foundry, which stores threads and calls tools server-side. Orchestrated from .NET with Agent Framework. A stronger "Best Use of Foundry" story, with less control and harder unit testing.
- **C. Semantic Kernel agents.** Continuity with ai-doc-intelligence ADR-001, but Microsoft now points new agent work towards Agent Framework.

**To verify before deciding:** current package versions and the GA or preview status of Agent Framework and Foundry Agent Service for .NET.

---

## ADR-007 — Which model each agent uses

**Status:** Proposed · needed before Stage 3

**Question**
Which model does each agent use, and what are the token limits?

**Options**
- **A. One small, fast model for everything.** Cheapest and fastest, but analysis may be shallow.
- **B. Split by job.** A small model for live commentary lines and localisation, a larger model for the Analyst and the full-time recap.
- **C. A larger model everywhere.** Best quality, highest cost and latency.

**To verify before deciding:** which chat models the chosen region offers in Foundry, and their prices. Embeddings use `text-embedding-3-small` regardless.

---

## ADR-008 — How to chunk a match timeline for retrieval

**Status:** Proposed · needed before Stage 3

**Question**
What is one "document" in the AI Search index?

**Options**
- **A. Fixed time windows** (for example 30 seconds). Simple and predictable, but a window can cut a move in half.
- **B. Possession sequences**, from winning the ball to losing it. This is football's natural unit of play and keeps a move intact, but lengths vary widely.
- **C. Windows anchored on key moments**, the N seconds before each one. Built for "why did this happen?", but it misses play that led nowhere.

This is the timeline equivalent of the document-chunking decision in ai-doc-intelligence.
