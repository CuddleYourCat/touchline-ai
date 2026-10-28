# DECISIONS.md — Architecture Decision Records

`touchline-ai` · .NET 10 + Azure AI · started for the Inside the Game hackathon (October 2026), not submitted

Each entry records what was decided, what the alternatives were, why this option was chosen, and the trade-offs. Numbers are stable. A superseded decision gets a new entry rather than an edit.

**Reading this log.** It was written as the project was built, between 16 and 28 October 2026, and is kept as it was. "The brief", "judging", "demo day" and "the submission" refer to the hackathon. Dates without a year are 2026. An early entry's figures can be overtaken by a later update or amendment, and where they differ, the latest entry and the code are right. `CLAUDE.md` was the private working agreement for the coding assistant and isn't part of this repository; its rules that matter here are stated in the entries that cite it.

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

**Update (22 Oct): the same seed on every platform**
CI found that a seed played a different match on Linux than on Windows. `Math.Exp`, `Math.Log` and `Math.Cos` call the operating system's C maths library, which can differ in the last bit, and one flipped bit steers every later random choice (here from event 109 of seed 174). The engine now uses `PortableMath`: Exp, Log and Cos built only from IEEE 754's correctly rounded operations (+, −, ×, ÷ and square root), so a seed replays identically on a PC, in CI and in the Azure job. A test pins exact bit patterns, so CI on Linux proves it. Matches changed only in their last digits (seed 174 is still the same 3–2), and calibration still passes. Any new transcendental function in the engine must go through `PortableMath` too.

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

**Status:** Accepted · Kick-off. The £30 budget is superseded by ADR-026 (£60, for Azure AI Search Basic).

**Decision**
Every touchline-ai resource lives in a new resource group, `rg-touchline-ai`, in **Sweden Central** (except Azure AI Search, which ADR-058 moves to UK South). It's on the existing pay-as-you-go subscription, alongside other projects but separate from them. A budget alert at **£30 a month** is scoped to the resource group, and the app must stay within it through judging.

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

## ADR-020 — Officials, temperament and the height of the ball

**Status:** Accepted · 1st half, before slice 04

**Decision**

*Referees*
- **A referee catalog**, embedded JSON like the team profiles, holds four fictional referees, from strict to lenient. Each `RefereeProfile` has an ID, a name and three traits from 0 to 1, where 0.5 is an average referee:
  - **`Strictness`**: cards per foul. Across the range, about 3 to 5 yellows a match.
  - **`Whistle`**: how readily a challenge is given as a foul. Across the range, about 18 to 25 fouls a match.
  - **`Advantage`**: how often play goes on after a foul, from about 15% to 45% of fouls outside the box.
