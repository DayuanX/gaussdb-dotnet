using System;
using System.Collections.Generic;
using System.Linq;

namespace HuaweiCloud.GaussDB;

static class GaussDBCoordinatorDiscovery
{
    internal readonly record struct CoordinatorNodeRecord(
        string NodeName,
        HaEndpoint NodeHostEndpoint,
        HaEndpoint NodeHost1Endpoint);

    internal static HaEndpoint[]? ResolveClusterEndpoints(
        IReadOnlyList<HaEndpoint> seedEndpoints,
        IReadOnlyList<HaEndpoint>? previousSnapshot,
        IReadOnlyList<CoordinatorNodeRecord> discoveredNodes,
        bool usingEip)
    {
        var knownNodeNames = new HashSet<string>(StringComparer.Ordinal);
        var knownEndpointKeys = new HashSet<string>(seedEndpoints.Select(static endpoint => endpoint.Key), StringComparer.Ordinal);

        if (previousSnapshot is not null)
        {
            foreach (var endpoint in previousSnapshot)
            {
                knownEndpointKeys.Add(endpoint.Key);
                if (!string.IsNullOrWhiteSpace(endpoint.NodeName))
                    knownNodeNames.Add(endpoint.NodeName);
            }
        }

        foreach (var node in discoveredNodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeName))
                continue;

            if (knownEndpointKeys.Contains(node.NodeHostEndpoint.Key) || knownEndpointKeys.Contains(node.NodeHost1Endpoint.Key))
                knownNodeNames.Add(node.NodeName);
        }

        if (knownNodeNames.Count == 0)
            return null;

        var refreshedEndpoints = new List<HaEndpoint>(knownNodeNames.Count);
        var seenNodeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in discoveredNodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeName) || !knownNodeNames.Contains(node.NodeName) || !seenNodeNames.Add(node.NodeName))
                continue;

            var selectedEndpoint = usingEip ? node.NodeHost1Endpoint : node.NodeHostEndpoint;
            if (string.IsNullOrWhiteSpace(selectedEndpoint.Host) || selectedEndpoint.Port <= 0)
                continue;

            refreshedEndpoints.Add(new(selectedEndpoint.Host, selectedEndpoint.Port, node.NodeName));
        }

        return refreshedEndpoints.Count == 0
            ? null
            : refreshedEndpoints.ToArray();
    }
}
