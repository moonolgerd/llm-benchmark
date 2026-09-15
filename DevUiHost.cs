using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using LlmBenchmark.Models;

namespace LlmBenchmark;

/// <summary>
/// Interactive alternative to the automated benchmark: hosts the same
/// Planner/Coder/Reviewer stages and the concurrency-load agent (built from
/// config.json's agentBenchmark section) as individually chattable entities in
/// Microsoft Agent Framework's DevUI, plus the full sequential workflow, all
/// pointed at the same local server the benchmark measures. Useful for poking
/// at a stage's behavior by hand instead of only seeing aggregate CSV numbers.
/// </summary>
public static class DevUiHost
{
    public const string WorkflowName = "planner-coder-reviewer";

    public static async Task RunAsync(BenchmarkConfig config, CancellationToken ct = default)
    {
        string modelId = string.IsNullOrWhiteSpace(config.AgentBenchmark.ModelId)
            ? config.Models.FirstOrDefault()?.Id ?? ""
            : config.AgentBenchmark.ModelId;

        if (string.IsNullOrWhiteSpace(modelId))
        {
            Console.WriteLine("No model configured for agentBenchmark and no fallback in models[] — cannot start DevUI.");
            return;
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://localhost:5273");

        var chatClient = AgentFrameworkRunner.CreateChatClient(config.BaseUrl, config.ApiKey, modelId);
        builder.Services.AddChatClient(chatClient);

        // Hosting.AddAIAgent requires the returned agent's Name to equal the
        // registration key, so these standalone (individually chattable in
        // DevUI) agents are named. The workflow below intentionally builds its
        // own separate, unnamed instances instead of reusing these — see the
        // comment there.
        var stages = config.AgentBenchmark.Workflow.Stages;
        var stageNames = new List<string>();
        foreach (var stage in stages)
        {
            int maxTokens = config.AgentBenchmark.Workflow.MaxTokensPerStage;
            string? instructions = stage.Instructions;
            builder.AddAIAgent(stage.Name, (sp, key) =>
                new ChatClientAgent(sp.GetRequiredService<IChatClient>(), new ChatClientAgentOptions
                {
                    Name = key,
                    ChatOptions = AgentFrameworkRunner.BuildChatOptions(instructions, maxTokens)
                }));
            stageNames.Add(stage.Name);
        }

        builder.AddAIAgent("ConcurrencyLoadAgent", (sp, key) =>
            new ChatClientAgent(sp.GetRequiredService<IChatClient>(), new ChatClientAgentOptions
            {
                Name = key,
                ChatOptions = AgentFrameworkRunner.BuildChatOptions(
                    config.AgentBenchmark.ConcurrencyLoad.Instructions,
                    config.AgentBenchmark.ConcurrencyLoad.MaxTokens)
            }));

        if (stages.Count > 0)
        {
            int maxTokensPerStage = config.AgentBenchmark.Workflow.MaxTokensPerStage;
            builder.AddWorkflow(WorkflowName, (sp, key) =>
            {
                // Fresh agent instances — deliberately not the named ones
                // registered above (Name left null): AgentWorkflowBuilder.
                // BuildSequential forwards each agent's AuthorName into the next
                // agent's request messages, and this local server 400s on "name"
                // appearing on a non-tool message (same fix as
                // AgentFrameworkRunner.MakeAgent). .Id is set instead, purely for
                // a readable workflow-graph label in DevUI — it never touches
                // AuthorName, so it doesn't reintroduce the bug.
                var workflowChatClient = sp.GetRequiredService<IChatClient>();
                var workflowStageAgents = stages.Select(s => new ChatClientAgent(
                    workflowChatClient,
                    new ChatClientAgentOptions
                    {
                        Id = s.Name,
                        ChatOptions = AgentFrameworkRunner.BuildChatOptions(s.Instructions, maxTokensPerStage)
                    }));
                return AgentWorkflowBuilder.BuildSequential(workflowName: key, agents: workflowStageAgents);
            }).AddAsAIAgent();
        }

        // No-op unless OTEL_EXPORTER_OTLP_ENDPOINT is set (see AgentTelemetry) —
        // point it at a collector (e.g. a standalone Aspire Dashboard container,
        // since --devui isn't run under the AppHost) to see the same agent/
        // workflow traces DevUI's own graph view doesn't provide.
        AgentTelemetry.AddToServices(builder.Services, "LlmBenchmark.DevUI");

        // Required for MapDevUI() below — undocumented as of 1.18.0-preview, where
        // MapDevUI() resolves DevUIAuthFilter from DI but nothing registers it
        // unless AddDevUI() was called first (see microsoft/agent-framework#6368).
        builder.AddDevUI();

        builder.Services.AddOpenAIResponses();
        builder.Services.AddOpenAIConversations();

        var app = builder.Build();

        app.MapOpenAIResponses();
        app.MapOpenAIConversations();
        app.MapDevUI();

        Console.WriteLine($"DevUI running against model: {modelId}");
        Console.WriteLine($"Entities: {string.Join(", ", stageNames)}, ConcurrencyLoadAgent" +
                           (stages.Count > 0 ? $", {WorkflowName}" : ""));
        Console.WriteLine("Open http://localhost:5273/devui in a browser. Press Ctrl+C to stop.");

        await app.RunAsync(ct);
    }
}
