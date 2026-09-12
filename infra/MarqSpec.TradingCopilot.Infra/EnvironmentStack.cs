using Amazon.CDK;
using Amazon.CDK.AWS.CertificateManager;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.EFS;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.Events;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.Route53;
using Amazon.CDK.AWS.Route53.Targets;
using Amazon.CDK.AWS.ServiceDiscovery;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Amazon.CDK.AWS.SSM;
using Constructs;
using CfnParameter = Amazon.CDK.CfnParameter;
using CfnParameterProps = Amazon.CDK.CfnParameterProps;
using EcsSecret = Amazon.CDK.AWS.ECS.Secret;
using EventTargets = Amazon.CDK.AWS.Events.Targets;
using FileSystem = Amazon.CDK.AWS.EFS.FileSystem;
using FileSystemProps = Amazon.CDK.AWS.EFS.FileSystemProps;
using SmSecret = Amazon.CDK.AWS.SecretsManager.Secret;
using SmSecretProps = Amazon.CDK.AWS.SecretsManager.SecretProps;

namespace MarqSpec.TradingCopilot.Infra;

/// <summary>
/// One deployed environment (ADR-0030): a VPC, an ALB as the edge, an ECS cluster running the
/// GHCR app image by digest and Timescale on EFS, Secrets Manager shells, deploy-history SSM,
/// CloudWatch alarms, and an OTLP sidecar. Instantiated twice — production and staging — from
/// the same class; what differs is in <see cref="EnvironmentStackProps"/>.
/// </summary>
/// <remarks>
/// Shape is the TopstepX <c>EnvironmentStack</c> in MarqSpec.Mcp.TopstepX (pattern library).
/// This product's names, JWT (R-18), and flatten duty (R-13) are ours. No Cognito, no MCP
/// host callbacks, no account IDs or hostnames copied from that repo.
/// <para>
/// Operational defaults this card took, none an ADR-0030 decision: two AZs; /24 subnets;
/// app task 512 CPU / 1024 MiB; store task 1024 CPU / 2048 MiB; ALB idle timeout 600 s
/// (SignalR); target-group 2 healthy / 3 unhealthy, 5 s timeout, 30 s deregistration;
/// HTTPS uses <c>SslPolicy.RECOMMENDED_TLS</c>; Cloud Map TTL 10 s.
/// </para>
/// </remarks>
public sealed class EnvironmentStack : Stack
{
    /// <summary>The published image, appended with <c>@&lt;digest&gt;</c> from the <c>ImageDigest</c> parameter.</summary>
    public const string AppImageRepository = "ghcr.io/adammarquette/trading-copilot";

    /// <summary>
    /// <c>timescale/timescaledb-ha:pg17</c> by digest — the multi-arch index digest, read from
    /// Docker Hub on 2026-09-12. The tag moves; this does not, and it is the image compose and
    /// the integration factory already pin (ADR-0030 decision 6). Bump it deliberately, in a
    /// pull request that says why, never by re-reading the tag.
    /// </summary>
    public const string PostgresImage = "timescale/timescaledb-ha@sha256:650e33ee8a3ba58d6651d23d217f6683b6c3a05c3a754533d549f5c65c2f8226";

    /// <summary>
    /// <c>otel/opentelemetry-collector-contrib:0.160.0</c> by digest — the multi-arch index digest,
    /// read from Docker Hub on 2026-09-12. The tag moves; this does not.
    /// </summary>
    public const string OtelCollectorImage = "otel/opentelemetry-collector-contrib@sha256:799dc6cf12c96192af37b5bdba804da8c10b3bc563b43cb90c3f3c58d9572ad6";

    /// <summary>
    /// Where the app exports OTLP: the task's own loopback. <c>127.0.0.1</c> rather than
    /// <c>localhost</c>, which can resolve to <c>::1</c> ahead of the IPv4 receiver.
    /// </summary>
    public const string OtelLoopbackEndpoint = "http://127.0.0.1:4317";

    private const string PostgresUser = "copilot";
    private const string PostgresDatabase = "tradingcopilot";
    private const string PostgresDataMount = "/home/postgres/pgdata";

