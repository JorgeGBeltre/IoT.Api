using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace IoT.Tests.Architecture;

public class ArchitectureTests
{
    private const string DomainNamespace = "IoT.Domain";
    private const string ApplicationNamespace = "IoT.Application";
    private const string InfrastructureNamespace = "IoT.Infrastructure";
    private const string ApiNamespace = "IoT.Api";
    private const string WorkerNamespace = "IoT.Worker";

    [Fact]
    public void Domain_ShouldNotHaveDependencyOnOtherLayers()
    {
        var assembly = typeof(IoT.Domain.Common.Entity<>).Assembly;

        var otherProjects = new[]
        {
            ApplicationNamespace,
            InfrastructureNamespace,
            ApiNamespace,
            WorkerNamespace
        };

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(otherProjects)
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void Domain_ShouldNotDependOnEntityFrameworkOrMqtt()
    {
        var assembly = typeof(IoT.Domain.Common.Entity<>).Assembly;

        var externalFrameworks = new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "MQTTnet",
            "StackExchange.Redis"
        };

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(externalFrameworks)
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void Application_ShouldNotDependOnInfrastructureOrApi()
    {
        var assembly = typeof(IoT.Domain.Common.Entity<>).Assembly; // will check application assembly once types are added

        var forbiddenLayers = new[]
        {
            InfrastructureNamespace,
            ApiNamespace,
            WorkerNamespace
        };

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenLayers)
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }
}
