using Moist.Core.Encoders;
using Moist.Core.Jobs;

namespace Moist.Core.Plugins;

/// <summary>
/// Plugin interface for extending moist with new encoders, filters, and tools.
/// </summary>
public interface IPlugin
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string Description { get; }

    void Register(EncoderRegistry encoderRegistry, JobQueue jobQueue);
}