    public EnvironmentStack(Construct scope, string id, EnvironmentStackProps props)
        : base(scope, id, new StackProps
        {
            Description = $"MarqSpec.TradingCopilot {props?.EnvName}: the BFF and its Timescale store (ADR-0030).",
        })
    {
        ArgumentNullException.ThrowIfNull(props);
        if (!Enum.IsDefined(props.OutboundPath))
        {
            throw new ArgumentOutOfRangeException(nameof(props), props.OutboundPath,
                "OutboundPath must be named: ADR-0030 leaves the tasks' outbound path to the operator, and a default would choose for them.");
        }

        var env = props.EnvName;
        Amazon.CDK.Tags.Of(this).Add("Project", "trading-copilot");
        Amazon.CDK.Tags.Of(this).Add("Environment", env);

        var natShape = props.OutboundPath is OutboundPath.NatGateway or OutboundPath.VpcEndpointsWithNatGateway;
        var endpointShape = props.OutboundPath is OutboundPath.VpcEndpointsWithPublicIp or OutboundPath.VpcEndpointsWithNatGateway;
        var taskSubnetType = natShape ? SubnetType.PRIVATE_WITH_EGRESS : SubnetType.PUBLIC;
        var taskSubnets = new SubnetSelection { SubnetType = taskSubnetType };
        var publicSubnets = new SubnetSelection { SubnetType = SubnetType.PUBLIC };

        // Digest and version are CloudFormation parameters, passed on `cdk deploy --parameters`,
        // not an SSM dynamic reference: an unversioned {{resolve:ssm}} is re-resolved only when
        // the template changes (ADR-0030 decision 3 / 5). No default on either.
        var imageDigest = new CfnParameter(this, "ImageDigest", new CfnParameterProps
        {
            Type = "String",
            Description = $"The digest of the {AppImageRepository} image to run, as sha256:<64 hex>. Never :latest, never a floating branch tag.",
            AllowedPattern = "^sha256:[0-9a-f]{64}$",
            ConstraintDescription = "must be sha256: followed by 64 lowercase hex characters",
        });
        var version = new CfnParameter(this, "Version", new CfnParameterProps
        {
            Type = "String",
            Description = "The release version the digest was published under. Written to SSM history beside the digest.",
            MinLength = 1,
        });
        var rootDomain = new CfnParameter(this, "RootDomain", new CfnParameterProps
        {
            Type = "String",
            Description = "DNS zone this stack creates. Operator-supplied; this repository does not invent a hostname (ADR-0030 decision 14).",
            MinLength = 1,
        });
        var hostname = new CfnParameter(this, "Hostname", new CfnParameterProps
        {
            Type = "String",
            Description = "FQDN the ALB answers on (host-header + certificate). Operator-supplied; no default.",
            MinLength = 1,
        });
        var dataTier = new CfnParameter(this, "ProjectXDataTier", new CfnParameterProps
        {
            Type = "String",
            Description = "ProjectX__DataTier: Simulated or Live, named on every deploy. Practice-only outside the live rung (R-14); a Live value there is refused in code, not here.",
            AllowedValues = ["Simulated", "Live"],
        });
        var alertsEmail = new CfnParameter(this, "AlertsEmail", new CfnParameterProps
        {
            Type = "String",
            Description = "Email that receives this environment's CloudWatch / EventBridge pages. No default — a default would be a literal in the template.",
            AllowedPattern = @".+@.+\..+",
            ConstraintDescription = "Must be an email address.",
        });
        var http5xxThreshold = new CfnParameter(this, "Http5xxAlarmThreshold", new CfnParameterProps
        {
            Type = "Number",
            Default = 10,
            MinValue = 1,
            Description = "ALB 5xx count in a 5-minute period that pages. Same threshold for ELB-generated and target 5xx.",
        });

        // Zone is created in-stack so `cdk synth --no-lookups` needs no AWS call and no invented
        // hosted-zone id. Operator delegates the NS at their registrar. A later Lookup import is
        // a follow-up once that zone exists.
        var zone = new PublicHostedZone(this, "Zone", new PublicHostedZoneProps
        {
            ZoneName = rootDomain.ValueAsString,
            Comment = $"trading-copilot {env}: created by the stack; operator delegates NS (ADR-0030).",
        });

        var vpc = new Vpc(this, "Vpc", new VpcProps
        {
            MaxAzs = 2,
            NatGateways = natShape ? 1 : 0,
            SubnetConfiguration = natShape
                ?
                [
                    new SubnetConfiguration { Name = "public", SubnetType = SubnetType.PUBLIC, CidrMask = 24 },
                    new SubnetConfiguration { Name = "tasks", SubnetType = SubnetType.PRIVATE_WITH_EGRESS, CidrMask = 24 },
                ]
                : [new SubnetConfiguration { Name = "public", SubnetType = SubnetType.PUBLIC, CidrMask = 24 }],
            RestrictDefaultSecurityGroup = false,
        });

        var albSg = Group(vpc, "AlbSecurityGroup", $"trading-copilot/{env}/alb", allowAllOutbound: false);
        var appSg = Group(vpc, "AppSecurityGroup", $"trading-copilot/{env}/app", allowAllOutbound: true);
        var postgresSg = Group(vpc, "PostgresSecurityGroup", $"trading-copilot/{env}/postgres", allowAllOutbound: true);
        var efsSg = Group(vpc, "EfsSecurityGroup", $"trading-copilot/{env}/efs", allowAllOutbound: false);
        albSg.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(443), "HTTPS from the internet");
        albSg.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(80), "HTTP from the internet, answered only with a redirect");
        albSg.AddEgressRule(appSg, Port.Tcp(8080), "plaintext to the app task only");
        appSg.AddIngressRule(albSg, Port.Tcp(8080), "plaintext from the load balancer only");
        postgresSg.AddIngressRule(appSg, Port.Tcp(5432), "from the app task only");
        efsSg.AddIngressRule(postgresSg, Port.Tcp(2049), "NFS from the store task only");

        if (endpointShape)
        {
            var endpointSg = Group(vpc, "EndpointSecurityGroup", $"trading-copilot/{env}/endpoints", allowAllOutbound: false);
            endpointSg.AddIngressRule(appSg, Port.Tcp(443), "the app task's calls to the AWS APIs");
            endpointSg.AddIngressRule(postgresSg, Port.Tcp(443), "the store task's calls to the AWS APIs");
            var services = new Dictionary<string, IInterfaceVpcEndpointService>
            {
                ["SecretsManager"] = InterfaceVpcEndpointAwsService.SECRETS_MANAGER,
                ["Ssm"] = InterfaceVpcEndpointAwsService.SSM,
                ["SsmMessages"] = InterfaceVpcEndpointAwsService.SSM_MESSAGES,
                ["Logs"] = InterfaceVpcEndpointAwsService.CLOUDWATCH_LOGS,
                ["Efs"] = InterfaceVpcEndpointAwsService.ELASTIC_FILESYSTEM,
                ["EcrApi"] = InterfaceVpcEndpointAwsService.ECR,
                ["EcrDocker"] = InterfaceVpcEndpointAwsService.ECR_DOCKER,
            };
            foreach (var (name, service) in services)
            {
                vpc.AddInterfaceEndpoint($"{name}Endpoint", new InterfaceVpcEndpointOptions
                {
                    Service = service,
                    Subnets = taskSubnets,
                    SecurityGroups = [endpointSg],
                    Open = false,
                    PrivateDnsEnabled = true,
                });
            }

            vpc.AddGatewayEndpoint("S3Endpoint", new GatewayVpcEndpointOptions { Service = GatewayVpcEndpointAwsService.S3, Subnets = [taskSubnets] });
        }

        // SHELLS, not values (ADR-0030 decision 5). Each holds the JSON keys the task definitions
        // read, every one empty. The operator writes the values by hand once; the runbook records
        // the names. NEVER EDIT A SHELL'S LITERAL AFTER THE VALUES ARE WRITTEN — CloudFormation
        // creates a new secret version whenever SecretString changes.
        var postgresSecret = Shell("PostgresSecret", env, "postgres", """{"password":"","connectionString":""}""",
            $"The store's password, and the connection string built on it: host postgres.{env}.tradingcopilot.internal, port 5432, database {PostgresDatabase}, username {PostgresUser}, plus that password.");
        var jwtSecret = Shell("JwtSecret", env, "jwt", """{"signingKey":""}""",
            "Jwt__SigningKey (R-18). A long random secret (>= 32 bytes). Required to run the API.");
        var bootstrapSecret = Shell("BootstrapSecret", env, "bootstrap", """{"email":"","password":""}""",
            "Bootstrap__Email / Bootstrap__Password. The one operator (ADR-0017); leave empty to skip seeding.");
        var projectXSecret = Shell("ProjectXSecret", env, "projectx", """{"apiKey":"","apiSecret":""}""",
            "The ProjectX login: apiKey is the TopstepX USERNAME and apiSecret the API KEY (.env.example).");
        var providersSecret = Shell("ProvidersSecret", env, "providers", """{"cohereApiKey":"","finnhubApiKey":"","tiingoApiKey":""}""",
            "Optional data-provider keys. An empty value leaves that integration off.");
        var llmSecret = Shell("LlmSecret", env, "llm", """{"apiKey":""}""",
            "Llm__ApiKey. Unset keeps the agent-review route inert.");
        var pushoverSecret = Shell("PushoverSecret", env, "pushover", """{"appToken":"","userKey":""}""",
            "Pushover__AppToken / Pushover__UserKey (ADR-0019). Unset logs what would have been sent.");
        var checkInSecret = Shell("CheckInSecret", env, "checkin", """{"heartbeatUrl":""}""",
            "CheckIn__HeartbeatUrl — a capability URL. The dead-man's switch must not share this host.");

        _ = new StringParameter(this, "ImageDigestParameter", new StringParameterProps
        {
            ParameterName = $"/trading-copilot/{env}/image-digest",
            StringValue = imageDigest.ValueAsString,
            Description = "The digest the app task definition currently references. Written by the stack on every deploy.",
        });
        _ = new StringParameter(this, "VersionParameter", new StringParameterProps
        {
            ParameterName = $"/trading-copilot/{env}/version",
            StringValue = version.ValueAsString,
            Description = "The release version the digest above was published under.",
        });

        var appLogs = LogGroupFor(env, "app");
        var postgresLogs = LogGroupFor(env, "postgres");

        var fileSystem = new FileSystem(this, "Store", new FileSystemProps
        {
            Vpc = vpc,
            VpcSubnets = taskSubnets,
            SecurityGroup = efsSg,
            Encrypted = true,
            PerformanceMode = PerformanceMode.GENERAL_PURPOSE,
            ThroughputMode = ThroughputMode.ELASTIC,
            RemovalPolicy = RemovalPolicy.RETAIN,
        });
        var accessPoint = fileSystem.AddAccessPoint("PostgresAccessPoint", new AccessPointOptions
        {
            Path = "/postgres",
            PosixUser = new PosixUser { Uid = "1000", Gid = "1000" },
            CreateAcl = new Acl { OwnerUid = "1000", OwnerGid = "1000", Permissions = "700" },
        });
        accessPoint.ApplyRemovalPolicy(RemovalPolicy.RETAIN);

        var cluster = new Cluster(this, "Cluster", new ClusterProps
        {
            ClusterName = $"trading-copilot-{env}",
            Vpc = vpc,
            ContainerInsightsV2 = ContainerInsights.ENABLED,
            ExecuteCommandConfiguration = new ExecuteCommandConfiguration
            {
                Logging = ExecuteCommandLogging.OVERRIDE,
                LogConfiguration = new ExecuteCommandLogConfiguration { CloudWatchLogGroup = postgresLogs },
            },
        });
        var ns = new PrivateDnsNamespace(this, "Namespace", new PrivateDnsNamespaceProps
        {
            Name = $"{env}.tradingcopilot.internal",
            Vpc = vpc,
            Description = "Service discovery for the store: the app's connection string names postgres.<env>.tradingcopilot.internal.",
        });

        var postgresTask = new FargateTaskDefinition(this, "PostgresTask", new FargateTaskDefinitionProps
        {
            Family = $"trading-copilot-{env}-postgres",
            Cpu = 1024,
            MemoryLimitMiB = 2048,
            RuntimePlatform = new RuntimePlatform { CpuArchitecture = CpuArchitecture.X86_64, OperatingSystemFamily = OperatingSystemFamily.LINUX },
            Volumes =
            [
                new Amazon.CDK.AWS.ECS.Volume
                {
                    Name = "pgdata",
                    EfsVolumeConfiguration = new EfsVolumeConfiguration
                    {
                        FileSystemId = fileSystem.FileSystemId,
                        TransitEncryption = "ENABLED",
                        AuthorizationConfig = new AuthorizationConfig { AccessPointId = accessPoint.AccessPointId, Iam = "ENABLED" },
                    },
                },
            ],
        });
        postgresTask.AddToTaskRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "MountTheStore",
            Actions = ["elasticfilesystem:ClientMount", "elasticfilesystem:ClientWrite"],
            Resources = [fileSystem.FileSystemArn],
            Conditions = new Dictionary<string, object>
            {
                ["StringEquals"] = new Dictionary<string, object> { ["elasticfilesystem:AccessPointArn"] = accessPoint.AccessPointArn },
            },
        }));
        var postgres = postgresTask.AddContainer("postgres", new ContainerDefinitionOptions
        {
            ContainerName = "postgres",
            Image = ContainerImage.FromRegistry(PostgresImage),
            PortMappings = [new PortMapping { ContainerPort = 5432, Protocol = Amazon.CDK.AWS.ECS.Protocol.TCP }],
            Logging = LogDrivers.AwsLogs(new AwsLogDriverProps { LogGroup = postgresLogs, StreamPrefix = "postgres" }),
            Environment = new Dictionary<string, string>
            {
                ["POSTGRES_USER"] = PostgresUser,
                ["POSTGRES_DB"] = PostgresDatabase,
                ["PGDATA"] = $"{PostgresDataMount}/data",
            },
            Secrets = new Dictionary<string, EcsSecret> { ["POSTGRES_PASSWORD"] = EcsSecret.FromSecretsManager(postgresSecret, "password") },
            StopTimeout = Duration.Seconds(120),
            HealthCheck = new Amazon.CDK.AWS.ECS.HealthCheck
            {
                Command = ["CMD-SHELL", $"pg_isready -U {PostgresUser} -d {PostgresDatabase} || exit 1"],
                Interval = Duration.Seconds(30),
                Timeout = Duration.Seconds(5),
                Retries = 3,
                StartPeriod = Duration.Seconds(120),
            },
        });
        postgres.AddMountPoints(new MountPoint { ContainerPath = PostgresDataMount, SourceVolume = "pgdata", ReadOnly = false });

        var postgresService = new FargateService(this, "PostgresService", new FargateServiceProps
        {
            ServiceName = $"trading-copilot-{env}-postgres",
            Cluster = cluster,
            TaskDefinition = postgresTask,
            DesiredCount = 1,
            MinHealthyPercent = 0,
            MaxHealthyPercent = 100,
            CircuitBreaker = new DeploymentCircuitBreaker { Enable = true, Rollback = true },
            SecurityGroups = [postgresSg],
            VpcSubnets = taskSubnets,
            AssignPublicIp = !natShape,
            EnableExecuteCommand = true,
            CloudMapOptions = new CloudMapOptions
            {
                CloudMapNamespace = ns,
                Name = "postgres",
                DnsRecordType = DnsRecordType.A,
                DnsTtl = Duration.Seconds(10),
            },
        });

        var appTask = new FargateTaskDefinition(this, "AppTask", new FargateTaskDefinitionProps
        {
            Family = $"trading-copilot-{env}-app",
            Cpu = 512,
            MemoryLimitMiB = 1024,
            RuntimePlatform = new RuntimePlatform { CpuArchitecture = CpuArchitecture.X86_64, OperatingSystemFamily = OperatingSystemFamily.LINUX },
        });

        var aspnetEnvironment = string.Equals(env, "production", StringComparison.Ordinal) ? "Production" : "Staging";
        var appEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_URLS"] = "http://+:8080",
            ["ASPNETCORE_ENVIRONMENT"] = aspnetEnvironment,
            ["ProjectX__CredentialKey"] = "projectx",
            ["ProjectX__DataTier"] = dataTier.ValueAsString,
        };

        if (props.Telemetry is not null)
        {
            appEnvironment["Telemetry__OtlpEndpoint"] = OtelLoopbackEndpoint;
            appEnvironment["Telemetry__ServiceName"] = "trading-copilot-api";
        }

        appTask.AddContainer("app", new ContainerDefinitionOptions
        {
            ContainerName = "app",
            Image = ContainerImage.FromRegistry($"{AppImageRepository}@{imageDigest.ValueAsString}"),
            PortMappings = [new PortMapping { ContainerPort = 8080, Protocol = Amazon.CDK.AWS.ECS.Protocol.TCP }],
            Logging = LogDrivers.AwsLogs(new AwsLogDriverProps { LogGroup = appLogs, StreamPrefix = "app" }),
            Environment = appEnvironment,
            Secrets = new Dictionary<string, EcsSecret>
            {
                ["ConnectionStrings__Default"] = EcsSecret.FromSecretsManager(postgresSecret, "connectionString"),
                ["Jwt__SigningKey"] = EcsSecret.FromSecretsManager(jwtSecret, "signingKey"),
                ["Bootstrap__Email"] = EcsSecret.FromSecretsManager(bootstrapSecret, "email"),
                ["Bootstrap__Password"] = EcsSecret.FromSecretsManager(bootstrapSecret, "password"),
                ["ProjectX__ApiKey"] = EcsSecret.FromSecretsManager(projectXSecret, "apiKey"),
                ["ProjectX__ApiSecret"] = EcsSecret.FromSecretsManager(projectXSecret, "apiSecret"),
                ["Cohere__ApiKey"] = EcsSecret.FromSecretsManager(providersSecret, "cohereApiKey"),
                ["Finnhub__ApiKey"] = EcsSecret.FromSecretsManager(providersSecret, "finnhubApiKey"),
                ["Tiingo__ApiKey"] = EcsSecret.FromSecretsManager(providersSecret, "tiingoApiKey"),
                ["Llm__ApiKey"] = EcsSecret.FromSecretsManager(llmSecret, "apiKey"),
                ["Pushover__AppToken"] = EcsSecret.FromSecretsManager(pushoverSecret, "appToken"),
                ["Pushover__UserKey"] = EcsSecret.FromSecretsManager(pushoverSecret, "userKey"),
                ["CheckIn__HeartbeatUrl"] = EcsSecret.FromSecretsManager(checkInSecret, "heartbeatUrl"),
            },
        });

        if (props.Telemetry is not null)
        {
            _ = new LogStream(this, "OtlpLogStream", new LogStreamProps
            {
                LogGroup = appLogs,
                LogStreamName = "otlp",
            });

            appTask.AddToTaskRolePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Sid = "OtlpTraces",
                Actions = ["xray:PutTraceSegments"],
                Resources = ["*"],
            }));
            appTask.AddToTaskRolePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Sid = "OtlpMetrics",
                Actions = ["cloudwatch:PutMetricData"],
                Resources = ["*"],
            }));
            appTask.AddToTaskRolePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Sid = "OtlpLogs",
                Actions = ["logs:PutLogEvents", "logs:CreateLogStream"],
                Resources = [appLogs.LogGroupArn, Fn.Join(":", [appLogs.LogGroupArn, "*"])],
            }));

            appTask.AddContainer("OtelCollector", new ContainerDefinitionOptions
            {
                ContainerName = "otel-collector",
                Image = ContainerImage.FromRegistry(props.Telemetry.CollectorImage),
                Essential = false,
                MemoryLimitMiB = props.Telemetry.MemoryLimitMiB,
                Logging = LogDrivers.AwsLogs(new AwsLogDriverProps { LogGroup = appLogs, StreamPrefix = "otel-collector" }),
                Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["OTEL_COLLECTOR_CONFIG"] = CollectorConfiguration.Yaml,
                    ["DEPLOYMENT_ENVIRONMENT"] = env,
                    ["SERVICE_VERSION"] = version.ValueAsString,
                    ["AWS_REGION"] = Fn.Ref("AWS::Region"),
                    ["AWS_OTLP_TRACES_ENDPOINT"] = Fn.Sub("https://xray.${AWS::Region}.amazonaws.com/v1/traces"),
                    ["AWS_OTLP_METRICS_ENDPOINT"] = Fn.Sub("https://monitoring.${AWS::Region}.amazonaws.com/v1/metrics"),
                    ["AWS_OTLP_LOGS_ENDPOINT"] = Fn.Sub("https://logs.${AWS::Region}.amazonaws.com/v1/logs"),
                    ["AWS_OTLP_LOG_GROUP"] = $"/trading-copilot/{env}/app",
                },
                Command = ["--config=env:OTEL_COLLECTOR_CONFIG"],
            });
        }

        // Access logs need a concrete region (the ELB account map). This stack is
        // environment-agnostic so `cdk synth --no-lookups` invents neither account nor region
        // (ADR-0030 decision 14). Wire logs when the operator supplies those at apply.
        var alb = new ApplicationLoadBalancer(this, "Alb", new ApplicationLoadBalancerProps
        {
            LoadBalancerName = $"trading-copilot-{env}",
            Vpc = vpc,
            VpcSubnets = publicSubnets,
            InternetFacing = true,
            SecurityGroup = albSg,
            IdleTimeout = Duration.Seconds(600),
            DropInvalidHeaderFields = true,
        });

        var certificate = new Certificate(this, "Certificate", new CertificateProps
        {
            DomainName = hostname.ValueAsString,
            Validation = CertificateValidation.FromDns(zone),
        });
        var targetGroup = new ApplicationTargetGroup(this, "AppTargetGroup", new ApplicationTargetGroupProps
        {
            Vpc = vpc,
            Port = 8080,
            Protocol = ApplicationProtocol.HTTP,
            TargetType = TargetType.IP,
            DeregistrationDelay = Duration.Seconds(30),
            HealthCheck = new Amazon.CDK.AWS.ElasticLoadBalancingV2.HealthCheck
            {
                Path = "/health",
                Interval = Duration.Seconds(30),
                Timeout = Duration.Seconds(5),
                HealthyHttpCodes = "200",
                HealthyThresholdCount = 2,
                UnhealthyThresholdCount = 3,
            },
        });
        var https = alb.AddListener("Https", new BaseApplicationListenerProps
        {
            Port = 443,
            Protocol = ApplicationProtocol.HTTPS,
            Certificates = [ListenerCertificate.FromCertificateManager(certificate)],
            SslPolicy = SslPolicy.RECOMMENDED_TLS,
            Open = false,
            DefaultAction = ListenerAction.FixedResponse(404, new FixedResponseOptions { ContentType = "text/plain", MessageBody = "not found" }),
        });
        https.AddAction("App", new AddApplicationActionProps
        {
            Priority = 10,
            Conditions = [ListenerCondition.HostHeaders([hostname.ValueAsString])],
            Action = ListenerAction.Forward([targetGroup]),
        });
        _ = alb.AddListener("Http", new BaseApplicationListenerProps
        {
            Port = 80,
            Protocol = ApplicationProtocol.HTTP,
            Open = false,
            DefaultAction = ListenerAction.Redirect(new RedirectOptions { Protocol = "HTTPS", Port = "443", Permanent = true }),
        });
        _ = new ARecord(this, "AppAlias", new ARecordProps
        {
            Zone = zone,
            RecordName = hostname.ValueAsString,
            Target = RecordTarget.FromAlias(new LoadBalancerTarget(alb)),
        });

        var app = new FargateService(this, "AppService", new FargateServiceProps
        {
            ServiceName = $"trading-copilot-{env}-app",
            Cluster = cluster,
            TaskDefinition = appTask,
            DesiredCount = 1,
            MinHealthyPercent = 0,
            MaxHealthyPercent = 100,
            CircuitBreaker = new DeploymentCircuitBreaker { Enable = true, Rollback = true },
            HealthCheckGracePeriod = Duration.Seconds(120),
            SecurityGroups = [appSg],
            VpcSubnets = taskSubnets,
            AssignPublicIp = !natShape,
            EnableExecuteCommand = false,
        });
        targetGroup.AddTarget(app);

        var alerts = new Topic(this, "Alerts", new TopicProps
        {
            TopicName = $"trading-copilot-{env}-alerts",
            DisplayName = $"trading-copilot {env} alerts",
        });
        alerts.AddSubscription(new EmailSubscription(alertsEmail.ValueAsString));

        Page(this, alerts, "AppRunningTasks", $"trading-copilot-{env}-app-running-tasks",
            ContainerInsightsRunningTasks(env, "app"),
            threshold: 1, ComparisonOperator.LESS_THAN_THRESHOLD, TreatMissingData.BREACHING,
            "app RunningTaskCount < 1 for 5 min. Missing data is breaching: Insights is off or the cluster is gone.");
        Page(this, alerts, "PostgresRunningTasks", $"trading-copilot-{env}-postgres-running-tasks",
            ContainerInsightsRunningTasks(env, "postgres"),
            threshold: 1, ComparisonOperator.LESS_THAN_THRESHOLD, TreatMissingData.BREACHING,
            "postgres RunningTaskCount < 1 for 5 min. Missing data is breaching: Insights is off or the cluster is gone.");
        Page(this, alerts, "UnhealthyHosts", $"trading-copilot-{env}-unhealthy-hosts",
            targetGroup.Metrics.UnhealthyHostCount(new MetricOptions { Period = Duration.Minutes(5), Statistic = Stats.MAXIMUM }),
            threshold: 1, ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD, TreatMissingData.NOT_BREACHING,
            "ALB UnHealthyHostCount ≥ 1 for 5 min on the app target group.");
        Page(this, alerts, "Elb5xx", $"trading-copilot-{env}-elb-5xx",
            alb.Metrics.HttpCodeElb(HttpCodeElb.ELB_5XX_COUNT, new MetricOptions { Period = Duration.Minutes(5), Statistic = Stats.SUM }),
            http5xxThreshold.ValueAsNumber, ComparisonOperator.GREATER_THAN_THRESHOLD, TreatMissingData.NOT_BREACHING,
            "ALB HTTPCode_ELB_5XX_Count above Http5xxAlarmThreshold per 5 min.");
        Page(this, alerts, "Target5xx", $"trading-copilot-{env}-target-5xx",
            targetGroup.Metrics.HttpCodeTarget(HttpCodeTarget.TARGET_5XX_COUNT, new MetricOptions { Period = Duration.Minutes(5), Statistic = Stats.SUM }),
            http5xxThreshold.ValueAsNumber, ComparisonOperator.GREATER_THAN_THRESHOLD, TreatMissingData.NOT_BREACHING,
            "ALB HTTPCode_Target_5XX_Count above Http5xxAlarmThreshold per 5 min.");

        var deploymentFailed = new Rule(this, "DeploymentFailed", new RuleProps
        {
            RuleName = $"trading-copilot-{env}-deployment-failed",
            Description = "Pages when the ECS circuit breaker rolls a deployment back (ADR-0030 decision 13).",
            EventPattern = new EventPattern
            {
                Source = ["aws.ecs"],
                DetailType = ["ECS Deployment State Change"],
                Resources = [app.ServiceArn, postgresService.ServiceArn],
                Detail = new Dictionary<string, object>
                {
                    ["eventName"] = new[] { "SERVICE_DEPLOYMENT_FAILED" },
                },
            },
        });
        deploymentFailed.AddTarget(new EventTargets.SnsTopic(alerts));

        _ = new CfnOutput(this, "LoadBalancerDnsName", new CfnOutputProps
        {
            Value = alb.LoadBalancerDnsName,
            Description = "The ALB DNS name. The Hostname A record aliases here once the operator delegates the zone.",
        });
        _ = new CfnOutput(this, "HostedZoneNameServers", new CfnOutputProps
        {
            Value = Fn.Join(",", zone.HostedZoneNameServers ?? []),
            Description = "NS to delegate at the registrar. This stack does not invent a parent zone.",
        });

        _ = postgresService;
    }

    private static Metric ContainerInsightsRunningTasks(string env, string service) =>
        new(new MetricProps
        {
            Namespace = "ECS/ContainerInsights",
            MetricName = "RunningTaskCount",
            DimensionsMap = new Dictionary<string, string>
            {
                ["ClusterName"] = $"trading-copilot-{env}",
                ["ServiceName"] = $"trading-copilot-{env}-{service}",
            },
            Period = Duration.Minutes(5),
            Statistic = Stats.AVERAGE,
        });

    private static void Page(
        Stack stack,
        ITopic topic,
        string id,
        string alarmName,
        IMetric metric,
        double threshold,
        ComparisonOperator comparison,
        TreatMissingData missing,
        string description)
    {
        var alarm = new Alarm(stack, id, new AlarmProps
        {
            AlarmName = alarmName,
            Metric = metric,
            Threshold = threshold,
            ComparisonOperator = comparison,
            EvaluationPeriods = 1,
            TreatMissingData = missing,
            AlarmDescription = description,
        });
        alarm.AddAlarmAction(new SnsAction(topic));
    }

    private SecurityGroup Group(IVpc vpc, string id, string description, bool allowAllOutbound) =>
        new(this, id, new SecurityGroupProps
        {
            Vpc = vpc,
            Description = description,
            AllowAllOutbound = allowAllOutbound,
        });

    private SmSecret Shell(string id, string env, string name, string emptyShell, string description) =>
        new(this, id, new SmSecretProps
        {
            SecretName = $"trading-copilot/{env}/{name}",
            Description = $"SHELL — values written by the operator, never in a file. {description}",
            SecretStringValue = SecretValue.UnsafePlainText(emptyShell),
            RemovalPolicy = RemovalPolicy.RETAIN,
        });

    private LogGroup LogGroupFor(string env, string container) =>
        new(this, $"{char.ToUpperInvariant(container[0])}{container[1..]}Logs", new LogGroupProps
        {
            LogGroupName = $"/trading-copilot/{env}/{container}",
            Retention = RetentionDays.ONE_MONTH,
            RemovalPolicy = RemovalPolicy.RETAIN,
        });
}
