namespace FileSharingModule.Models.Torrent;

public class TorrentPeerDescriptor
{
    public string NodeId { get; set; } = string.Empty;
    public List<string> SignalingEndpoints { get; set; } = new();

    public override bool Equals(object? obj)
    {
        if (obj is TorrentPeerDescriptor other)
        {
            return string.Equals(NodeId, other.NodeId, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(NodeId);
    }
}