- **The caller picks the referee**, or the seed does (`seed` modulo the catalog size, so no random draw is used and the match itself doesn't change). The same match can be replayed with a different referee.
- **`MatchMetadata` names the referee** (ID and name), so every consumer knows who's officiating.
- A test guards against real referees' surnames, as one already does for players (ADR-002).

*Advantage*
- **A foul where play goes on** is still a `Foul` event, with **`AdvantagePlayed = true`**. The attacking team keeps the ball, and the move (its counter and its assist) carries on.
- **A card the foul deserved is shown at the next stoppage**: a goal, a shot that goes dead, the next foul given, a corner or the end of the half. That's what real referees do, and it gives the commentary its "he's come back to book him" moment. It still counts towards a second yellow.
- **Every `Card` event names its foul**, in a new `FoulSequence` field (valid on cards only). A deferred card can trail its foul by several events, so consumers link the two by sequence, not by adjacency.
- A foul inside the box is never played on. It's a penalty.

*Temperament*
- **`Discipline`**, a player trait from 0 (a hot-head) to 1 (never in trouble), where 0.5 is average.
- It scales both how often the player's tackles are fouls and how often those fouls earn a card, by ×1.41 at 0 down to ×0.59 at 1. So a hot-head picks up about **twice** an average player's cards.
- Each squad's outfield average is 0.5, and the `Role` text matches the trait (for example "ball-winning midfielder, short-tempered").

*Height*
- **Shots record a `GoalMouth` position**: `Across` (metres from the centre of the goal, along the goal line) and `Up` (metres above the ground). The frame is ±3.66 m across and 2.44 m up.
  - It's **relative to the frame, not clipped to it**. A miss wide reads `Across = 5.1`, and one sent "over like a rugby kick" reads `Up = 7.5`.
  - Every shot that reaches the goal line has one. A blocked shot doesn't.
  - `ShotDetail` checks it agrees with the outcome: inside the frame for a goal or a save, outside it for a miss, and on the frame for a woodwork hit.
- **`HitWoodwork`** is a flag beside the outcome, not a fifth outcome:
  - *In off the post* is a `Goal` with the flag set.
  - *Off the bar and out* is `OffTarget` with the flag set. The ball rebounds into play, contested by both teams, and can lead to a second shot.
  - Commercial data feeds count woodwork-and-out as off target, so on-target figures stay comparable.
- **`PassHeight`** on `PassDetail`: Ground, Low or High. Short passes are mostly on the ground, long balls mostly in the air, and corners are always High.
- **An open-play header needs a cross**: the header chance inside the box applies only after a completed High pass. Headers no longer appear from nowhere.

*Penalties: taker against keeper*
- **Six goal-mouth zones**: left, centre or right, each low or high, **from the taker's point of view**. `GoalMouth.Across` uses the same view on every shot: positive is the shooter's right.
- **Takers have an aim** (`PenaltyAim`, weights over the six zones), and **goalkeepers a dive bias** (`Dive`: left, stay or right). Both are optional in the profile. A player without one uses the league-average distribution.
- **The duel**: the keeper commits to a dive, and the taker's zone is drawn from their aim.
  - Guess right, and a low shot is often saved and a high one sometimes.
  - Guess wrong, and only a miss (more likely when aiming high) stops a goal.
  - Finishing then scales the result, as for every shot.
- **xG stays 0.76**, the average player's conversion, and the tendencies change only the outcome. The calibration checks that conversion over a season stays within 0.70 to 0.82.
- The last five penalties per taker are a season projection (ADR-021). The events record everything it needs: the taker, the zone through the goal-mouth position, and the outcome.

*Season hooks in the metadata (the schema for ADR-021)*
- **`Kind`**: the type of match.
  - `Fixture`: an official match, which counts towards the season.
  - `Replay`: a re-run of a fixture, which never counts, and names the original in **`ReplayOf`**.
  - `Exhibition`: a demo or test match, which never counts. **It's the default**, so a test run or a demo can't pollute the season by accident.
- **`Season`** (for example "2026-27") and **`Round`**, both required on a fixture.
- A replay has its own match ID, so its documents never overwrite the fixture's.

*Calibration*
- The existing ranges stay.
- New checks:
  - about 0.5 to 1.3 woodwork hits a match;
  - penalty conversion stays within 0.70 to 0.82;
  - 2 to 6 advantages played a match;
  - the strictest referee shows at least 1.5 more yellows a match than the most lenient;
  - a hot-head (Discipline 0.2) collects at least 1.5 times the cards per appearance of a calm team-mate in the same position;
  - headers stay at 10–20% of shots.

*Settled during the build*
- **`ShotDetail` also records `KeeperDive`** on penalties. A keeper's predictable dive can't be found from the season's data unless every dive is recorded, so it belongs in the event, like the taker's zone.
- **Finishing is balanced per shot, not per squad.** xG models learn from the players who actually shoot, mostly forwards. With squad-average ratings of 0.5, goals ran 15% ahead of xG once headers needed crosses and rebounds fell to attackers. Outfield finishing was lowered by 0.12, so the average shot is now taken at about 0.46, and a test checks the shot-weighted average stays within 0.45 to 0.55. Passing, discipline and the referees' traits are still balanced per player, because everyone passes, tackles and is refereed.
- **The whistle runs from 0.80 to 1.20**, wider than first drafted, so the referees' fouls actually span about 18 to 25 a match (Pell 23.0, Okonkwo-Ward 19.0).
- **The hot-head check asks for 1.5, not 1.7.** On temperament alone, a 0.2 against a 0.6 is about 1.9 times the cards. A booked player eases off like anyone else, which brings it to about 1.7 to 2.1 depending on the seeds.
- **`MatchSimulator` takes a `MatchOptions`** (match ID, kick-off, referee, kind, season, round, replay), all optional, with an exhibition as the default. It builds the metadata when the match is set up, so invalid options fail at once rather than mid-stream.
- **The engine is split into partial files**: the core (the match loop, passes, carries, possession), fouls and officiating, shots, and player traits.

*Settled at code review*
- **A replay never shares its fixture's match ID.** By default it's `{fixture}-replay-{seed}`. An explicit ID equal to `ReplayOf` is refused by the simulator, and a metadata event whose replay names its own match ID fails `EnsureValid`, so the Event Processor rejects it too. Otherwise a replay's documents would overwrite the fixture's (ADR-021).
- **An exhibition can't carry a season or a round.** That combination is almost always a fixture whose kind was forgotten, which would silently drop a round from the season.
- **A penalty always lands in the zone it was aimed at**: goals, saves, misses (over the bar above that column, or wide on its side) and woodwork (that side's post, or the bar above it). A low shot down the middle can't miss or hit a post, only be saved. So a taker's history, and the scout that reads it, is never misled.
- **The metadata's starting eleven and the players on the pitch come from one definition**, `Lineup.StartingEleven`, so ADR-021's unavailability list can't make them disagree.
- **Advantage applies outside the box and outside the fouled team's own third.** The 15–45% is the share of those fouls, roughly 10–30% of all fouls.

**Alternatives considered**
- **One default referee.** Simpler, but referee style never varies, so the brief's referee story can't be told.
- **A referee chosen by the seed only.** Deterministic, but "the same match with a strict referee" is impossible as a demo.
- **Showing a deferred card at once**, while play goes on. Simpler to simulate, but it isn't how football works.
- **No advantage events**, where a lenient referee simply gives fewer fouls. The smallest change, but "let the game play on" never appears in the data.
- **A `Woodwork` outcome.** Easy to read, but in off the post would lose the detail, and every switch over outcomes would need a new branch.
- **Full 3D ball flight.** No consumer needs a trajectory. The goal mouth and pass height carry the moments people talk about.

**Rationale**
- Officials and temperament are among the strongest real causes of cards and fouls. Without them, every booking is just bad luck, and the Analyst can't explain *why* a match was scrappy.
- The goal mouth makes finishing visible (top corner or straight at the keeper), and it's the basis of post-shot xG if we ever want it.
- Every schema change here lands before Event Hubs fixes the shape (slice 04), like ADR-019's.

**Trade-offs**
- **A deferred card means state across events**: the engine has to remember the pending card, and `FoulSequence` adds one more optional field to every event.
- **The referee choice changes the match but not the random sequence**, so the same seed with two referees gives two different, still repeatable, matches.
- **Another catalog and another trait to keep consistent with `Role`** text.
- **Seeded matches change again**, so the calibration is re-run.

---

## ADR-021 — Season state: profiles in Cosmos DB, stats as an idempotent projection

**Status:** Accepted · built with slices 04 and 05

**Decision**

*Profiles*
- **Cosmos DB is the source of truth** for team, player and referee profiles.
- The embedded JSON files become **seed data**, loaded at provisioning. They also remain the offline fixtures the unit tests use, so no test needs a database.

*Season state is derived, never written by the simulator*
- The Event Processor projects the event stream into Cosmos DB. That includes:
  - per-player season stats (appearances, goals, assists, xG, passes, cards and so on);
  - suspensions;
  - each taker's last five penalties, with goal-mouth position and outcome.
- **Only `Fixture` matches count** (ADR-020). `Replay` and `Exhibition` matches are processed for their own documents, but never touch the season.
- **The projection is idempotent.** Each player's contribution to a match is one document, with ID `{matchId}:{playerId}`, written with an upsert. Season totals are the sum of those documents, so a fixture delivered twice (Event Hubs is at-least-once) overwrites rather than double-counts.

*Suspensions*
- The fictional league's rules are decided when this ADR is accepted (proposed: five yellows means a one-match ban, and a red card means one match).
- Before kick-off, the simulator worker reads who is unavailable and passes that list in. The engine stays pure, with no database dependency.

**Alternatives considered**
- **Profiles stay in JSON, and only state goes in Cosmos DB.** The git history records every rating change, but a profile change needs a deploy.
- **Running totals updated in place.** Simpler to read, but at-least-once delivery double-counts after a restart.
- **The simulator writes season stats itself.** One less service, but the stats then disagree with the stored events whenever anything is replayed or reprocessed.

**Rationale**
- Season state is a fact derived from events, so it belongs to the consumer that already reads every event. Rebuilding it from scratch is just replaying the fixtures' events.
- The change feed and materialised views are core Cosmos DB patterns (AI-200 Domain 2).

**Trade-offs**
- **Season totals are a query (a sum over documents)**, or a second, cached projection, rather than a single read.
- **The fixture order matters for suspensions**, so fixtures must be played in round order.
- **The seed data and the database can drift**, so provisioning has to be explicit about which one wins.

---

## ADR-022 — Lines, the ball out of play, and key moments redefined

**Status:** Accepted · 1st half, before slice 04

**Decision**

*Positions beyond the lines*
- **`PitchPosition` allows a 5 m margin** beyond every line: x from −5 to 110, and y from −5 to 73. That covers the net's depth and the run-off around the pitch. Validation still catches typos and unit errors.
- **`PitchDimensions.IsInPlay(position)` applies the Laws**: the ball is out only when **all of it** is over the line, so its centre must be more than the ball's radius (0.11 m) beyond.
- **`PitchDimensions.OverTheGoalLineBy(position, attacking)`** gives the metres by which the whole ball is over the goal line. It's positive for a goal, and negative for a ball that didn't fully cross.
- Every event **starts in play**, except `BallOutOfPlay`. A shot's or pass's `End` may be out: in the net, wide, or over the touchline.

*The ball going out of play*
- **A new `BallOutOfPlay` event**, raised the moment the ball goes out. It records:
  - **`Start`**, where it crossed, beyond the line;
  - the player who touched it last (its `Team` is theirs);
  - the **`Restart`**: `ThrowIn`, `GoalKick` or `Corner`.
- The restart happens on the next event, at the right spot: on the touchline, in the six-yard box or at the corner flag. It's a **notable** moment, not a key one.
- **The engine sends the ball out for real**: overhit and misplaced passes and clearances go out for throw-ins, misses for goal kicks, and saves and blocks out for corners.
- The calibration aims for about **35–50 throw-ins, 12–20 goal kicks and 8–12 corners** a match. The corners need more sources than today's saves and blocks.

*Clearances off the line*
- **A clearance off the line is still a `Blocked` shot** (off target, in data-feed terms), with **`ClearedOffLine = true`**. It also records the goal-mouth position where it was stopped, which is inside the frame and at or just short of the line.
- **Every blocked shot names its blocker** in a new second-player field, **`BlockedByPlayerId`** (ADR-017's pattern, valid on blocked shots only).
- Target: about 0.1–0.4 clearances off the line a match.

*Goals cross the line*
- A goal's `End` is **inside the net**, beyond the line, so `OverTheGoalLineBy` is positive.
- Most goals are well over. About 1 in 100 is over by under 5 cm, which is a **close call**.
- A clearance off the line can be a close call too: not fully over, by under 5 cm.

*Key moments, redefined*
- **New key-moment kinds**, alongside Goal, BigChance, PenaltyAwarded and RedCard:
  - **`PenaltySaved`**: the keeper stopped it;
  - **`PenaltyMissed`**: the taker missed, including off the woodwork;
  - **`ClearedOffLine`**: the defender's name and the goal it stopped.
  - A missed or saved penalty is no longer reported as a BigChance. The more specific moment wins.
- **A close goal-line call is a detail on the moment, not a separate kind.** A `Goal` or `ClearedOffLine` moment carries **`GoalLineMarginCm`** when the call was within 5 cm, and its reason says so ("goal-line technology confirms the whole ball was over, by 2 cm"). A single shot never raises two key moments.
- **Woodwork and the ball out of play stay in the notable tier** (backlog: template commentary, no model call).
- **Disallowed goals go to the backlog as a stretch goal.** They need offside, offside needs every player's position, and the engine only tracks the ball. A random "VAR check" would hand the agents an invented reason to explain as fact.

**Alternatives considered**
- **A separate out-of-play position type.** Strict, but every `End` becomes one of two types, and every consumer must handle both.
- **Keeping the lines as the limit**, with ends recorded as crossing points. The smallest change, but "in the net", "over by 2 cm" and "out for a throw" can't be expressed.
- **The restart recorded on the next event** instead of a `BallOutOfPlay` event. A smaller schema, but where and why the ball went out is lost.
- **A `ClearedOffLine` shot outcome.** Explicit, but every outcome switch grows a branch, and on-target statistics have to choose a side.
- **`GoalLineCall` as a key-moment kind.** That's two moments for one shot (a goal and a call), or a goal reported as something other than a goal.

**Rationale**
- The Laws define in and out by the *whole ball*, so the model needs space beyond the lines to say it truthfully.
- The rarest moments (a penalty saved, a ball cleared off the line, a goal by 2 cm) are the ones fans remember. They deserve the agent treatment, and their cost is negligible at well under one a match.
- Like ADR-019 and ADR-020, it changes the schema before Event Hubs fixes it. `BallOutOfPlay` and the new moment kinds are additive, and still rely on the tolerant readers that slice 04 will decide.

**Trade-offs**
- **The match has less play, and slightly fewer events (about 1,590, down from 1,775).** I'd expected more. The out-of-play events and restarts are new, but each restart also takes real stoppage time (a throw-in 8–15 s, a goal kick 22 s, a corner 25 s), so fewer passes and fouls fit into 90 minutes. That's realistic: the ball is in play for only about an hour in a real match.
- **The pitch's own rules move into `IsInPlay`**, so "on the pitch" is no longer just "a valid position". Every check that meant "in play" must call it.
- **The engine gains restart logic** for throw-ins and goal kicks, and corners from new sources, so the seeded matches and the calibration change again.

*Settled during the build*
- **Every restart is taken at once, as the next event**: a throw-in (a low pass from the touchline), a goal kick (the keeper's pass from the six-yard box) or a corner (from the flag). The ball going out is a stoppage, so any cards owed after advantage are shown then.
- **A clearance off the line doesn't change xG.** Like the weak foot (ADR-019), the engine draws a goal-bound shot 1 ÷ (1 − 0.06) more often, then clears 6% of them, so the share that goes in is still the xG.
- **Retuned:**
  - `CornerHeaderShare` is now 0.65 (it was 0.85), and `CrossHeaderShare` 0.45 (it was 0.55), because corners roughly tripled to about 9 a match;
  - `FailedPassOutShare` is 0.23;
  - `YellowPerFoul` rises from 0.19 to 0.21, to keep about 4 yellows a match with less playing time.
- **The penalty checks were redesigned.** With league-average tendencies the duel converts at about 77%, matching its 0.76 xG. The real squads convert less, because both keepers happen to read the opposing taker. So the mechanism is tested with a controlled duel: against a keeper who always reads the taker, conversion falls to about 44% (measured 45%), and against one who always guesses wrong, nothing is saved.
- **The goal-line margin rounds away from the line, after rounding to the micrometre.** That way floating-point error can't turn 3 cm into 4.

---

## ADR-023 — The event pipeline: envelope, tolerant readers, idempotent storage, paced publishing

**Status:** Accepted · 1st half, slice 04

**Decision**

*The message*
- Each Event Hubs message is **one `MatchEvent`** in `DomainJson`. Its **envelope** is carried as application properties:
  - `schemaVersion`, starting at `1`;
  - `type`;
  - `matchId`;
  - `sequence`.
- The **partition key is the match ID** (ADR-004), so a match's events stay in order on one partition.
- The producer fills an `EventDataBatch` and sends it when it's full or when the pacing says so. A full batch never drops an event, because a `TryAdd` that returns false triggers the send.

*Tolerant readers*
- **Consumers read the envelope first.** An event whose `type` they don't recognise, or whose `schemaVersion` has a newer **major** version, is **logged, counted** (an OpenTelemetry counter, `touchline.events.skipped`, tagged with the reason) **and skipped**. The checkpoint moves past it, so one unknown event never stalls a partition.
- **Unknown JSON fields are ignored**, as `System.Text.Json` already does.
- **An event that fails `EnsureValid` is treated the same way**: logged with its reason, counted and skipped. It's never retried forever.
- Adding an event type or an optional field is then a **minor** version change and backward compatible. A breaking change bumps the major version.
- **A new value in an existing enum field is a major change** (settled at code review). Older readers can't parse a body that contains a restart, body part or outcome they don't know, so without the major bump they'd skip it as invalid, and it would look like a bug rather than a version mismatch.
- **A gap in a match's sequence is recovered from, not waited on** (settled at code review). Within a partition, events arrive in order, so a gap means events were lost, usually a batch that failed every retry before the trigger checkpointed past it. Waiting for them would freeze the match for good. Instead:
  - the missing sequences are skipped, counted on `touchline.events.lost` and logged as an error;
  - the match carries on with incomplete totals.
- **A match processed without its metadata is counted** on `touchline.matches.missing_metadata`, because its kind (and so whether it counts towards the season) is then unknown.

*Storage in Cosmos DB (the emulator first, ADR-016)*
- **`events`**, partitioned by `/matchId`. The document ID is the event's sequence, written with an upsert. A redelivered batch (Event Hubs is at-least-once) overwrites rather than duplicates.
- **`matches`**, partitioned by `/matchId`. The live `MatchState`, plus the metadata, upserted **once per batch**, not per event. It's one point read for the Match Centre.
- The season projection (ADR-021) is slice 05's job. This slice keeps the `Kind` with each match so that projection can filter on it.

*Key moments*
- **The Event Processor runs `KeyMomentDetector`** on each event against the state before it. It publishes each key moment to the Service Bus topic **`key-moments`**, which has two subscriptions: `commentary` and `highlights`.
- The message ID is `{matchId}:{sequence}`, so Service Bus duplicate detection can drop a republished moment once the namespace supports it (the Standard tier, slice 05).

*Publishing pace*
- **The simulator worker waits the real gap between events on the match clock, divided by a configurable speed.**
  - The default is **10×**, so a match takes about 9 minutes.
  - `1×` gives a true live feel, and tests use a very high speed.
  - Events with no gap between them go in the same batch.
- **Matches start from configuration.** The worker plays the matches listed under `Simulator:Matches` (seed, kind, season, round, referee, speed) when it starts, then idles. A Match Centre trigger comes later, with the fan UI.

*Telemetry*
- **One trace per batch.** It runs from the producer's send, through the Event Hubs trigger, to the Cosmos DB writes and the Service Bus publish. W3C trace context is carried in the message's `Diagnostic-Id` and `traceparent` properties, which the Azure SDKs set and read.
- Each match also gets a `match.id` span attribute, so all its traces can be found together.

**Alternatives considered**
- **Parking unknown or invalid events** in a separate container before skipping them. Nothing would be lost, but it means more code and storage now. It's worth revisiting if skips ever happen in practice.
- **Event Hubs Schema Registry.** It's the strongest contract, but it needs the Standard tier and isn't in the emulator.
- **Storing events only**, and rebuilding state on read. The simplest write path, but every score read would scan about 1,600 documents.
- **One container with a document type.** Fewer resources, but every query has to filter by type, and their indexing needs differ.
- **Publishing as fast as possible**, or always in real time. The first never lets a match unfold for the agents; the second is too slow to develop against.
- **A Match Centre endpoint that starts matches.** Closer to the product, but it needs an authenticated API and a queue before there's a fan UI to call it.

**Rationale**
- **The envelope keeps a reader safe** however the schema evolves (ADR-019 to ADR-022), at no cost on the Basic tier.
- **Upserts keyed by the sequence** make at-least-once delivery harmless.
- **Paced publishing** gives the agents and the Match Centre a real match to follow.
- **These are the exam's Domain 2 and 3 patterns**: partition keys, batching, checkpoints, tolerant consumers, idempotent writes and trace propagation.

**Trade-offs**
- **A skipped event is gone from that consumer's view.** Only the log and the metric record it.
- **State is upserted per batch, so the Match Centre can lag a batch behind.** That's well under a second at 10×.
- **Three emulators** (Event Hubs, Service Bus, Cosmos DB) all run in Docker. The Event Hubs emulator also needs an Azurite storage container for its checkpoints.

---

## ADR-024 — The event pipeline in Azure: tiers, identities and the simulator job

**Status:** Accepted · 1st half, slice 05

**Decision**

*Resources, in `infra/main.bicep` (ADR-012)*
- **Event Hubs, Basic tier, 1 throughput unit.** One hub, `match-events`, with **2 partitions** and the `$Default` consumer group. Two matches can then be processed in parallel. The partition count can't be changed later on Basic.
- **Service Bus, Standard tier.** This corrects ADR-011, which budgeted for Basic: Basic has queues only, and ADR-004 and ADR-023 need a topic. The `key-moments` topic has **duplicate detection** on (a 10-minute window, matching the `{matchId}:{sequence}` message ID), with the `commentary` and `highlights` subscriptions.
- **Cosmos DB for NoSQL, serverless.** Database `touchline`, with the `events` and `matches` containers partitioned by `/matchId`, as on the emulator.
- **The Event Processor on Functions Flex Consumption** (ADR-015), .NET 10 isolated worker. Its own storage account holds the deployment package, the host's leases and the Event Hubs checkpoints.
- **The simulator as a Container Apps job**, `job-simulator`, in the existing environment. It's manually triggered, runs one execution at a time, and doesn't retry. Each execution plays the configured matches, then exits.

*Identity: Entra ID only*
- **Local (key) authentication is off** on Event Hubs, Service Bus and Cosmos DB, and shared-key access is off on the processor's storage account. No connection string or account key exists to leak.
- **One user-assigned identity per workload**, with each role scoped to the entity rather than the namespace:

| Identity | Role | Scope |
|---|---|---|
| `id-touchline-simulator` | Azure Event Hubs Data Sender | `match-events` |
| | AcrPull | the registry |
| `id-touchline-processor` | Azure Event Hubs Data Receiver | `match-events` |
| | Azure Service Bus Data Sender | `key-moments` |
| | Cosmos DB Built-in Data Contributor | the `touchline` database |
| | Storage Blob Data Owner | the processor's storage account |
| your account (`principalId`) | Cosmos DB Built-in Data Contributor | the account, for Data Explorer |

- **Cosmos DB data roles are not Azure RBAC.** They're the account's own `sqlRoleAssignments`, so they're declared as Cosmos DB child resources.
- **Locally, nothing changes.** Aspire still runs the emulators (ADR-016), and the same code switches targets through configuration.

*Deployment (ADR-013, ADR-015)*
- **CI deploys the function code** after the Match Centre, and **builds the simulator image** with ACR Tasks and points the job at it.
- `id-touchline-ci` gets **two more roles, each on one resource**: Website Contributor on the function app (to deploy its package) and Container Apps Jobs Contributor on `job-simulator` (to update its image).
- Infrastructure changes still go through `deploy.ps1`.

*Starting a match*
- **The seed is passed per run.** The job's configured seed is only a default. Each start overrides it with an environment variable (`Simulator__Matches__0__Seed`).
- **A repeated seed is a redelivery test.** The match ID comes from the seed, so the processor treats a rerun as duplicates and nothing changes.

*Proving it*
- **A manual runbook in the README**: start the job at high speed, then check:
  - the KQL in Application Insights for events processed, skipped and lost, and the key moments published;
  - the trace from the job's send to the Cosmos DB writes;
  - the match document in Data Explorer.
- The ADR-021 season projection is **slice 06**.

**Alternatives considered**
- **Service Bus Basic with two queues.** Pennies a month, but the processor would fan out to each queue itself, with no duplicate detection or subscription filters.
- **Service Bus Standard deployed only for the demo days.** Cheaper, but more moving parts, and it could be missing when judges try it.
- **Cosmos DB free tier.** 1,000 RU/s for nothing, but only one free-tier account is allowed per subscription, and the ai-doc-intelligence account may hold it.
- **An always-on simulator container app.** Simple, but it holds a replica and fills the database with exhibition matches.
- **The simulator on your machine against Azure.** Nothing to host, but the demo would depend on the laptop.
- **One shared identity (`id-touchline-ai`) for every workload.** Fewer resources, but the Match Centre could write to Cosmos DB and the simulator could read it.
- **Keys kept as a fallback.** Easier with tools that want a connection string, but the keys exist and can be listed.
- **A random seed when none is given.** No arguments needed, but reproducing a match means finding its seed in the logs.
- **An automated CI smoke test.** It proves the pipeline on every merge, but CI would need data-plane read rights and starting a job, and every push would cost traffic.

**Rationale**
- **The broker does the fan-out**, and duplicate detection backs the idempotent design. Topics, subscriptions and filters are core Domain 3 material.
- **Serverless Cosmos DB costs fractions of a penny a match** (about 1,600 upserts, roughly 10,000 RU), and it doesn't depend on the subscription's free-tier slot.
- **A job costs nothing between runs**, reuses the image pipeline, and gives the demo a "press go" moment. Container Apps jobs are Domain 1 material.
- **Per-workload identities with entity-scoped roles, and no keys**, are the least-privilege setup the exam's Domain 4 tests. This is managed identity's first real test: the emulators never checked it (ADR-016).

**Trade-offs**
- **About £21 a month in hourly charges**: ACR about £4, Event Hubs Basic about £9 and Service Bus Standard about £8. That leaves about £9 of the £30 budget (ADR-011) for model calls and logs. October and November are both part months before the 15 November teardown, which helps.
- **Event Hubs Basic has 1-day retention and one consumer group.** A second reader of the raw stream would need Standard. Stage 3's agents read Service Bus, so this isn't needed now.
- **No keys means no quick connection-string debugging.** Every tool has to sign in with Entra ID, and Cosmos DB Data Explorer needs the data role assignment.
- **Six new resources and six new role assignments** to keep in step with the AppHost by hand (ADR-012).
- **Each run needs a fresh seed**, or it looks as if nothing happened.
- **CI can now act as the pipeline's identities** (settled at code review). Deploying the function's code (Website Contributor) or an image or execution override for the job (Container Apps Jobs Contributor) means choosing what runs as `id-touchline-processor` or `id-touchline-simulator`. So a compromised workflow on `main` could reach those identities' data:
  - writing the Cosmos DB database;
  - sending to `key-moments`;
  - sending to `match-events`.

  This widens ADR-013's blast radius, which was building images and updating one container app. Two things limit it: the federated credential still only trusts `main`, and those identities' own roles stay scoped to one entity each. Accepted, because it's the price of CI deploying code at all. The alternative is deploying by hand.

**Settled during the build**
- **Telemetry wasn't reaching Azure at all.** The service defaults only exported over OTLP, which Aspire sets up locally. They now also use the **Azure Monitor exporter** (`Azure.Monitor.OpenTelemetry.Exporter`) whenever `APPLICATIONINSIGHTS_CONNECTION_STRING` is set. It's still OpenTelemetry, with no Application Insights SDK. The Match Centre benefits too. The exporter needs OpenTelemetry 1.18, so the five OpenTelemetry packages moved from 1.17.0 to 1.18.0.
- **The processor adds `UseFunctionsWorkerDefaults()`** (`Microsoft.Azure.Functions.Worker.OpenTelemetry`), so the worker's spans join the host's invocation trace.
- **CI's job role is Container Apps Jobs Contributor, not Container Apps Contributor.** The docs confirm that Container Apps Contributor only covers `containerApps/*`. Like its app counterpart, the jobs role can list the job's secrets (the Application Insights connection string), which is the same accepted trade-off as ADR-013.
- **The simulator worker idled forever after its matches**, so a job execution would never finish. `Simulator:ExitWhenDone` (on only in the job) stops the host afterwards. A failed match also stops it, with exit code 1, so the execution shows as failed rather than hanging until its 30-minute timeout.
- **An execution override replaces the whole job template; it doesn't merge with it.** So `infra/start-match.ps1` reads the job's template, sets the seed (and optionally the speed), and starts the job with the result.
- **The function app's name reaches CI as a repository variable**, `PROCESSOR_APP_NAME`, from the deploy outputs. That's the same pattern as `ACR_NAME`.
- **Website Contributor can read the function app's settings**, which include the Application Insights connection string. Same trade-off as above.

**Settled at code review**
- **Only the host's own shutdown counts as a clean stop.** The job's failure path used to exempt every `OperationCanceledException`, and an SDK timeout is one, so a half-published match could exit 0. It now checks `stoppingToken.IsCancellationRequested` instead.
- **`start-match.ps1` refuses speeds below 4.** A match runs to about 100 minutes of clock, and the replica timeout is 30 minutes.
- **The subscriptions don't dead-letter expired messages until Stage 3.** Nothing reads them yet, so unread moments simply expire after a day rather than filling the dead-letter queues with stale "failures". Stage 3 turns it back on with its consumers.
- **Telemetry volume against the 0.1 GB daily cap (ADR-011).**
  - A match was estimated at 5–8 MB (revised to about 10 MB below, once the SDK spans were switched on).
  - The Functions host's per-invocation "Executing/Executed" logs (`Function` category) drop to Warning. That removes about 1,800 records a match, and the invocation spans still show every run.
  - The README adds a `Usage` query to check today's ingestion.
  - Sampling is the next lever if the demo days get close to the cap.
- **The runbook's trace query joins on `operation_Id`.** Only the batch span carries `match.id`, so filtering on it hid the Cosmos DB and Service Bus calls.
- **A test now covers the Azure Monitor exporter's registration**, both with and without the connection string.

**Settled after the first Azure run**

*What the run showed*
- The pipeline worked: 1,521 events processed, 5 key moments, and a rerun of the same seed came through as 1,521 duplicates.
- But the trace query showed no Cosmos DB writes and no Service Bus sends. There were only the Cosmos DB SDK's HTTP metadata calls.

*The cause*
- **The Azure SDKs' OpenTelemetry spans are still behind an experimental switch,** `Azure.Experimental.EnableActivitySource`, and nothing turned it on.
- A probe host showed both sides:
  - **Cosmos DB:** tracing was enabled and its source was listened to, yet `read_item` only produced an `Azure.Cosmos.Operation` span with the switch on.
  - **Service Bus:** Aspire's integration only adds its `Azure.Messaging.ServiceBus.*` sources if the switch is already on when it registers.
- **It was easy to miss locally.** The emulator runs in Gateway mode, so its reads and writes showed up as plain HTTP spans. In Azure the SDK uses Direct mode (TCP), so without the SDK's own spans the data operations were invisible.

*The fix*
- **The service defaults set the switch first,** before any Azure client registers. That's the order every `Program.cs` already uses.
- **The tests:**
  - one checks the switch is set;
  - one checks that Aspire's Service Bus client is then listened to (it fails if the switch is removed);
  - one checks that the Event Hubs sources are listened to.
- **An architecture test enforces the order** (settled at code review). Every `Program.cs` that registers an Azure client must call `AddServiceDefaults` first, so a new service can't quietly lose its SDK spans. Setting the switch from a module initializer wouldn't help, because the service defaults' assembly may not be loaded yet when a client registers.

*What it corrects*
- **ADR-023 claimed one trace from the producer's send to the Cosmos DB writes.** Event Hubs differs from Service Bus here: Aspire listens to its sources whether or not the switch is on. What the switch decides is whether the SDK creates the spans at all, which is the Azure.Core gate the Cosmos DB probe showed. Without them, no trace context is written into the messages.
- **That last step isn't verified locally** (settled at code review). A model-factory batch isn't instrumented, so only a real hub shows it. The next Azure run confirms it: Event Hubs send spans from `job-simulator`, with the processor's batches as their children.

*Telemetry volume*
- The new spans add one per Cosmos DB operation, plus the Event Hubs sends with a span per event (about 1,600 a match), the Service Bus sends and the token requests. A match is now estimated at about 10 MB.
- **This needs measuring** (settled at code review). The next run's `Usage` query gives the real figure. If a match is over about 15 MB (six demo runs against the cap), the first thing to cut is the per-event Event Hubs spans. They're the bulk, and the processor's spans already identify each batch.

**Settled after the second Azure run: sampling and service names**
- **What the run showed:** Cosmos DB and Service Bus spans arrived, every call succeeded, and one batch's two key moments went in a single send. But of seed 171's 1,667 events, only 323 Event Hubs sends and 425 per-event spans arrived. The simulator also reported as `unknown_service:dotnet`.
- **The cause: the Azure Monitor exporter samples by default.** It uses a `RateLimitedSampler` at 5 new traces a second.
  - Each simulator send and each per-event span starts its own trace, about 748 over the 130-second run. That's the limit, and the rest were dropped.
  - The processor wasn't capped, because its spans continue the Functions host's trace, and the sampler keeps the parent's decision.
  - Metrics are never sampled, so the counters were exact throughout.
- **Decision: keep every trace.** The service defaults set `TracesPerSecond = null` and `SamplingRatio = 1.0`, so the demo's traces are complete and the runbook's span counts are exact.
  - *Alternatives:* raise the rate limit (say 25 a second, which covers a match at 60× but still drops silently when replays run faster), or keep the default (the cheapest, but producer-side counts are only a sample).
  - *Trade-off:* the simulator adds about 3,000 spans a match, so a match is likely above the 10 MB estimate. It's measured with the README's ingestion query, and the per-event Event Hubs spans remain the first cut if it's over 15 MB.
- **The job sets `OTEL_SERVICE_NAME=simulator`**, the same as its Aspire resource name. Only the job sets it: the Event Processor already reports under the function app's name, and naming its worker separately would split one invocation across two roles.
- **Still to check:** whether each processor batch links back to the simulator's sends. The Event Hubs trigger takes a batch, so expect span links rather than one parent trace. That would correct ADR-023's "one trace from the producer's send".

**Settled after the third Azure run: the size of a match**
- **Measured:** a full match with every trace kept (1,627 events, 1,445 batches, at 60×) came to **about 17 MB**:

  | Table | MB |
  |---|---|
  | dependencies | 11.4 |
  | traces | 3.3 |
  | requests | 1.8 |
  | customMetrics | 0.4 |

  That's over the 15 MB threshold above, so about five matches a day against the 100 MB cap. The simulator's spans arrived complete: 1,627 per-event `Message` spans and 1,622 sends, under the role `simulator`.
- **The cheaper cut was logs, not spans.** Nearly all of `traces` was the Event Hubs SDK's own Information logs: two lines per send (3,244 rows, 3.2 MB) recording what the spans already show. `Azure.Identity` token logs added a little more.
- **Decision: the service defaults raise the `Azure` log categories to Warning.** SDK warnings and errors still arrive, and a more specific category in configuration can lower it again for a debugging session.
  - That brings a match to about 13.5 MB, about seven a day. The per-event Event Hubs spans stay, so demo traces remain complete.
  - Tests check that Event Hubs and Identity Information logs are dropped, that their warnings are kept, and that the services' own Information logs (kick-off, full time) still arrive.
- **The README's ingestion query names its tables.** `union *` also reads tables without `_BilledSize`, which shows as an error in the portal.

---

## ADR-025 — The season projection: player stats, suspensions and penalty history from the change feed

**Status:** Accepted · 1st half, slice 06 (builds ADR-021's season half)

**Decision**

*The league's rules*
- **Five yellow cards in a season means a one-match ban. Any red card means a one-match ban.**
- **Bans count by round number.** A player's availability for round R comes only from fixtures in rounds before R, so a ban earned in round 3 is served in round 4, whatever order the jobs actually run in.
- **Only `Fixture` matches count** (ADR-020). Replays and exhibitions never touch the season.
- With two clubs, every round is the same pairing, so a ban bites in the very next match.

*The trigger: a second function, on the change feed*
- **`ProjectSeason`** is a Cosmos DB-triggered function in the Event Processor app, on the **`matches`** container, with a **`leases`** container for its change feed checkpoints.
- **It acts only on a finished fixture.** The `matches` document changes every batch, about 1,400 times a match. But the change feed delivers only the latest version of each changed document per poll (every 5 seconds by default), so those changes coalesce into a few dozen deliveries. The function ignores any that isn't a `Fixture` at full time.
- **`MatchState` learns when a match is over.** Full time is the second half's `PeriodEnd`, and `MatchState` doesn't record it yet. A domain change, with tests.
- **The connection is identity-based,** like the Event Hubs trigger's. The processor's existing data role on the `touchline` database covers the new containers, including `leases`.

*The documents*
- **`player-matches`, partitioned by `/playerId`.**
  - One document per player per fixture, ID `{matchId}:{playerId}`, written with an upsert, so a redelivered or re-projected fixture overwrites (ADR-021).
  - It holds the player's match: appearance, goals, assists, shots and xG, passes completed and attempted, fouls, and cards.
  - It also holds each penalty the player took, with the goal-mouth position, the outcome and the keeper's dive.
  - It's built from that match's stored events (one single-partition query on `events`).
- **`player-seasons`, partitioned by `/season`.**
  - One document per player per season: the totals, the yellow-card count, and the rounds they're banned for.
  - It's **rebuilt, never incremented**: after a fixture, each player in it gets their season recomputed by summing all their `player-matches` documents for that season, which is a single-partition query. So a redelivered fixture changes nothing.
- **A taker's last five penalties** is a single-partition query on `player-matches`, newest first. A keeper's dives are read from the same penalty records.

*Kick-off*
- **Before a fixture, the simulator worker reads the season's `player-seasons` partition** and passes the players banned for that round to the engine. The engine stays pure: `MatchOptions` gains the unavailable players, and `Lineup` picks the eleven from the rest of the squad.
- **`id-touchline-simulator` gets Cosmos DB Built-in Data Reader** on the `player-seasons` container only. That's its first database access, read-only and scoped to one container.

*Not in this slice*
- **The profiles move to Cosmos DB in a later slice** (ADR-021's profile half). Profiles stay embedded JSON until then.
- **No Match Centre API yet.** It's proven by tests and Data Explorer. A read endpoint comes with the fan UI, when the screens decide its shape.

**Alternatives considered**
- **Realistic rules** (10 yellows is 2 more matches, two yellows is 1, a straight red is 3). Richer for commentary, but more rules to test and explain.
- **Reds only.** The smallest version, but it loses the five-yellow tension.
- **Projecting inside `MatchEventProcessor` at full time.** No new trigger, but season logic would sit on the hot ingest path, and a failure would retry the whole batch.
- **Summing at read time**, with no season documents. Nothing can drift, but every read, including the kick-off check, becomes a query over the season.
- **A separate `penalties` container.** "Last five" would be a direct query, at the cost of a third container to keep in step.
- **Strict round order**, where the simulator refuses anything but the next round. Out-of-order runs become impossible, but a failed round blocks the season.
- **Passing bans in through `start-match.ps1`.** The simulator would need no database access, but whoever starts the job would decide who plays.

**Rationale**
- **The change feed is the natural fit for a materialised view** (ADR-021). The ingest path stays as it is, and a projection failure only retries the projection. The change feed, leases and Cosmos DB triggers are core AI-200 Domain 2 material.
- **Rebuilding rather than incrementing** makes at-least-once delivery harmless here too.
- **Partitioning by player and by season** makes every read this slice needs a single-partition query: a player's season, their penalties, and a round's bans.

**Trade-offs**
- **The trigger still runs many times a match for one useful change**: about one delivery per poll (5 seconds) while a match is being processed. That's about 30 a match at 60× and about 110 at 10×, each reading one document and returning. It's cheap, but visible in the telemetry (corrected at code review: an earlier draft said 1,400).
- **The change feed only delivers a document's latest version.** That's exactly what's wanted here (the finished state), but the projection can never react to intermediate states, and a version overwritten between polls is never seen at all.
- **A round can't be replayed as a fixture.** The simulator refuses a fixture for a round already played (re-running one is what a replay is for, ADR-020), so bans for later rounds never shift under matches already played.
- **Two more containers, a leases container, a new trigger and a new role assignment** to keep in step between the AppHost and Bicep (ADR-012).

**Settled during the build**
- **A fixture's match ID now includes its season and round:** `nbk-tvu-2026-27-r3-42`. Before, a fixture's ID came from the seed alone, the same as an exhibition's. So a fixture played with an already used seed found its `matches` document full, treated every event as a duplicate, and was never projected; worse, it kept the exhibition's metadata. Exhibitions keep `nbk-tvu-42`.
- **"Every fifth yellow" bans, not just the fifth:** the 5th, 10th and 15th yellow cards each earn one ban. A red and a fifth yellow in the same fixture are served in consecutive rounds.
- **A player's last five penalties live on their season document** (`recentPenaltiesTaken`), along with a keeper's last five faced (`recentPenaltiesFaced`), newest first. That makes "last five" one point read instead of a query. The full history stays in `player-matches`.
- **The penalty's keeper is the other side's first-named starter.** Team sheets list the goalkeeper first, and there are no substitutions yet.
- **Only starters get documents.** Without substitutions, every appearance is a start. The bench comes with the substitutions slice.
- **The trigger binds the change feed batch as a JSON string,** read with `DomainJson`, like every other stored document. The Functions worker's own serialiser would know nothing of the domain's converters.
- **The `leases` container is created by Bicep and the AppHost, partitioned by `/id`.** The trigger can't create it, because data roles can't create containers.
- **The simulator's reader role is scoped to `/dbs/touchline/colls/player-seasons`.** It can't read events, matches or player documents.
- **`start-match.ps1` gains `-Season` and `-Round`** (both or neither), which make the match a fixture.
- **A team needs eleven available players.** With 17 in a squad, bans can never get there, but the engine checks rather than fielding fewer.

**Settled at code review**
- **A round is played once.** Season documents record the rounds each player started (`rounds`). Before a fixture, the simulator reads the season partition once, and refuses a round that's already been played. `Rebuild` also counts each round once (the first fixture by match ID), as a defence: before, a rerun under a new seed counted twice.
- **A fixture waits for the previous round's projection.** Projection lags the last event by a batch or two and a change feed poll. So if round R−1 isn't in yet, the simulator polls every 5 seconds for up to 12 minutes (`MatchPublisher.ProjectionWait`; 2 at first, raised after the Azure run, see below), then fails clearly ("play the rounds in order") rather than kicking off with stale bans.
- **A sent-off keeper doesn't face the penalty.** If the starting goalkeeper has had a red card earlier in the match (often for the very foul), the penalty's `keeperId` is null and no keeper is credited, since whoever went in goal isn't known without substitutions.
- **An empty `Simulator:Matches` fails validation.** It cost a round trip in this slice's smoke test, where a misplaced section played nothing and still looked like success.
- **The Cosmos DB names live in the domain** (`CosmosNames`), and both services' adapters use them, so a rename can't leave the simulator reading a container the projection no longer writes. The AppHost and Bicep still spell them out.
- **The simulator's season read goes through the stream API and `DomainJson`,** like every other Cosmos DB read here, not the SDK's Newtonsoft default.
- **The projector rebuilds players' seasons four at a time,** like the store's writes, instead of 44 round trips in series.
- **The telemetry figure needs re-measuring.** The 13.5 MB a match (ADR-024) predates `ProjectSeason`. The first Azure fixture's ingestion query gives the new figure.

**Settled after the Azure run**
- **It works in Azure, keyless:** two fixtures (rounds 1 and 2) were projected through the change feed with the processor's managed identity, giving 22 `player-seasons` documents with `rounds: [1, 2]`.
- **Round 2 really did wait:** it started while round 1 was still in play, polled 18 times, and kicked off five seconds after round 1's projection landed.
- **A fixture measures about 14–15 MB.** The projection itself is about 0.2 MB of that: 147 trigger runs and 132 Cosmos DB calls for both fixtures. The rest is the match, and these fixtures simply had more batches (about 1,490) than the earlier 1,445. That's six or seven matches a day against the cap.
- **No idle cost.** The change feed's lease and feed polling doesn't appear in the telemetry at all.
- **`ProjectionWait` goes from 2 minutes to 12.** At 60×, round 2 waited about 90 of its 120 seconds. At the default 10×, a match takes about 9 minutes, so round 2 would have given up long before round 1 finished. Twelve minutes covers a whole match plus processing, well inside the job's 30-minute timeout, so rounds can be started back to back. A test checks the wait outlasts a match at the default speed.


---

## ADR-006 — Agent orchestration framework

**Status:** Accepted · 21 Oct, before Stage 3

**Question**
What hosts and orchestrates the agent squad (Orchestrator, Analyst, Commentator, Localiser, Producer)?

**Decision**
- **Microsoft Agent Framework, in process.** The agents run in our own .NET service and call Foundry models through `IChatClient`.
- **Tools are plain C# methods**, such as reading a match's state or searching its possessions.
- **An Agent Framework workflow orchestrates them,** with explicit steps rather than free-form agent chat, so each key moment takes a known, bounded path.

**Alternatives considered**
- **Foundry Agent Service, hosted.** Agents are defined in Foundry, which stores threads and calls tools server-side, orchestrated from .NET. A stronger Foundry story for the judges, but less control, harder unit testing, and one more hosted service to wire up in six days.
- **Semantic Kernel agents.** Continuity with ai-doc-intelligence ADR-001 and a mature package, but Microsoft now directs new agent work to Agent Framework, its successor.

**Rationale**
- **Agent Framework is the successor** to both Semantic Kernel and AutoGen, from the same teams, with workflows built for multi-agent orchestration. It fits the "Best Multi-Agent" target category.
- **In process, every agent and tool can be unit tested with a fake `IChatClient`.** CLAUDE.md requires that model calls are never live in unit tests.
- **Explicit workflows bound cost.** Each step has a known model and token cap (ADR-007).

**Trade-offs**
- **The Foundry package (`Microsoft.Agents.AI.Foundry`) still installs as a prerelease** (checked 21 Oct). APIs may shift, so versions are pinned centrally.
- **Agent state lives in our service**, not in Foundry threads. Anything that must survive a restart goes in Cosmos DB.

---

## ADR-007 — Which model each agent uses

**Status:** Accepted · 21 Oct, before Stage 3

**Question**
Which model does each agent use, and what are the token limits?

**Decision**
- **Split by job:**
  - **A small, fast model** (initially `gpt-5.4-mini`) for the jobs that run many times a match: the Commentator's live lines and the Localiser's translations.
  - **A larger model** (initially `gpt-5.5`) for the jobs that run a few times: the Analyst's "why did that happen?" and the Producer's full-time recap.
- **Both are Global Standard deployments** in the existing Foundry account in Sweden Central (checked available, 21 Oct). The exact versions are confirmed against the pricing page when the deployment slice runs.
- **Every call has a configurable maximum token limit** (CLAUDE.md), set per agent in configuration, and counted on an OpenTelemetry counter.
- **Embeddings stay `text-embedding-3-small`** (already deployed).

**Alternatives considered**
- **One small model for everything.** The cheapest and fastest, and one deployment, but the analysis and the recap may read shallow, which the judges will notice.
- **One large model for everything.** The best quality, but commentary runs on every key moment, so it's the highest cost and latency.

**Rationale**
Cost and latency follow call volume, and quality matters most where calls are few. A match has about 30 key moments but one recap, so the expensive model goes where it's called rarely and read closely.

**Trade-offs**
- **Two deployments to keep in step** in Bicep, configuration and the AppHost.
- **The model names will date.** They're configuration, not code, so changing a model is a redeploy, not a rebuild.

**As built (lane D slice 6, 24 Oct):** `infra/main.bicep` deploys `gpt-5.4-mini` version `2026-03-17` and `gpt-5.5` version `2026-04-24` (the Global Standard versions listed on Microsoft Learn on 24 Oct), each named after its model, with `versionUpgradeOption: OnceCurrentVersionExpired` so the model doesn't change under the judges. Capacity (tokens per minute) and the `deploySquadModels` switch are ADR-051. Both share the `touchline-squad` content filter: Microsoft.DefaultV2's categories at Medium, with Prompt Shields (Jailbreak and Indirect Attack) blocking on the prompt (ADR-044). The processor identity gets Cognitive Services OpenAI User on the account, as the Match Centre identity already had, and, while `deploySquadModels` is on, both apps get `Squad__FoundryEndpoint` (`https://<account>.openai.azure.com/`) and `Squad__Deployments__*`, with no keys. The recap trigger gets its own connection, `servicebusrecaps`, and the `commentary` subscription gets the receiver role it was missing.

---

## ADR-008 — How to chunk a match timeline for retrieval

**Status:** Accepted · 21 Oct, before Stage 3

**Question**
What is one "document" in the AI Search index?

**Decision**
- **One document per possession sequence:** from a team winning the ball to losing it, using the events' existing `possessionId`. The ID is `{matchId}:{possessionId}`.
- **Each document holds:**
  - the match, the team and the time span;
  - the players involved;
  - whether it ended in a shot or a key moment;
  - a short text summary of the move, for embedding with `text-embedding-3-small` and for full-text search.
- **The summary has a length cap,** because sequences vary from 2 events to 30 or more.
- **The moment indexer slice decides how documents get in** (from the stored events, after each possession ends).

**Alternatives considered**
- **Fixed time windows** (say 30 seconds). Simple sizes and complete coverage, but a window can cut a move in half, so retrieval often returns half a story.
- **Windows anchored on key moments** (the 60 seconds before each one). Built for "why did that happen?", but play that led to nothing is never indexed.

**Rationale**
A possession is football's natural unit of play. "Show me how that goal was built" retrieves one complete document, and so does "how did they keep the ball in the first half?". It's the timeline equivalent of chunking a document at its headings, not at a fixed character count (ai-doc-intelligence).

**Trade-offs**
- **Uneven lengths:** one long possession can dominate a summary's word budget, which is why the cap.
- **About 150–250 documents a match.** That's small, but a season adds up, and it all goes in the Basic tier's limits (ADR-026).

---

## ADR-026 — Azure AI Search Basic for retrieval, and a £60 monthly budget

**Status:** Accepted · 21 Oct, before Stage 3 (supersedes ADR-011's £30 budget)

**Decision**
- **Retrieval runs on Azure AI Search, Basic tier,** in `rg-touchline-ai`. It uses hybrid search (vector plus keyword) and semantic ranking, over the possession documents (ADR-008).
- **The monthly budget alert rises from £30 to £60** (ADR-011), in Bicep, so it still warns before anything runs away.
- **Search runs from the indexer slice through judging, to the 15 November teardown,** with its teardown date in the resource ledger.

**Alternatives considered**
- **Cosmos DB for NoSQL hybrid search** (vector plus BM25 full-text, combined with RRF), in the existing serverless account. No new fixed cost, but no semantic ranker (only in preview), and the exam's AI Search topics would be studied rather than built.
- **Azure AI Search Free.** No cost, but only if the subscription's one free service isn't already taken by ai-doc-intelligence, and it has no semantic ranker.
- **Search only for build and judging,** deleted afterwards. A few pounds cheaper at the end, but the app breaks if a judge looks late.

**Rationale**
- **Azure AI Search is the architecture's named retrieval service** (README), and the AI-200 exam's main retrieval topic: indexes, vectorisation, hybrid queries and semantic ranking.
- **Building it beats reading about it.** The extra spend is about £50 over the whole project.

**Trade-offs**
- **About £2 a day, billed hourly:** roughly £40–£50 a month in total with the other fixed charges. October goes over the old £30 line.
- **The budget is an alert, not a cap** (ADR-011), so the teardown date in the ledger is the real control.

---

## ADR-027 — The Match Centre API contract, defined before the agents exist

**Status:** Accepted · 22 Oct, start of Stage 3 (unblocks the UI lane)

**Question**
What does the Match Centre API look like, and how is that contract fixed so the UI can be built in parallel with the agents, the search index and the live feed that will sit behind it?

**Draft contract (v1)**

Everything is under `/api`, JSON in camelCase with enums as strings (as `DomainJson`). Errors are RFC 7807 problem details. Every response that carries match data says `"dataSource": "synthetic"`.

| Endpoint | Returns | Backed by |
|---|---|---|
| `GET /api/status` | Service name and data source (exists) | Match Centre |
| `GET /api/teams` | Team summaries: ID, name, short name, colours | Team profiles |
| `GET /api/teams/{teamId}` | A profile with its roster (ID, name, shirt number, position), for the favourite club and player pickers | Team profiles |
| `GET /api/matches?status=scheduled|live|fulltime&season=` | Match summaries: teams, score, clock, status, kick-off, kind, round | `matches` container |
| `GET /api/matches/{matchId}` | The match: metadata (line-ups, venue, referee, season, round) and state (score, clock, team stats, full time) | `matches` container |
| `GET /api/matches/{matchId}/events?after={sequence}&limit=` | A page of match events, for the timeline and the pitch view | `events` container |
| `GET /api/matches/{matchId}/momentum` | Momentum readings in five-minute windows (skill shot 2) | Domain, over the events |
| `GET /api/matches/{matchId}/moments` | Key and notable moments, each with its commentary line for the viewer | Moments, Commentator |
| `GET /api/matches/{matchId}/stream` | Server-sent events, live (below) | Live feed |
| `POST /api/matches/{matchId}/questions` | Fan Q&A: `{ question }` in, `{ text, citations[] }` out, citations naming possessions and moments | Analyst and AI Search |
| `GET /api/matches/{matchId}/recap` | The Producer's full-time recap; `409` before full time | Producer |
| `GET /api/seasons/{season}/players/{playerId}` | A player's season card (ADR-025) | `player-seasons` container |

**The viewer** (viewing modes) travels as query parameters on the endpoints that produce words: `mode=analyst|casual`, `lang=en|…`, `team={teamId}`, `player={playerId}`, `metric={name}`. The service stores no profile; the browser keeps it.

**The stream** (`text/event-stream`). Each message has an `event:` name, an `id:` for `Last-Event-ID` resume, and one JSON `data:` line. A heartbeat goes out after 15 quiet seconds so idle proxies don't cut the connection.

| `event:` | Payload | When |
|---|---|---|
| `state` | Score, clock, team stats | After every event, and first on a stream started "from now" |
| `event` | One match event | Every event (the pitch view) |
| `moment` | A key or notable moment, with its tier | When detected |
| `commentary` | `{ momentId, tier, text, mode, lang }` | Template lines at once, model lines a few seconds later |
| `analysis` | The Analyst's "why it happened", with citations | Key moments only, later still |
| `milestone` | A milestone (first goal, a brace, a 50th pass, a personal-best speed) | When detected |
| `momentum` | A new momentum reading | Each closed five-minute window |
| `overlay` | `{ at, type, durationMs, payload }` timecoded cards | With the moment or milestone they illustrate |
| `fulltime` | The final state | Once |
| `heartbeat` | The server time, with no `id` | After 15 quiet seconds |

**Options for fixing the contract**
- **A. Code first, with a demo data source.** The DTOs are C# records in the Match Centre. The OpenAPI 3.1 document is generated by .NET 10's built-in `Microsoft.AspNetCore.OpenApi` and committed. Every endpoint is live on day one, serving **one recorded, seeded match** replayed at speed, so the UI builds against the running service, not a mock. CI fails if the committed OpenAPI document drifts from the code. The recorded match later doubles as the fallback when the pipeline is cold (a chaos test).
- **B. Contract first.** A hand-written `openapi.yaml` is the source of truth, a mock server (such as Prism) serves it to the UI, and the C# side is checked against it. More neutral between front and back end, but two copies to keep in step, and OpenAPI can't describe a server-sent event stream properly, so the stream would still be a Markdown table.
- **C. Shared types only.** Agree the DTOs and stream messages, and let each side stub the other. Quickest to write, but nothing fails when one side drifts.

**Decision:** A. The recorded match is real simulator output, so the UI sees realistic data from the first hour, and nothing is thrown away later.

**As built (contract slice, 22 Oct)**
- **The contract is `contracts/matchcentre.openapi.json`** (OpenAPI 3.1), generated by `Microsoft.AspNetCore.OpenApi` and served live at `/openapi/v1.json`. A unit test fails when the committed file and the code differ. `TOUCHLINE_UPDATE_SNAPSHOTS=1` regenerates it. The UI lane generates its TypeScript types from this file.
- **The stream's message types are in the document too,** added as component schemas by a document transformer, because OpenAPI can describe the stream endpoint but not the messages inside it.
- **Numbers are strictly numbers.** The web JSON defaults also accept numbers as strings, which made every integer `integer | string` in the schema. The Match Centre turns that off.
- **The status filter takes the `MatchStatus` names:** `status=scheduled|live|fulltime`, case-insensitive.
- **Heartbeats are an `event: heartbeat` message** with no `id`, not an SSE comment, because .NET's `SseItem<T>` can't write a comment. It goes out after 15 quiet seconds (`MatchCentre:Stream:HeartbeatInterval`).
- **`fulltime` carries the final `LiveState` only.** A client asks for the recap, and gets `409` before full time and `503` if the squad is down.
- **A fan's question is at most 280 characters,** checked before any model sees it. It bounds the input tokens a question can cost.
- **The demo match is seed 572,** a 3–3 with nine key moments, the first in the opening minute and a penalty and goal in stoppage time. It's committed as gzipped JSON lines (55 KB), and a test fails if the engine no longer plays exactly that match for that seed. It replays in **laps** (`demo-1`, `demo-2` and so on) on one clock that starts with the service, at 10× by default, with a minute between laps. Each lap has its own ID, so stream IDs never go backwards within a match.
- **Where the ports live.** The ports that cross services (`ISquad`, `ICommentaryStore`, `IPossessionSearch`, and the `Viewer`) are in `Touchline.Domain`, so the agent and search libraries implement them without depending on the Match Centre. The Match Centre-only ports (`IMatchReadModel`, `ILiveFeed`) live in the Match Centre.
- **One projector for both feeds.** `MatchStreamProjector` turns events into stream messages (event, state, moment, momentum, full time), so the demo replay and the change-feed reader produce identical streams.
- **Stand-ins until their lanes land.** The demo's commentary is the detector's reason, its overlay is a plain card, and its squad answers from the score and moments only, starting with "The Analyst isn't connected in this demo build". No model is called.
- **Demo seed changed (23 Oct).** The demo moved from seed 174 (a 3–2 with no key moment before 47') to seed 572 for a livelier first half.
- **Demo seed changed again (26 Oct, ADR-064).** Substitutions change every match from the hour mark, and seed 572 is no longer a 3–3. The demo moved to seed 35600: a 3–3 with ten key moments, the first at 0:36, six in the first half, a stoppage-time penalty and goal, and seven substitutions.

---

## ADR-028 — The Match Centre UI stack

**Status:** Accepted · 22 Oct, start of Stage 3

**Question**
What is the Match Centre front end built with, and where is it served from?

**Options**
- **A. React and TypeScript (Vite), built to static files served by the Match Centre.** The richest ecosystem for the pitch view, animation and overlays. `EventSource` handles the stream natively. TypeScript types are generated from ADR-027's OpenAPI document, so CI catches drift. Costs: a second language, and an npm build in CI and the Dockerfile.
- **B. Blazor WebAssembly, served by the Match Centre.** All C#, sharing the DTO records directly, so the compiler checks the contract. Costs: a several-megabyte first download that a judge will notice, and the stream needs a fetch-streaming or JavaScript interop shim.
- **C. Plain HTML, CSS and JavaScript (web components), no build step,** like the replay viewer. The lightest and quickest to start, but no types against the contract, and harder to keep tidy as it grows.

**Either way,** the UI is served from the Match Centre's own container: same origin, so no CORS, one Container App, and one URL for the judges.

**Decision:** A, React and TypeScript, for the user-experience score (20% of the judging).

---

## ADR-029 — How the Match Centre hears about live events

**Status:** Accepted · 22 Oct, start of Stage 3

**Question**
The stream (ADR-027) needs events, moments and commentary as they happen. Where does the Match Centre get them?

**Options**
- **A. The Cosmos DB change feed.** A change feed processor in the Match Centre reads `events`, `matches` and a new `commentary` container, and fans out in memory to each open stream. Cosmos DB is already the source of truth, resuming after a reconnect is a query, and the pattern is the one ADR-025 built. Latency is a poll interval (about a second) behind the processor.
- **B. Its own Event Hubs consumer group.** The lowest latency, but **Event Hubs Basic allows only the `$Default` consumer group**, which the processor holds, so this means Standard (about £10 a month more). Commentary would still need a second source.
- **C. Each open stream polls Cosmos DB.** The simplest code, but request units grow with every viewer.

**Decision:** A. It also means the agents write their output to Cosmos DB and never talk to the Match Centre directly.

---

## ADR-030 — Where the agents run

**Status:** Accepted · 22 Oct, start of Stage 3 (builds on ADR-006)

**Question**
The squad has live work (commentary on each key moment, from the Service Bus `commentary` subscription) and on-demand work (a fan's question, the recap). Which process hosts which?

**Options**
- **A. One `Touchline.Agents` library, hosted twice.** The agents, workflow and tools live in a class library. Live commentary runs as a Service Bus-triggered function in the existing processor app. Q&A and the recap run inside the Match Centre, behind ADR-027's endpoints. No new Azure resource, and both hosts already have identities.
- **B. A new agents Container App** that does all of it, with the Match Centre calling it over HTTP for Q&A. A clean service boundary with its own scaling, but a new app, identity and network hop, and one more thing to keep warm for the judges.
- **C. Everything inside the Match Centre,** including a Service Bus processor. The fewest moving parts, but the UI host then scales with model calls, and a slow model call competes with open streams.

**Decision:** A. The library is the unit the agent lane builds and tests with a fake `IChatClient`, and the hosts stay thin.

**As built (lane D slice 3, 23 Oct):** `CommentaryFunction` in the Event Processor, on `key-moments`/`commentary` (`Microsoft.Azure.Functions.Worker.Extensions.ServiceBus` 5.24.0).
- The published `KeyMoment` gains optional `homeScore` and `awayScore` (after the trigger event), so older messages still read. A message without them has an unknown score: the stored state can be later than the moment, so it isn't used, and the template and prompt leave the score out.
- The processor writes new match metadata (the line-ups) before publishing moments, and the state (the watermark) still last, so the function finds the line-ups and a failed publish is still retried.
- Rosters are each team profile's whole squad, so substitutes pass the guardrail (the team sheet has no bench). A team not in the catalogue gets an empty roster, logged once, so its lines fall back to the template.
- Messages are settled by hand. An unreadable one is dead-lettered (`UnreadableKeyMoment`). A moment whose English model lines are stored for both modes (two point reads, `ICommentaryStore.FindAsync`) is completed without a run (a redelivery). An English model line that fails to save throws, so the moment is redelivered and rerun (a second set of model calls, accepted to keep the line); a failed template or translation save completes. Any other failure throws and Service Bus redelivers.
- The key-moment message ID is the moment ID. **D6 to-do:** turn on duplicate detection on the `key-moments` topic in Bicep, so a moment published twice is dropped before two deliveries can both pay for the models.
- `maxConcurrentCalls` is 4: each moment makes up to five model calls, and the deployments' quota is the limit.
- Without `Squad:FoundryEndpoint`, both model keys get a stand-in client that always throws, options aren't validated, `SquadOptions.ModelsAvailable` is false, and every moment stores its English template lines with no model call (fallback reason `no-models`). The AppHost passes `Squad:*` settings through only when set.

**Amended (23 Oct, lane D slice 5): the recap is written by the Event Processor, not the Match Centre.** Recaps are written **once, at full time**, and stored. The Match Centre stays read-only on Cosmos DB and only reads them.
- *Why:* the web tier keeps its read-only Cosmos DB role; the cost is predictable, at most 6 large-model calls per match (en, es and fr × Casual and Analyst), however many fans open the recap; a stored recap is shared by every replica and survives restarts, which an in-memory cache wouldn't be.
- *Trigger: the change feed is the outbox.* `AnnounceFinishedMatches`, a Cosmos DB trigger on `matches` with its own lease prefix (`recaps-`), publishes `{matchId}` on the `match-finished` topic (message ID `{matchId}:finished`) for every changed document at full time. *Why:* publishing from `MatchEventProcessor` after saving the state was a dual write, and a crash between the two could never heal: the replayed batch is all duplicates, so the transition isn't seen again. Here the save itself is the event. A failed publish throws, the trigger retries with backoff, and the lease checkpoints only after the batch succeeds. The retry is **indefinite** (`ExponentialBackoffRetry(-1, 2 s, 5 min)`, review of PR #42): a Cosmos DB trigger redelivers only while its retry policy lasts, then checkpoints the lease and the announcement is lost. Because the trigger has its own lease prefix, an outage holds up only the announcements, not `ProjectSeason`. Repeats, from a retry or a later save of the same document, are dropped by the topic's duplicate detection and then by `RecapWriter` skipping recaps already stored.
- *Writer:* `RecapFunction` on `match-finished`/`recaps` runs `RecapWriter`, which builds the facts from the stored match and events (key moments found again by the detector), then runs `ProducerRecap` for each pair in turn. A pair already stored is skipped; that point read is the last thing before the model call, so an overlapping delivery doesn't pay twice. A failed model call, or two guardrail misses, stores nothing for that pair and bumps its attempt marker (`{matchId}:{mode}:{lang}:attempts` in `recaps`, create-or-replace); after 2 fallbacks the pair isn't tried again, so re-announcements stop paying for it. Store failures don't count. A match not stored at full time is abandoned until its third delivery, then dead-lettered. The worst case per run is 12 large-model calls (6 pairs, each with a guardrail retry), so `maxAutoLockRenewalDuration` is 10 minutes in `host.json`. A failed read or save is counted, the other pairs carry on, and the message is then abandoned so a redelivery fills the gap. Without Foundry it writes **nothing**, the simpler option: the Match Centre's template covers it.
- *Store:* `IRecapStore` over a new `recaps` container (`/matchId`, ID `{matchId}:{mode}:{lang}`), create-if-absent. The Match Centre identity gets Data Reader on it; the processor's database-wide Data Contributor already covers writes.
- *Reader:* for a Cosmos DB match, the recap endpoint reads the viewer's mode and language, falling back to the base language and then to `en`. With nothing stored, or the store unavailable, it serves the template recap built from the match's facts. Demo matches keep the demo squad's recap. The `409` before full time is unchanged.

---

## ADR-031 — Build your own team

**Status:** Accepted · 22 Oct, Stage 3 (extends ADR-002 and ADR-027)

**Question**
Fans want to create and edit their own clubs: name, colours, crest, manager, players, and the line-up and subs for a match. How do we allow that without sign-in, without real clubs or players slipping in, and without breaking the simulator's calibration?

**Decision**
- **Ownership by edit key, no sign-in.** Creating a team returns a random 256-bit edit key, once. The service stores only its SHA-256 hash and compares in constant time. The browser keeps the key and sends it as `Touchline-Edit-Key` to update or delete. Anyone can view a fan team and play against it. A lost key leaves the team read-only.
- **Crests come from a builder, never uploads.** A crest is a shape, an emblem from a fixed set, up to three initials and two colours, and the service renders it as SVG. The emblem set avoids motifs tied to real clubs (no cannons, cockerels, liver birds, hammers or lions). Built-in teams get a crest derived from their colours and short name.
- **Names are free text, checked on the server.** Team, venue, manager and player names must:
  - pass a format rule (letters, spaces, hyphens, apostrophes and full stops; at most 30 characters and four words), which also keeps instructions out of names that later reach prompts;
  - pass Azure AI Content Safety, in the existing Foundry account (AIServices kind), keyless. One call checks for abuse and against a **custom blocklist** of real Premier League clubs, their nicknames and stadiums, and current players' full names.

  The blocklist lives in Content Safety, never in the repo: CLAUDE.md keeps real club names out of the code, and the repo goes public. Its source list is kept in the private notes repo and loaded by a script.

  A "suggest a name" generator offers fictional names. A rejected name gets a clear reason for an IP match and a generic one for abuse.
- **Players are archetypes.** Each player has a name, a shirt number, a preferred foot and an archetype (such as poacher, deep playmaker or sweeper keeper). The archetype sets the position and the tuned ratings, so a fan team stays inside the simulator's calibrated ranges. Ratings never appear in the API.
- **Managers have a style.** A manager has a name and one style (balanced, high press, possession, counter or direct), which sets the team's tendencies (ADR-014).
- **Squad rules:** 14 to 25 players with distinct shirt numbers from 1 to 99, and at least 2 goalkeepers, 4 defenders, 3 midfielders and 3 forwards. Built-in teams are read-only.
- **Fan teams are stored in a Cosmos DB `teams` container,** partitioned by `/id`, with the built-in profiles as seed data (ADR-021). IDs start with `fan-`, so they never collide with built-ins. Player IDs are `{teamId}-p{n}`, assigned by the service and kept across updates, so a player's history follows them through a rename. Deletes are soft, with a TTL, because the change feed doesn't see deletes.
- **Updates are optimistic.** Each team has a version, sent as an `ETag`. A `PUT` with a stale `If-Match` gets a `412`.
- **Fan teams play exhibitions only** (ADR-032). They never count towards a season.

**The contract** (added to ADR-027)

| Endpoint | Does | Errors |
|---|---|---|
| `GET /api/team-builder/options` | Archetypes, manager styles, crest shapes and emblems, and the squad rules, for the builder UI | — |
| `GET /api/team-builder/names?kind=team\|player\|manager\|venue&count=` | Suggested fictional names | `400` |
| `POST /api/team-builder/names/check` | Checks up to 30 names as the fan types; each comes back allowed, or with a reason | `400` |
| `GET /api/teams?origin=builtin\|fan` | Teams, now with origin and crest URL | `400` |
| `GET /api/teams/{teamId}` | A team with its manager, crest, squad (with archetypes) and version (`ETag`) | `404` |
| `POST /api/teams` | Creates a fan team; `201` with the team and its edit key (shown once) | `400`, `429` |
| `PUT /api/teams/{teamId}` | Replaces a fan team (`Touchline-Edit-Key`, optional `If-Match`) | `400`, `403`, `404`, `412` |
| `DELETE /api/teams/{teamId}` | Deletes a fan team (soft) | `403`, `404`, `409` while it's playing |
| `GET /api/teams/{teamId}/crest.svg` | The crest as SVG, cacheable | `404` |

**Alternatives considered**
- **Sign-in with Entra External ID.** Proper ownership and a "my teams" list, but a new identity set-up, and judges would need an account to try it.
- **Browser-only teams.** The simplest server, but teams couldn't be shared or found, and vanish with the browser's data.
- **Crest uploads with Content Safety.** Content Safety catches offensive images, not trademarks, so a real crest would get through.
- **Generated names only.** Watertight, but less personal. **A model check per name** costs a call per save and can be wrong either way.
- **A points budget, or free sliders.** More control, but a budget needs its own calibration, and free sliders let a maxed-out team wreck the xG and score realism the analysis depends on.

**Trade-offs**
- **A blocklist is never complete.** It covers this season's Premier League squads and clubs, not every footballer ever. Content Safety and the format rule narrow the gap; the rest is accepted risk, with fan teams clearly labelled as fan-made.
- **An edit key is a bearer secret.** Anyone holding it can edit, and there's no recovery. It's fine for a demo league, but not for anything valuable.
- **Fan names reach prompts.** The format rule limits what fits in a name, and the agent lane passes rosters as tool results, never in system prompts.
- **New rate limits:** team creation is limited per client (10 an hour), and so are the endpoints that call a model or Content Safety (see the review fixes under ADR-032). Container Apps passes the client address in `X-Forwarded-For`, and only the rightmost address is trustworthy.

**As built (contract slice, 22 Oct)**
- The endpoints run on an in-memory demo store, and the demo name guard applies only the format rule, so the UI can build against them now. The Cosmos DB store, Content Safety with its blocklist, archetype ratings, the rate limiter's production settings and the engine's chosen line-ups are lane work (lane H in the plan).

---

## ADR-032 — Fan-started matches: line-ups, bench, and limits

**Status:** Accepted · 22 Oct, Stage 3 (builds on ADR-024 and ADR-031)

**Question**
How does a fan pick a line-up and kick off a match between any two teams, and what stops that running up cost?

**Decision**
- **`POST /api/matches` starts an exhibition.** It names the home and away teams and, for each, an optional team sheet: a starting eleven and up to 7 substitutes, all from that team's squad. Without a sheet, the engine picks its default eleven. Anyone can pick a sheet for any team: a team sheet is for one match, not an edit to the team.
- **Sheet rules:** exactly 11 distinct starters with exactly one goalkeeper, and a bench of 0 to 7 distinct players not in the eleven, every player in the squad. Bans don't apply: they're served in fixtures (ADR-025), and fan matches are exhibitions. `MatchdaySheetRules` still takes an unavailable list for when fixtures use sheets. The formation shown is derived from the eleven's positions (for example 4-4-2).
- **The full pipeline plays it.** The Match Centre writes a match request to Cosmos DB and starts the simulator job (ADR-024) with the request's ID, so fan matches get the event pipeline, the agents and commentary like any other. The Match Centre's identity needs a role that can start the job.
- **Limits.** At most one fan match is live at once, and at most 5 a day (`MatchCentre:FanMatches`; ten from 27 Oct, see the amendment below). Beyond either, the response is `429` with `Retry-After` and a plain message. Each match is about 14 MB of telemetry against the 0.1 GB daily cap (ADR-011; 0.2 GB from 27 Oct), so the daily limit also protects monitoring.
- **Double-clicks are safe.** An optional `Idempotency-Key` header returns the same match for a repeated request.
- **The bench is stored, not yet used.** The engine plays the chosen eleven. Substitutions are a backlog slice, built only if time allows; until then the UI shows the bench as named substitutes.
- **The response** is `202 Accepted`, with the match ID, its status (`Scheduled`), its stream URL and a `Location` header. The match appears in `GET /api/matches` and streams like any other.

| Endpoint | Does | Errors |
|---|---|---|
| `POST /api/matches` | Starts an exhibition between two teams, with optional team sheets | `400`, `404` (unknown team), `409` (a team plays itself), `429`, `503` (no pipeline) |

**Alternatives considered**
- **Play fan matches inside the Match Centre, without the pipeline.** Cheap and instant, but no agents or commentary on fan matches, which undercuts the multi-agent entry.
- **Queue matches on a timetable.** Predictable cost, but fans wait, which is worse for judges trying it.
- **Build substitutions now, or have no bench.** The first touches the engine, calibration and event schema in a stage that's already a day late; the second drops part of the feature.

**Trade-offs**
- **The engine needs a change** to take a chosen eleven instead of picking its own (lane H).
- **A starting role for the Match Centre** widens its permissions: scope it to the one job.
- **Five matches a day is a low ceiling.** It's configuration, and can rise if the telemetry cap does.

**Amendment (27 Oct, Tracey's call): ten a day for judging**
- **`DailyLimit` defaults to 10,** so several judges a day can each start a match. The Log Analytics daily cap rises from 0.1 to 0.2 GB with it (ADR-011), because ten matches is about 140 MB of telemetry. The extra ingestion is at most about 25p a day. Fan matches stay on (the `FanMatches` flag) for judging.

**As built (contract slice, 22 Oct)**
- The endpoint validates the request and the sheets, then answers `503` ("fan matches need the live pipeline") from the demo launcher. The job launch, the request document, the limits and the idempotency store are lane work.

**Review fixes (PR #22, 22 Oct)**
- **Abbreviations are names too.** Short names and crest initials go to the name guard as `Abbreviation`, so a crest or scoreboard can't spell a real club's initials. The blocklist gains real clubs' abbreviations. *(Replaced 25 Oct: abbreviations are no longer on the blocklist, and are checked for abuse only; see ADR-049.)*
- **Every endpoint that costs money is rate limited per client:** fan questions, recaps and name checks share a `paid-calls` policy (10 a minute), beside team creation (10 an hour). Both live under `MatchCentre:RateLimits`.
- **`If-Match` follows RFC 9110:** `*` and lists of tags are accepted; weak tags never match.
- **Malformed requests are `400`s:** a null name entry, an undefined name kind, or a match request with a missing team ID.

---

## ADR-050 — Fan teams in the engine: archetype ratings, team sheets and starting the job

**Status:** Accepted · 24 Oct, lane H (builds on ADR-031 and ADR-032)

**Question**
ADR-031 makes fan players archetypes and managers styles, and ADR-032 lets a fan pick the eleven. How does a fan team become something the seeded engine can play without leaving its calibrated ranges, how does the engine take a chosen eleven without changing any match played without one, and how does the Match Centre start that match on the pipeline without running up cost?

**Decision**
- **A static archetype table.** Each `PlayerArchetype` maps to fixed `Finishing`, `Passing`, `Discipline`, `TopSpeedKmh` and a plain-words role, and each `ManagerStyle` to fixed tendencies (ADR-014). **Every value lies within the min and max of that trait across the built-in profiles**, for players per position. A unit test recomputes those bounds from the catalog, so it follows any edit to the built-ins. Penalty aim and dive bias are null: the league average (ADR-020). Ratings never appear in the API.
- **A pure builder.** `FanTeamProfiles.Build(FanTeamSpec)` in `Touchline.Domain/Teams/Builder` turns a fan team (names, colours, venue, a manager with a style, players with an ID, name, shirt number, foot and archetype) into a `TeamProfile`, and holds it to the catalog's own checks (`TeamProfileCatalog.Problems`, shared with the embedded profiles). It also enforces the fan-team rules defensively: an ID starting `fan-`, and `SquadRules.Problems` (14 to 25 players, distinct shirt numbers from 1 to 99, the minimum per position). It knows nothing of Cosmos DB or the Match Centre, which maps its stored team onto the spec.
- **The chosen eleven goes on `MatchOptions`** as `HomeSheet` and `AwaySheet` (a `MatchdaySheet`, null by default). The engine checks a sheet with `MatchdaySheetRules` (11 distinct squad players, exactly one goalkeeper, none unavailable) and refuses a bad one with an `ArgumentException`. It plays the eleven in line order (goalkeeper, defenders, midfielders, forwards), keeping the sheet's order within each line, so set-piece and penalty takers keep their rule (the first midfielder or forward on the pitch) and the metadata's `TeamSheet` lists the keeper first. The formation is derived from that list (`MatchdaySheetRules.Formation`), as the shape model already does. The bench is stored, not played. The bench never leaves the engine: it's carried by the match request (H3) and shown from there, not from the event stream.
- **No sheet means no change.** The default 1-4-3-3 pick is untouched, and a golden test pins the SHA-256 of three seeded matches' serialised event streams, computed on `main` before this change.
- **Starting the job** (built in H3/H4): the Match Centre calls the Container Apps job Start API, passing only the match request ID (its role scoped to that one job); the job reads the request from a `match-requests` Cosmos DB container.
- **Limits** (built in H3/H4): 5 a day is an ETag-guarded day counter document; the one-live check is the latest request's status, with a lease expiry just beyond the job's replica timeout. An `Idempotency-Key` maps to a deterministic request document ID; a start that fails marks the request failed and refunds the day's slot.

**Alternatives considered**
- **Reorder the squad instead of a sheet**, so the default picker takes the chosen players first. No engine change, but the eleven is then implied by an order, the 1-4-3-3 shape is forced, and a 5-4-1 can't be expressed.
- **Copy a template built-in player's traits** for each archetype. Realistic by construction, but a fan team would carry a built-in player's quirks, and an edit to a built-in squad would quietly change every fan team.
- **A Service Bus queue the job reads.** Decouples the Match Centre from the job, but adds a namespace, a scaler and a dead-letter path to look after, for at most five matches a day.
- **A simple counter with no idempotency or refund.** Less code, but a double-click spends two of the day's five slots, and a start that fails spends one for nothing.

**Trade-offs**
- **Tables are coarse.** Every poacher is the same poacher; fans choose a type, not a player. That is the point (ADR-031), but it limits variety within a squad.
- **The bounds follow the two built-in squads.** They're narrow, so the styles differ less than their names suggest. A third built-in profile would widen them, and the test would follow. Over 150 seeded matches of one fan squad against North Bank FC:
  - **Pressing** shows clearly: High press closes down on 33% of the opponent's actions, Counter on 21%.
  - **Directness** shows clearly: Direct and Counter go long on 24% of passes, Possession on 19%.
  - **Width** (0.5 to 0.7) barely shows: a pass moves 8.2 m across the pitch under High press against 7.6 m under Counter.
  - **Shot appetite** (0.50 to 0.55) barely shows: Direct shoots about 6% more often per action than Counter.
  - **Balanced and Possession play almost alike.** They differ only in directness (0.5 against 0.4) and by 0.02 in shot appetite.
- **The metadata carries no formation field.** Consumers derive it from the eleven's positions; adding one would change every stored event, including the default path.
- **Calibration is checked for two extremes, not every team.** Over 200 seeded matches each against North Bank FC, an all-poacher high-press side and an all-defensive counter side both sit near 2.6 goals and 2.5 to 2.6 xG a match, well inside the bands. Other combinations are assumed to fall between them.

**As built (H3, 24 Oct)**
- **The request document** is `FanMatchRequest` in `Touchline.Domain.Matches`, stored with `DomainJson` in a new `match-requests` container (partition `/id`, no time to live). Its ID is also the match ID: `exh-{yyyyMMddHHmm}-{8 hex}` (`FanMatchIds`), lower-case letters, digits and hyphens, so it's a valid Cosmos DB `id` and Search key, and the prefix tells any reader it has a request without a lookup. It holds the created time, a seed (random per request unless the fan gave one), the kick-off time, a status (`Scheduled`, `Running`, `Finished`, `Failed`, with a reason of at most 200 characters and an update time), and for each side the team ID, a **snapshot** (`FanTeamSpec`, or null for a built-in team) and the optional sheet with its bench. The snapshot is the roster of record: an edit or delete mid-match changes nothing.
- **The store** (`IFanMatchRequestStore`, `CosmosFanMatchRequestStore` in `Touchline.Storage`) is shaped for the limits slice: a create reports an existing ID (for an `Idempotency-Key` mapped to a deterministic ID), a status change is one patch with a filter predicate on the expected statuses (so a refund and a finish can't both win), and `ListActiveAsync` is what the one-live check will read.
- **The launcher** (`FanMatchLauncher`) maps the stored teams onto `FanTeamSpec`, builds both profiles once to refuse an unplayable team before anything is written, writes the request, then starts the job. `IsTeamPlayingAsync` is a cross-partition query for scheduled or running requests created in the last 35 minutes (`MatchCentre:FanMatches:ActiveFor`, just beyond the job's replica timeout), so a request whose execution died without a word stops holding its teams. A failed write or start answers `Unavailable` (the calm `503`); a failed start also marks the request `Failed`. Logs carry the request ID and the exception, never the template or names.
- **Starting the job: a copy of the template.** An override replaces the job's whole template for that execution, so `ArmSimulatorJob` reads the job through ARM (`Azure.ResourceManager.AppContainers` 1.6.0, `ContainerAppJobResource.GetAsync`), copies every container (image, command, arguments, resources, variables and their secret references), adds one variable, `Simulator__RequestId`, to the `simulator` container, and calls `StartAsync(WaitUntil.Started, template)`. Nothing else is needed for request mode: `Simulator:ExitWhenDone` is already set on the job. Volume mounts and probes can't go in an execution template; the job has neither. Keyless, with the registered `TokenCredential`.
- **The job role is a custom one.** Microsoft Learn lists *Container Apps Jobs Operator* (read, start, stop), but it and *Jobs Contributor* both grant `Microsoft.App/jobs/*/action`, which includes `listSecrets`. So `main.bicep` defines "Touchline simulator job starter" with only `Microsoft.App/jobs/read` and `Microsoft.App/jobs/start/action`, assignable in the resource group and assigned to the Match Centre identity at `job-simulator`'s scope. Learn's warning still applies: a start can reference the job's secrets (here, the Application Insights connection string) from an execution template, so the role is the narrowest that works, not a harmless one. Creating a role definition needs Owner or User Access Administrator on the group.
- **The simulator** plays a request when `Simulator:RequestId` is set, and then ignores `Simulator:Matches`, so `start-match.ps1` and the configured path are unchanged. It reads the request, moves it `Scheduled` to `Running` (a second execution for the same request is refused, so a match can't be published twice), plays it with `MatchOptions { MatchId, Kind = Exhibition, KickOffUtc, HomeSheet, AwaySheet }` and the request's seed through the same publisher path, then moves it to `Finished`; any failure after the read marks it `Failed` with a short reason and fails the execution. A request ID alone is a valid configuration.
- **One roster lookup.** `IMatchTeamProfiles.ForMatchAsync(matchId)` (in `Touchline.Domain.Teams`) returns the catalogue plus a fan match's snapshots, or null when the catalogue alone will do. `FanMatchTeamProfiles` (in `Touchline.Storage`) reads a fan match's request once and caches its profiles for the life of the process (up to 64 matches), because a fan match's roster never changes; a non-fan ID needs no read. A missing request, a snapshot that won't build or a Cosmos DB outage answers null, logged and not cached, so the match degrades to unnamed players and template lines. Every name resolver asks it per match: the processor's key-moment detector, the commentary context the guardrail checks, the recap facts, the possession indexer (it replaces the Search-only `ITeamProfiles` port), and in the Match Centre the line-ups, the moments, the live hubs' detectors and shape model, and the Analyst's squads. The detectors are built once with a fixed list, so they gain `WithTeams`, which makes a per-match copy (a few dozen players to index), rather than changing their shape. Names still reach the agents as JSON data in user messages, never in a system prompt (ADR-044).
- **Identities:** Data Contributor on `match-requests` for the Match Centre and the simulator, each on that container only. The processor already holds Data Contributor on the whole database (ADR-024), which covers its reads. The Match Centre gets `MatchCentre__FanMatches__JobResourceId`.
- **Local only:** under Aspire there's no Container Apps job, so the launcher stays unconnected (`503`) unless `MatchCentre:FanMatches:JobResourceId` is set. The `match-requests` container is declared, the roster lookup is live, and a request written by hand can be played by running the simulator with `Simulator__RequestId`.
- **Not built yet (H4):** one live at once, five a day, the `Idempotency-Key` (the header is still accepted and ignored) and refunds. A scheduled match isn't listed by `GET /api/matches` until its first event is stored.

**As built: review fixes (H3, 24 Oct)**
- **Off by default.** `main.bicep` gains `deployFanMatches` (default `false`): the Match Centre gets `MatchCentre__FanMatches__JobResourceId` only while it's on, so a merge to `main` before the limits slice can't open unlimited, billed job executions. After H4 it's the kill switch. The `match-requests` container and the job role are deployed either way.
- **The window before kick-off.** A fan match's first event lands 30 to 60 seconds after the `202`. `FanMatchReadModel` wraps the Cosmos DB read side (behind the existing router): when no match is stored for a fan match ID and its request is scheduled or running, `GET /api/matches/{id}` answers the match as `Scheduled`, with the teams, venue, referee (`RefereeCatalog.ForSeed`, the engine's own pick) and the line-ups the engine will play, from one shared rule (`StartingElevens.For`, which the engine's `Lineup` now calls), and its events, moments and momentum are empty. The stream needed nothing new: its hub loads an empty history and the change feed hands it the events, with heartbeats in between. The match page shows "Kick-off in a moment…" until the first state message.
- **A match that didn't start is gone.** A failed request answers `410` with type `.../match-not-started` on the match, its events, moments, momentum and stream; the page says calmly that the match didn't start and links back to kick-off.
- **A pick-up deadline.** A start that ARM accepts but whose execution never runs used to hold its teams for 35 minutes, which would block every fan match once the one-live check exists. Now a request still `Scheduled` after `MatchCentre:FanMatches:ScheduledFor` (3 minutes, beyond the job's cold start) no longer counts as active (`FanMatchRequest.LivenessAt`) and is marked failed ("The match didn't start.") the next time the launcher or the read model reads it, with a conditional patch, so a late pick-up by the job wins. `Running` keeps the 35-minute window.
- **Per-match detectors are built once.** `KeyMomentDetector.ForTeams` and `NotableMomentDetector.ForTeams` memoise the detector per profiles dictionary in a `ConditionalWeakTable`. The lookup caches that dictionary per match, so every Event Hubs batch and every moments request of a fan match reuses one detector, and it goes when the lookup drops the match.
- **Accepted risk: the start permission is wide.** `Microsoft.App/jobs/start/action` lets its holder start an execution with any template: any image, the job's secrets by name, and the simulator's identity. ARM has no narrower permission. Mitigations: the Match Centre's identity holds only this custom role, and only on `job-simulator`; the job pulls from our own registry with the simulator's identity; and the override is always built from the job's own template, adding just the request ID, never from fan input.

**As built (H4, 25 Oct)**
- **Order of operations in `FanMatchLauncher`:** validate (the endpoint: teams, sheets, the key) → the `Idempotency-Key` → one live → take the day's slot → one live again → write the request → start the job → on a failure after the slot, give it back. A limit or an outage before the request is written also releases the key.
- **Five a day.** One counter document per UTC day, `day-yyyyMMdd`, in `match-requests` (its own partition), changed only by an ETag-guarded replace, or a create for the day's first match, and retried on a `412` or `409` (up to 5 times), so two launches at once each count once. `MatchCentre:FanMatches:DailyLimit` (default 5). Over it: `Busy`, `Retry-After` to the next UTC midnight, "Today's five fan matches have been played. The next slot opens at midnight UTC."
- **One live.** Any request still active by H3's windows (scheduled under 3 minutes, running under 35) answers `Busy`, with `Retry-After` the newest one's start plus `TypicalMatchLength` (10 minutes at 10x), at least 30 s, and "A fan match is already being played. Watch it, or try again in a few minutes." **The check is advisory:** two launches a moment apart can both pass it before either has written its request. The second check, after the slot is taken, narrows that window to the time between one launch's slot and its request write (a few milliseconds); a launch that loses there gives its slot back. Closing it fully would need a lease document taken before the slot. At five a day that's not worth it: the worst case is two matches live at once, still inside the day's five.
- **`Idempotency-Key`.** 1 to 100 visible ASCII characters (`!` to `~`), otherwise a `400`. A key document `idem-{first 32 hex of SHA-256(key)}` holds the request ID and when it was claimed; the key itself is never stored. The launcher creates it if it doesn't exist (a `409` means a repeat). A repeat gets the same `202` and body, spends no slot and starts no job, while the first's request is scheduled, running or finished, and also while it's still being written (a claim under a minute old with no request yet). If the first failed, or its claim is over a minute old with no request (a launch that died between the claim and the write), the key moves to the new attempt with an ETag-guarded replace and the launch goes ahead.
- **Refunds.** A failed job start, or a failed request write after the slot was taken, gives the slot back with an ETag-guarded decrement that never goes below zero, and a failed start marks the request `Failed`. A refund that can't be written is logged as an error, never thrown: the fan's answer doesn't depend on it, and at worst the day has one slot fewer. **A request settled to failed because no execution picked it up keeps its slot, deliberately:** ARM accepted that start, and a job that silently ran but lost its status patch would otherwise grant an extra match, so keeping the slot is the safer cost guard.
- **A client that goes away can't burn a slot.** The key claim and the first one-live check run under the request's token, so an abort there simply ends the launch and releases the key. From the day's slot on, the work (taking the slot, the second check, the request write, the job start, and any refund) runs under its own 30-second token, not the caller's, so a launch that has started spending either starts its match or refunds the slot, and its key either points at a real request or is released. A job start that hits that deadline is treated as failed: the slot is refunded and the request marked failed, so if ARM did start it, the job finds it failed and refuses to play it.
- **Time to live.** `match-requests` now has `defaultTtl: -1` (on, nothing expires by default), so requests stay for good while day counters (3 days) and key claims (1 day) set their own `ttl` and clear themselves. The Aspire emulator container has no default TTL, so locally they stay; they're tiny.
- **The `429`** carries the stable type `https://touchline.example/problems/fan-match-limit` and `Retry-After` in seconds. The kick-off page already reads `Retry-After` (ADR-054); it now words waits of two hours or more in hours.
- **Outages.** The counter or key store unavailable answers `Unavailable` (`503`) with nothing started and nothing written.
- **Safe to switch on.** With the limits in place, `deployFanMatches=true` is now safe. It still defaults to `false` in `main.bicep`, so turning fan matches on stays a deliberate step, and turning them off is the kill switch.

---

## ADR-033 — Match Centre UI: project layout, type generation and build

**Status:** Accepted · 22 Oct, lane A

**Decision**
- **The app lives in `src/Touchline.MatchCentre.Web`** (React, TypeScript, Vite). A `node:22` stage in the Match Centre's Dockerfile builds it, and the output goes into `/app/wwwroot`. A `.dockerignore` keeps `node_modules` and `dist` out of the ACR upload. The Dockerfile stays plain multi-stage, because ACR Tasks uses the classic builder.
- **The Match Centre serves it** with `MapStaticAssets` and `MapFallbackToFile("index.html")`, mapped after the API, so `/api` and `/openapi` keep their own 404s. In development, the Vite dev server proxies `/api` to the Match Centre.
- **Types come from `contracts/matchcentre.openapi.json`** through openapi-typescript, with openapi-fetch as a thin typed client. A separate CI `web` job runs `npm ci`, regenerates the types and fails on `git diff`, then runs the type check, lint and Vitest. The .NET jobs stay free of Node.

**Alternatives considered:** an MSBuild target that runs npm on every `dotnet publish` (every .NET build would need Node); Orval-generated hooks (a larger generated surface); a JavaScript SDK `.esproj` (more moving parts).

**As built (23 Oct, at the user's request):** the AppHost now runs the Vite dev server locally as the `matchcentre-web` resource (`Aspire.Hosting.JavaScript`, `AddViteApp`), which runs `npm install` first, passes the port as `--port`, and gives the proxy the Match Centre's endpoint through `WithReference`. So a local run no longer needs `npm run dev` by hand. Production is unchanged: the Match Centre serves the built UI from `wwwroot`. The AppHost only orchestrates Node at run time, so the .NET build and CI jobs stay Node-free.

---

## ADR-034 — Match Centre UI: look, theme toggle, routing and synthetic labelling

**Status:** Accepted · 22 Oct, lane A

**Decision**
- **The look follows the study notes' console style:** deep-navy grid, orange accent, cyan highlights, with Chakra Petch for display, IBM Plex Sans for body and JetBrains Mono for data. A matching **light theme** comes from the same design tokens.
- **The theme is a manual toggle, not the OS setting.** The choice is kept in `localStorage`, and dark is the default. Only the visual style carries over: the learning material's gadget naming never appears in the app (CLAUDE.md section 2).
- **React Router with path URLs:** `/`, `/matches/:id`, `/teams`, `/teams/:id/edit`, `/kickoff`.
- **"Synthetic" is unmissable:** a persistent "SYNTHETIC DATA" badge in the header, and `dataSource` on every match card. Crests come only from `crest.svg`. There are no real names, logos or Premier League marks.

**Alternatives considered:** a light "matchday programme" palette; a floodlit dark broadcast palette; themes that follow the OS.

---

## ADR-035 — Live view: stream client, momentum, and player positions from a shape model

**Status:** Accepted · 22 Oct, lane A (the shape model is a contract change)

**Decision**
- **One `useMatchStream` hook over `EventSource`,** with a reducer per message type. It uses `?after=` on first load and `Last-Event-ID` on reconnect. Heartbeats only reset a staleness timer: after about 30 silent seconds a "reconnecting" banner shows. `fulltime` fetches the recap, and `409`, `429` and `503` are handled gracefully.
- **Momentum is hand-rolled SVG bars** around a zero line, coloured by team, so there's no chart dependency and the palette stays in control.
- **Player dots come from a shape model on the server.** The engine tracks only the ball, so positions are *modelled*, not tracked. A pure domain function places all 22 players from their formation slots (derived from the line-up's positions), shifted by the ball's position, the phase of play (who has the ball) and each team's tendencies (width, pressing). `MatchStreamProjector` sends the result as a new `shape` stream message (22 positions, keyed by player ID) after each event. The contract gains the `shape` message and its schema. The engine and event stream don't change.
- **The pitch view** animates the ball from each event's `start` to `end` (pass arc, carry line, shot to goal), with a short fading trail and markers for fouls and cards. Player dots ease towards each new shape.

**Alternatives considered:** a shape model in TypeScript (team tendencies aren't in the contract, and the logic would sit outside the NUnit-tested C#); simulated tracking in the engine (a big engine change, recalibration, and far more Event Hubs and Cosmos DB volume five days out); ball trail only (dots were a requirement).

**Trade-offs:** positions are plausible, not real, so nothing that needs real positions (offside, pressing distances) can use them. The UI and any overlay must not present them as tracking data.

---

## ADR-036 — Which source serves a match: the recording or Cosmos DB

**Status:** Accepted · 22 Oct, lane B

**Decision**
**Matches are routed by ID.** A composite serves `demo-*` IDs from the recording and every other ID from Cosmos DB. The match list merges both, and shows the demo alone if Cosmos DB fails. The chaos test is a Cosmos DB fake that throws: the demo's list, detail and stream still work, and other IDs answer `503`. Cosmos DB stays out of the liveness check (readiness only), so an outage doesn't make Container Apps restart the app.

**Alternatives considered:** a configuration switch (no fallback when Cosmos DB is down); a switch with automatic fallback (twice the paths to test).

---

## ADR-037 — How the Match Centre reads the change feed

**Status:** Accepted · 22 Oct, lane B (implements ADR-029)

**Decision**
- **Each replica reads the change feed itself with the pull model**, `GetChangeFeedStreamIterator` from `ChangeFeedStartFrom.Now()`, on `events` and `commentary`. It keeps no leases. Every replica needs every change for its own open streams, and the Reader role is enough.
- **One shared hub per live match, per replica:** one `MatchStreamProjector` fed by the change feed, fanned out to streams over channels, with duplicates dropped by sequence. A new stream gets a state snapshot plus a query for the events after `after`, so RU cost doesn't grow with viewers.

**Alternatives considered:** a change feed processor with a lease prefix per replica (it needs write access on `leases`, and orphaned leases pile up); a processor with shared leases (⚠️ it splits partitions *between* replicas, so each stream would miss events, and it would collide with `ProjectSeason`'s leases); a replay per stream (RU cost grows with every viewer).

---

## ADR-038 — The commentary container, the Match Centre's data role, and lane E

**Status:** Accepted · 22 Oct, lane B

**Decision**
- **`commentary` is partitioned by `/matchId`, with ID `{momentId}:{mode}:{language}`.** An upsert replaces a line, as `ICommentaryStore` promises, and a match's lines are one single-partition query.
- **The Match Centre's identity gets Cosmos DB Built-in Data Reader on four containers only:** `matches`, `events`, `player-seasons` and `commentary`, as a Bicep loop in the simulator's pattern. If commentary is ever written from the Match Centre, it gets Contributor on `commentary` alone.
- **Lane E** (notable-moment tiers and overlay cards) is a separate slice after the live feed works.

**Alternatives considered:** partitioning by `/momentId` (a match's commentary becomes a cross-partition query); Reader or Contributor on the whole database (broader than needed); folding lane E into the live-feed slice (a bigger, harder review).

---

## ADR-039 — How possessions get into the search index

**Status:** Accepted · 24 Oct, lane C (builds on ADR-008 and ADR-026)

**Decision**
**Push from the Event Processor at full time.** A new function on the `match-finished` topic, with its own `search-index` subscription (the same shape as `RecapFunction`), builds the ADR-008 possession summaries, embeds them in one batch with `text-embedding-3-small`, and merges them into the index with `MergeOrUploadDocuments`. The document key is `{matchId}:{possessionId}`, so a redelivered message rewrites the same documents and the run is idempotent. The subscription is created whether or not Search is deployed, so the trigger always has something to bind to. Its `maxDeliveryCount` is 3, matching ADR-052 (consumers that call a model retry at most three times). **The trigger needs its own Service Bus connection name** (for example `servicebussearch`), with its own `__fullyQualifiedNamespace`, `__credential` and `__clientId` app settings, because every trigger must have a unique connection name.

**Alternatives considered**
- **Live, per possession.** Fans could search mid-match, but it means many small embedding calls and index writes per match, and a possession's summary isn't final until it ends.
- **An indexer with integrated vectorisation over Cosmos DB.** Less code, but it can't compose the ADR-008 summaries, which are built from several events, so it would index raw events.

---

## ADR-040 — The possession index schema and how it is queried

**Status:** Accepted · 24 Oct, lane C (builds on ADR-008, ADR-026 and ADR-039)

**Decision**
- **Schema** (`infra/search/possessions-index.json`): `id`, `matchId` (filterable), `possessionId`, `team`, `fromClock` and `toClock` with an integer `fromSeconds`, `players` (`Collection(String)`, searchable), `endedIn` (filterable), `summary` (searchable, English analyser) and `summaryVector` (1,536 dimensions, float32, HNSW with cosine, no compression). The semantic configuration uses `summary` as content and `players` as keywords.
- **Query:** embed the question client-side, then send a `VectorizedQuery` plus the keyword text, filtered by `matchId`. Hybrid results are combined with RRF and re-ranked by the **semantic ranker on the free semantic plan only**. Standard semantic billing is never switched on without sign-off.
- **Fallbacks, with a `search.fallback` counter tagged by reason:** the embedding call fails, so keyword only; the semantic ranker returns 429 or 503, so hybrid without it; Search is down, so an empty list. The whole query has a timeout of about 3 seconds, so a slow Search never stalls the Analyst.

**Alternatives considered:** vector only (misses exact names and numbers); a vectorizer inside the index (the query then needs a Search-to-Foundry identity grant and the embedding failure can't fall back to keyword); scalar or binary quantisation (a saving that doesn't matter at a few thousand documents); semantic ranker on standard billing (cost without sign-off).

---

## ADR-041 — How the apps reach Azure AI Search

**Status:** Accepted · 24 Oct, lane C (builds on ADR-024 and ADR-026)

**Decision**
- **Keyless:** the service has `disableLocalAuth: true`, so there are no admin or query keys. The Event Processor's identity gets **Search Index Data Contributor** and the Match Centre's gets **Search Index Data Reader**, both on the service. Each app reads its endpoint from `Search:Endpoint` (`Search__Endpoint` in app settings), and nothing is a secret. The processor's Foundry role (Cognitive Services OpenAI User, for embeddings) comes from lane D's model-deployments slice, not from this one.
- **The index is created by `infra/deploy.ps1 -Action create-index`** (or the same call by hand), using the signed-in developer's token, so the processor never needs Search Service Contributor. The developer gets that role in the same deploy.
- **The service is behind a `deploySearch` Bicep parameter, off by default.** Basic bills about £2 a day while it exists, so it is only created when switched on. Once it exists, `deploy.ps1` keeps it on for later deploys. Its teardown date is 15 November (ADR-026). To stop the bill early, `deploy.ps1 -Action teardown-search` deletes only the Search service, and `-NoSearch` makes a deploy leave Search (and its app settings) out even while the service exists.

**Alternatives considered:** admin and query keys in Key Vault (secrets to leak and rotate); letting the processor create the index at start-up (a management role on a runtime identity); always deploying the service (a daily cost while nothing uses it).

---

## ADR-042 — The squad's workflow for a key moment

**Status:** Accepted · 22 Oct, lane D (builds on ADR-006)

**Decision**
**Write, check, fan out.** The Commentator writes the Casual and Analyst lines in one structured call (small model). The roster guardrail checks them. Then Localisers translate concurrently, using the workflow's fan-out and fan-in edges, and every line is stored through `ICommentaryStore`. The workflow is the orchestrator. The Analyst (Q&A) and Producer (recap) have their own workflows behind `ISquad`, on the large model. Built on `Microsoft.Agents.AI.Workflows` 1.25.0 (GA), and called as a typed workflow through `CommentaryWorkflow.RunAsync`.

**Amended (23 Oct, PR #29): not exposed with `AsAIAgent()`.** `Workflow.AsAIAgent()` in Agent Framework 1.25.0 needs a chat-protocol start executor that takes `List<ChatMessage>` and a `TurnToken`, and yields its output as chat messages. The commentary workflow starts from a typed key moment and match context, builds a fresh graph for each run, and writes to the store rather than replying. Wrapping it as a chat agent would mean serialising the typed input to JSON and adding an executor that carries no logic, so it stays a typed workflow called through `RunAsync`. The Analyst and Producer, which do answer in chat, are the candidates for an agent surface (lane D slices 4 and 5).

**Alternatives considered:** the Analyst alongside the Commentator on every key moment (richer, but the large model runs about 30 times a match); the Commentator alone (a weak multi-agent story).

---

## ADR-043 — Token budgets and the model client

**Status:** Accepted · 22 Oct, lane D (builds on ADR-007)

**Decision**
- **Budgets are enforced in one middleware,** a `DelegatingChatClient` that sets `MaxOutputTokens` from `SquadOptions`, trims input over about 4k tokens, and records tokens on an OpenTelemetry counter tagged by agent and model. Output caps: Commentator 150, Localiser 200, Analyst 600, Producer 1,200. All are configurable.
- **The client is the GA `OpenAI` SDK (2.14.0)** against the Foundry account's v1 endpoint with an Entra ID token, through `.AsIChatClient()` (`Microsoft.Extensions.AI.OpenAI` 10.11.0). There are two keyed clients, `small` and `large`.

**Alternatives considered:** caps per call (easy to forget one); the `Azure.AI.OpenAI` beta (a beta pinned in a release build; the stable 2.1.0 predates gpt-5 parameters); the prerelease `Microsoft.Agents.AI.Foundry` package (its APIs are still moving).

---

## ADR-044 — The roster guardrail and prompt-injection defences

**Status:** Accepted · 22 Oct, lane D (builds on ADR-002)

**Decision**
- **On a miss, retry once, then use the template line.** If output names anyone not in the match's rosters (fan-built teams included), the agent regenerates once with a corrective note, then falls back to the template line (`CommentarySource.Template`). A fan always gets a safe line.
- **Prompt injection:** Prompt Shields switched on in the deployments' content filter. The fan's question is wrapped as delimited user data, never in a system prompt. Agents can call read-only tools only. Rosters and names always arrive as tool results. The roster check runs on output as well.

**Alternatives considered:** redacting unknown names (awkward lines); dropping the line (gaps in the feed); a separate Content Safety `shieldPrompt` call (another resource, about 100 ms and cost); role separation only (the weakest defence).

**Amendment (27 Oct, Tracey's call): the recap skips sentence openers**
- **The bug.** In Azure only one recap of six was ever the model's (Casual, English); the other five served the template. The strict check treats an unknown word opening a sentence as a possible name, and its opener lists are short and mostly English. Recap prose opens sentences with ordinary words it has no list for: `Victoria` and `Después` in Spanish, `Large` and `Ensuite` in French, `Pressing`, `Expected` and `Possession` in analyst English. Two misses per pair left the template, and the attempt marker then stopped retries.
- **The fix.** The Producer uses `RosterGuardrail.FindUnknownNamesInProse`, which skips every sentence's first word, as the translation check already does. The rest of a run is still checked, so `Marco Rossi scored` is caught by `Rossi`, and so is any unknown capitalised word mid-sentence. The Commentator, the Analyst and the indexer keep the strict check: their lines are short and often open with a name.
- **Trade-off.** An invented single surname as a sentence's first word (`Rossi scored.`) passes in a recap, and that includes the headline's first word. Matches recapped before this change keep their template pairs (their attempt markers are spent); the wipe before judging clears them. Ask the Analyst still uses the strict check, so a Spanish or French answer can fall back the same way (backlog). The prompt still restricts names to the data, the facts carry only roster names, and Prompt Shields stay on.
- **Rejected:** per-language opener lists and an analytics term list. Both are whack-a-mole, and still miss the next ordinary word the model picks.

---

## ADR-045 — How commentary is localised

**Status:** Accepted · 22 Oct, lane D

**Decision**
**A fixed set, ahead of time.** Live lines are written in English, Spanish and French, in both modes (configurable). Q&A and the recap are written in the viewer's language on demand. `ICommentaryStore` already replaces lines by moment, mode and language.

**Alternatives considered:** on demand with a cache (the first viewer of each language waits); English only (`Viewer.Language` half-used).

**Note:** the Event Processor needs `Microsoft.Azure.Functions.Worker.Extensions.ServiceBus` for the live commentary trigger (lane D, slice 3).

---

## ADR-046 — Notable-moment tier

**Status:** Accepted · 24 Oct, lane E (builds on ADR-004, ADR-038)

**Decision**
- **Notable means a yellow card, a corner, or a shot off the woodwork** that isn't already a key moment. A throw-in or a goal kick is *minor*: the detector reports it with a flag, and it produces no card and no line unless the viewer is the Analyst (the viewer filter is slice E2; in E1 the flag is only carried). Substitutions are deferred, because the simulator emits none.
- **A pure `NotableMomentDetector` sits beside `KeyMomentDetector`** with the same `Detect(event, before)` shape. A key moment beats a notable one, and one event yields at most one moment: the detector asks the key detector first.
- **Lines are templates only, with no model call,** from a small domain catalogue keyed by moment kind and language (English, Spanish and French, as ADR-045), falling back to English for any other language.
- **Lines are computed in the projector's output, not persisted.** They are deterministic, cost no RUs, and still go out when the commentary store is down. A player no squad has is never printed as an ID: the line says "a player" (`un jugador`, `un joueur`) with no club in brackets. Callers that have already run the key detector use `NotableMomentDetector.DetectAfterKey`, which doesn't ask it again; `Detect` stays the self-checking version.

**Alternatives considered:** a model line for every notable moment (about 40 more calls a match, for little value); persisting template lines through `ICommentaryStore` (RUs and a write path for text that can be recomputed, and nothing on screen if the store is down); folding notable moments into `KeyMomentKind` (they would wake the agents and the Service Bus topic, which ADR-004 exists to prevent).

**Trade-offs:** a template line reads flatter than a model line, and the catalogue is one more place that needs a reviewer's eye for the roster rule (it uses roster names only).

---

## ADR-047 — Overlay card builder

**Status:** Accepted · 24 Oct, lane E (builds on ADR-027, ADR-029)

**Decision**
- **A pure `OverlayCardBuilder`** is called by `MatchStreamProjector` for every key and notable moment. It builds a neutral card: the type is the moment kind, then a title, the team and the player. A key card shows for 6 s and a notable card for 3 s. The ID is moment-based (`{momentId}:overlay`), so a replay after a reconnect can be de-duplicated by the client.
- **This gives the Cosmos live path its toasts,** which it never had: only the demo source made cards. `DemoMatchSource` drops its private helper and uses the builder, so there's one source of truth.
- **Viewer shaping belongs to slice E2, not here:** Casual gets a plain title and no minor cards; Analyst gets a metric subtitle (xG for shots, or the favourite metric) and minor cards at low priority. It's done per subscriber in the hub, so the shared projection stays viewer-neutral.
- **No card clash, decided on the server:** the projector skips a notable moment's card when a key moment's card went out in the last 3 events (`MatchStreamProjector.NotableCardQuietEvents`, counted in events rather than clock time so every replay speed decides the same way). The notable moment still gets its moment message and its template line. A key card is never skipped.
- **One rule for the team: "the side the moment is about"** (`KeyMoment.Side`, `NotableMoment.Team`), used by `MomentView.Team` and `OverlayPayload.Team`. A goal, big chance, saved or missed penalty and a clearance off the line are about the shooting side; a penalty awarded is about the side awarded it (the opposite of the fouling side that triggers it); a card is about the carded side; a corner, throw-in or goal kick is about the side that takes it. A clearance off the line is the shooter's because the chance, the xG and the featured player are the shooter's, and a card whose team and player disagreed would be wrong on screen. The featured player is on the same side: for a penalty awarded it is the fouled player, not the fouler. This changes what `MomentView.Team` means for penalties (it was the fouling side); the doc comments and the OpenAPI document say so.
- **A seam for milestones:** when a milestone fires on the same event (the first booking, say), its card replaces the notable card. The projector builds the card in one place so that can be added later; milestones themselves are not built yet.

**Alternatives considered:** cards built in the hub per subscriber (the work repeats for every viewer, and the demo and Cosmos paths could drift); cards built by the UI from the moment message (every client re-implements durations and titles).

---

## ADR-051 — Chat model capacity, and when the models deploy

**Status:** Accepted · 24 Oct, lane D slice 6 (builds on ADR-007)

**Question**
What tokens-per-minute (TPM) limit should each Global Standard chat deployment have, and how should the deployments ship without putting the rest of the template at risk?

**Decision**
- **Small by default: 50K TPM for `gpt-5.4-mini`, 30K for `gpt-5.5`** (`smallModelCapacity` and `largeModelCapacity` in `infra/main.bicep`, `-SmallModelCapacity` and `-LargeModelCapacity` on `deploy.ps1`).
- **Raised only for demo days** (recording the video, and the judging period), with a redeploy, then **lowered again** with a redeploy that leaves the switches out:
  - raise: `./infra/deploy.ps1 -Action deploy -SmallModelCapacity <n> -LargeModelCapacity <n>`
  - lower: `./infra/deploy.ps1 -Action deploy`
  - Record both dates in the resource ledger, like any other demo-day change.
- **The models deploy behind a switch,** `deploySquadModels` (default false in the template). The content filter, both chat deployments and the `Squad__*` settings on both apps exist only when it's on. The Service Bus roles and trigger settings deploy either way, so a model quota failure can't block them. On a fresh group, run once without the models, then again with `-DeploySquadModels`.
- **Once deployed, they stay on.** `deploy.ps1` keeps `deploySquadModels` true without the switch when the Foundry account already has the small chat deployment, so a deploy that forgets `-DeploySquadModels` can't delete them before judging. A lookup that fails throws rather than turning them off. `-NoSquadModels` forces them off (it deletes them), and can't be combined with `-DeploySquadModels`.

**Rationale**
Global Standard capacity is a **rate limit, not a reservation**: tokens are billed as used, so a higher limit costs nothing at rest. What it does set is the most a runaway loop (a redelivery storm, a retry bug, a scripted fan) can spend in a minute. A low limit everywhere else caps that worst case, and the cost of a demo-day raise is only the extra tokens the demo actually uses.

**Alternatives considered**
- **Minimal all the time.** The lowest ceiling, but a busy judging session would be throttled (429s) into template lines.
- **Sized for two parallel matches all the time.** No throttling, but a runaway could spend several times faster for the whole fortnight.

**Trade-offs**
- **Two manual redeploys per demo window,** and the risk of forgetting to raise before judging or lower after. The ledger entry is the reminder.
- **The `gpt-5.5` quota depends on the subscription's quota tier,** so a raise can fail at deploy. The switch keeps that failure away from the rest of the template.

---

## ADR-052 — Retries that cost model calls

**Status:** Accepted · 24 Oct, lane D slice 6 (builds on ADR-030, ADR-044)

**Question**
A recap pair whose model call fails stores nothing and completes the message, so a short model outage at full time leaves the template recap for good. And a failing key moment is redelivered up to ten times, each delivery making up to five model calls. Which failures are worth paying to retry, and how often?

**Decision**
- **A recap pair that falls back on a transient model error abandons the message,** so Service Bus delivers it again, up to the `recaps` subscription's max delivery count (5). The pairs stored meanwhile are skipped, so a retry pays only for the missing ones. A transient failure isn't counted on the pair's attempt marker.
- **Transient** is a throttled call (429), a request timeout (408), a server error (5xx), or a timeout (`TimeoutException`, or a `TaskCanceledException` the host's cancellation token didn't ask for). `ModelFailures.Classify` in `Touchline.Agents` decides it.
- **Permanent, as before:** a guardrail miss, a content-filter block (Prompt Shields or a harm category, HTTP 400 with the code `content_filter`, or a completion whose finish reason is `content_filter`), an unreadable reply, a budget stop, and anything unrecognised. These are completed and counted on the attempt marker.
- **The `commentary` subscription's max delivery count drops from 10 to 3,** so a failing moment costs at most three runs of the workflow.
- **A content-filter block is tagged `content_filter`** on the `touchline.agents.fallbacks` counter, for every agent, so blocks can be told apart from outages.

**Alternatives considered**
- **Leave it as it is.** No extra calls, but a short outage at full time leaves the template recap for good.
- **A manual rerun command** for one match. It pays only when someone chooses to, but needs a person and an authorised trigger during judging.

**Trade-offs**
- **A longer outage costs repeated calls,** up to five deliveries of the missing pairs. Calls that fail with 429 or 5xx aren't billed for tokens, and the OpenAI SDK already retries them with backoff inside each delivery.
- **The redelivery is immediate** (an explicit abandon), so five deliveries can pass during an outage of a few minutes. Scheduled redelivery would need a send-later, which isn't worth it here.
- **A plain network failure with no HTTP status** is classed as permanent, because the decision names status codes and timeouts only.

---

## ADR-058 — Azure AI Search lives in UK South

**Status:** Accepted · 24 Oct, lane C (amends ADR-011 for Search only)

**Question**
The first deploy with `-DeploySearch` failed because Sweden Central had no capacity for new Search services (`InsufficientResourcesAvailable`). Where should the Search service go, given that every other resource reads the same `location` parameter?

**Decision**
- **A separate `searchLocation` parameter** in `infra/main.bicep`, used only by the Search service. It defaults to `uksouth`, so a direct deploy of the template avoids Sweden Central too.
- **`deploy.ps1` passes `-SearchLocation`, default `uksouth`.** Every other resource stays in Sweden Central (ADR-011).
- **Once the service exists, `deploy.ps1` keeps its live region** and warns if `-SearchLocation` differs. A Search service can't change region in place, so a mismatch would otherwise fail the whole deployment. Moving it means `-Action teardown-search` first.

**Rationale**
UK South is the closest region to the developer, and has Basic tier and the free semantic ranker plan. Search is priced per region, so check the Basic hourly rate on the pricing page and note it in the resource ledger when the service is created. Search is only reached over HTTPS with managed identity and a data role, so the region change needs no code or role changes. The endpoint is built from the service name, not the region.

**Alternatives considered**
- **Retry Sweden Central later.** No change, but capacity shortages can last days, and submission is 27 Oct.
- **Move everything to another region.** One region again, but it would recreate the Foundry account, model deployments and data stores three days before submission.
- **North Europe.** Equally good; kept as the fallback if UK South is also short.

**Trade-offs**
- **Each query crosses regions,** adding roughly 20 to 40 ms. Small next to a model call.
- **Two regions to reason about** in the cost view and the resource ledger, though the resource group and budget are unchanged.
## ADR-056 — How a possession document is cut and summarised

**Status:** Accepted · 24 Oct, lane C (builds on ADR-008, ADR-039, ADR-040 and ADR-052)

**Question**
How does the indexer turn stored match events into possession documents, and what happens when the model that writes a summary fails?

**Decision**
- **Cutting:** group the stored events by the simulator's `PossessionId`, in `Sequence` order. There is no re-segmentation. Groups with no on-ball events are skipped. The clock range and `fromSeconds` come from the group's first and last events. `players` are the distinct roster names. `endedIn` comes from the last event: a key moment (by the moment detector) if there is one, otherwise turnover, out, foul and so on.
- **Document key:** `{matchId}_{possessionId}`, not the `{matchId}:{possessionId}` written in ADR-008 and ADR-039. Azure AI Search keys allow only letters, digits, `_`, `-` and `=`, so a colon is rejected. The possession ID is a number, so the last underscore splits the key unambiguously, and a redelivered message still rewrites the same documents. `PossessionDocument.KeyFor` rejects a match ID with any other character. `PossessionHit.Id` is this key.
- **`fromSeconds`:** the clock in seconds in the first half, plus 3,600 in the second half. First-half added time can show a clock past 45:00, so a plain clock would not sort or tell the halves apart. `ToHit` reads the period back from it.
- **Summary:** a deterministic template for every possession (team, clock range, passing chain with the first 6 and last 3 players for long moves, outcome), capped at about 120 tokens. For possessions that end in a key moment, the small chat model writes the summary through the token-budget client and the roster guardrail, for at most `MaxModelSummariesPerMatch` (20) per match.
- **Summary failures never cost a redelivery.** A model error, a guardrail miss, a blown budget or the cap means that possession keeps its template text and indexing carries on. Each is counted on `search.fallback` with the reason `summary_model_failed`, `summary_guardrail`, `summary_budget` or `summary_cap`.
- **Embeddings:** one batch call per match. If it fails, the message is abandoned for redelivery (at most 3, ADR-052) and then dead-lettered. Documents are never indexed without vectors.
- **Redeliveries pay for no model calls (added in review).** The function passes the Service Bus delivery count to the writer. On any delivery after the first (for example after an embedding failure), no model summaries are written and every possession uses its template text, counted once on `search.fallback` as `summary_redelivery`. Without this, a redelivery would repeat up to 20 paid calls.
- **Model summaries run with bounded concurrency** (`MaxSummaryConcurrency`, default 4). The `MaxModelSummariesPerMatch` candidates are chosen up front, goals and penalties first, so the cap is exact; `summary_cap` is counted once per match with the number of possessions skipped.
- **The summary call has its own agent,** `SquadAgent.Indexer` on the small model, with its own output cap (`Squad:MaxOutputTokens:Indexer`, default 160), so its tokens are not billed to the Commentator.
- **Lane H integration point:** player names come from `ITeamProfiles`, whose default is the embedded catalogue. Lane H registers an implementation over the fan-built teams store before `AddPossessionIndexer` (the indexer uses `TryAdd`), and fan teams then get named players in the index.

**Alternatives considered**
- **Template only.** Cheapest and fully predictable, but the text for the moments fans ask about most is flat.
- **The model for every possession.** Richest text, but 150-250 calls a match for little gain on routine play.
- **Abandon the message on a summary model error.** Keeps all text model-written, but an outage holds up indexing for the whole match.

**Trade-offs**
- **Routine possessions read like templates,** so semantic matching leans on the key-moment ones for "why" questions.
- **Up to 20 small-model calls a match** are added to the full-time cost, bounded by the cap and the token budget.
- **`fromSeconds` is not a plain clock,** so anything reading it must go through `PossessionDocument`.

---

## ADR-057 — Where the search code lives

**Status:** Accepted · 24 Oct, lane C (builds on ADR-039, ADR-041 and ADR-056)

**Question**
Which project holds the search code, and how do the two apps that use it behave when Search isn't deployed?

**Decision**
- **One `Touchline.Search` project** holds the document model, the summariser, the embedding wiring, the indexer (write) and `AzurePossessionSearch` (read, implementing `IPossessionSearch`). The Event Processor and the Match Centre both reference it.
- **Registrations are no-ops when `Search:Endpoint` is unset** (`PossessionSearchOptions.IsConfigured`). In the Event Processor a null indexer is registered, so the `search-index` trigger still binds and completes its message. In the Match Centre no `IPossessionSearch` is registered, so the Analyst has no search tool and answers from events and moments as it does today.
- **The query side (C3) is wired into the Analyst now,** gated on that configuration, not left for integration day.

**Alternatives considered**
- **Split indexing and query into two projects.** Cleaner dependencies, but the document model and options would need a third project, for a library of a few files.
- **Wire C3 on integration day.** Avoids touching the Analyst while lane D is still moving, but leaves the retrieval path unproven until the last day.

**Trade-offs**
- **The Match Centre carries the write code** (and the processor the read code) even though each uses half. The cost is a slightly larger assembly.
- **An unconfigured Search is silent by design,** so a deploy without Search works. The cost is that a missing setting only shows up as an absent tool, so each host should log at start-up which mode it is in.
## ADR-053 — Edit keys in the browser

**Status:** Accepted · 24 Oct, lane A (builds on ADR-031)

**Question**
Creating a fan team returns its edit key once (ADR-031). Where does the UI keep it, so the fan can edit the team again, without turning a bearer secret into a leak?

**Decision**
- **`localStorage`, keyed by team ID:** `touchline.editKeys` holds `{ teamId: key }`. Reading it is storage-safe, like the viewer (ADR-027): a parse that throws, a blocked store or a malformed entry falls back to "no key", and only entries shaped like a team ID and a base64url key are kept.
- **A one-time reveal after create:** the key in a read-only field with a Copy button, and a line saying this browser remembers it (or that it couldn't, so copy it now). Leaving the page drops it from the screen for good.
- **The edit page offers "Enter an edit key"** to unlock the team on another device, and **"Forget this key on this device"**. A `403` with the title "Wrong edit key" forgets the stored key and asks for it again, keeping the fan's unsaved changes.
- **The key only ever travels in the `Touchline-Edit-Key` header.** Never in a URL, a query string, router state or a log. Links carry team IDs only (`/kickoff?home=`, `/teams/:id/edit`).
- **A stored key is pruned when its team 404s** (a deleted team's key is no use). The Teams page lists "Your teams" as the fan teams this browser holds a key for, and offers Edit only on those.

**Alternatives considered**
- **`sessionStorage`.** Gone when the tab closes, so a returning fan finds their own team read-only.
- **Show once only, never stored.** Every edit needs a paste, which most fans won't have kept.

**Trade-offs**
- **Anything with script access to the origin can read the key.** The app renders all server and fan text as text (never as HTML) and loads no third-party scripts, which keeps that surface small. A demo league's teams aren't worth more than that.
- **Clearing site data loses the key.** The reveal says to copy it, and "Enter an edit key" restores it.

**Review fixes (24 Oct)**
- **No silent deletion.** Both known 403 titles are matched explicitly ("Read-only team", "Wrong edit key"); any other 403 is a generic "couldn't verify the edit key" notice that changes nothing. A wrong key is cleared from the page's state and the enter-key panel shows, but the stored key stays until the fan presses "Forget this key on this device". This replaces the earlier rule that a wrong-key 403 forgot the stored key.
- **A pasted key is held unverified.** "Enter an edit key" keeps the key in the page's state only. It's stored after the first successful save, and never overwrites a stored key before then. A successful delete forgets the team's key either way.

---

## ADR-054 — The kick-off sheet

**Status:** Accepted · 24 Oct, lane A (builds on ADR-032)

**Question**
How does the kick-off page let a fan pick a line-up for either side without making a quick match a chore?

**Decision**
- **"Engine picks" per side, on by default.** A side left to the engine sends no sheet, and the engine picks its default eleven (ADR-032).
- **Turned off, the fan picks:** each player in the squad is Not picked, Starting or Bench, grouped by position (from `PlayerSummary.position`, because `archetype` is null for built-in players). Live counts show the eleven (exactly 11), the goalkeepers (exactly 1) and the bench (0 to 7). The formation is derived from the starters' positions (`4-4-2`). **Auto-pick** fills a valid 4-4-2 by lowest shirt number, a short position borrowing from the other outfield players, with a spare goalkeeper first on the bench.
- **The client checks the sheet before sending, and the server checks it again.** A `400` is mapped onto the side it names.
- **`POST /api/matches` sends a fresh `Idempotency-Key` per submit** (`crypto.randomUUID`), and the button is disabled while a request is in flight. `202` opens the match. **`503` is not an error:** a calm banner says fan matches go live when the match pipeline is switched on and the teams and line-up are valid, and the selections stay. `429` uses `Retry-After`; `409` says a team can't play itself; `404` says a team no longer exists.
- **Build order:** the team list, then create, then kick-off, then edit and delete, so the slice's cut is whatever has landed by the deadline.

**Alternatives considered**
- **Always pick the sheet.** Full control, but twenty-odd choices before every match.
- **Drag and drop onto a pitch.** Looks great, but it's hard to make keyboard accessible and slow to build five days out.

**Trade-offs**
- **The bench is chosen but not yet used** (ADR-032): the UI shows it as named substitutes.
- **The sheet rules live in two places**, the client check and `MatchdaySheetRules`. The server stays the authority, and its messages reach the page.
## ADR-055 — Shaping overlay cards and minor restarts for each viewer

**Status:** Accepted · 24 Oct, lane E (builds on ADR-046, ADR-047)

**Question**
ADR-047 left viewer shaping to this slice: a Casual viewer wants a plain title and no minor cards, and an Analyst wants an xG subtitle and the throw-ins and goal kicks. The projection is shared by every viewer, so where does the difference live, and how do we keep a Casual viewer from ever receiving a minor card?

**Decision**
- **Minor cards travel on a side channel.** `MatchStreamProjector` builds a minor moment's moment message and card into an internal `MinorExtras` on its `Projection`, never into the shared `Messages`. The hub stores them beside each history entry and injects them for Analyst subscribers only, in both `Broadcast` and `Replay`. A Casual subscriber is never sent a byte of them. The minor template lines are held the same way and are kept out of the commentary the hub replays to any viewer.
- **Quiet window:** a minor card neither starts nor resets the `NotableCardQuietEvents` window, and is itself skipped inside it. Its moment message and line are not skipped.
- **A `priority` on the overlay card,** optional and additive: `high` for a key moment, `normal` for a notable one and `low` for a minor one. The UI may ignore it. A minor card shows for 2 s.
- **One pure `OverlayShaping`,** used by the hub and by `DemoMatchSource`. Casual gets a friendly sentence-case title from a small map keyed by card type ("Goal!", "Big chance", "Off the woodwork"; any other type keeps the neutral title), no subtitle and no low-priority card; the Analyst keeps the broadcast-style title. Analyst gets `xG 0.34` (invariant culture) on a shot-based card; on any other card, the match xG (`xG 1.20 - 0.80`) only when the viewer's favourite metric is `xG` (case-insensitive), and nothing otherwise, so a metric is never invented. The ID, time and duration are kept so a client's de-duplication after a reconnect holds, and the neutral card is never changed.
- **Minor moments are their own tier:** `MomentTier` gains `Minor` (additive), set on the moment message and the template line of a throw-in or a goal kick, so a client can tell it from a yellow card. The Analyst's `moments` endpoint (demo and Cosmos) includes them with their template line; Casual's does not.
- **An event is written whole or not at all.** The hub builds a subscriber's full list for an event first and writes it only if the channel has room for all of it; otherwise it ends the stream before writing any of it. Every message of an event has the same ID, so a half-written event would make a client resuming after that ID lose the rest (the state, say).

**Alternatives considered**
- **A shared stream with a flag, filtered by the hub.** One projection and one history, but the minor card would sit in the shared messages and a missed filter would leak it, and it needs a new marker on the wire.
- **No priority field,** with low priority shown only through a shorter duration. No contract change, but a client can't tell a minor card from a short notable one.

**Trade-offs**
- **The hub does a little work per subscriber per event** (a new message list when the event has an overlay or a minor moment; the shared list itself otherwise). It is small beside the channel writes.
- **The match xG subtitle shows both sides' totals without names,** home first, because the card carries no team names. It is shown only to an Analyst who asked for xG.
- **A Cosmos read model change:** `GetMomentsAsync` now depends on the viewer's mode for minor moments.

---

## ADR-048 — The teams container

**Status:** Accepted · 24 Oct, lane H (builds on ADR-031)

**Question**
ADR-031 puts fan teams in a Cosmos DB `teams` container. How is it partitioned, where do the built-in teams live, how does a delete work given the change feed, and how does `If-Match` become a real concurrency check?

**Decision**
- **Partitioned by `/id`.** Every get, add, replace and delete is a point operation on one team, which is the cheapest Cosmos DB call. Listing fan teams and finding a player by ID are cross-partition queries; at demo scale on a serverless account they cost a few request units each and are fine.
- **Built-in teams are not written to Cosmos DB.** They are served from the embedded catalogue (`TeamProfileCatalog`) by the store, which merges them ahead of the fan teams. No seed step, no write on start-up, and a built-in team cannot be edited by a bug in the write path. Fan team IDs start `fan-` and a built-in ID never does, so the store knows which side to ask without a lookup.
- **Soft delete.** The container has `defaultTtl: -1` (time to live on, nothing expires by default). A delete sets `deleted: true` and a per-document `ttl` (7 days, `MatchCentre:Teams:DeletedRetention`) and replaces the document. Every read and query filters `deleted`. The change feed sees the flagged document, which it would never see for a hard delete, and Cosmos DB removes it a week later.
- **The edit key stays a plain SHA-256 hash,** compared in constant time (`EditKeys`). The key is 256 random bits, so there is no dictionary to guess from.
- **Optimistic concurrency uses the document's version and its `_etag`.** `ReplaceAsync` reads the team, returns false unless its `version` is the one the caller read, then replaces with `IfMatchEtag` set to the `_etag` it just read. A `412` from Cosmos DB, or a missing document, returns false, and the endpoint answers `412 Precondition Failed`. The version is what the API shows as the `ETag`; the `_etag` closes the gap between the read and the write.
- **`PlayerExistsAsync` is a cross-partition query** on `c.detail.squad` player IDs, skipping deleted teams. Built-in squads are checked in memory first.
- **When Cosmos DB is not configured** (tests, a plain `dotnet run`) the in-memory demo store stays, switched the same way as the read model (ADR-036). A Cosmos DB outage reads as a `503` through the existing source-unavailable handler.
- **Identity:** the Match Centre identity holds *Cosmos DB Built-in Data Contributor* on the `teams` container only; the rest stays read-only (ADR-038).

**Alternatives considered**
- **Partition by `/origin`.** Only two values (built-in, fan), so one hot partition holding every fan team, with a 20 GB logical partition cap, and no gain: lists would still fan out within it.
- **Hierarchical partition key `/origin`, `/id`.** Makes the by-origin list a single-prefix query, but it needs a more complex container for a list that is already cheap at this scale.
- **Seed the built-in teams into Cosmos DB.** One uniform read path, but a seed step to run and keep in step with the catalogue, and built-ins would become writable data.
- **A Key Vault pepper on the edit key** (an HMAC rather than a plain hash). Protects against a stolen container with a weak key, but the key is 256 random bits; it would add a secret to rotate and a Key Vault call to every write. Skipped.
- **A hard delete.** Simpler, but invisible to the change feed.

**Trade-offs**
- **Listing is a cross-partition query.** It is slow and costly only if there are thousands of teams; a rate limit of 10 creates an hour per client keeps that far away.
- **A delete leaves the document for a week,** so a deleted team's name and squad are still in the container until the TTL fires.
- **The local emulator container has no default TTL,** so a deleted team stays flagged there for good. It is hidden by every read.
- **Replace is a read then a conditional write,** two round trips, in exchange for a clean conflict.

---

## ADR-049 — The name guard fails closed

**Status:** Accepted · 24 Oct, lane H (builds on ADR-031, ADR-044)

**Question**
ADR-031 sends every fan-supplied name through Azure AI Content Safety and a blocklist of real names. What does the guard do when Content Safety cannot answer?

**Decision**
- **Order of checks:** the format rule first (`NameFormat`, `NameRules`: no Content Safety call is made for a name that already fails), then Content Safety text analysis.
- **One call per name** to `ContentSafetyClient.AnalyzeTextAsync`, with `BlocklistNames` set to the custom blocklist (`touchline-real-names`), `HaltOnBlocklistHit` on, and the four harm categories at four severity levels. A blocklist hit refuses the name with a clear reason ("looks like a real club, ground or person"); a harm score at or above `MinBlockedSeverity` (default 4, medium) refuses it with a generic one ("isn't allowed"), which says nothing about what was flagged. The blocklist lives in the Foundry account (kind `AIServices`), loaded by `infra/load-name-blocklist.ps1` from a private file, never from the repo.
- **Keyless.** `DefaultAzureCredential` (the user-assigned identity named by `AZURE_CLIENT_ID` in Azure, the developer's sign-in locally). The identity holds *Cognitive Services User* on the account: the *OpenAI User* role it already has does not cover Content Safety.
- **Fail closed.** If Content Safety throws, times out (`MatchCentre:ContentSafety:Timeout`, 5 s) or throttles, the guard throws `NameGuardUnavailableException` and the API answers **`503`** with a plain "try again" problem. This covers `POST /api/teams`, `PUT /api/teams/{id}` and `POST /api/team-builder/names/check`. Nothing is stored. Teams already stored still play, because nothing on the read or match path calls the guard.
- **Cached verdicts.** `IMemoryCache`, keyed by kind and the normalised name (trimmed, whitespace collapsed, case-folded), for `VerdictCacheTtl` (default 1 hour). Only definite verdicts, allowed or refused, are cached; a failure never is, so a recovered service is used at once.
- **Batches.** A request checks up to 30 names. Each distinct name is one call, run with bounded concurrency (`MaxConcurrency`, default 4), so one request cannot flood the account.
- **Problem types.** Each 403 and the new 503 carry a stable `type` URI (`https://touchline.example/problems/read-only-team`, `.../wrong-edit-key`, `.../name-check-unavailable`; `ProblemTypes`). The web client matches on `type`, not on the title, and keeps a generic fallback for an unknown 403.
- **Not configured,** the demo's format-only guard stays: Bicep sets the endpoint only when `deployNameGuard` is on, which is turned on after the blocklist is loaded, because with the guard on and no blocklist every create would be a `503`.
- **Fan names stay untrusted text.** They pass the guard before storage, are rendered as text and never as HTML, and no new path skips the guard. Agent prompts are unchanged in this slice.

**Alternatives considered**
- **Fail open on a Content Safety outage.** Judges could always build a team, but during an outage a real club or player name could slip into a stored team and from there into commentary. The IP rule in the hackathon terms matters more than a short outage.
- **Fail open on the format rule only,** with Content Safety advisory. Same risk, and the format rule is what keeps instructions out of names that reach prompts.
- **A local blocklist in the repo.** No outage, but it puts real names into a public repo, which CLAUDE.md forbids.
- **Batch all names in one text.** One call, but one verdict for the whole batch and no per-field reason.

**Trade-offs**
- **An outage blocks create, edit and name checks.** It is a short, honest failure with a clear message, and the demo's existing teams keep playing.
- **Blocklist changes take about five minutes to apply,** so a term added just before a demo may not be live.
- **Cached verdicts are per replica** and last an hour, so a term added to the blocklist can take that long to bite on a name checked recently.
- **Each name is a paid call.** The name check shares the `PaidCalls` rate limit, and the cache makes repeats free.
- **The Content Safety client retries once;** the timeout caps the whole call, so a request cannot hang.

**As built: review fixes (24 Oct)**
- **Names are normalised before anything else** (`NameNormaliser`): NFKD, combining marks dropped, Cyrillic and Greek look-alikes folded to Latin (upper and lower case), a few Latin letters NFKD leaves alone (ø, ł, đ, ð, ı) folded, lower-cased, apostrophes, full stops and hyphens removed, white space collapsed. The normalised text is what is sent to Content Safety and what the cache is keyed on, so "Blöcked Üníted", a Cyrillic-lettered spelling and "Block.ed Un'ited" all reach the blocklist as the plain term. The blocklist loader writes terms in the same form, each with and without spaces, because removing hyphens turns "Blocked-United" into one word.
- **The format rule refuses a name that mixes Latin letters with Cyrillic or Greek ones** (`NameRules`). A name wholly in another alphabet is still allowed; it only reaches the blocklist if folding makes it Latin.
- **The cache is keyed by normalised text alone,** not kind: the analysis doesn't depend on the kind, so a team and a player of the same text share one call.
- **A team ID collision** (Cosmos DB 409 on create) throws `TeamAlreadyExistsException`; the create endpoint retries with a fresh ID up to 3 times, then answers a 409 problem.
- **A teams outage says so:** the store passes its own `503` detail ("The teams aren't available right now") through a `GuardAsync` overload; the match wording is unchanged.
- **Residual risk:** the blocklist only catches what it lists. Spellings that change letters (a "z" for an "s", a missing letter) or non-folded scripts still pass, and Content Safety's own matching rules apply on top.

**As built: no abbreviations on the blocklist (25 Oct, Tracey's call)**
- **Real clubs' abbreviations are no longer on the blocklist,** which replaces ADR-032's review fix ("the blocklist gains real clubs' abbreviations"). Broadcasters use different codes for the same club, so there's no agreed set to block, and a three-letter list would keep refusing fans hunting for a short name. Short names and crest initials still go to the guard as `Abbreviation` and are still checked by Content Safety for abuse; only the blocklist match is gone.
- **The trade-off:** a fan team can use a real club's initials on its scoreboard or crest. The full name, nicknames and ground are still blocked, initials rarely identify a club on their own, and fan teams are labelled as fan-made.

**As built: verdicts in Redis (25 Oct, ADR-061)**
- **Verdicts are now shared across replicas and survive redeploys.** With Redis on, HybridCache keeps each verdict in Redis (`touchline:hc:name-guard|{normalised text}`) for `VerdictCacheTtl` (1 hour), and in each replica's memory for `VerdictLocalCacheTtl` (5 minutes, never more than the TTL). An entry refilled from Redis starts a fresh local lifetime, so the worst case is `VerdictCacheTtl` plus the local expiry, about 65 minutes. The keys carry no revision, on purpose: loading the blocklist doesn't make a new revision, so a revision prefix wouldn't help.
- **So after `load-name-blocklist.ps1`,** a name allowed in the last hour can stay allowed for up to that long. To make a new term bite at once, delete the `touchline:hc:*` keys in Redis, or wait the hour (plus the five minutes).

## ADR-059 — Wiping the stored matches before judging

**Status:** Accepted · 25 Oct, Tracey's call

**Question**
Every smoke test left a match in Cosmos DB, so the Match Centre's list is full of test runs. How should the stored data be cleared before the judges see it, and what goes?

**Decision**
- **Everything built from a match goes:** `matches`, `events`, `commentary`, `recaps`, the season projections (`player-matches`, `player-seasons`), the Search `possessions` index, and the change feed `leases`. The fan side goes too: `teams` (with the edit keys used in testing) and `match-requests` (rosters and the day counters). The demo match is a recording, so the list is never empty.
- **The containers are deleted and recreated, not emptied.** `deploy.ps1 -Action wipe-data` refuses while a simulator execution is running, asks for the resource group name, stops the processor, deletes the containers and the index through the control plane, redeploys (which recreates them from `main.bicep`), recreates the index, starts the processor again, and restarts the Match Centre's active revisions.
- **A test keeps the wipe list equal to `cosmosContainerDefinitions`,** so a container added later can't be missed.

**Rationale**
Deleting a container is one control-plane call that works with the developer's normal rights: nobody holds a Cosmos DB data role apart from the apps' identities, and the `az` CLI has no item deletes. Recreating from the template keeps each container's partition key and time to live as reviewed. The leases have to go with the containers, because a checkpoint into a deleted container means nothing in its replacement. The Match Centre keeps a change feed position in memory for the same reason, so it is restarted.

**Alternatives considered**
- **Delete item by item** with a small tool. Containers and leases stay, but it needs a data role for the developer, costs RUs per document and is slower.
- **Keep chosen matches.** Needs the item-by-item tool and a list of IDs; judges are better served by a clean list and one or two fresh showcase matches.
- **Partition-key delete.** A preview feature that has to be switched on per account.

**Trade-offs**
- **The app is briefly broken** between the deletes and the end of the redeploy (a few minutes). A failed redeploy leaves containers missing until `-Action wipe-data` is re-run, which deletes only what's left, redeploys and restarts the apps.
- **Fan matches can't start mid-wipe** once ADR-060's maintenance page is up, because it answers every `POST` with a 503. The live-match check runs again after it goes up, for a match started just before.
- **Fan teams built in testing are gone,** and their edit keys stop working.

## ADR-060 — A maintenance page while the data is wiped

**Status:** Accepted · 25 Oct, Tracey's call

**Question**
While `wipe-data` (ADR-059) deletes and recreates the containers, the Match Centre would serve errors and half-deleted data. What should a visitor see, and how is it switched on and off?

**Decision**
- **A flag in the app.** `MatchCentre:Maintenance:Enabled` turns on a short-circuit middleware, placed just after the exception handler. Page requests get a self-contained maintenance page. Requests to `/api` and `/openapi`, the live stream included, get a 503 problem of type `.../problems/maintenance`. Both carry `Retry-After`, `Cache-Control: no-store` and an `X-Touchline-Maintenance: on` header. `/health` and `/alive` still answer, so Container Apps keeps the replica and doesn't restart it.
- **The page** follows Tracey's layout: a header bar, a cone icon, "PITCH MAINTENANCE IN PROGRESS", an estimated kick-off (`EstimatedMinutes`, default 15) and a "RETURN TO TOUCHLINE" link. It has the synthetic-data label, uses the app's theme colours in light and dark, has no scripts or bundles, and reloads itself every `RetryAfterSeconds` (default 30).
- **`wipe-data` switches it.** Before anything is deleted, it sets the setting with `az containerapp update`, which makes a new revision that takes the traffic. It then waits until the live app answers with the header. If it doesn't (an image from before this change), the wipe stops with no data deleted. A `maintenanceMode` template parameter keeps the setting through the wipe's own redeploy. In a `finally`, the setting is removed, which makes another new revision with fresh replicas. Maintenance goes off whenever the script ends, even after a failure, as Tracey asked.
- **The wipe refuses to start when traffic is pinned** to a revision (a `judging` label, ADR-009), because a new revision would then get no traffic.

**Alternatives considered**
- **Swap traffic to a revision running a static maintenance image.** This needs no app change, but it needs a second image, and the mid-wipe redeploy would read the maintenance image as the live one and bake it into the template.
- **Scale to zero.** Visitors would get a connection error, not a page.

**Trade-offs**
- **Each switch is a new revision** and a cold start (up to about a minute). Old revisions keep no traffic and stay at zero replicas.
- **A failed wipe ends with the app up but missing containers,** answering 503s, until `wipe-data` is run again. Leaving maintenance on after a failure would hide that from visitors, but it was asked to go off when the script ends.
- **The 503 isn't in the OpenAPI document,** because it comes from middleware, not an endpoint. The README's API notes should mention it (Penalties).

**As built: open tabs follow maintenance (25 Oct, Tracey's call)**
- **A tab opened before maintenance began reloads by itself.** The typed API client's response hook reloads the page when a response carries `X-Touchline-Maintenance: on`, so the reload lands on the server's maintenance page. The live stream's `EventSource` can't see headers, so when it drops, the stream sends one `HEAD /api/matches` to ask; outside maintenance that is a cheap 405. The page reloads once at most, and the maintenance page has no scripts, so it can't loop. Only reads reload: a save (team builder, a fan question) shows its 503 and keeps the form, because reloading would throw away what the fan typed. A tab coming back into view also asks once, so pages that never poll (the recap, the team builder) switch too; a background tab never asks, so it can't wake a Match Centre that has scaled to zero. When maintenance ends, the page's own refresh brings the visitor back to the URL they were on.

## ADR-061 — A Redis cache, and what goes in it

**Status:** Accepted · 25 Oct, Tracey's call: A with 1, 2 and 3

**Question**
Stage 4 keeps Redis in scope (Tracey, 25 Oct). Which Redis service, what does it cache, and how do the apps reach it?

**Options: the service**
- **A. Azure Managed Redis, Balanced B0, high availability off.** The current service. Non-HA halves the price and has no SLA, which suits a judged demo. Entra ID sign-in through an access policy assignment (it has no Entra RBAC data roles), so the apps stay keyless.
- **B. Azure Cache for Redis Basic C0.** Familiar and cheap, but Microsoft has announced the retirement of every tier and recommends Managed Redis for new work.
- **C. No Redis in Azure.** Use Redis under Aspire locally only, with the in-memory cache deployed. Free, but the "shared across replicas" story is never shown running.

**Options: what it caches** (any mix)
- **1. Name-guard verdicts** (ADR-049) through `HybridCache`: memory first, Redis second. Fixes ADR-049's trade-off that verdicts are per replica, so every replica sees a refusal at once.
- **2. Output cache for finished matches:** the match list, summaries, moments and recaps, keyed by match and viewer. A finished match never changes, so this is safe to cache for a long time; live matches are never cached.
- **3. The fan-match daily counters and Idempotency-Keys** (ADR-050). Moves state off Cosmos DB, but these must not be lost, and a non-HA cache can lose data in maintenance. Not recommended.

**Decision**
- **Azure Managed Redis, Balanced B0, high availability off,** keyless through an Entra access policy assignment for the Match Centre's identity. Behind a `deployRedis` parameter, off by default, like `deploySearch`.
- **It caches all three:** name-guard verdicts through `HybridCache`, the output cache for finished-match reads, and the fan-match daily counters and Idempotency-Keys.
- **Counters and keys in Redis:** a counter is an `INCR` on a key per edit key and UTC day, expiring at the next UTC midnight; an Idempotency-Key is a `SET NX` with the same lifetime, holding the started match ID. The refund (ADR-050) is a `DECR`.
- **With `deployRedis` off, or Redis unreachable,** the caches fall back to memory and the counters and keys fall back to the existing Cosmos DB store, so the app keeps working and the limits still hold.

**Recommendation (as proposed)**
A with 1 and 2. Tracey added 3.

**Trade-offs**
- **It is billed by the hour** from the moment it is created, so it goes in the resource ledger with a teardown date in the same session. Check the price for B0 non-HA in Sweden Central on the pricing page before deploying.
- **Managed Redis is clustered by default.** StackExchange.Redis handles OSS clustering, but each key must live in one slot; no multi-key commands are used here.
- **A cache outage must not take the app down:** every Redis call has a short timeout, and a failure is a miss, logged.
- **Non-HA Redis can lose the counters and keys in maintenance.** Lost counters reset a fan's day (they could start up to five more matches); a lost Idempotency-Key lets a retry of the same request start a second match. Both are bounded by the one-live-match rule, which stays in Cosmos DB.
- **Two stores for the limits** while the fallback exists: a day that switches between Redis and Cosmos DB mid-way counts in each separately. Accepted for a demo.

**As built (G1, 25 Oct)**
- **One counter per UTC day, not per edit key.** The decision says "per edit key", but the Cosmos DB store it falls back to counts the whole app's day (ADR-050's five a day), so Redis does the same: `touchline:fan-matches:day-yyyyMMdd`, an atomic check-and-`INCR` in Lua against the limit, expiring at the next UTC midnight. The refund is a `DECR` that never goes below zero and keeps the expiry.
- **An Idempotency-Key lasts a day, not until midnight.** `touchline:fan-matches:idem-{32 hex of its SHA-256}`, a `SET NX` for one day, as in Cosmos DB, so a retry just after midnight is still a repeat. It holds the claim time and the request ID (`{Unix ms}:{request ID}`). Moving a claim to a new request and releasing it are Lua scripts that compare the request ID first, so one request can't overwrite or release another's. Every script touches one key, so it runs in one cluster slot.
- **The fallback** (`FallbackFanMatchLimitStore`) sends a call to Cosmos DB when Redis is disconnected, times out or refuses the command, and logs it. A bug or a cancellation is not retried there. A command that timed out after Redis ran it can be repeated in Cosmos DB and spend a slot twice; the one-live rule bounds that too. **The other direction leaks a slot:** a slot taken in Cosmos DB while Redis is down, then refunded in Redis once it's back, leaves the Redis day one too low, so one extra match can start that day.
- **What the output cache stores.** `GET /api/matches/{id}` at full time. Its moments only when the match was at full time *before* they were read, and every key moment's line is the model's in exactly the viewer's language and mode. Its recap only when it's the model's in exactly the viewer's language and mode. The read side falls back to a template, the base language or English while the right line is still being written, and none of those is cached (`es-MX` is not `es`). **If the models are never deployed, moments and recaps simply never cache.** **Demo laps are never cached:** each replica replays the recording on its own clock, so a lap that has finished on one may be live on another. The viewer's `mode`, `lang`, `team`, `player` and `metric` vary the moments and recap.
- **The match list isn't cached.** The running demo lap is always on it, so a "finished matches only" rule would never store it. There's no list policy, so nobody should expect one.
- **Lifetime.** `MatchCentre:Caching:FinishedMatchTtl` (10 minutes), shorter than "a long time" to bound memory and any late correction.
- **Memory first, Redis second, for both caches.** HybridCache's own first level, and a small in-memory level in front of the Redis output cache store (2,000 entries), so a Redis outage still leaves each replica its own cache. Output cache keys carry the Container Apps revision, so a new revision starts empty; `wipe-data` always ends on a new revision, so nothing deleted can be served from Redis afterwards. The counters and claims aren't wiped from Redis; they clear themselves within a day.
- **Middleware order:** the output cache sits after the rate limiter, so a cached recap still counts against `PaidCalls`, as before.
- **The moments cost one extra match read on a cache miss**, to know the match is over before the moments are read.
- **Connection.** One multiplexer, connected in the background at start-up: connect 3 s, commands 1 s, fail-fast while disconnected, a fresh attempt 30 s after one that failed outright (for example no Entra ID token). Locally Aspire's Redis container (`ConnectionStrings:redis`); in Azure `MatchCentre__Redis__Endpoint` with the app's identity through `Microsoft.Azure.StackExchangeRedis`. The access policy assignment uses the `default` policy, which is full data access on that one database.
- **Tests** run one contract against both limit stores. The Lua scripts were also run against a real Redis 7 container (`TOUCHLINE_TEST_REDIS`, opt-in, skipped in CI). Not yet checked against Managed Redis's cluster, and the Bicep is compiled but not deployed.

## ADR-062 — App Configuration: which settings and flags move

**Status:** Accepted · 25 Oct, Tracey's call: the recommendation (Free tier, flags and the squad's settings)

**Question**
Which settings move from `appsettings` and Bicep environment variables into Azure App Configuration, which tier, and how do the apps pick up a change?

**Options: the tier**
- **A. Free.** No charge. 1,000 requests a day for the whole store, no SLA, 10 MB. The Free tier counts every refresh check against the daily limit, so refresh intervals have to be long.
- **B. Developer.** A small daily charge, 6,000 requests an hour, 500 MB.
- **C. Standard.** About £1 a day, 30,000 requests an hour, an SLA. More than this app needs.

**Options: what moves**
- **1. Feature flags** through `Microsoft.FeatureManagement`: fan matches, fan Q&A, possession search, the squad's model calls. A judge-day switch with no redeploy and no new revision.
- **2. The squad's settings:** model deployment names, token caps, languages (ADR-043, ADR-045).
- **3. Rate limits and fan-match limits** (ADR-050).
- **Stays where it is:** endpoints and identity client IDs (Bicep knows them at deploy time), the maintenance flag (ADR-060 relies on it making a new revision), and anything secret (Key Vault).

**Recommendation**
A with 1 and 2. Keyless, with **App Configuration Data Reader** for each app's identity. One sentinel key, refreshed every 5 minutes; feature flags refreshed on the same interval. `appsettings` keeps every default, and the provider is optional at startup, so a 429 or an outage leaves the app on its last good values. Behind a `deployAppConfig` parameter, off by default.

**Trade-offs**
- **Free tier arithmetic:** the first estimate here (about 600 requests a day, at 5 minutes) was wrong, and is corrected in the as-built note below. On a 429 the provider keeps the values it has, so the cost is staleness, not an outage. Developer is the fallback if it bites.
- **Two sources of truth for a while:** a value set in both App Configuration and an environment variable resolves by provider order, which is set explicitly and tested.
- **Changing a model deployment name live** only works if the new deployment exists; the squad already falls back to template lines on a failed call.

**As built (25 Oct, slice G2)**
- **Provider order, lowest first:** `appsettings`, user secrets, App Configuration, environment variables, command line. A real host has several environment variable sources (the `DOTNET_` and `ASPNETCORE_` prefixed ones sit before the JSON files), so the provider is inserted before the last one with no prefix, and tests check this against a real `WebApplication` builder and the Functions builder. So an environment variable (Bicep, or an emergency `az containerapp update`) always beats the store. A test covers the order. It is the order in the recommendation, with no change.
- **The store, in Bicep:** `appcs-touchline-<suffix>`, Free, `disableLocalAuth: true`, behind `deployAppConfig` (off by default; `deploy.ps1 -DeployAppConfig` turns it on, keeps it on while the Match Centre has `AppConfiguration__Endpoint`, and `-NoAppConfig` turns it off). Both apps' identities get App Configuration Data Reader. The developer gets Data Owner, to flip flags in the portal.
- **Seeding from a template needs `dataPlaneProxy.authenticationMode: Pass-through`** while local auth is off (Microsoft Learn, "Deployment overview"), with the deployer holding Data Owner. Seeded values reset every time the template is deployed, which would undo a flag flipped in the portal. So `seedAppConfig` is true only when the store is first created, or with `-ReseedAppConfig`. A first deploy may need running twice if the Data Owner role is slow to apply. Only a user deploy is given Data Owner, so a service principal (CI) run creates the store but leaves it unseeded, with a warning. Free stores are limited to three per region per subscription.
- **Keys seeded:** `Touchline:Sentinel` (1); `Squad:MaxInputTokens`; `Squad:MaxOutputTokens:{Commentator,Localiser,Analyst,Producer,Indexer}`; `Squad:Languages:0..2`. They match the apps' defaults, so seeding changes nothing. **Model deployment names and the Foundry endpoint are not seeded:** Bicep sets them as environment variables, which win, and a store outage at start-up must not take them away (the options validator would stop the app). They apply only if an environment variable is absent.
- **Flags** (key-value feature flags, `application/vnd.microsoft.appconfig.ff+json`, all on): `FanMatches` (POST /api/matches, 503 when off), `FanQa` (POST .../questions, 503 when off), `PossessionSearch` (the Analyst's search tool finds nothing when off), `SquadModels` (commentary, recaps, Q&A answers and possession summaries use the template and fixed lines, as with no Foundry). The 503s reuse the existing untyped "Temporarily unavailable" problem, so the OpenAPI document didn't change. Defaults are `FeatureManagement:*: true` in each app's `appsettings.json`; a flag defined in the store beats that default. Flags are evaluated per call through `IFeatureManager`, never cached at start-up.
- **Free tier arithmetic, corrected (review fix).** Each refresh check is two requests, one for the sentinel and one for the flags, and the Free tier counts both. At 5 minutes that was 576 a day for each running instance, and the container's `/alive` and `/health` probes kept triggering checks, so two instances passed 1,000. The interval is now **15 minutes** (`AppConfiguration:RefreshInterval`, minimum 1 minute), which is 192 requests a day for each instance. The Match Centre runs up to two replicas and the processor up to two instances, so the worst case is 4 x 192 = 768, plus start-up loads (a few requests for each cold start or restart). That is inside 1,000 with little room, so a day of heavy scaling or restarts can still pass it. Over the limit the store answers 429 until 00:00 UTC. **The risk is a cold start in that window:** an instance that starts while the store is throttled loads nothing, so its flags fall back to the `true` in `appsettings`, and **a kill switch Tracey turned off in the store comes back on** for that instance. For a switch that must hold, use a hold-fast override (next bullet), or `-NoFanMatches` for fan matches. Developer tier is the fix if it bites.
- **Hold-fast overrides (tested, and a correction).** An environment variable `FeatureManagement__FanQa=false` does **not** beat a flag in the store. The store writes flags in the Microsoft schema (`feature_management:feature_flags`), and `Microsoft.FeatureManagement` 4.8.0 prefers that over the older `FeatureManagement` section whatever the source order, so the store's `true` wins (a test shows it). The `FeatureManagement:*` defaults in `appsettings` only work because the store says nothing about that flag. What does hold: the setting **`Touchline:Kill:<Flag>`** set to `true`, as the environment variable `Touchline__Kill__<Flag>`. A wrapper around `IFeatureManager` reads it first and answers "off" for that flag, for all four (`FanMatches`, `FanQa`, `PossessionSearch`, `SquadModels`), whatever the store holds, and even when the store is down or throttled. One command sets it, for example `az containerapp update -n ca-matchcentre -g rg-touchline-ai --set-env-vars Touchline__Kill__FanQa=true` (the processor takes the same setting as an app setting for `SquadModels`). It makes a new revision, which is the point: it can't revert. Remove it the same way (`--remove-env-vars`). `deploy.ps1 -NoFanMatches` is the other hold-fast switch for fan matches: it removes the job's resource ID, so POST /api/matches answers 503 with no flag involved.
- **Only API calls trigger a refresh in the Match Centre.** The middleware runs for `/api` paths other than a match's `/stream`, through `UseWhen`, so the probes, static files and the stream don't.
- **Bad stored values are contained.** A value that won't bind (`Squad:MaxInputTokens` = `abc`) or fails validation is caught for any exception, logged, and the last good options stay. At start-up, if the options fail with the store's values, the app logs an error and builds them from the configuration without the store (appsettings and environment), and throws only if that fails too. `IOptions` and `IOptionsMonitor` of `SquadOptions` are both replaced, so `ValidateOnStart` reads the contained value and can't stop the host.
- **Refresh:** both apps check the sentinel and the flags at most every 15 minutes (`Register("Touchline:Sentinel", refreshAll: true)`, `UseFeatureFlags` with the same interval). The Match Centre uses `UseAzureAppConfiguration()` middleware, so an API call can trigger a check. The Event Processor is a Functions isolated worker with no web request, so it uses `Microsoft.Azure.AppConfiguration.Functions.Worker`'s middleware, which asks for a refresh at the start of each invocation. Start-up waits at most 10 seconds for the store (the default is 100), and the provider is `optional`, so a down or throttled store leaves `appsettings` (or the last good values).
- **Live, no restart:** the four flags, token caps (`Squad:MaxOutputTokens:*`), `Squad:MaxInputTokens` and `Squad:Languages`. `IOptions<SquadOptions>` is replaced by a version that rebuilds, with validation, when configuration reloads; a changed value that fails validation is logged and the last good one stays. **Needs a restart:** `Squad:FoundryEndpoint`, `Squad:Deployments:*` and `Squad:ManagedIdentityClientId` (read when the model clients and credentials are created), and `AppConfiguration:Endpoint` itself.
- **Not gated:** the processor's possession indexer keeps indexing with `PossessionSearch` off, so a match played meanwhile is still searchable when it comes back on.
- **Local:** Aspire has `AddAzureAppConfiguration(...).RunAsEmulator()`. The AppHost adds it only when `AppConfiguration:Emulator` is `true` in its configuration, and passes both apps `ConnectionStrings__appconfig`, which they use when there is no endpoint. It starts empty. Without it, local runs use `appsettings` alone.

## ADR-063 — The synthetic dataset export

**Status:** Accepted · 25 Oct, Tracey's call: the recommendation (A, committed if small, otherwise a release asset)

**Question**
The brief asks for a synthetic dataset as a deliverable. What does the season contain, how is it produced, and where is it published?

**Options: the fixtures** (the catalogue has two fictional clubs)
- **A. A 38-match head-to-head season** between the two catalogue clubs, home and away alternating, seeds 1 to 38.
- **B. A 20-club league** using fan-style archetype teams (ADR-050) for the other 18 clubs: 380 matches, a real league table. Far bigger, and the 18 clubs need generated fictional names that pass the name guard.
- **C. A 38-round season for a 20-club league, one fixture per round** (one club's season). Smaller than B, but still needs the 18 generated clubs.

**Options: where it lives**
- **1. Committed to the repo:** `dataset/` with gzipped JSON lines, a schema and the calibration table. Simple for judges, but only if it is small.
- **2. A GitHub Release asset,** with a small sample match committed. Keeps the repo light; publishing a release needs Tracey's go-ahead.
- **3. Reproducible only:** the export command and a fixed seed list, with no data committed. Anyone can rebuild it byte for byte, but a judge has to run it.

**Recommendation**
A, published as 1 if the gzipped season is under about 20 MB, otherwise 2, with the export always reproducible. An `export` mode on the simulator (no Azure needed) writes `events.jsonl.gz` (one event per line, the same JSON as Event Hubs), `matches.jsonl` (metadata, line-ups, final score, totals), `schema.json` (JSON Schema for both) and `CALIBRATION.md` (the season's averages against the ranges in `CalibrationTests`). Every file says the data is synthetic and every name fictional.

**Trade-offs**
- **A head-to-head season has no league table,** but it is the honest size for two clubs, and the calibration numbers are the same.
- **Determinism is the selling point:** a test exports two matches twice and compares the bytes, so the published files can be checked against the code.
- **The schema can drift from the events** unless a test validates every exported line against `schema.json`.

---

## ADR-064 — Substitutions

**Status:** Accepted · 26 Oct, Tracey's call: the recommendation

**Question**
The bench is chosen and stored but never played (ADR-032, ADR-050). How does the engine make substitutions, and how far through the system does a substitution reach before submission?

**Options: how many, and when**
- **A. The modern rule:** up to 5 substitutes in up to 3 stoppages, plus half time. Most come between 55 and 85 minutes. Each team decides independently, at a stoppage (a foul, the ball going out, a goal, half time).
- **B. Three substitutes, one at a time,** at stoppages between 60 and 85 minutes. Simpler, and it reads like older football.

**Options: who comes off**
- **1. Like for like:** a defender for a defender, and so on, picked at random among the outfield starters still on.
- **2. Like for like, with a reason:** a booked player is more likely to come off (so he can't pick up a second yellow), and a team that's behind after 70 minutes swaps a defender or midfielder for a forward. A team that's ahead swaps a forward for a defender or midfielder.
- In every option **the goalkeeper is never substituted.** With no injuries there's no reason to, and the season projection's "the keeper is the first-named starter" stays true (ADR-025).

**Options: how far it reaches** (it's the last day of extra time)
- **X. End to end:** a `Substitution` event (`PlayerId` comes off, a new `ReplacementPlayerId` goes on), the bench in the metadata's `TeamSheet`, the shape model swapping the slot, the pitch and timeline drawing it, the moment detector reporting it as notable, the guardrail and commentary naming the new player, and the player documents giving substitutes an appearance.
- **Y. Engine, shape and UI only:** the event, the metadata bench, the shape swap and the timeline. The moments, agents and season projection see an event type they skip.

**Recommendation**
A with 2, reaching X but cutting anything that slips to the backlog, in that order: the season projection's substitute appearances first, then the notable moment.

**Trade-offs**
- **It changes every match's event stream.** Substitution draws use the seeded random, so the golden hashes, the calibration run and #66's committed dataset all regenerate. The calibration ranges should still hold, because a substitute plays with his own traits but the same tendencies.
- **A new enum member breaks exhaustive clients** (already in the backlog for the README): the OpenAPI enum and the TypeScript schema gain `Substitution`.
- **A fan team's bench of 0 means no substitutions,** and a default pick (no sheet) uses the five squad players not in the eleven.

**Settled during the build**
- **Substitutions draw from their own seeded stream** (the match seed mixed with a constant), not the play's. Deciding a substitution never moves a pass or a shot; play changes only once a different player is on. A match is unchanged, apart from the new fields, up to its first substitution.
- **Each team plans at kick-off** how many substitutes it uses (1 to 5, weighted towards more, capped by its outfield bench), whether one comes at half time (15%), and 1 to 3 window minutes drawn from 55 to 85. A window opens at the first stoppage at or after its minute; who comes off is chosen then, from the score and the bookings. A plan never has more than three windows, so the window limit needs no check during play.
- **Stoppages are** a foul that stops play, the ball going out, a goal, and half time. Cards owed are shown first. A keeper holding a save isn't a stoppage. A substitution takes no clock time of its own: it happens inside the stoppage it follows. **A penalty foul shows its cards but makes no substitutions:** they wait for the penalty's own outcome, so a fresh substitute never takes a kick given before he came on.
- **Only an outfield starter still on can come off;** a substitute who came on stays on. A bench goalkeeper never comes on. The substitute takes the departing player's place in the line-up, so he can inherit set pieces and penalties.
- **On the pitch, the shape follows the eleven on the pitch.** The projector rebuilds that side's team sheet with the substitute at the departing player's index and works the formation out again, so a forward who replaces a defender plays up front (a 4-3-3 becomes a 3-3-4). A half-time substitution is drawn with the ball on the centre spot.
- **In the facts the agents read,** substitutions only fill the event slots that goals, cards, shots, fouls and corners leave, so a busy hour of changes never crowds out the play.
- **Tactical changes** apply from the 70th minute to 60% of a trailing or leading team's changes, when the bench has the right player. A booked player is three times as likely to be picked.
- **The substitution event** starts on the halfway line at the near touchline (52.5, 0) and has no end. It shares the possession ID of the stoppage, and possession summaries leave it out. Its shape is drawn where the ball stopped.
- **`TeamSheet.Substitutes`** is optional in JSON (old documents read with an empty bench). `MatchDetail` gains `homeSubstitutes` and `awaySubstitutes`, so the pitch can label a substitute. `PlayerMatch` gains `substitute`, and `PlayerSeason` gains `substituteAppearances`.
- **`appearances` follows the football convention:** every fixture played, starting or from the bench, and `rounds` lists every round played in. The simulator's "rounds done" read is unchanged by it, since every fixture has starters, and no other reader assumed a start. Season documents are rebuilt from player-match documents, so an old season document takes the new meaning on its next projection.
- **The demo moved to seed 35600** (see ADR-027's notes).

---

## ADR-065 — A little life in the players' positions

**Status:** Accepted · 26 Oct, Tracey's call: the recommendation

**Question**
The shape model places every line evenly spaced and perfectly straight (ADR-035), so on screen the players stand in a grid. How do we roughen it a little, without making it look random?

**Options**
- **A. A personal offset:** each player gets a fixed nudge for the whole match, up to about 2 m deep and 2 m across, so lines are never quite straight and gaps never quite equal. It stays still between events.
- **B. A wobble per event:** a small nudge, up to about 1 m, that changes with every event, so the dots drift slightly even when the ball barely moves.
- **C. Both:** A's personal offset plus B's wobble at half the size.

**How it stays deterministic (all options)**
The nudge comes from a hash, not from the engine's seeded random: the personal offset from the player ID alone (so a player's spot is his, match to match), and the wobble from the player ID and the ball's position rounded to 0.1 m. So the shape model stays a pure function, the event stream doesn't change, and nothing in #66's dataset moves. The nudge is applied before the edge clamp, so a player never leaves the pitch, and never to the goalkeeper's depth (he still stands on his line).

**Recommendation**
C, with the sizes as tuning constants beside the others in `ShapeModel`, so they're easy to turn down after a look on screen.

**Trade-offs**
- **The shape test pins exact positions** in places. Those tests switch to "within the nudge of the formation", and one test pins the hash so the nudge can't change silently.
- **Modelled, not tracked, still applies:** the nudge makes the picture livelier, not more true.

**Settled during the build**
- The personal offset hashes the player ID alone (`ShapeModel.Place` has no match ID), so a player stands in the same spot in every match. The wobble hashes the player ID with the ball's position (in the team's own frame) rounded to 0.1 m, so `Place`'s signature is unchanged.
- The nudge is applied to the slot before the minimum-depth floor and the ball pull, so a defender never ends up behind his keeper and a player beside the ball is drawn towards it, not past it.
- The sideways nudge is capped at 35% of the gap between neighbours in a line, so a crowded line can't overlap or swap.

---

## ADR-066 — Half time: a pause and a numbers card

**Status:** Accepted · 26 Oct, Tracey's call: the recommendation on both questions

**Question**
Nothing marks half time. The pacer (ADR-023) starts the second half's clock at 45:00, earlier than the end of the first half's added time, so the second half kicks off the moment the first ends. How long should the pause be, and what does the page show during it?

**Options: the pause**
- **A. Scaled with a floor:** 15 minutes of match time divided by the speed, never less than 15 seconds. 90 s at 10×, 15 s at 60× and 100×.
- **B. Fixed:** always 30 seconds.
- **C. Scaled, no floor:** 9 s at 100×, which is too short to read the card.

**Options: the card**
- **A. Numbers only:** score, shots, shots on target, xG, pass accuracy, cards and a momentum summary, from data the page already has. No model call and no back-end change.
- **B. Numbers and a Producer summary:** a "first half so far" mode. Needs a trigger, storage, a contract change and an overlay card, and costs model calls.

**Decision**
Pause A, card A.
- **The pause is the simulator's,** in `MatchPacer`: the batch after the first half's `PeriodEnd` waits `max(15 min / speed, 15 s)`, instead of the clock gap. The event stream and the engine don't change, so the dataset (ADR-063) and the golden hashes don't move.
- **`LiveState` gains `halfTime`:** true from the first half's `PeriodEnd` until the second half's first event. A page opened during the pause gets it in its first `state` snapshot, because it has none of the events. It's additive on the wire.
- **The page shows half time** while `halfTime` is true: the clock reads "HT", and a half-time card sits over the pitch.
- **A replay (ADR-067) shows the card** as it reaches half time too, for the same paced time.

**Trade-offs**
- **A fan who opens the page during the pause** sees the card straight away, because the latest state is the first half's end.
- **No agent at half time.** The agents still speak at key moments and full time. The Producer's half-time mode stays on the backlog.

---

## ADR-067 — Replay from a point

**Status:** Accepted · 26 Oct, Tracey's call: the recommendation on both questions

**Question**
Fans want to watch a moment again: click a moment's card, or drag the timeline, and the match plays from just before it. How is it played back, and when is it allowed?

**Options: how**
- **A. A full, paced replay:** the page reopens the stream from just before the event and plays it back at the match's pace: positions, ball, score, moments and toasts.
- **B. Events only:** the ball, moments and score replay, but the players stay in their latest positions.
- **C. A jump:** the pitch and score as they were at that moment, frozen.

**Options: when**
- **A. At any time,** with a "Back to live" button during a live match.
- **B. After full time only.**

**Decision**
How A, when A.
- **The server already replays.** `GET /api/matches/{matchId}/stream?after=N` sends every message after event N, shaped for the viewer, including a `state` after each event. But it keeps only the latest `shape` (`ReplayShapes.LatestOnly`), because a reconnecting client draws only the latest. A new optional query parameter, `replay=true`, keeps every shape. It's additive, so existing clients don't change.
- **A replay can start from a match time.** The timeline's slider gives a minute, not an event, and the page keeps only the latest events. So the stream also takes `fromPeriod` and `fromMinute` (both or neither, instead of `after`), and the server starts two events before the first event at or after that time.
- **The client buffers and paces.** A replay opens a second stream with `after` set to two events before the chosen one (so the build-up shows) and `replay=true`, and plays the buffered messages on a client clock, from the events' match times. The speed matches the live match's speed where it's known, otherwise 10×. Half time pauses as in ADR-066.
- **The live stream keeps running.** During a replay the live match carries on underneath, and a badge says how far behind live the replay is. "Back to live" closes the replay stream and shows the live state again. A replay that reaches the live edge returns to live on its own.
- **Toasts and the goal toast play** as the replay passes them, marked as a replay.

**Trade-offs**
- **Two streams at once** during a live replay, so twice the server's per-viewer work for that fan. A replay of a finished match is one stream that ends at full time.
- **The replay's commentary** is whatever exists now, which may be better than what was shown live. That's fine for a replay.

---

## ADR-068 — Match page panels: line-ups, timeline, player flyout and a goal toast

**Status:** Accepted · 26 Oct, Tracey's call

**Question**
The match page needs to look and feel more professional before the demo. Which panels are added, and how are they built so several can be built at once?

**Decision**
- **A line-ups card:** both starting elevens and benches, by shirt number and position, with goal, yellow card, red card and substitution icons and their minutes. A player's name opens the player flyout.
- **A timeline:** one bar across the match, with an icon for each goal, card, penalty, big chance and substitution, and a slider to replay from a point (ADR-067). The momentum bars keep their own axis.
- **A player flyout,** opened from the line-ups card: name, shirt number, position, this match's goals, assists, cards and minutes in or out, and the season's stats from `GET /api/seasons/{season}/players/{playerId}` when the match is a fixture. A fan or exhibition match has no season, so the flyout says so. **The pitch dots stay as they are:** no clickable dots (Tracey).
- **A goal toast:** a goal shows large in the centre of the pitch, instead of in the toast at the top. Other toasts don't change.
- **The other player in a moment.** `MomentView` gains `relatedPlayerId`: the player taken off for a substitution (its `playerId` is already the one coming on), or the assister for a goal. The page keeps only the last 200 events, so the moments must carry it. It's additive on the wire.
- **Built as components first, then the page.** Each panel is a component with its own tests and its own stylesheet, built in parallel. A separate slice puts them on the match page and wires the replay. Shared pieces come first, in their own slice: the per-player facts derived from the moments, and the moment icons.

**Trade-offs**
- **Per-player facts come from moments, not events,** so they're complete after a reload. A stat that isn't a moment, such as passes, needs the season endpoint.
- **More on one page.** The page slice has to keep it usable on a phone: panels stack, and the flyout is a sheet from the bottom.

---

## ADR-069 — Visual system and image assets

**Status:** Accepted · 27 Oct, Tracey's call: look A, images A, as built in #97

**Question**
The UI polish (a sci-fi broadcast look: glass panels, neon edges, a hero header and art behind the page) needs a visual system and a way to ship three generated images (two arrive with the foundation slice, the third, the offline state, with the states slice). How are they built and shipped?

**Options: the look**
- **A. Tokens plus a few shared surfaces.** Glass, edge and glow are CSS custom properties in `styles.css` (ADR-034). Components opt in with existing classes. Light theme gets opaque equivalents.
- **B. A component library** (for example a headless kit plus a theme). Faster for complex widgets, but a new dependency and a rewrite of every page.
- **C. Per-component styles only.** No shared tokens, so each slice would restyle by hand and drift.

**Options: the images**
- **A. Resize once, ship WebP through Vite.** Originals (2752 px JPEG, about 2.5 MB each) stay out of the repo. Optimised copies (about 1920 px, 40 to 150 KB) live in `src/assets` and are referenced from CSS, so Vite hashes and caches them.
- **B. Commit the originals** and let the browser scale them. Seven times the bytes on the wire, and 7.6 MB in git for good.
- **C. Host them outside the app** (Blob Storage, CDN). Another moving part, and a failure mode on demo day.

**Decision**
Look A, images A.
- **Tokens carry the theme.** `--glass`, `--glass-strong`, `--glass-edge`, `--masthead-image`, `--page-art` and `--page-art-opacity` are defined for dark and light. Light theme sets the images to `none` and the glass to opaque surfaces, so contrast does not depend on a picture.
- **Images are decoration only.** They sit behind a dark gradient overlay and carry no information, so they have no alt text and nothing relies on them. Text over them keeps WCAG AA contrast.
- **Fallback.** Where `backdrop-filter` is missing, glass panels fall back to the opaque surface colour.
- **Provenance.** The images are AI-generated (Gemini) with no real clubs, crests, logos or Premier League marks, checked by eye on 26 Oct. The "synthetic" badge stays in the header.
- **Pipeline.** A one-off `sharp` run, not a build step and not a new dependency. The originals are kept outside this repo.

**Trade-offs**
- **Binary files in git:** about 250 KB for the first two images, more as more are added. Cheap, but they never leave the history.
- **No build-time image pipeline:** if the art changes, someone reruns the one-off resize by hand.
- **Fixed background art** repaints on scroll in some browsers. It is low-contrast and masked, and reduced-motion users are unaffected because nothing animates.

---

## ADR-070 — Notable lines that don't repeat themselves (UI-B4)

**Status:** Accepted · 26 Oct, Tracey's call (the small version, before submission)

**Question**
Consecutive notable moments of the same kind read as copies in the feed. A run of corners shows "Corner. It came off …" each time. These are template lines (ADR-046, `NotableLines`), not model lines. How should they vary without a model call, a contract change or any invented facts?

**Options**
- **A. De-duplicate identical consecutive lines.** This drops a moment's line from the feed, so a corner happens with nothing said.
- **B. Template variants, chosen by `Trigger.Sequence`.** Stateless, but two corners in a row land on the same variant about a third of the time.
- **C. Template variants, chosen by the moment's ordinal within its kind** (the 1st, 2nd, 3rd corner of the match) modulo the variant count. Two moments of the same kind in a row always get different variants.
- **D. Tactical context in the agent prompts.** It doesn't apply: the repeated lines never reach a model. Agent prompts and budgets stay as they are.

**Decision**
**C.** The scope is the kinds that repeat:
- **Corner, Throw-in and Goal kick** get three variants each, in English, Spanish and French.
- Yellow card, Woodwork and Substitution keep one line, because they rarely come back to back.
- The anonymous "a player" lines keep one line.
- The ordinal is counted from the match's ordered event stream, so the live hub, the read model and the demo pick the same variant for the same moment. No viewer or subscriber state is involved.

**Trade-offs**
- **Placeholders keep their meaning.** In every variant, `{0}` and `{1}` are still the last player to touch the ball and their club. For a restart, that is the side that **conceded** it. A variant must never read as if `{1}` won the corner.
- **The wording stays neutral.** It is filled only from roster names, with no invented tactical or medical detail. The roster guardrail is unaffected.
- **No change on the wire or in storage.** Only the text of a `CommentaryLine` changes. Lines are built when read, not stored, so a finished match shows the new wording too.
- **There are 27 new strings to keep in step across three languages.** A test checks every kind and language has the same number of variants.

---

## ADR-071 — Match page on one screen; club pages

**Status:** Accepted · 27 Oct 2026, Tracey's call

**Context**
The match page had grown into a long scroll. Settings were a wide card at the top, the line-ups and the Ask the Analyst box sat in the middle of the page, and three legends (pitch, timeline, momentum) each explained which side was which. The pitch, the thing a fan came for, was pushed down. "Favourite club" was offered in the settings, but the server validates the `team` parameter and never uses it, so choosing a club changed nothing. On the Teams page, club cards were plain and not clickable, and the empty fan-made clubs message gave no way forward.

**Decision**
Match page:
- **Favourite club is removed from the UI.** The viewer is now only a mode and a language, and the web app no longer stores or sends `team`. The server parameter is left in place, unused, so nothing on the API changes.
- **Settings sit behind a gear button in the scoreboard.** It opens a small popover with View (Casual or Analyst), Language and the grey-pitch accessibility option. It closes on Escape (focus returns to the gear), on a click outside and when focus leaves it. The recap page uses the same gear.
- **The colour key lives only in the scoreboard.** Each team's kit colour is shown beside its name, as a circle for home and a diamond for away, matching the shapes on the pitch. The legends under the pitch, the timeline and the momentum strip are removed. Their meaning stays in visually-hidden text and chart labels for screen readers.
- **The "Goal!" toast is plain.** White text with a shadow over the pitch, no background, border or team colour. The animation and the replay tag stay.
- **Ask the Analyst is a chat flyout pinned bottom-right.** A pill opens a panel above it: the fan's questions on the right, the Analyst's answers on the left, cited moments still clickable, input and send at the bottom. Escape or the close button closes it, and focus moves into the box on open and back to the pill on close. On a phone the panel is close to full width.
- **The page fits one screen with no scroll on desktop (960px and up).** A compact scoreboard row on top (names, colour key, score, clock, Line-ups and gear), the pitch big on the left with the timeline and a slim momentum strip under it, and the Moments list in a right column that scrolls on its own. A phone still stacks and scrolls.
- **Line-ups open in a drawer from the left; clicking a player opens the player card from the right.** Both can be open at once. Escape closes the topmost first. Both respect reduced motion.

Teams page (built in a separate change):
- **Club cards are clickable** and styled like the home page's match cards, in the club's home colour.
- **Each card opens `/teams/:id`**, a club page that shows the club the way the line-ups do, plus the full squad, the manager and the stadium, with a back link to Teams.
- **The empty fan-made clubs message** links "Build the first one" to `/teams/new`.

**Consequences**
- **The page is denser.** The pitch scales with the window height; very short windows get a minimum height and then scroll.
- **Line-ups are one click away instead of always visible.** That is the price of the pitch-first layout. Players are still clickable, and the drawer is non-modal so the pitch stays in view.
- **The colour key depends on the team records loading.** Until a kit loads, the key and the pitch both use the house red and blue.
- **Existing saved viewers keep working.** A stored `team` is ignored on load and dropped on the next save.
- **The Ask the Analyst conversation survives closing the panel** but not a change of match, as before.
- **The fixed bottom-right pill covers the end of the moments list**, so the list leaves room under its last item.

**Amendment (27 Oct, smoke test)**
Smoke-testing the one-screen match page and the club pages changed these decisions:
- **The goal toast is one word.** The big toast over the pitch says only "GOAL!" in bold display capitals, white with a shadow and no box. The scorer, team, score and minute left it; they are in the screen-reader announcement. The standard overlay toast now shows goal cards again (it had filtered them out), so the scorer and details appear in their usual place at the top while the big word sits in the middle.
- **The green frame hugs the drawn pitch.** `Pitch.tsx` sets `--pitch-aspect` from the same constants as the viewBox, and on desktop the frame is sized from its container's height and centred, with the drawing filling it. The pitch is therefore as big as the height allows instead of half the green. The timeline hint becomes a visually-hidden description of the slider on desktop and the momentum strip is slimmer, to give the pitch more height.
- **Added time reads the football way everywhere.** The scoreboard clock, its accessible label and the overlay toast use `clockLabel` (`90+3'` once a half is past 45 or 90 minutes, `67:12` before), as the home page cards do. The server's template moment reasons and commentary start with the raw clock (`92:02: GOAL! ...`); a leading `mm:ss:` is stripped wherever that text is shown, because the moments list has its own minute column.
- **Scorers are on the scoreboard.** Under each club's name: the scorers' surnames with their minutes, grouped by player, penalties marked `(pen)` (read from the moment's reason). It follows the view on screen, so a replay shows only the goals so far. It is clipped to two lines and the full names are in the accessible label. Goals are credited to the moment's team, since the moments carry nothing that marks an own goal.
- **A replay on a finished match holds at full time.** It used to return to the full-time view as soon as it reached full time, which made a late-moment replay look as if nothing had happened. It now stops on its last frame (status `ended`), the bar reads "Replay finished" and Back to live stays until the fan clicks it or starts another replay. A replay of a live match still ends by itself, and announces "Back to live", when it catches up. Replay mode is unmistakable: a REPLAY badge over the pitch's top-left corner, a highlighted, larger replay bar and a primary Back to live button.
- **Club page players open the player card.** Each squad name is a button that opens the same card as the match page, sliding in from the right, with no "This match" section. The season is that of the most recent match in the matches list that has one, so built-in clubs show season stats and fan clubs, which play exhibitions only, say there are none.

**Amendment 2 (27 Oct)**
A second smoke test changed these decisions:
- **Scorers sit beside the score,** like a TV score bug: home's right-aligned against the digits, away's left-aligned, small, three lines at most. On a phone they drop below the score. This replaces the placement under the club names.
- **The recap lists the scorers** under the final result, with full names, minutes and (pen), grouped per player. It loads the moments itself and shows no scorers if they fail.
- **The theme toggle shows the current theme:** a "Theme" label and a Dark | Light switch with the current one highlighted (`aria-pressed`), instead of a button named for the theme it would switch to.
- **Timeline seek commits on any release:** pointerup, mouseup, touchend or the native change event, once, with minutes clamped to the server's ranges (first half 0 to 59, second half 45 to 120).
- **A replay tracks half time itself** (from the first half's `PeriodEnd` until the second half's first event), so the half-time card shows during the pause even if the replayed states don't carry `halfTime`.
- **UI audit fixes:** on desktop Ask the Analyst docks as a slide-over that covers the Moments column (measured from the column; phones keep the near-full-width panel); the `Template` tag is gone from moment cards; the momentum panel is not drawn until there are readings, and its grid row closes; the pitch frame has a soft inner shadow and a 1px edge (darker in the light theme); key moments' Replay buttons use the accent colour; shirt numbers sit in a fixed-width, right-aligned, tabular column in the line-ups and the club squad.
- **The open player card's player is ringed on the pitch** (audit item A): the card stays on the right, and a pulsing cyan ring marks that player's token, still under reduced motion, so the fan can find them. Tracey chose this over moving the card into the line-ups drawer.

**Amendment 3 (27 Oct)**
- **A replay's half-time pause is 10 s.** It was 15 match minutes divided by the replay speed, 90 s at the default, which looked like a crash. Ten seconds is enough to read the half-time card.
- **The pitch is empty at half time,** live and in a replay: no player tokens and no ball while `halfTime` is on, because the teams are off the pitch.

**Amendment 4 (27 Oct)**
- **The team builder's Remove buttons are trash-can icons.** A light-grey inline SVG (`--muted`) that turns `--card-red` on hover or focus, in a 36 px square button. The accessible name stays `Remove {player}` (aria-label, with a matching tooltip), and behaviour is unchanged.
- **The recap shows a full-time stats card.** `HalfTimeCard` takes `phase: 'half' | 'full'` (default `half`). The full variant is titled "Full time", sits in the page flow under the final score and scorers, and summarises every momentum reading ("had the better of the match"). It is fed `detail.state` and `GET /api/matches/{id}/momentum`, fetched beside the recap. A failed momentum call only drops the momentum line; a detail with no team stats leaves the card out.
- **Render errors show a friendly page.** The router has an `errorElement`, on a pathless route inside the layout (so the header stays) and on the layout itself as a last resort. It says something went wrong on this page, with Try again (reload) and Matches buttons, over full-bleed night-stadium artwork (`error-page-dark.webp`, `error-page-light.webp`, about 200 KB and 130 KB at 1920 px) that follows the theme, with the text on the empty left half under a readable overlay. In a dev build only, the error message sits in a collapsed details element.

**Amendment 5 (27 Oct)**
- **Unknown paths call offside.** The not-found page uses the error page's layout: "Offside!", "That page isn't where it should be. The flag's up.", and links to Matches and Teams, over its own theme-following artwork (`--not-found-art`). The unused `Placeholder` page is removed.


## ADR-072 — Kick-off checks fan matches first; Coming Soon page

**Status:** Accepted · 27 Oct 2026, decided by Tracey ("the check should be first, show a Coming Soon holding page")

**Context**
A fan could pick two teams, set line-ups and click Kick off, and only then learn that fan matches weren't running. The `POST /api/matches` answered 503 when the `FanMatches` flag (ADR-062) was off, and the demo build's launcher (`UnconnectedMatchLauncher`, ADR-032) can never start a match. The page then said "Fan matches go live when the match pipeline is switched on", which was a surprise after all that effort. The maintenance page (ADR-060) was also plain, with no picture.

**Decision**
- **`GET /api/status` reports `fanMatches`.** It is true only when the `FanMatches` flag is on (read per request, as the kill switch is) and the registered `IMatchLauncher` says `CanLaunch`. `FanMatchLauncher` is true and `UnconnectedMatchLauncher` is false, so the status doesn't type-check a concrete class. The only work is the flag read, so the call stays cheap. The OpenAPI contract and `schema.d.ts` carry the new field.
- **`/kickoff` asks first.** It fetches the status before loading clubs or showing the form, with a light "Checking the pitch..." line meanwhile. If `fanMatches` is false it shows a Coming Soon holding page: a full-bleed background image under a dark gradient (so the text reads in both themes), a large "COMING SOON" label in the display font, two calm sentences, and links to Matches and Build a team. If the status call fails, the form shows. The 503 handling on submit stays as the safety net, with its message reworded so it no longer claims the line-up is valid.
- **One image, two uses.** `src/assets/coming-soon.webp` (about 80 KB, 1920px wide, converted from a 2.5 MB JPEG) is imported once by the Coming Soon page and embedded as an assembly resource in `Touchline.MatchCentre`, linked to that same file. Swapping the image is therefore a one-file change.
- **The maintenance page uses it as its background.** The page has no scripts and the app's bundles aren't served during maintenance, so the short-circuit in `MaintenanceMode` serves one fixed path, `/maintenance/background.webp`, from the embedded resource with `Cache-Control: public, max-age=86400`, alongside the health probes' exemption. The page keeps its text and theme colours, with a dark gradient and translucent panels so it reads in light and dark. Every other path still gets the maintenance answer.

**Consequences**
- **A fan never fills in a form that can't be submitted** while the flag is off or the pipeline isn't connected. Turning the flag on in App Configuration opens the form on the next page load, with no deploy.
- **The status call is now needed to see the form.** A failing call shows the form, so an outage of `/api/status` alone never hides a working feature, and a failing submit still ends in the calm banner.
- **The cache is a day, not a year,** because the path is fixed and the file will be replaced by a purpose-made image. A browser may show the old picture on the maintenance page for up to a day after a swap.
- **The embedded copy is read from the web app's assets folder,** so the .NET build needs that file present. The Dockerfile already copies all of `src/`.
- **Light mode keeps a dark image treatment.** The Coming Soon text is always light on a dark overlay, regardless of the theme toggle.

**Amendment (27 Oct): artwork**
- **Coming Soon has its own pictures per theme.** `coming-soon-dark.webp` (131 KB) and `coming-soon-light.webp` (204 KB), 1920 px wide, are applied through a `--coming-soon-art` token set for each theme, so the default (dark) and the toggle both work. The readable overlay stays.
- **The maintenance page keeps its own picture.** The old file is renamed `maintenance.webp` (assets folder, embedded resource `maintenance.webp`), so the two pages no longer share an image.
- **The light theme gets page artwork.** `bg-hud-grid-light.webp` (68 KB, its native 1376 px) is the light counterpart of `bg-hud-grid.webp` behind the page.
- **Built-in clubs have image crests.** Both were AI-generated, and one was then edited by hand. North Bank FC and Thames Valley United are 256 x 256 WebP files (17 KB and 18 KB), cropped from the navy-square sources, embedded in `Touchline.MatchCentre`. `GetCrest` serves them as `image/webp` with a hash ETag from the same `/crest.svg` route, so every `crestUrl` works with no client change; browsers decode an `<img>` by content, not by suffix. Fan teams still get the generated SVG. Crest images get a 12% border radius so the navy square reads as a badge tile.
- **The Coming Soon page follows the theme in light mode too.** It used a dark navy overlay with light text in both themes, which made the light image look dark. The light theme now fades to the page background (45% to 75%) with dark text and a light label shadow, like the error and not-found pages; the dark theme is unchanged. This replaces the "Light mode keeps a dark image treatment" consequence above.
- **Ask the Analyst follows its flag too.** `GET /api/status` also reports `fanQa` (the `FanQa` flag, kill switch included), and the match page leaves the Ask the Analyst button out while it is off. If the status call fails or is still loading, the button shows and the 503 on asking stays the safety net. Like every flag, a change in App Configuration reaches the apps at their next refresh, up to 15 minutes later, or at once on a restart.

## ADR-073 — CI deploys to Azure only while DEPLOY_TO_AZURE is on

**Status:** Accepted · 28 Oct 2026, decided by Tracey ("switch it off", after choosing a full teardown)

**Context**
After the hackathon the Azure environment is torn down (`deploy.ps1 -Action teardown`), which deletes the CI identity's federated credential with the resource group. Every push to `main` would then fail at the Azure sign-in in both deploy jobs (ADR-013, ADR-024), leaving a red check on a public repository.

**Decision**
- **Both deploy jobs also need the repository variable `DEPLOY_TO_AZURE` set to `true`.** Unset, or any other value, the jobs are skipped; build, test, UI and infrastructure validation still run on every push and pull request.
- **Turning deploys back on** is a redeploy (`deploy.ps1 -Action deploy`), the deploy outputs set as repository variables as before, then `gh variable set DEPLOY_TO_AZURE --body true`.

**Consequences**
- **`main` stays green while nothing is deployed,** and the deploy jobs show as skipped rather than failed.
- **A forgotten variable fails quietly:** with the environment back but the variable unset, pushes build but don't deploy. The README's deploy section names the variable so it isn't missed.
