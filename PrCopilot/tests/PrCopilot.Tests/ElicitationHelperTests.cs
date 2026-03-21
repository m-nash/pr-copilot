// Licensed under the MIT License.

using System.Text.Json;
using PrCopilot.StateMachine;
using PrCopilot.Tools;

namespace PrCopilot.Tests;

public class ElicitationHelperTests
{
    [Fact]
    public void BuildElicitRequest_StaticChoices_MapsToEnumSchema()
    {
        var action = new MonitorAction
        {
            Action = "ask_user",
            Question = "CI is green, PR is approved. What would you like to do?",
            Choices = ["Merge the PR", "Wait for another approver", "I'll handle it myself"]
        };

        var request = ElicitationHelper.BuildElicitRequest(action);

        Assert.Equal(action.Question, request.Message);
        Assert.NotNull(request.RequestedSchema);
        Assert.True(request.RequestedSchema.Properties.ContainsKey("choice"));
        Assert.Contains("choice", request.RequestedSchema.Required!);

        var schema = request.RequestedSchema.Properties["choice"];
        var enumSchema = Assert.IsType<ModelContextProtocol.Protocol.ElicitRequestParams.TitledSingleSelectEnumSchema>(schema);
        Assert.Equal(3, enumSchema.OneOf.Count);

        Assert.Equal("merge", enumSchema.OneOf[0].Const);
        Assert.Equal("Merge the PR", enumSchema.OneOf[0].Title);

        Assert.Equal("wait_for_approver", enumSchema.OneOf[1].Const);
        Assert.Equal("Wait for another approver", enumSchema.OneOf[1].Title);

        Assert.Equal("handle_myself", enumSchema.OneOf[2].Const);
        Assert.Equal("I'll handle it myself", enumSchema.OneOf[2].Title);
    }

    [Fact]
    public void BuildElicitRequest_ChoiceIsRequired()
    {
        var action = new MonitorAction
        {
            Action = "ask_user",
            Question = "Test",
            Choices = ["Resume monitoring"]
        };

        var request = ElicitationHelper.BuildElicitRequest(action);

        Assert.Contains("choice", request.RequestedSchema!.Required!);
        Assert.Single(request.RequestedSchema.Properties);
    }

    [Fact]
    public void BuildElicitRequest_NoOtherAppended()
    {
        var action = new MonitorAction
        {
            Action = "ask_user",
            Question = "Test",
            Choices = ["Resume monitoring"]
        };

        var request = ElicitationHelper.BuildElicitRequest(action);
        var schema = (ModelContextProtocol.Protocol.ElicitRequestParams.TitledSingleSelectEnumSchema)
            request.RequestedSchema!.Properties["choice"];

        Assert.Single(schema.OneOf);
        Assert.Equal("resume", schema.OneOf[0].Const);
    }

    [Fact]
    public void BuildElicitRequest_DynamicChoices_UsesFullTextAsConst()
    {
        var action = new MonitorAction
        {
            Action = "ask_user",
            Question = "Which comment would you like to address?",
            Choices = [
                "1. alice: Fix the null check (src/Auth.cs:42)",
                "2. bob: Add error handling (src/Api.cs:10)",
                "I'll handle them myself"
            ]
        };

        var request = ElicitationHelper.BuildElicitRequest(action);
        var schema = (ModelContextProtocol.Protocol.ElicitRequestParams.TitledSingleSelectEnumSchema)
            request.RequestedSchema!.Properties["choice"];

        Assert.Equal(3, schema.OneOf.Count);
        Assert.Equal("1. alice: Fix the null check (src/Auth.cs:42)", schema.OneOf[0].Const);
        Assert.Equal("handle_myself", schema.OneOf[2].Const);
    }

    [Fact]
    public void BuildElicitRequest_NullQuestion_UsesDefault()
    {
        var action = new MonitorAction
        {
            Action = "ask_user",
            Question = null,
            Choices = ["Resume monitoring"]
        };

        var request = ElicitationHelper.BuildElicitRequest(action);
        Assert.Equal("What would you like to do?", request.Message);
    }

    [Fact]
    public void BuildElicitRequest_EmptyChoices_EmptyEnum()
    {
        var action = new MonitorAction
        {
            Action = "ask_user",
            Question = "No choices available",
            Choices = []
        };

        var request = ElicitationHelper.BuildElicitRequest(action);
        var schema = (ModelContextProtocol.Protocol.ElicitRequestParams.TitledSingleSelectEnumSchema)
            request.RequestedSchema!.Properties["choice"];
        Assert.Empty(schema.OneOf);
    }

