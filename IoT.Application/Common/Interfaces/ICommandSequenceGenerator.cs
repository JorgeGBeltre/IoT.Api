namespace IoT.Application.Common.Interfaces;

public interface ICommandSequenceGenerator
{
    Task<long> NextSequenceAsync(Guid deviceId, CancellationToken cancellationToken = default);
}

