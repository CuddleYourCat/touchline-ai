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

GitHub Actions signs in as **`id-touchline-ci`**, a user-assigned managed identity defined in `main.bicep`. Its federated credential trusts only GitHub's OIDC tokens for this repository's `main` branch, so no Azure secret is stored in GitHub. The repository uses GitHub's **immutable subject** format, `repo:CuddleYourCat@37294565/touchline-ai@1422432373:ref:refs/heads/main`. It includes the owner and repository IDs, so a deleted and recreated repository with the same name can't reuse the identity. The first deploy failed with `AADSTS700213` because the credential expected the plain `repo:owner/name` subject. The identity has exactly three roles:
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

## ADR-014 — The simulator models possession chains between pitch zones

**Status:** Accepted · 1st half

**Decision**
A match is a sequence of **possessions**. Each possession moves the ball between pitch **zones** through actions: passes, carries, pressure, tackles, shots and fouls. The probability of each next action comes from the two teams' **tendency profiles** (pressing intensity, width, directness, shot appetite). Shot quality (xG) depends on the shooting zone and the situation (open play, counter, set piece). A seeded random number generator makes every match exactly repeatable.

Pitch coordinates are in **metres on a 105 × 68 pitch**, with the home team attacking left to right. These are real units, so "11 m from goal" means just that. Each event records its match clock, team, player, start and end position, outcome and a possession ID, so the indexer can later group whole moves (ADR-008).

**Alternatives considered**
- **Simulating every player's position several times a second.** The most realistic option, but far more code and calibration than the 1st half allows.
- **Scripted move templates chained together.** Quick to write, but the agents would keep finding the same few canned patterns, which defeats "explain the tactics".

**Rationale**
Possession chains are the unit coaches and analysts already think in. Team tendencies produce believable, *explainable* patterns. For example, a high-pressing side wins the ball back high up the pitch more often, and the Analyst agent can point to those events. The model can be calibrated against league averages (about 2.8 goals and 25 shots per match), and it stays small enough to unit-test exactly with a fixed seed.

**Trade-offs**
There are no off-ball positions, so questions like "the left-back was out of position" have to be inferred from events rather than from tracking data. Calibration is iterative: the first version will be too random or too tidy.

---

## ADR-015 — The Event Processor runs on Azure Functions Flex Consumption

**Status:** Accepted · 1st half

**Decision**
`Touchline.EventProcessor` is an Azure Functions app (.NET isolated worker) with an **Event Hubs trigger**, hosted on the **Flex Consumption** plan. It writes events and match state to Cosmos DB and publishes key moments to Service Bus. It authenticates everywhere with a managed identity, through identity-based trigger and binding connections, with no connection strings.

**Alternatives considered**
- **Functions on Container Apps.** It would reuse the existing environment, registry and deploy pipeline, and scale on Event Hubs lag through KEDA, but it's a less common hosting model in exam material.
- **A plain worker using `EventProcessorClient` in Container Apps.** Full control of checkpointing, but no Functions triggers or bindings, which are core Domain 3 topics.

**Rationale**
Flex Consumption is the current serverless Functions plan, and its event-driven scaling per trigger is what the exam's Functions questions assume. Triggers and output bindings remove most of the plumbing code.

**Trade-offs**
- **A second hosting model** alongside Container Apps, with its own storage account for deployment packages and its own CI deploy step. The CI identity needs an extra narrowly scoped role on the function app.
- **Local runs need Azure Functions Core Tools** for Aspire to start the Functions host.

---

## ADR-016 — Event Hubs runs in the emulator until the pipeline works end to end

**Status:** Accepted · 1st half

**Decision**
During the 1st half, the simulator and Event Processor run against the **Event Hubs emulator** in Docker, wired up through the Aspire AppHost. The Azure Event Hubs namespace (Basic tier) is created only once the whole pipeline works locally.

**Alternatives considered**
- **Create Event Hubs in Azure straight away.** Simpler to reason about, but the hourly charge (about £8–9 a month) starts on day one, out of a £30 budget (ADR-011).

**Rationale**
The emulator speaks the same protocol, so the code doesn't change between local and Azure. Only the connection target changes, and Aspire handles that. It keeps the budget for model calls, and the local development loop needs no network.

