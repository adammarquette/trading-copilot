namespace MarqSpec.TradingCopilot.Infra;

/// <summary>
/// How the two Fargate tasks reach GHCR, the venue, and the AWS APIs outbound.
/// </summary>
/// <remarks>
/// ADR-0030 leaves NAT vs. public IP vs. VPC endpoints undecided. The enum starts at 1 so
/// <c>default</c> is not a member; <see cref="EnvironmentStack"/> refuses an unnamed shape.
/// CI synthesises every value. When the operator picks one, it becomes a literal in
/// <c>Program.cs</c> and this comment is replaced.
/// </remarks>
public enum OutboundPath
{
    /// <summary>Each task carries a public IP; no NAT.</summary>
    PublicIpPerTask = 1,

    /// <summary>Tasks sit in private subnets behind one NAT gateway.</summary>
    NatGateway = 2,

    /// <summary>Interface endpoints for the AWS APIs, plus a public IP for GHCR and the venue.</summary>
    VpcEndpointsWithPublicIp = 3,

    /// <summary>The same endpoints, plus a NAT gateway for GHCR and the venue.</summary>
    VpcEndpointsWithNatGateway = 4,
}
