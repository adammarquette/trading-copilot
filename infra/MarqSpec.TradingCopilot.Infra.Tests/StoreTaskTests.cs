using System.Text.Json.Nodes;
using FluentAssertions;
using MarqSpec.TradingCopilot.Infra;

namespace MarqSpec.TradingCopilot.Infra.Tests;

/// <summary>
/// The store is the Timescale image this repo already tests, on EFS, not RDS (ADR-0030 decision 6).
/// </summary>
public sealed class StoreTaskTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    private static JsonObject PostgresContainer(Synthesised t) => t.Container("-postgres", "postgres");

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_image_is_timescaledb_ha_pg17_by_digest(string env)
    {
        var t = templates.For(env);
        var image = PostgresContainer(t)["Image"]!.GetValue<string>();

        image.Should().Be(EnvironmentStack.PostgresImage);
        image.Should().StartWith("timescale/timescaledb-ha@sha256:");
        image.Should().NotContain(":pg17", "the tag moves; the digest is what compose and CI already pin");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_data_directory_is_an_efs_access_point_mounted_at_pgdata_over_tls_with_iam(string env)
    {
        var t = templates.For(env);
        var (_, taskDefinition, _) = t.TaskDefinition("-postgres");
        var (fileSystemId, _) = t.Single("AWS::EFS::FileSystem");
        var (accessPointId, accessPoint) = t.Single("AWS::EFS::AccessPoint");

        var volume = t.Properties(taskDefinition)["Volumes"]!.AsArray().Should().ContainSingle().Which!;
        var efs = volume["EFSVolumeConfiguration"]!;
        Synthesised.LogicalIdOf(efs["FilesystemId"]).Should().Be(fileSystemId);
        efs["TransitEncryption"]!.GetValue<string>().Should().Be("ENABLED");
        Synthesised.LogicalIdOf(efs["AuthorizationConfig"]!["AccessPointId"]).Should().Be(accessPointId);
        efs["AuthorizationConfig"]!["IAM"]!.GetValue<string>().Should().Be("ENABLED");

        var mount = PostgresContainer(t)["MountPoints"]!.AsArray().Should().ContainSingle().Which!;
        mount["ContainerPath"]!.GetValue<string>().Should().Be("/home/postgres/pgdata");

        var posix = t.Properties(accessPoint)["PosixUser"]!;
        posix["Uid"]!.GetValue<string>().Should().Be("1000");
        posix["Gid"]!.GetValue<string>().Should().Be("1000");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_file_system_is_encrypted_and_reachable_only_through_its_own_group(string env)
    {
        var t = templates.For(env);
        var (_, fs) = t.Single("AWS::EFS::FileSystem");
        t.Properties(fs)["Encrypted"]!.GetValue<bool>().Should().BeTrue();

        var (efsGroupId, _) = t.SecurityGroup($"trading-copilot/{env}/efs");
        var mountTargets = t.Resources("AWS::EFS::MountTarget").Values;
        mountTargets.Should().NotBeEmpty();
        foreach (var target in mountTargets)
        {
            var groups = t.Properties(target)["SecurityGroups"]!.AsArray();
            groups.Select(Synthesised.LogicalIdOf).Should().Contain(efsGroupId);
        }
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void There_is_no_rds_instance(string env)
    {
        templates.For(env).Resources("AWS::RDS::DBInstance").Should().BeEmpty();
        templates.For(env).Resources("AWS::RDS::DBCluster").Should().BeEmpty();
    }
}
