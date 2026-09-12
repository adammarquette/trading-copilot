using FluentAssertions;

namespace MarqSpec.TradingCopilot.Infra.Tests;

/// <summary>
/// CloudWatch alarms for task count, unhealthy target, and deploy rollback sit beside ADR-0019
/// paging (ADR-0030 decision 13). The subscription endpoint is a parameter, never a literal email.
/// </summary>
public sealed class AlarmTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void One_sns_topic_subscribes_the_alerts_email_parameter(string env)
    {
        var t = templates.For(env);
        var topics = t.Resources("AWS::SNS::Topic").Values.Select(t.Properties).ToList();
        topics.Should().ContainSingle();
        topics[0]["TopicName"]!.GetValue<string>().Should().Be($"trading-copilot-{env}-alerts");

        t.Parameter("AlertsEmail").Should().NotBeNull();
        t.Parameter("AlertsEmail")!.ContainsKey("Default").Should().BeFalse();

        var subscription = t.Resources("AWS::SNS::Subscription").Values.Select(t.Properties).Should().ContainSingle().Which;
        Synthesised.Text(subscription["Endpoint"]).Should().Be("{\"Ref\":\"AlertsEmail\"}");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Task_count_unhealthy_and_rollback_alarms_exist(string env)
    {
        var t = templates.For(env);
        var names = t.Resources("AWS::CloudWatch::Alarm").Values
            .Select(a => t.Properties(a)["AlarmName"]!.GetValue<string>())
            .ToList();

        names.Should().Contain($"trading-copilot-{env}-app-running-tasks");
        names.Should().Contain($"trading-copilot-{env}-postgres-running-tasks");
        names.Should().Contain($"trading-copilot-{env}-unhealthy-hosts");

        var rules = t.Resources("AWS::Events::Rule").Values.Select(t.Properties).ToList();
        rules.Should().Contain(r => r["Name"]!.GetValue<string>() == $"trading-copilot-{env}-deployment-failed");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Every_alarm_sets_treat_missing_data_and_publishes(string env)
    {
        var t = templates.For(env);
        foreach (var alarm in t.Resources("AWS::CloudWatch::Alarm").Values)
        {
            var props = t.Properties(alarm);
            props.ContainsKey("TreatMissingData").Should().BeTrue(props["AlarmName"]!.GetValue<string>());
            props["AlarmActions"]!.AsArray().Should().NotBeEmpty(props["AlarmName"]!.GetValue<string>());
        }
    }
}
