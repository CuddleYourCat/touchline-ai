using System.ComponentModel.DataAnnotations;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Models;
using Microsoft.Extensions.Options;

namespace Touchline.MatchCentre.TeamBuilding;

/// <summary>
/// Settings for fan matches (ADR-032, ADR-050). Bound from <c>MatchCentre:FanMatches</c>.
/// </summary>
public sealed class FanMatchOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "MatchCentre:FanMatches";

    /// <summary>
    /// The ARM resource ID of the simulator job, <c>job-simulator</c>. When it's unset the launcher
    /// stays unconnected and every request is a <c>503</c>, as locally under Aspire, where there's
    /// no Container Apps job.
    /// </summary>
    public string? JobResourceId { get; set; }

    /// <summary>
    /// How long after it was created a running request counts as playing. Just beyond the job's
    /// 30-minute replica timeout, so a request whose execution died without a word stops holding its teams.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan ActiveFor { get; set; } = TimeSpan.FromMinutes(35);

    /// <summary>
    /// The pick-up deadline: how long a request may stay scheduled before it's treated as a start
    /// that ARM accepted but that never ran, no longer counts as playing, and is marked failed the
    /// next time it's read. Beyond the job's cold start of up to a minute.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:30", "00:30:00")]
    public TimeSpan ScheduledFor { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How many fan matches may start in a UTC day (ADR-032, ten from 27 Oct). Each is about 14 MB of
    /// telemetry against the 0.2 GB daily cap (ADR-011), so this also protects monitoring.
    /// </summary>
    [Range(0, 100)]
    public int DailyLimit { get; set; } = 10;

    /// <summary>
    /// About how long a fan match takes from its request to full time, at the job's default 10x:
    /// the estimate behind the <c>Retry-After</c> while one is live.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan TypicalMatchLength { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Starts the simulator job for one fan match (ADR-050). A port, so the launcher's tests never
/// call Azure Resource Manager.
/// </summary>
public interface ISimulatorJob
{
    /// <summary>Starts one execution that plays the given request.</summary>
    /// <param name="requestId">The match request, already written.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes once Azure has accepted the start.</returns>
    Task StartAsync(string requestId, CancellationToken cancellationToken);
}

/// <summary>
/// Builds the template one execution runs with (ADR-050). A start with a template override
/// replaces the job's whole template for that execution rather than merging with it, so the
/// override is a copy of the job's own template with one environment variable added.
/// </summary>
public static class SimulatorJobTemplates
{
    /// <summary>The environment variable that puts the simulator in request mode (<c>Simulator:RequestId</c>).</summary>
    public const string RequestIdVariable = "Simulator__RequestId";

    /// <summary>The job's simulator container, which gets the request ID.</summary>
    public const string ContainerName = "simulator";

    /// <summary>
    /// The job's template, copied container by container, with <see cref="RequestIdVariable"/>
    /// added to the simulator container (or to the first container, if none has that name).
    /// Everything else (images, commands, arguments, resources, the other variables and their
    /// secret references) is carried over unchanged.
    /// </summary>
    /// <param name="template">The job's current template, as ARM returns it.</param>
    /// <param name="requestId">The request to play.</param>
    /// <returns>The execution template.</returns>
    /// <exception cref="InvalidOperationException">The job has no containers.</exception>
    public static ContainerAppJobExecutionTemplate ForRequest(ContainerAppJobTemplate template, string requestId)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (template.Containers.Count == 0)
        {
            throw new InvalidOperationException("The simulator job's template has no containers.");
        }

        var target = template.Containers.FirstOrDefault(c => c.Name == ContainerName) ?? template.Containers[0];
        var execution = new ContainerAppJobExecutionTemplate();
        foreach (var container in template.Containers)
        {
            var copy = Copy(container);
            if (ReferenceEquals(container, target))
            {
                // Replaced rather than duplicated, should the job ever carry a stale one.
                var existing = copy.Env.Where(e => e.Name == RequestIdVariable).ToList();
                existing.ForEach(e => copy.Env.Remove(e));
                copy.Env.Add(new ContainerAppEnvironmentVariable { Name = RequestIdVariable, Value = requestId });
            }

            execution.Containers.Add(copy);
        }

        foreach (var init in template.InitContainers)
        {
            execution.InitContainers.Add(Copy(init));
        }

        return execution;
    }

    // Volume mounts and probes have no place in an execution template; the job uses neither.
    private static JobExecutionContainer Copy(ContainerAppBaseContainer container)
    {
        var copy = new JobExecutionContainer
        {
            Name = container.Name,
            Image = container.Image,
            Resources = container.Resources,
        };

        foreach (var command in container.Command)
        {
            copy.Command.Add(command);
        }

        foreach (var arg in container.Args)
        {
            copy.Args.Add(arg);
        }

        foreach (var variable in container.Env)
        {
            copy.Env.Add(new ContainerAppEnvironmentVariable { Name = variable.Name, Value = variable.Value, SecretRef = variable.SecretRef });
        }

        return copy;
    }
}

/// <summary>
/// Starts the simulator job through Azure Resource Manager (ADR-050): reads the job's template,
/// copies it with the request ID added (<see cref="SimulatorJobTemplates"/>), and starts one
/// execution with it. Keyless: the Match Centre's identity holds a custom role with only
/// <c>Microsoft.App/jobs/read</c> and <c>Microsoft.App/jobs/start/action</c> on this one job.
/// </summary>
/// <param name="arm">The ARM client, signed in with the registered credential.</param>
/// <param name="options">The job's resource ID.</param>
internal sealed class ArmSimulatorJob(ArmClient arm, IOptions<FanMatchOptions> options) : ISimulatorJob
{
    /// <inheritdoc />
    public async Task StartAsync(string requestId, CancellationToken cancellationToken)
    {
        var job = arm.GetContainerAppJobResource(new ResourceIdentifier(options.Value.JobResourceId!));
        var current = await job.GetAsync(cancellationToken);
        var template = SimulatorJobTemplates.ForRequest(current.Value.Data.Template, requestId);

        // Started, not Completed: the call returns once the execution is accepted, not after the match.
        await job.StartAsync(WaitUntil.Started, template, cancellationToken);
    }
}