    [Fact]
    public void BuildElicitRequest_AllStaticChoices_HaveMappedValues()
    {
        foreach (var (displayText, internalValue) in MonitorTransitions.ChoiceValueMap)
        {
            var action = new MonitorAction
            {
                Action = "ask_user",
                Question = "Test",
                Choices = [displayText]
            };

            var request = ElicitationHelper.BuildElicitRequest(action);
            var schema = (ModelContextProtocol.Protocol.ElicitRequestParams.TitledSingleSelectEnumSchema)
                request.RequestedSchema!.Properties["choice"];

            Assert.Single(schema.OneOf);
            Assert.Equal(internalValue, schema.OneOf[0].Const);
            Assert.Equal(displayText, schema.OneOf[0].Title);
        }
    }

    [Fact]
    public void BuildElicitRequest_CiInvestigationResultsChoices_CorrectMapping()
    {
        var action = new MonitorAction
        {
            Action = "ask_user",
            Question = "Investigation findings...",
            Choices = ["Apply the recommendation", "Re-run failed jobs", "I'll handle it myself"]
        };

        var request = ElicitationHelper.BuildElicitRequest(action);
        var schema = (ModelContextProtocol.Protocol.ElicitRequestParams.TitledSingleSelectEnumSchema)
            request.RequestedSchema!.Properties["choice"];

        Assert.Equal(3, schema.OneOf.Count);
        Assert.Equal("apply_fix", schema.OneOf[0].Const);
        Assert.Equal("rerun", schema.OneOf[1].Const);
        Assert.Equal("handle_myself", schema.OneOf[2].Const);
    }

    #region ExtractElicitResult tests

    [Fact]
    public void ExtractElicitResult_KnownChoice_StandardPath()
    {
        var knownConsts = new HashSet<string> { "merge", "resume" };
        var result = new ModelContextProtocol.Protocol.ElicitResult
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>
            {
                ["choice"] = JsonDocument.Parse("\"merge\"").RootElement
            }
        };

        var extracted = ElicitationHelper.ExtractElicitResult(result, knownConsts);

        Assert.NotNull(extracted);
        Assert.False(extracted!.IsFreeform);
        Assert.Equal("merge", extracted.Value);
    }

    [Fact]
    public void ExtractElicitResult_UnknownChoice_DetectedAsFreeform()
    {
        var knownConsts = new HashSet<string> { "merge", "resume" };
        var result = new ModelContextProtocol.Protocol.ElicitResult
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>
            {
                ["choice"] = JsonDocument.Parse("\"please also fix the tests\"").RootElement
            }
        };

        var extracted = ElicitationHelper.ExtractElicitResult(result, knownConsts);

        Assert.NotNull(extracted);
        Assert.True(extracted!.IsFreeform);
        Assert.Equal("please also fix the tests", extracted.Value);
    }

    [Fact]
    public void ExtractElicitResult_NoKnownConsts_TreatedAsStandard()
    {
        var result = new ModelContextProtocol.Protocol.ElicitResult
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>
            {
                ["choice"] = JsonDocument.Parse("\"merge\"").RootElement
            }
        };

        // Without knownConsts, everything is treated as standard choice
        var extracted = ElicitationHelper.ExtractElicitResult(result);

        Assert.NotNull(extracted);
        Assert.False(extracted!.IsFreeform);
        Assert.Equal("merge", extracted.Value);
    }

    [Fact]
    public void ExtractElicitResult_ResponseField_ReturnsFreeformText()
    {
        var result = new ModelContextProtocol.Protocol.ElicitResult
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>
            {
                ["response"] = JsonDocument.Parse("\"merge but also resolve all threads\"").RootElement
            }
        };

        var extracted = ElicitationHelper.ExtractElicitResult(result);

        Assert.NotNull(extracted);
        Assert.True(extracted!.IsFreeform);
        Assert.Equal("merge but also resolve all threads", extracted.Value);
    }

    [Fact]
    public void ExtractElicitResult_EmptyChoice_FallsToResponse()
    {
        var result = new ModelContextProtocol.Protocol.ElicitResult
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>
            {
                ["choice"] = JsonDocument.Parse("\"\"").RootElement,
                ["response"] = JsonDocument.Parse("\"custom instruction\"").RootElement
            }
        };

        var extracted = ElicitationHelper.ExtractElicitResult(result);

        Assert.NotNull(extracted);
        Assert.True(extracted!.IsFreeform);
        Assert.Equal("custom instruction", extracted.Value);
    }

    [Fact]
    public void ExtractElicitResult_Declined_ReturnsNull()
    {
        var result = new ModelContextProtocol.Protocol.ElicitResult
        {
            Action = "decline"
        };

        var extracted = ElicitationHelper.ExtractElicitResult(result);
        Assert.Null(extracted);
    }

    [Fact]
    public void ExtractElicitResult_NeitherSet_ReturnsNull()
    {
        var result = new ModelContextProtocol.Protocol.ElicitResult
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>()
        };

        var extracted = ElicitationHelper.ExtractElicitResult(result);
        Assert.Null(extracted);
    }

    #endregion
}