**Trade-offs**
The emulator doesn't do everything: there's no Entra ID authentication, and its quotas and behaviour differ slightly. Managed identity access to Event Hubs is first exercised in Azure, so that slice needs its own end-to-end check.

---

## ADR-017 — Events name the second player with type-specific fields

**Status:** Accepted · 1st half

**Decision**
`MatchEvent` gains three optional properties, each valid on one event type only:
- **`RecipientPlayerId`** on a **pass**: the team-mate it was aimed at.
- **`AssistPlayerId`** on a **shot**: the team-mate whose pass or set-piece delivery created it.
- **`FouledPlayerId`** on a **foul**: the opponent who was fouled.

`EnsureValid` rejects any of them on the wrong event type. The simulator sets them, because it chose who passed, who shot and who was tackled, so it knows the truth.

**Alternatives considered**
- **One generic `RelatedPlayerId`**, whose meaning depends on the event type, as in several commercial data feeds. Fewer properties, but every reader has to know the per-type meaning, and an agent prompt is less clear.
- **Deriving them in the Event Processor** (the assist as the last team-mate pass in the possession, the fouled player as the opponent who last had the ball). No schema change, but it needs state across events, and it's ambiguous around deflections and off-ball fouls.

**Rationale**
Raised during skill shot 1. A penalty reason should say who was fouled, and a goal reason who made the assist. Named fields are self-documenting, checkable, and give the agents unambiguous facts at no extra cost in tokens or code.

**Trade-offs**
There are three nullable properties on every event, most of them empty on any given event. A future relationship (for example "tackled player" on a tackle) means another field and another `EnsureValid` rule.

---

## ADR-018 — The match engine lives in Touchline.Simulator

**Status:** Accepted · 1st half

**Decision**
The possession-chain engine (ADR-014) is in `Touchline.Simulator`, beside the worker that runs it, as plain classes with no Azure dependency. The domain stays the shared vocabulary every service needs: events, state, team profiles and key-moment rules.

**Alternatives considered**
- **Putting the engine in `Touchline.Domain`.** It would be reusable anywhere, for example by an agent tool that replays "what if" scenarios, but every service would then carry the engine whether it used it or not.

**Rationale**
Only the simulator generates matches. The Event Processor and the agents consume events, so they shouldn't depend on how events are made. The engine can still move later if a second consumer appears.

**Trade-offs**
The unit tests now reference `Touchline.Simulator` as well, and an engine class can't be reused from another service without a project reference or a move.

---

## ADR-019 — Richer events: metadata, pressure, xPass, speeds and player traits

**Status:** Accepted · 1st half, before slice 04

**Decision**

*Match metadata*
- A new **`MatchMetadata`** event at sequence 1, before kick-off. It carries the two team IDs and names, both starting elevens, a fictional venue, the kick-off time (UTC) and the seed.
- It is a `MatchEvent` like any other, so the stream has one type. By convention its `Team` is `Home` and its `Start` is the centre spot. `MatchState.Apply` accepts it and changes nothing but the sequence.

*Pressure and difficulty*
- **`UnderPressure`** (bool) on passes, carries and shots. The engine already decides it and currently throws it away.
- **xPass** on every pass: the probability that an **average player** completes that pass, from distance, pressure, the target's third and long ball. This is the number the engine already computes. It lives in a new **`PassDetail`**, beside the pass's speed.

