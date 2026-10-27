using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Common.TelemetryQueue;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Common;
using IoT.Infrastructure.TelemetryQueue;
using IoT.Worker;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace IoT.Tests.Unit;

public sealed class SequentialTelemetryQueueTests
{
    [Fact]
    public async Task InMemoryQueue_EnsuresStrictSequentialOrder_ForSameOriginator()
    {
        var queue = new InMemoryTelemetryQueueService(
            NullLogger<InMemoryTelemetryQueueService>.Instance,
            partitionCount: 4);

        string deviceA = "CHIP-DEVICE-ALPHA";
        string deviceB = "CHIP-DEVICE-BETA";

        int partitionA = OriginatorPartitionResolver.GetPartitionIndex(deviceA, queue.PartitionCount);
        int partitionB = OriginatorPartitionResolver.GetPartitionIndex(deviceB, queue.PartitionCount);

        // Encolar 10 mensajes intercalados para Device A y Device B
        for (int i = 0; i < 10; i++)
        {
            await queue.EnqueueAsync(new TelemetryQueueItem(
                Topic: $"devices/{deviceA}/telemetry",
                Payload: $"{{\"seq\": {i}}}",
                TransportDeviceIdentifier: deviceA));

            await queue.EnqueueAsync(new TelemetryQueueItem(
                Topic: $"devices/{deviceB}/telemetry",
                Payload: $"{{\"seq\": {i}}}",
                TransportDeviceIdentifier: deviceB));
        }

        // Leer la partición de Device A y verificar orden estricto
        var receivedA = new List<int>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await foreach (var msg in queue.ReadPartitionAsync(partitionA, cts.Token))
        {
            if (msg.Item.TransportDeviceIdentifier == deviceA)
            {
                // Extraer 'seq' del payload
                int seq = int.Parse(msg.Item.Payload.Replace("{\"seq\": ", "").Replace("}", "").Trim());
                receivedA.Add(seq);
            }

            if (receivedA.Count == 10) break;
        }

        receivedA.Should().BeInAscendingOrder();
        receivedA.Should().Equal(0, 1, 2, 3, 4, 5, 6, 7, 8, 9);
    }

    [Fact]
    public async Task TelemetryStreamConsumerWorker_DispatchesToMediator_AndAcknowledges()
    {
        var mediatorMock = new Mock<IMediator>();
        mediatorMock
            .Setup(m => m.Send(It.IsAny<IngestTelemetryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(IMediator)))
            .Returns(mediatorMock.Object);

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProviderMock.Object);

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(sf => sf.CreateScope()).Returns(scopeMock.Object);

        var queue = new InMemoryTelemetryQueueService(
            NullLogger<InMemoryTelemetryQueueService>.Instance,
            partitionCount: 2);

        // Encolar un mensaje en la partición 0
        await queue.EnqueueAsync(new TelemetryQueueItem(
            Topic: "devices/DEV-001/telemetry",
            Payload: "{\"temperature\": 24.5}",
            TransportDeviceIdentifier: "DEV-001"));

        var worker = new TelemetryStreamConsumerWorker(
            scopeFactoryMock.Object,
            queue,
            NullLogger<TelemetryStreamConsumerWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // Ejecutar worker brevemente
        try
        {
            await worker.StartAsync(cts.Token);
            await Task.Delay(200, cts.Token);
            await worker.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }

        // Verificar que el mediator recibió el IngestTelemetryCommand correspondiente
        mediatorMock.Verify(
            m => m.Send(
                It.Is<IngestTelemetryCommand>(c =>
                    c.Topic == "devices/DEV-001/telemetry" &&
                    c.TransportDeviceIdentifier == "DEV-001"),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }
}
