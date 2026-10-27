using System.Data;
using System.Data.Common;
using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

public class PostgreSqlRowLevelSecurityTests
{
    private readonly Mock<ITenantContext> _tenantContextMock = new();
    private readonly Mock<ILogger<TenantSessionInterceptor>> _loggerMock = new();

    private sealed class FakeNpgsqlDbConnection : DbConnection
    {
        public string LastExecutedCommand { get; private set; } = string.Empty;
        public int ExecuteCount { get; private set; }
        private ConnectionState _state = ConnectionState.Closed;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "iot_platform_db";
        public override string DataSource => "localhost";
        public override string ServerVersion => "16.3";
        public override ConnectionState State => _state;

        public void SetState(ConnectionState state) => _state = state;

        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotImplementedException();
        protected override DbCommand CreateDbCommand() => new FakeNpgsqlDbCommand(this);

        public void RecordExecution(string commandText)
        {
            LastExecutedCommand = commandText;
            ExecuteCount++;
        }
    }

    private sealed class FakeNpgsqlDbCommand : DbCommand
    {
        private readonly FakeNpgsqlDbConnection _connection;

        public FakeNpgsqlDbCommand(FakeNpgsqlDbConnection connection)
        {
            _connection = connection;
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotImplementedException();
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }
        public override int ExecuteNonQuery()
        {
            _connection.RecordExecution(CommandText);
            return 1;
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            _connection.RecordExecution(CommandText);
            return Task.FromResult(1);
        }

        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotImplementedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotImplementedException();
    }

    [Fact]
    public async Task ConnectionOpenedAsync_WithValidTenantId_SetsSessionTenantAndDisablesSystemSession()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        _tenantContextMock.Setup(c => c.TenantId).Returns(tenantId);

        var config = new ConfigurationBuilder().Build();
        var interceptor = new TenantSessionInterceptor(_tenantContextMock.Object, config, _loggerMock.Object);

        using var connection = new FakeNpgsqlDbConnection();
        connection.SetState(ConnectionState.Open);

        // Act
        await interceptor.ConnectionOpenedAsync(connection, default!);

        // Assert
        connection.ExecuteCount.Should().Be(1);
        connection.LastExecutedCommand.Should().Contain($"SELECT set_config('app.tenant_id', '{tenantId}', false);");
        connection.LastExecutedCommand.Should().Contain("SELECT set_config('app.is_system_session', 'false', false);");
    }

    [Fact]
    public async Task ConnectionOpenedAsync_WithEmptyTenantId_SetsSystemSessionMode()
    {
        // Arrange
        _tenantContextMock.Setup(c => c.TenantId).Returns(Guid.Empty);

        var config = new ConfigurationBuilder().Build();
        var interceptor = new TenantSessionInterceptor(_tenantContextMock.Object, config, _loggerMock.Object);

        using var connection = new FakeNpgsqlDbConnection();
        connection.SetState(ConnectionState.Open);

        // Act
        await interceptor.ConnectionOpenedAsync(connection, default!);

        // Assert
        connection.ExecuteCount.Should().Be(1);
        connection.LastExecutedCommand.Should().Contain("SELECT set_config('app.tenant_id', '', false);");
        connection.LastExecutedCommand.Should().Contain("SELECT set_config('app.is_system_session', 'true', false);");
    }

    [Fact]
    public async Task ConnectionOpenedAsync_WithEmptyTenantIdAndStrictMode_ThrowsSecurityViolation()
    {
        // Arrange
        _tenantContextMock.Setup(c => c.TenantId).Returns(Guid.Empty);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:Rls:Enforce"] = "true"
            })
            .Build();

        var interceptor = new TenantSessionInterceptor(_tenantContextMock.Object, config, _loggerMock.Object);

        using var connection = new FakeNpgsqlDbConnection();
        connection.SetState(ConnectionState.Open);

        // Act
        var act = async () => await interceptor.ConnectionOpenedAsync(connection, default!);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Row-Level Security (RLS) Violation*");
    }

    [Fact]
    public async Task ConnectionClosedAsync_CleansUpSessionSettings_PreventsPoolLeakage()
    {
        // Arrange
        _tenantContextMock.Setup(c => c.TenantId).Returns(Guid.NewGuid());

        var config = new ConfigurationBuilder().Build();
        var interceptor = new TenantSessionInterceptor(_tenantContextMock.Object, config, _loggerMock.Object);

        using var connection = new FakeNpgsqlDbConnection();
        connection.SetState(ConnectionState.Open);

        // Act
        await interceptor.ConnectionClosedAsync(connection, default!);

        // Assert
        connection.ExecuteCount.Should().Be(1);
        connection.LastExecutedCommand.Should().Be("RESET app.tenant_id; RESET app.is_system_session; RESET app.customer_path;");
    }

    [Fact]
    public void Synchronous_ConnectionOpenedAndClosed_OperateCorrectly()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        _tenantContextMock.Setup(c => c.TenantId).Returns(tenantId);

        var config = new ConfigurationBuilder().Build();
        var interceptor = new TenantSessionInterceptor(_tenantContextMock.Object, config, _loggerMock.Object);

        using var connection = new FakeNpgsqlDbConnection();
        connection.SetState(ConnectionState.Open);

        // Act Open
        interceptor.ConnectionOpened(connection, default!);

        // Assert Open
        connection.ExecuteCount.Should().Be(1);
        connection.LastExecutedCommand.Should().Contain($"SELECT set_config('app.tenant_id', '{tenantId}', false);");

        // Act Close
        interceptor.ConnectionClosed(connection, default!);

        // Assert Close
        connection.ExecuteCount.Should().Be(2);
        connection.LastExecutedCommand.Should().Be("RESET app.tenant_id; RESET app.is_system_session; RESET app.customer_path;");
    }
}
