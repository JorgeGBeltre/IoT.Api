using System.Globalization;
using System.Text.Json;
using AutoMapper;
using IoT.Protocols.Contracts;
using IoT.Protocols.Models;

namespace IoT.Protocols.Mapping;

/// <summary>
/// Perfil AutoMapper para telemetría IoT.
/// Realiza el mapeo automático de DTOs, estructuras de Types.h y diccionarios de propiedades
/// hacia el modelo canónico unificado CanonicalDeviceTelemetry y NormalizedTelemetryEvent.
/// </summary>
public sealed class TelemetryMappingProfile : Profile
{
    public TelemetryMappingProfile()
    {
        // 1. Types.h DeviceInfo -> CanonicalDeviceTelemetry
        CreateMap<DeviceInfoDto, CanonicalDeviceTelemetry>()
            .ForMember(dest => dest.ChipId, opt => opt.MapFrom(src => src.ChipId))
            .ForMember(dest => dest.ChipType, opt => opt.MapFrom(src => src.ChipType))
            .ForMember(dest => dest.MacAddress, opt => opt.MapFrom(src => src.MacAddress))
            .ForMember(dest => dest.ClientId, opt => opt.MapFrom(src => src.ClientID))
            .ForMember(dest => dest.FirmwareVersion, opt => opt.MapFrom(src => src.FirmwareVersion))
            .ForAllMembers(opt => opt.Condition((src, dest, srcMember) => srcMember != null));

        // 2. Types.h GPSData -> CanonicalDeviceTelemetry
        CreateMap<GpsDataDto, CanonicalDeviceTelemetry>()
            .ForMember(dest => dest.Latitude, opt => opt.MapFrom(src => src.Latitude))
            .ForMember(dest => dest.Longitude, opt => opt.MapFrom(src => src.Longitude))
            .ForMember(dest => dest.Altitude, opt => opt.MapFrom(src => src.Altitude))
            .ForMember(dest => dest.Speed, opt => opt.MapFrom(src => src.Speed))
            .ForMember(dest => dest.Course, opt => opt.MapFrom(src => src.Course))
            .ForMember(dest => dest.Satellites, opt => opt.MapFrom(src => src.Satellites))
            .ForMember(dest => dest.IsValid, opt => opt.MapFrom(src => src.IsValid))
            .ForMember(dest => dest.Timestamp, opt => opt.MapFrom(src => ParseDate(src.Timestamp)));

        // 3. Types.h AccidentData -> CanonicalDeviceTelemetry
        CreateMap<AccidentDataDto, CanonicalDeviceTelemetry>()
            .ForMember(dest => dest.ImpactDetected, opt => opt.MapFrom(src => src.Impact_detected))
            .ForMember(dest => dest.RolloverDetected, opt => opt.MapFrom(src => src.Rollover_detected))
            .ForMember(dest => dest.ImpactMagnitude, opt => opt.MapFrom(src => src.Impact_magnitude))
            .ForMember(dest => dest.RollAngle, opt => opt.MapFrom(src => src.Roll_angle))
            .ForMember(dest => dest.PitchAngle, opt => opt.MapFrom(src => src.Pitch_angle))
            .ForMember(dest => dest.DetectionTime, opt => opt.MapFrom(src => src.Detection_time));

        // 4. MqttDeviceMessageDto -> CanonicalDeviceTelemetry
        CreateMap<MqttDeviceMessageDto, CanonicalDeviceTelemetry>()
            .AfterMap((src, dest, context) =>
            {
                if (src.Device != null)
                {
                    context.Mapper.Map(src.Device, dest);
                }

                if (!string.IsNullOrWhiteSpace(src.Timestamp) && dest.Timestamp == null)
                {
                    dest.Timestamp = ParseDate(src.Timestamp);
                }

                if (src.Details != null)
                {
                    TelemetryAutoMapper.PopulateFromDictionary(dest, src.Details);
                }
            });

        // 5. CanonicalDeviceTelemetry -> NormalizedTelemetryEvent
        CreateMap<CanonicalDeviceTelemetry, NormalizedTelemetryEvent>()
            .ConvertUsing(src => src.ToNormalizedEvent(null, null, null));
    }

    private static DateTime? ParseDate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        if (DateTime.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
        {
            return dt;
        }

        if (long.TryParse(input, out var epoch))
        {
            return epoch > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime
                : DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
        }

        return null;
    }
}
