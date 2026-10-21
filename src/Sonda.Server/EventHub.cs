using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Sonda.Server;

public sealed record ServerEvent(string Name, string Data);

public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<ServerEvent>> _subscribers = new();
    private readonly JsonSerializerOptions _json;

    public EventHub(JsonSerializerOptions json) => _json = json;

    public int Subscribers => _subscribers.Count;

    public void Publish<T>(string name, T payload)
    {
        var data = JsonSerializer.Serialize(payload, _json);
        var item = new ServerEvent(name, data);
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(item);
        }
    }

    public (Guid Id, ChannelReader<ServerEvent> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ServerEvent>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }
}
