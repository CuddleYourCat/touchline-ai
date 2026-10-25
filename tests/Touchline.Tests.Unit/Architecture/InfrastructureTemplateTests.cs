extern alias processor;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using processor::Touchline.EventProcessor;
using Touchline.MatchCentre.Maintenance;

namespace Touchline.Tests.Unit.Architecture;

/// <summary>
/// Checks <c>infra/main.bicep</c> against the Event Processor's Service Bus triggers, so a trigger
/// can't ship without the app settings and role it needs to read its subscription in Azure.
/// </summary>
[TestFixture]
public class InfrastructureTemplateTests
{
    private static readonly string[] _identitySettings = ["fullyQualifiedNamespace", "credential", "clientId"];

    private string _template = null!;

    [OneTimeSetUp]
    public void LoadTemplate() =>
        _template = File.ReadAllText(Path.Combine(RepositoryFiles.Root(), "infra", "main.bicep"));

    [Test]
    public void Each_Service_Bus_trigger_has_its_own_connection_name()
    {
        var triggers = ServiceBusTriggers();

        triggers.Should().HaveCountGreaterThanOrEqualTo(3, "the commentary, the recaps and the search indexer each read a subscription");
        triggers.Select(t => t.Connection).Should().OnlyHaveUniqueItems();
        RecapFunction.ConnectionName.Should().NotBe(CommentaryFunction.ConnectionName);
        PossessionIndexFunction.ConnectionName.Should().Be("servicebussearch").And.NotBe(RecapFunction.ConnectionName);
        PossessionIndexFunction.SubscriptionName.Should().Be("search-index");
    }

    [Test]
    public void Each_Service_Bus_trigger_connection_has_keyless_app_settings()
    {
        foreach (var trigger in ServiceBusTriggers())
        {
            foreach (var setting in _identitySettings)
            {
                _template.Should().Contain(
                    $"name: '{trigger.Connection}__{setting}'",
                    "the {0} trigger signs in with the processor identity", trigger.SubscriptionName);
            }
        }
    }

    [Test]
    public void Each_subscription_a_trigger_reads_has_a_receiver_role()
    {
        foreach (var trigger in ServiceBusTriggers())
        {
            // A subscription's name is written out, or held in a var such as searchSubscriptionName.
            var nameVar = Regex.Match(_template, $@"var (\w+) = '{Regex.Escape(trigger.SubscriptionName!)}'");
            var nameForms = nameVar.Success
                ? $"(?:'{Regex.Escape(trigger.SubscriptionName!)}'|{nameVar.Groups[1].Value})"
                : $"'{Regex.Escape(trigger.SubscriptionName!)}'";
            var symbol = Regex.Match(
                _template,
                $@"resource (\w+) 'Microsoft\.ServiceBus/namespaces/topics/subscriptions@[^']+' = \{{\s*parent: \w+\s*name: {nameForms}");
            symbol.Success.Should().BeTrue("the {0} subscription is declared as its own resource", trigger.SubscriptionName);

            _template.Should().MatchRegex(
                $@"scope: {symbol.Groups[1].Value}\s*properties: \{{\s*roleDefinitionId: [^\n]*roles\.serviceBusDataReceiver",
                "without the receiver role the {0} trigger can't read its subscription", trigger.SubscriptionName);
        }
    }

    [Test]
    public void Both_squad_hosts_get_the_model_settings_only_when_the_models_are_deployed_and_no_keys()
    {
        var squadSettings = Regex.Match(_template, @"var squadSettings = deploySquadModels\s*\?\s*\[(?<settings>.*?)\]\s*:\s*\[\]", RegexOptions.Singleline);
        squadSettings.Success.Should().BeTrue("the Squad__* settings exist only when deploySquadModels is on (ADR-051)");
        foreach (var setting in new[] { "Squad__FoundryEndpoint", "Squad__Deployments__Small", "Squad__Deployments__Large" })
        {
            squadSettings.Groups["settings"].Value.Should().Contain($"name: '{setting}'");
            Regex.Matches(_template, $"name: '{setting}'").Should().HaveCount(1, "both hosts take them from squadSettings");
        }

        Regex.Matches(_template, @"\.\.\.squadSettings\b").Should().HaveCount(2, "the Match Centre and the processor both host the squad");

        _template.Should().NotContain("foundry.listKeys(", "the models are called with an Entra ID token, never a key");
    }

