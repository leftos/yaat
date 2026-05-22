namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// Round-trips in-flight coordination channel state for scenario snapshots and session checkpoints.
/// </summary>
public static class CoordinationChannelSnapshotMapper
{
    public static Dictionary<string, CoordinationChannelDto>? ToSnapshotDictionary(Dictionary<string, CoordinationChannel> channels)
    {
        if (channels.Count == 0)
        {
            return null;
        }

        return channels.ToDictionary(kv => kv.Key, kv => ToSnapshot(kv.Value));
    }

    public static CoordinationChannelDto ToSnapshot(CoordinationChannel channel) =>
        new()
        {
            Id = channel.Id,
            ListId = channel.ListId,
            Title = channel.Title,
            SendingTcps = channel.SendingTcps.Count > 0 ? channel.SendingTcps.Select(t => t.ToSnapshot()).ToList() : null,
            Receivers =
                channel.Receivers.Count > 0
                    ? channel
                        .Receivers.Select(r => new CoordinationReceiverDto { Tcp = r.Tcp.ToSnapshot(), IsAutoRelease = r.AutoAcknowledge })
                        .ToList()
                    : null,
            Items =
                channel.Items.Count > 0
                    ? channel
                        .Items.Select(i => new CoordinationItemDto
                        {
                            Id = i.Id,
                            AircraftId = i.AircraftId,
                            Status = (int)i.Status,
                            Message = i.Message,
                            ExpireTime = i.ExpireTime,
                            OriginTcp = i.OriginTcp.ToSnapshot(),
                            ExitFix = i.ExitFix,
                            WasAutomaticRelease = i.WasAutomaticRelease,
                            SequenceNumber = i.SequenceNumber,
                        })
                        .ToList()
                    : null,
            NextSequence = channel.NextSequence,
        };

    public static void RestoreChannels(Dictionary<string, CoordinationChannel> target, Dictionary<string, CoordinationChannelDto>? dtos)
    {
        target.Clear();
        if (dtos is null)
        {
            return;
        }

        foreach (var (id, dto) in dtos)
        {
            target[id] = FromSnapshot(dto);
        }
    }

    public static CoordinationChannel FromSnapshot(CoordinationChannelDto dto)
    {
        var channel = new CoordinationChannel
        {
            Id = dto.Id,
            ListId = dto.ListId,
            Title = dto.Title,
            SendingTcps = dto.SendingTcps?.Select(Tcp.FromSnapshot).ToList() ?? [],
            Receivers = dto.Receivers?.Select(r => new CoordinationReceiver(Tcp.FromSnapshot(r.Tcp), r.IsAutoRelease)).ToList() ?? [],
            NextSequence = dto.NextSequence,
        };

        if (dto.Items is not null)
        {
            foreach (var itemDto in dto.Items)
            {
                channel.Items.Add(
                    new CoordinationItem
                    {
                        Id = itemDto.Id,
                        AircraftId = itemDto.AircraftId,
                        Status = (StarsCoordinationStatus)itemDto.Status,
                        Message = itemDto.Message,
                        ExpireTime = itemDto.ExpireTime,
                        OriginTcp = Tcp.FromSnapshot(itemDto.OriginTcp),
                        ExitFix = itemDto.ExitFix,
                        WasAutomaticRelease = itemDto.WasAutomaticRelease,
                        SequenceNumber = itemDto.SequenceNumber,
                    }
                );
            }
        }

        return channel;
    }
}
