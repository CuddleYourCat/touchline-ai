extern alias processor;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using processor::Touchline.EventProcessor;

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

        triggers.Should().HaveCountGreaterThanOrEqualTo(2, "the commentary and the recaps each read a subscription");
        triggers.Select(t => t.Connection).Should().OnlyHaveUniqueItems();
        RecapFunction.ConnectionName.Should().NotBe(CommentaryFunction.ConnectionName);
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
            var symbol = Regex.Match(
                _template,
                $@"resource (\w+) 'Microsoft\.ServiceBus/namespaces/topics/subscriptions@[^']+' = \{{\s*parent: \w+\s*name: '{Regex.Escape(trigger.SubscriptionName!)}'");
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

        foreach (var symbol in new[] { "commentaryReceiverForProcessor", "recapsReceiverForProcessor", "commentarySubscription", "recapsSubscription" })
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

    private static List<ServiceBusTriggerAttribute> ServiceBusTriggers() =>
        typeof(CommentaryFunction).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods())
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.GetCustomAttribute<ServiceBusTriggerAttribute>())
            .OfType<ServiceBusTriggerAttribute>()
            .ToList();
}
