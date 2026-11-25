using System.Text.Json;

using P2PModule.Models;

namespace P2PModule.Services.Maintenance;

/// <summary>
/// Node files in the consensus folders come in two shapes: a staging record (WeightedConsensusNode, the node under
/// "NodeData") or a plain PeerNodeHistoric (deltas, ConsensusStore). Reading one as the other does NOT throw in
/// System.Text.Json - it just yields an object with nulls - so the old "try weighted, catch -> historic" fallback
/// never ran for plain records: delta nodes were silently dropped and their verify keys never found.
/// This picks the shape from the JSON itself.
/// </summary>
public static class ConsensusNodeJson
{
    public static PeerNodeHistoric? ReadHistoric(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(prop.Name, nameof(WeightedConsensusNode.NodeData), StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonSerializer.Deserialize<WeightedConsensusNode>(json)?.NodeData;
                    }
                }
            }
            return JsonSerializer.Deserialize<PeerNodeHistoric>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