*Speeds, all in km/h*
- **`SpeedKmh`** on passes (ball speed, about 30–90), shots (about 60–125 footed, 10% slower on the weak foot so down to 54, and 40–70 headed) and carries (the player's speed).
- Carry distance isn't stored. It's derived from `Start` and `End`.

*Player traits, in the team profiles*
- **`PreferredFoot`**: Left, Right or Both. The free-text `Role` keeps its description; this is the structured version.
- **`TopSpeedKmh`**: the player's **recorded previous best**, for example 34.2.
- **`Finishing`** and **`Passing`**: ratings from 0 to 1, where 0.5 is an average player.
- `TeamProfileCatalog` validates all four. Every player in both squads gets values that match their `Role`.

*How the traits are used: they affect outcomes, not expectations*
- **Shots record a `BodyPart`** (LeftFoot, RightFoot, Head) and a **`WeakFoot`** flag. The engine picks the body part from the situation (headers from corners and crosses) and the shooter's preferred foot (a "Both" player never has a weak foot).
- **xG stays an average-player measure**, but now includes the body part: a header's xG is 0.6 × the footed value, as in public xG models.
- **The result** uses the player: the chance of scoring is xG × a finishing factor (0.75–1.25 across the rating range) × 0.8 on the weak foot. A pass completes with xPass × a passing factor, × 0.9 on the weak foot.
- **Carry speed** is drawn from 70–98% of the player's `TopSpeedKmh`. Very rarely (about once in 400 sprints) a carry exceeds it by 0.1 to 0.5 km/h. The minimum gain is the 0.1 km/h that speeds are rounded to, so a record is never "beaten" by a speed equal to it.
- The event stores **only the speed**. Spotting a **new personal best** is left to the milestones detector, which compares speeds with the profile and with earlier carries in the match.

*Settled during the build*
- **`PassDetail` also records `WeakFoot`**, because a weak-foot pass completes less often, and the Analyst should be able to say why.
- **The venue is the home club's ground**, from a new required `Venue` on `TeamProfile`: Northgate Stadium and Kennet Meadow.
- **`DomainJson` now sets `RespectRequiredConstructorParameters`.** A constructor value missing from the JSON (a player's top speed, a shot's speed) is an error, instead of quietly becoming 0. `PreferredFoot` starts at 1, so an unset foot fails validation too.

*Settled at code review*
- **Weak-foot factors are relative to the stronger foot, and the two are normalised.** An average one-footed player uses their weaker foot 22% of the time, and that mix is part of "average". So when the weaker foot is possible, the stronger foot is scaled slightly up (×1.046 for shots, ×1.022 for passes) and the weaker one down (0.8 and 0.9 of the stronger), keeping the mix at 1. Without that, xG and xPass would overstate every one-footed player.
- **Outfield ratings average 0.5**, as the definition requires. Passing was lowered by 0.13 across the outfield, from about 0.63, where the surplus had been hiding the un-normalised weak-foot penalty.
- **Metadata is validated on construction.** Each team sheet needs an ID, a name, a short name and eleven different player IDs. The venue is required, the two teams must differ, the kick-off is stored in UTC whatever offset it arrives in, and the metadata event may not name a player or an end position. `TeamSheet` compares its starting eleven by value, so two identical sheets read from JSON are equal.
- **Enum values are checked.** JSON accepts any number for an enum, so `EnsureValid` rejects an undefined type, team or card colour, and `ShotDetail` an undefined outcome, situation or body part.

*Calibration*
- The existing ranges stay.
- New checks:
  - weak-foot shots are 15–30% of footed shots, and headers 10–20% of all shots;
  - mean xPass is within 0.02 of the actual completion rate;
  - goals stay within ±0.4 of xG over 200 matches, because the ratings average 0.5.

**Alternatives considered**
- **Player-adjusted xG and xPass**, where a weak-foot shot or a poor finisher gets a lower number. More personal, but the numbers stop being comparable across players, and the over- and under-performance story disappears into them.
- **No player traits.** Simplest, but footedness, finishing and pace never appear in the match or the explanations.
- **Metadata as a separate match document.** A smaller event type, but every consumer must fetch it separately, and stream-only consumers (the overlay feed) start without names or line-ups.
- **Speeds in m/s**, the SI unit. Consistent with metres and seconds, but every display and prompt has to convert.
- **A personal-best flag on the carry.** Cheaper to read, but the simulator would be deciding a milestone, which belongs to the deterministic detector.

**Rationale**
- The brief asks for match metadata, pressure, pass difficulty, and player and ball speeds (backlog items i-a and ii).
- Adding them **before** slice 04 fixes the event schema once: the producer, the stored documents and the consumers are never changed for it later.
- Keeping xG and xPass as average-player measures matches industry practice, and it gives the Analyst a true story to tell: *"Ashworth beat his xG on his weaker right foot."*

**Trade-offs**
- **`MatchMetadata` gives `Team` and `Start` conventional values**, because they are required members. That's a small wart in exchange for a single event type.
- **Every player now carries four numbers to keep consistent with their `Role`** text. The catalog checks the ranges, not the consistency.
- **Each trait adds randomness**, so all pinned seed tests and the calibration have to be re-run, and the engine's random draws (and so every seeded match) change.

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