    [Test]
    public void The_models_are_optional_but_the_trigger_wiring_is_not()
    {
        foreach (var symbol in new[] { "squadContentFilter", "smallModelDeployment", "largeModelDeployment" })
        {
            _template.Should().MatchRegex($@"resource {symbol} '[^']+' = if \(deploySquadModels\)", "a model quota failure mustn't block the rest (ADR-051)");
        }

        foreach (var symbol in new[] { "commentaryReceiverForProcessor", "recapsReceiverForProcessor", "commentarySubscription", "recapsSubscription", "searchIndexReceiverForProcessor", "searchIndexSubscription" })
        {
            _template.Should().MatchRegex($@"resource {symbol} '[^']+' = \{{", "the Service Bus wiring deploys on every run");
        }
    }

    [Test]
    public void The_deploy_script_looks_for_the_small_model_the_template_deploys()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryFiles.Root(), "infra", "deploy.ps1"));
        var templateName = Regex.Match(_template, @"var smallModel = \{\s*name: '(?<name>[^']+)'").Groups["name"].Value;
        var scriptName = Regex.Match(script, @"\$SmallModelDeploymentName = '(?<name>[^']+)'").Groups["name"].Value;

        templateName.Should().NotBeEmpty();
        scriptName.Should().Be(templateName, "deploy.ps1 keeps the models on by finding this deployment (ADR-051)");
    }

    [Test]
    public void The_data_wipe_deletes_every_container_the_template_creates()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryFiles.Root(), "infra", "deploy.ps1"));
        var definitions = Regex.Match(_template, @"var cosmosContainerDefinitions = \[(?<body>.*?)\n\]", RegexOptions.Singleline).Groups["body"].Value;
        var templateNames = Regex.Matches(definitions, @"\{ name: (?:'(?<literal>[^']+)'|(?<variable>\w+)),")
            .Select(m => m.Groups["literal"].Success
                ? m.Groups["literal"].Value
                : Regex.Match(_template, $@"var {m.Groups["variable"].Value} = '(?<name>[^']+)'").Groups["name"].Value)
            .ToList();
        var wipeList = Regex.Match(script, @"\$WipedContainers = @\((?<body>.*?)\)", RegexOptions.Singleline).Groups["body"].Value;
        var scriptNames = Regex.Matches(wipeList, "'(?<name>[^']+)'").Select(m => m.Groups["name"].Value).ToList();

        templateNames.Should().NotBeEmpty().And.NotContain(string.Empty);
        scriptNames.Should().BeEquivalentTo(templateNames, "a container left out of the wipe would keep data that points at deleted matches");
    }

    [Test]
    public void Maintenance_mode_uses_the_setting_and_header_the_Match_Centre_reads()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryFiles.Root(), "infra", "deploy.ps1"));
        var setting = MaintenanceOptions.SectionName.Replace(":", "__", StringComparison.Ordinal) + "__" + nameof(MaintenanceOptions.Enabled);

        script.Should().Contain($"$MaintenanceSetting = '{setting}'", "wipe-data turns maintenance on with this setting (ADR-060)");
        script.Should().Contain($"$MaintenanceHeader = '{MaintenanceMode.HeaderName}'", "wipe-data checks the page is up by this header");
        _template.Should().Contain($"...(maintenanceMode ? [{{ name: '{setting}', value: 'true' }}] : [])", "the wipe's own redeploy must keep the page up");
    }

    private static List<ServiceBusTriggerAttribute> ServiceBusTriggers() =>
        typeof(CommentaryFunction).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods())
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.GetCustomAttribute<ServiceBusTriggerAttribute>())
            .OfType<ServiceBusTriggerAttribute>()
            .ToList();
}
