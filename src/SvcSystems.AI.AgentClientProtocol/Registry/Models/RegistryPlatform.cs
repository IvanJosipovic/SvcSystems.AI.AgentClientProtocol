namespace SvcSystems.AI.AgentClientProtocol.Registry.Models;

/// <summary>A host runtime identifier and its corresponding registry platform key.</summary>
internal sealed record RegistryPlatform(string RegistryName, string RuntimeIdentifier)
{
    internal static RegistryPlatform? FromRuntimeIdentifier(string? runtimeIdentifier)
    {
        if (string.IsNullOrWhiteSpace(runtimeIdentifier))
        {
            return null;
        }

        var components = runtimeIdentifier.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length < 2)
        {
            return null;
        }

        var operatingSystem = components[0] switch
        {
            "win" => "windows",
            "osx" => "darwin",
            var value => value,
        };
        var architecture = components[^1] switch
        {
            "x64" => "x86_64",
            "arm64" => "aarch64",
            var value => value,
        };
        var qualifiers = components[1..^1];
        var registryName = $"{operatingSystem}-{architecture}" +
            (qualifiers.Length == 0 ? string.Empty : $"-{string.Join('-', qualifiers)}");

        return new RegistryPlatform(registryName, runtimeIdentifier);
    }
}
