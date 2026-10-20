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
