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

    /// <summary>Disparado a cada evento publicado, com o nome e o JSON já serializado (usado pelos webhooks).</summary>
    public event Action<string, string>? Published;

    public void Publish<T>(string name, T payload)
    {
        var data = JsonSerializer.Serialize(payload, _json);
        var item = new ServerEvent(name, data);
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(item);
        }

        Published?.Invoke(name, data);
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
