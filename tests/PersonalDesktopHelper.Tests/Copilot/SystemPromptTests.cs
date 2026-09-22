using ModelContextProtocol.Protocol;
using System.Text.Json;
using PersonalDesktopHelper.Copilot;
using PersonalDesktopHelper.Persistence;

namespace PersonalDesktopHelper.Tests;

public sealed class SystemPromptTests
{
    [Fact]
    public void ConstantPromptContainsToolSchemaAndCombinesWithEditableInstructions()
    {
        var tool = new Tool
        {
            Name = "test_tool", Description = "A module tool",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "string" } } })
        };
        var constant = SystemPromptDefinition.BuildConstant([tool]);

        Assert.Contains("test_tool", constant);
        Assert.Contains("A module tool", constant);
        Assert.Contains("\"value\"", constant);
        Assert.Contains("without per-action confirmation", constant);
        Assert.StartsWith(constant, SystemPromptDefinition.Compose(constant, "Use short replies."));
        Assert.EndsWith("Use short replies.", SystemPromptDefinition.Compose(constant, "Use short replies."));
        Assert.Equal(constant, SystemPromptDefinition.Compose(constant, "  "));
    }

    [Fact]
    public void OnlyEditablePromptPersistsAndLegacyProfilesHaveAnEmptyEditablePart()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.StatePath, """{"version":1,"notificationsEnabled":true,"tasks":[],"copilot":{"model":"test"}}""");
        var store = new JsonStateStore(directory.StatePath);
        Assert.Equal("", store.State.Copilot.AdditionalSystemPrompt);
        store.SetCopilotSettings(store.State.Copilot with { AdditionalSystemPrompt = "Use short replies.\nPrefer local time." });

        var loaded = new JsonStateStore(directory.StatePath).State;
        Assert.Equal("test", loaded.Copilot.Model);
        Assert.Equal("Use short replies.\nPrefer local time.", loaded.Copilot.AdditionalSystemPrompt);
        Assert.DoesNotContain("Available MCP tools", File.ReadAllText(directory.StatePath));
        Assert.Throws<InvalidDataException>(() =>
            store.SetCopilotSettings(loaded.Copilot with { AdditionalSystemPrompt = "\0" }));
    }
}
