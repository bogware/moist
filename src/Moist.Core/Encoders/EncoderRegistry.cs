using System.Collections.Concurrent;
using Moist.Core.Jobs;

namespace Moist.Core.Encoders;

/// <summary>
/// Central registry for all available encoders.
/// Replaces MeGUI's PackageSystem with a simpler, typed approach.
/// </summary>
public sealed class EncoderRegistry
{
    private readonly ConcurrentDictionary<string, EncoderInfo> _encoders = new();
    private readonly ConcurrentDictionary<string, Func<IJobProcessor>> _processorFactories = new();

    public IReadOnlyDictionary<string, EncoderInfo> Encoders => _encoders;

    public void Register(EncoderInfo info, Func<IJobProcessor> factory)
    {
        _encoders[info.Id] = info;
        _processorFactories[info.Id] = factory;
    }

    public IJobProcessor CreateProcessor(string encoderId)
    {
        if (!_processorFactories.TryGetValue(encoderId, out var factory))
            throw new InvalidOperationException($"Unknown encoder: {encoderId}");
        return factory();
    }

    public IEnumerable<EncoderInfo> GetVideoEncoders()
        => _encoders.Values.Where(e => e.Category == "Video");

    public IEnumerable<EncoderInfo> GetAudioEncoders()
        => _encoders.Values.Where(e => e.Category == "Audio");

    public IEnumerable<EncoderInfo> GetMuxers()
        => _encoders.Values.Where(e => e.Category == "Muxer");
}
